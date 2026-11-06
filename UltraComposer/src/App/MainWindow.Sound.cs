using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using UltraComposer.Interop;
using Param = UltraComposer.App.SoundParameters.Param;
using Section = UltraComposer.App.SoundParameters.Section;
using SP = UltraComposer.App.SoundParameters;

namespace UltraComposer.App;

/// <summary>
/// Ultra Composer 2.0 - the "Zvuk i efekti" / "Sound and effects" view
/// (Ultra FM, performance controls, arpeggiator, effects rack), the new
/// performance-keyboard shortcuts (pitch bend, sustain, modulation...) and
/// saving all of it in the project. Kept in its own file so MainWindow.xaml.cs
/// only needs a few hooks.
///
/// Built for the keyboard and a screen reader first: two lists, one for the
/// sections and one for the settings of the chosen section. In the settings
/// list Left/Right change the value, Page Up/Page Down change it in big steps,
/// Home/End go to the minimum/maximum, Space/Enter switch on/off (or go to the
/// next choice) and Delete restores the starting value. Every change is spoken
/// right away, like a slider.
/// </summary>
public partial class MainWindow
{
    private bool _soundViewBuilt;
    private int _fxPresetIndex;
    private readonly HashSet<Key> _perfControlKeysHeld = new();

    // ------------------------------------------------------------------
    // Engine access
    // ------------------------------------------------------------------

    private float GetSound(int id) => id == SP.FxPresetPseudoId ? _fxPresetIndex : AudioEngineInterop.GetSoundParam(id);

    private void SetSound(int id, float value)
    {
        if (id == SP.FxPresetPseudoId)
        {
            ApplyFxPreset((int)Math.Round(value));
            return;
        }
        AudioEngineInterop.SetSoundParam(id, value);
    }

    private void ApplyFxPreset(int index)
    {
        index = Math.Clamp(index, 0, SP.FxPresets.Length - 1);
        _fxPresetIndex = index;
        // Start every effect from its normal settings, all switched off, then switch on what the preset uses.
        for (int id = 151; id <= 299; id++)
        {
            if (SP.Find(id) != null) AudioEngineInterop.SetSoundParam(id, AudioEngineInterop.GetSoundParamDefault(id));
        }
        foreach (int on in SP.EffectOnIds) AudioEngineInterop.SetSoundParam(on, 0);
        foreach (var (id, value) in SP.FxPresets[index].Values) AudioEngineInterop.SetSoundParam(id, value);
        AudioEngineInterop.SetSoundParam(SP.FxOn, 1);
    }

    // ------------------------------------------------------------------
    // Texts
    // ------------------------------------------------------------------

    private string SectionText(Section s)
    {
        if (s.OnOffId is int on)
        {
            return $"{s.Name}: {SP.OnOff(GetSound(on))}";
        }
        if (s.Operator > 0)
        {
            int algorithm = (int)Math.Round(GetSound(SP.FmAlgorithm));
            bool carrier = SP.IsCarrier(algorithm, s.Operator);
            bool silent = GetSound(SP.FmOpBase + (s.Operator - 1) * 10) <= 0.0005f;
            string role = carrier ? SP.L("nosilac, čuje se", "carrier, heard") : SP.L("modulator, menja boju tona", "modulator, shapes the tone");
            return silent ? $"{s.Name}, {role}, {SP.L("utišan", "silent")}" : $"{s.Name}, {role}";
        }
        return s.Name;
    }

    private string ParamText(Param p) => $"{p.Name}: {SP.FormatValue(p, GetSound(p.Id))}";

    private Section? CurrentSoundSection =>
        (SoundSectionsListBox.SelectedItem as ListBoxItem)?.Tag as Section;

    private void PopulateSoundSections()
    {
        int keep = Math.Max(0, SoundSectionsListBox.SelectedIndex);
        SoundSectionsListBox.Items.Clear();
        foreach (var s in SP.Sections)
        {
            SoundSectionsListBox.Items.Add(new ListBoxItem { Content = SectionText(s), Tag = s });
        }
        _soundViewBuilt = true;
        SoundSectionsListBox.SelectedIndex = Math.Min(keep, SoundSectionsListBox.Items.Count - 1);
        PopulateSoundParams();
    }

    private void PopulateSoundParams()
    {
        int keep = Math.Max(0, SoundParamsListBox.SelectedIndex);
        SoundParamsListBox.Items.Clear();
        var section = CurrentSoundSection;
        if (section == null) return;
        foreach (var p in section.Params)
        {
            SoundParamsListBox.Items.Add(new ListBoxItem { Content = ParamText(p), Tag = p });
        }
        SoundParamsListBox.SelectedIndex = Math.Min(keep, SoundParamsListBox.Items.Count - 1);
        AutomationProperties.SetName(SoundParamsListBox, SP.L($"Podešavanja: {section.Name}", $"Settings: {section.Name}"));
    }

    /// <summary>Re-reads every shown value from the engine (after presets, MIDI controllers, project load...).</summary>
    private void RefreshSoundTexts()
    {
        if (!_soundViewBuilt) return;
        foreach (ListBoxItem item in SoundSectionsListBox.Items)
        {
            if (item.Tag is Section s) item.Content = SectionText(s);
        }
        foreach (ListBoxItem item in SoundParamsListBox.Items)
        {
            if (item.Tag is Param p) item.Content = ParamText(p);
        }
    }

    private void ApplySoundViewLanguage()
    {
        bool sr = SP.Serbian;
        ShowSoundMenuItem.Header = Localization.T("Menu.View.Sound");
        SoundTitleText.Text = sr ? "Zvuk i efekti (Ultra Composer 2.0)" : "Sound and effects (Ultra Composer 2.0)";
        SoundIntroText.Text = sr
            ? "Ovde podešavaš zvuk klavijature za sviranje: Ultra FM sintisajzer (6 operatora, kao FM8), pitch bend, vibrato, portamento, mono i legato, arpeđator i efekte. " +
              "U listi odeljaka izaberi odeljak (razmak uključuje ili isključuje efekat), pa Tab do podešavanja. Tamo strelice levo i desno menjaju vrednost, Page Up i Page Down u većim koracima, " +
              "Home i End na najmanju i najveću, razmak ili Enter uključuje, isključuje ili bira sledeće, a Delete vraća početnu vrednost. Escape vraća na odeljke. " +
              "Ultra FM se čuje kad u Sloju 1 nije učitana SoundFont banka ni VST (dugme Isključi banku); efekti i kontrole rade sa svakim instrumentom."
            : "Here you shape the sound of the performance keyboard: the Ultra FM synthesizer (6 operators, like FM8), pitch bend, vibrato, portamento, mono and legato, the arpeggiator and the effects. " +
              "In the section list pick a section (Space switches an effect on or off), then Tab to its settings. There Left and Right arrows change the value, Page Up and Page Down in bigger steps, " +
              "Home and End go to the lowest and highest, Space or Enter switches on, off or picks the next choice, and Delete restores the starting value. Escape goes back to the sections. " +
              "Ultra FM is heard when no SoundFont bank or VST is loaded in Layer 1 (Unload bank button); effects and controls work with every instrument.";
        SoundSectionsLabel.Content = sr ? "_Odeljci:" : "_Sections:";
        SoundParamsLabel.Content = sr ? "_Podešavanja:" : "Se_ttings:";
        AutomationProperties.SetName(SoundSectionsListBox, sr ? "Odeljci zvuka i efekata" : "Sound and effects sections");
        SoundResetSectionButton.Content = sr ? "_Vrati odeljak na početne vrednosti" : "_Reset this section";
        SoundPanicButton.Content = sr ? "_Zaustavi sve tonove (panic)" : "Stop all _notes (panic)";
        SoundKeysHelpText.Text = sr
            ? "Na klavijaturi za sviranje: strelice levo/desno = pitch bend dok ih držiš, razmak = sustain pedala, F2/F3 = modulacija (vibrato) manje/više, " +
              "F4/F5 = transpozicija pola stepena dole/gore, F6 = arpeđator, F7 = portamento, F8 = poli/mono/legato, F9 = svi efekti, Home = zaustavi sve i vrati točkiće, " +
              "strelice gore/dole = sledeći zvuk (SoundFont ili Ultra FM). Sa MIDI klavijature rade pitch bend točkić, modulacioni točkić, sustain pedala i portamento kontrole."
            : "On the performance keyboard: Left/Right arrows = pitch bend while held, Space = sustain pedal, F2/F3 = modulation (vibrato) less/more, " +
              "F4/F5 = transpose a semitone down/up, F6 = arpeggiator, F7 = portamento, F8 = poly/mono/legato, F9 = all effects, Home = stop everything and centre the wheels, " +
              "Up/Down arrows = next sound (SoundFont or Ultra FM). From a MIDI keyboard the pitch bend wheel, modulation wheel, sustain pedal and portamento controls work too.";
        if (_soundViewBuilt)
        {
            RefreshSoundTexts();
            if (CurrentSoundSection is Section s)
                AutomationProperties.SetName(SoundParamsListBox, SP.L($"Podešavanja: {s.Name}", $"Settings: {s.Name}"));
        }
    }

    // ------------------------------------------------------------------
    // Speaking
    // ------------------------------------------------------------------

    /// <summary>Speaks <paramref name="text"/> through the screen reader right away (and shows it in the view's status line).</summary>
    private void AnnounceSound(string text, UIElement? source = null)
    {
        SoundStatusText.Text = text;
        AutomationPeer? peer = null;
        if (source != null)
        {
            peer = UIElementAutomationPeer.FromElement(source) ?? UIElementAutomationPeer.CreatePeerForElement(source);
        }
        peer ??= UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, "UltraComposerSound");
    }

    // ------------------------------------------------------------------
    // The two lists
    // ------------------------------------------------------------------

    private void SoundSectionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_soundViewBuilt) return;
        SoundParamsListBox.SelectedIndex = -1;
        PopulateSoundParams();
    }

    private void SoundSectionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var section = CurrentSoundSection;
        if (section == null) return;

        if (e.Key == Key.Space && section.OnOffId is int on)
        {
            float v = GetSound(on) >= 0.5f ? 0 : 1;
            SetSound(on, v);
            RefreshSoundTexts();
            AnnounceSound($"{section.Name}: {SP.OnOff(v)}", SoundSectionsListBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter || e.Key == Key.Right)
        {
            FocusSoundParam(Math.Max(0, SoundParamsListBox.SelectedIndex));
            e.Handled = true;
        }
    }

    private void FocusSoundParam(int index)
    {
        if (SoundParamsListBox.Items.Count == 0) return;
        index = Math.Clamp(index, 0, SoundParamsListBox.Items.Count - 1);
        SoundParamsListBox.SelectedIndex = index;
        (SoundParamsListBox.Items[index] as ListBoxItem)?.Focus();
    }

    private void SoundParamsListBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Values can change behind the view's back (MIDI controllers, F-keys on the keyboard).
        // Only when focus comes in from outside the list, not on every arrow key inside it.
        if (e.OldFocus is System.Windows.Media.Visual old && SoundParamsListBox.IsAncestorOf(old)) return;
        RefreshSoundTexts();
    }

    private void SoundParamsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SoundParamsListBox.SelectedItem is not ListBoxItem { Tag: Param p }) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        float current = GetSound(p.Id);

        switch (e.Key)
        {
            case Key.Left:
            case Key.Subtract:
            case Key.OemMinus:
                ChangeSound(p, SP.StepValue(p, current, -1, false));
                break;
            case Key.Right:
            case Key.Add:
            case Key.OemPlus:
                ChangeSound(p, SP.StepValue(p, current, +1, false));
                break;
            case Key.PageDown:
                ChangeSound(p, SP.StepValue(p, current, -1, true));
                break;
            case Key.PageUp:
                ChangeSound(p, SP.StepValue(p, current, +1, true));
                break;
            case Key.Home when !ctrl:
                ChangeSound(p, p.Kind == SP.Kind.Number && p.Table != null ? p.Table[0] : p.Min);
                break;
            case Key.End when !ctrl:
                ChangeSound(p, p.Kind == SP.Kind.Number && p.Table != null ? p.Table[^1] : SP.MaxOf(p));
                break;
            case Key.Space:
            case Key.Enter:
                if (p.Kind == SP.Kind.Toggle)
                {
                    ChangeSound(p, current >= 0.5f ? 0 : 1);
                }
                else if (p.Kind == SP.Kind.Choice)
                {
                    int count = (int)SP.MaxOf(p) + 1;
                    int next = p.Id == SP.FxPresetPseudoId ? (int)Math.Round(current) : ((int)Math.Round(current) + 1) % count;
                    ChangeSound(p, next);
                }
                else
                {
                    AnnounceSound(SP.FormatValue(p, current), SoundParamsListBox);
                }
                break;
            case Key.Delete:
            case Key.Back:
                ChangeSound(p, p.Id == SP.FxPresetPseudoId ? 0 : AudioEngineInterop.GetSoundParamDefault(p.Id));
                break;
            case Key.Escape:
                (SoundSectionsListBox.SelectedItem as ListBoxItem)?.Focus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ChangeSound(Param p, float value)
    {
        SetSound(p.Id, value);
        float now = GetSound(p.Id);

        if (p.Id == SP.FmPreset || p.Id == SP.FxPresetPseudoId || p.Id == SP.FmAlgorithm || p.Kind == SP.Kind.Toggle
            || (p.Id >= SP.FmOpBase && p.Id < SP.FmOpBase + 60 && (p.Id - SP.FmOpBase) % 10 == 0))
        {
            RefreshSoundTexts(); // presets change many values; switches and levels change section names
        }
        else if (SoundParamsListBox.SelectedItem is ListBoxItem item)
        {
            item.Content = ParamText(p);
        }
        AnnounceSound(SP.FormatValue(p, now), SoundParamsListBox);
    }

    private void SoundResetSectionButton_Click(object sender, RoutedEventArgs e)
    {
        var section = CurrentSoundSection;
        if (section == null) return;
        foreach (var p in section.Params)
        {
            if (p.Id == SP.FxPresetPseudoId) continue;
            if (section.Operator > 0 || p.Id == SP.FmAlgorithm || p.Id == SP.FmFeedback || p.Id == SP.FmVolume || p.Id == SP.FmBrightness)
            {
                // Ultra FM: back to the values of the chosen preset, not of the first one.
                continue;
            }
            AudioEngineInterop.SetSoundParam(p.Id, AudioEngineInterop.GetSoundParamDefault(p.Id));
        }
        if (section.Operator > 0 || section.Params.Any(p => p.Id == SP.FmPreset))
        {
            AudioEngineInterop.SetSoundParam(SP.FmPreset, GetSound(SP.FmPreset)); // reloads the whole preset
        }
        if (section.Params.Any(p => p.Id == SP.FxPresetPseudoId)) _fxPresetIndex = 0;
        RefreshSoundTexts();
        AnnounceSound(SP.L($"{section.Name}: vraćene početne vrednosti.", $"{section.Name}: reset to starting values."), SoundResetSectionButton);
    }

    private void SoundPanicButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.PerformancePanic();
        ReleaseAllHeldNotes();
        RefreshSoundTexts();
        AnnounceSound(SP.L("Svi tonovi zaustavljeni, pitch bend i modulacija vraćeni na nulu.", "All notes stopped, pitch bend and modulation back to zero."), SoundPanicButton);
    }

    private void ShowSoundMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(AppView.Sound);
    }

    private void OnSoundViewShown()
    {
        if (!_soundViewBuilt)
        {
            if (!_engineRunning) return;
            PopulateSoundSections();
        }
        RefreshSoundTexts();
        Dispatcher.BeginInvoke(new Action(() => (SoundSectionsListBox.SelectedItem as ListBoxItem)?.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    // ------------------------------------------------------------------
    // Performance keyboard: pitch bend, sustain, modulation, transpose...
    // ------------------------------------------------------------------

    /// <summary>Returns true when the key was a performance control (and is handled).</summary>
    private bool HandlePerformanceControlKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                if (_perfControlKeysHeld.Add(Key.Space)) AudioEngineInterop.SetSoundParam(SP.Sustain, 1);
                break;
            case Key.F2:
            case Key.F3:
            {
                float mod = Math.Clamp(AudioEngineInterop.GetSoundParam(SP.ModWheel) + (e.Key == Key.F3 ? 0.1f : -0.1f), 0f, 1f);
                mod = (float)Math.Round(mod, 2);
                AudioEngineInterop.SetSoundParam(SP.ModWheel, mod);
                AnnounceSound(SP.L($"Modulacija {Math.Round(mod * 100)} %", $"Modulation {Math.Round(mod * 100)}%"), PerformanceKeyboardBorder);
                break;
            }
            case Key.F4:
            case Key.F5:
            {
                float t = Math.Clamp(AudioEngineInterop.GetSoundParam(SP.Transpose) + (e.Key == Key.F5 ? 1 : -1), -12, 12);
                AudioEngineInterop.SetSoundParam(SP.Transpose, t);
                var def = SP.Find(SP.Transpose)!;
                AnnounceSound($"{def.Name}: {SP.FormatValue(def, t)}", PerformanceKeyboardBorder);
                break;
            }
            case Key.F6:
            case Key.F7:
            case Key.F9:
            {
                if (e.IsRepeat) break;
                int id = e.Key == Key.F6 ? SP.ArpOn : e.Key == Key.F7 ? SP.PortamentoOn : SP.FxOn;
                float v = AudioEngineInterop.GetSoundParam(id) >= 0.5f ? 0 : 1;
                AudioEngineInterop.SetSoundParam(id, v);
                string what = e.Key == Key.F6 ? SP.L("Arpeđator", "Arpeggiator") : e.Key == Key.F7 ? SP.L("Portamento", "Portamento") : SP.L("Svi efekti", "All effects");
                AnnounceSound($"{what}: {SP.OnOff(v)}", PerformanceKeyboardBorder);
                break;
            }
            case Key.F8:
            {
                if (e.IsRepeat) break;
                var def = SP.Find(SP.VoiceMode)!;
                float v = ((int)Math.Round(AudioEngineInterop.GetSoundParam(SP.VoiceMode)) + 1) % 3;
                AudioEngineInterop.SetSoundParam(SP.VoiceMode, v);
                AnnounceSound(SP.FormatValue(def, v), PerformanceKeyboardBorder);
                break;
            }
            case Key.Home:
                if (e.IsRepeat) break;
                _perfControlKeysHeld.Clear();
                foreach (Key key in _heldKeys)
                {
                    if (_mapper.TryGetMidiNote(key, out int note)) AudioEngineInterop.NoteOff(note);
                }
                _heldKeys.Clear();
                AudioEngineInterop.PerformancePanic();
                AnnounceSound(SP.L("Svi tonovi zaustavljeni, pitch bend i modulacija na nuli.", "All notes stopped, pitch bend and modulation at zero."), PerformanceKeyboardBorder);
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    private bool HandlePerformanceControlKeyUp(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _perfControlKeysHeld.Remove(Key.Space);
                AudioEngineInterop.SetSoundParam(SP.Sustain, 0);
                break;
            case Key.F2: case Key.F3: case Key.F4: case Key.F5: case Key.F6: case Key.F7: case Key.F8: case Key.F9: case Key.Home:
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    // Pitch bend keys: the key left of 1 (` on US, the dead-key position on Serbian/Croatian
    // layouts) bends down, 1 bends up. Recognised by physical scan code so it works on every
    // keyboard layout. Arrow keys are deliberately NOT used (JAWS and list navigation need them).
    private const Key PitchBendDownToken = Key.Oem3;
    private const Key PitchBendUpToken = Key.D1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>-1 = bend down key, +1 = bend up key, 0 = neither.</summary>
    private static int PitchBendKeyDirection(KeyEventArgs e)
    {
        Key key = e.Key == Key.DeadCharProcessed ? e.DeadCharProcessedKey
                : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
                : e.Key;
        if (key == Key.D1) return 1;
        if (key == Key.None) return 0;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0 && MapVirtualKey((uint)vk, 0) == 0x29) return -1; // scan code 0x29 = key left of 1
        return 0;
    }

    /// <summary>Called from the window-level key handlers. Returns true if the key was a pitch bend key.</summary>
    private bool HandlePitchBendKey(KeyEventArgs e, bool isDown)
    {
        int dir = PitchBendKeyDirection(e);
        if (dir == 0) return false;
        Key token = dir < 0 ? PitchBendDownToken : PitchBendUpToken;
        if (isDown) _perfControlKeysHeld.Add(token);
        else _perfControlKeysHeld.Remove(token);
        UpdateKeyboardPitchBend();
        e.Handled = true;
        return true;
    }

    private bool IsPitchBendKeyHeld(KeyEventArgs e)
    {
        int dir = PitchBendKeyDirection(e);
        return dir != 0 && _perfControlKeysHeld.Contains(dir < 0 ? PitchBendDownToken : PitchBendUpToken);
    }

    private void UpdateKeyboardPitchBend()
    {
        bool down = _perfControlKeysHeld.Contains(PitchBendDownToken), up = _perfControlKeysHeld.Contains(PitchBendUpToken);
        float target = down == up ? 0 : (up ? 1 : -1);
        AudioEngineInterop.SetSoundParam(SP.PitchBendKeys, target);
    }

    /// <summary>Focus left the keyboard: let go of the wheel and the pedal so nothing stays bent or held.</summary>
    private void ReleasePerformanceControls()
    {
        if (!_engineRunning) return;
        if (_perfControlKeysHeld.Contains(Key.Space)) AudioEngineInterop.SetSoundParam(SP.Sustain, 0);
        _perfControlKeysHeld.Clear();
        AudioEngineInterop.SetSoundParam(SP.PitchBendKeys, 0);
    }

    /// <summary>Up/Down on the keyboard with no SoundFont/VST loaded: next/previous Ultra FM preset.</summary>
    private void CycleFmPreset(int delta)
    {
        int count = AudioEngineInterop.GetFmPresetCount();
        if (count <= 0) return;
        int current = (int)Math.Round(AudioEngineInterop.GetSoundParam(SP.FmPreset));
        int next = ((current + delta) % count + count) % count;
        AudioEngineInterop.SetSoundParam(SP.FmPreset, next);
        RefreshSoundTexts();
        AnnounceSound($"Ultra FM: {SP.FmPresetNames()[Math.Min(next, SP.FmPresetNames().Length - 1)]}", PerformanceKeyboardBorder);
    }

    // ------------------------------------------------------------------
    // Project file
    // ------------------------------------------------------------------

    private void GatherSoundSettings(ProjectFile.ProjectData data)
    {
        data.SoundSettings.Clear();
        foreach (var p in SP.AllParams)
        {
            if (!p.Persist || p.Id == SP.FxPresetPseudoId) continue;
            data.SoundSettings[p.Id.ToString(CultureInfo.InvariantCulture)] = AudioEngineInterop.GetSoundParam(p.Id);
        }
    }

    private void ApplySoundSettings(ProjectFile.ProjectData data)
    {
        // Preset first (it loads a whole Ultra FM patch), then everything else in id order,
        // so the project's own operator settings win over the preset's. Settings missing from
        // the file (a project from before 2.0) get their starting values.
        var ids = SP.AllParams.Where(p => p.Persist && p.Id != SP.FxPresetPseudoId).Select(p => p.Id).Distinct().OrderBy(id => id);
        foreach (int id in ids)
        {
            float value = data.SoundSettings.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out float saved)
                ? saved
                : AudioEngineInterop.GetSoundParamDefault(id);
            AudioEngineInterop.SetSoundParam(id, value);
        }
        _fxPresetIndex = 0;
        RefreshSoundTexts();
    }
}
