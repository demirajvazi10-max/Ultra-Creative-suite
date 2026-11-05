using System.Windows;

namespace UltraCast
{
    public partial class AboutWindow : Window
    {
        // The support guide follows the language chosen in the app.
        private static bool Serbian => L.Serbian;

        public AboutWindow()
        {
            InitializeComponent();
            SupportLine.Text = UltraSupport.SupportInfo.AboutLine(Serbian);
            SupportButton.Content = UltraSupport.SupportInfo.ButtonText(Serbian);
        }

        private void SupportButton_Click(object sender, RoutedEventArgs e)
            => UltraSupport.SupportInfo.Show(this, "Ultra Cast", Serbian);

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
