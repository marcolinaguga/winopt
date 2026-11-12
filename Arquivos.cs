using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace OtimizadorWindows
{
    /// <summary>Contadores de uma limpeza de arquivos.</summary>
    internal sealed class ResultadoLimpeza
    {
        public int ArquivosRemovidos;
        public int ItensIgnorados;
        public long BytesLiberados;
    }

    /// <summary>Exclusão e medição segura de arquivos.</summary>
    internal static class Arquivos
    {
        /// <summary>
        /// Limpa o conteúdo de cada pasta da lista e registra um resumo no log.
        /// Pastas inexistentes são ignoradas em silêncio.
        /// </summary>
        public static void LimparPastas(Contexto ctx, IEnumerable<string> pastas)
        {
            foreach (string pasta in pastas)
            {
                ctx.Token.ThrowIfCancellationRequested();

                if (!Directory.Exists(pasta))
                {
                    continue;
                }

                ResultadoLimpeza resultado = new ResultadoLimpeza();
                ctx.Log("Limpando " + pasta + " ...");
                LimparConteudo(new DirectoryInfo(pasta), resultado, ctx.Token);
                RegistrarResultado(ctx, resultado);
            }
        }

        public static void RegistrarResultado(Contexto ctx, ResultadoLimpeza resultado)
        {
            ctx.Log(string.Format("  {0} arquivo(s) removido(s), {1} liberado(s).",
                resultado.ArquivosRemovidos, FormatarTamanho(resultado.BytesLiberados)), TipoLog.Sucesso);

            if (resultado.ItensIgnorados > 0)
            {
                ctx.Log(string.Format("  {0} item(ns) ignorado(s) por estarem em uso ou protegidos.",
                    resultado.ItensIgnorados), TipoLog.Aviso);
            }
        }

        /// <summary>
        /// Apaga recursivamente o conteúdo de uma pasta (mas não a pasta em si).
        /// Arquivos em uso por outros programas são simplesmente ignorados.
        /// </summary>
        public static void LimparConteudo(DirectoryInfo pasta, ResultadoLimpeza resultado, CancellationToken token)
        {
            FileInfo[] arquivos;
            DirectoryInfo[] subpastas;

            try
            {
                arquivos = pasta.GetFiles();
                subpastas = pasta.GetDirectories();
            }
            catch (Exception ex)
            {
                if (EhFalhaDeAcesso(ex))
                {
                    resultado.ItensIgnorados++;
                    return;
                }
                throw;
            }

            foreach (FileInfo arquivo in arquivos)
            {
                token.ThrowIfCancellationRequested();
                ApagarArquivo(arquivo, resultado);
            }

            foreach (DirectoryInfo subpasta in subpastas)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    // Segurança: junções/links simbólicos apontam para fora da pasta.
                    // Nunca entramos neles; removemos apenas o link, preservando o destino.
                    if (!EhLink(subpasta))
                    {
                        LimparConteudo(subpasta, resultado, token);
                    }

                    subpasta.Delete(false); // só remove se ficou vazia
                }
                catch (Exception ex)
                {
                    // Pasta ainda contém arquivos em uso, ou sem permissão: segue adiante.
                    if (!EhFalhaDeAcesso(ex))
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>Apaga um arquivo; se estiver em uso ou protegido, apenas conta como ignorado.</summary>
        public static void ApagarArquivo(FileInfo arquivo, ResultadoLimpeza resultado)
        {
            try
            {
                long tamanho = arquivo.Length;
                arquivo.Attributes = FileAttributes.Normal; // remove "somente leitura", se houver
                arquivo.Delete();
                resultado.ArquivosRemovidos++;
                resultado.BytesLiberados += tamanho;
            }
            catch (Exception ex)
            {
                // IOException = arquivo em uso; UnauthorizedAccessException = sem permissão.
                if (!EhFalhaDeAcesso(ex))
                {
                    throw;
                }
                resultado.ItensIgnorados++;
            }
        }

        /// <summary>Soma o tamanho de todos os arquivos de uma pasta, sem alterar nada.</summary>
        public static long MedirConteudo(DirectoryInfo pasta, CancellationToken token)
        {
            long total = 0;
            FileInfo[] arquivos;
            DirectoryInfo[] subpastas;

            try
            {
                arquivos = pasta.GetFiles();
                subpastas = pasta.GetDirectories();
            }
            catch (Exception ex)
            {
                if (EhFalhaDeAcesso(ex))
                {
                    return 0;
                }
                throw;
            }

            foreach (FileInfo arquivo in arquivos)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    total += arquivo.Length;
                }
                catch (Exception ex)
                {
                    if (!EhFalhaDeAcesso(ex))
                    {
                        throw;
                    }
                }
            }

            foreach (DirectoryInfo subpasta in subpastas)
            {
                token.ThrowIfCancellationRequested();
                if (!EhLink(subpasta))
                {
                    total += MedirConteudo(subpasta, token);
                }
            }

            return total;
        }

        public static string FormatarTamanho(long bytes)
        {
            string[] unidades = { "B", "KB", "MB", "GB", "TB" };
            double valor = bytes;
            int indice = 0;

            while (valor >= 1024 && indice < unidades.Length - 1)
            {
                valor /= 1024;
                indice++;
            }

            return valor.ToString(indice == 0 ? "0" : "0.0") + " " + unidades[indice];
        }

        private static bool EhLink(DirectoryInfo pasta)
        {
            return (pasta.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static bool EhFalhaDeAcesso(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException;
        }
    }
}
