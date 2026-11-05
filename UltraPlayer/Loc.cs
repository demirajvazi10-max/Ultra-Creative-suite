// Localization for Ultra Player: English and Serbian (Latin script).
//
// How it works: the English text is the key. L.T("Open") returns the Serbian text when Serbian is on,
// and the English text otherwise (also for anything not translated yet, so nothing ever shows a blank).
// L.Apply(window) walks the window and translates every fixed label (Content, Header, Text, Title,
// tooltips and screen-reader names) in place; it can be called again after switching the language,
// so the switch is instant, no restart. Values that come from bindings are never touched.
// Inside DataTemplates use {loc:T 'English text'} in XAML.
//
// The choice is saved in %LOCALAPPDATA%\UltraPlayer\language.txt. Until the user picks one,
// the language follows Windows (Serbian Windows -> Serbian, anything else -> English).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace UltraPlayer
{
    public static partial class L
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraPlayer", "language.txt");

        private static Dictionary<string, string>? _toEnglish;

        public static bool Serbian { get; private set; }

        /// <summary>Raised after the language changes, so windows can refresh texts they build in code.</summary>
        public static event Action? Changed;

        static L()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    Serbian = File.ReadAllText(SettingsPath).Trim() == "sr";
                    return;
                }
            }
            catch { }
            Serbian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "sr";
        }

        public static string T(string english)
            => Serbian && Sr.TryGetValue(english, out var s) ? s : english;

        public static string F(string englishFormat, params object?[] args)
        {
            try { return string.Format(T(englishFormat), args); }
            catch (FormatException) { return string.Format(englishFormat, args); }
        }

        public static void SetLanguage(bool serbian)
        {
            Serbian = serbian;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, serbian ? "sr" : "en");
            }
            catch { }
            foreach (Window w in Application.Current.Windows) Apply(w);
            Changed?.Invoke();
        }

        /// <summary>Switches between English and Serbian and says so (the button that calls this keeps focus).</summary>
        public static void Toggle(Button languageButton)
        {
            SetLanguage(!Serbian);
            UpdateLanguageButton(languageButton);
            var peer = UIElementAutomationPeer.FromElement(languageButton) ?? UIElementAutomationPeer.CreatePeerForElement(languageButton);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent,
                Serbian ? "Jezik: srpski" : "Language: English", "UltraLanguage");
        }

        /// <summary>The language button always shows the OTHER language, written in that language.</summary>
        public static void UpdateLanguageButton(Button b)
        {
            b.Content = Serbian ? "English" : "Srpski";
            AutomationProperties.SetName(b, Serbian ? "Switch to English (jezik: srpski)" : "Prebaci na srpski (language: English)");
        }

        private static string Translate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (_toEnglish == null)
            {
                _toEnglish = new Dictionary<string, string>();
                foreach (var kv in Sr) _toEnglish[kv.Value] = kv.Key;
            }
            string english = _toEnglish.TryGetValue(text, out var en) ? en : text;
            return T(english);
        }

        // Translates one property only when it holds a plain local string (not a binding or resource).
        private static void Fix(DependencyObject o, DependencyProperty p)
        {
            if (o.ReadLocalValue(p) is string s && !BindingOperations.IsDataBound(o, p))
            {
                var t = Translate(s);
                if (!ReferenceEquals(t, s) && t != s) o.SetValue(p, t);
            }
        }

        /// <summary>Translates every fixed label under <paramref name="root"/> to the current language.</summary>
        public static void Apply(DependencyObject root)
        {
            var seen = new HashSet<DependencyObject>();
            Walk(root, seen);
        }

        private static void Walk(DependencyObject o, HashSet<DependencyObject> seen)
        {
            if (!seen.Add(o)) return;

            Fix(o, AutomationProperties.NameProperty);
            Fix(o, AutomationProperties.HelpTextProperty);
            Fix(o, FrameworkElement.ToolTipProperty);
            switch (o)
            {
                case Window w: Fix(w, Window.TitleProperty); break;
                case TextBlock tb: Fix(tb, TextBlock.TextProperty); break;
                case HeaderedContentControl hc: Fix(hc, HeaderedContentControl.HeaderProperty); break;
                case HeaderedItemsControl hi: Fix(hi, HeaderedItemsControl.HeaderProperty); break;
            }
            if (o is ContentControl cc && o is not Window) Fix(cc, ContentControl.ContentProperty);
            if (o is ListView lv && lv.View is GridView gv)
                foreach (var col in gv.Columns) Fix(col, GridViewColumn.HeaderProperty);

            // Logical children (also reaches things not yet shown, like other tabs) ...
            foreach (var child in LogicalTreeHelper.GetChildren(o))
                if (child is DependencyObject d) Walk(d, seen);
            // ... and visual children (reaches the inside of controls such as UserControls and templates).
            if (o is System.Windows.Media.Visual || o is System.Windows.Media.Media3D.Visual3D)
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(o, i), seen);
            }
        }
    }

    /// <summary>XAML: {loc:T 'English text'} - for texts inside DataTemplates.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension() { }
        public TExtension(string text) { Text = text; }
        [ConstructorArgument("text")] public string Text { get; set; } = "";
        public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
    }

    public static partial class L
    {
        // English text -> Serbian (Latin script).
        private static readonly Dictionary<string, string> Sr = new()
        {
            ["Theme:"] = "Tema:",
            ["Theme selector"] = "Izbor teme",
            ["Light"] = "Svetla",
            ["Dark"] = "Tamna",
            ["High Contrast"] = "Visok kontrast",
            ["About"] = "O programu",
            ["Close"] = "Zatvori",
            ["Close about dialog"] = "Zatvori prozor O programu",
            ["How to support this project"] = "Kako podržati projekat",
            ["Created by Demir Ajvazi"] = "Autor: Demir Ajvazi",
            ["Version 0.1 (early skeleton)"] = "Verzija 0.1 (rana osnovna verzija)",
            ["Ready."] = "Spremno.",
            ["Add Files..."] = "Dodaj fajlove...",
            ["Add audio files to playlist"] = "Dodaj audio fajlove na listu",
            ["Remove"] = "Ukloni",
            ["Remove selected track from playlist"] = "Ukloni izabranu numeru sa liste",
            ["About Ultra Player"] = "O programu Ultra Player",
            ["Playlist"] = "Lista za reprodukciju",
            ["Previous"] = "Prethodna",
            ["Previous track"] = "Prethodna numera",
            ["Play (Space)"] = "Pusti (razmak)",
            ["Pause (Space)"] = "Pauza (razmak)",
            ["Play or pause"] = "Pusti ili pauziraj",
            ["Stop"] = "Zaustavi",
            ["Stop playback"] = "Zaustavi reprodukciju",
            ["Next"] = "Sledeća",
            ["Next track"] = "Sledeća numera",
            ["Playback position"] = "Pozicija reprodukcije",
            ["Speed:"] = "Brzina:",
            ["Playback speed"] = "Brzina reprodukcije",
            ["Sleep timer:"] = "Tajmer za spavanje:",
            ["Sleep timer"] = "Tajmer za spavanje",
            ["Off"] = "Isključen",
            ["15 minutes"] = "15 minuta",
            ["30 minutes"] = "30 minuta",
            ["45 minutes"] = "45 minuta",
            ["60 minutes"] = "60 minuta",
            ["End of track"] = "Kraj numere",
            ["Add files to build a playlist."] = "Dodaj fajlove da napraviš listu za reprodukciju.",
            ["Audio files (*.mp3;*.wav;*.m4a;*.wma;*.aac)|*.mp3;*.wav;*.m4a;*.wma;*.aac|All files (*.*)|*.*"] = "Audio fajlovi (*.mp3;*.wav;*.m4a;*.wma;*.aac)|*.mp3;*.wav;*.m4a;*.wma;*.aac|Svi fajlovi (*.*)|*.*",
            ["{0} file(s) added. Playlist has {1} track(s)."] = "Dodato fajlova: {0}. Na listi je numera: {1}.",
            ["Playlist is empty. Add files first."] = "Lista je prazna. Prvo dodaj fajlove.",
            ["Playing: {0}"] = "Svira: {0}",
            ["Paused."] = "Pauzirano.",
            ["Playing."] = "Svira.",
            ["Stopped."] = "Zaustavljeno.",
            ["Sleep timer: stopped at end of track."] = "Tajmer za spavanje: zaustavljeno na kraju numere.",
            ["Sleep timer: off"] = "Tajmer za spavanje: isključen",
            ["Sleep timer: end of current track"] = "Tajmer za spavanje: kraj trenutne numere",
            ["Sleep timer: {0} minutes remaining"] = "Tajmer za spavanje: preostalo minuta: {0}",
            ["Sleep timer: playback paused."] = "Tajmer za spavanje: reprodukcija je zaustavljena.",
            ["Sleep timer: {0} minute(s) remaining"] = "Tajmer za spavanje: preostalo minuta: {0}",
            ["An accessible podcast/audiobook player - playlist, adjustable playback speed, and a sleep timer, all fully keyboard controlled. Part of the Ultra suite of accessible software."] = "Pristupačan plejer za podkaste i audio-knjige: lista za reprodukciju, podesiva brzina i tajmer za spavanje, sve potpuno sa tastature. Deo Ultra porodice pristupačnih programa.",
        };
    }
}
