using System.Globalization;

namespace UltraComposer.App;

/// <summary>
/// Ultra Composer 2.0 - the list of every setting shown in the "Zvuk i efekti" /
/// "Sound and effects" view: performance controls, arpeggiator, the Ultra FM
/// synthesizer and the effects rack. Each one is a native SoundParam id
/// (src/AudioEngine/SoundParams.h - the numbers must match), plus what the
/// view needs to show and change it: its name in both languages, its range,
/// how big one step is and how the value is read out.
///
/// Values are always read back from the engine, so this file never holds state.
/// </summary>
public static class SoundParameters
{
    public enum Kind { Toggle, Choice, Number }

    /// <summary>Not a native setting: applies one of the effect presets in <see cref="FxPresets"/>.</summary>
    public const int FxPresetPseudoId = 1000;

    public sealed class Param
    {
        public int Id { get; init; }
        public string Sr { get; init; } = "";
        public string En { get; init; } = "";
        public Kind Kind { get; init; }
        public float Min { get; init; }
        public float Max { get; init; } = 1;
        public float Step { get; init; } = 0.01f;
        public float BigStep { get; init; } = 0.1f;
        /// <summary>Allowed values in order (for times and other uneven scales); Left/Right moves one entry.</summary>
        public float[]? Table { get; init; }
        public Func<string[]>? Choices { get; init; }
        public Func<float, string>? Format { get; init; }
        /// <summary>Saved in the project file (pitch bend and the sustain pedal are not - they're "in the hands").</summary>
        public bool Persist { get; init; } = true;
        public string Name => L(Sr, En);
    }

    public sealed class Section
    {
        public string Sr { get; init; } = "";
        public string En { get; init; } = "";
        /// <summary>The section's own on/off switch (effects, arpeggiator), toggled with Space in the section list.</summary>
        public int? OnOffId { get; init; }
        /// <summary>Ultra FM operator number 1-6, so the section name can say whether it's heard or modulating.</summary>
        public int Operator { get; init; }
        public List<Param> Params { get; } = new();
        public string Name => L(Sr, En);
    }

    // ------------------------------------------------------------------
    // Native ids (SoundParams.h)
    // ------------------------------------------------------------------
    public const int ModWheel = 1, Sustain = 2, BendRange = 3, VibratoDepth = 4, VibratoRate = 5,
        PortamentoOn = 6, PortamentoTime = 7, VoiceMode = 8, Transpose = 9,
        ArpOn = 10, ArpMode = 11, ArpRate = 12, ArpOctaves = 13, ArpGate = 14, ArpLatch = 15, ArpTempo = 16,
        PitchBendKeys = 17;
    public const int FmPreset = 50, FmAlgorithm = 51, FmFeedback = 52, FmVolume = 53, FmBrightness = 54, FmOpBase = 60;
    public const int FxOn = 150;
    public const int DriveOn = 160, FilterOn = 170, EqOn = 180, CompOn = 190, ChorusOn = 200, FlangerOn = 210,
        PhaserOn = 220, TremoloOn = 230, AutoPanOn = 240, RotaryOn = 250, DelayOn = 260, ReverbOn = 270;
    public static readonly int[] EffectOnIds = { DriveOn, FilterOn, EqOn, CompOn, ChorusOn, FlangerOn, PhaserOn, TremoloOn, AutoPanOn, RotaryOn, DelayOn, ReverbOn };

    // ------------------------------------------------------------------
    // Text helpers
    // ------------------------------------------------------------------
    public static bool Serbian => Localization.Current == AppLanguage.Serbian;
    public static string L(string sr, string en) => Serbian ? sr : en;
    private static CultureInfo Culture => Serbian ? CultureInfo.GetCultureInfo("sr-Latn-RS") : CultureInfo.GetCultureInfo("en-US");
    private static string N(double v, string format = "0.##") => v.ToString(format, Culture);

    /// <summary>Serbian plural: 1 poluton, 2 polutona, 5 polutona, 21 poluton...</summary>
    private static string SrPlural(double value, string one, string few, string many)
    {
        if (Math.Abs(value - Math.Round(value)) > 0.0001) return few; // decimals: "0,5 polutona"
        long n = Math.Abs((long)Math.Round(value));
        if (n % 10 == 1 && n % 100 != 11) return one;
        if (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14)) return few;
        return many;
    }

    public static string OnOff(float v) => v >= 0.5f ? L("uključeno", "on") : L("isključeno", "off");
    private static string Pct(float v) => Serbian ? $"{N(Math.Round(v * 100))} %" : $"{N(Math.Round(v * 100))}%";
    private static string Semis(float v)
    {
        string sign = v > 0.0001f ? "+" : "";
        return Serbian ? $"{sign}{N(v)} {SrPlural(v, "poluton", "polutona", "polutona")}"
                       : $"{sign}{N(v)} {(Math.Abs(v - 1) < 0.0001 || Math.Abs(v + 1) < 0.0001 ? "semitone" : "semitones")}";
    }
    private static string SemisUnsigned(float v) => Serbian ? $"{N(v)} {SrPlural(v, "poluton", "polutona", "polutona")}"
                                                            : $"{N(v)} {(Math.Abs(v - 1) < 0.0001 ? "semitone" : "semitones")}";
    private static string Hz(float v) => v >= 1000 ? $"{N(v / 1000.0, "0.0#")} kHz" : $"{N(v, v < 10 ? "0.0#" : "0")} Hz";
    private static string Seconds(float v) => v < 1 ? $"{N(v * 1000, v < 0.01 ? "0.#" : "0")} ms" : $"{N(v, "0.0#")} s";
    private static string Ms(float v) => v >= 1000 ? $"{N(v / 1000.0, "0.0#")} s" : $"{N(v, v < 10 ? "0.#" : "0")} ms";
    private static string Db(float v) => $"{(v > 0.001f ? "+" : "")}{N(v, "0.#")} dB";
    private static string Cents(float v) => Serbian ? $"{(v > 0 ? "+" : "")}{N(v)} {SrPlural(v, "cent", "centa", "centi")}"
                                                    : $"{(v > 0 ? "+" : "")}{N(v)} cents";
    private static string Count(float v, string srOne, string srFew, string srMany, string enOne, string enMany)
        => Serbian ? $"{N(v)} {SrPlural(v, srOne, srFew, srMany)}" : $"{N(v)} {(Math.Abs(v - 1) < 0.0001 ? enOne : enMany)}";
    private static string CutoffHz(float v) => Hz((float)(20.0 * Math.Pow(1000.0, Math.Clamp(v, 0f, 1f))));

    // ------------------------------------------------------------------
    // Value tables
    // ------------------------------------------------------------------
    private static readonly float[] EnvTimes = { 0.001f, 0.002f, 0.003f, 0.005f, 0.007f, 0.01f, 0.015f, 0.02f, 0.03f, 0.04f, 0.05f, 0.07f,
        0.1f, 0.13f, 0.15f, 0.2f, 0.25f, 0.3f, 0.4f, 0.45f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.8f, 2f, 2.5f, 3f, 3.5f, 4f, 5f, 6f, 7f, 8f, 10f };
    private static readonly float[] GlideTimes = { 0.01f, 0.02f, 0.03f, 0.05f, 0.07f, 0.1f, 0.15f, 0.2f, 0.3f, 0.4f, 0.5f, 0.7f, 1f, 1.5f, 2f };
    private static readonly float[] EqFreqs = { 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000 };
    private static readonly float[] CompAttacks = { 0.1f, 0.5f, 1, 2, 5, 10, 20, 30, 50, 100, 200 };
    private static readonly float[] CompReleases = { 10, 20, 50, 100, 150, 200, 300, 500, 800, 1000, 1500, 2000 };

    // ------------------------------------------------------------------
    // Names of things
    // ------------------------------------------------------------------
    public static string[] FmPresetNames() => Serbian
        ? new[] { "Električni klavir", "Zvono", "FM bas", "Limeni duvači", "Orgulje", "Meki pad", "Solo sintisajzer", "Gudači",
                  "Marimba", "Klavinet", "Flauta", "Staklo", "Trzani bas", "Hor", "Steel drum (čelični bubanj)", "Čist sinusni ton" }
        : new[] { "Electric piano", "Bell", "FM bass", "Brass", "Organ", "Soft pad", "Synth lead", "Strings",
                  "Marimba", "Clavinet", "Flute", "Glass", "Pluck bass", "Choir", "Steel drum", "Pure sine" };

    public static string[] AlgorithmNames() => Serbian
        ? new[] { "1: jedan stub, 6 do 1 (najsjajniji)", "2: dva stuba, 2 u 1 i 6-5-4 u 3", "3: tri para, 2 u 1, 4 u 3, 6 u 5 (električni klavir)",
                  "4: dve trojke, 3-2-1 i 6-5-4 (duvači)", "5: grane, 2 i 3 u 1, 5 i 6 u 4", "6: operator 6 menja 1 do 5",
                  "7: svih šest se čuje (orgulje)", "8: par 2 u 1, plus 3, 4, 5 i 6 se čuju" }
        : new[] { "1: one stack, 6 down to 1 (brightest)", "2: two stacks, 2 into 1 and 6-5-4 into 3", "3: three pairs, 2 into 1, 4 into 3, 6 into 5 (electric piano)",
                  "4: two triples, 3-2-1 and 6-5-4 (brass)", "5: branches, 2 and 3 into 1, 5 and 6 into 4", "6: operator 6 modulates 1 to 5",
                  "7: all six heard (organ)", "8: pair 2 into 1, plus 3, 4, 5 and 6 heard" };

    // Which operators are heard (carriers) in each algorithm - mirrors kAlgorithms in FmSynth.cpp.
    private static readonly int[] CarrierMasks = { 0b000001, 0b000101, 0b010101, 0b001001, 0b001001, 0b011111, 0b111111, 0b111101 };

    public static bool IsCarrier(int algorithm, int operatorNumber)
        => algorithm >= 0 && algorithm < CarrierMasks.Length && ((CarrierMasks[algorithm] >> (operatorNumber - 1)) & 1) == 1;

    // ------------------------------------------------------------------
    // Effect presets ("Brza podešavanja efekata")
    // ------------------------------------------------------------------
    public sealed record FxPreset(string Sr, string En, (int Id, float Value)[] Values);

    public static readonly FxPreset[] FxPresets =
    {
        new("Bez efekata", "No effects", Array.Empty<(int, float)>()),
        new("Mala sala", "Small room", new[] { (ReverbOn, 1f), (271, 0.3f), (272, 0.5f), (273, 0.15f) }),
        new("Koncertna sala", "Concert hall", new[] { (ReverbOn, 1f), (271, 0.85f), (272, 0.35f), (273, 0.3f) }),
        new("Električni klavir: auto-pan i chorus", "Electric piano: auto-pan and chorus",
            new[] { (AutoPanOn, 1f), (241, 4.5f), (242, 0.5f), (ChorusOn, 1f), (202, 0.4f), (203, 0.35f), (ReverbOn, 1f), (273, 0.15f) }),
        new("Orgulje sa Lesli zvučnikom", "Organ with rotary speaker",
            new[] { (RotaryOn, 1f), (251, 0f), (DriveOn, 1f), (161, 0.25f), (163, 0.6f), (ReverbOn, 1f), (273, 0.12f) }),
        new("Rok solo: distorzija i eho", "Rock solo: drive and echo",
            new[] { (DriveOn, 1f), (161, 0.7f), (162, 0.55f), (163, 0.45f), (DelayOn, 1f), (261, 375f), (262, 0.35f), (263, 0.25f), (ReverbOn, 1f), (273, 0.18f) }),
        new("Svemirski pad", "Space pad",
            new[] { (PhaserOn, 1f), (221, 0.2f), (ChorusOn, 1f), (DelayOn, 1f), (261, 500f), (262, 0.5f), (263, 0.3f), (ReverbOn, 1f), (271, 0.9f), (273, 0.4f) }),
        new("Vau-vau (auto-wah)", "Auto-wah",
            new[] { (FilterOn, 1f), (171, 2f), (172, 0.35f), (173, 0.75f), (176, 0.85f), (ReverbOn, 1f), (273, 0.12f) }),
        new("Eho levo-desno", "Ping-pong echo",
            new[] { (DelayOn, 1f), (261, 300f), (262, 0.45f), (263, 0.35f), (264, 1f), (ReverbOn, 1f), (273, 0.15f) }),
        new("Stari radio", "Old radio",
            new[] { (EqOn, 1f), (181, -15f), (182, 8f), (183, 1600f), (184, -12f), (DriveOn, 1f), (161, 0.3f), (163, 0.5f) }),
        new("Gitarski flanger", "Guitar flanger",
            new[] { (FlangerOn, 1f), (211, 0.2f), (212, 0.8f), (213, 0.6f), (214, 0.5f), (ReverbOn, 1f), (273, 0.15f) }),
        new("Vibrafon tremolo", "Vibraphone tremolo",
            new[] { (TremoloOn, 1f), (231, 6f), (232, 0.45f), (ReverbOn, 1f), (273, 0.2f) }),
    };

    public static string[] FxPresetNames() => FxPresets.Select(p => L(p.Sr, p.En)).ToArray();

    // ------------------------------------------------------------------
    // The catalog
    // ------------------------------------------------------------------
    private static List<Section>? _sections;
    public static IReadOnlyList<Section> Sections => _sections ??= Build();

    public static IEnumerable<Param> AllParams => Sections.SelectMany(s => s.Params);

    public static Param? Find(int id) => AllParams.FirstOrDefault(p => p.Id == id);

    private static Param Toggle(int id, string sr, string en, bool persist = true)
        => new() { Id = id, Sr = sr, En = en, Kind = Kind.Toggle, Min = 0, Max = 1, Step = 1, BigStep = 1, Format = OnOff, Persist = persist };

    private static Param Choice(int id, string sr, string en, Func<string[]> choices, bool persist = true)
        => new() { Id = id, Sr = sr, En = en, Kind = Kind.Choice, Min = 0, Step = 1, BigStep = 1, Choices = choices, Persist = persist };

    private static Param Percent(int id, string sr, string en, float max = 1f)
        => new() { Id = id, Sr = sr, En = en, Kind = Kind.Number, Min = 0, Max = max, Step = 0.01f, BigStep = 0.1f, Format = Pct };

    private static Param Number(int id, string sr, string en, float min, float max, float step, float big, Func<float, string> format)
        => new() { Id = id, Sr = sr, En = en, Kind = Kind.Number, Min = min, Max = max, Step = step, BigStep = big, Format = format };

    private static Param Table(int id, string sr, string en, float[] table, Func<float, string> format)
        => new() { Id = id, Sr = sr, En = en, Kind = Kind.Number, Min = table[0], Max = table[^1], Table = table, Format = format };

    private static List<Section> Build()
    {
        var list = new List<Section>();

        var perf = new Section { Sr = "Izvođenje: pitch bend, vibrato, portamento, legato", En = "Performance: pitch bend, vibrato, portamento, legato" };
        perf.Params.Add(Choice(VoiceMode, "Način sviranja", "Voice mode", () => Serbian
            ? new[] { "polifono (više tonova odjednom)", "mono (jedan ton, svaki sa novim napadom)", "legato (jedan ton, vezano, bez novog napada)" }
            : new[] { "poly (several notes at once)", "mono (one note, each with a new attack)", "legato (one note, connected, no new attack)" }));
        perf.Params.Add(Toggle(PortamentoOn, "Portamento (klizanje između tonova)", "Portamento (glide between notes)"));
        perf.Params.Add(Table(PortamentoTime, "Vreme portamenta", "Portamento time", GlideTimes, Seconds));
        perf.Params.Add(Number(BendRange, "Opseg pitch bend-a", "Pitch bend range", 1, 24, 1, 12, SemisUnsigned));
        perf.Params.Add(Percent(ModWheel, "Modulacioni točkić (vibrato)", "Modulation wheel (vibrato)"));
        perf.Params.Add(Number(VibratoDepth, "Dubina vibrata na punom točkiću", "Vibrato depth at full wheel", 0, 2, 0.05f, 0.25f, SemisUnsigned));
        perf.Params.Add(Number(VibratoRate, "Brzina vibrata", "Vibrato speed", 1, 12, 0.1f, 1, Hz));
        perf.Params.Add(Number(Transpose, "Transpozicija", "Transpose", -12, 12, 1, 12, Semis));
        perf.Params.Add(Toggle(Sustain, "Sustain pedala (drži tonove)", "Sustain pedal (holds notes)", persist: false));
        list.Add(perf);

        var arp = new Section { Sr = "Arpeđator", En = "Arpeggiator", OnOffId = ArpOn };
        arp.Params.Add(Toggle(ArpOn, "Arpeđator", "Arpeggiator"));
        arp.Params.Add(Choice(ArpMode, "Smer", "Pattern", () => Serbian
            ? new[] { "naviše", "naniže", "naviše pa naniže", "nasumično", "redom kako sviraš", "ceo akord" }
            : new[] { "up", "down", "up and down", "random", "as played", "whole chord" }));
        arp.Params.Add(Choice(ArpRate, "Brzina (dužina note)", "Rate (note length)", () => Serbian
            ? new[] { "četvrtine (1/4)", "osmine (1/8)", "osminske triole (1/8T)", "šesnaestine (1/16)", "šesnaestinske triole (1/16T)", "tridesetdvojke (1/32)" }
            : new[] { "quarter notes (1/4)", "eighth notes (1/8)", "eighth triplets (1/8T)", "sixteenth notes (1/16)", "sixteenth triplets (1/16T)", "thirty-second notes (1/32)" }));
        arp.Params.Add(Number(ArpOctaves, "Opseg", "Range", 1, 4, 1, 1, v => Count(v, "oktava", "oktave", "oktava", "octave", "octaves")));
        arp.Params.Add(Number(ArpGate, "Dužina tona (gate)", "Note length (gate)", 0.1f, 1, 0.05f, 0.2f, Pct));
        arp.Params.Add(Toggle(ArpLatch, "Zadrži i kad pustiš tastere (latch)", "Keep playing after release (latch)"));
        arp.Params.Add(Number(ArpTempo, "Tempo arpeđatora", "Arpeggiator tempo", 40, 240, 1, 10, v => $"{N(v, "0")} BPM"));
        list.Add(arp);

        var fm = new Section { Sr = "Ultra FM sintisajzer", En = "Ultra FM synthesizer" };
        fm.Params.Add(Choice(FmPreset, "Zvuk (preset)", "Sound (preset)", FmPresetNames));
        fm.Params.Add(Choice(FmAlgorithm, "Algoritam", "Algorithm", AlgorithmNames));
        fm.Params.Add(Percent(FmVolume, "Jačina", "Volume"));
        fm.Params.Add(Number(FmBrightness, "Sjaj (svi modulatori)", "Brightness (all modulators)", 0, 2, 0.05f, 0.25f, Pct));
        fm.Params.Add(Percent(FmFeedback, "Povratna sprega operatora 6", "Operator 6 feedback"));
        list.Add(fm);

        for (int op = 1; op <= 6; op++)
        {
            int b = FmOpBase + (op - 1) * 10;
            var s = new Section { Sr = $"Ultra FM operator {op}", En = $"Ultra FM operator {op}", Operator = op };
            s.Params.Add(Percent(b + 0, "Jačina (nivo)", "Level"));
            s.Params.Add(Number(b + 1, "Odnos frekvencije (puta osnovni ton)", "Frequency ratio (times the note)", 0.5f, 16, 0.5f, 2, v => N(v)));
            s.Params.Add(Number(b + 2, "Fino štimovanje", "Fine detune", -50, 50, 1, 10, Cents));
            s.Params.Add(Table(b + 3, "Napad (attack)", "Attack", EnvTimes, Seconds));
            s.Params.Add(Table(b + 4, "Opadanje (decay)", "Decay", EnvTimes, Seconds));
            s.Params.Add(Percent(b + 5, "Zadržavanje (sustain)", "Sustain"));
            s.Params.Add(Table(b + 6, "Otpuštanje (release)", "Release", EnvTimes, Seconds));
            s.Params.Add(Percent(b + 7, "Osetljivost na jačinu udara", "Velocity sensitivity"));
            list.Add(s);
        }

        var fx = new Section { Sr = "Efekti: glavni prekidač i brza podešavanja", En = "Effects: master switch and quick presets", OnOffId = FxOn };
        fx.Params.Add(Toggle(FxOn, "Svi efekti", "All effects"));
        fx.Params.Add(Choice(FxPresetPseudoId, "Brzo podešavanje (Enter ili razmak primenjuje)", "Quick preset (Enter or Space applies)", FxPresetNames, persist: false));
        list.Add(fx);

        var drive = new Section { Sr = "Distorzija (overdrive)", En = "Overdrive", OnOffId = DriveOn };
        drive.Params.Add(Toggle(DriveOn, "Distorzija", "Overdrive"));
        drive.Params.Add(Percent(161, "Jačina distorzije", "Drive"));
        drive.Params.Add(Percent(162, "Boja tona (više = svetlije)", "Tone (higher = brighter)"));
        drive.Params.Add(Percent(163, "Izlazna jačina", "Output level"));
        list.Add(drive);

        var filter = new Section { Sr = "Filter i vau-vau (auto-wah)", En = "Filter and auto-wah", OnOffId = FilterOn };
        filter.Params.Add(Toggle(FilterOn, "Filter", "Filter"));
        filter.Params.Add(Choice(171, "Vrsta filtera", "Filter type", () => Serbian
            ? new[] { "niskopropusni, propušta duboke (low-pass)", "visokopropusni, propušta visoke (high-pass)", "pojasni, propušta sredinu (band-pass)" }
            : new[] { "low-pass", "high-pass", "band-pass" }));
        filter.Params.Add(Number(172, "Granična frekvencija (cutoff)", "Cutoff", 0, 1, 0.01f, 0.1f, CutoffHz));
        filter.Params.Add(Percent(173, "Rezonanca", "Resonance"));
        filter.Params.Add(Number(174, "Brzina LFO talasanja", "LFO speed", 0.1f, 20, 0.1f, 1, Hz));
        filter.Params.Add(Percent(175, "Dubina LFO talasanja", "LFO depth"));
        filter.Params.Add(Percent(176, "Praćenje jačine sviranja (auto-wah)", "Follows playing dynamics (auto-wah)"));
        list.Add(filter);

        var eq = new Section { Sr = "Ekvilajzer (3 opsega)", En = "Equalizer (3 bands)", OnOffId = EqOn };
        eq.Params.Add(Toggle(EqOn, "Ekvilajzer", "Equalizer"));
        eq.Params.Add(Number(181, "Basovi (200 Hz)", "Bass (200 Hz)", -15, 15, 0.5f, 3, Db));
        eq.Params.Add(Number(182, "Srednji", "Middle", -15, 15, 0.5f, 3, Db));
        eq.Params.Add(Table(183, "Frekvencija srednjih", "Middle frequency", EqFreqs, Hz));
        eq.Params.Add(Number(184, "Visoki (4 kHz)", "Treble (4 kHz)", -15, 15, 0.5f, 3, Db));
        list.Add(eq);

        var comp = new Section { Sr = "Kompresor", En = "Compressor", OnOffId = CompOn };
        comp.Params.Add(Toggle(CompOn, "Kompresor", "Compressor"));
        comp.Params.Add(Number(191, "Prag", "Threshold", -60, 0, 1, 6, Db));
        comp.Params.Add(Number(192, "Odnos kompresije", "Ratio", 1, 20, 0.5f, 2, v => $"{N(v)}:1"));
        comp.Params.Add(Table(193, "Napad", "Attack", CompAttacks, Ms));
        comp.Params.Add(Table(194, "Otpuštanje", "Release", CompReleases, Ms));
        comp.Params.Add(Number(195, "Pojačanje posle kompresije", "Make-up gain", 0, 24, 0.5f, 3, Db));
        list.Add(comp);

        var chorus = new Section { Sr = "Korus (chorus)", En = "Chorus", OnOffId = ChorusOn };
        chorus.Params.Add(Toggle(ChorusOn, "Korus", "Chorus"));
        chorus.Params.Add(Number(201, "Brzina", "Speed", 0.05f, 10, 0.05f, 0.5f, Hz));
        chorus.Params.Add(Percent(202, "Dubina", "Depth"));
        chorus.Params.Add(Percent(203, "Količina efekta", "Mix"));
        list.Add(chorus);

        var flanger = new Section { Sr = "Flanger", En = "Flanger", OnOffId = FlangerOn };
        flanger.Params.Add(Toggle(FlangerOn, "Flanger", "Flanger"));
        flanger.Params.Add(Number(211, "Brzina", "Speed", 0.02f, 5, 0.02f, 0.25f, Hz));
        flanger.Params.Add(Percent(212, "Dubina", "Depth"));
        flanger.Params.Add(Percent(213, "Povratna sprega", "Feedback"));
        flanger.Params.Add(Percent(214, "Količina efekta", "Mix"));
        list.Add(flanger);

        var phaser = new Section { Sr = "Fejzer (phaser)", En = "Phaser", OnOffId = PhaserOn };
        phaser.Params.Add(Toggle(PhaserOn, "Fejzer", "Phaser"));
        phaser.Params.Add(Number(221, "Brzina", "Speed", 0.02f, 8, 0.02f, 0.25f, Hz));
        phaser.Params.Add(Percent(222, "Dubina", "Depth"));
        phaser.Params.Add(Percent(223, "Povratna sprega", "Feedback"));
        phaser.Params.Add(Percent(224, "Količina efekta", "Mix"));
        list.Add(phaser);

        var tremolo = new Section { Sr = "Tremolo", En = "Tremolo", OnOffId = TremoloOn };
        tremolo.Params.Add(Toggle(TremoloOn, "Tremolo", "Tremolo"));
        tremolo.Params.Add(Number(231, "Brzina", "Speed", 0.1f, 20, 0.1f, 1, Hz));
        tremolo.Params.Add(Percent(232, "Dubina", "Depth"));
        list.Add(tremolo);

        var pan = new Section { Sr = "Auto-pan (šetanje levo-desno)", En = "Auto-pan", OnOffId = AutoPanOn };
        pan.Params.Add(Toggle(AutoPanOn, "Auto-pan", "Auto-pan"));
        pan.Params.Add(Number(241, "Brzina", "Speed", 0.05f, 20, 0.05f, 0.5f, Hz));
        pan.Params.Add(Percent(242, "Širina šetanja", "Width"));
        list.Add(pan);

        var rotary = new Section { Sr = "Rotacioni zvučnik (Lesli)", En = "Rotary speaker (Leslie)", OnOffId = RotaryOn };
        rotary.Params.Add(Toggle(RotaryOn, "Rotacioni zvučnik", "Rotary speaker"));
        rotary.Params.Add(Choice(251, "Brzina okretanja", "Rotor speed", () => Serbian
            ? new[] { "sporo (chorale)", "brzo (tremolo)" } : new[] { "slow (chorale)", "fast (tremolo)" }));
        rotary.Params.Add(Percent(252, "Količina efekta", "Mix"));
        list.Add(rotary);

        var delay = new Section { Sr = "Eho (delay)", En = "Echo (delay)", OnOffId = DelayOn };
        delay.Params.Add(Toggle(DelayOn, "Eho", "Echo"));
        delay.Params.Add(Number(261, "Vreme između ponavljanja", "Time between repeats", 20, 2000, 10, 100, Ms));
        delay.Params.Add(Number(262, "Broj ponavljanja (povratna sprega)", "Repeats (feedback)", 0, 0.95f, 0.01f, 0.1f, Pct));
        delay.Params.Add(Percent(263, "Jačina eha", "Echo level"));
        delay.Params.Add(Toggle(264, "Levo-desno (ping-pong)", "Left-right (ping-pong)"));
        delay.Params.Add(Percent(265, "Prigušenje visokih u ehu", "High-cut in the echo"));
        list.Add(delay);

        var reverb = new Section { Sr = "Reverb (prostor)", En = "Reverb (room)", OnOffId = ReverbOn };
        reverb.Params.Add(Toggle(ReverbOn, "Reverb", "Reverb"));
        reverb.Params.Add(Percent(271, "Veličina prostora", "Room size"));
        reverb.Params.Add(Percent(272, "Prigušenje visokih", "Damping"));
        reverb.Params.Add(Percent(273, "Količina reverba", "Reverb amount"));
        reverb.Params.Add(Percent(274, "Stereo širina", "Stereo width"));
        list.Add(reverb);

        return list;
    }

    // ------------------------------------------------------------------
    // Reading and stepping values
    // ------------------------------------------------------------------
    public static string FormatValue(Param p, float value)
    {
        if (p.Kind == Kind.Choice && p.Choices != null)
        {
            var c = p.Choices();
            int i = Math.Clamp((int)Math.Round(value), 0, c.Length - 1);
            return c[i];
        }
        return p.Format != null ? p.Format(value) : N(value);
    }

    public static float MaxOf(Param p) => p.Kind == Kind.Choice && p.Choices != null ? p.Choices().Length - 1 : p.Max;

    /// <summary>Next value for <paramref name="steps"/> small steps (or big steps if <paramref name="big"/>), clamped to the range.</summary>
    public static float StepValue(Param p, float current, int steps, bool big)
    {
        if (p.Kind == Kind.Toggle) return steps > 0 ? 1 : 0;
        if (p.Kind == Kind.Choice)
        {
            int count = (int)MaxOf(p) + 1;
            int i = (int)Math.Round(current) + steps;
            return Math.Clamp(i, 0, count - 1);
        }
        if (p.Table != null)
        {
            int nearest = 0;
            for (int i = 1; i < p.Table.Length; i++)
                if (Math.Abs(p.Table[i] - current) < Math.Abs(p.Table[nearest] - current)) nearest = i;
            int move = big ? steps * 3 : steps;
            return p.Table[Math.Clamp(nearest + move, 0, p.Table.Length - 1)];
        }
        float step = big ? p.BigStep : p.Step;
        float v = current + steps * step;
        v = (float)(Math.Round(v / p.Step) * p.Step); // stay on the grid, no 0.30000001
        return Math.Clamp(v, p.Min, p.Max);
    }
}
