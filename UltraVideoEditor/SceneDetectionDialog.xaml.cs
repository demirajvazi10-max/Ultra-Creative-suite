using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using Microsoft.Win32;

using MessageBox       = System.Windows.MessageBox;
using CheckBox         = System.Windows.Controls.CheckBox;
using Image            = System.Windows.Controls.Image;
using Orientation      = System.Windows.Controls.Orientation;
using Clipboard        = System.Windows.Clipboard;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Brush            = System.Windows.Media.Brush;
using Brushes          = System.Windows.Media.Brushes;
using SolidColorBrush  = System.Windows.Media.SolidColorBrush;
using Color            = System.Windows.Media.Color;

namespace UltraVideoEditor
{
    public partial class SceneDetectionDialog : Window
    {
        private SceneDetectionResult        _result;
        private CancellationTokenSource     _cts;
        private bool                        _running = false;
        private readonly List<SceneSegment> _selected = new();

        public List<SceneSegment> SelectedScenes => new List<SceneSegment>(_selected);

        public SceneDetectionDialog()
        {
            InitializeComponent();
            UiScaling.Register(this);
        }

        // ── Odabir fajla ──────────────────────────────────────────────

        private void BtnPickVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select a video file",
                Filter = "Video files|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv|All files|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                TxtVideoPath.Text       = dlg.FileName;
                TxtVideoPath.Foreground = ThemeBrushes.TextPrimary;
                BtnDetect.IsEnabled     = true;
            }
        }

        // ── Detekcija ─────────────────────────────────────────────────

        private async void BtnDetect_Click(object sender, RoutedEventArgs e)
        {
            if (_running) { _cts?.Cancel(); return; }

            string videoPath = TxtVideoPath.Text;
            if (!File.Exists(videoPath))
            {
                MessageBox.Show("Please select a video file.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _running = true;
            _cts     = new CancellationTokenSource();
            _result  = null;
            _selected.Clear();
            ScenesPanel.Children.Clear();
            TxtSceneCount.Text          = "Detektujem…";
            TxtReport.Text              = "";
            BtnAddToTimeline.IsEnabled  = false;
            BtnSelectAll.IsEnabled      = false;
            BtnDeselectAll.IsEnabled    = false;
            BtnCopyReport.Visibility    = Visibility.Collapsed;
            ProgressPanel.Visibility    = Visibility.Visible;
            ProgressBar.Value           = 0;
            BtnDetect.Content           = "⏹ Cancel";

            var progress = new Progress<(int Percent, string Message)>(p =>
            {
                if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetProgress(p.Percent, p.Message)); return; }
                SetProgress(p.Percent, p.Message);
            });

            try
            {
                _result = await SmartSceneDetector.DetectAsync(videoPath, progress, _cts.Token);

                if (_result.Success)
                {
                    PopulateScenes(_result.Scenes);
                    TxtReport.Text           = _result.Report;
                    BtnCopyReport.Visibility = Visibility.Visible;
                    BtnAddToTimeline.IsEnabled = _selected.Count > 0;
                    BtnSelectAll.IsEnabled   = true;
                    BtnDeselectAll.IsEnabled = true;
                }
                else
                {
                    TxtSceneCount.Text = "Error.";
                    MessageBox.Show(_result.Error, "Detection Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                TxtSceneCount.Text = "Cancelled.";
                SetProgress(0, "Cancelled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running = false;
                _cts?.Dispose(); _cts = null;
                BtnDetect.Content   = "🔍 Detektuj scene";
                BtnDetect.IsEnabled = File.Exists(TxtVideoPath.Text);
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        // ── Prikaz scena ──────────────────────────────────────────────

        private void PopulateScenes(List<SceneSegment> scenes)
        {
            ScenesPanel.Children.Clear();
            _selected.Clear();

            foreach (var scene in scenes)
            {
                var card = BuildSceneCard(scene);
                ScenesPanel.Children.Add(card);
                _selected.Add(scene); // all included by default
            }

            TxtSceneCount.Text = $"{scenes.Count} scenes  ·  {AIHighlightEngine.FormatTime(_result.TotalDuration)}";
            BtnAddToTimeline.IsEnabled = _selected.Count > 0;
        }

        private Border BuildSceneCard(SceneSegment scene)
        {
            var card = new Border
            {
                Background   = ThemeBrushes.PanelBg2,
                CornerRadius = new CornerRadius(8),
                Margin       = new Thickness(0, 0, 0, 8),
                Padding      = new Thickness(10),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Thumbnail
            var imgBorder = new Border
            {
                Width        = 160,
                Height       = 90,
                CornerRadius = new CornerRadius(5),
                Background   = ThemeBrushes.PanelBg,
                Margin       = new Thickness(0, 0, 10, 0),
            };
            if (!string.IsNullOrEmpty(scene.ThumbnailPath) && File.Exists(scene.ThumbnailPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource      = new Uri(scene.ThumbnailPath);
                    bmp.CacheOption    = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    imgBorder.Child = new Image
                    {
                        Source  = bmp,
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                    };
                }
                catch { }
            }
            Grid.SetColumn(imgBorder, 0);
            grid.Children.Add(imgBorder);

            // Info panel
            var info = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };

            // Header: checkbox + naslov
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var chk = new CheckBox
            {
                IsChecked  = true,
                Foreground = ThemeBrushes.TextPrimary,
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                Content    = $"#{scene.Index:D3}  {AIHighlightEngine.FormatTime(scene.Start)} → {AIHighlightEngine.FormatTime(scene.End)}  ({scene.Duration:F1}s)",
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(chk, $"Scene {scene.Index}, {scene.Duration:F1} seconds");
            chk.Checked   += (_, _) => { if (!_selected.Contains(scene)) { _selected.Add(scene); UpdateAddButton(); } };
            chk.Unchecked += (_, _) => { _selected.Remove(scene); UpdateAddButton(); };
            DockPanel.SetDock(chk, Dock.Left);
            header.Children.Add(chk);
            info.Children.Add(header);

            // Label
            var lblText = new TextBlock
            {
                Text       = scene.Label,
                Foreground = ThemeBrushes.TextSecondary,
                FontSize   = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin     = new Thickness(0, 0, 0, 4),
            };
            info.Children.Add(lblText);

            // Motion info
            if (scene.Motion != null)
            {
                string motionStr = scene.Motion.IsStatic ? "📷 Static shot"
                    : $"🎥 Motion: {scene.Motion.Direction}";
                info.Children.Add(new TextBlock
                {
                    Text       = motionStr,
                    Foreground = ThemeBrushes.TextSecondary,
                    FontSize   = 11,
                });
            }

            Grid.SetColumn(info, 1);
            grid.Children.Add(info);
            card.Child = grid;
            return card;
        }

        private void UpdateAddButton()
            => BtnAddToTimeline.IsEnabled = _selected.Count > 0;

        // ── Select / Deselect ─────────────────────────────────────────

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
            => SetAllChecked(true);

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
            => SetAllChecked(false);

        private void SetAllChecked(bool val)
        {
            foreach (var child in ScenesPanel.Children)
            {
                if (child is Border b && b.Child is Grid g)
                    foreach (UIElement col in g.Children)
                        if (col is StackPanel sp)
                            foreach (UIElement item in sp.Children)
                                if (item is DockPanel dp)
                                    foreach (UIElement dpc in dp.Children)
                                        if (dpc is CheckBox chk)
                                            chk.IsChecked = val;
            }
        }

        // ── Dodaj na timeline ─────────────────────────────────────────

        private void BtnAddToTimeline_Click(object sender, RoutedEventArgs e)
        {
            if (_selected.Count == 0) return;

            if (Owner is MainWindow mw)
            {
                double cursor = 0.0;
                foreach (var scene in _selected.OrderBy(s => s.Start))
                {
                    var item = new TimelineItem
                    {
                        Path             = scene.SourcePath,
                        Start            = scene.Start,
                        End              = scene.End,
                        Duration         = scene.Duration,
                        Name             = $"Scena {scene.Index:D3}",
                        Type             = "Scene",
                        TrackIndex       = 0,
                        FixedPosition    = cursor,
                        UseFixedPosition = true,
                        AccessibilityDescription =
                            $"Scena {scene.Index}, {scene.Label}, {scene.Duration:F1} sekundi",
                    };
                    mw.timelineItems.Add(item);
                    cursor += scene.Duration;
                }
                MessageBox.Show(
                    $"Added {_selected.Count} scenes to the timeline.",
                    "Timeline", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
                return;
            }

            DialogResult = true;
            Close();
        }

        // ── Report ──────────────────────────────────────────────────

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtReport.Text))
                Clipboard.SetText(TxtReport.Text);
        }

        // ── Progress / helpers ────────────────────────────────────────

        private void SetProgress(int pct, string msg)
        {
            ProgressBar.Value   = pct;
            TxtProgressPct.Text = $"{pct}%";
            TxtProgressMsg.Text = msg;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            base.OnClosed(e);
        }
    }
}
