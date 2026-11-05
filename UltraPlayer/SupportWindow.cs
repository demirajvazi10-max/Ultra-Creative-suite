// Ultra — "How to support this project" window.
// The same file is used in every Ultra app, so the guide reads the same everywhere.
// Self-contained: it has its own English and Serbian texts and takes its colours and
// text size from the window that opens it, so it follows each app's theme.
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UltraSupport
{
    // Aliases keep this file compiling in projects that also use WinForms or System.Drawing.
    using Brush = System.Windows.Media.Brush;
    using Brushes = System.Windows.Media.Brushes;
    using Button = System.Windows.Controls.Button;
    using Clipboard = System.Windows.Clipboard;
    using Color = System.Windows.Media.Color;
    using Colors = System.Windows.Media.Colors;
    using HorizontalAlignment = System.Windows.HorizontalAlignment;
    using SystemColors = System.Windows.SystemColors;
    using TextBox = System.Windows.Controls.TextBox;

    public static class SupportInfo
    {
        public const string SponsorsUrl = "https://github.com/sponsors/demirajvazi10-max";
        public const string KofiUrl = "https://ko-fi.com/ultracreativesuite";
        public const string BankAccount = "265-0000006185877-28";
        public const string BankAccountDigits = "265000000618587728";   // digits only: the safest format to paste into a banking app
        public const string BankRecipient = "Demir Ajvazi";
        public const string BankName = "Raiffeisen banka";

        /// <summary>Opens the guide. <paramref name="serbian"/> picks the language of the texts.</summary>
        public static void Show(Window? owner, string appName, bool serbian)
            => new SupportWindow(owner, appName, serbian).ShowDialog();

        public static string MenuText(bool serbian) => serbian ? "Kako podržati p_rojekat" : "How to support this _project";
        public static string ButtonText(bool serbian) => serbian ? "Kako podržati" : "How to support";
        public static string AboutLine(bool serbian) => serbian
            ? "Projekat možeš da podržiš uplatom na tekući račun (iz Srbije) ili platnom karticom, PayPal ne treba — pogledaj \"Kako podržati projekat\"."
            : "You can support the project with an ordinary payment card, no PayPal account needed — see \"How to support this project\".";

        public static string GuideText(string app, bool sr) => sr
            ? $"{app} je besplatan i takav će i ostati. Ako ti pomaže i želiš da podržiš razvoj, evo tri načina. Podrška ide celoj Ultra porodici programa, ne samo ovom.\n\n" +
              "1. IZ SRBIJE — UPLATA NA TEKUĆI RAČUN (najlakše)\n" +
              $"Primalac: {BankRecipient}\nRačun: {BankAccount}\nBanka: {BankName}\nSvrha uplate: Donacija za Ultra programe\n" +
              "Uplati iz mobilnog ili elektronskog bankarstva, kao bilo koji račun. Dugme \"Kopiraj broj računa\" ispod stavlja broj u međuspremnik, pa ga samo nalepiš. Ne treba nikakav nalog ni kartica za internet.\n\n" +
              "2. KARTICOM PREKO KO-FI (bez naloga)\n" +
              $"{KofiUrl}\n" +
              "Pritisni \"Otvori Ko-fi\", upiši iznos, pritisni \"Donate\" i plati karticom (Visa ili Mastercard, kreditna ili debitna) ili PayPal-om. Nalog ti ne treba. Ovo je najlakši način za ljude iz inostranstva.\n\n" +
              "3. GITHUB SPONSORS (mesečno ili jednokratno)\n" +
              $"{SponsorsUrl}\n" +
              "Za one koji imaju GitHub nalog (ili žele da ga naprave — besplatno je). Plaća se običnom platnom karticom, PayPal ne treba. Pritisni \"Otvori GitHub Sponsors\", prijavi se, izaberi mesečni iznos ili jednokratnu uplatu, upiši podatke sa kartice i potvrdi (banka će možda tražiti potvrdu u aplikaciji ili SMS kodom). Mesečna podrška se otkazuje kad god hoćeš u GitHub podešavanjima (Settings > Billing and licensing).\n\n" +
              "Možeš da pomogneš i besplatno: preporuči program drugima, prijavi problem ili daj projektu zvezdicu na GitHub-u.\n\nHvala!"
            : $"{app} is free and will stay free. If it helps you and you would like to support its development, here are three ways. Your support goes to the whole Ultra family of apps, not just this one.\n\n" +
              "1. BY CARD ON KO-FI (no account needed — easiest)\n" +
              $"{KofiUrl}\n" +
              "Press \"Open Ko-fi\", type an amount, press \"Donate\" and pay with your card (Visa or Mastercard, credit or debit) or PayPal. You don't need an account.\n\n" +
              "2. GITHUB SPONSORS (monthly or one-time)\n" +
              $"{SponsorsUrl}\n" +
              "For people who have a GitHub account (or want one — it's free). Paid with an ordinary payment card, no PayPal needed. Press \"Open GitHub Sponsors\", sign in, choose a monthly amount or a one-time payment, enter your card details and confirm (your bank may ask you to confirm in its app or with an SMS code). Monthly support can be cancelled at any time in your GitHub settings (Settings > Billing and licensing).\n\n" +
              "3. BANK TRANSFER FROM SERBIA\n" +
              $"Recipient: {BankRecipient}\nAccount: {BankAccount}\nBank: {BankName}\nPurpose: Donation for Ultra apps\n" +
              "Meant for domestic transfers; international (SWIFT) transfers usually cost more in fees than a small donation is worth, so from abroad Ko-fi is the better choice.\n\n" +
              "You can also help for free: tell others about the app, report problems, or give the project a star on GitHub.\n\nThank you!";

        public static void OpenSponsors() => OpenUrl(SponsorsUrl);
        public static void OpenKofi() => OpenUrl(KofiUrl);

        public static void OpenUrl(string url)
        {
            // explorer.exe hands the link to the default browser at normal user level, which also
            // works when the app runs as administrator (a direct ShellExecute can then fail silently).
            try { Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }
        }
    }

    public sealed class SupportWindow : Window
    {
        private readonly TextBlock _status;

        public SupportWindow(Window? owner, string appName, bool sr)
        {
            if (owner != null && owner.IsLoaded) Owner = owner;
            Title = sr ? $"Kako podržati {appName}" : $"How to support {appName}";
            Width = 680; Height = 600; MinWidth = 420; MinHeight = 360;
            WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            // Colours follow the app's current theme (dark, light or high contrast).
            Brush bg = owner?.Background as SolidColorBrush is { } b && b.Color.A > 0 ? b : SystemColors.WindowBrush;
            Brush fg = ReadableForeground(bg, owner?.Foreground);
            Background = bg; Foreground = fg;
            if (owner != null) { FontSize = Math.Max(owner.FontSize, 13); FontFamily = owner.FontFamily; }

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Read-only text box: a screen reader can read it line by line and with arrow keys.
            var text = new TextBox
            {
                Text = SupportInfo.GuideText(appName, sr),
                IsReadOnly = true, IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = bg, Foreground = fg, CaretBrush = fg,
                BorderBrush = fg, BorderThickness = new Thickness(1),
                Padding = new Thickness(8), FontSize = FontSize
            };
            AutomationProperties.SetName(text, Title);
            grid.Children.Add(text);

            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);

            _status = new TextBlock { Foreground = fg, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Assertive);
            Grid.SetRow(_status, 2);
            grid.Children.Add(_status);

            Button Add(string label, Action click, bool isCancel = false)
            {
                var btn = new Button
                {
                    Content = label, MinWidth = 110, Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(8, 4, 0, 0), IsCancel = isCancel
                };
                AutomationProperties.SetName(btn, label.Replace("_", ""));
                btn.Click += (_, __) => click();
                buttons.Children.Add(btn);
                return btn;
            }
            void Copy(string value, string done)
            {
                try { Clipboard.SetText(value); Announce(done); }
                catch { Announce(sr ? "Nije moglo da se kopira." : "Could not copy."); }
            }
            if (sr)
            {
                Add("Kopiraj _broj računa", () => Copy(SupportInfo.BankAccountDigits, "Broj računa je kopiran: " + SupportInfo.BankAccount));
                Add("Otvori _Ko-fi", SupportInfo.OpenKofi);
                Add("Otvori _GitHub Sponsors", SupportInfo.OpenSponsors);
            }
            else
            {
                Add("Open _Ko-fi", SupportInfo.OpenKofi);
                Add("Open _GitHub Sponsors", SupportInfo.OpenSponsors);
                Add("Copy bank _account number", () => Copy(SupportInfo.BankAccountDigits, "Account number copied: " + SupportInfo.BankAccount));
            }
            Add(sr ? "_Zatvori" : "C_lose", Close, isCancel: true);

            Content = grid;
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
            Loaded += (_, __) => { text.Focus(); text.CaretIndex = 0; };
        }

        private void Announce(string message)
        {
            _status.Text = message;
            var peer = UIElementAutomationPeer.FromElement(_status) ?? UIElementAutomationPeer.CreatePeerForElement(_status);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        // Use the app's text colour if it contrasts with the background; otherwise black or white.
        private static Brush ReadableForeground(Brush bg, Brush? preferred)
        {
            var back = (bg as SolidColorBrush)?.Color ?? Colors.White;
            double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            if (preferred is SolidColorBrush p && Math.Abs(Lum(p.Color) - Lum(back)) > 0.45) return p;
            return Lum(back) > 0.5 ? Brushes.Black : Brushes.White;
        }
    }
}
