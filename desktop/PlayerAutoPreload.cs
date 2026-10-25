using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

// Once the immediate handoff is ready, build the complete following handoff.
// Keep one speculative preparation and reuse its mixers when its turn arrives.
internal sealed class AutoTransitionSource
{
    internal SongInfo Song;
    internal StemMixer Mixer;
    internal string Audio, Identity;
    internal string[] Files;
    internal PlaybackTransform AudioTransform;
    internal PlaybackTransform[] FileTransforms;
    internal StemTiming[] StemTiming;
    internal double Bpm, Phase;
}

internal sealed class PreparedAutoSong
{
    internal double? TuningHz;
    internal MusicTraits Traits;
    internal long Frames;
    internal string Audio;
    internal float LoudnessGain = 1;
    internal readonly string[] Files = new string[4];
    internal readonly float[][] Peaks = new float[4][];
    internal StemSourceState MixSources;
    internal MusicTraits NativeTraits;
    internal long NativeFrames;
    internal string[] OriginalFiles;
}

internal sealed class AutoSongPreload
{
    internal double? TuningHz;
    internal string QueueEntryId, Identity;
    internal int Rate;
    internal bool CompletionShown, Consumed;
    internal AutoTransitionSource Source;
    internal string SettingsIdentity;
    internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
    internal readonly StemLoadingProgress Progress = new StemLoadingProgress();
    internal Task<PreparedAutoSong> Loading;
    internal Task<PreparedAutoTransition> Preparing;
}

internal sealed partial class PlayerForm
{
    private AutoSongPreload autoPreload;
    private Task<PreparedAutoSong> autoPreloadTask;
    private Task<PreparedAutoTransition> autoPreloadPreparationTask;
    private bool updatingAutoPreload;
    private bool fillingAutoQueue;
    private const int AutoQueueLookahead = 10;

    private void FillAutoQueue()
    {
        if (fillingAutoQueue || startingPlaylistAuto || !autoPlayEnabled || IsDisposed || Disposing || loading || editingArtists || current == null || mixer == null) return;
        fillingAutoQueue = true;
        try
        {
            FollowSessionSong(current);
            while (songQueue.CurrentIndex >= 0 && songQueue.Items.Count - songQueue.CurrentIndex - 1 < AutoQueueLookahead)
            {
                PlaybackQueueEntry tail = songQueue.Items.Last();
                SongInfo previous = QueuedSong(tail);
                if (previous == null) return;
                double bpm = autoTransition != null && tail.Id == autoTransition.QueueEntryId ? autoTransition.Traits.Bpm
                    : tail.Id == songQueue.CurrentId && !AutoTransitionActive ? sharedBpm ?? (currentNativeTraits == null ? previous.Bpm ?? 0 : currentNativeTraits.Bpm)
                    : previous.Bpm ?? 0;
                if (bpm < 30 || bpm > 300) return;
                // Each suggestion follows its predecessor's BPM, including
                // explicit queue choices. Never insert ahead of existing rows.
                SongInfo fresh = autoShuffle.Choose(AutoPlaybackSongs(), previous, bpm, autoSettings, candidate => UsableAutoSong(candidate, autoSettings));
                if (fresh == null || AddQueuedSong(fresh, songQueue.Items.Count, true) == null) return;
            }
        }
        finally { fillingAutoQueue = false; }
    }

    private string AutoSongIdentity(SongInfo song)
    {
        if (song == null || !song.HasStems) return null;
        try
        {
            IEnumerable<string> files = new[] { song.Audio, Path.Combine(song.Folder, "metadata.json") }.Concat(StemMixer.StemNames.Select(stem => Path.Combine(song.Folder, stem + ".wav")));
            if (song.SavedMix != null) foreach (SavedStemBank bank in song.SavedMix.Banks)
            {
                SongInfo donor = SavedMixSong(bank.Audio);
                files = files.Concat(new[] { donor.Audio, Path.Combine(donor.Folder, "metadata.json") }).Concat(StemMixer.StemNames.Select(stem => Path.Combine(donor.Folder, stem + ".wav")));
            }
            return String.Join("|", files.Select(file => { var info = new FileInfo(file); return info.FullName + ":" + (info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "missing"); })) + (song.SavedMix == null ? "" : new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(song.SavedMix));
        }
        catch { return null; }
    }

    private void CancelAutoPreload()
    {
        AutoSongPreload previous = autoPreload; autoPreload = null;
        if (previous == null) return;
        previous.Cancellation.Cancel();
        if (previous.Preparing.IsCompleted)
        {
            if (!previous.Consumed) DisposeAutoTransition(previous.Preparing.Result);
            previous.Cancellation.Dispose();
        }
        UpdateStemLoadingButtons(); RenderPlaybackQueue();
    }

    private string AutoSettingsIdentity()
    {
        return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { auto = autoSettings.Saved(), tuning = tuningReferenceHz });
    }

    private void UpdateAutoPreload()
    {
        if (updatingAutoPreload) return;
        updatingAutoPreload = true;
        try
        {
            if (!autoPlayEnabled || IsDisposed || Disposing || loading || editingArtists) { CancelAutoPreload(); return; }
            PreparedAutoTransition transition = autoTransition;
            string predecessorId = transition == null ? null : transition.QueueEntryId;
            if (predecessorId == null)
            {
                // Preserve the warmed successor through the final handoff so
                // normal preparation can consume work that is still running.
                PlaybackQueueEntry next = songQueue.Next;
                if (autoPreload != null && (next == null || next.Id != autoPreload.QueueEntryId || AutoSongIdentity(QueuedSong(next)) != autoPreload.Identity || autoPreload.Source.Mixer != mixer || autoPreload.SettingsIdentity != AutoSettingsIdentity())) CancelAutoPreload();
                return;
            }
            FillAutoQueue();
            int index = songQueue.Items.FindIndex(entry => entry.Id == predecessorId);
            if (index < 0) { CancelAutoPreload(); return; }
            PlaybackQueueEntry successor = songQueue.Items.Skip(index + 1).FirstOrDefault();
            SongInfo song = QueuedSong(successor);
            if (song == null || !song.HasStems || !song.Bpm.HasValue || song.Bpm < 30 || song.Bpm > 300) { CancelAutoPreload(); return; }
            string identity = AutoSongIdentity(song); int rate = transition.Timing.Rate;
            string settingsIdentity = AutoSettingsIdentity(), sourceIdentity = AutoSongIdentity(transition.Song);
            if (identity == null) { CancelAutoPreload(); return; }
            if (autoPreload != null && autoPreload.QueueEntryId == successor.Id && autoPreload.Rate == rate && autoPreload.Identity == identity && autoPreload.Source.Mixer == transition.Next && autoPreload.Source.Identity == sourceIdentity && autoPreload.SettingsIdentity == settingsIdentity)
            {
                if (autoPreload.Preparing.IsCompleted && !autoPreload.CompletionShown) { autoPreload.CompletionShown = true; RenderPlaybackQueue(); UpdateAutoRestart(); }
                return;
            }
            CancelAutoPreload();
            var source = new AutoTransitionSource { Song = transition.Song, Mixer = transition.Next, Audio = transition.NextAudio, Files = transition.NextFiles, StemTiming = new[] { new StemTiming(), new StemTiming(), new StemTiming(), new StemTiming() }, Bpm = transition.Traits.Bpm, Phase = transition.Traits.BeatOffset, Identity = sourceIdentity };
            var preload = new AutoSongPreload { QueueEntryId = successor.Id, Rate = rate, Identity = identity, Source = source, SettingsIdentity = settingsIdentity };
            preload.TuningHz = tuningReferenceHz;
            preload.Loading = LoadAutoSuccessor(song, preload, autoPreloadTask);
            preload.Preparing = PrepareAutoSuccessor(successor, song, preload, autoSettings.Copy(), autoPreloadPreparationTask);
            autoPreload = preload; autoPreloadTask = preload.Loading; autoPreloadPreparationTask = preload.Preparing;
            RenderPlaybackQueue();
        }
        finally { updatingAutoPreload = false; }
    }

    private async Task<PreparedAutoTransition> PrepareAutoSuccessor(PlaybackQueueEntry entry, SongInfo song, AutoSongPreload preload, AutoPlaySettings settings, Task<PreparedAutoTransition> previous)
    {
        PreparedAutoTransition prepared = null;
        CancellationToken token = preload.Cancellation.Token;
        try
        {
            if (previous != null) await previous;
            PreparedAutoSong ready = await preload.Loading;
            token.ThrowIfCancellationRequested();
            if (ready == null) return null;
            prepared = await BuildAutoTransition(preload.Source, entry, song, ready, settings, false, false, token, delegate { }, preload.Progress);
            token.ThrowIfCancellationRequested();
            if (preload.Identity != AutoSongIdentity(song) || preload.Source.Identity != AutoSongIdentity(preload.Source.Song)) { DisposeAutoTransition(prepared); return null; }
            return prepared;
        }
        catch { DisposeAutoTransition(prepared); return null; }
        finally { if (token.IsCancellationRequested) preload.Cancellation.Dispose(); }
    }

    private async Task<PreparedAutoTransition> UseAutoPreloadedTransition(PlaybackQueueEntry entry, SongInfo song, AutoTransitionSource source, CancellationToken token)
    {
        AutoSongPreload preload = autoPreload;
        if (preload == null || preload.QueueEntryId != entry.Id || preload.Identity != AutoSongIdentity(song) || preload.Source.Mixer != source.Mixer || preload.Source.Identity != AutoSongIdentity(source.Song) || preload.SettingsIdentity != AutoSettingsIdentity() || Math.Abs(preload.Source.Bpm - source.Bpm) > .000001 || Math.Abs(preload.Source.Phase - source.Phase) > .000001 || !preload.Source.Files.SequenceEqual(source.Files, StringComparer.OrdinalIgnoreCase) || source.StemTiming.Any(timing => timing.Changed)) return null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            Task canceled = Task.Delay(Timeout.Infinite, wait.Token);
            await Task.WhenAny(preload.Preparing, canceled); wait.Cancel(); token.ThrowIfCancellationRequested();
        }
        PreparedAutoTransition ready = await preload.Preparing;
        if (ready == null || autoPreload != preload || preload.Identity != AutoSongIdentity(song)) return null;
        preload.Consumed = true;
        autoPreload = null;
        preload.Cancellation.Dispose();
        return ready;
    }

    private async Task<PreparedAutoSong> LoadAutoSuccessor(SongInfo song, AutoSongPreload preload, Task<PreparedAutoSong> previous)
    {
        CancellationToken token = preload.Cancellation.Token;
        try
        {
            // Replacements wait for canceled readers/renders to release their
            // handles; at most one speculative song is loaded at a time.
            if (previous != null) await previous;
            token.ThrowIfCancellationRequested();
            PreparedAutoSong result = await Task.Run(async delegate
            {
                if (song.SavedMix != null) return await PreparePlaylistMix(song, preload.Rate, token, preload.Progress);
                var ready = new PreparedAutoSong { Traits = await PlayerMatching.Analyze(project, song, token), LoudnessGain = await PlayerLoudness.Gain(project, song.Audio, token) };
                ready.TuningHz = preload.TuningHz;
                using (var reader = new WaveFileReader(Path.Combine(song.Folder, "vocals.wav"))) ready.Frames = (long)Math.Round(reader.Length / (double)reader.WaveFormat.BlockAlign * preload.Rate / reader.WaveFormat.SampleRate);
                ready.Audio = await TuneIncomingFile(song.Audio, song, ready.TuningHz, preload.Rate, ready.Frames, token, ready, preload.Progress.AllRange(0, .1));
                for (int lane = 0; lane < 4; lane++)
                {
                    token.ThrowIfCancellationRequested();
                    ready.Files[lane] = await TuneIncomingFile(Path.Combine(song.Folder, StemMixer.StemNames[lane] + ".wav"), song, ready.TuningHz, preload.Rate, ready.Frames, token, ready, preload.Progress.Range(lane, .1, .2));
                    ready.Peaks[lane] = PlayerWaveforms.Read(ready.Files[lane], token, preload.Progress.Range(lane, .2, .3));
                }
                token.ThrowIfCancellationRequested();
                return AutoSongIdentity(song) == preload.Identity ? ready : null;
            }, token);
            return result;
        }
        catch (OperationCanceledException) { return null; }
        // A speculative failure must not remove or mark the queued song played.
        // The ordinary preparation path can retry and report a useful error.
        catch { return null; }
    }

    private async Task<PreparedAutoSong> UseAutoPreload(PlaybackQueueEntry entry, SongInfo song, int rate, CancellationToken token)
    {
        AutoSongPreload preload = autoPreload;
        bool matches = preload != null && preload.QueueEntryId == entry.Id && preload.Rate == rate && preload.Identity == AutoSongIdentity(song);
        if (!matches) CancelAutoPreload();
        Task<PreparedAutoSong> task = matches ? preload.Loading : autoPreloadTask;
        if (task == null) return null;
        // Canceling foreground preparation must not wait for background IO.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            Task canceled = Task.Delay(Timeout.Infinite, wait.Token);
            await Task.WhenAny(task, canceled); wait.Cancel(); token.ThrowIfCancellationRequested();
        }
        PreparedAutoSong ready = await task;
        return matches && autoPreload == preload && preload.Identity == AutoSongIdentity(song) ? ready : null;
    }
}
