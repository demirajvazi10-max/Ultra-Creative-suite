using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Documents;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4.5 — translates the texts written directly in the XAML windows (labels, buttons,
    /// headers, tooltips, window titles and the screen-reader names) into the interface
    /// language. Every window runs through it when it opens (hooked in App), and the main
    /// window runs all open windows through it again when the language changes.
    ///
    /// Each element remembers which row it was translated from, so switching languages back
    /// and forth always starts from the right row, even where two rows share one word.
    /// Combo box and list box items are left alone: the code reads their text as values.
    /// Bound texts are left alone too, so a binding is never broken.
    /// </summary>
    internal static class XamlTranslator
    {
        private sealed class Memo { public int Row; public string Written; public bool Plain; }

        private static Dictionary<string, int> _index, _plainIndex;
        private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Memo>> _memo = new();

        private static Dictionary<string, int> Index
        {
            get
            {
                if (_index != null) return _index;
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                var rows = UiTranslations.Rows;
                // English first, then Serbian, then German: the first row that claims a text keeps it
                for (int col = 0; col < 3; col++)
                    for (int r = 0; r < rows.Length; r++)
                    {
                        string k = rows[r][col]?.Trim();
                        if (!string.IsNullOrEmpty(k) && !d.ContainsKey(k)) d[k] = r;
                    }
                // the same texts without emoji: the screen-reader names made by UiScaling.FixEmojiNames
                var p = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int col = 0; col < 3; col++)
                    for (int r = 0; r < rows.Length; r++)
                    {
                        string k = UiScaling.StripSymbols(rows[r][col] ?? "");
                        if (k.Length > 0 && !p.ContainsKey(k)) p[k] = r;
                    }
                _plainIndex = p;
                return _index = d;
            }
        }

        private static int Column(string lang) => lang switch { "sr" => 1, "de" => 2, _ => 0 };

        /// <summary>Translate the window and every element in it.</summary>
        public static void Translate(DependencyObject root, string lang)
        {
            if (root == null) return;
            try { Walk(root, Column(lang ?? "en"), 0); }
            catch (Exception ex) { AppLog.Error("Translating the window texts", ex); }
        }

        /// <summary>Translate every open window (after a language change).</summary>
        public static void TranslateAllWindows(string lang)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            foreach (Window w in app.Windows) Translate(w, lang);
        }

        private static void Walk(DependencyObject obj, int col, int depth)
        {
            if (obj == null || depth > 200) return;

            switch (obj)
            {
                case Window w:
                    Apply(w, Window.TitleProperty, col);
                    break;
                case WControls.TextBlock tb:
                    // a text block made of several parts (bold words, links) is translated part by part
                    if (tb.Inlines.Count <= 1) Apply(tb, WControls.TextBlock.TextProperty, col);
                    break;
                case Run run:
                    if (run.Parent is WControls.TextBlock owner && owner.Inlines.Count <= 1) break;
                    Apply(run, Run.TextProperty, col);
                    break;
            }

            if (obj is WControls.HeaderedContentControl || obj is WControls.HeaderedItemsControl)
                Apply(obj, obj is WControls.HeaderedContentControl ? WControls.HeaderedContentControl.HeaderProperty
                                                                   : WControls.HeaderedItemsControl.HeaderProperty, col);

            if (obj is WControls.ContentControl && obj is not WControls.ComboBoxItem && obj is not WControls.ListBoxItem
                && obj is not Window)
                Apply(obj, WControls.ContentControl.ContentProperty, col);

            if (obj is FrameworkElement fe)
            {
                Apply(fe, FrameworkElement.ToolTipProperty, col);
                if (fe.ToolTip is DependencyObject tip) Walk(tip, col, depth + 1);
                if (fe.ContextMenu != null) Walk(fe.ContextMenu, col, depth + 1);
            }

            if (obj is UIElement || obj is FrameworkContentElement)
            {
                Apply(obj, AutomationProperties.NameProperty, col);
                Apply(obj, AutomationProperties.HelpTextProperty, col);
            }

            if (obj is WControls.ListView lv && lv.View is WControls.GridView gv)
                foreach (var c in gv.Columns) Apply(c, WControls.GridViewColumn.HeaderProperty, col);

            foreach (object child in LogicalTreeHelper.GetChildren(obj))
                if (child is DependencyObject d) Walk(d, col, depth + 1);
        }

        private static void Apply(DependencyObject obj, DependencyProperty prop, int col)
        {
            if (obj.GetValue(prop) is not string text || text.Trim().Length == 0) return;
            if (BindingOperations.IsDataBound(obj, prop)) return;

            var memos = _memo.GetOrCreateValue(obj);
            int row;
            bool plain = false;
            if (memos.TryGetValue(prop, out var m) && m.Written == text)
            {
                row = m.Row;                          // written by us last time: keep its row
                plain = m.Plain;
            }
            else if (!Index.TryGetValue(text.Trim(), out row))
            {
                // a name without its emoji ("CLEAR ALL SUBTITLES" for "🗑 CLEAR ALL SUBTITLES")
                if (prop == AutomationProperties.NameProperty && _plainIndex.TryGetValue(text.Trim(), out row))
                    plain = true;
                else
                {
                    memos.Remove(prop);               // a text we do not know (a name, a number…)
                    return;
                }
            }

            string target = UiTranslations.Rows[row][col];
            if (plain) target = UiScaling.StripSymbols(target ?? "");
            if (string.IsNullOrEmpty(target)) return;
            // keep the spaces around the text (some labels are glued to the next one)
            string trimmed = text.Trim();
            int lead = text.IndexOf(trimmed, StringComparison.Ordinal);
            string result = text.Substring(0, Math.Max(0, lead)) + target.Trim() + text.Substring(Math.Max(0, lead) + trimmed.Length);

            if (result != text) obj.SetCurrentValue(prop, result);
            memos[prop] = new Memo { Row = row, Written = result, Plain = plain };
        }
    }
}
