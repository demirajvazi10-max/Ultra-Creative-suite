using System.IO;
using System.Windows;
using System.Windows.Input;
using UltraAudioEditor.Localization;
using UltraAudioEditor.Models;
using UltraAudioEditor.Services;
using UltraAudioEditor.Services.Dsp;

namespace UltraAudioEditor.ViewModels
{
    // ════════════════════════════════════════════════════════════════════════
    // "Sound Forge" deo editora: izbor (selekcija), meni Obrada, restauracija,
    // statistika, uklanjanje tišina, kriva jačine, redosled efekata, primena
    // efekta na izbor, transkripcija, grupna konverzija i oznake pesme.
    //
    // Pravilo za obradu: radi na IZABRANOJ traci (ako nijedna nije izabrana — na
    // svim trakama sa zvukom), u opsegu IZBORA (ako izbora nema — izabrani klip,
    // a ako ni njega nema — cela traka). Obrađeni deo se upisuje u novi WAV u
    // Dokumenti\Ultra Audio Editor\Renders, originalni fajlovi se nikad ne menjaju,
    // a Ctrl+Z vraća sve.
    // ════════════════════════════════════════════════════════════════════════
    public partial class MainViewModel
    {
        // ─── Izbor ──────────────────────────────────────────────────────────
        private double? _selStart, _selEnd;
        public double? SelectionStart { get => _selStart; private set { _selStart = value; SelChanged(); } }
        public double? SelectionEnd { get => _selEnd; private set { _selEnd = value; SelChanged(); } }
        private void SelChanged()
        {
            OnPropertyChanged(nameof(SelectionStart)); OnPropertyChanged(nameof(SelectionEnd));
            OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(SelectionStartOrNaN)); OnPropertyChanged(nameof(SelectionEndOrNaN));
        }
        // Za vizuelni prikaz izbora na talasnom obliku (NaN = nema izbora)
        public double SelectionStartOrNaN => HasSelection ? _selStart!.Value : double.NaN;
        public double SelectionEndOrNaN => HasSelection ? _selEnd!.Value : double.NaN;
        public bool HasSelection => _selStart.HasValue && _selEnd.HasValue && _selEnd.Value - _selStart.Value > 0.005;
        public string SelectionText => HasSelection
            ? string.Format(Lang.T("sel_text"), Fmt(_selStart!.Value), Fmt(_selEnd!.Value), _selEnd.Value - _selStart.Value)
            : Lang.T("sel_none");

        private float[]? _noiseProfile;

        // ─── Kretanje kroz vreme ────────────────────────────────────────────
        public ICommand ForwardCommand { get; private set; } = null!;
        public ICommand BackCommand { get; private set; } = null!;
        public ICommand ForwardFineCommand { get; private set; } = null!;
        public ICommand BackFineCommand { get; private set; } = null!;
        public ICommand GoToTimeCommand { get; private set; } = null!;

        private bool _scrubEnabled = true;
        /// <summary>Preslušavanje pri pomeranju: svaki korak pusti ~0,3 s od nove pozicije (kad muzika ne svira).</summary>
        public bool ScrubEnabled { get => _scrubEnabled; set { _scrubEnabled = value; OnPropertyChanged(); } }
        private bool _scrubbing;
        private System.Windows.Threading.DispatcherTimer? _scrubTimer, _seekRestartTimer;

        /// <summary>
        /// Pomeri poziciju. Dok muzika svira, reprodukcija se odmah nastavlja sa novog mesta
        /// (ranije je pozicija skakala nazad jer je engine nastavljao sa starog). Kad ne svira,
        /// pusti se kratak delić sa nove pozicije da se čuje gde si.
        /// </summary>
        public void SeekTo(double pos, bool announce = true)
        {
            double max = Math.Max(Project.ContentEnd, 0);
            pos = Math.Clamp(pos, 0, max);
            StopPreviewTimer();
            _selPlayTimer?.Stop();
            PlayheadPosition = pos;
            if (IsRecording) return;
            if (IsPlaying)
            {
                // ponovno pokretanje tek kad se taster pusti (bez zvuka "mitraljeza" pri držanju)
                _seekRestartTimer?.Stop();
                _seekRestartTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _seekRestartTimer.Tick += (_, __) =>
                {
                    _seekRestartTimer?.Stop();
                    if (IsPlaying) _engine.Play(Project, PlayheadPosition);
                };
                _seekRestartTimer.Start();
                _engine.Pause();   // odmah utihne staro mesto
            }
            else if (ScrubEnabled) Scrub(pos);
            if (announce) Announce(string.Format(Lang.T("pos_at"), Fmt(pos)));
        }

        public void SeekBy(double delta) => SeekTo(PlayheadPosition + delta);

        private void Scrub(double pos)
        {
            _scrubTimer?.Stop();
            _scrubbing = true;
            _engine.Play(Project, pos);
            _scrubTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _scrubTimer.Tick += (_, __) =>
            {
                _scrubTimer?.Stop();
                if (!IsPlaying) _engine.Stop();
                _scrubbing = false;
            };
            _scrubTimer.Start();
        }

        private void GoToTime()
        {
            var d = new Views.SetValueDialog(Lang.T("goto_title"), Lang.T("goto_prompt"), Fmt(PlayheadPosition), "") { Owner = Application.Current?.MainWindow };
            if (d.ShowDialog() != true) return;
            double p = Views.Controls.AccessibleTrackList.ParsePos(d.ResultValue);
            if (p < 0) { Announce(Lang.T("goto_invalid")); return; }
            SeekTo(p);
        }
        private System.Windows.Threading.DispatcherTimer? _selPlayTimer;

        public ICommand SelStartCommand { get; private set; } = null!;
        public ICommand SelEndCommand { get; private set; } = null!;
        public ICommand SelectAllCommand { get; private set; } = null!;
        public ICommand SelectClipRangeCommand { get; private set; } = null!;
        public ICommand SelectBetweenMarkersCommand { get; private set; } = null!;
        public ICommand ClearSelectionCommand { get; private set; } = null!;
        public ICommand PlaySelectionCommand { get; private set; } = null!;
        public ICommand CutSelectionCommand { get; private set; } = null!;
        public ICommand DeleteSelectionCommand { get; private set; } = null!;
        public ICommand TrimToSelectionCommand { get; private set; } = null!;
        public ICommand SilenceSelectionCommand { get; private set; } = null!;
        // Obrada
        public ICommand GainCommand { get; private set; } = null!;
        public ICommand NormalizePeakCommand { get; private set; } = null!;
        public ICommand NormalizeLufsCommand { get; private set; } = null!;
        public ICommand FadeInProCommand { get; private set; } = null!;
        public ICommand FadeOutProCommand { get; private set; } = null!;
        public ICommand ReverseCommand { get; private set; } = null!;
        public ICommand InsertSilenceCommand { get; private set; } = null!;
        public ICommand ToMonoCommand { get; private set; } = null!;
        public ICommand SwapChannelsCommand { get; private set; } = null!;
        public ICommand RemoveDcCommand { get; private set; } = null!;
        public ICommand HighPassCommand { get; private set; } = null!;
        public ICommand LowPassCommand { get; private set; } = null!;
        public ICommand RemoveHumCommand { get; private set; } = null!;
        public ICommand RemoveClicksCommand { get; private set; } = null!;
        public ICommand LearnNoiseCommand { get; private set; } = null!;
        public ICommand ReduceNoiseCommand { get; private set; } = null!;
        public ICommand RemoveSilencesCommand { get; private set; } = null!;
        public ICommand ApplyEffectToSelectionCommand { get; private set; } = null!;
        public ICommand StatisticsCommand { get; private set; } = null!;
        // Alati
        public ICommand EnvelopeCommand { get; private set; } = null!;
        public ICommand EffectOrderCommand { get; private set; } = null!;
        public ICommand TagsCommand { get; private set; } = null!;
        public ICommand TranscribeLocalCommand { get; private set; } = null!;
        public ICommand BatchConvertCommand { get; private set; } = null!;

        /// <summary>Poziva se iz konstruktora (MainViewModel.cs).</summary>
        private void InitProCommands()
        {
            // Kratka zaštita (25 ms): isti pritisak koji stigne i iz liste i iz prozora računa
            // se jednom, a držanje tastera (ponavljanje ~30 ms) i dalje radi.
            ForwardCommand = new RelayCommand(() => { if (Gate("seek", 25)) SeekBy(1.0); });
            BackCommand = new RelayCommand(() => { if (Gate("seek", 25)) SeekBy(-1.0); });
            ForwardFineCommand = new RelayCommand(() => { if (Gate("seek", 25)) SeekBy(0.1); });
            BackFineCommand = new RelayCommand(() => { if (Gate("seek", 25)) SeekBy(-0.1); });
            GoToTimeCommand = Cmd("goto", GoToTime);
            SelStartCommand = Cmd("selstart", () => SetSelectionEdge(true));
            SelEndCommand = Cmd("selend", () => SetSelectionEdge(false));
            SelectAllCommand = Cmd("selall", () => SetSelection(0, Project.ContentEnd, Lang.T("sel_all")));
            SelectClipRangeCommand = Cmd("selclip", SelectClipRange);
            SelectBetweenMarkersCommand = Cmd("selmarkers", SelectBetweenMarkers);
            ClearSelectionCommand = Cmd("selclear", () => { if (HasSelection) { SelectionStart = SelectionEnd = null; NotifyEdited(); Announce(Lang.T("sel_cleared")); } });
            PlaySelectionCommand = Cmd("selplay", PlaySelection);
            CutSelectionCommand = Cmd("selcut", () => { if (RequireSelection()) { CopySelectionToClipboard(); RippleDeleteSelection(); } });
            DeleteSelectionCommand = Cmd("seldel", () => { if (RequireSelection()) RippleDeleteSelection(); });
            TrimToSelectionCommand = Cmd("seltrim", TrimToSelection);
            SilenceSelectionCommand = Cmd("selsilence", () => _ = ProcessAsync(Lang.T("op_silence"), (b, sr) => { Processing.Silence(b); return null; }));

            GainCommand = Cmd("gain", () =>
            {
                var v = AskNumber(Lang.T("op_gain"), Lang.T("op_gain_prompt"), "3", "dB", -48, 48);
                if (v != null) _ = ProcessAsync(Lang.T("op_gain"), (b, sr) => { Processing.Gain(b, v.Value); return string.Format(Lang.T("op_gain_done"), v.Value); });
            });
            NormalizePeakCommand = Cmd("normpeak", () =>
            {
                var v = AskNumber(Lang.T("op_norm_peak"), Lang.T("op_norm_peak_prompt"), "-1", "dB", -40, 0);
                if (v != null) _ = ProcessAsync(Lang.T("op_norm_peak"), (b, sr) => string.Format(Lang.T("op_gain_done"), Processing.NormalizePeak(b, v.Value)));
            });
            NormalizeLufsCommand = Cmd("normlufs", () =>
            {
                double[] targets = { -14, -16, -19, -23 };
                int i = Choose(Lang.T("op_norm_lufs"), Lang.T("op_norm_lufs_prompt"),
                    new[] { Lang.T("lufs_14"), Lang.T("lufs_16"), Lang.T("lufs_19"), Lang.T("lufs_23") }, 0);
                if (i < 0) return;
                double t = targets[i];
                _ = ProcessAsync(Lang.T("op_norm_lufs"), (b, sr) =>
                {
                    var (g, res, limited) = Processing.NormalizeLufs(b, sr, t, -1);
                    return string.Format(limited ? Lang.T("op_lufs_done_limited") : Lang.T("op_lufs_done"), res, g);
                });
            });
            FadeInProCommand = Cmd("fadein", () => Fade(true));
            FadeOutProCommand = Cmd("fadeout", () => Fade(false));
            ReverseCommand = Cmd("reverse", () => _ = ProcessAsync(Lang.T("op_reverse"), (b, sr) => { Processing.Reverse(b); return null; }));
            InsertSilenceCommand = Cmd("inssilence", InsertSilence);
            ToMonoCommand = Cmd("mono", () => _ = ProcessAsync(Lang.T("op_mono"), (b, sr) => { Processing.ToMono(b); return null; }));
            SwapChannelsCommand = Cmd("swap", () => _ = ProcessAsync(Lang.T("op_swap"), (b, sr) => { Processing.SwapChannels(b); return null; }));
            RemoveDcCommand = Cmd("dc", () => _ = ProcessAsync(Lang.T("op_dc"), (b, sr) => { Processing.RemoveDc(b); return null; }));
            HighPassCommand = Cmd("hp", () =>
            {
                var v = AskNumber(Lang.T("op_highpass"), Lang.T("op_highpass_prompt"), "80", "Hz", 10, 2000);
                if (v != null) _ = ProcessAsync(Lang.T("op_highpass"), (b, sr) => { Processing.HighPass(b, sr, (float)v.Value); return null; });
            });
            LowPassCommand = Cmd("lp", () =>
            {
                var v = AskNumber(Lang.T("op_lowpass"), Lang.T("op_lowpass_prompt"), "12000", "Hz", 500, 20000);
                if (v != null) _ = ProcessAsync(Lang.T("op_lowpass"), (b, sr) => { Processing.LowPass(b, sr, (float)v.Value); return null; });
            });
            RemoveHumCommand = Cmd("hum", () =>
            {
                int i = Choose(Lang.T("op_hum"), Lang.T("op_hum_prompt"), new[] { Lang.T("hum_50"), Lang.T("hum_60") }, 0);
                if (i >= 0) _ = ProcessAsync(Lang.T("op_hum"), (b, sr) => { Processing.RemoveHum(b, sr, i == 0 ? 50 : 60); return null; });
            });
            RemoveClicksCommand = Cmd("clicks", () =>
            {
                int i = Choose(Lang.T("op_clicks"), Lang.T("op_clicks_prompt"), new[] { Lang.T("lvl_gentle"), Lang.T("lvl_medium"), Lang.T("lvl_strong") }, 1);
                if (i >= 0) _ = ProcessAsync(Lang.T("op_clicks"), (b, sr) => string.Format(Lang.T("op_clicks_done"), Processing.RemoveClicks(b, sr, 1 + i)));
            });
            LearnNoiseCommand = Cmd("learnnoise", LearnNoise);
            ReduceNoiseCommand = Cmd("reducenoise", () =>
            {
                if (_noiseProfile == null || _noiseProfile.Length == 0) { Announce(Lang.T("noise_need_profile")); return; }
                int i = Choose(Lang.T("op_noise"), Lang.T("op_noise_prompt"), new[] { Lang.T("noise_lvl_1"), Lang.T("noise_lvl_2"), Lang.T("noise_lvl_3") }, 1);
                if (i < 0) return;
                double red = new[] { 8.0, 14.0, 22.0 }[i], sens = new[] { 1.0, 1.5, 2.2 }[i];
                var prof = _noiseProfile;
                _ = ProcessAsync(Lang.T("op_noise"), (b, sr) => { Processing.ReduceNoise(b, prof, red, sens); return null; });
            });
            RemoveSilencesCommand = Cmd("silences", () => _ = RemoveSilencesAsync());
            ApplyEffectToSelectionCommand = Cmd("applyfx", ApplyEffectToSelection);
            StatisticsCommand = Cmd("stats", () => _ = StatisticsAsync());

            EnvelopeCommand = Cmd("envelope", () =>
            {
                if (SelectedTrack == null) { Announce(Lang.T("fx_select_track")); return; }
                PushUndo();
                new Views.EnvelopeWindow(this, SelectedTrack) { Owner = Application.Current?.MainWindow }.ShowDialog();
                NotifyEdited();
            });
            EffectOrderCommand = Cmd("fxorder", () =>
            {
                if (SelectedTrack == null) { Announce(Lang.T("fx_select_track")); return; }
                PushUndo();
                new Views.EffectOrderWindow(SelectedTrack) { Owner = Application.Current?.MainWindow }.ShowDialog();
                Announce(string.Format(Lang.T("fxorder_note"), SelectedTrack.Name));
            });
            TagsCommand = Cmd("tags", () =>
            {
                new Views.TagsDialog(Project.Tags) { Owner = Application.Current?.MainWindow }.ShowDialog();
            });
            TranscribeLocalCommand = Cmd("whisper", () => _ = TranscribeAsync());
            BatchConvertCommand = Cmd("batch", () => new Views.BatchConvertWindow(Project.SampleRate) { Owner = Application.Current?.MainWindow }.Show());
        }

        // ─── Pomoćni dijalozi ───────────────────────────────────────────────
        private static double? AskNumber(string title, string prompt, string def, string unit, double min, double max)
        {
            var d = new Views.SetValueDialog(title, prompt, def, unit) { Owner = Application.Current?.MainWindow };
            if (d.ShowDialog() != true) return null;
            if (!double.TryParse(d.ResultValue.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v)) return null;
            return Math.Clamp(v, min, max);
        }

        private static int Choose(string title, string prompt, string[] options, int def)
        {
            var d = new Views.ChoiceDialog(title, prompt, options, def) { Owner = Application.Current?.MainWindow };
            return d.ShowDialog() == true ? d.SelectedIndex : -1;
        }

        // ─── Izbor ──────────────────────────────────────────────────────────
        private void SetSelection(double a, double b, string? how = null)
        {
            if (b < a) (a, b) = (b, a);
            SelectionStart = Math.Max(0, a); SelectionEnd = Math.Max(0, b);
            NotifyEdited();
            Announce((how != null ? how + " " : "") + SelectionText);
        }

        /// <summary>I = početak izbora na poziciji, O = kraj izbora na poziciji.</summary>
        private void SetSelectionEdge(bool start)
        {
            double ph = PlayheadPosition;
            if (start)
            {
                SelectionStart = ph;
                if (!_selEnd.HasValue || _selEnd.Value <= ph) SelectionEnd = null;
                NotifyEdited();
                Announce(HasSelection ? SelectionText : string.Format(Lang.T("sel_start_set"), Fmt(ph)));
            }
            else
            {
                SelectionEnd = ph;
                if (!_selStart.HasValue || _selStart.Value >= ph) SelectionStart = 0;   // bez početka: izbor od početka projekta
                NotifyEdited();
                Announce(SelectionText);
            }
        }

        private void SelectClipRange()
        {
            var c = SelectedClip ?? SelectedTrack?.Clips.FirstOrDefault(x => x.StartTime <= PlayheadPosition && x.EndTime > PlayheadPosition);
            if (c == null) { Announce(Lang.T("clip_select_first")); return; }
            SetSelection(c.StartTime, c.EndTime, Lang.T("sel_clip_done"));
        }

        private void SelectBetweenMarkers()
        {
            double ph = PlayheadPosition;
            double? a = Project.Markers.Where(m => m <= ph + 0.001).Cast<double?>().LastOrDefault();
            double? b = Project.Markers.Where(m => m > ph + 0.001).Cast<double?>().FirstOrDefault();
            if (a == null || b == null) { Announce(Lang.T("ripple_need_markers")); return; }
            SetSelection(a.Value, b.Value, Lang.T("sel_markers_done"));
        }

        private bool RequireSelection()
        {
            if (HasSelection) return true;
            Announce(Lang.T("sel_required"));
            return false;
        }

        /// <summary>Shift+Space — pusti samo izbor (uz Loop se ponavlja).</summary>
        private void PlaySelection()
        {
            if (!RequireSelection()) return;
            double a = _selStart!.Value, b = _selEnd!.Value;
            StopPreviewTimer();
            _selPlayTimer?.Stop();
            _engine.Stop();
            _engine.Play(Project, a);
            IsPlaying = true;
            _selPlayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _selPlayTimer.Tick += (_, __) =>
            {
                if (!IsPlaying) { _selPlayTimer?.Stop(); return; }
                if (PlayheadPosition >= b)
                {
                    _engine.Stop();
                    if (IsLooping && HasSelection) { _engine.Play(Project, a); PlayheadPosition = a; }
                    else { _selPlayTimer?.Stop(); IsPlaying = false; PlayheadPosition = a; }
                }
            };
            _selPlayTimer.Start();
        }

        // ─── Rezanje opsega na traci ────────────────────────────────────────
        /// <summary>
        /// Izreže opseg [A, B] sa trake (klipovi se skraćuju/dele). shift > 0 pomera sve
        /// posle B unazad za toliko (ripple); shift = 0 ostavlja prazninu.
        /// </summary>
        private void CarveRange(AudioTrack track, double A, double B, double shift)
        {
            foreach (var c in track.Clips.ToList())
            {
                if (c.SourceDuration <= 0) c.SourceDuration = c.TrimStart + c.Duration;
                double s = c.StartTime, e = c.EndTime;
                if (e <= A + Eps) continue;
                if (s >= B - Eps) { c.StartTime = s - shift; continue; }
                if (s >= A - Eps && e <= B + Eps) { track.Clips.Remove(c); continue; }
                if (s < A && e > B)
                {
                    var right = c.CloneWithNewId();
                    right.StartTime = B - shift;
                    right.TrimStart = c.TrimStart + (B - s);
                    right.Duration = e - B;
                    c.Duration = A - s;
                    track.Clips.Insert(track.Clips.IndexOf(c) + 1, right);
                    continue;
                }
                if (s < A) { c.Duration = A - s; continue; }
                c.TrimStart += B - s;
                c.Duration = e - B;
                c.StartTime = B - shift;
            }
        }

        /// <summary>Izbaci [A, B] sa SVIH traka i zatvori rupu (vokal i podloga ostaju sinhronizovani).</summary>
        private void RippleDeleteRange(double A, double B)
        {
            double gap = B - A;
            foreach (var t in Project.Tracks) CarveRange(t, A, B, gap);
            var kept = Project.Markers.Where(m => m < A - 0.001 || m > B + 0.001).Select(m => m > B ? m - gap : m).ToList();
            Project.Markers.Clear();
            foreach (var m in kept) Project.Markers.Add(m);
            if (SelectedClip != null && !Project.Tracks.Any(t => t.Clips.Contains(SelectedClip)))
            { SuppressSelectionAnnounce = true; SelectedClip = null; SuppressSelectionAnnounce = false; }
        }

        private void RippleDeleteSelection()
        {
            double A = _selStart!.Value, B = _selEnd!.Value;
            PushUndo();
            RippleDeleteRange(A, B);
            SelectionStart = SelectionEnd = null;
            PlayheadPosition = A;
            NotifyEdited();
            Announce(string.Format(Lang.T("sel_deleted"), B - A, Fmt(A)));
        }

        private void TrimToSelection()
        {
            if (!RequireSelection()) return;
            double A = _selStart!.Value, B = _selEnd!.Value, end = Project.ContentEnd;
            PushUndo();
            if (end > B) RippleDeleteRange(B, end + 1);
            if (A > 0) RippleDeleteRange(0, A);
            SelectionStart = 0; SelectionEnd = B - A;
            PlayheadPosition = 0;
            NotifyEdited();
            Announce(string.Format(Lang.T("sel_trimmed"), B - A));
        }

        /// <summary>Ctrl+C dok postoji izbor: deo izabrane trake postaje jedan klip za lepljenje.</summary>
        private bool CopySelectionToClipboard()
        {
            if (!HasSelection) return false;
            var track = SelectedTrack;
            if (track == null) { Announce(Lang.T("fx_select_track")); return true; }
            double A = _selStart!.Value, B = _selEnd!.Value;
            var buf = AudioBuffers.ReadTrackRange(track, A, B, Project.SampleRate);
            var path = AudioBuffers.WriteRender(buf, Project.SampleRate, "copy");
            double len = buf.Length / 2.0 / Project.SampleRate;
            _clipboard = new AudioClip
            {
                Name = string.Format(Lang.T("sel_clip_name"), track.Name), FilePath = path,
                Duration = len, SourceDuration = len, WaveformData = GetWaveform(path)
            };
            Announce(string.Format(Lang.T("sel_copied"), len, track.Name));
            return true;
        }

        private void InsertSilence()
        {
            var v = AskNumber(Lang.T("op_ins_silence"), Lang.T("op_ins_silence_prompt"), "1", "s", 0.01, 600);
            if (v == null) return;
            double at = PlayheadPosition, len = v.Value;
            PushUndo();
            foreach (var t in Project.Tracks)
            {
                var c = t.Clips.FirstOrDefault(x => Covers(x, at));
                if (c != null) SplitClip(t, c, at);
                foreach (var x in t.Clips) if (x.StartTime >= at - Eps) x.StartTime += len;
            }
            var ms = Project.Markers.Select(m => m >= at ? m + len : m).ToList();
            Project.Markers.Clear(); foreach (var m in ms) Project.Markers.Add(m);
            NotifyEdited();
            Announce(string.Format(Lang.T("op_ins_silence_done"), len, Fmt(at)));
        }

        // ─── Obrada (Sound Forge stil) ──────────────────────────────────────
        /// <summary>Koje trake i koji opseg obrada pogađa (vidi pravilo na vrhu fajla).</summary>
        private List<(AudioTrack track, double a, double b)> ProcessTargets(out string scope)
        {
            var tracks = SelectedTrack != null ? new List<AudioTrack> { SelectedTrack }
                                               : Project.Tracks.Where(t => t.Clips.Count > 0).ToList();
            var list = new List<(AudioTrack, double, double)>();
            if (HasSelection)
            {
                scope = string.Format(Lang.T("scope_selection"), _selEnd!.Value - _selStart!.Value);
                foreach (var t in tracks) list.Add((t, _selStart.Value, _selEnd.Value));
            }
            else if (SelectedClip != null && SelectedTrack != null && SelectedTrack.Clips.Contains(SelectedClip))
            {
                scope = string.Format(Lang.T("scope_clip"), SelectedClip.Name);
                list.Add((SelectedTrack, SelectedClip.StartTime, SelectedClip.EndTime));
            }
            else
            {
                scope = SelectedTrack != null ? string.Format(Lang.T("scope_track"), SelectedTrack.Name) : Lang.T("scope_all");
                foreach (var t in tracks.Where(t => t.Clips.Count > 0))
                    list.Add((t, t.Clips.Min(c => c.StartTime), t.Clips.Max(c => c.EndTime)));
            }
            return list.Where(x => x.Item3 - x.Item2 > 0.005 && x.Item1.Clips.Any(c => c.StartTime < x.Item3 && c.EndTime > x.Item2)).ToList();
        }

        private bool _processing;

        /// <summary>Pročitaj opseg, obradi, upiši novi WAV, vrati na traku kao jedan klip.</summary>
        private async Task ProcessAsync(string label, Func<float[], int, string?> op)
        {
            if (_processing) { Announce(Lang.T("op_busy")); return; }
            var targets = ProcessTargets(out string scope);
            if (targets.Count == 0) { Announce(Lang.T("op_nothing")); return; }
            if (IsPlaying) { _engine.Stop(); IsPlaying = false; }
            _processing = true;
            PushUndo();
            Announce(string.Format(Lang.T("op_running"), label));
            string? extra = null;
            int sr = Project.SampleRate;
            try
            {
                foreach (var (track, a, b) in targets)
                {
                    var (path, len, msg) = await Task.Run(() =>
                    {
                        var buf = AudioBuffers.ReadTrackRange(track, a, b, sr);
                        var m = op(buf, sr);
                        var p = AudioBuffers.WriteRender(buf, sr, label);
                        return (p, buf.Length / 2.0 / sr, m);
                    });
                    extra ??= msg;
                    CarveRange(track, a, b, 0);
                    var clip = new AudioClip
                    {
                        Name = $"{track.Name} — {label}", FilePath = path, StartTime = a,
                        Duration = len, SourceDuration = len, WaveformData = GetWaveform(path)
                    };
                    track.Clips.Add(clip);
                    if (SelectedTrack == track) { SuppressSelectionAnnounce = true; SelectedClip = clip; SuppressSelectionAnnounce = false; }
                }
                NotifyEdited();
                Announce(string.Format(Lang.T("op_done"), label, scope) + (extra != null ? " " + extra : ""));
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Process '{label}' failed: {ex}");
                Announce(Lang.T("operation_failed"));
            }
            finally { _processing = false; }
        }

        private void Fade(bool fadeIn)
        {
            double? seconds = null;
            if (!HasSelection)
            {
                seconds = AskNumber(fadeIn ? Lang.T("op_fade_in") : Lang.T("op_fade_out"), Lang.T("op_fade_len_prompt"), "2", "s", 0.01, 600);
                if (seconds == null) return;
            }
            int c = Choose(fadeIn ? Lang.T("op_fade_in") : Lang.T("op_fade_out"), Lang.T("op_fade_curve_prompt"),
                new[] { Lang.T("curve_linear"), Lang.T("curve_fast"), Lang.T("curve_slow"), Lang.T("curve_s") }, 3);
            if (c < 0) return;
            var curve = (FadeCurve)c;
            _ = ProcessAsync(fadeIn ? Lang.T("op_fade_in") : Lang.T("op_fade_out"), (b, sr) =>
            {
                double len = seconds ?? b.Length / 2.0 / sr;
                if (fadeIn) Processing.FadeIn(b, sr, len, curve); else Processing.FadeOut(b, sr, len, curve);
                return null;
            });
        }

        private void LearnNoise()
        {
            var track = SelectedTrack;
            if (track == null || !HasSelection) { Announce(Lang.T("noise_learn_how")); return; }
            var buf = AudioBuffers.ReadTrackRange(track, _selStart!.Value, _selEnd!.Value, Project.SampleRate);
            if (buf.Length / 2 < Processing.NoiseFft * 2) { Announce(Lang.T("noise_too_short")); return; }
            _noiseProfile = Processing.LearnNoiseProfile(buf);
            Announce(string.Format(Lang.T("noise_learned"), _selEnd.Value - _selStart.Value));
        }

        // ─── Statistika (F7) ────────────────────────────────────────────────
        private async Task StatisticsAsync()
        {
            int sr = Project.SampleRate;
            float[] buf;
            string scope;
            if (SelectedTrack != null)
            {
                var targets = ProcessTargets(out scope);
                if (targets.Count == 0) { Announce(Lang.T("op_nothing")); return; }
                var (t, a, b) = targets[0];
                buf = await Task.Run(() => AudioBuffers.ReadTrackRange(t, a, b, sr));
            }
            else
            {
                // ceo miks, kako se čuje (sa efektima), ili deo miksa u izboru
                if (!Project.Tracks.Any(t => t.Clips.Count > 0)) { Announce(Lang.T("op_nothing")); return; }
                Announce(Lang.T("stats_running"));
                var mix = await Task.Run(() => AudioEngine.RenderMix(Project));
                try
                {
                    buf = await Task.Run(() => AudioBuffers.ReadWholeFile(mix.TempPath, sr));
                    if (HasSelection)
                    {
                        int a = (int)(_selStart!.Value * sr) * 2, b = Math.Min(buf.Length, (int)(_selEnd!.Value * sr) * 2);
                        buf = a < b ? buf[a..b] : Array.Empty<float>();
                        scope = string.Format(Lang.T("scope_mix_selection"), _selEnd.Value - _selStart.Value);
                    }
                    else scope = Lang.T("scope_mix");
                }
                finally { try { File.Delete(mix.TempPath); } catch { } }
            }
            var s = await Task.Run(() => Processing.Analyze(buf, sr));
            var msg = string.Format(Lang.T("stats_main"), scope, s.Lufs, s.PeakDb, s.RmsDb, s.LoudestMomentLufs);
            msg += " " + (s.ClippedSamples > 0
                ? string.Format(Lang.T("stats_clipped"), s.ClippedSamples, Fmt(s.FirstClipSeconds))
                : Lang.T("stats_no_clip"));
            if (s.SilentPercent >= 1) msg += " " + string.Format(Lang.T("stats_silence"), s.SilentPercent);
            if (s.DcOffsetPercent >= 0.5) msg += " " + string.Format(Lang.T("stats_dc"), s.DcOffsetPercent);
            Announce(msg);
        }

        // ─── Uklanjanje tišina ──────────────────────────────────────────────
        private async Task RemoveSilencesAsync()
        {
            if (!Project.Tracks.Any(t => t.Clips.Count > 0)) { Announce(Lang.T("op_nothing")); return; }
            int i = Choose(Lang.T("op_silences"), Lang.T("op_silences_prompt"),
                new[] { Lang.T("sil_speech"), Lang.T("sil_strict"), Lang.T("sil_long") }, 0);
            if (i < 0) return;
            double thr = new[] { -40.0, -35.0, -50.0 }[i], min = new[] { 0.8, 0.5, 2.0 }[i], keep = new[] { 0.3, 0.2, 0.5 }[i];
            double from = HasSelection ? _selStart!.Value : 0, to = HasSelection ? _selEnd!.Value : Project.ContentEnd;
            if (IsPlaying) { _engine.Stop(); IsPlaying = false; }
            Announce(Lang.T("stats_running"));
            int sr = Project.SampleRate;
            var mix = await Task.Run(() => AudioEngine.RenderMix(Project));
            List<(double start, double end)> sil;
            try
            {
                var buf = await Task.Run(() => AudioBuffers.ReadWholeFile(mix.TempPath, sr));
                sil = await Task.Run(() => Processing.FindSilences(buf, sr, thr, min));
            }
            finally { try { File.Delete(mix.TempPath); } catch { } }
            sil = sil.Where(x => x.start >= from - 0.001 && x.end <= to + 0.001).ToList();
            if (sil.Count == 0) { Announce(Lang.T("sil_none_found")); return; }
            PushUndo();
            double removed = 0;
            foreach (var (s, e) in sil.OrderByDescending(x => x.start))   // od kraja, da raniji opsezi ostanu tačni
            {
                double a = s + keep / 2, b = e - keep / 2;
                if (b - a < 0.05) continue;
                RippleDeleteRange(a, b);
                removed += b - a;
            }
            if (HasSelection) SelectionEnd = Math.Max(_selStart!.Value, _selEnd!.Value - removed);
            NotifyEdited();
            Announce(string.Format(Lang.T("sil_done"), sil.Count, removed));
        }

        // ─── Primena efekta na izbor (trajno) ───────────────────────────────
        private void ApplyEffectToSelection()
        {
            var track = SelectedTrack;
            if (track == null) { Announce(Lang.T("fx_select_track")); return; }
            var types = Enum.GetValues<EffectType>();
            int i = Choose(Lang.T("op_apply_fx"), Lang.T("op_apply_fx_prompt"), types.Select(t => Views.EffectDialog.TitleFor(t)).ToArray(), 1);
            if (i < 0) return;
            var type = types[i];
            var temp = new TrackEffects();
            var original = track.Effects;
            // Dok je dijalog otvoren, traka svira SAMO sa ovim efektom — Ctrl+Space u
            // dijalogu pušta izbor (ili od pozicije) i odmah se čuje rezultat.
            if (IsPlaying) { _engine.Stop(); IsPlaying = false; }
            track.Effects = temp;
            try
            {
                var dlg = new Views.EffectDialog($"{Views.EffectDialog.TitleFor(type)} — {Lang.T("op_apply_fx_title_suffix")}", temp, type,
                    () => { if (IsPlaying) { _engine.Stop(); IsPlaying = false; } else if (HasSelection) PlaySelection(); else PlayPauseCommand.Execute(null); })
                { Owner = Application.Current?.MainWindow };
                dlg.ShowDialog();
            }
            finally
            {
                if (IsPlaying) { _engine.Stop(); IsPlaying = false; }
                track.Effects = original;
            }
            bool on = type switch
            {
                EffectType.Equalizer => temp.EqEnabled, EffectType.Reverb => temp.ReverbEnabled, EffectType.Delay => temp.DelayEnabled,
                EffectType.Compressor => temp.CompressorEnabled, EffectType.NoiseGate => temp.NoiseGateEnabled, EffectType.BassBoost => temp.BassBostEnabled,
                EffectType.PitchShift => temp.PitchEnabled, EffectType.Chorus => temp.ChorusEnabled, _ => false
            };
            if (!on) { Announce(Lang.T("op_apply_fx_cancel")); return; }
            var fx = temp;
            _ = ProcessAsync(Views.EffectDialog.TitleFor(type), (b, sr) =>
            {
                var chain = AudioEngine.BuildEffectChain(new BufferSampleProvider(b.ToArray(), sr), fx);
                int got = 0, n;
                while (got < b.Length && (n = chain.Read(b, got, b.Length - got)) > 0) got += n;
                return null;
            });
        }

        // ─── Transkripcija (lokalni Whisper) ────────────────────────────────
        private async Task TranscribeAsync()
        {
            var track = SelectedTrack ?? Project.Tracks.FirstOrDefault(t => t.Clips.Count > 0);
            if (track == null || track.Clips.Count == 0) { Announce(Lang.T("op_nothing")); return; }
            if (!WhisperService.IsAvailable()) { MessageBox.Show(Lang.T("whisper_missing"), Lang.T("whisper_title"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
            int li = Choose(Lang.T("whisper_title"), Lang.T("whisper_lang_prompt"), new[] { "Srpski", "English", Lang.T("whisper_auto") }, Lang.Current == "sr" ? 0 : 1);
            if (li < 0) return;
            string lang = new[] { "sr", "en", "" }[li];
            int mi = Choose(Lang.T("whisper_title"), Lang.T("whisper_model_prompt"), new[] { Lang.T("whisper_small"), Lang.T("whisper_medium"), Lang.T("whisper_large") }, 1);
            if (mi < 0) return;
            string model = new[] { "small", "medium", "large-v3" }[mi];

            double from = HasSelection ? _selStart!.Value : 0;
            double to = HasSelection ? _selEnd!.Value : track.Clips.Max(c => c.EndTime);
            Announce(string.Format(Lang.T("whisper_running"), track.Name));
            IsAiBusy = true;
            try
            {
                var buf = await Task.Run(() => AudioBuffers.ReadTrackRange(track, from, to, Project.SampleRate));
                var prog = new Progress<string>(m => StatusMessage = m);
                var segs = await WhisperService.TranscribeAsync(buf, Project.SampleRate, lang, model, prog);
                foreach (var s in segs) { s.Start += from; s.End += from; }
                if (segs.Count == 0) { Announce(Lang.T("whisper_empty")); return; }
                Announce(string.Format(Lang.T("whisper_done"), segs.Count));
                new Views.TranscriptWindow(this, segs, track.Name) { Owner = Application.Current?.MainWindow }.Show();
            }
            catch (Exception ex)
            {
                DebugLog.Write("Whisper failed: " + ex);
                MessageBox.Show(string.Format(Lang.T("error_prefix"), ex.Message), Lang.T("whisper_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { IsAiBusy = false; }
        }

        /// <summary>Dodaj marker (iz prozora transkripcije).</summary>
        public void AddMarkerAt(double t)
        {
            if (Project.Markers.Any(m => Math.Abs(m - t) < 0.05)) return;
            int idx = 0;
            while (idx < Project.Markers.Count && Project.Markers[idx] < t) idx++;
            Project.Markers.Insert(idx, Math.Round(t, 3));
        }
    }
}
