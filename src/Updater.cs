using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace QuadriSize
{
    /// <summary>Résultat de la recherche de mises à jour.</summary>
    public sealed class UpdateCheck
    {
        /// <summary>Dossier du clone git (celui qui contient build.bat), ou nul si l'appli n'en vient pas.</summary>
        public string RepoDir;
        public string Git;
        public string Branch;
        /// <summary>Les nouveautés disponibles sur GitHub, une ligne par commit.</summary>
        public List<string> NewCommits = new List<string>();
        /// <summary>Message d'erreur à afficher, ou nul si tout s'est bien passé.</summary>
        public string Error;

        public bool HasUpdate { get { return Error == null && NewCommits.Count > 0; } }
    }

    /// <summary>
    /// Met Quadri-Size à jour depuis GitHub, comme on le ferait à la main :
    /// git pull, puis build.bat, puis redémarrage sur le nouvel exécutable.
    /// </summary>
    public static class Updater
    {
        private const string OldExeSuffix = ".old.exe";

        /// <summary>Remonte depuis l'exécutable jusqu'au dossier du clone git (qui contient .git et build.bat).</summary>
        public static string FindRepo(string exePath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(exePath)));
            for (int depth = 0; dir != null && depth < 4; depth++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) && File.Exists(Path.Combine(dir.FullName, "build.bat")))
                    return dir.FullName;
            }
            return null;
        }

        /// <summary>git.exe : dans le PATH, sinon à l'emplacement habituel de Git pour Windows.</summary>
        public static string FindGit()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string folder in path.Split(';'))
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim().Trim('"'), "git.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string candidate = Path.Combine(Environment.GetFolderPath(root), "Git", "cmd", "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Git", "cmd", "git.exe");
            return File.Exists(local) ? local : null;
        }

        /// <summary>Demande à GitHub s'il y a des nouveautés (git fetch), sans rien modifier.</summary>
        public static UpdateCheck Check(string exePath)
        {
            var check = new UpdateCheck { RepoDir = FindRepo(exePath), Git = FindGit() };
            if (check.RepoDir == null)
            {
                check.Error = "Quadri-Size n'a pas été lancé depuis un dossier récupéré avec git clone, "
                    + "donc il ne peut pas se mettre à jour tout seul.";
                return check;
            }
            if (check.Git == null)
            {
                check.Error = "Git est introuvable sur ce PC. Installe-le depuis git-scm.com, puis réessaie.";
                return check;
            }

            var branch = RunGit(check, "rev-parse --abbrev-ref HEAD");
            if (!branch.Ok) { check.Error = branch.Describe("Impossible de lire la branche actuelle."); return check; }
            check.Branch = branch.Output.Trim();

            var upstream = RunGit(check, "rev-parse --abbrev-ref --symbolic-full-name @{u}");
            if (!upstream.Ok)
            {
                check.Error = "La branche « " + check.Branch + " » n'est reliée à aucune branche sur GitHub.";
                return check;
            }

            var fetch = RunGit(check, "fetch --quiet");
            if (!fetch.Ok) { check.Error = fetch.Describe("Impossible de joindre GitHub. Vérifie ta connexion internet."); return check; }

            var log = RunGit(check, "log --no-merges --format=%s HEAD..@{u}");
            if (!log.Ok) { check.Error = log.Describe("Impossible de comparer avec GitHub."); return check; }
            check.NewCommits = log.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return check;
        }

        /// <summary>
        /// Télécharge les nouveautés et recompile. Renvoie nul si tout s'est bien passé, sinon le message d'erreur.
        /// En cas d'échec de la compilation, l'ancien exécutable est remis en place.
        /// </summary>
        public static string Apply(UpdateCheck check, string exePath)
        {
            var pull = RunGit(check, "pull --ff-only --quiet");
            if (!pull.Ok)
                return pull.Describe("Le téléchargement des nouveautés a échoué. Si tu as modifié des fichiers du projet "
                    + "à la main, git refuse de les écraser.");

            // Windows interdit d'écraser un programme en cours d'exécution, mais permet de le renommer.
            string exe = Path.GetFullPath(exePath);
            string old = OldExePath(exe);
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
            }
            catch (Exception ex)
            {
                return "Les nouveautés sont téléchargées, mais l'appli n'a pas pu être remplacée : " + ex.Message
                    + "\n\nFerme Quadri-Size et lance build.bat à la main.";
            }

            var build = Run("cmd.exe", "/c \"\"" + Path.Combine(check.RepoDir, "build.bat") + "\"\"", check.RepoDir, 300000);
            if (!build.Ok || !File.Exists(exe))
            {
                try
                {
                    if (File.Exists(exe)) File.Delete(exe);
                    File.Move(old, exe);
                }
                catch (Exception) { }
                return build.Describe("Les nouveautés sont téléchargées, mais la compilation a échoué. "
                    + "L'ancienne version est gardée.");
            }
            return null;
        }

        /// <summary>Démarre la nouvelle version (avec les mêmes droits administrateur).</summary>
        public static void StartNewVersion(string exePath)
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath) });
        }

        /// <summary>Supprime l'ancien exécutable laissé par la dernière mise à jour.</summary>
        public static void CleanupOldExe(string exePath)
        {
            string old = OldExePath(Path.GetFullPath(exePath));
            for (int attempt = 0; attempt < 10 && File.Exists(old); attempt++)
            {
                try { File.Delete(old); }
                catch (IOException) { Thread.Sleep(300); } // l'ancienne version finit de se fermer
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }

        private static string OldExePath(string exe)
        {
            return Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + OldExeSuffix);
        }

        // ---------------------------------------------------------------- Lancement de commandes

        private sealed class RunResult
        {
            public bool Ok;
            public string Output = "";
            public string Errors = "";

            public string Describe(string message)
            {
                string details = (Errors + "\n" + Output).Trim();
                if (details.Length > 1500) details = "…" + details.Substring(details.Length - 1500);
                return details.Length == 0 ? message : message + "\n\n" + details;
            }
        }

        private static RunResult RunGit(UpdateCheck check, string arguments)
        {
            // safe.directory : git peut refuser un dossier créé par un autre compte (l'appli tourne en administrateur).
            string safe = "-c \"safe.directory=" + check.RepoDir.Replace('\\', '/') + "\" ";
            return Run(check.Git, safe + "-C \"" + check.RepoDir + "\" " + arguments, check.RepoDir, 120000);
        }

        private static RunResult Run(string file, string arguments, string workDir, int timeoutMs)
        {
            var result = new RunResult();
            var info = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0"; // jamais de question bloquante dans une fenêtre cachée

            var output = new StringBuilder();
            var errors = new StringBuilder();
            try
            {
                using (var process = new Process { StartInfo = info })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        result.Errors = "La commande a mis trop de temps et a été arrêtée.";
                        return result;
                    }
                    process.WaitForExit(); // termine la lecture des sorties
                    result.Ok = process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                result.Errors = ex.Message;
                return result;
            }
            result.Output = output.ToString();
            result.Errors = errors.ToString() + result.Errors;
            return result;
        }
    }
}
