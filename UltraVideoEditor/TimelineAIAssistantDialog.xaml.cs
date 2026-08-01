using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;

using NAudio.Wave;
using MessageBox      = System.Windows.MessageBox;
using Button          = System.Windows.Controls.Button;
using Clipboard       = System.Windows.Clipboard;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color           = System.Windows.Media.Color;

namespace UltraVideoEditor
{
    public partial class TimelineAIAssistantDialog : Window
    {
        // STT polja
        private WaveInEvent                     _waveIn;
        private WaveFileWriter                  _waveWriter;
        private string                          _recordingPath;
        private bool                            _recording = false;

        private List<TimelineItem>              _workingItems;   // trenutno stanje (preview)
        private readonly List<TimelineItem>     _originalItems;  // backup za undo
        private readonly Stack<List<TimelineItem>> _undoStack = new();
        private CancellationTokenSource         _cts;
        private bool                            _running = false;
        private bool                            _hasChanges = false;

        public List<TimelineItem> ResultItems => new List<TimelineItem>(_workingItems);

        public TimelineAIAssistantDialog(List<TimelineItem> timelineItems)
        {
            InitializeComponent();
            UiScaling.Register(this);
            _originalItems = new List<TimelineItem>(timelineItems);
            _workingItems  = new List<TimelineItem>(timelineItems);
            TxtSubtitle.Text = $"Timeline: {timelineItems.Count} clips  ·  Ollama (local AI) + rule-based fallback";
        }

        // ── Primeri ───────────────────────────────────────────────────

        private void BtnExample_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
                TxtCommand.Text = btn.Content.ToString();
            TxtCommand.Focus();
        }

        // ── STT — snimanje mikrofona ──────────────────────────────────

        private async void BtnMic_Click(object sender, RoutedEventArgs e)
        {
            if (_recording)
            {
                StopRecording();
                return;
            }

            // Provjera da li postoji mikrofon
            if (WaveInEvent.DeviceCount == 0)
            {
                ShowStatus("⚠️  Microphone not found.", null, true, isError: true);
                return;
            }

            _recordingPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"stt_{Guid.NewGuid():N}.wav");

            try
            {
                _waveIn = new WaveInEvent
                {
                    WaveFormat     = new WaveFormat(16000, 1), // 16kHz mono — Whisper standard
                    BufferMilliseconds = 100,
                };
                _waveWriter = new WaveFileWriter(_recordingPath, _waveIn.WaveFormat);
                _waveIn.DataAvailable += (_, args) =>
                    _waveWriter?.Write(args.Buffer, 0, args.BytesRecorded);
                _waveIn.RecordingStopped += async (s2, e2) =>
                {
                    _waveWriter?.Flush();
                    _waveWriter?.Dispose();
                    _waveWriter = null;
                    if (File.Exists(_recordingPath))
                        await Dispatcher.InvokeAsync(() => TranscribeRecordingAsync());
                };

                _waveIn.StartRecording();
                _recording = true;
                BtnMic.Content = "⏹ Stop";
                SetMicButtonColor(true);
                ShowStatus("🎙 Snimam… Izgovorite komandu, pa pritisnite Stop.", null, true, false);

                // Auto-stop posle 10 sekundi
                _ = Task.Delay(10000).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { if (_recording) StopRecording(); }));
            }
            catch (Exception ex)
            {
                ShowStatus($"⚠️  Microphone error: {ex.Message}", null, true, isError: true);
                _recording = false;
            }
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            BtnMic.Content = "🎙 Govori";
            SetMicButtonColor(false);
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
        }

        private async void TranscribeRecordingAsync()
        {
            if (!File.Exists(_recordingPath)) return;

            try
            {
                BtnMic.IsEnabled     = false;
                BtnExecute.IsEnabled = false;
                ShowStatus("⏳ Transkribujem snimak (Whisper large-v3)…", null, true, false);

                string ffmpegPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

                var result = await AITranscription.TranscribeAsync(
                    mediaPath:  _recordingPath,
                    language:   "sr",
                    ffmpegPath: ffmpegPath,
                    modelSize:  "large-v3",
                    ct:         _cts?.Token ?? CancellationToken.None);

                if (result.Success && !string.IsNullOrWhiteSpace(result.FullText))
                {
                    TxtCommand.Text = result.FullText.Trim();
                    ShowStatus($"✅ Prepoznato: \"{result.FullText.Trim()}\"", null, true, false);
                    // Execute automatically
                    await Task.Delay(400);
                    BtnExecute_Click(null, null);
                }
                else
                {
                    ShowStatus("⚠️  Whisper did not recognize the command. Please try again.",
                               result.ErrorMessage, true, isError: true);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"⚠️  Transcription error: {ex.Message}", null, true, isError: true);
            }
            finally
            {
                BtnMic.IsEnabled     = true;
                BtnExecute.IsEnabled = true;
                try { if (File.Exists(_recordingPath)) File.Delete(_recordingPath); } catch { }
                _recordingPath = null;
            }
        }

        private void TxtCommand_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnExecute_Click(sender, e);
        }

        // ── Execution ───────────────────────────────────────────────

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            string cmd = TxtCommand.Text.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            if (_running) return;

            _running = true;
            BtnExecute.IsEnabled = false;
            BtnExecute.Content   = "⏳";
            ShowStatus(null, null, false);

            _cts = new CancellationTokenSource();

            try
            {
                var result = await TimelineAIAssistant.ExecuteCommandAsync(
                    cmd, _workingItems, _cts.Token);

                if (result.Success)
                {
                    _undoStack.Push(new List<TimelineItem>(_workingItems));
                    _workingItems  = result.UpdatedItems;
                    _hasChanges    = true;

                    ShowStatus($"✅  {result.Summary}",
                               $"Understood: {result.Command?.Explanation ?? cmd}  |  " +
                               $"{result.OriginalCount} → {result.ResultCount} clips", true);

                    AddHistory(cmd, result);
                    TxtSubtitle.Text = $"Timeline: {_workingItems.Count} clips (preview — not applied)";
                    BtnUndoLast.IsEnabled = true;
                    BtnApplyAll.IsEnabled = true;
                    TxtCommand.Clear();
                }
                else
                {
                    ShowStatus($"⚠️  {result.Error}", null, false, isError: true);
                }
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Cancelled.", null, false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", null, false, isError: true);
            }
            finally
            {
                _running             = false;
                BtnExecute.IsEnabled = true;
                BtnExecute.Content   = "▶ Execute";
                _cts?.Dispose(); _cts = null;
            }
        }

        // ── Undo ─────────────────────────────────────────────────────

        private void BtnUndoLast_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0) return;
            _workingItems = _undoStack.Pop();
            TxtSubtitle.Text = $"Timeline: {_workingItems.Count} clips (undone)";
            BtnUndoLast.IsEnabled = _undoStack.Count > 0;
            if (_undoStack.Count == 0) _hasChanges = false;

            // Ukloni poslednji history entry
            if (HistoryPanel.Children.Count > 0)
                HistoryPanel.Children.RemoveAt(HistoryPanel.Children.Count - 1);

            ShowStatus("↩ Last command has been undone.", null, true);
        }

        // ── Primeni na timeline ───────────────────────────────────────

        private void BtnApplyAll_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasChanges) return;

            var confirm = MessageBox.Show(
                $"Apply changes to the timeline?\n\n" +
                $"Original: {_originalItems.Count} clips\n" +
                $"New state: {_workingItems.Count} clips",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            if (Owner is MainWindow mw)
            {
                mw.timelineItems.Clear();
                foreach (var item in _workingItems)
                    mw.timelineItems.Add(item);
            }

            DialogResult = true;
            Close();
        }

        // ── Istorija ──────────────────────────────────────────────────

        private void AddHistory(string cmd, AssistantResult result)
        {
            var entry = new Border
            {
                Background   = ThemeBrushes.PanelBg2,
                CornerRadius = new CornerRadius(6),
                Margin       = new Thickness(0, 0, 0, 6),
                Padding      = new Thickness(10, 8, 10, 8),
            };

            var sp = new StackPanel();

            var row = new DockPanel();
            var badge = new Border
            {
                Background   = new SolidColorBrush(Color.FromRgb(124, 58, 237)),
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(6, 2, 6, 2),
                Margin       = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Child = new TextBlock
            {
                Text       = result.Command?.Action ?? "cmd",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize   = 10,
                FontWeight = FontWeights.Bold,
            };
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);

            var timeText = new TextBlock
            {
                Text       = DateTime.Now.ToString("HH:mm:ss"),
                Foreground = ThemeBrushes.TextSecondary,
                FontSize   = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(timeText, Dock.Right);
            row.Children.Add(timeText);

            row.Children.Add(new TextBlock
            {
                Text       = $"\"{cmd}\"",
                Foreground = ThemeBrushes.TextPrimary,
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            sp.Children.Add(row);
            sp.Children.Add(new TextBlock
            {
                Text       = $"{result.OriginalCount} → {result.ResultCount} clips  ·  {result.Summary}",
                Foreground = ThemeBrushes.TextSecondary,
                FontSize   = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 4, 0, 0),
            });

            entry.Child = sp;
            AutomationProperties.SetName(entry,
                $"Command {cmd}, {result.OriginalCount} to {result.ResultCount} clips");
            HistoryPanel.Children.Add(entry);
        }

        // ── Status panel ──────────────────────────────────────────────

        private void ShowStatus(string msg, string explanation, bool show,
                                bool isError = false)
        {
            if (!show) { StatusPanel.Visibility = Visibility.Collapsed; return; }

            StatusPanel.Background  = isError
                ? new SolidColorBrush(Color.FromArgb(40, 239, 68, 68))
                : new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
            StatusPanel.BorderBrush = isError
                ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
                : new SolidColorBrush(Color.FromRgb(16, 185, 129));
            StatusPanel.BorderThickness = new Thickness(1);
            TxtStatusMsg.Text           = msg ?? "";
            TxtCommandExplanation.Text  = explanation ?? "";
            TxtCommandExplanation.Visibility = string.IsNullOrEmpty(explanation)
                ? Visibility.Collapsed : Visibility.Visible;
            StatusPanel.Visibility = Visibility.Visible;
        }

        private void SetMicButtonColor(bool recording)
        {
            // Nalazimo Border unutar Button template-a i mijenjamo boju
            if (BtnMic.Template?.FindName("MicBorder", BtnMic) is System.Windows.Controls.Border b)
                b.Background = recording
                    ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
                    : new SolidColorBrush(Color.FromRgb(22, 163, 74));
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_hasChanges)
            {
                var r = MessageBox.Show(
                    "You have unapplied changes. Apply them before closing?",
                    "Unapplied changes",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes) { BtnApplyAll_Click(sender, e); return; }
            }
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_recording) StopRecording();
            _waveWriter?.Dispose();
            try { if (_recordingPath != null && File.Exists(_recordingPath)) File.Delete(_recordingPath); } catch { }
            _cts?.Cancel();
            _cts?.Dispose();
            base.OnClosed(e);
        }
    }
}
