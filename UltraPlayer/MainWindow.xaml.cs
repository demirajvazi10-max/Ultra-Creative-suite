using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UltraPlayer.Models;
using UltraPlayer.ViewModels;

namespace UltraPlayer
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();
        private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        private static readonly double[] SpeedSteps = { 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };

        private MediaTimeline? _timeline;
        private MediaClock? _clock;
        private bool _isPlaying;
        private bool _isDraggingSlider;

        private int _sleepSecondsRemaining = -1;   // -1 = timer inactive
        private bool _sleepAtEndOfTrack;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            PlaylistListBox.ItemsSource = _vm.Playlist;

            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            _sleepTimer.Tick += SleepTimer_Tick;

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.StatusMessage))
                    StatusText.Text = _vm.StatusMessage;
            };
        }

        // ── Playlist management ─────────────────────────────────────────

        private void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Audio files (*.mp3;*.wav;*.m4a;*.wma;*.aac)|*.mp3;*.wav;*.m4a;*.wma;*.aac|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                    _vm.AddTrack(file);

                _vm.StatusMessage = $"{dialog.FileNames.Length} file(s) added. Playlist has {_vm.Playlist.Count} track(s).";
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.RemoveTrackCommand.CanExecute(null))
                _vm.RemoveTrackCommand.Execute(null);
        }

        private void PlaylistListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.SelectedTrack = PlaylistListBox.SelectedItem as PlayerTrack;
        }

        private void PlaylistListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_vm.SelectedTrack != null)
                PlaySelected();
        }

        // ── Playback ─────────────────────────────────────────────────────

        private void PlaySelected()
        {
            var track = _vm.SelectedTrack ?? (_vm.Playlist.Count > 0 ? _vm.Playlist[0] : null);
            if (track == null)
            {
                _vm.StatusMessage = "Playlist is empty. Add files first.";
                return;
            }

            foreach (var t in _vm.Playlist) t.IsCurrent = false;
            track.IsCurrent = true;
            PlaylistListBox.SelectedItem = track;

            _timeline = new MediaTimeline(new Uri(track.FilePath)) { SpeedRatio = _vm.CurrentSpeed };
            _clock = _timeline.CreateClock();
            Player.Clock = _clock;
            _clock.Controller?.Begin();

            _isPlaying = true;
            PlayPauseButton.Content = "Pause (Space)";
            _vm.StatusMessage = $"Playing: {track.Title}";
        }

        private void TogglePlayPause()
        {
            if (_clock == null)
            {
                PlaySelected();
                return;
            }

            if (_isPlaying)
            {
                _clock.Controller?.Pause();
                _isPlaying = false;
                PlayPauseButton.Content = "Play (Space)";
                _vm.StatusMessage = "Paused.";
            }
            else
            {
                _clock.Controller?.Resume();
                _isPlaying = true;
                PlayPauseButton.Content = "Pause (Space)";
                _vm.StatusMessage = "Playing.";
            }
        }

        private void Stop()
        {
            _clock?.Controller?.Stop();
            _clock = null;
            _timeline = null;
            _isPlaying = false;
            PlayPauseButton.Content = "Play (Space)";
            PositionSlider.Value = 0;
            _vm.StatusMessage = "Stopped.";
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
        private void StopButton_Click(object sender, RoutedEventArgs e) => Stop();

        private void NextButton_Click(object sender, RoutedEventArgs e) => GoToTrack(1);
        private void PreviousButton_Click(object sender, RoutedEventArgs e) => GoToTrack(-1);

        private void GoToTrack(int offset)
        {
            if (_vm.Playlist.Count == 0) return;

            int currentIndex = _vm.SelectedTrack != null ? _vm.Playlist.IndexOf(_vm.SelectedTrack) : -1;
            int nextIndex = currentIndex + offset;
            if (nextIndex < 0 || nextIndex >= _vm.Playlist.Count) return;

            _vm.SelectedTrack = _vm.Playlist[nextIndex];
            PlaylistListBox.SelectedItem = _vm.SelectedTrack;
            PlaySelected();
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (Player.NaturalDuration.HasTimeSpan)
                PositionSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (_sleepAtEndOfTrack)
            {
                Stop();
                ResetSleepTimerUi();
                _vm.StatusMessage = "Sleep timer: stopped at end of track.";
                return;
            }

            GoToTrack(1);
        }

        // ── Position slider ─────────────────────────────────────────────

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            if (_clock == null || _isDraggingSlider) return;
            if (!Player.NaturalDuration.HasTimeSpan) return;

            PositionSlider.Value = Player.Position.TotalSeconds;
            PositionText.Text = $"{Format(Player.Position)} / {Format(Player.NaturalDuration.TimeSpan)}";
        }

        private void PositionSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isDraggingSlider = true;

        private void PositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            SeekTo(TimeSpan.FromSeconds(PositionSlider.Value));
        }

        private void SeekTo(TimeSpan position)
        {
            _clock?.Controller?.Seek(position, TimeSeekOrigin.BeginTime);
        }

        private void SeekRelative(double seconds)
        {
            if (_clock == null) return;
            var target = Player.Position + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            SeekTo(target);
        }

        // ── Speed ────────────────────────────────────────────────────────

        private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeedComboBox.SelectedItem is not ComboBoxItem item) return;
            string text = (item.Content as string ?? "1.0x").TrimEnd('x');
            if (!double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out double speed)) return;

            _vm.CurrentSpeed = speed;
            ApplySpeedToCurrentPlayback(speed);
        }

        /// <summary>
        /// WPF's ClockController has no way to change a running clock's speed
        /// ratio - SpeedRatio is fixed on the Timeline at the moment the
        /// clock is created. To change speed mid-playback, rebuild the clock
        /// at the new speed and seek it to where the old one had reached, so
        /// it sounds like a continuous change rather than a restart.
        /// </summary>
        private void ApplySpeedToCurrentPlayback(double speed)
        {
            if (_timeline == null || _clock == null) return;

            var resumePosition = Player.Position;
            bool wasPlaying = _isPlaying;

            _clock.Controller?.Stop();

            _timeline = new MediaTimeline(_timeline.Source) { SpeedRatio = speed };
            _clock = _timeline.CreateClock();
            Player.Clock = _clock;
            _clock.Controller?.Begin();
            _clock.Controller?.Seek(resumePosition, TimeSeekOrigin.BeginTime);

            if (!wasPlaying)
                _clock.Controller?.Pause();
        }

        private void ChangeSpeedStep(int direction)
        {
            int currentIndex = Array.IndexOf(SpeedSteps, _vm.CurrentSpeed);
            if (currentIndex < 0) currentIndex = 1; // default to 1.0x if not found
            int newIndex = Math.Clamp(currentIndex + direction, 0, SpeedSteps.Length - 1);
            SpeedComboBox.SelectedIndex = newIndex; // triggers SelectionChanged above
        }

        // ── Sleep timer ──────────────────────────────────────────────────

        private void SleepTimerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SleepTimerComboBox.SelectedItem is not ComboBoxItem item) return;
            string label = item.Content as string ?? "Off";

            _sleepTimer.Stop();
            _sleepAtEndOfTrack = false;

            switch (label)
            {
                case "Off":
                    _sleepSecondsRemaining = -1;
                    _vm.SleepTimerLabel = "Sleep timer: off";
                    break;
                case "End of track":
                    _sleepAtEndOfTrack = true;
                    _vm.SleepTimerLabel = "Sleep timer: end of current track";
                    break;
                default:
                    int minutes = int.Parse(label.Split(' ')[0]);
                    _sleepSecondsRemaining = minutes * 60;
                    _vm.SleepTimerLabel = $"Sleep timer: {minutes} minutes remaining";
                    _sleepTimer.Start();
                    break;
            }
        }

        private void SleepTimer_Tick(object? sender, EventArgs e)
        {
            if (_sleepSecondsRemaining <= 0)
            {
                _sleepTimer.Stop();
                Stop();
                ResetSleepTimerUi();
                _vm.StatusMessage = "Sleep timer: playback paused.";
                return;
            }

            _sleepSecondsRemaining--;
            int minutesLeft = (_sleepSecondsRemaining + 59) / 60;
            _vm.SleepTimerLabel = $"Sleep timer: {minutesLeft} minute(s) remaining";
        }

        private void ResetSleepTimerUi()
        {
            _sleepTimer.Stop();
            _sleepSecondsRemaining = -1;
            _sleepAtEndOfTrack = false;
            SleepTimerComboBox.SelectedIndex = 0; // "Off"
            _vm.SleepTimerLabel = "Sleep timer: off";
        }

        // ── About ────────────────────────────────────────────────────────

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow { Owner = this };
            about.ShowDialog();
        }

        // ── Keyboard shortcuts ───────────────────────────────────────────
        // Space = play/pause, Right/Left = seek +/-10s, Ctrl+Right/Left =
        // next/prev track, +/- = speed up/down. Arrow keys are skipped
        // while focus is on the playlist or a combo box, so they keep doing
        // normal list/combo navigation there instead of seeking/skipping.

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool focusOnSelector = Keyboard.FocusedElement is Selector;

            switch (e.Key)
            {
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;

                case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                    GoToTrack(1);
                    e.Handled = true;
                    break;

                case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                    GoToTrack(-1);
                    e.Handled = true;
                    break;

                case Key.Right when !focusOnSelector && Keyboard.Modifiers == ModifierKeys.None:
                    SeekRelative(10);
                    e.Handled = true;
                    break;

                case Key.Left when !focusOnSelector && Keyboard.Modifiers == ModifierKeys.None:
                    SeekRelative(-10);
                    e.Handled = true;
                    break;

                case Key.OemPlus:
                case Key.Add:
                    ChangeSpeedStep(1);
                    e.Handled = true;
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    ChangeSpeedStep(-1);
                    e.Handled = true;
                    break;
            }
        }

        private static string Format(TimeSpan t) => t.ToString(t.Hours > 0 ? @"h\:mm\:ss" : @"mm\:ss");
    }
}
