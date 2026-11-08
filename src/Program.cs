using System;
using System.Windows;
using System.Windows.Threading;

namespace QuadriSize
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // Les droits administrateur et la netteté à haute résolution sont demandés
            // dans app.manifest, intégré à l'exécutable par build.bat.
            var ui = new MainUi();
            Application.Current.DispatcherUnhandledException += (object sender, DispatcherUnhandledExceptionEventArgs e) =>
            {
                Dialogs.Show(ui.Window, DialogKind.Error, "Erreur inattendue",
                    "Quelque chose s'est mal passé. Tu peux continuer à utiliser Quadri-Size.", e.Exception.Message);
                e.Handled = true;
            };
            Application.Current.Run(ui.Window);
        }
    }
}
