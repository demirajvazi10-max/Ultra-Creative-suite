using System;
using System.Windows;
using UltraRecord.ViewModels;

namespace UltraRecord
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            TracksListBox.ItemsSource = _vm.Tracks;

            _vm.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(MainViewModel.StatusMessage):
                        StatusText.Text = _vm.StatusMessage;
                        break;
                    case nameof(MainViewModel.OutputFolder):
                        OutputFolderText.Text = string.IsNullOrWhiteSpace(_vm.OutputFolder)
                            ? "No output folder chosen"
                            : $"Output: {_vm.OutputFolder}";
                        break;
                }
            };
        }

        private void AddTrackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.AddTrackCommand.CanExecute(null))
                _vm.AddTrackCommand.Execute(null);
        }

        private void RemoveTrackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.RemoveTrackCommand.CanExecute(null))
                _vm.RemoveTrackCommand.Execute(null);
        }

        private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
            if (dialog.ShowDialog(this) == true)
                _vm.OutputFolder = dialog.SelectedPath;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.StartCommand.CanExecute(null))
                _vm.StartCommand.Execute(null);
            else if (string.IsNullOrWhiteSpace(_vm.OutputFolder))
                StatusText.Text = "Choose an output folder before starting.";
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.StopCommand.CanExecute(null))
                _vm.StopCommand.Execute(null);
        }

        private void TracksListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _vm.SelectedTrack = TracksListBox.SelectedItem as Models.RecordingTrack;
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow { Owner = this };
            about.ShowDialog();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_vm.IsRecording && _vm.StopCommand.CanExecute(null))
                _vm.StopCommand.Execute(null);
            base.OnClosed(e);
        }
    }
}
