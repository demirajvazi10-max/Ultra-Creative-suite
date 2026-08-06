using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UltraCaptions.Models;
using UltraCaptions.ViewModels;

namespace UltraCaptions
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();
        private readonly DispatcherTimer _positionTimer;
        private bool _isDraggingSlider;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            CaptionsListView.ItemsSource = _vm.Captions;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.StatusMessage))
                    StatusText.Text = _vm.StatusMessage;
            };
        }

        // ── Toolbar ──────────────────────────────────────────────────────

        private void OpenMediaButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Media files (*.mp4;*.mkv;*.mov;*.mp3;*.wav;*.m4a)|*.mp4;*.mkv;*.mov;*.mp3;*.wav;*.m4a|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                _vm.MediaPath = dialog.FileName;
                Player.Source = new Uri(dialog.FileName);
                Player.Play();
                Player.Pause(); // load the media without starting playback
                StatusText.Text = $"Loaded: {System.IO.Path.GetFileName(dialog.FileName)}";
            }
        }

        private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.TranscribeCommand.CanExecute(null))
                _vm.TranscribeCommand.Execute(null);
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private void ImportSrtButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "SubRip subtitle (*.srt)|*.srt" };
            if (dialog.ShowDialog() == true)
                _vm.ImportSrt(dialog.FileName);
        }

        private void ExportSrtButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "SubRip subtitle (*.srt)|*.srt", FileName = "captions.srt" };
            if (dialog.ShowDialog() == true)
                _vm.ExportSrt(dialog.FileName);
        }

        private void NewLineButton_Click(object sender, RoutedEventArgs e) => AddNewLineAtCurrentPosition();

        private void DeleteLineButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.DeleteCaptionCommand.CanExecute(null))
                _vm.DeleteCaptionCommand.Execute(null);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow { Owner = this };
            about.ShowDialog();
        }

        // ── Caption list / detail panel ─────────────────────────────────

        private void CaptionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.SelectedCaption = CaptionsListView.SelectedItem as CaptionEntry;
            RefreshDetailPanel();
        }

        private void RefreshDetailPanel()
        {
            var c = _vm.SelectedCaption;
            LineTextBox.Text = c?.Text ?? "";
            StartValueText.Text = c != null ? Format(c.Start) : "--:--.--";
            EndValueText.Text = c != null ? Format(c.End) : "--:--.--";
        }

        private void LineTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_vm.SelectedCaption != null)
                _vm.SelectedCaption.Text = LineTextBox.Text;
        }

        // ── Manual timing ────────────────────────────────────────────────

        private void MarkStartButton_Click(object sender, RoutedEventArgs e) => MarkStart();
        private void MarkEndButton_Click(object sender, RoutedEventArgs e) => MarkEnd();

        private void MarkStart()
        {
            var target = _vm.SelectedCaption ?? CreateAndSelectNewLine();
            target.Start = Player.Position;
            RefreshDetailPanel();
            StatusText.Text = $"Start marked at {Format(Player.Position)}.";
        }

        private void MarkEnd()
        {
            var target = _vm.SelectedCaption ?? CreateAndSelectNewLine();
            target.End = Player.Position;
            RefreshDetailPanel();
            StatusText.Text = $"End marked at {Format(Player.Position)}.";
        }

        private CaptionEntry CreateAndSelectNewLine()
        {
            _vm.AddNewCaption();
            CaptionsListView.SelectedItem = _vm.SelectedCaption;
            return _vm.SelectedCaption!;
        }

        private void AddNewLineAtCurrentPosition()
        {
            _vm.AddNewCaption(Player.Position, Player.Position);
            CaptionsListView.SelectedItem = _vm.SelectedCaption;
            RefreshDetailPanel();
        }

        // ── Playback ─────────────────────────────────────────────────────

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (Player.NaturalDuration.HasTimeSpan)
                PositionSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

        private void TogglePlayPause()
        {
            if (Player.Source == null) return;

            // MediaElement doesn't expose an "is playing" flag directly, so we
            // track it indirectly via the button toggle instead of querying it.
            if (_isPlaying)
            {
                Player.Pause();
                _isPlaying = false;
            }
            else
            {
                Player.Play();
                _isPlaying = true;
            }
        }

        private bool _isPlaying;

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            if (Player.Source == null || _isDraggingSlider) return;

            if (Player.NaturalDuration.HasTimeSpan)
            {
                PositionSlider.Value = Player.Position.TotalSeconds;
                PositionText.Text = $"{Format(Player.Position)} / {Format(Player.NaturalDuration.TimeSpan)}";
            }
        }

        private void PositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            Player.Position = TimeSpan.FromSeconds(PositionSlider.Value);
            _isDraggingSlider = false;
        }

        // ── Keyboard shortcuts ───────────────────────────────────────────
        // Space = play/pause, [ = mark start, ] = mark end, Ctrl+N = new line,
        // Delete = delete selected line. All disabled while typing in a text
        // box, so they never interfere with editing caption text.

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool typingInTextBox = Keyboard.FocusedElement is TextBox;

            if (typingInTextBox)
            {
                if (e.Key == Key.Escape)
                    Keyboard.ClearFocus();
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;
                case Key.OemOpenBrackets:
                    MarkStart();
                    e.Handled = true;
                    break;
                case Key.OemCloseBrackets:
                    MarkEnd();
                    e.Handled = true;
                    break;
                case Key.N when Keyboard.Modifiers == ModifierKeys.Control:
                    AddNewLineAtCurrentPosition();
                    e.Handled = true;
                    break;
                case Key.Delete when _vm.SelectedCaption != null && !(Keyboard.FocusedElement is TextBox):
                    if (_vm.DeleteCaptionCommand.CanExecute(null))
                        _vm.DeleteCaptionCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }

        private static string Format(TimeSpan t) => t.ToString(@"mm\:ss\.ff");
    }
}
