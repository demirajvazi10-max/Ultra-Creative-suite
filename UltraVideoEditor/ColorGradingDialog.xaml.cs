using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;

using MessageBox      = System.Windows.MessageBox;
using CheckBox        = System.Windows.Controls.CheckBox;
using Image           = System.Windows.Controls.Image;
using Orientation     = System.Windows.Controls.Orientation;
using Button          = System.Windows.Controls.Button;
using Clipboard       = System.Windows.Clipboard;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color           = System.Windows.Media.Color;

namespace UltraVideoEditor
{
    public partial class ColorGradingDialog : Window
    {
        private readonly List<TimelineItem>     _items;
        private List<ClipGradeResult>           _gradeResults;
        private CancellationTokenSource         _cts;
        private bool                            _running  = false;
        private GradePreset                     _selected = GradePreset.Auto;

        // ToggleButton referenca po presetu
        private readonly Dictionary<GradePreset, ToggleButton> _presetBtns = new();

        public ColorGradingDialog(List<TimelineItem> timelineItems)
        {
            InitializeComponent();
            _items = timelineItems.Where(i => i.IsVideoTrack && File.Exists(i.Path)).ToList();
            BuildPresetButtons();
            TxtClipCount.Text = $"{_items.Count} video clips on the timeline";
        }

        // ── Preset buttons ────────────────────────────────────────────

        private void BuildPresetButtons()
        {
            var presets = new[]
            {
                (GradePreset.Auto,      "✨ Auto AI"),
                (GradePreset.Cinematic, "🎬 Cinematic"),
                (GradePreset.Warm,      "🌅 Warm"),
                (GradePreset.Cool,      "❄️ Cool"),
                (GradePreset.Vintage,   "📷 Vintage"),
                (GradePreset.Vivid,     "🌈 Vivid"),
                (GradePreset.Noir,      "⚫ Noir"),
                (GradePreset.Golden,    "🌟 Golden"),
                (GradePreset.Morning,   "🌤 Morning"),
                (GradePreset.Moody,     "🌑 Moody"),
                (GradePreset.Natural,   "🍃 Natural"),
            };

            foreach (var (preset, label) in presets)
            {
                var btn = new ToggleButton
                {
                    Content   = label,
                    IsChecked = preset == GradePreset.Auto,
                    Style     = (Style)FindResource("PresetBtn"),
                };
                AutomationProperties.SetName(btn,
                    $"Preset {label}: {ColorGradingEngine.PresetDescriptions[preset]}");

                var p = preset; // closure capture
                btn.Checked += (_, _) =>
                {
                    _selected = p;
                    TxtPresetDesc.Text = ColorGradingEngine.PresetDescriptions[p];
                    // Uncheck ostale
                    foreach (var (_, b) in _presetBtns)
                        if (b != btn) b.IsChecked = false;
                };
                btn.Unchecked += (_, _) =>
                {
                    // Ne dozvoli da nijedan bude unchecked — vrati Auto
                    bool anyChecked = _presetBtns.Values.Any(b => b.IsChecked == true);
                    if (!anyChecked)
                    {
                        _presetBtns[GradePreset.Auto].IsChecked = true;
                        _selected = GradePreset.Auto;
                    }
                };

                _presetBtns[preset] = btn;
                PresetPanel.Children.Add(btn);
            }
        }

        // ── Analiza ───────────────────────────────────────────────────

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_running) { _cts?.Cancel(); return; }
            if (_items.Count == 0)
            {
                MessageBox.Show("No video clips on the timeline.",
                    "Color Grading", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _running              = true;
            _gradeResults         = null;
            ClipsPanel.Children.Clear();
            BtnApply.IsEnabled    = false;
            BtnSelectAll.IsEnabled   = false;
            BtnDeselectAll.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Value        = 0;
            BtnAnalyze.Content       = "⏹ Cancel";
            _cts = new CancellationTokenSource();

            var progress = new Progress<(int Percent, string Message)>(p =>
            {
                if (!Dispatcher.CheckAccess())
                { Dispatcher.Invoke(() => SetProgress(p.Percent, p.Message)); return; }
                SetProgress(p.Percent, p.Message);
            });

            try
            {
                _gradeResults = await ColorGradingEngine.AnalyzeAndGradeAsync(
                    _items, _selected, progress, _cts.Token);

                PopulateClips(_gradeResults);
                BtnApply.IsEnabled       = true;
                BtnSelectAll.IsEnabled   = true;
                BtnDeselectAll.IsEnabled = true;
                TxtClipCount.Text = $"{_gradeResults.Count} clips  ·  preset: {_selected}";
            }
            catch (OperationCanceledException)
            {
                SetProgress(0, "Cancelled.");
                TxtClipCount.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Color Grading",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running = false;
                _cts?.Dispose(); _cts = null;
                BtnAnalyze.Content       = "🎨 Analyze and grade";
                BtnAnalyze.IsEnabled     = true;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        // ── Prikaz klipoiva ───────────────────────────────────────────

        private void PopulateClips(List<ClipGradeResult> results)
        {
            ClipsPanel.Children.Clear();
            foreach (var r in results)
                ClipsPanel.Children.Add(BuildClipCard(r));
        }

        private Border BuildClipCard(ClipGradeResult r)
        {
            var card = new Border
            {
                Width        = 200,
                Background   = ThemeBrushes.PanelBg2,
                CornerRadius = new CornerRadius(8),
                Margin       = new Thickness(0, 0, 8, 8),
                Padding      = new Thickness(8),
            };

            var sp = new StackPanel();

            // Thumbnail
            var imgBorder = new Border
            {
                Width        = 184,
                Height       = 103,
                CornerRadius = new CornerRadius(5),
                Background   = ThemeBrushes.PanelBg,
                Margin       = new Thickness(0, 0, 0, 6),
            };

            if (!string.IsNullOrEmpty(r.PreviewPath) && File.Exists(r.PreviewPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(r.PreviewPath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    imgBorder.Child = new Image
                    {
                        Source  = bmp,
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                    };
                }
                catch { }
            }

            // Grade badge overlay
            var imgGrid = new Grid();
            imgGrid.Children.Add(imgBorder);
            var badge = new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(200, 245, 158, 11)),
                CornerRadius      = new CornerRadius(4),
                Padding           = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment   = System.Windows.VerticalAlignment.Bottom,
                Margin            = new Thickness(0, 0, 4, 4),
            };
            badge.Child = new TextBlock
            {
                Text       = r.Grade.AppliedPreset.ToString(),
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(26, 26, 46)),
            };
            imgGrid.Children.Add(badge);
            sp.Children.Add(imgGrid);

            // Checkbox + naziv
            var chkLabel = new TextBlock
            {
                Text         = r.Item.Name ?? System.IO.Path.GetFileNameWithoutExtension(r.Item.Path),
                TextWrapping = TextWrapping.Wrap,
                FontSize     = 11,
                Foreground   = ThemeBrushes.TextPrimary,
            };
            var chk = new CheckBox
            {
                IsChecked = true,
                Content   = chkLabel,
                Margin    = new Thickness(0, 0, 0, 3),
            };
            AutomationProperties.SetName(chk,
                $"{r.Item.Name}, preset {r.Grade.AppliedPreset}");
            chk.Checked   += (_, _) => { r.Selected = true;  UpdateApplyButton(); };
            chk.Unchecked += (_, _) => { r.Selected = false; UpdateApplyButton(); };
            sp.Children.Add(chk);

            // Opis preseta
            sp.Children.Add(new TextBlock
            {
                Text         = r.Grade.Description,
                Foreground   = ThemeBrushes.TextSecondary,
                FontSize     = 10,
                TextWrapping = TextWrapping.Wrap,
            });

            card.Child = sp;
            AutomationProperties.SetName(card,
                $"Clip {r.Item.Name}, grade preset {r.Grade.AppliedPreset}");
            return card;
        }

        private void UpdateApplyButton()
        {
            if (_gradeResults == null) return;
            BtnApply.IsEnabled = _gradeResults.Any(g => g.Selected && g.Grade.Success);
        }

        // ── Select / Deselect ─────────────────────────────────────────

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
            => SetAllChecked(true);

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
            => SetAllChecked(false);

        private void SetAllChecked(bool val)
        {
            foreach (UIElement child in ClipsPanel.Children)
                if (child is Border b && b.Child is StackPanel sp)
                    foreach (UIElement item in sp.Children)
                        if (item is CheckBox chk)
                            chk.IsChecked = val;
        }

        // ── Primeni ───────────────────────────────────────────────────

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_gradeResults == null) return;
            int count = _gradeResults.Count(g => g.Selected && g.Grade.Success);
            if (count == 0) return;

            var confirm = MessageBox.Show(
                $"Apply color grade to {count} clips?\n\n" +
                "The grade filter is written into each clip's ContentTag\n" +
                "and is applied at the next render.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            ColorGradingEngine.ApplyGradesToItems(_gradeResults);

            MessageBox.Show(
                $"Color grade applied to {count} clips.\n" +
                "Run the render to see the result.",
                "Color Grading", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }

        // ── Helpers ───────────────────────────────────────────────────

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
