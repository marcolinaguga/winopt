using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OtimizadorWindows
{
    /// <summary>Execução silenciosa de utilitários do Windows, com captura da saída.</summary>
    internal static class Processos
    {
        // Encontra porcentagens como "32%", "20.0%" ou "20,0 %".
        private static readonly Regex RegexPorcentagem =
            new Regex(@"(\d{1,3})(?:[.,]\d+)?\s*%", RegexOptions.Compiled);

        /// <summary>
        /// Caminho completo de um utilitário do System32. Se o aplicativo rodar em 32 bits em um
        /// Windows 64 bits, usa "Sysnative" para alcançar as versões reais de 64 bits.
        /// </summary>
        public static string System32(string executavel)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pasta = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? "Sysnative"
                : "System32";
            return Path.Combine(windows, pasta, executavel);
        }

        /// <summary>
        /// Executa um utilitário sem janela, enviando cada linha de saída para o log.
        /// Com <paramref name="lerProgresso"/>, linhas de porcentagem alimentam a barra de
        /// progresso em vez de irem para o log. Retorna o código de saída do processo.
        /// </summary>
        public static Task<int> ExecutarAsync(string executavel, string argumentos, Contexto ctx, bool lerProgresso)
        {
            return Task.Run(() =>
            {
                ctx.Token.ThrowIfCancellationRequested();

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = executavel;
                info.Arguments = argumentos;
                info.UseShellExecute = false;        // necessário para redirecionar a saída
                info.CreateNoWindow = true;          // não abre janela de prompt
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                object trava = new object();
                string ultimaLinha = null;

                // Chamado a cada linha emitida pelo processo (em threads de background).
                Action<string> aoLerLinha = bruta =>
                {
                    string linha = bruta.Replace("\0", "").Replace("﻿", "").Trim();
                    if (linha.Length == 0)
                    {
                        return;
                    }

                    int porcentagem;
                    if (lerProgresso && TentarLerPorcentagem(linha, out porcentagem))
                    {
                        ctx.Progresso(porcentagem);
                        return;
                    }

                    // Evita repetir linhas idênticas em sequência.
                    lock (trava)
                    {
                        if (linha == ultimaLinha)
                        {
                            return;
                        }
                        ultimaLinha = linha;
                    }

                    ctx.Log("  " + linha);
                };

                using (Process processo = new Process())
                {
                    processo.StartInfo = info;
                    processo.Start();

                    // Se o usuário cancelar, encerra o utilitário e os processos filhos dele.
                    using (ctx.Token.Register(() => EncerrarArvore(processo)))
                    {
                        Stream saida = processo.StandardOutput.BaseStream;
                        Stream erros = processo.StandardError.BaseStream;
                        Task leituraSaida = Task.Run(() => LerFluxo(saida, aoLerLinha));
                        Task leituraErros = Task.Run(() => LerFluxo(erros, aoLerLinha));

                        processo.WaitForExit();

                        // Limite de espera: um processo filho pode manter o canal de saída aberto.
                        Task.WaitAll(new[] { leituraSaida, leituraErros }, 5000);
                    }

                    ctx.Token.ThrowIfCancellationRequested();
                    return processo.ExitCode;
                }
            });
        }

        /// <summary>Extrai a última porcentagem (0 a 100) de uma linha curta de progresso.</summary>
        internal static bool TentarLerPorcentagem(string linha, out int porcentagem)
        {
            porcentagem = 0;

            // Linhas longas ou no formato "campo = 5%" são resultados (ex.: relatório do defrag), não progresso.
            if (linha.Length > 160 || linha.Contains(" = "))
            {
                return false;
            }

            MatchCollection achados = RegexPorcentagem.Matches(linha);
            if (achados.Count == 0)
            {
                return false;
            }

            int valor = int.Parse(achados[achados.Count - 1].Groups[1].Value);
            if (valor > 100)
            {
                return false;
            }

            porcentagem = valor;
            return true;
        }

        // Letras acentuadas comuns em texto corrido: servem para decidir entre as páginas de código.
        private const string LetrasAcentuadas = "áàâãäåçéèêëíìîïñóòôõöúùûüÁÀÂÃÉÊÍÓÔÕÚÇ";

        private static readonly Encoding Utf8Estrito = new UTF8Encoding(false, true);

        /// <summary>
        /// Indica se a amostra inicial da saída está em UTF-16 (caso do sfc).
        /// Em UTF-16 com texto ocidental, quase todo byte de posição ímpar é zero.
        /// </summary>
        internal static bool EhUtf16(byte[] dados, int quantidade)
        {
            if (quantidade >= 2 && dados[0] == 0xFF && dados[1] == 0xFE)
            {
                return true;
            }

            int pares = Math.Min(quantidade, 256) / 2;
            int zeros = 0;
            for (int i = 0; i < pares; i++)
            {
                if (dados[i * 2 + 1] == 0)
                {
                    zeros++;
                }
            }

            return pares > 0 && zeros * 10 >= pares * 4;
        }

        /// <summary>
        /// Converte em texto uma linha de saída de 8 bits. Os utilitários do Windows não seguem um
        /// padrão: dism e ipconfig usam a página de código OEM do console (850 em pt-BR), o chkdsk
        /// usa a ANSI (1252) e ferramentas mais novas usam UTF-8. Como não há marcação, a linha é
        /// decodificada das formas possíveis e vence a que produz mais letras acentuadas válidas.
        /// </summary>
        internal static string DecodificarLinha(byte[] dados, int quantidade)
        {
            bool somenteAscii = true;
            for (int i = 0; i < quantidade; i++)
            {
                if (dados[i] >= 0x80)
                {
                    somenteAscii = false;
                    break;
                }
            }

            if (somenteAscii)
            {
                return Encoding.ASCII.GetString(dados, 0, quantidade);
            }

            try
            {
                return Utf8Estrito.GetString(dados, 0, quantidade); // só passa se for UTF-8 válido
            }
            catch (ArgumentException)
            {
                // Não é UTF-8: decide entre OEM e ANSI abaixo.
            }

            string oem = Encoding.GetEncoding((int)GetOEMCP()).GetString(dados, 0, quantidade);
            string ansi = Encoding.Default.GetString(dados, 0, quantidade);
            return ContarAcentuadas(ansi) > ContarAcentuadas(oem) ? ansi : oem;
        }

        private static int ContarAcentuadas(string texto)
        {
            int total = 0;
            foreach (char c in texto)
            {
                if (LetrasAcentuadas.IndexOf(c) >= 0)
                {
                    total++;
                }
            }
            return total;
        }

        /// <summary>Lê um fluxo de saída até o fim, entregando uma linha por vez.</summary>
        private static void LerFluxo(Stream fluxo, Action<string> aoLerLinha)
        {
            try
            {
                byte[] buffer = new byte[4096];
                int quantidade = 0;
                int lidos;

                // Junta uma pequena amostra inicial para saber se a saída é UTF-16.
                while (quantidade < 4 && (lidos = fluxo.Read(buffer, quantidade, buffer.Length - quantidade)) > 0)
                {
                    quantidade += lidos;
                }

                if (quantidade == 0)
                {
                    return;
                }

                // Em ambos os formatos, "\r" sozinho também encerra linha: é como os
                // utilitários redesenham o progresso na mesma linha do console.
                if (EhUtf16(buffer, quantidade))
                {
                    Decoder decodificador = Encoding.Unicode.GetDecoder();
                    char[] caracteres = new char[Encoding.Unicode.GetMaxCharCount(buffer.Length)];
                    StringBuilder linha = new StringBuilder();

                    do
                    {
                        int total = decodificador.GetChars(buffer, 0, quantidade, caracteres, 0);
                        for (int i = 0; i < total; i++)
                        {
                            char c = caracteres[i];
                            if (c == '\r' || c == '\n')
                            {
                                if (linha.Length > 0)
                                {
                                    aoLerLinha(linha.ToString());
                                    linha.Length = 0;
                                }
                            }
                            else
                            {
                                linha.Append(c);
                            }
                        }
                    }
                    while ((quantidade = fluxo.Read(buffer, 0, buffer.Length)) > 0);

                    if (linha.Length > 0)
                    {
                        aoLerLinha(linha.ToString());
                    }
                }
                else
                {
                    MemoryStream linha = new MemoryStream();

                    do
                    {
                        for (int i = 0; i < quantidade; i++)
                        {
                            byte b = buffer[i];
                            if (b == (byte)'\r' || b == (byte)'\n')
                            {
                                if (linha.Length > 0)
                                {
                                    aoLerLinha(DecodificarLinha(linha.GetBuffer(), (int)linha.Length));
                                    linha.SetLength(0);
                                }
                            }
                            else
                            {
                                linha.WriteByte(b);
                            }
                        }
                    }
                    while ((quantidade = fluxo.Read(buffer, 0, buffer.Length)) > 0);

                    if (linha.Length > 0)
                    {
                        aoLerLinha(DecodificarLinha(linha.GetBuffer(), (int)linha.Length));
                    }
                }
            }
            catch (IOException)
            {
                // O processo foi encerrado durante a leitura: nada mais a ler.
            }
            catch (ObjectDisposedException)
            {
                // Idem: o fluxo foi fechado junto com o processo.
            }
        }

        /// <summary>Encerra o processo e todos os filhos (ex.: o dismhost.exe criado pelo DISM).</summary>
        private static void EncerrarArvore(Process processo)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = System32("taskkill.exe");
                info.Arguments = "/PID " + processo.Id + " /T /F";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;

                using (Process taskkill = Process.Start(info))
                {
                    taskkill.WaitForExit(10000);
                }
            }
            catch (Exception)
            {
                // Se o taskkill falhar, tenta ao menos encerrar o processo principal.
                try
                {
                    processo.Kill();
                }
                catch (Exception)
                {
                    // O processo já terminou: nada a fazer.
                }
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }
}
