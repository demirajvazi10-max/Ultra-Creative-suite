using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Navigation;

// Explicit aliases — resolves ambiguity between WPF and WinForms
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
            LanguageManager.CurrentLanguage ?? "en";
        private string L(string key) => LanguageManager.GetText(key, _lang);

        private readonly AIVideoCreator _creator;

        public MediaProvidersDialog(AIVideoCreator creator = null)
        {
            InitializeComponent();
            UiScaling.Register(this);
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

            statusLabel.Text       = LanguageManager.T(configured ? "lx_prov_active" : "lx_prov_not_set");
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

        // ── Save / Delete logic ───────────────────────────────────────────────

        private void SaveKey(string providerName, WpfTextBox keyBox, WpfTextBlock statusLabel)
        {
            string input = keyBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                WpfMessageBox.Show(LanguageManager.T("lx_enter_api_key"), LanguageManager.T("lx_warning"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                keyBox.Focus();
                return;
            }

            // Masked display — user has not changed anything
            if (!input.Contains('*'))
            {
                bool ok = MediaProviderSettings.SaveKey(providerName, input);
                if (ok)
                {
                    _creator?.SaveMediaProviderKey(providerName, input);
                    RefreshStatus(providerName, statusLabel, keyBox);
                    txtInfo.Text = LanguageManager.F("lx_ok_the_key_is_saved_6a51d", providerName);
                }
                else
                {
                    txtInfo.Text = LanguageManager.F("lx_error_saving_failed_8f8d5", providerName);
                }
            }
            else
            {
                txtInfo.Text = LanguageManager.F("lx_the_key_was_not_changed_f8b21", providerName);
            }
        }

        private void DeleteKey(string providerName, WpfTextBox keyBox, WpfTextBlock statusLabel)
        {
            var result = WpfMessageBox.Show(
                "Delete the API key for " + providerName + "?\nThe provider will be deactivated.",
                "Confirm deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            MediaProviderSettings.DeleteKey(providerName);
            _creator?.DeleteMediaProviderKey(providerName);
            RefreshStatus(providerName, statusLabel, keyBox);
            txtInfo.Text = LanguageManager.F("lx_the_key_is_deleted_the_a0bdf", providerName);
        }

        // ── Hyperlink ──────────────────────────────────────────────────────────

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception swallowed) { AppLog.Error("Opening a provider link", swallowed); }   // v1.4: was silent
            e.Handled = true;
        }

        // ── JSON providers ────────────────────────────────────────────────────

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = JsonProviderLoader.GetProvidersFolder();
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(LanguageManager.F("lx_open_folder_error", ex.Message),
                    LanguageManager.T("generic_error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            MediaProviderRegistry.Instance.ReloadJsonProviders();
            RefreshJsonProvidersList();
            txtInfo.Text = LanguageManager.T("lx_providers_refreshed_0e951");
        }

        private void RefreshJsonProvidersList()
        {
            var jsonProviders = MediaProviderRegistry.Instance.All
                .OfType<JsonMediaProvider>().ToList();

            txtJsonProviders.Text = jsonProviders.Count == 0
                ? LanguageManager.T("lx_prov_none_loaded")
                : LanguageManager.F("lx_prov_loaded", string.Join(", ", jsonProviders.Select(p =>
                    p.Name + LanguageManager.T(p.IsConfigured ? "lx_prov_on_mark" : "lx_prov_nokey_mark"))));
        }

        // ── Close ────────────────────────────────────────────────────────────

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
