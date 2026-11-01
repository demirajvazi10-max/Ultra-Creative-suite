using System;
using System.Windows;
using System.Windows.Automation;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — small building blocks for the windows that are built in code (Export options,
    /// Layer and colour). Every control gets its label as its screen-reader name, and the
    /// label's access key (the letter after "_") moves the focus to it.
    /// </summary>
    internal static class UiKit
    {
        internal static string L(string key) => LanguageManager.GetText(key, LanguageManager.CurrentLanguage ?? "en");
        internal static string LF(string key, params object[] a) => string.Format(L(key), a);

        internal static WControls.Button Btn(string text)
        {
            var b = new WControls.Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 8, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            b.SetResourceReference(WControls.Control.BackgroundProperty, "PanelBg");
            b.SetResourceReference(WControls.Control.ForegroundProperty, "TextPrimary");
            AutomationProperties.SetName(b, text.Replace("_", ""));
            return b;
        }

        internal static WControls.StackPanel Labelled(string label, WControls.Control control)
        {
            var sp = new WControls.StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var lbl = new WControls.Label { Content = label, Target = control, Padding = new Thickness(0, 0, 0, 3) };
            lbl.SetResourceReference(WControls.Control.ForegroundProperty, "TextPrimary");
            AutomationProperties.SetName(control, label.Replace("_", ""));
            sp.Children.Add(lbl);
            sp.Children.Add(control);
            return sp;
        }

        internal static WControls.ComboBox Combo(params (string Text, object Tag)[] items)
        {
            var c = new WControls.ComboBox { MinWidth = 320, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            foreach (var (text, tag) in items)
                c.Items.Add(new WControls.ComboBoxItem { Content = text, Tag = tag });
            c.SelectedIndex = 0;
            return c;
        }

        internal static void Select(WControls.ComboBox c, object tag)
        {
            for (int i = 0; i < c.Items.Count; i++)
                if (c.Items[i] is WControls.ComboBoxItem it && Equals(it.Tag, tag)) { c.SelectedIndex = i; return; }
        }

        internal static object Tag(WControls.ComboBox c) => (c.SelectedItem as WControls.ComboBoxItem)?.Tag;

        internal static WControls.CheckBox Check(string text, bool value)
        {
            var c = new WControls.CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 10, 0, 0) };
            c.SetResourceReference(WControls.Control.ForegroundProperty, "TextPrimary");
            AutomationProperties.SetName(c, text.Replace("_", ""));
            return c;
        }

        internal static WControls.TextBlock Text(string text, double top = 0)
        {
            var t = new WControls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            t.SetResourceReference(WControls.TextBlock.ForegroundProperty, "TextPrimary");
            return t;
        }

        /// <summary>A slider with its value spoken as a plain number (percent etc.).</summary>
        internal static WControls.Slider Slider(double min, double max, double value, double step)
        {
            var s = new WControls.Slider
            {
                Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10,
                IsSnapToTickEnabled = true, TickFrequency = step, Width = 320, HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            return s;
        }

        /// <summary>Says a message through UI Automation (JAWS, NVDA and Narrator read it).</summary>
        internal static void Announce(WControls.TextBlock status, string text)
        {
            if (status == null) return;
            status.Text = text;
            try
            {
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(status);
                if (peer != null)
                {
                    peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
                    peer.RaiseNotificationEvent(System.Windows.Automation.AutomationNotificationKind.ActionCompleted,
                        System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, text, "UltraVideoEditor.Status");
                }
            }
            catch (Exception ex) { AppLog.Error("Announcement", ex); }
        }

        internal static WControls.TextBlock Status()
        {
            var t = Text("", 10);
            AutomationProperties.SetLiveSetting(t, AutomationLiveSetting.Polite);
            return t;
        }
    }
}
