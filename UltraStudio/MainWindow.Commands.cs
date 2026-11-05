using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Newtonsoft.Json;
using UltraStudio.Localization;
using UltraStudio.Models;
using UltraStudio.Services;
using UltraStudio.Views;

namespace UltraStudio
{
    /// <summary>
    /// Sve komande iz menija i prečica na jednom mestu. Meni stavka ima Tag = id
    /// komande; prečice (ShortcutFor) vraćaju isti id — jedan put izvršavanja.
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<string, Action> _commands = new();
        private (FilterKind kind, double amount)? _lastFilter;
        private CanvasEngine.ExportOptions _lastExport = new() { Format = "jpg", Quality = 92 };
        private string? _lastExportFolder;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string ImageFilter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp;*.gif;*.heic|All files|*.*";

        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string id) Execute(id);
        }

        // Ostaci iz XAML-a (dugme u AI panelu)
        private void BtnDescribe_Click(object sender, RoutedEventArgs e) => Execute("describe");

        private void Execute(string id)
        {
            DebugLog.Write($"Execute: {id}");
            if (_busy && id is not ("status" or "shortcuts" or "log" or "about" or "support"))
            {
                SetStatus(Lang.T("status_busy"));
                return;
            }
            try
            {
                if (id.StartsWith("filter:")) { RunFilter(Enum.Parse<FilterKind>(id[7..]), null); return; }
                if (id.StartsWith("arr:")) { Arrange(id[4..]); return; }
                if (id.StartsWith("nudge:"))
                {
                    var parts = id.Split(':');
                    Nudge(double.Parse(parts[1], Inv), double.Parse(parts[2], Inv));
                    return;
                }
                if (_commands.TryGetValue(id, out var a)) a();
                else DebugLog.Write($"Execute: nepoznata komanda {id}");
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void BuildCommands()
        {
            // Fajl
            _commands["new"] = NewCanvas;
            _commands["open"] = OpenFile;
            _commands["save"] = () => SaveProject(false);
            _commands["saveas"] = () => SaveProject(true);
            _commands["export"] = ExportImage;
            _commands["batch"] = () => { if (_busy) return; new BatchWindow(this, _project).ShowDialog(); };
            _commands["exit"] = Close;

            // Uređivanje
            _commands["undo"] = () => UndoRedo(true);
            _commands["redo"] = () => UndoRedo(false);
            _commands["history"] = ShowHistory;
            _commands["copylayer"] = () => _ = CopyToLayer(false);
            _commands["cutlayer"] = () => _ = CopyToLayer(true);
            _commands["fill"] = FillSelection;
            _commands["clear"] = () => _ = RunOpAsync(Lang.T("menu_delete_selection"), () => EditOps.DeleteSelection(_project), () => Lang.T("done_delete_selection"), requireSelection: true);
            _commands["designcheck"] = DesignCheck;
            _commands["status"] = QuickStatus;
            _commands["language"] = ToggleLanguage;
            _commands["togglemode"] = () => { if (_isVisualMode) SetJawsMode(); else SetVisualMode(); SetStatus(CurrentModeLabel.Text); };

            // Selekcija
            _commands["selall"] = () => SetSelection(new Selection { Kind = SelectionKind.Rectangle, X = 0, Y = 0, Width = _project.CanvasWidth, Height = _project.CanvasHeight, Label = Lang.T("sel_all") }, Lang.T("menu_select_all"));
            _commands["deselect"] = () => SetSelection(new Selection(), Lang.T("menu_deselect"));
            _commands["selinvert"] = InvertSelection;
            _commands["selrect"] = () => SelectByNumbers(SelectionKind.Rectangle);
            _commands["selellipse"] = () => SelectByNumbers(SelectionKind.Ellipse);
            _commands["selpreset"] = SelectPresetArea;
            _commands["selsubject"] = () => _ = AiSelectAsync(null);
            _commands["seldescribe"] = () => { var d = AskText(Lang.T("menu_select_describe"), Lang.T("extract_prompt"), ""); if (d != null) _ = AiSelectAsync(d); };
            _commands["colorrange"] = () => _ = ColorRangeAsync();
            _commands["feather"] = Feather;
            _commands["selinfo"] = () => SetStatus(EditOps.DescribeSelection(_project));
            _commands["selwhat"] = () => _ = WhatsInSelectionAsync();

            // Slika
            _commands["cropsel"] = CropToSelection;
            _commands["cropratio"] = () => _ = CropToRatioAsync();
            _commands["cropnum"] = CropByNumbers;
            _commands["imagesize"] = ImageSize;
            _commands["canvassize"] = CanvasSizeDialog;
            _commands["rot90"] = () => _ = RunOpAsync(Lang.T("menu_rotate_cw"), () => EditOps.Rotate(_project, 90), SizeDone, requireImage: false);
            _commands["rot270"] = () => _ = RunOpAsync(Lang.T("menu_rotate_ccw"), () => EditOps.Rotate(_project, 270), SizeDone, requireImage: false);
            _commands["rot180"] = () => _ = RunOpAsync(Lang.T("menu_rotate_180"), () => EditOps.Rotate(_project, 180), SizeDone, requireImage: false);
            _commands["rotany"] = RotateAny;
            _commands["fliph"] = () => _ = RunOpAsync(Lang.T("menu_flip_h"), () => EditOps.Flip(_project, true), null, requireImage: false);
            _commands["flipv"] = () => _ = RunOpAsync(Lang.T("menu_flip_v"), () => EditOps.Flip(_project, false), null, requireImage: false);
            _commands["flatten"] = () => _ = RunOpAsync(Lang.T("menu_flatten"), () => EditOps.Flatten(_project), () => Lang.T("done_flatten"), requireImage: false);

            // Podešavanja
            _commands["looks"] = ChooseLook;
            _commands["autotone"] = () => _ = RunOpAsync(Lang.T("menu_auto_tone"), () => EditOps.Auto(_project, EditOps.AutoKind.Tone));
            _commands["autocontrast"] = () => _ = RunOpAsync(Lang.T("menu_auto_contrast"), () => EditOps.Auto(_project, EditOps.AutoKind.Contrast));
            _commands["autocolor"] = () => _ = RunOpAsync(Lang.T("menu_auto_color"), () => EditOps.Auto(_project, EditOps.AutoKind.Color));
            _commands["aisuggest"] = () => _ = SuggestionsAsync();
            _commands["bake"] = () => _ = RunOpAsync(Lang.T("menu_bake"), () => EditOps.BakeAdjustments(_project), () => Lang.T("done_bake"));
            _commands["reset"] = () =>
            {
                if (!_project.HasAdjustments) { SetStatus(Lang.T("nothing_to_reset")); return; }
                Checkpoint(Lang.T("menu_reset")); _project.ResetAdjustments(); RefreshList(); RefreshPreview(); SetStatus(Lang.T("done_reset"));
            };
            _commands["refilter"] = () =>
            {
                if (_lastFilter == null) { SetStatus(Lang.T("no_last_filter")); return; }
                RunFilter(_lastFilter.Value.kind, _lastFilter.Value.amount);
            };

            // Retuš
            _commands["removeobj"] = () => _ = RemoveObjectAsync();
            _commands["removesel"] = () => _ = RunOpAsync(Lang.T("menu_remove_selected"), () => EditOps.Inpaint(_project, EditOps.FullMask(_project)), () => Lang.T("done_removed"), requireSelection: true);
            _commands["spot"] = SpotHeal;
            _commands["redeyeai"] = () => _ = RedEyeAiAsync();
            _commands["redeyesel"] = () => _ = RedEyeSelectionAsync();

            // AI
            _commands["describe"] = () => _ = DescribeAsync();
            _commands["describelook"] = () => _ = DescribeLookAsync();
            _commands["removebg"] = () => _ = BackgroundAsync(BgAction.Remove);
            _commands["bgcolor"] = () => _ = BackgroundAsync(BgAction.Color);
            _commands["bgimage"] = () => _ = BackgroundAsync(BgAction.Image);
            _commands["bgblur"] = () => _ = BackgroundAsync(BgAction.Blur);
            _commands["extract"] = () => _ = ExtractObjectAsync();

            // Slojevi
            _commands["addtext"] = AddTextLayer;
            _commands["addrect"] = () => AddShapeLayer(ShapeKind.Rectangle);
            _commands["addellipse"] = () => AddShapeLayer(ShapeKind.Ellipse);
            _commands["addline"] = () => AddShapeLayer(ShapeKind.Line);
            _commands["addimage"] = AddImageLayer;
            _commands["addgradient"] = AddGradientLayer;
            _commands["layerprops"] = () => { if (RequireLayer()) OpenLayerProperties(_selectedLayer!); };
            _commands["duplayer"] = DuplicateLayer;
            _commands["dellayer"] = DeleteLayer;
            _commands["layerup"] = () => MoveLayer(+1);
            _commands["layerdown"] = () => MoveLayer(-1);

            BuildDocumentCommands();

            // Ostalo
            _commands["proofdoc"] = ProofreadDocument;
            _commands["shortcuts"] = () => new ReportWindow(this, Lang.T("menu_shortcuts"), Lang.T("shortcuts_text")).ShowDialog();
            _commands["log"] = () => new LogWindow(this).Show();
            _commands["about"] = () => new AboutWindow(this).ShowDialog();
            _commands["support"] = () => UltraSupport.SupportInfo.Show(this, "Ultra Studio", Lang.Current == "sr");
        }

        // ════════════════════════════════════════════════════════════════
        // ZAJEDNIČKO
        // ════════════════════════════════════════════════════════════════
        private string SizeDone() => string.Format(Lang.T("done_size"), _project.CanvasWidth, _project.CanvasHeight);

        /// <summary>Destruktivna izmena u pozadini: Checkpoint, rad, osvežavanje; greška = vraćanje stanja.</summary>
        private async Task<bool> RunOpAsync(string label, Action op, Func<string>? done = null,
            bool requireImage = true, bool requireSelection = false)
        {
            if (_busy) { SetStatus(Lang.T("status_busy")); return false; }
            if (requireImage && !_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return false; }
            if (requireSelection && !_project.Selection.IsActive) { SetStatus(Lang.T("need_selection")); return false; }
            _busy = true;
            Checkpoint(label);
            SetStatus(string.Format(Lang.T("status_working"), label));
            try
            {
                await Task.Run(op);
                _busy = false;
                RefreshAll();
                SetStatus(done?.Invoke() ?? string.Format(Lang.T("status_done"), label));
                return true;
            }
            catch (Exception ex)
            {
                _undo.Rollback(_project);
                _busy = false;
                RefreshAll();
                ShowError(ex);
                return false;
            }
            finally { _busy = false; }
        }

        private bool RequireLayer()
        {
            if (_selectedLayer != null && _project.Layers.Contains(_selectedLayer)) return true;
            SetStatus(Lang.T("layer_none_selected"));
            return false;
        }

        private string? AskText(string title, string prompt, string value)
        {
            var dlg = new SetValueDialog(title, prompt, value, "") { Owner = this };
            return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.ResultValue) ? dlg.ResultValue.Trim() : null;
        }

        private double? AskNumber(string title, string prompt, double value, double min, double max)
        {
            var f = new FormDialog(this, title, null, new[] { FormField.Num("v", prompt, value, min, max) });
            return f.ShowDialog() == true ? f.Numbers["v"] : null;
        }

        private string[] Positions() => Enumerable.Range(0, 9).Select(i => Lang.T($"pos_{i / 3}{i % 3}")).ToArray();

        /// <summary>Postavlja okvir na jedno od 9 mesta (sa marginom 5%) — za dodavanje slojeva tastaturom.</summary>
        private void PlaceAt(Layer l, int pos)
        {
            double m = Math.Min(_project.CanvasWidth, _project.CanvasHeight) * 0.05;
            int col = pos % 3, row = pos / 3;
            l.X = col == 0 ? m : col == 1 ? (_project.CanvasWidth - l.Width) / 2 : _project.CanvasWidth - l.Width - m;
            l.Y = row == 0 ? m : row == 1 ? (_project.CanvasHeight - l.Height) / 2 : _project.CanvasHeight - l.Height - m;
            l.X = Math.Round(l.X); l.Y = Math.Round(l.Y);
        }

        private string NextLayerName(string prefixKey)
        {
            string name = $"{Lang.T(prefixKey)} {_project.NextLayerNumber}";
            _project.NextLayerNumber++;
            return name;
        }

        /// <summary>Base64 JPEG trenutnog izgleda (sa slojevima ili bez) za AI.</summary>
        private async Task<string> SnapshotBase64Async(bool withLayers, uint maxDim, (int x, int y, int w, int h)? crop = null)
        {
            var snap = ProjectJson.Clone(_project);
            uint? renderDim = crop == null ? maxDim : null; // bez isecanja odmah renderuj umanjeno (brže)
            return await Task.Run(() =>
            {
                using var img = withLayers ? CanvasEngine.RenderComposite(snap, renderDim, out _) : CanvasEngine.RenderBase(snap, renderDim, out _);
                if (crop != null)
                {
                    img.Crop(new ImageMagick.MagickGeometry(crop.Value.x, crop.Value.y, (uint)crop.Value.w, (uint)crop.Value.h));
                    img.ResetPage();
                }
                if (img.Width > maxDim || img.Height > maxDim) img.Resize(maxDim, maxDim);
                img.BackgroundColor = ImageMagick.MagickColors.White;
                img.Alpha(ImageMagick.AlphaOption.Remove);
                img.Format = ImageMagick.MagickFormat.Jpeg;
                img.Quality = 90;
                return Convert.ToBase64String(img.ToByteArray());
            });
        }

        // ════════════════════════════════════════════════════════════════
        // FAJL
        // ════════════════════════════════════════════════════════════════
        private void NewCanvas()
        {
            if (!ConfirmDiscard()) return;
            var presets = CanvasPresets.All;
            var f = new FormDialog(this, Lang.T("menu_new_canvas"), Lang.T("new_canvas_hint"), new[]
            {
                FormField.Choice("preset", Lang.T("new_canvas_preset"), presets.Select(CanvasPresets.Label).ToArray(), 0),
                FormField.Num("w", Lang.T("canvas_width_prompt"), 1200, 1, 20000),
                FormField.Num("h", Lang.T("canvas_height_prompt"), 800, 1, 20000),
                FormField.Choice("bg", Lang.T("new_canvas_bg"), new[] { Lang.T("bg_white"), Lang.T("bg_black"), Lang.T("bg_transparent"), Lang.T("bg_custom") }, 0),
                FormField.Text("bghex", Lang.T("new_canvas_bg_hex"), "#FFFFFF"),
            });
            if (f.ShowDialog() != true) return;
            var preset = presets[f.Choices["preset"]];
            int w = preset.Width > 0 ? preset.Width : (int)f.Numbers["w"];
            int h = preset.Height > 0 ? preset.Height : (int)f.Numbers["h"];
            string bg = f.Choices["bg"] switch { 0 => "#FFFFFF", 1 => "#000000", 2 => "transparent", _ => f.Text["bghex"].Trim() };

            ProjectJson.Restore(_project, ProjectJson.Serialize(new ImageProject()));
            _project.ProjectPath = null;
            _project.CanvasWidth = w; _project.CanvasHeight = h; _project.CanvasBackgroundHex = bg; _project.BlankCanvas = true; _project.Dpi = preset.Dpi;
            _selectedLayer = null;
            _undo.Clear();
            RefreshAll();
            SetStatus(string.Format(Lang.T("canvas_created"), w, h) + (preset.Width > 0 ? $" — {Lang.T(preset.Key)}" : ""));
        }

        private void OpenFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = Lang.T("menu_open"),
                Filter = Lang.T("open_filter") + "|*.ustudio;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp;*.gif;*.heic|" +
                         Lang.T("project_filter") + "|*.ustudio|" + ImageFilter
            };
            if (dlg.ShowDialog(this) != true) return;
            if (!ConfirmDiscard()) return;
            OpenPath(dlg.FileName);
        }

        private async void OpenPath(string path)
        {
            try
            {
                _busy = true;
                SetStatus(string.Format(Lang.T("status_working"), Path.GetFileName(path)));
                if (path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    var tmp = new ImageProject();
                    await Task.Run(() => ProjectFile.Load(tmp, path));
                    ProjectJson.Restore(_project, ProjectJson.Serialize(tmp));
                    _project.ProjectPath = path;
                    _undo.Clear();
                    _selectedLayer = null;
                    _busy = false;
                    RefreshAll();
                    SetStatus(string.Format(Lang.T("project_loaded"), Path.GetFileName(path), _project.Layers.Count));
                    return;
                }

                // Fotografija: telefonske slike nose EXIF orijentaciju — pravimo uspravnu radnu kopiju,
                // da se AI selekcija (SAM) i koordinate poklope sa onim što korisnik vidi.
                var (oriented, w, h) = await Task.Run(() =>
                {
                    using var img = new ImageMagick.MagickImage(path);
                    bool needs = img.Orientation != ImageMagick.OrientationType.TopLeft && img.Orientation != ImageMagick.OrientationType.Undefined;
                    string? copy = null;
                    if (needs)
                    {
                        img.AutoOrient();
                        copy = Session.NewFile("oriented");
                        ImageEngine.SavePng(img, copy);
                    }
                    return (copy, (int)img.Width, (int)img.Height);
                });

                ProjectJson.Restore(_project, ProjectJson.Serialize(new ImageProject()));
                _project.ProjectPath = null;
                _project.OriginalPath = path;
                _project.BasePath = oriented;
                _project.OriginalWidth = w; _project.OriginalHeight = h;
                _project.CanvasWidth = w; _project.CanvasHeight = h;
                _selectedLayer = null;
                _undo.Clear();
                _busy = false;
                RefreshAll();
                SetStatus(string.Format(Lang.T("img_loaded"), Path.GetFileName(path), w, h));
            }
            catch (Exception ex) { _busy = false; ShowError(ex); }
            finally { _busy = false; }
        }

        private bool SaveProject(bool saveAs)
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("status_no_canvas")); return false; }
            string? path = _project.ProjectPath;
            if (saveAs || path == null)
            {
                var dlg = new SaveFileDialog
                {
                    Filter = Lang.T("project_filter") + "|*.ustudio",
                    FileName = (_project.OriginalPath != null ? Path.GetFileNameWithoutExtension(_project.OriginalPath) : Lang.T("untitled")) + ProjectFile.Extension,
                    InitialDirectory = _project.OriginalPath != null ? Path.GetDirectoryName(_project.OriginalPath) : null
                };
                if (dlg.ShowDialog(this) != true) return false;
                path = dlg.FileName;
            }
            try
            {
                ProjectFile.Save(_project, path!);
                _undo.Dirty = false;
                UpdateTitle();
                SetStatus(string.Format(Lang.T("project_saved"), path));
                return true;
            }
            catch (Exception ex) { ShowError(ex); return false; }
        }

        private async void ExportImage()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("status_no_canvas")); return; }
            int fmt = Array.IndexOf(ExportSizes.Formats, _lastExport.Format);
            int size = ExportSizes.All.FindIndex(s => s.W == _lastExport.TargetWidth && s.H == _lastExport.TargetHeight);
            var f = new FormDialog(this, Lang.T("menu_export"), Lang.T("export_hint"), new[]
            {
                FormField.Choice("fmt", Lang.T("export_format"), ExportSizes.Formats.Select(x => x.ToUpperInvariant()).ToArray(), Math.Max(0, fmt)),
                FormField.Num("q", Lang.T("export_quality"), _lastExport.Quality, 1, 100),
                FormField.Choice("size", Lang.T("export_size"), ExportSizes.All.Select(ExportSizes.Label).ToArray(), Math.Max(0, size)),
            });
            if (f.ShowDialog() != true) return;
            var s = ExportSizes.All[f.Choices["size"]];
            _lastExport = new CanvasEngine.ExportOptions
            {
                Format = ExportSizes.Formats[f.Choices["fmt"]], Quality = (int)f.Numbers["q"],
                TargetWidth = s.W, TargetHeight = s.H, CoverCrop = s.Cover
            };
            string ext = _lastExport.Format;
            string baseName = _project.ProjectPath != null ? Path.GetFileNameWithoutExtension(_project.ProjectPath)
                            : _project.OriginalPath != null ? Path.GetFileNameWithoutExtension(_project.OriginalPath) + "_edited" : "design";
            if (s.H > 0) baseName += "_" + s.W + "x" + s.H;
            var dlg = new SaveFileDialog
            {
                Filter = $"{ext.ToUpperInvariant()}|*.{ext}",
                FileName = baseName + "." + ext,
                InitialDirectory = _lastExportFolder ?? (_project.OriginalPath != null ? Path.GetDirectoryName(_project.OriginalPath) : null)
            };
            if (dlg.ShowDialog(this) != true) return;
            _lastExportFolder = Path.GetDirectoryName(dlg.FileName);

            _busy = true;
            SetStatus(string.Format(Lang.T("status_working"), Lang.T("menu_export")));
            var snap = ProjectJson.Clone(_project);
            var opt = _lastExport;
            try
            {
                await Task.Run(() => CanvasEngine.Export(snap, dlg.FileName, opt));
                long kb = new FileInfo(dlg.FileName).Length / 1024;
                SetStatus(string.Format(Lang.T("img_saved"), dlg.FileName) + $" ({kb} KB)");
            }
            catch (Exception ex) { ShowError(ex); }
            finally { _busy = false; }
        }

        // ════════════════════════════════════════════════════════════════
        // UNDO / ISTORIJA
        // ════════════════════════════════════════════════════════════════
        private void UndoRedo(bool undo)
        {
            string? label = undo ? _undo.Undo(_project) : _undo.Redo(_project);
            if (label == null) { SetStatus(Lang.T(undo ? "nothing_to_undo" : "nothing_to_redo")); return; }
            _selectedLayer = _project.Layers.FirstOrDefault(l => l.Id == _selectedLayer?.Id);
            RefreshAll();
            SetStatus(string.Format(Lang.T(undo ? "undone" : "redone"), label));
        }

        private void ShowHistory()
        {
            var w = new HistoryWindow(this, _undo.UndoList.Select(x => x.label).ToList(), _undo.RedoList.Select(x => x.label).ToList());
            if (w.ShowDialog() != true || w.Steps == 0) return;
            string? last = null;
            for (int i = 0; i < Math.Abs(w.Steps); i++) last = w.Steps > 0 ? _undo.Undo(_project) : _undo.Redo(_project);
            _selectedLayer = _project.Layers.FirstOrDefault(l => l.Id == _selectedLayer?.Id);
            RefreshAll();
            SetStatus(string.Format(Lang.T("history_moved"), Math.Abs(w.Steps), last ?? ""));
        }

        // ════════════════════════════════════════════════════════════════
        // SELEKCIJA
        // ════════════════════════════════════════════════════════════════
        private void SetSelection(Selection s, string label)
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            Checkpoint(label);
            _project.Selection = s;
            RefreshPreview();
            SetStatus(EditOps.DescribeSelection(_project));
        }

        private void InvertSelection()
        {
            if (!_project.Selection.IsActive) { SetStatus(Lang.T("need_selection")); return; }
            var s = _project.Selection.Clone();
            s.Inverted = !s.Inverted;
            SetSelection(s, Lang.T("menu_invert_selection"));
        }

        private void Feather()
        {
            if (!_project.Selection.IsActive) { SetStatus(Lang.T("need_selection")); return; }
            var v = AskNumber(Lang.T("menu_feather"), Lang.T("feather_prompt"), _project.Selection.Feather, 0, 500);
            if (v == null) return;
            var s = _project.Selection.Clone();
            s.Feather = v.Value;
            SetSelection(s, Lang.T("menu_feather"));
        }

        private void SelectByNumbers(SelectionKind kind)
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            var cur = _project.Selection.Kind is SelectionKind.Rectangle or SelectionKind.Ellipse ? _project.Selection : null;
            var f = new FormDialog(this, Lang.T(kind == SelectionKind.Ellipse ? "menu_select_ellipse" : "menu_select_rect"),
                string.Format(Lang.T("sel_numbers_hint"), _project.CanvasWidth, _project.CanvasHeight), new[]
            {
                FormField.Choice("unit", Lang.T("sel_unit"), new[] { Lang.T("unit_px"), Lang.T("unit_pct") }, 0),
                FormField.Num("x", Lang.T("layer_field_x"), cur?.X ?? 0),
                FormField.Num("y", Lang.T("layer_field_y"), cur?.Y ?? 0),
                FormField.Num("w", Lang.T("layer_field_width"), cur?.Width ?? _project.CanvasWidth / 2, 1),
                FormField.Num("h", Lang.T("layer_field_height"), cur?.Height ?? _project.CanvasHeight / 2, 1),
                FormField.Num("feather", Lang.T("feather_prompt"), cur?.Feather ?? 0, 0, 500),
            });
            if (f.ShowDialog() != true) return;
            bool pct = f.Choices["unit"] == 1;
            double fx = pct ? _project.CanvasWidth / 100.0 : 1, fy = pct ? _project.CanvasHeight / 100.0 : 1;
            SetSelection(new Selection
            {
                Kind = kind, X = f.Numbers["x"] * fx, Y = f.Numbers["y"] * fy,
                Width = f.Numbers["w"] * fx, Height = f.Numbers["h"] * fy, Feather = f.Numbers["feather"]
            }, Lang.T(kind == SelectionKind.Ellipse ? "menu_select_ellipse" : "menu_select_rect"));
        }

        private void SelectPresetArea()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            double W = _project.CanvasWidth, H = _project.CanvasHeight, sq = Math.Min(W, H);
            var areas = new (string key, double x, double y, double w, double h, SelectionKind k)[]
            {
                ("area_all", 0, 0, W, H, SelectionKind.Rectangle),
                ("area_left_half", 0, 0, W / 2, H, SelectionKind.Rectangle),
                ("area_right_half", W / 2, 0, W / 2, H, SelectionKind.Rectangle),
                ("area_top_half", 0, 0, W, H / 2, SelectionKind.Rectangle),
                ("area_bottom_half", 0, H / 2, W, H / 2, SelectionKind.Rectangle),
                ("area_center", W / 4, H / 4, W / 2, H / 2, SelectionKind.Rectangle),
                ("area_center_square", (W - sq) / 2, (H - sq) / 2, sq, sq, SelectionKind.Rectangle),
                ("area_center_circle", (W - sq * 0.8) / 2, (H - sq * 0.8) / 2, sq * 0.8, sq * 0.8, SelectionKind.Ellipse),
                ("area_left_third", 0, 0, W / 3, H, SelectionKind.Rectangle),
                ("area_middle_third_v", W / 3, 0, W / 3, H, SelectionKind.Rectangle),
                ("area_right_third", W * 2 / 3, 0, W / 3, H, SelectionKind.Rectangle),
                ("area_top_third", 0, 0, W, H / 3, SelectionKind.Rectangle),
                ("area_middle_third_h", 0, H / 3, W, H / 3, SelectionKind.Rectangle),
                ("area_bottom_third", 0, H * 2 / 3, W, H / 3, SelectionKind.Rectangle),
                ("area_sky", 0, 0, W, H * 0.4, SelectionKind.Rectangle),
            };
            var dlg = new ChoiceDialog(this, Lang.T("menu_select_preset"), Lang.T("sel_preset_prompt"), areas.Select(a => Lang.T(a.key)).ToList(), 0);
            if (dlg.ShowDialog() != true) return;
            var a = areas[dlg.SelectedIndex];
            SetSelection(new Selection { Kind = a.k, X = Math.Round(a.x), Y = Math.Round(a.y), Width = Math.Round(a.w), Height = Math.Round(a.h), Label = Lang.T(a.key) }, Lang.T("menu_select_preset"));
        }

        private async Task ColorRangeAsync()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            var ranges = Enum.GetValues<PixelOps.ColorRange>();
            var f = new FormDialog(this, Lang.T("menu_color_range"), Lang.T("color_range_hint"), new[]
            {
                FormField.Choice("range", Lang.T("color_range_which"), ranges.Select(r => Lang.T("cr_" + r)).ToArray(), 0),
                FormField.Text("hex", Lang.T("color_range_custom"), "#3366CC"),
                FormField.Num("fz", Lang.T("color_range_fuzz"), 40, 0, 100),
            });
            if (f.ShowDialog() != true) return;
            var range = ranges[f.Choices["range"]];
            var custom = ImageEngine.ColorBytes(f.Text["hex"]);
            double fz = f.Numbers["fz"];
            var snap = ProjectJson.Clone(_project);
            _busy = true;
            SetStatus(string.Format(Lang.T("status_working"), Lang.T("menu_color_range")));
            try
            {
                var (mask, w, h) = await Task.Run(() =>
                {
                    using var img = CanvasEngine.RenderBase(snap, 2048, out _);
                    int iw = (int)img.Width, ih = (int)img.Height;
                    var px = ImageEngine.ToRgba(img);
                    return (PixelOps.ColorRangeMask(px, iw, ih, range, fz, (custom.r, custom.g, custom.b)), iw, ih);
                });
                double cov = PixelOps.Coverage(mask) * 100;
                _busy = false;
                if (cov < 0.05) { SetStatus(Lang.T("color_range_empty")); return; }
                Checkpoint(Lang.T("menu_color_range"));
                EditOps.SetMask(_project, mask, w, h, Lang.T("cr_" + range));
                RefreshPreview();
                SetStatus(EditOps.DescribeSelection(_project));
            }
            catch (Exception ex) { ShowError(ex); }
            finally { _busy = false; }
        }

        private async Task WhatsInSelectionAsync()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            var b = EditOps.SelectionBounds(_project);
            if (b == null) { SetStatus(Lang.T("need_selection")); return; }
            SetAiResult(Lang.T("ai_describing"));
            try
            {
                var b64 = await SnapshotBase64Async(true, 1024, b);
                var text = await _ai.DescribeRegionAsync(b64);
                SetAiResult(EditOps.DescribeSelection(_project) + "\n\n" + text);
            }
            catch (Exception ex) { SetAiResult(string.Format(Lang.T("ai_error"), ex.Message)); }
        }

        // ════════════════════════════════════════════════════════════════
        // AI MASKA OBJEKTA — Ollama nađe tačku, SAM izdvoji tačan oblik.
        // Bez SAM modela: Ollama vrati okvir, selekcija je elipsa u njemu.
        // ════════════════════════════════════════════════════════════════
        private async Task<(byte[] mask, string label)?> SubjectMaskAsync(string? description, ProgressDialog progress)
        {
            string what = string.IsNullOrWhiteSpace(description) ? "the main subject of the photo (the person, animal or object in focus)" : description!;
            string label = string.IsNullOrWhiteSpace(description) ? Lang.T("sel_subject") : "AI: " + description;
            int W = _project.CanvasWidth, H = _project.CanvasHeight;

            progress.SetStage(Lang.T("extract_locating"));
            string b64 = await SnapshotBase64Async(false, 1280);

            if (!SamSegmenter.ModelsAvailable)
            {
                var box = await _ai.FindBoxForDescriptionAsync(b64, W, H, what);
                if (box == null) return null;
                var sel = new Selection { Kind = SelectionKind.Ellipse, X = box.Value.x, Y = box.Value.y, Width = box.Value.w, Height = box.Value.h, Feather = Math.Max(2, Math.Min(W, H) / 200.0) };
                var m = await Task.Run(() => ImageEngine.BuildMask(sel, W, H));
                SetStatus(Lang.T("sam_fallback_box"));
                return (m, label);
            }

            var point = await _ai.FindPointForDescriptionAsync(b64, W, H, what);
            if (point == null) return null;

            progress.SetStage(Lang.T("extract_segmenting"));
            _sam ??= new SamSegmenter();
            await _sam.EnsureEmbeddingAsync(_project.SourcePath!);
            var (mask, maskScale) = await _sam.SegmentFromPointAsync(point.Value.x, point.Value.y);
            var full = await Task.Run(() =>
            {
                int mh = mask.GetLength(0), mw = mask.GetLength(1);
                var g = new byte[mw * mh];
                for (int y = 0; y < mh; y++) for (int x = 0; x < mw; x++) g[y * mw + x] = mask[y, x] ? (byte)255 : (byte)0;
                using var mi = ImageEngine.FromGray(g, mw, mh);
                mi.Resize(new ImageMagick.MagickGeometry((uint)W, (uint)H) { IgnoreAspectRatio = true });
                mi.Blur(0, 0.8); // meka ivica od 1px — bez "stepenica" na rubu
                return ImageEngine.ToGray(mi);
            });
            return (full, label);
        }

        private async Task<(byte[] mask, string label)?> RunSubjectMaskAsync(string? description, string title)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return null; }
            if (_busy) { SetStatus(Lang.T("status_busy")); return null; }
            _busy = true;
            var progress = new ProgressDialog(title, this);
            progress.Show();
            try
            {
                var r = await SubjectMaskAsync(description, progress);
                if (r == null)
                {
                    string msg = string.Format(Lang.T("extract_not_found"), description ?? Lang.T("sel_subject"));
                    progress.Close();
                    SetStatus(msg);
                    MessageBox.Show(this, msg, Lang.T("error_title"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return r;
            }
            catch (Exception ex) { progress.Close(); ShowError(ex); return null; }
            finally
            {
                if (progress.IsVisible) progress.Close();
                _busy = false;
                Activate();
            }
        }

        private async Task AiSelectAsync(string? description)
        {
            var r = await RunSubjectMaskAsync(description, Lang.T("menu_select_subject"));
            if (r == null) return;
            Checkpoint(Lang.T("menu_select_subject"));
            EditOps.SetMask(_project, r.Value.mask, _project.CanvasWidth, _project.CanvasHeight, r.Value.label);
            RefreshPreview();
            SetStatus(EditOps.DescribeSelection(_project));
        }

        /// <summary>Objekat za operacije nad pozadinom: aktivna selekcija ako postoji, inače AI.</summary>
        private async Task<byte[]?> SubjectForBackgroundAsync(string title)
        {
            if (_project.Selection.IsActive)
            {
                SetStatus(Lang.T("using_selection_as_subject"));
                return EditOps.FullMask(_project);
            }
            var r = await RunSubjectMaskAsync(null, title);
            return r?.mask;
        }

        private enum BgAction { Remove, Color, Image, Blur }

        private async Task BackgroundAsync(BgAction action)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            string title = Lang.T(action switch
            {
                BgAction.Remove => "menu_remove_bg", BgAction.Color => "menu_replace_bg_color",
                BgAction.Image => "menu_replace_bg_image", _ => "menu_blur_bg"
            });
            string? color = null, image = null;
            double amount = 50;
            if (action == BgAction.Color)
            {
                color = AskText(title, Lang.T("bg_color_prompt"), "#FFFFFF");
                if (color == null) return;
            }
            else if (action == BgAction.Image)
            {
                var d = new OpenFileDialog { Title = title, Filter = ImageFilter };
                if (d.ShowDialog(this) != true) return;
                image = d.FileName;
            }
            else if (action == BgAction.Blur)
            {
                var v = AskNumber(title, Lang.T("strength_prompt"), 50, 1, 100);
                if (v == null) return;
                amount = v.Value;
            }

            var subject = await SubjectForBackgroundAsync(title);
            if (subject == null) return;
            await RunOpAsync(title, () =>
            {
                switch (action)
                {
                    case BgAction.Remove: EditOps.RemoveBackground(_project, subject); break;
                    case BgAction.Color: EditOps.ReplaceBackground(_project, subject, color, null); break;
                    case BgAction.Image: EditOps.ReplaceBackground(_project, subject, null, image); break;
                    case BgAction.Blur: EditOps.BlurBackground(_project, subject, amount); break;
                }
                _project.Selection = new Selection();
            }, () => Lang.T(action == BgAction.Remove ? "done_bg_removed" : "done_bg_changed"));
        }

        private async Task ExtractObjectAsync()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            string? desc = _project.Selection.IsActive ? null : AskText(Lang.T("extract_prompt_title"), Lang.T("extract_prompt"), "");
            if (!_project.Selection.IsActive && desc == null) return;
            byte[]? mask = _project.Selection.IsActive ? EditOps.FullMask(_project) : (await RunSubjectMaskAsync(desc, Lang.T("extract_prompt_title")))?.mask;
            if (mask == null) return;
            string name = desc ?? (_project.Selection.Label.Length > 0 ? _project.Selection.Label : Lang.T("sel_kind_mask"));
            string? savedNext = null;
            await RunOpAsync(Lang.T("menu_extract_object"), () =>
            {
                var c = EditOps.Cutout(_project, mask) ?? throw new InvalidOperationException(Lang.T("need_selection"));
                _project.Layers.Add(new ImageLayer { Name = Lang.T("layer_object") + ": " + name, SourcePath = c.path, X = c.x, Y = c.y, Width = c.w, Height = c.h });
                // Kopija pored originala, kao ranije — da isečak bude i običan PNG fajl
                try
                {
                    if (_project.OriginalPath != null)
                    {
                        string safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '_');
                        savedNext = Path.Combine(Path.GetDirectoryName(_project.OriginalPath)!, Path.GetFileNameWithoutExtension(_project.OriginalPath) + "_" + safe + "_cutout.png");
                        File.Copy(c.path, savedNext, true);
                    }
                }
                catch { savedNext = null; }
            }, () => string.Format(Lang.T("done_extract"), name) + (savedNext != null ? " " + string.Format(Lang.T("done_extract_file"), savedNext) : ""), requireImage: true);
            _selectedLayer = _project.Layers.LastOrDefault();
            RefreshLayersUi();
        }

        // ════════════════════════════════════════════════════════════════
        // SLIKA (isecanje, veličina, rotacija)
        // ════════════════════════════════════════════════════════════════
        private void CropToSelection()
        {
            var b = EditOps.SelectionBounds(_project);
            if (b == null) { SetStatus(Lang.T("need_selection")); return; }
            _ = RunOpAsync(Lang.T("menu_crop_selection"), () => EditOps.Crop(_project, b.Value.x, b.Value.y, b.Value.w, b.Value.h), SizeDone, requireImage: false);
        }

        private async Task CropToRatioAsync()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            var ratios = new (string label, double w, double h)[]
            {
                ("1:1 — " + Lang.T("ratio_square"), 1, 1), ("4:5 — " + Lang.T("ratio_ig_portrait"), 4, 5), ("5:4", 5, 4),
                ("16:9 — " + Lang.T("ratio_wide"), 16, 9), ("9:16 — " + Lang.T("ratio_story"), 9, 16),
                ("3:2 — " + Lang.T("ratio_photo"), 3, 2), ("2:3", 2, 3), ("4:3", 4, 3), ("3:4", 3, 4),
                ("21:9 — " + Lang.T("ratio_cinema"), 21, 9), ("1.91:1 — Facebook / LinkedIn", 1.91, 1),
            };
            var f = new FormDialog(this, Lang.T("menu_crop_ratio"), Lang.T("crop_ratio_hint"), new[]
            {
                FormField.Choice("r", Lang.T("crop_ratio_which"), ratios.Select(r => r.label).ToArray(), 0),
                FormField.Text("focus", Lang.T("crop_focus_prompt"), ""),
            });
            if (f.ShowDialog() != true) return;
            var ratio = ratios[f.Choices["r"]];
            string focus = f.Text["focus"].Trim();
            double? cx = null, cy = null;
            if (focus.Length > 0 && _project.HasImage)
            {
                _busy = true;
                SetStatus(Lang.T("extract_locating"));
                try
                {
                    var pt = await _ai.FindPointForDescriptionAsync(await SnapshotBase64Async(false, 1280), _project.CanvasWidth, _project.CanvasHeight, focus);
                    if (pt != null) { cx = pt.Value.x; cy = pt.Value.y; }
                    else SetStatus(string.Format(Lang.T("extract_not_found"), focus));
                }
                catch (Exception ex) { SetStatus(string.Format(Lang.T("ai_error"), ex.Message)); }
                finally { _busy = false; }
            }
            var r = EditOps.RatioRect(_project.CanvasWidth, _project.CanvasHeight, ratio.w, ratio.h, cx, cy);
            await RunOpAsync(Lang.T("menu_crop_ratio"), () => EditOps.Crop(_project, r.x, r.y, r.w, r.h), SizeDone, requireImage: false);
        }

        private void CropByNumbers()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            var f = new FormDialog(this, Lang.T("menu_crop_numbers"), string.Format(Lang.T("sel_numbers_hint"), _project.CanvasWidth, _project.CanvasHeight), new[]
            {
                FormField.Num("x", Lang.T("layer_field_x"), 0, 0, _project.CanvasWidth - 1),
                FormField.Num("y", Lang.T("layer_field_y"), 0, 0, _project.CanvasHeight - 1),
                FormField.Num("w", Lang.T("layer_field_width"), _project.CanvasWidth, 1, _project.CanvasWidth),
                FormField.Num("h", Lang.T("layer_field_height"), _project.CanvasHeight, 1, _project.CanvasHeight),
            });
            if (f.ShowDialog() != true) return;
            _ = RunOpAsync(Lang.T("menu_crop_numbers"), () => EditOps.Crop(_project, (int)f.Numbers["x"], (int)f.Numbers["y"], (int)f.Numbers["w"], (int)f.Numbers["h"]), SizeDone, requireImage: false);
        }

        private void ImageSize()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            var f = new FormDialog(this, Lang.T("menu_image_size"), string.Format(Lang.T("image_size_hint"), _project.CanvasWidth, _project.CanvasHeight), new[]
            {
                FormField.Num("pct", Lang.T("image_size_percent"), 0, 0, 1000),
                FormField.Num("w", Lang.T("layer_field_width"), _project.CanvasWidth, 0, 30000),
                FormField.Num("h", Lang.T("image_size_height_auto"), 0, 0, 30000),
            });
            if (f.ShowDialog() != true) return;
            double pct = f.Numbers["pct"], w = f.Numbers["w"], h = f.Numbers["h"];
            int nw, nh;
            if (pct > 0) { nw = (int)Math.Round(_project.CanvasWidth * pct / 100); nh = (int)Math.Round(_project.CanvasHeight * pct / 100); }
            else if (w > 0 && h > 0) { nw = (int)w; nh = (int)h; }
            else if (w > 0) { nw = (int)w; nh = (int)Math.Round(w * _project.CanvasHeight / _project.CanvasWidth); }
            else if (h > 0) { nh = (int)h; nw = (int)Math.Round(h * _project.CanvasWidth / _project.CanvasHeight); }
            else { SetStatus(Lang.T("image_size_nothing")); return; }
            nw = Math.Max(1, nw); nh = Math.Max(1, nh);
            _ = RunOpAsync(Lang.T("menu_image_size"), () => EditOps.Resize(_project, nw, nh), SizeDone, requireImage: false);
        }

        private void CanvasSizeDialog()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            var f = new FormDialog(this, Lang.T("menu_canvas_size"), string.Format(Lang.T("image_size_hint"), _project.CanvasWidth, _project.CanvasHeight), new[]
            {
                FormField.Num("w", Lang.T("canvas_width_prompt"), _project.CanvasWidth, 1, 30000),
                FormField.Num("h", Lang.T("canvas_height_prompt"), _project.CanvasHeight, 1, 30000),
                FormField.Choice("anchor", Lang.T("canvas_anchor"), Positions(), 4),
                FormField.Text("bg", Lang.T("canvas_extend_color"), "transparent"),
            });
            if (f.ShowDialog() != true) return;
            int nw = (int)f.Numbers["w"], nh = (int)f.Numbers["h"], anchor = f.Choices["anchor"];
            string bg = f.Text["bg"];
            _ = RunOpAsync(Lang.T("menu_canvas_size"), () => EditOps.CanvasSize(_project, nw, nh, anchor, bg), SizeDone, requireImage: false);
        }

        private void RotateAny()
        {
            var v = AskNumber(Lang.T("menu_rotate_any"), Lang.T("rotate_prompt"), 0, -360, 360);
            if (v == null || v.Value == 0) return;
            double a = v.Value;
            _ = RunOpAsync(Lang.T("menu_rotate_any"), () => EditOps.Rotate(_project, a), SizeDone, requireImage: false);
        }

        // ════════════════════════════════════════════════════════════════
        // IZGLEDI, AUTO, FILTERI
        // ════════════════════════════════════════════════════════════════
        private void ChooseLook()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            var looks = LookPresets.All;
            Checkpoint(Lang.T("menu_looks"));
            var before = ProjectJson.Clone(_project);
            // Prva stavka = "kako je sada", da otvaranje liste ništa ne promeni dok se ne pomeri strelica
            var names = new List<string> { Lang.T("look_current") };
            names.AddRange(looks.Select(LookPresets.Name));
            void ApplyIndex(int i)
            {
                if (i <= 0) _project.CopyAdjustmentsFrom(before);
                else LookPresets.Apply(_project, looks[i - 1]);
            }
            ChoiceDialog? dlg = null;
            dlg = new ChoiceDialog(this, Lang.T("menu_looks"), Lang.T("looks_prompt"), names, 0,
                description: i => i == 0 ? Lang.T("look_current_desc") : LookPresets.Description(looks[i - 1]),
                onSelect: i => { ApplyIndex(i); RefreshPreview(); },
                extraButton: (Lang.T("looks_ai_describe"), async i =>
                {
                    dlg!.SetDescription(Lang.T("ai_describing"));
                    try { dlg.SetDescription(await _ai.DescribeLookAsync(await SnapshotBase64Async(false, 1024))); }
                    catch (Exception ex) { dlg.SetDescription(string.Format(Lang.T("ai_error"), ex.Message)); }
                }));
            if (dlg.ShowDialog() == true && dlg.SelectedIndex > 0)
            {
                ApplyIndex(dlg.SelectedIndex);
                RefreshList(); RefreshPreview(); UpdateTitle();
                SetStatus(string.Format(Lang.T("look_applied"), LookPresets.Name(looks[dlg.SelectedIndex - 1])));
            }
            else
            {
                _undo.Rollback(_project);
                RefreshList(); RefreshPreview();
                SetStatus(Lang.T("look_cancelled"));
            }
        }

        private void RunFilter(FilterKind kind, double? amount)
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            double a = amount ?? AskNumber(Lang.T("flt_" + kind), Lang.T("strength_prompt") + (_project.Selection.IsActive ? " " + Lang.T("applies_to_selection") : ""),
                _lastFilter?.kind == kind ? _lastFilter.Value.amount : 50, 1, 100) ?? -1;
            if (a < 0) return;
            _lastFilter = (kind, a);
            _ = RunOpAsync(Lang.T("flt_" + kind), () => EditOps.Filter(_project, kind, a), requireImage: false);
        }

        private async Task SuggestionsAsync()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            SetStatus(Lang.T("ai_suggesting"));
            try
            {
                var suggestions = await _ai.SuggestEditsAsync(await SnapshotBase64Async(false, 1024));
                if (suggestions.Count == 0) { SetStatus(Lang.T("ai_no_suggestions")); return; }
                int applied = 0;
                foreach (var sug in suggestions)
                {
                    var row = _rows.FirstOrDefault(r => r.Key == "adj_" + sug.Action);
                    if (row == null) continue;
                    var res = MessageBox.Show(this, string.Format(Lang.T("ai_apply_suggestion"), $"{Lang.T(row.Key)} {sug.Value:+0.#;-0.#}", sug.Reason),
                        Lang.T("ai_panel_header"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res != MessageBoxResult.Yes) continue;
                    Checkpoint(Lang.T("hist_ai_suggestion"));
                    if (row.IsBoolean) row.SetBool(_project, true);
                    else row.SetNum(_project, Math.Clamp(sug.Value, row.Min, row.Max));
                    applied++;
                }
                RefreshList(); RefreshPreview();
                Activate(); Keyboard.Focus(BtnDescribe);
                SetStatus(string.Format(Lang.T("ai_suggestions_done"), applied, suggestions.Count));
            }
            catch (Exception ex) { SetAiResult(string.Format(Lang.T("ai_error"), ex.Message)); }
        }

        private async Task DescribeAsync()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            if (_describeBusy) return;
            _describeBusy = true;
            SetAiResult(Lang.T("ai_describing"));
            try
            {
                string description = await _ai.DescribeImageAsync(await SnapshotBase64Async(true, 1024));
                SetAiResult(description);
            }
            catch (Exception ex) { SetAiResult(string.Format(Lang.T("ai_error"), ex.Message)); }
            finally { _describeBusy = false; }
            if (_project.HasImage && MessageBox.Show(this, Lang.T("ai_want_suggestions"), Lang.T("ai_panel_header"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await SuggestionsAsync();
        }

        private async Task DescribeLookAsync()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            SetAiResult(Lang.T("ai_describing"));
            try { SetAiResult(await _ai.DescribeLookAsync(await SnapshotBase64Async(false, 1024))); }
            catch (Exception ex) { SetAiResult(string.Format(Lang.T("ai_error"), ex.Message)); }
        }

        // ════════════════════════════════════════════════════════════════
        // RETUŠ
        // ════════════════════════════════════════════════════════════════
        private async Task RemoveObjectAsync()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            var desc = AskText(Lang.T("menu_remove_object"), Lang.T("remove_object_prompt"), "");
            if (desc == null) return;
            var r = await RunSubjectMaskAsync(desc, Lang.T("menu_remove_object"));
            if (r == null) return;
            double cov = PixelOps.Coverage(r.Value.mask) * 100;
            if (cov > 35 && MessageBox.Show(this, string.Format(Lang.T("remove_large_warning"), cov.ToString("0")), Lang.T("menu_remove_object"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RunOpAsync(Lang.T("menu_remove_object") + ": " + desc, () => EditOps.Inpaint(_project, r.Value.mask),
                () => string.Format(Lang.T("done_removed_named"), desc));
        }

        private void SpotHeal()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            var f = new FormDialog(this, Lang.T("menu_spot_heal"), string.Format(Lang.T("spot_hint"), _project.CanvasWidth, _project.CanvasHeight), new[]
            {
                FormField.Num("x", Lang.T("spot_x"), _project.CanvasWidth / 2, 0, _project.CanvasWidth),
                FormField.Num("y", Lang.T("spot_y"), _project.CanvasHeight / 2, 0, _project.CanvasHeight),
                FormField.Num("r", Lang.T("spot_radius"), Math.Max(5, Math.Min(_project.CanvasWidth, _project.CanvasHeight) / 80), 1, 2000),
            });
            if (f.ShowDialog() != true) return;
            double x = f.Numbers["x"], y = f.Numbers["y"], rad = f.Numbers["r"];
            _ = RunOpAsync(Lang.T("menu_spot_heal"), () =>
                EditOps.Inpaint(_project, PixelOps.CircleMask(_project.CanvasWidth, _project.CanvasHeight, x, y, rad)), () => Lang.T("done_removed"));
        }

        private async Task RedEyeSelectionAsync()
        {
            if (!_project.Selection.IsActive) { SetStatus(Lang.T("need_selection")); return; }
            int changed = 0;
            await RunOpAsync(Lang.T("menu_redeye_sel"), () => changed = EditOps.RedEye(_project, EditOps.FullMask(_project)),
                () => changed > 0 ? Lang.T("done_redeye") : Lang.T("redeye_none"));
        }

        private async Task RedEyeAiAsync()
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            if (_busy) { SetStatus(Lang.T("status_busy")); return; }
            int W = _project.CanvasWidth, H = _project.CanvasHeight;
            var points = new List<(int x, int y)>();
            _busy = true;
            var progress = new ProgressDialog(Lang.T("menu_redeye_ai"), this);
            progress.Show();
            try
            {
                progress.SetStage(Lang.T("redeye_finding"));
                string b64 = await SnapshotBase64Async(false, 1280);
                foreach (var eye in new[] { "the left eye of the person (on the image's left side)", "the right eye of the person (on the image's right side)" })
                {
                    var p = await _ai.FindPointForDescriptionAsync(b64, W, H, eye);
                    if (p != null) points.Add(p.Value);
                }
            }
            catch (Exception ex) { progress.Close(); _busy = false; ShowError(ex); return; }
            finally { if (progress.IsVisible) progress.Close(); _busy = false; Activate(); }

            if (points.Count == 0) { SetStatus(Lang.T("redeye_no_eyes")); return; }
            double radius = Math.Max(6, Math.Min(W, H) * 0.03);
            int changed = 0;
            await RunOpAsync(Lang.T("menu_redeye_ai"), () =>
            {
                var mask = new byte[W * H];
                foreach (var (x, y) in points)
                {
                    var c = PixelOps.CircleMask(W, H, x, y, radius);
                    for (int i = 0; i < mask.Length; i++) if (c[i] > mask[i]) mask[i] = c[i];
                }
                changed = EditOps.RedEye(_project, mask);
            }, () => changed > 0 ? string.Format(Lang.T("done_redeye_n"), points.Count) : Lang.T("redeye_none"));
        }

        // ════════════════════════════════════════════════════════════════
        // UREĐIVANJE: kopija u sloj, popuna, provera dizajna, status
        // ════════════════════════════════════════════════════════════════
        private async Task CopyToLayer(bool cut)
        {
            if (!_project.HasImage) { SetStatus(Lang.T("ai_no_image")); return; }
            if (!_project.Selection.IsActive) { SetStatus(Lang.T("need_selection")); return; }
            string name = NextLayerName("layer_piece");
            ImageLayer? created = null;
            bool ok = await RunOpAsync(Lang.T(cut ? "menu_cut_to_layer" : "menu_copy_to_layer"), () =>
            {
                created = EditOps.CopySelectionToLayer(_project, name, cut) ?? throw new InvalidOperationException(Lang.T("need_selection"));
                _project.Layers.Add(created);
                _project.Selection = new Selection();
            }, () => string.Format(Lang.T("done_copy_layer"), name));
            if (ok && created != null) { _selectedLayer = created; RefreshLayersUi(); }
        }

        private void FillSelection()
        {
            if (!_project.Selection.IsActive) { SetStatus(Lang.T("need_selection")); return; }
            var c = AskText(Lang.T("menu_fill_selection"), Lang.T("fill_color_prompt"), "#FFFFFF");
            if (c == null) return;
            _ = RunOpAsync(Lang.T("menu_fill_selection"), () => EditOps.FillSelection(_project, c), requireImage: false);
        }

        private async void DesignCheck()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            _busy = true;
            SetStatus(string.Format(Lang.T("status_working"), Lang.T("menu_design_check")));
            var snap = ProjectJson.Clone(_project);
            string report;
            try { report = await Task.Run(() => DesignChecker.Run(snap)); }
            catch (Exception ex) { _busy = false; ShowError(ex); return; }
            finally { _busy = false; }
            if (_project.PageCount > 1) report = char.ToUpper(PageInfo()[0]) + PageInfo()[1..] + ".\n" + report;
            SetStatus(report.Split('\n').FirstOrDefault() ?? "");
            new ReportWindow(this, Lang.T("menu_design_check"), report).ShowDialog();
        }

        private void QuickStatus()
        {
            var sb = new StringBuilder();
            string? preset = CanvasPresets.Match(_project.CanvasWidth, _project.CanvasHeight);
            if (_project.PageCount > 1) sb.Append(PageInfo()).Append(". ");
            sb.Append(string.Format(Lang.T("qs_canvas"), _project.CanvasWidth, _project.CanvasHeight, preset != null ? " " + preset : ""));
            sb.Append(' ').Append(EditOps.DescribeSelection(_project)).Append('.');
            if (_selectedLayer != null && _project.Layers.Contains(_selectedLayer)) sb.Append(' ').Append(Describe.Layer(_selectedLayer, _project));
            int active = _rows.Count(r => r.IsBoolean ? r.GetBool(_project) : r.GetNum(_project) != 0);
            if (!string.IsNullOrEmpty(_project.LookName)) sb.Append(' ').Append(string.Format(Lang.T("dc_look"), _project.LookName));
            sb.Append(' ').Append(string.Format(Lang.T("qs_adjustments"), active, _project.Layers.Count));
            if (_undo.Dirty) sb.Append(' ').Append(Lang.T("qs_unsaved"));
            SetStatus(sb.ToString());
        }

        private void ToggleLanguage()
        {
            Lang.SetLanguage(Lang.Current == "sr" ? "en" : "sr");
            nativeAdjustList.Items.Clear();
            SetupNativeList();
            BuildVisualPanel();
            SetupNativeLayerList();
            RefreshAll();
            CurrentModeLabel.Text = Lang.T(_isVisualMode ? "visual_mode_indicator" : "jaws_mode_indicator");
            SetStatus(Lang.T("language_changed"));
        }

        // ════════════════════════════════════════════════════════════════
        // SLOJEVI
        // ════════════════════════════════════════════════════════════════
        private void AddLayer(Layer l, string? status = null)
        {
            Checkpoint(string.Format(Lang.T("hist_add_layer"), l.Name));
            _project.Layers.Add(l);
            _selectedLayer = l;
            RefreshLayersUi();
            SetStatus(status ?? Describe.Layer(l, _project));
        }

        private void AddTextLayer()
        {
            var f = new FormDialog(this, Lang.T("menu_add_text_layer"), Lang.T("add_text_hint"), new[]
            {
                FormField.Multi("text", Lang.T("layer_field_text"), Lang.T("layer_default_text")),
                FormField.Num("size", Lang.T("layer_field_font_size"), Math.Round(Math.Max(16, _project.CanvasHeight / 12.0)), 1, 2000),
                FormField.Text("color", Lang.T("layer_field_color"), _project.HasImage ? "#FFFFFF" : "#222222"),
                FormField.Bool("bold", Lang.T("layer_field_bold"), true),
                FormField.Choice("pos", Lang.T("add_position"), Positions(), 1),
                FormField.Bool("shadow", Lang.T("add_text_shadow"), _project.HasImage),
            });
            if (f.ShowDialog() != true || string.IsNullOrWhiteSpace(f.Text["text"])) return;
            int pos = f.Choices["pos"];
            var t = new TextLayer
            {
                Name = NextLayerName("layer_type_text"), Text = f.Text["text"].Trim(), FontSize = f.Numbers["size"],
                ColorHex = f.Text["color"].Trim(), Bold = f.Checks["bold"], ShadowEnabled = f.Checks["shadow"],
                ShadowOffsetX = 3, ShadowOffsetY = 3, ShadowBlur = 6,
                Align = (pos % 3) switch { 0 => TextAlign.Left, 1 => TextAlign.Center, _ => TextAlign.Right }
            };
            var (w, h) = CanvasEngine.MeasureText(t);
            t.Width = w; t.Height = h;
            PlaceAt(t, pos);
            AddLayer(t);
        }

        private void AddShapeLayer(ShapeKind kind)
        {
            double defW = Math.Round(_project.CanvasWidth * 0.3), defH = kind == ShapeKind.Line ? 1 : Math.Round(_project.CanvasHeight * 0.2);
            var f = new FormDialog(this, Lang.T("shape_" + kind.ToString().ToLowerInvariant()), null, new[]
            {
                FormField.Num("w", Lang.T("layer_field_width"), defW, 1, 30000),
                FormField.Num("h", Lang.T(kind == ShapeKind.Line ? "line_height_prompt" : "layer_field_height"), defH, kind == ShapeKind.Line ? 0 : 1, 30000),
                FormField.Text("color", Lang.T(kind == ShapeKind.Line ? "layer_field_stroke_color" : "layer_field_fill_color"), "#7C6AF7"),
                FormField.Choice("pos", Lang.T("add_position"), Positions(), 4),
                FormField.Num("radius", Lang.T("layer_field_corner_radius"), 0, 0, 5000),
            });
            if (f.ShowDialog() != true) return;
            var s = new ShapeLayer
            {
                Name = NextLayerName("shape_" + kind.ToString().ToLowerInvariant()), ShapeKind = kind,
                Width = f.Numbers["w"], Height = Math.Max(1, f.Numbers["h"]), CornerRadius = f.Numbers["radius"]
            };
            if (kind == ShapeKind.Line) { s.FillEnabled = false; s.StrokeEnabled = true; s.StrokeColorHex = f.Text["color"].Trim(); s.StrokeWidth = Math.Max(2, _project.CanvasHeight / 300.0); }
            else s.FillColorHex = f.Text["color"].Trim();
            PlaceAt(s, f.Choices["pos"]);
            AddLayer(s);
        }

        private void AddImageLayer()
        {
            var dlg = new OpenFileDialog { Title = Lang.T("menu_add_image_layer"), Filter = ImageFilter };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                int w, h;
                using (var probe = ImageEngine.Load(dlg.FileName)) { w = (int)probe.Width; h = (int)probe.Height; } // sa EXIF orijentacijom
                double scale = Math.Min(1.0, Math.Min(_project.CanvasWidth * 0.8 / w, _project.CanvasHeight * 0.8 / h));
                var l = new ImageLayer
                {
                    Name = Path.GetFileNameWithoutExtension(dlg.FileName), SourcePath = dlg.FileName,
                    Width = Math.Max(1, Math.Round(w * scale)), Height = Math.Max(1, Math.Round(h * scale))
                };
                PlaceAt(l, 4);
                AddLayer(l);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void AddGradientLayer()
        {
            var f = new FormDialog(this, Lang.T("menu_add_gradient_layer"), Lang.T("gradient_hint"), new[]
            {
                FormField.Choice("kind", Lang.T("gradient_kind"), new[] { Lang.T("gradient_linear"), Lang.T("gradient_radial") }, 0),
                FormField.Text("c1", Lang.T("gradient_start"), "#000000"),
                FormField.Num("o1", Lang.T("gradient_start_opacity"), _project.HasImage ? 0 : 100, 0, 100),
                FormField.Text("c2", Lang.T("gradient_end"), _project.HasImage ? "#000000" : "#7C6AF7"),
                FormField.Num("o2", Lang.T("gradient_end_opacity"), _project.HasImage ? 75 : 100, 0, 100),
                FormField.Num("angle", Lang.T("gradient_angle"), 90, -360, 360),
            });
            if (f.ShowDialog() != true) return;
            var g = new GradientLayer
            {
                Name = NextLayerName("layer_type_gradient"), Kind = (GradientKind)f.Choices["kind"],
                StartColorHex = f.Text["c1"].Trim(), StartOpacity = f.Numbers["o1"], EndColorHex = f.Text["c2"].Trim(), EndOpacity = f.Numbers["o2"],
                Angle = f.Numbers["angle"], X = 0, Y = 0, Width = _project.CanvasWidth, Height = _project.CanvasHeight
            };
            AddLayer(g);
        }

        private void DuplicateLayer()
        {
            if (!RequireLayer()) return;
            var src = _selectedLayer!;
            var json = JsonConvert.SerializeObject(src, typeof(Layer), ProjectJson.Settings);
            var copy = JsonConvert.DeserializeObject<Layer>(json, ProjectJson.Settings)!;
            copy.Id = Guid.NewGuid();
            copy.Name = src.Name + " " + Lang.T("layer_copy_suffix");
            copy.X += 20; copy.Y += 20;
            Checkpoint(string.Format(Lang.T("hist_add_layer"), copy.Name));
            _project.Layers.Insert(_project.Layers.IndexOf(src) + 1, copy);
            _selectedLayer = copy;
            RefreshLayersUi();
            SetStatus(Describe.Layer(copy, _project));
        }

        private void DeleteLayer()
        {
            if (!RequireLayer()) return;
            var l = _selectedLayer!;
            int idx = _project.Layers.IndexOf(l);
            Checkpoint(string.Format(Lang.T("hist_delete_layer"), l.Name));
            _project.Layers.Remove(l);
            _selectedLayer = _project.Layers.Count == 0 ? null : _project.Layers[Math.Clamp(idx - 1, 0, _project.Layers.Count - 1)];
            RefreshLayersUi();
            SetStatus(string.Format(Lang.T("layer_deleted"), l.Name));
        }

        private void MoveLayer(int dir)
        {
            if (!RequireLayer()) return;
            int idx = _project.Layers.IndexOf(_selectedLayer!), target = idx + dir;
            if (target < 0 || target >= _project.Layers.Count) { SetStatus(Lang.T(dir > 0 ? "layer_already_top" : "layer_already_bottom")); return; }
            Checkpoint(Lang.T(dir > 0 ? "menu_move_layer_up" : "menu_move_layer_down"));
            _project.Layers.Move(idx, target);
            RefreshLayersUi();
            SetStatus(string.Format(Lang.T("layer_order"), _selectedLayer!.Name, _project.Layers.Count - target, _project.Layers.Count));
        }

        private void Arrange(string how)
        {
            if (!RequireLayer()) return;
            var l = _selectedLayer!;
            if (how == "where") { SetStatus(Describe.Layer(l, _project)); return; }
            Checkpoint(string.Format(Lang.T("hist_move"), l.Name));
            double W = _project.CanvasWidth, H = _project.CanvasHeight;
            switch (how)
            {
                case "ch": l.X = Math.Round((W - l.Width) / 2); break;
                case "cv": l.Y = Math.Round((H - l.Height) / 2); break;
                case "c": l.X = Math.Round((W - l.Width) / 2); l.Y = Math.Round((H - l.Height) / 2); break;
                case "l": l.X = 0; break;
                case "r": l.X = Math.Round(W - l.Width); break;
                case "t": l.Y = 0; break;
                case "b": l.Y = Math.Round(H - l.Height); break;
                case "fill": l.X = 0; l.Y = 0; l.Width = W; l.Height = H; if (l is TextLayer tl) tl.AutoSize = false; break;
            }
            RefreshLayersUi();
            SetStatus(Describe.Layer(l, _project));
        }

        private void Nudge(double dx, double dy)
        {
            if (!RequireLayer()) return;
            var l = _selectedLayer!;
            Checkpoint(string.Format(Lang.T("hist_move"), l.Name), merge: true);
            l.X += dx; l.Y += dy;
            RefreshPreview();
            SetStatus($"X {l.X:0}, Y {l.Y:0}");
        }

        // ════════════════════════════════════════════════════════════════
        // LEKTURA DOKUMENTA (nepromenjeno, samo premešteno)
        // ════════════════════════════════════════════════════════════════
        private void ProofreadDocument()
        {
            var openDlg = new OpenFileDialog { Title = Lang.T("menu_proofread_document"), Filter = Lang.T("proof_open_filter") };
            if (openDlg.ShowDialog(this) != true) return;
            string text;
            try { text = DocumentTextExtractor.ExtractText(openDlg.FileName); }
            catch (Exception ex) { ShowError(ex); return; }

            var dlg = new ProofreadingDialog(text, string.Format(Lang.T("proof_title_document"), Path.GetFileName(openDlg.FileName))) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string inExt = Path.GetExtension(openDlg.FileName).ToLowerInvariant();
            var saveDlg = new SaveFileDialog
            {
                Filter = Lang.T("proof_save_filter"),
                FileName = Path.GetFileNameWithoutExtension(openDlg.FileName) + "_lektorisano" + (inExt is ".docx" or ".rtf" ? inExt : ".txt"),
                FilterIndex = inExt switch { ".docx" => 2, ".rtf" => 3, _ => 1 }
            };
            if (saveDlg.ShowDialog(this) != true) return;
            DocumentTextExtractor.SaveDocument(dlg.ResultText, saveDlg.FileName);
            SetStatus(string.Format(Lang.T("proof_saved"), saveDlg.FileName));
        }
    }
}
