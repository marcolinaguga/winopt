using System;
using System.Windows.Forms;

namespace Winopt
{
    internal static class Program
    {
        /// <summary>Ponto de entrada do aplicativo.</summary>
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }
    }
}
