using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraStudio.Localization;

namespace UltraStudio.Views
{
    /// <summary>Zajednički izgled i pomoćne metode za dijaloge (tamna tema, imena za čitač ekrana).</summary>
    internal static class Ui
    {
        public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        public static Style GetStyle(string key) => (Style)Application.Current.Resources[key];

        public static void Prepare(Window w, string title, double width, double height)
        {
            w.Title = title; w.Width = width; w.Height = height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ShowInTaskbar = false;
            w.Background = Res("BrBgDark");
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } };
        }

        public static TextBlock Label(string text, double top = 0) => new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Res("BrTextSec"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 4)
        };

        public static Button Button(string text, Action click, bool primary = false, bool isCancel = false, bool isDefault = false)
        {
            var b = new Button
            {
                Content = text, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0),
                Style = GetStyle(primary ? "AIButton" : "StdButton"), IsCancel = isCancel, IsDefault = isDefault
            };
            b.SetValue(AutomationProperties.NameProperty, text);
            b.Click += (_, __) => click();
            return b;
        }

        public static TextBox TextBox(string name, string value, bool multiline = false)
        {
            var t = new TextBox
            {
                Text = value, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
                Background = Res("BrBgMid"), Foreground = Res("BrText"), CaretBrush = Res("BrText")
            };
            if (multiline)
            {
                t.AcceptsReturn = true; t.TextWrapping = TextWrapping.Wrap; t.MinHeight = 80;
                t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else { t.Height = 30; t.VerticalContentAlignment = VerticalAlignment.Center; }
            t.SetValue(AutomationProperties.NameProperty, name);
            return t;
        }

        public static ComboBox Combo(string name, IEnumerable<string> items, int selected)
        {
            var c = new ComboBox { FontSize = 13, Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var i in items) c.Items.Add(i);
            c.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, c.Items.Count - 1));
            c.SetValue(AutomationProperties.NameProperty, name);
            return c;
        }

        public static CheckBox Check(string text, bool value) => new CheckBox
        {
            Content = text, IsChecked = value, Foreground = Res("BrText"), FontSize = 13, Margin = new Thickness(0, 2, 0, 10)
        };

        public static StackPanel Row(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var i in items) p.Children.Add(i);
            return p;
        }

        public static ListBox List(string name)
        {
            var l = new ListBox { FontSize = 13, Background = Res("BrBgMid"), Foreground = Res("BrText"), MinHeight = 160 };
            l.SetValue(AutomationProperties.NameProperty, name);
            return l;
        }

        public static void Announce(UIElement el, string text)
        {
            el.SetValue(AutomationProperties.NameProperty, text);
            var peer = UIElementAutomationPeer.FromElement(el) ?? UIElementAutomationPeer.CreatePeerForElement(el);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        public static bool TryNum(string s, out double v) =>
            double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    // ════════════════════════════════════════════════════════════════
    // FORMA — jedan dijalog za sve "upiši brojeve / izaberi opciju" slučajeve
    // (selekcija brojevima, isecanje, veličina, izvoz, gradijent...). Svako
    // polje ima ime za čitač ekrana; Tab ide redom, Enter potvrđuje.
    // ════════════════════════════════════════════════════════════════
    public sealed class FormField
    {
        public enum FieldKind { Text, Number, Choice, Check, MultilineText }
        public string Key = "";
        public string Label = "";
        public FieldKind Kind = FieldKind.Text;
        public string Value = "";
        public string[] Options = Array.Empty<string>();
        public int SelectedIndex;
        public bool Checked;
        public double Min = double.MinValue, Max = double.MaxValue;

        public static FormField Text(string key, string label, string value) => new() { Key = key, Label = label, Value = value };
        public static FormField Multi(string key, string label, string value) => new() { Key = key, Label = label, Value = value, Kind = FieldKind.MultilineText };
        public static FormField Num(string key, string label, double value, double min = double.MinValue, double max = double.MaxValue) =>
            new() { Key = key, Label = label, Value = value.ToString("0.##", CultureInfo.InvariantCulture), Kind = FieldKind.Number, Min = min, Max = max };
        public static FormField Choice(string key, string label, string[] options, int selected) =>
            new() { Key = key, Label = label, Options = options, SelectedIndex = selected, Kind = FieldKind.Choice };
        public static FormField Bool(string key, string label, bool value) => new() { Key = key, Label = label, Checked = value, Kind = FieldKind.Check };
    }

    public class FormDialog : Window
    {
        private readonly List<(FormField f, Control c)> _controls = new();
        private readonly TextBlock _error;
        public Dictionary<string, string> Text { get; } = new();
        public Dictionary<string, double> Numbers { get; } = new();
        public Dictionary<string, int> Choices { get; } = new();
        public Dictionary<string, bool> Checks { get; } = new();

        /// <summary>Poziva se pri svakoj izmeni (npr. da "zadrži razmeru" preračuna visinu).</summary>
        public Action<FormDialog>? OnChanged;

        public FormDialog(Window? owner, string title, string? hint, IEnumerable<FormField> fields, double width = 460)
        {
            Owner = owner;
            var list = fields.ToList();
            Ui.Prepare(this, title, width, Math.Min(760, 170 + list.Count * 64 + (hint != null ? 40 : 0)));
            var sp = new StackPanel { Margin = new Thickness(16) };
            if (!string.IsNullOrEmpty(hint)) sp.Children.Add(Ui.Label(hint));

            foreach (var f in list)
            {
                Control c;
                switch (f.Kind)
                {
                    case FormField.FieldKind.Check:
                        var cb = Ui.Check(f.Label, f.Checked);
                        cb.Checked += (_, __) => OnChanged?.Invoke(this);
                        cb.Unchecked += (_, __) => OnChanged?.Invoke(this);
                        c = cb;
                        break;
                    case FormField.FieldKind.Choice:
                        sp.Children.Add(Ui.Label(f.Label, 2));
                        var combo = Ui.Combo(f.Label, f.Options, f.SelectedIndex);
                        combo.SelectionChanged += (_, __) => OnChanged?.Invoke(this);
                        c = combo;
                        break;
                    default:
                        sp.Children.Add(Ui.Label(f.Label, 2));
                        var tb = Ui.TextBox(f.Label, f.Value, f.Kind == FormField.FieldKind.MultilineText);
                        tb.LostFocus += (_, __) => OnChanged?.Invoke(this);
                        c = tb;
                        break;
                }
                _controls.Add((f, c));
                sp.Children.Add(c);
            }

            _error = new TextBlock { Foreground = Ui.Res("BrDanger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            _error.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Assertive);
            sp.Children.Add(_error);
            sp.Children.Add(Ui.Row(
                Ui.Button(Lang.T("btn_ok"), Ok, primary: true, isDefault: true),
                Ui.Button(Lang.T("btn_cancel"), () => DialogResult = false, isCancel: true)));

            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += (_, __) =>
            {
                var first = _controls.FirstOrDefault().c;
                first?.Focus();
                if (first is TextBox t) t.SelectAll();
            };
        }

        public Control? ControlFor(string key) => _controls.FirstOrDefault(x => x.f.Key == key).c;
        public string GetText(string key) => ControlFor(key) is TextBox t ? t.Text : "";
        public void SetText(string key, string v) { if (ControlFor(key) is TextBox t) t.Text = v; }
        public int GetChoice(string key) => ControlFor(key) is ComboBox c ? c.SelectedIndex : -1;
        public bool GetCheck(string key) => ControlFor(key) is CheckBox c && c.IsChecked == true;

        private void Ok()
        {
            foreach (var (f, c) in _controls)
            {
                switch (c)
                {
                    case CheckBox cb: Checks[f.Key] = cb.IsChecked == true; break;
                    case ComboBox co: Choices[f.Key] = co.SelectedIndex; break;
                    case TextBox tb when f.Kind == FormField.FieldKind.Number:
                        if (!Ui.TryNum(tb.Text, out double v) || v < f.Min || v > f.Max)
                        {
                            string range = f.Min > double.MinValue && f.Max < double.MaxValue
                                ? string.Format(Lang.T("form_range"), f.Min.ToString("0.##", CultureInfo.InvariantCulture), f.Max.ToString("0.##", CultureInfo.InvariantCulture))
                                : "";
                            _error.Text = string.Format(Lang.T("form_bad_number"), f.Label) + range;
                            Ui.Announce(_error, _error.Text);
                            tb.Focus(); tb.SelectAll();
                            return;
                        }
                        Numbers[f.Key] = v;
                        Text[f.Key] = tb.Text;
                        break;
                    case TextBox tb: Text[f.Key] = tb.Text; break;
                }
            }
            DialogResult = true;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // IZBOR IZ LISTE — strelice biraju, Enter potvrđuje. Opciono: opis
    // stavke se izgovara pri svakom pomeranju, a onSelect dozvoljava živi
    // pregled (npr. look presetovi se vide na slici dok se biraju).
    // ════════════════════════════════════════════════════════════════
    public class ChoiceDialog : Window
    {
        public int SelectedIndex { get; private set; } = -1;
        private readonly ListBox _list;
        private readonly TextBlock _desc;

        public ChoiceDialog(Window? owner, string title, string prompt, IList<string> options, int def,
            Func<int, string>? description = null, Action<int>? onSelect = null, (string text, Action<int> action)? extraButton = null)
        {
            Owner = owner;
            Ui.Prepare(this, title, 560, Math.Min(620, 230 + options.Count * 24));
            var root = new DockPanel { Margin = new Thickness(16) };
            var lbl = Ui.Label(prompt); DockPanel.SetDock(lbl, Dock.Top); root.Children.Add(lbl);
            _list = Ui.List(prompt);
            foreach (var o in options) _list.Items.Add(o);

            _desc = new TextBlock { Foreground = Ui.Res("BrTextSec"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 34 };
            _desc.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Polite);

            void Ok() { SelectedIndex = _list.SelectedIndex; DialogResult = SelectedIndex >= 0; }
            _list.MouseDoubleClick += (_, __) => Ok();
            _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Ok(); e.Handled = true; } };
            _list.SelectionChanged += (_, __) =>
            {
                int i = _list.SelectedIndex;
                if (i < 0) return;
                if (description != null)
                {
                    _desc.Text = description(i);
                    // Opis kao HelpText stavke — JAWS ga čita uz naziv, bez dodatnog brbljanja
                    if (_list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem it)
                        it.SetValue(AutomationProperties.HelpTextProperty, _desc.Text);
                }
                onSelect?.Invoke(i);
            };

            var buttons = new List<UIElement>();
            if (extraButton != null)
            {
                var (text, action) = extraButton.Value;
                buttons.Add(Ui.Button(text, () => { if (_list.SelectedIndex >= 0) action(_list.SelectedIndex); }));
            }
            buttons.Add(Ui.Button(Lang.T("btn_ok"), Ok, primary: true));
            buttons.Add(Ui.Button(Lang.T("btn_cancel"), () => DialogResult = false, isCancel: true));
            var row = Ui.Row(buttons.ToArray());
            DockPanel.SetDock(row, Dock.Bottom); root.Children.Add(row);
            DockPanel.SetDock(_desc, Dock.Bottom); root.Children.Add(_desc);
            root.Children.Add(_list);
            Content = root;

            _list.SelectedIndex = options.Count == 0 ? -1 : Math.Clamp(def, 0, options.Count - 1);
            Loaded += (_, __) =>
            {
                _list.Focus();
                if (_list.SelectedIndex >= 0 && _list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) is ListBoxItem it) it.Focus();
            };
        }

        public void SetDescription(string text) { _desc.Text = text; Ui.Announce(_desc, text); }
    }

    // ════════════════════════════════════════════════════════════════
    // IZVEŠTAJ — read-only tekst (provera dizajna, AI opis, prečice).
    // Čitač ekrana ga čita red po red; Ctrl+A, Ctrl+C rade normalno.
    // ════════════════════════════════════════════════════════════════
    public class ReportWindow : Window
    {
        public ReportWindow(Window? owner, string title, string text)
        {
            Owner = owner;
            Ui.Prepare(this, title, 680, 560);
            ResizeMode = ResizeMode.CanResize;
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13, Padding = new Thickness(8),
                Background = Ui.Res("BrBgMid"), Foreground = Ui.Res("BrText"), BorderThickness = new Thickness(0)
            };
            box.SetValue(AutomationProperties.NameProperty, title);
            Grid.SetRow(box, 0); grid.Children.Add(box);
            var row = Ui.Row(
                Ui.Button(Lang.T("btn_copy"), () => { try { Clipboard.SetText(text); } catch { } }),
                Ui.Button(Lang.T("btn_close"), Close, primary: true, isCancel: true));
            Grid.SetRow(row, 1); grid.Children.Add(row);
            Content = grid;
            Loaded += (_, __) => { box.Focus(); box.CaretIndex = 0; };
        }
    }

    // ════════════════════════════════════════════════════════════════
    // ISTORIJA — svi koraci; Enter na stavku vraća projekat u to stanje.
    // ════════════════════════════════════════════════════════════════
    public class HistoryWindow : Window
    {
        /// <summary>Broj koraka unazad (pozitivno = Undo toliko puta, negativno = Redo).</summary>
        public int Steps { get; private set; }

        public HistoryWindow(Window owner, IReadOnlyList<string> undoLabels, IReadOnlyList<string> redoLabels)
        {
            Owner = owner;
            Ui.Prepare(this, Lang.T("history_title"), 520, 520);
            var root = new DockPanel { Margin = new Thickness(16) };
            var lbl = Ui.Label(Lang.T("history_hint")); DockPanel.SetDock(lbl, Dock.Top); root.Children.Add(lbl);
            var list = Ui.List(Lang.T("history_title"));

            // Redosled: najstariji gore, trenutno stanje označeno, pa koraci koji mogu da se ponove
            list.Items.Add(Lang.T("history_start"));
            for (int i = 0; i < undoLabels.Count; i++) list.Items.Add($"{i + 1}. {undoLabels[i]}");
            int current = undoLabels.Count;
            list.Items[current] = list.Items[current] + "  " + Lang.T("history_current");
            for (int i = redoLabels.Count - 1, n = 1; i >= 0; i--, n++)
                list.Items.Add($"{current + n}. {redoLabels[i]}  {Lang.T("history_undone")}");

            void Go()
            {
                if (list.SelectedIndex < 0) return;
                Steps = current - list.SelectedIndex;
                DialogResult = true;
            }
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(); e.Handled = true; } };
            list.MouseDoubleClick += (_, __) => Go();
            var row = Ui.Row(Ui.Button(Lang.T("history_go"), Go, primary: true), Ui.Button(Lang.T("btn_close"), Close, isCancel: true));
            DockPanel.SetDock(row, Dock.Bottom); root.Children.Add(row);
            root.Children.Add(list);
            Content = root;
            list.SelectedIndex = current;
            Loaded += (_, __) =>
            {
                list.Focus();
                list.ScrollIntoView(list.SelectedItem);
                if (list.ItemContainerGenerator.ContainerFromIndex(current) is ListBoxItem it) it.Focus();
            };
        }
    }
}
