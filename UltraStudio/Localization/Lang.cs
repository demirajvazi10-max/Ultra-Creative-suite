using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace UltraStudio.Localization
{
    // Isti mehanizam kao Ultra Audio Editor: XAML koristi {DynamicResource L_kljuc},
    // kod koristi Lang.T("kljuc"). Engleski je DEFAULT od prvog dana — naučeno
    // večeras da to nikad ne bude naknadna zakrpa.
    public static class Lang
    {
        public static string Current { get; private set; } = "en";
        public static event Action? LanguageChanged;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UltraStudio", "language.txt");

        // key -> (english, serbian)
        private static readonly Dictionary<string, (string en, string sr)> Table = new()
        {
            // ===== Main window =====
            ["app_title"] = ("Ultra Studio — Accessible Photo Editor", "Ultra Studio — Pristupačan Photo Editor"),
            ["statusbar_ready"] = ("Ultra Studio v0.1 | JAWS accessible", "Ultra Studio v0.1 | JAWS pristupačno"),
            ["statusbar_ready_sr_detected"] = ("Ultra Studio v0.1 | Screen reader detected — JAWS Mode active", "Ultra Studio v0.1 | Detektovan čitač ekrana — aktivan JAWS mod"),
            ["statusbar_ready_visual"] = ("Ultra Studio v0.1 | No screen reader detected — Visual Mode active", "Ultra Studio v0.1 | Čitač ekrana nije detektovan — aktivan Vizuelni mod"),
            ["mode_toast_jaws"] = ("JAWS Mode active — screen reader detected", "JAWS mod aktivan — detektovan čitač ekrana"),
            ["mode_toast_visual"] = ("Visual Mode active — no screen reader detected", "Vizuelni mod aktivan — čitač ekrana nije detektovan"),

            // ===== Menu: File =====
            ["menu_file"] = ("_File", "_Fajl"),
            ["menu_open_image"] = ("_Open image... (Ctrl+O)", "_Otvori sliku... (Ctrl+O)"),
            ["menu_save"] = ("_Save (Ctrl+S)", "_Sačuvaj (Ctrl+S)"),
            ["menu_save_as"] = ("Save _as... (Ctrl+Shift+S)", "Sačuvaj _kao... (Ctrl+Shift+S)"),
            ["menu_exit"] = ("E_xit", "I_zlaz"),

            // ===== Menu: Edit =====
            ["menu_edit"] = ("_Edit", "_Uređivanje"),
            ["menu_undo"] = ("_Undo (Ctrl+Z)", "_Poništi (Ctrl+Z)"),
            ["menu_redo"] = ("_Redo (Ctrl+Y)", "_Ponovi (Ctrl+Y)"),
            ["menu_reset"] = ("_Reset to original", "_Vrati na original"),

            // ===== Visual/JAWS mode toggle =====
            ["visual_mode_btn"] = ("Visual Mode", "Vizuelni mod"),
            ["jaws_mode_btn"] = ("JAWS Mode", "JAWS mod"),
            ["visual_mode_indicator"] = ("Current: Visual Mode (Alt+W to switch)", "Trenutno: Vizuelni mod (Alt+W za promenu)"),
            ["jaws_mode_indicator"] = ("Current: JAWS Mode (Alt+W to switch)", "Trenutno: JAWS mod (Alt+W za promenu)"),
            ["acc_switch_visual"] = ("Switch to Visual Mode, with mouse-friendly sliders", "Prebaci na Vizuelni mod, sa sliderima za miš"),
            ["acc_switch_jaws"] = ("Switch to JAWS Mode, with a keyboard-accessible list", "Prebaci na JAWS mod, sa listom pristupačnom tastaturi"),
            ["visual_mode_tt"] = ("Mouse-friendly sliders for sighted users", "Slideri za miš, za korisnike koji vide"),
            ["jaws_mode_tt"] = ("Keyboard-accessible list for screen readers", "Lista pristupačna tastaturi, za čitače ekrana"),
            ["menu_extract_object"] = ("_Extract object... (AI)", "_Izdvoji objekat... (AI)"),

            // ===== Menu: Help =====
            ["menu_help"] = ("_Help", "_Pomoć"),
            ["menu_about"] = ("_About", "_O programu"),
            ["about_title"] = ("About Ultra Studio", "O Ultra Studio programu"),
            ["about_text"] = ("Ultra Studio v0.1\n\nAccessible photo editor\nMade for all users\n\nCreated by Demir Ajvazi\n© 2026",
                               "Ultra Studio v0.1\n\nPristupačni photo editor\nNapravljen za sve korisnike\n\nAutor: Demir Ajvazi\n© 2026"),

            ["extract_prompt_title"] = ("Extract object", "Izdvoji objekat"),
            ["extract_prompt"] = ("Describe what to extract (e.g. \"the child\", \"the car\"):", "Opiši šta da izdvojim (npr. \"dete\", \"auto\"):"),
            ["extract_locating"] = ("Locating object with AI...", "Tražim objekat pomoću AI-ja..."),
            ["extract_not_found"] = ("AI couldn't locate \"{0}\" in this image. Try a more specific description.", "AI nije uspeo da pronađe \"{0}\" na slici. Probaj precizniji opis."),
            ["extract_segmenting"] = ("Segmenting object...", "Izdvajam objekat..."),
            ["extract_done"] = ("Object extracted and saved to {0}. Open it as the new image?", "Objekat izdvojen i sačuvan u {0}. Otvoriti kao novu sliku?"),
            ["sam_models_missing"] = ("SAM model files are not installed.\n\nExpected at:\n{0}\n{1}\n\nSee the app documentation for download instructions.", "SAM model fajlovi nisu instalirani.\n\nOčekuju se u:\n{0}\n{1}\n\nPogledaj dokumentaciju programa za uputstvo za preuzimanje."),

            ["ai_apply_suggestion"] = ("AI suggests: {0} ({1}). Apply this?", "AI predlaže: {0} ({1}). Primeniti?"),
            ["ai_apply_btn"] = ("Apply", "Primeni"),
            ["ai_suggestion_applied"] = ("Applied: {0}", "Primenjeno: {0}"),

            // ===== Accessible list =====
            ["acc_list_help"] = ("Image adjustments list. Use arrow keys to navigate. Enter to edit a value. Shift+F10 for menu.",
                                  "Lista podešavanja slike. Strelice za kretanje. Enter za izmenu vrednosti. Shift+F10 za meni."),
            ["col_adjustment"] = ("Adjustment", "Podešavanje"),
            ["col_value"] = ("Value", "Vrednost"),

            // ===== Adjustments (rows in the accessible list) =====
            ["adj_brightness"] = ("Brightness", "Svetlina"),
            ["adj_contrast"] = ("Contrast", "Kontrast"),
            ["adj_saturation"] = ("Saturation", "Zasićenost"),
            ["adj_sharpen"] = ("Sharpen", "Izoštravanje"),
            ["adj_blur"] = ("Blur", "Zamućenje"),
            ["adj_rotate"] = ("Rotate", "Rotacija"),
            ["adj_grayscale"] = ("Grayscale", "Crno-belo"),
            ["adj_sepia"] = ("Sepia", "Sepija"),
            ["adj_flip_h"] = ("Flip horizontal", "Obrni horizontalno"),
            ["adj_flip_v"] = ("Flip vertical", "Obrni vertikalno"),
            ["adj_crop"] = ("Crop...", "Iseci..."),

            // ===== AI description panel =====
            ["ai_panel_header"] = ("AI Image Description", "AI Opis Slike"),
            ["ai_describe_btn"] = ("Describe this image", "Opiši ovu sliku"),
            ["ai_describing"] = ("Analyzing image, please wait...", "Analiziram sliku, sačekaj trenutak..."),
            ["ai_no_image"] = ("Open an image first.", "Prvo otvori sliku."),
            ["ai_result_placeholder"] = ("AI description will appear here.", "AI opis će se pojaviti ovde."),
            ["ai_error"] = ("AI error: {0}", "AI greška: {0}"),
            ["ollama_not_running"] = ("Ollama is not running on this computer (localhost:11434).\n\nStart the Ollama app and make sure a vision model is installed (ollama pull qwen2.5vl:latest).",
                                       "Ollama nije pokrenuta na ovom računaru (localhost:11434).\n\nPokreni Ollama aplikaciju i proveri da li je instaliran vision model (ollama pull qwen2.5vl:latest)."),

            // ===== Status / dialogs =====
            ["error_title"] = ("Error", "Greška"),
            ["error_prefix"] = ("Error: {0}", "Greška: {0}"),
            ["done_title"] = ("Done", "Gotovo"),
            ["btn_cancel"] = ("Cancel", "Otkaži"),
            ["btn_close"] = ("Close", "Zatvori"),
            ["btn_apply"] = ("Apply", "Primeni"),
            ["log_window_title"] = ("Diagnostic Log", "Dnevnik dijagnostike"),
            ["log_window_path"] = ("Also written to disk at: {0}", "Takođe se upisuje na disk: {0}"),
            ["log_window_refresh"] = ("Refresh", "Osveži"),
            ["log_window_copy"] = ("Copy All", "Kopiraj sve"),
            ["log_window_copied"] = ("Diagnostic Log — copied!", "Dnevnik dijagnostike — kopirano!"),
            ["img_loaded"] = ("Image loaded: {0}, {1}x{2} pixels", "Slika učitana: {0}, {1}x{2} piksela"),
            ["img_saved"] = ("Image saved to {0}", "Slika sačuvana u {0}"),
            ["status_no_canvas"] = ("Nothing to save yet — open a photo, start a new canvas, or add a layer.", "Nema šta da se sačuva — otvori fotografiju, napravi novo platno ili dodaj sloj."),

            // ===== Menu: Layers =====
            ["menu_layers"] = ("_Layers", "_Slojevi"),
            ["menu_new_canvas"] = ("_New canvas...", "_Novo platno..."),
            ["menu_add_text_layer"] = ("Add _text layer...", "Dodaj _tekst sloj..."),
            ["menu_add_shape_layer"] = ("Add _shape layer", "Dodaj sloj _oblika"),
            ["shape_rectangle"] = ("Rectangle", "Pravougaonik"),
            ["shape_ellipse"] = ("Ellipse", "Elipsa"),
            ["shape_line"] = ("Line", "Linija"),
            ["menu_add_image_layer"] = ("Add _image layer...", "Dodaj sloj sa _slikom..."),
            ["menu_layer_properties"] = ("Layer _properties... (Enter)", "_Svojstva sloja... (Enter)"),
            ["menu_duplicate_layer"] = ("_Duplicate layer", "_Dupliraj sloj"),
            ["menu_delete_layer"] = ("De_lete layer (Del)", "O_briši sloj (Del)"),
            ["menu_move_layer_up"] = ("Move layer _up (bring forward)", "Pomeri sloj _gore (unapred)"),
            ["menu_move_layer_down"] = ("Move layer _down (send backward)", "Pomeri sloj do_le (unazad)"),

            // ===== Layers panel (JAWS + visual list) =====
            ["layers_header"] = ("LAYERS", "SLOJEVI"),
            ["acc_layer_list_help"] = ("Layers list. Use arrow keys to navigate. Enter for properties, Space to toggle visibility, Delete to remove. Shift+F10 for menu.",
                                        "Lista slojeva. Strelice za kretanje. Enter za svojstva, Space za vidljivost, Delete za brisanje. Shift+F10 za meni."),
            ["col_layer_name"] = ("Name", "Ime"),
            ["col_layer_type"] = ("Type", "Tip"),
            ["col_layer_visible"] = ("Visible", "Vidljiv"),
            ["col_layer_opacity"] = ("Opacity", "Providnost"),
            ["layer_on"] = ("On", "Uklj"),
            ["layer_off"] = ("Off", "Isklj"),
            ["layer_type_text"] = ("Text", "Tekst"),
            ["layer_type_shape"] = ("Shape", "Oblik"),
            ["layer_type_image"] = ("Image", "Slika"),
            ["layer_none_selected"] = ("Select a layer first.", "Prvo izaberi sloj."),
            ["layer_copy_suffix"] = ("copy", "kopija"),
            ["layer_default_text"] = ("New text", "Novi tekst"),
            ["btn_layer_properties"] = ("Properties...", "Svojstva..."),

            // ===== Layer properties dialog =====
            ["layer_props_title"] = ("Layer properties — {0}", "Svojstva sloja — {0}"),
            ["layer_field_name"] = ("Name", "Ime"),
            ["layer_field_x"] = ("X position (px)", "X pozicija (px)"),
            ["layer_field_y"] = ("Y position (px)", "Y pozicija (px)"),
            ["layer_field_width"] = ("Width (px)", "Širina (px)"),
            ["layer_field_height"] = ("Height (px)", "Visina (px)"),
            ["layer_field_opacity"] = ("Opacity (0-100)", "Providnost (0-100)"),
            ["layer_field_visible"] = ("Visible", "Vidljiv"),
            ["layer_section_text"] = ("Text", "Tekst"),
            ["layer_field_text"] = ("Text content", "Sadržaj teksta"),
            ["layer_field_font"] = ("Font family", "Font"),
            ["layer_field_font_size"] = ("Font size", "Veličina fonta"),
            ["layer_field_color"] = ("Text color (#RRGGBB)", "Boja teksta (#RRGGBB)"),
            ["layer_field_bold"] = ("Bold", "Podebljano"),
            ["layer_field_italic"] = ("Italic", "Kurziv"),
            ["layer_section_shape"] = ("Shape", "Oblik"),
            ["layer_field_fill_enabled"] = ("Fill enabled", "Popuna uključena"),
            ["layer_field_fill_color"] = ("Fill color (#RRGGBB)", "Boja popune (#RRGGBB)"),
            ["layer_field_stroke_enabled"] = ("Outline enabled", "Kontura uključena"),
            ["layer_field_stroke_color"] = ("Outline color (#RRGGBB)", "Boja konture (#RRGGBB)"),
            ["layer_field_stroke_width"] = ("Outline width", "Debljina konture"),
            ["layer_section_image"] = ("Image source", "Izvor slike"),

            // ===== New canvas =====
            ["canvas_width_prompt"] = ("Canvas width (px):", "Širina platna (px):"),
            ["canvas_height_prompt"] = ("Canvas height (px):", "Visina platna (px):"),
            ["canvas_created"] = ("New blank canvas: {0}x{1} pixels", "Novo prazno platno: {0}x{1} piksela"),
            ["canvas_info_blank"] = ("Blank canvas — {0}x{1}px, no image open", "Prazno platno — {0}x{1}px, nema otvorene slike"),

            // ===== Lektura (proofreading) =====
            ["menu_proofread"] = ("Pro_ofreading", "_Lektura"),
            ["menu_proofread_document"] = ("Proofread a _document...", "Lekturiši _dokument..."),
            ["proof_open_filter"] = (
                "All Supported Documents|*.pdf;*.docx;*.doc;*.rtf;*.odt;*.epub;*.html;*.htm;*.xhtml;*.srt;*.vtt;*.txt;*.md|PDF (*.pdf)|*.pdf|Word 2007+ (*.docx)|*.docx|Word 97-2003 (*.doc)|*.doc|Rich Text (*.rtf)|*.rtf|OpenDocument (*.odt)|*.odt|EPUB (*.epub)|*.epub|HTML (*.html;*.htm;*.xhtml)|*.html;*.htm;*.xhtml|Subtitles (*.srt;*.vtt)|*.srt;*.vtt|Text (*.txt;*.md)|*.txt;*.md",
                "Svi podržani dokumenti|*.pdf;*.docx;*.doc;*.rtf;*.odt;*.epub;*.html;*.htm;*.xhtml;*.srt;*.vtt;*.txt;*.md|PDF (*.pdf)|*.pdf|Word 2007+ (*.docx)|*.docx|Word 97-2003 (*.doc)|*.doc|Rich Text (*.rtf)|*.rtf|OpenDocument (*.odt)|*.odt|EPUB (*.epub)|*.epub|HTML (*.html;*.htm;*.xhtml)|*.html;*.htm;*.xhtml|Titlovi (*.srt;*.vtt)|*.srt;*.vtt|Tekst (*.txt;*.md)|*.txt;*.md"),
            ["proof_save_filter"] = ("Text (*.txt)|*.txt|Word 2007+ (*.docx)|*.docx|Rich Text (*.rtf)|*.rtf", "Tekst (*.txt)|*.txt|Word 2007+ (*.docx)|*.docx|Rich Text (*.rtf)|*.rtf"),
            ["menu_layer_proofread"] = ("Proofread text...", "Lektura teksta..."),
            ["proof_title_layer"] = ("Proofreading — text layer", "Lektura — tekst sloja"),
            ["proof_title_document"] = ("Proofreading — {0}", "Lektura — {0}"),
            ["proof_text_label"] = ("Text", "Tekst"),
            ["proof_run"] = ("Run proofreading", "Pokreni lekturu"),
            ["proof_running"] = ("Analyzing text with AI...", "AI analizira tekst..."),
            ["proof_running_chunk"] = ("Analyzing part {0} of {1}...", "Analiziram deo {0} od {1}..."),
            ["proof_finalizing"] = ("Finishing up...", "Završavam..."),
            ["proof_cancel"] = ("Cancel", "Otkaži"),
            ["proof_cancelled"] = ("Proofreading cancelled.", "Lektura otkazana."),
            ["proof_found_issues"] = ("Found {0} suggestion(s) below.", "Pronađeno {0} predloga ispod."),
            ["proof_no_issues"] = ("No issues found — text looks good.", "Nema primedbi — tekst izgleda dobro."),
            ["proof_issues_label"] = ("Suggestions", "Predlozi"),
            ["proof_col_type"] = ("Type", "Tip"),
            ["proof_col_original"] = ("Original", "Original"),
            ["proof_col_suggestion"] = ("Suggestion", "Predlog"),
            ["proof_col_explanation"] = ("Explanation", "Objašnjenje"),
            ["proof_apply_selected"] = ("Apply selected", "Primeni izabrano"),
            ["proof_apply_all"] = ("Apply all", "Primeni sve"),
            ["proof_use_rewrite"] = ("Use AI's full rewrite", "Koristi AI prepis celog teksta"),
            ["proof_type_spelling"] = ("Spelling", "Pravopis"),
            ["proof_type_grammar"] = ("Grammar", "Gramatika"),
            ["proof_type_style"] = ("Style", "Stil"),
            ["proof_saved"] = ("Proofread text saved to {0}", "Lektorisan tekst sačuvan u {0}"),
        };

        public static string T(string key)
        {
            if (!Table.TryGetValue(key, out var v)) return key;
            return Current == "sr" ? v.sr : v.en;
        }

        public static void ApplyToResources()
        {
            var res = Application.Current?.Resources;
            if (res == null) return;
            foreach (var kvp in Table)
                res["L_" + kvp.Key] = Current == "sr" ? kvp.Value.sr : kvp.Value.en;
        }

        public static void SetLanguage(string code, bool persist = true)
        {
            Current = code == "sr" ? "sr" : "en";
            ApplyToResources();
            if (persist) Save();
            LanguageChanged?.Invoke();
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var code = File.ReadAllText(SettingsPath).Trim();
                    Current = code == "sr" ? "sr" : "en";
                }
            }
            catch { Current = "en"; }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, Current);
            }
            catch { }
        }
    }
}
