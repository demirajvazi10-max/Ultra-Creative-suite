using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace UltraAudioEditor.Localization
{
    // Centralni sistem za prevode. Konvencija ista kao LanguageManager u Video Editoru.
    // XAML koristi {DynamicResource L_kljuc}, kod koristi Lang.T("kljuc").
    // Novi jezik = dodati kolonu u tabelu ispod.
    public static class Lang
    {
        public static string Current { get; private set; } = "en";
        public static event Action LanguageChanged;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UltraAudioEditor", "language.txt");

        // key -> (english, serbian)
        private static readonly Dictionary<string, (string en, string sr)> Table = new Dictionary<string, (string, string)>
        {
            // ===== Main window / naslov =====
            ["app_title"] = ("Ultra Audio Editor — Professional Audio Editor", "Ultra Audio Editor — Profesionalni Audio Editor"),
            ["statusbar_ready"] = ("Ultra Audio Editor v1.0 | JAWS accessible", "Ultra Audio Editor v1.0 | JAWS pristupacno"),

            // ===== Meni: Fajl =====
            ["menu_file"] = ("_File", "_Fajl"),
            ["menu_new_project"] = ("_New project (Ctrl+N)", "_Novi projekat (Ctrl+N)"),
            ["menu_open_project"] = ("_Open project (Ctrl+O)", "_Otvori projekat (Ctrl+O)"),
            ["menu_save"] = ("_Save (Ctrl+S)", "_Sačuvaj (Ctrl+S)"),
            ["menu_save_as"] = ("Save _as (Ctrl+Shift+S)", "Sačuvaj _kao (Ctrl+Shift+S)"),
            ["menu_import_audio"] = ("_Import audio (Ctrl+I)", "_Uvezi audio (Ctrl+I)"),
            ["menu_export_audio"] = ("_Export audio (Ctrl+E)", "_Izvezi audio (Ctrl+E)"),
            ["menu_exit"] = ("E_xit", "I_zlaz"),

            // ===== Meni: Uredjivanje =====
            ["menu_edit"] = ("_Edit", "_Uređivanje"),
            ["menu_undo"] = ("_Undo (Ctrl+Z)", "_Poništi (Ctrl+Z)"),
            ["menu_redo"] = ("_Redo (Ctrl+Y)", "_Ponovi (Ctrl+Y)"),
            ["menu_normalize"] = ("_Normalize", "_Normalizuj"),
            ["menu_fade_in"] = ("Fade _In", "Fade _In"),
            ["menu_fade_out"] = ("Fade _Out", "Fade _Out"),

            // ===== Meni: Klip =====
            ["menu_clip"] = ("_Clip", "_Klip"),
            ["menu_set_position"] = ("Set _position... (F2)", "Postavi _poziciju... (F2)"),
            ["menu_move_left_1s"] = ("Move left 1s (Ctrl+Left)", "Pomjeri lijevo 1s (Ctrl+Lijevo)"),
            ["menu_move_right_1s"] = ("Move right 1s (Ctrl+Right)", "Pomjeri desno 1s (Ctrl+Desno)"),
            ["menu_move_left_01s"] = ("Move left 0.1s (Ctrl+Shift+Left)", "Pomjeri lijevo 0.1s (Ctrl+Shift+Lijevo)"),
            ["menu_move_right_01s"] = ("Move right 0.1s (Ctrl+Shift+Right)", "Pomjeri desno 0.1s (Ctrl+Shift+Desno)"),
            ["menu_delete_clip"] = ("_Delete clip (Shift+Del)", "_Obriši klip (Shift+Del)"),

            // ===== Meni: Trake =====
            ["menu_tracks"] = ("_Tracks", "_Trake"),
            ["menu_add_track"] = ("_Add track (Ctrl+Alt+T)", "_Dodaj traku (Ctrl+Alt+T)"),
            ["menu_delete_track"] = ("_Delete track", "_Obriši traku"),
            ["menu_duplicate_track"] = ("D_uplicate track (Ctrl+D)", "D_upliraj traku (Ctrl+D)"),
            ["menu_move_up"] = ("Move up (Alt+Up)", "Pomjeri gore (Alt+Up)"),
            ["menu_move_down"] = ("Move down (Alt+Down)", "Pomjeri dole (Alt+Down)"),
            ["menu_mute_all"] = ("_Mute all", "_Utišaj sve"),
            ["menu_unmute_all"] = ("Unmute _all", "Aktiviraj _sve"),

            // ===== Meni: AI =====
            ["menu_ai"] = ("A_I Functions", "A_I Funkcije"),
            ["menu_ai_transcription"] = ("AI _Transcription", "AI _Transkripcija"),
            ["menu_ai_noise"] = ("AI _Noise removal", "AI _Uklanjanje šuma"),
            ["menu_ai_smartcut"] = ("AI _SmartCut", "AI _SmartCut"),
            ["menu_ai_vocal_sep"] = ("AI _Vocal Separator", "AI _Vocal Separator"),
            ["menu_ai_describe"] = ("AI Project _description", "AI _Opis projekta"),
            ["menu_ai_vocal_mixer"] = ("AI Vocal _Mixer", "AI Vocal _Mixer"),
            ["menu_ai_eq"] = ("AI E_Q recommendations", "AI E_Q preporuke"),
            ["menu_ai_autolevel"] = ("AI Auto _Level", "AI Auto _Level"),

            // ===== Meni: Pogled =====
            ["menu_view"] = ("_View", "_Pogled"),
            ["menu_zoom_in"] = ("Zoom in (Ctrl++)", "Uvećaj (Ctrl++)"),
            ["menu_zoom_out"] = ("Zoom out (Ctrl+-)", "Umanji (Ctrl+-)"),
            ["menu_zoom_fit"] = ("Fit to screen (Ctrl+0)", "Podesi na ekran (Ctrl+0)"),

            // ===== Meni: Jezik =====
            ["menu_language"] = ("_Language", "_Jezik"),
            ["menu_lang_en"] = ("English", "English"),
            ["menu_lang_sr"] = ("Srpski", "Srpski"),

            // ===== Meni: Pomoc =====
            ["menu_help"] = ("_Help", "_Pomoć"),
            ["menu_shortcuts"] = ("_Keyboard shortcuts", "_Tastaturne prečice"),
            ["menu_about"] = ("_About", "_O programu"),

            // ===== Toolbar tooltips =====
            ["tt_new_project"] = ("New project (Ctrl+N)", "Novi projekat (Ctrl+N)"),
            ["tt_import"] = ("Import audio (Ctrl+I)", "Uvezi audio (Ctrl+I)"),
            ["tt_export"] = ("Export (Ctrl+E)", "Izvezi (Ctrl+E)"),
            ["tt_undo"] = ("Undo (Ctrl+Z)", "Poništi (Ctrl+Z)"),
            ["tt_redo"] = ("Redo (Ctrl+Y)", "Ponovi (Ctrl+Y)"),
            ["tt_normalize"] = ("Normalize volume", "Normalizuj glasnoću"),
            ["tt_fade_in"] = ("Fade In", "Fade In"),
            ["tt_fade_out"] = ("Fade Out", "Fade Out"),
            ["tt_zoom_out"] = ("Zoom out (Ctrl+-)", "Umanji (Ctrl+-)"),
            ["tt_zoom_in"] = ("Zoom in (Ctrl++)", "Uvećaj (Ctrl++)"),
            ["tt_zoom_fit"] = ("Fit (Ctrl+0)", "Fit (Ctrl+0)"),
            ["tt_new_track"] = ("New track (Ctrl+Alt+T)", "Nova traka (Ctrl+Alt+T)"),
            ["tt_to_start"] = ("Go to beginning (Home)", "Na početak (Home)"),
            ["tt_play_pause"] = ("Play/Pause (Space)", "Reprodukuj/Pauziraj (Space)"),
            ["tt_stop"] = ("Stop (S)", "Zaustavi (S)"),
            ["tt_record"] = ("Record (R)", "Snimi (R)"),
            ["tt_to_end"] = ("Go to end (End)", "Na kraj (End)"),
            ["tt_loop"] = ("Loop (L)", "Loop (L)"),

            // ===== Transport / master =====
            ["master_label"] = ("Master:", "Master:"),
            ["master_help"] = ("Set master volume from 0 to 100 percent", "Podesi master glasnoću od 0 do 100 posto"),
            ["visual_mode_btn"] = ("Visual mode", "Vizualni mod"),
            ["visual_mode_tt"] = ("Visual mode — waveform display (Alt+W)", "Vizualni mod — waveform prikaz (Alt+W)"),
            ["jaws_mode_btn"] = ("JAWS mode (Alt+W)", "JAWS mod (Alt+W)"),
            ["jaws_mode_tt"] = ("JAWS mode — accessible text view (Alt+W)", "JAWS mod — tekstualni pristupacni prikaz (Alt+W)"),
            ["visual_mode_indicator"] = ("● VISUAL MODE", "● VIZUALNI MOD"),
            ["jaws_mode_indicator"] = ("● JAWS MODE", "● JAWS MOD"),
            ["tracks_header"] = ("TRACKS", "TRAKE"),
            ["vol_short"] = ("Vol", "Vol"),

            // ===== Desni panel =====
            ["effects_header"] = ("Effects", "Efekti"),
            ["select_track_effects"] = ("Select a track to see effects.", "Odaberite traku da vidite efekte."),
            ["ai_panel_header"] = ("AI Functions", "AI Funkcije"),
            ["api_key_label"] = ("Anthropic API Key", "Anthropic API Ključ"),
            ["ai_provider_label"] = ("AI Provider", "AI Provajder"),
            ["ollama_local"] = ("Ollama (LOCAL, default)", "Ollama (LOKALNO, podrazumevano)"),
            ["groq_free"] = ("Groq (FREE)", "Groq (BESPLATNO)"),
            ["anthropic_claude"] = ("Anthropic Claude", "Anthropic Claude"),
            ["groq_quota"] = ("Ollama: completely free, works offline. Groq: 14,400 free requests/day with a key.", "Ollama: potpuno besplatno, radi bez interneta. Groq: 14.400 besplatnih zahtjeva/dan uz ključ."),
            ["transcription_lang"] = ("Transcription language:", "Jezik transkripcije:"),
            ["noise_level"] = ("Noise removal level:", "Nivo uklanjanja suma:"),
            ["silence_threshold"] = ("Silence threshold (SmartCut):", "Prag tisine (SmartCut):"),
            ["ai_progress"] = ("AI processing progress:", "Napredak AI obrade:"),
            ["ai_results"] = ("Results:", "Rezultati:"),
            ["export_header"] = ("Export", "Izvoz"),
            ["export_format"] = ("Export format", "Format izvoza"),

            // ===== SetClipPositionDialog =====
            ["setpos_title"] = ("Set clip position", "Postavi poziciju klipa"),
            ["setpos_intro"] = ("Enter the new start position of the clip in seconds:", "Unesite novu startnu poziciju klipa u sekundama:"),
            ["setpos_sec_help"] = ("Enter the number of seconds, e.g. 15 or 15.5", "Unesite broj sekundi, npr. 15 ili 15.5"),
            ["setpos_mmss_intro"] = ("or in MM:SS format (e.g. 0:15 or 1:30):", "ili u formatu MM:SS (npr. 0:15 ili 1:30):"),
            ["setpos_mmss_help"] = ("Enter in MM:SS format, e.g. 0:15 for 15 seconds", "Unesite u formatu MM:SS, npr. 0:15 za 15 sekundi"),
            ["btn_cancel"] = ("Cancel", "Otkaži"),
            ["btn_set"] = ("Set", "Postavi"),

            // ===== AccessibleTrackList XAML =====
            ["select_track_left"] = ("Select a track from the left panel", "Odaberite traku sa lijevog panela"),

            // ===== Kontekst meni klipa (XAML) =====
            ["ctx_set_clip_pos"] = ("Set clip position... (F2)", "Postavi poziciju klipa... (F2)"),
            ["ctx_delete_clip"] = ("Delete clip (Shift+Delete)", "Obriši klip (Shift+Delete)"),

            // ===== ViewModel: status/announce poruke =====
            ["ai_results_placeholder"] = ("AI results will appear here.", "Ovdje će se pojaviti AI rezultati."),
            ["groq_key_hint"] = ("Groq API key — free at console.groq.com", "Groq API ključ — besplatno na console.groq.com"),
            ["ollama_key_hint"] = ("Ollama runs locally — no API key needed.", "Ollama radi lokalno — API ključ nije potreban."),
            ["anthropic_key_hint"] = ("Anthropic API key — console.anthropic.com", "Anthropic API ključ — console.anthropic.com"),
            ["playback_finished"] = ("Playback finished.", "Reprodukcija završena."),
            ["playback_started"] = ("Playback started.", "Reprodukcija pokrenuta."),
            ["paused_at"] = ("Paused at {0}.", "Pauzirano na {0}."),
            ["to_start"] = ("At the beginning.", "Na početak."),
            ["loop_on"] = ("Loop on.", "Loop uključen."),
            ["loop_off"] = ("Loop off.", "Loop isključen."),
            ["all_muted"] = ("All tracks muted.", "Sve trake utišane."),
            ["new_project_confirm"] = ("New project? All unsaved changes will be lost.", "Novi projekat? Sve nesnimljene izmjene biti će izgubljene."),
            ["new_project_title"] = ("New project", "Novi projekat"),
            ["new_project_created"] = ("New project created.", "Novi projekat kreiran."),
            ["export_done_announce"] = ("Export finished: {0}", "Izvoz završen: {0}"),
            ["export_done_msg"] = ("Audio exported successfully:\n{0}", "Audio uspješno izvezen:\n{0}"),
            ["export_done_title"] = ("Export finished", "Izvoz završen"),
            ["stopped"] = ("Stopped. Position at the beginning.", "Zaustavljeno. Pozicija na početku."),
            ["record_hint"] = ("Recording: this feature requires audio input setup. Use Settings to choose a microphone.", "Snimanje: ova funkcija zahtijeva podešavanje audio ulaza. Koristite Settings da odaberete mikrofon."),
            ["nothing_to_undo"] = ("Nothing to undo.", "Ništa za poništiti."),
            ["undone"] = ("Undone.", "Poništeno."),
            ["nothing_to_redo"] = ("Nothing to redo.", "Ništa za ponavljati."),
            ["redone"] = ("Redone.", "Ponovljeno."),
            ["no_audio_normalize"] = ("No audio content to normalize.", "Nema audio sadržaja za normalizaciju."),
            ["file_not_found"] = ("File not found.", "Fajl nije pronađen."),
            ["normalize_done"] = ("Normalization finished.", "Normalizacija završena."),
            ["no_audio_fade"] = ("No audio for fade.", "Nema audio za fade."),
            ["fade_in_applied"] = ("Fade in applied (2 seconds).", "Fade in primijenjen (2 sekunde)."),
            ["fade_out_applied"] = ("Fade out applied (2 seconds).", "Fade out primijenjen (2 sekunde)."),
            ["no_tracks"] = ("No tracks.", "Nema traka."),
            ["track_moved_up"] = ("Track '{0}' moved up.", "Traka '{0}' premještena gore."),
            ["track_moved_down"] = ("Track '{0}' moved down.", "Traka '{0}' premještena dole."),
            ["track_added"] = ("Track added: {0}. Total {1} tracks.", "Dodana traka: {0}. Ukupno {1} traka."),
            ["track_deleted"] = ("Track '{0}' deleted. {1} tracks remaining.", "Traka '{0}' obrisana. Preostalo {1} traka."),
            ["track_duplicated"] = ("Track '{0}' duplicated.", "Traka '{0}' duplicirana."),
            ["track_n"] = ("Track {0}", "Traka {0}"),
            ["clip_moved"] = ("Clip '{0}' moved to {1:F2} seconds.", "Klip '{0}' pomjeren na {1:F2} sekundi."),
            ["clip_set"] = ("Clip '{0}' set to {1:F2} seconds.", "Klip '{0}' postavljen na {1:F2} sekundi."),
            ["clip_deleted"] = ("Clip '{0}' deleted.", "Klip '{0}' obrisan."),
            ["need_api_key_announce"] = ("Enter your {0} API key in the AI panel.", "Unesite {0} API ključ u AI panelu."),
            ["need_api_key_msg"] = ("Please enter your {0} API key in the 'API Key' field in the right panel.\n\nGet a free key at: {1}", "Molimo unesite {0} API ključ u polju 'API Ključ' u desnom panelu.\n\nDobijte besplatni ključ na: {1}"),
            ["need_api_key_title"] = ("API key required", "API ključ potreban"),
            ["error_prefix"] = ("Error: {0}", "Greška: {0}"),
            ["error_title"] = ("Error", "Greska"),
            ["ai_error"] = ("AI error. Check the AI panel.", "AI greška. Pogledaj AI panel."),
            ["ollama_not_running"] = ("Ollama is not running on this computer (localhost:11434).\n\nStart the Ollama app, or switch the AI provider to Groq/Anthropic in settings if you have a cloud API key.", "Ollama nije pokrenuta na ovom računaru (localhost:11434).\n\nPokrenite Ollama aplikaciju, ili u podešavanjima prebacite AI provajdera na Groq/Anthropic ako imate cloud API ključ."),
            ["transcription_done"] = ("Transcription finished. Results in the AI panel.", "Transkripcija završena. Rezultati u AI panelu."),
            ["noise_started"] = ("AI noise analysis started...", "AI analiza šuma pokrenuta..."),
            ["noise_done"] = ("Noise analysis finished. Results in the AI panel.", "Analiza šuma završena. Rezultati u AI panelu."),
            ["smartcut_done"] = ("SmartCut analysis finished. Suggestions in the AI panel.", "SmartCut analiza završena. Prijedlozi u AI panelu."),
            ["describe_started"] = ("AI is creating a verbal description of the project...", "AI kreira verbalni opis projekta..."),
            ["vocal_tips_done"] = ("Vocal separation tips available in the AI panel.", "Savjeti za vokalnu separaciju dostupni u AI panelu."),
            ["vocalmix_done"] = ("Vocal mix recommendations available in the AI panel.", "Vocal mix preporuke dostupne u AI panelu."),
            ["eq_select_track"] = ("Select a track for EQ recommendations.", "Odaberite traku za EQ preporuke."),
            ["eq_done"] = ("EQ recommendations available in the AI panel.", "EQ preporuke dostupne u AI panelu."),
            ["autolevel_done"] = ("Level analysis finished. Recommendations in the AI panel.", "Analiza nivoa završena. Preporuke u AI panelu."),
            ["ready_hint"] = ("Ready. Press Ctrl+I to import an audio file.", "Spreman. Pritisnite Ctrl+I da uvezete audio fajl."),
            ["imported_file"] = ("Imported file: {0}, duration {1:F1} seconds. Waveform shown.", "Uvezen fajl: {0}, trajanje {1:F1} sekundi. Waveform prikazan."),
            ["invalid_file_format"] = ("Invalid file format.", "Neispravan format fajla."),
            ["project_opened"] = ("Project opened: {0}. {1} tracks loaded.", "Projekat otvoren: {0}. {1} traka ucitano."),
            ["dlg_save_project"] = ("Save project — Ultra Audio Editor", "Sačuvaj projekat — Ultra Audio Editor"),
            ["dlg_open_project"] = ("Open project — Ultra Audio Editor", "Otvori projekat — Ultra Audio Editor"),
            ["dlg_import_audio"] = ("Import audio file — Ultra Audio Editor", "Uvezi audio fajl - Ultra Audio Editor"),
            ["dlg_export_audio"] = ("Export audio — Ultra Audio Editor", "Izvezi audio - Ultra Audio Editor"),
            ["project_filter"] = ("Ultra Audio project|*.paproj|All files|*.*", "Ultra Audio projekat|*.paproj|Svi fajlovi|*.*"),

            // ===== MainWindow.xaml.cs =====
            ["app_loaded"] = ("Ultra Audio Editor loaded. Alt+W for JAWS mode, F6 for status, Ctrl+I to import.", "Ultra Audio Editor ucitan. Alt+W za JAWS mod, F6 za status, Ctrl+I za uvoz."),
            ["exit_confirm"] = ("Exit Ultra Audio Editor?", "Izaći iz Ultra Audio Editora?"),
            ["exit_title"] = ("Exit", "Izlaz"),
            ["dropped_file"] = ("Dropped file: {0}, duration {1:F1}s.", "Prevučen fajl: {0}, trajanje {1:F1}s."),
            ["position_is"] = ("Position: {0}.", "Pozicija: {0}."),
            ["shortcuts_title"] = ("Keyboard shortcuts — Ultra Audio Editor", "Tastaturne prečice - Ultra Audio Editor"),
            ["visual_mode_on"] = ("Visual mode active. Waveform display.", "Vizualni mod aktiviran. Waveform prikaz."),
            ["jaws_mode_on"] = ("JAWS mode active. Tab to navigate, Shift+F10 for track menu, F6 for status.", "JAWS mod aktiviran. Tab za navigaciju, Shift+F10 za meni trake, F6 za status."),
            ["about_get_key"] = ("Get it at: console.anthropic.com", "Dobijte ga na: console.anthropic.com"),
            ["about_title"] = ("About Ultra Audio Editor", "O Ultra Audio Editoru"),
            ["language_changed"] = ("Language changed to English.", "Jezik promijenjen na srpski."),

            // ===== ProjectStatusWindow =====
            ["status_title"] = ("Project status — Ultra Audio Editor", "Status projekta — Ultra Audio Editor"),
            ["status_header"] = ("Project status (F6)", "Status projekta (F6)"),
            ["status_accname"] = ("Project status, all tracks and clips", "Status projekta, sve trake i klipovi"),

            // ===== EffectDialog =====
            ["effect_enabled"] = ("Effect enabled", "Efekat uključen"),
            ["fx_delay"] = ("Delay", "Kašnjenje"),
            ["fx_gain"] = ("Gain", "Pojačanje"),
            ["fx_close_hint"] = ("Close dialog. The effect remains applied.", "Zatvori dijalog. Efekat ostaje primijenjen."),
            ["fx_range_hint"] = ("from {0} to {1}. Arrow keys to adjust.", "od {0} do {1}. Strelice za podešavanje."),

            // ===== AccessibleTrackList (kontekst meniji, JAWS najave) =====
            ["trk_import_here"] = ("Import audio to this track   Ctrl+I", "Uvezi audio na ovu traku   Ctrl+I"),
            ["trk_import_playhead"] = ("Import at playhead  ({0})", "Uvezi i postavi na playhead  ({0})"),
            ["trk_import_at_pos"] = ("Import at position...", "Uvezi i postavi na poziciju..."),
            ["trk_import_at_pos_title"] = ("Import at position", "Uvezi na poziciju"),
            ["trk_import_new_track"] = ("Import audio to a new track", "Uvezi audio na novu traku"),
            ["trk_import_at"] = ("Import audio at {0}", "Uvezi audio na {0}"),
            ["trk_volume_menu"] = ("Volume:  {0:P0}  ---  adjust...", "Glasnoća:  {0:P0}  ---  podesi..."),
            ["trk_volume_title"] = ("Volume: {0}", "Glasnoca: {0}"),
            ["trk_volume_prompt"] = ("Volume from 0 to 100:", "Glasnoca od 0 do 100:"),
            ["trk_volume_announce"] = ("Volume: {0:P0}", "Glasnoca: {0:P0}"),
            ["trk_normalize"] = ("Normalize volume", "Normalizuj glasnoću"),
            ["trk_fade_in"] = ("Fade In  (2 seconds)", "Fade In  (2 sekunde)"),
            ["trk_fade_out"] = ("Fade Out  (2 seconds)", "Fade Out  (2 sekunde)"),
            ["trk_effects"] = ("Effects ", "Efekti "),
            ["trk_combine_with"] = ("Combine with:  {0}  ({1} files)...", "Kombinuj sa:  {0}  ({1} fajlova)..."),
            ["trk_move_up"] = ("Move up   Alt+Up arrow", "Pomjeri gore   Alt+strelica gore"),
            ["trk_move_down"] = ("Move down   Alt+Down arrow", "Pomjeri dole   Alt+strelica dole"),
            ["trk_delete"] = ("Delete track", "Obriši traku"),
            ["trk_rename_prompt"] = ("New name:", "Novi naziv:"),
            ["trk_no_files"] = ("No files", "Nema fajlova"),
            ["trk_no_audio_files"] = ("Track has no audio files.", "Traka nema audio fajlova."),
            ["trk_summary"] = ("Track {0}, {1}, volume {2:P0}, ", "Traka {0}, {1}, glasnoca {2:P0}, "),
            ["trk_files_summary"] = ("{0} files, duration {1}", "{0} fajlova, trajanje {1}"),
            ["trk_files_of"] = ("Files of track {0}. ", "Fajlovi trake {0}. "),
            ["trk_row_info"] = ("{0}  |  Vol {1:P0}  |  {2} file(s)", "{0}  |  Vol {1:P0}  |  {2} fajl(ova)"),
            ["clip_at"] = ("Clip at {0}.", "Klip na {0}."),
            ["clip_move_fwd_1"] = ("Move forward 1s   ({0})", "Pomjeri naprijed 1s   ({0})"),
            ["clip_move_back_1"] = ("Move back 1s      ({0})", "Pomjeri nazad 1s      ({0})"),
            ["clip_move_fwd_01"] = ("Move forward 0.1s  ({0})", "Pomjeri naprijed 0.1s  ({0})"),
            ["clip_move_back_01"] = ("Move back 0.1s    ({0})", "Pomjeri nazad 0.1s    ({0})"),
            ["clip_goto_pos"] = ("Go to position, opens a dialog to enter seconds", "Idi na poziciju, otvara dijalog za unos sekundi"),
            ["clip_import_on_track"] = ("Import to track '{0}' at {1}...", "Uvezi na traku '{0}' na {1}..."),
            ["clip_delete_menu"] = ("Delete clip   Delete", "Obrisi klip   Delete"),
            ["arrows_01_hint"] = ("Left/Right arrows for 0.1 seconds. ", "Strelice levo desno za 0.1 sekunde. "),
            ["ctrl_arrows_hint"] = ("Ctrl plus arrows for 1 second. ", "Ctrl plus strelice za 1 sekundu. "),
            ["enter_tab_hint"] = ("Enter or Tab for the track list. ", "Enter ili Tab za listu traka. "),
            ["demucs_done"] = ("Demucs finished. Imported {0} tracks.", "Demucs zavrsen. Uvezeno {0} traka."),
            ["demucs_pick_folder"] = ("Choose a folder for stems", "Odaberite folder za stemove"),

            // ===== DemucsService =====
            ["demucs_no_audio"] = ("Audio file not found.", "Audio fajl nije pronađen."),
            ["demucs_no_python"] = ("Python not found. Install Python 3.8+ from python.org", "Python nije pronađen. Instalirajte Python 3.8+ sa python.org"),
            ["demucs_checking"] = ("Checking Demucs installation, this can take a moment on first run...", "Proveravam Demucs instalaciju, ovo može malo potrajati prvi put..."),
            ["demucs_not_found_msg"] = ("Demucs not found. Run: pip install demucs", "Demucs nije pronađen. Pokrenite: pip install demucs"),
            ["demucs_available"] = ("Demucs is available.", "Demucs je dostupan."),
            ["demucs_timeout"] = ("Demucs did not respond within 90 seconds (first run can be slow while PyTorch loads — try again). If this keeps happening, run 'python -m demucs --help' directly in a terminal to see what's actually wrong.", "Demucs se nije odazvao u roku od 90 sekundi (prvi put može biti sporo dok se učitava PyTorch — probaj ponovo). Ako se ponavlja, pokreni 'python -m demucs --help' direktno u terminalu da vidiš šta je stvarno u pitanju."),
            ["demucs_searching"] = ("Looking for output files...", "Tražim izlazne fajlove..."),

            // ===== AnthropicService =====
            ["api_error"] = ("API error {0}: {1}", "API greška {0}: {1}"),
            ["no_response"] = ("No response.", "Nema odgovora."),
            ["sys_stem"] = ("You are an audio engineer specialized in stem separation. Respond in English.", "Ti si audio inžinjer specijalizovan za stem separation. Odgovaraj na srpskom jeziku."),
            ["sys_loudness"] = ("You are a loudness normalization expert. Respond in English.", "Ti si loudness normalizacijski stručnjak. Odgovaraj na srpskom jeziku."),
            ["sys_mastering"] = ("You are a mastering engineer. Respond in English with concrete frequencies.", "Ti si mastering inžinjer. Odgovaraj na srpskom jeziku sa konkretnim frekvencijama."),
            ["sys_mix"] = ("You are a professional mix engineer. Respond in English.", "Ti si profesionalni mix inžinjer. Odgovaraj na srpskom jeziku."),


            // ===== AutomationProperties (JAWS/NVDA najave elemenata) =====
            ["acc_ai_eq_track"] = ("AI EQ recommendations for the selected track", "AI EQ preporuke za odabranu traku"),
            ["acc_ai_eq"] = ("AI EQ recommendations", "AI EQ preporuke"),
            ["acc_ai_smartcut"] = ("AI SmartCut", "AI SmartCut"),
            ["acc_ai_levels"] = ("AI analysis and recommendations for volume levels", "AI analiza i preporuke za nivoe glasnoće"),
            ["acc_ai_autolevel"] = ("AI auto level", "AI auto level"),
            ["acc_ai_silence"] = ("AI silence detection and smart cutting", "AI detekcija tišine i pametno rezanje"),
            ["acc_ai_progress"] = ("AI progress", "AI napredak"),
            ["acc_ai_vocalsep"] = ("AI separation of vocals from instrumental", "AI odvajanje glasa od instrumentala"),
            ["acc_ai_describe"] = ("AI project description", "AI opis projekta"),
            ["acc_ai_vocalmix"] = ("AI vocal mixing recommendations", "AI preporuke za miksovanje glasa"),
            ["acc_ai_results"] = ("AI results", "AI rezultati"),
            ["acc_ai_transcribe_full"] = ("AI speech-to-text transcription", "AI transkripcija govora u tekst"),
            ["acc_ai_transcribe"] = ("AI transcription", "AI transkripcija"),
            ["acc_ai_noise_full"] = ("AI background noise removal", "AI uklanjanje pozadinskog šuma"),
            ["acc_ai_noise"] = ("AI noise removal", "AI uklanjanje suma"),
            ["acc_ai_describe_full"] = ("AI verbal description of the audio project", "AI verbalni opis audio projekta"),
            ["acc_ai_vocalmix_short"] = ("AI vocal mix", "AI vocal mix"),
            ["acc_ai_vocalsep_short"] = ("AI vocal separator", "AI vocal separator"),
            ["acc_unmute_all"] = ("Unmute all tracks", "Aktiviraj sve trake"),
            ["acc_right_panel"] = ("Right panel with effects and AI functions", "Desni panel sa efektima i AI funkcijama"),
            ["acc_add_track"] = ("Add a new track", "Dodaj novu traku"),
            ["acc_transport_buttons"] = ("Transport buttons", "Dugmad za transport"),
            ["acc_duplicate_track"] = ("Duplicate the selected track", "Dupliraj odabranu traku"),
            ["acc_file_menu"] = ("File menu", "Fajl meni"),
            ["acc_fine_right"] = ("Fine move clip right", "Fino pomjeranje klipa desno"),
            ["acc_fine_left"] = ("Fine move clip left", "Fino pomjeranje klipa lijevo"),
            ["acc_export_format"] = ("Export format", "Format za izvoz"),
            ["acc_track_volume"] = ("Track volume", "Glasnoća trake"),
            ["acc_main_menu"] = ("Main application menu", "Glavni meni aplikacije"),
            ["acc_exit"] = ("Exit the program", "Izlaz iz programa"),
            ["acc_export_audio"] = ("Export audio", "Izvezi audio"),
            ["acc_jaws_view"] = ("JAWS accessible view of tracks and clips", "JAWS pristupacni prikaz traka i klipova"),
            ["acc_transcription_lang"] = ("Transcription language", "Jezik transkripcije"),
            ["acc_tab_ai"] = ("AI functions tab", "Kartica AI funkcije"),
            ["acc_tab_effects"] = ("Effects tab", "Kartica efekti"),
            ["acc_tab_export"] = ("Export tab", "Kartica izvoz"),
            ["acc_use_anthropic"] = ("Use Anthropic Claude AI", "Koristi Anthropic Claude AI"),
            ["acc_use_groq"] = ("Use Groq AI, free", "Koristi Groq AI, besplatno"),
            ["acc_use_ollama"] = ("Use Ollama, local AI without internet or API key", "Koristi Ollama, lokalni AI bez interneta i bez API ključa"),
            ["acc_track_list_visual"] = ("Audio track list, visual view", "Lista audio traka vizualni prikaz"),
            ["acc_track_list"] = ("Project track list", "Lista traka projekta"),
            ["acc_track_list_help"] = ("Track list. Up and down arrows to navigate, Enter to open, Shift+F10 for menu.", "Lista traka. Strelice gore dole za navigaciju, Enter za otvaranje, Shift+F10 za meni."),
            ["acc_master_slider"] = ("Master volume slider", "Master glasnoća klizač"),
            ["acc_master_volume"] = ("Master volume", "Master glasnoća"),
            ["acc_menu_ai"] = ("AI functions menu", "Meni za AI funkcije"),
            ["acc_menu_view"] = ("View menu", "Meni za pogled"),
            ["acc_menu_help"] = ("Help menu", "Meni za pomoć"),
            ["acc_menu_tracks"] = ("Tracks menu", "Meni za trake"),
            ["acc_menu_clips"] = ("Clip management menu", "Meni za upravljanje klipovima"),
            ["acc_menu_edit"] = ("Edit menu", "Meni za uređivanje"),
            ["acc_mute_btn"] = ("Mute button", "Mute dugme"),
            ["acc_to_end"] = ("Go to end of project", "Na kraj projekta"),
            ["acc_to_start"] = ("Go to beginning", "Na početak"),
            ["acc_noise_level"] = ("Noise removal level", "Nivo noise removal"),
            ["acc_normalize_track"] = ("Normalize the volume of the selected track", "Normalizuj glasnoću odabrane trake"),
            ["acc_new_project"] = ("New project", "Novi projekat"),
            ["acc_about"] = ("About Ultra Audio Editor", "O Ultra Audio Editor programu"),
            ["acc_delete_clip"] = ("Delete the selected clip", "Obriši odabrani klip"),
            ["acc_delete_track"] = ("Delete the selected track", "Obriši odabranu traku"),
            ["acc_lang_select"] = ("Language selection", "Odabir jezika"),
            ["acc_cancel_pos"] = ("Cancel setting the position", "Otkaži postavljanje pozicije"),
            ["acc_open_project"] = ("Open a saved project", "Otvori sacuvani projekat"),
            ["acc_open_api_page"] = ("Open the API key page", "Otvori stranicu za API ključ"),
            ["acc_playhead_help"] = ("Playhead control. Tab to focus the slider, arrows to move.", "Playhead kontrola. Tab za fokus na slajder, strelice za pomjeranje."),
            ["acc_zoom_fit_all"] = ("Fit zoom to the whole project", "Podesi zoom na cijeli projekat"),
            ["acc_zoom_fit"] = ("Fit zoom to screen", "Podesi zoom na ekran"),
            ["acc_start_export"] = ("Start audio file export", "Pokreni izvoz audio fajla"),
            ["acc_start_record"] = ("Start recording", "Pokreni snimanje"),
            ["acc_clip_right_1s"] = ("Move clip right by one second", "Pomjeri klip desno za jednu sekundu"),
            ["acc_clip_left_1s"] = ("Move clip left by one second", "Pomjeri klip lijevo za jednu sekundu"),
            ["acc_track_down"] = ("Move track down", "Pomjeri traku dole"),
            ["acc_track_up"] = ("Move track up", "Pomjeri traku gore"),
            ["acc_undo"] = ("Undo", "Poništi"),
            ["acc_redo"] = ("Redo", "Ponovi"),
            ["acc_status_message"] = ("Status message", "Poruka statusa"),
            ["acc_set_clip_pos"] = ("Set the position of the selected clip", "Postavi poziciju odabranog klipa"),
            ["acc_confirm_pos"] = ("Confirm and set the new clip position", "Potvrdi i postavi novu poziciju klipa"),
            ["acc_pos_mmss"] = ("Position in minutes and seconds format", "Pozicija u formatu minuti i sekundi"),
            ["acc_pos_seconds"] = ("Position in seconds", "Pozicija u sekundama"),
            ["acc_silence_slider"] = ("Silence threshold slider", "Prag tisine klizac"),
            ["acc_switch_jaws"] = ("Switch to JAWS accessible text view", "Prebaci na JAWS pristupacni tekstualni prikaz"),
            ["acc_switch_visual"] = ("Switch to visual waveform view", "Prebaci na vizualni waveform prikaz"),
            ["acc_show_shortcuts"] = ("Show keyboard shortcuts", "Prikaži tastaturne prečice"),
            ["acc_fade_in"] = ("Apply fade in", "Primijeni fade in"),
            ["acc_fade_out"] = ("Apply fade out", "Primijeni fade out"),
            ["acc_splitter"] = ("Panel splitter", "Razdjelnik panela"),
            ["acc_play_pause"] = ("Play or pause", "Reprodukuj ili pauziraj"),
            ["acc_save_as"] = ("Save the project under a new name", "Sacuvaj projekat pod novim imenom"),
            ["acc_save"] = ("Save the project", "Sacuvaj projekat"),
            ["acc_solo_btn"] = ("Solo button", "Solo dugme"),
            ["acc_status_bar"] = ("Application status bar", "Status traka programa"),
            ["acc_toolbar"] = ("Toolbar", "Toolbar sa alatima"),
            ["acc_transport"] = ("Playback transport controls", "Transport kontrole za reprodukciju"),
            ["acc_current_pos"] = ("Current playback position", "Trenutna pozicija reproducije"),
            ["acc_zoom_level"] = ("Current zoom level", "Trenutni nivo zuma"),
            ["acc_loop"] = ("Enable loop playback", "Uključi loop reprodukciju"),
            ["acc_workspace"] = ("Ultra Audio Editor workspace", "Ultra Audio Editor radni prostor"),
            ["acc_zoom_out"] = ("Zoom out", "Umanji prikaz"),
            ["acc_enter_api_key"] = ("Enter API key", "Unesi API ključ"),
            ["acc_pos_help"] = ("Instructions: enter the position in seconds", "Uputstvo: unesite poziciju u sekundama"),
            ["acc_mute_all"] = ("Mute all tracks", "Utišaj sve trake"),
            ["acc_import_file"] = ("Import an audio file", "Uvezi audio fajl"),
            ["acc_import"] = ("Import audio", "Uvezi audio"),
            ["acc_zoom_in"] = ("Zoom in", "Uvećaj prikaz"),
            ["acc_stop"] = ("Stop playback", "Zaustavi reprodukciju"),
            ["acc_lang_menu"] = ("Language selection menu", "Meni za odabir jezika"),
            ["unexpected_error"] = ("Unexpected error: {0}\n\n{1}", "Neočekivana greška: {0}\n\n{1}"),
            ["unexpected_error_title"] = ("Ultra Audio Editor — Error", "Ultra Audio Editor - Greška"),
            ["shortcuts_text"] = ("KEYBOARD SHORTCUTS — Ultra Audio Editor\n\nTRANSPORT:\n  Space          — Play / Pause\n  S              — Stop\n  R              — Record\n  L              — Loop on/off\n  Home           — Go to beginning\n  End            — Go to end\n\nFILE:\n  Ctrl+N         — New project\n  Ctrl+I         — Import audio\n  Ctrl+E         — Export audio\n\nEDIT:\n  Ctrl+Z         — Undo\n  Ctrl+Y         — Redo\n  Ctrl+D         — Duplicate track\n\nTRACKS:\n  Ctrl+Alt+T     — New track\n  Alt+Up         — Track up\n  Alt+Down       — Track down\n\nVIEW:\n  Ctrl++         — Zoom in\n  Ctrl+-         — Zoom out\n  Ctrl+0         — Fit to screen\n\nCLIPS:\n  Click clip        — Select clip\n  Double click      — Set position (dialog)\n  F2                — Set position of selected clip\n  Ctrl+Left         — Move clip left 1s\n  Ctrl+Right        — Move clip right 1s\n  Ctrl+Shift+Left   — Move clip left 0.1s\n  Ctrl+Shift+Right  — Move clip right 0.1s\n  Shift+Delete      — Delete clip\n  Right click       — Clip context menu\n\nNAVIGATION (JAWS):\n  Tab            — Next element\n  Shift+Tab      — Previous element\n  Enter/Space    — Activate button\n  Alt+F4         — Exit", "TASTATURNE PREČICE - Ultra Audio Editor\n\nTRANSPORT:\n  Space          — Reprodukuj / Pauziraj\n  S              — Zaustavi\n  R              — Snimi\n  L              — Loop uključi/isključi\n  Home           — Na početak\n  End            — Na kraj\n\nFAJL:\n  Ctrl+N         — Novi projekat\n  Ctrl+I         — Uvezi audio\n  Ctrl+E         — Izvezi audio\n\nUREĐIVANJE:\n  Ctrl+Z         — Poništi\n  Ctrl+Y         — Ponovi\n  Ctrl+D         — Dupliraj traku\n\nTRAKE:\n  Ctrl+Alt+T     — Nova traka\n  Alt+Up         — Traka gore\n  Alt+Down       — Traka dole\n\nPRIKAZ:\n  Ctrl++         — Uvećaj\n  Ctrl+-         — Umanji\n  Ctrl+0         — Podesi na ekran\n\nKLIPOVI:\n  Klik na klip      — Odaberi klip\n  Dvostruki klik    — Postavi poziciju (dialog)\n  F2                — Postavi poziciju odabranog klipa\n  Ctrl+Lijevo       — Pomjeri klip lijevo 1s\n  Ctrl+Desno        — Pomjeri klip desno 1s\n  Ctrl+Shift+Lijevo — Pomjeri klip lijevo 0.1s\n  Ctrl+Shift+Desno  — Pomjeri klip desno 0.1s\n  Shift+Delete      — Obriši klip\n  Desni klik        — Kontekst meni klipa\n\nNAVIGACIJA (JAWS):\n  Tab            — Sledeći element\n  Shift+Tab      — Prethodni element\n  Enter/Space    — Aktiviraj dugme\n  Alt+F4         — Izlaz"),
            ["about_text"] = ("Ultra Audio Editor v1.0\n\nProfessional Windows audio editor with full accessibility.\nCompatible with the JAWS for Windows screen reader.\n\nCreated by Demir Ajvazi\n\nTechnologies:\n\u2022 WPF (.NET 8)\n\u2022 NAudio — audio engine\n\u2022 Anthropic Claude AI — AI features\n\nAI features require an Anthropic API key.\nGet it at: console.anthropic.com", "Ultra Audio Editor v1.0\n\nProfesionalni Windows audio editor sa punom pristupačnošću.\nKompatibilan sa JAWS for Windows čitačem ekrana.\n\nAutor: Demir Ajvazi\n\nTehnologije:\n\u2022 WPF (.NET 8)\n\u2022 NAudio — audio engine\n\u2022 Anthropic Claude AI — AI funkcije\n\nAI funkcije zahtijevaju Anthropic API ključ.\nDobijte ga na: console.anthropic.com"),
            ["clip_selected"] = ("Clip selected: {0}, position {1:F2}s, duration {2:F2}s. Press Enter to set position, Ctrl+arrows to move.", "Odabran klip: {0}, pozicija {1:F2}s, trajanje {2:F2}s. Pritisni Enter za postavljanje pozicije, Ctrl+strelice za pomjeranje."),
            ["audio_filter"] = ("Audio files|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aiff;*.aif|WAV|*.wav|MP3|*.mp3|All files|*.*", "Audio fajlovi|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aiff;*.aif|WAV|*.wav|MP3|*.mp3|Svi fajlovi|*.*"),
            ["describe_done"] = ("Audio description created. Available in the AI panel.", "Audio opis kreiran. Dostupan u AI panelu."),
            ["vocalmix_started"] = ("AI vocal mix recommendations...", "AI preporuke za vocal mix..."),
            ["eq_started"] = ("AI EQ recommendations for track: {0}...", "AI EQ preporuke za traku: {0}..."),
            ["autolevel_started"] = ("AI level analysis...", "AI analiza nivoa..."),
            ["save_error"] = ("Error while saving:\n{0}", "Greska pri cuvanju:\n{0}"),
            ["open_error"] = ("Error while opening:\n{0}", "Greska pri otvaranju:\n{0}"),
            ["st_project"] = ("PROJECT: {0}", "PROJEKAT: {0}"),
            ["st_tracks"] = ("Tracks: {0}", "Trake: {0}"),
            ["st_track_line"] = ("TRACK: {0} | Vol: {1:P0} | Mute: {2} | Solo: {3}", "TRAKA: {0} | Vol: {1:P0} | Mute: {2} | Solo: {3}"),
            ["st_no_clips"] = ("  No clips.", "  Nema klipova."),
            ["st_clip_line"] = ("  Clip: {0} | Starts: {1:F3}s | Lasts: {2:F3}s | End: {3:F3}s", "  Klip: {0} | Pocinje: {1:F3}s | Traje: {2:F3}s | Kraj: {3:F3}s"),
            ["yes"] = ("Yes", "Da"),
            ["no"] = ("No", "Ne"),
            ["trk_demucs"] = ("Separate vocals and instrumental  (Demucs)...", "Razdvoji vokal i instrumental  (Demucs)..."),
            ["trk_mute"] = ("Mute track", "Mute traku"),
            ["trk_solo"] = ("Solo track", "Solo traku"),
            ["trk_pan_menu"] = ("Pan:  {0:F2}  ---  adjust...", "Panorama:  {0:F2}  ---  podesi..."),
            ["fx_compressor"] = ("Compressor", "Kompresor"),
            ["trk_duplicate"] = ("Duplicate track   Ctrl+D", "Dupliraj traku   Ctrl+D"),
            ["trk_rename"] = ("Rename track...", "Preimenuj traku..."),
            ["trk_rename_title"] = ("Rename", "Preimenuj"),
            ["clip_header"] = ("  Start: {0}   Duration: {1}   End: {2}", "  Pocetak: {0}   Trajanje: {1}   Kraj: {2}"),
            ["clip_set_pos_menu"] = ("Set position...   F2", "Postavi poziciju...   F2"),
            ["clip_set_playhead"] = ("Set to playhead  ({0})", "Postavi na playhead  ({0})"),
            ["clip_set_at_pos"] = ("Set to position...", "Postavi na poziciju..."),
            ["clip_set_title"] = ("Set clip", "Postavi klip"),
            ["acc_playhead_pos"] = ("Playhead position", "Playhead pozicija"),
            ["goto_btn"] = ("Go to...", "Idi na..."),
            ["goto_title"] = ("Go to position", "Idi na poziciju"),
            ["playhead_hint"] = ("Arrows 0.1s  |  Ctrl+arrows 1s  |  Home/End  |  Space play  |  Shift+F10 menu", "Strelice 0.1s  |  Ctrl+strelice 1s  |  Home/End  |  Space reprodukcija  |  Shift+F10 meni"),
            ["tt_vocal"] = ("Microphone", "Mikrofon"),
            ["tt_instrumental"] = ("Guitar", "Gitara"),
            ["tt_audio"] = ("Audio", "Audio"),
            ["tt_empty"] = ("Empty", "Prazna"),
            ["row_no_clips"] = ("(no clips — Shift+F10 to import)", "(nema klipova — Shift+F10 za uvoz)"),
            ["row_track_summary"] = ("— {0} file(s) below, Shift+F10 for track menu —", "— {0} fajl(ova) ispod, Shift+F10 za meni trake —"),
            ["batch_selected"] = ("Selected: {0} row(s)", "Izabrano: {0} red(ova)"),
            ["batch_mute"] = ("Mute selected tracks", "Utišaj izabrane trake"),
            ["batch_delete_clips"] = ("Delete selected clips", "Obriši izabrane klipove"),
            ["batch_move_fwd"] = ("Move selected clips forward 1s", "Pomjeri izabrane klipove naprijed 1s"),
            ["batch_move_back"] = ("Move selected clips back 1s", "Pomjeri izabrane klipove nazad 1s"),
            ["duration_fmt"] = ("Duration: {0}", "Trajanje: {0}"),
            ["suffix_muted"] = (", muted", ", utisano"),
            ["suffix_solo"] = (", solo", ", solo"),
            ["press_shift_f10"] = ("Press Shift F10 for the track menu.", "Pritisni Shift F10 za meni trake."),
            ["filelist_hint"] = ("Shift+F10 or Apps key = menu  |  F2 = set position  |  Ctrl+arrows = move  |  Del = delete", "Shift+F10 ili Apps tipka = meni  |  F2 = postavi poziciju  |  Ctrl+strelice = pomjeri  |  Del = obrisi"),
            ["no_files_hint"] = ("No files on this track.\nShift+F10 then Import audio, or Ctrl+I.", "Nema fajlova na ovoj traci.\nShift+F10 zatim Uvezi audio, ili Ctrl+I."),
            ["filelist_nav_help"] = ("Up and down arrows to navigate. Shift F10 for menu. F2 to set position. Ctrl arrows to move. Delete to remove.", "Strelice gore i dole za navigaciju. Shift F10 za meni. F2 za postavljanje pozicije. Ctrl strelice za pomjeranje. Delete za brisanje."),
            ["col_name"] = ("Name", "Naziv"),
            ["col_num"] = ("#", "#"),
            ["acc_list_help"] = ("Project list: tracks and clips. Use arrow keys to navigate rows and columns. F2 to set clip position. Ctrl+arrows to move a clip. Delete to remove selected clips. Shift+F10 or the Menu key opens the context menu. Ctrl+Click or Shift+Click to select multiple rows.", "Lista projekta: trake i klipovi. Strelice za kretanje kroz redove i kolone. F2 za poziciju klipa. Ctrl+strelice za pomjeranje klipa. Delete za brisanje izabranih klipova. Shift+F10 ili taster Meni otvara kontekstni meni. Ctrl+klik ili Shift+klik za izbor više redova."),
            ["col_track"] = ("Track", "Traka"),
            ["col_type"] = ("Type", "Tip"),
            ["col_status"] = ("Status", "Status"),
            ["col_start"] = ("Start", "Pocetak"),
            ["col_start_s"] = ("Start s", "Poc. s"),
            ["col_duration"] = ("Duration", "Trajanje"),
            ["col_dur_s"] = ("Dur. s", "Tra. s"),
            ["col_end"] = ("End", "Kraj"),
            ["demucs_not_installed_msg"] = ("Demucs is not installed.\n\n{0}\n\nInstall Python 3.8+ from python.org, then run:\n  pip install demucs", "Demucs nije instaliran.\n\n{0}\n\nInstalirajte Python 3.8+ sa python.org, zatim pokrenite:\n  pip install demucs"),
            ["demucs_not_installed_title"] = ("Demucs is not installed", "Demucs nije instaliran"),
            ["demucs_mode_msg"] = ("YES  — 2 stems: Vocals + Instrumental (faster)\nNO  — 4 stems: Vocals + Drums + Bass + Other", "DA  — 2 stema: Vokal + Instrumental (brze)\nNE  — 4 stema: Vokal + Bubnjevi + Bas + Ostalo"),
            ["demucs_mode_title"] = ("Separation mode", "Mod razdvajanja"),
            ["demucs_started"] = ("Demucs started. Please wait a few minutes...", "Pokrenut Demucs. Sacekajte nekoliko minuta..."),
            ["demucs_progress"] = ("Demucs progress: {0}%", "Demucs napredak: {0}%"),
            ["demucs_dialog_starting"] = ("Starting Demucs, this can take a moment...", "Pokrećem Demucs, ovo može malo potrajati..."),
            ["demucs_dialog_title"] = ("Separating vocals and instrumental — Demucs", "Razdvajanje vokala i instrumentala — Demucs"),
            ["demucs_dialog_progress_indeterminate"] = ("Progress: starting up", "Napredak: pokretanje"),
            ["demucs_dialog_progress_name"] = ("Progress: {0} percent", "Napredak: {0} procenata"),
            ["demucs_dialog_cancel_hint"] = ("Cancel the Demucs separation", "Otkaži Demucs razdvajanje"),
            ["demucs_dialog_cancelling"] = ("Cancelling, please wait...", "Otkazujem, sačekaj trenutak..."),
            ["demucs_dialog_cancelled"] = ("Cancelled.", "Otkazano."),
            ["demucs_error"] = ("Demucs error:\n{0}", "Demucs greska:\n{0}"),
            ["done_title"] = ("Done", "Gotovo"),
            ["status_summary"] = ("{0}  |  Tracks: {1}  |  Files: {2}  |  Total: {3}", "{0}  |  Trake: {1}  |  Fajlova: {2}  |  Ukupno: {3}"),
            ["sel_clip"] = ("Clip: {0}  |  Start: {1:F3}s  |  Dur: {2:F3}s  |  End: {3:F3}s", "Klip: {0}  |  Poc: {1:F3}s  |  Tra: {2:F3}s  |  Kraj: {3:F3}s"),
            ["sel_track"] = ("Track: {0}  |  {1} files  |  Vol: {2:P0}", "Traka: {0}  |  {1} fajlova  |  Vol: {2:P0}"),
            ["select_track_left_list"] = ("Select a track from the left list.", "Odaberite traku sa lijeve liste."),
            ["fx_enter_number"] = ("{0}, enter a number from {1} to {2}", "{0}, unesi broj od {1} do {2}"),
            ["fx_reset"] = ("Reset to default", "Resetuj na default"),
            ["fx_reset_help"] = ("Reset all parameters to default values", "Resetuj sve parametre na podrazumijevane vrijednosti"),
            ["btn_close"] = ("Close", "Zatvori"),
            ["btn_confirm"] = ("Confirm", "Potvrdi"),
            ["acc_confirm_input"] = ("Confirm input", "Potvrdi unos"),
            ["fx_toggle_state"] = ("{0} {1}. Press Space to change.", "{0} {1}. Pritisnite Space da promijenite."),
            ["state_on"] = ("on", "uključen"),
            ["state_off"] = ("off", "isključen"),
            ["fx_param_state"] = ("{0}, currently {1}, from {2} to {3}. Arrow keys to adjust.", "{0}, trenutno {1}, od {2} do {3}. Strelice za podešavanje."),
            ["fx_eq_low"] = ("Bass (200 Hz)", "Bas (200 Hz)"),
            ["fx_eq_mid"] = ("Mid (1 kHz)", "Srednji (1 kHz)"),
            ["fx_eq_high"] = ("Treble (8 kHz)", "Visoki (8 kHz)"),
            ["fx_reverb_mix"] = ("Mix (dry/wet)", "Mix (suho/mokro)"),
            ["fx_room_size"] = ("Room size", "Veličina sobe"),
            ["fx_feedback"] = ("Feedback", "Povratnost"),
            ["fx_threshold"] = ("Threshold", "Prag (Threshold)"),
            ["fx_ratio"] = ("Ratio", "Omjer (Ratio)"),
            ["fx_attack"] = ("Attack (ms)", "Napad (Attack ms)"),
            ["fx_release"] = ("Release (ms)", "Otpust (Release ms)"),
            ["fx_semitones"] = ("Semitones", "Polutonovi"),
            ["fx_depth"] = ("Depth", "Dubina"),
            ["setpos_for"] = ("Set position: {0}", "Postavi poziciju: {0}"),
            ["setpos_invalid_msg"] = ("Enter a valid position.\n\nExamples:\n  15        (15 seconds)\n  15.5      (15 and a half seconds)\n  1:30      (1 minute and 30 seconds)\n  0:15      (15 seconds)", "Unesite valjanu poziciju.\n\nPrimjeri:\n  15        (15 sekundi)\n  15.5      (15 i po sekundi)\n  1:30      (1 minuta i 30 sekundi)\n  0:15      (15 sekundi)"),
            ["setpos_invalid_title"] = ("Invalid input", "Neispravan unos"),
            ["sys_general"] = ("You are an AI assistant in a professional audio editor. Respond in English. Be concise and expert.", "Ti si AI asistent u profesionalnom audio editoru. Odgovaraj na srpskom jeziku. Budi koncizan i stručan."),
            ["sys_describe"] = ("You are an audio describer for people with visual impairment. Respond in English. Be detailed.", "Ti si audio opisivač za osobe sa oštećenjem vida. Odgovaraj na srpskom jeziku. Budi detaljan."),
            // ===== Modeli (default nazivi) =====
            ["default_clip_name"] = ("Clip", "Klip"),
            ["default_project_name"] = ("New project", "Novi projekat"),
            ["default_track_name"] = ("Track", "Traka"),
        };

        public static string T(string key)
        {
            if (!Table.TryGetValue(key, out var v)) return key;
            return Current == "sr" ? v.sr : v.en;
        }

        // Puni Application.Current.Resources tako da XAML {DynamicResource L_kljuc}
        // automatski dobije prevod i osvezi se pri promeni jezika.
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
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, Current);
            }
            catch { }
        }
    }
}
