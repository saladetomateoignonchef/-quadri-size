using System;
using System.IO;
using System.Linq;

namespace QuadriSize.Tests
{
    /// <summary>Crée une petite arborescence aux tailles connues et vérifie ce que trouve le scanner.</summary>
    public static class ScanTest
    {
        private static int _failures;

        public static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "quadri-size-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                //  root/
                //    gros/a.bin       6000 o
                //    gros/sous/b.bin  2000 o
                //    petit/c.bin      1500 o
                //    vide/
                //    racine.txt        500 o
                Write(root, @"gros\a.bin", 6000);
                Write(root, @"gros\sous\b.bin", 2000);
                Write(root, @"petit\c.bin", 1500);
                Directory.CreateDirectory(Path.Combine(root, "vide"));
                Write(root, "racine.txt", 500);

                var scanner = new Scanner();
                DirNode node = scanner.Scan(root);

                Check("taille totale", node.Size, 10000L);
                Check("nombre de fichiers", node.FileCount, 4L);
                Check("nombre de dossiers", node.DirCount, 4L);
                Check("fichiers comptés", scanner.FilesScanned, 4L);
                Check("dossiers refusés", scanner.DeniedCount, 0L);

                DirNode gros = node.Dirs.Single(d => d.Name == "gros");
                Check("taille de gros", gros.Size, 8000L);
                Check("% de gros", Math.Round(Format.Percent(gros.Size, node.Size), 1), 80.0);
                Check("taille de gros/sous", gros.Dirs.Single().Size, 2000L);
                Check("taille de vide", node.Dirs.Single(d => d.Name == "vide").Size, 0L);
                Check("fichier racine", node.Files.Single().Size, 500L);

                Check("format 500 o", Format.Size(500), "500 o");
                Check("format 1,5 Ko", Format.Size(1536), (1.5).ToString("0.00") + " Ko");
                Check("format 2 Go", Format.Size(2L * 1024 * 1024 * 1024), (2.0).ToString("0.00") + " Go");
                Check("% sur un total nul", Format.Percent(10, 0), 0.0);

                // Après une suppression, les totaux de tous les parents suivent.
                DirNode sous = gros.Dirs.Single();
                gros.RemoveDir(sous);
                Check("gros après retrait de sous", gros.Size, 6000L);
                Check("total après retrait de sous", node.Size, 8000L);
                Check("fichiers après retrait de sous", node.FileCount, 3L);
                Check("dossiers après retrait de sous", node.DirCount, 3L);

                node.RemoveFile(node.Files.Single());
                Check("total après retrait du fichier racine", node.Size, 7500L);
                Check("fichiers après retrait du fichier racine", node.FileCount, 2L);

                // Rescan d'un dossier qui a changé sur le disque.
                File.Delete(Path.Combine(root, @"gros\a.bin"));
                Write(root, @"gros\nouveau.bin", 100);
                DirNode grosRescanne = scanner.Scan(Path.Combine(root, "gros"), node);
                node.ReplaceDir(gros, grosRescanne);
                Check("gros rescanné", grosRescanne.Size, 2100L); // nouveau.bin + sous/b.bin toujours sur le disque
                Check("total après rescan", node.Size, 3600L);
                Check("dossiers après rescan", node.DirCount, 4L);

                CheckSplit("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", @"C:\Program Files\App\unins000.exe", "/SILENT");
                CheckSplit("MsiExec.exe /X{1234-ABCD}", "MsiExec.exe", "/X{1234-ABCD}");
                CheckSplit(@"C:\Program Files\App\uninstall.exe --remove", @"C:\Program Files\App\uninstall.exe", "--remove");
                CheckSplit(@"C:\App\setup.exe", @"C:\App\setup.exe", "");

                var programs = new[]
                {
                    new InstalledProgram { Name = "VLC", InstallLocation = @"C:\Program Files\VideoLAN\VLC" },
                    new InstalledProgram { Name = "Photoshop", InstallLocation = @"C:\Program Files\Adobe\Photoshop" },
                    new InstalledProgram { Name = "Acrobat", InstallLocation = @"C:\Program Files\Adobe\Acrobat" },
                };
                Check("programme : son dossier", Names(programs, @"C:\Program Files\VideoLAN\VLC\"), "VLC");
                Check("programme : un sous-dossier", Names(programs, @"C:\Program Files\VideoLAN\VLC\plugins"), "VLC");
                Check("programme : dossier parent", Names(programs, @"C:\Program Files\Adobe"), "Photoshop,Acrobat");
                Check("programme : nom proche", Names(programs, @"C:\Program Files\VideoLAN\VLC2"), "");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }

            Console.WriteLine(_failures == 0 ? "Tous les tests passent." : _failures + " test(s) en échec.");
            return _failures == 0 ? 0 : 1;
        }

        private static void Write(string root, string relative, int size)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[size]);
        }

        private static void CheckSplit(string command, string expectedFile, string expectedArgs)
        {
            string file, args;
            InstalledPrograms.SplitCommand(command, out file, out args);
            Check("commande " + command, file + " | " + args, expectedFile + " | " + expectedArgs);
        }

        private static string Names(InstalledProgram[] programs, string folder)
        {
            return string.Join(",", InstalledPrograms.FindFor(programs, folder).Select(p => p.Name));
        }

        private static void Check<T>(string name, T actual, T expected)
        {
            bool ok = Equals(actual, expected);
            if (!ok) _failures++;
            Console.WriteLine((ok ? "  ok    " : "  ÉCHEC ") + name + (ok ? "" : " : obtenu " + actual + ", attendu " + expected));
        }
    }
}
