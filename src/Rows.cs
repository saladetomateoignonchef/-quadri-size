using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

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

    /// <summary>
    /// Une ligne du tableau. L'arbre est affiché « à plat » : déplier un dossier insère ses
    /// enfants juste en dessous, avec un retrait. Ça garde les colonnes alignées.
    /// </summary>
    public sealed class RowItem : INotifyPropertyChanged
    {
        private static readonly Brush DirBar = Frozen(0x14, 0xB8, 0xA6);
        private static readonly Brush FileBar = Frozen(0x94, 0xA3, 0xB8);

        internal readonly ItemInfo Item; // nul pour la ligne « … et N autres éléments »
        public readonly int Depth;

        private readonly string _summaryText;
        private readonly long _summarySize;
        private readonly double _summaryPercent;
        private bool _expanded;

        public event PropertyChangedEventHandler PropertyChanged;

        internal RowItem(ItemInfo item, int depth)
        {
            Item = item;
            Depth = depth;
        }

        internal RowItem(int depth, string text, long size, double percent)
        {
            Depth = depth;
            _summaryText = text;
            _summarySize = size;
            _summaryPercent = percent;
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public bool IsSummary { get { return Item == null; } }
        public bool CanExpand { get { return Item != null && Item.IsDir && Item.Dir.HasChildren; } }

        public bool IsExpanded
        {
            get { return _expanded; }
            set
            {
                _expanded = value;
                Notify("IsExpanded");
                Notify("ChevronAngle");
            }
        }

        public Thickness Indent { get { return new Thickness(4 + Depth * 22, 0, 0, 0); } }
        public Visibility ChevronVisibility { get { return CanExpand ? Visibility.Visible : Visibility.Hidden; } }
        public double ChevronAngle { get { return _expanded ? 90 : 0; } }
        public Visibility IconVisibility { get { return IsSummary ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility FolderVisibility { get { return Item != null && Item.IsDir ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility FileVisibility { get { return Item != null && !Item.IsDir ? Visibility.Visible : Visibility.Collapsed; } }

        public string Name { get { return IsSummary ? _summaryText : Item.Name; } }
        public FontWeight NameWeight { get { return Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal; } }
        public double NameOpacity { get { return IsSummary ? 0.6 : 1; } }

        public Visibility DeniedVisibility
        {
            get { return Item != null && Item.IsDir && Item.Dir.Denied ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string SizeText { get { return Format.Size(IsSummary ? _summarySize : Item.Size); } }
        public double Percent { get { return IsSummary ? _summaryPercent : Item.Percent; } }
        public string PercentText { get { return Format.PercentText(Percent); } }

        private double ClampedPercent { get { return Math.Max(0, Math.Min(100, Percent)); } }
        public GridLength BarFill { get { return new GridLength(ClampedPercent, GridUnitType.Star); } }
        public GridLength BarRest { get { return new GridLength(100 - ClampedPercent, GridUnitType.Star); } }
        public Brush BarBrush { get { return Item != null && Item.IsDir ? DirBar : FileBar; } }

        public string FilesText
        {
            get { return Item != null && Item.IsDir ? Format.Count(Item.Dir.FileCount, "fichier") : ""; }
        }

        /// <summary>Redemande à l'écran toutes les valeurs (après une suppression, les tailles changent).</summary>
        public void Refresh()
        {
            Notify(string.Empty);
        }

        private void Notify(string property)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
