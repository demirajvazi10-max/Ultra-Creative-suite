using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UltraComposer.App;

/// <summary>
/// On-screen piano keyboard visual control (Task 2a).
/// Draws 5 octaves (C2 to C7, 61 keys) with realistic white and black keys.
/// Keys light up when sounding from any source (computer keyboard, mouse, MIDI).
/// Mouse click and drag triggers NoteOn and NoteOff.
/// Marked as decorative/visual aid for JAWS (Focusable=false, IsTabStop=false).
/// </summary>
public partial class PianoKeyboardControl : UserControl
{
    private sealed class KeyVisual
    {
        public int MidiNote { get; init; }
        public bool IsBlack { get; init; }
        public required Border Element { get; init; }
        public required Brush NormalBrush { get; init; }
        public required Brush ActiveBrush { get; init; }
        public Rect Bounds { get; set; }
    }

    public const int StartMidiNote = 36; // C2
    public const int EndMidiNote = 96;   // C7 (61 keys)

    private const double WhiteKeyWidth = 22.0;
    private const double WhiteKeyHeight = 92.0;
    private const double BlackKeyWidth = 14.0;
    private const double BlackKeyHeight = 56.0;

    private readonly Dictionary<int, KeyVisual> _keys = new();
    private readonly HashSet<int> _activeSoundingNotes = new();
    private int? _mouseHeldNote;

    public event Action<int, float>? NoteOnRequested;
    public event Action<int>? NoteOffRequested;

    public PianoKeyboardControl()
    {
        InitializeComponent();
        BuildKeyboard();
    }

    private void BuildKeyboard()
    {
        KeyboardCanvas.Children.Clear();
        _keys.Clear();

        var whiteNormal = (Brush)FindResource("WhiteKeyNormalBrush");
        var whiteBorder = (Brush)FindResource("WhiteKeyBorderBrush");
        var whiteActive = (Brush)FindResource("WhiteKeyActiveBrush");

        var blackNormal = (Brush)FindResource("BlackKeyNormalBrush");
        var blackBorder = (Brush)FindResource("BlackKeyBorderBrush");
        var blackActive = (Brush)FindResource("BlackKeyActiveBrush");

        var labelBrush = (Brush)FindResource("LabelBrush");
        var octaveLabelBrush = (Brush)FindResource("OctaveLabelBrush");

        // White note offsets in semitones within an octave: C=0, D=2, E=4, F=5, G=7, A=9, B=11
        int[] whiteSemitones = { 0, 2, 4, 5, 7, 9, 11 };
        string[] noteNames = { "C", "D", "E", "F", "G", "A", "B" };

        double whiteX = 0;

        // Create white keys first
        for (int note = StartMidiNote; note <= EndMidiNote; note++)
        {
            int semitoneInOctave = (note - 12) % 12;
            int octave = (note / 12) - 1;

            bool isWhite = false;
            string letter = "";
            for (int i = 0; i < whiteSemitones.Length; i++)
            {
                if (whiteSemitones[i] == semitoneInOctave)
                {
                    isWhite = true;
                    letter = noteNames[i];
                    break;
                }
            }

            if (!isWhite)
            {
                continue;
            }

            var border = new Border
            {
                Width = WhiteKeyWidth,
                Height = WhiteKeyHeight,
                Background = whiteNormal,
                BorderBrush = whiteBorder,
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 3, 3),
                Focusable = false,
                SnapsToDevicePixels = true,
            };

            string labelText = letter == "C" ? $"C{octave}" : letter;
            var textBlock = new TextBlock
            {
                Text = labelText,
                FontSize = letter == "C" ? 10 : 9,
                FontWeight = letter == "C" ? FontWeights.Bold : FontWeights.Normal,
                Foreground = letter == "C" ? octaveLabelBrush : labelBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 4),
                Focusable = false,
            };
            border.Child = textBlock;

            Canvas.SetLeft(border, whiteX);
            Canvas.SetTop(border, 0);
            Panel.SetZIndex(border, 1);
            KeyboardCanvas.Children.Add(border);

            var keyVisual = new KeyVisual
            {
                MidiNote = note,
                IsBlack = false,
                Element = border,
                NormalBrush = whiteNormal,
                ActiveBrush = whiteActive,
                Bounds = new Rect(whiteX, 0, WhiteKeyWidth, WhiteKeyHeight),
            };
            _keys[note] = keyVisual;

            whiteX += WhiteKeyWidth;
        }

        // Black note definitions: relative to white key offsets
        // C# sits between C(0) and D(1) -> X center = 22 -> left = 15
        // D# sits between D(1) and E(2) -> X center = 44 -> left = 37
        // F# sits between F(3) and G(4) -> X center = 88 -> left = 81
        // G# sits between G(4) and A(5) -> X center = 110 -> left = 103
        // A# sits between A(5) and B(6) -> X center = 132 -> left = 125
        (int semitone, double centerOffset)[] blackKeysDef =
        {
            (1, WhiteKeyWidth * 1),   // C#
            (3, WhiteKeyWidth * 2),   // D#
            (6, WhiteKeyWidth * 4),   // F#
            (8, WhiteKeyWidth * 5),   // G#
            (10, WhiteKeyWidth * 6),  // A#
        };

        for (int oct = 2; oct <= 6; oct++)
        {
            double octBaseX = (oct - 2) * (7 * WhiteKeyWidth);
            int octBaseMidi = (oct + 1) * 12;

            foreach (var (semi, center) in blackKeysDef)
            {
                int note = octBaseMidi + semi;
                if (note > EndMidiNote)
                {
                    continue;
                }

                double left = octBaseX + center - (BlackKeyWidth / 2.0);

                var border = new Border
                {
                    Width = BlackKeyWidth,
                    Height = BlackKeyHeight,
                    Background = blackNormal,
                    BorderBrush = blackBorder,
                    BorderThickness = new Thickness(1, 1, 1, 1),
                    CornerRadius = new CornerRadius(0, 0, 2, 2),
                    Focusable = false,
                    SnapsToDevicePixels = true,
                };

                Canvas.SetLeft(border, left);
                Canvas.SetTop(border, 0);
                Panel.SetZIndex(border, 10);
                KeyboardCanvas.Children.Add(border);

                var keyVisual = new KeyVisual
                {
                    MidiNote = note,
                    IsBlack = true,
                    Element = border,
                    NormalBrush = blackNormal,
                    ActiveBrush = blackActive,
                    Bounds = new Rect(left, 0, BlackKeyWidth, BlackKeyHeight),
                };
                _keys[note] = keyVisual;
            }
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        Point pos = e.GetPosition(KeyboardCanvas);
        int? note = HitTestNote(pos);
        if (note.HasValue)
        {
            CaptureMouse();
            TriggerMouseNoteOn(note.Value);
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            Point pos = e.GetPosition(KeyboardCanvas);
            int? note = HitTestNote(pos);
            if (note != _mouseHeldNote)
            {
                if (_mouseHeldNote.HasValue)
                {
                    TriggerMouseNoteOff(_mouseHeldNote.Value);
                }
                if (note.HasValue)
                {
                    TriggerMouseNoteOn(note.Value);
                }
            }
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        if (_mouseHeldNote.HasValue)
        {
            TriggerMouseNoteOff(_mouseHeldNote.Value);
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mouseHeldNote.HasValue)
        {
            TriggerMouseNoteOff(_mouseHeldNote.Value);
        }
    }

    private int? HitTestNote(Point pos)
    {
        if (pos.Y < 0 || pos.Y > WhiteKeyHeight || pos.X < 0 || pos.X > KeyboardCanvas.Width)
        {
            return null;
        }

        // Test black keys first because they sit on top (Z-Index)
        foreach (var kv in _keys.Values)
        {
            if (kv.IsBlack && kv.Bounds.Contains(pos))
            {
                return kv.MidiNote;
            }
        }

        // Test white keys
        foreach (var kv in _keys.Values)
        {
            if (!kv.IsBlack && kv.Bounds.Contains(pos))
            {
                return kv.MidiNote;
            }
        }

        return null;
    }

    private void TriggerMouseNoteOn(int midiNote)
    {
        _mouseHeldNote = midiNote;
        SetNoteActive(midiNote, true);
        NoteOnRequested?.Invoke(midiNote, 0.79f); // velocity ~100/127
    }

    private void TriggerMouseNoteOff(int midiNote)
    {
        if (_mouseHeldNote == midiNote)
        {
            _mouseHeldNote = null;
        }
        SetNoteActive(midiNote, false);
        NoteOffRequested?.Invoke(midiNote);
    }

    /// <summary>
    /// Lights up or dims a piano key when a note starts or stops sounding.
    /// Thread-safe via Dispatcher.
    /// </summary>
    public void SetNoteActive(int midiNote, bool active)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetNoteActive(midiNote, active));
            return;
        }

        if (active)
        {
            _activeSoundingNotes.Add(midiNote);
        }
        else
        {
            _activeSoundingNotes.Remove(midiNote);
        }

        if (_keys.TryGetValue(midiNote, out var keyVisual))
        {
            keyVisual.Element.Background = active ? keyVisual.ActiveBrush : keyVisual.NormalBrush;
        }
    }

    /// <summary>
    /// Turns off all key highlights (e.g. panic or note release).
    /// </summary>
    public void ClearActiveNotes()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ClearActiveNotes);
            return;
        }

        foreach (int note in _activeSoundingNotes)
        {
            if (_keys.TryGetValue(note, out var keyVisual))
            {
                keyVisual.Element.Background = keyVisual.NormalBrush;
            }
        }
        _activeSoundingNotes.Clear();
        _mouseHeldNote = null;
    }

    public void SetAutomationName(string name)
    {
        AutomationProperties.SetName(this, name);
    }
}
