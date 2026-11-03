using System.Collections.Generic;

namespace UltraAudioEditor.Localization
{
    // Prevodi za uređivanje (sečenje, markeri, kopiranje), snimanje, pametan izvoz,
    // "gde sam" (F6), log prozor i About. Isti format kao glavna tabela u Lang.cs:
    // ključ -> (engleski, srpski). Ključevi odavde imaju PREDNOST nad Lang.cs.
    public static partial class Lang
    {
        private static readonly Dictionary<string, (string en, string sr)> Extra = new Dictionary<string, (string, string)>
        {
            // ===== Naslov / statusna traka / About =====
            ["statusbar_ready"] = ("Ultra Audio Editor v{0} | screen reader ready", "Ultra Audio Editor v{0} | prilagođen čitačima ekrana"),
            ["about_text"] = (
                "Ultra Audio Editor v{0}\n\nAccessible multi-track audio editor for Windows, built keyboard-first and fully usable with JAWS and NVDA, as well as with a mouse.\n\nCreated by Demir Ajvazi\n© 2026 Demir Ajvazi — GPL-3.0\n\nPart of the Ultra Creative Suite.\n\nAI features run locally through Ollama by default (no API key, nothing leaves your computer). Groq and Anthropic are optional.\n\nTechnologies: WPF (.NET 8), NAudio, Demucs, Ollama.\n\nIf Ultra Audio Editor helps you, you can support its development on GitHub Sponsors:\n{1}",
                "Ultra Audio Editor v{0}\n\nPristupačan audio editor sa više traka za Windows, napravljen prvo za tastaturu — potpuno upotrebljiv sa JAWS-om i NVDA-om, ali i mišem.\n\nAutor: Demir Ajvazi\n© 2026 Demir Ajvazi — GPL-3.0\n\nDeo Ultra Creative Suite kolekcije.\n\nAI funkcije podrazumevano rade lokalno preko Ollame (bez API ključa, ništa ne napušta tvoj računar). Groq i Anthropic su opcioni.\n\nTehnologije: WPF (.NET 8), NAudio, Demucs, Ollama.\n\nAko ti Ultra Audio Editor pomaže, razvoj možeš da podržiš na GitHub Sponsors:\n{1}"),
            ["about_btn_sponsors"] = ("Support on GitHub Sponsors", "Podrži na GitHub Sponsors"),
            ["about_btn_repo"] = ("GitHub repository", "GitHub repozitorijum"),

            // ===== Log prozor (Ctrl+Shift+L) =====
            ["log_window_title"] = ("Diagnostic log — Ultra Audio Editor", "Dnevnik dijagnostike — Ultra Audio Editor"),
            ["log_window_path"] = ("Log file: {0}", "Fajl dnevnika: {0}"),
            ["log_window_refresh"] = ("Refresh", "Osveži"),
            ["log_window_copy"] = ("Copy all", "Kopiraj sve"),
            ["log_window_copied"] = ("Copied to clipboard", "Kopirano u clipboard"),
            ["menu_log"] = ("Diagnostic _log (Ctrl+Shift+L)", "_Dnevnik dijagnostike (Ctrl+Shift+L)"),

            // ===== Meni: Uređivanje (novo) =====
            ["menu_split"] = ("_Cut clip at position (Ctrl+X)", "_Preseci klip na poziciji (Ctrl+X)"),
            ["menu_cut_markers"] = ("Cut at _all markers (Ctrl+Shift+X)", "Preseci na _svim markerima (Ctrl+Shift+X)"),
            ["menu_copy_clip"] = ("C_opy clip (Ctrl+C)", "_Kopiraj klip (Ctrl+C)"),
            ["menu_paste_clip"] = ("_Paste clip at position (Ctrl+V)", "_Nalepi klip na poziciju (Ctrl+V)"),
            ["menu_delete_between"] = ("_Delete between markers (Ctrl+Delete)", "_Obriši deo između markera (Ctrl+Delete)"),

            // ===== Meni: Markeri =====
            ["menu_markers"] = ("_Markers", "_Markeri"),
            ["menu_add_marker"] = ("_Add marker at position (M)", "_Dodaj marker na poziciji (M)"),
            ["menu_delete_marker"] = ("Delete _nearest marker", "Obriši _najbliži marker"),
            ["menu_clear_markers"] = ("Delete all _markers", "Obriši sve _markere"),

            // ===== Meni: Reprodukcija =====
            ["menu_playback"] = ("_Playback", "_Reprodukcija"),
            ["menu_play_pause"] = ("_Play / Pause (Space)", "_Pusti / Pauziraj (Space)"),
            ["menu_stop"] = ("_Stop (S)", "_Zaustavi (S)"),
            ["menu_record"] = ("_Record / stop recording (Ctrl+R)", "_Snimaj / zaustavi snimanje (Ctrl+R)"),
            ["menu_preview"] = ("Pre_view around position (P)", "Pre_slušaj oko pozicije (P)"),
            ["menu_to_start"] = ("Go to _beginning (Home)", "Na _početak (Home)"),
            ["menu_to_end"] = ("Go to _end (End)", "Na _kraj (End)"),
            ["menu_loop"] = ("_Loop on/off (L)", "_Loop uključi/isključi (L)"),

            // ===== Meni: Pogled =====
            ["menu_where_am_i"] = ("_Where am I (F6)", "_Gde sam (F6)"),
            ["menu_project_overview"] = ("Project _overview (Shift+F6)", "_Pregled projekta (Shift+F6)"),

            // ===== Toolbar =====
            ["tb_split"] = ("Cut", "Preseci"),
            ["tb_marker"] = ("Marker", "Marker"),
            ["tb_delete_between"] = ("Delete between markers", "Obriši između markera"),

            // ===== Kontekst meniji liste =====
            ["clip_split_menu"] = ("Cut clip here  ({0})   Ctrl+X", "Preseci klip ovde  ({0})   Ctrl+X"),
            ["clip_copy_menu"] = ("Copy clip   Ctrl+C", "Kopiraj klip   Ctrl+C"),
            ["clip_preview_menu"] = ("Preview around position   P", "Preslušaj oko pozicije   P"),
            ["trk_paste_here"] = ("Paste copied clip here at {0}   Ctrl+V", "Nalepi kopirani klip ovde na {0}   Ctrl+V"),
            ["trk_record_here"] = ("Record on this track from {0}   Ctrl+R", "Snimaj na ovu traku od {0}   Ctrl+R"),
            ["marker_col"] = ("Marker", "Marker"),
            ["marker_row_name"] = ("Marker {0} of {1}", "Marker {0} od {1}"),
            ["mk_goto"] = ("Go to marker", "Idi na marker"),
            ["mk_split_here"] = ("Cut track '{0}' at this marker", "Preseci traku '{0}' na ovom markeru"),
            ["mk_delete"] = ("Delete this marker   Delete", "Obriši ovaj marker   Delete"),

            // ===== Poruke: sečenje / kopiranje =====
            ["split_none"] = ("No clip to cut at {0}. Select the track and place the position inside a clip.", "Nema klipa za sečenje na {0}. Izaberi traku i postavi poziciju unutar klipa."),
            ["split_done"] = ("Cut at {0}. Left part {1:F2} s, right part {2:F2} s. The right part is selected.", "Presečeno na {0}. Levi deo {1:F2} s, desni deo {2:F2} s. Desni deo je izabran."),
            ["cut_markers_none"] = ("No clip lies under any marker.", "Nijedan klip nije ispod nekog markera."),
            ["cut_markers_done_track"] = ("{0} cut(s) at markers on track '{1}'.", "Napravljeno {0} rez(ova) na markerima na traci '{1}'."),
            ["cut_markers_done_all"] = ("{0} cut(s) at markers on all tracks.", "Napravljeno {0} rez(ova) na markerima na svim trakama."),
            ["copy_none"] = ("Select a clip first, then press Ctrl+C.", "Prvo izaberi klip, pa pritisni Ctrl+C."),
            ["copy_done"] = ("Copied: {0}, {1:F2} s. Ctrl+V pastes it at the position on the selected track.", "Kopirano: {0}, {1:F2} s. Ctrl+V ga lepi na poziciju na izabranoj traci."),
            ["paste_empty"] = ("Nothing copied yet. Select a clip and press Ctrl+C.", "Ništa nije kopirano. Izaberi klip i pritisni Ctrl+C."),
            ["paste_no_track"] = ("Select the track to paste on.", "Izaberi traku na koju lepiš."),
            ["paste_done"] = ("Pasted {0} on track '{1}', from {2} to {3}. Position moved to the end of the pasted part.", "Nalepljeno {0} na traku '{1}', od {2} do {3}. Pozicija je pomerena na kraj nalepljenog dela."),

            // ===== Poruke: markeri =====
            ["markers_none"] = ("There are no markers. Press M to add one at the position.", "Nema markera. Pritisni M da dodaš marker na poziciji."),
            ["marker_exists"] = ("Marker {0} is already here, at {1}.", "Marker {0} već postoji ovde, na {1}."),
            ["marker_added"] = ("Marker {0} added at {1}. Total markers: {2}.", "Marker {0} dodat na {1}. Ukupno markera: {2}."),
            ["marker_at"] = ("Marker {0} of {1}, {2}.", "Marker {0} od {1}, {2}."),
            ["marker_no_next"] = ("No marker after the position.", "Nema markera posle pozicije."),
            ["marker_no_prev"] = ("No marker before the position.", "Nema markera pre pozicije."),
            ["marker_deleted"] = ("Marker {0} at {1} deleted. Markers left: {2}.", "Obrisan marker {0} na {1}. Preostalo markera: {2}."),
            ["markers_cleared"] = ("All {0} markers deleted.", "Obrisano svih {0} markera."),
            ["ripple_need_markers"] = ("Place the position between two markers. Everything between them will be deleted on all tracks.", "Postavi poziciju između dva markera. Sve između njih biće obrisano na svim trakama."),
            ["ripple_done"] = ("Deleted {0:F2} s between {1} and {2} on all tracks. Everything after it moved back, no gap left.", "Obrisano {0:F2} s između {1} i {2} na svim trakama. Sve posle toga je pomereno unazad, bez rupe."),

            // ===== Poruke: snimanje =====
            ["rec_no_device"] = ("No microphone found. Connect one and choose it in Windows sound settings.", "Mikrofon nije pronađen. Priključi ga i izaberi u Windows podešavanjima zvuka."),
            ["rec_started"] = ("Recording.", "Snimanje."),
            ["rec_error"] = ("Recording error: {0}", "Greška pri snimanju: {0}"),
            ["rec_too_short"] = ("Recording too short, discarded.", "Snimak je prekratak, odbačen."),
            ["rec_track_name"] = ("Recording {0}", "Snimak {0}"),
            ["rec_done"] = ("Recorded {0:F1} s on track '{1}', starting at {2}.", "Snimljeno {0:F1} s na traku '{1}', od {2}."),
            ["rec_warn_silent"] = ("Warning: the recording is almost silent. Check that the right microphone is selected and not muted.", "Upozorenje: snimak je skoro nečujan. Proveri da li je izabran pravi mikrofon i da nije isključen."),
            ["rec_warn_loud"] = ("Warning: the recording is too loud in places (distorted). Lower the microphone level and try again.", "Upozorenje: snimak je mestimično preglasan (izobličen). Smanji jačinu mikrofona i probaj ponovo."),
            ["record_hint"] = ("Ctrl+R starts recording from the position. Ctrl+R, Space or S stops it.", "Ctrl+R počinje snimanje od pozicije. Ctrl+R, Space ili S ga zaustavlja."),

            // ===== Poruke: izvoz =====
            ["export_nothing"] = ("There is nothing to export. Import or record audio first.", "Nema šta da se izveze. Prvo uvezi ili snimi zvuk."),
            ["export_running"] = ("Exporting...", "Izvoz u toku..."),
            ["export_no_audible"] = ("Nothing can be heard in the mix: all tracks with audio are muted, outside solo, or their files are missing.", "U miksu se ništa ne čuje: sve trake sa zvukom su utišane, van solo-a, ili im fajlovi nedostaju."),
            ["export_too_loud_title"] = ("The mix is too loud", "Miks je preglasan"),
            ["export_too_loud_msg"] = ("The loudest peak of the mix is {0:F1} dB above the maximum, so the exported audio would be distorted.\n\nYES — lower the volume of the whole mix just enough (recommended)\nNO — export as it is\nCANCEL — stop the export", "Najglasniji vrh miksa je {0:F1} dB iznad maksimuma, pa bi izvezeni zvuk bio izobličen.\n\nDA — smanji glasnoću celog miksa taman koliko treba (preporučeno)\nNE — izvezi ovako kako jeste\nOTKAŽI — prekini izvoz"),
            ["export_cancelled"] = ("Export cancelled.", "Izvoz otkazan."),
            ["export_failed"] = ("Export failed.", "Izvoz nije uspeo."),
            ["export_done_announce"] = ("Export finished: {0}", "Izvoz završen: {0}"),
            ["export_sum_file"] = ("File: {0}", "Fajl: {0}"),
            ["export_sum_length"] = ("Length: {0}, size {1:F1} MB", "Trajanje: {0}, veličina {1:F1} MB"),
            ["export_sum_tracks"] = ("Mixed: {0} track(s), {1} clip(s)", "U miksu: {0} trak(a), {1} klip(ova)"),
            ["export_sum_skipped"] = ("Skipped: {0} muted track(s) or outside solo", "Preskočeno: {0} utišanih trak(a) ili van solo-a"),
            ["export_sum_missing"] = ("Warning: {0} clip(s) skipped because the file is missing", "Upozorenje: {0} klip(ova) preskočeno jer fajl nedostaje"),
            ["export_sum_peak"] = ("Loudest peak: {0:F1} dB", "Najglasniji vrh: {0:F1} dB"),
            ["export_sum_lowered"] = ("Volume lowered by {0:F1} dB to avoid distortion", "Glasnoća smanjena za {0:F1} dB da ne bude izobličenja"),
            ["export_sum_clipped"] = ("Warning: exported without lowering, loud parts are distorted", "Upozorenje: izvezeno bez smanjenja, glasni delovi su izobličeni"),
            ["export_sum_quiet"] = ("Note: the mix is quite quiet. Normalize or raise track volume if needed.", "Napomena: miks je prilično tih. Po potrebi normalizuj ili pojačaj trake."),
            ["export_format_help"] = ("WAV — lossless. MP3 — smaller, plays everywhere. M4A — AAC, small and good quality. AIFF — lossless, for Mac.", "WAV — bez gubitka kvaliteta. MP3 — manji, svuda radi. M4A — AAC, mali i kvalitetan. AIFF — bez gubitka, za Mac."),

            // ===== F6 "gde sam" =====
            ["qs_recording"] = ("Recording in progress, {0:F1} s.", "Snimanje u toku, {0:F1} s."),
            ["qs_time"] = ("{0} of {1}, {2} to the end.", "{0} od {1}, do kraja {2}."),
            ["qs_track"] = ("Track {0}{1}{2}.", "Traka {0}{1}{2}."),
            ["qs_muted"] = (", muted", ", utišana"),
            ["qs_solo"] = (", solo", ", solo"),
            ["qs_under"] = ("Under the position: {0}, from {1} to {2}.", "Ispod pozicije: {0}, od {1} do {2}."),
            ["qs_under_none"] = ("No clip under the position.", "Ispod pozicije nema klipa."),
            ["qs_no_track"] = ("No track selected. Tracks playing here: {0}.", "Nijedna traka nije izabrana. Ovde svira traka: {0}."),
            ["qs_on_marker"] = ("On marker {0} of {1}.", "Na markeru {0} od {1}."),
            ["qs_marker_ahead"] = ("Nearest marker {0} of {1}, {2:F1} s ahead.", "Najbliži marker {0} od {1}, {2:F1} s napred."),
            ["qs_marker_behind"] = ("Nearest marker {0} of {1}, {2:F1} s behind.", "Najbliži marker {0} od {1}, {2:F1} s iza."),
            ["st_markers"] = ("MARKERS: {0}", "MARKERI: {0}"),
            ["st_marker_line"] = ("  Marker {0}: {1}", "  Marker {0}: {1}"),
            ["st_no_markers"] = ("No markers.", "Nema markera."),

            ["clip_select_first"] = ("No clip is selected. Select a clip in the project list first (arrow keys), then try again.", "Nijedan klip nije izabran. Prvo izaberi klip u listi projekta (strelicama), pa probaj ponovo."),

            // ===== Meni Efekti =====
            ["menu_effects"] = ("Effect_s", "_Efekti"),
            ["fxm_eq"] = ("_Equalizer (EQ)...", "_Ekvilajzer (EQ)..."),
            ["fxm_reverb"] = ("_Reverb...", "_Reverb..."),
            ["fxm_delay"] = ("_Delay / Echo...", "_Delay / Eho..."),
            ["fxm_comp"] = ("_Compressor...", "_Kompresor..."),
            ["fxm_gate"] = ("_Noise Gate...", "_Noise Gate..."),
            ["fxm_bass"] = ("_Bass Boost...", "_Bass Boost..."),
            ["fxm_pitch"] = ("_Pitch Shift...", "_Pitch Shift (visina tona)..."),
            ["fxm_chorus"] = ("C_horus...", "C_horus..."),
            ["menu_fx_all_off"] = ("Turn _off all effects on the track", "_Isključi sve efekte na traci"),
            ["fx_select_track"] = ("Select a track first, effects apply to the selected track.", "Prvo izaberi traku, efekti se primenjuju na izabranu traku."),
            ["fx_all_off"] = ("All effects turned off on track '{0}'.", "Svi efekti isključeni na traci '{0}'."),
            ["fx_panel_hint"] = ("Effects for the selected track. A check mark in the Effects menu means the effect is on. Changes are heard live.", "Efekti za izabranu traku. Kvačica u meniju Efekti znači da je efekat uključen. Promene se čuju uživo."),

            // ===== Preseti efekata =====
            ["fx_preset_label"] = ("Preset", "Preset"),
            ["fx_preset_choose"] = ("— choose a preset —", "— izaberi preset —"),
            ["fx_preset_help"] = ("Up and down arrows try presets one by one, each is applied and heard immediately. The sliders below fine-tune it.", "Strelice gore i dole isprobavaju presete jedan po jedan, svaki se odmah primenjuje i čuje. Klizači ispod ga fino podešavaju."),
            ["fx_damping"] = ("Damping (dark walls)", "Prigušenje (tamni zidovi)"),
            ["fx_predelay"] = ("Pre-delay", "Pre-delay (do prvog odjeka)"),
            ["fx_mix"] = ("Mix", "Mix (jačina efekta)"),
            ["fx_makeup"] = ("Make-up gain", "Pojačanje posle kompresije"),
            ["fx_rate"] = ("Rate", "Brzina"),
            // Reverb
            ["pr_rev_small_room"] = ("Small room", "Mala soba"),
            ["pr_rev_bathroom"] = ("Bathroom (tiles)", "Kupatilo (pločice)"),
            ["pr_rev_room"] = ("Room", "Soba"),
            ["pr_rev_studio"] = ("Vocal studio", "Vokalni studio"),
            ["pr_rev_plate"] = ("Plate (bright vocal)", "Plate (svetao vokal)"),
            ["pr_rev_church"] = ("Small church", "Mala crkva"),
            ["pr_rev_hall"] = ("Concert hall", "Koncertna dvorana"),
            ["pr_rev_large_hall"] = ("Large hall", "Velika sala"),
            ["pr_rev_cathedral"] = ("Cathedral (huge space)", "Katedrala (ogroman prostor)"),
            ["pr_rev_arena"] = ("Arena / stadium", "Arena / stadion"),
            ["pr_rev_cave"] = ("Cave", "Pećina"),
            // Delay
            ["pr_dl_doubling"] = ("Doubling (thicker voice)", "Dupliranje (puniji glas)"),
            ["pr_dl_slapback"] = ("Slapback (rock'n'roll)", "Slapback (rokenrol)"),
            ["pr_dl_short"] = ("Short echo", "Kratak eho"),
            ["pr_dl_rhythm"] = ("Rhythmic echo", "Ritmični eho"),
            ["pr_dl_canyon"] = ("Canyon echo", "Eho u kanjonu"),
            ["pr_dl_long"] = ("Long echo", "Dugi eho"),
            ["pr_dl_space"] = ("Endless space echo", "Beskrajni svemirski eho"),
            // Compressor
            ["pr_cp_gentle_vocal"] = ("Gentle vocal", "Blag vokal"),
            ["pr_cp_vocal_front"] = ("Vocal up front", "Vokal napred (jak)"),
            ["pr_cp_podcast"] = ("Speech / podcast", "Govor / podkast"),
            ["pr_cp_drums"] = ("Drum punch", "Udarni bubnjevi"),
            ["pr_cp_bass"] = ("Even bass", "Ujednačen bas"),
            ["pr_cp_glue"] = ("Mix glue (master)", "Lepak za miks (master)"),
            ["pr_cp_limiter"] = ("Limiter (no peaks)", "Limiter (bez vrhova)"),
            // EQ
            ["pr_eq_flat"] = ("Flat (no change)", "Ravno (bez promene)"),
            ["pr_eq_warm"] = ("Warm", "Toplo"),
            ["pr_eq_bright"] = ("Bright", "Svetlo"),
            ["pr_eq_presence"] = ("Vocal presence", "Prisutnost glasa"),
            ["pr_eq_loudness"] = ("Loudness (smile)", "Loudness (osmeh)"),
            ["pr_eq_bass"] = ("Strong bass", "Jak bas"),
            ["pr_eq_lowcut"] = ("Low cut (remove rumble)", "Seci bas (bez brujanja)"),
            ["pr_eq_radio"] = ("Old radio", "Stari radio"),
            ["pr_eq_phone"] = ("Telephone", "Telefon"),
            // Noise gate
            ["pr_ng_gentle"] = ("Gentle", "Blag"),
            ["pr_ng_voice"] = ("Studio speech", "Studijski govor"),
            ["pr_ng_medium"] = ("Medium", "Srednji"),
            ["pr_ng_strong"] = ("Strong (noisy room)", "Jak (bučna prostorija)"),
            // Bass boost
            ["pr_bb_light"] = ("Light", "Blag"),
            ["pr_bb_medium"] = ("Medium", "Srednji"),
            ["pr_bb_strong"] = ("Strong", "Jak"),
            ["pr_bb_extreme"] = ("Extreme", "Ekstremni"),
            // Pitch
            ["pr_ps_octave_down"] = ("One octave down", "Oktava niže"),
            ["pr_ps_deep"] = ("Deep voice", "Dubok glas"),
            ["pr_ps_lower"] = ("A little lower", "Malo niže"),
            ["pr_ps_higher"] = ("A little higher", "Malo više"),
            ["pr_ps_child"] = ("Child voice", "Dečji glas"),
            ["pr_ps_chipmunk"] = ("Chipmunk", "Veverica"),
            ["pr_ps_octave_up"] = ("One octave up", "Oktava više"),
            // Chorus
            ["pr_ch_subtle"] = ("Subtle", "Diskretan"),
            ["pr_ch_classic"] = ("Classic chorus", "Klasičan chorus"),
            ["pr_ch_rich"] = ("Rich (choir)", "Bogat (hor)"),
            ["pr_ch_fast"] = ("Fast shimmer", "Brzo treperenje"),
            ["pr_ch_vibrato"] = ("Vibrato", "Vibrato"),

            // ===== Dijalog efekata =====
            ["fx_play_btn"] = ("Play / Pause (Ctrl+Space)", "Pusti / pauziraj (Ctrl+Space)"),
            ["fx_close_hint"] = ("Close. Changes are already applied and heard live.", "Zatvori. Promene su već primenjene i čuju se uživo."),

            // ===== Ostalo =====
            ["to_end"] = ("At the end, {0}.", "Na kraju, {0}."),
            ["all_unmuted"] = ("All tracks unmuted.", "Sve trake aktivirane."),
            ["zoom_reset"] = ("Zoom reset.", "Zoom resetovan."),
            ["processing"] = ("Processing...", "Obrada u toku..."),
            ["operation_failed"] = ("The operation failed. Details are in the diagnostic log (Ctrl+Shift+L).", "Operacija nije uspela. Detalji su u dnevniku dijagnostike (Ctrl+Shift+L)."),
            ["project_saved"] = ("Project saved: {0}", "Projekat sačuvan: {0}"),
            ["acc_list_help"] = (
                "Project list: tracks, clips and markers. Arrows to move between rows. F2 sets clip position, Ctrl+arrows move a clip, Delete removes it. Ctrl+X cuts at the position, M adds a marker, F6 tells where you are. Shift+F10 or the Menu key opens the menu for the row.",
                "Lista projekta: trake, klipovi i markeri. Strelice za kretanje po redovima. F2 postavlja poziciju klipa, Ctrl+strelice pomeraju klip, Delete ga briše. Ctrl+X seče na poziciji, M dodaje marker, F6 kaže gde si. Shift+F10 ili taster Meni otvara meni za taj red."),

            ["shortcuts_text"] = (
                "KEYBOARD SHORTCUTS — Ultra Audio Editor\n" +
                "Same shortcuts as Ultra Video Editor wherever both apps do the same thing.\n\n" +
                "MOVING THROUGH TIME (works anywhere in the program):\n  Alt+Left / Alt+Right        — Back / forward 1 second\n  Alt+Shift+Left / Alt+Shift+Right — Back / forward 0.1 second\n  Alt+Home / Alt+End          — Beginning / end\n  Ctrl+G                      — Go to a time you type\n  (Each move plays a short piece so you hear where you are.)\n\n" +
                "PLAYBACK AND RECORDING:\n  Space          — Play / Pause\n  S              — Stop\n  Ctrl+R         — Record from the position / stop recording\n  P              — Preview: 2 s before and after the position\n  L              — Loop on/off\n  Home / End     — Beginning / end\n\n" +
                "WHERE AM I:\n  F6             — Time, time left, track, clip under the position, nearest marker\n  Shift+F6       — Full project overview window\n\n" +
                "EDITING:\n  Ctrl+X         — Cut the clip at the position (selected track)\n  Ctrl+Shift+X   — Cut at all markers\n  Ctrl+C         — Copy the selected clip\n  Ctrl+V         — Paste at the position on the selected track\n  Ctrl+Delete    — Delete everything between the two markers around the position (all tracks, no gap left)\n  Ctrl+Z / Ctrl+Y — Undo / Redo\n\n" +
                "SELECTION (Sound Forge style):\n  I / O          — Selection start / end at the position\n  Ctrl+A         — Select all\n  Shift+Space    — Play the selection (with L it loops)\n  Escape         — Clear the selection\n  Ctrl+C         — Copy the selection   Ctrl+Delete — Delete it and close the gap\n  Process menu   — gain, normalize, LUFS, fades, noise, clicks, hum, filters...\n  F7             — Statistics: loudness (LUFS), peak, distortion\n\n" +
                "MARKERS:\n  M              — Add marker at the position\n  Alt+Page Down  — Next marker\n  Alt+Page Up    — Previous marker\n  (Markers are also listed at the end of the project list.)\n\n" +
                "CLIPS:\n  F2                — Set position of the selected clip\n  Ctrl+Left/Right   — Move clip 1 s\n  Ctrl+Shift+Left/Right — Move clip 0.1 s\n  Delete (in list) / Shift+Delete — Delete clip\n  Shift+F10         — Menu for the selected row\n\n" +
                "FILE:\n  Ctrl+N  New project    Ctrl+O  Open    Ctrl+S  Save    Ctrl+Shift+S  Save as\n  Ctrl+I  Import audio   Ctrl+E  Export\n\n" +
                "TRACKS:\n  Ctrl+Alt+T  New track    Ctrl+D  Duplicate    Alt+Up/Down  Move track\n\n" +
                "OTHER:\n  Alt+W          — Switch screen reader / visual mode\n  Ctrl+Shift+L   — Diagnostic log\n  Ctrl++ / Ctrl+- / Ctrl+0 — Zoom",
                "TASTATURNE PREČICE — Ultra Audio Editor\n" +
                "Iste prečice kao u Ultra Video Editoru svuda gde oba programa rade istu stvar.\n\n" +
                "KRETANJE KROZ VREME (radi bilo gde u programu):\n  Alt+Levo / Alt+Desno        — Nazad / napred 1 sekundu\n  Alt+Shift+Levo / Alt+Shift+Desno — Nazad / napred 0,1 sekundu\n  Alt+Home / Alt+End          — Početak / kraj\n  Ctrl+G                      — Idi na vreme koje ukucaš\n  (Svaki korak pusti kratak delić, pa čuješ gde si.)\n\n" +
                "REPRODUKCIJA I SNIMANJE:\n  Space          — Pusti / Pauziraj\n  S              — Zaustavi\n  Ctrl+R         — Snimaj od pozicije / zaustavi snimanje\n  P              — Preslušaj: 2 s pre i posle pozicije\n  L              — Loop uključi/isključi\n  Home / End     — Početak / kraj\n\n" +
                "GDE SAM:\n  F6             — Vreme, koliko do kraja, traka, klip ispod pozicije, najbliži marker\n  Shift+F6       — Prozor sa kompletnim pregledom projekta\n\n" +
                "UREĐIVANJE:\n  Ctrl+X         — Preseci klip na poziciji (izabrana traka)\n  Ctrl+Shift+X   — Preseci na svim markerima\n  Ctrl+C         — Kopiraj izabrani klip\n  Ctrl+V         — Nalepi na poziciju na izabranoj traci\n  Ctrl+Delete    — Obriši sve između dva markera oko pozicije (sve trake, bez rupe)\n  Ctrl+Z / Ctrl+Y — Poništi / Ponovi\n\n" +
                "IZBOR (Sound Forge stil):\n  I / O          — Početak / kraj izbora na poziciji\n  Ctrl+A         — Izaberi sve\n  Shift+Space    — Pusti izbor (sa L se ponavlja)\n  Escape         — Poništi izbor\n  Ctrl+C         — Kopiraj izbor   Ctrl+Delete — Obriši ga i zatvori rupu\n  Meni Obrada    — pojačanje, normalizacija, LUFS, fade, šum, klikovi, brujanje, filteri...\n  F7             — Statistika: glasnoća (LUFS), vrh, izobličenja\n\n" +
                "MARKERI:\n  M              — Dodaj marker na poziciji\n  Alt+Page Down  — Sledeći marker\n  Alt+Page Up    — Prethodni marker\n  (Markeri su navedeni i na kraju liste projekta.)\n\n" +
                "KLIPOVI:\n  F2                — Postavi poziciju izabranog klipa\n  Ctrl+Levo/Desno   — Pomeri klip 1 s\n  Ctrl+Shift+Levo/Desno — Pomeri klip 0.1 s\n  Delete (u listi) / Shift+Delete — Obriši klip\n  Shift+F10         — Meni za izabrani red\n\n" +
                "FAJL:\n  Ctrl+N  Novi projekat   Ctrl+O  Otvori   Ctrl+S  Sačuvaj   Ctrl+Shift+S  Sačuvaj kao\n  Ctrl+I  Uvezi audio     Ctrl+E  Izvezi\n\n" +
                "TRAKE:\n  Ctrl+Alt+T  Nova traka   Ctrl+D  Dupliraj   Alt+Gore/Dole  Pomeri traku\n\n" +
                "OSTALO:\n  Alt+W          — Prebaci mod za čitač ekrana / vizuelni mod\n  Ctrl+Shift+L   — Dnevnik dijagnostike\n  Ctrl++ / Ctrl+- / Ctrl+0 — Zoom"),
        };
    }
}
