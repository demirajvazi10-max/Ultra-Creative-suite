using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Navigation;

// Eksplicitni aliasi — rješava ambiguity između WPF i WinForms
using WpfApp        = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfTextBlock  = System.Windows.Controls.TextBlock;
using WpfColor      = System.Windows.Media.Color;
using WpfBrush      = System.Windows.Media.SolidColorBrush;

namespace UltraVideoEditor
{
    public partial class MediaProvidersDialog : Window
    {
        private string _lang =>
            (WpfApp.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private string L(string key) => LanguageManager.GetText(key, _lang);

        private readonly AIVideoCreator _creator;

        public MediaProvidersDialog(AIVideoCreator creator = null)
        {
            InitializeComponent();
            _creator = creator;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshAllStatuses();
            RefreshJsonProvidersList();
        }

        private void RefreshAllStatuses()
        {
            RefreshStatus("Pixabay", txtPixabayStatus, txtPixabayKey);
            RefreshStatus("Pexels",  txtPexelsStatus,  txtPexelsKey);
            RefreshStatus("Coverr",  txtCoverrStatus,  txtCoverrKey);
        }

        private static void RefreshStatus(string providerName,
            WpfTextBlock statusLabel, WpfTextBox keyBox)
        {
            string key        = MediaProviderSettings.LoadKey(providerName);
            bool   configured = !string.IsNullOrWhiteSpace(key);

            statusLabel.Text       = configured ? "● Aktivan" : "● Nije konfigurisan";
            statusLabel.Foreground = configured
                ? new WpfBrush(WpfColor.FromRgb(0x00, 0xE6, 0x76))
                : new WpfBrush(WpfColor.FromRgb(0xEF, 0x53, 0x50));

            if (configured && key.Length > 6)
                keyBox.Text = key.Substring(0, 6) + new string('*', Math.Min(key.Length - 6, 20));
            else if (configured)
                keyBox.Text = new string('*', key.Length);
            else
                keyBox.Text = string.Empty;
        }

        // ── PIXABAY ────────────────────────────────────────────────────────────

        private void BtnPixabaySave_Click(object sender, RoutedEventArgs e)
            => SaveKey("Pixabay", txtPixabayKey, txtPixabayStatus);

        private void BtnPixabayDelete_Click(object sender, RoutedEventArgs e)
            => DeleteKey("Pixabay", txtPixabayKey, txtPixabayStatus);

        // ── PEXELS ─────────────────────────────────────────────────────────────

        private void BtnPexelsSave_Click(object sender, RoutedEventArgs e)
            => SaveKey("Pexels", txtPexelsKey, txtPexelsStatus);

        private void BtnPexelsDelete_Click(object sender, RoutedEventArgs e)
            => DeleteKey("Pexels", txtPexelsKey, txtPexelsStatus);

        // ── COVERR ─────────────────────────────────────────────────────────────

        private void BtnCoverrSave_Click(object sender, RoutedEventArgs e)
            => SaveKey("Coverr", txtCoverrKey, txtCoverrStatus);

        private void BtnCoverrDelete_Click(object sender, RoutedEventArgs e)
            => DeleteKey("Coverr", txtCoverrKey, txtCoverrStatus);

        // ── Save / Delete logika ───────────────────────────────────────────────

        private void SaveKey(string providerName, WpfTextBox keyBox, WpfTextBlock statusLabel)
        {
            string input = keyBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                WpfMessageBox.Show("Unesite API kljuc.", "Upozorenje",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                keyBox.Focus();
                return;
            }

            // Maskirani prikaz — korisnik nije ništa mijenjao
            if (!input.Contains('*'))
            {
                bool ok = MediaProviderSettings.SaveKey(providerName, input);
                if (ok)
                {
                    _creator?.SaveMediaProviderKey(providerName, input);
                    RefreshStatus(providerName, statusLabel, keyBox);
                    txtInfo.Text = "OK [" + providerName + "] Kljuc sacuvan, provajder aktivan.";
                }
                else
                {
                    txtInfo.Text = "GRESKA [" + providerName + "] Greska pri cuvanju.";
                }
            }
            else
            {
                txtInfo.Text = "[" + providerName + "] Kljuc nije promijenjen.";
            }
        }

        private void DeleteKey(string providerName, WpfTextBox keyBox, WpfTextBlock statusLabel)
        {
            var result = WpfMessageBox.Show(
                "Obrisati API kljuc za " + providerName + "?\nProvajder ce biti deaktiviran.",
                "Potvrda brisanja",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            MediaProviderSettings.DeleteKey(providerName);
            _creator?.DeleteMediaProviderKey(providerName);
            RefreshStatus(providerName, statusLabel, keyBox);
            txtInfo.Text = "[" + providerName + "] Kljuc obrisan, provajder deaktiviran.";
        }

        // ── Hyperlink ──────────────────────────────────────────────────────────

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
        }

        // ── JSON provajderi ────────────────────────────────────────────────────

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = JsonProviderLoader.GetProvidersFolder();
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show("Greska pri otvaranju foldera: " + ex.Message,
                    "Greska", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            MediaProviderRegistry.Instance.ReloadJsonProviders();
            RefreshJsonProvidersList();
            txtInfo.Text = "Provajderi osvjezeni.";
        }

        private void RefreshJsonProvidersList()
        {
            var jsonProviders = MediaProviderRegistry.Instance.All
                .OfType<JsonMediaProvider>().ToList();

            txtJsonProviders.Text = jsonProviders.Count == 0
                ? "Nema ucitanih eksternih provajdera."
                : "Ucitani: " + string.Join(", ", jsonProviders.Select(p =>
                    p.Name + (p.IsConfigured ? " (aktivan)" : " (bez kljuca)")));
        }

        // ── Zatvori ────────────────────────────────────────────────────────────

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
