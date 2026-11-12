using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Winopt
{
    /// <summary>
    /// Janela principal: menu de ferramentas agrupadas, botões de ação, progresso e console de log.
    /// A interface é montada em código (sem Form1.Designer.cs).
    /// </summary>
    public class Form1 : Form
    {
        // ------------------------------------------------------------------
        // Controles da interface
        // ------------------------------------------------------------------
        private readonly Dictionary<Etapa, CheckBox> _caixas = new Dictionary<Etapa, CheckBox>();
        private Button btnOtimizar;
        private Button btnAnalisar;
        private Button btnCancelar;
        private CheckBox chkPontoDeRestauracao;
        private LinkLabel lnkLogs;
        private ProgressBar barraProgresso;
        private Label lblStatus;
        private RichTextBox txtLog;

        // ------------------------------------------------------------------
        // Estado da execução
        // ------------------------------------------------------------------
        private readonly List<Etapa> _etapas;
        private CancellationTokenSource _cancelamento;
        private bool _emExecucao;
        private string _statusEtapa = "";
        private StreamWriter _arquivoLog;

        public Form1() : this(Etapas.CriarCatalogo())
        {
        }

        /// <summary>Permite montar a janela com outra lista de etapas (usado nos testes).</summary>
        internal Form1(List<Etapa> etapas)
        {
            _etapas = etapas;
            MontarInterface();
        }

        /// <summary>Pasta onde o log de cada execução é salvo.</summary>
        internal static string PastaDeLogs
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Winopt", "Logs");
            }
        }

        // ==================================================================
        // INTERFACE
        // ==================================================================

        private void MontarInterface()
        {
            SuspendLayout();

            Text = "Winopt";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(880, 680);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            CarregarIcone();

            // Grade principal: cada linha se ajusta ao conteúdo; o log ocupa o que sobrar.
            TableLayoutPanel raiz = new TableLayoutPanel();
            raiz.Dock = DockStyle.Fill;
            raiz.Padding = new Padding(12);
            raiz.ColumnCount = 1;
            raiz.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            raiz.RowCount = 6;
            raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // título
            raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // grupos de ferramentas
            raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // botões
            raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // barra de progresso
            raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // status
            raiz.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));  // log

            Label lblTitulo = new Label();
            lblTitulo.Text = "Selecione as ferramentas que deseja executar:";
            lblTitulo.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            lblTitulo.AutoSize = true;
            lblTitulo.Margin = new Padding(3, 0, 3, 8);

            barraProgresso = new ProgressBar();
            barraProgresso.Dock = DockStyle.Fill;
            barraProgresso.Height = 14;
            barraProgresso.Margin = new Padding(3, 8, 3, 2);
            barraProgresso.MarqueeAnimationSpeed = 30;

            lblStatus = new Label();
            lblStatus.Text = "Pronto.";
            lblStatus.AutoSize = false;
            lblStatus.AutoEllipsis = true;
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.Height = 20;
            lblStatus.Margin = new Padding(3, 0, 3, 6);

            txtLog = new RichTextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.FromArgb(24, 24, 24);
            txtLog.ForeColor = Color.Gainsboro;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.HideSelection = false;
            txtLog.DetectUrls = false;

            raiz.Controls.Add(lblTitulo, 0, 0);
            raiz.Controls.Add(CriarPainelDeGrupos(), 0, 1);
            raiz.Controls.Add(CriarPainelDeBotoes(), 0, 2);
            raiz.Controls.Add(barraProgresso, 0, 3);
            raiz.Controls.Add(lblStatus, 0, 4);
            raiz.Controls.Add(txtLog, 0, 5);
            Controls.Add(raiz);

            AcceptButton = btnOtimizar;
            FormClosing += Form1_FormClosing;

            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>Usa na janela e na barra de tarefas o mesmo ícone embutido no executável.</summary>
        private void CarregarIcone()
        {
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Sem o ícone a janela usa o padrão do Windows; não é motivo para falhar.
            }
        }

        /// <summary>
        /// Grupos de checkboxes: "Limpeza" à esquerda (duas colunas) e, à direita,
        /// "Reparo" sobre "Otimização". Grupos sem ferramentas não são criados.
        /// </summary>
        private Control CriarPainelDeGrupos()
        {
            TableLayoutPanel grade = new TableLayoutPanel();
            grade.Dock = DockStyle.Fill;
            grade.AutoSize = true;
            grade.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grade.Margin = new Padding(0);
            grade.ColumnCount = 2;
            grade.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));
            grade.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            grade.RowCount = 2;
            grade.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grade.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            GroupBox limpeza = CriarGrupo("Limpeza", GrupoEtapa.Limpeza, 2);
            GroupBox reparo = CriarGrupo("Reparo", GrupoEtapa.Reparo, 1);
            GroupBox otimizacao = CriarGrupo("Otimização", GrupoEtapa.Otimizacao, 1);

            if (limpeza != null)
            {
                grade.Controls.Add(limpeza, 0, 0);
                grade.SetRowSpan(limpeza, 2);
            }

            if (reparo != null)
            {
                grade.Controls.Add(reparo, 1, 0);
            }

            if (otimizacao != null)
            {
                grade.Controls.Add(otimizacao, 1, 1);
            }

            return grade;
        }

        private GroupBox CriarGrupo(string titulo, GrupoEtapa grupo, int colunas)
        {
            List<Etapa> doGrupo = _etapas.FindAll(e => e.Grupo == grupo);
            if (doGrupo.Count == 0)
            {
                return null;
            }

            TableLayoutPanel painel = new TableLayoutPanel();
            painel.Dock = DockStyle.Top;
            painel.AutoSize = true;
            painel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            painel.ColumnCount = colunas;
            for (int c = 0; c < colunas; c++)
            {
                painel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / colunas));
            }

            // Preenche coluna por coluna, para a leitura de cima para baixo seguir a ordem de execução.
            int linhas = (doGrupo.Count + colunas - 1) / colunas;
            painel.RowCount = linhas;
            for (int i = 0; i < doGrupo.Count; i++)
            {
                Etapa etapa = doGrupo[i];
                CheckBox caixa = new CheckBox();
                caixa.Text = etapa.Nome;
                caixa.AutoSize = true;
                caixa.Checked = etapa.MarcadaPorPadrao;
                caixa.Margin = new Padding(6, 3, 3, 3);
                _caixas[etapa] = caixa;
                painel.Controls.Add(caixa, i / linhas, i % linhas);
            }

            GroupBox caixaDeGrupo = new GroupBox();
            caixaDeGrupo.Text = titulo;
            caixaDeGrupo.Dock = DockStyle.Fill;
            caixaDeGrupo.AutoSize = true;
            caixaDeGrupo.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            caixaDeGrupo.Padding = new Padding(6, 4, 6, 6);
            caixaDeGrupo.Controls.Add(painel);
            return caixaDeGrupo;
        }

        private Control CriarPainelDeBotoes()
        {
            FlowLayoutPanel painel = new FlowLayoutPanel();
            painel.Dock = DockStyle.Fill;
            painel.AutoSize = true;
            painel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            painel.WrapContents = false;
            painel.Margin = new Padding(0, 8, 0, 0);

            btnOtimizar = new Button();
            btnOtimizar.Text = "Otimizar Sistema";
            btnOtimizar.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnOtimizar.Size = new Size(170, 38);
            btnOtimizar.Click += BtnOtimizar_Click;

            // "Analisar" mostra quanto cada limpeza liberaria, sem apagar nada.
            btnAnalisar = new Button();
            btnAnalisar.Text = "Analisar";
            btnAnalisar.Size = new Size(110, 38);
            btnAnalisar.Click += BtnAnalisar_Click;

            btnCancelar = new Button();
            btnCancelar.Text = "Cancelar";
            btnCancelar.Size = new Size(110, 38);
            btnCancelar.Enabled = false;
            btnCancelar.Click += BtnCancelar_Click;

            chkPontoDeRestauracao = new CheckBox();
            chkPontoDeRestauracao.Text = "Criar ponto de restauração antes de reparos";
            chkPontoDeRestauracao.AutoSize = true;
            chkPontoDeRestauracao.Checked = true;
            chkPontoDeRestauracao.Margin = new Padding(12, 12, 3, 0);

            lnkLogs = new LinkLabel();
            lnkLogs.Text = "Abrir pasta de logs";
            lnkLogs.AutoSize = true;
            lnkLogs.Margin = new Padding(12, 13, 3, 0);
            lnkLogs.LinkClicked += LnkLogs_LinkClicked;

            painel.Controls.Add(btnOtimizar);
            painel.Controls.Add(btnAnalisar);
            painel.Controls.Add(btnCancelar);
            painel.Controls.Add(chkPontoDeRestauracao);
            painel.Controls.Add(lnkLogs);
            return painel;
        }

        /// <summary>Habilita/desabilita os controles enquanto a otimização (ou a análise) roda.</summary>
        private void DefinirEstadoExecucao(bool executando, bool analisando = false)
        {
            _emExecucao = executando;
            btnOtimizar.Enabled = !executando;
            btnAnalisar.Enabled = !executando;
            chkPontoDeRestauracao.Enabled = !executando;
            btnCancelar.Enabled = executando;
            btnCancelar.Text = "Cancelar";
            btnOtimizar.Text = executando && !analisando ? "Executando..." : "Otimizar Sistema";
            btnAnalisar.Text = executando && analisando ? "Analisando..." : "Analisar";

            foreach (CheckBox caixa in _caixas.Values)
            {
                caixa.Enabled = !executando;
            }

            if (!executando)
            {
                barraProgresso.Style = ProgressBarStyle.Blocks;
                barraProgresso.Value = 0;
            }
        }

        // ==================================================================
        // FLUXO PRINCIPAL
        // ==================================================================

        private async void BtnOtimizar_Click(object sender, EventArgs e)
        {
            await ExecutarFilaAsync(false);
        }

        private async void BtnAnalisar_Click(object sender, EventArgs e)
        {
            await ExecutarFilaAsync(true);
        }

        /// <summary>
        /// Percorre as ferramentas marcadas, em sequência. No modo análise, cada ferramenta
        /// apenas estima o espaço que liberaria; nada é alterado no sistema.
        /// </summary>
        private async Task ExecutarFilaAsync(bool analisar)
        {
            if (_emExecucao)
            {
                return;
            }

            // Lê as escolhas na thread da interface antes de iniciar o trabalho em background.
            List<Etapa> selecionadas = _etapas.FindAll(etapa => _caixas[etapa].Checked);
            if (selecionadas.Count == 0)
            {
                MessageBox.Show(this, "Selecione pelo menos uma ferramenta.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // O ponto de restauração só vale a pena quando alguma ferramenta mexe em componentes do Windows.
            bool criarPonto = !analisar && chkPontoDeRestauracao.Checked
                && selecionadas.Exists(etapa => etapa.AlteraSistema);
            string operacao = analisar ? "Análise" : "Otimização";

            DefinirEstadoExecucao(true, analisar);
            txtLog.Clear();
            AbrirArquivoDeLog(analisar ? "analise" : "otimizacao");

            _cancelamento = new CancellationTokenSource();
            Contexto ctx = new Contexto(_cancelamento.Token, Log, DefinirProgresso);
            Stopwatch cronometro = Stopwatch.StartNew();
            long livreAntes = EspacoLivreNoDiscoDoSistema();
            bool cancelado = false;

            Log(analisar ? "Iniciando análise (nada será apagado)..." : "Iniciando otimização...", TipoLog.Titulo);

            try
            {
                if (criarPonto)
                {
                    cancelado = !await ExecutarPassoAsync("Ponto de Restauração",
                        "[preparação] Ponto de Restauração", Etapas.CriarPontoDeRestauracao, ctx, false);
                }

                for (int i = 0; i < selecionadas.Count && !cancelado; i++)
                {
                    Etapa etapa = selecionadas[i];
                    string status = string.Format("[{0}/{1}] {2}", i + 1, selecionadas.Count, etapa.Nome);
                    cancelado = !await ExecutarPassoAsync(etapa.Nome, status,
                        analisar ? etapa.Analisar : etapa.Executar, ctx, analisar);
                }

                Log("", TipoLog.Normal);

                if (analisar)
                {
                    Log("Estimativa total: até " + Arquivos.FormatarTamanho(ctx.BytesEstimados)
                        + " podem ser liberados. Nada foi apagado.", TipoLog.Sucesso);
                }
                else
                {
                    RegistrarEspacoLiberado(livreAntes);
                }

                if (cancelado)
                {
                    Log(operacao + " cancelada pelo usuário após " + FormatarDuracao(cronometro.Elapsed) + ".", TipoLog.Aviso);
                }
                else
                {
                    Log(operacao + " concluída em " + FormatarDuracao(cronometro.Elapsed) + ".", TipoLog.Sucesso);
                }
            }
            catch (Exception ex)
            {
                // Rede de segurança: nenhuma exceção deve derrubar o aplicativo.
                Log("Erro inesperado: " + ex.Message, TipoLog.Erro);
            }
            finally
            {
                FecharArquivoDeLog();
                _cancelamento.Dispose();
                _cancelamento = null;

                if (!IsDisposed)
                {
                    DefinirEstadoExecucao(false);
                    lblStatus.Text = cancelado ? "Cancelado." : "Pronto.";
                }
            }
        }

        /// <summary>
        /// Executa um passo da fila isolando suas falhas: se uma ferramenta falhar, as próximas
        /// ainda rodam. Retorna false se o usuário cancelou.
        /// </summary>
        private async Task<bool> ExecutarPassoAsync(string nome, string status,
            Func<Contexto, Task> acao, Contexto ctx, bool analisar)
        {
            if (ctx.Token.IsCancellationRequested)
            {
                return false;
            }

            _statusEtapa = status;
            Log("", TipoLog.Normal);
            Log("=== " + nome + " ===", TipoLog.Titulo);
            DefinirProgresso(-1);

            if (acao == null)
            {
                Log(analisar
                    ? "Sem estimativa: o ganho desta ferramenta só é conhecido ao executar."
                    : "Ferramenta indisponível.", TipoLog.Normal);
                return true;
            }

            try
            {
                // Cada passo roda fora da thread da interface, para a janela não travar.
                await Task.Run(() => acao(ctx));
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log("Falha em \"" + nome + "\": " + ex.Message, TipoLog.Erro);
            }

            return !ctx.Token.IsCancellationRequested;
        }

        private void BtnCancelar_Click(object sender, EventArgs e)
        {
            SolicitarCancelamento();
        }

        /// <summary>Pede a interrupção da fila e encerra o utilitário em andamento.</summary>
        private void SolicitarCancelamento()
        {
            CancellationTokenSource cancelamento = _cancelamento;
            if (cancelamento == null || cancelamento.IsCancellationRequested)
            {
                return;
            }

            btnCancelar.Enabled = false;
            btnCancelar.Text = "Cancelando...";
            Log("Cancelamento solicitado. Encerrando a etapa atual...", TipoLog.Aviso);

            // Encerrar o processo em andamento leva alguns instantes: faz fora da thread da interface.
            Task.Run(() =>
            {
                try
                {
                    cancelamento.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A execução terminou antes do cancelamento: nada a fazer.
                }
            });
        }

        private static long EspacoLivreNoDiscoDoSistema()
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)).TotalFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private void RegistrarEspacoLiberado(long livreAntes)
        {
            long livreDepois = EspacoLivreNoDiscoDoSistema();
            if (livreAntes < 0 || livreDepois < 0)
            {
                return;
            }

            string unidade = Path.GetPathRoot(Environment.SystemDirectory);
            long liberado = livreDepois - livreAntes;

            Log(string.Format("Espaço livre em {0} antes: {1} | depois: {2}", unidade,
                Arquivos.FormatarTamanho(livreAntes), Arquivos.FormatarTamanho(livreDepois)), TipoLog.Normal);

            if (liberado > 0)
            {
                Log("Total liberado: " + Arquivos.FormatarTamanho(liberado), TipoLog.Sucesso);
            }
            else
            {
                Log("Sem ganho de espaço mensurável nesta execução.", TipoLog.Normal);
            }
        }

        // ==================================================================
        // PROGRESSO
        // ==================================================================

        /// <summary>
        /// Atualiza barra e status. Valor de 0 a 100 mostra progresso real;
        /// -1 mostra a animação contínua. Pode ser chamado de qualquer thread.
        /// </summary>
        private void DefinirProgresso(int porcentagem)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int>(DefinirProgresso), porcentagem);
                }
                catch (InvalidOperationException)
                {
                    // A janela foi fechada durante a execução: ignora.
                }
                return;
            }

            if (!_emExecucao)
            {
                return; // atualização atrasada de uma execução já encerrada
            }

            if (porcentagem < 0)
            {
                barraProgresso.Style = ProgressBarStyle.Marquee;
                lblStatus.Text = _statusEtapa + "...";
            }
            else
            {
                barraProgresso.Style = ProgressBarStyle.Blocks;
                barraProgresso.Value = Math.Min(100, porcentagem);
                lblStatus.Text = _statusEtapa + " — " + porcentagem + "%";
            }
        }

        // ==================================================================
        // LOG
        // ==================================================================

        /// <summary>Escreve uma linha no console e no arquivo de log. Pode ser chamado de qualquer thread.</summary>
        private void Log(string mensagem, TipoLog tipo)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<string, TipoLog>(Log), mensagem, tipo);
                }
                catch (InvalidOperationException)
                {
                    // A janela foi fechada enquanto o processo ainda escrevia: ignora.
                }
                return;
            }

            string texto = mensagem.Length == 0
                ? ""
                : "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + mensagem;

            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = CorDoLog(tipo);
            txtLog.AppendText(texto + Environment.NewLine);
            txtLog.ScrollToCaret();

            if (_arquivoLog != null)
            {
                try
                {
                    _arquivoLog.WriteLine(texto);
                }
                catch (IOException)
                {
                    // Falha ao gravar o arquivo não deve interromper a otimização.
                }
            }
        }

        private static Color CorDoLog(TipoLog tipo)
        {
            switch (tipo)
            {
                case TipoLog.Titulo: return Color.DeepSkyBlue;
                case TipoLog.Sucesso: return Color.LightGreen;
                case TipoLog.Aviso: return Color.Khaki;
                case TipoLog.Erro: return Color.Salmon;
                default: return Color.Gainsboro;
            }
        }

        private void AbrirArquivoDeLog(string prefixo)
        {
            try
            {
                Directory.CreateDirectory(PastaDeLogs);
                string caminho = Path.Combine(PastaDeLogs,
                    prefixo + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
                _arquivoLog = new StreamWriter(caminho, false, Encoding.UTF8);
                _arquivoLog.AutoFlush = true; // o arquivo fica completo mesmo se o programa for encerrado
            }
            catch (Exception)
            {
                _arquivoLog = null; // sem arquivo de log, o programa segue só com o console
            }
        }

        private void FecharArquivoDeLog()
        {
            if (_arquivoLog != null)
            {
                _arquivoLog.Dispose();
                _arquivoLog = null;
            }
        }

        private void LnkLogs_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(PastaDeLogs);
                Process.Start("explorer.exe", "\"" + PastaDeLogs + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Não foi possível abrir a pasta de logs:\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string FormatarDuracao(TimeSpan duracao)
        {
            return duracao.TotalMinutes >= 1
                ? string.Format("{0} min {1} s", (int)duracao.TotalMinutes, duracao.Seconds)
                : string.Format("{0} s", duracao.Seconds);
        }

        // ==================================================================
        // FECHAMENTO DA JANELA
        // ==================================================================

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_emExecucao)
            {
                return;
            }

            DialogResult resposta = MessageBox.Show(this,
                "A otimização ainda está em andamento.\nDeseja interromper e sair?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (resposta != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Encerra o utilitário em andamento antes de sair, para não deixá-lo órfão.
            CancellationTokenSource cancelamento = _cancelamento;
            if (cancelamento != null)
            {
                try
                {
                    cancelamento.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A execução terminou neste meio-tempo.
                }
            }
        }
    }
}
