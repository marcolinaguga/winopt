using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Winopt
{
    /// <summary>Grupo em que a etapa aparece na interface.</summary>
    internal enum GrupoEtapa
    {
        Limpeza,
        Reparo,
        Otimizacao
    }

    /// <summary>Uma ferramenta que o usuário pode marcar para execução.</summary>
    internal sealed class Etapa
    {
        public string Nome;
        public GrupoEtapa Grupo;
        public bool MarcadaPorPadrao;

        /// <summary>Executa a ferramenta. Sempre chamada fora da thread da interface.</summary>
        public Func<Contexto, Task> Executar;

        /// <summary>Estima o espaço que a ferramenta liberaria, sem alterar nada. Nulo se não houver estimativa.</summary>
        public Func<Contexto, Task> Analisar;

        /// <summary>Indica que a ferramenta mexe em componentes do Windows (justifica um ponto de restauração).</summary>
        public bool AlteraSistema;
    }

    /// <summary>Catálogo das ferramentas disponíveis e a implementação de cada uma.</summary>
    internal static class Etapas
    {
        // Número do perfil usado pelo cleanmgr (/sagerun:777). Valor arbitrário entre 0 e 9999.
        private const int PerfilCleanmgr = 777;

        // Categorias que o cleanmgr pode limpar sem risco de perda de dados do usuário.
        // A pasta Downloads e a Lixeira ficam de fora de propósito.
        private static readonly string[] CategoriasSegurasCleanmgr =
        {
            "Temporary Files",
            "Temporary Setup Files",
            "Internet Cache Files",
            "Downloaded Program Files",
            "Thumbnail Cache",
            "D3D Shader Cache",
            "Delivery Optimization Files",
            "Windows Error Reporting Files",
            "Update Cleanup",
            "Old ChkDsk Files",
            "Setup Log Files"
        };

        /// <summary>
        /// Lista das etapas NA ORDEM DE EXECUÇÃO. Limpezas vêm primeiro; o DISM vem antes do
        /// SFC porque o SFC usa a imagem do Windows (reparada pelo DISM) como fonte dos arquivos.
        /// </summary>
        public static List<Etapa> CriarCatalogo()
        {
            List<Etapa> etapas = new List<Etapa>();

            etapas.Add(Criar("Limpar Arquivos Temporários", GrupoEtapa.Limpeza, true, LimparTemporarios, AnalisarTemporarios, false));
            etapas.Add(Criar("Esvaziar Lixeira", GrupoEtapa.Limpeza, true, EsvaziarLixeira, AnalisarLixeira, false));
            etapas.Add(Criar("Cache dos Navegadores", GrupoEtapa.Limpeza, false, LimparCacheDosNavegadores, AnalisarNavegadores, false));
            etapas.Add(Criar("Despejos e Relatórios de Erro", GrupoEtapa.Limpeza, false, LimparDespejosDeErro, AnalisarDespejos, false));
            etapas.Add(Criar("Cache DNS", GrupoEtapa.Limpeza, false, LimparCacheDns, null, false));
            etapas.Add(Criar("Cache do Windows Update", GrupoEtapa.Limpeza, false, LimparCacheDoWindowsUpdate, AnalisarCacheDoWindowsUpdate, true));
            etapas.Add(Criar("Limpeza de Disco (Cleanmgr)", GrupoEtapa.Limpeza, false, ExecutarCleanmgr, null, false));
            etapas.Add(Criar("Restaurar Imagem do Windows (DISM)", GrupoEtapa.Reparo, false, ExecutarDism, null, true));
            etapas.Add(Criar("Verificar Integridade (SFC)", GrupoEtapa.Reparo, false, ExecutarSfc, null, true));
            etapas.Add(Criar("Componentes Antigos do Windows", GrupoEtapa.Limpeza, false, LimparComponentesAntigos, AnalisarComponentes, true));
            etapas.Add(Criar("Verificar Disco (CHKDSK)", GrupoEtapa.Reparo, false, VerificarDisco, null, false));
            etapas.Add(Criar("Otimizar Unidades (TRIM/Desfragmentar)", GrupoEtapa.Otimizacao, false, OtimizarUnidades, null, false));

            return etapas;
        }

        private static Etapa Criar(string nome, GrupoEtapa grupo, bool padrao,
            Func<Contexto, Task> executar, Func<Contexto, Task> analisar, bool alteraSistema)
        {
            Etapa etapa = new Etapa();
            etapa.Nome = nome;
            etapa.Grupo = grupo;
            etapa.MarcadaPorPadrao = padrao;
            etapa.Executar = executar;
            etapa.Analisar = analisar;
            etapa.AlteraSistema = alteraSistema;
            return etapa;
        }

        private static readonly Task Concluida = Task.FromResult(0);

        // ==================================================================
        // LIMPEZA
        // ==================================================================

        /// <summary>Pastas de temporários: do usuário (%TEMP%) e do sistema (C:\Windows\Temp).</summary>
        internal static string[] PastasTemporarias()
        {
            return new[]
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
            };
        }

        private static Task LimparTemporarios(Contexto ctx)
        {
            Arquivos.LimparPastas(ctx, PastasTemporarias());
            return Concluida;
        }

        private static Task EsvaziarLixeira(Contexto ctx)
        {
            // API nativa do Shell: esvazia a Lixeira de todas as unidades, sem diálogos nem som.
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);

            if (hr == 0)
            {
                ctx.Log("Lixeira esvaziada.", TipoLog.Sucesso);
            }
            else if (hr == NativeMethods.E_UNEXPECTED)
            {
                // O Windows devolve este código quando não há nada para remover.
                ctx.Log("A Lixeira já estava vazia.");
            }
            else
            {
                ctx.Log(string.Format("Não foi possível esvaziar a Lixeira (código 0x{0:X8}).", hr), TipoLog.Erro);
            }

            return Concluida;
        }

        // ------------------------------------------------------------------
        // Cache dos navegadores
        // ------------------------------------------------------------------

        /// <summary>Um navegador instalado e as pastas de cache encontradas nos perfis dele.</summary>
        internal sealed class Navegador
        {
            public string Nome;
            public string Processo;
            public List<string> PastasDeCache = new List<string>();

            public bool EstaAberto()
            {
                Process[] processos = Process.GetProcessesByName(Processo);
                foreach (Process processo in processos)
                {
                    processo.Dispose();
                }
                return processos.Length > 0;
            }
        }

        // Somente pastas de cache: podem ser apagadas sem perder senhas, cookies, histórico ou favoritos.
        private static readonly string[] CachesPorPerfilChromium =
        {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"
        };

        private static readonly string[] CachesGlobaisChromium =
        {
            "ShaderCache", "GrShaderCache", "GraphiteDawnCache"
        };

        /// <summary>
        /// Procura Chrome, Edge, Brave e Firefox a partir da pasta AppData\Local informada
        /// e devolve apenas os que têm alguma pasta de cache.
        /// </summary>
        internal static List<Navegador> LocalizarNavegadores(string appDataLocal)
        {
            List<Navegador> navegadores = new List<Navegador>();

            AdicionarChromium(navegadores, "Google Chrome", "chrome",
                Path.Combine(appDataLocal, "Google", "Chrome", "User Data"));
            AdicionarChromium(navegadores, "Microsoft Edge", "msedge",
                Path.Combine(appDataLocal, "Microsoft", "Edge", "User Data"));
            AdicionarChromium(navegadores, "Brave", "brave",
                Path.Combine(appDataLocal, "BraveSoftware", "Brave-Browser", "User Data"));

            // Firefox: o cache fica em AppData\Local (os dados do usuário ficam em Roaming, intocados).
            string perfisFirefox = Path.Combine(appDataLocal, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(perfisFirefox))
            {
                Navegador firefox = new Navegador();
                firefox.Nome = "Mozilla Firefox";
                firefox.Processo = "firefox";
                foreach (string perfil in Directory.GetDirectories(perfisFirefox))
                {
                    AdicionarSeExistir(firefox.PastasDeCache, Path.Combine(perfil, "cache2"));
                }
                if (firefox.PastasDeCache.Count > 0)
                {
                    navegadores.Add(firefox);
                }
            }

            return navegadores;
        }

        private static void AdicionarChromium(List<Navegador> navegadores, string nome, string processo, string dadosDoUsuario)
        {
            if (!Directory.Exists(dadosDoUsuario))
            {
                return;
            }

            Navegador navegador = new Navegador();
            navegador.Nome = nome;
            navegador.Processo = processo;

            foreach (string global in CachesGlobaisChromium)
            {
                AdicionarSeExistir(navegador.PastasDeCache, Path.Combine(dadosDoUsuario, global));
            }

            // Perfis: "Default", "Profile 1", "Profile 2"...
            foreach (string perfil in Directory.GetDirectories(dadosDoUsuario))
            {
                string nomePerfil = Path.GetFileName(perfil);
                bool ehPerfil = nomePerfil == "Default"
                    || nomePerfil == "Guest Profile"
                    || nomePerfil.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                if (!ehPerfil)
                {
                    continue;
                }

                foreach (string cache in CachesPorPerfilChromium)
                {
                    AdicionarSeExistir(navegador.PastasDeCache, Path.Combine(perfil, cache));
                }
            }

            if (navegador.PastasDeCache.Count > 0)
            {
                navegadores.Add(navegador);
            }
        }

        private static void AdicionarSeExistir(List<string> lista, string pasta)
        {
            if (Directory.Exists(pasta))
            {
                lista.Add(pasta);
            }
        }

        private static Task LimparCacheDosNavegadores(Contexto ctx)
        {
            string appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LimparNavegadores(ctx, LocalizarNavegadores(appDataLocal));
            return Concluida;
        }

        internal static void LimparNavegadores(Contexto ctx, List<Navegador> navegadores)
        {
            if (navegadores.Count == 0)
            {
                ctx.Log("Nenhum navegador compatível encontrado (Chrome, Edge, Brave, Firefox).");
                return;
            }

            ctx.Log("Apenas o cache é removido: senhas, cookies, histórico e favoritos são preservados.");

            foreach (Navegador navegador in navegadores)
            {
                ctx.Token.ThrowIfCancellationRequested();

                // Com o navegador aberto os arquivos de cache ficam travados e o resultado seria parcial.
                if (navegador.EstaAberto())
                {
                    ctx.Log(navegador.Nome + " está aberto: feche-o e execute de novo para limpar o cache.", TipoLog.Aviso);
                    continue;
                }

                ResultadoLimpeza resultado = new ResultadoLimpeza();
                foreach (string pasta in navegador.PastasDeCache)
                {
                    Arquivos.LimparConteudo(new DirectoryInfo(pasta), resultado, ctx.Token);
                }

                ctx.Log(navegador.Nome + ":");
                Arquivos.RegistrarResultado(ctx, resultado);
            }
        }

        // ------------------------------------------------------------------
        // Despejos de memória e relatórios de erro
        // ------------------------------------------------------------------

        /// <summary>Pastas com despejos de falhas e relatórios do Windows Error Reporting.</summary>
        internal static string[] PastasDeDespejos()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string dadosComuns = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return new[]
            {
                Path.Combine(windows, "Minidump"),
                Path.Combine(windows, "LiveKernelReports"),
                Path.Combine(dadosComuns, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(dadosComuns, "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(dadosComuns, "Microsoft", "Windows", "WER", "Temp"),
                Path.Combine(appDataLocal, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(appDataLocal, "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(appDataLocal, "CrashDumps")
            };
        }

        /// <summary>Despejo completo de memória gerado em telas azuis (pode ter vários GB).</summary>
        internal static string ArquivoDeDespejoCompleto()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
        }

        private static Task LimparDespejosDeErro(Contexto ctx)
        {
            ResultadoLimpeza resultado = new ResultadoLimpeza();

            FileInfo despejoCompleto = new FileInfo(ArquivoDeDespejoCompleto());
            if (despejoCompleto.Exists)
            {
                Arquivos.ApagarArquivo(despejoCompleto, resultado);
            }

            foreach (string pasta in PastasDeDespejos())
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (Directory.Exists(pasta))
                {
                    Arquivos.LimparConteudo(new DirectoryInfo(pasta), resultado, ctx.Token);
                }
            }

            Arquivos.RegistrarResultado(ctx, resultado);
            return Concluida;
        }

        // ------------------------------------------------------------------
        // Cache DNS
        // ------------------------------------------------------------------

        private static async Task LimparCacheDns(Contexto ctx)
        {
            int codigo = await Processos.ExecutarAsync(Processos.System32("ipconfig.exe"), "/flushdns", ctx, false);
            if (codigo != 0)
            {
                ctx.Log("ipconfig terminou com código " + codigo + ".", TipoLog.Aviso);
            }
        }

        // ------------------------------------------------------------------
        // Cache do Windows Update
        // ------------------------------------------------------------------

        // Serviços que usam a pasta de downloads do Windows Update.
        private static readonly string[] ServicosDoWindowsUpdate = { "wuauserv", "bits" };

        internal static string PastaDeDownloadsDoWindowsUpdate()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SoftwareDistribution", "Download");
        }

        private static Task LimparCacheDoWindowsUpdate(Contexto ctx)
        {
            string pasta = PastaDeDownloadsDoWindowsUpdate();
            if (!Directory.Exists(pasta))
            {
                ctx.Log("Pasta não encontrada: " + pasta, TipoLog.Aviso);
                return Concluida;
            }

            // Os serviços são parados para liberar os arquivos e religados ao final,
            // mesmo que a limpeza falhe ou seja cancelada.
            List<string> parados = new List<string>();
            try
            {
                foreach (string servico in ServicosDoWindowsUpdate)
                {
                    if (PararServico(ctx, servico))
                    {
                        parados.Add(servico);
                    }
                }

                Arquivos.LimparPastas(ctx, new[] { pasta });
                ctx.Log("Atualizações já instaladas não são afetadas; downloads pendentes serão baixados de novo.");
            }
            finally
            {
                foreach (string servico in parados)
                {
                    IniciarServico(ctx, servico);
                }
            }

            return Concluida;
        }

        /// <summary>Para um serviço em execução. Retorna true se foi este método que o parou.</summary>
        private static bool PararServico(Contexto ctx, string nome)
        {
            try
            {
                using (ServiceController servico = new ServiceController(nome))
                {
                    if (servico.Status == ServiceControllerStatus.Stopped)
                    {
                        return false;
                    }

                    ctx.Log("Parando o serviço " + nome + " ...");
                    if (servico.Status != ServiceControllerStatus.StopPending)
                    {
                        servico.Stop();
                    }
                    servico.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Sem conseguir parar, a limpeza segue e apenas pula os arquivos em uso.
                ctx.Log("Não foi possível parar o serviço " + nome + ": " + ex.Message, TipoLog.Aviso);
                return false;
            }
        }

        private static void IniciarServico(Contexto ctx, string nome)
        {
            try
            {
                using (ServiceController servico = new ServiceController(nome))
                {
                    if (servico.Status == ServiceControllerStatus.Stopped)
                    {
                        servico.Start();
                        servico.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }
                    ctx.Log("Serviço " + nome + " religado.");
                }
            }
            catch (Exception ex)
            {
                ctx.Log("Não foi possível religar o serviço " + nome + " (o Windows o inicia sozinho quando precisar): "
                    + ex.Message, TipoLog.Aviso);
            }
        }

        // ------------------------------------------------------------------
        // Componentes antigos do Windows
        // ------------------------------------------------------------------

        private static async Task LimparComponentesAntigos(Contexto ctx)
        {
            ctx.Log("Remove versões substituídas de componentes do Windows. Pode levar vários minutos.", TipoLog.Aviso);

            // Sem /ResetBase de propósito: assim as atualizações instaladas continuam desinstaláveis.
            int codigo = await Processos.ExecutarAsync(Processos.System32("dism.exe"),
                "/Online /Cleanup-Image /StartComponentCleanup", ctx, true);

            if (codigo == 0)
            {
                ctx.Log("Limpeza de componentes concluída.", TipoLog.Sucesso);
            }
            else if (codigo == 3010)
            {
                ctx.Log("Limpeza de componentes concluída. Reinicie o computador para finalizar.", TipoLog.Aviso);
            }
            else
            {
                ctx.Log("DISM terminou com código " + codigo + ". Detalhes em C:\\Windows\\Logs\\DISM\\dism.log", TipoLog.Erro);
            }
        }

        // ------------------------------------------------------------------
        // Limpeza de Disco (cleanmgr)
        // ------------------------------------------------------------------

        private static async Task ExecutarCleanmgr(Contexto ctx)
        {
            string cleanmgr = Processos.System32("cleanmgr.exe");
            if (!File.Exists(cleanmgr))
            {
                ctx.Log("cleanmgr.exe não está disponível nesta versão do Windows.", TipoLog.Aviso);
                return;
            }

            // O cleanmgr só roda sem perguntas no modo /sagerun, que lê as categorias de um
            // perfil salvo. Gravamos esse perfil (apenas configuração do próprio cleanmgr).
            int categorias = ConfigurarPerfilCleanmgr();
            ctx.Log(categorias + " categoria(s) de limpeza selecionada(s). Executando...");
            ctx.Log("O cleanmgr não gera saída de texto; uma pequena janela de progresso pode aparecer.");

            int codigo = await Processos.ExecutarAsync(cleanmgr, "/sagerun:" + PerfilCleanmgr, ctx, false);

            if (codigo == 0)
            {
                ctx.Log("Limpeza de Disco concluída.", TipoLog.Sucesso);
            }
            else
            {
                ctx.Log("Limpeza de Disco terminou com código " + codigo + ".", TipoLog.Aviso);
            }
        }

        /// <summary>
        /// Marca, no perfil do cleanmgr, somente as categorias seguras; todas as demais
        /// (inclusive a pasta Downloads) são explicitamente desmarcadas.
        /// Retorna quantas categorias foram ativadas.
        /// </summary>
        private static int ConfigurarPerfilCleanmgr()
        {
            const string caminho = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
            string nomeValor = "StateFlags" + PerfilCleanmgr.ToString("D4");
            int ativadas = 0;

            using (RegistryKey raiz = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey caches = raiz.OpenSubKey(caminho))
            {
                if (caches == null)
                {
                    throw new InvalidOperationException("Configuração do cleanmgr não encontrada.");
                }

                foreach (string categoria in caches.GetSubKeyNames())
                {
                    bool segura = Array.IndexOf(CategoriasSegurasCleanmgr, categoria) >= 0;

                    using (RegistryKey chave = caches.OpenSubKey(categoria, true))
                    {
                        if (chave == null)
                        {
                            continue;
                        }

                        // 2 = categoria marcada; 0 = desmarcada.
                        chave.SetValue(nomeValor, segura ? 2 : 0, RegistryValueKind.DWord);
                        if (segura)
                        {
                            ativadas++;
                        }
                    }
                }
            }

            return ativadas;
        }

        // ==================================================================
        // REPARO
        // ==================================================================

        private static async Task ExecutarDism(Contexto ctx)
        {
            ctx.Log("Esta etapa pode levar vários minutos e precisa de internet (Windows Update).", TipoLog.Aviso);

            int codigo = await Processos.ExecutarAsync(Processos.System32("dism.exe"),
                "/Online /Cleanup-Image /RestoreHealth", ctx, true);

            if (codigo == 0)
            {
                ctx.Log("DISM concluído com sucesso.", TipoLog.Sucesso);
            }
            else if (codigo == 3010)
            {
                ctx.Log("DISM concluído. Reinicie o computador para finalizar o reparo.", TipoLog.Aviso);
            }
            else
            {
                ctx.Log("DISM terminou com código " + codigo + ". Detalhes em C:\\Windows\\Logs\\DISM\\dism.log", TipoLog.Erro);
            }
        }

        private static async Task ExecutarSfc(Contexto ctx)
        {
            ctx.Log("Esta etapa pode levar vários minutos.", TipoLog.Aviso);

            int codigo = await Processos.ExecutarAsync(Processos.System32("sfc.exe"), "/scannow", ctx, true);

            if (codigo == 0)
            {
                ctx.Log("SFC concluído.", TipoLog.Sucesso);
            }
            else
            {
                ctx.Log("SFC terminou com código " + codigo + ". Detalhes em C:\\Windows\\Logs\\CBS\\CBS.log", TipoLog.Aviso);
            }
        }

        // ==================================================================
        // REPARO E OTIMIZAÇÃO (Rodada 3)
        // ==================================================================

        private static async Task VerificarDisco(Contexto ctx)
        {
            // "C:" (sem barra) é o formato que o chkdsk espera.
            string unidade = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');
            ctx.Log("Varredura online de " + unidade + ": o disco continua em uso e nada é alterado nesta etapa.");

            int codigo = await Processos.ExecutarAsync(Processos.System32("chkdsk.exe"), unidade + " /scan", ctx, true);

            if (codigo == 0)
            {
                ctx.Log("Nenhum problema encontrado no disco.", TipoLog.Sucesso);
            }
            else
            {
                ctx.Log("O CHKDSK terminou com código " + codigo + ". Se foram apontados erros, execute \"chkdsk "
                    + unidade + " /f\" em um prompt de administrador e reinicie o computador.", TipoLog.Aviso);
            }
        }

        private static async Task OtimizarUnidades(Contexto ctx)
        {
            ctx.Log("O Windows escolhe a operação certa para cada unidade: TRIM em SSD, desfragmentação em HD.");
            ctx.Log("Em HDs muito fragmentados esta etapa pode levar bastante tempo.", TipoLog.Aviso);

            // /C = todas as unidades; /O = otimização adequada ao tipo de mídia.
            int codigo = await Processos.ExecutarAsync(Processos.System32("defrag.exe"), "/C /O", ctx, true);

            if (codigo == 0)
            {
                ctx.Log("Otimização das unidades concluída.", TipoLog.Sucesso);
            }
            else
            {
                ctx.Log("O defrag terminou com código " + codigo + ".", TipoLog.Aviso);
            }
        }

        /// <summary>
        /// Cria um ponto de restauração do sistema. Se não for possível (proteção desativada ou
        /// já existe um ponto nas últimas 24 h), apenas avisa: a otimização segue normalmente.
        /// </summary>
        public static async Task CriarPontoDeRestauracao(Contexto ctx)
        {
            string powershell = Path.Combine(Path.GetDirectoryName(Processos.System32("cmd.exe")),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            // Em caso de erro, mostra só a mensagem (sem o rastreamento do PowerShell) e devolve código 1.
            const string comando = "$ProgressPreference='SilentlyContinue'; " +
                "try { Checkpoint-Computer -Description 'Winopt' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop } " +
                "catch { Write-Output $_.Exception.Message; exit 1 }";

            int codigo = await Processos.ExecutarAsync(powershell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + comando + "\"", ctx, false);

            if (codigo == 0)
            {
                ctx.Log("Ponto de restauração verificado. (O Windows cria no máximo um a cada 24 horas.)", TipoLog.Sucesso);
            }
            else
            {
                ctx.Log("Não foi possível criar o ponto de restauração. Verifique se a Proteção do Sistema está "
                    + "ativada para a unidade do Windows. A otimização continua.", TipoLog.Aviso);
            }
        }

        // ==================================================================
        // ANÁLISE (estimativas, sem alterar nada)
        // ==================================================================

        private static void RegistrarEstimativa(Contexto ctx, long bytes)
        {
            ctx.SomarEstimativa(bytes);
            ctx.Log("Pode liberar até " + Arquivos.FormatarTamanho(bytes) + ".", bytes > 0 ? TipoLog.Sucesso : TipoLog.Normal);
        }

        private static long MedirPastas(Contexto ctx, IEnumerable<string> pastas)
        {
            long total = 0;
            foreach (string pasta in pastas)
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (Directory.Exists(pasta))
                {
                    total += Arquivos.MedirConteudo(new DirectoryInfo(pasta), ctx.Token);
                }
            }
            return total;
        }

        private static Task AnalisarTemporarios(Contexto ctx)
        {
            RegistrarEstimativa(ctx, MedirPastas(ctx, PastasTemporarias()));
            return Concluida;
        }

        private static Task AnalisarLixeira(Contexto ctx)
        {
            long bytes;
            long itens;
            if (NativeMethods.ConsultarLixeira(out bytes, out itens))
            {
                ctx.Log(itens + " item(ns) na Lixeira.");
                RegistrarEstimativa(ctx, bytes);
            }
            else
            {
                ctx.Log("Não foi possível consultar a Lixeira.", TipoLog.Aviso);
            }
            return Concluida;
        }

        private static Task AnalisarNavegadores(Contexto ctx)
        {
            string appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<Navegador> navegadores = LocalizarNavegadores(appDataLocal);
            if (navegadores.Count == 0)
            {
                ctx.Log("Nenhum navegador compatível encontrado (Chrome, Edge, Brave, Firefox).");
                return Concluida;
            }

            long total = 0;
            foreach (Navegador navegador in navegadores)
            {
                long bytes = MedirPastas(ctx, navegador.PastasDeCache);
                total += bytes;
                ctx.Log("  " + navegador.Nome + ": " + Arquivos.FormatarTamanho(bytes)
                    + (navegador.EstaAberto() ? " (está aberto: feche-o antes de limpar)" : ""));
            }

            RegistrarEstimativa(ctx, total);
            return Concluida;
        }

        private static Task AnalisarDespejos(Contexto ctx)
        {
            long total = MedirPastas(ctx, PastasDeDespejos());

            FileInfo despejoCompleto = new FileInfo(ArquivoDeDespejoCompleto());
            if (despejoCompleto.Exists)
            {
                total += despejoCompleto.Length;
            }

            RegistrarEstimativa(ctx, total);
            return Concluida;
        }

        private static Task AnalisarCacheDoWindowsUpdate(Contexto ctx)
        {
            RegistrarEstimativa(ctx, MedirPastas(ctx, new[] { PastaDeDownloadsDoWindowsUpdate() }));
            return Concluida;
        }

        private static async Task AnalisarComponentes(Contexto ctx)
        {
            ctx.Log("Consultando o repositório de componentes (pode levar um ou dois minutos)...");

            int codigo = await Processos.ExecutarAsync(Processos.System32("dism.exe"),
                "/Online /Cleanup-Image /AnalyzeComponentStore", ctx, true);

            if (codigo == 0)
            {
                ctx.Log("O espaço recuperável aparece no relatório acima (não entra na soma da estimativa).");
            }
            else
            {
                ctx.Log("DISM terminou com código " + codigo + ".", TipoLog.Aviso);
            }
        }

        // ==================================================================
        // APIs NATIVAS DO WINDOWS
        // ==================================================================

        private static class NativeMethods
        {
            public const uint SHERB_NOCONFIRMATION = 0x1;
            public const uint SHERB_NOPROGRESSUI = 0x2;
            public const uint SHERB_NOSOUND = 0x4;
            public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

            // A estrutura SHQUERYRBINFO tem alinhamento diferente em 32 e 64 bits.
            [StructLayout(LayoutKind.Sequential)]
            private struct SHQUERYRBINFO64
            {
                public int cbSize;
                public long i64Size;
                public long i64NumItems;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct SHQUERYRBINFO32
            {
                public int cbSize;
                public long i64Size;
                public long i64NumItems;
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
            private static extern int SHQueryRecycleBin64(string pszRootPath, ref SHQUERYRBINFO64 info);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
            private static extern int SHQueryRecycleBin32(string pszRootPath, ref SHQUERYRBINFO32 info);

            /// <summary>Tamanho total e quantidade de itens na Lixeira de todas as unidades.</summary>
            public static bool ConsultarLixeira(out long bytes, out long itens)
            {
                bytes = 0;
                itens = 0;
                int hr;

                if (IntPtr.Size == 8)
                {
                    SHQUERYRBINFO64 info = new SHQUERYRBINFO64();
                    info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO64));
                    hr = SHQueryRecycleBin64(null, ref info);
                    bytes = info.i64Size;
                    itens = info.i64NumItems;
                }
                else
                {
                    SHQUERYRBINFO32 info = new SHQUERYRBINFO32();
                    info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO32));
                    hr = SHQueryRecycleBin32(null, ref info);
                    bytes = info.i64Size;
                    itens = info.i64NumItems;
                }

                return hr == 0;
            }
        }
    }
}
