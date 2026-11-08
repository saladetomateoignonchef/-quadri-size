using System;
using System.Windows.Forms;

namespace QuadriSize
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // Les droits administrateur et la netteté à haute résolution sont demandés
            // dans app.manifest, intégré à l'exécutable par build.bat.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
