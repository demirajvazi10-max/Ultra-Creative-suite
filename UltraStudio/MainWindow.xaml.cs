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

        // Trenutno selektovan sloj — jedan izvor istine za JAWS listu I
        // vizuelni panel (isti princip kao _project za podešavanja), tako da
        // Layers meni (Properties/Delete/Move/Duplicate) radi identično bez
        // obzira u kom je modu korisnik.
        private Layer? _selectedLayer;
        private readonly Dictionary<Layer, Border> _visualRowByLayer = new();

        public MainWindow()
        {
            InitializeComponent();
            Lang.ApplyToResources();
            BuildAdjustmentRows();
            SetupNativeList();
            BuildVisualPanel();
            SetupNativeLayerList();
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
            wfhLayers.Visibility = Visibility.Collapsed;
            VisualLayerPanel.Visibility = Visibility.Visible;
            RefreshVisualLayerPanel();
            CurrentModeLabel.Text = Lang.T("visual_mode_indicator");
        }

        private void SetJawsMode()
        {
            _isVisualMode = false;
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
        // SLOJEVI (grafički dizajn deo) — JAWS lista + vizuelni panel, isti
        // dual-mod obrazac kao podešavanja gore, ali nad _project.Layers.
        // Redosled u listi = redosled crtanja (poslednji = najviše, kao
        // Photoshop/Canva "layer stack").
        // ════════════════════════════════════════════════════════════════
        private void SetupNativeLayerList()
        {
            nativeLayerList.Columns.Clear();
            nativeLayerList.Columns.Add(Lang.T("col_layer_name"), 150, WF.HorizontalAlignment.Left);
            nativeLayerList.Columns.Add(Lang.T("col_layer_type"), 70, WF.HorizontalAlignment.Left);
            nativeLayerList.Columns.Add(Lang.T("col_layer_visible"), 60, WF.HorizontalAlignment.Left);
            nativeLayerList.Columns.Add(Lang.T("col_layer_opacity"), 60, WF.HorizontalAlignment.Left);

            nativeLayerList.BackColor = System.Drawing.Color.FromArgb(20, 20, 34);
            nativeLayerList.ForeColor = System.Drawing.Color.White;
            nativeLayerList.Font = new System.Drawing.Font("Segoe UI", 10);
            nativeLayerList.AccessibleName = Lang.T("acc_layer_list_help");

            nativeLayerList.KeyDown += NativeLayerList_KeyDown;
            nativeLayerList.SelectedIndexChanged += (_, __) =>
            {
                _selectedLayer = nativeLayerList.SelectedItems.Count > 0
                    ? (Layer)nativeLayerList.SelectedItems[0].Tag!
                    : null;
                HighlightSelectedVisualRow();
            };
            RefreshLayerList();
        }

        private void RefreshLayerList()
        {
            nativeLayerList.BeginUpdate();
            int selectedIndex = nativeLayerList.SelectedIndices.Count > 0 ? nativeLayerList.SelectedIndices[0] : -1;
            nativeLayerList.Items.Clear();
            foreach (var layer in _project.Layers)
            {
                var lvi = new WF.ListViewItem(new[]
                {
                    layer.Name,
                    Lang.T(layer.TypeLabelKey),
                    layer.Visible ? Lang.T("layer_on") : Lang.T("layer_off"),
                    $"{layer.Opacity:0}%"
                })
                { Tag = layer };
                nativeLayerList.Items.Add(lvi);
            }
            nativeLayerList.EndUpdate();

            if (nativeLayerList.Items.Count > 0)
            {
                int idx = selectedIndex >= 0 ? Math.Min(selectedIndex, nativeLayerList.Items.Count - 1) : nativeLayerList.Items.Count - 1;
                nativeLayerList.Items[idx].Selected = true;
                _selectedLayer = (Layer)nativeLayerList.Items[idx].Tag!;
            }
            else
            {
                _selectedLayer = null;
            }
        }

        private void NativeLayerList_KeyDown(object? sender, WF.KeyEventArgs e)
        {
            if (_selectedLayer == null) return;

            if (e.KeyCode == WF.Keys.Enter || e.KeyCode == WF.Keys.F2)
            {
                OpenLayerProperties(_selectedLayer);
                e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Space)
            {
                _selectedLayer.Visible = !_selectedLayer.Visible;
                RefreshLayersUi();
                e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Delete)
            {
                DeleteLayer_Click(sender!, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        // Vizuelni panel: jedan "red" po sloju sa checkbox-om vidljivosti,
        // sliderom providnosti i dugmetom za svojstva — bira se klikom bilo
        // gde na red (isto ponašanje kao selekcija u JAWS listi).
        private void RefreshVisualLayerPanel()
        {
            VisualLayerStack.Children.Clear();
            _visualRowByLayer.Clear();

            foreach (var layer in _project.Layers)
            {
                var border = new Border
                {
                    BorderBrush = (System.Windows.Media.Brush)Resources["BrBorder"],
                    BorderThickness = new Thickness(1),
                    Background = (System.Windows.Media.Brush)Resources["BrBgPanel"],
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(8),
                    Cursor = System.Windows.Input.Cursors.Hand
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
                var visCheck = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center,
                    Content = Lang.T("layer_field_visible") };
                visCheck.Checked += (_, __) => { layer.Visible = true; RefreshLayersUi(); };
                visCheck.Unchecked += (_, __) => { layer.Visible = false; RefreshLayersUi(); };
                Grid.SetColumn(nameLbl, 0); Grid.SetColumn(visCheck, 1);
                header.Children.Add(nameLbl); header.Children.Add(visCheck);
                stack.Children.Add(header);

                var opacitySlider = new Slider
                {
                    Minimum = 0, Maximum = 100, Value = layer.Opacity, SmallChange = 5, LargeChange = 20,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                opacitySlider.SetValue(AutomationProperties.NameProperty, Lang.T("layer_field_opacity") + " — " + layer.Name);
                opacitySlider.ValueChanged += (_, e) => { layer.Opacity = e.NewValue; RefreshPreview(); };
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

        /// <summary>Osvežava OBA prikaza slojeva + preview — jedina tačka posle bilo koje izmene liste slojeva.</summary>
        private void RefreshLayersUi()
        {
            RefreshLayerList();
            RefreshVisualLayerPanel();
            RefreshPreview();
        }

        private void OpenLayerProperties(Layer layer)
        {
            var dlg = new LayerPropertiesDialog(layer) { Owner = this };
            if (dlg.ShowDialog() == true) RefreshLayersUi();
        }

        private string NextLayerName(string prefix)
        {
            string name = $"{prefix} {_project.NextLayerNumber}";
            _project.NextLayerNumber++;
            return name;
        }

        private void AddTextLayer_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SetValueDialog(Lang.T("menu_add_text_layer"), Lang.T("layer_field_text"), Lang.T("layer_default_text"), "") { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultValue)) return;

            var layer = new TextLayer
            {
                Name = NextLayerName("Text"),
                Text = dlg.ResultValue.Trim(),
                X = 40, Y = 40, Width = 400, Height = 60
            };
            _project.Layers.Add(layer);
            _selectedLayer = layer;
            RefreshLayersUi();
        }

        private void AddShapeLayer(ShapeKind kind, string namePrefix)
        {
            var layer = new ShapeLayer
            {
                Name = NextLayerName(namePrefix),
                ShapeKind = kind,
                X = 60, Y = 60,
                Width = 200,
                Height = kind == ShapeKind.Line ? 0 : 140
            };
            if (kind == ShapeKind.Line) { layer.FillEnabled = false; layer.StrokeEnabled = true; }
            _project.Layers.Add(layer);
            _selectedLayer = layer;
            RefreshLayersUi();
            OpenLayerProperties(layer);
        }

        private void AddRectangleLayer_Click(object sender, RoutedEventArgs e) => AddShapeLayer(ShapeKind.Rectangle, "Rectangle");
        private void AddEllipseLayer_Click(object sender, RoutedEventArgs e) => AddShapeLayer(ShapeKind.Ellipse, "Ellipse");
        private void AddLineLayer_Click(object sender, RoutedEventArgs e) => AddShapeLayer(ShapeKind.Line, "Line");

        private void AddImageLayer_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = Lang.T("menu_add_image_layer"),
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var (w, h) = ImageEngine.GetDimensions(dlg.FileName);
                // Uklopi u platno ako je veća od njega — isto ponašanje kao
                // "fit to canvas" u standardnim dizajn alatima.
                double scale = Math.Min(1.0, Math.Min((double)_project.CanvasWidth / w, (double)_project.CanvasHeight / h));

                var layer = new ImageLayer
                {
                    Name = NextLayerName("Image"),
                    SourcePath = dlg.FileName,
                    X = 20, Y = 20,
                    Width = Math.Max(1, w * scale),
                    Height = Math.Max(1, h * scale)
                };
                _project.Layers.Add(layer);
                _selectedLayer = layer;
                RefreshLayersUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Lang.T("error_prefix"), ex.Message), Lang.T("error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LayerProperties_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLayer == null) { SetStatus(Lang.T("layer_none_selected")); return; }
            OpenLayerProperties(_selectedLayer);
        }

        private void DuplicateLayer_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLayer == null) { SetStatus(Lang.T("layer_none_selected")); return; }

            Layer copy = _selectedLayer switch
            {
                TextLayer t => new TextLayer { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, Bold = t.Bold, Italic = t.Italic, ColorHex = t.ColorHex },
                ShapeLayer s => new ShapeLayer { ShapeKind = s.ShapeKind, FillEnabled = s.FillEnabled, FillColorHex = s.FillColorHex, StrokeEnabled = s.StrokeEnabled, StrokeColorHex = s.StrokeColorHex, StrokeWidth = s.StrokeWidth },
                ImageLayer i => new ImageLayer { SourcePath = i.SourcePath },
                _ => throw new InvalidOperationException()
            };
            copy.Name = _selectedLayer.Name + " " + Lang.T("layer_copy_suffix");
            copy.X = _selectedLayer.X + 20;
            copy.Y = _selectedLayer.Y + 20;
            copy.Width = _selectedLayer.Width;
            copy.Height = _selectedLayer.Height;
            copy.Opacity = _selectedLayer.Opacity;
            copy.Visible = _selectedLayer.Visible;

            int idx = _project.Layers.IndexOf(_selectedLayer);
            _project.Layers.Insert(idx + 1, copy);
            _selectedLayer = copy;
            RefreshLayersUi();
        }

        private void DeleteLayer_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLayer == null) { SetStatus(Lang.T("layer_none_selected")); return; }
            _project.Layers.Remove(_selectedLayer);
            _selectedLayer = null;
            RefreshLayersUi();
        }

        private void MoveLayerUp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLayer == null) return;
            int idx = _project.Layers.IndexOf(_selectedLayer);
            if (idx < _project.Layers.Count - 1) _project.Layers.Move(idx, idx + 1);
            RefreshLayersUi();
        }

        private void MoveLayerDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLayer == null) return;
            int idx = _project.Layers.IndexOf(_selectedLayer);
            if (idx > 0) _project.Layers.Move(idx, idx - 1);
            RefreshLayersUi();
        }

        private void NewCanvas_Click(object sender, RoutedEventArgs e)
        {
            var wDlg = new SetValueDialog(Lang.T("menu_new_canvas"), Lang.T("canvas_width_prompt"), _project.CanvasWidth.ToString(), "px") { Owner = this };
            if (wDlg.ShowDialog() != true || !int.TryParse(wDlg.ResultValue, out int newW) || newW < 1) return;

            var hDlg = new SetValueDialog(Lang.T("menu_new_canvas"), Lang.T("canvas_height_prompt"), _project.CanvasHeight.ToString(), "px") { Owner = this };
            if (hDlg.ShowDialog() != true || !int.TryParse(hDlg.ResultValue, out int newH) || newH < 1) return;

            _project.OriginalPath = null;
            _project.OriginalWidth = 0;
            _project.OriginalHeight = 0;
            _project.ResetAdjustments();
            _project.Layers.Clear();
            _project.CanvasWidth = newW;
            _project.CanvasHeight = newH;
            _lastSavePath = null;
            _selectedLayer = null;

            UpdateImageInfo();
            RefreshLayersUi();
            SetStatus(string.Format(Lang.T("canvas_created"), newW, newH));
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
                _project.CanvasWidth = w;
                _project.CanvasHeight = h;
                _project.ResetAdjustments();
                // Nova fotografija = novi projekat — slojevi od PRETHODNE slike
                // (tekst/oblici pozicionirani za drugu veličinu platna) se ne
                // prenose automatski, isto kao što File > Open u drugim
                // editorima ne čuva stari canvas.
                _project.Layers.Clear();
                _selectedLayer = null;
                _lastSavePath = null;

                UpdateImageInfo();
                RefreshLayersUi();
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
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("status_no_canvas")); return; }
            if (_lastSavePath == null) { SaveAs_Click(sender, e); return; }
            DoExport(_lastSavePath);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("status_no_canvas")); return; }
            var dlg = new SaveFileDialog
            {
                Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp|TIFF|*.tiff",
                FileName = (_project.HasImage ? Path.GetFileNameWithoutExtension(_project.OriginalPath) : "design") + "_edited.png"
            };
            if (dlg.ShowDialog() != true) return;
            _lastSavePath = dlg.FileName;
            DoExport(dlg.FileName);
        }

        private void DoExport(string path)
        {
            try
            {
                CanvasEngine.Export(_project, path);
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
            if (!_project.HasCanvasContent) { TxtNoImage.Visibility = Visibility.Visible; ImgPreview.Source = null; return; }

            try
            {
                var bytes = CanvasEngine.RenderPreviewJpeg(_project);
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
                : string.Format(Lang.T("canvas_info_blank"), _project.CanvasWidth, _project.CanvasHeight);
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
