using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

internal sealed partial class PlayerForm
{
    private readonly GlassButton currentBpm = new GlassButton(), currentKey = new GlassButton(), addedBpm = new GlassButton(), addedKey = new GlassButton();
    private readonly Label currentMusicSeparator = new Label(), addedMusicSeparator = new Label();
    private readonly Dictionary<string, int> keyShifts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private MusicTraits currentNativeTraits;
    private double? sharedBpm;
    private double? tuningReferenceHz, loadedTuningHz;
    private double originalTempo = 1;
    private long nativeFrames;
    private int nativeRate;
    private string originalPlaybackAudio;
    private string[] originalPlaybackFiles;
    private PlaybackTransform originalPlaybackTransform;
    private PlaybackTransform[] originalPlaybackTransforms;
    private bool editingTuning;
    private bool tuningDialogOpen;
    private CancellationTokenSource tuningCancellation;

    private void InitializeTuning()
    {
        foreach (GlassButton button in new[] { currentBpm, currentKey, addedBpm, addedKey })
        {
            button.Font = matchLabel.Font; button.ForeColor = PlayerTheme.Muted; button.TextOnly = true;
            button.AccessibleDescription = "Change playback tempo or transpose this song's key.";
        }
        SetupButton(currentBpm, "BPM unknown", info, null); SetupButton(currentKey, "Key unknown", info, null);
        SetupButton(addedBpm, "BPM unknown", addedInfo, null); SetupButton(addedKey, "Key unknown", addedInfo, null);
        ConfigureLabel(currentMusicSeparator, info, "·", 9, PlayerTheme.Muted, false);
        ConfigureLabel(addedMusicSeparator, addedInfo, "·", 9, PlayerTheme.Muted, false);
        currentBpm.AccessibleName = "Change shared BPM from original song"; addedBpm.AccessibleName = "Change shared BPM from added song";
        currentKey.AccessibleName = "Change original song key"; addedKey.AccessibleName = "Change added song key";
        currentBpm.Click += async delegate { await OpenTempoEditor(); }; addedBpm.Click += async delegate { await OpenTempoEditor(); };
        currentKey.Click += async delegate { if (editingTuning) CancelTuning(); else await OpenKeyEditor(false); }; addedKey.Click += async delegate { await OpenKeyEditor(true); };
        matchLabel.Visible = addedTraits.Visible = false;
        Disposed += delegate { CancelTuning(); };
    }
    private void CancelTuning() { if (tuningCancellation != null) tuningCancellation.Cancel(); }
    private void ResetTuning()
    {
        CancelTuning(); sharedBpm = null; originalTempo = 1; keyShifts.Clear(); currentNativeTraits = null;
        originalPlaybackAudio = null; originalPlaybackFiles = null; nativeFrames = 0; nativeRate = 0;
        originalPlaybackTransform = null; originalPlaybackTransforms = null;
    }
    private int KeyShift(SongInfo song) { int shift; return song != null && keyShifts.TryGetValue(song.Audio, out shift) ? shift : 0; }
    private double PlaybackPitch(SongInfo song, int shift) { return TuningReference.Pitch(song.TuningHz, loadedTuningHz, shift); }
    private string TuningDescription(SongInfo song)
    {
        if (song == null || !song.TuningHz.HasValue) return "A4 tuning unknown; reference correction is skipped.";
        return "Original A4: " + song.TuningHz.Value.ToString("0.###") + " Hz (estimated). Playback: " + (loadedTuningHz ?? song.TuningHz.Value).ToString("0.###") + " Hz.";
    }
    private MusicTraits NativeTraits(SongInfo song)
    {
        if (song == null) return null;
        if (current != null && String.Equals(song.Audio, current.Audio, StringComparison.OrdinalIgnoreCase)) return currentNativeTraits;
        if (stemSources != null) foreach (StemSourceBank bank in stemSources.Active.Concat(new[] { stemSources.Available }).Where(bank => bank != null))
            if (String.Equals(bank.Song.Audio, song.Audio, StringComparison.OrdinalIgnoreCase)) return bank.Traits;
        return null;
    }
    private int KeySection(SongInfo song, MusicTraits traits, bool original = false)
    {
        return traits == null || traits.Tonic < 0 ? -1 : Camelot.Section((traits.Tonic + (original ? 0 : KeyShift(song)) + 12) % 12, traits.Minor);
    }
    private string PlaybackKey(SongInfo song, MusicTraits traits)
    {
        int shift = AutoTransitionActive && autoTransition != null && Object.ReferenceEquals(traits, autoTransition.Traits) ? 0 : KeyShift(song);
        return traits == null || traits.Tonic < 0 ? "Key unknown" : MusicTraits.Notes[(traits.Tonic + shift + 12) % 12] + (traits.Minor ? " minor" : " major") + " (estimated)";
    }
    private void UpdateTuningHeader()
    {
        if (currentBpm.Parent == null) return;
        bool ready = current != null && mixer != null && !loading && !editingTuning && !editingArtists && !preparingReplacement && !AutoTransitionActive;
        double bpm = sharedBpm ?? (currentNativeTraits == null ? current != null && current.Bpm.HasValue ? current.Bpm.Value : 0 : currentNativeTraits.Bpm);
        currentBpm.Text = bpm > 0 ? bpm.ToString("0.00") + " BPM" : "BPM unknown";
        currentKey.Text = PlaybackKey(current, currentNativeTraits);
        if (editingTuning) currentKey.Text = "Cancel changes";
        currentKey.AccessibleName = editingTuning ? "Cancel tempo and key changes" : "Change original song key";
        currentBpm.Enabled = ready; currentKey.Enabled = ready || editingTuning;
        StemSourceBank bank = stemSources == null ? null : stemSources.Available;
        bool preview = AutoEditorEnabled && AutoMarkersReady && displayedAddedSong != null && String.Equals(autoTransition.Song.Audio, displayedAddedSong.Audio, StringComparison.OrdinalIgnoreCase);
        if (preview) bank = autoTransition.Bank;
        if (bank != null && (displayedAddedSong == null || !String.Equals(bank.Song.Audio, displayedAddedSong.Audio, StringComparison.OrdinalIgnoreCase))) bank = null;
        MusicTraits traits = bank == null ? null : bank.Traits;
        double addedTempo = (preview || AutoTransitionActive) && bank != null ? bank.Plan.MatchedBpm : sharedBpm ?? (bank == null ? 0 : bank.Plan.MatchedBpm);
        addedBpm.Text = addedTempo > 0 ? addedTempo.ToString("0.00") + " BPM" : "Matching tempo…";
        addedKey.Text = PlaybackKey(displayedAddedSong, traits);
        addedBpm.Enabled = addedKey.Enabled = ready && bank != null && !preview;
        addedBpm.Visible = addedKey.Visible = displayedAddedSong != null;
        addedMusicSeparator.Visible = displayedAddedSong != null;
        LayoutMusicDetails(currentBpm, currentKey, currentMusicSeparator, matchLabel.Bounds);
        LayoutMusicDetails(addedBpm, addedKey, addedMusicSeparator, addedTraits.Bounds);
        tip.SetToolTip(currentBpm, "Click to change the shared playback tempo. Original: " + (currentNativeTraits == null ? "unknown" : currentNativeTraits.Bpm.ToString("0.00")) + " BPM.");
        tip.SetToolTip(addedBpm, "One tempo slider changes both loaded songs together. Original: " + (traits == null ? "unknown" : traits.Bpm.ToString("0.00")) + " BPM.");
        tip.SetToolTip(currentKey, editingTuning ? "Cancel preparation and keep the current playback settings." : "Click for the Camelot wheel. Original: " + (currentNativeTraits == null ? "unknown" : currentNativeTraits.Key) + "\n" + TuningDescription(current));
        tip.SetToolTip(addedKey, "Click for the Camelot wheel. Original: " + (traits == null ? "unknown" : traits.Key) + "\n" + TuningDescription(displayedAddedSong));
    }
    private bool CanTune() { return !IsDisposed && current != null && mixer != null && !loading && !editingTuning && !editingArtists && !preparingReplacement && !AutoTransitionActive; }
    private async Task EnsureNativeTraits(CancellationToken? cancellation = null)
    {
        if (currentNativeTraits != null) return;
        int ticket = generation; SongInfo song = current;
        CancellationToken token = cancellation ?? (waveformCancellation == null ? CancellationToken.None : waveformCancellation.Token);
        MusicTraits traits = await PlayerMatching.Analyze(project, song, token, true);
        token.ThrowIfCancellationRequested();
        if (ticket != generation || IsDisposed) throw new OperationCanceledException();
        currentNativeTraits = traits; UpdateTuningHeader();
    }
    private async Task OpenTempoEditor()
    {
        if (!CanTune() || tuningDialogOpen) return;
        tuningDialogOpen = true; CancelAutoPreparation();
        try
        {
            await EnsureNativeTraits();
            if (currentNativeTraits.Bpm < 30 || currentNativeTraits.Bpm > 300) throw new InvalidOperationException("This song's BPM is unknown. A known original BPM is needed to change its tempo.");
            using (Form dialog = new Form { Text = "Shared playback tempo", ClientSize = new Size(560, 244), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = PlayerTheme.Background, ForeColor = PlayerTheme.Text, Font = Font })
            {
                TrackBar slider = new TrackBar { Minimum = 300, Maximum = 3000, TickFrequency = 300, SmallChange = 1, LargeChange = 100, Value = (int)Math.Round((sharedBpm ?? currentNativeTraits.Bpm) * 10), Bounds = new Rectangle(20, 69, 516, 55), AccessibleName = "Shared BPM for all loaded songs" };
                Label value = new Label { Bounds = new Rectangle(20, 18, 520, 38), Font = new Font("Segoe UI", 20, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
                Label hint = new Label { Text = displayedAddedSong == null ? "Change this song's tempo while preserving its key." : "Both loaded songs use this BPM. Each song keeps its selected key.", Bounds = new Rectangle(22, 132, 516, 42), ForeColor = PlayerTheme.Muted };
                Button apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, Bounds = new Rectangle(342, 193, 92, 30) };
                Button reset = new Button { Text = "Original tempo", DialogResult = DialogResult.Retry, Bounds = new Rectangle(22, 193, 138, 30) };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(442, 193, 92, 30) };
                slider.ValueChanged += delegate { value.Text = (slider.Value / 10.0).ToString("0.0") + " BPM"; }; value.Text = (slider.Value / 10.0).ToString("0.0") + " BPM";
                dialog.Controls.AddRange(new Control[] { slider, value, hint, apply, reset, cancel }); dialog.AcceptButton = apply; dialog.CancelButton = cancel;
                DialogResult result = dialog.ShowDialog(this);
                if ((result == DialogResult.OK || result == DialogResult.Retry) && CanTune()) await ApplyTuning(result == DialogResult.Retry ? (double?)null : slider.Value / 10.0, new Dictionary<string, int>(keyShifts, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed) ShowError(error); }
        finally { tuningDialogOpen = false; }
    }
    private async Task OpenKeyEditor(bool added)
    {
        if (!CanTune() || tuningDialogOpen) return;
        tuningDialogOpen = true; CancelAutoPreparation();
        try
        {
            await EnsureNativeTraits();
            SongInfo selected = added ? displayedAddedSong : current, other = added ? current : displayedAddedSong;
            MusicTraits traits = NativeTraits(selected), otherTraits = NativeTraits(other);
            if (traits == null || traits.Tonic < 0) throw new InvalidOperationException("This song's original key is unknown. Wait for key analysis to complete before transposing it.");
            int choice = -1;
            using (Form dialog = new Form { Text = "Change key · " + selected.Title, ClientSize = new Size(610, 756), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = PlayerTheme.Background, ForeColor = PlayerTheme.Text, Font = Font })
            using (CamelotWheel wheel = new CamelotWheel { Bounds = new Rectangle(20, 45, 570, 570), Current = KeySection(selected, traits), Original = KeySection(selected, traits, true), Other = KeySection(other, otherTraits), MinorMode = traits.Minor, ShowHoverInfo = tip.Active })
            {
                Label title = new PlayerLabel { HoverInfo = tip, Text = selected.Title, AutoEllipsis = true, Bounds = new Rectangle(24, 13, 560, 26), Font = new Font("Segoe UI", 12, FontStyle.Bold) };
                string[] legends = { "Current · " + Camelot.Code(wheel.Current) + " · " + Camelot.Key(wheel.Current), wheel.Original == wheel.Current ? "Original · same as current" : "Original · " + Camelot.Code(wheel.Original) + " · " + Camelot.Key(wheel.Original), other == null ? "Other song · none loaded" : "Other song · " + Camelot.Code(wheel.Other) + " · " + Camelot.Key(wheel.Other) };
                Color[] fills = { CamelotWheel.CurrentFill, wheel.Original == wheel.Current ? PlayerTheme.Muted : CamelotWheel.OriginalFill, CamelotWheel.OtherFill };
                for (int i = 0; i < 3; i++)
                {
                    Label marker = new Label { BackColor = fills[i], Bounds = new Rectangle(25, 617 + i * 23, 13, 13) };
                    Label label = new Label { Text = legends[i], Bounds = new Rectangle(46, 612 + i * 23, 530, 23), ForeColor = i == 1 && wheel.Original == wheel.Current ? PlayerTheme.Muted : fills[i] };
                    dialog.Controls.Add(marker); dialog.Controls.Add(label);
                }
                Label hint = new Label { Text = "Click a " + (traits.Minor ? "minor (A)" : "major (B)") + " sector to transpose. Major/minor mode is preserved.", Bounds = new Rectangle(24, 683, 560, 25), ForeColor = PlayerTheme.Muted, Font = new Font("Segoe UI", 8.5f) };
                Button reset = new Button { Text = "Original key", Bounds = new Rectangle(24, 718, 114, 28) }; reset.Click += delegate { choice = wheel.Original; dialog.DialogResult = DialogResult.OK; };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(480, 718, 104, 28) }; dialog.CancelButton = cancel;
                wheel.Selected = delegate(int section) { choice = section; dialog.DialogResult = DialogResult.OK; };
                dialog.Controls.AddRange(new Control[] { title, wheel, hint, reset, cancel });
                if (dialog.ShowDialog(this) == DialogResult.OK && choice >= 0 && CanTune())
                {
                    var shifts = new Dictionary<string, int>(keyShifts, StringComparer.OrdinalIgnoreCase);
                    shifts[selected.Audio] = Camelot.Shift(traits.Tonic, Camelot.Tonic(choice));
                    await ApplyTuning(sharedBpm, shifts);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed) ShowError(error); }
        finally { tuningDialogOpen = false; }
    }
    private async Task ApplyTuning(double? targetBpm, Dictionary<string, int> shifts)
    {
        if (!CanTune()) return;
        CancelAutoPreparation();
        editingTuning = true; list.Enabled = browser.Enabled = libraryTab.Enabled = false; SetEnabled(true);
        int ticket = generation; var cancellation = new CancellationTokenSource(); tuningCancellation = cancellation;
        StemMixer prepared = null;
        bool applied = false;
        try
        {
            await EnsureNativeTraits(cancellation.Token);
            if (targetBpm.HasValue && (Double.IsNaN(targetBpm.Value) || targetBpm.Value < 30 || targetBpm.Value > 300 || currentNativeTraits.Bpm < 30 || currentNativeTraits.Bpm > 300)) throw new InvalidOperationException("Changing tempo requires an original and target BPM between 30 and 300.");
            if (waveformCancellation != null) waveformCancellation.Cancel();
            if (waveformTask != null) await waveformTask;
            double tempo = targetBpm.HasValue ? targetBpm.Value / currentNativeTraits.Bpm : 1;
            long frames = Math.Max(1, (long)Math.Round(nativeFrames / tempo)); int shift;
            shifts.TryGetValue(current.Audio, out shift);
            StemMatchPlan originalPlan = new StemMatchPlan { Tempo = tempo, Pitch = PlaybackPitch(current, shift), MatchedBpm = targetBpm ?? currentNativeTraits.Bpm };
            bool unchangedOriginal = Math.Abs(tempo - 1) < .0000001 && Math.Abs(originalPlan.Pitch - 1) < .0000001;
            message.ForeColor = PlayerTheme.Muted; message.Text = "Preparing tempo and key changes…";
            string[] originals = mixer.HasStems ? new string[4] : null;
            var nextSources = new StemSourceState(current.Folder, originals);
            string audio = current.Audio;
            PlaybackTransform audioTransform = unchangedOriginal ? null : new PlaybackTransform(originalPlan, nativeRate, frames);
            var originalTransforms = originals == null ? null : new PlaybackTransform[4];
            nextSources.OriginalTuning = originalTransforms;
            if (originals != null) for (int lane = 0; lane < 4; lane++)
            {
                string source = Path.Combine(current.Folder, StemMixer.StemNames[lane] + ".wav");
                originals[lane] = source;
                originalTransforms[lane] = unchangedOriginal ? null : new PlaybackTransform(originalPlan, nativeRate, frames, lane);
                float[] peaks = stemSources.NativePeaks[lane] ?? await Task.Run(() => PlayerWaveforms.Read(source, cancellation.Token));
                nextSources.NativePeaks[lane] = peaks;
                nextSources.OriginalPeaks[lane] = unchangedOriginal ? peaks : originalTransforms[lane].Peaks(peaks, nativeFrames / (double)nativeRate);
            }
            var changedBanks = new Dictionary<StemSourceBank, StemSourceBank>();
            foreach (StemSourceBank old in stemSources.Active.Concat(new[] { stemSources.Available }).Where(bank => bank != null).Distinct())
            {
                StemMatchPlan plan = targetBpm.HasValue ? StemMatchPlan.AtBpm(currentNativeTraits, old.Traits, targetBpm.Value) : StemMatchPlan.Create(currentNativeTraits, old.Traits);
                shifts.TryGetValue(old.Song.Audio, out shift); plan.Pitch = PlaybackPitch(old.Song, shift);
                var relative = new StemMatchPlan { Tempo = plan.Tempo / old.Plan.Tempo, Pitch = plan.Pitch / old.Plan.Pitch };
                relative.Offset = plan.Offset - old.Plan.Offset / relative.Tempo;
                var bank = new StemSourceBank { Song = old.Song, Traits = old.Traits, Plan = plan, LoudnessGain = old.LoudnessGain, Files = new string[4], Peaks = new float[4][], Tuning = new PlaybackTransform[4] };
                for (int lane = 0; lane < 4; lane++)
                {
                    double duration;
                    using (var reader = new WaveFileReader(old.Files[lane])) duration = old.Tuning == null ? reader.TotalTime.TotalSeconds : old.Tuning[lane].Frames / (double)nativeRate;
                    bank.Files[lane] = Path.Combine(bank.Song.Folder, StemMixer.StemNames[lane] + ".wav");
                    bank.Tuning[lane] = PlayerMatching.LiveTransform(bank.Files[lane], plan, nativeRate, frames, lane);
                    var envelope = new PlaybackTransform(relative, nativeRate, bank.Tuning[lane].Frames, lane);
                    bank.Peaks[lane] = envelope.Peaks(old.Peaks[lane], duration);
                }
                changedBanks[old] = bank;
            }
            cancellation.Token.ThrowIfCancellationRequested(); if (IsDisposed || generation != ticket) throw new OperationCanceledException();
            prepared = await Task.Run(() => new StemMixer(audio, originals == null ? "" : current.Folder, originals, audioTransform, originalTransforms));
            prepared.SetLoudness(mixer.OriginalLoudnessGain);
            cancellation.Token.ThrowIfCancellationRequested(); if (IsDisposed || generation != ticket) throw new OperationCanceledException();
            if (stemSources.Available != null) nextSources.Available = changedBanks[stemSources.Available];
            for (int lane = 0; lane < 4; lane++) if (stemSources.Active[lane] != null) { nextSources.Active[lane] = changedBanks[stemSources.Active[lane]]; prepared.ReplaceStem(lane, nextSources.Active[lane].Files[lane], nextSources.Active[lane].LoudnessGain, nextSources.Active[lane].Tuning[lane]); }
            prepared.Volume = volume.Value / 100f; prepared.ApplyMix(mix); prepared.OriginalMode = preferOriginal || !prepared.HasStems;
            // Convert playback time through the original timeline, preserving the
            // current musical position when the shared tempo changes.
            prepared.RestoreStemTiming(StemTimingSnapshot(), originalTempo / tempo);
            prepared.Seek(mixer.Seconds * originalTempo / tempo);
            await Task.Run(() => prepared.WarmTransforms(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (IsDisposed || generation != ticket) throw new OperationCanceledException();
            if (playback == null) playback = new PlayerPlayback(mixer);
            if (!playback.ReplaceCurrent(mixer, prepared, originalTempo / tempo)) throw new InvalidOperationException("Playback changed while applying tempo and key.");
            mixer = prepared; prepared = null; stemSources = nextSources; originalPlaybackAudio = audio; originalPlaybackFiles = originals;
            originalPlaybackTransform = audioTransform; originalPlaybackTransforms = originalTransforms;
            originalTempo = tempo; sharedBpm = targetBpm; keyShifts.Clear(); foreach (var pair in shifts) keyShifts[pair.Key] = pair.Value;
            applied = true;
            for (int lane = 0; lane < 4; lane++) { waveforms[lane].Duration = mixer.Duration; ShowSource(lane); }
            UpdateTransport(); StyleModes(); UpdateMessage();
            message.Text = "Tempo and key changes applied."; message.ForeColor = PlayerTheme.Green;
        }
        catch (OperationCanceledException) { if (!IsDisposed && generation == ticket) { message.Text = "Tempo and key changes canceled."; message.ForeColor = PlayerTheme.Muted; } }
        catch (Exception error) { if (!IsDisposed && generation == ticket) ShowError(error); }
        finally
        {
            if (prepared != null) prepared.Dispose(); if (tuningCancellation == cancellation) tuningCancellation = null; cancellation.Dispose();
            editingTuning = false;
            if (!IsDisposed && generation == ticket)
            {
                if (!applied && mixer != null && mixer.HasStems)
                {
                    waveformCancellation = new CancellationTokenSource();
                    waveformTask = LoadWaveforms(stemSources, ticket, waveformCancellation.Token);
                }
                list.Enabled = browser.Enabled = libraryTab.Enabled = true; SetEnabled(mixer != null); UpdateTuningHeader();
            }
        }
    }
    private void RemapTuning(object[] mappings)
    {
        var shifts = keyShifts.ToArray(); keyShifts.Clear(); foreach (var pair in shifts) keyShifts[RemappedPath(pair.Key, mappings)] = pair.Value;
    }
}
