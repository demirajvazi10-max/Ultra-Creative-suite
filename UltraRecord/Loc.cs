// Localization for Ultra Record: English and Serbian (Latin script).
//
// How it works: the English text is the key. L.T("Open") returns the Serbian text when Serbian is on,
// and the English text otherwise (also for anything not translated yet, so nothing ever shows a blank).
// L.Apply(window) walks the window and translates every fixed label (Content, Header, Text, Title,
// tooltips and screen-reader names) in place; it can be called again after switching the language,
// so the switch is instant, no restart. Values that come from bindings are never touched.
// Inside DataTemplates use {loc:T 'English text'} in XAML.
//
// The choice is saved in %LOCALAPPDATA%\UltraRecord\language.txt. Until the user picks one,
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

namespace UltraRecord
{
    public static partial class L
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraRecord", "language.txt");

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
            ["Add Track"] = "Dodaj traku",
            ["Add new track"] = "Dodaj novu traku",
            ["Remove Track"] = "Ukloni traku",
            ["Remove selected track"] = "Ukloni izabranu traku",
            ["Choose Output Folder..."] = "Izaberi izlazni folder...",
            ["Choose output folder"] = "Izaberi izlazni folder",
            ["About Ultra Record"] = "O programu Ultra Record",
            ["Tracks"] = "Trake",
            ["Track name"] = "Naziv trake",
            ["Input device"] = "Ulazni uređaj",
            ["Record this track"] = "Snimaj ovu traku",
            ["Track status"] = "Stanje trake",
            ["Start Recording"] = "Počni snimanje",
            ["Start recording all selected tracks"] = "Počni snimanje svih označenih traka",
            ["Stop Recording"] = "Zaustavi snimanje",
            ["Stop recording"] = "Zaustavi snimanje",
            ["No output folder chosen"] = "Izlazni folder nije izabran",
            ["Output: {0}"] = "Izlaz: {0}",
            ["Choose an output folder before starting."] = "Pre početka izaberi izlazni folder.",
            ["Ready. Add at least one track and choose an output folder to begin."] = "Spremno. Dodaj bar jednu traku i izaberi izlazni folder da počneš.",
            ["Track {0}"] = "Traka {0}",
            ["Recording all selected tracks..."] = "Snimam sve označene trake...",
            ["Stopped. Files saved to: {0}"] = "Zaustavljeno. Fajlovi su sačuvani u: {0}",
            ["Ready"] = "Spremna",
            ["Recording"] = "Snima",
            ["Stopped"] = "Zaustavljena",
            ["Error: {0}"] = "Greška: {0}",
            ["Clipping!"] = "Izobličenje!",
            ["Accessible multi-track audio recording - one input device per track, recorded simultaneously. Clipping is signaled by sound, not a visual meter, so nothing depends on seeing a level indicator. Part of the Ultra suite of accessible software."] = "Pristupačno snimanje zvuka na više traka: jedan ulazni uređaj po traci, sve se snima istovremeno. Izobličenje (clipping) se javlja zvukom, a ne samo pokazivačem nivoa, pa ništa ne zavisi od gledanja. Deo Ultra porodice pristupačnih programa.",
        };
    }
}
