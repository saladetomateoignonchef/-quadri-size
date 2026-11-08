using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace QuadriSize
{
    /// <summary>Ce que représente une ligne de l'arbre : un dossier ou un fichier.</summary>
    internal sealed class ItemInfo
    {
        public DirNode Dir;      // non nul pour un dossier
        public FileEntry File;   // non nul pour un fichier
        public DirNode Parent;   // dossier qui contient l'élément (nul pour la racine du scan)
        public string Name;
        public string FullPath;

        public bool IsDir { get { return Dir != null; } }
        public long Size { get { return IsDir ? Dir.Size : File.Size; } }

        /// <summary>Part de la taille du dossier parent.</summary>
        public double Percent { get { return Parent == null ? 100 : Format.Percent(Size, Parent.Size); } }
    }

    public sealed class MainForm : Form
    {
        private const string Placeholder = "…";
        private const int MaxItemsPerFolder = 500;
        private const string AppName = "Quadri-Size";

        private readonly ComboBox _pathBox = new ComboBox();
        private readonly Button _browseButton = new Button();
        private readonly Button _scanButton = new Button();
        private readonly Button _cancelButton = new Button();
        private readonly TreeView _tree = new TreeView();
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();
        private readonly System.Windows.Forms.Timer _progressTimer = new System.Windows.Forms.Timer();

        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private ToolStripMenuItem _uninstallItem;
        private ToolStripMenuItem _refreshItem;

        private readonly Color _dirBar = Color.FromArgb(126, 178, 236);
        private readonly Color _fileBar = Color.FromArgb(196, 202, 210);
        private readonly Color _barBack = Color.FromArgb(234, 237, 242);
        private readonly Color _barText = Color.FromArgb(30, 30, 30);

        private readonly bool _isAdmin = IsRunningAsAdmin();
        private Scanner _scanner;
        private bool _busy;
        private Stopwatch _clock;
        private int _barWidth;
        private string _textPrefix = "";
        private List<InstalledProgram> _programs;

        /// <summary>Déclenché quand un scan se termine (terminé, annulé ou en erreur).</summary>
        public event EventHandler ScanCompleted;

        /// <summary>Déclenché quand une suppression ou une actualisation se termine.</summary>
        public event EventHandler OperationCompleted;

        public MainForm()
        {
            Text = _isAdmin ? AppName + " — administrateur" : AppName;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Px(900), Px(650));
            MinimumSize = new Size(Px(500), Px(300));

            BuildLayout();
            FillDrives();

            _progressTimer.Interval = 150;
            _progressTimer.Tick += delegate { ShowProgress(); };
        }

        private static bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private float _dpiScale;

        /// <summary>Convertit une taille pensée pour un écran à 100 % vers l'échelle réelle de l'écran.</summary>
        private int Px(int pixels)
        {
            if (_dpiScale == 0)
                using (var g = CreateGraphics())
                    _dpiScale = g.DpiX / 96f;
            return (int)Math.Round(pixels * _dpiScale);
        }

        // ---------------------------------------------------------------- Interface

        private void BuildLayout()
        {
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 5,
                Padding = new Padding(Px(6)),
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var label = new Label { Text = "Dossier à analyser :", AutoSize = true, Anchor = AnchorStyles.Left };
            _pathBox.Dock = DockStyle.Fill;
            _pathBox.DropDownStyle = ComboBoxStyle.DropDown;
            _pathBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartScan(SelectedPath()); }
            };

            SetupButton(_browseButton, "Parcourir…");
            SetupButton(_scanButton, "Analyser");
            SetupButton(_cancelButton, "Annuler");
            _cancelButton.Enabled = false;

            _browseButton.Click += delegate { Browse(); };
            _scanButton.Click += delegate { StartScan(SelectedPath()); };
            _cancelButton.Click += delegate { if (_scanner != null) _scanner.Cancel(); };

            top.Controls.Add(label, 0, 0);
            top.Controls.Add(_pathBox, 1, 0);
            top.Controls.Add(_browseButton, 2, 0);
            top.Controls.Add(_scanButton, 3, 0);
            top.Controls.Add(_cancelButton, 4, 0);

            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.ShowLines = false;
            _tree.FullRowSelect = true;
            _tree.ItemHeight = Px(22);
            _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _tree.DrawNode += OnDrawNode;
            _tree.BeforeExpand += OnBeforeExpand;
            _tree.KeyDown += OnTreeKeyDown;
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node;
            };
            _tree.ContextMenuStrip = _menu;

            BuildMenu();

            var statusBar = new StatusStrip();
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "Choisis un lecteur ou un dossier, puis clique sur « Analyser ».";
            statusBar.Items.Add(_status);

            // L'élément qui remplit l'espace doit être ajouté en premier.
            Controls.Add(_tree);
            Controls.Add(statusBar);
            Controls.Add(top);

            _barWidth = Px(64);
            int spaceWidth = Math.Max(1, TextRenderer.MeasureText(" ", _tree.Font, Size.Empty, TextFormatFlags.NoPadding).Width);
            _textPrefix = new string(' ', (int)Math.Ceiling((_barWidth + Px(10)) / (double)spaceWidth));
        }

        private void BuildMenu()
        {
            _menu.Items.Add("Ouvrir dans l'Explorateur", null, delegate { OpenInExplorer(); });
            _menu.Items.Add("Copier le chemin", null, delegate { CopyPath(); });
            _refreshItem = new ToolStripMenuItem("Actualiser ce dossier", null, delegate { RefreshSelected(); })
            {
                ShortcutKeyDisplayString = "F5",
            };
            _menu.Items.Add(_refreshItem);
            _menu.Items.Add(new ToolStripSeparator());

            _uninstallItem = new ToolStripMenuItem("Désinstaller");
            _uninstallItem.Click += delegate
            {
                var program = _uninstallItem.Tag as InstalledProgram;
                if (program != null && _uninstallItem.DropDownItems.Count == 0) Uninstall(program);
            };
            _menu.Items.Add(_uninstallItem);

            _menu.Items.Add(new ToolStripMenuItem("Mettre à la corbeille", null, delegate { DeleteSelected(false); })
            {
                ShortcutKeyDisplayString = "Suppr",
            });
            _menu.Items.Add(new ToolStripMenuItem("Supprimer définitivement…", null, delegate { DeleteSelected(true); })
            {
                ShortcutKeyDisplayString = "Maj+Suppr",
            });

            _menu.Opening += (s, e) =>
            {
                var item = SelectedItem();
                if (item == null || _busy) { e.Cancel = true; return; }
                _refreshItem.Visible = item.IsDir;
                SetupUninstallItem(item);
            };
        }

        private void SetupUninstallItem(ItemInfo item)
        {
            _uninstallItem.DropDownItems.Clear();
            _uninstallItem.Tag = null;
            var programs = item.IsDir ? ProgramsFor(item.FullPath) : new List<InstalledProgram>();
            _uninstallItem.Visible = programs.Count > 0;

            if (programs.Count == 1)
            {
                _uninstallItem.Text = "Désinstaller « " + programs[0].Name + " »…";
                _uninstallItem.Tag = programs[0];
            }
            else
            {
                _uninstallItem.Text = "Désinstaller";
                foreach (var program in programs)
                {
                    var p = program;
                    _uninstallItem.DropDownItems.Add(p.Name + "…", null, delegate { Uninstall(p); });
                }
            }
        }

        private void SetupButton(Button button, string text)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Anchor = AnchorStyles.Left;
            button.Padding = new Padding(Px(6), 0, Px(6), 0);
        }

        private sealed class DriveItem
        {
            public string Path;
            public string Label;
            public override string ToString() { return Label; }
        }

        private void FillDrives()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string name = string.IsNullOrEmpty(drive.VolumeLabel) ? "" : drive.VolumeLabel + " ";
                    string label = drive.Name + "   " + name + "(" + Format.Size(drive.TotalFreeSpace)
                        + " libres sur " + Format.Size(drive.TotalSize) + ")";
                    _pathBox.Items.Add(new DriveItem { Path = drive.RootDirectory.FullName, Label = label });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (_pathBox.Items.Count > 0) _pathBox.SelectedIndex = 0;
        }

        private string SelectedPath()
        {
            string text = _pathBox.Text.Trim();
            foreach (DriveItem item in _pathBox.Items)
                if (item.Label == text) return item.Path;
            return text;
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choisis le dossier à analyser";
                string current = SelectedPath();
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.SelectedIndex = -1;
                    _pathBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void SetBusy(bool busy, bool scanning)
        {
            _busy = busy;
            _scanButton.Enabled = !busy;
            _browseButton.Enabled = !busy;
            _pathBox.Enabled = !busy;
            _cancelButton.Enabled = busy && scanning;
            UseWaitCursor = busy;
            if (busy && scanning) _progressTimer.Start(); else _progressTimer.Stop();
        }

        // ---------------------------------------------------------------- Scan

        public void StartScan(string path)
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                MessageBox.Show(this, "Ce dossier n'existe pas :\n" + path, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _tree.Nodes.Clear();
            _programs = null;
            GC.Collect(); // libère le résultat du scan précédent avant d'en lancer un autre

            var scanner = new Scanner();
            _scanner = scanner;
            _clock = Stopwatch.StartNew();
            SetBusy(true, true);

            RunInBackground(() =>
            {
                DirNode root = null;
                Exception error = null;
                try { root = scanner.Scan(path); }
                catch (Exception ex) { error = ex; }
                return (Action)(() => OnScanFinished(scanner, root, error));
            });
        }

        /// <summary>Lance un travail dans un thread séparé puis exécute son résultat dans la fenêtre.</summary>
        private void RunInBackground(Func<Action> work)
        {
            var worker = new Thread(() =>
            {
                Action done = work();
                try { BeginInvoke(done); }
                catch (InvalidOperationException) { } // fenêtre déjà fermée
            }, 64 * 1024 * 1024); // grosse pile : les arborescences peuvent être très profondes
            worker.SetApartmentState(ApartmentState.STA); // nécessaire aux boîtes de dialogue de Windows
            worker.IsBackground = true;
            worker.Start();
        }

        private void ShowProgress()
        {
            if (_scanner == null) return;
            _status.Text = "Analyse en cours… " + _scanner.FilesScanned.ToString("N0") + " fichiers — "
                + _scanner.CurrentPath;
        }

        private void OnScanFinished(Scanner scanner, DirNode root, Exception error)
        {
            _scanner = null;
            _clock.Stop();
            SetBusy(false, false);

            if (error != null)
            {
                _status.Text = "Erreur pendant l'analyse : " + error.Message;
            }
            else if (root != null)
            {
                var item = new ItemInfo { Dir = root, Name = root.FullPath, FullPath = root.FullPath };
                var node = MakeTreeNode(item);
                _tree.Nodes.Add(node);
                node.Expand();
                _tree.SelectedNode = node;
                _tree.Focus();

                string summary = Summary(root) + " — " + (_clock.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s";
                if (scanner.DeniedCount > 0)
                {
                    summary += " — " + Format.Count(scanner.DeniedCount, "dossier") + " inaccessible" + (scanner.DeniedCount > 1 ? "s" : "");
                    if (!_isAdmin) summary += " (lance en administrateur pour tout voir)";
                }
                if (scanner.Cancelled)
                    summary = "Analyse annulée, résultats partiels : " + summary;
                _status.Text = summary;
            }

            var handler = ScanCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static string Summary(DirNode root)
        {
            return Format.Size(root.Size) + " — " + Format.Count(root.FileCount, "fichier") + ", "
                + Format.Count(root.DirCount, "dossier");
        }

        // ---------------------------------------------------------------- Arbre

        private TreeNode MakeTreeNode(ItemInfo item)
        {
            var node = new TreeNode(_textPrefix + DescribeItem(item)) { Tag = item };
            if (item.IsDir && item.Dir.HasChildren) node.Nodes.Add(new TreeNode(Placeholder));
            return node;
        }

        private static string DescribeItem(ItemInfo item)
        {
            string text = Format.Size(item.Size) + "    " + item.Name;
            if (item.IsDir)
            {
                text += "    (" + Format.Count(item.Dir.FileCount, "fichier") + ")";
                if (item.Dir.Denied) text += " — accès refusé";
            }
            return text;
        }

        private void OnBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var item = e.Node.Tag as ItemInfo;
            if (item == null || !item.IsDir) return;
            if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Tag != null) return; // déjà rempli

            DirNode dir = item.Dir;
            var children = new List<ItemInfo>(dir.Dirs.Count + dir.Files.Count);
            foreach (var d in dir.Dirs)
                children.Add(new ItemInfo { Dir = d, Parent = dir, Name = d.Name, FullPath = d.FullPath });
            foreach (var f in dir.Files)
                children.Add(new ItemInfo { File = f, Parent = dir, Name = f.Name, FullPath = Path.Combine(dir.FullPath, f.Name) });

            // Les plus gros en premier ; à taille égale, les dossiers avant les fichiers puis par nom.
            var sorted = children
                .OrderByDescending(c => c.Size)
                .ThenBy(c => c.IsDir ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _tree.BeginUpdate();
            try
            {
                e.Node.Nodes.Clear();
                var nodes = sorted.Take(MaxItemsPerFolder).Select(MakeTreeNode).ToArray();
                e.Node.Nodes.AddRange(nodes);

                if (sorted.Count > MaxItemsPerFolder)
                {
                    var rest = sorted.Skip(MaxItemsPerFolder).ToList();
                    long restSize = rest.Sum(c => c.Size);
                    e.Node.Nodes.Add(new TreeNode("… et " + rest.Count.ToString("N0") + " autres éléments ("
                        + Format.Size(restSize) + ", " + Format.PercentText(Format.Percent(restSize, dir.Size)) + ")")
                    {
                        ForeColor = SystemColors.GrayText,
                    });
                }
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        /// <summary>Remet à jour tailles et pourcentages affichés, de <paramref name="start"/> jusqu'à la racine.</summary>
        private void RefreshTexts(TreeNode start)
        {
            _tree.BeginUpdate();
            try
            {
                for (var node = start; node != null; node = node.Parent)
                {
                    UpdateText(node);
                    foreach (TreeNode child in node.Nodes) UpdateText(child);
                }
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private void UpdateText(TreeNode node)
        {
            var item = node.Tag as ItemInfo;
            if (item != null) node.Text = _textPrefix + DescribeItem(item);
        }

        private void OnDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            var item = e.Node.Tag as ItemInfo;
            if (item == null || e.Bounds.Width == 0 || e.Bounds.Height == 0)
            {
                e.DrawDefault = true;
                return;
            }

            Graphics g = e.Graphics;
            Rectangle b = e.Bounds;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;

            Color back = selected ? (_tree.Focused ? SystemColors.Highlight : SystemColors.ControlLight) : _tree.BackColor;
            Color fore = selected && _tree.Focused ? SystemColors.HighlightText
                : item.IsDir ? _tree.ForeColor : SystemColors.GrayText;
            using (var brush = new SolidBrush(back))
                g.FillRectangle(brush, b.X, b.Y, Math.Max(b.Width, _tree.ClientSize.Width - b.X), b.Height);

            // Barre de pourcentage
            double percent = item.Percent;
            int pad = Math.Max(2, b.Height / 6);
            var bar = new Rectangle(b.X + 2, b.Y + pad, _barWidth, b.Height - 2 * pad);
            using (var brush = new SolidBrush(_barBack))
                g.FillRectangle(brush, bar);
            int filled = (int)Math.Round(bar.Width * Math.Min(100, percent) / 100.0);
            if (filled > 0)
                using (var brush = new SolidBrush(item.IsDir ? _dirBar : _fileBar))
                    g.FillRectangle(brush, bar.X, bar.Y, filled, bar.Height);
            TextRenderer.DrawText(g, Format.PercentText(percent), _tree.Font, bar, _barText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Taille et nom
            var textRect = new Rectangle(bar.Right + 8, b.Y, Math.Max(0, _tree.ClientSize.Width - bar.Right - 8), b.Height);
            TextRenderer.DrawText(g, DescribeItem(item), _tree.Font, textRect, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            if (_busy) return;
            if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                DeleteSelected(e.Shift);
            }
            else if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                RefreshSelected();
            }
        }

        // ---------------------------------------------------------------- Actions simples

        private ItemInfo SelectedItem()
        {
            return _tree.SelectedNode == null ? null : _tree.SelectedNode.Tag as ItemInfo;
        }

        private void OpenInExplorer()
        {
            var item = SelectedItem();
            if (item == null) return;
            try
            {
                if (item.IsDir) Process.Start("explorer.exe", "\"" + item.FullPath + "\"");
                else Process.Start("explorer.exe", "/select,\"" + item.FullPath + "\"");
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void CopyPath()
        {
            var item = SelectedItem();
            if (item != null) Clipboard.SetText(item.FullPath);
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---------------------------------------------------------------- Suppression

        /// <summary>Raison pour laquelle on refuse de supprimer cet élément, ou nul si c'est permis.</summary>
        private static string ProtectionReason(ItemInfo item)
        {
            if (item.Parent == null)
                return "Impossible de supprimer le dossier analysé lui-même.\n\n"
                    + "Analyse le dossier qui le contient si tu veux vraiment le supprimer.";

            string path = InstalledPrograms.Normalize(item.FullPath) ?? item.FullPath;
            string windows = InstalledPrograms.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (windows != null && (InstalledPrograms.SamePath(path, windows) || InstalledPrograms.IsInside(path, windows)))
                return "Les fichiers de Windows sont protégés : les supprimer peut empêcher l'ordinateur de démarrer.\n\n"
                    + "Pour libérer de la place dans Windows, utilise plutôt « Nettoyage de disque » (cleanmgr).";

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var systemFolders = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                profile,
                Path.GetDirectoryName(profile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            if (systemFolders.Any(f => !string.IsNullOrEmpty(f) && InstalledPrograms.SamePath(path, InstalledPrograms.Normalize(f))))
                return "Ce dossier fait partie de Windows et ne peut pas être supprimé.\n\n"
                    + "Tu peux en revanche supprimer ce qu'il contient.";

            // Éléments système à la racine d'un lecteur (fichier d'échange, corbeille, démarrage…).
            string parent = Path.GetDirectoryName(path);
            if (parent != null && parent.Length <= 3)
            {
                var rootItems = new[] { "System Volume Information", "Recovery", "Boot", "EFI", "bootmgr", "BOOTNXT",
                    "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp" };
                if (item.Name.StartsWith("$") || rootItems.Any(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase)))
                    return "« " + item.Name + " » est un élément système de Windows : il ne peut pas être supprimé ici.";
            }
            return null;
        }

        private void DeleteSelected(bool permanent)
        {
            var node = _tree.SelectedNode;
            var item = node == null ? null : node.Tag as ItemInfo;
            if (item == null || _busy) return;

            string refusal = ProtectionReason(item);
            if (refusal != null)
            {
                MessageBox.Show(this, refusal, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string message = (permanent ? "Supprimer définitivement " : "Mettre à la corbeille ")
                + (item.IsDir ? "le dossier" : "le fichier") + " « " + item.Name + " » ?\n\n"
                + item.FullPath + "\nTaille : " + Format.Size(item.Size);
            if (item.IsDir) message += " (" + Format.Count(item.Dir.FileCount, "fichier") + ")";

            var programs = item.IsDir ? ProgramsFor(item.FullPath) : new List<InstalledProgram>();
            if (programs.Count > 0)
                message += "\n\nAttention : ce dossier contient le programme « " + programs[0].Name + " ».\n"
                    + "Pour l'enlever proprement, utilise plutôt clic droit → Désinstaller.";
            if (permanent) message += "\n\nCette action est irréversible.";

            var answer = MessageBox.Show(this, message, AppName, MessageBoxButtons.YesNo,
                permanent || programs.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) DeleteItem(node, permanent);
        }

        /// <summary>Supprime l'élément sans demander de confirmation (déjà faite), puis met l'arbre à jour.</summary>
        private void DeleteItem(TreeNode node, bool permanent)
        {
            var item = (ItemInfo)node.Tag;
            SetBusy(true, false);
            _status.Text = (permanent ? "Suppression de " : "Mise à la corbeille de ") + item.FullPath + "…";
            var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;

            RunInBackground(() =>
            {
                string error = null;
                try
                {
                    // Les fenêtres d'erreur de Windows (fichier ouvert, etc.) s'affichent si besoin.
                    if (item.IsDir) FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, recycle);
                    else FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, recycle);
                }
                catch (OperationCanceledException) { error = "Suppression annulée."; }
                catch (Exception ex) { error = ex.Message; }

                // Une suppression peut être partielle (fichier ouvert…) : on remesure ce qui reste.
                DirNode remaining = Rescan(item);
                return (Action)(() =>
                {
                    long before = item.Size;
                    bool gone = ApplyRescan(node, item, remaining);
                    SetBusy(false, false);

                    if (gone)
                        _status.Text = "« " + item.Name + " » " + (permanent ? "supprimé définitivement" : "mis à la corbeille")
                            + " (" + Format.Size(before) + ")" + (permanent ? "" : " — vide la corbeille pour libérer la place")
                            + ". Total : " + Summary(RootDir());
                    else
                    {
                        long freed = before - item.Size;
                        _status.Text = "Suppression incomplète de « " + item.Name + " » : " + Format.Size(freed) + " libérés, "
                            + Format.Size(item.Size) + " restants.";
                        if (error != null && error != "Suppression annulée.")
                            ShowError("Une partie de « " + item.Name + " » n'a pas pu être supprimée :\n\n" + error);
                    }
                    RaiseOperationCompleted();
                });
            });
        }

        /// <summary>Remesure un élément sur le disque (appelé hors de la fenêtre). Nul si le dossier n'existe plus.</summary>
        private static DirNode Rescan(ItemInfo item)
        {
            if (!item.IsDir || !Directory.Exists(item.FullPath)) return null;
            try { return new Scanner().Scan(item.FullPath, item.Parent); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Met le modèle et l'arbre en accord avec le disque après une suppression ou une actualisation.
        /// Renvoie vrai si l'élément n'existe plus.
        /// </summary>
        private bool ApplyRescan(TreeNode node, ItemInfo item, DirNode rescanned)
        {
            TreeNode parentNode = node.Parent;
            bool exists = item.IsDir ? Directory.Exists(item.FullPath) : System.IO.File.Exists(item.FullPath);

            if (!exists)
            {
                if (item.IsDir) item.Parent.RemoveDir(item.Dir);
                else item.Parent.RemoveFile(item.File);
                node.Remove();
                if (parentNode != null) _tree.SelectedNode = parentNode;
            }
            else if (item.IsDir && rescanned != null)
            {
                item.Parent.ReplaceDir(item.Dir, rescanned);
                item.Dir = rescanned;

                bool expanded = node.IsExpanded;
                node.Collapse();
                node.Nodes.Clear();
                if (rescanned.HasChildren) node.Nodes.Add(new TreeNode(Placeholder));
                if (expanded) node.Expand();
                _tree.SelectedNode = node;
            }

            RefreshTexts(exists ? node : parentNode);
            return !exists;
        }

        private DirNode RootDir()
        {
            var item = _tree.Nodes.Count > 0 ? _tree.Nodes[0].Tag as ItemInfo : null;
            return item == null ? null : item.Dir;
        }

        private void RaiseOperationCompleted()
        {
            var handler = OperationCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- Actualisation

        private void RefreshSelected()
        {
            var node = _tree.SelectedNode;
            var item = node == null ? null : node.Tag as ItemInfo;
            if (item == null || !item.IsDir || _busy) return;

            if (item.Parent == null)
            {
                StartScan(item.FullPath); // la racine : on relance l'analyse complète
                return;
            }

            SetBusy(true, false);
            _status.Text = "Actualisation de " + item.FullPath + "…";
            RunInBackground(() =>
            {
                DirNode rescanned = Rescan(item);
                return (Action)(() =>
                {
                    bool gone = ApplyRescan(node, item, rescanned);
                    SetBusy(false, false);
                    _status.Text = gone
                        ? "« " + item.Name + " » n'existe plus. Total : " + Summary(RootDir())
                        : "« " + item.Name + " » actualisé : " + Format.Size(item.Size) + ". Total : " + Summary(RootDir());
                    RaiseOperationCompleted();
                });
            });
        }

        // ---------------------------------------------------------------- Désinstallation

        private List<InstalledProgram> ProgramsFor(string folder)
        {
            if (_programs == null) _programs = InstalledPrograms.Load();
            return InstalledPrograms.FindFor(_programs, folder);
        }

        private void Uninstall(InstalledProgram program)
        {
            var answer = MessageBox.Show(this,
                "Lancer le désinstalleur de « " + program.Name + " » ?\n\n"
                + "C'est la même chose que de le désinstaller depuis les Paramètres de Windows.",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            try
            {
                string file, arguments;
                InstalledPrograms.SplitCommand(program.UninstallCommand, out file, out arguments);
                Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
                _programs = null; // la liste aura changé une fois la désinstallation finie
                _status.Text = "Désinstalleur de « " + program.Name + " » lancé. Quand il a fini, "
                    + "fais clic droit → Actualiser ce dossier (F5) pour mettre l'arbre à jour.";
            }
            catch (Exception ex)
            {
                ShowError("Impossible de lancer le désinstalleur de « " + program.Name + " » :\n\n" + ex.Message
                    + "\n\nTu peux le désinstaller depuis Paramètres → Applications → Applications installées.");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_scanner != null) _scanner.Cancel();
            base.OnFormClosing(e);
        }
    }
}
