using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuadriSize
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main()
        {
            // Texte net sur les écrans à haute résolution (sinon Windows étire une image floue).
            if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
