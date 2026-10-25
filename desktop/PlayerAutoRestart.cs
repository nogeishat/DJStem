using System;
using System.Linq;

internal sealed partial class PlayerForm
{
    private StemMixer autoIncomingMixer;
    private double autoPreparationSeconds;

    private void UpdateAutoReplayAfterSeek()
    {
        if (!autoPlayEnabled || !preparingAutoPlay || playback == null || playback.Current != mixer || currentNativeTraits == null) return;
        SongInfo next = QueuedSong(songQueue.Next);
        if (next == null || !next.Bpm.HasValue) return;
        double bpm = sharedBpm ?? currentNativeTraits.Bpm, phase = currentNativeTraits.BeatOffset / originalTempo;
        double target = AutoTempo.InRange(bpm, next.Bpm.Value, autoSettings) ?? next.Bpm.Value;
        var timing = new AutoTransitionTiming(bpm, target, mixer.WaveFormat.SampleRate, autoSettings, incomingBpm: next.Bpm.Value);
        AutoLoopTiming loop = mixer.ReplayAnalysis == null ? AutoReplayAnalysis.Fallback(bpm, phase, timing.Rate, timing.SourceEndFrame(mixer.TotalFrames), mixer.PositionFrames, timing.SourceStart(mixer.Duration, phase))
            : mixer.ReplayAnalysis.Plan(bpm, phase, timing.Rate, timing.SourceEndFrame(mixer.TotalFrames), mixer.PositionFrames, timing.SourceStart(mixer.Duration, phase));
        playback.LoopStartFrame = null;
        if (autoIncomingMixer == mixer) playback.SetSongRestartLoop(mixer, loop);
        else playback.SetOutroLoop(loop);
    }

    private void UpdateAutoRestart()
    {
        RefreshAutoRestart(true);
    }
    private void RefreshAutoRestart(bool waitForAudio)
    {
        PreparedAutoTransition plan = autoTransition;
        if (playback == null || plan == null) return;
        int index = songQueue.Items.FindIndex(entry => entry.Id == plan.QueueEntryId);
        SongInfo successor = index < 0 ? null : QueuedSong(songQueue.Items.Skip(index + 1).FirstOrDefault());
        bool guard = autoPlayEnabled && successor != null && successor.HasStems && successor.Bpm.HasValue && successor.Bpm >= 30 && successor.Bpm <= 300;
        bool restart = false;
        double latestStart = 0;
        if (guard)
        {
            double bpm = plan.Traits.Bpm;
            double target = AutoTempo.InRange(bpm, successor.Bpm.Value, autoSettings) ?? successor.Bpm.Value;
            var following = new AutoTransitionTiming(bpm, target, plan.Timing.Rate, autoSettings, incomingBpm: successor.Bpm.Value);
            double consumed = plan.Traits.BeatOffset + plan.Timing.IncomingSeconds;
            latestStart = following.SourceStart(plan.Next.Duration, plan.Traits.BeatOffset);
            // Leave a beat for scheduling, plus the measured preparation time
            // when the successor's background load has not finished yet.
            PlaybackQueueEntry entry = songQueue.Items[index + 1];
            bool loaded = autoPreload != null && autoPreload.QueueEntryId == entry.Id && autoPreload.Loading.IsCompleted && autoPreload.Preparing.IsCompleted && autoPreload.Preparing.Result != null;
            if (loaded) latestStart = autoPreload.Preparing.Result.SourceStart;
            double lead = 60 / bpm + (loaded ? 0 : Math.Max(1, autoPreparationSeconds));
            restart = consumed + lead > latestStart;
        }
        var loopTiming = new AutoTransitionTiming(plan.Traits.Bpm, plan.Traits.Bpm, plan.Next.WaveFormat.SampleRate, autoSettings);
        var loop = AutoReplayAnalysis.Fallback(plan.Traits.Bpm, plan.Traits.BeatOffset, plan.Timing.Rate, loopTiming.SourceEndFrame(plan.Next.TotalFrames), plan.Next.PositionFrames, latestStart);
        if (guard && plan.Next.ReplayAnalysis != null)
            loop = plan.Next.ReplayAnalysis.Plan(plan.Traits.Bpm, plan.Traits.BeatOffset, plan.Timing.Rate, loopTiming.SourceEndFrame(plan.Next.TotalFrames), plan.Next.PositionFrames, latestStart, restart);
        if (waitForAudio) playback.ProtectIncoming(plan.Next, restart, guard, loop);
        else playback.TryProtectIncoming(plan.Next, restart, guard, loop);
    }
}
