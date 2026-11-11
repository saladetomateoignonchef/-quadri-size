using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace QuadriSize
{
    /// <summary>La fenêtre principale (MainWindow.xaml) et tout ce qui s'y passe.</summary>
    public sealed class MainUi
    {
        private const int MaxItemsPerFolder = 500;
        private const string AppName = "Quadri-Size";

        public readonly Window Window;

        private readonly DataGrid _tree;
        private readonly TextBlock _pageTitle, _status, _kpiSize, _kpiFiles, _kpiDirs, _kpiFree, _kpiDriveLabel, _kpiDriveInfo;
        private readonly TextBlock _emptyTitle, _emptyText;
        private readonly ColumnDefinition _driveUsedCol, _driveFreeCol;
        private readonly Border _driveUsedBar;
        private readonly Button _btnScan, _btnCancel, _btnCollapse, _navBrowse, _btnUpdate;
        private readonly CheckBox _themeSwitch;
        private readonly ProgressBar _busyBar;
        private readonly StackPanel _drivesPanel, _emptyState;
        private readonly ContextMenu _menu = new ContextMenu();

        private readonly ObservableCollection<RowItem> _rows = new ObservableCollection<RowItem>();
        private readonly List<Button> _driveButtons = new List<Button>();
        private readonly bool _isAdmin = IsRunningAsAdmin();
        private readonly DispatcherTimer _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        private Scanner _scanner;
        private bool _busy;
        private Stopwatch _clock;
        private string _target;
        private DirNode _root;
        private List<InstalledProgram> _programs;

        /// <summary>Déclenché quand un scan se termine (terminé, annulé ou en erreur).</summary>
        public event EventHandler ScanCompleted;

        /// <summary>Déclenché quand une suppression ou une actualisation se termine.</summary>
        public event EventHandler OperationCompleted;

        public MainUi()
        {
            Theme.EnsureApplication();
            Theme.Apply(Theme.LoadPreference());
            Window = (Window)Theme.LoadXaml("MainWindow.xaml");

            _tree = Find<DataGrid>("Tree");
            _pageTitle = Find<TextBlock>("PageTitle");
            _status = Find<TextBlock>("StatusText");
            _kpiSize = Find<TextBlock>("KpiSize");
            _kpiFiles = Find<TextBlock>("KpiFiles");
            _kpiDirs = Find<TextBlock>("KpiDirs");
            _kpiFree = Find<TextBlock>("KpiFree");
            _kpiDriveLabel = Find<TextBlock>("KpiDriveLabel");
            _kpiDriveInfo = Find<TextBlock>("KpiDriveInfo");
            _emptyTitle = Find<TextBlock>("EmptyTitle");
            _emptyText = Find<TextBlock>("EmptyText");
            _driveUsedCol = Find<ColumnDefinition>("DriveUsedCol");
            _driveFreeCol = Find<ColumnDefinition>("DriveFreeCol");
            _driveUsedBar = Find<Border>("DriveUsedBar");
            _btnScan = Find<Button>("BtnScan");
            _btnCancel = Find<Button>("BtnCancel");
            _btnCollapse = Find<Button>("BtnCollapse");
            _navBrowse = Find<Button>("NavBrowse");
            _btnUpdate = Find<Button>("BtnUpdate");
            _themeSwitch = Find<CheckBox>("ThemeSwitch");
            _busyBar = Find<ProgressBar>("Busy");
            _drivesPanel = Find<StackPanel>("DrivesPanel");
            _emptyState = Find<StackPanel>("EmptyState");

            Window.Title = _isAdmin ? AppName + " — administrateur" : AppName;
            Window.SourceInitialized += delegate { Theme.ColorCaption(Window); };
            Window.Closing += delegate { if (_scanner != null) _scanner.Cancel(); };

            _tree.ItemsSource = _rows;
            _tree.ContextMenu = _menu;
            _tree.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnTreeButtonClick));
            _tree.MouseDoubleClick += OnTreeDoubleClick;
            _tree.PreviewKeyDown += OnTreeKeyDown;
            _tree.ContextMenuOpening += OnContextMenuOpening;

            _btnScan.Click += delegate { if (_target != null) StartScan(_target); };
            _btnCancel.Click += delegate { if (_scanner != null) _scanner.Cancel(); };
            _btnCollapse.Click += delegate { CollapseAll(); };
            _navBrowse.Click += delegate { Browse(); };
            _btnUpdate.Click += delegate { UpdateApp(); };

            _themeSwitch.IsChecked = Theme.Current == Theme.Dark;
            _themeSwitch.Checked += delegate { ChangeTheme(Theme.Dark); };
            _themeSwitch.Unchecked += delegate { ChangeTheme(Theme.Light); };

            _progressTimer.Tick += delegate { ShowProgress(); };

            FillSidebarInfo();
            FillDrives();
        }

        private T Find<T>(string name) where T : class
        {
            var element = Window.FindName(name) as T;
            if (element == null) throw new InvalidOperationException("Élément introuvable dans MainWindow.xaml : " + name);
            return element;
        }

        private static bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void ChangeTheme(string name)
        {
            Theme.Apply(name);
            Theme.SavePreference(name);
        }

        // ---------------------------------------------------------------- Menu de gauche

        private void FillSidebarInfo()
        {
            Find<TextBlock>("PcName").Text = Environment.MachineName;
            Find<TextBlock>("AdminText").Text = _isAdmin ? "Mode administrateur" : "Mode standard (accès limité)";
            Find<System.Windows.Shapes.Ellipse>("AdminDot").Fill = _isAdmin ? Brushes.LimeGreen : Brushes.Orange;

            Find<TextBlock>("PcInfo").Text = WindowsName() + " · " + (Environment.Is64BitOperatingSystem ? "64 bits" : "32 bits");
        }

        /// <summary>« Windows 11 » ou « Windows 10 », d'après le numéro de build lu dans le registre.</summary>
        private static string WindowsName()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build;
                    if (key != null && int.TryParse(key.GetValue("CurrentBuildNumber") as string, out build))
                        return build >= 22000 ? "Windows 11" : build >= 10240 ? "Windows 10" : "Windows";
                }
            }
            catch (System.Security.SecurityException) { }
            return "Windows";
        }

        private void FillDrives()
        {
            _drivesPanel.Children.Clear();
            _driveButtons.Clear();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var button = MakeDriveButton(drive);
                    _drivesPanel.Children.Add(button);
                    _driveButtons.Add(button);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            HighlightDrive();
        }

        private Button MakeDriveButton(DriveInfo drive)
        {
            string letter = drive.Name.TrimEnd('\\');
            string label = string.IsNullOrEmpty(drive.VolumeLabel) ? DriveTypeName(drive.DriveType) : drive.VolumeLabel;
            double used = Format.Percent(drive.TotalSize - drive.TotalFreeSpace, drive.TotalSize);

            var name = new TextBlock { Text = letter + "  " + label, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
            var free = new TextBlock
            {
                Text = Format.Size(drive.TotalFreeSpace) + " libres",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA6, 0xBC)),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var top = new DockPanel();
            DockPanel.SetDock(free, Dock.Right);
            top.Children.Add(free);
            top.Children.Add(name);

            var bar = new Grid { Height = 4, Margin = new Thickness(0, 7, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - used, GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(0x21, 0x40, 0x5C)) };
            Grid.SetColumnSpan(track, 2);
            var fill = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(used >= 90 ? Color.FromRgb(0xFB, 0xBF, 0x24) : Color.FromRgb(0x2D, 0xD4, 0xBF)),
            };
            bar.Children.Add(track);
            bar.Children.Add(fill);

            var content = new StackPanel();
            content.Children.Add(top);
            content.Children.Add(bar);

            var button = new Button
            {
                Style = (Style)Application.Current.Resources["Nav"],
                Content = content,
                Tag = drive.RootDirectory.FullName,
                Padding = new Thickness(12, 9, 12, 10),
                ToolTip = Format.Size(drive.TotalSize - drive.TotalFreeSpace) + " utilisés sur " + Format.Size(drive.TotalSize),
            };
            button.Click += (s, e) => StartScan((string)((Button)s).Tag);
            return button;
        }

        private static string DriveTypeName(DriveType type)
        {
            switch (type)
            {
                case DriveType.Removable: return "Amovible";
                case DriveType.Network: return "Réseau";
                case DriveType.CDRom: return "Disque optique";
                default: return "Disque local";
            }
        }

        private void HighlightDrive()
        {
            foreach (var button in _driveButtons)
            {
                // Le lecteur qui contient le dossier analysé est mis en évidence.
                bool active = _target != null && InstalledPrograms.SamePath(
                    InstalledPrograms.Normalize((string)button.Tag), InstalledPrograms.Normalize(Path.GetPathRoot(_target)));
                button.Background = active ? new SolidColorBrush(Color.FromRgb(0x1D, 0x3A, 0x55)) : Brushes.Transparent;
                button.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xB8, 0xC7, 0xD6));
            }
        }

        private sealed class OwnerHandle : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; set; }
        }

        private void Browse()
        {
            if (_busy) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choisis le dossier à analyser";
                if (_target != null && Directory.Exists(_target)) dialog.SelectedPath = _target;
                var owner = new OwnerHandle { Handle = new WindowInteropHelper(Window).Handle };
                if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    StartScan(dialog.SelectedPath);
            }
        }

        // ---------------------------------------------------------------- Scan

        public void StartScan(string path)
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                Dialogs.Show(Window, DialogKind.Warning, "Dossier introuvable", "Ce dossier n'existe pas ou n'est plus accessible.", path);
                return;
            }

            _target = Path.GetFullPath(path);
            _rows.Clear();
            _root = null;
            _programs = null;
            GC.Collect(); // libère le résultat du scan précédent avant d'en lancer un autre

            HighlightDrive();
            _pageTitle.Text = _target;
            _status.Text = "Analyse en cours…";
            _kpiSize.Text = "–";
            _kpiFiles.Text = "0";
            _kpiDirs.Text = "–";
            UpdateDriveKpi();
            ShowEmptyState("Analyse en cours…", "Les fichiers sont comptés un par un, ça peut prendre une minute pour un disque entier.");

            var scanner = new Scanner();
            _scanner = scanner;
            _clock = Stopwatch.StartNew();
            SetBusy(true, true);

            string target = _target;
            RunInBackground(() =>
            {
                DirNode root = null;
                Exception error = null;
                try { root = scanner.Scan(target); }
                catch (Exception ex) { error = ex; }
                return (Action)(() => OnScanFinished(scanner, root, error));
            });
        }

        /// <summary>Lance un travail dans un thread séparé puis exécute son résultat dans la fenêtre.</summary>
        private void RunInBackground(Func<Action> work)
        {
            var dispatcher = Window.Dispatcher;
            var worker = new Thread(() =>
            {
                Action done = work();
                dispatcher.BeginInvoke(done);
            }, 64 * 1024 * 1024); // grosse pile : les arborescences peuvent être très profondes
            worker.SetApartmentState(ApartmentState.STA); // nécessaire aux fenêtres de Windows (corbeille…)
            worker.IsBackground = true;
            worker.Start();
        }

        private void SetBusy(bool busy, bool scanning)
        {
            _busy = busy;
            _btnScan.IsEnabled = !busy && _target != null;
            _btnCancel.Visibility = busy && scanning ? Visibility.Visible : Visibility.Collapsed;
            _btnCollapse.IsEnabled = !busy && _rows.Count > 0;
            _busyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            _navBrowse.IsEnabled = !busy;
            _btnUpdate.IsEnabled = !busy;
            foreach (var button in _driveButtons) button.IsEnabled = !busy;
            _tree.IsHitTestVisible = !busy;
            Window.Cursor = busy ? (scanning ? Cursors.AppStarting : Cursors.Wait) : null;
            if (busy && scanning) _progressTimer.Start(); else _progressTimer.Stop();
        }

        private void ShowProgress()
        {
            if (_scanner == null) return;
            _kpiFiles.Text = _scanner.FilesScanned.ToString("N0");
            _status.Text = "Analyse en cours… " + _scanner.CurrentPath;
        }

        private void ShowEmptyState(string title, string text)
        {
            _emptyTitle.Text = title;
            _emptyText.Text = text;
            _emptyState.Visibility = Visibility.Visible;
        }

        private void OnScanFinished(Scanner scanner, DirNode root, Exception error)
        {
            _scanner = null;
            _clock.Stop();

            if (error != null)
            {
                SetBusy(false, false);
                _status.Text = "L'analyse a échoué.";
                ShowEmptyState("L'analyse a échoué", error.Message);
            }
            else if (root != null)
            {
                _root = root;
                _emptyState.Visibility = Visibility.Collapsed;
                var row = new RowItem(new ItemInfo { Dir = root, Name = root.FullPath, FullPath = root.FullPath }, 0);
                _rows.Add(row);
                Expand(row);
                SetBusy(false, false);
                _tree.SelectedItem = row;
                _tree.Focus();

                string status = "Analysé en " + (_clock.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s";
                if (scanner.DeniedCount > 0)
                {
                    status += " · " + Format.Count(scanner.DeniedCount, "dossier") + " inaccessible" + (scanner.DeniedCount > 1 ? "s" : "");
                    if (!_isAdmin) status += " (lance en administrateur pour tout voir)";
                }
                if (scanner.Cancelled) status = "Analyse annulée : résultats partiels · " + status;
                _status.Text = status;
                UpdateKpis();
            }

            var handler = ScanCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void UpdateKpis()
        {
            if (_root != null)
            {
                _kpiSize.Text = Format.Size(_root.Size);
                _kpiFiles.Text = _root.FileCount.ToString("N0");
                _kpiDirs.Text = _root.DirCount.ToString("N0");
            }
            UpdateDriveKpi();
        }

        private void UpdateDriveKpi()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_target));
                long used = drive.TotalSize - drive.TotalFreeSpace;
                double percent = Format.Percent(used, drive.TotalSize);
                _kpiDriveLabel.Text = "Espace libre sur " + drive.Name.TrimEnd('\\');
                _kpiFree.Text = Format.Size(drive.TotalFreeSpace);
                _kpiDriveInfo.Text = Format.Size(used) + " utilisés sur " + Format.Size(drive.TotalSize)
                    + " (" + Format.PercentText(percent) + ")";
                _driveUsedCol.Width = new GridLength(percent, GridUnitType.Star);
                _driveFreeCol.Width = new GridLength(100 - percent, GridUnitType.Star);
                _driveUsedBar.Background = new SolidColorBrush(percent >= 90 ? Color.FromRgb(0xD9, 0x77, 0x06) : Color.FromRgb(0x14, 0xB8, 0xA6));
            }
            catch (Exception)
            {
                _kpiFree.Text = "–";
                _kpiDriveInfo.Text = "";
            }
        }

        // ---------------------------------------------------------------- Arbre

        private void Expand(RowItem row)
        {
            if (row.IsSummary) { ShowMore(row); return; }
            if (!row.CanExpand || row.IsExpanded) return;
            DirNode dir = row.Item.Dir;

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

            InsertChildren(_rows.IndexOf(row) + 1, row.Depth + 1, sorted, dir);
            row.IsExpanded = true;
        }

        /// <summary>
        /// Insère les 500 premiers éléments de la liste, puis une ligne « … et N autres éléments »
        /// sur laquelle on peut cliquer deux fois pour afficher la suite : aucun fichier n'est caché
        /// (un fichier vide, de 0 octet, arrive en toute fin de liste).
        /// </summary>
        private void InsertChildren(int index, int depth, List<ItemInfo> items, DirNode dir)
        {
            foreach (var child in items.Take(MaxItemsPerFolder))
                _rows.Insert(index++, new RowItem(child, depth));

            if (items.Count > MaxItemsPerFolder)
            {
                var rest = items.Skip(MaxItemsPerFolder).ToList();
                long restSize = rest.Sum(c => c.Size);
                _rows.Insert(index, new RowItem(depth,
                    "… et " + rest.Count.ToString("N0") + " autres éléments (double-clic pour afficher la suite)",
                    restSize, Format.Percent(restSize, dir.Size), rest, dir));
            }
        }

        /// <summary>Remplace la ligne « … et N autres éléments » par les éléments suivants.</summary>
        private void ShowMore(RowItem summary)
        {
            int index = _rows.IndexOf(summary);
            if (index < 0) return;
            _rows.RemoveAt(index);
            InsertChildren(index, summary.Depth, summary.Remaining, summary.SummaryDir);
            if (index < _rows.Count) _tree.SelectedItem = _rows[index];
        }

        private void Collapse(RowItem row)
        {
            int index = _rows.IndexOf(row);
            if (index < 0) return;
            while (index + 1 < _rows.Count && _rows[index + 1].Depth > row.Depth)
                _rows.RemoveAt(index + 1);
            row.IsExpanded = false;
        }

        private void Toggle(RowItem row)
        {
            if (row == null || _busy) return;
            if (row.IsSummary) ShowMore(row);
            else if (row.IsExpanded) Collapse(row);
            else Expand(row);
        }

        private void CollapseAll()
        {
            if (_rows.Count == 0) return;
            var root = _rows[0];
            Collapse(root);
            Expand(root);
            _tree.SelectedItem = root;
            _tree.ScrollIntoView(root);
        }

        private RowItem SelectedRow()
        {
            return _tree.SelectedItem as RowItem;
        }

        /// <summary>La ligne du tableau sous un élément cliqué, ou nul (en-tête, zone vide…).</summary>
        private static RowItem RowFromElement(object source, bool ignoreButtons)
        {
            var element = source as DependencyObject;
            while (element != null)
            {
                if (ignoreButtons && element is ButtonBase) return null;
                var row = element as DataGridRow;
                if (row != null) return row.Item as RowItem;
                element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }
            return null;
        }

        private void OnTreeButtonClick(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button;
            if (button == null || !"chevron".Equals(button.Tag)) return;
            Toggle(button.DataContext as RowItem);
            e.Handled = true;
        }

        private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = RowFromElement(e.OriginalSource, true);
            if (row != null) Toggle(row);
        }

        private void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            var row = SelectedRow();
            if (row == null || _busy) return;
            switch (e.Key)
            {
                case Key.Delete:
                    e.Handled = true;
                    DeleteSelected((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    break;
                case Key.F5:
                    e.Handled = true;
                    RefreshSelected();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    Toggle(row);
                    break;
                case Key.Right:
                    e.Handled = true;
                    Expand(row);
                    break;
                case Key.Left:
                    e.Handled = true;
                    if (row.IsExpanded) Collapse(row);
                    else SelectParent(row);
                    break;
            }
        }

        private void SelectParent(RowItem row)
        {
            for (int i = _rows.IndexOf(row) - 1; i >= 0; i--)
            {
                if (_rows[i].Depth < row.Depth)
                {
                    _tree.SelectedItem = _rows[i];
                    _tree.ScrollIntoView(_rows[i]);
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- Menu du clic droit

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var row = RowFromElement(e.OriginalSource, false) ?? SelectedRow();
            if (row == null || row.IsSummary || _busy)
            {
                e.Handled = true;
                return;
            }
            _tree.SelectedItem = row;
            var item = row.Item;

            _menu.Items.Clear();
            AddMenuItem("Ouvrir dans l'Explorateur", null, false, delegate { OpenInExplorer(item); });
            AddMenuItem("Copier le chemin", null, false, delegate { CopyPath(item); });
            if (item.IsDir) AddMenuItem("Actualiser ce dossier", "F5", false, delegate { RefreshSelected(); });
            _menu.Items.Add(new Separator { Style = (Style)Application.Current.Resources["MenuSeparator"] });

            if (item.IsDir)
                foreach (var program in ProgramsFor(item.FullPath))
                {
                    var p = program;
                    AddMenuItem("Désinstaller « " + p.Name + " »…", null, false, delegate { Uninstall(p); });
                }
            AddMenuItem("Mettre à la corbeille", "Suppr", true, delegate { DeleteSelected(false); });
            AddMenuItem("Supprimer définitivement…", "Maj+Suppr", true, delegate { DeleteSelected(true); });
        }

        private void AddMenuItem(string header, string gesture, bool danger, RoutedEventHandler onClick)
        {
            var menuItem = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (danger) menuItem.SetResourceReference(Control.ForegroundProperty, "DangerBrush");
            menuItem.Click += onClick;
            _menu.Items.Add(menuItem);
        }

        private void OpenInExplorer(ItemInfo item)
        {
            try
            {
                if (item.IsDir) Process.Start("explorer.exe", "\"" + item.FullPath + "\"");
                else Process.Start("explorer.exe", "/select,\"" + item.FullPath + "\"");
            }
            catch (Exception ex)
            {
                Dialogs.Show(Window, DialogKind.Error, "Impossible d'ouvrir l'Explorateur", ex.Message, item.FullPath);
            }
        }

        private void CopyPath(ItemInfo item)
        {
            try
            {
                Clipboard.SetText(item.FullPath);
                _status.Text = "Chemin copié : " + item.FullPath;
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        // ---------------------------------------------------------------- Suppression

        /// <summary>Raison pour laquelle on refuse de supprimer cet élément, ou nul si c'est permis.</summary>
        private static string ProtectionReason(ItemInfo item)
        {
            if (item.Parent == null)
                return "Impossible de supprimer le dossier analysé lui-même. Analyse le dossier qui le contient si tu veux vraiment le supprimer.";

            string path = InstalledPrograms.Normalize(item.FullPath) ?? item.FullPath;
            string windows = InstalledPrograms.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (windows != null && (InstalledPrograms.SamePath(path, windows) || InstalledPrograms.IsInside(path, windows)))
                return "Les fichiers de Windows sont protégés : les supprimer peut empêcher l'ordinateur de démarrer. "
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
                return "Ce dossier fait partie de Windows et ne peut pas être supprimé. Tu peux en revanche supprimer ce qu'il contient.";

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
            var row = SelectedRow();
            if (row == null || row.IsSummary || _busy) return;
            var item = row.Item;

            string refusal = ProtectionReason(item);
            if (refusal != null)
            {
                Dialogs.Show(Window, DialogKind.Info, "Suppression impossible", refusal, item.FullPath);
                return;
            }

            string what = item.IsDir ? "Le dossier" : "Le fichier";
            string text = permanent
                ? what + " « " + item.Name + " » sera supprimé définitivement. Cette action est irréversible."
                : what + " « " + item.Name + " » sera déplacé dans la corbeille. Tu pourras le récupérer tant qu'elle n'est pas vidée.";

            var programs = item.IsDir ? ProgramsFor(item.FullPath) : new List<InstalledProgram>();
            if (programs.Count > 0)
                text += "\n\nAttention : ce dossier contient le programme « " + programs[0].Name + " ». "
                    + "Pour l'enlever proprement, utilise plutôt clic droit → Désinstaller.";

            string detail = item.FullPath + "\n" + Format.Size(item.Size)
                + (item.IsDir ? " · " + Format.Count(item.Dir.FileCount, "fichier") : "");

            DialogKind kind = permanent ? DialogKind.Danger : programs.Count > 0 ? DialogKind.Warning : DialogKind.Question;
            bool confirmed = Dialogs.Ask(Window, kind,
                permanent ? "Supprimer définitivement ?" : "Mettre à la corbeille ?", text, detail,
                permanent ? "Supprimer définitivement" : "Mettre à la corbeille", "Annuler");
            if (confirmed) DeleteRow(row, permanent);
        }

        /// <summary>Supprime l'élément sans demander de confirmation (déjà faite), puis met l'arbre à jour.</summary>
        private void DeleteRow(RowItem row, bool permanent)
        {
            var item = row.Item;
            SetBusy(true, false);
            _status.Text = (permanent ? "Suppression de " : "Mise à la corbeille de ") + item.FullPath + "…";
            var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;

            RunInBackground(() =>
            {
                string error = null;
                bool cancelled = false;
                try
                {
                    // Les fenêtres d'erreur de Windows (fichier ouvert, etc.) s'affichent si besoin.
                    if (item.IsDir) FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, recycle);
                    else FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, recycle);
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { error = ex.Message; }

                // Une suppression peut être partielle (fichier ouvert…) : on remesure ce qui reste.
                DirNode remaining = Rescan(item);
                return (Action)(() =>
                {
                    long before = item.Size;
                    bool gone = ApplyRescan(row, item, remaining);
                    SetBusy(false, false);

                    if (gone)
                    {
                        _status.Text = "« " + item.Name + " » " + (permanent ? "supprimé définitivement" : "mis à la corbeille")
                            + " · " + Format.Size(before) + (permanent ? " libérés" : " (vide la corbeille pour libérer la place)");
                    }
                    else
                    {
                        _status.Text = (cancelled ? "Suppression annulée" : "Suppression incomplète") + " de « " + item.Name + " » · "
                            + Format.Size(before - item.Size) + " libérés, " + Format.Size(item.Size) + " restants";
                        if (error != null)
                            Dialogs.Show(Window, DialogKind.Error, "Suppression incomplète",
                                "Une partie de « " + item.Name + " » n'a pas pu être supprimée.", error);
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
        /// Met le modèle et le tableau en accord avec le disque après une suppression ou une actualisation.
        /// Renvoie vrai si l'élément n'existe plus.
        /// </summary>
        private bool ApplyRescan(RowItem row, ItemInfo item, DirNode rescanned)
        {
            bool exists = item.IsDir ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath);

            if (!exists)
            {
                if (item.IsDir) item.Parent.RemoveDir(item.Dir);
                else item.Parent.RemoveFile(item.File);
                int index = _rows.IndexOf(row);
                Collapse(row);
                _rows.Remove(row);
                if (index > 0 && index <= _rows.Count)
                    _tree.SelectedItem = _rows[Math.Min(index, _rows.Count - 1)];
            }
            else if (item.IsDir && rescanned != null)
            {
                item.Parent.ReplaceDir(item.Dir, rescanned);
                item.Dir = rescanned;
                bool expanded = row.IsExpanded;
                Collapse(row);
                if (expanded) Expand(row);
                _tree.SelectedItem = row;
            }

            foreach (var r in _rows) r.Refresh();
            UpdateKpis();
            FillDrives();
            return !exists;
        }

        private void RaiseOperationCompleted()
        {
            var handler = OperationCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- Actualisation

        private void RefreshSelected()
        {
            var row = SelectedRow();
            if (row == null || row.IsSummary || !row.Item.IsDir || _busy) return;
            var item = row.Item;

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
                    bool gone = ApplyRescan(row, item, rescanned);
                    SetBusy(false, false);
                    _status.Text = gone
                        ? "« " + item.Name + " » n'existe plus."
                        : "« " + item.Name + " » actualisé · " + Format.Size(item.Size);
                    RaiseOperationCompleted();
                });
            });
        }

        // ---------------------------------------------------------------- Mise à jour de l'appli (bouton « maj git »)

        private static string ExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        private void UpdateApp()
        {
            if (_busy) return;
            SetBusy(true, false);
            string previousStatus = _status.Text;
            _status.Text = "Recherche de nouveautés sur GitHub…";

            RunInBackground(() =>
            {
                UpdateCheck check = Updater.Check(ExePath);
                return (Action)(() =>
                {
                    SetBusy(false, false);
                    _status.Text = previousStatus;
                    if (check.Error != null)
                    {
                        Dialogs.Show(Window, DialogKind.Warning, "Mise à jour impossible", check.Error,
                            check.RepoDir == null ? "Pour pouvoir mettre l'appli à jour, récupère-la avec :\ngit clone "
                                + "https://github.com/saladetomateoignonchef/-quadri-size.git\npuis lance build.bat." : null);
                        return;
                    }
                    if (!check.HasUpdate)
                    {
                        Dialogs.Show(Window, DialogKind.Info, "Quadri-Size est à jour",
                            "Tu as déjà la dernière version disponible sur GitHub.", check.RepoDir);
                        return;
                    }

                    var list = check.NewCommits.Take(8).Select(c => "• " + c).ToList();
                    if (check.NewCommits.Count > 8) list.Add("… et " + (check.NewCommits.Count - 8) + " autres");
                    bool confirmed = Dialogs.Ask(Window, DialogKind.Question,
                        check.NewCommits.Count > 1 ? check.NewCommits.Count + " nouveautés disponibles" : "Une nouveauté disponible",
                        "Quadri-Size va télécharger les changements depuis GitHub, se recompiler puis redémarrer."
                        + (_rows.Count > 0 ? " L'analyse affichée sera perdue." : ""),
                        string.Join("\n", list), "Mettre à jour", "Plus tard");
                    if (confirmed) ApplyUpdate(check);
                });
            });
        }

        private void ApplyUpdate(UpdateCheck check)
        {
            if (_scanner != null) _scanner.Cancel();
            SetBusy(true, false);
            _status.Text = "Mise à jour : téléchargement puis compilation… (environ 30 secondes)";

            RunInBackground(() =>
            {
                string error = Updater.Apply(check, ExePath);
                return (Action)(() =>
                {
                    SetBusy(false, false);
                    if (error != null)
                    {
                        _status.Text = "La mise à jour a échoué.";
                        Dialogs.Show(Window, DialogKind.Error, "La mise à jour a échoué", error.Split('\n')[0],
                            string.Join("\n", error.Split('\n').Skip(1)).Trim());
                        return;
                    }
                    try
                    {
                        Updater.StartNewVersion(ExePath);
                        Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Dialogs.Show(Window, DialogKind.Info, "Mise à jour terminée",
                            "La nouvelle version est prête, mais n'a pas pu démarrer toute seule. Ferme Quadri-Size et relance-le.", ex.Message);
                    }
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
            bool confirmed = Dialogs.Ask(Window, DialogKind.Question, "Désinstaller « " + program.Name + " » ?",
                "Le désinstalleur du programme va s'ouvrir. C'est la même chose que de le désinstaller depuis "
                + "Paramètres → Applications → Applications installées.",
                program.InstallLocation, "Désinstaller", "Annuler");
            if (!confirmed) return;

            try
            {
                string file, arguments;
                InstalledPrograms.SplitCommand(program.UninstallCommand, out file, out arguments);
                Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
                _programs = null; // la liste aura changé une fois la désinstallation finie
                _status.Text = "Désinstalleur de « " + program.Name + " » lancé. Quand il a fini, appuie sur F5 pour actualiser le dossier.";
            }
            catch (Exception ex)
            {
                Dialogs.Show(Window, DialogKind.Error, "Désinstalleur introuvable",
                    "Impossible de lancer le désinstalleur de « " + program.Name + " ». Tu peux le désinstaller depuis "
                    + "Paramètres → Applications → Applications installées.", ex.Message);
            }
        }
    }
}
