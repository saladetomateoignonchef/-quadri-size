using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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

        /// <summary>Retire un sous-dossier (supprimé du disque) et met à jour les totaux des dossiers parents.</summary>
        public void RemoveDir(DirNode child)
        {
            if (Dirs.Remove(child))
                AddToTotals(-child.Size, -child.FileCount, -(1 + child.DirCount));
        }

        /// <summary>Retire un fichier (supprimé du disque) et met à jour les totaux des dossiers parents.</summary>
        public void RemoveFile(FileEntry file)
        {
            if (Files.Remove(file))
                AddToTotals(-file.Size, -1, 0);
        }

        /// <summary>Remplace un sous-dossier par une version rescannée et met à jour les totaux.</summary>
        public void ReplaceDir(DirNode oldChild, DirNode newChild)
        {
            int index = Dirs.IndexOf(oldChild);
            if (index < 0) return;
            Dirs[index] = newChild;
            AddToTotals(newChild.Size - oldChild.Size, newChild.FileCount - oldChild.FileCount,
                newChild.DirCount - oldChild.DirCount);
        }

        private void AddToTotals(long size, long files, long dirs)
        {
            for (var node = this; node != null; node = node.Parent)
            {
                node.Size += size;
                node.FileCount += files;
                node.DirCount += dirs;
            }
        }
    }

    /// <summary>
    /// Parcourt un dossier récursivement et calcule la taille de chaque sous-dossier.
    /// Les liens (jonctions, liens symboliques) ne sont pas suivis, pour éviter
    /// de compter deux fois les mêmes fichiers ou de tourner en boucle.
    ///
    /// Pour aller vite, plusieurs dossiers sont lus en même temps (un petit groupe de threads
    /// se partage une pile de dossiers à lire) et, sous Windows, la liste des fichiers est
    /// demandée directement à l'API FindFirstFileEx, qui est bien plus rapide que DirectoryInfo.
    /// Chaque thread ne remplit que le dossier qu'il lit ; les totaux (taille, nombre de
    /// fichiers) sont additionnés à la fin, d'un seul coup, du bas vers le haut de l'arbre.
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
            return Scan(rootPath, null);
        }

        /// <summary>Scanne un dossier ; <paramref name="parent"/> sert quand on rescanne un sous-dossier.</summary>
        public DirNode Scan(string path, DirNode parent)
        {
            var dir = new DirectoryInfo(path);
            var root = new DirNode(parent == null ? DisplayName(dir) : dir.Name, dir.FullName, parent);

            var run = new ScanRun();
            run.Pending.Push(root);

            int threads = Math.Max(4, Math.Min(12, Environment.ProcessorCount * 2));
            var helpers = new List<Thread>();
            for (int i = 1; i < threads; i++)
            {
                var thread = new Thread(() => Work(run));
                thread.IsBackground = true;
                thread.Start();
                helpers.Add(thread);
            }
            Work(run); // le thread appelant travaille aussi
            foreach (var thread in helpers) thread.Join();

            if (run.Failure != null) throw run.Failure;
            AddUpTotals(root);
            return root;
        }

        private static string DisplayName(DirectoryInfo di)
        {
            // Pour une racine de lecteur (C:\), Name vaut "C:\" : on le garde tel quel.
            return string.IsNullOrEmpty(di.Name) ? di.FullName : di.Name;
        }

        /// <summary>État partagé par les threads pendant un scan.</summary>
        private sealed class ScanRun
        {
            public readonly object Lock = new object();
            public readonly Stack<DirNode> Pending = new Stack<DirNode>();
            public int Active;          // nombre de threads en train de lire un dossier
            public Exception Failure;   // première erreur imprévue
        }

        /// <summary>Boucle d'un thread : prend un dossier à lire, le lit, et met ses sous-dossiers à la file.</summary>
        private void Work(ScanRun run)
        {
            var children = new List<DirNode>();
            while (true)
            {
                DirNode node;
                lock (run.Lock)
                {
                    while (run.Pending.Count == 0)
                    {
                        if (run.Active == 0 || run.Failure != null)
                        {
                            Monitor.PulseAll(run.Lock); // réveille les autres : c'est fini
                            return;
                        }
                        Monitor.Wait(run.Lock);
                    }
                    if (run.Failure != null)
                    {
                        Monitor.PulseAll(run.Lock);
                        return;
                    }
                    node = run.Pending.Pop();
                    run.Active++;
                }

                children.Clear();
                Exception error = null;
                try { ReadDirectory(node, children); }
                catch (Exception ex) { error = ex; }

                lock (run.Lock)
                {
                    if (error != null && run.Failure == null) run.Failure = error;
                    foreach (var child in children) run.Pending.Push(child);
                    run.Active--;
                    Monitor.PulseAll(run.Lock);
                }
            }
        }

        /// <summary>Lit le contenu d'un seul dossier : remplit ses fichiers et crée ses sous-dossiers (à lire ensuite).</summary>
        private void ReadDirectory(DirNode node, List<DirNode> children)
        {
            if (_cancel) return;
            _currentPath = node.FullPath;

            if (NativeAvailable)
            {
                try
                {
                    ReadDirectoryNative(node, children);
                    return;
                }
                catch (DllNotFoundException) { NativeBroken = true; }
                catch (EntryPointNotFoundException) { NativeBroken = true; }
                // Les fichiers déjà lus avant l'erreur sont refaits proprement par la méthode de secours.
                node.Files.Clear();
                node.Dirs.Clear();
                children.Clear();
                node.Size = 0;
                node.FileCount = 0;
            }
            ReadDirectoryManaged(node, children);
        }

        /// <summary>Méthode de secours (et hors Windows) : DirectoryInfo, plus lente.</summary>
        private void ReadDirectoryManaged(DirNode node, List<DirNode> children)
        {
            long count = 0;
            try
            {
                foreach (var entry in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos())
                {
                    var asDir = entry as DirectoryInfo;
                    if (asDir != null)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            var child = new DirNode(asDir.Name, asDir.FullName, node);
                            node.Dirs.Add(child);
                            children.Add(child);
                        }
                        continue;
                    }

                    var file = (FileInfo)entry;
                    long len = file.Length;
                    node.Files.Add(new FileEntry(file.Name, len));
                    node.Size += len;
                    node.FileCount++;
                    count++;
                }
            }
            catch (Exception ex)
            {
                if (!IsAccessProblem(ex)) throw;
                node.Denied = true;
                Interlocked.Increment(ref _deniedCount);
            }
            Interlocked.Add(ref _filesScanned, count);
        }

        /// <summary>Windows : FindFirstFileEx sans noms courts 8.3 et avec de gros paquets, bien plus rapide.</summary>
        private void ReadDirectoryNative(DirNode node, List<DirNode> children)
        {
            WIN32_FIND_DATA data;
            IntPtr handle = FindFirstFileEx(SearchPattern(node.FullPath), FindExInfoBasic, out data,
                FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
            if (handle == InvalidHandle)
            {
                // Accès refusé, dossier disparu, nom invalide… : comme avant, le dossier est « inaccessible ».
                node.Denied = true;
                Interlocked.Increment(ref _deniedCount);
                return;
            }

            long count = 0;
            try
            {
                do
                {
                    string name = data.cFileName;
                    if (name == "." || name == "..") continue;

                    if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        // Jonctions et liens symboliques : ignorés.
                        if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                        {
                            var child = new DirNode(name, Path.Combine(node.FullPath, name), node);
                            node.Dirs.Add(child);
                            children.Add(child);
                        }
                    }
                    else
                    {
                        long len = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                        node.Files.Add(new FileEntry(name, len));
                        node.Size += len;
                        node.FileCount++;
                        count++;
                    }
                }
                while (FindNextFile(handle, out data));

                if (Marshal.GetLastWin32Error() != ERROR_NO_MORE_FILES)
                {
                    node.Denied = true; // la lecture s'est arrêtée sur une erreur
                    Interlocked.Increment(ref _deniedCount);
                }
            }
            finally
            {
                FindClose(handle);
                Interlocked.Add(ref _filesScanned, count);
            }
        }

        /// <summary>Additionne les totaux du bas vers le haut : chaque dossier s'ajoute à son parent.</summary>
        private static void AddUpTotals(DirNode root)
        {
            // Parcours « parent avant enfants » ; en le lisant à l'envers, les enfants passent avant leur parent.
            var order = new List<DirNode>();
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                order.Add(node);
                foreach (var child in node.Dirs) stack.Push(child);
            }

            for (int i = order.Count - 1; i > 0; i--)
            {
                var node = order[i];
                var parent = node.Parent;
                parent.Size += node.Size;
                parent.FileCount += node.FileCount;
                parent.DirCount += 1 + node.DirCount;
            }
        }

        private static bool IsAccessProblem(Exception ex)
        {
            return ex is UnauthorizedAccessException
                || ex is SecurityException
                || ex is IOException; // inclut PathTooLongException et DirectoryNotFoundException
        }

        // ---------------------------------------------------------------- API Windows

        private static volatile bool NativeBroken;

        private static bool NativeAvailable
        {
            get { return !NativeBroken && Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private const int FindExInfoBasic = 1;          // pas de nom court 8.3
        private const int FindExSearchNameMatch = 0;
        private const int FIND_FIRST_EX_LARGE_FETCH = 2; // gros paquets de résultats
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const int ERROR_NO_MORE_FILES = 18;

        /// <summary>« C:\dossier » devient « C:\dossier\* » ; les très longs chemins prennent le préfixe \\?\.</summary>
        private static string SearchPattern(string directory)
        {
            string path = directory.TrimEnd('\\') + "\\*";
            if (path.Length < 248 || path.StartsWith(@"\\?\")) return path;
            return path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(string fileName, int infoLevel, out WIN32_FIND_DATA data,
            int searchOp, IntPtr searchFilter, int additionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFile(IntPtr handle, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);
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

        /// <summary>« 1 fichier », « 3 fichiers »…</summary>
        public static string Count(long count, string word)
        {
            return count.ToString("N0") + " " + word + (count > 1 ? "s" : "");
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
