using System.Windows;
using System.Windows.Automation;

using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class TextOverlayDialog : Window
    {
        // Language helper
        private string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public string Text => txtText.Text;
        public string Font => (cmbFont.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "Arial";
        public int SelectedFontSize => int.TryParse(cmbFontSize.Text, out int size) ? size : 48;
        public string TextColor
        {
            get
            {
                var selected = cmbTextColor.SelectedItem as System.Windows.Controls.ComboBoxItem;
                return selected?.Tag?.ToString() ?? "#FFFFFF";
            }
        }
        public string Position => (cmbPosition.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "Centar";

        public TextOverlayDialog(string title = "Add text to image", string initialText = null, bool styleOptions = false)
        {
            InitializeComponent();
            UiScaling.Register(this);
            this.Title = title;
            AutomationProperties.SetName(this, title);
            // v1.4: this dialog is used as a general text prompt (subtitle, audio description,
            // intro/outro text, API IDs). The text box used to be announced as "Text to display
            // on image" and font/colour/position fields were shown for all of them. Now the box
            // is named after the question, and the styling fields appear only for text on images.
            AutomationProperties.SetName(txtText, title);
            // (UiScaling.Register wraps the window content, so the root grid is found via the text box)
            if (!styleOptions && txtText.Parent is System.Windows.Controls.Grid root)
            {
                foreach (System.Windows.UIElement child in root.Children)
                {
                    int row = System.Windows.Controls.Grid.GetRow(child);
                    if (row == 0 || (row >= 3 && row <= 10))
                        child.Visibility = Visibility.Collapsed;
                }
                btnApply.Content = "✅ OK";
                AutomationProperties.SetName(btnApply, "OK");
                SizeToContent = SizeToContent.Height;
            }
            if (!string.IsNullOrEmpty(initialText))
                txtText.Text = initialText;
            Loaded += (s, e) => { txtText.Focus(); txtText.SelectAll(); };
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtText.Text))
            {
                WpfMessageBox.Show(L("textoverlay_enter_text"), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}