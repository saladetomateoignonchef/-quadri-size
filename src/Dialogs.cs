using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuadriSize
{
    internal enum DialogKind { Info, Question, Warning, Error, Danger }

    /// <summary>Fenêtres de message aux couleurs de l'appli (Dialog.xaml), à la place des boîtes grises de Windows.</summary>
    internal static class Dialogs
    {
        /// <summary>Pose une question. Renvoie vrai si on clique sur le bouton principal.</summary>
        public static bool Ask(Window owner, DialogKind kind, string title, string text, string detail,
            string primary, string secondary)
        {
            var dialog = Create(owner, kind, title, text, detail, primary, secondary);
            dialog.ShowDialog();
            return (bool)dialog.Tag;
        }

        public static void Show(Window owner, DialogKind kind, string title, string text, string detail = null)
        {
            Create(owner, kind, title, text, detail, "OK", null).ShowDialog();
        }

        public static Window Create(Window owner, DialogKind kind, string title, string text, string detail,
            string primary, string secondary)
        {
            var dialog = (Window)Theme.LoadXaml("Dialog.xaml");
            if (owner != null && owner.IsLoaded) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Title = title;
            dialog.Tag = false;

            string background, foreground, glyph;
            switch (kind)
            {
                case DialogKind.Warning: background = "#FEF3C7"; foreground = "#B45309"; glyph = "!"; break;
                case DialogKind.Error: background = "#FEE2E2"; foreground = "#B91C1C"; glyph = "✕"; break;
                case DialogKind.Danger: background = "#FEE2E2"; foreground = "#B91C1C"; glyph = "!"; break;
                case DialogKind.Question: background = "#CCFBF1"; foreground = "#0F766E"; glyph = "?"; break;
                default: background = "#CCFBF1"; foreground = "#0F766E"; glyph = "i"; break;
            }
            ((Border)dialog.FindName("DlgIcon")).Background = ToBrush(background);
            var glyphText = (TextBlock)dialog.FindName("DlgGlyph");
            glyphText.Foreground = ToBrush(foreground);
            glyphText.Text = glyph;
            ((TextBlock)dialog.FindName("DlgTitle")).Text = title;
            ((TextBlock)dialog.FindName("DlgText")).Text = text;
            if (!string.IsNullOrEmpty(detail))
            {
                ((TextBlock)dialog.FindName("DlgDetail")).Text = detail;
                ((Border)dialog.FindName("DlgDetailBox")).Visibility = Visibility.Visible;
            }

            var primaryButton = (Button)dialog.FindName("BtnDlgPrimary");
            var secondaryButton = (Button)dialog.FindName("BtnDlgSecondary");
            primaryButton.Content = primary;
            if (kind == DialogKind.Danger) primaryButton.Style = (Style)Application.Current.Resources["BtnDanger"];

            if (secondary == null)
            {
                secondaryButton.Visibility = Visibility.Collapsed;
                primaryButton.IsCancel = true;
                primaryButton.IsDefault = true;
            }
            else
            {
                secondaryButton.Content = secondary;
                // Pour une action risquée, Entrée ne doit pas confirmer par accident.
                primaryButton.IsDefault = kind != DialogKind.Danger;
            }

            primaryButton.Click += delegate { dialog.Tag = true; dialog.Close(); };
            secondaryButton.Click += delegate { dialog.Close(); };
            ((FrameworkElement)dialog.FindName("DlgHeader")).MouseLeftButtonDown += delegate
            {
                try { dialog.DragMove(); } catch (System.InvalidOperationException) { }
            };
            dialog.Loaded += delegate
            {
                if (kind == DialogKind.Danger && secondary != null) secondaryButton.Focus();
                else primaryButton.Focus();
            };
            return dialog;
        }

        private static Brush ToBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
