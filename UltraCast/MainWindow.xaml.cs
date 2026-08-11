using System;
using System.Windows;
using UltraCast.Services;
using UltraCast.ViewModels;

namespace UltraCast
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();
        private readonly GlobalHotkeyService _hotkeys = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

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
                    case nameof(MainViewModel.IsPaused):
                        PauseButton.Content = _vm.IsPaused ? "Resume" : "Pause";
                        break;
                }
            };

            Loaded += (_, __) => _hotkeys.Register(this);
            _hotkeys.ToggleRequested += () => Dispatcher.Invoke(_vm.HandleToggleHotkey);
            _hotkeys.PauseToggleRequested += () => Dispatcher.Invoke(_vm.HandlePauseHotkey);

            // AppLogger can be written to from any thread (capture loops,
            // NAudio callbacks, the global exception handlers in App.xaml.cs) -
            // always hop to the UI thread before touching LogTextBox.
            AppLogger.LineLogged += line => Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(line + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
        }

        private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
            if (dialog.ShowDialog(this) == true)
                _vm.OutputFolder = dialog.SelectedPath;
        }

        private void AudioOption_Changed(object sender, RoutedEventArgs e)
        {
            // IsChecked="True" in XAML fires this Checked event immediately
            // during InitializeComponent(), while later-declared named
            // elements (like MicrophoneCheckBox, which sits below
            // SystemAudioCheckBox in the XAML tree) haven't been connected
            // to their fields yet - so guard against that first, spurious
            // call instead of crashing on a null reference.
            if (SystemAudioCheckBox == null || MicrophoneCheckBox == null)
                return;

            _vm.CaptureSystemAudio = SystemAudioCheckBox.IsChecked == true;
            _vm.CaptureMicrophone = MicrophoneCheckBox.IsChecked == true;
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

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.PauseCommand.CanExecute(null))
                _vm.PauseCommand.Execute(null);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow { Owner = this };
            about.ShowDialog();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Unregister the global hotkeys HERE, while the window's handle
            // (HwndSource) is still fully alive - not in OnClosed. By the
            // time OnClosed fires, WPF has already begun tearing down the
            // HwndSource, and calling RemoveHook on it at that point throws
            // an InvalidOperationException (visible as a first-chance
            // exception in the debugger right at exit). Wrapped in a
            // try/catch as well, since this is teardown code where the
            // safest failure mode is "log it and keep closing", not crash
            // the app on the way out.
            try
            {
                _hotkeys.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Log("MainWindow: hotkey cleanup during close - " + ex.Message);
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_vm.IsRecording && _vm.StopCommand.CanExecute(null))
                _vm.StopCommand.Execute(null);
            base.OnClosed(e);
        }
    }
}
