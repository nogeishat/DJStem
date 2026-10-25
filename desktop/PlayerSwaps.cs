using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

internal sealed partial class PlayerForm
{
    private readonly GlassButton[] sourceButtons = new GlassButton[4];
    private readonly string[] playingStemFiles = new string[4];
    private readonly Label matchLabel = new PlayerLabel();
    private readonly GlassButton resetStems = new GlassButton();
    private StemSourceState stemSources;
    private CancellationTokenSource replacementCancellation;
    private bool preparingReplacement;
    private int replacementTicket;

    private void InitializeMatching()
    {
        ConfigureLabel(matchLabel, info, "BPM and key are saved after download.", 9, PlayerTheme.Muted, false); matchLabel.AutoEllipsis = true;
        SetupButton(resetStems, "Reset stems", transport, null); resetStems.Font = new Font("Segoe UI", 8.5f); resetStems.Radius = 13; resetStems.Click += delegate { RestoreStems(); };
    }
    private void InitializeSourceButton(int index)
    {
        int lane = index;
        sourceButtons[index] = MakeButton(StemMixer.StemNames[index].ToUpperInvariant(), lanes[index], 58, 0, 84, 28, null); sourceButtons[index].Radius = 6;
        sourceButtons[index].Font = new Font("Segoe UI", 9, FontStyle.Bold);
        sourceButtons[index].Toggle = true;
        sourceButtons[index].AccessibleName = "Toggle " + StemMixer.StemNames[index] + " original / replacement";
        sourceButtons[index].Click += delegate { ToggleStemSource(lane); };
    }
    private void CancelMatching()
    {
        replacementTicket++; preparingReplacement = false; replacementLoadingProgress = null;
        if (replacementCancellation != null) replacementCancellation.Cancel();
        UpdateStemLoadingButtons();
    }
    private void ResetSourceLabels(SongInfo song)
    {
        CancelMatching(); stemSources = new StemSourceState(song.Folder, originalPlaybackFiles);
        stemSources.OriginalTuning = originalPlaybackTransforms;
        PlayerMatchedCache.Keep(stemSources, originalPlaybackAudio);
        ShowAddedSong(null);
        for (int i = 0; i < 4; i++) playingStemFiles[i] = stemSources.Original(i);
        matchLabel.Text = "Reading BPM and key…"; UpdateSourceButtons();
    }
    private async Task AnalyzeCurrent(SongInfo song, int ticket, CancellationToken cancellation)
    {
        try
        {
            MusicTraits traits = await PlayerMatching.Analyze(project, song, cancellation, true);
            if (!IsDisposed && ticket == generation)
            {
                matchLabel.Text = traits.Bpm.ToString("0.00") + " BPM · " + traits.Key + " (estimated)";
                currentNativeTraits = traits; UpdateTuningHeader();
                tip.SetToolTip(matchLabel, "Replacement tempo uses the closest normal, half, double, or further power-of-two BPM; pitch/key are preserved.\n" + (traits.BeatConfidence < .5 ? "Beat estimate has low confidence; check alignment by ear." : "BPM and beat timing are estimates."));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed && ticket == generation) { matchLabel.Text = "BPM/key analysis unavailable"; tip.SetToolTip(matchLabel, error.Message); } }
    }
    private void UpdateSourceButtons()
    {
        bool ready = !loading && !editingTuning && !editingArtists && !AutoTransitionActive && mixer != null && mixer.HasStems && stemSources != null;
        for (int i = 0; i < 4; i++)
        {
            StemSourceBank active = stemSources == null ? null : stemSources.Active[i];
            StemSourceBank available = stemSources == null ? null : stemSources.Available;
            Color color = SongColor(active == null ? current : active.Song, active != null);
            sourceButtons[i].Swatch = color;
            waveforms[i].Accent = color; waveforms[i].Invalidate();
            sourceButtons[i].Selected = active != null; sourceButtons[i].Enabled = ready && available != null; sourceButtons[i].Invalidate();
            SongInfo source = active == null ? current : active.Song;
            string description = "Playing " + (active == null ? "original" : "replacement") + (source == null ? "" : ": " + source.Title + " — " + source.Artist) + ".";
            description += available == null ? preparingReplacement ? "\nPreparing four replacement stems…" : "\nDrag a library song onto any stem to prepare a replacement for every lane." : "\nReplacement ready: " + available.Song.Title + " — " + available.Song.Artist + ".\nClick to switch " + (active == null ? "to replacement" : "to original") + " instantly. " + available.Plan.Summary;
            description += "\n" + StemSwitchKeys[i] + ": switch " + StemMixer.StemNames[i] + " between original and replacement.";
            sourceButtons[i].AccessibleDescription = description; tip.SetToolTip(sourceButtons[i], description);
            SetAutoSourceButton(i);
        }
        UpdateStemLoadingButtons();
        UpdateResetStemsButton();
        UpdateProgressColors();
        UpdateTuningHeader();
        UpdateSaveMixButton();
    }
    private void UpdateResetStemsButton(StemMixer.DisplayState display = null)
    {
        bool ready = !loading && !editingTuning && !editingArtists && !AutoTransitionActive && mixer != null && mixer.HasStems && stemSources != null;
        bool timingChanged = false;
        if (ready) for (int lane = 0; lane < 4; lane++) timingChanged |= (display == null ? mixer.StemState(lane) : display.Timing[lane]).Changed;
        resetStems.Enabled = ready && (preparingReplacement || stemSources.Available != null || timingChanged);
    }
    private async Task ReplaceFromDrop(SongInfo donor, int index)
    {
        if (!CanReplace(donor)) return;
        CancelAutoPreparation();
        CancelMatching(); int bankTicket = replacementTicket, songTicket = generation;
        SongInfo reference = current;
        int rate = mixer.WaveFormat.SampleRate; long frames = mixer.TotalFrames;
        var cancellation = new CancellationTokenSource(); replacementCancellation = cancellation; preparingReplacement = true;
        var loadProgress = new StemLoadingProgress(); replacementLoadingProgress = loadProgress;
        ShowAddedSong(donor); UpdateSourceButtons();
        message.ForeColor = PlayerTheme.Muted; message.Text = "Preparing four stems from " + donor.Title + "…";
        try
        {
            MusicTraits referenceTraits = await PlayerMatching.Analyze(project, reference, cancellation.Token);
            MusicTraits donorTraits = await PlayerMatching.Analyze(project, donor, cancellation.Token);
            StemMatchPlan plan = sharedBpm.HasValue ? StemMatchPlan.AtBpm(referenceTraits, donorTraits, sharedBpm.Value) : StemMatchPlan.Create(referenceTraits, donorTraits);
            plan.Pitch = PlaybackPitch(donor, KeyShift(donor));
            if (IsDisposed || generation != songTicket || replacementTicket != bankTicket) return;
            ShowAddedSong(donor, donorTraits, plan);
            var bank = new StemSourceBank { Song = donor, Traits = donorTraits, Plan = plan, LoudnessGain = await PlayerLoudness.Gain(project, donor.Audio, cancellation.Token), Files = new string[4], Peaks = new float[4][], Tuning = new PlaybackTransform[4] };
            loadProgress.All(.1);
            for (int lane = 0; lane < 4; lane++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                bank.Files[lane] = Path.Combine(donor.Folder, StemMixer.StemNames[lane] + ".wav");
                string file = bank.Files[lane]; bank.Tuning[lane] = PlayerMatching.LiveTransform(file, plan, rate, frames, lane);
                int indexToLoad = lane;
                float[] peaks = await Task.Run(() => PlayerWaveforms.Read(file, cancellation.Token, loadProgress.Range(indexToLoad, .1, .95)));
                using (var reader = new WaveFileReader(file)) bank.Peaks[lane] = bank.Tuning[lane].Peaks(peaks, reader.TotalTime.TotalSeconds);
                loadProgress.Report(lane, 1);
                if (!IsDisposed && generation == songTicket && replacementTicket == bankTicket) UpdateStemLoadingButtons();
                if (!IsDisposed && generation == songTicket && replacementTicket == bankTicket) message.Text = "Preparing " + donor.Title + " · " + (lane + 1) + " / 4 stems";
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || generation != songTicket || replacementTicket != bankTicket || mixer == null) return;
            mixer.ReplaceStem(index, bank.Files[index], bank.LoudnessGain, bank.Tuning[index]); stemSources.Stage(bank, index);
            ShowSource(index); SelectMix(); ApplyMix();
            message.ForeColor = PlayerTheme.Green; message.Text = donor.Title + " · " + plan.Summary + " · all four replacements ready";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed && generation == songTicket && replacementTicket == bankTicket) ShowError(error); }
        finally
        {
            if (!IsDisposed && generation == songTicket && replacementTicket == bankTicket)
            {
                preparingReplacement = false; replacementLoadingProgress = null; UpdateSourceButtons();
                StemSourceBank available = stemSources.Available;
                ShowAddedSong(available == null ? null : available.Song, available == null ? null : available.Traits, available == null ? null : available.Plan);
            }
            if (replacementCancellation == cancellation) replacementCancellation = null;
            cancellation.Dispose();
        }
    }
    private void ShowSource(int index)
    {
        playingStemFiles[index] = stemSources.File(index);
        waveforms[index].SourceDuration = mixer.StemDuration(index);
        waveforms[index].Peaks = stemSources.Peaks(index); waveforms[index].Invalidate();
        UpdateSourceButtons();
    }
    private void ToggleStemSource(int index)
    {
        if (AutoEditorEnabled || loading || editingTuning || AutoTransitionActive || mixer == null || !mixer.HasStems || stemSources == null || stemSources.Available == null) return;
        CancelAutoPreparation();
        try
        {
            float loudness = stemSources.Active[index] == null ? stemSources.Available.LoudnessGain : mixer.OriginalLoudnessGain;
            PlaybackTransform transform = stemSources.Active[index] == null ? stemSources.Available.Tuning == null ? null : stemSources.Available.Tuning[index] : stemSources.OriginalTuning == null ? null : stemSources.OriginalTuning[index];
            mixer.ReplaceStem(index, stemSources.ToggleFile(index), loudness, transform); stemSources.Toggle(index); ShowSource(index); SelectMix(); ApplyMix();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void RestoreStems()
    {
        if (editingTuning || AutoTransitionActive) return;
        CancelAutoPreparation();
        CancelMatching();
        if (stemSources == null || mixer == null || !mixer.HasStems) return;
        try
        {
            for (int i = 0; i < 4; i++) if (stemSources.Active[i] != null) mixer.ReplaceStem(i, stemSources.Original(i), mixer.OriginalLoudnessGain, stemSources.OriginalTuning == null ? null : stemSources.OriginalTuning[i]);
            for (int i = 0; i < 4; i++) waveforms[i].ResetGesture();
            mixer.RestoreStemTiming(new[] { new StemTiming(), new StemTiming(), new StemTiming(), new StemTiming() });
            stemSources.Reset(); for (int i = 0; i < 4; i++) ShowSource(i);
            ShowAddedSong(null);
            UpdateSourceButtons(); UpdateTransport(); UpdateMessage();
        }
        catch (Exception error) { ShowError(error); }
    }
}
