using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace QuadriSize
{
    /// <summary>Un programme listé dans « Applications installées » de Windows.</summary>
    public sealed class InstalledProgram
    {
        public string Name;
        public string InstallLocation;
        public string UninstallCommand;
    }

    /// <summary>
    /// Lit la liste des programmes installés (la même que « Applications installées »)
    /// pour proposer leur vrai désinstalleur plutôt qu'un simple effacement du dossier.
    /// </summary>
    public static class InstalledPrograms
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>Au-delà, le dossier choisi est trop général (ex. Program Files) pour proposer une désinstallation.</summary>
        private const int MaxMatchesForParentFolder = 5;

        public static List<InstalledProgram> Load()
        {
            var result = new List<InstalledProgram>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, result, seen);
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, result, seen);
            ReadHive(RegistryHive.CurrentUser, RegistryView.Default, result, seen);
            return result;
        }

        private static void ReadHive(RegistryHive hive, RegistryView view, List<InstalledProgram> result, HashSet<string> seen)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var uninstall = baseKey.OpenSubKey(UninstallKey))
                {
                    if (uninstall == null) return;
                    foreach (string subName in uninstall.GetSubKeyNames())
                    {
                        using (var key = uninstall.OpenSubKey(subName))
                        {
                            if (key == null) continue;
                            var program = ReadProgram(key);
                            if (program != null && seen.Add(program.Name + "|" + program.InstallLocation))
                                result.Add(program);
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            catch (IOException) { }
        }

        private static InstalledProgram ReadProgram(RegistryKey key)
        {
            string name = key.GetValue("DisplayName") as string;
            string location = key.GetValue("InstallLocation") as string;
            string uninstall = key.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uninstall)) return null;
            if (string.IsNullOrWhiteSpace(location)) return null;
            // Composants cachés et mises à jour : Windows ne les montre pas non plus.
            if (Equals(key.GetValue("SystemComponent"), 1)) return null;
            if (key.GetValue("ParentKeyName") != null) return null;

            location = Normalize(location.Trim().Trim('"'));
            if (location == null || IsTooGeneric(location)) return null;

            return new InstalledProgram { Name = name.Trim(), InstallLocation = location, UninstallCommand = uninstall.Trim() };
        }

        /// <summary>Les programmes qui correspondent au dossier choisi.</summary>
        public static List<InstalledProgram> FindFor(IEnumerable<InstalledProgram> programs, string folder)
        {
            folder = Normalize(folder);
            if (folder == null) return new List<InstalledProgram>();

            // Le dossier du programme lui-même, ou un de ses sous-dossiers.
            var direct = programs.Where(p => SamePath(p.InstallLocation, folder) || IsInside(folder, p.InstallLocation)).ToList();
            if (direct.Count > 0) return direct;

            // Un dossier qui contient quelques programmes (ex. « Program Files\Adobe »).
            var contained = programs.Where(p => IsInside(p.InstallLocation, folder)).ToList();
            return contained.Count <= MaxMatchesForParentFolder ? contained : new List<InstalledProgram>();
        }

        /// <summary>Sépare une commande du registre en programme + arguments.</summary>
        public static void SplitCommand(string command, out string file, out string arguments)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                if (end > 0)
                {
                    file = command.Substring(1, end - 1);
                    arguments = command.Substring(end + 1).Trim();
                    return;
                }
            }

            // Sans guillemets, le chemin peut contenir des espaces : on coupe après le premier « .exe ».
            int index = 0;
            while ((index = command.IndexOf(".exe", index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int stop = index + 4;
                if (stop == command.Length || command[stop] == ' ')
                {
                    file = command.Substring(0, stop);
                    arguments = command.Substring(stop).Trim();
                    return;
                }
                index = stop;
            }

            file = command;
            arguments = "";
        }

        public static string Normalize(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                return full.Length > root.Length ? full.TrimEnd('\\') : full;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        public static bool SamePath(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Vrai si <paramref name="child"/> est strictement à l'intérieur de <paramref name="parent"/>.</summary>
        public static bool IsInside(string child, string parent)
        {
            string prefix = parent.EndsWith("\\") ? parent : parent + "\\";
            return child.Length > prefix.Length && child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTooGeneric(string location)
        {
            if (location.Length <= 3) return true; // racine d'un lecteur
            var generic = new[]
            {
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            };
            return generic.Any(f => SamePath(Normalize(Environment.GetFolderPath(f)) ?? "", location));
        }
    }
}
