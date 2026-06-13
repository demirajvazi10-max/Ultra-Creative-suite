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

// Eksplicitni aliasi — razrešavaju konflikte System.Drawing / System.Windows.Forms
using Brush            = System.Windows.Media.Brush;
using Brushes          = System.Windows.Media.Brushes;
using Color            = System.Windows.Media.Color;
using ColorConverter   = System.Windows.Media.ColorConverter;
using SolidColorBrush  = System.Windows.Media.SolidColorBrush;
using Image            = System.Windows.Controls.Image;
using CheckBox         = System.Windows.Controls.CheckBox;
using Orientation      = System.Windows.Controls.Orientation;
using Clipboard        = System.Windows.Clipboard;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using MessageBox       = System.Windows.MessageBox;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;

namespace UltraVideoEditor
{
    /// <summary>
    /// Code-behind za HighlightCreatorDialog v2.
    /// 
    /// Faza 2 / A — Render pipeline (BtnRender_Click)
    /// Faza 2 / B — Preview panel sa thumbnailovima i per-segment toggle
    /// Faza 2 / C — Arc scoring prikazan u preview kartama
    /// </summary>
    public partial class HighlightCreatorDialogV2 : Window
    {
        // ── State ─────────────────────────────────────────────────────
        private HighlightResult           _result;
        private CancellationTokenSource   _cts;
        private bool                      _analyzing  = false;
        private bool                      _rendering  = false;

        /// <summary>
        /// Lista segmenata koje je korisnik uključio u preview panelu.
        /// Ažurira se svaki put kad korisnik toggle-uje checkbox.
        /// </summary>
        private readonly List<HighlightSegment> _activeSegments = new();

        /// <summary>
        /// Javni rezultat — pozivajući kod može da pročita segmente
        /// ako DialogResult == true.
        /// </summary>
        public List<HighlightSegment> ResultSegments =>
            new List<HighlightSegment>(_activeSegments);

        /// <summary>Kompletan HighlightResult za Fazu 3.</summary>
        public HighlightResult PublicResult => _result;

        // ── Konstruktor ───────────────────────────────────────────────
        public HighlightCreatorDialogV2()
        {
            InitializeComponent();

            // Default output path
            TxtOutputPath.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                $"highlight_{DateTime.Now:yyyyMMdd_HHmm}.mp4");
        }

        // ── Odabir fajlova ────────────────────────────────────────────

        private void BtnPickVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Odaberite izvorni video",
                Filter = "Video fajlovi|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv|Svi fajlovi|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                TxtVideoPath.Text       = dlg.FileName;
                TxtVideoPath.Foreground = FindBrush("TextPrimaryBrush");
                UpdateButtons();
            }
        }

        private void BtnPickMusic_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Odaberite pesmu",
                Filter = "Audio fajlovi|*.mp3;*.wav;*.m4a;*.flac;*.ogg;*.aac|Svi fajlovi|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                TxtMusicPath.Text       = dlg.FileName;
                TxtMusicPath.Foreground = FindBrush("TextPrimaryBrush");
                UpdateButtons();
            }
        }

        private void BtnPickOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title      = "Sačuvaj highlight video",
                Filter     = "MP4 video|*.mp4",
                FileName   = Path.GetFileName(TxtOutputPath.Text),
                InitialDirectory = Path.GetDirectoryName(TxtOutputPath.Text),
            };
            if (dlg.ShowDialog() == true)
                TxtOutputPath.Text = dlg.FileName;
        }

        private void ChkUseMusic_Changed(object sender, RoutedEventArgs e)
        {
            bool useMusic = ChkUseMusic.IsChecked == true;
            if (MusicPanel != null) MusicPanel.IsEnabled = useMusic;
            if (MusicPanel != null) MusicPanel.Opacity   = useMusic ? 1.0 : 0.4;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (TxtVideoPath == null || TxtMusicPath == null || BtnAnalyze == null) return;
            bool videoOk  = File.Exists(TxtVideoPath.Text);
            bool useMusic = ChkUseMusic?.IsChecked == true;
            bool musicOk  = !useMusic || File.Exists(TxtMusicPath.Text);
            bool ready    = videoOk && musicOk && !_analyzing && !_rendering;
            BtnAnalyze.IsEnabled = ready;
        }

        // ── Analiza ───────────────────────────────────────────────────

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_analyzing || _rendering) return;

            string videoPath = TxtVideoPath.Text;
            bool   useMusic  = ChkUseMusic?.IsChecked == true;
            string musicPath = useMusic ? TxtMusicPath.Text : null;

            if (!File.Exists(videoPath))
            {
                ShowError("Proverite putanju do video fajla.");
                return;
            }
            if (useMusic && !File.Exists(musicPath))
            {
                ShowError("Proverite putanju do audio fajla, ili isključite opciju muzike.");
                return;
            }

            // Reset UI
            _analyzing = true;
            _cts       = new CancellationTokenSource();
            _result    = null;
            _activeSegments.Clear();
            SegmentsPanel.Children.Clear();
            TxtSegmentCount.Text         = "Analizira se…";
            TxtReport.Text               = "";
            BtnApplyToTimeline.IsEnabled = false;
            BtnRender.IsEnabled          = false;
            BtnSelectAll.IsEnabled       = false;
            BtnDeselectAll.IsEnabled     = false;
            BtnCopyReport.Visibility     = Visibility.Collapsed;
            BtnExport.Visibility         = Visibility.Collapsed;
            ProgressPanel.Visibility     = Visibility.Visible;
            ProgressBar.Value            = 0;

            SetAnalyzeButtonToCancel();

            var progress = new Progress<(int Percent, string Message)>(OnProgress);

            try
            {
                _result = await AIHighlightEngine.AnalyzeAsync(
                    videoPath, musicPath, progress, _cts.Token); // musicPath may be null (video-only)

                if (_result.Success)
                {
                    // Popuni preview panel
                    PopulatePreviewPanel(_result.Segments);

                    // Izveštaj
                    TxtReport.Text           = _result.Report;
                    BtnCopyReport.Visibility = Visibility.Visible;
                    BtnExport.Visibility     = Visibility.Visible;

                    // Aktiviraj dugmiće
                    BtnApplyToTimeline.IsEnabled = true;
                    BtnRender.IsEnabled          = true;
                    BtnSelectAll.IsEnabled       = true;
                    BtnDeselectAll.IsEnabled     = true;

                    // Accessibility: najavi rezultat
                    AutomationProperties.SetName(TxtReport,
                        $"Analiza završena. Selektovano {_result.Segments.Count} segmenata, " +
                        $"ukupno {AIHighlightEngine.FormatTime(_result.TotalDuration)}. " +
                        "Proverite Preview tab za detalje.");
                }
                else
                {
                    ShowError(_result.Error ?? "Analiza nije uspela.");
                    TxtSegmentCount.Text = "Analiza neuspešna.";
                }
            }
            catch (OperationCanceledException)
            {
                TxtReport.Text       = "Analiza je otkazana.";
                TxtSegmentCount.Text = "Otkazano.";
                SetProgress(0, "Otkazano.");
            }
            catch (Exception ex)
            {
                ShowError($"Neočekivana greška: {ex.Message}");
            }
            finally
            {
                _analyzing = false;
                _cts?.Dispose();
                _cts = null;
                SetAnalyzeButtonToAnalyze();
                UpdateButtons();
            }
        }

        private void BtnCancelAnalysis_Click(object sender, RoutedEventArgs e)
            => _cts?.Cancel();

        // ── Faza 2 / B: Preview panel ─────────────────────────────────

        /// <summary>
        /// Dinamički gradi listu kartica za svaki segment.
        /// Svaka kartica ima: thumbnail, checkbox, trajanje, skor, arc opis, frame score bar.
        /// </summary>
        private void PopulatePreviewPanel(List<HighlightSegment> segments)
        {
            SegmentsPanel.Children.Clear();
            _activeSegments.Clear();

            int enabledCount = 0;

            foreach (var seg in segments)
            {
                // Sve segmente uključujemo po defaultu
                _activeSegments.Add(seg);

                var card = BuildSegmentCard(seg, isChecked: true, onToggle: (isOn) =>
                {
                    if (isOn)  { if (!_activeSegments.Contains(seg)) _activeSegments.Add(seg); }
                    else       { _activeSegments.Remove(seg); }
                    UpdateSegmentCount();
                    UpdateRenderButtonState();
                });

                SegmentsPanel.Children.Add(card);
                enabledCount++;
            }

            UpdateSegmentCount();
        }

        /// <summary>
        /// Gradi jednu karticu segmenta za preview panel.
        /// </summary>
        private UIElement BuildSegmentCard(
            HighlightSegment seg,
            bool             isChecked,
            Action<bool>     onToggle)
        {
            // Outer border
            var card = new Border
            {
                Background      = FindBrush("CardBrush"),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(10),
                Margin          = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(1),
                BorderBrush     = FindBrush("BorderBrush"),
            };

            // Accessibility
            AutomationProperties.SetName(card,
                $"Segment {seg.Order}, {AIHighlightEngine.FormatTime(seg.Duration)}, " +
                $"skor {seg.ImportanceScore:F0}. " +
                $"{seg.ContentDescription}. Arc: {seg.ArcDescription}.");

            // Layout: thumbnail levo, info desno
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.Child = grid;

            // ── Thumbnail ─────────────────────────────────────────────
            var thumbBorder = new Border
            {
                Width           = 88,
                Height          = 50,
                CornerRadius    = new CornerRadius(5),
                Background      = new SolidColorBrush(Color.FromRgb(15, 52, 96)),
                Margin          = new Thickness(0, 0, 10, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                ClipToBounds    = true,
            };

            if (!string.IsNullOrEmpty(seg.ThumbnailPath) && File.Exists(seg.ThumbnailPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource        = new Uri(seg.ThumbnailPath);
                    bmp.CacheOption      = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 160;
                    bmp.EndInit();
                    bmp.Freeze();

                    thumbBorder.Child = new Image
                    {
                        Source  = bmp,
                        Stretch = Stretch.UniformToFill,
                    };
                }
                catch
                {
                    thumbBorder.Child = new TextBlock
                    {
                        Text = "🎬",
                        FontSize = 22,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Foreground = FindBrush("TextMutedBrush"),
                    };
                }
            }
            else
            {
                thumbBorder.Child = new TextBlock
                {
                    Text = "🎬",
                    FontSize = 22,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Foreground = FindBrush("TextMutedBrush"),
                };
            }

            // Redni broj overlay na thumbnail
            var numOverlay = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(180, 124, 58, 237)),
                CornerRadius    = new CornerRadius(4, 0, 4, 0),
                Padding         = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text       = $"#{seg.Order:D2}",
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                },
            };

            var thumbGrid = new Grid();
            thumbGrid.Children.Add(thumbBorder);
            thumbGrid.Children.Add(numOverlay);

            Grid.SetColumn(thumbGrid, 0);
            grid.Children.Add(thumbGrid);

            // ── Info panel ────────────────────────────────────────────
            var infoPanel = new StackPanel { Margin = new Thickness(0) };
            Grid.SetColumn(infoPanel, 1);
            grid.Children.Add(infoPanel);

            // Header: checkbox + trajanje + skor
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            var chk = new CheckBox
            {
                IsChecked  = isChecked,
                Foreground = FindBrush("TextPrimaryBrush"),
                FontSize   = 13,
                FontWeight = FontWeights.SemiBold,
                Content    = $"{AIHighlightEngine.FormatTime(seg.SourceStart)} → " +
                             $"{AIHighlightEngine.FormatTime(seg.SourceEnd)}  " +
                             $"({seg.Duration:F2}s)",
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            chk.Checked   += (_, _) => onToggle(true);
            chk.Unchecked += (_, _) => onToggle(false);
            AutomationProperties.SetName(chk,
                $"Uključi segment {seg.Order}, " +
                $"trajanje {seg.Duration:F1} sekundi");

            // Skor badge
            string scoreColor = seg.ImportanceScore >= 70 ? "#10B981"
                              : seg.ImportanceScore >= 40 ? "#F59E0B"
                              : "#EF4444";
            var scoreBadge = new Border
            {
                Background   = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(scoreColor)),
                CornerRadius = new CornerRadius(10),
                Padding      = new Thickness(7, 2, 7, 2),
                
                Child = new TextBlock
                {
                    Text       = $"{seg.ImportanceScore:F0}",
                    FontSize   = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                },
            };
            AutomationProperties.SetName(scoreBadge, $"Skor važnosti: {seg.ImportanceScore:F0} od 100");

            header.Children.Add(scoreBadge);
            header.Children.Add(chk);
            infoPanel.Children.Add(header);

            // Sadržaj
            if (!string.IsNullOrEmpty(seg.ContentDescription))
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text       = $"🏷  {seg.ContentDescription}",
                    FontSize   = 11,
                    Foreground = FindBrush("TextMutedBrush"),
                    Margin     = new Thickness(0, 0, 0, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            // Arc opis (Faza 2 / C)
            if (!string.IsNullOrEmpty(seg.ArcDescription) && seg.ArcDescription != "bez izraženog arc-a")
            {
                var arcBorder = new Border
                {
                    Background   = new SolidColorBrush(Color.FromArgb(40, 124, 58, 237)),
                    CornerRadius = new CornerRadius(4),
                    Padding      = new Thickness(6, 2, 6, 2),
                    Margin       = new Thickness(0, 0, 0, 4),
                    Child = new TextBlock
                    {
                        Text       = $"Arc: {seg.ArcDescription}",
                        FontSize   = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                AutomationProperties.SetName(arcBorder, $"Arc razvoj kadra: {seg.ArcDescription}");
                infoPanel.Children.Add(arcBorder);
            }

            // Frame score mini-bar (Faza 2 / C)
            if (seg.FrameScores?.Count > 0)
            {
                var barPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 2, 0, 0),
                };
                AutomationProperties.SetName(barPanel,
                    $"Frejmovi: {string.Join(", ", seg.FrameScores.Select(s => $"{s:F0}"))}");

                foreach (double fs in seg.FrameScores)
                {
                    double heightPct = Math.Clamp(fs / 75.0, 0.05, 1.0);
                    string barColor  = fs >= 50 ? "#10B981" : fs >= 25 ? "#F59E0B" : "#EF4444";

                    barPanel.Children.Add(new Border
                    {
                        Width        = 10,
                        Height       = Math.Round(14 * heightPct + 3),
                        Margin       = new Thickness(1, 0, 1, 0),
                        CornerRadius = new CornerRadius(2),
                        Background   = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(barColor)),
                        VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                        ToolTip           = $"{fs:F0}/75",
                    });
                }

                // Legenda
                barPanel.Children.Add(new TextBlock
                {
                    Text       = $"  {ARC_FRAME_LABEL}",
                    FontSize   = 10,
                    Foreground = FindBrush("TextMutedBrush"),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                });

                infoPanel.Children.Add(barPanel);
            }

            // Beat info
            if (seg.BeatTimestamp > 0.0)
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text       = $"🎵 Beat rez: {AIHighlightEngine.FormatTime(seg.BeatTimestamp)}",
                    FontSize   = 10,
                    Foreground = FindBrush("TextMutedBrush"),
                    Margin     = new Thickness(0, 3, 0, 0),
                });
            }

            return card;
        }

        private const string ARC_FRAME_LABEL = "5 frejmova";

        private void UpdateSegmentCount()
        {
            int total   = _result?.Segments.Count ?? 0;
            int active  = _activeSegments.Count;
            double dur  = _activeSegments.Sum(s => s.Duration);
            TxtSegmentCount.Text = total == 0
                ? "Nema segmenata"
                : $"{active}/{total} segmenata uključeno  ·  {AIHighlightEngine.FormatTime(dur)}";
        }

        private void UpdateRenderButtonState()
        {
            BtnRender.IsEnabled          = _activeSegments.Count > 0 && !_rendering;
            BtnApplyToTimeline.IsEnabled = _activeSegments.Count > 0 && !_rendering;
        }

        // ── Select/Deselect all ───────────────────────────────────────

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
            => SetAllCheckboxes(true);

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
            => SetAllCheckboxes(false);

        private void SetAllCheckboxes(bool value)
        {
            foreach (var child in SegmentsPanel.Children)
            {
                if (child is Border card && card.Child is Grid g)
                {
                    foreach (var col in g.Children)
                    {
                        if (col is StackPanel sp)
                        {
                            foreach (var item in sp.Children)
                            {
                                if (item is DockPanel dp)
                                {
                                    foreach (var dpChild in dp.Children)
                                    {
                                        if (dpChild is CheckBox chk)
                                            chk.IsChecked = value;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // ── Faza 2 / A: Render ───────────────────────────────────────

        private async void BtnRender_Click(object sender, RoutedEventArgs e)
        {
            if (_rendering || _activeSegments.Count == 0) return;

            bool   useMusic   = ChkUseMusic?.IsChecked == true;
            string musicPath  = useMusic ? TxtMusicPath.Text : null;
            string outputPath = TxtOutputPath.Text.Trim();

            if (useMusic && !File.Exists(musicPath))
            {
                ShowError("Audio fajl nije pronađen. Proverite putanju.");
                return;
            }
            if (string.IsNullOrEmpty(outputPath))
            {
                ShowError("Odaberite putanju za output video.");
                return;
            }

            // Rezolucija
            string resolution = "1920x1080";
            if (CmbResolution.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
                resolution = tag;

            bool useGPU = ChkUseGPU.IsChecked == true;

            // Potvrda ako fajl postoji
            if (File.Exists(outputPath))
            {
                var confirm = MessageBox.Show(
                    $"Fajl već postoji:\n{outputPath}\n\nPrepiši ga?",
                    "Potvrda", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            _rendering = true;
            _cts       = new CancellationTokenSource();

            BtnRender.IsEnabled          = false;
            BtnAnalyze.IsEnabled         = false;
            BtnApplyToTimeline.IsEnabled = false;
            ProgressPanel.Visibility     = Visibility.Visible;
            ProgressBar.Value            = 0;

            // Privremeni HighlightResult samo sa aktivnim segmentima
            var renderResult = new HighlightResult
            {
                Segments      = new List<HighlightSegment>(_activeSegments),
                TargetDuration = _result?.TargetDuration ?? _activeSegments.Sum(s => s.Duration),
                Beats          = _result?.Beats,
                BPM            = _result?.BPM ?? 0,
            };

            var progress = new Progress<(int Percent, string Message)>(OnProgress);

            try
            {
                await HighlightRenderer.RenderAsync(
                    result     : renderResult,
                    musicPath  : musicPath,
                    outputPath : outputPath,
                    resolution : resolution,
                    useGPU     : useGPU,
                    progress   : progress,
                    ct         : _cts.Token);

                MessageBox.Show(
                    $"✅ Video uspešno renderovan!\n\n{outputPath}\n\n" +
                    $"Trajanje: {AIHighlightEngine.FormatTime(renderResult.TotalDuration)}\n" +
                    $"Segmenata: {renderResult.Segments.Count}\n" +
                    $"Rezolucija: {resolution}",
                    "Render završen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                SetProgress(0, "Render otkazan.");
                MessageBox.Show("Render je otkazan.", "Otkazano",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ShowError($"Greška tokom rendera: {ex.Message}");
            }
            finally
            {
                _rendering = false;
                _cts?.Dispose();
                _cts = null;
                UpdateRenderButtonState();
                UpdateButtons();
            }
        }

        // ── Dodaj na timeline ─────────────────────────────────────────

        private void BtnApplyToTimeline_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSegments.Count == 0)
            {
                ShowError("Nema selektovanih segmenata.");
                return;
            }

            var parent = Owner;
            if (parent is MainWindow mainWindow)
            {
                double cursor = 0.0;
                int added = 0;
                foreach (var seg in _activeSegments.OrderBy(s => s.Order))
                {
                    var item = new TimelineItem
                    {
                        Path             = seg.SourcePath,
                        Start            = seg.SourceStart,
                        End              = seg.SourceEnd,
                        Duration         = seg.Duration,
                        Name             = $"Highlight {seg.Order:D2}",
                        Type             = "Highlight",
                        TrackIndex       = 0,
                        FixedPosition    = cursor,
                        UseFixedPosition = true,
                        AccessibilityDescription =
                            $"Highlight {seg.Order}, {seg.ContentDescription}, " +
                            $"{seg.Duration:F1}s",
                    };
                    // AddTimelineItemExternal nije definisan -- koristimo DialogResult fallback
                    added = 0;
                    break;
                }

                if (added > 0)
                {
                    MessageBox.Show(
                        $"Dodato {added} segmenata na timeline.",
                        "Timeline", MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                    return;
                }
            }

            // Fallback: vrati kroz DialogResult
            DialogResult = true;
            Close();
        }

        // ── Izveštaj ─────────────────────────────────────────────────

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtReport.Text)) return;
            Clipboard.SetText(TxtReport.Text);
            string orig = BtnCopyReport.Content?.ToString() ?? "";
            BtnCopyReport.Content = "✓ Kopirano!";
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => { BtnCopyReport.Content = orig; t.Stop(); };
            t.Start();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtReport.Text)) return;
            var dlg = new SaveFileDialog
            {
                Title    = "Sačuvaj izveštaj",
                Filter   = "Tekstualni fajl|*.txt",
                FileName = $"highlight_report_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, TxtReport.Text, System.Text.Encoding.UTF8);
                    MessageBox.Show($"Sačuvano:\n{dlg.FileName}", "Sačuvano",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { ShowError($"Greška: {ex.Message}"); }
            }
        }

        // ── Progress ──────────────────────────────────────────────────

        private void OnProgress((int Percent, string Message) p)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnProgress(p));
                return;
            }
            SetProgress(p.Percent, p.Message);
        }

        private void SetProgress(int pct, string msg)
        {
            ProgressBar.Value   = pct;
            TxtProgressPct.Text = $"{pct}%";
            TxtProgressMsg.Text = msg;
        }

        // ── Zatvori ───────────────────────────────────────────────────

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_analyzing || _rendering)
            {
                var c = MessageBox.Show(
                    "Operacija je u toku. Otkaži i zatvori?",
                    "Potvrda", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (c != MessageBoxResult.Yes) return;
                _cts?.Cancel();
            }
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            base.OnClosed(e);
        }

        // ── Helpers ───────────────────────────────────────────────────

        private void SetAnalyzeButtonToCancel()
        {
            BtnAnalyze.Content =  "⏹ Otkaži";
            BtnAnalyze.Click  -= BtnAnalyze_Click;
            BtnAnalyze.Click  += BtnCancelAnalysis_Click;
            BtnAnalyze.IsEnabled = true;
        }

        private void SetAnalyzeButtonToAnalyze()
        {
            BtnAnalyze.Content =  "🔍 Analiziraj video";
            BtnAnalyze.Click  -= BtnCancelAnalysis_Click;
            BtnAnalyze.Click  += BtnAnalyze_Click;
        }

        private void ShowError(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ShowError(message));
                return;
            }
            TxtReport.Text           = $"⚠️  {message}";
            ProgressPanel.Visibility = Visibility.Collapsed;
            MessageBox.Show(message, "AI Highlight Engine — Greška",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private Brush FindBrush(string key)
        {
            return TryFindResource(key) as Brush
                ?? Brushes.White;
        }
    }
}
