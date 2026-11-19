# FM8 preset (.nfm8) format notes

Working notes for importing Native Instruments FM8 presets into Ultra
Composer's Ultra FM synthesizer. Everything under "Verified" was checked
against a corpus of 61 real presets; everything under "Open" still has to
be worked out.

The test corpus lives OUTSIDE this repository, at
`D:\Projekat Editor\Boje Senad\BOJE FM8 SENAD` on the author's machine.
Those presets belong to other people (some are commercial), so they must
never be copied into this repository. `*.nfm8` is in `.gitignore` for that
reason. The only exception is `src/App/Library/`, which holds sounds whose
author made them for Ultra Composer and asked for them to be shipped (only
presets with no third-party author in their metadata).

## Verified

### 1. Container (Native Instruments "NI container")

- Little-endian throughout.
- Nested chunks: header segments tagged `hsin` and data segments tagged
  `DSIN` (each with a u32 type, e.g. `DSINv`, `DSINj`, `DSINl`, `DSINy`,
  `DSINt`, `DSINs`, `DSINe`), each `hsin` followed by a 16-byte GUID.
- The uncompressed metadata part holds UTF-16 "pascal" strings (u32 char
  count + UTF-16LE text): preset name, author (e.g. "Puremagnetik"),
  vendor ("Native Instruments"), product ("FM8"), tags
  ("Organ", "Synthetic", "Transient Attacks", ...), then the 8 macro knob
  names ("Brightness", "Harmonic", "Detune", ...) with float values.
- An open-source project that understands the container (not the FM8
  parameters): https://github.imc.re/TriYop/ni-file

### 2. Compressed preset body

Behind a 9-byte header:

```
u8   1
u32  uncompressed size
u32  compressed size
...  LZF stream (exactly "compressed size" bytes)
```

The stream is plain LZF (liblzf scheme):

- control byte `c < 32`: copy the next `c + 1` bytes literally
- otherwise: `len = c >> 5` (if 7, add the next byte), then
  `offset = ((c & 0x1F) << 8) + next byte + 1`, copy `len + 2` bytes
  from `offset` bytes back in the output.

The header position varies per file; `nfm8_decode.py` finds it by scanning
for a header whose stream decompresses to exactly the declared size. All
61 corpus files decode this way. A few trailing bytes after the stream
are not part of it.

### 3. Inside the body: the `E8MF` chunk

The decompressed body is another small NI container; the FM8 data is the
`E8MF` chunk (at offset 0xBC in every corpus file):

```
"E8MF"
u32  version            = 206 in all 61 files
u32  header value       = 9148 in all 61 files (meaning unknown)
4 x  { u32 length, latin-1 text }   - 4 names (see below)
...  parameter block (about 2990..4570 bytes)
```

The 4 names are the preset's 4 morph snapshot names (FM8 has a
morph square with 4 corners), e.g. "Titanium Organ / Trash / Dry /
Filter Click"; in most user presets all 4 are the same. 12 corpus presets
were built from FM8's empty starting patch and carry "init sound" in all 4.

### 4. Parameter block - what is known so far

- Mostly 32-bit little-endian floats (values like 0.5, 1.0, 8.0, 27.0,
  0.9 = 0x3F666666 appear a lot) mixed with u32 counts.
- It is NOT fixed length: corpus lengths range from 2987 to 4571 bytes.
  Variable-length arrays are visible, e.g. a u32 count 2 or 3 followed
  by groups of floats - very likely envelope breakpoints (FM8 envelopes
  have a variable number of breakpoints).
- No further strings after the 4 snapshot names.
- Confirmed on all 61 files: the first 856 bytes of the parameter block
  are 214 fixed-position float32 words (same layout in every preset).
  Right after them, at byte 856, the data switches to single-byte values
  (0/1/2/3 - flags or small enums, e.g. `01 01 01 01 01 02 01 01 02`),
  so from there on the block is NOT 4-byte aligned. The variable-length
  part comes later. Per-word min/max/unique values for the 214 floats can
  be printed with `analysis/analyze_first_856.py` (needs the corpus).

### 5. Parameter block layout (verified on all 61 files)

Offsets are from the start of the parameter block (right after the 4
snapshot names).

- `0..855`: 214 float32, fixed positions (global settings - not mapped
  yet; words 137-212 form 4 records of 19 words, likely per snapshot).
- `856..1144`: fixed-length bytes and unaligned floats (flags, enums,
  waveforms...). Not mapped yet.
- `1145..1504`: 4 operator records of 90 bytes, one per morph snapshot.
  At record offset 18: 9 float32 = frequency **ratio** of A, B, C, D,
  E, F, X, Z, pitch. Followed by 6 float32 (probably offset in Hz for
  A-F) and bytes. Confirmed by Electric Harp: 3.003 3.002 1.004 2.997
  2.999 0.998.
- `1505`: 51 arrays, each `u8 n` followed by `n + 1` float32:
  - arrays 0-8: envelope segment **times in ms** for A-F, X, Z, pitch
    (the last value is the release segment),
  - arrays 9-17: envelope **levels** reached at the end of each segment
    (pitch envelope is bipolar),
  - arrays 18-26: envelope slopes/curves (0..1),
  - arrays 27-34: keyscaling breakpoints (MIDI notes, e.g. 0 60 127),
  - arrays 35-42: keyscaling amounts,
  - arrays 43-50: unknown 0..1 curves (velocity?).
- Right after the arrays: the **FM matrix**, 496 bytes = 124 cells of
  4 bytes (one byte per snapshot). Cell index = row * 11 + col, rows
  0-7 = source operator A-F, X, Z; cols 0-7 = destination A-F, X, Z
  (the diagonal is feedback), col 8 = output level, col 9 probably pan,
  col 10 unused. Values 0-100. Orientation confirmed by Electric Harp
  (A->B->C and D->E->F stacks, C and F to the output).
- After the matrix: a run of `0xFF` bytes, then arpeggiator-like
  records and a short trailer. Not mapped.

### 6. Importer (src/App/Nfm8Importer.cs)

Uses snapshot 0, keeps A-F, brute-forces the 8 Ultra FM algorithms x
720 operator placements to maximise reproduced modulation + output,
sets levels from the matrix (carriers: output column; modulators:
their strongest matched amount), feedback from the diagonal of the
operator placed on slot 6, ratios clamped to 0.5-16, envelopes reduced
to ADSR (attack = first time, decay = sum of middle times, sustain =
level before release, release = last time, capped at 4 s).

## Open

1. Meaning of the fixed fields at 0..1144 (global settings, waveforms,
   operator on/off, morph position, LFOs, effects).
2. Which snapshot FM8 actually plays when the 4 snapshots differ.
3. Meaning of each remaining field. FM8 has 8 operators (A-F, X noise/filter
   operator, Z filter), a free FM matrix (every operator can modulate
   every other, plus feedback), envelopes with breakpoints, keyscaling,
   LFOs, an arpeggiator, an effects section and the morph square.
4. How to map that onto Ultra FM, which is simpler: 6 operators, 8 fixed
   algorithms, one feedback amount, per-operator level, ratio, detune,
   ADSR and velocity sensitivity (see `src/AudioEngine/SoundParams.h`,
   IDs 50-119). The import will therefore be an approximation.

## Suggested approach for the open items

- Start from the 12 "init sound" presets: they all began as the same
  patch, so comparing them shows which fields people actually change.
- Compare all 61 files field by field: fields that are always 0 or 1
  are flags; fields between 0 and 1 are levels or amounts; fields like
  0.5, 1, 2, 3 ... 32 are likely operator ratios.
- Look for 8 repeating per-operator records and an 8x8 (or 8x9 / 9x8)
  block of matrix amounts.
- Use the FM8 manual (free PDF from Native Instruments) for parameter
  names and ranges.
