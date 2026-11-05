// Localization for Ultra Cast: English and Serbian (Latin script).
//
// How it works: the English text is the key. L.T("Open") returns the Serbian text when Serbian is on,
// and the English text otherwise (also for anything not translated yet, so nothing ever shows a blank).
// L.Apply(window) walks the window and translates every fixed label (Content, Header, Text, Title,
// tooltips and screen-reader names) in place; it can be called again after switching the language,
// so the switch is instant, no restart. Values that come from bindings are never touched.
// Inside DataTemplates use {loc:T 'English text'} in XAML.
//
// The choice is saved in %LOCALAPPDATA%\UltraCast\language.txt. Until the user picks one,
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

namespace UltraCast
{
    public static partial class L
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraCast", "language.txt");

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
            ["About Ultra Cast"] = "O programu Ultra Cast",
            ["Choose Output Folder..."] = "Izaberi izlazni folder...",
            ["Choose output folder for recordings"] = "Izaberi izlazni folder za snimke",
            ["No output folder chosen"] = "Izlazni folder nije izabran",
            ["Selected output folder"] = "Izabrani izlazni folder",
            ["Output: {0}"] = "Izlaz: {0}",
            ["Record system audio (includes screen reader speech)"] = "Snimaj zvuk sistema (uključuje govor čitača ekrana)",
            ["Record system audio, including screen reader speech"] = "Snimaj zvuk sistema, uključujući govor čitača ekrana",
            ["Record microphone (narration)"] = "Snimaj mikrofon (naracija)",
            ["Record microphone for narration"] = "Snimaj mikrofon za naraciju",
            ["Start Recording"] = "Počni snimanje",
            ["Start recording the screen"] = "Počni snimanje ekrana",
            ["Pause"] = "Pauza",
            ["Resume"] = "Nastavi",
            ["Pause or resume recording"] = "Pauziraj ili nastavi snimanje",
            ["Stop Recording"] = "Zaustavi snimanje",
            ["Stop recording and save"] = "Zaustavi snimanje i sačuvaj",
            ["Global hotkeys (work from any window): Ctrl+Alt+R = start/stop, Ctrl+Alt+P = pause/resume."] = "Globalne prečice (rade iz bilo kog prozora): Ctrl+Alt+R = počni/zaustavi, Ctrl+Alt+P = pauza/nastavak.",
            ["Status"] = "Stanje",
            ["Log"] = "Dnevnik",
            ["Activity log"] = "Dnevnik aktivnosti",
            ["Choose an output folder before starting."] = "Pre početka izaberi izlazni folder.",
            ["Ready. Choose an output folder, then start recording (Ctrl+Alt+R works from any window)."] = "Spremno. Izaberi izlazni folder, pa počni snimanje (Ctrl+Alt+R radi iz bilo kog prozora).",
            ["Problem: {0}"] = "Problem: {0}",
            ["Recording. Press Ctrl+Alt+R from anywhere to stop, Ctrl+Alt+P to pause."] = "Snimam. Ctrl+Alt+R bilo gde zaustavlja snimanje, Ctrl+Alt+P pauzira.",
            ["Could not start recording: {0}  (see log: {1})"] = "Snimanje nije moglo da počne: {0}  (pogledaj dnevnik: {1})",
            ["Finishing up - combining video and audio..."] = "Završavam - spajam video i zvuk...",
            ["Saved: {0}"] = "Sačuvano: {0}",
            ["Recording stopped, but there was a problem saving it: {0}  (see log: {1})"] = "Snimanje je zaustavljeno, ali je bilo problema pri čuvanju: {0}  (pogledaj dnevnik: {1})",
            ["Paused. Press Ctrl+Alt+P to resume."] = "Pauzirano. Ctrl+Alt+P nastavlja.",
            ["Recording resumed."] = "Snimanje je nastavljeno.",
            ["System audio capture stopped: {0}"] = "Snimanje zvuka sistema je prekinuto: {0}",
            ["Microphone capture stopped: {0}"] = "Snimanje mikrofona je prekinuto: {0}",
            ["Audio mixing failed: {0}"] = "Mešanje zvuka nije uspelo: {0}",
            ["Video: {0}"] = "Video: {0}",
            ["Audio: {0}"] = "Zvuk: {0}",
            ["No output folder chosen."] = "Izlazni folder nije izabran.",
            ["Not currently recording."] = "Snimanje trenutno nije u toku.",
            ["Combining video and audio failed: {0}"] = "Spajanje videa i zvuka nije uspelo: {0}",
            ["Ultra Cast ran into a problem and had to recover:\n\n{0}\n\nDetails were written to:\n{1}"] = "Ultra Cast je naišao na problem i morao je da se oporavi:\n\n{0}\n\nDetalji su upisani u:\n{1}",
            ["Ultra Cast - Error"] = "Ultra Cast - greška",
            ["Could not start ffmpeg.exe to combine video and audio."] = "ffmpeg.exe nije mogao da se pokrene za spajanje videa i zvuka.",
            ["Could not start ffmpeg.exe for screen capture."] = "ffmpeg.exe nije mogao da se pokrene za snimanje ekrana.",
            ["Accessible screen recording for tutorials, demos, and walkthroughs. Records the screen along with system audio - which naturally includes screen-reader speech - and an optional microphone track for narration. Fully controllable from the keyboard, including global hotkeys that work even while another window is focused. Part of the Ultra suite of accessible software."] = "Pristupačno snimanje ekrana za uputstva, demonstracije i prikaze. Snima ekran zajedno sa zvukom sistema - koji prirodno uključuje i govor čitača ekrana - i, po želji, mikrofon za naraciju. Potpuno se upravlja tastaturom, uključujući globalne prečice koje rade i dok je u fokusu drugi prozor. Deo Ultra porodice pristupačnih programa.",
        };
    }
}
