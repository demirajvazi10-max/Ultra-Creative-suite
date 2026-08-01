using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;

namespace UltraVideoEditor
{
    public partial class AnimationPreviewDialog : Window
    {
        private List<AnimationScene> _scenes;

        public AnimationPreviewDialog(List<AnimationScene> scenes, List<string> availableImages)
        {
            InitializeComponent();
            UiScaling.Register(this);

            // Postavi dostupne slike za svaku scenu
            foreach (var scene in scenes)
            {
                scene.AvailableImages = availableImages;
                scene.AvailableEffects = new List<string> { "Fade In", "Fade Out", "Zoom In", "Zoom Out", "Slide Left", "Slide Right", "None" };
            }

            _scenes = scenes;
            lstScenes.ItemsSource = _scenes;

            AutomationProperties.SetName(this, "Animation scenario preview dialog");
        }

        public List<AnimationScene> GetScenes() => _scenes;

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
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