using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuadriSize
{
    /// <summary>Thème clair / sombre (mêmes couleurs que Driver) et chargement des fichiers XAML.</summary>
    internal static class Theme
    {
        public const string Light = "Light";
        public const string Dark = "Dark";

        private static readonly Dictionary<string, string> LightPalette = new Dictionary<string, string>
        {
            { "BgBrush", "#F4F6F8" }, { "SurfaceBrush", "#FFFFFF" }, { "SurfaceAltBrush", "#F8FAFC" }, { "LineBrush", "#E3E8ED" },
            { "TextBrush", "#17202A" }, { "MutedBrush", "#5D6B78" }, { "BtnLineBrush", "#D3DAE1" }, { "ChipBrush", "#F1F5F9" },
            { "ChipTextBrush", "#334155" }, { "DropLineBrush", "#9FB3C5" }, { "SelectedBrush", "#E6F4F1" }, { "HoverBrush", "#F1F5F9" },
            { "DangerBrush", "#DC2626" }, { "FolderBrush", "#F5B942" },
        };

        private static readonly Dictionary<string, string> DarkPalette = new Dictionary<string, string>
        {
            { "BgBrush", "#0E1621" }, { "SurfaceBrush", "#16212D" }, { "SurfaceAltBrush", "#1B2836" }, { "LineBrush", "#263546" },
            { "TextBrush", "#E6EDF3" }, { "MutedBrush", "#93A4B5" }, { "BtnLineBrush", "#34465A" }, { "ChipBrush", "#223244" },
            { "ChipTextBrush", "#CBD5E1" }, { "DropLineBrush", "#45607A" }, { "SelectedBrush", "#173A37" }, { "HoverBrush", "#1B2836" },
            { "DangerBrush", "#F87171" }, { "FolderBrush", "#E0A93A" },
        };

        public static string Current { get; private set; }

        /// <summary>Crée l'application WPF si besoin et charge les styles de Theme.xaml.</summary>
        public static void EnsureApplication()
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            if (!Application.Current.Resources.Contains("Card"))
                Application.Current.Resources = (ResourceDictionary)LoadXaml("Theme.xaml");
        }

        /// <summary>Charge un fichier XAML intégré à l'exécutable (dossier src/ui).</summary>
        public static object LoadXaml(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuadriSize.ui." + name))
            {
                if (stream == null) throw new InvalidOperationException("Fichier d'interface manquant : " + name);
                return XamlReader.Load(stream);
            }
        }

        public static void Apply(string name)
        {
            var palette = name == Dark ? DarkPalette : LightPalette;
            foreach (var pair in palette)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
                brush.Freeze();
                Application.Current.Resources[pair.Key] = brush;
            }
            Current = name == Dark ? Dark : Light;
        }

        private static string SettingsFile
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QuadriSize", "theme.txt");
            }
        }

        /// <summary>Le thème choisi la dernière fois, sinon celui de Windows.</summary>
        public static string LoadPreference()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    return File.ReadAllText(SettingsFile).Trim() == Dark ? Dark : Light;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null && Equals(key.GetValue("AppsUseLightTheme"), 0)) return Dark;
                }
            }
            catch (System.Security.SecurityException) { }
            return Light;
        }

        public static void SavePreference(string name)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, name);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Barre de titre bleu nuit, comme le menu de gauche (Windows 11).</summary>
        public static void ColorCaption(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                int caption = 0x003A2612; // #12263A, écrit en BGR
                int text = 0x00FFFFFF;
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4); // DWMWA_CAPTION_COLOR
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);    // DWMWA_TEXT_COLOR
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}
