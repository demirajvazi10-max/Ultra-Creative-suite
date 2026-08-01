using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class ApiKeyDialog : Window
    {
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "en";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        // Pixabay key (originalni)
        public string ApiKey => txtApiKey.Text.Trim();

        // Azure AI Foundry polja
        public string AzureEndpoint   => txtAzureEndpoint.Text.Trim();
        public string AzureApiKey     => txtAzureApiKey.Text.Trim();
        public string AzureDeployment => string.IsNullOrWhiteSpace(txtAzureDeployment.Text)
                                         ? "gpt-4o-mini"
                                         : txtAzureDeployment.Text.Trim();

        public ApiKeyDialog(string service, string message,
            string existingPixabayKey    = null,
            string existingAzureEndpoint = null,
            string existingAzureKey      = null,
            string existingAzureDeploy   = null)
        {
            InitializeComponent();
            UiScaling.Register(this);
            Title = LF("akd_service_title", service);
            txtApiKey.ToolTip = message;

            // Popuni postojece vrijednosti
            if (!string.IsNullOrWhiteSpace(existingPixabayKey))
                txtApiKey.Text = existingPixabayKey;

            if (!string.IsNullOrWhiteSpace(existingAzureEndpoint))
                txtAzureEndpoint.Text = existingAzureEndpoint;

            if (!string.IsNullOrWhiteSpace(existingAzureKey))
                txtAzureApiKey.Text = existingAzureKey;

            if (!string.IsNullOrWhiteSpace(existingAzureDeploy))
                txtAzureDeployment.Text = existingAzureDeploy;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Pixabay nije obavezan ako se samo Azure podesava i obrnuto
            // Ali barem jedno mora biti popunjeno
            bool hasPixabay = !string.IsNullOrWhiteSpace(txtApiKey.Text);
            bool hasAzure   = !string.IsNullOrWhiteSpace(txtAzureEndpoint.Text) &&
                              !string.IsNullOrWhiteSpace(txtAzureApiKey.Text);

            if (!hasPixabay && !hasAzure)
            {
                WpfMessageBox.Show(
                    "Unesi barem Pixabay API key ili Azure AI Foundry endpoint i key.",
                    L("warning"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void HelpLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://pixabay.com/api/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(LF("akd_link_error", ex.Message), L("error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
