using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace UltraStudio.Views
{
    /// <summary>
    /// Mala, ne-modalna traka napretka za operacije koje mogu potrajati (npr.
    /// AI izdvajanje objekta) — bez ovoga, spora operacija je izgledala kao
    /// da se aplikacija zamrzla/srušila, jer nije bilo NIKAKVE povratne
    /// informacije dok je trajala. Stage tekst je live region (Assertive),
    /// pa JAWS pročita svaku promenu odmah, a vizuelni korisnici vide
    /// neodređenu (indeterminate) traku napretka + tekst faze.
    /// </summary>
    public partial class ProgressDialog : Window
    {
        private readonly TextBlock _stageText;

        public ProgressDialog(string title, Window owner)
        {
            Title = title;
            Owner = owner;
            Width = 420; Height = 130;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = (Brush)Application.Current.Resources["BrBgDark"];

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _stageText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["BrText"],
                FontSize = 13
            };
            _stageText.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Assertive);
            Grid.SetRow(_stageText, 0);
            grid.Children.Add(_stageText);

            var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 16, 0, 0) };
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);

            Content = grid;
        }

        /// <summary>Ažurira tekst faze i odmah ga najavljuje čitaču ekrana.</summary>
        public void SetStage(string text)
        {
            _stageText.Text = text;
            _stageText.SetValue(AutomationProperties.NameProperty, text);
            var peer = UIElementAutomationPeer.FromElement(_stageText) ?? UIElementAutomationPeer.CreatePeerForElement(_stageText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
