# Ultra Video Editor

> An accessible video editor for Windows, with a built-in AI music video creator. Fully usable with JAWS and NVDA, and equally usable for sighted and low-vision users.

Part of [Ultra Creative Suite](../README.md). Built by a blind developer and tested daily with JAWS for Windows.

---

## What is this?

Ultra Video Editor is a WPF desktop application (.NET 8) with two sides:

- **A timeline video editor.** You can add video, images and audio, cut, arrange, add effects, transitions, keyframe animations and subtitles, and export, all from the keyboard with full screen reader feedback.
- **An AI music video creator.** It takes an audio file and song lyrics and produces a finished music video. A local LLM (Ollama/Qwen) analyzes the lyrics, matching stock footage is downloaded from Pixabay, cuts are synced to the music, and FFmpeg renders the result with color grading, transitions and ambient sound.

---

## Download and Install

Download the latest installer (`UltraVideoEditorSetup-vX.Y.Z.exe`) from the [Releases page](https://github.com/demirajvazi10-max/Ultra-Creative-suite/releases) and run it. Windows will ask for administrator permission; this is expected.

The installer:

- Lets you choose the language at the start (English, German or Serbian). This also becomes the app's default language.
- Automatically downloads and sets up **FFmpeg** (video processing), **VLC** (preview) and **Ollama** with AI models chosen for your hardware (GPU and RAM are detected automatically).
- Optionally installs AI transcription and subtitles (**Faster-Whisper-XXL**, about 1.3 GB). This is left as a checkbox because of its size.
- Starts the app when it finishes.

The ambient sound/SFX library is **not** included in the installer. If you want it, unpack it into the `Assets\` folder next to the `.exe`.

The app checks for new versions itself (**Help → Updates**) and can download and start the new installer for you.

---

## Features

### Editing
- Multiple video and audio tracks, with filtering by track type
- Cut, copy, paste, move clips left/right/up/down, set clip duration and volume
- Markers, including "cut at all markers"
- Video and audio effects, transitions between clips, keyframe animations (zoom, rotation, position)
- Text overlays and synchronized subtitles
- Project templates, batch export, and export for Shorts/TikTok/Instagram (9:16 and square)
- GPU-accelerated encoding (NVIDIA NVENC) with automatic fallback to `libx264`

### Accessibility
- Three display modes: **Sighted**, **Accessibility (screen reader)** and **Low Vision**
- Dark, light and high-contrast themes, plus adjustable text and control size
- Every action is available from the keyboard; menus show their shortcuts
- Status changes, progress and errors are announced to JAWS/NVDA through live regions, so you never need to open the log window to know what happened
- AI-generated audio descriptions of clips and an exportable accessibility report

### Import from YouTube (Timeline → Import from YouTube)
- Video (MP4, choice of quality) or audio (MP3, choice of bitrate)
- **Playlist links are never downloaded in full by accident.** When a link is part of a playlist you choose:
  - **Just this video**: the default when the link points to a specific video. Only that video is downloaded.
  - **Entire playlist**: private or deleted videos are skipped, and the rest is still downloaded.
  - **Choose specific tracks**: use the arrow keys to move and Space to select. Each selection is announced ("3 of 15 tracks selected"), and **Select all** / **Clear selection** buttons are available.
- Downloaded files can be added straight to the timeline
- Progress is announced at 25/50/75% and per track ("Track 2 of 5"); **Cancel** really stops the download
- Uses [yt-dlp](https://github.com/yt-dlp/yt-dlp). If it is missing, the app offers to download it, and keeps its own copy up to date weekly. If YouTube requires the Deno JavaScript helper, the app offers to download it and retries.

### AI tools
- AI music video creator (Video Engine, AUTO), with word-level lyric sync, karaoke subtitles and an accessible scene review
- AI video review, clip descriptions (vision AI), smart scene detection, AI highlight engine
- Voice generation, audio transcription, lyric sync
- Timeline AI assistant and color grading engine

### Languages
- English, German and Serbian. Change the language anytime from **View → Language**.
- Since 1.4.6 every dialog follows the chosen language: the texts written in the window layouts (labels, buttons, tooltips, window titles and the names screen readers announce) are translated when a window opens and again when you switch the language. The editor's status and log messages are translated too. Choices inside drop-down lists stay in English for now, because the program reads them as settings. The example commands in the Timeline AI Assistant also stay in English, because that is the language the assistant understands.

---

## What's New in 1.4.6 (audio description that sounds human)

- **Gemini watches the whole video:** with a free Gemini key, Gemini watches picture and sound together and writes the audio description like a human describer — natural Serbian, English or German, characters kept consistent, on-screen text read out, descriptions placed in the pauses (or over the singing in a song, so the picture is never left undescribed for long). Films and series are described in 10-minute parts. See *Audio description for viewers* below.
- **Natural voices:** Microsoft neural voices (Serbian: Sofija and Nikola) free and without a key, and Google Cloud Chirp 3 HD voices with your own key.
- **Better local model:** Qwen3-VL replaces Qwen2.5-VL for describing pictures without the internet.
- **Every dialog follows the interface language**, and all editor messages are translated (Serbian in ekavian).
- **Audio description window:** opens with Ctrl+Alt+A, takes any video (also a clip from the timeline), speaks its own progress, and works with an empty timeline.
- **Fixes:** descriptions are never cut in the middle; no more long silent stretches; descriptions in the chosen language (they came back in English after switching); times past one hour are kept in scripts (films).

## What's New in 1.4 (released as 1.4.5: AI music video creator, editing without sight, audio description)

**Lyrics follow the singing**
- Your own lyrics (typed or pasted) are now aligned to the song word by word. Whisper listens to the song, and every word you wrote is matched to a word it heard. Misheard words, extra words and repeated choruses are handled. Before, typed lyrics had no timing at all and scenes were spread evenly over the song.
- If you edit or re-split the lyrics after transcription, the timing is redone automatically (from cached words, no new Whisper run).
- Works with Whisper's Cyrillic output too; Serbian Latin and Cyrillic are compared after normalization.
- Transcription with faster-whisper-xxl failed with "Whisper did not generate SRT file": the command used `--vad_parameters`, an option faster-whisper-xxl does not have (it comes from whisperX), so the program printed its usage and stopped. It now uses the real options `--vad_threshold` and `--vad_min_silence_duration_ms`. The compute type is `auto` instead of a fixed `float16`, so it also works on computers without an NVIDIA card.
- If a Whisper build ever rejects an output option, transcription retries automatically with `srt json`, and finally with plain `srt` (lines only, without word timing).
- **Vocal separation before transcription:** faster-whisper-xxl first separates the voice from the music (its built-in MDX "Kim_Vocal_2" filter, `--ff_vocal_extract mdx_kim2`; the model is downloaded once on first use). Whisper then no longer hears instruments as words or misses quiet lines. If separation fails (for example no internet for the first download), the full mix is transcribed instead, automatically.
- **Choruses typed once get every repeat:** *Sync lyrics* now aligns the lyrics as they are sung. Each piece of what Whisper heard is matched to the typed line that was sung there, any number of times and in any order, so a chorus typed once gets a subtitle every time it is sung. Heard words that match no line (instrumental "hallucinations" such as "Mala pauza...") are skipped.
- **Transcribe uses your lyrics when you have them:** if the song's lyrics are in the lyrics box, *Transcribe* uses them instead of Whisper's text (Whisper only provides the timing). For example "moja đele, da ti hotoneš u san" becomes "moj Anđele, da tiho toneš u san".

**Karaoke subtitles** (new option: *Karaoke: each word lights up while it is sung*)
- When *Show lyrics on screen* is on, each word fills from white to gold exactly while it is sung (ASS karaoke via libass). If the FFmpeg build has no libass, plain subtitles are rendered automatically instead.

**Scene review** (new option, on by default; skipped when auto-render is on)
- After all clips are downloaded and before the video is built, a list of all scenes opens. For every scene you hear the lyric, **what the clip shows** (described by the vision AI), its score and the search words.
- Any scene can get a new clip: change the search words if you like and press Enter. The new clip and its description are announced; the old clip is kept if nothing better is found. *Open clip* plays it in the default player for sighted users.

**Director pass**
- The AI now plans the whole song at once: it fixes one visual identity (main character, setting, palette) and plans every line against it. Repeated lines (chorus) get the same search, so the video has a recurring visual motif.
- The scene loop uses these plans directly instead of asking Ollama again for every line, which is also faster. The literal keyword map and the director's own fallback are tried before the generic fallback.

**Better vision analysis**
- Qwen2.5-VL now sees a strip of 3 frames spread over the part of the clip that is actually used, at 448 px width with the correct aspect ratio. Before it saw one frame from second 1, squeezed to 224×224.
- Qwen also writes a one-sentence description of each clip (used in the scene review).

**Ambient sounds, fixed**
- Neighbouring scenes with the same sound are now one continuous sound, started at a random point in the recording. Before, the sound restarted from second 0 with a fade on every cut (every 2-4 seconds), which was heard as stuttering.
- Ambient sounds were sometimes attached to the wrong scene (scene numbers were shifted by the intro/instrumental clips). Fixed.
- Ambient sounds came in too early by the length of the silence at the start of the song. Fixed.
- With ambient sounds on, the last seconds of the song were cut off. Fixed; the mix keeps the full song.
- The ambient bed now ducks under the vocals, and the mix is lossless WAV (the song is no longer encoded twice). AAC audio in the final video is 256 kbps, 48 kHz.
- Lyric words are matched at word starts after removing diacritics, so "šuma", "kiša" and "čizme" are recognised, and "gledaj" no longer triggers a blizzard, "opasno" a dog or "nagrada" city traffic.
- If mixing fails, it is announced instead of silently falling back.

**Bug fixes**
- The AI's keywords were thrown away almost always (a check meant for Serbian letters š, đ, č, ć, ž tested for s, d, j, c, z).
- The per-line season ("Kad je zima...") was reset to the song's season just before the search, so winter lines could get spring clips.
- Whisper word timestamps were parsed with a regex that broke on Serbian letters in openai-whisper JSON; now parsed as real JSON.
- Video creation stopped with "An item with the same key has already been added" at the first line that needed a literal search query (for example a winter line naming clothes: boots, gloves, hat, scarf). The Serbian→English word list contained "prolece" twice. Fixed, and an unexpected error while finding media for one scene no longer ends the whole video: it is logged and that scene falls back to a safe query.
- **Long lines get several clips:** a scene now lasts until the next line starts. Before, every scene was cut to one shot (6 s in a lullaby), and in a slow song the render then looped that one clip for up to 40 seconds. Long scenes become a montage of up to 8 different clips; a long scene planned as one photo uses video clips instead.
- Clips downloaded while the project had never been saved got a path without a folder ("AI_1234.mp4"), so the render could not find them. They now go to the creator's temporary folder (copied into the project on save), and any folder-less path is resolved before rendering and saving.
- The literal query builder turned winter into summer ("zima" → "summer", "sneg" → "sunny") and removed snow/winter/cold from every query, an old "seasonal lock". Winter lines now get winter clips.

### Editor fixes in 1.4 (whole-program review)

**Timeline**
- Tracks now run in parallel: Video 1, Video 2, Audio 1 and Audio 2 each start at 0. Before, every refresh of the timeline list put all clips of all tracks one after another, so the AI creator's timing, pinned positions and overlays were destroyed as soon as the list was shown.
- Pinned clips (fixed position) stay exactly where they are, and the other clips on the same track flow around them. The editor, the renderer and Batch Export use the same layout code (`TimelineLayout`).
- Opening a project keeps the saved positions. *Save* writes to the current project file; *Save as* asks for a new one. *New project* really starts from an empty state.
- *Move up/down* swaps clips on the same track only. *Set duration* takes its undo snapshot only when you press OK.
- Undo now works for scene detection, Timeline AI, colour grading, templates and *Set image position*; cancelling these dialogs leaves no empty undo step.
- The Timeline AI assistant works only on the picture and video clips of Video 1. Before, commands such as "keep only clips with faces" also applied to the song, ambient sounds and overlays, and could remove the song. It now works on copies, so Cancel leaves the timeline untouched.
- *Set image position*: a typed *End* time is used (it was ignored), *Evenly distribute* works only on the selected clip's track, and Cancel really undoes the distribution.

**Projects**
- The program asks to save unsaved changes before closing, opening another project or starting a new one.
- Saving is crash-safe: the project is written to a temporary file and swapped in, and the previous version is kept as `.bak`.
- Clips that the AI tools downloaded into the Windows TEMP folder are copied into `<project>_media` next to the project when it is saved (Windows cleans TEMP on its own, so older AI projects could lose their clips).
- Closing the main window always closes the program (the hidden log window could keep it running in the background).

**Cut and markers**
- *Cut at playhead* did nothing in most cases: the player position is inside the current clip, but the code treated it as a timeline position. Fixed.
- A cut part now continues from where the cut was made (new `SourceOffset` = in-point) instead of replaying the clip from its start. *Cut at all markers* only cuts the tracks you are editing.
- Markers, "go to marker" and keyframes use timeline time consistently. Playing a cut part starts and stops at its in-point and out-point.

**Rendering**
- The video follows the timeline exactly: each picture lasts until the next one starts, gaps are kept, clips start at their in-point, and crossfades no longer pull the pictures ahead of the music (before, the picture drifted ahead of the song a little on every cut).
- The main audio is placed where it is on the timeline (with its in-point), and extra audio clips play at their real volume (a 15 % ambience was played at 50 %).
- Transitions chosen per clip are stored on the clip and used by the renderer, including transitions picked by the AI. Invalid FFmpeg transition names ("zoom") are mapped to valid ones.
- The quality setting is honoured (CRF 18 / 23 / 28).
- Numbers are always written with a decimal point. On Windows with Serbian regional settings (decimal comma) many FFmpeg commands failed.

**Export**
- *Export audio only* and Batch Export MP3 work (they used to crash).
- WebM (VP9 + Opus) and AVI (MPEG-4 + MP3) exports work; the video is rendered to MP4 first and then converted.
- The resolution question is always asked, and Cancel stops the export. If the project was never saved, *Save as* is offered first.
- Square (1:1) export crops to the centre instead of failing.
- Highlight *Phase 3* exports the highlight itself. It used to export the whole source video with a highlight-length music mix. The segments are now cut from their sources first (keeping their own sound), then mixed and exported. The output folder is chosen with a real folder picker, and the accessibility report follows the app language.

**AI tools**
- Cloudflare: the account ID is no longer hard-coded; it is asked for once and saved with the API token.
- Transcription adds subtitles at the real line times (shifted by the clip's start) and stops cleanly on errors. *Sync lyrics* uses the language you selected and the large-v3 model.
- Text-to-speech splits long texts into chunks with unique file names on Audio 2.
- Highlights and scene detection add clips that start at the right place in the source video (before, every highlight played from the very beginning).
- Video Engine: the ambient sound chosen for each shot is now actually heard (it was stored on the clip but never mixed); only Video 1 is replaced, overlays on Video 2 stay.
- Pixabay videos keep their real length (they were all cut to 5 seconds).
- The video review window runs on the main UI thread. Before, it ran on its own thread and crashed or did nothing when it read texts or sought the player. *From timeline* now picks the last rendered video.
- The AI music video creator places the song on Audio 1 at the timeline cursor and pins all scenes, ambient sounds and pops so that nothing moves afterwards.

**Accessibility**
- Buttons and menu items whose visible text starts with an emoji get a clean accessible name, so the screen reader no longer reads emoji names before the label.
- Missing names were added to the subtitle lists and the Pixabay *Editor's choice* checkbox. Text overlay on images opens a simple, focused dialog.
- Items in the log, batch export, animation scenes, Video Engine shots, video review findings and Pixabay results now have readable names. Before, screen readers could read the class name (for example "UltraVideoEditor.LogEntry") instead of the text.
- Pixabay search announces how many results were found.
- Fewer double announcements. Play/Pause labels are translated. Previous-marker has an accessible name.
- *Help → User manual* opens the keyboard help instead of a "coming soon" message. *Settings* explains where language, theme and display mode are set (View menu).

**Fixes after the first real test (1.4)**
- **"Serious error" when a clip finished playing:** the player reports the end of a clip on its own thread, and the program read the interface language from there, which WPF does not allow. The language is now readable from any thread, and player events no longer wait for the UI thread.
- **Error messages follow the interface language** (they were always Serbian), say what happened and where the log file is. The position announcement and the log window are translated too.
- **Text in videos (titles, outro, subtitles):** the Windows FFmpeg build crashed on every text step ("Fontconfig error: Cannot load default config file"). The program now writes a small font configuration that points to the Windows fonts and hands it to FFmpeg.
- **GPU encoder fallback:** when a render step fails with the NVIDIA encoder (NVENC), it is repeated with the CPU encoder, and the rest of the render stays on the CPU. Every failed step of a render is now kept in `Logs\render_last_error.log`, not just the last one.
- **The song is never cut short:** if some clips failed and the picture is shorter than the song, the last picture is held until the music ends and you hear a warning. Before, the song was silently cut to the length of the picture (a 3:30 song became a 36-second video).

**Editing review from a blind editor's point of view (roadmap R2)**
- **Your own footage is no longer changed behind your back:** the automatic children's-video look (warm grade, seasonal colours, denoise, Ken Burns zoom and pan) was applied to EVERY video, including your own recordings. It is now used only for clips the AI music video creator made; your clips render exactly as shot.
- **Your clips keep their sound:** every video was rendered without its audio, so a recording of children talking or singing came out silent. The original sound of your own video clips is now mixed in at its place on the timeline, with the clip's volume (AI stock clips, muted clips and clips with a changed speed stay silent). A project with no music track still gets the clips' sound.
- **Effects reach the video:** brightness, contrast and blur from the Effects tab, and the clip speed, were stored on the clip and never rendered. They are applied now. The volume of the music clip (Ctrl+Alt+V) is honoured too.
- **Shortcuts that only existed in the menus now work:** Ctrl+Z (undo), Ctrl+Y (redo), Ctrl+A (select all), Ctrl+I (add video or image), Ctrl+Shift+I (add audio), Ctrl+Shift+E (apply effects) and F1 (help) were shown in the menus, but WPF menus only display gesture text, and nothing handled the keys. In a text field, Ctrl+Z, Ctrl+Y and Ctrl+A keep their usual text meaning.
- **Seeking from the keyboard:** Alt+Left/Right moves 5 seconds, Alt+Shift+Left/Right a tenth of a second, and the new position is always announced (before, only the two on-screen buttons could seek).
- **Hear the whole montage without a render (roadmap R3):** Ctrl+Shift+Space (also *Timeline → Play the whole montage* and the 🎧 button next to Play) mixes every sound of the timeline — music, ambient sounds, effects, narration and the sound of your own video clips — at its place, in-point, length and volume, using the same rules as the final render. Mixing takes seconds; the mix is cached and reused until the timeline changes. Playback starts where the player stands or at the selected clip; the same key pauses and continues, Ctrl+Space works too, and Alt+arrows seek inside the montage. While it plays, the name of the shot that is on screen at that moment (and its on-screen text) is announced, so a blind editor hears the picture order together with the sound; *Timeline → Announce shots while the montage plays* turns that off. The preview is sound only — the picture area stays empty; selecting a clip goes back to normal clip playback.
- **Ambient sound library from Adobe:** the library is Adobe Audition's free sound effects, which Adobe's licence does not allow to share as a standalone package, so it is not hosted by this project. Instead the installer has an optional task (*Download the ambient sound library from Adobe*, unchecked) and the program has *Help → Download the sound library from Adobe…*. Both run `Tools\DownloadAdobeSounds.ps1`: it downloads the six categories the AI creator uses (Weather, Animals, Human Elements, Liquid and Water, Ambience 1 and 2, about 3.7 GB) straight from Adobe, one at a time, converts every sound to OGG and keeps only those (about 350 MB) in `%LOCALAPPDATA%\UltraVideoEditor\Sounds`. Finished categories are skipped if the download is started again; each step is announced. The AI creator and the sound library read both this folder and `Assets\Sounds` next to the program.
- **Audio description of long clips:** a clip longer than 12 seconds without picture descriptions (for example a whole finished spot put on the timeline) is now described part by part at its shot changes before the script is written — before, it got a single description for the whole clip. Progress is spoken every fifth step.
- **Display modes, quieter and smarter:**
  - What the modes do: in *Accessibility* mode the screen reader speaks the program's messages (clip actions, positions, slider values, render progress) and a banner says the mode is on; *Sighted* is the same program without speech; *Low vision* adds high contrast and enlarged text (Ctrl+Shift+Plus/Minus) without speech.
  - The automatic position announcement while playing is now **off by default** (it spoke every 10 seconds). *View → Announce the position every 30 seconds while playing* turns it on; the choice is remembered. The position is always said when you press K or seek.
  - A screen reader started **after** the program is noticed within about 10 seconds and Accessibility mode is turned on, with an announcement (before, the program stayed silent until Ctrl+Shift+A). If you chose Sighted or Low vision yourself, that choice is respected.
  - Leaving Accessibility or Low vision mode is still said aloud, even though the new mode is a silent one.
  - Leaving Low vision mode gives back the theme you had before (it stayed high contrast, also at the next start).
- **Audio description for viewers (roadmap 4.2):** *AI → Audio description for viewers…* makes a version of the finished video that blind viewers can follow — the picture told in words between the lines of the song.
  1. **Choose the finished video and write the description script:** like audio description on Netflix, the script is made from the finished video itself — it is cut at its real shot changes (FFmpeg scene detection; shots under 1.2 s are joined, shots longer than 8 s get another description about every 8 s if the picture changed), and the shots are described in the order a human describer would: the program knows roughly how long each sentence takes to say (about 2.7 words a second in English, 2.3 in Serbian and German), so it describes the next shot that is still on screen when the previous sentence has finished, and skips only the shots that are already gone. A shot that gives no description is tried once more in its middle. Every shot it describes gets one short, complete sentence written for viewers (Qwen2.5-VL with a viewer prompt: who is there and what happens, titles and credits read out, no camera words), timed at the moment the shot appears. It does not depend on the timeline, so it is right even for an older project or an imported finished spot. (Before, it used the timeline's clip times; with an older project those did not match the rendered video, and only the start and end got descriptions.)
  2. **Read and correct it** in a plain text field, one line per description: `mm:ss.f | text`. Lines can be changed, removed or added; a line without a time continues the previous one.
  3. **Choose the voice** and how much the music goes down under the voice (a little, medium, a lot). Voices, in the program language (since 1.4.6):
     - **Google HD voices** (Google Cloud Chirp 3 HD, a female and a male voice) — the most natural ones. They appear at the top of the list once a Google Cloud API key is saved in the window (*Test and save the key*; the key is tested with one word, then stored encrypted on this computer). The key needs the Cloud Text-to-Speech API turned on; the first million characters a month are free (a 3-minute song with 20–25 descriptions uses about 2,000).
     - **Microsoft natural voices** (the neural voices Microsoft Edge uses for *Read aloud*; Serbian: Sofija and Nikola) — free, no key, no account. This is not an official developer service and may change; the access token is a C# port of the open-source edge-tts project (`EdgeTtsToken.cs`, checked against its output).
     - The plain Google voice (online) and any installed Windows voice (offline).
     Serbian is sent to the natural voices in Cyrillic letters, which they read most reliably. If a natural voice fails for a line, that line is spoken with the plain Google voice and the reason is written to the log.
  4. **Create:** every line is spoken and heard the moment its shot appears; if the previous description is still being spoken it waits; a description that would come more than 2 seconds late, when the next one is already due, is left out (and counted). Sentences are never cut in the middle: since 1.4.6 a description is always a whole sentence (a very long one is shortened only at a comma). Before, sentences were cut to a word count ("A girl walks through the forest with a.") and every slightly late description was dropped, which left long silent stretches while the shots changed. The descriptions are mixed over the finished video with the program sound lowered under the voice (sidechain compression), the picture is copied unchanged.
  - Result, next to the video: `<name> (audio description).mp4`, `<name> (audio description).txt` (timed text for the YouTube description) and `<name> (audio description).vtt` (WebVTT description track). The script stays in the window while the program runs. The finished video from the last render is filled in automatically.
  - **Local model (since 1.4.6):** the shot-by-shot mode prefers Qwen3-VL (`qwen3-vl:4b-instruct` for 4–8 GB of graphics memory, `qwen3-vl:8b-instruct` for 8 GB or more, `qwen3-vl:2b-instruct` without a suitable graphics card), which describes pictures much better than Qwen2.5-VL; the installer pulls the right one, and Qwen2.5-VL, LLaVA and Moondream still work when they are what is installed. The AI creator's vision analysis and the video review use the installed Qwen3-VL too. Qwen3-VL needs a recent Ollama.
  - **Language (since 1.4.6):** descriptions follow the program language. A remembered description is kept per language, so switching from English to Serbian and describing again gives Serbian, not the English text from the first run. The vision model always describes in English (small vision models describe well in English but write poor Serbian: Croatian forms, invented words), and camera words at the start ("Close-up of…") are removed. The English sentence is then translated: with Google Cloud Translation when a Google Cloud key is saved (the Cloud Translation API must be turned on for that key; 500,000 characters a month are free), otherwise with the free Google Translate service, and only if both fail by the local model. Serbian comes back in Latin letters. The English description is remembered, so describing the same video in another language only translates.
  - **Gemini watches the whole video (best mode, since 1.4.6):** with a Gemini key, *How the descriptions are written* offers *Gemini watches the whole video, with sound*. A smaller copy of the video (480p, 2 frames a second, mono sound) is uploaded to the Gemini File API; Gemini watches picture and sound together, the way a human describer works: it sees movement and what happens across shots, keeps characters consistent (names once they are known), reads on-screen text, and puts each description into a pause of the speech or singing, sized to fit (about 2.5 words a second). Long videos (films, series) are described in 10-minute parts at low media resolution; each part is told the last descriptions before it, so names and wording stay consistent. The answer comes back as the usual `mm:ss | text` script (minutes go past 59 for films), and the uploaded copy is deleted afterwards. The stages (preparing, sending with progress at every quarter, watching, part X of Y) are announced. *Shot by shot* remains for videos that should not leave the computer or when there is no key.
  - **How often to describe (Gemini, since 1.4.6):** *Normal* (default) prefers pauses, never covers dialogue in a film, and in a song sung almost all the way through also describes over the singing (the music is lowered under the voice), so the picture is never left undescribed for more than about 12 seconds; *Dense* describes every change of picture, about every 5–8 seconds; *Sparse* speaks only in clear pauses (the first version's behaviour, which left a song with gaps of over a minute). After each part, stretches still longer than 15 seconds (9 for *Dense*) without a description are sent back to Gemini once, asking for descriptions only inside them.
  - **Gemini (optional, since 1.4.6):** with a Gemini API key (made free of charge in Google AI Studio, saved in the audio description window with *Test and save the key*, stored encrypted), Gemini looks at each shot and writes the description directly in the program language — natural Serbian instead of a translation — and also translates when needed. This applies to the clip descriptions in the editor too. The program picks the newest general Gemini Flash model the key offers (so it keeps working when Google renames models); on the free tier's per-minute limit it waits and tries again, and if Gemini fails the local model takes over. Without a key nothing changes. Gemini also works without Ollama installed.
  - **Which video (since 1.4.6):** any video can be described, not only the finished render. The window has a *Video from the timeline* list with every video clip on the timeline; a video clip selected in the clip list is filled in automatically, otherwise the last render. *Choose video…* still picks any file from disk, and the window also opens with an empty timeline, so the editor can be used only for audio description. A timeline clip is described as its whole file (trimming on the timeline is not applied), and the result is saved next to that file.
  - **Working with the window (since 1.4.6):** it opens on the next thing to do (the script if there is one, otherwise *Write the description script* when the video is known, otherwise the video field). After *Choose video…* the focus comes back to this window, straight on *Write the description script*, and the chosen video is announced; Enter in the video field also writes the script. While the work runs, a *Stop* button takes the focus. The window speaks for itself: the start, the progress at every quarter (25, 50, 75 percent, no constant talking) and the end are announced from the window (UI Automation notification and live region), with a short system sound when it finishes or fails. Before, these messages went to the main window's status line, which a screen reader does not read while a dialog is open, so the work started and finished in silence, and after choosing the video the focus could land in the main window.
- **Graphic timeline for sighted editors (roadmap R7):** under the clip list there is now a graphical timeline, a second view of the same montage. Video 2, Video 1, Audio 1 and Audio 2 are lanes; clips are blocks with their first picture (video, photos) or their waveform (sound, and the sound of your own videos), name and length; the ruler shows time, markers are dashed yellow lines and the playhead is red. ∿ marks a clip with a volume curve, 👁 one with picture descriptions.
  - **Mouse:** click a clip to select it and put the playhead there (paused), double-click to play from there, click the ruler or an empty lane to move the playhead, drag a clip to move it (also to the other video or audio track — video stays on video tracks, sound on audio tracks), drag its left or right edge to trim it. Clips snap to clip edges, markers, the playhead and 0; hold Shift to drag freely; right-click during a drag cancels it. Ctrl+mouse wheel zooms (the same zoom as Ctrl+plus/minus).
  - Moving or trimming here is an ordinary edit: it goes into undo (Ctrl+Z), the clip list is updated at once and the change is announced, so a blind and a sighted editor can work on the same project together. Trimming the start of a clip keeps its volume points and picture descriptions on the same material (also for Q).
  - While playing, the view scrolls with the playhead. The splitter between list and timeline changes their heights; *View → Show the graphic timeline* hides it. For screen readers it is one labelled element that never takes the keyboard focus, so the list stays the keyboard way to edit.
  - Pictures and waveforms are made in the background with FFmpeg (two files at a time) and kept while the program runs.
  - **Undo fix:** Ctrl+Z after trimming, a speed change or "lower the music under speech" also threw away the clip's volume points, audio effects and picture descriptions; undo copies now keep them.
- **Safe updates:** the program downloads a new version from GitHub and used to run the installer (with administrator rights) without checking it. It now compares the file's SHA-256 with the one GitHub publishes for the release (the asset digest, or the `.sha256` file the release workflow now uploads next to the installer). If they differ, or no checksum exists, the file is deleted, nothing is run, and the release page opens instead.
- **Third-party licences:** `THIRD-PARTY-NOTICES.md` lists the libraries, tools, AI models and media services the program uses, with their licences. It is installed next to the program and opens from *Help → Third-party licences*. Note: on weaker computers the installer picks the small Qwen2.5 3B model, which is licensed for non-commercial use only.
- **Sound library in OGG:** `ConvertAssetsToOgg_InPlace.ps1` converts the ambient library from WAV to OGG (several times smaller, so it fits a GitHub release). It now finds FFmpeg by itself and also removes the old WAV copies Visual Studio left in the build folder (to the Recycle Bin), so no sound is listed twice. The transition sounds (pop, whoosh) are found in any format.
- **About window with support links:** *Help → About* is now a small window instead of a message box: the version, the author (Demir Ajvazi) and the licence in a read-only text field you can read line by line, and buttons that open GitHub Sponsors, Ko-fi, PayPal and the project page in your browser. Escape or Enter closes it.
- **NVDA and Narrator (roadmap R6, first step):** the program now recognises which screen reader is running. With NVDA or Narrator (and no JAWS), spoken messages are sent as one UI Automation notification, which both read reliably, instead of the two alternating live regions made for JAWS (NVDA could read those twice or not at all). With JAWS nothing changes.
- **Sound editing (roadmap R5, second part):**
  - **Volume over time:** Ctrl+Shift+V (*Timeline → Volume point at the playhead*) sets the volume at the place you stand, in the same volume window as Ctrl+Alt+V. With two or more points the volume moves smoothly from point to point (louder here, quieter there); before the first point it keeps the first value, after the last one the last value. A point within 0.05 s of an existing one changes it. *Read the clip's volume points* speaks them all with their times, *Remove the clip's volume points* goes back to one steady volume. The render, the montage mix (Ctrl+Shift+Space) and the clip preview all follow the curve.
  - **Lower the music under speech** (*Timeline* menu): wherever another audio clip of yours (narration, voice) or the sound of your own video plays, the music drops to 30 % with a 0.4-second fade down and up. It writes ordinary volume points on the music clip (the selected audio clip, or the longest one), so you can hear it in the montage mix, adjust single points with Ctrl+Shift+V, and undo it with Ctrl+Z. AI sound effects and ambience do not lower the music.
  - **Audio effects reach the video:** bass, treble, reverb and compressor in the Effects tab only coloured the preview; nothing was stored or rendered. *Apply effects* (Ctrl+Shift+E) now stores them on the clip together with brightness, contrast and blur, says what was applied, and the render and the montage mix use them.
- **What is in the picture here — for every clip, including your own footage (roadmap R4):**
  - **Ctrl+Shift+F** (*AI → Describe the frame I am on*) describes the exact frame under the playhead with the local vision model (Qwen2.5-VL through Ollama; llava or moondream if that is what is installed): who or what is in the picture, what is happening, where, the shot size and any readable text, in the program language. It works on a clip, on a picture and during the montage mix. Nothing leaves the computer.
  - **Ctrl+Alt+A** opens *AI → Audio description for viewers* directly (since 1.4.6).
  - **Ctrl+Alt+D** (*AI → Describe the selected clip by parts*) finds the shot changes inside the clip with FFmpeg's scene detection, divides it into parts of 2–8 seconds (at most 40) and describes each part in the background while you keep editing. *Describe all my clips by parts* does the same for every video and picture of yours that has no description yet (AI creator clips are skipped); *Stop describing* cancels.
  - The descriptions are saved in the project. While you play, seek, step frames or shuttle through a described clip, the description of each part is announced as the playhead enters it, and during the montage mix every new shot is announced with its description. *AI → Announce picture descriptions while moving through a clip* turns that off.
  - **Songs saved as video:** a song exported as .mp4 often has no picture at all, or only a still cover picture stored as a single frame. Asking for the frame at 0:34 of such a file returned nothing, so every description failed ("Could not extract a frame"). The file is now checked first: a sound-only file is reported as such, and a cover picture is described once for the whole clip.
  - *Describe all clips* now uses the same describer (one description from the middle of each of your clips). A failure is reported and no longer stored as the clip's description; the clip list always reads the clip name first and a real description after it (old error texts and AI tags are not read).
  - **After a clip or the montage ended,** Play, J/K/L, comma/period and Alt+arrows did nothing (the player stays "ended" and ignores seeking — "position 00:00" forever). The clip or mix is now reloaded: Play starts again from the beginning, seeking reloads it paused at the new place.
  - "Position X of Y" now gives the length of the clip, not of its whole file (a 10-second cut part said "of 02:59").
  - *Read audio description*, *Add audio description* and *Auto-describe* in the clip's context menu work for every clip with a picture, not only for pictures ("Select an image first"). Reading also gives the described part under the playhead.
  - Selecting the clip again after a list refresh (describing, trimming, volume…) no longer restarts it from 0:00; playback stays where it was.
  - A J (backward) still running when the montage mix starts no longer jumps the mix to the start.
  - Describing a frame takes a few seconds to half a minute, depending on the computer; the same frame is described only once per session. Clips are described on request, not automatically when they are added, so the vision model never competes with the AI music video creator without you knowing.
- **Precise editing from the keyboard (roadmap R5, first part):**
  - **J / K / L** — shuttle like in Premiere: L plays (press again for 2× and 4×), K stops and announces the exact position, J runs backward (1×, 2×, 4×, 8× — VLC cannot play in reverse, so the playhead steps back four times a second while paused).
  - **Comma / Period** — one frame back / forward (the frame rate is read from the clip, 25 fps if unknown), with the time announced to the hundredth of a second.
  - **Q / W** — trim the start / the end of the clip to the playhead. The clips after it on the same track move up, so no gap is left (ripple). The clip then plays from its new start, or its last two seconds, so the edit is heard at once. Ctrl+Z undoes it.
  - **Ctrl+Alt+S** (also *Timeline → Clip speed…*) — speed of a video clip from 0.25× to 4×, chosen from an accessible list. Clip playback in the editor now runs at that speed too. A clip with a changed speed plays without its own sound, as in the render.
  - These keys work in the clip list and anywhere in the main window except text fields. In the clip list, J, K, L, Q and W no longer jump to clips by their first letter.
  - **Photos:** your own pictures were checked — they never got the AI look, only fitted to the frame. They now also get brightness, contrast and blur from the Effects tab, and phone photos are turned upright from their EXIF orientation (they could come out sideways).
  - **Delete** was already a ripple delete for clips without a fixed position: the following clips on the track move up.
  - Seeking (Alt+arrows, J, comma/period) now stays inside the clip: a cut part or a highlight starts in the middle of its file, and seeking used to run into footage that is not on the timeline.
  - Ctrl+Space, Ctrl+Shift+Space and Alt+arrows also work while the focus is in the clip list (a Windows Forms control whose keys do not always reach the window), and a key that arrives both ways acts only once.
- The menu and the keyboard help said Ctrl+Shift+P for "previous marker", but that key opens Project Templates; both now show the real key, Ctrl+Shift+, (comma).
- *Describe all clips* uses the installed vision model (Qwen2.5-VL first); before it accepted only moondream.
- A few spoken messages that were still Serbian (paused at, playback started, "(deo 1)") follow the program language.

**Stability (roadmap phase 1)**
- **Autosave and crash recovery:** every 2 minutes, if something changed, the project is copied to `%LOCALAPPDATA%\UltraVideoEditor\Autosave`. A normal save or close removes the copy. If the program did not close normally, it offers to restore the project at the next start.
- **Projects can be moved:** each clip also stores its path relative to the project file. When a project is opened from another folder, disk or computer, its media is found again automatically. Anything still missing can be searched for, by file name, in a folder you choose.
- **No more silent errors:** failures that were ignored before (reading keys, Whisper output, beat and motion analysis, loudness, templates, theme, AI answers...) are written to a daily log file and shown in the log window. Unexpected errors also go to the log, and the error message says where the file is. *Help → Open the log folder* opens it.
- **Render errors can be diagnosed:** when an FFmpeg step fails, its command and the end of its output are saved to `Logs\render_last_error.log`. Before, that output was thrown away. The error message names the file instead of reading a stack trace aloud.
- **One place for tools and keys:** FFmpeg and ffprobe are found the same way everywhere (program folder, PATH, usual install folders). There is one Pixabay key for the whole program, the same one the *Media providers* window edits; before, the AI creator, Video Engine and Pixabay search read an older copy. The key can be replaced in the Pixabay search window too. Other keys (Cloudflare and others) go through one encrypted key store.
- **Automated tests:** a test project in the `Tests` folder (see *Building from Source*).

**Fixes after the second real test (1.4)**
- **Most clips failed with FFmpeg 8:** the seasonal colour grade used curve points above 1 (`1/1.08`, `1/1.02`), which FFmpeg 8 rejects ("Invalid key point coordinates"). Every winter, spring and autumn shot failed, so a 3-minute song got about 40 seconds of picture. All curve points are now within 0–1.
- **Gentler colours:** the same warm tint and saturation boost were stacked four times on every clip (about 1.5× saturation, orange skin). Each clip now gets one gentle base grade plus its seasonal grade.
- **Text in videos:** every `drawtext` (intro, outro, subtitles) now names its font file directly (Arial, or Segoe UI / Tahoma), so FFmpeg no longer asks fontconfig and no longer crashes. Serbian letters (č, ć, š, ž, đ) are drawn as they are instead of being simplified, an apostrophe in a lyric ("nek' divan") no longer breaks the whole subtitle step, and `%` is shown correctly. Karaoke subtitles get the Windows font folder too.
- **The outro showed the words "Outro text":** the AI creator's outro is now named `Outro text: <your text>` and the renderer reads the text from it. The default outro follows the program language, and a song file without "Artist - Title" in its name no longer puts the song title in the outro.
- **Sound quality:**
  - The render kept the sound as AAC between its steps and encoded it again at the end (three lossy passes). Every intermediate step is now 32-bit float WAV, and the finished video takes its sound straight from that lossless master: one AAC encode, 256 kbps, 48 kHz.
  - The ambient mix resampled the song to 44.1 kHz with the default resampler and ran it through a limiter with automatic levelling, which pumped the whole song. It now works at 48 kHz with a high-quality resampler and has no limiter; a gentle peak limiter (no levelling) is applied once, at the very end.
  - Pops and whooshes on transitions are off in calm songs (lullaby, sad, love, calm, nature), at most one every 8 seconds elsewhere, and much quieter (25–40 % instead of 55–85 %).
- **Wrong ambient sounds:** the sound library search gave every "Ambience ..." file the same score, so the first file in the folder won (a lullaby could get an air conditioner or an auto repair shop). Generic words now count very little, a file must match at least one specific word, machines, shops and vehicles are filtered out for every song (unless the AI asked for a city sound), and nothing is used rather than a wrong sound.
- **Ambient sounds were one-shot effects on a loop:** besides about 150 continuous background recordings, the sound library holds hundreds of single effects (one bird chirp of half a second, one dog bark, one footstep in snow). Birds, spring, snow, animals and the default category all landed on such one-shots, and the mixer looped them for the whole scene, so the same chirp repeated like a machine gun. Now only recordings of 15 seconds or longer can be a background; every category is mapped to a real bed (birds and spring → "Ambience Nature 180", snow → wind through trees, frogs → nature near a swamp, night → wind with crickets); a file whose name starts with the requested words wins over the synonym search; a lullaby only gets calm beds; and a scene shorter than 8 seconds keeps the background of the scene before it instead of switching.
- **Children's content only (review of "Pesma o šetnji"):** a video review found physiotherapy of an elderly person in a wheelchair, road works with a danger sign, aerial shots of crowds, and a snowy mountain with a ski lift plus a French press for the line "leti kupite sladoled, zimi neki topli čaj, može i topla čokolada". Fixes:
  - The keyword map is written with č, ć, š, ž, đ, but typed lyrics often are not ("caj", "cokolada"), so those words were never found and the line fell back to its season word. Lyrics and keywords are now compared without diacritics.
  - When a line names real things, season-only shots (snowy mountain, sunny meadow) are left out of its montage; hot drinks search for "child drinking hot chocolate mug" instead of "hot drink cup".
  - A line that names something a child can see (ice cream, hot chocolate, scooter, ball, dog...) shows that thing, even when the director planned a mood shot. It is deterministic, so a repeated chorus keeps the same picture.
  - Every director query contains a child; grandparents, mum and dad appear only together with a child. The director prompt says so too.
  - A new filter (`IskraKidsSafeQuery.IsUnsuitableForKids`) rejects care and therapy, wheelchairs, building sites and road works, warning signs, crowds and aerial shots of people, coffee makers and ski lifts, and old people without a child. It is used for Pixabay videos, Pexels and, for the first time, Pixabay **pictures** (they were not checked at all). The vision AI has the same rules.
  - The anti-repetition step no longer adds season words to a search ("+ snow frost winter" is what found ski slopes).
- **Third test – the vision AI never looked at a clip:** every clip scored exactly 6.0/10 with "Motion: Unknown". A downloaded clip was still open (locked, partly unflushed) while FFmpeg tried to read it for the brightness check, the vision AI (Qwen) and the motion analysis, so all three silently failed and the fallback score was used. Downloads are now closed before anything reads them. The log names the analyser for every score (Qwen, ONNX or FFmpeg with its reason).
  - The FFmpeg fallback metrics and the brightness/pink check never got any numbers (signalstats prints only with `metadata=print`, blurdetect prints "blur mean", and signalstats has no RAVG/GAVG). All three are fixed; dark shots below 60/255 are rejected.
  - Each Pixabay clip is downloaded once, in Full HD ("medium"). A rejected clip used to be downloaded again in the next size and rejected again (up to three times, starting with 4K): one test video downloaded 1061 files (30 GB).
  - A finished sub-scene montage is no longer thrown away for a second montage; every montage clip must show a child too, and a montage that NVENC cannot build is retried on the CPU.
  - A line that names its own season ("Kad je zima nosi čizme", "na velikom snegu") only gets clips that clearly show that season (no autumn leaves or season-less clips). Season words are recognised with or without diacritics.
  - The filter also rejects silhouettes against windows, waiting rooms, airports, buildings and columns, parking lots and motorbikes, cooking pots, hair close-ups, and wild animals or insects that the lyric does not name (meerkat, monkeys, ants). The vision AI has the same rules.
- **Fourth test – 3.5 hours for 8 scenes:** with the vision AI finally working, every candidate clip cost a download plus about 30 seconds of analysis, and style rules then threw most of them away (101 "two close shots in a row" and 89 "bridge" rejections; any clip with a child counts as a close shot, and every lyric clip must have a child). One 3-second intro shot took 13 minutes.
  - The two shot-size rules are off; the director's plan and the vision AI already vary the shots.
  - **Candidate budget:** style rules (camera motion, indoor/outdoor, brightness jumps, the vision AI's "could fit better") may reject the first three analysed clips of a search; from the fourth on they are only preferences. Content rules (unsafe content, the vision AI's reject, a season the lyric names) always apply.
  - A season the lyric names is checked from the clip's tags **before** downloading. Winter and autumn lines need a visibly wintry or autumnal clip; spring and summer lines only refuse cold-season clips. Season tags are matched as whole words, so an ice-cream clip is no longer "winter" and a waterfall no longer "autumn".
  - The motion analysis never ran (an invalid FFmpeg option, inputs used four times without split, and no printed numbers), so every clip was "Motion: Unknown". Fixed.
  - Ollama keeps the models loaded for 30 minutes, uses a 4096-token context and at most 400 answer tokens for the vision AI. The log shows the analysis time and the candidate number of every accepted clip.
- **Fifth test – the vision AI was slow and said "7" to everything:** each analysis took 45–95 seconds, and 29 of 31 clips got exactly 7.0. The prompt had 93 lines of partly contradicting rules (about 2000 tokens), and its JSON template contained `"score":7`, which the model copied. It is now a short prompt: a compact reject list, a score scale with meaning (9–10 = a child clearly doing what the line says), and an example without numbers. The three frames sent to it are 336 px wide instead of 448. A "reject" written as the text "null" is no longer taken as a reason.
  - The "camera motion too aggressive" rule is off until the newly working motion measurement is calibrated (most ordinary clips measured 100 of 100, and the rule threw away 37 analysed clips).
- **Sixth test – the vision AI now judges, but rejected clips still slipped in:** the short prompt works (scores from 1 to 10, about 29 s per analysis), but the last two results of a search were accepted even when the AI had rejected them: 11 clips with score 1.0 got into the video, and the quality gate later cut two lyric lines out, leaving holes. A clip scored below 4 is now never used; after six analysed clips a search gives way to the next, simpler query. Instrumental intro and outro shots are judged as instrumental parts (nature without children is fine there) instead of against the last lyric line. Very quiet ambient recordings may be raised up to ×16 (was ×8).
- **Third Gemini review – montage parts and trains:** in the ice-cream line ("leti kupite sladoled, zimi neki topli čaj, može i topla čokolada") every montage part was judged against the WHOLE line, so an ice-cream clip scored 1/10 because it showed no tea or winter; after 14 rejections the search fell back to generic words and brought an icy cliff, a box of brushes and a train by an industrial fence.
  - Each montage part is now judged by what it must show ("this clip is one part of a montage and only needs to show: child eating ice cream").
  - The second try of a search keeps its concrete words ("family buying ice cream" → "buying ice cream child") instead of "first word + children outdoor".
  - A line about tea or hot chocolate may show a cosy home (mug, family, fireplace) even in an outdoor song.
  - Trains, railways, stations, industrial areas, chain-link fences, cooking pots and steam, icy cliffs and glaciers are refused unless the lyric names them (and the vision AI refuses them too).
- **Fourth Gemini review – almost there:** the video was called "a full hit"; two shots stood out. Tigers in the instrumental outro (the vision AI had been told that animals without children are fine in instrumental parts) and a boy looking at a tablet. Wild animals (tiger, lion, wolf, bear, crocodile, shark, snake) and screens (tablet, phone, computer, TV, video games) are now refused unless the lyric names them, by the tag filter and by the vision AI. The quality gate no longer removes lyric scenes (it left a line without its own picture); it only drops weak instrumental shots. The last, safe search of a winter or autumn line keeps its season ("children playing in snow"), so such a line no longer ends up with no picture.
- **Ambient sounds were inaudible:** quiet field recordings got a fixed small gain and were then ducked hard under the music for the whole song. Every recording is now measured (FFmpeg volumedetect) and set to a fixed distance below the song's average loudness (14–20 dB depending on the song), ducking is gentle (at most about 6 dB, only when the singing band is loud), and the background is chosen per part of the song (15 s or more) by majority instead of letting the first line decide for a minute.
- **Downloads no longer fill the disk:** every clip the AI creator tried was kept in the project folder, including all rejected ones (one test project collected more than 1000 clips, 27 GB). At the end of a run every download and temporary file the timeline does not use is deleted. The temporary folder is no longer deleted as a whole after the auto-render, because the timeline still points to the clips in it.
- **Language consistency:** messages that were still in Serbian (progress, announcements, dialogs, reports, log lines) are now translated or follow the program language.
- `-vsync` (deprecated in FFmpeg 8) was replaced with `-fps_mode`, and durations in FFmpeg commands are rounded to milliseconds.

---

## What's New in 1.0.3

- YouTube import: "Just this video" and "Choose specific tracks" now download exactly what you chose (previously a whole playlist could be downloaded). Cancel now works, only files from the current download are added to the timeline, and screen reader announcements are audible.
- Fixed possible freezes during transcription, beat detection, 9:16/square export and other FFmpeg operations on longer videos. Cancelling these now stops FFmpeg/Whisper too.
- Update download no longer times out on slower connections.
- Correct version number in the title bar and About dialog.
- Complete German translation of the YouTube dialog and update messages.

---

## Requirements

| Component | Note |
|---|---|
| Windows | 10 / 11, 64-bit |
| RAM | 8 GB minimum, 16 GB+ recommended |
| GPU | NVIDIA GPU optional (NVENC encoding, faster AI) |
| FFmpeg, VLC, Ollama + models | Installed automatically by the installer |
| Whisper (faster-whisper-xxl) | Optional, installed by the installer on request |
| Pixabay API key | Free, needed for the AI music video creator ([pixabay.com/api/docs](https://pixabay.com/api/docs/)). Enter it under **Tools → Media Providers (API keys)**. |
| yt-dlp | Only for YouTube import; downloaded by the app on first use |

---

## Building from Source

This project lives in the Ultra Creative Suite monorepo:

```bash
git clone https://github.com/demirajvazi10-max/Ultra-Creative-suite.git
cd Ultra-Creative-suite/UltraVideoEditor
dotnet restore
dotnet build -c Release
```

Folders named `Backup_*` (local copies kept before larger changes) are excluded from the build in the project file, so source copies inside them never cause duplicate definitions.

You can also open `UltraVideoEditor.slnx` in Visual Studio 2022 and build there.

### Tests

The `Tests` folder holds an xUnit test project (`UltraVideoEditor.Tests.csproj`, part of the solution). It covers the parts that do not need a window: timeline layout (parallel tracks, pinned clips), Serbian text normalization, lyric alignment, transition names and file type detection. Run it in Visual Studio with *Test → Run All Tests* (Ctrl+R, A), or from the command line:

```bash
dotnet test Tests/UltraVideoEditor.Tests.csproj
```

The main project excludes the `Tests` folder, so the tests never end up in the app.

For a local build, place the external tools next to the built `.exe` (the installer does this for end users):

```
UltraVideoEditor/
├── Ffmpeg/
│   ├── ffmpeg.exe
│   └── ffprobe.exe
├── Whisper/
│   └── faster-whisper-xxl.exe   # or whisper.exe
└── ...
```

Then start Ollama and pull the models, for example:
```bash
ollama pull qwen2.5:7b
ollama pull qwen2.5vl:7b
```

NuGet packages:
```xml
<PackageReference Include="LibVLCSharp" Version="3.9.6" />
<PackageReference Include="LibVLCSharp.WPF" Version="3.9.6" />
<PackageReference Include="Magick.NET-Q16-AnyCPU" Version="14.16.0" />
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.19.0" />
<PackageReference Include="Microsoft.Windows.Compatibility" Version="10.0.6" />
<PackageReference Include="NAudio" Version="2.3.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
<PackageReference Include="SkiaSharp" Version="2.88.6" />
<PackageReference Include="SkiaSharp.Views.WPF" Version="2.88.6" />
<PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.0.23" />
```

### Releases

Releases are built by GitHub Actions (`.github/workflows/build-release-video.yml`) when a tag in the form `video-vX.Y.Z` is pushed. The version from the tag is written into the app automatically, so the version shown in the app always matches the release.

---

## System Architecture

```
Audio file + Lyrics
        │
        ▼
┌─────────────────────┐
│   AITranscription   │  Whisper → timestamped lyrics
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│   BeatDetection     │  FFmpeg RMS energy → beat timestamps
│   + Piano Mode      │  Spectral flux → melodic phrases
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  AIVideoCreator     │  Main orchestrator
│  ┌───────────────┐  │
│  │ StrictQuery   │  │  LAYER 1: Ollama/Qwen → semantic query
│  │ Engine        │  │  LAYER 2: _actionMap (552+ SR/BA/HR → EN)
│  │               │  │  LAYER 3: SmartFallback
│  └───────────────┘  │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Pixabay API        │  Stock video search + deduplication
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  VisionAnalyzer     │  Qwen2-VL / ONNX → score, HasChildren,
│                     │  HasSmile, IsOutdoor, season, motion
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  MotionAnalyzer     │  FFmpeg optical flow → direction matching
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  RenderEngine       │  FFmpeg filter_complex → final video
│                     │  xfade + color grading + denoise
└─────────────────────┘
```

---

## How the AI Music Video Creator Works

### 1. Query Pipeline (director + 3 fallback layers)

**Layer 0 — Director pass (since 1.4)**
Before any search, the whole song is planned in batches of 16 lines. The model first fixes a visual identity (main character, setting, palette) and then writes, for every line, a 3-6 word search, a simpler fallback and an ambient sound. The identity and the chorus searches are carried from batch to batch. These searches are used directly; the layers below are used only when the director's answer is missing or rejected by the safety filters.

Every lyric line without a usable director search passes through three layers:

**Layer 1 — Ollama/Qwen (primary)**
Qwen receives the lyric line + `LyricTagType` (Action/Atmospheric/Object/Narrative) + `SentimentPolarity` (Positive/Negative/Neutral) + a `needsCloseUp` flag, and generates a 3-5 word English search query.

**Layer 2 — `_actionMap` (552+ entries)**
Direct matching of Serbian/Bosnian/Croatian keywords to an English visual query. Priority scoring favors concrete objects over abstract states — "ice cream" (score 50) always wins over "flies" (score 14) within the same line.

**Layer 3 — SmartFallback**
Contextual fallback based on the detected season and mood — never returns null, never a black screen.

### 2. Beat Detection + Piano Mode

For standard music with drums: RMS energy spikes → beat timestamps → cuts on downbeats.

For piano/melodic music (low confidence or uneven beats):
- **Phrase detection**: smoothed energy profile → spectral flux → melodic phrase boundaries
- **Dynamic pacing**: `NoteDensity` (0-1) mapped to shot duration — quiet phrase → 4.5s, dense passage → 1.8s
- **VibeScore modifier**: high-energy scenes get a 25% shorter shot

Log: `🎹 Piano mode active: 12 melodic phrases, density=0.43 → 3.3s average`

### 3. Vision Analysis

Every downloaded clip is analyzed by **Qwen2-VL** (if available) or **ONNX MobileNetV2** (fallback). Qwen gets a strip of 3 frames (15 %, 50 % and 85 % of the first 6 seconds of the clip, 448 px wide, aspect kept); ONNX and the FFmpeg metrics use a single 224×224 frame.

- `Score` 1-10 (overall visual quality)
- `HasChildren`, `HasFaces`, `HasSmile` — presence of children and emotion
- `IsOutdoor`, `IsWarm` — setting and color temperature
- `RetryNeeded` — Qwen flags that the clip doesn't match the lyric's context
- `Description` — one sentence about what the clip shows, read out in the scene review

**Smile bonus**: if `HasSmile=true` and the lyric is `Positive` sentiment → VisionScore +1.5

### 4. Seasonal Color Grading (per shot)

Every shot gets an FFmpeg `curves` + `eq` filter based on the season of **that specific lyric line**, not the song's global season:

| Season | Effect |
|---|---|
| `winter` | Blue tones, reduced R, slightly boosted B in the mid-tones, -12% desaturation |
| `summer` | Golden tones, boosted R/G, reduced B, +18% saturation |
| `spring` | Fresh greenish tint, slightly boosted G |
| `autumn` | Warm orange, boosted R, reduced B |

### 5. Shot Composition

The system tracks the sequence of shots and avoids:
- Two consecutive `wide` shots without children (PATCH9 30% Rule)
- Two consecutive `medium` shots (shot composition filter)
- An extreme jump from `wide → close` without a bridge shot

### 6. Motion Matching

`MotionAnalyzer` analyzes the optical flow of the first and **last** frame of every clip. The next clip must have a compatible motion direction — eliminating jump-cut problems.

### 7. Query Cooldown

The same visual theme (first 2 keywords of a query) may not repeat within 4 consecutive scenes (~12-16s). If a repeat is detected, the query gets a seasonal variant instead.

### 8. Lyric Alignment (since 1.4)

`LyricAligner` aligns the user's lines with the words Whisper heard: both are normalized (`SerbianText`: Cyrillic→Latin, no diacritics, no punctuation), then a Needleman–Wunsch alignment over words with Levenshtein similarity pairs them. Unmatched words get interpolated times. The alignment is trusted when at least 35 % of the words match; otherwise the old segment mapping is used. The result gives each lyric line its start time and each word its time (used for karaoke).

### 9. Ambient Sound Mix (since 1.4)

Each scene gets an ambient category from its lyric (word-start rules on normalized text; the lyric wins over the AI's suggestion). Neighbouring scenes with the same sound file become one region; each region starts at a random point in the file and overlaps its neighbours by 0.4 s for a crossfade. The regions are shifted by the same `AudioStartSeconds` the render cuts from the music, mixed into one bed, padded to the song length, ducked under the vocal band with `sidechaincompress`, and mixed with the song into a lossless 48 kHz float WAV (no limiter; the render applies one gentle peak limiter at the very end). Only continuous recordings of at least 15 seconds are used as a background (the length is read from the WAV header), and every category is first mapped to a bed that exists in the library (`MapToAmbientBed`). The sound file for a category is chosen from `Assets\Sounds` by its name: a file whose name starts with the requested words wins; otherwise specific words (crickets, creek, rain) count, generic ones (ambience, animal, weather) barely do, and machine, shop and vehicle recordings are filtered out.

### 10. Scene Review (since 1.4)

`SceneReviewDialog` lists every lyric scene with its lyric, the vision description, the score and the search words. *Find another clip* runs the normal search again (the current clip is already marked as used, so a different one comes back), trims it to the scene length and replaces it in place.

---

## Key Classes

| Class | Responsibility |
|---|---|
| `MainWindow` | Timeline editor, menus, keyboard shortcuts, screen reader announcements |
| `AIVideoCreator` | Main orchestrator of the AI music video creator: scene loop, query pipeline, media selection |
| `StrictQueryEngine` | SR/BA/HR → EN keyword map, Ollama prompt builder, ClassifyLyric/Sentiment |
| `BeatDetection` | Audio analysis, beat timestamps, piano mode phrase detection |
| `VisionAnalyzer` | Qwen2-VL / ONNX shot analysis, score, labels, smile |
| `MotionAnalyzer` | FFmpeg optical flow, direction matching |
| `RenderEngine` | FFmpeg filter_complex build, xfade, color grading, denoise |
| `SkiaAnimationEngine` | Skia-based text overlay and title animations |
| `CinematicProcessor` | Ken Burns, zoom/pan effects, Shorts/square export |
| `LocalSoundLibrary` | 1279+ ambient sounds, categorization and matching |
| `AITranscription` | Whisper transcription and lyric alignment |
| `LyricAligner` | Word-level alignment of the user's lyrics to the words Whisper heard |
| `SerbianText` | Serbian text normalization (Latin/Cyrillic, diacritics) and word-start matching |
| `SceneReviewDialog` | Accessible scene review: what each clip shows, replace any clip |
| `TimelineLayout` / `ExportHelpers` | Shared timeline layout (parallel tracks, pinned clips); audio-only and WebM/AVI export; cutting highlight segments |
| `AppPaths` / `KeyStore` / `AppLog` | Where FFmpeg and the app folders are; encrypted key storage; the error log file |
| `YouTubeDownloadDialog` | Accessible YouTube import (yt-dlp), playlist scope and track selection |
| `UpdateChecker` | Checks GitHub Releases for new `video-v*` versions |
| `LanguageManager` | All UI text in English, German and Serbian |
| `XamlTranslator` / `UiTranslations` | Translates the texts written in the XAML of every window (and the screen-reader names) into the interface language |
| `ProcessExtensions` | Safe handling of external processes (FFmpeg, Whisper): no stalls on full output buffers, killed on cancel |

---

## Configuration

Everything is configured directly in code. Key parameters:

```csharp
// AIVideoCreator.xaml.cs
const int QUERY_COOLDOWN_SCENES = 4;     // Anti-repetition of themes

// BeatDetection.cs
double baseDuration = 4.5 - NoteDensity * 2.7; // Piano pacing: 1.8-4.5s

// RenderEngine.cs
// Pacing-aware fade durations:
"fast"     => 0.25s
"standard" => 0.50s
"slow"     => 0.70s

// hqdn3d=1.5:1.5:6:6  — denoise
// unsharp=3:3:0.4      — sharpening
```

---

## Stability & Diagnostics

The app has a global handler for unexpected errors (`App.xaml.cs`), at three levels:

- `DispatcherUnhandledException`: errors on the main (UI) thread. The user sees a message and the app **keeps running** instead of silently closing. This matters especially for screen reader users, for whom a silent close gives no signal at all that something happened.
- `AppDomain.CurrentDomain.UnhandledException`: more serious errors outside the main thread.
- `TaskScheduler.UnobservedTaskException`: errors from "fire and forget" async tasks that nobody awaited, which would otherwise vanish without a trace.

External tools (FFmpeg, Whisper, yt-dlp) are always run with their output read, so long operations cannot stall on a full output buffer. Cancelling an operation also stops the external process and its child processes.

Since 1.4 every error the program used to ignore silently is written to a daily log file, and the details of a failed FFmpeg render step are kept too:

```
%LOCALAPPDATA%\UltraVideoEditor\Logs\ultra_yyyy-MM-dd.log
%LOCALAPPDATA%\UltraVideoEditor\Logs\render_last_error.log
```

*Help → Open the log folder* opens this folder. The same message within 30 seconds is written only once, so errors inside loops cannot flood the log.

`RenderEngine.cs` additionally logs rare, non-critical errors during processing (filter string building, the post-processing file swap, temp folder cleanup, FFmpeg duration parsing) to:

```
%APPDATA%\UltraVideoEditor\render_errors.log
```

---

## Log Messages (reference)

```
🥁 Beat detection: 120 BPM, 148 beats, confidence=0.72
🎹 Piano mode active: 12 melodic phrases, density=0.43 → 3.3s average
🗓 Season: global=spring, per-line=winter → ✅ Season changed to: winter
🏷 LyricTag: Action | Sentiment: Positive | CloseUp: True
🤖 Ollama query: 'child running park joy sunlight'
😊 Smile bonus +1.5 (sentiment=Positive): 6.0 → 7.5
🎬 Shot composition: two consecutive 'medium' — looking for another type...
📐 PATCH9 30% Rule: wide shot without children — looking for medium/close shot...
🔄 Cooldown variant (theme 'children stream' was scene 3): '...'
🎭 Visual identity: {"main_character":"little girl with pigtails","setting":"sunny park","palette":"warm golden"}
🎬 Director query: 'girl eating ice cream park'
🔤 Lyrics aligned: 24 lines, 131 words, matched 92%
🔊 Mixing ambient sounds: 24 scenes → 7 continuous regions, gain=0.20, offset=2.10s
🎤 Karaoke subtitles rendered (each word lights up as it is sung)
✅ Score 7.5/10 [Qwen] | Motion:Right | Shot:medium | Season:winter | Children:True Smile:True
✨ Cross-dissolve: 45 clips, avg fade 0.50s (pacing-aware)
```

---

## Known Limitations

- **Pixabay pool**: For long songs (3+ minutes), the deduplication pool can run dry for recurring query themes. The system uses a cooldown variant as mitigation.
- **Beat detection on piano**: Piano mode relies on energy flux detection. For solo piano without accompaniment, it may generate fewer phrase boundaries than optimal.
- **GPU encoder**: Uses `h264_nvenc` (NVIDIA). On systems without an NVIDIA GPU, it automatically falls back to `libx264`.
- **Preview**: the preview player plays one clip at a time (from its in-point). The full mix of tracks, transitions and subtitles is only seen in the rendered video.
- **Some older dialogs** (e.g. *Set image position*, the keyboard help) still have English-only text.
- **YouTube import**: YouTube changes often. The app updates its own copy of yt-dlp weekly, but a download can occasionally fail until yt-dlp publishes a fix.

---

## Development

The project is active. The full roadmap to version 2.0 (in Serbian) is in [PLAN_RAZVOJA.md](PLAN_RAZVOJA.md). The main steps:

- **Stability:** automated tests (timeline layout, render commands, lyric alignment), no silently ignored errors, autosave and crash recovery, portable projects (relative media paths).
- **Product:** first-run wizard and settings window (FFmpeg, Whisper, Ollama models, API keys), smaller installer with optional sound packs, signed installer, complete translation, user manual, third-party licence and credits file.
- **Big step:** audio mix preview, fast low-resolution timeline preview, a graphical timeline for sighted users that stays in sync with the accessible list.
- **Unique features:** a described timeline (AI tells a blind editor what is in each shot), audio description of the finished video for blind viewers, voice commands, pre-publish checks (subtitle readability, flashing, loudness, licences).

---

## Author

Created by **Demir Ajvazi**.

## Support this project

Ultra Video Editor is free and open source, built by a blind developer so that blind and sighted people can edit video equally. If it helps you, you can support further development:

- [GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max)
- [Ko-fi](https://ko-fi.com/ultracreativesuite)
- [PayPal](https://paypal.me/demirajvazi10)

The same links are in the program under *Help → About*.

## License

Licensed under the **GNU General Public License v3.0**. See [LICENSE](LICENSE).
