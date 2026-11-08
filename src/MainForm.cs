using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace QuadriSize
{
    /// <summary>Ce que représente une ligne de l'arbre : un dossier ou un fichier.</summary>
    internal sealed class ItemInfo
    {
        public DirNode Dir;      // non nul pour un dossier
        public FileEntry File;   // non nul pour un fichier
        public DirNode Parent;   // dossier qui contient l'élément (nul pour la racine)
        public string Name;
        public string FullPath;
        public long Size;
        public double Percent;   // part de la taille du dossier parent

        public bool IsDir { get { return Dir != null; } }
    }

    public sealed class MainForm : Form
    {
        private const string Placeholder = "…";
        private const int MaxItemsPerFolder = 500;

        private readonly ComboBox _pathBox = new ComboBox();
        private readonly Button _browseButton = new Button();
        private readonly Button _scanButton = new Button();
        private readonly Button _cancelButton = new Button();
        private readonly TreeView _tree = new TreeView();
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();
        private readonly System.Windows.Forms.Timer _progressTimer = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();

        private readonly Color _dirBar = Color.FromArgb(126, 178, 236);
        private readonly Color _fileBar = Color.FromArgb(196, 202, 210);
        private readonly Color _barBack = Color.FromArgb(234, 237, 242);
        private readonly Color _barText = Color.FromArgb(30, 30, 30);

        private Scanner _scanner;
        private Stopwatch _clock;
        private int _barWidth;
        private string _textPrefix = "";

        /// <summary>Déclenché quand un scan se termine (terminé, annulé ou en erreur).</summary>
        public event EventHandler ScanCompleted;

        public MainForm()
        {
            Text = "Quadri-Size";
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
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node;
            };
            _tree.ContextMenuStrip = _menu;

            _menu.Items.Add("Ouvrir dans l'Explorateur", null, delegate { OpenInExplorer(); });
            _menu.Items.Add("Copier le chemin", null, delegate { CopyPath(); });
            _menu.Opening += (s, e) => { e.Cancel = SelectedItem() == null; };

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

        // ---------------------------------------------------------------- Scan

        public void StartScan(string path)
        {
            if (_scanner != null) return;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                MessageBox.Show(this, "Ce dossier n'existe pas :\n" + path, "Quadri-Size",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _tree.Nodes.Clear();
            GC.Collect(); // libère le résultat du scan précédent avant d'en lancer un autre

            var scanner = new Scanner();
            _scanner = scanner;
            _clock = Stopwatch.StartNew();
            SetScanning(true);

            var worker = new Thread(() =>
            {
                DirNode root = null;
                Exception error = null;
                try { root = scanner.Scan(path); }
                catch (Exception ex) { error = ex; }
                try { BeginInvoke((Action)(() => OnScanFinished(scanner, root, error))); }
                catch (InvalidOperationException) { } // fenêtre déjà fermée
            }, 64 * 1024 * 1024); // grosse pile : les arborescences peuvent être très profondes
            worker.IsBackground = true;
            worker.Start();
        }

        private void SetScanning(bool scanning)
        {
            _scanButton.Enabled = !scanning;
            _browseButton.Enabled = !scanning;
            _pathBox.Enabled = !scanning;
            _cancelButton.Enabled = scanning;
            UseWaitCursor = scanning;
            if (scanning) _progressTimer.Start(); else _progressTimer.Stop();
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
            SetScanning(false);

            if (error != null)
            {
                _status.Text = "Erreur pendant l'analyse : " + error.Message;
            }
            else if (root != null)
            {
                var item = new ItemInfo
                {
                    Dir = root, Name = root.FullPath, FullPath = root.FullPath,
                    Size = root.Size, Percent = 100,
                };
                var node = MakeTreeNode(item);
                _tree.Nodes.Add(node);
                node.Expand();
                _tree.SelectedNode = node;
                _tree.Focus();

                string summary = Format.Size(root.Size) + " — " + root.FileCount.ToString("N0") + " fichiers, "
                    + root.DirCount.ToString("N0") + " dossiers — " + (_clock.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s";
                if (scanner.DeniedCount > 0)
                    summary += " — " + scanner.DeniedCount.ToString("N0") + " dossiers inaccessibles (lance en administrateur pour tout voir)";
                if (scanner.Cancelled)
                    summary = "Analyse annulée, résultats partiels : " + summary;
                _status.Text = summary;
            }

            var handler = ScanCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
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
                long count = item.Dir.FileCount;
                text += "    (" + count.ToString("N0") + (count > 1 ? " fichiers)" : " fichier)");
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
                children.Add(new ItemInfo
                {
                    Dir = d, Parent = dir, Name = d.Name, FullPath = d.FullPath,
                    Size = d.Size, Percent = Format.Percent(d.Size, dir.Size),
                });
            foreach (var f in dir.Files)
                children.Add(new ItemInfo
                {
                    File = f, Parent = dir, Name = f.Name, FullPath = Path.Combine(dir.FullPath, f.Name),
                    Size = f.Size, Percent = Format.Percent(f.Size, dir.Size),
                });

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
            int pad = Math.Max(2, b.Height / 6);
            var bar = new Rectangle(b.X + 2, b.Y + pad, _barWidth, b.Height - 2 * pad);
            using (var brush = new SolidBrush(_barBack))
                g.FillRectangle(brush, bar);
            int filled = (int)Math.Round(bar.Width * Math.Min(100, item.Percent) / 100.0);
            if (filled > 0)
                using (var brush = new SolidBrush(item.IsDir ? _dirBar : _fileBar))
                    g.FillRectangle(brush, bar.X, bar.Y, filled, bar.Height);

            string pct = Format.PercentText(item.Percent);
            var pctFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, pct, _tree.Font, bar, _barText, pctFlags);

            // Taille et nom
            var textRect = new Rectangle(bar.Right + 8, b.Y, Math.Max(0, _tree.ClientSize.Width - bar.Right - 8), b.Height);
            TextRenderer.DrawText(g, DescribeItem(item), _tree.Font, textRect, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        // ---------------------------------------------------------------- Menu contextuel

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
                MessageBox.Show(this, ex.Message, "Quadri-Size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopyPath()
        {
            var item = SelectedItem();
            if (item != null) Clipboard.SetText(item.FullPath);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_scanner != null) _scanner.Cancel();
            base.OnFormClosing(e);
        }
    }
}
