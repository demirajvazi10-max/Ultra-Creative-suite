# Ultra Composer

Part of the Ultra Creative Suite. An accessible, keyboard-first alternative to
FL Studio — built as a professional-grade DAW architecture from day one, not
a simplified prototype.

## Author

Demir Ajvazi

## Status

Proof that the real architecture works end to end, with the bigger DAW
features added on top of it one at a time.

What it does right now:

- Starts a native, real-time audio engine (C++, PortAudio) from the WPF app
- Lets you **play notes on your computer keyboard**, like FL Studio's
  typing-keyboard-to-piano feature
- Lets you **play notes from a MIDI keyboard** (e.g. a Yamaha P-125 or any
  other class-compliant USB-MIDI device), via RtMidi - see "MIDI hardware
  input" below
- Plays a simple polyphonic synth voice (sine wave with a short attack/release
  envelope) so you can hear what you play, up to 32 notes at once, from
  either input at the same time
- **Hosts a VST3 instrument plug-in** alongside the built-in synth, using
  Steinberg's own VST3 SDK hosting classes - see "VST3 instrument hosting"
  below
- **Plays back a simple, single-track sequence of notes in a loop** - a
  first, list-based slice of piano-roll-style sequencing - see "Sequencer"
  below
- **Hosts a SoundFont (.sf2) instrument bank** alongside the synth and any
  loaded VST3 instrument, with a real bundled default bank that works with
  zero setup - see "SoundFont instrument banks" below
- **Automatic third harmonization** - a toggle that adds a second note (a
  third above or below, your choice) to every note played, from any input
  at all - see "Auto-third harmonization" below
- **Arranger** - chain saved sequencer patterns into one ordered
  arrangement (verse, verse, chorus, verse, ...) - see "Arranger" below
- A **menu bar** (Fajl/Prikaz/Pomoć) that switches between three focused
  views - Banka instrumenata, Sekvenser, Aranžer - see "Menu and views"
  below
- **Fully bilingual UI (Serbian/English)**, switchable at runtime with no
  restart - see "Language" below
- **A piano roll** - an optional, purely visual grid view of the
  sequencer's notes, alongside (not instead of) the JAWS-friendly list -
  see "Piano roll" below
- **Multi-track sequencing + mixer** - several independent tracks playing
  together, each with its own note pattern, its own instrument (built-in
  synth or its own SoundFont bank/preset), and its own volume/pan/mute/solo
  - see "Multi-track & mixer" below
- **Export to WAV** - render the current multi-track mix down to a plain
  16-bit stereo `.wav` file, e.g. to bring into Ultra Audio Editor for
  further mixing with vocals - see "Multi-track & mixer" below
- **Auto-accompaniment** - pick a built-in rhythm/style and it plays drums,
  bass, kontra and harmony on its own, automatically following whatever
  chord you play (left hand on the keyboard, or picked manually) - see
  "Auto-accompaniment" below
- **Project save/load** - "Fajl" menu → "Sačuvaj projekat"/"Sačuvaj kao..."
  writes the complete project state to a `.adem` file, and "Otvori
  projekat..." reopens it exactly as it was left - see "Project save/load"
  below
- **In-app User Guide** - "Pomoć" → "Uputstvo za upotrebu", a detailed,
  task-oriented reference for every feature above, plus a bilingual
  first-run guide offered right after installing - see "User Guide" below
- Is fully operable and announced through JAWS (see Accessibility below)

What it does **not** do yet (next milestone):

1. "Demix" (AI source separation - splitting vocals from instrumentals),
   mirroring the existing feature in Ultra Audio Editor - still just an
   idea being considered, not committed to yet

## MIDI hardware input

- On startup (and whenever you click "Osveži uređaje"), the app lists every
  MIDI input port the system currently sees.
- If exactly one is found, it connects automatically - no extra step needed
  each time you plug in the P-125.
- If more than one is found, pick one from the dropdown and click "Poveži".
- Computer-keyboard playing and a connected MIDI device both feed the same
  synth at the same time - you can use either, or both.
- `MidiInput` gracefully handles a machine with no working MIDI subsystem at
  all (reports zero ports instead of crashing), and note events from the
  MIDI thread and the UI thread are protected by a small mutex around the
  synth's voice pool (see the comment in `Synth.h`) so the two can't race
  with each other or with the audio callback.

### Audio latency

- The audio callback runs a 256-sample buffer at 44100Hz (~5.8ms of
  buffering on its own), but the real end-to-end latency you feel when
  pressing a key also depends on which Windows audio backend PortAudio
  actually opens the output stream through.
- `AudioEngine::Start()` now explicitly asks for the WASAPI host API in
  *shared* mode first (falling back to PortAudio's own default choice only
  if WASAPI isn't available or fails to open). This matters because
  PortAudio's own "default" pick on Windows is often the decades-old MME
  backend, which commonly adds 100ms+ of latency on top of the buffer
  above - WASAPI shared mode brings the real-world round-trip down to
  roughly the buffer size itself, i.e. low single-digit milliseconds,
  which should not be perceptible as "lag" when playing.
- Deliberately not using WASAPI *exclusive* mode: exclusive mode would grab
  the output device away from every other application for as long as the
  engine is running, which could include JAWS's own speech output if it
  happens to share the same device - trading a few more milliseconds of
  latency for silenced speech would be a bad trade, so this stays on
  shared mode.
- If it's still audibly laggy on your machine after this change, the next
  thing to check is Windows' own default *format* for the output device
  (Sound settings → your device → Properties → Advanced) - a mismatched
  sample rate there can force an extra resampling step outside this app's
  control.

## VST3 instrument hosting

- Type or paste the path to a `.vst3` file into the box (or use
  "Pretraži..." to browse to it - both the classic single-file form and
  the newer bundle-folder form are supported), then click "Učitaj
  instrument".
- The first instrument class found inside is loaded and takes over playing
  from the computer keyboard, a connected MIDI device, and the sequencer,
  all at the same time (the built-in synth is only a fallback for when
  nothing else is loaded - see "Built-in synth vs. real instruments"
  below).
- "Isključi instrument" unloads it again (the built-in synth resumes
  playing on its own).
- Hosting itself is built entirely on Steinberg's own official VST3 SDK
  hosting helpers (module loading, plug provider, process data, etc. -
  see `src/AudioEngine/VstHost.cpp`), not a hand-rolled reimplementation of
  the VST3 interfaces.
- Not yet done: showing the plug-in's own editor/GUI window, and a UI for
  its parameters - both wait for VST3 GUI hosting, a later milestone.

## Sequencer

A first, deliberately simple slice of automatic note playback - a plain
list of notes rather than a visual piano-roll grid, so it's fully usable
with JAWS from day one:

- Add a note (or a whole chord) with a general/solfege note name - the
  naming taught via Braille music notation (`c`, `cis`, `d`, `dis`/`es`,
  `e`, `f`, `fis`, `g`, `gis`/`as`, `a`, `ais`/`b`, `h`) followed by an
  octave number, e.g. `c4` for middle C, `cis4` for the black key between
  c4 and d4. Note: `h` is the natural B, and `b` is B-flat - not English's
  "B" (a plain letter-plus-accidental spelling like `C4`/`C#4`/`Db4` is
  also accepted, for anyone who prefers it; a plain MIDI number 0-127
  works too, and this always works regardless of the interface language -
  see "Language" below) - plus a start time and length (both in beats, not seconds -
  they automatically follow whatever tempo you set) and a velocity
  (1-127), then click "Dodaj notu/akord". To add a chord in one click,
  separate several note names with commas or spaces (e.g. `c4, e4, g4`) -
  they're all added starting at the same beat.
- To enter a chord by shape instead of typing note names, click "Slušaj
  akord", then play it - one or more keys at once, held together, on
  either the computer keyboard or a connected MIDI device - and let go of
  every key. The played note(s) are added as one chord automatically, at
  whatever start beat is currently set, using each note's own actual
  velocity (how hard it was played) rather than the Jačina box. After each
  capture, the start beat advances by the current length, so playing a
  progression one chord (or one note) at a time - like building up a
  pattern on a home keyboard/arranger - lands each one right after the
  last without retyping the start beat in between. Click "Prestani da
  slušaš" when done; only listens for genuinely live playing, never for
  whatever the Sekvenser/Aranžer/Multi-trake happen to be playing back at
  the same time - but while armed, it does add whatever you play, so it's
  worth remembering to turn it off before just playing normally.
- The list shows every note added so far; select one (Tab into the list,
  then arrow keys) and click "Ukloni izabranu notu" to remove it, or
  "Obriši sve note" to clear the whole pattern.
- Set the tempo (BPM) and how many beats the pattern loops after, then
  click "Sviraj" to start playback (always from the beginning, looping
  forever) or "Zaustavi" to stop.
- Sequenced notes feed both the built-in synth and a loaded VST3
  instrument, exactly like the computer keyboard and MIDI input do.
- The note names shown in the list (and typed in the note field) switch
  between the general/solfege naming and English letter names depending on
  the interface language - see "Language" below.
- Saving a pattern to disk on its own isn't a separate action - it's part
  of the whole-project save described in "Project save/load" below.
  Multiple tracks with per-track instrument routing now exist too, as a
  separate view - see "Multi-track & mixer" below; this plain sequencer
  stays as it is (a single, always-available quick pattern to sketch in or
  feed the Arranger).

## Piano roll

An optional, purely visual grid view of the same notes as the list above -
not a replacement for it. The list stays the primary, JAWS-friendly way to
enter and edit notes; the piano roll is for anyone who wants (or is
helping you) to see the notes laid out visually, e.g. a sighted
collaborator, or just to sanity-check a pattern's shape at a glance.

- Off by default (a large, purely visual canvas has nothing useful to
  announce, so it stays out of the Tab order until turned on). Check
  "Prikaži piano roll" in the Sekvenser view to show it.
- Time runs left to right (one column per beat, with a slightly darker
  line every 4 beats), pitch runs bottom to top (one row per semitone,
  black-key rows shaded gray, with a darker line at every C) - the same
  layout convention as FL Studio's and most other DAWs' piano rolls.
- **Left-click** an empty cell to add a note there, using whatever
  length/velocity are currently set in the boxes above the list.
  **Right-click** an existing note to remove it. Left-click an existing
  note to select it (highlighted) without adding a duplicate on top.
- Fully two-way with the note list: adding/removing a note either place
  updates both views immediately, since they're both just showing
  `_sequencerNotes` (the same underlying data the plain sequencer plays).
- The visible pitch range is C2-C7 (5 octaves) - a note outside that range
  is still perfectly usable, it just won't be drawn; enter/edit it through
  the list instead.
- Not yet done: resizing a note's length by dragging its edge (add/remove
  only, for now - change length through the boxes above or the list), and
  scrolling/zooming beyond the fixed grid size.

## SoundFont instrument banks

Real, ready-made instruments (piano, strings, drums, guitar, etc.) loaded
from a `.sf2` SoundFont bank - the same kind of file Cakewalk and most
other DAWs use for their built-in sound sets. Hosting is built on
TinySoundFont, a small, well-tested SoundFont synthesizer.

- The "Zvučna banka (SoundFont)" box already has a path filled in to a
  **bundled default bank** (GeneralUser GS, ~30 MB, 287 instruments)
  installed right next to the app - click "Učitaj banku" and it just
  works, no downloading or setup required.
- To use a different bank instead (your own `.sf2` file, downloaded or
  from another program), type/paste its path or use "Pretraži..." to
  browse to it, then click "Učitaj banku" - this replaces whichever bank
  was loaded before.
- Once a bank is loaded, the "Instrument" dropdown lists every instrument
  (preset) inside it - pick one to switch instruments; switching cleanly
  stops any notes still sounding from the previous instrument first, so
  nothing gets stuck.
- The loaded bank takes over playing from the computer keyboard, a MIDI
  device, and the sequencer (the built-in synth is muted while it's
  loaded - see "Built-in synth vs. real instruments" below); if a VST3
  instrument is also loaded at the same time, the two mix together.
- "Isključi banku" unloads it again (the built-in synth resumes playing on
  its own, unless a VST3 instrument is still loaded).
- While the performance keyboard region has focus, `↑`/`↓` (arrow up/down)
  switch to the next/previous instrument in the loaded bank, without
  needing to tab away to the "Instrument" dropdown - handy for trying
  sounds while actually playing. Does nothing if no bank is loaded.
- Not yet done: saving which bank/instrument was last selected between
  runs (today it always starts back on the bundled default).

## Built-in synth vs. real instruments

The built-in sine synth is only meant as a fallback so something is
audible before any real instrument is loaded - it's automatically muted
the moment a VST3 instrument or a SoundFont bank is loaded (loading both
at once mixes the two real instruments together, still without the
built-in synth). Unloading everything brings the built-in synth back.

Whatever is currently playing also passes through a soft limiter (a
`tanh()` curve on the final mixed output) before reaching the audio
device. A single note stays essentially untouched, but a chord - or even
one note, if a SoundFont instrument layers several sample regions per
key/velocity - that would otherwise add up past full volume gets bent
back smoothly instead of harshly clipping (heard as crackling/distortion
before this was added).

## Auto-third harmonization

A quick way to get two-part harmony without a second hand, a second MIDI
note, or a second sequencer track:

- In the "Banka instrumenata" view, check "Uključi automatsku tercu" and
  pick "Gornja terca" (a third above) or "Donja terca" (a third below).
- While it's on, **every** note played anywhere - the computer keyboard,
  a connected MIDI device, the sequencer, and the arranger - automatically
  triggers a second note a third away, at the same time, with the same
  velocity.
- The third is diatonically correct, not a fixed major third everywhere:
  pick the scale (osnovni ton + Dur/Mol/Hidžaz/Hidžaz kar) right below the
  direction radio buttons, and the harmony note uses whatever interval that
  scale's degree actually calls for - e.g. in C major the third above D is
  F (a MINOR third), matching real harmony instead of always adding 4
  semitones. **Hidžaz** and **Hidžaz kar** are the two "oriental"-flavored
  options - see "Oriental sounds" below. A note that doesn't belong to the
  chosen scale at all (a chromatic/"blue" note) still gets a plain fixed
  major third, since there's no single correct diatonic answer for it.
  Defaults to C major.
- This is implemented once, at the single point every note source already
  passes through inside the native engine (`AudioEngine::NoteOn`/`NoteOff`),
  so it's genuinely independent of whether MIDI is connected - the setting
  applies equally to computer-keyboard playing, a Yamaha/MIDI keyboard, the
  sequencer, and the arranger, with no separate wiring needed per input.
- If the harmony note would fall outside the valid MIDI range (0-127) -
  e.g. asking for a third above a note already near the very top of the
  keyboard - that one harmony note is silently skipped rather than clamped
  to a wrong pitch; the note you actually played always still sounds.
- Off by default; the checkbox, direction, and scale are remembered only
  for the current run (not yet saved between sessions).

## Arranger

A first slice of arranger-style structure: chain multiple saved patterns
into one ordered arrangement (verse, verse, chorus, verse, ...), building
on the plain Sequencer rather than replacing it:

1. Compose a pattern in the Sekvenser view as usual (notes/chords, tempo,
   loop length).
2. Switch to the Aranžer view, type a name, and click "Sačuvaj trenutni
   sekvenser kao obrazac" - this takes a snapshot of the sequencer's
   current notes and loop length and stores it under that name. The
   sequencer and the saved obrazac (pattern) are independent after that -
   editing the sequencer further doesn't change a pattern already saved.
3. Select a saved pattern and click "Dodaj u redosled" to append it to the
   play order (the same pattern can be added more than once - e.g. verse,
   verse, chorus).
4. Set a tempo and click "Sviraj aranžman". Playback goes through the
   order from the top, looping the whole arrangement once it reaches the
   end, and a live-region status line reports which position/pattern is
   currently playing.
5. "Ukloni poslednji iz redosleda" / "Obriši ceo redosled" edit the play
   order; "Ukloni izabrani obrazac" deletes a saved pattern entirely
   (references to it in the order are dropped automatically).
- Playing the arranger stops the plain sequencer, and starting the plain
  sequencer stops the arranger - both feed the same instruments, so only
  one is ever actually advancing at a time (enforced in the native engine
  itself, not just in the UI, so it holds no matter which button is
  clicked).
- At every boundary between one pattern and the next (and at the end of
  the whole arrangement, looping back to the start), any note still
  sounding is cut off first - so a held note from one pattern never bleeds
  into the next pattern's context.
- Not yet done: reordering an existing entry in the middle of the play
  order (today you can only append to the end or remove the last one),
  and saving patterns/arrangements to disk between runs.

## Multi-track & mixer

Several independent tracks, each with its own note pattern and its own
instrument, playing together as one song - the multi-track sequencing and
mixer that were deliberately built as one combined feature, since a mixer
has nothing to mix without more than one track:

1. In the "Multi-trake i mikser" view, type a name and click "Dodaj traku"
   to add a track. Add as many as you like.
2. Select a track from the list to edit it in the "Izabrana traka" panel:
   rename it, pick its instrument (the built-in synth, "SoundFont banka"
   with its own independently loaded `.sf2` bank and instrument, or "VST3"
   with its own independently loaded `.vst3` plug-in - loading a bank or a
   plug-in for a track automatically switches that track to the matching
   mode), set its volume (0-1.5) and pan (-1 left to 1 right) and click
   "Primeni jačinu i pan", and check "Isključi zvuk (mute)" or "Solo" as
   needed.
3. Enter that track's own notes/chords exactly like the plain Sequencer
   (same note-name syntax, same start/length/velocity fields, same list,
   same "Slušaj akord" chord-by-shape entry - see "Sequencer" above) -
   each track has its own independent note list. Only one track (and only
   one of the Sekvenser/Multi-trake views) can have "Slušaj akord" armed at
   a time; arming it on a track fixes which track it inserts into for as
   long as it stays armed, even if you select a different track in the
   list meanwhile.
4. Tempo and loop length are shared across every track (they're playing one
   song together, not independent loops) - set them once at the bottom and
   click "Sviraj sve trake" to start every track's transport together, or
   "Zaustavi" to stop.
5. "Izvezi kao WAV..." renders the current mix (respecting every track's
   volume/pan/mute/solo) offline for the given duration and saves it as a
   plain 16-bit stereo `.wav` file - meant for bringing a finished
   composition into **Ultra Audio Editor** for further mixing, e.g. with a
   recorded vocal. WAV rather than a compressed format on purpose: no lossy
   step before you've even finished mixing.
- While any track is soloed, only soloed tracks are heard (their own mute
  is ignored); with nothing soloed, every unmuted track plays.
- Each SoundFont-mode or VST3-mode track loads its own independent copy of
  its bank or plug-in (even if two tracks point at the same file), so
  tracks can each sound completely different at the same time - unlike the
  single shared SoundFont bank/VST3 instrument in "Banka instrumenata"
  (also used by the plain Sequencer/Arranger), which only ever plays one
  instrument at a time across the whole app.
- Playing multi-track playback stops the plain Sequencer and the Arranger,
  and starting either of those stops multi-track playback - all three
  share one transport, so only one is ever actually advancing (enforced in
  the native engine, not just the UI).
- Exporting briefly stops and restarts live audio playback if it was
  running, so the offline render can't race with it - anything being
  monitored live (e.g. a MIDI keyboard played at that exact moment) goes
  silent for that short window.
- Not yet done: reordering tracks, and MP3 export (WAV was chosen
  deliberately - see above).

## Auto-accompaniment

A built-in rhythm/style bank, like a home keyboard's auto-accompaniment:
pick a style and it plays a full backing - drums, bass, "kontra" (rhythmic
chord stabs) and a sustained harmony pad - that automatically follows
whatever chord is currently active, so you only have to play the melody
(or the chord itself) yourself.

1. In the "Auto-pratnja" view, pick a rhythm from the "Ritam" list - the
   starter bank ships with ten: **Pop**, **Rok**, **Balada**, **Valcer**
   (a 3/4 waltz), **Latino**, **Sving**, **Regi** (reggae), **Fanki**
   (funk), **Bluz** and **Orijentalni** (a Maqsum-style darbuka/dumbek
   pattern - see "Oriental sounds" below). Selecting one adopts its own
   suggested tempo automatically (still freely adjustable afterwards).
2. Check/uncheck **Bubnjevi** (drums), **Bas**, **Kontra** and **Harmonija**
   to turn each layer on or off independently, at any time, even while
   playing.
3. Choose how the accompaniment learns which chord to play:
   - **Automatski** - play the chord's actual notes (any voicing, any
     inversion - the lowest note held is read as the root) below a
     configurable split point (default C4/c4, set it in "Granica podele").
     Notes below the split are absorbed into chord detection instead of
     sounding as an ordinary note, exactly like a real keyboard's left-hand
     accompaniment zone; notes above the split play completely normally.
     Releasing every held note keeps the last chord sounding rather than
     going silent, so a brief break between chords doesn't cut the backing.
   - **Ručno** - pick a root note and a chord type (dur/mol/septakord/
     umanjen) directly and click "Primeni akord"; the split zone plays
     normally in this mode.
   - The chord quality is detected from the actual intervals played (a
     minor third → mol, a major third plus a minor seventh → septakord,
     and so on) rather than a fixed keyboard convention, since it maps
     directly onto real chord theory instead of an extra code to memorize.
     A single note by itself defaults to major.
4. Click "Sviraj pratnju" to start, "Zaustavi" to stop. The accompaniment
   runs independently of the Sequencer/Arranger/Multi-track transports - it
   deliberately does **not** stop them, and they don't stop it, since it's
   meant to play underneath live keyboard/MIDI performance (or alongside a
   programmed part, if you want both).
5. Whenever the current chord changes - whether from playing a new chord in
   the auto zone or applying a manual one - every layer that has a note
   already sustaining is re-voiced to the new chord immediately, rather than
   waiting for that layer's next scheduled hit. Some styles hold a single
   bass/kontra/harmonija note for a whole bar, so without this a chord
   change could otherwise go unheard for a few seconds.
- Every percussion sound (kick, snare, closed/open hi-hat, clap, crash) is
  synthesized procedurally (a pitched sine sweep for the kick's thump,
  filtered noise for everything else) rather than sampled - deliberately
  dependency-free, and it means the drums always sound regardless of which
  SoundFont bank (if any) happens to be loaded elsewhere in the app.
- **Instrument per layer**: under "Instrumenti pratnje", pick which of
  bass/kontra/harmonija to edit from the "Sloj" list, then either load a
  .sf2 file just for that layer and pick one of its instruments, load a
  .vst3 instrument just for that layer, or switch back to "Ugrađeni
  sintisajzer". Each layer's SoundFont/VST3 is its own independent load
  (even if it's the same file as another layer, or the same file already
  loaded in "Banka instrumenata"), which is what lets bass/kontra/harmonija
  each sound a different real instrument at once. The "Sloj" list has a
  fourth entry, "Komplet", for loading one .sf2 file into all three layers
  at once instead of doing it three separate times - type or browse to a
  bank and click "Učitaj banku za sve slojeve", and it guesses a sensible
  starting instrument for each layer by name (something with "bass" in it
  for the bass layer, and so on); "Komplet" is SoundFont-only, so a VST3
  instrument still has to be loaded per layer individually.
- **Chord density (gustina akorda)**: under "Gustina akorda", pick how
  many chord tones the kontra and harmonija layers actually sound -
  "Kako je ritam napisao" keeps each style's own authored choice (the
  default, unchanged from before); "Trozvuk" always forces a plain
  root/third/fifth no matter what the style wrote; "Četvorozvuk" always
  adds a fourth (seventh/color) tone. Never affects bass - a bass
  pattern's chord-tone reference picks WHICH single note is the bass note
  (root vs fifth, say), not a stack of tones, so this selector leaves it
  alone. Takes effect immediately, including re-voicing whatever is
  already sustaining, the same way a chord change does.
- **Custom rhythm ("Sopstveni ritam")**: build your own rhythm/style
  entirely from the UI instead of only picking from the built-in bank.
  Under "Sopstveni ritam" (further down the Auto-pratnja view), build up
  one bar ("takt") at a time:
  1. Set the bar's length in beats, then add drum hits (pick a sound -
     **bas bubanj**, **doboš**, closed/open hi-hat, **pljesak**, **cinela**,
     **tom** (a pitched tom-tom, a stand-in for a timpani-style accent), or
     **Dumbek - Dum/Tek** (the two core darbuka/dumbek strokes - see
     "Oriental sounds" below), a beat position, and a velocity) and/or
     bass/kontra/harmonija hits
     (pick the layer, a start beat and length, which chord tone(s) it
     sounds - root/third/fifth/seventh - an octave offset, and a velocity).
     Both lists work exactly like the Sequencer's note list: add one at a
     time, remove the selected one, or clear them all.
  2. Click "Dodaj kao novi takt" once the bar sounds the way you want -
     it's added to the rhythm and the editor clears, ready for the next
     bar. Add as many bars as you like this way; they play back-to-back in
     the order you added them, looping the whole chain, so additional bars
     are how you build variations (start simple with a couple of drum
     hits, then add a bar with cymbals or a bass line layered in, and so
     on) - the same idea as building up a song one bar at a time on an
     arranger keyboard. "Ukloni poslednji takt"/"Obriši sve taktove" undo
     the last bar or start over.
  3. Name the rhythm and click "Sačuvaj ritam" - it's installed right next
     to the built-in styles in the "Ritam" list and selected immediately,
     ready to play with "Sviraj pratnju" like any other style.
  4. Any number of custom rhythms can exist side by side. Click "Novi
     ritam" to clear the bar editor and start building a completely
     different one from scratch - the ones you already saved aren't
     touched. Every saved custom rhythm shows up in the "Sačuvani sopstveni
     ritmovi" list below the editor: select one and click "Uredi izabrani"
     to load its bars back into the editor for further changes (clicking
     "Sačuvaj ritam" again then updates that same rhythm in place, instead
     of adding a duplicate), or "Ukloni izabrani ritam" to delete it.
- Not yet done: more built-in styles beyond the current ten (custom
  rhythms built via the UI are a separate, complementary way to get more
  variety). Saving a project (see "Project save/load" below) persists
  every saved custom rhythm's full bar-by-bar editable state between
  sessions - whatever's still sitting unsaved in the bar editor at the
  time isn't included, the same as any other in-progress, not-yet-added
  draft elsewhere in the app.

## Oriental sounds

A first pass at an "oriental" flavor, pulled together from three angles
rather than one single feature - scale, rhythm, and instrument sound:

- **Scale**: "Banka instrumenata" → "Uključi automatsku tercu" now offers
  **Hidžaz** (1 b2 3 4 5 b6 b7 - Phrygian dominant/"Freygish") and
  **Hidžaz kar** (1 b2 3 4 5 b6 7 - double harmonic major/"Byzantine")
  alongside the existing Dur/Mol, so the auto-third harmony (see
  "Auto-third harmonization" above) can add a diatonically-correct second
  voice in an oriental scale instead of only a Western major/minor one.
  Picking a scale here also governs any other place in the app that reads
  a diatonic scale degree.
- **Rhythm**: the built-in style bank (see "Auto-accompaniment" above)
  gained a tenth style, **Orijentalni** - a Maqsum-style darbuka/dumbek
  skeleton (the classic "Dum-tek-tek Dum-tek" feel) with a light hi-hat
  layer on top. The same two darbuka/dumbek strokes it's built from,
  **Dumbek - Dum** (the low, resonant open stroke) and **Dumbek - Tek**
  (the high, crisp rim stroke), are also selectable individually in
  "Sopstveni ritam" (custom rhythm), so you can build your own oriental
  (or oriental-flavored) rhythm bar by bar the same way as any other
  custom rhythm.
- **Instrument sound**: no new code is needed for the actual timbre of an
  oud, ney, kanun, or darbuka - "Banka instrumenata", each multi-track
  track, and each auto-accompaniment layer already load any `.sf2`
  SoundFont bank or `.vst3` plug-in you point them at (see "SoundFont
  instrument banks" and "VST3 instrument hosting" below), so loading a
  free oriental-instrument SoundFont/VST3 there is the way to get real
  oriental timbres, not just oriental scales/rhythm. Good free banks to
  try for this are still being looked into.
- What this deliberately is **not**: true microtonal tuning. Hidžaz and
  Hidžaz kar above are built in the ordinary 12-tone equal-tempered
  system already used everywhere else in the app (every note still lands
  on a normal MIDI semitone) - they get the oriental scale *shape*
  right, but not the smaller-than-a-semitone pitch inflections (e.g. a
  "neutral" second/third) that some real maqam performance uses. True
  microtonal support would be a separate, considerably bigger change
  (retuning the whole pitch system, not just picking which scale degrees
  are used) and isn't part of this pass.

## Menu and views

A menu bar (Fajl/Prikaz/Pomoć) replaces the earlier single long scrolling
page. **Prikaz** switches which view is showing:

- **Banka instrumenata** - MIDI connection, VST3, SoundFont, auto-third,
  and the performance keyboard. Everything about *what* plays.
- **Sekvenser** - the note list and its own playback controls.
- **Aranžer** - saved patterns and the play order.
- **Multi-trake i mikser** - several tracks, each with its own instrument,
  notes, volume and pan, plus WAV export - see "Multi-track & mixer" above.
- **Auto-pratnja** - rhythm/style picker, layer toggles, and chord input -
  see "Auto-accompaniment" above.

Only one view's controls are visible/reachable by Tab at a time, which
keeps each screen short and focused instead of one long page to navigate
through with JAWS. Switching views never resets or reloads anything: the
loaded VST3/SoundFont instrument, the MIDI connection, the selected
preset, and the auto-third setting all live in the native engine itself,
not in any one view, so whatever you pick in "Banka instrumenata" is what
plays no matter which view is currently showing. **Fajl** also has
**Otvori projekat...**, **Sačuvaj projekat**, and **Sačuvaj kao...** (see
"Project save/load" below) before **Izlaz**, which closes the app;
**Pomoć → Uputstvo za upotrebu** opens the full in-app user guide (see
"User Guide" below), and **Pomoć → O programu** shows the author/license
credit.

## User Guide

A second, non-modal window (**Pomoć → Uputstvo za upotrebu** / **Help →
User Guide**) with detailed, task-oriented instructions for every feature -
what each button does, not just what it's called. Deliberately separate
from this README: written for someone actually using the app day to day,
in whichever language (Serbian/English) is currently selected, rather than
this file's more architecture-flavored explanations for developers.

- A `ListBox` of topics on the left (one per major feature area - keyboard/
  MIDI, SoundFont/VST3, auto-third, Sequencer, Arranger, Multi-track &
  mixer, Auto-accompaniment, project save/load, language) and a read-only,
  scrollable text box on the right showing whichever topic is selected -
  arrow through the topic list, then Tab into the text box to read it at
  your own pace with normal JAWS reading commands.
- The topic title is a live region, so arrowing through the list alone
  still gives a lightweight spoken confirmation of which topic is now
  selected, even before you Tab into the full text.
- Content lives in `HelpContent.cs`, kept separate from `Localization.cs`
  since these are long, topic-sized blocks rather than short UI labels.
  Picks up whichever language is current at the moment the window opens;
  switching language while it's already open doesn't retranslate it live -
  close and reopen it to see the new language, same as most of the rest of
  the app's one-shot-localized content.
- Opening it twice (e.g. clicking the menu item again) brings the existing
  window to the front instead of opening a second copy.

A separate, shorter `docs/GettingStarted.txt` (bilingual, plain text) ships
with the installer and is offered on the installer's Finished page - see
"Releasing" below - as a first-run orientation that also points back to
this in-app guide for the full detail.

## Project save/load

**Fajl → Sačuvaj projekat** (or **Sačuvaj kao...** the first time, or to
pick a new file) writes the entire project to a `.adem` file - a plain
JSON file, so it's readable/diagnosable if something ever looks wrong,
just saved with a different extension. **Fajl → Otvori projekat...**
reopens one, restoring everything exactly as it was left:

- The Sequencer's notes, tempo, and loop length.
- Every Multi-track track: its notes, name, volume, pan, mute/solo, and
  whichever instrument it was routed to (built-in synth or a specific
  loaded SoundFont bank/preset).
- Every Arranger pattern and the play order built from them.
- The whole Auto-accompaniment state: which style was selected (including
  a custom rhythm, if one was active), tempo, which of the four layers
  (bubanj/bas/kontra/harmonija) are enabled, manual vs. keyboard-follow
  chord input and the manually picked chord, chord voicing, the split
  point, and each layer's own loaded SoundFont/VST3 instrument. Every
  custom rhythm saved in "Sopstveni ritam" is saved bar by bar, still
  editable after reopening - not just as the flattened result installed
  into the engine (whatever's still unsaved in the bar editor itself is
  not included - see "Auto-accompaniment" above).
- The auto-third setting (on/off, upper/lower, scale).
- The shared instrument loaded in "Banka instrumenata" for live
  keyboard/MIDI playing (SoundFont bank/preset or VST3).

Nothing persists between runs on its own outside of a saved `.adem` file -
closing the app without saving loses whatever changed since the last
save, same as any other document-based program.

## Language

The whole UI - every menu, label, button, status message, and screen-reader
name/help text - is available in Serbian and English, switchable instantly
from the **Jezik/Language** menu, no restart needed:

- Every piece of UI text lives in one small bilingual table
  (`src/App/Localization.cs`) rather than in `.resx` satellite resource
  files - `.resx` needs an app restart to change culture, which would be a
  jarring way to switch languages; this way it's instant.
- Switching re-renders every control's text immediately from that table,
  including screen-reader-only text (AutomationProperties Name/HelpText),
  so JAWS announces everything in the newly selected language right away
  too.
- Status lines that depend on what's actually loaded/connected/playing
  (e.g. "Loaded: GeneralUser GS (287 instruments)...") are recomputed from
  the real current state at the moment you switch, not just word-for-word
  re-translated - so they stay accurate. A one-off result message already
  on screen from something you just did (e.g. "Note added") isn't
  retroactively translated - only what's shown from that point on is
  guaranteed to be in the new language.
- The sequencer's note names switch too: general/solfege names (`c`,
  `cis`, `h`, ...) in Serbian, English letter names (`C`, `C#`, ...) in
  English - see "Sequencer" above. What you can *type* into the note field
  is unaffected either way - both spellings are always accepted, in both
  languages, so nothing you've already typed stops working after a switch.
- Defaults to Serbian on startup; the choice isn't saved between runs yet.
- Per standing decision, every feature added to Ultra Composer from here
  on adds its strings to this same table in both languages from the start,
  rather than translating it later.

## Architecture

- **UI**: WPF (`UltraComposer.App`), meant to sit on top of the Ultra
  Accessible UI Kit once integrated — kept as plain WPF for now so this
  milestone has no dependency on that other repo
- **Audio engine**: native C++ DLL (`UltraComposer.AudioEngine`), using
  PortAudio for audio I/O. Runs entirely on its own real-time thread —
  the UI can never block or glitch the audio, and vice versa
- **Bridge**: a small C ABI (`AudioEngineApi.h`/`.cpp`, functions prefixed
  `UC_`) exposed by the native DLL, wrapped by a C# P/Invoke layer
  (`UltraComposer.Interop`)

This is the same style of separation professional DAWs use (native audio
core + separate UI layer), chosen specifically so the engine can later be
hardened or extended (more DSP, more voices, more plug-in formats) without
ever touching the accessible UI layer.

## Accessibility notes

- The "Klavijatura za sviranje" (performance keyboard) region is a
  `Border` control you reach with Tab. Only while it has keyboard focus do
  key presses turn into notes — this keeps every other JAWS shortcut and
  normal Tab/arrow navigation working everywhere else in the app.
- Entering/leaving that region, and every octave change (Page Up/Page Down),
  updates a live-region text block so JAWS announces it automatically.
- Standard mapping (matches FL Studio / most trackers):
  - Lower row (one octave): `Z S X D C V G B H N J M ,`
  - Upper row (next octave up): `Q 2 W 3 E R 5 T 6 Y 7 U I`
  - `Page Up` / `Page Down`: shift the base octave
  - `↑` / `↓` (arrow up/down): switch instrument in the loaded SoundFont
    bank, if one is loaded (see "SoundFont instrument banks" above)
- The sequencer is a plain add/remove note list (not a visual grid you'd
  need a mouse to place notes on), and every status change (note added,
  note removed, playback started/stopped) is announced through a
  live-region text block, same as the rest of the app.

## Prerequisites (Visual Studio)

1. **Visual Studio**, with these workloads/components installed (Visual
   Studio Installer → Modify):
   - Workloads: "Desktop development with C++" and ".NET desktop development"
   - Individual components: "vcpkg package manager"
2. **vcpkg integrated once**, from an elevated (Administrator) Developer
   Command Prompt:
   ```
   "<your VS install path>\VC\vcpkg\vcpkg.exe" integrate install
   ```
   On this machine that's:
   ```
   "C:\Program Files\Microsoft Visual Studio\18\Community\VC\vcpkg\vcpkg.exe" integrate install
   ```
   This lets Visual Studio automatically fetch and link PortAudio/RtMidi from
   the `vcpkg.json` manifest in `src\AudioEngine\` (vcpkg's MSBuild
   integration looks for the manifest next to the `.vcxproj`, not at the
   solution root) — no manual library setup needed.

## Building

1. Open `UltraComposer.sln` in Visual Studio.
2. Build the solution (Ctrl+Shift+B). The solution file already declares
   `UltraComposer.App` as depending on `UltraComposer.AudioEngine`, so
   Visual Studio always builds the native engine first and the DLL is
   guaranteed to exist by the time `App` needs it — no manual setup and no
   "build twice" step required.
3. Run `UltraComposer.App` (set it as the startup project if it isn't
   already).

## Running

1. The window opens (showing the "Banka instrumenata" view) and starts the
   audio engine automatically.
2. Press Tab until focus reaches "Klavijatura za sviranje".
3. Play `Z S X D C V G B H N J M` for one octave, `Q 2 W 3 E R 5 T 6 Y 7 U`
   for the octave above. `Page Up`/`Page Down` shift the octave.
4. To try a SoundFont instrument: Tab to the "Zvučna banka (SoundFont)"
   section (the bundled default bank's path is already filled in), click
   "Učitaj banku", then pick an instrument from the "Instrument" dropdown
   and play it from the keyboard, a MIDI device, or the sequencer.
5. To try auto-third: check "Uključi automatsku tercu" (still in "Banka
   instrumenata") and play a few notes - each one now sounds with a second
   note a third above (or below, if you picked "Donja terca") it.
6. To try the sequencer: open the "Prikaz" menu, choose "Sekvenser", add a
   few notes (e.g. `c4`, then `e4`, then `g4`) or a whole chord at once
   (`c4, e4, g4`), then click "Sviraj".
7. To try the arranger: with a pattern in the sequencer, open "Prikaz" →
   "Aranžer", name and save that pattern, add it to the play order (more
   than once if you like), then click "Sviraj aranžman".
8. To try the piano roll: still in "Sekvenser", check "Prikaži piano roll"
   - left-click the grid to add notes, right-click one to remove it.
9. To try multi-track + mixer: open "Prikaz" → "Multi-trake i mikser", add
   two or three tracks, give each a different instrument/volume/pan and a
   few notes, then click "Sviraj sve trake". Try "Izvezi kao WAV..." to
   render the mix to a file.
10. To try auto-accompaniment: open "Prikaz" → "Auto-pratnja", pick a
    rhythm (e.g. "Pop"), click "Sviraj pratnju", then Tab to "Banka
    instrumenata"'s performance keyboard and play a chord below C4 with one
    hand ("Automatski" chord mode is on by default) - the backing follows
    it - and a melody above C4 with the other.
11. To try English: open the "Jezik" menu and choose "Engleski" - every
    label, menu, and status message switches immediately, no restart.

## Releasing

Automated the same way as Ultra Video Editor/Ultra Audio Editor:
`.github/workflows/composer-release.yml` (repo root) builds the solution
and packages it with Inno Setup (`installer/UltraComposer.iss`, also repo
root) whenever a tag matching `composer-v*` is pushed (e.g.
`composer-v1.0.0`), then attaches the resulting installer to a GitHub
Release. It can also be run by hand from the Actions tab
(`workflow_dispatch`) without pushing a tag, useful for a first test run.

Before tagging a release, bump the single `<Version>` property in
`src/App/UltraComposer.App.csproj` - that's the one place to edit; the
window/About dialog's version display and the installer's filename both
derive from it (either from that property directly, in the local
Visual Studio build, or from the tag name, in the CI build - the two are
meant to be kept in sync by hand when you bump one).

Betas are marked with a standard SemVer prerelease suffix, e.g.
`1.0.0-beta.1` (tag: `composer-v1.0.0-beta.1`) - the workflow detects the
`-` and automatically flags the GitHub Release as a "Pre-release" rather
than the repo's latest/stable release. Drop the `-beta.N` suffix entirely
for the actual first full release (plain `1.0.0`), matching Ultra Video
Editor's "first full (non-beta) release is v1.0.0" convention.

The installer targets 64-bit Windows only (matching the native
AudioEngine, which is x64-only) and is framework-dependent rather than
self-contained, same as the other Ultra apps - it expects the .NET 8
Desktop Runtime already on the machine and points the user at the
download page if it looks missing, rather than bundling/installing it
silently.

It also bundles `docs/GettingStarted.txt` (bilingual, plain text) into the
install folder and offers to open it on the installer's Finished page,
checked by default, right next to the usual "Launch Ultra Composer"
checkbox - the same "readme with a checkbox" pattern many installers use.
That file is a short first-run orientation, and points back to the fuller
in-app "Pomoć → Uputstvo za upotrebu" / "Help → User Guide" (see "User
Guide" above) for the real detail.

## Licensing

Targeting GPL-3.0 for the whole Ultra Creative Suite, consistent with the
other apps. Third-party pieces used here:

- **PortAudio** — permissive (MIT-style) license, compatible with GPL-3.0
- **RtMidi** — permissive (MIT-style) license, compatible with GPL-3.0
- **VST3 SDK** — vendored directly from Steinberg into
  `src/AudioEngine/thirdparty/vst3sdk`, used under its GPL-3.0 licensing
  option (see `src/AudioEngine/thirdparty/vst3sdk/LICENSE.txt`), matching
  the license of the rest of this project. Vendored as plain source rather
  than through vcpkg because vcpkg's own `vst3sdk` port is incompatible
  with this project's static-CRT build setting (see
  `src/AudioEngine/thirdparty/vst3sdk/README-ultracomposer.txt`).
- **TinySoundFont** — permissive (MIT) license, vendored as a single
  header into `src/AudioEngine/thirdparty/tsf` (see
  `src/AudioEngine/thirdparty/tsf/LICENSE.txt`), compatible with GPL-3.0.
- **GeneralUser GS** (the bundled default SoundFont bank) — by S.
  Christian Collins, used under its own permissive license ("use without
  restriction, private or commercial" - see
  `src/AudioEngine/thirdparty/soundfonts/LICENSE.txt`), chosen specifically
  over other common free banks (e.g. TimGM6mb, which is GPL-2) to avoid
  any GPL-2/GPL-3 compatibility question. See
  `src/AudioEngine/thirdparty/soundfonts/README-ultracomposer.txt` for
  details; loading your own `.sf2` file instead is always an option and
  isn't bound by this license.

GPL-3.0 — see the repository's [LICENSE](../LICENSE) file. This tool shares
its license with the rest of the Ultra Creative Suite repository.
