using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using UltraStudio.Localization;
using UltraStudio.Models;
using UltraStudio.Services;
using UltraStudio.Views;

namespace UltraStudio
{
    /// <summary>
    /// Paket 2: višestranični dokument (strane, tekstualni okviri, tekst kroz strane,
    /// brojevi strana, PDF) i srpska tipografija / pismo / lektura celog dokumenta.
    /// </summary>
    public partial class MainWindow
    {
        private void BuildDocumentCommands()
        {
            _commands["pagenew"] = () => PageCommand(Lang.T("menu_page_new"), () => PageOps.Insert(_project, PageOps.BlankLike(_project), _project.CurrentPage + 1));
            _commands["pagenewsize"] = NewPageWithSize;
            _commands["pagedup"] = () => PageCommand(Lang.T("menu_page_duplicate"), () => PageOps.Duplicate(_project));
            _commands["pagedel"] = DeletePage;
            _commands["pageup"] = () => MovePage(-1);
            _commands["pagedown"] = () => MovePage(+1);
            _commands["pagenext"] = () => GoToPage(_project.CurrentPage + 1);
            _commands["pageprev"] = () => GoToPage(_project.CurrentPage - 1);
            _commands["pagelist"] = PageList;
            _commands["flowtext"] = () => _ = FlowTextAsync();
            _commands["addframe"] = AddTextFrame;
            _commands["pagenumbers"] = PageNumbers;
            _commands["docdpi"] = DocumentDpi;
            _commands["exportpdf"] = () => _ = ExportPdfAsync();
            _commands["exportpages"] = () => _ = ExportPagesAsync();

            _commands["typo"] = TypographyCommand;
            _commands["tocyr"] = () => ScriptCommand(toCyrillic: true);
            _commands["tolat"] = () => ScriptCommand(toCyrillic: false);
            _commands["typodoc"] = TypographyDocumentFile;
            _commands["proofall"] = ProofreadAllText;
        }

        private string PageInfo() =>
            _project.PageCount > 1 ? string.Format(Lang.T("page_of"), _project.CurrentPage + 1, _project.PageCount) : "";

        private string CurrentPageSummary() =>
            PageOps.Summary(_project, _project.CurrentPage, _project.PageCount);

        private void EnsureDocumentCanvas()
        {
            if (_project.HasCanvasContent) return;
            // Ništa nije otvoreno: dokument kreće od prazne A4 strane za štampu
            _project.CanvasWidth = 2480; _project.CanvasHeight = 3508; _project.Dpi = 300;
            _project.CanvasBackgroundHex = "#FFFFFF"; _project.BlankCanvas = true;
        }

        private void PageCommand(string label, Action op)
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            Checkpoint(label);
            try { op(); }
            catch (Exception ex) { _undo.Rollback(_project); ShowError(ex); return; }
            _selectedLayer = null;
            RefreshAll();
            SetStatus(label + ". " + CurrentPageSummary());
        }

        private void NewPageWithSize()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            var presets = CanvasPresets.All;
            var f = new FormDialog(this, Lang.T("menu_page_new_size"), null, new[]
            {
                FormField.Choice("preset", Lang.T("new_canvas_preset"), presets.Select(CanvasPresets.Label).ToArray(), presets.Count - 1),
                FormField.Num("w", Lang.T("canvas_width_prompt"), _project.CanvasWidth, 1, 20000),
                FormField.Num("h", Lang.T("canvas_height_prompt"), _project.CanvasHeight, 1, 20000),
            });
            if (f.ShowDialog() != true) return;
            var pr = presets[f.Choices["preset"]];
            int w = pr.Width > 0 ? pr.Width : (int)f.Numbers["w"], h = pr.Height > 0 ? pr.Height : (int)f.Numbers["h"];
            PageCommand(Lang.T("menu_page_new"), () => PageOps.Insert(_project, PageOps.BlankLike(_project, w, h), _project.CurrentPage + 1));
        }

        private void DeletePage()
        {
            if (_project.PageCount <= 1) { SetStatus(Lang.T("page_last")); return; }
            if (MessageBox.Show(this, string.Format(Lang.T("page_delete_confirm"), _project.CurrentPage + 1), Lang.T("menu_page_delete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            PageCommand(Lang.T("menu_page_delete"), () => PageOps.Delete(_project));
        }

        private void MovePage(int dir)
        {
            if (_project.PageCount <= 1) { SetStatus(Lang.T("page_single")); return; }
            Checkpoint(Lang.T(dir < 0 ? "menu_page_up" : "menu_page_down"));
            if (!PageOps.Move(_project, dir)) { _undo.Rollback(_project); SetStatus(Lang.T(dir < 0 ? "page_already_first" : "page_already_last")); return; }
            RefreshAll();
            SetStatus(string.Format(Lang.T("page_moved"), _project.CurrentPage + 1, _project.PageCount));
        }

        private void GoToPage(int index)
        {
            if (_project.PageCount <= 1) { SetStatus(Lang.T("page_single")); return; }
            if (index < 0) { SetStatus(Lang.T("page_already_first")); return; }
            if (index >= _project.PageCount) { SetStatus(Lang.T("page_already_last")); return; }
            PageOps.GoTo(_project, index);  // navigacija nije izmena — ne ide u Undo
            _selectedLayer = null;
            RefreshAll();
            SetStatus(CurrentPageSummary());
        }

        private void PageList()
        {
            if (_project.PageCount <= 1) { SetStatus(Lang.T("page_single") + " " + CurrentPageSummary()); return; }
            var pages = PageOps.All(_project);
            var names = pages.Select((pg, i) => PageOps.Summary(pg, i, pages.Count)).ToList();
            var dlg = new ChoiceDialog(this, Lang.T("menu_page_list"), Lang.T("page_list_prompt"), names, _project.CurrentPage);
            if (dlg.ShowDialog() == true) GoToPage(dlg.SelectedIndex);
        }

        private string[] AlignOptions() => new[] { Lang.T("align_left"), Lang.T("align_center"), Lang.T("align_right"), Lang.T("align_justify") };

        private async Task FlowTextAsync()
        {
            var f = new FormDialog(this, Lang.T("menu_flow_text"), Lang.T("flow_hint"), new[]
            {
                FormField.Choice("src", Lang.T("flow_source"), new[] { Lang.T("flow_source_file"), Lang.T("flow_source_typed") }, 0),
                FormField.Multi("text", Lang.T("flow_text"), ""),
                FormField.Text("font", Lang.T("layer_field_font"), "Georgia"),
                FormField.Num("pt", Lang.T("flow_size_pt"), 11, 4, 200),
                FormField.Num("spacing", Lang.T("layer_field_line_spacing"), 1.35, 0.8, 4),
                FormField.Choice("align", Lang.T("layer_field_align"), AlignOptions(), 3),
                FormField.Num("margin", Lang.T("flow_margin_mm"), 20, 0, 200),
                FormField.Bool("numbers", Lang.T("flow_page_numbers"), true),
                FormField.Bool("typo", Lang.T("flow_typography"), true),
            }, 520);
            if (f.ShowDialog() != true) return;

            string text;
            if (f.Choices["src"] == 0)
            {
                var od = new OpenFileDialog { Title = Lang.T("menu_flow_text"), Filter = Lang.T("proof_open_filter") };
                if (od.ShowDialog(this) != true) return;
                try { text = DocumentTextExtractor.ExtractText(od.FileName); }
                catch (Exception ex) { ShowError(ex); return; }
            }
            else text = f.Text["text"];
            if (string.IsNullOrWhiteSpace(text)) { SetStatus(Lang.T("flow_empty")); return; }
            if (f.Checks["typo"]) text = Typography.Fix(text, new Typography.Options(), out _);

            EnsureDocumentCanvas();
            double dpi = _project.Dpi > 0 ? _project.Dpi : 300;
            var o = new DocumentOps.FlowOptions
            {
                FontFamily = string.IsNullOrWhiteSpace(f.Text["font"]) ? "Georgia" : f.Text["font"].Trim(),
                FontSizePx = DocumentOps.PtToPx(f.Numbers["pt"], dpi), LineSpacing = f.Numbers["spacing"],
                Align = (TextAlign)f.Choices["align"], MarginPx = DocumentOps.MmToPx(f.Numbers["margin"], dpi),
                PageNumbers = f.Checks["numbers"], ColorHex = _project.CanvasBackgroundHex.Equals("#000000", StringComparison.OrdinalIgnoreCase) ? "#F0F0F0" : "#1A1A1A"
            };
            int first = (!_project.HasImage && _project.Layers.Count == 0) ? _project.CurrentPage : _project.CurrentPage + 1;
            int count = 0;
            bool ok = await RunOpAsync(Lang.T("menu_flow_text"), () =>
            {
                count = DocumentOps.FlowText(_project, text, o);
                PageOps.GoTo(_project, first);
            }, () => string.Format(Lang.T("flow_done"), count, _project.PageCount), requireImage: false);
            if (ok) { _selectedLayer = _project.Layers.FirstOrDefault(l => l.Role == "flow"); RefreshLayersUi(); }
        }

        private void AddTextFrame()
        {
            if (!_project.HasCanvasContent) EnsureDocumentCanvas();
            double dpi = _project.Dpi > 0 ? _project.Dpi : 300;
            var f = new FormDialog(this, Lang.T("menu_add_frame"), Lang.T("frame_hint"), new[]
            {
                FormField.Multi("text", Lang.T("layer_field_text"), ""),
                FormField.Text("font", Lang.T("layer_field_font"), "Segoe UI"),
                FormField.Num("pt", Lang.T("flow_size_pt"), 12, 4, 400),
                FormField.Choice("align", Lang.T("layer_field_align"), AlignOptions(), 0),
                FormField.Num("margin", Lang.T("flow_margin_mm"), 20, 0, 500),
            });
            if (f.ShowDialog() != true || string.IsNullOrWhiteSpace(f.Text["text"])) return;
            double m = DocumentOps.MmToPx(f.Numbers["margin"], dpi);
            var t = new TextLayer
            {
                Name = NextLayerName("frame_name"), Text = Typography.Fix(f.Text["text"], new Typography.Options { NonBreaking = false }, out _),
                FontFamily = string.IsNullOrWhiteSpace(f.Text["font"]) ? "Segoe UI" : f.Text["font"].Trim(),
                FontSize = DocumentOps.PtToPx(f.Numbers["pt"], dpi), Align = (TextAlign)f.Choices["align"],
                ColorHex = _project.HasImage ? "#FFFFFF" : "#1A1A1A", Wrap = true, AutoSize = false, LineSpacing = 1.3,
                X = Math.Round(m), Y = Math.Round(m), Width = Math.Max(20, _project.CanvasWidth - 2 * m), Height = Math.Max(20, _project.CanvasHeight - 2 * m)
            };
            int over = TextLayout.OverflowLines(t);
            AddLayer(t, Describe.Layer(t, _project) + (over > 0 ? ". " + string.Format(Lang.T("frame_overflow"), over) : ""));
        }

        private void PageNumbers()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("ai_no_image")); return; }
            double dpi = _project.Dpi > 0 ? _project.Dpi : 300;
            var f = new FormDialog(this, Lang.T("menu_page_numbers"), Lang.T("page_numbers_hint"), new[]
            {
                FormField.Choice("fmt", Lang.T("page_numbers_format"), new[] { "1, 2, 3", string.Format(Lang.T("page_number_word"), 1), "1 / 10", "— 1 —", Lang.T("page_numbers_remove") }, 0),
                FormField.Choice("pos", Lang.T("add_position"), new[] { Lang.T("pn_bottom_center"), Lang.T("pn_bottom_right"), Lang.T("pn_bottom_outer"), Lang.T("pn_top_right") }, 0),
                FormField.Num("pt", Lang.T("flow_size_pt"), 9, 4, 200),
                FormField.Text("color", Lang.T("layer_field_color"), "#555555"),
            });
            if (f.ShowDialog() != true) return;
            int fmt = f.Choices["fmt"], pos = f.Choices["pos"];
            double px = DocumentOps.PtToPx(f.Numbers["pt"], dpi);
            string color = f.Text["color"].Trim();
            PageCommand(Lang.T("menu_page_numbers"), () =>
            {
                if (fmt == 4) DocumentOps.ForEachPage(_project, pg => { foreach (var l in pg.Layers.Where(l => l.Role == "page_number").ToList()) pg.Layers.Remove(l); });
                else DocumentOps.AddPageNumbers(_project, fmt, pos, px, color);
            });
        }

        private void DocumentDpi()
        {
            var v = AskNumber(Lang.T("menu_doc_dpi"), string.Format(Lang.T("doc_dpi_prompt"),
                Math.Round(_project.CanvasWidth / _project.Dpi * 25.4), Math.Round(_project.CanvasHeight / _project.Dpi * 25.4)), _project.Dpi, 36, 1200);
            if (v == null) return;
            Checkpoint(Lang.T("menu_doc_dpi"));
            double dpi = v.Value;
            DocumentOps.ForEachPage(_project, pg => pg.Dpi = dpi);
            SetStatus(string.Format(Lang.T("doc_dpi_set"), v.Value, Math.Round(_project.CanvasWidth / v.Value * 25.4), Math.Round(_project.CanvasHeight / v.Value * 25.4)));
        }

        private async Task ExportPdfAsync()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("status_no_canvas")); return; }
            string baseName = _project.ProjectPath != null ? Path.GetFileNameWithoutExtension(_project.ProjectPath)
                            : _project.OriginalPath != null ? Path.GetFileNameWithoutExtension(_project.OriginalPath) : Lang.T("untitled");
            var f = new FormDialog(this, Lang.T("menu_export_pdf"), string.Format(Lang.T("pdf_hint"), _project.PageCount), new[]
            {
                FormField.Text("title", Lang.T("pdf_title"), baseName),
                FormField.Bool("real", Lang.T("pdf_real_text"), true),
                FormField.Choice("quality", Lang.T("pdf_quality"), new[] { Lang.T("pdf_q_print"), Lang.T("pdf_q_standard"), Lang.T("pdf_q_small") }, 0),
                FormField.Bool("open", Lang.T("pdf_open_after"), true),
            });
            if (f.ShowDialog() != true) return;
            var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = baseName + ".pdf", InitialDirectory = _lastExportFolder };
            if (dlg.ShowDialog(this) != true) return;
            _lastExportFolder = Path.GetDirectoryName(dlg.FileName);

            var opt = new PdfExporter.Options
            {
                Title = f.Text["title"], RealText = f.Checks["real"],
                MaxImageDpi = f.Choices["quality"] switch { 0 => 300, 1 => 200, _ => 150 },
                JpegQuality = f.Choices["quality"] == 2 ? 75 : 88
            };
            _busy = true;
            var pages = PageOps.All(_project);
            SetStatus(string.Format(Lang.T("status_working"), Lang.T("menu_export_pdf")));
            try
            {
                var r = await Task.Run(() => PdfExporter.Export(pages, dlg.FileName, opt,
                    (i, n) => Dispatcher.BeginInvoke(new Action(() => TxtStatusMessage.Text = string.Format(Lang.T("pdf_progress"), i, n)))));
                long kb = new FileInfo(dlg.FileName).Length / 1024;
                string msg = string.Format(Lang.T("pdf_done"), r.Pages, kb, dlg.FileName);
                if (opt.RealText && r.RasterTextLayers > 0) msg += " " + string.Format(Lang.T("pdf_raster_note"), r.RasterTextLayers);
                SetStatus(msg);
                if (f.Checks["open"]) AppInfo.OpenUrl(dlg.FileName);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { _busy = false; }
        }

        private async Task ExportPagesAsync()
        {
            if (!_project.HasCanvasContent) { SetStatus(Lang.T("status_no_canvas")); return; }
            var fd = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog { Description = Lang.T("menu_export_pages"), UseDescriptionForTitle = true };
            if (fd.ShowDialog(this) != true) return;
            string folder = fd.SelectedPath;
            string baseName = _project.ProjectPath != null ? Path.GetFileNameWithoutExtension(_project.ProjectPath) : "page";
            var opt = new CanvasEngine.ExportOptions { Format = _lastExport.Format, Quality = _lastExport.Quality };
            var pages = PageOps.All(_project);
            _busy = true;
            SetStatus(string.Format(Lang.T("status_working"), Lang.T("menu_export_pages")));
            try
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i < pages.Count; i++)
                        CanvasEngine.Export(pages[i], Path.Combine(folder, $"{baseName}_{i + 1:000}.{opt.Format}"), opt);
                });
                SetStatus(string.Format(Lang.T("pages_exported"), pages.Count, opt.Format.ToUpperInvariant(), folder));
            }
            catch (Exception ex) { ShowError(ex); }
            finally { _busy = false; }
        }

        // ════════════════════════════════════════════════════════════════
        // TIPOGRAFIJA, PISMO, LEKTURA CELOG DOKUMENTA
        // ════════════════════════════════════════════════════════════════
        private string[] ScopeOptions() => new[] { Lang.T("scope_selected"), Lang.T("scope_page"), Lang.T("scope_document") };

        /// <summary>Primeni izmenu teksta na izabrani obim; okvir sloja se ponovo prilagodi tekstu.</summary>
        private int ApplyToText(int scope, Func<string, string> change)
        {
            bool Change(TextLayer t)
            {
                string n = change(t.Text ?? "");
                if (n == t.Text) return false;
                t.Text = n;
                if (t.AutoSize && !t.Wrap)
                {
                    double cx = t.CenterX;
                    var (w, h) = CanvasEngine.MeasureText(t);
                    t.Width = w; t.Height = h;
                    if (t.Align == TextAlign.Center) t.X = Math.Round(cx - w / 2);
                }
                return true;
            }
            if (scope == 0)
            {
                if (_selectedLayer is not TextLayer sel) return -1;
                return Change(sel) ? 1 : 0;
            }
            return DocumentOps.ForEachTextLayer(_project, scope == 2, Change);
        }

        private void TypographyCommand()
        {
            var f = new FormDialog(this, Lang.T("menu_typography"), Lang.T("typo_hint"), new[]
            {
                FormField.Choice("scope", Lang.T("scope_label"), ScopeOptions(), _selectedLayer is TextLayer ? 0 : 2),
                FormField.Bool("quotes", Lang.T("typo_quotes"), true),
                FormField.Bool("en", Lang.T("typo_english_quotes"), false),
                FormField.Bool("dashes", Lang.T("typo_dashes"), true),
                FormField.Bool("spaces", Lang.T("typo_spaces"), true),
                FormField.Bool("ellipsis", Lang.T("typo_ellipsis"), true),
                FormField.Bool("nbsp", Lang.T("typo_nbsp"), true),
            }, 520);
            if (f.ShowDialog() != true) return;
            var o = new Typography.Options
            {
                Quotes = f.Checks["quotes"], EnglishQuotes = f.Checks["en"], Dashes = f.Checks["dashes"],
                Spaces = f.Checks["spaces"], Ellipsis = f.Checks["ellipsis"], NonBreaking = f.Checks["nbsp"]
            };
            int fixes = 0;
            Checkpoint(Lang.T("menu_typography"));
            int layers = ApplyToText(f.Choices["scope"], s => { var r = Typography.Fix(s, o, out var rep); fixes += rep.Total; return r; });
            if (layers < 0) { _undo.Rollback(_project); SetStatus(Lang.T("need_text_layer")); return; }
            if (layers == 0) _undo.Rollback(_project);
            RefreshAll();
            SetStatus(string.Format(Lang.T("typo_result"), fixes, layers));
        }

        private void ScriptCommand(bool toCyrillic)
        {
            var dlg = new ChoiceDialog(this, Lang.T(toCyrillic ? "menu_to_cyrillic" : "menu_to_latin"), Lang.T("scope_label"), ScopeOptions(), _selectedLayer is TextLayer ? 0 : 2);
            if (dlg.ShowDialog() != true) return;
            Checkpoint(Lang.T(toCyrillic ? "menu_to_cyrillic" : "menu_to_latin"));
            int letters = 0;
            int layers = ApplyToText(dlg.SelectedIndex, s =>
            {
                var r = toCyrillic ? Typography.ToCyrillic(s, out int n) : Typography.ToLatin(s, out n);
                letters += n; return r;
            });
            if (layers < 0) { _undo.Rollback(_project); SetStatus(Lang.T("need_text_layer")); return; }
            if (layers == 0) _undo.Rollback(_project);
            RefreshAll();
            SetStatus(string.Format(Lang.T(toCyrillic ? "script_done_cyr" : "script_done_lat"), layers)
                      + (toCyrillic && FontsWarning() is string w ? " " + w : ""));
        }

        /// <summary>Upozorenje ako neki font u dokumentu verovatno nema ćirilicu (dekorativni fontovi).</summary>
        private string? FontsWarning()
        {
            var safe = new[] { "Segoe UI", "Arial", "Times New Roman", "Georgia", "Calibri", "Cambria", "Verdana", "Tahoma", "Trebuchet MS", "Courier New", "Consolas", "Candara", "Constantia", "Corbel", "Palatino Linotype", "Book Antiqua", "Garamond", "Sitka" };
            var risky = PageOps.All(_project).SelectMany(pg => pg.Layers.OfType<TextLayer>()).Select(t => t.FontFamily)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(fam => !safe.Any(s => fam.StartsWith(s, StringComparison.OrdinalIgnoreCase))).ToList();
            return risky.Count == 0 ? null : string.Format(Lang.T("script_font_warning"), string.Join(", ", risky));
        }

        private void TypographyDocumentFile()
        {
            var od = new OpenFileDialog { Title = Lang.T("menu_typography_file"), Filter = Lang.T("proof_open_filter") };
            if (od.ShowDialog(this) != true) return;
            string text;
            try { text = DocumentTextExtractor.ExtractText(od.FileName); }
            catch (Exception ex) { ShowError(ex); return; }
            var f = new FormDialog(this, Lang.T("menu_typography_file"), Path.GetFileName(od.FileName), new[]
            {
                FormField.Bool("typo", Lang.T("typo_all_rules"), true),
                FormField.Bool("en", Lang.T("typo_english_quotes"), false),
                FormField.Choice("script", Lang.T("typo_script"), new[] { Lang.T("script_keep"), Lang.T("menu_to_cyrillic"), Lang.T("menu_to_latin") }, 0),
            });
            if (f.ShowDialog() != true) return;
            int fixes = 0, letters = 0;
            if (f.Checks["typo"]) { text = Typography.Fix(text, new Typography.Options { EnglishQuotes = f.Checks["en"] }, out var rep); fixes = rep.Total; }
            if (f.Choices["script"] == 1) text = Typography.ToCyrillic(text, out letters);
            else if (f.Choices["script"] == 2) text = Typography.ToLatin(text, out letters);

            string inExt = Path.GetExtension(od.FileName).ToLowerInvariant();
            var sd = new SaveFileDialog
            {
                Filter = Lang.T("proof_save_filter"),
                FileName = Path.GetFileNameWithoutExtension(od.FileName) + "_tipografija" + (inExt is ".docx" or ".rtf" ? inExt : ".txt"),
                FilterIndex = inExt switch { ".docx" => 2, ".rtf" => 3, _ => 1 },
                InitialDirectory = Path.GetDirectoryName(od.FileName)
            };
            if (sd.ShowDialog(this) != true) return;
            try
            {
                DocumentTextExtractor.SaveDocument(text, sd.FileName);
                SetStatus(string.Format(Lang.T("typo_file_done"), fixes, letters, sd.FileName));
            }
            catch (Exception ex) { ShowError(ex); }
        }

        /// <summary>AI lektura svakog tekst-sloja u dokumentu, jedan po jedan (isti dijalog kao za jedan sloj).</summary>
        private void ProofreadAllText()
        {
            var pages = PageOps.All(_project);
            var items = new List<(int page, Guid id, string name, string text)>();
            for (int i = 0; i < pages.Count; i++)
                foreach (var t in pages[i].Layers.OfType<TextLayer>().Where(t => t.Role != "page_number" && !string.IsNullOrWhiteSpace(t.Text)).OrderBy(t => t.Y))
                    items.Add((i, t.Id, t.Name, t.Text));
            if (items.Count == 0) { SetStatus(Lang.T("need_text_layer")); return; }

            var changes = new Dictionary<Guid, string>();
            for (int k = 0; k < items.Count; k++)
            {
                var it = items[k];
                string title = string.Format(Lang.T("proof_all_title"), k + 1, items.Count, it.name, it.page + 1);
                var dlg = new ProofreadingDialog(it.text, title) { Owner = this };
                if (dlg.ShowDialog() == true) { if (dlg.ResultText != it.text) changes[it.id] = dlg.ResultText; }
                else if (k < items.Count - 1 && MessageBox.Show(this, Lang.T("proof_all_stop"), Lang.T("menu_proofread_all"),
                             MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) break;
            }
            if (changes.Count == 0) { SetStatus(Lang.T("proof_all_none")); return; }
            Checkpoint(Lang.T("menu_proofread_all"));
            DocumentOps.ForEachTextLayer(_project, true, t =>
            {
                if (!changes.TryGetValue(t.Id, out var n)) return false;
                t.Text = n;
                if (t.AutoSize && !t.Wrap) { var (w, h) = CanvasEngine.MeasureText(t); t.Width = w; t.Height = h; }
                return true;
            });
            RefreshAll();
            SetStatus(string.Format(Lang.T("proof_all_done"), changes.Count));
        }
    }
}
