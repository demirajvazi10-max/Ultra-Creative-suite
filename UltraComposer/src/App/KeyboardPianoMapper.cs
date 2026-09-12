using System.Windows.Input;

namespace UltraComposer.App;

/// <summary>
/// Maps the standard "typing keyboard as piano" layout - the same convention
/// used by FL Studio, LMMS and most trackers - onto MIDI note numbers, with
/// an adjustable base octave (Page Up / Page Down).
///
/// Lower row (one octave, starting at the base octave):
///     Z S X D C V G B H N J M ,
/// Upper row (the next octave up):
///     Q 2 W 3 E R 5 T 6 Y 7 U I
///
/// The black-key letters (S D, G B H, ...) sit directly above the white-key
/// letters they're a semitone above from, exactly like a real keyboard layout,
/// which is what makes this mapping learnable by feel.
/// </summary>
public sealed class KeyboardPianoMapper
{
    private static readonly Dictionary<Key, int> LowerRowOffsets = new()
    {
        [Key.Z] = 0,
        [Key.S] = 1,
        [Key.X] = 2,
        [Key.D] = 3,
        [Key.C] = 4,
        [Key.V] = 5,
        [Key.G] = 6,
        [Key.B] = 7,
        [Key.H] = 8,
        [Key.N] = 9,
        [Key.J] = 10,
        [Key.M] = 11,
        [Key.OemComma] = 12,
    };

    private static readonly Dictionary<Key, int> UpperRowOffsets = new()
    {
        [Key.Q] = 12,
        [Key.D2] = 13,
        [Key.W] = 14,
        [Key.D3] = 15,
        [Key.E] = 16,
        [Key.R] = 17,
        [Key.D5] = 18,
        [Key.T] = 19,
        [Key.D6] = 20,
        [Key.Y] = 21,
        [Key.D7] = 22,
        [Key.U] = 23,
        [Key.I] = 24,
    };

    private const int MinOctave = 0;
    private const int MaxOctave = 8;

    /// <summary>Current base octave. Octave 4 puts Z on middle C (MIDI note 60).</summary>
    public int BaseOctave { get; private set; } = 4;

    public void ShiftOctave(int delta)
    {
        BaseOctave = Math.Clamp(BaseOctave + delta, MinOctave, MaxOctave);
    }

    public bool TryGetMidiNote(Key key, out int midiNote)
    {
        int baseNote = (BaseOctave + 1) * 12; // MIDI note 0 = C-1, so octave 4 starts at note 60

        if (LowerRowOffsets.TryGetValue(key, out int lowerOffset))
        {
            midiNote = baseNote + lowerOffset;
            return midiNote is >= 0 and <= 127;
        }

        if (UpperRowOffsets.TryGetValue(key, out int upperOffset))
        {
            midiNote = baseNote + upperOffset;
            return midiNote is >= 0 and <= 127;
        }

        midiNote = 0;
        return false;
    }
}
