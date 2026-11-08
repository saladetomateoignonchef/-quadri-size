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
            // Après une mise à jour (« maj git »), l'ancien exécutable est encore là : on le supprime.
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { Updater.CleanupOldExe(exe); });

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
