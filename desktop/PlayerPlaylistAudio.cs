using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

internal sealed partial class PlayerForm
{
    private async Task<PreparedAutoSong> PreparePlaylistMix(SongInfo song, int rate, CancellationToken token, StemLoadingProgress progress = null)
    {
        progress = progress ?? new StemLoadingProgress();
        SavedMix saved = song.SavedMix;
        if (saved == null || !saved.Valid()) throw new InvalidOperationException("The playlist mix has incomplete settings.");
        SongInfo original = SavedMixSong(saved.OriginalAudio);
        double? referenceHz = tuningReferenceHz;
        var ready = new PreparedAutoSong { NativeTraits = await PlayerMatching.Analyze(project, original, token), LoudnessGain = await PlayerLoudness.Gain(project, original.Audio, token), OriginalFiles = new string[4] };
        ready.TuningHz = referenceHz;
        int nativeRate;
        using (var reader = new WaveFileReader(Path.Combine(original.Folder, "vocals.wav")))
        {
            nativeRate = reader.WaveFormat.SampleRate; ready.NativeFrames = (long)Math.Round(reader.Length / (double)reader.WaveFormat.BlockAlign * rate / nativeRate);
        }
        ready.Frames = Math.Max(1, (long)Math.Round(ready.NativeFrames / saved.OriginalTempo));
        ready.MixSources = new StemSourceState(original.Folder, ready.OriginalFiles);
        ready.Traits = ready.NativeTraits.Copy(); ready.Traits.Bpm = saved.Bpm ?? ready.NativeTraits.Bpm; ready.Traits.BeatOffset /= saved.OriginalTempo;
        int shift; saved.KeyShifts.TryGetValue(original.Audio, out shift); if (ready.Traits.Tonic >= 0) ready.Traits.Tonic = (ready.Traits.Tonic + shift + 12) % 12;
        var plan = new StemMatchPlan { Tempo = saved.OriginalTempo, Pitch = TuningReference.Pitch(original.TuningHz, referenceHz, shift), MatchedBpm = ready.Traits.Bpm };
        bool changed = Math.Abs(plan.Tempo - 1) > .0000001 || Math.Abs(plan.Pitch - 1) > .0000001 || rate != nativeRate;
        ready.Audio = changed ? await PlayerMatching.PrepareFile(project, original.Audio, plan, rate, ready.Frames, token, cacheOwner: ready.MixSources, progress: progress.AllRange(0, .05)) : await NormalizeAutoFile(project, original.Audio, rate, ready.Frames, token, ready.MixSources, progress.AllRange(0, .05));
        for (int lane = 0; lane < 4; lane++)
        {
            string source = Path.Combine(original.Folder, StemMixer.StemNames[lane] + ".wav");
            ready.OriginalFiles[lane] = changed ? await PlayerMatching.PrepareFile(project, source, plan, rate, ready.Frames, token, lane, ready.MixSources, progress.Range(lane, .05, .1)) : source;
            int index = lane;
            string file = ready.OriginalFiles[lane]; ready.MixSources.OriginalPeaks[lane] = await Task.Run(() => PlayerWaveforms.Read(file, token, progress.Range(index, .1, .15)));
        }
        var banks = new Dictionary<string, StemSourceBank>(StringComparer.OrdinalIgnoreCase);
        int bankIndex = 0;
        foreach (SavedStemBank settings in saved.Banks)
        {
            SongInfo donor = SavedMixSong(settings.Audio);
            var bank = new StemSourceBank { Song = donor, Plan = settings.Plan(), Traits = await PlayerMatching.Analyze(project, donor, token), LoudnessGain = await PlayerLoudness.Gain(project, donor.Audio, token), Files = new string[4], Peaks = new float[4][] };
            saved.KeyShifts.TryGetValue(donor.Audio, out shift); bank.Plan.Pitch = TuningReference.Pitch(donor.TuningHz, referenceHz, shift);
            for (int lane = 0; lane < 4; lane++)
            {
                double start = .15 + .1 * bankIndex / saved.Banks.Count, end = .15 + .1 * (bankIndex + 1) / saved.Banks.Count;
                bank.Files[lane] = await PlayerMatching.Prepare(project, donor, lane, bank.Plan, rate, ready.Frames, token, bank, progress.Range(lane, start, (start + end) / 2));
                int index = lane;
                string file = bank.Files[lane]; bank.Peaks[lane] = await Task.Run(() => PlayerWaveforms.Read(file, token, progress.Range(index, (start + end) / 2, end)));
            }
            banks.Add(settings.Audio, bank);
            bankIndex++;
        }
        ready.MixSources.Available = String.IsNullOrEmpty(saved.AddedAudio) ? null : banks[saved.AddedAudio];
        for (int lane = 0; lane < 4; lane++)
        {
            ready.MixSources.Active[lane] = String.IsNullOrEmpty(saved.Sources[lane]) ? null : banks[saved.Sources[lane]];
            // Auto play resets offsets and loops, including on incoming mixes.
            ready.Files[lane] = await NormalizeAutoFile(project, ready.MixSources.File(lane), rate, ready.Frames, token, ready, progress.Range(lane, .25, .28));
            int index = lane;
            string file = ready.Files[lane]; ready.Peaks[lane] = await Task.Run(() => PlayerWaveforms.Read(file, token, progress.Range(index, .28, .3)));
        }
        return ready;
    }
    private static PlayerMix SavedPlaybackMix(SavedMix saved)
    {
        var result = new PlayerMix(); Array.Copy(saved.Muted, result.Muted, 4); Array.Copy(saved.Solo, result.Solo, 4); Array.Copy(saved.Levels, result.Levels, 4); result.Preset = "custom"; return result;
    }
    private void PromotePlaylistMix(PreparedAutoTransition plan)
    {
        SavedMix saved = plan.Song.SavedMix; PreparedAutoSong ready = plan.PlaylistMix;
        loadedMixId = saved.Id; stemSources = ready.MixSources; sharedBpm = saved.Bpm; originalTempo = saved.OriginalTempo;
        songTitle.Text = saved.OriginalTitle; tip.SetToolTip(songTitle, saved.OriginalTitle);
        currentNativeTraits = ready.NativeTraits; nativeFrames = ready.NativeFrames; originalPlaybackFiles = ready.OriginalFiles; originalPlaybackAudio = ready.Audio;
        keyShifts.Clear(); foreach (var pair in saved.KeyShifts) keyShifts[pair.Key] = pair.Value;
        Array.Copy(saved.Muted, mix.Muted, 4); Array.Copy(saved.Solo, mix.Solo, 4); Array.Copy(saved.Levels, mix.Levels, 4); mix.Preset = "custom";
        readingPreferences = true;
        try { volume.Value = saved.Volume; for (int lane = 0; lane < 4; lane++) faders[lane].Value = (int)Math.Round(saved.Levels[lane] * 100); }
        finally { readingPreferences = false; }
        mixer.FollowLiveMix = true; mixer.Volume = saved.Volume / 100f; mixer.ApplyMix(mix);
        for (int lane = 0; lane < 4; lane++) ShowSource(lane);
        StemSourceBank added = stemSources.Available; ShowAddedSong(added == null ? null : added.Song, added == null ? null : added.Traits, added == null ? null : added.Plan);
    }
}
