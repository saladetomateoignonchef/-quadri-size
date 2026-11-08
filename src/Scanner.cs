using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;

namespace QuadriSize
{
    /// <summary>Un fichier trouvé pendant le scan.</summary>
    public sealed class FileEntry
    {
        public readonly string Name;
        public readonly long Size;

        public FileEntry(string name, long size)
        {
            Name = name;
            Size = size;
        }
    }

    /// <summary>Un dossier scanné, avec sa taille totale (sous-dossiers compris).</summary>
    public sealed class DirNode
    {
        public readonly string Name;
        public readonly string FullPath;
        public readonly DirNode Parent;
        public readonly List<DirNode> Dirs = new List<DirNode>();
        public readonly List<FileEntry> Files = new List<FileEntry>();

        /// <summary>Taille totale en octets, sous-dossiers compris.</summary>
        public long Size;
        /// <summary>Nombre total de fichiers, sous-dossiers compris.</summary>
        public long FileCount;
        /// <summary>Nombre total de sous-dossiers, à tous les niveaux.</summary>
        public long DirCount;
        /// <summary>Vrai si le contenu de ce dossier n'a pas pu être lu.</summary>
        public bool Denied;

        public DirNode(string name, string fullPath, DirNode parent)
        {
            Name = name;
            FullPath = fullPath;
            Parent = parent;
        }

        public bool HasChildren
        {
            get { return Dirs.Count > 0 || Files.Count > 0; }
        }
    }

    /// <summary>
    /// Parcourt un dossier récursivement et calcule la taille de chaque sous-dossier.
    /// Les liens (jonctions, liens symboliques) ne sont pas suivis, pour éviter
    /// de compter deux fois les mêmes fichiers ou de tourner en boucle.
    /// </summary>
    public sealed class Scanner
    {
        private long _filesScanned;
        private long _deniedCount;
        private volatile string _currentPath = "";
        private volatile bool _cancel;

        public long FilesScanned { get { return Interlocked.Read(ref _filesScanned); } }
        public long DeniedCount { get { return Interlocked.Read(ref _deniedCount); } }
        public string CurrentPath { get { return _currentPath; } }
        public bool Cancelled { get { return _cancel; } }

        public void Cancel()
        {
            _cancel = true;
        }

        public DirNode Scan(string rootPath)
        {
            var root = new DirectoryInfo(rootPath);
            var node = new DirNode(DisplayName(root), root.FullName, null);
            ScanInto(root, node);
            return node;
        }

        private static string DisplayName(DirectoryInfo di)
        {
            // Pour une racine de lecteur (C:\), Name vaut "C:\" : on le garde tel quel.
            return string.IsNullOrEmpty(di.Name) ? di.FullName : di.Name;
        }

        private void ScanInto(DirectoryInfo dir, DirNode node)
        {
            if (_cancel) return;
            _currentPath = dir.FullName;

            var subDirs = new List<DirectoryInfo>();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    var asDir = entry as DirectoryInfo;
                    if (asDir != null)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                            subDirs.Add(asDir);
                        continue;
                    }

                    var file = (FileInfo)entry;
                    long len = file.Length;
                    node.Files.Add(new FileEntry(file.Name, len));
                    node.Size += len;
                    node.FileCount++;
                    Interlocked.Increment(ref _filesScanned);
                }
            }
            catch (Exception ex)
            {
                if (!IsAccessProblem(ex)) throw;
                node.Denied = true;
                Interlocked.Increment(ref _deniedCount);
            }

            foreach (var sub in subDirs)
            {
                if (_cancel) return;
                var child = new DirNode(sub.Name, sub.FullName, node);
                ScanInto(sub, child);
                node.Dirs.Add(child);
                node.Size += child.Size;
                node.FileCount += child.FileCount;
                node.DirCount += 1 + child.DirCount;
            }
        }

        private static bool IsAccessProblem(Exception ex)
        {
            return ex is UnauthorizedAccessException
                || ex is SecurityException
                || ex is IOException; // inclut PathTooLongException et DirectoryNotFoundException
        }
    }

    public static class Format
    {
        private static readonly string[] Units = { "o", "Ko", "Mo", "Go", "To", "Po" };

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " o";
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            string pattern = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
            return value.ToString(pattern) + " " + Units[unit];
        }

        public static double Percent(long part, long whole)
        {
            return whole <= 0 ? 0 : part * 100.0 / whole;
        }

        public static string PercentText(double percent)
        {
            return percent.ToString("0.0") + " %";
        }
    }
}
