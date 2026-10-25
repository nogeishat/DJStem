using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

internal sealed class PreparedAutoTransition
{
    internal SongInfo Song;
    internal MusicTraits Traits;
    internal AutoTransitionTiming Timing;
    internal StemMixer Reference, Bridge, Next;
    internal string BridgeAudio, NextAudio, QueueEntryId;
    internal string[] OutgoingFiles, IncomingFiles, NextFiles;
    internal float[][] OutgoingPeaks, IncomingPeaks, NextPeaks;
    internal AutoAudioDynamics[] OutgoingDynamics, IncomingDynamics;
    internal double SourceStart;
    internal long[] SwapFrames, CutFrames;
    internal StemTiming[] OutgoingTiming;
    internal bool Started, Requested;
    internal AutoTransitionSource Source;
    internal readonly bool[] Switched = new bool[4];
    internal readonly bool[] Silenced = new bool[4];
    internal StemSourceBank Bank;
    internal PreparedAutoSong PlaylistMix;
    internal double? TuningHz;
}

internal sealed partial class PlayerForm
{
    private readonly GlassButton autoPlayButton = new GlassButton(), browserAutoPlayButton = new GlassButton(), settingsButton = new GlassButton();
    private AutoPlaySettings autoSettings = new AutoPlaySettings();
    private readonly AutoShuffle autoShuffle = new AutoShuffle();
    private readonly HashSet<string> autoFailedSongs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool autoPlayEnabled, preparingAutoPlay, autoNextRequested, requestingAutoNext;
    private string autoAttempt;
    private int autoTicket;
    private CancellationTokenSource autoCancellation;
    private PreparedAutoTransition autoTransition;
    private Task autoPreparationTask;
    private PlayerPlayback playback;
    private bool AutoTransitionActive { get { return autoTransition != null && playback != null && playback.Current != null && (playback.Current == autoTransition.Bridge || playback.Current == autoTransition.Next); } }

    private void InitializeAutoPlay()
    {
        SetupButton(autoPlayButton, "Auto play", sidebar, null); autoPlayButton.Bounds = new Rectangle(95, 20, 78, 32);
        SetupButton(settingsButton, "⚙", sidebar, null); settingsButton.Bounds = new Rectangle(180, 20, 30, 32); settingsButton.Font = new Font("Segoe UI Symbol", 14); settingsButton.AccessibleName = "Settings";
        SetupButton(browserAutoPlayButton, "Auto play", browser, null); browserAutoPlayButton.Bounds = new Rectangle(150, 13, 94, 32);
        foreach (GlassButton button in new[] { autoPlayButton, browserAutoPlayButton })
        {
            button.Toggle = true; button.SelectedFill = Color.White; button.Font = new Font("Segoe UI", 8.5f);
            button.Click += delegate { SetAutoPlay(!autoPlayEnabled); };
            tip.SetToolTip(button, "Shuffle with a tempo ramp, then replace the next song's stems one at a time.");
        }
        settingsButton.Click += delegate { OpenPlayerSettings(); };
        tip.SetToolTip(settingsButton, "Settings · Auto play"); LayoutBrowser();
    }
    private void SetAutoPlay(bool enabled)
    {
        if (autoPlayEnabled == enabled) return;
        autoPlayEnabled = enabled; autoAttempt = null; autoFailedSongs.Clear();
        UpdateStemGestureMode();
        if (enabled && mixer != null) mixer.RestoreStemTiming(new[] { new StemTiming(), new StemTiming(), new StemTiming(), new StemTiming() });
        UpdateTransport();
        autoNextRequested = false;
        if (!AutoTransitionActive) CancelAutoPreparation();
        EndQueueSession();
        if (enabled) FillAutoQueue();
        UpdateAutoRestart();
        RenderPlaybackQueue();
        LayoutContent(); SetEnabled(mixer != null); UpdateAutoMarkers();
        autoPlayButton.Selected = browserAutoPlayButton.Selected = enabled; autoPlayButton.Invalidate(); browserAutoPlayButton.Invalidate();
        if (enabled && output != null && !loading && output.PlaybackState != PlaybackState.Playing) TogglePlay();
        if (!enabled && AutoTransitionActive) message.Text = "Auto play off · the current transition will finish.";
        else if (!enabled) message.Text = "Auto play off.";
        SavePreferences();
    }
    private bool SeekPreparedAutoTransition(double seconds)
    {
        PreparedAutoTransition plan = autoTransition; PlaybackQueueEntry next = songQueue.Next;
        if (!autoPlayEnabled || plan == null || plan.Started || plan.Reference != mixer || playback == null || next == null || next.Id != plan.QueueEntryId) return false;
        long frame = Math.Max(0, Math.Min(mixer.TotalFrames, (long)Math.Round(seconds * plan.Timing.Rate)));
        double elapsed = plan.Timing.ElapsedAtSource(frame / (double)plan.Timing.Rate - plan.SourceStart);
        long bridgeFrame = Math.Max(0, Math.Min(plan.Timing.TotalFrames, (long)Math.Round(elapsed * plan.Timing.Rate)));
        long nextFrame = plan.Timing.IncomingEndFrame(plan.Traits.BeatOffset);
        return playback.SeekScheduled(plan.Reference, plan.Bridge, plan.Next, frame, (long)Math.Round(plan.SourceStart * plan.Timing.Rate), bridgeFrame, nextFrame);
    }
    private void CancelAutoPreparation()
    {
        bool hadPreview = transitionEditorEnabled && autoTransition != null;
        CancelAutoPreload();
        autoTicket++; preparingAutoPlay = false; autoLoadingProgress = null; preparingQueueId = null; autoAttempt = null; autoNextRequested = false;
        if (autoCancellation != null) autoCancellation.Cancel();
        if (!AutoTransitionActive)
        {
            StemMixer expected = autoTransition == null ? mixer : autoTransition.Reference;
            if (playback == null || playback.CancelPending(expected)) autoTransition = null;
        }
        if (autoTransition == null && hadPreview)
        {
            StemSourceBank bank = stemSources == null ? null : stemSources.Available;
            ShowAddedSong(bank == null ? null : bank.Song, bank == null ? null : bank.Traits, bank == null ? null : bank.Plan);
        }
        UpdateAutoMarkers(); UpdateSourceButtons(); RenderPlaybackQueue();
    }
    private void StopAutoTimeline()
    {
        autoIncomingMixer = null;
        if (playback != null) playback.ClearRestartProtection();
        CancelAutoPreload();
        autoTicket++; preparingAutoPlay = false; autoLoadingProgress = null; preparingQueueId = null; autoAttempt = null; queueAdvancePending = false; autoNextRequested = false;
        if (autoCancellation != null) autoCancellation.Cancel();
        autoTransition = null;
        UpdateAutoMarkers();
        UpdateStemLoadingButtons();
    }
    private async Task RequestAutoNext()
    {
        if (!autoPlayEnabled) return;
        if (AutoTransitionActive) { SkipAutoTransition(); return; }
        if (requestingAutoNext) return;
        requestingAutoNext = true;
        try
        {
            Task previousPreparation = autoPreparationTask;
            autoNextRequested = true;
            if (output != null && output.PlaybackState != PlaybackState.Playing) output.Play();
            message.Text = "Auto play · preparing a beat-matched transition from the current position…";
            if (previousPreparation != null) await previousPreparation;
            if (!autoPlayEnabled || !autoNextRequested || IsDisposed || loading) return;
            if (AutoTransitionActive) { autoNextRequested = false; return; }
            if (autoTransition == null) { autoPreparationTask = PrepareAutoTransition(); await autoPreparationTask; }
            PreparedAutoTransition ready = autoTransition;
            if (autoNextRequested && ready != null && !ready.Requested && !AutoTransitionActive)
            {
                autoPreparationTask = PrepareRequestedAutoTransition(ready); await autoPreparationTask;
            }
            if (autoNextRequested && autoTransition == null && !preparingAutoPlay)
            {
                autoNextRequested = false;
                // Missing stems/BPM cannot be beat-matched. A requested queue
                // advance still loads that song instead of waiting for the end.
                if (songQueue.Next == null && songs.Count > 1)
                {
                    int index = songs.FindIndex(song => current != null && String.Equals(song.Audio, current.Audio, StringComparison.OrdinalIgnoreCase));
                    FollowSessionSong(current);
                    SongInfo fresh = AutoPlaybackSongs().FirstOrDefault(song => File.Exists(song.Audio) && songQueue.CanAdd(song.PlaybackKey));
                    if (fresh != null) AddQueuedSong(fresh, songQueue.Items.Count, true);
                }
                if (songQueue.Next != null) await PlayQueuedSong(songQueue.Next);
            }
        }
        finally { autoNextRequested = false; requestingAutoNext = false; }
    }
    private void SkipAutoTransition()
    {
        PreparedAutoTransition plan = autoTransition;
        if (plan == null || playback == null) return;
        long phase = (long)Math.Round(plan.Traits.BeatOffset * plan.Timing.Rate);
        if (!playback.SkipTransition(plan.Bridge, plan.Next, frame => phase + plan.Timing.IncomingSourceFrame(frame))) return;
        autoNextRequested = false; queueAdvancePending = false;
        if (preparingAutoPlay)
        {
            autoTicket++; preparingAutoPlay = false; preparingQueueId = null;
            if (autoCancellation != null) autoCancellation.Cancel();
        }
        PollAutoTransition(); UpdateAutoPreload(); RenderPlaybackQueue();
        if (output != null && output.PlaybackState != PlaybackState.Playing) output.Play();
        UpdateTransport();
    }
    private async Task TickAutoPlay()
    {
        if (openingSavedMix) return;
        PollAutoTransition();
        FillAutoQueue();
        UpdateAutoPreload();
        RefreshAutoRestart(false);
        // Preparation may finish after the outgoing device has reached its end.
        // Resume the scheduled handoff only after a natural finish, never a pause.
        if (queueAdvancePending && autoTransition != null && output != null && output.PlaybackState == PlaybackState.Stopped)
        {
            queueAdvancePending = false; output.Play();
        }
        if (await AdvanceQueuedSong()) return;
        if (!autoPlayEnabled || preparingAutoPlay || requestingAutoNext || autoTransition != null || loading || editingTuning || tuningDialogOpen || editingArtists || preparingReplacement || StemGestureActive || mixer == null || output == null || output.PlaybackState != PlaybackState.Playing) return;
        string attempt = (current == null ? "" : current.Audio) + "|" + sharedBpm + "|" + (songQueue.Next == null ? "" : songQueue.Next.Id) + "|" + String.Join(";", songs.Select(song => song.Audio + ":" + song.Bpm + ":" + song.HasStems));
        if (autoAttempt == attempt) return;
        autoAttempt = attempt;
        autoPreparationTask = PrepareAutoTransition(); await autoPreparationTask;
    }
    private static async Task<string> NormalizeAutoFile(string project, string file, int rate, long frames, CancellationToken token, object cacheOwner = null, Action<double> progress = null)
    {
        try { using (WaveFileReader reader = new WaveFileReader(file)) if (reader.WaveFormat.SampleRate == rate && reader.WaveFormat.Channels == 2 && reader.Length / reader.WaveFormat.BlockAlign == frames) { PlayerMatchedCache.Keep(cacheOwner, file); if (progress != null) progress(1); return file; } } catch { }
        return await PlayerMatching.PrepareFiltered(project, file, "aresample=" + rate + ",apad,atrim=end_sample=" + frames + ",asetpts=PTS-STARTPTS", rate, frames, token, cacheOwner, progress);
    }
    private Task<string> TuneIncomingFile(string file, SongInfo song, double? referenceHz, int rate, long frames, CancellationToken token, object owner, Action<double> progress = null)
    {
        double pitch = TuningReference.Pitch(song.TuningHz, referenceHz);
        return Math.Abs(pitch - 1) < .0000001 ? NormalizeAutoFile(project, file, rate, frames, token, owner, progress)
            : PlayerMatching.PrepareFile(project, file, new StemMatchPlan { Tempo = 1, Pitch = pitch }, rate, frames, token, cacheOwner: owner, progress: progress);
    }
    private bool UsableAutoSong(SongInfo song, AutoPlaySettings settings)
    {
        if (autoFailedSongs.Contains(song.PlaybackKey) || !songQueue.CanAdd(song.PlaybackKey)) return false;
        try { using (WaveFileReader reader = new WaveFileReader(Path.Combine(song.Folder, "vocals.wav"))) return reader.TotalTime.TotalSeconds / (song.SavedMix == null ? 1 : song.SavedMix.OriginalTempo) > settings.StemBars * 720 / song.Bpm.Value + 60 / song.Bpm.Value + .05; } catch { return false; }
    }
    private async Task PrepareAutoTransition()
    {
        if (StemGestureActive) return;
        if (current == null || mixer == null || !mixer.HasStems || preparingAutoPlay || AutoTransitionActive) { if (autoPlayEnabled) message.Text = "Auto play needs a current song with completed stems and a known BPM."; return; }
        int ticket = ++autoTicket, songTicket = generation; var cancellation = new CancellationTokenSource(); autoCancellation = cancellation; preparingAutoPlay = true;
        var loadProgress = new StemLoadingProgress(); autoLoadingProgress = loadProgress; UpdateStemLoadingButtons();
        Stopwatch preparationTime = Stopwatch.StartNew();
        StemMixer expected = mixer; PreparedAutoTransition prepared = null; bool scheduled = false, requested = autoNextRequested; SongInfo next = null;
        FollowSessionSong(current);
        PlaybackQueueEntry queueEntry = songQueue.Next; bool newShuffle = queueEntry == null;
        preparingQueueId = queueEntry == null ? null : queueEntry.Id; RenderPlaybackQueue();
        try
        {
            await EnsureNativeTraits(cancellation.Token);
            double from = sharedBpm ?? currentNativeTraits.Bpm;
            if (from < 30 || from > 300) throw new InvalidOperationException("Auto play needs a known current BPM between 30 and 300.");
            AutoPlaySettings settings = autoSettings.Copy();
            if (queueEntry != null)
            {
                next = QueuedSong(queueEntry);
                if (next == null || !next.HasStems || !next.Bpm.HasValue || next.Bpm < 30 || next.Bpm > 300) { message.Text = "Queue · the next song will play normally when this song ends; a transition needs completed stems and a known BPM."; return; }
            }
            else next = autoShuffle.Choose(AutoPlaybackSongs(), current, from, settings, song => UsableAutoSong(song, settings));
            if (next == null) { message.Text = "Auto play · no unplayed eligible song in this session's BPM range."; return; }
            SelectMix();
            if (queueEntry == null) queueEntry = AddQueuedSong(next, songQueue.Items.Count, true);
            FillAutoQueue();
            int rate = expected.WaveFormat.SampleRate;
            double phase = currentNativeTraits.BeatOffset / originalTempo;
            if (playback == null) playback = new PlayerPlayback(expected);
            // Install a safe loop immediately, then refine it from the actual
            // vocal phrases and instrumental sections before waiting on Next.
            var waitingTiming = new AutoTransitionTiming(from, from, rate, settings);
            double waitingTarget = AutoTempo.InRange(from, next.Bpm.Value, settings) ?? next.Bpm.Value;
            var upcomingTiming = new AutoTransitionTiming(from, waitingTarget, rate, settings, incomingBpm: next.Bpm.Value);
            var waitingLoop = AutoReplayAnalysis.Fallback(from, phase, rate, waitingTiming.SourceEndFrame(expected.TotalFrames), expected.PositionFrames, upcomingTiming.SourceStart(expected.Duration, phase));
            if (autoIncomingMixer == expected) playback.SetSongRestartLoop(expected, waitingLoop);
            else playback.SetOutroLoop(waitingLoop);
            if (expected.ReplayAnalysis == null)
            {
                string[] replayFiles = Enumerable.Range(0, 4).Select(lane => stemSources.File(lane)).ToArray();
                PlaybackTransform[] replayTransforms = Enumerable.Range(0, 4).Select(lane => stemSources.Transform(lane)).ToArray();
                expected.ReplayAnalysis = await Task.Run(() => AutoReplayAnalysis.Read(replayFiles, replayTransforms, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (IsDisposed || generation != songTicket || ticket != autoTicket) throw new OperationCanceledException();
            }
            if (expected.ReplayAnalysis != null)
            {
                waitingLoop = expected.ReplayAnalysis.Plan(from, phase, rate, waitingTiming.SourceEndFrame(expected.TotalFrames), expected.PositionFrames, upcomingTiming.SourceStart(expected.Duration, phase));
                if (autoIncomingMixer == expected) playback.SetSongRestartLoop(expected, waitingLoop);
                else playback.SetOutroLoop(waitingLoop);
            }
            preparingQueueId = queueEntry.Id; RenderPlaybackQueue();
            autoShuffle.Played(current);
            bool followingPreload = autoPreload != null && autoPreload.QueueEntryId == queueEntry.Id && autoPreload.Rate == rate && autoPreload.Identity == AutoSongIdentity(next);
            if (followingPreload) autoLoadingProgress = loadProgress = autoPreload.Progress;
            PreparedAutoSong warmed = await UseAutoPreload(queueEntry, next, rate, cancellation.Token);
            if (followingPreload && warmed == null) { autoLoadingProgress = loadProgress = new StemLoadingProgress(); followingPreload = false; UpdateStemLoadingButtons(); }
            if (next.SavedMix != null && warmed == null) warmed = await PreparePlaylistMix(next, rate, cancellation.Token, loadProgress);
            var source = new AutoTransitionSource { Song = current, Mixer = expected, Audio = originalPlaybackAudio ?? current.Audio, Files = Enumerable.Range(0, 4).Select(lane => stemSources.File(lane)).ToArray(), StemTiming = StemTimingSnapshot(), Bpm = from, Phase = phase };
            source.AudioTransform = originalPlaybackTransform;
            source.FileTransforms = Enumerable.Range(0, 4).Select(lane => stemSources.Transform(lane)).ToArray();
            prepared = await UseAutoPreloadedTransition(queueEntry, next, source, cancellation.Token);
            requested = requested || autoNextRequested;
            if (prepared == null)
            {
                // A reused song can still need a fresh handoff after an edit.
                // Detach its old renderer's reports before starting that work.
                if (followingPreload) { autoLoadingProgress = loadProgress = new StemLoadingProgress(); loadProgress.All(.3); UpdateStemLoadingButtons(); }
                prepared = await BuildAutoTransition(source, queueEntry, next, warmed, settings, newShuffle, requested, cancellation.Token, text => { if (!IsDisposed && ticket == autoTicket) message.Text = text; }, loadProgress);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || generation != songTicket || ticket != autoTicket) throw new OperationCanceledException();
            AutoTransitionTiming timing = prepared.Timing; MusicTraits traits = prepared.Traits;
            bool prompt = requested || autoNextRequested;
            while (true)
            {
                if (prompt)
                {
                    PreparedAutoTransition previous = prepared;
                    prepared = await BuildRequestedAutoTransition(previous, cancellation.Token);
                    previous.Bridge.Dispose();
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (IsDisposed || generation != songTicket || ticket != autoTicket) throw new OperationCanceledException();
                }
                ApplyPreparedAutoMix(prepared);
                long startFrame = (long)Math.Round(prepared.SourceStart * rate);
                bool restartIfLate = autoPlayEnabled && !prompt;
                if (restartIfLate && expected.ReplayAnalysis != null && expected.PositionFrames > startFrame + rate / 20)
                {
                    var lateLoop = expected.ReplayAnalysis.Plan(from, phase, rate, timing.SourceEndFrame(expected.TotalFrames), expected.PositionFrames, prepared.SourceStart, true);
                    if (autoIncomingMixer == expected) playback.SetSongRestartLoop(expected, lateLoop);
                    else playback.SetOutroLoop(lateLoop);
                    // A very short song can have a calculated start at zero.
                    // Render its bridge from the interior replay destination,
                    // rather than hiding a second jump to the intro in a bridge.
                    if (playback.LoopStartFrame.HasValue && playback.LoopStartFrame.Value > startFrame)
                    {
                        PreparedAutoTransition previous = prepared;
                        prepared = await BuildRequestedAutoTransition(previous, cancellation.Token, playback.LoopStartFrame.Value / (double)rate);
                        previous.Bridge.Dispose();
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (IsDisposed || generation != songTicket || ticket != autoTicket) throw new OperationCanceledException();
                        startFrame = (long)Math.Round(prepared.SourceStart * rate);
                    }
                }
                if (!prompt)
                {
                    if (!restartIfLate && expected.PositionFrames > startFrame)
                    {
                        double bar = 240 / from;
                        startFrame = (long)Math.Round((phase + Math.Ceiling((expected.Seconds - phase) / bar) * bar) * rate);
                    }
                    startFrame = Math.Min(startFrame, timing.SourceEndFrame(expected.TotalFrames));
                }
                if (playback.Schedule(expected, startFrame, prepared.Bridge, prepared.Next, restartIfLate, prompt)) break;
                if (!prompt || playback.Current != expected) throw new OperationCanceledException();
            }
            autoTransition = prepared; scheduled = true;
            if (!requestingAutoNext || prepared.Requested) autoNextRequested = false;
            if (AutoEditorEnabled) ShowAddedSong(prepared.Song, prepared.Traits, prepared.Bank.Plan);
            UpdateAutoMarkers(); UpdateSourceButtons();
            autoPreparationSeconds = preparationTime.Elapsed.TotalSeconds;
            UpdateAutoPreload();
            UpdateAutoRestart();
            RenderPlaybackQueue();
            message.Text = playback.WaitingForLoop ? "Auto play · replaying " + current.Title + " at a musical loop boundary · next: " + next.Title : "Auto play · next: " + next.Title + " · " + traits.Bpm.ToString("0.##") + " BPM";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed && autoTicket == ticket)
            {
                if (queueEntry != null && !newShuffle) message.Text = "Queue · " + error.Message + " The next song will play normally when this song ends.";
                else { ShowError(error); if (next != null) { autoFailedSongs.Add(next.PlaybackKey); autoAttempt = null; } if (queueEntry != null) RemoveQueuedSong(queueEntry.Id); }
            }
        }
        finally
        {
            if (!scheduled) DisposeAutoTransition(prepared);
            if (autoTicket == ticket)
            {
                preparingAutoPlay = false; autoLoadingProgress = null; preparingQueueId = null;
                if (!scheduled && playback != null) playback.LoopStartFrame = null;
                UpdateAutoPreload(); UpdateStemLoadingButtons(); RenderPlaybackQueue();
            }
            if (autoCancellation == cancellation) autoCancellation = null; cancellation.Dispose();
        }
    }
    private async Task<PreparedAutoTransition> BuildAutoTransition(AutoTransitionSource source, PlaybackQueueEntry queueEntry, SongInfo next, PreparedAutoSong warmed, AutoPlaySettings settings, bool newShuffle, bool requested, CancellationToken token, Action<string> progress, StemLoadingProgress loadProgress = null)
    {
        loadProgress = loadProgress ?? new StemLoadingProgress();
        double? referenceHz = warmed == null ? tuningReferenceHz : warmed.TuningHz;
        StemMixer expected = source.Mixer;
        int rate = expected.WaveFormat.SampleRate;
        double from = source.Bpm, phase = source.Phase;
        string outgoingAudio = source.Audio; string[] outgoing = source.Files; StemTiming[] outgoingTiming = source.StemTiming;
        PreparedAutoTransition prepared = null;
        try
        {
            // Offline transition ramps need files in the audible timeline.
            // Materialize live transforms here, on the background preparation
            // path, never while applying a user's tempo/key change.
            if (source.AudioTransform != null)
            {
                PlaybackTransform transform = source.AudioTransform;
                outgoingAudio = await PlayerMatching.PrepareFile(project, outgoingAudio, transform.Plan, transform.Rate, transform.Frames, token, transform.Stem, source, loadProgress.AllRange(0, .08));
            }
            if (source.FileTransforms != null)
            {
                outgoing = (string[])outgoing.Clone();
                for (int lane = 0; lane < 4; lane++) if (source.FileTransforms[lane] != null)
                {
                    PlaybackTransform transform = source.FileTransforms[lane];
                    outgoing[lane] = await PlayerMatching.PrepareFile(project, outgoing[lane], transform.Plan, transform.Rate, transform.Frames, token, transform.Stem, source, loadProgress.Range(lane, .08, .2));
                }
            }
            // Retain the materialized outgoing sources so an early Next request
            // only needs a new excerpt, never a second incoming preparation.
            source.Audio = outgoingAudio; source.Files = outgoing;
            source.AudioTransform = null; source.FileTransforms = null;
            MusicTraits traits = warmed == null ? await PlayerMatching.Analyze(project, next, token) : warmed.Traits;
            float nextLoudness = warmed == null ? await PlayerLoudness.Gain(project, next.Audio, token) : warmed.LoudnessGain;
            if (traits.Bpm < 30 || traits.Bpm > 300) throw new InvalidOperationException("The queued song needs a known BPM between 30 and 300 for a transition.");
            double? equivalent = AutoTempo.InRange(from, traits.Bpm, settings);
            if (newShuffle && !equivalent.HasValue) throw new InvalidOperationException("The next song's analyzed BPM is outside the selected range, including half/double time.");
            double target = equivalent ?? traits.Bpm;
            var timing = new AutoTransitionTiming(from, target, rate, settings, incomingBpm: traits.Bpm);
            long nextFrames = warmed == null ? 0 : warmed.Frames;
            if (warmed == null) using (WaveFileReader reader = new WaveFileReader(Path.Combine(next.Folder, "vocals.wav"))) nextFrames = (long)Math.Round(reader.Length / (double)reader.WaveFormat.BlockAlign * rate / reader.WaveFormat.SampleRate);
            if (nextFrames / (double)rate <= traits.BeatOffset + timing.IncomingSeconds) throw new InvalidOperationException("The next song is too short for these stem-change intervals. Reduce the bars in Settings.");
            double start = timing.SourceStart(expected.Duration, phase);
            requested = requested || (source.Mixer == mixer && autoNextRequested);
            if (requested) start = AutoTransitionTiming.RequestedStart(expected.Seconds, from, phase, timing.SourceEnd(expected.Duration));
            bool editedStems = outgoingTiming.Any(state => state.Changed);
            if (settings.EarlyStemTransitions && !requested)
            {
                progress("Auto play · checking the outgoing fade…");
                double firstSwap = start + timing.SourceRampSeconds;
                double searchStart = Math.Max(expected.Seconds, firstSwap - Math.Max(30, timing.SourceSeconds));
                double early = await Task.Run(() =>
                {
                    AutoAudioDynamics songDynamics = editedStems ? null : AutoAudioDynamics.Read(outgoingAudio, token);
                    double result = songDynamics != null && songDynamics.HasSignal ? songDynamics.EarlySeconds(searchStart, firstSwap, from) : firstSwap;
                    for (int lane = 0; lane < 4; lane++)
                    {
                        // Edited lanes are analyzed after their source-time mapping
                        // is rendered, so an audible loop cannot look like an EOF.
                        if (outgoingTiming[lane].Changed) continue;
                        AutoAudioDynamics dynamics = AutoAudioDynamics.Read(outgoing[lane], token);
                        if (dynamics.HasSignal) result = Math.Min(result, dynamics.EarlySeconds(searchStart, firstSwap, from));
                    }
                    return result;
                }, token);
                double bar = 240 / from;
                if (early < firstSwap - .00001)
                {
                    double normalStart = start;
                    // Finish the ramp before an early ending. Extend the bridge
                    // so unaffected stems retain their original source positions.
                    double earliestStart = Math.Max(0, normalStart - Math.Max(0, nextFrames / (double)rate - traits.BeatOffset - timing.IncomingSeconds - .1) * target / from);
                    start = Math.Min(normalStart, Math.Max(phase + Math.Ceiling((earliestStart - phase) / bar) * bar, Math.Max(0, phase + Math.Floor((early - timing.SourceRampSeconds - phase) / bar) * bar)));
                    timing = new AutoTransitionTiming(from, target, rate, settings, (normalStart - start) * from / target, traits.Bpm);
                }
            }
            long normalFrames = timing.TotalFrames;
            if (settings.EarlyStemTransitions && !requested)
            {
                double spare = Math.Max(0, nextFrames / (double)rate - traits.BeatOffset - timing.IncomingSeconds - .1);
                timing.SetTotalFrames(normalFrames + (long)Math.Round(Math.Min(Math.Max(4, 8 * 240 / traits.Bpm), spare) * rate));
            }
            prepared = new PreparedAutoTransition { Song = next, PlaylistMix = next.SavedMix == null ? null : warmed, QueueEntryId = queueEntry.Id, Traits = traits, Timing = timing, SourceStart = start, Reference = expected, Source = source, OutgoingTiming = outgoingTiming, SwapFrames = new long[4], OutgoingFiles = new string[4], IncomingFiles = new string[4], NextFiles = new string[4], OutgoingPeaks = new float[4][], IncomingPeaks = new float[4][], NextPeaks = new float[4][] };
            for (int position = 0; position < 4; position++) prepared.SwapFrames[timing.Order[position]] = timing.SwapFrame(position);
            prepared.TuningHz = referenceHz;
            progress("Auto play · preparing transition to " + next.Title + "…");
            prepared.BridgeAudio = await PlayerMatching.PrepareFiltered(project, outgoingAudio, timing.RampFilter(project, start, expected.TotalFrames, 0), rate, timing.TotalFrames, token, prepared, loadProgress.AllRange(.2, .28));
            prepared.NextAudio = warmed == null ? await TuneIncomingFile(next.Audio, next, referenceHz, rate, nextFrames, token, prepared, loadProgress.AllRange(.28, .35)) : warmed.Audio;
            loadProgress.All(.35);
            PlayerMatchedCache.Keep(prepared, prepared.NextAudio);
            for (int lane = 0; lane < 4; lane++)
            {
                string editedFilter = outgoingTiming[lane].Changed ? outgoingTiming[lane].Filter(rate, expected.TotalFrames) + "," : "";
                prepared.OutgoingFiles[lane] = await PlayerMatching.PrepareFiltered(project, outgoing[lane], editedFilter + timing.RampFilter(project, start, expected.TotalFrames, lane), rate, timing.TotalFrames, token, prepared, loadProgress.Range(lane, .35, .55));
                prepared.NextFiles[lane] = warmed == null ? await TuneIncomingFile(Path.Combine(next.Folder, StemMixer.StemNames[lane] + ".wav"), next, referenceHz, rate, nextFrames, token, prepared, loadProgress.Range(lane, .55, .7)) : warmed.Files[lane];
                PlayerMatchedCache.Keep(prepared, prepared.NextFiles[lane]);
                prepared.IncomingFiles[lane] = await PlayerMatching.PrepareFiltered(project, prepared.NextFiles[lane], timing.IncomingFilter(traits.BeatOffset), rate, timing.TotalFrames, token, prepared, loadProgress.Range(lane, .7, .88));
                int index = lane;
                await Task.Run(() =>
                {
                    prepared.OutgoingPeaks[index] = PlayerWaveforms.Read(prepared.OutgoingFiles[index], token, loadProgress.Range(index, .88, .9)); prepared.IncomingPeaks[index] = PlayerWaveforms.Read(prepared.IncomingFiles[index], token, loadProgress.Range(index, .9, .92)); prepared.NextPeaks[index] = warmed == null ? PlayerWaveforms.Read(prepared.NextFiles[index], token, loadProgress.Range(index, .92, .94)) : warmed.Peaks[index];
                });
                loadProgress.Report(lane, .94);
                progress("Auto play · preparing " + next.Title + " · " + (lane + 1) + " / 4 stems");
            }
            // A requested Next retains the incoming side of this preparation.
            // Its outgoing excerpt is refreshed and validated before scheduling.
            prepared.OutgoingDynamics = await Task.Run(() => prepared.OutgoingFiles.Select(file => AutoAudioDynamics.Read(file, token)).ToArray(), token);
            prepared.IncomingDynamics = await Task.Run(() => prepared.IncomingFiles.Select(file => AutoAudioDynamics.Read(file, token)).ToArray(), token);
            requested = requested || (source.Mixer == mixer && autoNextRequested);
            if (settings.EarlyStemTransitions && !requested)
            {
                progress("Auto play · choosing quiet stem and vocal phrase boundaries…");
                AutoStemTransitionPlan changes = await Task.Run(() => AutoStemTransitions.Plan(
                    prepared.OutgoingDynamics, prepared.IncomingDynamics, timing, token), token);
                prepared.CutFrames = changes.CutFrames; prepared.SwapFrames = changes.StartFrames;
                long frames = Math.Max(normalFrames, prepared.SwapFrames.Max() + Math.Max(1, rate / 20));
                if (frames != timing.TotalFrames)
                {
                    timing.SetTotalFrames(frames);
                    prepared.BridgeAudio = await NormalizeAutoFile(project, prepared.BridgeAudio, rate, frames, token, prepared, loadProgress.AllRange(.94, .95));
                    for (int lane = 0; lane < 4; lane++)
                    {
                        prepared.OutgoingFiles[lane] = await NormalizeAutoFile(project, prepared.OutgoingFiles[lane], rate, frames, token, prepared, loadProgress.Range(lane, .95, .96));
                        prepared.IncomingFiles[lane] = await NormalizeAutoFile(project, prepared.IncomingFiles[lane], rate, frames, token, prepared, loadProgress.Range(lane, .96, .97));
                        int index = lane;
                        await Task.Run(() => { prepared.OutgoingPeaks[index] = PlayerWaveforms.Read(prepared.OutgoingFiles[index], token, loadProgress.Range(index, .97, .98)); prepared.IncomingPeaks[index] = PlayerWaveforms.Read(prepared.IncomingFiles[index], token, loadProgress.Range(index, .98, .99)); }, token);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            prepared.Bridge = new StemMixer(prepared.BridgeAudio, source.Song.Folder, prepared.OutgoingFiles);
            prepared.Next = new StemMixer(prepared.NextAudio, next.Folder, prepared.PlaylistMix == null ? prepared.NextFiles : prepared.PlaylistMix.OriginalFiles);
            AutoReplayAnalysis replayAnalysis = await Task.Run(() => AutoReplayAnalysis.Read(prepared.NextFiles, null, token), token);
            prepared.Bridge.SetLoudness(expected.OriginalLoudnessGain, Enumerable.Range(0, 4).Select(expected.StemLoudnessGain).ToArray());
            prepared.Next.SetLoudness(nextLoudness);
            prepared.Bridge.Volume = prepared.Next.Volume = volume.Value / 100f; prepared.Bridge.ApplyMix(mix); prepared.Next.ApplyMix(mix);
            PlayerMix incomingMix = next.SavedMix == null ? null : SavedPlaybackMix(next.SavedMix);
            if (incomingMix != null)
            {
                prepared.Next.Volume = next.SavedMix.Volume / 100f; prepared.Next.ApplyMix(incomingMix); prepared.Next.FollowLiveMix = false;
                for (int lane = 0; lane < 4; lane++) if (warmed.MixSources.Active[lane] != null) prepared.Next.ReplaceStem(lane, warmed.MixSources.Active[lane].Files[lane], warmed.MixSources.Active[lane].LoudnessGain);
            }
            prepared.Next.ReplayAnalysis = replayAnalysis;
            prepared.Next.SeekFrame(timing.IncomingEndFrame(traits.BeatOffset));
            for (int lane = 0; lane < 4; lane++)
            {
                float laneLoudness = prepared.PlaylistMix == null || warmed.MixSources.Active[lane] == null ? nextLoudness : warmed.MixSources.Active[lane].LoudnessGain;
                prepared.Bridge.ScheduleStemTransition(lane, prepared.CutFrames == null ? prepared.SwapFrames[lane] : prepared.CutFrames[lane], prepared.SwapFrames[lane], prepared.IncomingFiles[lane], laneLoudness, incomingMix == null ? (float?)null : incomingMix.Audible(lane) ? incomingMix.Levels[lane] : 0, incomingMix == null ? (float?)null : next.SavedMix.Volume / 100f);
            }
            prepared.Bank = new StemSourceBank { Song = next, Traits = traits, LoudnessGain = nextLoudness, Plan = new StemMatchPlan { Tempo = 1, MatchedBpm = traits.Bpm }, Files = prepared.IncomingFiles, Peaks = prepared.IncomingPeaks };
            PlayerMatchedCache.Keep(prepared.Bank, prepared.IncomingFiles);
            loadProgress.All(1);
            return prepared;
        }
        catch
        {
            DisposeAutoTransition(prepared);
            throw;
        }
    }
    private async Task<PreparedAutoTransition> BuildRequestedAutoTransition(PreparedAutoTransition previous, CancellationToken token, double? replayStart = null)
    {
        StemMixer expected = previous.Reference; AutoTransitionTiming timing = previous.Timing;
        AutoTransitionSource source = previous.Source;
        // Incoming audio is independent of the outgoing starting position.
        // Reuse it, but always render outgoing audio from the scheduled frame.
        var prepared = new PreparedAutoTransition
        {
            Song = previous.Song, Traits = previous.Traits, Timing = timing, Reference = expected, Source = source, Requested = !replayStart.HasValue,
            QueueEntryId = previous.QueueEntryId, PlaylistMix = previous.PlaylistMix, TuningHz = previous.TuningHz, OutgoingTiming = previous.OutgoingTiming,
            Next = previous.Next, NextAudio = previous.NextAudio, NextFiles = previous.NextFiles, NextPeaks = previous.NextPeaks,
            IncomingFiles = previous.IncomingFiles, IncomingPeaks = previous.IncomingPeaks, IncomingDynamics = previous.IncomingDynamics, Bank = previous.Bank,
            SwapFrames = new long[4], CutFrames = new long[4], OutgoingFiles = new string[4], OutgoingPeaks = new float[4][]
        };
        try
        {
            double start = replayStart ?? AutoTransitionTiming.RequestedStart(expected.Seconds, source.Bpm, source.Phase, expected.Duration);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (IsDisposed || playback.Current != expected || (!replayStart.HasValue && expected.PositionFrames >= expected.TotalFrames)) throw new OperationCanceledException();
                if (start >= expected.Duration - .02) throw new OperationCanceledException();
                double position = expected.Seconds; Stopwatch renderTime = Stopwatch.StartNew();
                if (prepared.Bridge != null) { prepared.Bridge.Dispose(); prepared.Bridge = null; }
                prepared.SourceStart = start;
                message.Text = "Auto play · beat-matching from the current position…";
                prepared.BridgeAudio = await PlayerMatching.PrepareFiltered(project, source.Audio, timing.RampFilter(project, start, expected.TotalFrames, 0, replayStart.HasValue), timing.Rate, timing.TotalFrames, token, prepared);
                for (int lane = 0; lane < 4; lane++)
                {
                    string edited = prepared.OutgoingTiming[lane].Changed ? prepared.OutgoingTiming[lane].Filter(timing.Rate, expected.TotalFrames) + "," : "";
                    prepared.OutgoingFiles[lane] = await PlayerMatching.PrepareFiltered(project, source.Files[lane], edited + timing.RampFilter(project, start, expected.TotalFrames, lane, replayStart.HasValue), timing.Rate, timing.TotalFrames, token, prepared);
                    int index = lane;
                    prepared.OutgoingPeaks[lane] = await Task.Run(() => PlayerWaveforms.Read(prepared.OutgoingFiles[index], token), token);
                }
                prepared.OutgoingDynamics = await Task.Run(() => prepared.OutgoingFiles.Select(file => AutoAudioDynamics.Read(file, token)).ToArray(), token);
                token.ThrowIfCancellationRequested();
                prepared.Bridge = new StemMixer(prepared.BridgeAudio, source.Song.Folder, prepared.OutgoingFiles);
                prepared.Bridge.SetLoudness(expected.OriginalLoudnessGain, Enumerable.Range(0, 4).Select(expected.StemLoudnessGain).ToArray());
                ApplyPreparedAutoMix(prepared);
                PlayerMix incomingMix = prepared.Song.SavedMix == null ? null : SavedPlaybackMix(prepared.Song.SavedMix);
                for (int positionIndex = 0; positionIndex < 4; positionIndex++)
                {
                    int lane = timing.Order[positionIndex]; long frame = timing.SwapFrame(positionIndex);
                    prepared.CutFrames[lane] = prepared.SwapFrames[lane] = frame;
                    float loudness = prepared.PlaylistMix == null || prepared.PlaylistMix.MixSources.Active[lane] == null ? prepared.Next.OriginalLoudnessGain : prepared.PlaylistMix.MixSources.Active[lane].LoudnessGain;
                    prepared.Bridge.ScheduleStemTransition(lane, frame, frame, prepared.IncomingFiles[lane], loudness,
                        incomingMix == null ? (float?)null : incomingMix.Audible(lane) ? incomingMix.Levels[lane] : 0, incomingMix == null ? (float?)null : prepared.Song.SavedMix.Volume / 100f);
                }
                // All I/O and analysis must finish before checking the cursor.
                // Never slide an old excerpt to a later beat: that skips music.
                double now = expected.Seconds;
                if (replayStart.HasValue || (now <= start - .02 && start <= now + 5))
                {
                    PlayerMatchedCache.Keep(prepared, prepared.IncomingFiles.Concat(prepared.NextFiles).Concat(new[] { prepared.NextAudio }).ToArray());
                    return prepared;
                }
                // Estimate just the outgoing render, not the full incoming load.
                // Use a beat on retries so a slow-tempo bar cannot add a long wait.
                double lead = now > position + .01 ? renderTime.Elapsed.TotalSeconds + .25 : .05;
                double beat = 60 / source.Bpm;
                start = Math.Min(expected.Duration, source.Phase + Math.Ceiling((now + lead - source.Phase) / beat) * beat);
            }
        }
        catch
        {
            prepared.Next = null; DisposeAutoTransition(prepared);
            throw;
        }
    }
    private async Task PrepareRequestedAutoTransition(PreparedAutoTransition previous)
    {
        if (previous.Requested || playback == null || IsDisposed) return;
        int ticket = ++autoTicket, songTicket = generation;
        var cancellation = new CancellationTokenSource(); autoCancellation = cancellation;
        preparingAutoPlay = true; preparingQueueId = previous.QueueEntryId; RenderPlaybackQueue();
        PreparedAutoTransition prepared = null; bool scheduled = false;
        try
        {
            while (true)
            {
                PreparedAutoTransition replacement = await BuildRequestedAutoTransition(previous, cancellation.Token);
                if (prepared != null) prepared.Bridge.Dispose();
                prepared = replacement;
                cancellation.Token.ThrowIfCancellationRequested();
                if (IsDisposed || generation != songTicket || ticket != autoTicket || autoTransition != previous) throw new OperationCanceledException();
                long startFrame = (long)Math.Round(prepared.SourceStart * prepared.Timing.Rate);
                if (playback.ReplaceScheduledBridge(prepared.Reference, previous.Bridge, prepared.Bridge, prepared.Next, startFrame, true)) break;
                if (playback.Current != prepared.Reference) return;
            }
            autoTransition = prepared; scheduled = true;
            UpdateAutoMarkers(); UpdateSourceButtons(); UpdateAutoPreload(); UpdateAutoRestart();
            message.Text = "Auto play · next: " + prepared.Song.Title + " · " + prepared.Traits.Bpm.ToString("0.##") + " BPM";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed && ticket == autoTicket) ShowError(error); }
        finally
        {
            if (!scheduled && prepared != null) { prepared.Next = null; DisposeAutoTransition(prepared); }
            if (ticket == autoTicket) { preparingAutoPlay = false; preparingQueueId = null; RenderPlaybackQueue(); }
            if (autoCancellation == cancellation) autoCancellation = null;
            cancellation.Dispose();
        }
    }
    private static void DisposeAutoTransition(PreparedAutoTransition prepared)
    {
        if (prepared == null) return;
        if (prepared.Bridge != null) prepared.Bridge.Dispose();
        if (prepared.Next != null) prepared.Next.Dispose();
    }
    private void ApplyPreparedAutoMix(PreparedAutoTransition prepared)
    {
        prepared.Bridge.Volume = volume.Value / 100f; prepared.Bridge.ApplyMix(mix);
        if (prepared.Next.FollowLiveMix) { prepared.Next.Volume = volume.Value / 100f; prepared.Next.ApplyMix(mix); }
    }
    private void PollAutoTransition()
    {
        PreparedAutoTransition plan = autoTransition;
        if (plan == null || playback == null) return;
        StemMixer live = playback.Current;
        if (live == plan.Bridge)
        {
            if (!plan.Started)
            {
                plan.Started = true; mixer = live; preferOriginal = false;
                if (waveformCancellation != null) waveformCancellation.Cancel();
                stemSources = new StemSourceState(current.Folder, plan.OutgoingFiles); stemSources.Available = plan.Bank;
                for (int lane = 0; lane < 4; lane++) { stemSources.OriginalPeaks[lane] = plan.OutgoingPeaks[lane]; playingStemFiles[lane] = plan.OutgoingFiles[lane]; waveforms[lane].Peaks = plan.OutgoingPeaks[lane]; waveforms[lane].Duration = live.Duration; }
                if (!String.Equals(plan.Song.Audio, current.Audio, StringComparison.OrdinalIgnoreCase)) keyShifts.Remove(plan.Song.Audio);
                ShowAddedSong(plan.Song, plan.Traits, plan.Bank.Plan); StyleModes(); SetEnabled(true); RenderPlaybackQueue();
                SelectQueuedSong(plan.QueueEntryId);
                UpdateAutoPreload();
                UpdateAutoRestart();
            }
            sharedBpm = plan.Timing.BpmAt(live.Seconds);
            for (int position = 0; position < 4; position++)
            {
                int lane = plan.Timing.Order[position];
                bool incoming = live.PositionFrames >= plan.SwapFrames[lane];
                bool silent = !incoming && live.PositionFrames >= (plan.CutFrames == null ? plan.SwapFrames[lane] : plan.CutFrames[lane]);
                if (plan.Switched[lane] != incoming || plan.Silenced[lane] != silent)
                {
                    plan.Switched[lane] = incoming; plan.Silenced[lane] = silent;
                    stemSources.Active[lane] = incoming ? plan.Bank : null; ShowSource(lane);
                    if (silent) waveforms[lane].Peaks = new float[100];
                    waveforms[lane].Invalidate();
                }
            }
            UpdateTuningHeader();
            message.Text = live.PositionFrames < plan.Timing.RampFrames ? "Auto play · matching tempo to " + plan.Timing.ToBpm.ToString("0.##") + " BPM" : plan.CutFrames != null && live.PositionFrames >= plan.CutFrames[0] && live.PositionFrames < plan.SwapFrames[0] ? "Auto play · waiting for the next vocal phrase" : "Auto play · transitioning to " + plan.Song.Title;
        }
        else if (live == plan.Next)
        {
            bool restarted = playback.RestartedHandoff(live);
            autoIncomingMixer = autoPlayEnabled ? live : null;
            generation++; mixer = live; current = plan.Song; preferredSong = current.Audio; currentNativeTraits = plan.Traits;
            loadedMixId = null;
            sharedBpm = null; originalTempo = 1; keyShifts.Clear(); nativeFrames = live.TotalFrames; nativeRate = live.WaveFormat.SampleRate;
            loadedTuningHz = plan.TuningHz;
            originalPlaybackAudio = plan.NextAudio; originalPlaybackFiles = plan.NextFiles; preferOriginal = false;
            originalPlaybackTransform = null; originalPlaybackTransforms = null;
            stemSources = new StemSourceState(current.Folder, plan.NextFiles);
            PlayerMatchedCache.Keep(stemSources, plan.NextAudio);
            for (int lane = 0; lane < 4; lane++) { stemSources.OriginalPeaks[lane] = plan.NextPeaks[lane]; playingStemFiles[lane] = plan.NextFiles[lane]; waveforms[lane].Peaks = plan.NextPeaks[lane]; waveforms[lane].Duration = live.Duration; }
            autoShuffle.Played(current); autoTransition = null; autoAttempt = null;
            UpdateAutoMarkers();
            SelectQueuedSong(plan.QueueEntryId);
            songTitle.Text = current.Title; songTitle.ForeColor = SongColor(current); artistLabel.Text = current.Artist; tip.SetToolTip(songTitle, current.Title); tip.SetToolTip(artistLabel, current.Artist);
            if (cover.Image != null) { cover.Image.Dispose(); cover.Image = null; }
            try { if (File.Exists(current.Cover)) using (Image source = Image.FromFile(current.Cover)) cover.Image = SquareCover(source, 512); } catch { }
            ShowAddedSong(null); UpdateBrowserSong(); RenderSongs(); StyleModes(); SetEnabled(true); SavePreferences();
            if (plan.PlaylistMix != null) { PromotePlaylistMix(plan); StyleModes(); UpdateBrowserSong(); UpdateTransport(); SavePreferences(); }
            playback.ReleaseRetiredInBackground(); message.Text = autoPlayEnabled ? restarted ? "Auto play · looped back within " + current.Title + " to leave time for the next song" : "Auto play · " + current.Title : "Auto play off.";
        }
    }
}
