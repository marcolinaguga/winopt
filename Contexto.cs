using System;
using System.Threading;

namespace Winopt
{
    /// <summary>Tipo de mensagem do log (define a cor exibida no console).</summary>
    internal enum TipoLog
    {
        Normal,
        Titulo,
        Sucesso,
        Aviso,
        Erro
    }

    /// <summary>
    /// Tudo o que uma etapa precisa para conversar com a interface sem conhecê-la:
    /// escrever no log, informar progresso e saber se o usuário cancelou.
    /// </summary>
    internal sealed class Contexto
    {
        private readonly Action<string, TipoLog> _log;
        private readonly Action<int> _progresso;
        private long _bytesEstimados;

        public Contexto(CancellationToken token, Action<string, TipoLog> log, Action<int> progresso)
        {
            Token = token;
            _log = log;
            _progresso = progresso;
        }

        /// <summary>Sinaliza o pedido de cancelamento feito pelo usuário.</summary>
        public CancellationToken Token { get; private set; }

        /// <summary>Total que as etapas analisadas estimam poder liberar (modo "Analisar").</summary>
        public long BytesEstimados
        {
            get { return Interlocked.Read(ref _bytesEstimados); }
        }

        public void Log(string mensagem)
        {
            _log(mensagem, TipoLog.Normal);
        }

        public void Log(string mensagem, TipoLog tipo)
        {
            _log(mensagem, tipo);
        }

        /// <summary>Informa o progresso da etapa atual (0 a 100). Use -1 para "indeterminado".</summary>
        public void Progresso(int porcentagem)
        {
            _progresso(porcentagem);
        }

        public void SomarEstimativa(long bytes)
        {
            Interlocked.Add(ref _bytesEstimados, bytes);
        }
    }
}
