using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraStudio.Localization;
using UltraStudio.Models;
using UltraStudio.Services;
using UltraStudio.Views;

using WF = System.Windows.Forms;

namespace UltraStudio
{
    /// <summary>
    /// Jedan red u listi podešavanja. Boolean = true za on/off stavke.
    /// </summary>
    public class AdjustmentRow
    {
        public string Key { get; set; } = "";
        public bool IsBoolean { get; set; }
        public Func<ImageProject, double> GetNum { get; set; } = _ => 0;
        public Action<ImageProject, double> SetNum { get; set; } = (_, __) => { };
        public Func<ImageProject, bool> GetBool { get; set; } = _ => false;
        public Action<ImageProject, bool> SetBool { get; set; } = (_, __) => { };
        public double Min, Max, Step;
        public string Unit { get; set; } = "";
        public string Format { get; set; } = "+0.#;-0.#;0";

        public string ValueText(ImageProject p) => IsBoolean
            ? Lang.T(GetBool(p) ? "layer_on" : "layer_off")
            : GetNum(p).ToString(Format, System.Globalization.CultureInfo.InvariantCulture) + Unit;
    }

    public partial class MainWindow : Window
    {
        private readonly ImageProject _project = new();
        private readonly OllamaVisionClient _ai = new();
        private readonly UndoManager _undo = new();
        private SamSegmenter? _sam;
        private bool _describeBusy;
        private bool _busy;               // duga operacija u toku — blokira druge komande
        private List<AdjustmentRow> _rows = new();
        private bool _isVisualMode;
        private readonly Dictionary<AdjustmentRow, Slider> _sliderByRow = new();
        private readonly Dictionary<AdjustmentRow, CheckBox> _checkByRow = new();
        private readonly Dictionary<AdjustmentRow, TextBlock> _valueLabelByRow = new();

        private Layer? _selectedLayer;
        private readonly Dictionary<Layer, Border> _visualRowByLayer = new();
        private readonly bool _screenReaderDetected;

        // Pregled se renderuje u pozadini; generacija odbacuje zastarele rezultate
        private readonly DispatcherTimer _previewTimer;
        private int _previewGen;

        public MainWindow()
        {
            InitializeComponent();
            Session.CleanupOld();

            nativeAdjustList.Host = wfhAdjustments;
            nativeLayerList.Host = wfhLayers;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _previewTimer.Tick += (_, __) => { _previewTimer.Stop(); _ = RenderPreviewAsync(); };

            Lang.ApplyToResources();
            BuildAdjustmentRows();
            SetupNativeList();
            BuildVisualPanel();
            SetupNativeLayerList();
            BuildCommands();

            _screenReaderDetected = ScreenReaderDetector.IsScreenReaderRunning();
            if (_screenReaderDetected) SetJawsMode(); else SetVisualMode();

            UpdateImageInfo();
            UpdateTitle();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetStatus(string.Format(Lang.T(_screenReaderDetected ? "statusbar_ready_sr_detected" : "statusbar_ready_visual"), AppInfo.Version));
                ShowModeToast(_screenReaderDetected);

                // Dvoklik na .ustudio ili fotografiju u Exploreru: putanja stiže kao argument
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && File.Exists(args[1])) OpenPath(args[1]);
            }), DispatcherPriority.ApplicationIdle);
        }

        private void ShowModeToast(bool jawsMode)
        {
            ModeToastText.Text = Lang.T(jawsMode ? "mode_toast_jaws" : "mode_toast_visual");
            ModeToastDot.Fill = (System.Windows.Media.Brush)Resources[jawsMode ? "BrAccentAI" : "BrAccent"];
            ModeToast.Visibility = Visibility.Visible;
            ModeToast.Opacity = 0;
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromMilliseconds(3000) };
            var sb = new System.Windows.Media.Animation.Storyboard();
            System.Windows.Media.Animation.Storyboard.SetTarget(fadeIn, ModeToast);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            System.Windows.Media.Animation.Storyboard.SetTarget(fadeOut, ModeToast);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(fadeIn); sb.Children.Add(fadeOut);
            sb.Completed += (_, __) => ModeToast.Visibility = Visibility.Collapsed;
            sb.Begin();
        }

        // ════════════════════════════════════════════════════════════════
        // PODEŠAVANJA — redosled kao u Lightroom/Camera Raw
        // ════════════════════════════════════════════════════════════════
        private void BuildAdjustmentRows()
        {
            AdjustmentRow N(string key, Func<ImageProject, double> get, Action<ImageProject, double> set, double min, double max, double step, string unit = "", string fmt = "+0.#;-0.#;0")
                => new() { Key = key, GetNum = get, SetNum = set, Min = min, Max = max, Step = step, Unit = unit, Format = fmt };
            AdjustmentRow B(string key, Func<ImageProject, bool> get, Action<ImageProject, bool> set)
                => new() { Key = key, IsBoolean = true, GetBool = get, SetBool = set };

            _rows = new List<AdjustmentRow>
            {
                N("adj_exposure", p => p.Exposure, (p, v) => p.Exposure = v, -3, 3, 0.1, " EV", "+0.0#;-0.0#;0"),
                N("adj_brightness", p => p.Brightness, (p, v) => p.Brightness = v, -100, 100, 5),
                N("adj_contrast", p => p.Contrast, (p, v) => p.Contrast = v, -100, 100, 5),
                N("adj_highlights", p => p.Highlights, (p, v) => p.Highlights = v, -100, 100, 5),
                N("adj_shadows", p => p.Shadows, (p, v) => p.Shadows = v, -100, 100, 5),
                N("adj_gamma", p => p.Gamma, (p, v) => p.Gamma = v, -100, 100, 5),
                N("adj_temperature", p => p.Temperature, (p, v) => p.Temperature = v, -100, 100, 5),
                N("adj_tint", p => p.Tint, (p, v) => p.Tint = v, -100, 100, 5),
                N("adj_vibrance", p => p.Vibrance, (p, v) => p.Vibrance = v, -100, 100, 5),
                N("adj_saturation", p => p.Saturation, (p, v) => p.Saturation = v, -100, 100, 5),
                N("adj_hue", p => p.Hue, (p, v) => p.Hue = v, -180, 180, 5, "°"),
                N("adj_clarity", p => p.Clarity, (p, v) => p.Clarity = v, -100, 100, 5),
                N("adj_sharpen", p => p.Sharpen, (p, v) => p.Sharpen = v, 0, 10, 0.5, "", "0.#"),
                N("adj_blur", p => p.Blur, (p, v) => p.Blur = v, 0, 10, 0.5, "", "0.#"),
                N("adj_vignette", p => p.Vignette, (p, v) => p.Vignette = v, 0, 100, 5, "", "0"),
                N("adj_fade", p => p.Fade, (p, v) => p.Fade = v, 0, 100, 5, "", "0"),
                N("adj_posterize", p => p.Posterize, (p, v) => p.Posterize = v < 2 ? 0 : v, 0, 32, 1, "", "0"),
                N("adj_threshold", p => p.Threshold, (p, v) => p.Threshold = v, 0, 100, 5, "", "0"),
                B("adj_grayscale", p => p.Grayscale, (p, v) => p.Grayscale = v),
                B("adj_sepia", p => p.Sepia, (p, v) => p.Sepia = v),
                B("adj_invert", p => p.Invert, (p, v) => p.Invert = v),
            };
        }

        private bool _listsInitialized;

        private void SetupNativeList()
        {
            nativeAdjustList.Columns.Clear();
            nativeAdjustList.Items.Clear();
            nativeAdjustList.Columns.Add(Lang.T("col_adjustment"), 200, WF.HorizontalAlignment.Left);
            nativeAdjustList.Columns.Add(Lang.T("col_value"), 140, WF.HorizontalAlignment.Left);
            nativeAdjustList.BackColor = System.Drawing.Color.FromArgb(20, 20, 34);
            nativeAdjustList.ForeColor = System.Drawing.Color.White;
            nativeAdjustList.Font = new System.Drawing.Font("Segoe UI", 10);
            nativeAdjustList.AccessibleName = Lang.T("acc_list_help");
            if (!_listsInitialized)
            {
                // Rukovaoci se kače samo jednom (SetupNativeList se ponovo zove pri promeni jezika)
                nativeAdjustList.HandleCreated += (s, e) => NativeTheme.DisableListViewHeaderTheme(nativeAdjustList);
                nativeAdjustList.KeyDown += NativeAdjustList_KeyDown;
            }
            RefreshList();
        }

        private void RefreshList()
        {
            int selectedIndex = nativeAdjustList.SelectedIndices.Count > 0 ? nativeAdjustList.SelectedIndices[0] : 0;
            nativeAdjustList.BeginUpdate();
            if (nativeAdjustList.Items.Count == _rows.Count)
            {
                // Samo vrednosti — bez brisanja liste, da fokus i JAWS ostanu na istom redu
                for (int i = 0; i < _rows.Count; i++)
                {
                    var it = nativeAdjustList.Items[i];
                    it.SubItems[0].Text = Lang.T(_rows[i].Key);
                    it.SubItems[1].Text = _rows[i].ValueText(_project);
                }
            }
            else
            {
                nativeAdjustList.Items.Clear();
                foreach (var row in _rows)
                    nativeAdjustList.Items.Add(new WF.ListViewItem(new[] { Lang.T(row.Key), row.ValueText(_project) }) { Tag = row });
            }
            nativeAdjustList.EndUpdate();
            if (nativeAdjustList.Items.Count > 0 && nativeAdjustList.SelectedIndices.Count == 0)
            {
                var item = nativeAdjustList.Items[Math.Min(selectedIndex, nativeAdjustList.Items.Count - 1)];
                item.Selected = true; item.Focused = true;
            }
            if (_sliderByRow.Count > 0) SyncVisualPanelFromProject();
        }

        private void BuildVisualPanel()
        {
            VisualAdjustStack.Children.Clear();
            _sliderByRow.Clear(); _checkByRow.Clear(); _valueLabelByRow.Clear();
            foreach (var row in _rows)
            {
                var container = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                if (row.IsBoolean)
                {
                    var check = new CheckBox { Content = Lang.T(row.Key), Foreground = (System.Windows.Media.Brush)Resources["BrText"], FontSize = 13 };
                    check.Checked += (_, __) => { if (_syncingVisualPanel) return; SetBoolRow(row, true); };
                    check.Unchecked += (_, __) => { if (_syncingVisualPanel) return; SetBoolRow(row, false); };
                    _checkByRow[row] = check;
                    container.Children.Add(check);
                }
                else
                {
                    var header = new Grid();
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = new TextBlock { Text = Lang.T(row.Key), Foreground = (System.Windows.Media.Brush)Resources["BrText"], FontSize = 13 };
                    var valueLabel = new TextBlock { Foreground = (System.Windows.Media.Brush)Resources["BrTextSec"], FontSize = 12 };
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
                        if (_syncingVisualPanel) return;
                        SetNumRow(row, Math.Round(e.NewValue / row.Step) * row.Step, announce: false);
                    };
                    slider.MouseDoubleClick += (_, __) => SetNumRow(row, 0, announce: true); // dupli klik = nula, kao u Lightroomu
                    _sliderByRow[row] = slider;
                    _valueLabelByRow[row] = valueLabel;
                    container.Children.Add(slider);
                }
                VisualAdjustStack.Children.Add(container);
            }
            SyncVisualPanelFromProject();
        }

        private bool _syncingVisualPanel;

        private void SyncVisualPanelFromProject()
        {
            _syncingVisualPanel = true;
            try
            {
                foreach (var kv in _sliderByRow) kv.Value.Value = kv.Key.GetNum(_project);
                foreach (var kv in _checkByRow) kv.Value.IsChecked = kv.Key.GetBool(_project);
                foreach (var kv in _valueLabelByRow) kv.Value.Text = kv.Key.ValueText(_project);
            }
            finally { _syncingVisualPanel = false; }
        }

        private void SetNumRow(AdjustmentRow row, double v, bool announce)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            v = Math.Clamp(Math.Round(v, 3), row.Min, row.Max);
            if (Math.Abs(v - row.GetNum(_project)) < 1e-9) { if (announce) SetStatus($"{Lang.T(row.Key)} {row.ValueText(_project)}"); return; }
            Checkpoint(Lang.T(row.Key), merge: true);
            row.SetNum(_project, v);
            _project.LookName = "";
            RefreshList();
            RefreshPreview();
            if (announce) SetStatus($"{Lang.T(row.Key)} {row.ValueText(_project)}");
        }

        private void SetBoolRow(AdjustmentRow row, bool v)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); SyncVisualPanelFromProject(); return; }
            Checkpoint(Lang.T(row.Key));
            row.SetBool(_project, v);
            RefreshList();
            RefreshPreview();
            SetStatus($"{Lang.T(row.Key)} {row.ValueText(_project)}");
        }

        private void SetVisualMode()
        {
            _isVisualMode = true;
            BtnVisualMode.Style = (Style)Resources["AIButton"];
            BtnJawsMode.Style = (Style)Resources["StdButton"];
            SyncVisualPanelFromProject();
            wfhAdjustments.Visibility = Visibility.Collapsed;
            VisualAdjustPanel.Visibility = Visibility.Visible;
            wfhLayers.Visibility = Visibility.Collapsed;
            VisualLayerPanel.Visibility = Visibility.Visible;
            RefreshVisualLayerPanel();
            CurrentModeLabel.Text = Lang.T("visual_mode_indicator");
        }

        private void SetJawsMode()
        {
            _isVisualMode = false;
            BtnVisualMode.Style = (Style)Resources["StdButton"];
            BtnJawsMode.Style = (Style)Resources["AIButton"];
            RefreshList();
            VisualAdjustPanel.Visibility = Visibility.Collapsed;
            wfhAdjustments.Visibility = Visibility.Visible;
            VisualLayerPanel.Visibility = Visibility.Collapsed;
            wfhLayers.Visibility = Visibility.Visible;
            RefreshLayerList();
            CurrentModeLabel.Text = Lang.T("jaws_mode_indicator");
        }

        private void BtnVisualMode_Click(object sender, RoutedEventArgs e) => SetVisualMode();
        private void BtnJawsMode_Click(object sender, RoutedEventArgs e) => SetJawsMode();

        private void NativeAdjustList_KeyDown(object? sender, WF.KeyEventArgs e)
        {
            if (HandleWinFormsShortcut(e)) return;
            if (nativeAdjustList.SelectedItems.Count == 0) return;
            var row = (AdjustmentRow)nativeAdjustList.SelectedItems[0].Tag!;

            if (e.KeyCode == WF.Keys.Enter || e.KeyCode == WF.Keys.F2)
            {
                if (row.IsBoolean) SetBoolRow(row, !row.GetBool(_project));
                else EditNumericRow(row);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == WF.Keys.Space && row.IsBoolean)
            {
                SetBoolRow(row, !row.GetBool(_project));
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (!row.IsBoolean && !e.Alt && !e.Control && (e.KeyCode == WF.Keys.Right || e.KeyCode == WF.Keys.Left))
            {
                // Strelice levo/desno menjaju vrednost (Shift = 5 koraka) — bez otvaranja dijaloga
                double step = row.Step * (e.Shift ? 5 : 1) * (e.KeyCode == WF.Keys.Right ? 1 : -1);
                SetNumRow(row, row.GetNum(_project) + step, announce: true);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == WF.Keys.Delete || e.KeyCode == WF.Keys.Back)
            {
                if (row.IsBoolean) SetBoolRow(row, false); else SetNumRow(row, 0, announce: true);
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void EditNumericRow(AdjustmentRow row)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            var dlg = new SetValueDialog(Lang.T(row.Key), $"{Lang.T(row.Key)} ({row.Min} {Lang.T("to")} {row.Max}{row.Unit}):",
                row.GetNum(_project).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), row.Unit) { Owner = this };
            if (dlg.ShowDialog() == true && Ui.TryNum(dlg.ResultValue, out double v))
                SetNumRow(row, v, announce: true);
        }

        // ════════════════════════════════════════════════════════════════
        // SLOJEVI — JAWS lista + vizuelni panel
        // ════════════════════════════════════════════════════════════════
        private void SetupNativeLayerList()
        {
            nativeLayerList.Columns.Clear();
            nativeLayerList.Columns.Add(Lang.T("col_layer_name"), 140, WF.HorizontalAlignment.Left);
            nativeLayerList.Columns.Add(Lang.T("col_layer_type"), 70, WF.HorizontalAlignment.Left);
            nativeLayerList.Columns.Add(Lang.T("col_layer_visible"), 60, WF.HorizontalAlignment.Left);
            nativeLayerList.Columns.Add(Lang.T("col_layer_opacity"), 60, WF.HorizontalAlignment.Left);
            nativeLayerList.Columns.Add(Lang.T("col_layer_position"), 110, WF.HorizontalAlignment.Left);
            nativeLayerList.BackColor = System.Drawing.Color.FromArgb(20, 20, 34);
            nativeLayerList.ForeColor = System.Drawing.Color.White;
            nativeLayerList.Font = new System.Drawing.Font("Segoe UI", 10);
            nativeLayerList.AccessibleName = Lang.T("acc_layer_list_help");
            if (!_listsInitialized)
            {
                nativeLayerList.HandleCreated += (s, e) => NativeTheme.DisableListViewHeaderTheme(nativeLayerList);
                nativeLayerList.KeyDown += NativeLayerList_KeyDown;
                nativeLayerList.SelectedIndexChanged += (_, __) =>
                {
                    if (_refreshingLayers) return;
                    _selectedLayer = nativeLayerList.SelectedItems.Count > 0 ? (Layer)nativeLayerList.SelectedItems[0].Tag! : null;
                    HighlightSelectedVisualRow();
                };
            }
            _listsInitialized = true;
            RefreshLayerList();
        }

        private bool _refreshingLayers;

        private void RefreshLayerList()
        {
            _refreshingLayers = true;
            try
            {
                nativeLayerList.BeginUpdate();
                nativeLayerList.Items.Clear();
                // Najviši sloj prvi — kao Photoshop panel slojeva
                for (int i = _project.Layers.Count - 1; i >= 0; i--)
                {
                    var layer = _project.Layers[i];
                    nativeLayerList.Items.Add(new WF.ListViewItem(new[]
                    {
                        layer.Name, Lang.T(layer.TypeLabelKey),
                        layer.Visible ? Lang.T("layer_on") : Lang.T("layer_off"),
                        $"{layer.Opacity:0}%",
                        Describe.Position(layer.CenterX, layer.CenterY, _project.CanvasWidth, _project.CanvasHeight)
                    }) { Tag = layer });
                }
                nativeLayerList.EndUpdate();

                if (_selectedLayer != null && !_project.Layers.Contains(_selectedLayer)) _selectedLayer = null;
                if (_selectedLayer == null && _project.Layers.Count > 0) _selectedLayer = _project.Layers[^1];
                foreach (WF.ListViewItem it in nativeLayerList.Items)
                    if (it.Tag == _selectedLayer) { it.Selected = true; it.Focused = true; it.EnsureVisible(); }
            }
            finally { _refreshingLayers = false; }
        }

        private void NativeLayerList_KeyDown(object? sender, WF.KeyEventArgs e)
        {
            if (HandleWinFormsShortcut(e)) return;
            if (_selectedLayer == null) return;
            if (e.KeyCode == WF.Keys.Enter || e.KeyCode == WF.Keys.F2)
            {
                OpenLayerProperties(_selectedLayer);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == WF.Keys.Space)
            {
                Checkpoint(Lang.T("hist_visibility"));
                _selectedLayer.Visible = !_selectedLayer.Visible;
                RefreshLayersUi();
                SetStatus($"{_selectedLayer.Name}: {(_selectedLayer.Visible ? Lang.T("layer_on") : Lang.T("layer_off"))}");
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == WF.Keys.Delete)
            {
                Execute("dellayer");
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void RefreshVisualLayerPanel()
        {
            VisualLayerStack.Children.Clear();
            _visualRowByLayer.Clear();
            for (int i = _project.Layers.Count - 1; i >= 0; i--)
            {
                var layer = _project.Layers[i];
                var border = new Border
                {
                    BorderBrush = (System.Windows.Media.Brush)Resources["BrBorder"], BorderThickness = new Thickness(1),
                    Background = (System.Windows.Media.Brush)Resources["BrBgPanel"], Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(8), Cursor = Cursors.Hand
                };
                var stack = new StackPanel();
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var nameLbl = new TextBlock
                {
                    Text = $"{layer.Name} ({Lang.T(layer.TypeLabelKey)})",
                    Foreground = (System.Windows.Media.Brush)Resources["BrText"], FontSize = 12, FontWeight = FontWeights.Bold
                };
                var visCheck = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, Content = Lang.T("layer_field_visible") };
                visCheck.Checked += (_, __) => { if (layer.Visible) return; Checkpoint(Lang.T("hist_visibility")); layer.Visible = true; RefreshLayersUi(); };
                visCheck.Unchecked += (_, __) => { if (!layer.Visible) return; Checkpoint(Lang.T("hist_visibility")); layer.Visible = false; RefreshLayersUi(); };
                Grid.SetColumn(nameLbl, 0); Grid.SetColumn(visCheck, 1);
                header.Children.Add(nameLbl); header.Children.Add(visCheck);
                stack.Children.Add(header);

                var opacitySlider = new Slider { Minimum = 0, Maximum = 100, Value = layer.Opacity, SmallChange = 5, LargeChange = 20, Margin = new Thickness(0, 6, 0, 0) };
                opacitySlider.SetValue(AutomationProperties.NameProperty, Lang.T("layer_field_opacity") + " — " + layer.Name);
                opacitySlider.ValueChanged += (_, e) => { Checkpoint(Lang.T("layer_field_opacity"), merge: true); layer.Opacity = e.NewValue; RefreshPreview(); };
                stack.Children.Add(opacitySlider);

                var editBtn = new Button
                {
                    Content = Lang.T("btn_layer_properties"), Style = (Style)Resources["StdButton"],
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0)
                };
                editBtn.Click += (_, __) => { _selectedLayer = layer; OpenLayerProperties(layer); };
                stack.Children.Add(editBtn);

                border.Child = stack;
                border.MouseLeftButtonDown += (_, __) => { _selectedLayer = layer; HighlightSelectedVisualRow(); };
                _visualRowByLayer[layer] = border;
                VisualLayerStack.Children.Add(border);
            }
            HighlightSelectedVisualRow();
        }

        private void HighlightSelectedVisualRow()
        {
            foreach (var kv in _visualRowByLayer)
                kv.Value.BorderBrush = (System.Windows.Media.Brush)Resources[kv.Key == _selectedLayer ? "BrAccent" : "BrBorder"];
        }

        private void RefreshLayersUi()
        {
            RefreshLayerList();
            if (_isVisualMode) RefreshVisualLayerPanel();
            RefreshPreview();
        }

        /// <summary>Posle bilo koje veće izmene: liste, info, naslov, pregled.</summary>
        private void RefreshAll()
        {
            RefreshList();
            RefreshLayersUi();
            UpdateImageInfo();
            UpdateTitle();
        }

        private void OpenLayerProperties(Layer layer)
        {
            Checkpoint(string.Format(Lang.T("hist_layer_props"), layer.Name));
            var dlg = new LayerPropertiesDialog(layer) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                RefreshLayersUi();
                string msg = Describe.Layer(layer, _project);
                if (layer is TextLayer tl && tl.Wrap)
                {
                    int over = TextLayout.OverflowLines(tl);
                    if (over > 0) msg += ". " + string.Format(Lang.T("frame_overflow"), over);
                }
                SetStatus(msg);
            }
            else _undo.Rollback(_project);
        }

        // ════════════════════════════════════════════════════════════════
        // UNDO / STATUS / NASLOV
        // ════════════════════════════════════════════════════════════════
        private void Checkpoint(string label, bool merge = false)
        {
            _undo.Checkpoint(_project, label, merge);
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = _project.ProjectPath != null ? Path.GetFileNameWithoutExtension(_project.ProjectPath)
                        : _project.OriginalPath != null ? Path.GetFileName(_project.OriginalPath)
                        : Lang.T("untitled");
            string page = _project.PageCount > 1 ? $" ({PageInfo()})" : "";
            Title = $"{name}{page}{(_undo.Dirty ? " *" : "")} — Ultra Studio {AppInfo.Version}";
        }

        private void UpdateImageInfo()
        {
            string? preset = CanvasPresets.Match(_project.CanvasWidth, _project.CanvasHeight);
            string size = $"{_project.CanvasWidth} × {_project.CanvasHeight} px" + (preset != null ? $" ({preset})" : "");
            TxtImageInfo.Text = _project.OriginalPath != null
                ? $"{Path.GetFileName(_project.OriginalPath)} — {size}"
                : string.Format(Lang.T("canvas_info_blank"), size);
            if (_project.PageCount > 1) TxtImageInfo.Text += " — " + PageInfo();
            var peer = UIElementAutomationPeer.FromElement(TxtImageInfo) ?? UIElementAutomationPeer.CreatePeerForElement(TxtImageInfo);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void SetAiResult(string text)
        {
            TxtAiResult.Text = text;
            TxtAiResult.SetValue(AutomationProperties.NameProperty, text);
            var peer = UIElementAutomationPeer.FromElement(TxtAiResult) ?? UIElementAutomationPeer.CreatePeerForElement(TxtAiResult);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void SetStatus(string text)
        {
            TxtStatusMessage.Text = text;
            var peer = UIElementAutomationPeer.FromElement(TxtStatusMessage) ?? UIElementAutomationPeer.CreatePeerForElement(TxtStatusMessage);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void ShowError(Exception ex)
        {
            DebugLog.Write("GREŠKA: " + ex);
            SetStatus(string.Format(Lang.T("error_prefix"), ex.Message));
            MessageBox.Show(this, string.Format(Lang.T("error_prefix"), ex.Message), Lang.T("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // ════════════════════════════════════════════════════════════════
        // PREGLED — u pozadini, sa odlaganjem (slajder ne čeka render)
        // ════════════════════════════════════════════════════════════════
        private void RefreshPreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private async Task RenderPreviewAsync()
        {
            if (!_project.HasCanvasContent)
            {
                TxtNoImage.Visibility = Visibility.Visible;
                ImgPreview.Source = null;
                return;
            }
            if (_busy) { RefreshPreview(); return; } // projekat se upravo menja u pozadini — probaj malo kasnije
            int gen = ++_previewGen;
            var snap = ProjectJson.Clone(_project);
            try
            {
                var bytes = await Task.Run(() => CanvasEngine.RenderPreviewJpeg(snap, 1600));
                if (gen != _previewGen) return; // u međuvremenu je stigla novija izmena
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
                DebugLog.Write($"RenderPreview: GREŠKA — {ex}");
                if (gen == _previewGen) SetStatus(string.Format(Lang.T("error_prefix"), ex.Message));
            }
        }

        // ════════════════════════════════════════════════════════════════
        // MIŠ NA PREGLEDU (za korisnike koji vide): klik bira sloj i vuče ga;
        // prevlačenje po praznom delu fotografije pravi pravougaonu selekciju.
        // ════════════════════════════════════════════════════════════════
        private Layer? _dragLayer;
        private Point _dragStartCanvas;
        private double _dragLayerX, _dragLayerY;
        private bool _dragSelecting;

        private Point ToCanvas(MouseEventArgs e)
        {
            var pos = e.GetPosition(ImgPreview);
            double sx = _project.CanvasWidth / Math.Max(1, ImgPreview.ActualWidth);
            double sy = _project.CanvasHeight / Math.Max(1, ImgPreview.ActualHeight);
            return new Point(pos.X * sx, pos.Y * sy);
        }

        private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_busy || !_project.HasCanvasContent) return;
            var p = ToCanvas(e);
            _dragStartCanvas = p;
            _dragLayer = _project.Layers.LastOrDefault(l => l.Visible && l is not GradientLayer && p.X >= l.X && p.X <= l.X + l.Width && p.Y >= l.Y && p.Y <= l.Y + l.Height);
            if (_dragLayer != null)
            {
                _selectedLayer = _dragLayer;
                _dragLayerX = _dragLayer.X; _dragLayerY = _dragLayer.Y;
                Checkpoint(string.Format(Lang.T("hist_move"), _dragLayer.Name));
                RefreshLayerList(); HighlightSelectedVisualRow();
            }
            else if (_project.HasImage)
            {
                _dragSelecting = true;
                Checkpoint(Lang.T("hist_select"));
            }
            ImgPreview.CaptureMouse();
        }

        private void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = ToCanvas(e);
            if (_dragLayer != null)
            {
                _dragLayer.X = Math.Round(_dragLayerX + p.X - _dragStartCanvas.X);
                _dragLayer.Y = Math.Round(_dragLayerY + p.Y - _dragStartCanvas.Y);
                RefreshPreview();
            }
            else if (_dragSelecting)
            {
                double x0 = Math.Clamp(Math.Min(p.X, _dragStartCanvas.X), 0, _project.CanvasWidth);
                double y0 = Math.Clamp(Math.Min(p.Y, _dragStartCanvas.Y), 0, _project.CanvasHeight);
                double x1 = Math.Clamp(Math.Max(p.X, _dragStartCanvas.X), 0, _project.CanvasWidth);
                double y1 = Math.Clamp(Math.Max(p.Y, _dragStartCanvas.Y), 0, _project.CanvasHeight);
                _project.Selection = new Selection { Kind = SelectionKind.Rectangle, X = Math.Round(x0), Y = Math.Round(y0), Width = Math.Round(x1 - x0), Height = Math.Round(y1 - y0) };
                RefreshPreview();
            }
        }

        private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
        {
            ImgPreview.ReleaseMouseCapture();
            if (_dragLayer != null)
            {
                if (Math.Abs(_dragLayer.X - _dragLayerX) < 0.5 && Math.Abs(_dragLayer.Y - _dragLayerY) < 0.5) _undo.Rollback(_project);
                RefreshLayersUi();
                SetStatus(Describe.Layer(_dragLayer, _project));
            }
            else if (_dragSelecting)
            {
                if (_project.Selection.Width < 3 || _project.Selection.Height < 3) _project.Selection = new Selection();
                RefreshPreview();
                SetStatus(EditOps.DescribeSelection(_project));
            }
            _dragLayer = null; _dragSelecting = false;
        }

        // ════════════════════════════════════════════════════════════════
        // TASTATURA — iste prečice stižu i iz WinForms lista (Gate sprečava duplo izvršavanje)
        // ════════════════════════════════════════════════════════════════
        private readonly Dictionary<string, DateTime> _gate = new();

        private bool Gate(string id, int ms = 250)
        {
            var now = DateTime.UtcNow;
            if (_gate.TryGetValue(id, out var last) && (now - last).TotalMilliseconds < ms) return false;
            _gate[id] = now;
            return true;
        }

        // Isti taster stigne dvaput u par milisekundi (WinForms lista + WPF prozor); pomeranje sloja
        // držanjem strelice mora da prođe brzo, pa za njega kraći razmak.
        private static int GateMs(string id) => id.StartsWith("nudge") ? 30 : 250;

        /// <summary>Prečica -> id komande. null = nije naša prečica.</summary>
        private string? ShortcutFor(Key key, ModifierKeys mods)
        {
            bool ctrl = mods == ModifierKeys.Control, ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
            bool alt = mods == ModifierKeys.Alt, altShift = mods == (ModifierKeys.Alt | ModifierKeys.Shift), none = mods == ModifierKeys.None;
            if (ctrl) switch (key)
            {
                case Key.N: return "new"; case Key.O: return "open"; case Key.S: return "save"; case Key.E: return "export";
                case Key.Z: return "undo"; case Key.Y: return "redo"; case Key.H: return "history";
                case Key.J: return "copylayer"; case Key.D: return "deselect"; case Key.L: return "looks"; case Key.F: return "refilter";
                case Key.A: return "selall"; case Key.G: return "pagelist";
                case Key.PageDown: return "pagenext"; case Key.PageUp: return "pageprev";
            }
            if (ctrlShift) switch (key)
            {
                case Key.S: return "saveas"; case Key.Z: return "redo"; case Key.J: return "cutlayer";
                case Key.I: return "selinvert"; case Key.L: return "log"; case Key.E: return "exportpdf";
            }
            if (none) switch (key) { case Key.F1: return "shortcuts"; case Key.F6: return "status"; case Key.F7: return "designcheck"; }
            if (alt && key == Key.W) return "togglemode";
            if (alt || altShift)
            {
                string step = altShift ? "1" : "10";
                switch (key)
                {
                    case Key.Left: return "nudge:-" + step + ":0";
                    case Key.Right: return "nudge:" + step + ":0";
                    case Key.Up: return "nudge:0:-" + step;
                    case Key.Down: return "nudge:0:" + step;
                }
            }
            return null;
        }

        private bool HandleWinFormsShortcut(WF.KeyEventArgs e)
        {
            var key = KeyInterop.KeyFromVirtualKey((int)e.KeyCode);
            var mods = ModifierKeys.None;
            if (e.Control) mods |= ModifierKeys.Control;
            if (e.Shift) mods |= ModifierKeys.Shift;
            if (e.Alt) mods |= ModifierKeys.Alt;
            var id = ShortcutFor(key, mods);
            if (id == null) return false;
            e.Handled = e.SuppressKeyPress = true;
            if (Gate(id, GateMs(id))) Dispatcher.BeginInvoke(new Action(() => Execute(id)));
            return true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var id = ShortcutFor(key, Keyboard.Modifiers);
            if (id == null) return;
            // U poljima za tekst Ctrl+A/Z/Y/strelice pripadaju tom polju
            if (Keyboard.FocusedElement is TextBox && (id is "selall" or "undo" or "redo" || id.StartsWith("nudge"))) return;
            e.Handled = true;
            if (Gate(id, GateMs(id))) Execute(id);
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            try { _sam?.Dispose(); } catch { }
        }

        /// <summary>Pita za čuvanje ako ima nesačuvanih izmena. false = korisnik je otkazao.</summary>
        private bool ConfirmDiscard()
        {
            if (!_undo.Dirty || !_project.HasCanvasContent) return true;
            var r = MessageBox.Show(this, Lang.T("confirm_save_changes"), Lang.T("app_name"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return SaveProject(false);
            return true;
        }
    }
}
