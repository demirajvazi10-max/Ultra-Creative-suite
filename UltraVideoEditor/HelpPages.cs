using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — help as web pages, which every screen reader reads completely: headings, lists
    /// and tables, in the browser the user knows. The old help window only showed text blocks
    /// that JAWS and NVDA could not reach (only its Close button was read).
    ///
    ///  • Keyboard shortcuts (F1): built from the program's own menus, in the language in use,
    ///    so it always matches what the menus say, plus the keys that are not in any menu.
    ///  • Manual: the guide to the program in the program's language (Help/manual.en.html,
    ///    manual.sr.html, manual.de.html, built into the program; a new language adds its file).
    ///
    /// Both pages are written to %LOCALAPPDATA%\UltraVideoEditor\Help and opened in the
    /// default browser.
    /// </summary>
    public static class HelpPages
    {
        private static string T(string key) => LanguageManager.GetText(key, LanguageManager.CurrentLanguage ?? "en");
        private static string H(string s) => WebUtility.HtmlEncode(s ?? "");

        private static string Dir
        {
            get
            {
                string d = Path.Combine(AppPaths.DataDir, "Help");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>A menu header as plain text: no access-key underscores, no emoji or symbols in front.</summary>
        public static string Clean(string header)
        {
            if (string.IsNullOrEmpty(header)) return "";
            string s = header.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_").Trim();
            int i = 0;
            while (i < s.Length && !char.IsLetterOrDigit(s[i])) i++;
            // "Open..." → "Open": screen readers would read the dots
            return s.Substring(i).Trim().TrimEnd('.', '…').Trim();
        }

        /// <summary>
        /// The accessible name of every menu command gets its shortcut, as in other Windows
        /// programs ("Open Ctrl+O"), so JAWS and NVDA read it with the command.
        /// </summary>
        public static void AddShortcutsToMenuNames(IEnumerable<MenuItem> items)
        {
            foreach (var mi in items)
            {
                var memo = Memos.Table.GetOrCreateValue(mi);
                // the shortcut as written in the XAML, shown with key names in the program's language
                memo.Gesture ??= mi.InputGestureText ?? "";
                string g = KeyNames(memo.Gesture);
                if (mi.InputGestureText != g) mi.InputGestureText = g;

                string raw = AutomationProperties.GetName(mi);
                if (string.IsNullOrWhiteSpace(raw)) raw = mi.Header as string;
                string name = Clean(raw);
                if (!string.IsNullOrWhiteSpace(g)) name = name + " " + g;
                if (string.IsNullOrWhiteSpace(name)) continue;
                memo.Base = AutomationProperties.GetName(mi);
                memo.Written = name;
                AutomationProperties.SetName(mi, name);
            }
        }

        /// <summary>
        /// Before the interface is translated again: the menu names get back the text they had
        /// before the shortcut was added, so the translation finds them.
        /// </summary>
        public static void RestoreMenuNames(IEnumerable<MenuItem> items)
        {
            foreach (var mi in items)
                if (Memos.Table.TryGetValue(mi, out var memo) && memo.Written != null && AutomationProperties.GetName(mi) == memo.Written)
                    AutomationProperties.SetName(mi, memo.Base ?? "");
        }

        private sealed class MenuMemo { public string Gesture, Base, Written; }
        // in its own class, so the key names and the page can be made without loading WPF (tests)
        private static class Memos { public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuItem, MenuMemo> Table = new(); }

        private static readonly System.Text.RegularExpressions.Regex KeyWords = new(
            @"\b(Ctrl|Shift|Alt|Del|Delete|Left|Right|Up|Down|PgUp|PgDn|Page Up|Page Down|Space|Plus|Minus|Comma|Period|Home|End|Escape|Enter|Applications|Tab)\b(?!\w)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Key names in the program's language ("Ctrl+Left" → "Strg+Pfeil links" in German).
        /// Each name is a language key "kn_…", so a new language only needs those keys.
        /// </summary>
        public static string KeyNames(string keys)
        {
            if (string.IsNullOrEmpty(keys)) return keys ?? "";
            return KeyWords.Replace(keys, m =>
            {
                string k = "kn_" + m.Value.Replace(" ", "");
                string t = T(k);
                return string.IsNullOrEmpty(t) || t == k ? m.Value : t;
            });
        }

        /// <summary>F1: opens the list of keyboard shortcuts, made now from the menus.</summary>
        public static bool OpenShortcuts(Menu menu)
        {
            WriteManual();   // the shortcuts page links to the manual
            string file = Path.Combine(Dir, "shortcuts.html");
            File.WriteAllText(file, BuildShortcuts(menu), new UTF8Encoding(false));
            return Open(file);
        }

        /// <summary>Help → Manual.</summary>
        public static bool OpenManual(Menu menu)
        {
            string file = WriteManual();
            // the manual links to the shortcuts page, so it is written too
            try { File.WriteAllText(Path.Combine(Dir, "shortcuts.html"), BuildShortcuts(menu), new UTF8Encoding(false)); } catch { }
            return file != null && Open(file);
        }

        private static string WriteManual()
        {
            string file = Path.Combine(Dir, "manual.html");
            try
            {
                // the manual in the program's language (Help/manual.xx.html); English when there is none yet
                string lang = LanguageManager.CurrentLanguage ?? "en";
                var asm = typeof(HelpPages).Assembly;
                using var s = asm.GetManifestResourceStream($"UltraVideoEditor.Help.manual.{lang}.html")
                              ?? asm.GetManifestResourceStream("UltraVideoEditor.Help.manual.en.html");
                if (s == null) return File.Exists(file) ? file : null;
                using var r = new StreamReader(s, Encoding.UTF8);
                File.WriteAllText(file, r.ReadToEnd(), new UTF8Encoding(false));
                return file;
            }
            catch (Exception ex) { AppLog.Error("Writing the manual", ex); return File.Exists(file) ? file : null; }
        }

        private static bool Open(string file)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
                return true;
            }
            catch (Exception ex) { AppLog.Error("Opening help", ex); return false; }
        }

        /// <summary>Keys that are not on any menu: (keys, description key), in groups.</summary>
        private static readonly (string Group, (string Keys, string Text)[] Keys)[] Extra =
        {
            ("help_grp_playback", new[]
            {
                ("Ctrl+Space", "hk_play_clip"),
                ("Alt+Left, Alt+Right", "hk_seek5"),
                ("Alt+Shift+Left, Alt+Shift+Right", "hk_seek01"),
                ("Comma, Period", "hk_frame"),
                ("L", "hk_l"),
                ("J", "hk_j"),
                ("K", "hk_k"),
                ("Space", "hk_space_live"),
                ("1 … 9", "hk_multicam"),
                ("Q, W", "hk_qw"),
            }),
            ("help_grp_list", new[]
            {
                ("Up, Down", "hk_updown"),
                ("Page Up, Page Down", "hk_page"),
                ("Home, End", "hk_homeend"),
                ("Shift+Up, Shift+Down", "hk_select_more"),
                ("Ctrl+L", "hk_ctrl_l"),
                ("Ctrl+G", "hk_ctrl_g"),
                ("Applications, Shift+F10", "hk_context"),
            }),
            ("help_grp_other", new[]
            {
                ("Ctrl+R", "hk_ctrl_r"),
                ("Ctrl+Tab, Ctrl+Shift+Tab", "hk_tabs"),
                ("Enter, Space", "hk_transition_apply"),
                ("F1", "hk_transition_f1"),
                ("Escape", "hk_escape"),
            }),
        };

        /// <summary>The menus as (title, rows of command and shortcut), only commands that have a shortcut.</summary>
        public static List<(string Title, List<(string Cmd, string Keys)> Rows)> MenuRows(Menu menu)
        {
            var result = new List<(string, List<(string, string)>)>();
            if (menu == null) return result;
            foreach (var top in menu.Items.OfType<MenuItem>())
            {
                var rows = new List<(string, string)>();
                Collect(top, "", rows);
                if (rows.Count > 0) result.Add((Clean(top.Header as string), rows));
            }
            return result;
        }

        private static string BuildShortcuts(Menu menu) => BuildShortcutsHtml(MenuRows(menu));

        /// <summary>The shortcuts page: one heading and one table per menu, then the other keys.</summary>
        public static string BuildShortcutsHtml(List<(string Title, List<(string Cmd, string Keys)> Rows)> menus)
        {
            string lang = LanguageManager.CurrentLanguage ?? "en";
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(H(lang)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
              .Append("<title>").Append(H(T("help_sc_title"))).Append(" – Ultra Video Editor</title>\n")
              .Append(Style).Append("</head>\n<body>\n<header>\n<h1>").Append(H(T("help_sc_title"))).Append("</h1>\n")
              .Append("<p>").Append(H(T("help_sc_intro"))).Append("</p>\n")
              .Append("<p><a href=\"manual.html\">").Append(H(T("help_sc_manual_link"))).Append("</a></p>\n</header>\n<main>\n");

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (title, rows) in menus ?? new List<(string, List<(string, string)>)>())
            {
                sb.Append("<section>\n<h2>").Append(H(title)).Append("</h2>\n");
                Table(sb, title, rows);
                sb.Append("</section>\n");
                foreach (var r in rows) used.Add(r.Keys);
            }

            sb.Append("<section>\n<h2>").Append(H(T("help_sc_other"))).Append("</h2>\n");
            foreach (var (group, keys) in Extra)
            {
                var rows = keys.Select(k => (Cmd: T(k.Text), Keys: KeyNames(k.Keys))).Where(k => !used.Contains(k.Keys)).ToList();
                if (rows.Count == 0) continue;
                sb.Append("<h3>").Append(H(T(group))).Append("</h3>\n");
                Table(sb, T(group), rows);
            }
            sb.Append("</section>\n</main>\n<footer><p>Ultra Video Editor · Demir Ajvazi</p></footer>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static void Collect(MenuItem parent, string path, List<(string, string)> rows)
        {
            foreach (var mi in parent.Items.OfType<MenuItem>())
            {
                string name = Clean(mi.Header as string);
                if (string.IsNullOrEmpty(name)) name = Clean(AutomationProperties.GetName(mi));
                string full = path.Length > 0 ? path + ": " + name : name;
                if (!string.IsNullOrWhiteSpace(mi.InputGestureText)) rows.Add((full, mi.InputGestureText.Trim()));
                if (mi.Items.Count > 0) Collect(mi, full, rows);
            }
        }

        private static void Table(StringBuilder sb, string caption, List<(string Cmd, string Keys)> rows)
        {
            sb.Append("<table>\n<caption>").Append(H(caption)).Append("</caption>\n<thead><tr><th scope=\"col\">")
              .Append(H(T("help_sc_col_cmd"))).Append("</th><th scope=\"col\">").Append(H(T("help_sc_col_key"))).Append("</th></tr></thead>\n<tbody>\n");
            foreach (var (cmd, keys) in rows)
                sb.Append("<tr><th scope=\"row\">").Append(H(cmd)).Append("</th><td><kbd>").Append(H(keys)).Append("</kbd></td></tr>\n");
            sb.Append("</tbody>\n</table>\n");
        }

        private const string Style =
            "<style>\n" +
            ":root { color-scheme: light dark; --bg: #fff; --fg: #1b1b1b; --accent: #0b5cad; --line: #d0d7de; --head: #f3f5f7; }\n" +
            "@media (prefers-color-scheme: dark) { :root { --bg: #16181b; --fg: #e8e8e8; --accent: #7cb7ff; --line: #3a3f45; --head: #23272c; } }\n" +
            "body { background: var(--bg); color: var(--fg); font: 17px/1.55 \"Segoe UI\", system-ui, sans-serif; max-width: 52rem; margin: 0 auto; padding: 1.5rem 1rem 4rem; }\n" +
            "h1 { font-size: 1.9rem; margin-top: 0; } h2 { font-size: 1.4rem; margin-top: 2.2rem; } h3 { font-size: 1.15rem; }\n" +
            "a { color: var(--accent); }\n" +
            "table { border-collapse: collapse; width: 100%; margin: .5rem 0 1rem; }\n" +
            "caption { text-align: left; font-weight: 600; padding: .3rem 0; position: absolute; left: -10000px; }\n" +
            "th, td { border: 1px solid var(--line); padding: .4rem .6rem; text-align: left; vertical-align: top; }\n" +
            "thead th { background: var(--head); } tbody th { font-weight: normal; }\n" +
            "td { white-space: nowrap; width: 1%; }\n" +
            "kbd { font-family: Consolas, monospace; }\n" +
            ":focus { outline: 3px solid var(--accent); outline-offset: 2px; }\n" +
            "</style>\n";
    }
}
