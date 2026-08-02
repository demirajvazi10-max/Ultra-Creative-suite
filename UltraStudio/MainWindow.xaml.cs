using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UltraStudio.Localization;
using UltraStudio.Models;
using UltraStudio.Services;
using UltraStudio.Views;

using WF = System.Windows.Forms;

namespace UltraStudio
{
    /// <summary>
    /// Jedan red u listi podešavanja. Boolean = true za on/off stavke
    /// (Grayscale/Sepia/Flip), false za numeričke (Brightness/Contrast/itd.)
    /// </summary>
    public class AdjustmentRow
    {
        public string Key { get; set; } = "";       // Lang.T ključ za naziv
        public bool IsBoolean { get; set; }
        public Func<ImageProject, double> GetNum { get; set; } = _ => 0;
        public Action<ImageProject, double> SetNum { get; set; } = (_, __) => { };
        public Func<ImageProject, bool> GetBool { get; set; } = _ => false;
        public Action<ImageProject, bool> SetBool { get; set; } = (_, __) => { };
        public double Min, Max, Step;
        public string Unit { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        private readonly ImageProject _project = new();
        private readonly OllamaVisionClient _ai = new();
        private List<AdjustmentRow> _rows = new();
        private string? _lastSavePath;
        private bool _isVisualMode;
        private readonly Dictionary<AdjustmentRow, Slider> _sliderByRow = new();
        private readonly Dictionary<AdjustmentRow, CheckBox> _checkByRow = new();

        public MainWindow()
        {
            InitializeComponent();
            Lang.ApplyToResources();
            BuildAdjustmentRows();
            SetupNativeList();
            BuildVisualPanel();
            SetJawsMode(); // podrazumevani mod — isti izbor kao ostatak Ultra paketa
            UpdateImageInfo();
            SetStatus(Lang.T("statusbar_ready"));
        }

        // ════════════════════════════════════════════════════════════════
        // RED DEFINICIJE
        // ════════════════════════════════════════════════════════════════
        private void BuildAdjustmentRows()
        {
            _rows = new List<AdjustmentRow>
            {
                new() { Key = "adj_brightness", GetNum = p => p.Brightness, SetNum = (p, v) => p.Brightness = v, Min = -100, Max = 100, Step = 5 },
                new() { Key = "adj_contrast",   GetNum = p => p.Contrast,   SetNum = (p, v) => p.Contrast = v,   Min = -100, Max = 100, Step = 5 },
                new() { Key = "adj_saturation", GetNum = p => p.Saturation, SetNum = (p, v) => p.Saturation = v, Min = -100, Max = 100, Step = 5 },
                new() { Key = "adj_sharpen",    GetNum = p => p.Sharpen,    SetNum = (p, v) => p.Sharpen = v,    Min = 0, Max = 10, Step = 0.5 },
                new() { Key = "adj_blur",       GetNum = p => p.Blur,       SetNum = (p, v) => p.Blur = v,       Min = 0, Max = 10, Step = 0.5 },
                new() { Key = "adj_rotate",     GetNum = p => p.Rotate,     SetNum = (p, v) => p.Rotate = v,     Min = -180, Max = 180, Step = 5, Unit = "°" },
                new() { Key = "adj_grayscale",  IsBoolean = true, GetBool = p => p.Grayscale,      SetBool = (p, v) => p.Grayscale = v },
                new() { Key = "adj_sepia",      IsBoolean = true, GetBool = p => p.Sepia,          SetBool = (p, v) => p.Sepia = v },
                new() { Key = "adj_flip_h",     IsBoolean = true, GetBool = p => p.FlipHorizontal, SetBool = (p, v) => p.FlipHorizontal = v },
                new() { Key = "adj_flip_v",     IsBoolean = true, GetBool = p => p.FlipVertical,   SetBool = (p, v) => p.FlipVertical = v },
            };
        }

        // ════════════════════════════════════════════════════════════════
        // NATIVNA WIN32 LISTVIEW — isti obrazac kao Audio Editor
        // ════════════════════════════════════════════════════════════════
        private void SetupNativeList()
        {
            nativeAdjustList.Columns.Clear();
            nativeAdjustList.Columns.Add(Lang.T("col_adjustment"), 200, WF.HorizontalAlignment.Left);
            nativeAdjustList.Columns.Add(Lang.T("col_value"), 140, WF.HorizontalAlignment.Left);

            nativeAdjustList.BackColor = System.Drawing.Color.FromArgb(20, 20, 34);
            nativeAdjustList.ForeColor = System.Drawing.Color.White;
            nativeAdjustList.Font = new System.Drawing.Font("Segoe UI", 10);
            nativeAdjustList.AccessibleName = Lang.T("acc_list_help");

            nativeAdjustList.KeyDown += NativeAdjustList_KeyDown;
            RefreshList();
        }

        private void RefreshList()
        {
            nativeAdjustList.BeginUpdate();
            int selectedIndex = nativeAdjustList.SelectedIndices.Count > 0 ? nativeAdjustList.SelectedIndices[0] : 0;
            nativeAdjustList.Items.Clear();
            foreach (var row in _rows)
            {
                string valueText = row.IsBoolean
                    ? (row.GetBool(_project) ? "On" : "Off")
                    : $"{row.GetNum(_project):0.#}{row.Unit}";
                var lvi = new WF.ListViewItem(new[] { Lang.T(row.Key), valueText }) { Tag = row };
                nativeAdjustList.Items.Add(lvi);
            }
            nativeAdjustList.EndUpdate();

            if (nativeAdjustList.Items.Count > 0)
            {
                int idx = Math.Min(selectedIndex, nativeAdjustList.Items.Count - 1);
                nativeAdjustList.Items[idx].Selected = true;
            }

            // Isti izvor istine (ImageProject) za oba prikaza — kad god se JAWS
            // lista osveži, prepiši i slidere/checkbox-ove (ako postoje — konstruktor
            // zove RefreshList() pre BuildVisualPanel() prvi put, pa je ovo bezbedno).
            if (_sliderByRow != null && _sliderByRow.Count > 0) SyncVisualPanelFromProject();
        }

        // ════════════════════════════════════════════════════════════════
        // VIZUELNI MOD — pravi WPF slideri/checkbox-ovi za sighted korisnike,
        // isti obrazac kao Ultra Audio Editor (Alt+W). Grade se iz ISTIH _rows
        // definicija kao JAWS lista — nema duplirane logike, samo drugi prikaz.
        // ════════════════════════════════════════════════════════════════
        private void BuildVisualPanel()
        {
            VisualAdjustStack.Children.Clear();
            _sliderByRow.Clear();
            _checkByRow.Clear();

            foreach (var row in _rows)
            {
                var container = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

                if (row.IsBoolean)
                {
                    var check = new CheckBox
                    {
                        Content = Lang.T(row.Key),
                        Foreground = (System.Windows.Media.Brush)Resources["BrText"],
                        FontSize = 13
                    };
                    check.Checked += (_, __) => { row.SetBool(_project, true); RefreshList(); RefreshPreview(); };
                    check.Unchecked += (_, __) => { row.SetBool(_project, false); RefreshList(); RefreshPreview(); };
                    _checkByRow[row] = check;
                    container.Children.Add(check);
                }
                else
                {
                    var header = new Grid();
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = new TextBlock { Text = Lang.T(row.Key), Foreground = (System.Windows.Media.Brush)Resources["BrText"], FontSize = 13 };
                    var valueLabel = new TextBlock { Foreground = (System.Windows.Media.Brush)Resources["BrTextMuted"], FontSize = 12 };
                    Grid.SetColumn(label, 0); Grid.SetColumn(valueLabel, 1);
                    header.Children.Add(label); header.Children.Add(valueLabel);
                    container.Children.Add(header);

                    var slider = new Slider
                    {
                        Minimum = row.Min, Maximum = row.Max, SmallChange = row.Step, LargeChange = row.Step * 4,
                        IsSnapToTickEnabled = false, Margin = new Thickness(0, 4, 0, 0)
                    };
                    slider.SetValue(AutomationProperties.NameProperty, Lang.T(row.Key));
                    slider.ValueChanged += (_, e) =>
                    {
                        row.SetNum(_project, e.NewValue);
                        valueLabel.Text = $"{e.NewValue:0.#}{row.Unit}";
                        RefreshList();
                        RefreshPreview();
                    };
                    _sliderByRow[row] = slider;
                    container.Children.Add(slider);
                }
                VisualAdjustStack.Children.Add(container);
            }
        }

        // Prepiše trenutne vrednosti iz _project u slidere/checkbox-ove BEZ
        // ponovnog pokretanja ImageEngine obrade za svaku stavku pojedinačno
        // (obrada se svakako radi jednom preko RefreshPreview posle).
        private void SyncVisualPanelFromProject()
        {
            foreach (var kv in _sliderByRow) kv.Value.Value = kv.Key.GetNum(_project);
            foreach (var kv in _checkByRow) kv.Value.IsChecked = kv.Key.GetBool(_project);
        }

        private void SetVisualMode()
        {
            _isVisualMode = true;
            SyncVisualPanelFromProject();
            wfhAdjustments.Visibility = Visibility.Collapsed;
            VisualAdjustPanel.Visibility = Visibility.Visible;
            CurrentModeLabel.Text = Lang.T("visual_mode_indicator");
        }

        private void SetJawsMode()
        {
            _isVisualMode = false;
            RefreshList();
            VisualAdjustPanel.Visibility = Visibility.Collapsed;
            wfhAdjustments.Visibility = Visibility.Visible;
            CurrentModeLabel.Text = Lang.T("jaws_mode_indicator");
        }

        private void BtnVisualMode_Click(object sender, RoutedEventArgs e) => SetVisualMode();
        private void BtnJawsMode_Click(object sender, RoutedEventArgs e) => SetJawsMode();
        private void NativeAdjustList_KeyDown(object? sender, WF.KeyEventArgs e)
        {
            if (nativeAdjustList.SelectedItems.Count == 0) return;
            var row = (AdjustmentRow)nativeAdjustList.SelectedItems[0].Tag!;

            if (e.KeyCode == WF.Keys.Enter || e.KeyCode == WF.Keys.F2)
            {
                if (row.IsBoolean)
                {
                    row.SetBool(_project, !row.GetBool(_project));
                    RefreshList(); RefreshPreview();
                }
                else
                {
                    EditNumericRow(row);
                }
                e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Space && row.IsBoolean)
            {
                row.SetBool(_project, !row.GetBool(_project));
                RefreshList(); RefreshPreview();
                e.Handled = true;
            }
        }

        private void EditNumericRow(AdjustmentRow row)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }

            var dlg = new SetValueDialog(Lang.T(row.Key), $"{Lang.T(row.Key)} ({row.Min} to {row.Max}{row.Unit}):",
                row.GetNum(_project).ToString("0.#"), row.Unit) { Owner = this };
            if (dlg.ShowDialog() == true &&
                double.TryParse(dlg.ResultValue.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
            {
                row.SetNum(_project, Math.Clamp(v, row.Min, row.Max));
                RefreshList();
                RefreshPreview();
            }
        }

        // ════════════════════════════════════════════════════════════════
        // OTVARANJE / ČUVANJE SLIKE
        // ════════════════════════════════════════════════════════════════
        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = Lang.T("menu_open_image"),
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var (w, h) = ImageEngine.GetDimensions(dlg.FileName);
                _project.OriginalPath = dlg.FileName;
                _project.OriginalWidth = w;
                _project.OriginalHeight = h;
                _project.ResetAdjustments();
                _lastSavePath = null;

                UpdateImageInfo();
                RefreshPreview();
                SetStatus(string.Format(Lang.T("img_loaded"), Path.GetFileName(dlg.FileName), w, h));
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Lang.T("error_prefix"), ex.Message), Lang.T("error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            if (_lastSavePath == null) { SaveAs_Click(sender, e); return; }
            DoExport(_lastSavePath);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            var dlg = new SaveFileDialog
            {
                Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp|TIFF|*.tiff",
                FileName = Path.GetFileNameWithoutExtension(_project.OriginalPath) + "_edited.png"
            };
            if (dlg.ShowDialog() != true) return;
            _lastSavePath = dlg.FileName;
            DoExport(dlg.FileName);
        }

        private void DoExport(string path)
        {
            try
            {
                ImageEngine.Export(_project.OriginalPath!, _project, path);
                SetStatus(string.Format(Lang.T("img_saved"), path));
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Lang.T("error_prefix"), ex.Message), Lang.T("error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (!_project.HasImage) return;
            _project.ResetAdjustments();
            RefreshList();
            RefreshPreview();
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(Lang.T("about_text"), Lang.T("about_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ════════════════════════════════════════════════════════════════
        // PREVIEW
        // ════════════════════════════════════════════════════════════════
        private void RefreshPreview()
        {
            if (!_project.HasImage) { TxtNoImage.Visibility = Visibility.Visible; ImgPreview.Source = null; return; }

            try
            {
                var bytes = ImageEngine.RenderPreviewJpeg(_project.OriginalPath!, _project);
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                ImgPreview.Source = bmp;
                TxtNoImage.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Lang.T("error_prefix"), ex.Message));
            }
        }

        private void UpdateImageInfo()
        {
            TxtImageInfo.Text = _project.HasImage
                ? $"{Path.GetFileName(_project.OriginalPath)} — {_project.OriginalWidth}x{_project.OriginalHeight}px"
                : "No image open";
        }

        // ════════════════════════════════════════════════════════════════
        // AI OPIS SLIKE + IZVRŠIVI PREDLOZI (dva ODVOJENA poziva — vidi
        // OllamaVisionClient: forsiranje JSON-a na opis ga je osiromašilo).
        // Svaki predlog ide kroz NATIVE MessageBox (Da/Ne) — pouzdanije za
        // JAWS nego custom panel koji smo probali (nedostajao mu je
        // AutomationProperties.Name + live region, isti bag klasa koju smo
        // lovili celo veče kod Audio Editora).
        // ════════════════════════════════════════════════════════════════
        private async void BtnDescribe_Click(object sender, RoutedEventArgs e)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }

            BtnDescribe.IsEnabled = false;
            SetAiResult(Lang.T("ai_describing"));

            try
            {
                // Šalje se TRENUTNI preview (sa primenjenim podešavanjima), ne
                // originalni fajl — AI opisuje ono što korisnik stvarno vidi/pravi.
                var bytes = ImageEngine.RenderPreviewJpeg(_project.OriginalPath!, _project, maxDimension: 1024);
                string base64 = Convert.ToBase64String(bytes);

                string description = await _ai.DescribeImageAsync(base64);
                SetAiResult(description);
                BtnDescribe.IsEnabled = true; // ponovo dostupno odmah — predlozi mogu potrajati

                var suggestions = await _ai.SuggestEditsAsync(base64);
                foreach (var sug in suggestions)
                {
                    var result = MessageBox.Show(
                        string.Format(Lang.T("ai_apply_suggestion"),
                            $"{sug.Action} {sug.Value:+0.#;-0.#}", sug.Reason),
                        Lang.T("ai_panel_header"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                        ApplySuggestion(sug);
                }
            }
            catch (Exception ex)
            {
                SetAiResult(string.Format(Lang.T("ai_error"), ex.Message));
            }
            finally
            {
                BtnDescribe.IsEnabled = true;
            }
        }

        // Mapira AI-jev predlog ("contrast", +15) na POSTOJEĆE kontrole u listi —
        // primenjuje se preko iste ImageProject/AdjustmentRow mašinerije kao ručna
        // izmena, ništa posebno se ne gradi samo za AI put.
        private void ApplySuggestion(ImageSuggestion sug)
        {
            var row = _rows.FirstOrDefault(r => r.Key == "adj_" + sug.Action);
            if (row == null) return;

            if (row.IsBoolean) row.SetBool(_project, true);
            else row.SetNum(_project, Math.Clamp(sug.Value, row.Min, row.Max));

            RefreshList();
            RefreshPreview();
            SetStatus(string.Format(Lang.T("ai_suggestion_applied"), Lang.T(row.Key)));
        }

        // ════════════════════════════════════════════════════════════════
        // SAM — IZDVAJANJE OBJEKTA IZ SLIKE
        // ════════════════════════════════════════════════════════════════
        private async void ExtractObject_Click(object sender, RoutedEventArgs e)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }

            if (!SamSegmenter.ModelsAvailable)
            {
                MessageBox.Show(
                    string.Format(Lang.T("sam_models_missing"), SamSegmenter.EncoderPath, SamSegmenter.DecoderPath),
                    Lang.T("error_title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SetValueDialog(Lang.T("extract_prompt_title"), Lang.T("extract_prompt"), "", "") { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultValue)) return;
            string description = dlg.ResultValue.Trim();

            try
            {
                SetStatus(Lang.T("extract_locating"));
                var bytes = ImageEngine.RenderPreviewJpeg(_project.OriginalPath!, _project, maxDimension: 1280);
                string base64 = Convert.ToBase64String(bytes);
                var point = await _ai.FindPointForDescriptionAsync(
                    base64, _project.OriginalWidth, _project.OriginalHeight, description);

                if (point == null)
                {
                    SetStatus(string.Format(Lang.T("extract_not_found"), description));
                    return;
                }

                SetStatus(Lang.T("extract_segmenting"));
                using var sam = new SamSegmenter();
                await sam.EnsureEmbeddingAsync(_project.OriginalPath!);
                var mask = await sam.SegmentFromPointAsync(point.Value.x, point.Value.y);

                string outPath = Path.Combine(
                    Path.GetDirectoryName(_project.OriginalPath!)!,
                    Path.GetFileNameWithoutExtension(_project.OriginalPath!) + $"_{description.Replace(' ', '_')}_cutout.png");
                SamSegmenter.ExportCutout(_project.OriginalPath!, mask, outPath);

                var open = MessageBox.Show(string.Format(Lang.T("extract_done"), outPath),
                    Lang.T("done_title"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                {
                    var (w, h) = ImageEngine.GetDimensions(outPath);
                    _project.OriginalPath = outPath;
                    _project.OriginalWidth = w;
                    _project.OriginalHeight = h;
                    _project.ResetAdjustments();
                    _lastSavePath = null;
                    UpdateImageInfo();
                    RefreshPreview();
                }
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Lang.T("error_prefix"), ex.Message));
            }
        }

        private void SetAiResult(string text)
        {
            TxtAiResult.Text = text;
            // Isti popravljen obrazac kao u Audio Editoru: Name mora da prati
            // STVARNI sadržaj, ne fiksnu etiketu, inače JAWS ponavlja isto.
            TxtAiResult.SetValue(AutomationProperties.NameProperty, text);
            var peer = UIElementAutomationPeer.FromElement(TxtAiResult) ?? UIElementAutomationPeer.CreatePeerForElement(TxtAiResult);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        // ════════════════════════════════════════════════════════════════
        // STATUS + PRECICE
        // ════════════════════════════════════════════════════════════════
        private void SetStatus(string text)
        {
            TxtStatusMessage.Text = text;
            var peer = UIElementAutomationPeer.FromElement(TxtStatusMessage) ?? UIElementAutomationPeer.CreatePeerForElement(TxtStatusMessage);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            bool ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
            bool alt = Keyboard.Modifiers == ModifierKeys.Alt;

            if (ctrl && e.Key == Key.O) { OpenImage_Click(sender, e); e.Handled = true; }
            else if (ctrlShift && e.Key == Key.S) { SaveAs_Click(sender, e); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { Save_Click(sender, e); e.Handled = true; }
            else if (alt && e.Key == Key.W)
            {
                if (_isVisualMode) SetJawsMode(); else SetVisualMode();
                e.Handled = true;
            }
        }
    }
}
