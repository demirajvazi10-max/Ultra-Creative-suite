using System.Windows;

namespace UltraComposer.App;

/// <summary>
/// The "Uputstvo za upotrebu"/"User Guide" window - a plain topic list plus
/// a read-only body, showing the content from <see cref="HelpContent"/> in
/// whichever language is current (see Localization.Current) at the moment
/// this window is opened. Doesn't react to a language switch while it's
/// already open - closing and reopening it (from the Pomoć/Help menu) picks
/// up the new language, same as everything else that reads Localization.T
/// only at the point it's shown, not continuously.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Title = Localization.T("Help.WindowTitle");
        WindowTitleText.Text = Localization.T("Help.WindowTitle");
        TopicsLabelText.Text = Localization.T("Help.TopicsLabel");
        CloseButton.Content = Localization.T("Help.CloseButton");

        TopicsListBox.ItemsSource = HelpContent.GetTopics();
        if (TopicsListBox.Items.Count > 0)
        {
            TopicsListBox.SelectedIndex = 0;
        }

        TopicsListBox.Focus();
    }

    private void TopicsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TopicsListBox.SelectedItem is HelpContent.Topic topic)
        {
            TopicTitleText.Text = topic.Title;
            TopicBodyTextBox.Text = topic.Body;
            TopicBodyTextBox.ScrollToHome();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
