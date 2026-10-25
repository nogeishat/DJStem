using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.Wave;

internal static class PlayerAutoPlayTests
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Get(object source, string field) { return source.GetType().GetField(field, Fields).GetValue(source); }
    private static void Set(object source, string field, object value) { source.GetType().GetField(field, Fields).SetValue(source, value); }
    private static object Invoke(object source, string method, params object[] arguments) { return source.GetType().GetMethod(method, Fields).Invoke(source, arguments); }
    private static void Finish(Task task)
    {
        Stopwatch clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.ElapsedMilliseconds < 45000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Auto play preparation must finish while the UI remains responsive"); task.GetAwaiter().GetResult();
    }
    private static void Wave(string file, int rate, double seconds, double frequency, double pulseBpm = 0, double beatOffset = 0)
    {
        using (WaveFileWriter writer = new WaveFileWriter(file, new WaveFormat(rate, 24, 2)))
        for (int frame = 0; frame < seconds * rate; frame++)
        {
            double time = frame / (double)rate;
            float sample = pulseBpm > 0 ? (time >= beatOffset && (time - beatOffset) % (60 / pulseBpm) < .009 ? .7f : 0) : (float)(.12 * Math.Sin(2 * Math.PI * frequency * time)); writer.WriteSample(sample); writer.WriteSample(sample);
        }
    }
    private static SongInfo Song(string root, string name, double bpm, double frequency = 440)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder); string audio = Path.Combine(folder, "song.wav"); Wave(audio, 16000, 12, frequency);
        for (int lane = 0; lane < 4; lane++) Wave(Path.Combine(folder, StemMixer.StemNames[lane] + ".wav"), 16000, 12, frequency + lane * 20);
        File.WriteAllText(Path.Combine(folder, "metadata.json"), new JavaScriptSerializer().Serialize(new { bpm = bpm, key = "A minor", analysis = new { version = 2, source = "mp3", beatOffset = 0, beatConfidence = .9, keyConfidence = .9 } }));
        return new SongInfo { Audio = audio, Folder = folder, Cover = Path.Combine(folder, "missing.jpg"), HasStems = true, Bpm = bpm, Artist = "Artist", Title = name };
    }
    private static void DynamicWave(string file, Func<double, double> gain, double frequency = 440)
    {
        using (WaveFileWriter writer = new WaveFileWriter(file, new WaveFormat(16000, 24, 2)))
        for (int frame = 0; frame < 192000; frame++)
        {
            double time = frame / 16000.0; float sample = (float)(.12 * gain(time) * Math.Sin(2 * Math.PI * frequency * time));
            writer.WriteSample(sample); writer.WriteSample(sample);
        }
    }
    private static void Dynamics(string root)
    {
        string file = Path.Combine(root, "dynamics.wav");
        DynamicWave(file, time => 1);
        Check(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) == 8, "Steady audible audio must keep its planned transition");
        DynamicWave(file, time => time >= 4 ? 0 : 1);
        Check(Math.Abs(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) - 4) < .11, "A sustained quiet ending must switch at its onset");
        DynamicWave(file, time => time >= 4 ? .015 : 1);
        Check(Math.Abs(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) - 4) < .11, "A nearly quiet stem must transition even with a nonzero residual tail");
        DynamicWave(file, time => time >= 4 && time < 4.8 ? 0 : 1);
        Check(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) == 8, "A brief rest with resumed audio must not advance a stem");
        DynamicWave(file, time => time >= 4 && time < 7 ? 0 : 1);
        Check(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) == 8, "A breakdown that recovers before the planned swap must retain the schedule");
        DynamicWave(file, time => time < 4 ? 1 : Math.Max(0, 1 - (time - 4) / 8));
        double fade = AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 9, 120);
        Check(fade >= 3.9 && fade <= 4.5, "A gradual fade must switch near its start while audio is still audible");
        DynamicWave(file, time => time < 4 ? 1 : time < 6 ? 1 - (time - 4) / 2 : 1);
        Check(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) == 8, "A temporary falling passage must not be mistaken for an ending fade");
        Wave(file, 16000, 12, 0, 120);
        Check(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) == 8, "Spaces between drum hits must not advance a steady rhythmic stem");
        DynamicWave(file, time => 0);
        Check(AutoAudioDynamics.Read(file, CancellationToken.None).EarlySeconds(2, 8, 120) == 2, "An absent stem may switch as soon as tempo matching is complete");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool canceled = false;
            try { AutoAudioDynamics.Read(file, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Dynamics analysis must honor preparation cancellation");
        }
    }
    private static void EarlyTransitions(string root)
    {
        root = Path.Combine(root, "early-transition-tests"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Early ending", 120), next = Song(root, "Early incoming", 132, 660);
        DynamicWave(Path.Combine(original.Folder, "vocals.wav"), time => time >= 3 ? 0 : 1);
        foreach (bool enabled in new[] { false, true })
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var settings = new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = 1, EarlyStemTransitions = enabled };
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next }); Set(form, "autoSettings", settings); Set(form, "autoPlayEnabled", true);
            PlayerMix mix = (PlayerMix)Get(form, "mix"); mix.Solo[0] = true; mix.Muted[1] = true; mix.Levels[0] = .6f; native.ApplyMix(mix);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null, "Both enabled and disabled early-transition settings must prepare successfully: " + ((Label)Get(form, "message")).Text);
                var normal = new AutoTransitionTiming(120, 132, 16000, settings); double normalStart = normal.SourceStart(native.Duration, 0);
                if (!enabled)
                {
                    Check(plan.SourceStart == normalStart && Enumerable.Range(0, 4).All(position => plan.SwapFrames[normal.Order[position]] == normal.SwapFrame(position)), "Turning the setting off must preserve every original transition frame despite quiet stems");
                    Invoke(form, "StopAutoTimeline"); continue;
                }
                Check(plan.SourceStart < normalStart && plan.SwapFrames[0] < plan.Timing.SwapFrame(3) && plan.SwapFrames[0] >= plan.Timing.RampFrames, "A stem ending before the normal ramp must advance preparation and switch after tempo matching");
                Check(plan.SwapFrames.Distinct().Count() == 4 && plan.SwapFrames.All(frame => frame >= plan.Timing.RampFrames), "Peak-aware timing must keep muted/unsoloed lanes staggered after tempo matching");
                long start = (long)Math.Round(plan.SourceStart * 16000); var buffer = new float[4096];
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                long target = plan.SwapFrames[0] + 1600;
                while (plan.Bridge.PositionFrames < target) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (target - plan.Bridge.PositionFrames) * 2));
                Invoke(form, "PollAutoTransition");
                StemSourceState state = (StemSourceState)Get(form, "stemSources");
                Check(state.Active[0] == plan.Bank && state.Active[1] == null, "The UI must show an early vocal switch before the originally first drum switch");
                float[] incoming = new float[6400]; playback.Read(incoming, 0, incoming.Length);
                Check(Math.Abs(Frequency(incoming, 16000, .025, .15) - 660) < 8, "Early transition frames must replace the actual audio samples with the incoming stem");
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Invoke(form, "PollAutoTransition");
                Check(Get(form, "current") == next && plan.Next.Seconds >= plan.Traits.BeatOffset + plan.Timing.IncomingSeconds && plan.Next.Seconds < plan.Traits.BeatOffset + plan.Timing.IncomingSeconds + .3, "An early switch must hand off at the incoming song's continuous position");
                Invoke(form, "SavePreferences");
                var saved = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root, "data", "player-settings.json")));
                Check(AutoPlaySettings.Read((Dictionary<string, object>)saved["autoPlaySettings"]).EarlyStemTransitions, "The enabled early-transition setting must survive saving and loading preferences");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
        // A whole-song fade starts before the normal first swap, so all lanes
        // should become eligible near the fade, including the initially first one.
        DynamicWave(original.Audio, time => time < 3 ? 1 : Math.Max(0, 1 - (time - 3) / 9));
        for (int lane = 0; lane < 4; lane++) DynamicWave(Path.Combine(original.Folder, StemMixer.StemNames[lane] + ".wav"), time => time < 3 ? 1 : Math.Max(0, 1 - (time - 3) / 9));
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            var settings = new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = 1, EarlyStemTransitions = true };
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next }); Set(form, "autoSettings", settings); Set(form, "autoPlayEnabled", true);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.SourceStart == 0 && plan.SwapFrames.Distinct().Count() == 4 && plan.SwapFrames.All(frame => frame >= plan.Timing.RampFrames && frame < plan.Timing.TotalFrames), "A whole-song fade must move the ramp earlier while retaining separate safe stem joins");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void PeakBoundaries(string root)
    {
        string outgoingFile = Path.Combine(root, "phrase-out.wav"), incomingFile = Path.Combine(root, "phrase-in.wav");
        DynamicWave(outgoingFile, time => time >= 4 && time < 6 ? 0 : 1);
        DynamicWave(incomingFile, time => time >= 6 && time < 8 ? 0 : 1);
        AutoAudioDynamics outgoing = AutoAudioDynamics.Read(outgoingFile, CancellationToken.None), incoming = AutoAudioDynamics.Read(incomingFile, CancellationToken.None);
        var timing = new AutoTransitionTiming(120, 120, 16000, new AutoPlaySettings { RampBars = 0, StemBars = .5 }); timing.SetTotalFrames(160000);
        var changes = AutoStemTransitions.Plan(Enumerable.Repeat(outgoing, 4).ToArray(), Enumerable.Repeat(incoming, 4).ToArray(), timing, CancellationToken.None);
        Check(changes.CutFrames[0] >= 4 * 16000 && changes.CutFrames[0] < 6 * 16000, "Outgoing vocals must cut in a phrase pause rather than inside the loud verse");
        Check(changes.StartFrames[0] >= 6 * 16000 && changes.StartFrames[0] < 8 * 16000 && changes.StartFrames[0] > changes.CutFrames[0], "Incoming vocals must wait for their own phrase pause, allowing a gap after the outgoing verse");
        Check(outgoing.BoundaryCost(3, 0, 120) > outgoing.BoundaryCost(3, 1, 120) && outgoing.BoundaryCost(3, 1, 120) > outgoing.BoundaryCost(3, 2, 120), "Peak avoidance must be strictest for vocals, less strict for drums/other, and least strict for bass");
        DynamicWave(outgoingFile, time => time >= 4 && time < 4.2 ? 0 : time >= 6 && time < 8 ? 0 : 1);
        outgoing = AutoAudioDynamics.Read(outgoingFile, CancellationToken.None);
        Check(outgoing.BoundaryCost(4.1, 0, 120) >= 500 && outgoing.BoundaryCost(6.5, 0, 120) < 1, "Brief word gaps must not be accepted as vocal phrase boundaries");
        Check(incoming.BoundaryCost(8, 0, 120, true) < 1 && incoming.BoundaryCost(8.5, 0, 120, true) > 100, "Incoming vocals may join at a verse onset after a quiet lead-in, but not midway through that verse");
        DynamicWave(outgoingFile, time => 0); outgoing = AutoAudioDynamics.Read(outgoingFile, CancellationToken.None);
        var collision = new AutoTransitionTiming(120, 120, 16000, new AutoPlaySettings { RampBars = 0, StemBars = .25, Order = new[] { 2, 3, 1, 0 } });
        changes = AutoStemTransitions.Plan(Enumerable.Repeat(outgoing, 4).ToArray(), Enumerable.Repeat(outgoing, 4).ToArray(), collision, CancellationToken.None);
        Check(changes.StartFrames[0] == 0 && changes.StartFrames[3] < changes.StartFrames[2] && changes.StartFrames[1] < changes.StartFrames[2] && changes.StartFrames.Distinct().Count() == 4, "Vocals must retain the best boundary ahead of drums/other and bass when every stem wants the same frame");
        for (int lane = 0; lane < 4; lane++) for (int other = lane + 1; other < 4; other++)
            Check(new[] { changes.CutFrames[lane], changes.StartFrames[lane] }.All(frame => frame != changes.CutFrames[other] && frame != changes.StartFrames[other]), "No two lanes may start or cut at the exact same frame");
        var fast = new AutoTransitionTiming(300, 300, 16000, new AutoPlaySettings { RampBars = 0, StemBars = .25 });
        changes = AutoStemTransitions.Plan(Enumerable.Repeat(outgoing, 4).ToArray(), Enumerable.Repeat(outgoing, 4).ToArray(), fast, CancellationToken.None);
        Check(changes.StartFrames.Distinct().Count() == 4 && changes.StartFrames.All(frame => frame < fast.TotalFrames), "Even the shortest allowed bridge must stagger all four stems without overflowing");
    }
    private static void PhraseTransitions(string root)
    {
        root = Path.Combine(root, "phrase-transitions"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Outgoing verse", 120), next = Song(root, "Incoming verse", 120, 660);
        DynamicWave(Path.Combine(original.Folder, "vocals.wav"), time => time >= 11 ? 0 : 1);
        DynamicWave(Path.Combine(next.Folder, "vocals.wav"), time => time >= 5 && time < 7 ? 0 : 1, 660);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            var settings = new AutoPlaySettings { RampBars = 0, StemBars = .5, EarlyStemTransitions = true };
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next }); Set(form, "autoSettings", settings); Set(form, "autoPlayEnabled", true);
            var mix = (PlayerMix)Get(form, "mix"); mix.Solo[0] = true; native.ApplyMix(mix);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.CutFrames[0] < plan.SwapFrames[0], "The real prepared bridge must schedule separate outgoing and incoming vocal boundaries");
                Check(plan.SwapFrames[0] >= 5 * 16000 && plan.SwapFrames[0] < 7 * 16000 && plan.Timing.TotalFrames > new AutoTransitionTiming(120, 120, 16000, settings).TotalFrames, "The bridge must extend when the next vocal phrase's quiet lead-in is later than the normal handoff");
                var samples = new float[4096]; long start = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(samples, 0, (int)Math.Min(samples.Length, (start - native.PositionFrames) * 2));
                playback.Read(samples, 0, 2); Invoke(form, "PollAutoTransition");
                long gap = plan.CutFrames[0] + 19200;
                while (plan.Bridge.PositionFrames < gap) playback.Read(samples, 0, (int)Math.Min(samples.Length, (gap - plan.Bridge.PositionFrames) * 2));
                playback.Read(samples, 0, samples.Length); Invoke(form, "PollAutoTransition");
                Check(samples.All(value => Math.Abs(value) < .000001), "Outgoing vocals must stay silent during the gap even if their rendered outro loop starts singing again");
                Check(plan.Silenced[0] && !plan.Switched[0] && ((WaveformView[])Get(form, "waveforms"))[0].Peaks.All(value => value == 0) && ((Label)Get(form, "message")).Text.Contains("vocal phrase"), "The UI must show the silent vocal gap while waiting for the incoming phrase");
                while (playback.Current == plan.Bridge) playback.Read(samples, 0, samples.Length);
                Invoke(form, "PollAutoTransition");
                Check(Get(form, "current") == next && plan.Next.Seconds >= plan.Traits.BeatOffset + plan.Timing.IncomingSeconds, "The final handoff must continue at the incoming song's position after the extended bridge");
                while (plan.Next.Seconds < 7.1) playback.Read(samples, 0, samples.Length);
                var verse = new float[6400]; playback.Read(verse, 0, verse.Length);
                Check(Math.Abs(Frequency(verse, 16000, .025, .15) - 660) < 8, "The next vocal verse must begin audibly after its quiet lead-in without restarting the song");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void EquivalentTempos()
    {
        var settings = new AutoPlaySettings();
        double[][] matches = { new[] { 156.0, 80, 160 }, new[] { 80.0, 156, 78 }, new[] { 156.0, 40, 160 }, new[] { 39.0, 156, 39 }, new[] { 300.0, 37.5, 300 }, new[] { 37.5, 300, 37.5 }, new[] { 120.0, 30, 120 }, new[] { 156.0, 73, 146 }, new[] { 156.0, 83, 166 } };
        foreach (double[] match in matches)
        {
            Check(AutoTempo.InRange(match[0], match[1], settings) == match[2], "Autoplay must include half/double, quarter/quadruple, further powers of two, and both BPM range boundaries");
            var current = new SongInfo { Audio = "current", HasStems = true, Bpm = match[0] };
            var next = new SongInfo { Audio = "next", HasStems = true, Bpm = match[1] };
            var shuffle = new AutoShuffle();
            Check(shuffle.Choose(new[] { current, next }, current, match[0], settings) == next, "Equivalent-tempo songs must be selectable for the autoplay queue");
            shuffle.Played(next);
            Check(shuffle.Choose(new[] { current, next }, current, match[0], settings) == null, "Equivalent-tempo matches must still respect session repeat prevention");
        }
        var asymmetric = new AutoPlaySettings { Below = 2, Above = 3 };
        Check(AutoTempo.InRange(156, 77, asymmetric) == 154 && AutoTempo.InRange(156, 79.5, asymmetric) == 159, "Asymmetric limits must apply to the equivalent BPM, including decimal endpoints");
        Check(!AutoTempo.InRange(156, 76.9, asymmetric).HasValue && !AutoTempo.InRange(156, 79.6, asymmetric).HasValue, "Equivalent BPM just outside either limit must remain excluded");
        Check(AutoTempo.InRange(120, 30, new AutoPlaySettings { Below = 0, Above = 0 }) == 120, "An exact-match range must accept a power-of-two equivalent");
        Check(AutoTempo.InRange(100, 75, new AutoPlaySettings { Below = 1, Above = 60 }) == 150, "Choose an equivalent inside the range even when a closer equivalent is outside it");
        Check(AutoTempo.InRange(100, 150, new AutoPlaySettings { Below = 100, Above = 100 }) == 75, "When multiple equivalents fit, prefer the smallest proportional speed change");
        var reference = new SongInfo { Audio = "reference", HasStems = true, Bpm = 156 };
        var invalid = new[] { new SongInfo { Audio = "slow", HasStems = true, Bpm = 72.9 }, new SongInfo { Audio = "fast", HasStems = true, Bpm = 83.1 }, new SongInfo { Audio = "unknown", HasStems = true }, new SongInfo { Audio = "unfinished", Bpm = 80 }, new SongInfo { Audio = "nan", HasStems = true, Bpm = Double.NaN }, new SongInfo { Audio = "infinite", HasStems = true, Bpm = Double.PositiveInfinity }, new SongInfo { Audio = "invalid-bpm", HasStems = true, Bpm = 15 } };
        Check(new AutoShuffle().Choose(invalid.Concat(new[] { reference }), reference, 156, settings) == null, "Unknown/invalid BPM, unfinished stems, current songs, and out-of-range equivalents must be excluded");
    }
    private static void EquivalentTransitions(string root)
    {
        root = Path.Combine(root, "equivalent-tempos"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Equivalent original", 156), next = Song(root, "Half time", 80, 660), second = Song(root, "Quarter time", 42, 880);
        Wave(Path.Combine(original.Folder, "drums.wav"), 16000, 12, 0, 156);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 156, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, second }); Set(form, "autoSettings", new AutoPlaySettings { RampBars = 4, StemBars = .25 }); Set(form, "autoPlayEnabled", true);
            try
            {
                // Prepare directly to exercise the analyzed-BPM check as well
                // as the normal queue lookahead.
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var plan = (PreparedAutoTransition)Get(form, "autoTransition"); var queue = (PlaybackQueue)Get(form, "songQueue");
                Check(plan != null && plan.Song == next && plan.Timing.FromBpm == 156 && plan.Timing.ToBpm == 160 && plan.Traits.Bpm == 80, "An 80 BPM shuffle suggestion must prepare a 156 to 160 BPM ramp, retaining native incoming BPM");
                Check(queue.Items.Select(entry => entry.Audio).SequenceEqual(new[] { original.Audio, next.Audio, second.Audio }), "The second buffer suggestion must use its predecessor's equivalent-tempo range");
                Check(plan.Timing.IncomingBpm == 80 && plan.Timing.IntervalSeconds == .75, "Stem spacing must count bars at the incoming song's native BPM");
                var onsets = Onsets(Samples(plan.OutgoingFiles[1]), 16000);
                Check(onsets.Count > 8 && Math.Abs(onsets.Last() - onsets[onsets.Count - 2] - 60.0 / 160) < .025, "The rendered outgoing beat must reach 160 BPM rather than slowing to 80 BPM");
                Check(Math.Abs(Frequency(Samples(plan.NextFiles[1]), 16000, 1, 1) - 680) < 3, "Incoming audio must retain its native speed and pitch");
                long start = (long)Math.Round(plan.SourceStart * 16000); var buffer = new float[4096];
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                Check(playback.Current == plan.Bridge && ((GlassButton)Get(form, "addedBpm")).Text.StartsWith("80"), "The transition must show the incoming song's actual BPM while the outgoing song ramps at its equivalent tempo");
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Invoke(form, "PollAutoTransition");
                Check(Get(form, "current") == next && ((MusicTraits)Get(form, "currentNativeTraits")).Bpm == 80 && Get(form, "sharedBpm") == null, "After the equivalent-tempo handoff, playback must continue at the incoming song's native BPM");
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var following = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(following != null && following.Song == second && following.Timing.FromBpm == 80 && following.Timing.ToBpm == 84 && following.Traits.Bpm == 42, "A buffered equivalent-tempo song must also prepare correctly after the first handoff");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
        root = Path.Combine(root, "reverse"); Directory.CreateDirectory(root);
        original = Song(root, "Reverse original", 80); next = Song(root, "Double time", 156);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 80, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next }); Set(form, "autoSettings", new AutoPlaySettings { RampBars = 1, StemBars = .25 }); Set(form, "autoPlayEnabled", true);
            try
            {
                Invoke(form, "FillAutoQueue"); Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Song == next && plan.Timing.FromBpm == 80 && plan.Timing.ToBpm == 78 && plan.Timing.IncomingBpm == 156, "An already buffered 156 BPM song must prepare an 80 to 78 BPM ramp using half time");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void QueueBuffer(string root)
    {
        root = Path.Combine(root, "queue-buffer"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Buffer original", 120), next = Song(root, "Buffer next", 132), second = Song(root, "Buffer second", 140), third = Song(root, "Buffer third", 148), manual = Song(root, "Buffer manual", 180), afterManual = Song(root, "Buffer after manual", 188);
        var future = new List<SongInfo> { next, second, third };
        for (int index = 3; index < 12; index++) future.Add(Song(root, "Buffer future " + (index + 1), 148 + (index - 2) * 8));
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120 });
            Set(form, "songs", new[] { original }.Concat(future).ToList()); Set(form, "autoSettings", new AutoPlaySettings { Below = 0, Above = 15, RampBars = 1, StemBars = .25 }); Set(form, "autoPlayEnabled", true);
            var queue = (PlaybackQueue)Get(form, "songQueue");
            try
            {
                Invoke(form, "FillAutoQueue");
                Check(queue.Items.Select(entry => entry.Audio).SequenceEqual(new[] { original.Audio }.Concat(future.Take(10).Select(song => song.Audio))), "Autoplay must plan exactly ten upcoming songs, matching each song to its predecessor's BPM range");
                Invoke(form, "ShowPlaybackQueue", true);
                var rows = ((QueueList)Get(form, "queueList")).Items.Cast<QueueRow>().ToArray();
                Check(rows.Length == 11 && rows.Skip(1).All(row => row.Song != null && row.Entry.Automatic && !row.Played) && Get(form, "autoPreload") == null && Get(form, "autoTransition") == null, "The queue must display the current song and ten distinct planned songs without starting their audio preparation");
                string[] prefix = queue.Items.Take(10).Select(entry => entry.Id).ToArray();
                string removed = queue.Items[10].Id; queue.Remove(removed); Invoke(form, "FillAutoQueue");
                Check(queue.Items.Count == 11 && queue.Items.Take(10).Select(entry => entry.Id).SequenceEqual(prefix) && queue.Items[10].Audio == future[9].Audio && queue.Items[10].Id != removed && !queue.HasPlayed(future[9].Audio), "Refilling a removed upcoming song must preserve every earlier planned row and leave it unplayed");
                Set(form, "current", next); Set(form, "currentNativeTraits", new MusicTraits { Bpm = 132 }); queue.Select(queue.Items[1].Id); Invoke(form, "FillAutoQueue");
                Check(queue.Items.Count == 12 && queue.Items.Count - queue.CurrentIndex - 1 == 10 && queue.Next.Audio == second.Audio && queue.Items.Last().Audio == future[10].Audio && queue.Items.Skip(2).All(entry => !queue.HasPlayed(entry.Audio)), "Advancing the queue must restore ten upcoming songs while retaining history, planned order, and the immediate next song");
                Set(form, "current", original); Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120 }); queue.Select(queue.Items[0].Id);
                string[] ids = queue.Items.Select(entry => entry.Id).ToArray(); Invoke(form, "FillAutoQueue"); Invoke(form, "FillAutoQueue");
                Check(queue.Items.Select(entry => entry.Id).SequenceEqual(ids) && queue.Next.Audio == next.Audio, "Replaying history with enough existing songs must preserve queue order and must not add more shuffle choices");
                Set(form, "songs", new List<SongInfo> { original, manual, afterManual });
                queue.Clear(); queue.Follow(original.Audio); PlaybackQueueEntry chosen = queue.Add(manual.Audio, 1); Invoke(form, "FillAutoQueue");
                Check(queue.Next == chosen && !chosen.Automatic && queue.Items.Count == 3 && queue.Items[2].Audio == afterManual.Audio, "A manually queued next song must stay first, and the second suggestion must match that song rather than the current one");
                foreach (PlaybackQueueEntry entry in queue.Items) queue.Select(entry.Id);
                Set(form, "current", afterManual); Set(form, "currentNativeTraits", new MusicTraits { Bpm = 188 }); Invoke(form, "FillAutoQueue");
                Check(queue.Items.Count == 3 && queue.Next == null, "An exhausted eligible pool must leave fewer than ten future songs without repeating played entries");
                Invoke(form, "SetAutoPlay", false); Invoke(form, "FillAutoQueue");
                Check(queue.Items.Count == 0 && Get(form, "autoPreload") == null && native.PositionFrames == 0, "Turning autoplay off must leave its session empty without changing the active audio position");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void Preloading(string root)
    {
        root = Path.Combine(root, "preloading"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Preload original", 120), next = Song(root, "Preload incoming", 132, 660), later = Song(root, "Preload later", 170, 880), moved = Song(root, "Preload moved", 180, 990);
        Wave(later.Audio, 44100, 12, 880);
        for (int lane = 0; lane < 4; lane++) Wave(Path.Combine(later.Folder, StemMixer.StemNames[lane] + ".wav"), 22050, 12, 880 + lane * 20);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, later, moved }); Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = .25 }); Set(form, "autoPlayEnabled", true);
            var queue = (PlaybackQueue)Get(form, "songQueue");
            Invoke(form, "FollowSessionSong", original);
            var incomingEntry = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", next, queue.Items.Count, false);
            var laterEntry = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", later, queue.Items.Count, false);
            var movedEntry = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", moved, queue.Items.Count, false);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                var preload = (AutoSongPreload)Get(form, "autoPreload");
                Check(plan != null && plan.QueueEntryId == incomingEntry.Id && preload != null && preload.QueueEntryId == laterEntry.Id && playback.Current == native, "The song after the incoming track must start loading before a queued transition begins, even outside the shuffle BPM range");
                Finish(preload.Preparing); PreparedAutoSong ready = preload.Loading.Result;
                Check(ready != null && ready.Frames == 192000 && ready.Traits.Bpm == 170 && ready.Files.Length == 4 && ready.Peaks.All(peaks => peaks != null && peaks.Length == 1600), "Preloading must prepare analysis, the full incoming audio, all four stems, and waveforms");
                using (var reader = new WaveFileReader(ready.Audio)) Check(reader.WaveFormat.SampleRate == 16000 && reader.Length / reader.WaveFormat.BlockAlign == 192000, "Preloaded audio must use the active output rate and the stem timeline");
                Check(!queue.HasPlayed(later.Audio) && queue.Items.Count == 4 && Get(form, "current") == original, "Preloading must not advance the queue, mark the song played, or add extra shuffle songs before the transition");
                Invoke(form, "ShowPlaybackQueue", true);
                Check(((QueueList)Get(form, "queueList")).Items.Cast<QueueRow>().Any(row => row.Entry != null && row.Entry.Id == laterEntry.Id && row.Status == "Ready ahead"), "The queue must show when a future song has loaded ahead");
                float[] buffer = new float[4096]; long start = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                long position = plan.Bridge.PositionFrames;
                Invoke(form, "MoveQueuedSong", movedEntry.Id, queue.CurrentIndex + 1);
                var replacement = (AutoSongPreload)Get(form, "autoPreload");
                Check(replacement != null && replacement != preload && replacement.QueueEntryId == movedEntry.Id && preload.Cancellation.IsCancellationRequested && Get(form, "autoTransition") == plan && plan.Bridge.PositionFrames == position, "Reordering the successor during a transition must cancel and retarget preload without moving the active audio");
                Finish(replacement.Loading);
                File.WriteAllText(later.Audio, "Invalid speculative audio");
                Invoke(form, "RemoveQueuedSong", movedEntry.Id);
                preload = (AutoSongPreload)Get(form, "autoPreload");
                Check(!queue.HasPlayed(moved.Audio) && (queue.CanAdd(moved.Audio) || queue.Items.Any(entry => entry.Audio == moved.Audio)) && preload != null && preload.QueueEntryId == laterEntry.Id, "Removing an unplayed preloaded song must leave it eligible for buffer refill and warm the new immediate successor");
                Finish(preload.Loading);
                Check(preload.Loading.Result == null && queue.Items.Any(entry => entry.Id == laterEntry.Id) && !queue.HasPlayed(later.Audio) && Get(form, "autoTransition") == plan && plan.Bridge.PositionFrames == position, "A failed background load must keep the queued song unplayed and leave the active transition untouched");
                Wave(later.Audio, 44100, 12, 880); Invoke(form, "UpdateAutoPreload"); preload = (AutoSongPreload)Get(form, "autoPreload");
                Finish(preload.Loading); ready = preload.Loading.Result;
                Check(ready != null, "Restoring changed audio must safely retry a failed preload");
                string identity = preload.Identity;
                File.SetLastWriteTimeUtc(Path.Combine(later.Folder, "metadata.json"), DateTime.UtcNow.AddSeconds(2));
                Invoke(form, "UpdateAutoPreload"); preload = (AutoSongPreload)Get(form, "autoPreload");
                Check(preload.Identity != identity, "Changing a queued song's metadata must invalidate its preload");
                Finish(preload.Loading); ready = preload.Loading.Result;
                var loading = new TaskCompletionSource<PreparedAutoSong>(); preload.Loading = loading.Task; Set(form, "autoPreloadTask", loading.Task);
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Invoke(form, "PollAutoTransition");
                Task preparation = (Task)Invoke(form, "PrepareAutoTransition");
                Check(!preparation.IsCompleted && (bool)Get(form, "preparingAutoPlay") && Get(form, "current") == next && playback.Current == plan.Next, "The next handoff must reuse an in-flight preload while the current song keeps its audio timeline");
                loading.SetResult(ready); Finish(preparation);
                var following = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(following != null && following.Song == later && following.NextAudio == ready.Audio && following.NextFiles.SequenceEqual(ready.Files) && Object.ReferenceEquals(following.NextPeaks[0], ready.Peaks[0]), "The following transition must consume preloaded audio and waveforms instead of loading the song again");
                Invoke(form, "CancelAutoPreparation");
                Check(Get(form, "autoPreload") == null && Get(form, "autoTransition") == null && playback.Current == plan.Next, "Canceling a pending transition must clear its speculative preload and preserve current playback");
            }
            finally { Invoke(form, "StopAutoTimeline"); if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask")); if (Get(form, "autoPreloadPreparationTask") != null) Finish((Task)Get(form, "autoPreloadPreparationTask")); Invoke(form, "CloseAudio"); }
        }
        root = Path.Combine(root, "shuffle"); Directory.CreateDirectory(root);
        original = Song(root, "Shuffle preload original", 120); next = Song(root, "Shuffle preload incoming", 132); later = Song(root, "Shuffle preload successor", 140);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, later }); Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = .25 }); Set(form, "autoPlayEnabled", true);
            var queue = (PlaybackQueue)Get(form, "songQueue");
            try
            {
                Set(form, "autoPlayEnabled", false); Invoke(form, "SetAutoPlay", true);
                Check(queue.Items.Count == 3 && queue.Items[1].Audio == next.Audio && queue.Items[2].Audio == later.Audio && queue.Items.Skip(1).All(entry => entry.Automatic && !queue.HasPlayed(entry.Audio)), "Enabling autoplay must plan every eligible song when fewer than ten are available, matching each suggestion to its predecessor's BPM range");
                Check(Get(form, "autoPreload") == null && Get(form, "autoTransition") == null && playback.Current == native, "Queueing two songs must not start the second load ahead of preparing the immediate next song");
                Set(form, "autoSettings", new AutoPlaySettings { Above = 5, RampBars = 1, StemBars = .25 });
                var pending = new TaskCompletionSource<PreparedAutoSong>(); Set(form, "autoPreloadTask", pending.Task);
                Task preparation = (Task)Invoke(form, "PrepareAutoTransition"); Invoke(form, "UpdateAutoPreload");
                Check(!preparation.IsCompleted && (bool)Get(form, "preparingAutoPlay") && Get(form, "autoPreload") == null, "Second-song loading must remain deferred while foreground preparation waits for its audio resources");
                pending.SetResult(null); Finish(preparation); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                var preload = (AutoSongPreload)Get(form, "autoPreload");
                Check(plan.Song == next && queue.Items.Count == 3 && preload != null && preload.QueueEntryId == queue.Items[2].Id && playback.Current == native, "A buffered next song must retain its queue priority even after narrowing the BPM range, with its successor already loading");
                var buffer = new float[4096]; long start = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                Check(Get(form, "autoPreload") == preload && queue.Items.Count == 3 && queue.Next.Audio == later.Audio && queue.Next.Automatic && !queue.HasPlayed(later.Audio), "Starting a transition must preserve the already queued and loading successor without marking it played");
                Invoke(form, "UpdateAutoPreload"); Invoke(form, "UpdateAutoPreload");
                Check(Get(form, "autoPreload") == preload && queue.Items.Count == 3 && queue.Items.Select(entry => entry.Audio).Distinct().Count() == 3, "Repeated autoplay ticks must keep one preload without duplicate or played songs");
                Finish(preload.Loading);
                Invoke(form, "SetAutoPlay", false);
                Check(Get(form, "autoPreload") == null && preload.Cancellation.IsCancellationRequested && queue.Items.Count == 0 && playback.Current == plan.Bridge && Get(form, "autoTransition") == plan, "Turning autoplay off during a transition must cancel loading ahead and clear the session while allowing the active handoff to finish");
            }
            finally { Invoke(form, "StopAutoTimeline"); if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask")); if (Get(form, "autoPreloadPreparationTask") != null) Finish((Task)Get(form, "autoPreloadPreparationTask")); Invoke(form, "CloseAudio"); }
        }
    }
    private static void SequentialPreloading(string root)
    {
        root = Path.Combine(root, "sequential-preload"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Original", 120), next = Song(root, "Incoming", 132), second = Song(root, "Second", 140), moved = Song(root, "Moved", 148);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, second, moved }); Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = .25, EarlyStemTransitions = true }); Set(form, "autoPlayEnabled", true);
            var queue = (PlaybackQueue)Get(form, "songQueue"); Invoke(form, "FollowSessionSong", original);
            var incoming = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", next, queue.Items.Count, false);
            var successor = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", second, queue.Items.Count, false);
            var replacement = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", moved, queue.Items.Count, false);
            Task preparation = null; bool gateHeld = false;
            try
            {
                PlayerMatchedCache.RenderGate.Wait(); gateHeld = true;
                preparation = (Task)Invoke(form, "PrepareAutoTransition");
                Stopwatch clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 250) { Application.DoEvents(); Thread.Sleep(5); }
                Invoke(form, "UpdateAutoPreload");
                Check(!preparation.IsCompleted && Get(form, "autoTransition") == null && Get(form, "autoPreload") == null,
                    "The second queued song must wait until the immediate transition is completely prepared");
                Invoke(form, "UpdateStemLoadingButtons");
                var loadingButtons = (GlassButton[])Get(form, "sourceButtons");
                Check(loadingButtons.All(button => button.LoadingProgress >= 0 && button.LoadingProgress < 1), "All four immediate-song buttons must show loading while Auto play waits for a render slot");
                Invoke(form, "MoveQueuedSong", replacement.Id, 2);
                Check(Get(form, "autoPreload") == null && !preparation.IsCompleted,
                    "Reordering a future song must preserve the foreground preparation's priority");
                PlayerMatchedCache.RenderGate.Release(); gateHeld = false; Finish(preparation);
                var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                var preload = (AutoSongPreload)Get(form, "autoPreload");
                Check(plan != null && plan.QueueEntryId == incoming.Id && preload != null && preload.QueueEntryId == replacement.Id,
                    "Finishing the immediate handoff must immediately prepare the second queued song in its latest order: " + ((Label)Get(form, "message")).Text);
                Invoke(form, "UpdateStemLoadingButtons");
                Check(loadingButtons.All(button => button.LoadingProgress == -1), "Preparing the second next song must keep the stem buttons in their default style");
                Invoke(form, "RemoveQueuedSong", replacement.Id);
                var retargeted = (AutoSongPreload)Get(form, "autoPreload");
                Check(retargeted != null && retargeted.QueueEntryId == successor.Id && preload.Cancellation.IsCancellationRequested && Get(form, "autoTransition") == plan,
                    "Removing the preparing successor must retarget full preparation without replacing the immediate handoff");
                preload = retargeted; Finish(preload.Preparing);
                PreparedAutoTransition ready = preload.Preparing.Result;
                Invoke(form, "UpdateStemLoadingButtons");
                Check(Enumerable.Range(0, 4).All(lane => preload.Progress.Read(lane) == 1 && loadingButtons[lane].LoadingProgress == -1), "Completing the prepared successor must finish measured progress and restore every default stem button");
                Check(ready != null && ready.Reference == plan.Next && ready.Bridge != null && ready.Next != null && ready.Bank != null && ready.OutgoingFiles.All(File.Exists) && ready.IncomingFiles.All(File.Exists) && ready.NextFiles.All(File.Exists) && ready.OutgoingPeaks.All(peaks => peaks != null) && ready.IncomingPeaks.All(peaks => peaks != null) && ready.NextPeaks.All(peaks => peaks != null) && ready.CutFrames != null,
                    "Preparation ahead must finish the ramp, stem timing, waveforms, dynamics, and both playback mixers");
                Check(playback.Current == native && native.PositionFrames == 0 && ready.Bridge.PositionFrames == 0 && !queue.HasPlayed(second.Audio),
                    "Fully preparing ahead must not advance playback or mark a future song played");
                Invoke(form, "ShowPlaybackQueue", true);
                Check(((QueueList)Get(form, "queueList")).Items.Cast<QueueRow>().Any(row => row.Entry.Id == successor.Id && row.Status == "Ready ahead"),
                    "The queue must show a future song as ready only after its complete transition is prepared");
                var buffer = new float[4096]; long start = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Invoke(form, "PollAutoTransition");
                var mix = (PlayerMix)Get(form, "mix"); mix.Solo[2] = true;
                PlayerMatchedCache.RenderGate.Wait(); gateHeld = true;
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                Check(Get(form, "autoTransition") == ready && preload.Consumed && ready.NextPeaks[0] == preload.Loading.Result.Peaks[0] && playback.Current == plan.Next,
                    "The next handoff must adopt the complete prepared transition without rendering or opening another mixer");
                Check(((bool[])Get(ready.Bridge, "solo")).SequenceEqual(mix.Solo) && ((bool[])Get(ready.Next, "solo")).SequenceEqual(mix.Solo), "Adopting a prepared handoff must apply the latest live stem mix");
                Invoke(form, "SetAutoPlay", false);
                Check(Get(form, "autoTransition") == null && Get(form, "autoPreload") == null && playback.Current == plan.Next,
                    "Turning Auto play off must release the prepared future handoffs and preserve current playback");
                PlayerMatchedCache.RenderGate.Release(); gateHeld = false;
                Set(form, "autoPlayEnabled", true); Invoke(form, "FollowSessionSong", next);
                Invoke(form, "AddQueuedSong", second, queue.Items.Count, false); Invoke(form, "AddQueuedSong", moved, queue.Items.Count, false);
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                preload = (AutoSongPreload)Get(form, "autoPreload"); Finish(preload.Preparing);
                ((AutoPlaySettings)Get(form, "autoSettings")).StemBars = .5;
                Invoke(form, "UpdateAutoPreload"); retargeted = (AutoSongPreload)Get(form, "autoPreload");
                Check(retargeted != preload && preload.Cancellation.IsCancellationRequested && retargeted.SettingsIdentity != preload.SettingsIdentity,
                    "Changing Auto play settings must invalidate a fully prepared successor");
                Finish(retargeted.Preparing);
                PlayerMatchedCache.RenderGate.Wait(); gateHeld = true;
                ((AutoPlaySettings)Get(form, "autoSettings")).RampBars = 2;
                Invoke(form, "UpdateAutoPreload");
                preload = (AutoSongPreload)Get(form, "autoPreload");
                Invoke(form, "SetAutoPlay", false);
                Check(Get(form, "autoPreload") == null && preload.Cancellation.IsCancellationRequested,
                    "Turning Auto play off must cancel full preparation ahead even while rendering waits for resources");
            }
            finally
            {
                Invoke(form, "StopAutoTimeline");
                if (gateHeld) PlayerMatchedCache.RenderGate.Release();
                if (preparation != null && !preparation.IsCompleted) Finish(preparation);
                if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask"));
                if (Get(form, "autoPreloadPreparationTask") != null) Finish((Task)Get(form, "autoPreloadPreparationTask"));
                Invoke(form, "CloseAudio");
            }
        }
    }

    private static void ReadUntilRestart(PlayerPlayback playback, float[] buffer)
    {
        int restarts = playback.RestartCount;
        long budget = playback.Current.TotalFrames * 2 + buffer.Length;
        bool continuous = true;
        while (playback.RestartCount == restarts && budget > 0) { continuous &= playback.Read(buffer, 0, buffer.Length) == buffer.Length; budget -= buffer.Length; }
        Check(continuous && playback.RestartCount == restarts + 1, "A protected replay must reach its musical boundary without EOF or a silent device restart");
    }
    private sealed class BlockingDisposeReader : WaveStream
    {
        private readonly WaveStream source;
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
        internal BlockingDisposeReader(WaveStream reader) { source = reader; }
        public override WaveFormat WaveFormat { get { return source.WaveFormat; } }
        public override long Length { get { return source.Length; } }
        public override long Position { get { return source.Position; } set { source.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) { return source.Read(buffer, offset, count); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Entered.Set(); Release.WaitOne(); source.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private static void Handoff(string root)
    {
        string audio = Path.Combine(root, "handoff.wav"); Wave(audio, 16000, 3, 660);
        using (var playback = new PlayerPlayback(new StemMixer(audio, "")))
        {
            StemMixer old = playback.Current, bridge = new StemMixer(audio, ""), next = new StemMixer(audio, "");
            WaveStream[] readers = (WaveStream[])Get(old, "readers");
            var blocked = new BlockingDisposeReader(readers[0]); readers[0] = blocked;
            Check(playback.Schedule(old, old.TotalFrames, bridge, next), "The cleanup regression must schedule the complete handoff");
            var samples = new float[1024];
            while (playback.Current != next) playback.Read(samples, 0, samples.Length);
            Task cleanup = Task.Run(() => playback.ReleaseRetired()); Task<int> read = null; bool continued;
            try
            {
                Check(blocked.Entered.WaitOne(5000), "Old-reader cleanup must enter the simulated slow decoder shutdown");
                read = Task.Run(() => playback.Read(samples, 0, samples.Length));
                continued = read.Wait(1000);
            }
            finally { blocked.Release.Set(); cleanup.GetAwaiter().GetResult(); if (read != null) read.GetAwaiter().GetResult(); }
            Check(continued && read.Result == samples.Length && samples.Any(sample => Math.Abs(sample) > .01), "Closing old decoders after a handoff must never block the next audio callback");
        }
        foreach (int rate in new[] { 16000, 44100 })
        foreach (double phase in new[] { 0.0, .123456 })
        foreach (bool changeMix in new[] { false, true })
        {
            string tone = Path.Combine(root, "handoff-tone-" + rate + ".wav"); Wave(tone, rate, 12, 660);
            var timing = new AutoTransitionTiming(120, 132, rate, new AutoPlaySettings { RampBars = 1, StemBars = .25 });
            string incoming = PlayerMatching.PrepareFiltered(root, tone, timing.IncomingFilter(phase), rate, timing.TotalFrames, CancellationToken.None).GetAwaiter().GetResult();
            var bridgeFiles = Enumerable.Repeat(incoming, 4).ToArray(); var nextFiles = Enumerable.Repeat(tone, 4).ToArray();
            var bridge = new StemMixer(incoming, "", bridgeFiles); var next = new StemMixer(tone, "", nextFiles);
            bridge.SetLoudness(4); next.SetLoudness(4); bridge.Volume = next.Volume = .83f;
            for (int lane = 0; lane < 4; lane++) bridge.ScheduleStemTransition(lane, timing.SwapFrame(lane), timing.SwapFrame(lane), incoming, 4);
            long handoff = timing.IncomingEndFrame(phase); next.SeekFrame(handoff);
            Check(handoff == (long)Math.Round(phase * rate) + timing.TotalFrames - timing.RampFrames, "The handoff frame must use the same rounded beat phase as the rendered incoming stems");
            using (var playback = new PlayerPlayback(new StemMixer(tone, "")))
            using (var continuous = new StemMixer(tone, "", nextFiles))
            {
                Check(playback.Schedule(playback.Current, 0, bridge, next), "The sample-continuity regression must schedule the bridge and full song");
                var scratch = new float[2054]; const int tail = 128;
                do { playback.Read(scratch, 0, (int)Math.Min(scratch.Length, (timing.TotalFrames - tail - bridge.PositionFrames) * 2)); }
                while (bridge.PositionFrames < timing.TotalFrames - tail);
                continuous.SetLoudness(4); continuous.Volume = .83f; continuous.SeekFrame(handoff - tail);
                Set(continuous, "peakGain", Get(bridge, "peakGain"));
                if (changeMix)
                {
                    var mix = new PlayerMix(); mix.Solo[3] = true; mix.Levels[3] = .35f;
                    playback.Apply(.83f, mix); continuous.ApplyMix(mix);
                }
                var expected = new float[1800]; var actual = new float[1808];
                Check(continuous.Read(expected, 0, expected.Length) == expected.Length && playback.Read(actual, 4, expected.Length) == expected.Length, "A final handoff inside an uneven callback must fill every requested sample");
                Check(playback.Current == next && next.PositionFrames == handoff + expected.Length / 2 - tail, "The full song must resume at the exact next incoming frame without skipping or repeating audio");
                float error = expected.Select((sample, index) => Math.Abs(sample - actual[index + 4])).Max();
                Check(error < .00001, "The final handoff must preserve incoming samples and peak-protection state at " + rate + " Hz (error " + error + ")");
            }
        }
        string shortBridge = Path.Combine(root, "handoff-short.wav"); Wave(shortBridge, 16000, .005, 660);
        using (var playback = new PlayerPlayback(new StemMixer(audio, "")))
        {
            Check(playback.Schedule(playback.Current, 0, new StemMixer(shortBridge, ""), new StemMixer(audio, "")), "A very short bridge must still schedule normally");
            var samples = new float[64]; playback.Read(samples, 0, samples.Length); playback.ReleaseRetired();
            bool continuous = true;
            for (int part = 0; part < 12; part++) { continuous &= playback.Read(samples, 0, samples.Length) == samples.Length; playback.ReleaseRetired(); }
            Check(continuous, "Retired cleanup must preserve a reader that is still supplying the initial crossfade through a short bridge");
            Check(((List<StemMixer>)Get(playback, "retired")).Count == 0, "Cleanup must release all retired readers after their crossfade completes");
        }
        using (var playback = new PlayerPlayback(new StemMixer(audio, "")))
        {
            playback.Schedule(playback.Current, 0, new StemMixer(shortBridge, ""), new StemMixer(audio, ""));
            var samples = new float[64]; playback.Read(samples, 0, samples.Length); playback.Dispose();
            Check(playback.Read(samples, 0, samples.Length) == 0, "Closing during a handoff must stop all subsequent callbacks");
            using (var exclusive = new FileStream(audio, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(exclusive.Length > 0, "Closing during a crossfade must release the old, queued and current song's file handles");
        }
    }
    private static void SkippingTransitions(string root)
    {
        const int rate = 16000; const double phase = .123456;
        var timing = new AutoTransitionTiming(120, 132, rate, new AutoPlaySettings { RampBars = 1, StemBars = .25 });
        string outgoing = Path.Combine(root, "skip-outgoing.wav"), incoming = Path.Combine(root, "skip-incoming.wav");
        Wave(outgoing, rate, timing.TotalFrames / (double)rate, 330); Wave(incoming, rate, 5, 880);
        foreach (long at in new[] { 1L, timing.RampFrames - 1, timing.RampFrames + rate / 4, timing.TotalFrames - 1 })
        using (var playback = new PlayerPlayback(new StemMixer(outgoing, "", Enumerable.Repeat(outgoing, 4).ToArray())))
        {
            var bridge = new StemMixer(outgoing, "", Enumerable.Repeat(outgoing, 4).ToArray()); var next = new StemMixer(incoming, "", Enumerable.Repeat(incoming, 4).ToArray());
            next.SeekFrame(timing.IncomingEndFrame(phase));
            playback.Schedule(playback.Current, 0, bridge, next);
            var samples = new float[4096];
            int reads = 0;
            do { playback.Read(samples, 0, (int)Math.Min(samples.Length, (at - bridge.PositionFrames) * 2)); } while (bridge.PositionFrames < at && reads++ < 100);
            Check(playback.Current == bridge && bridge.PositionFrames == at, "The skip fixture must reach the selected transition sample");
            Func<long, long> position = frame => (long)Math.Round(phase * rate) + timing.IncomingSourceFrame(frame);
            long expected = position(bridge.PositionFrames);
            Check(playback.SkipTransition(bridge, next, position) && playback.Current == next && next.PositionFrames == expected,
                "Skipping before, during or after the tempo ramp must continue the incoming song at its exact mapped sample");
            playback.ReleaseRetired();
            Check(playback.Read(samples, 0, samples.Length) == samples.Length && ToneAmplitude(samples.Skip(rate / 25 * 2).ToArray(), rate, 880) > .08 && ToneAmplitude(samples.Skip(rate / 25 * 2).ToArray(), rate, 330) < .005,
                "A skipped transition must play only the incoming song after the short crossfade, without an audio gap");
            expected = next.PositionFrames;
            Check(playback.SkipTransition(bridge, next, position) && next.PositionFrames == expected,
                "A skip racing with a completed handoff must not seek the incoming song again or advance another song");
            playback.ReleaseRetired();
            Check(((List<StemMixer>)Get(playback, "retired")).Count == 0, "Skipping must release outgoing readers after their crossfade finishes");
        }
    }
    private static void ReplayTo(PlayerPlayback playback, long frame)
    {
        var buffer = new float[2054]; int reads = 0; bool continuous = true;
        while (playback.Current.PositionFrames < frame && reads++ < 2000)
        {
            int count = (int)Math.Min(buffer.Length, (frame - playback.Current.PositionFrames) * 2);
            int read = playback.Read(buffer, 0, count); continuous &= read == count; if (read == 0) break;
        }
        Check(continuous && playback.Current.PositionFrames == frame, "A replay must fill audio through the selected point without an unexpected wrap");
    }
    private static void SmoothReplays(string root)
    {
        string folder = Path.Combine(root, "smooth-replays"); Directory.CreateDirectory(folder);
        const int rate = 16000;
        string audio = Path.Combine(folder, "song.wav"); Wave(audio, rate, 32, 330);
        string[] files = StemMixer.StemNames.Select(name => Path.Combine(folder, name + ".wav")).ToArray();
        double[] notes = { 110.0, 164.81, 146.83, 130.81 };
        for (int lane = 0; lane < 4; lane++)
        using (var writer = new WaveFileWriter(files[lane], new WaveFormat(rate, 24, 2)))
        for (int frame = 0; frame < 32 * rate; frame++)
        {
            double at = frame / (double)rate, phrase = at % 4;
            // Half-second and one-second phrase rests, plus a word-sized dip
            // that must never be selected as a verse boundary.
            double voice = (phrase >= .5 && phrase < 3 && !(phrase >= 1.5 && phrase < 1.6) ? .12 : .006) * Math.Sin(2 * Math.PI * 660 * at);
            double note = notes[(int)(at / 2) % 4];
            float sample = (float)(lane == 0 ? voice : lane == 1 ? (at % .5 < .009 ? .7 : 0) : .08 * Math.Sin(2 * Math.PI * note * (lane == 2 ? 1 : 2) * at));
            writer.WriteSample(sample); writer.WriteSample(sample);
        }
        AutoReplayAnalysis analysis = AutoReplayAnalysis.Read(files, null, CancellationToken.None);
        AutoLoopTiming loop = analysis.Plan(120, 0, rate, 30 * rate, (long)(14.2 * rate), 24);
        Check(loop.SmoothReplay && loop.EndFrame <= 24 * rate && loop.StartFrame > 0 && loop.StartFrame < 22 * rate, "Replay selection must find an interior instrumental join before the calculated transition, leaving time to reach it again");
        Check((loop.EndFrame - loop.StartFrame) % (8 * rate) == 0, "Drum-led joins may precede a hit while preserving matching bar phase and repeated harmonic sections: " + loop.StartFrame + " to " + loop.EndFrame);
        Check(loop.VocalOutFrame / (double)rate % 4 >= 3 && loop.VocalOutFrame < loop.EndFrame && Math.Abs((loop.VocalInFrame / (double)rate + .05) % 4 - .5) < .001, "Vocals must leave after a phrase and enter at the next phrase, skipping short word gaps");
        using (var playback = new PlayerPlayback(new StemMixer(audio, folder)))
        {
            StemMixer current = playback.Current; current.ReplayAnalysis = analysis; current.Seek(14.2);
            var mix = new PlayerMix(); mix.Levels[0] = .6f; mix.Muted[2] = true; current.ApplyMix(mix);
            playback.SetSongRestartLoop(current, loop);
            ReplayTo(playback, (long)(14.5 * rate));
            var audible = new float[6400]; playback.Read(audible, 0, audible.Length);
            Check(ToneAmplitude(audible, rate, 660) > .04, "The replay guard must retain the current vocal phrase before its ending");
            ReplayTo(playback, loop.EndFrame); playback.Read(new float[2], 0, 2);
            Check(playback.RestartCount == 1 && current.PositionFrames == loop.StartFrame + 1, "A musical replay must wrap at its exact chosen sample within the current output");
            ReplayTo(playback, loop.StartFrame + rate / 10); playback.Read(audible, 0, audible.Length);
            Check(ToneAmplitude(audible, rate, 660) < .001 && audible.Any(sample => Math.Abs(sample) > .02), "Vocals must stay silent over the loop join while instruments continue");
            ReplayTo(playback, loop.VocalInFrame + rate / 10); playback.Read(audible, 0, audible.Length);
            Check(ToneAmplitude(audible, rate, 660) > .04, "The next complete vocal phrase must fade back to the user's level");
            Check(((float[])Get(current, "levels"))[0] == .6f && !((bool[])Get(current, "enabled"))[2], "Automatic vocal gating must preserve user faders and mute settings");
            var muted = new PlayerMix(); muted.Muted[0] = true; playback.Apply(.7f, muted);
            playback.Read(audible, 0, audible.Length); Check(ToneAmplitude(audible, rate, 660) < .001, "Automatic vocal entry must never override a user vocal mute");
            playback.CancelPending();
            Check(playback.LoopStartFrame == null && !current.ReplayVocalsCommitted, "Ending autoplay must release both the replay and its automatic vocal gate");
        }
        using (var playback = new PlayerPlayback(new StemMixer(audio, folder)))
        {
            StemMixer current = playback.Current; current.Seek(14.2); playback.SetSongRestartLoop(current, loop);
            ReplayTo(playback, loop.VocalOutFrame + rate / 10);
            var bridge = new StemMixer(audio, folder); var next = new StemMixer(audio, folder);
            Check(playback.Schedule(current, 24 * rate, bridge, next, true) && playback.WaitingForLoop, "If Next becomes ready after the vocal exit, finish the musical loop before scheduling the transition");
            ReplayTo(playback, loop.EndFrame); playback.Read(new float[2], 0, 2);
            Check(!playback.WaitingForLoop && current.PositionFrames == loop.StartFrame + 1, "A committed replay must return to the selected section before the prepared handoff");
            ReplayTo(playback, loop.VocalInFrame + rate / 10);
            var audible = new float[6400]; playback.Read(audible, 0, audible.Length);
            Check(ToneAmplitude(audible, rate, 660) > .075, "Completing a pending replay must preserve the delayed vocal re-entry even after removing its repeat");
            ReplayTo(playback, 24 * rate); playback.Read(new float[2], 0, 2);
            Check(playback.Current == bridge && playback.RestartCount == 1, "After the replay, the ready transition must begin at its calculated start exactly once");
        }
        using (var playback = new PlayerPlayback(new StemMixer(audio, folder)))
        {
            StemMixer current = playback.Current; current.Seek(14.2); playback.SetSongRestartLoop(current, loop);
            ReplayTo(playback, loop.VocalOutFrame + rate / 10);
            var bridge = new StemMixer(audio, folder); bridge.SeekFrame(loop.StartFrame);
            var next = new StemMixer(audio, folder);
            Check(playback.Schedule(current, loop.StartFrame, bridge, next, true), "An interior replay must allow a short-song bridge to start at its return point");
            ReplayTo(playback, loop.EndFrame); playback.Read(new float[2], 0, 2);
            Check(playback.Current == bridge && playback.RestartCount == 1 && bridge.PositionFrames == loop.StartFrame + 1, "A short-song replay must enter its rebased bridge without another jump to the beginning");
            ReplayTo(playback, loop.StartFrame + rate / 10);
            var audible = new float[6400]; playback.Read(audible, 0, audible.Length);
            Check(ToneAmplitude(audible, rate, 660) < .001, "An immediate bridge must inherit the vocal mute across the drum splice");
            ReplayTo(playback, loop.VocalInFrame + rate / 10); playback.Read(audible, 0, audible.Length);
            Check(ToneAmplitude(audible, rate, 660) > .075, "The inherited vocal mute must still end on its planned phrase entry inside the bridge");
        }
        var renders = new List<float[]>();
        foreach (int size in new[] { 126, 2054 })
        using (var playback = new PlayerPlayback(new StemMixer(audio, folder)))
        {
            playback.Current.Seek(22); playback.SetSongRestartLoop(playback.Current, loop);
            var rendered = new float[rate * 10]; int written = 0; bool filled = true;
            while (written < rendered.Length)
            {
                int count = Math.Min(size, rendered.Length - written);
                filled &= playback.Read(rendered, written, count) == count; written += count;
            }
            Check(filled && playback.RestartCount == 1, "Uneven device callbacks must remain filled across a phrase-aware replay");
            renders.Add(rendered);
        }
        Check(renders[0].Select((value, index) => Math.Abs(value - renders[1][index])).Max() < .000001, "Loop joins and vocal fades must sound identical across device callback sizes");
        using (var playback = new PlayerPlayback(new StemMixer(audio, folder)))
        {
            playback.Current.Seek(14.2); playback.SetSongRestartLoop(playback.Current, loop);
            ReplayTo(playback, loop.VocalOutFrame + rate / 10); playback.CancelPending();
            ReplayTo(playback, loop.EndFrame + (long)(.6 * rate));
            var audible = new float[6400]; playback.Read(audible, 0, audible.Length);
            Check(playback.RestartCount == 0 && ToneAmplitude(audible, rate, 660) > .075, "Turning autoplay off during its vocal gap must restore normal vocals and let the song continue");
        }
        AutoLoopTiming late = analysis.Plan(120, 0, rate, 30 * rate, (long)(25.1 * rate), 24, true);
        Check(late.EndFrame > 25.1 * rate && late.EndFrame <= 30 * rate && late.StartFrame < 24 * rate, "Late preparation must choose a future musical exit and return before the missed transition, respecting the end cut");
        AutoLoopTiming shortLoop = analysis.Plan(120, 0, rate, (long)(1.8 * rate), 0, 0);
        Check(shortLoop.EndFrame > shortLoop.StartFrame && shortLoop.EndFrame <= 1.8 * rate, "Very short excerpts must retain a usable beat-aligned fallback");
        var transform = new PlaybackTransform(new StemMatchPlan { Tempo = 2, Pitch = 1.1, Offset = .1 }, rate, (long)(16.1 * rate));
        AutoReplayAnalysis transformed = AutoReplayAnalysis.Read(files, Enumerable.Repeat(transform, 4).ToArray(), CancellationToken.None);
        AutoLoopTiming faster = transformed.Plan(240, .1, rate, (long)(16.1 * rate), (long)(7.2 * rate), 12.1);
        Check(faster.SmoothReplay && faster.StartFrame > .1 * rate && faster.EndFrame <= 12.1 * rate && (faster.EndFrame - faster.StartFrame) % (rate / 4) == 0, "Drum-led replay analysis must follow the live tempo and alignment offset while preserving the beat spacing");
        var token = new CancellationToken(true); bool canceled = false;
        try { AutoReplayAnalysis.Read(files, null, token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Canceling autoplay must cancel replay analysis as well as next-song preparation");
        foreach (bool continuousDrums in new[] { false, true })
        {
            using (var writer = new WaveFileWriter(files[1], new WaveFormat(rate, 24, 2)))
            for (int frame = 0; frame < 32 * rate; frame++)
            {
                double at = frame / (double)rate;
                double pulse = at >= .27 && (at - .27) % .5 < .02 ? .7 : 0;
                float sample = (float)(pulse + (continuousDrums ? .2 * Math.Sin(2 * Math.PI * 1000 * at) : 0));
                writer.WriteSample(sample); writer.WriteSample(sample);
            }
            AutoReplayAnalysis drums = AutoReplayAnalysis.Read(files, null, CancellationToken.None);
            AutoLoopTiming guided = drums.Plan(120, 0, rate, 30 * rate, (long)(14.2 * rate), 24);
            double exitLead = (.27 - guided.EndFrame / (double)rate) % .5; if (exitLead < 0) exitLead += .5;
            double entryLead = (.27 - guided.StartFrame / (double)rate) % .5; if (entryLead < 0) entryLead += .5;
            Check(guided.StartFrame >= 2 * rate && guided.EndFrame <= 24 * rate && guided.VocalOutFrame < guided.EndFrame && guided.VocalInFrame > guided.StartFrame,
                "Drum-guided replays must exclude the beginning and retain the vocal exit and entry for " + (continuousDrums ? "a continuous drum bed" : "quiet drums"));
            Check(exitLead >= .009 && exitLead <= .031 && entryLead >= .009 && entryLead <= .031,
                "Replay departure and return must both precede an actual drum hit, including off-grid drums: " + exitLead + " / " + entryLead);
            if (!continuousDrums)
            using (var playback = new PlayerPlayback(new StemMixer(audio, folder)))
            {
                var drumMix = new PlayerMix(); drumMix.Solo[1] = true; playback.Current.ApplyMix(drumMix);
                playback.Current.SeekFrame(guided.EndFrame - rate * 3 / 4); playback.SetSongRestartLoop(playback.Current, guided);
                var rendered = new float[rate * 6]; int written = 0; bool filled = true;
                while (written < rendered.Length) { int count = Math.Min(2054, rendered.Length - written); filled &= playback.Read(rendered, written, count) == count; written += count; }
                List<double> hits = Onsets(rendered, rate);
                Check(filled && playback.RestartCount == 1 && hits.Count >= 5 && hits.Skip(1).Select((at, index) => Math.Abs(at - hits[index] - .5)).All(error => error < .012),
                    "A drum-led splice must preserve actual kick spacing without clipping or doubling hits across the join");
            }
        }
        AutoLoopTiming emergency = AutoReplayAnalysis.Fallback(120, 0, rate, 8 * rate, (long)(6.7 * rate), 0);
        AutoLoopTiming tail = analysis.Plan(120, 0, rate, 30 * rate, (long)(29.98 * rate), 24, true);
        Check(emergency.SmoothReplay && emergency.StartFrame > 0 && tail.SmoothReplay && tail.StartFrame > 0 && tail.EndFrame <= 30 * rate,
            "Missing analysis and late-tail fallback paths must also retain vocal gating and never restart from the beginning");
    }
    private static void MusicalLoops(string root)
    {
        const int rate = 16000; const double bpm = 123, phase = .13;
        string audio = Path.Combine(root, "loop-pulses.wav"); Wave(audio, rate, 5.37, 0, bpm, phase);
        var loop = new AutoLoopTiming(bpm, phase, rate, (long)(5.37 * rate));
        Check(loop.StartFrame == 2080 && Math.Abs(loop.EndFrame / (double)rate - phase - 480 / bpm) < 1.0 / rate, "Full-song replays must exclude the partial intro and tail and contain whole bars at a noninteger beat length");
        foreach (bool outro in new[] { false, true })
        using (var playback = new PlayerPlayback(new StemMixer(audio, "")))
        {
            var region = new AutoLoopTiming(bpm, phase, rate, playback.Current.TotalFrames, outro);
            playback.Current.SeekFrame(region.StartFrame);
            if (outro) playback.SetOutroLoop(region.StartFrame, region.EndFrame);
            else playback.SetSongRestartLoop(playback.Current, region);
            float[] samples = new float[(region.EndFrame - region.StartFrame) * 6]; int written = 0; bool continuous = true;
            // Deliberately cross each boundary inside an uneven audio callback.
            while (written < samples.Length) { int part = Math.Min(2054, samples.Length - written); continuous &= playback.Read(samples, written, part) == part; written += part; }
            var beats = Onsets(samples, rate);
            Check(continuous && beats.Count >= (outro ? 12 : 24), "Both ending loops and full replays must fill callbacks with audible beats through repeated wraps");
            Check(beats.Skip(1).Select((time, index) => Math.Abs(time - beats[index] - 60 / bpm)).All(error => error < .007), "Beat spacing must remain continuous across every replay join, without a shortened beat or an extra partial bar");
        }
        using (var playback = new PlayerPlayback(new StemMixer(audio, "")))
        {
            StemMixer current = playback.Current; playback.SetSongRestartLoop(current, loop); current.Seek(3.1);
            long position = current.PositionFrames, transitionStart = (long)Math.Round((phase + 240 / bpm) * rate);
            StemMixer bridge = new StemMixer(audio, ""), next = new StemMixer(audio, "");
            Check(playback.Schedule(current, transitionStart, bridge, next, true) && playback.WaitingForLoop && current.PositionFrames == position && playback.RestartCount == 0, "A prepared transition that missed its window must wait for the loop boundary instead of seeking mid-bar");
            var buffer = new float[2054]; ReadUntilRestart(playback, buffer);
            Check(playback.Current == current && current.PositionFrames < transitionStart && !playback.WaitingForLoop, "After the musical replay, the prepared transition must wait for its original bar in the next play-through");
            while (current.PositionFrames < transitionStart) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (transitionStart - current.PositionFrames) * 2));
            playback.Read(buffer, 0, 2);
            Check(playback.Current == bridge, "A late prepared transition must enter its bridge exactly at the scheduled bar after replay");
        }
        string jump = Path.Combine(root, "loop-join.wav");
        using (var writer = new WaveFileWriter(jump, new WaveFormat(rate, 24, 2)))
        for (int frame = 0; frame < 5.37 * rate; frame++) { float sample = frame < 2.5 * rate ? -.2f : .2f; writer.WriteSample(sample); writer.WriteSample(sample); }
        using (var playback = new PlayerPlayback(new StemMixer(jump, "")))
        {
            playback.SetSongRestartLoop(playback.Current, loop); playback.Current.SeekFrame(loop.EndFrame - 16);
            var samples = new float[256]; Check(playback.Read(samples, 0, samples.Length) == samples.Length, "A replay join must remain in a single filled callback");
            Check(Math.Abs(samples[30] - samples[32]) < .000001 && Math.Abs(samples[192] + .14) < .001, "The join must begin at the last outgoing sample and settle smoothly onto the replay within five milliseconds");
        }
        var cut = new AutoTransitionTiming(bpm, bpm, rate, new AutoPlaySettings { EndCutBars = 1 });
        var cutLoop = new AutoLoopTiming(bpm, phase, rate, cut.SourceEndFrame((long)(5.37 * rate)));
        Check(cutLoop.EndFrame <= cut.SourceEndFrame((long)(5.37 * rate)) && cutLoop.EndFrame < loop.EndFrame, "Musical replay boundaries must respect the configured end cut");
        var shortLoop = new AutoLoopTiming(120, .1, rate, (long)(.8 * rate));
        Check(shortLoop.StartFrame == 1600 && shortLoop.EndFrame == 9600, "A track shorter than one bar must repeat a complete beat when available");
        var tinyLoop = new AutoLoopTiming(120, .1, rate, 800);
        Check(tinyLoop.StartFrame == 0 && tinyLoop.EndFrame == 800, "An excerpt with no complete beat must retain a nonempty safe loop");
    }
    private static void ShortSongRestarts(string root)
    {
        root = Path.Combine(root, "short-restarts"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Restart original", 120), shortSong = Song(root, "Restart short", 120, 660), successor = Song(root, "Restart successor", 120, 880);
        Wave(shortSong.Audio, 16000, 8, 660);
        for (int lane = 0; lane < 4; lane++) Wave(Path.Combine(shortSong.Folder, StemMixer.StemNames[lane] + ".wav"), 16000, 8, 660 + lane * 20);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, shortSong, successor }); Set(form, "autoSettings", new AutoPlaySettings { RampBars = 0, StemBars = 1 }); Set(form, "autoPlayEnabled", true);
            var mix = (PlayerMix)Get(form, "mix"); mix.Solo[0] = true; native.ApplyMix(mix);
            var queue = (PlaybackQueue)Get(form, "songQueue"); Invoke(form, "FollowSessionSong", original);
            var incoming = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", shortSong, queue.Items.Count, false);
            var following = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", successor, queue.Items.Count, false);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                var preload = (AutoSongPreload)Get(form, "autoPreload"); Finish(preload.Preparing); PreparedAutoSong ready = preload.Loading.Result;
                Invoke(form, "UpdateAutoRestart");
                var buffer = new float[4096]; long start = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Check(playback.RestartCount == 0 && playback.Current == plan.Next && plan.Next.Seconds > 6, "A short incoming song must keep playing after handoff until a matching bar boundary");
                long replayStart = playback.LoopStartFrame.Value;
                ReadUntilRestart(playback, buffer);
                Invoke(form, "PollAutoTransition");
                Check(playback.RestartCount == 1 && playback.Current == plan.Next && replayStart > 0 && plan.Next.PositionFrames >= replayStart && plan.Next.PositionFrames < replayStart + 4800 && plan.Timing.IncomingSeconds > 6, "A short incoming song must replay from an interior point after reaching the matching boundary, even if the successor is already loaded");
                Check(Get(form, "current") == shortSong && queue.Current.Id == incoming.Id && queue.Next.Id == following.Id && queue.Items.Count == 3 && !queue.HasPlayed(successor.Audio), "A protective restart must keep the same queue row and leave the future song unplayed");
                Check(((Label)Get(form, "message")).Text.Contains("looped back within") && ((PlayerMix)Get(form, "mix")).Solo[0], "The UI must explain the interior replay while retaining the user's stem mix");
                var phrase = new float[6400]; playback.Read(phrase, 0, phrase.Length);
                Check(phrase.Max(sample => Math.Abs(sample)) < .001, "A replay must hold the vocals muted across the join rather than repeating a cut-off line");
                ReplayTo(playback, ((AutoLoopTiming)Get(playback, "currentLoop")).VocalInFrame + 1600);
                playback.Read(phrase, 0, phrase.Length);
                Check(Math.Abs(Frequency(phrase, 16000, .025, .15) - 660) < 8, "Replayed vocals must return at a logical entry through the existing output");
                var loading = new TaskCompletionSource<PreparedAutoSong>(); preload.Loading = loading.Task; Set(form, "autoPreloadTask", loading.Task);
                Task preparation = (Task)Invoke(form, "PrepareAutoTransition");
                Check(!preparation.IsCompleted && playback.LoopStartFrame > 0, "While the following song is still loading, the current song must be protected by an interior replay rather than a restart from its beginning");
                replayStart = playback.LoopStartFrame.Value;
                int loops = playback.RestartCount;
                long paused = plan.Next.PositionFrames; Thread.Sleep(20);
                Check(plan.Next.PositionFrames == paused && playback.RestartCount == loops, "Pausing sample consumption must freeze protective restarts as well as playback");
                long remaining = plan.Next.TotalFrames - plan.Next.PositionFrames;
                bool uninterrupted = true;
                while (remaining > 0) { int frames = (int)Math.Min(buffer.Length / 2, remaining); uninterrupted &= playback.Read(buffer, 0, frames * 2) == frames * 2; remaining -= frames; }
                Check(uninterrupted && playback.Read(buffer, 0, buffer.Length) == buffer.Length && playback.RestartCount == loops + 1 && plan.Next.PositionFrames >= replayStart && plan.Next.PositionFrames < replayStart + 4800, "If loading outlasts the song, replay an interior section without an EOF while retaining the vocal gap");
                loading.SetResult(ready); Finish(preparation);
                var nextPlan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(nextPlan != null && nextPlan.Song == successor && (!playback.LoopStartFrame.HasValue || playback.WaitingForLoop) && queue.Items.Count == 3, "Once the successor is ready, schedule its transition without a duplicate queue entry, keeping the musical replay if its window has passed");
                Check(nextPlan.SourceStart > 0 && !nextPlan.Requested, "A short-song transition must render from its interior replay destination rather than hiding another jump to the intro inside its bridge");
                Invoke(form, "SetAutoPlay", false);
                Check(Get(form, "autoTransition") == null && playback.LoopStartFrame == null && playback.Current == plan.Next, "Turning autoplay off must cancel a pending transition and its automatic restarts");
                plan.Next.Seek(plan.Next.Duration - .05); loops = playback.RestartCount;
                Check(playback.Read(buffer, 0, buffer.Length) < buffer.Length && playback.RestartCount == loops, "With autoplay off, a song must finish instead of restarting");
            }
            finally { Invoke(form, "StopAutoTimeline"); if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask")); if (Get(form, "autoPreloadPreparationTask") != null) Finish((Task)Get(form, "autoPreloadPreparationTask")); Invoke(form, "CloseAudio"); }
        }
        root = Path.Combine(root, "loading-budget"); Directory.CreateDirectory(root);
        original = Song(root, "Budget original", 120); shortSong = Song(root, "Budget incoming", 120); successor = Song(root, "Budget successor", 120);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, shortSong, successor }); Set(form, "autoSettings", new AutoPlaySettings { RampBars = 1, StemBars = .25 }); Set(form, "autoPlayEnabled", true);
            var queue = (PlaybackQueue)Get(form, "songQueue"); Invoke(form, "FollowSessionSong", original);
            Invoke(form, "AddQueuedSong", shortSong, queue.Items.Count, false); Invoke(form, "AddQueuedSong", successor, queue.Items.Count, false);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                var preload = (AutoSongPreload)Get(form, "autoPreload"); Finish(preload.Loading); PreparedAutoSong ready = preload.Loading.Result;
                var loading = new TaskCompletionSource<PreparedAutoSong>(); preload.Loading = loading.Task; Set(form, "autoPreloadTask", loading.Task); Set(form, "autoPreparationSeconds", 20.0);
                Invoke(form, "UpdateAutoRestart");
                var buffer = new float[4096]; long start = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Check(playback.RestartCount == 0 && plan.Next.Seconds > 1, "A long successor load must not cause an immediate mid-bar restart at handoff");
                long replayStart = playback.LoopStartFrame.Value;
                ReadUntilRestart(playback, buffer);
                Invoke(form, "PollAutoTransition");
                Check(playback.RestartCount == 1 && replayStart > 0 && plan.Next.PositionFrames >= replayStart && plan.Next.PositionFrames < replayStart + 4800, "An unfinished successor load must replay an interior section at its next matching boundary");
                Task preparation = (Task)Invoke(form, "PrepareAutoTransition");
                // A seek during loading can still put playback beyond the
                // calculated transition, even when the waiting loop is earlier.
                plan.Next.Seek(9);
                loading.SetResult(ready); Finish(preparation);
                var followingPlan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(followingPlan != null && followingPlan.Song == successor && playback.RestartCount == 1 && plan.Next.Seconds >= 9 && playback.WaitingForLoop && followingPlan.SourceStart >= 8, "If preparation misses the transition window, keep playing to the loop boundary before its next play-through");
                Check(((Label)Get(form, "message")).Text.Contains("loop boundary") && queue.Items.Count == 3, "A scheduled musical replay must be explained without adding another queue row");
                replayStart = playback.LoopStartFrame.Value;
                ReadUntilRestart(playback, buffer);
                Check(playback.RestartCount == 2 && replayStart > 0 && plan.Next.PositionFrames >= replayStart && plan.Next.PositionFrames < replayStart + 4800, "Late preparation must replay an interior section at the matching boundary before starting the prepared transition");
                Invoke(form, "SetAutoPlay", false);
                Check(playback.LoopStartFrame == null && queue.Items.Count == 0, "Ending the session after a protected handoff must release the full-song restart guard");
            }
            finally { Invoke(form, "StopAutoTimeline"); if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask")); if (Get(form, "autoPreloadPreparationTask") != null) Finish((Task)Get(form, "autoPreloadPreparationTask")); Invoke(form, "CloseAudio"); }
        }
    }
    private static float[] Samples(string file)
    {
        using (WaveFileReader reader = new WaveFileReader(file))
        {
            float[] samples = new float[reader.Length / reader.WaveFormat.BlockAlign * 2]; ISampleProvider source = StemMixer.Samples(reader); int offset = 0;
            while (offset < samples.Length) { int read = source.Read(samples, offset, samples.Length - offset); if (read == 0) break; offset += read; } return samples;
        }
    }
    private static double Frequency(float[] samples, int rate, double start, double seconds)
    {
        int begin = (int)(start * rate), end = Math.Min(samples.Length / 2, (int)((start + seconds) * rate)), crossings = 0;
        for (int frame = begin + 1; frame < end; frame++) if (samples[(frame - 1) * 2] <= 0 && samples[frame * 2] > 0) crossings++;
        return crossings * rate / (double)(end - begin);
    }
    private static double ToneAmplitude(float[] samples, int rate, double frequency)
    {
        double real = 0, imaginary = 0; int first = rate / 40, end = samples.Length / 2;
        for (int frame = first; frame < end; frame++)
        {
            double angle = 2 * Math.PI * frequency * frame / rate;
            real += samples[frame * 2] * Math.Cos(angle); imaginary += samples[frame * 2] * Math.Sin(angle);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / (end - first);
    }
    private static List<double> Onsets(float[] samples, int rate)
    {
        var starts = new List<double>();
        for (int frame = 0; frame < samples.Length / 2; frame++) if (Math.Abs(samples[frame * 2]) > .15 && (starts.Count == 0 || frame / (double)rate - starts.Last() > .15)) starts.Add(frame / (double)rate);
        return starts;
    }
    private static void Rendering(string root)
    {
        var settings = new AutoPlaySettings { RampBars = 4, StemBars = .25 };
        var timing = new AutoTransitionTiming(120, 180, 16000, settings);
        string tone = Path.Combine(root, "tone.wav"), drums = Path.Combine(root, "pulses.wav"); Wave(tone, 16000, 12, 440); Wave(drums, 16000, 12, 0, 120);
        string file = PlayerMatching.PrepareFiltered(root, tone, timing.RampFilter(root, 0, 192000, 0), 16000, timing.TotalFrames, CancellationToken.None).GetAwaiter().GetResult();
        float[] samples = Samples(file);
        Check(samples.Length == timing.TotalFrames * 2, "Tempo ramps must render to the scheduled exact frame count");
        Check(Math.Abs(Frequency(samples, 16000, .6, 1) - 440) < 3 && Math.Abs(Frequency(samples, 16000, timing.RampSeconds - 1.5, 1) - 440) < 3, "Pitch must stay constant at the beginning and end of a gradual tempo ramp");
        file = PlayerMatching.PrepareFiltered(root, drums, timing.RampFilter(root, 0, 192000, 1), 16000, timing.TotalFrames, CancellationToken.None).GetAwaiter().GetResult();
        var onsets = Onsets(Samples(file), 16000);
        var intervals = onsets.Skip(1).Select((time, index) => time - onsets[index]).ToArray();
        Check(intervals.Length > 12, "The ramp must retain the outgoing song's beat pulses");
        Check(intervals.Take(3).Average() > intervals.Skip(10).Take(3).Average() + .06, "Speeding up must gradually shorten the audible beat intervals");
        Check(Math.Abs(intervals.Last() - 60.0 / 180) < .025, "The ramp must reach the next song's real BPM before stem changes begin");
        var slow = new AutoTransitionTiming(120, 90, 16000, settings);
        file = PlayerMatching.PrepareFiltered(root, drums, slow.RampFilter(root, 0, 192000, 1), 16000, slow.TotalFrames, CancellationToken.None).GetAwaiter().GetResult();
        onsets = Onsets(Samples(file), 16000); intervals = onsets.Skip(1).Select((time, index) => time - onsets[index]).ToArray();
        Check(intervals.Skip(10).Take(3).Average() > intervals.Take(3).Average() + .07, "Slowing down must gradually lengthen the audible beat intervals");
        Check(Math.Abs(intervals.Last() - 60.0 / 90) < .025, "A slow-down must reach the selected destination BPM");
        string marker = Path.Combine(root, "cut-ending.wav");
        using (WaveFileWriter writer = new WaveFileWriter(marker, new WaveFormat(16000, 24, 2)))
        for (int frame = 0; frame < 96000; frame++)
        {
            float sample = frame < 32000 ? 0 : (float)(.15 * Math.Sin(2 * Math.PI * 800 * frame / 16000)); writer.WriteSample(sample); writer.WriteSample(sample);
        }
        var cut = new AutoTransitionTiming(120, 120, 16000, new AutoPlaySettings { RampBars = 1, StemBars = 1, EndCutBars = 2 });
        file = PlayerMatching.PrepareFiltered(root, marker, cut.RampFilter(root, cut.SourceStart(6, 0), 96000, 0), 16000, cut.TotalFrames, CancellationToken.None).GetAwaiter().GetResult();
        Check(Samples(file).All(sample => Math.Abs(sample) < .001), "An outgoing transition longer than the available excerpt must loop retained audio without playing the cut ending");
        using (var playback = new PlayerPlayback(new StemMixer(marker, "")))
        {
            playback.Current.Seek(1); playback.SetOutroLoop(16000, cut.SourceEndFrame(96000)); var buffer = new float[128000];
            Check(playback.Read(buffer, 0, buffer.Length) == buffer.Length && buffer.All(sample => Math.Abs(sample) < .001), "Late preparation must loop before the cut ending instead of reaching the discarded tail");
            playback.CancelPending(); playback.Read(buffer, 0, buffer.Length);
            Check(playback.LoopStartFrame == null && buffer.Any(sample => Math.Abs(sample) > .1), "Canceling Auto play must restore access to the full original ending");
        }
    }
    private static void Transition(string root)
    {
        SongInfo original = Song(root, "Original", 120), next = Song(root, "Next", 132, 660), fast = Song(root, "Explicit fast choice", 156, 880);
        var settings = new AutoPlaySettings { Above = 15, Below = 5, RampBars = 1, StemBars = .25, EndCutBars = 1, Order = new[] { 2, 1, 0, 3 } };
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); };
            IntPtr handle = form.Handle; var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, fast }); Set(form, "autoSettings", settings); Set(form, "autoPlayEnabled", true);
            object[] settingsArguments = { null };
            using (Form dialog = (Form)Invoke(form, "CreatePlayerSettingsDialog", settingsArguments))
            {
                Control settingsBody = dialog.Controls.Find("SettingsContent", true).Single();
                var fields = settingsBody.Controls.OfType<NumericUpDown>().OrderBy(control => control.Top).ToArray();
                GroupBox tuning = settingsBody.Controls.OfType<GroupBox>().Single(control => control.Text == "Song tuning (A4 reference)");
                var tuningChoice = tuning.Controls.OfType<RadioButton>().Single(control => control.Text == "Specific frequency");
                var frequency = tuning.Controls.OfType<NumericUpDown>().Single();
                Check(!((Func<double?>)dialog.Tag)().HasValue && !frequency.Enabled, "Song tuning must default to Original");
                tuningChoice.Checked = true; frequency.Value = 432;
                Check(frequency.Enabled && ((Func<double?>)dialog.Tag)() == 432 && Get(form, "tuningReferenceHz") == null, "The tuning setting must accept a custom A4 reference without applying canceled edits");
                Check(fields.Length == 5 && fields[0].Value == 5 && fields[1].Value == 15 && fields[2].Value == 1 && fields[3].Value == .25m && fields[4].Value == 1, "Settings must expose BPM offsets, transition durations, and the end cut in bars");
                CheckBox early = settingsBody.Controls.OfType<CheckBox>().Single();
                Check(!early.Checked && early.Text == "Transition fading or quiet stems early", "Settings must expose the optional early-transition toggle, off by default"); early.Checked = true;
                fields[0].Value = 7.5m; fields[1].Value = 20; fields[2].Value = 2.5m; fields[3].Value = 3.25m; fields[4].Value = 4.25m;
                Check(settingsBody.Controls.OfType<Label>().Any(label => label.Text.Contains((8.5).ToString("0.##") + " seconds earlier")), "The end-cut preview must convert bars using the current playback BPM");
                ListBox order = settingsBody.Controls.OfType<ListBox>().Single(); order.SelectedIndex = 0;
                Invoke(settingsBody.Controls.OfType<Button>().Single(button => button.Text == "Move down"), "OnClick", EventArgs.Empty);
                AutoPlaySettings edited = ((Func<AutoPlaySettings>)settingsArguments[0])();
                Check(edited.Below == 7.5 && edited.Above == 20 && edited.RampBars == 2.5 && edited.StemBars == 3.25 && edited.EndCutBars == 4.25 && edited.EarlyStemTransitions && edited.Order.SequenceEqual(new[] { 1, 2, 0, 3 }), "Editing the settings controls and reordering stems must produce the chosen transition settings");
                Check(((AutoPlaySettings)Get(form, "autoSettings")).Below == 5 && ((AutoPlaySettings)Get(form, "autoSettings")).EndCutBars == 1 && !((AutoPlaySettings)Get(form, "autoSettings")).EarlyStemTransitions, "Settings dialog edits must not alter live settings until saved");
                dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000); dialog.Opacity = 0; dialog.Show();
                using (var preview = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(preview, new Rectangle(Point.Empty, dialog.Size)); string path = Path.Combine(Directory.GetCurrentDirectory(), "data", "auto-play-settings-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)); preview.Save(path);
                }
                dialog.Hide();
            }
            ((PlayerMix)Get(form, "mix")).Solo[2] = true; native.ApplyMix((PlayerMix)Get(form, "mix"));
            original.TuningHz = next.TuningHz = fast.TuningHz = 440; Set(form, "tuningReferenceHz", (double?)432);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Song == next, "Auto play must prepare an eligible shuffled song");
                Check(plan.TuningHz == 432 && Math.Abs(Frequency(Samples(plan.NextFiles[2]), 16000, 1, 1) - 700 * 432.0 / 440) < 4, "Auto play must tune incoming stems before transition and handoff");
                var unchangedTiming = new AutoTransitionTiming(120, 132, 16000, new AutoPlaySettings { RampBars = 1, StemBars = .25 });
                Check(Math.Abs(plan.SourceStart - (unchangedTiming.SourceStart(native.Duration, 0) - 2)) < .00001, "The real prepared transition must begin one current-tempo bar earlier when the end cut is one bar");
                var queue = (PlaybackQueue)Get(form, "songQueue");
                Check(queue.Next.Automatic && queue.Next.Id == plan.QueueEntryId && queue.Next.Audio == next.Audio, "Queue must show the actual prepared shuffle suggestion before the transition starts");
                var explicitChoice = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", fast, queue.Items.Count, false);
                Check(Get(form, "autoTransition") == null && playback.Current == native && queue.Next == explicitChoice, "Inserting a new next song must cancel the prepared handoff and preserve the outgoing audio");
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var explicitPlan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(explicitPlan != null && explicitPlan.Song == fast && explicitPlan.Timing.ToBpm == 156, "An explicitly queued song must take priority over shuffle and its BPM range");
                Invoke(form, "RemoveQueuedSong", explicitChoice.Id);
                Check(Get(form, "autoTransition") == null && queue.Next.Audio == next.Audio && playback.Current == native, "Removing the pending next song must retain later entries and cancel its audio timelines");
                Finish((Task)Invoke(form, "PrepareAutoTransition")); plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(Invoke(form, "AddQueuedSong", original, 2, false) == null && queue.Items.Count == 2, "Played songs must not be requeued or invalidate a prepared transition");
                var promotedNext = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", next, queue.Items.Count, false);
                Check(promotedNext == queue.Next && !promotedNext.Automatic && Get(form, "autoTransition") == plan, "Manually choosing the already prepared automatic song must promote its existing row and preserve the handoff");
                var later = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", fast, 2, false);
                Check(Get(form, "autoTransition") == plan && queue.Items.Count == 3, "Appending a future song must preserve the prepared next transition");
                long start = (long)Math.Round(plan.SourceStart * 16000); var buffer = new float[4096];
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                Check(playback.Current == plan.Bridge && Get(form, "mixer") == plan.Bridge, "The same audio feed must enter the prepared tempo ramp without restarting the output device");
                Invoke(form, "ShowPlaybackQueue", true);
                Invoke(form, "RemoveQueuedSong", plan.QueueEntryId); Invoke(form, "MoveQueuedSong", plan.QueueEntryId, 0);
                Check(queue.Items.Count == 3 && queue.Current.Id == plan.QueueEntryId && ((QueueRow)((QueueList)Get(form, "queueList")).Items[1]).Locked, "The song already transitioning must stay locked while future entries remain editable");
                Check(Invoke(form, "AddQueuedSong", fast, 0, false) == null, "An already queued future song must not be inserted twice");
                Check(Get(form, "autoTransition") == plan && queue.Items.Count == 3 && queue.Current.Id == plan.QueueEntryId && queue.Next == later, "Rejected duplicate additions must keep the active transition intact");
                Check(((GlassButton)Get(form, "currentBpm")).Enabled == false && ((GlassButton)Get(form, "addedBpm")).Text.StartsWith("132"), "Transition controls must show ramping current BPM and the next song's target BPM");
                long paused = plan.Bridge.PositionFrames; Thread.Sleep(30); Invoke(form, "PollAutoTransition");
                Check(plan.Bridge.PositionFrames == paused, "Pausing audio must pause transition progress, independent of UI or wall-clock timers");
                float[] outro = new float[16000]; playback.Read(outro, 0, outro.Length);
                Check(Math.Abs(Frequency(outro, 16000, .05, .4) - 480) < 5, "The tempo ramp must still play the outgoing bass at its selected pitch");
                for (int position = 0; position < 4; position++)
                {
                    long target = plan.Timing.SwapFrame(position) + 1600;
                    while (playback.Current == plan.Bridge && plan.Bridge.PositionFrames < target) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (target - plan.Bridge.PositionFrames) * 2));
                    Invoke(form, "PollAutoTransition");
                    if (position < 3)
                    {
                        StemSourceState state = (StemSourceState)Get(form, "stemSources");
                        Check(state.Active[settings.Order[position]] != null && state.Active.Count(bank => bank != null) == position + 1, "Stems must change one by one in the configured order on the audio timeline");
                    }
                    if (position == 0) { Invoke(form, "SetAutoPlay", false); Check(playback.Current == plan.Bridge && !(bool)Get(form, "autoPlayEnabled") && queue.Items.Count == 0, "Turning Auto play off during a transition must finish its handoff and clear the session queue"); }
                    if (position == 0)
                    {
                        float[] incoming = new float[6400]; playback.Read(incoming, 0, incoming.Length);
                        Check(Math.Abs(Frequency(incoming, 16000, .025, .15) - 700 * 432.0 / 440) < 8, "The scheduled first stem must replace the outgoing bass at the selected tuning reference");
                    }
                }
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Invoke(form, "PollAutoTransition");
                Check(Get(form, "current") == next && Get(form, "mixer") == plan.Next && Get(form, "autoTransition") == null, "After all four stems change, the next song must become the current song");
                Check(queue.Items.Count == 0, "Finishing an old handoff must not restore entries cleared at the session boundary");
                Check(plan.Next.Seconds >= plan.Traits.BeatOffset + plan.Timing.IncomingSeconds && plan.Next.Seconds < plan.Traits.BeatOffset + plan.Timing.IncomingSeconds + .3, "The incoming song must continue from its already-playing position, without restarting");
                Check(((GlassButton)Get(form, "currentBpm")).Text.StartsWith("132") && ((GlassButton)Get(form, "currentBpm")).Enabled, "Normal tempo/key controls must return after handoff");
                Invoke(form, "SavePreferences");
                var saved = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root, "data", "player-settings.json")));
                AutoPlaySettings restored = AutoPlaySettings.Read((Dictionary<string, object>)saved["autoPlaySettings"]);
                Check(restored.Below == 5 && restored.Above == 15 && restored.RampBars == 1 && restored.StemBars == .25 && restored.EndCutBars == 1 && restored.Order.SequenceEqual(settings.Order), "BPM ranges, durations, end cut, and stem order must persist together");
                var reverse = settings.Copy(); reverse.Below = 15; Set(form, "autoSettings", reverse); Set(form, "autoPlayEnabled", true);
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var second = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(second != null && second.Song == original && second.Timing.FromBpm == 132 && second.Timing.ToBpm == 120, "After handoff, shuffle must prepare another transition using the new current song's BPM");
                Invoke(form, "SetAutoPlay", false);
                Check(Get(form, "autoTransition") == null && playback.Current == plan.Next, "Turning off before a scheduled outro begins must remove the queued timelines and keep the current song playing");
                Check(queue.Items.Count == 0, "Turning Auto play off must clear explicit songs and shuffle history together");
                Set(form, "autoPlayEnabled", true); Set(form, "autoSettings", new AutoPlaySettings { Below = 0, Above = 0 });
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                Check(Get(form, "autoTransition") == null && Get(form, "mixer") == plan.Next, "A strict range with no eligible song must preserve current playback");
                Set(form, "autoSettings", settings);
                // The reverse range includes the original again.
                ((AutoPlaySettings)Get(form, "autoSettings")).Below = 15;
                Task canceled = (Task)Invoke(form, "PrepareAutoTransition"); Invoke(form, "SetAutoPlay", false); Finish(canceled);
                Check(Get(form, "autoTransition") == null && playback.Current == plan.Next && playback.LoopStartFrame == null, "Turning off during preparation must cancel every pending render and queued transition");
                Set(form, "autoPlayEnabled", true);
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var removedSuggestion = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(removedSuggestion != null && queue.Next.Automatic, "Shuffle must expose a removable next-song suggestion");
                Invoke(form, "RemoveQueuedSong", removedSuggestion.QueueEntryId); Finish((Task)Invoke(form, "PrepareAutoTransition"));
                var requeuedSuggestion = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(requeuedSuggestion != null && requeuedSuggestion.Song == original && requeuedSuggestion.QueueEntryId != removedSuggestion.QueueEntryId && !queue.HasPlayed(original.Audio) && playback.Current == plan.Next, "Removing an unplayed shuffle suggestion must leave it eligible to be queued again without marking it as played");
                playback.Current.Seek(playback.Current.Duration); ((MixSlider)Get(form, "volume")).Value = 0; playback.Apply(0, (PlayerMix)Get(form, "mix")); Invoke(form, "CreateOutput");
                Finish((Task)Invoke(form, "TickAutoPlay"));
                Check(((WaveOutEvent)Get(form, "output")).PlaybackState == PlaybackState.Stopped, "A prepared handoff must not resume stopped playback without a natural-finish signal");
                Set(form, "queueAdvancePending", true); Finish((Task)Invoke(form, "TickAutoPlay"));
                Check(((WaveOutEvent)Get(form, "output")).PlaybackState == PlaybackState.Playing, "A handoff prepared after the outgoing device finishes must resume its scheduled audio instead of stalling the queue");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void RequestedTiming()
    {
        foreach (double bpm in new[] { 30.0, 37.5, 59, 80, 120, 127.3, 300 })
        foreach (double phase in new[] { 0.0, .173, 60 / bpm })
        for (double position = 0; position < 50; position += .137)
        {
            double start = AutoTransitionTiming.RequestedStart(position, bpm, phase, 100);
            Check(start >= position + .049999 && start <= position + 5.000001, "Requested transitions must stay within five seconds at every supported tempo and beat phase");
            double beat = (start - phase) * bpm / 60;
            Check(Math.Abs(beat - Math.Round(beat)) < .000001, "The five-second deadline must retain the analyzed beat phase");
        }
        Check(AutoTransitionTiming.RequestedStart(.25, 30, .17, 100) == 2.17, "A slow track must use the next beat when its next bar is over five seconds away");
        Check(AutoTransitionTiming.RequestedStart(.25, 120, 0, 100) == 2, "A requested transition must prefer the next bar when it fits the deadline");
        Check(AutoTransitionTiming.RequestedStart(8.1, 120, 0, 9) == 8.5, "Use an available beat before the end cut when the next bar lies beyond it");
        Check(AutoTransitionTiming.RequestedStart(9.2, 120, 0, 9) == 9.2, "A request beyond the end cut must start immediately without seeking backward");
    }
    private static void RequestedSlowNext(string root)
    {
        root = Path.Combine(root, "requested-slow-next"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Slow original", 30), next = Song(root, "Double-time incoming", 60);
        Wave(original.Audio, 16000, 12, 0, 30, .17);
        for (int lane = 0; lane < 4; lane++) Wave(Path.Combine(original.Folder, StemMixer.StemNames[lane] + ".wav"), 16000, 12, 0, 30, .17);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native); native.Seek(.4);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 30, BeatOffset = .17 }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next }); Set(form, "autoSettings", new AutoPlaySettings { Below = 0, Above = 0, RampBars = 1, StemBars = .25, EarlyStemTransitions = true }); Set(form, "autoPlayEnabled", true);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var ready = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(ready != null && ready.Timing.ToBpm == 30 && ready.Traits.Bpm == 60, "The slow fixture must prepare a half/double-time beat match");
                string bridgeAudio = ready.BridgeAudio; StemMixer bridge = ready.Bridge; int ticket = (int)Get(form, "autoTicket");
                Finish((Task)Invoke(form, "MoveSong", 1));
                ready = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(ready.Requested && Math.Abs(ready.SourceStart - 2.17) < .000001 && ready.Bridge != bridge && ready.BridgeAudio != bridgeAudio && (int)Get(form, "autoTicket") > ticket,
                    "Next must render the current section and start on the next beat within five seconds even at 30 BPM");
                bridge = ready.Bridge;
                var samples = new float[4096]; long start = (long)Math.Round(ready.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(samples, 0, (int)Math.Min(samples.Length, (start - native.PositionFrames) * 2));
                Check(playback.Current == native, "The outgoing song must continue until the requested beat");
                Check(playback.Read(samples, 0, samples.Length) == samples.Length && playback.Current == bridge && samples.Any(sample => Math.Abs(sample) > .05), "The requested beat must enter an audible bridge without an EOF or silence");
                Invoke(form, "PollAutoTransition");
                Check(ready.Started && playback.RestartCount == 0, "A prompt Next must start its transition without waiting for another play-through");
                while (playback.Current == bridge) playback.Read(samples, 0, samples.Length);
                Invoke(form, "PollAutoTransition");
                Check(playback.Current == ready.Next && Get(form, "current") == next, "A beat-based prompt start must still complete the prepared incoming handoff");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void RequestedNext(string root)
    {
        root = Path.Combine(root, "requested-next"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Requested original", 120), next = Song(root, "Requested incoming", 132, 660), later = Song(root, "Requested later", 140, 880);
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            ((MixSlider)Get(form, "volume")).Value = 0;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native); native.Seek(.25);
            var settings = new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = .25 };
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, later }); Set(form, "autoSettings", settings); Set(form, "autoPlayEnabled", true);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var normal = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(normal != null, "The immediate handoff must prepare: " + ((Label)Get(form, "message")).Text);
                var preload = (AutoSongPreload)Get(form, "autoPreload");
                Check(preload != null, "The following queued song must begin preparing before Next is pressed"); Finish(preload.Preparing);
                var queue = (PlaybackQueue)Get(form, "songQueue"); string[] ids = queue.Items.Select(entry => entry.Id).ToArray();
                Task nextRequest = (Task)Invoke(form, "MoveSong", 1);
                Check(!nextRequest.IsCompleted && playback.Current == native && native.Seconds == .25, "Next must keep the current playback position while preparing its outgoing section");
                Finish(nextRequest); var requested = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(requested != normal && requested.Song == next && requested.Requested && requested.SourceStart < normal.SourceStart && requested.SourceStart <= 2.01, "Next with Auto play on must render a fresh outgoing section at the next bar within five seconds");
                Check(requested.Next == normal.Next && requested.IncomingFiles == normal.IncomingFiles && requested.NextFiles == normal.NextFiles && requested.IncomingPeaks == normal.IncomingPeaks && requested.NextPeaks == normal.NextPeaks && requested.IncomingDynamics == normal.IncomingDynamics,
                    "Next must reuse the incoming song's loaded mixer, stem files, waveforms and analysis instead of preparing them again");
                Check(Get(form, "autoPreload") == preload && !preload.Cancellation.IsCancellationRequested,
                    "Next must preserve preparation ahead for the following song when its incoming mixer is unchanged");
                Check(Math.Abs(requested.SourceStart / 2 - Math.Round(requested.SourceStart / 2)) < .00001 && requested.SwapFrames.All(frame => frame >= requested.Timing.RampFrames), "Requested Next must begin on an outgoing bar and complete beat-matching before any incoming stem");
                Check(playback.Current == native && Get(form, "mixer") == native && queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Requesting an early transition must retain the outgoing audio feed and the session list");
                Check(Math.Abs(Frequency(Samples(requested.BridgeAudio), 16000, .5, .5) - 440) < 5, "The early transition must preserve outgoing pitch through tempo matching");
                playback.ReleaseRetired();
                Check(requested.Bridge.Read(new float[2], 0, 2) == 2, "Replacing the outro must not retire or dispose its new outgoing audio"); requested.Bridge.SeekFrame(0);
                var buffer = new float[4096]; long start = (long)Math.Round(requested.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                Check(playback.Current == requested.Bridge && queue.Current.Id == requested.QueueEntryId && queue.Previous.Audio == original.Audio, "Entering the requested handoff must retain the outgoing song as the previous session entry");
                long incomingFrame = (long)Math.Round(requested.Traits.BeatOffset * requested.Timing.Rate) + requested.Timing.IncomingSourceFrame(requested.Bridge.PositionFrames);
                Finish((Task)Invoke(form, "MoveSong", 1));
                Check(Get(form, "autoTransition") == null && Get(form, "current") == next && playback.Current == requested.Next,
                    "Next during a transition must immediately finish the handoff to its incoming song");
                Check(requested.Next.PositionFrames == incomingFrame && Get(form, "autoPreload") == preload && !preload.Cancellation.IsCancellationRequested && (bool)Get(form, "autoPlayEnabled"),
                    "Skipping a transition must reuse the incoming audio at its current song position and preserve Auto play and preparation ahead");
                Check(queue.Current.Id == requested.QueueEntryId && queue.Previous.Audio == original.Audio && queue.Items.Select(entry => entry.Id).SequenceEqual(ids),
                    "Skipping the transition must promote the incoming queue entry without skipping another song or changing session history");
                Finish((Task)Invoke(form, "MoveSong", -1)); ((WaveOutEvent)Get(form, "output")).Pause();
                Check(Get(form, "current") == original && Get(form, "autoTransition") == null && queue.CurrentIndex == 0 && queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Previous during an Auto play transition must load the previous song and preserve the queue");

                Task latePreparation = (Task)Invoke(form, "MoveSong", 1);
                ((StemMixer)Get(form, "mixer")).Seek(4); Finish(latePreparation);
                var late = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(late != null && late.SourceStart > 4 && late.SourceStart <= 9, "If playback passes the initially requested bar during loading, Next must render a matching future excerpt within five seconds of readiness");
                ((WaveOutEvent)Get(form, "output")).Pause(); Invoke(form, "CancelAutoPreparation"); ((StemMixer)Get(form, "mixer")).Seek(0);

                var loading = new TaskCompletionSource<PreparedAutoSong>(); Set(form, "autoPreloadTask", loading.Task);
                Task preparation = (Task)Invoke(form, "PrepareAutoTransition"); Set(form, "autoPreparationTask", preparation);
                int ticket = (int)Get(form, "autoTicket"); var cancellation = (CancellationTokenSource)Get(form, "autoCancellation");
                Task waiting = (Task)Invoke(form, "MoveSong", 1);
                Check(!waiting.IsCompleted && (bool)Get(form, "autoNextRequested") && Get(form, "current") == original, "Next must wait for an in-flight preparation without cutting to an unloaded song");
                Check((int)Get(form, "autoTicket") == ticket && Get(form, "autoPreparationTask") == preparation && !cancellation.IsCancellationRequested,
                    "Next during loading must keep the original preparation task and cancellation token running");
                loading.SetResult(null); Finish(waiting);
                var loaded = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(loaded != null && loaded.Requested && loaded.SourceStart - loaded.Reference.Seconds <= 5 && queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Once loading finishes, Next must schedule within five seconds without replacing session entries");
                Invoke(form, "CancelAutoPreparation");
                var canceledLoading = new TaskCompletionSource<PreparedAutoSong>(); Set(form, "autoPreloadTask", canceledLoading.Task);
                Set(form, "autoPreparationTask", (Task)Invoke(form, "PrepareAutoTransition"));
                Task canceledNext = (Task)Invoke(form, "MoveSong", 1); Invoke(form, "SetAutoPlay", false); canceledLoading.SetResult(null); Finish(canceledNext);
                Check(Get(form, "autoTransition") == null && queue.Items.Count == 0 && !(bool)Get(form, "autoNextRequested"), "Ending the session while Next waits for loading must cancel the request and keep the queue cleared");
            }
            finally
            {
                Invoke(form, "StopAutoTimeline");
                if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask"));
                if (Get(form, "autoPreloadPreparationTask") != null) Finish((Task)Get(form, "autoPreloadPreparationTask"));
                Invoke(form, "CloseAudio");
            }
        }
    }
    private static void RequestedPositionContinuity(string root)
    {
        foreach (string scenario in new[] { "ready", "advanced-during-render", "past-end-cut", "cancelled-during-render" })
        {
            string folder = Path.Combine(root, "requested-continuity", scenario); Directory.CreateDirectory(folder);
            SongInfo original = Song(folder, "Position markers", 120), next = Song(folder, "Incoming", 132, 660);
            // Every moment has a different audible frequency. A constant tone
            // cannot detect a bridge that jumps to another part of the song.
            foreach (string file in new[] { original.Audio }.Concat(StemMixer.StemNames.Select(name => Path.Combine(original.Folder, name + ".wav"))))
            using (var writer = new WaveFileWriter(file, new WaveFormat(16000, 24, 2)))
            for (int frame = 0; frame < 12 * 16000; frame++)
            {
                double time = frame / 16000.0;
                float sample = (float)(.12 * Math.Sin(2 * Math.PI * (220 * time + 40 * time * time)));
                writer.WriteSample(sample); writer.WriteSample(sample);
            }
            using (var form = new PlayerForm(folder, folder))
            {
                form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
                var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
                native.Seek(scenario == "ready" ? .25 : scenario == "past-end-cut" ? 8.25 : 4.25);
                Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
                Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120 }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
                Set(form, "songs", new List<SongInfo> { original, next }); Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = .25, EndCutBars = scenario == "past-end-cut" ? 2 : 0 }); Set(form, "autoPlayEnabled", true);
                PlayerMix mix = (PlayerMix)Get(form, "mix"); mix.Solo[0] = true; native.ApplyMix(mix);
                try
                {
                    Finish((Task)Invoke(form, "PrepareAutoTransition")); var normal = (PreparedAutoTransition)Get(form, "autoTransition");
                    Check(normal != null, "The continuity fixture must have a fully loaded incoming song");
                    Task request = (Task)Invoke(form, "MoveSong", 1);
                    if (scenario == "advanced-during-render")
                    {
                        var continuing = new float[4096]; long target = (long)(6.25 * 16000);
                        while (native.PositionFrames < target) playback.Read(continuing, 0, (int)Math.Min(continuing.Length, (target - native.PositionFrames) * 2));
                    }
                    long position = native.PositionFrames;
                    if (scenario == "cancelled-during-render")
                    {
                        Invoke(form, "SetAutoPlay", false); Finish(request);
                        Check(Get(form, "autoTransition") == null && playback.Current == native && native.PositionFrames == position, "Canceling outgoing preparation must retain current playback and remove the pending handoff");
                        Check(playback.Read(new float[4096], 0, 4096) == 4096, "Canceling Next must not dispose the current song's audio");
                        continue;
                    }
                    Finish(request); var prepared = (PreparedAutoTransition)Get(form, "autoTransition");
                    Check(prepared != normal && prepared.Requested && prepared.Next == normal.Next && prepared.IncomingFiles == normal.IncomingFiles, "Next must refresh the outgoing position while retaining the loaded incoming song");
                    Check(playback.Current == native && native.PositionFrames == position && prepared.SourceStart > native.Seconds && prepared.SourceStart <= native.Seconds + 5, "Preparing Next must not seek, pause, replace, or replay the current song");
                    double expected = 220 + 80 * (prepared.SourceStart + prepared.Timing.SourceAt(.12));
                    foreach (string file in new[] { prepared.BridgeAudio }.Concat(prepared.OutgoingFiles))
                        Check(Math.Abs(Frequency(Samples(file), 16000, .06, .12) - expected) < 16, "Every rendered outgoing source must contain the scheduled current section, never the old outro or a missed beat");
                    Check(!playback.ReplaceScheduledBridge(native, prepared.Bridge, normal.Bridge, prepared.Next, position - 1, true), "The audio thread must reject a replacement whose exact source frame is already in the past");
                    var samples = new float[4800]; long start = (long)Math.Round(prepared.SourceStart * 16000);
                    while (native.PositionFrames < start)
                    {
                        int count = (int)Math.Min(samples.Length, (start - native.PositionFrames) * 2);
                        Check(playback.Read(samples, 0, count) == count, "The current song must continue filling audio buffers while approaching the transition");
                    }
                    Check(playback.Read(samples, 0, samples.Length) == samples.Length && playback.Current == prepared.Bridge, "The freshly rendered transition must enter exactly at its original source frame");
                    double audible = 220 + 80 * (prepared.SourceStart + prepared.Timing.SourceAt(.09));
                    Check(Math.Abs(Frequency(samples, 16000, .04, .10) - audible) < 16, "The audible transition must continue the current passage without jumping to the old outro");
                }
                finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
            }
        }
    }
    private static void RequestedPreloadedNext(string root)
    {
        root = Path.Combine(root, "requested-preloaded-next"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Original", 120), next = Song(root, "Incoming", 132), later = Song(root, "Following", 140);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", new StemSourceState(original.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, later }); Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = .25, EarlyStemTransitions = true }); Set(form, "autoPlayEnabled", true);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                var preload = (AutoSongPreload)Get(form, "autoPreload"); Finish(preload.Preparing); PreparedAutoTransition ready = preload.Preparing.Result;
                var buffer = new float[4096]; long start = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < start) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (start - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition");
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Invoke(form, "PollAutoTransition");
                Finish((Task)Invoke(form, "MoveSong", 1)); var requested = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(preload.Consumed && requested != null && requested.Song == later && requested.Next == ready.Next && requested.IncomingFiles == ready.IncomingFiles && requested.IncomingPeaks == ready.IncomingPeaks && requested.IncomingDynamics == ready.IncomingDynamics,
                    "Next after a handoff must consume the complete prepared successor and reuse its incoming mixer, stems, waveforms and dynamics");
                Check(playback.Current == plan.Next && requested.SourceStart - plan.Next.Seconds <= 5 && requested.CutFrames != null && requested.CutFrames.SequenceEqual(requested.SwapFrames) && Enumerable.Range(0, 4).All(index => requested.SwapFrames[requested.Timing.Order[index]] == requested.Timing.SwapFrame(index)),
                    "Next with quiet-stem detection enabled must retain current playback and use beat-matched stem switches without waiting for vocal phrases");
                int ticket = (int)Get(form, "autoTicket");
                Finish((Task)Invoke(form, "MoveSong", 1));
                Check(Get(form, "autoTransition") == requested && (int)Get(form, "autoTicket") == ticket,
                    "Pressing Next again for the same ready early handoff must not prepare its audio again");
            }
            finally
            {
                Invoke(form, "StopAutoTimeline");
                if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask"));
                if (Get(form, "autoPreloadPreparationTask") != null) Finish((Task)Get(form, "autoPreloadPreparationTask"));
                Invoke(form, "CloseAudio");
            }
        }
    }
    private static void ReplayQueue(string root)
    {
        root = Path.Combine(root, "replay-queue"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Replay original", 120), history = Song(root, "Played next", 156, 660), last = Song(root, "Played last", 132, 880), fresh = Song(root, "Unqueued fresh", 134, 550), manual = Song(root, "Manually appended", 180, 770);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            Set(form, "songs", new List<SongInfo> { original, history, last, fresh, manual }); ((MixSlider)Get(form, "volume")).Value = 0;
            var queue = (PlaybackQueue)Get(form, "songQueue");
            foreach (SongInfo song in new[] { original, history, last }) { PlaybackQueueEntry entry = queue.Add(song.Audio, queue.Items.Count, true); queue.Select(entry.Id); }
            string[] ids = queue.Items.Select(entry => entry.Id).ToArray();
            var settings = new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = .25 };
            Set(form, "autoSettings", settings); Set(form, "autoPlayEnabled", true);
            try
            {
                Finish((Task)Invoke(form, "PlayQueuedSong", queue.Items[0])); ((WaveOutEvent)Get(form, "output")).Pause();
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Song == history && plan.QueueEntryId == ids[1] && queue.Items.Take(3).Select(entry => entry.Id).SequenceEqual(ids) && queue.Items.Count == 4 && queue.Items.Last().Audio == fresh.Audio, "Replaying history must retain the immediate played next row and existing order while appending eligible songs to the lookahead");
                Invoke(form, "MoveQueuedSong", ids[2], 1);
                Check(Get(form, "autoTransition") == null, "Reordering played next rows must invalidate the pending transition");
                Finish((Task)Invoke(form, "PrepareAutoTransition")); plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Song == last && plan.QueueEntryId == ids[2] && queue.Items.Count == 4 && queue.Items.Last().Audio == fresh.Audio, "Autoplay must follow reordered history while retaining the planned future song at the end");
                Invoke(form, "MoveQueuedSong", ids[2], 3);
                Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = 64 });
                Finish((Task)Invoke(form, "PrepareAutoTransition"));
                Check(Get(form, "autoTransition") == null && queue.Next.Id == ids[1] && queue.Items.Count == 4, "An existing automatic history row that cannot transition must stay queued for normal playback rather than being replaced");
                Set(form, "autoSettings", settings);
                Finish((Task)Invoke(form, "PlayQueuedSong", queue.Items[2])); ((WaveOutEvent)Get(form, "output")).Pause();
                var appended = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", manual, queue.CurrentIndex + 1, false);
                Finish((Task)Invoke(form, "PrepareAutoTransition")); plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Song == manual && plan.QueueEntryId == appended.Id && queue.Items.Count == 5 && queue.Items.Last().Audio == fresh.Audio, "A manually inserted next song must take priority while retaining the previously planned shuffle song");
                Invoke(form, "RemoveQueuedSong", appended.Id);
                Finish((Task)Invoke(form, "PrepareAutoTransition")); plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Song == fresh && queue.Items.Count == 4 && queue.Items.Last().Id == plan.QueueEntryId, "Removing a manual next choice must return to the already planned shuffle song without appending a duplicate");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void MarkerEditing(string root)
    {
        root = Path.Combine(root, "marker-edits"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Marker original", 120), next = Song(root, "Next song with a longer title", 132, 660);
        foreach (SongInfo song in new[] { original, next }) using (var cover = new Bitmap(128, 128))
        {
            using (Graphics graphics = Graphics.FromImage(cover)) graphics.Clear(song == original ? Color.Coral : Color.MediumSlateBlue);
            cover.Save(song.Cover, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; form.Size = new Size(1400, 960); form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show();
            Stopwatch loaded = Stopwatch.StartNew(); while ((bool)Get(form, "refreshing") && loaded.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
            ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            var sources = new StemSourceState(original.Folder);
            var views = (WaveformView[])Get(form, "waveforms");
            for (int lane = 0; lane < 4; lane++) { sources.OriginalPeaks[lane] = views[lane].Peaks = PlayerWaveforms.Read(sources.Original(lane), CancellationToken.None); views[lane].Duration = native.Duration; }
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", sources);
            ((Label)Get(form, "songTitle")).Text = original.Title; ((Label)Get(form, "artistLabel")).Text = original.Artist;
            ((PictureBox)Get(form, "cover")).Image = new Bitmap(original.Cover);
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next }); Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = 1 }); Set(form, "autoPlayEnabled", true);
            Invoke(form, "UpdateStemGestureMode"); Invoke(form, "LayoutContent"); Invoke(form, "SetEnabled", true); Invoke(form, "UpdateTransport");
            Check(views.All(view => view.AutoMarkers == null) && !(bool)Invoke(form, "EditAutoTransitionPoint", 2, false, 5.0), "Markers and editing must wait for the entire next song and plan");
            var buttons = (GlassButton[])Get(form, "sourceButtons"); var viewSwitch = (GlassButton)Get(form, "transitionEditorButton");
            Check(!(bool)Get(form, "transitionEditorEnabled") && buttons.All(button => !button.DisplayOnly && button.Toggle && button.Subtitle == null), "Every new player session must default to the previous waveform view and stem switches");
            viewSwitch.PerformClick();
            Check((bool)Get(form, "transitionEditorEnabled") && views.All(view => view.AutoMarkers == null) && !(bool)Invoke(form, "EditAutoTransitionPoint", 2, true, 3.0), "Opting into the editor must still wait for the next song and transition points");
            viewSwitch.PerformClick();
            PlayerMix mix = (PlayerMix)Get(form, "mix"); mix.Solo[2] = true; native.ApplyMix(mix);
            try
            {
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition"); Invoke(form, "UpdateTransport");
                Check(plan != null && views.All(view => view.AutoMarkers == null) && buttons.All(button => !button.DisplayOnly && button.Subtitle == null), "A ready transition must retain the previous view until the editor is explicitly enabled");
                long beforeView = native.PositionFrames; viewSwitch.PerformClick();
                Check(views.All(view => view.AutoMarkers != null) && native.PositionFrames == beforeView && Get(form, "autoTransition") == plan, "Switching views must preserve playback and the prepared transition");
                Check(Enumerable.Range(0, 4).All(lane => views[lane].AutoMarkers.OutgoingDuration == plan.Bridge.Duration && views[lane].AutoMarkers.IncomingDuration == plan.Bridge.Duration && views[lane].AutoMarkers.OutgoingPeaks == plan.OutgoingPeaks[lane] && views[lane].AutoMarkers.IncomingPeaks == plan.IncomingPeaks[lane]), "Both songs and every lane must use the same aligned transition excerpt before playback starts");
                Check(buttons.All(button => button.DisplayOnly && !button.Toggle && button.Text == next.Title && button.Subtitle.Contains("OUT") && button.Subtitle.Contains("IN")), "Autoplay source buttons must display the prepared next song and both source timestamps");
                var tips = (ToolTip)Get(form, "tip");
                Check(views.All(view => String.IsNullOrEmpty(tips.GetToolTip(view))) && buttons.All(button => String.IsNullOrEmpty(tips.GetToolTip(button))), "The transition editor must not show hover instructions over its stems or source cards");
                Check(views.All(view => view.AutoMarkers.OutgoingMinimum == 0 && view.AutoMarkers.IncomingMinimum == 0), "No portion of the editor range may be shaded as unavailable");
                Check(plan.IncomingPeaks[2].Take(200).Any(peak => peak > .01), "The next song must have real waveform audio available before tempo matching ends");
                float[] incomingTimeline = Samples(plan.IncomingFiles[2]), nextTimeline = Samples(plan.NextFiles[2]);
                Check(new[] { plan.Timing.RampFrames, plan.Timing.RampFrames + 1600, plan.Timing.TotalFrames - 1 }.All(frame => Math.Abs(incomingTimeline[frame * 2] - nextTimeline[(frame - plan.Timing.RampFrames) * 2]) < .000001), "Preparing early IN audio must retain the next song's exact normal timeline and final handoff samples");
                Check(new[] { 0L, plan.Timing.RampFrames / 2, plan.Timing.RampFrames - 1 }.All(frame => Math.Abs(incomingTimeline[frame * 2] - nextTimeline[plan.Timing.IncomingSourceFrame(frame) * 2]) < .000001), "Early IN audio and displayed source timestamps must share the same beat-aligned opening-bar loop");
                Check(((GlassButton)Get(form, "addedBpm")).Text.StartsWith("132") && !((GlassButton)Get(form, "addedBpm")).Enabled, "The fully loaded next-song header must show its known tempo without exposing donor tuning");
                buttons[2].PerformClick(); Check(Get(form, "autoTransition") == plan && sources.Active[2] == null, "Display-only next-song buttons must not switch sources or cancel a transition");
                var missing = plan.NextPeaks[3]; plan.NextPeaks[3] = null; Invoke(form, "UpdateTransport");
                Check(views.All(view => view.AutoMarkers == null) && !(bool)Invoke(form, "EditAutoTransitionPoint", 2, false, 5.0), "A partially loaded next song must never expose editable transition points");
                plan.NextPeaks[3] = missing; Invoke(form, "UpdateTransport");
                int zoomX = (int)Math.Round((views[1].Width - 1) * .63); double anchor = views[1].AutoTimeAt(zoomX), fullSpan = plan.Bridge.Duration;
                Invoke(views[1], "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, zoomX, views[1].Height - 4, 120));
                double span = views[1].AutoMarkers.ViewSeconds, startView = views[1].AutoMarkers.ViewStart;
                Check(span < fullSpan && Math.Abs(views[1].AutoTimeAt(zoomX) - anchor) < .000001 && views.All(view => view.AutoMarkers.ViewSeconds == span && view.AutoMarkers.ViewStart == startView), "Wheel zoom must keep the hovered time under the mouse and zoom all eight aligned waveforms together");
                Invoke(form, "UpdateTransport");
                Check(views.All(view => view.AutoMarkers.ViewSeconds == span && view.AutoMarkers.ViewStart == startView) && native.PositionFrames == beforeView, "Transport refreshes must preserve zoom without moving playback");
                var otherFocus = ((GlassButton[])Get(form, "mutes"))[0]; otherFocus.Focus();
                Point hover = views[3].PointToScreen(new Point(views[3].Width / 3, views[3].Height - 4));
                int location = (hover.X & 0xffff) | ((hover.Y & 0xffff) << 16);
                Message wheel = Message.Create(otherFocus.Handle, 0x020A, new IntPtr(120 << 16), new IntPtr(location));
                Check(((IMessageFilter)Get(form, "autoWheelFilter")).PreFilterMessage(ref wheel) && views.All(view => view.AutoMarkers.ViewSeconds < span) && otherFocus.Focused, "Hover zoom must work with focus on another control and must not steal that focus");
                double outsideSpan = views[0].AutoMarkers.ViewSeconds; Point outside = form.PointToScreen(new Point(30, 30));
                Message outsideWheel = Message.Create(otherFocus.Handle, 0x020A, new IntPtr(120 << 16), new IntPtr((outside.X & 0xffff) | ((outside.Y & 0xffff) << 16)));
                Check(!((IMessageFilter)Get(form, "autoWheelFilter")).PreFilterMessage(ref outsideWheel) && views[0].AutoMarkers.ViewSeconds == outsideSpan, "Scrolling outside stem waveforms must leave the shared zoom unchanged");
                Invoke(views[0], "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 0, 5, 12000));
                Check(views.All(view => view.AutoMarkers.ViewSeconds > 0 && view.AutoMarkers.ViewStart >= 0 && view.AutoMarkers.ViewStart + view.AutoMarkers.ViewSeconds <= fullSpan + .000001), "Deep zoom at an edge must keep every view within the prepared audio");
                Invoke(views[0], "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, views[0].Width - 1, 5, -12000));
                Check(views.All(view => Math.Abs(view.AutoMarkers.ViewSeconds - fullSpan) < .000001 && view.AutoMarkers.ViewStart == 0), "Wheel zoom out must restore the entire aligned transition");
                long[] unchanged = (long[])plan.SwapFrames.Clone();
                Check((bool)Invoke(form, "SetAutoTransitionPoint", 2, true, fullSpan, false) && plan.SwapFrames[2] == plan.Timing.TotalFrames, "IN must also be placeable at the end of the full transition range");
                Check((bool)Invoke(form, "EditAutoTransitionPoint", 2, true, plan.SwapFrames[1] / 16000.0) && plan.SwapFrames[2] == plan.SwapFrames[1], "Different stems must be allowed to start at exactly the same point");
                double desiredCut = plan.Timing.RampSeconds + 60 / plan.Timing.ToBpm;
                int x = (int)Math.Round(desiredCut / views[2].AutoMarkers.OutgoingDuration * (views[2].Width - 1));
                Invoke(views[2], "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, x, 5, 0));
                Check(plan.CutFrames != null && plan.CutFrames[2] < unchanged[2] && plan.CutFrames[2] >= plan.Timing.RampFrames, "Right-clicking the upper waveform must move the audible outgoing cut after the ramp: enabled=" + views[2].Enabled + ", duration=" + views[2].Duration + ", width=" + views[2].Width + ", x=" + x + ", cut=" + (plan.CutFrames == null ? -1 : plan.CutFrames[2]) + ", original=" + unchanged[2] + ", status=" + ((Label)Get(form, "message")).Text);
                double desiredStart = plan.Timing.RampSeconds + 5 * 60 / plan.Timing.IncomingBpm;
                x = (int)Math.Round(desiredStart / views[2].AutoMarkers.IncomingDuration * (views[2].Width - 1));
                Invoke(views[2], "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, x, views[2].Height - 3, 0));
                Check(plan.SwapFrames[2] > unchanged[2] && plan.SwapFrames[2] > plan.CutFrames[2], "Right-clicking the lower waveform must move the next song's actual incoming start independently");
                double beats = (plan.SwapFrames[2] - plan.Timing.RampFrames) / (double)plan.Timing.Rate * plan.Timing.IncomingBpm / 60;
                Check(Math.Abs(beats - Math.Round(beats)) < .0001 && Enumerable.Range(0, 4).Where(lane => lane != 2).All(lane => plan.SwapFrames[lane] == unchanged[lane]), "Snapping must retain the incoming beat grid and preserve the other stem schedules");
                double zoomCut = plan.Timing.RampSeconds + 2 * 60 / plan.Timing.ToBpm;
                int zoomPoint = (int)Math.Round(zoomCut / fullSpan * (views[2].Width - 1));
                Invoke(views[2], "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, zoomPoint, 5, 240));
                x = (int)Math.Round((zoomCut - views[2].AutoMarkers.ViewStart) / views[2].AutoMarkers.ViewSeconds * (views[2].Width - 1));
                Invoke(views[2], "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, x, 5, 0));
                Check(Math.Abs(plan.CutFrames[2] / 16000.0 - zoomCut) < .0001, "Right-click editing while zoomed must use the viewport's actual transition time");
                long savedCut = plan.CutFrames[2], savedArrival = plan.SwapFrames[2]; span = views[2].AutoMarkers.ViewSeconds; startView = views[2].AutoMarkers.ViewStart;
                viewSwitch.PerformClick();
                Check(views.All(view => view.AutoMarkers == null) && buttons.All(button => !button.DisplayOnly && button.Subtitle == null) && Get(form, "autoTransition") == plan && native.PositionFrames == beforeView, "Returning to the standard view must restore its controls without canceling the planned audio");
                viewSwitch.PerformClick();
                Check(views.All(view => view.AutoMarkers.ViewSeconds == span && view.AutoMarkers.ViewStart == startView) && plan.CutFrames[2] == savedCut && plan.SwapFrames[2] == savedArrival, "Returning to the editor must retain its zoom and edited transition points");
                Check(!(bool)Invoke(form, "EditAutoTransitionPoint", 2, false, Double.NaN), "Invalid click coordinates must leave the schedule intact");
                using (var preview = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size)); preview.Save(Path.Combine(Directory.GetCurrentDirectory(), "tools", "auto-transition-markers-preview.png")); }
                var buffer = new float[4096]; long begin = (long)Math.Round(plan.SourceStart * 16000);
                while (native.PositionFrames < begin) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (begin - native.PositionFrames) * 2));
                playback.Read(buffer, 0, 2); Invoke(form, "PollAutoTransition"); Invoke(form, "UpdateTransport");
                Check(views.All(view => view.Enabled && view.AutoMarkers.OutgoingDuration == plan.Bridge.Duration), "Markers must move onto the bridge's exact timeline and stay editable during the ramp");
                Check(views.All(view => view.AutoMarkers.ViewSeconds == span && view.AutoMarkers.ViewStart == startView), "Entering the transition must preserve alignment and the selected zoom");
                long bridgePosition = plan.Bridge.PositionFrames; viewSwitch.PerformClick();
                Check(views.All(view => view.AutoMarkers == null && !view.Enabled) && plan.Bridge.PositionFrames == bridgePosition, "The standard view must retain its original transition behavior during playback");
                viewSwitch.PerformClick();
                Check(views.All(view => view.AutoMarkers != null && view.Enabled) && plan.Bridge.PositionFrames == bridgePosition, "The editor must remain switchable during a live transition without restarting audio");
                Check((bool)Invoke(form, "EditAutoTransitionPoint", 2, false, -20.0) && plan.CutFrames[2] == 0, "The beginning of the transition range must be editable before tempo matching ends");
                long cut = plan.CutFrames[2], arrival = plan.SwapFrames[2];
                while (plan.Bridge.PositionFrames < cut + 1600) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (cut + 1600 - plan.Bridge.PositionFrames) * 2));
                var quiet = new float[3200]; playback.Read(quiet, 0, quiet.Length); Invoke(form, "PollAutoTransition");
                Check(quiet.All(sample => Math.Abs(sample) < .003) && plan.Silenced[2] && !plan.Switched[2], "The edited cut must actually silence its stem while preserving the later arrival");
                Check((bool)Invoke(form, "EditAutoTransitionPoint", 2, false, zoomCut) && plan.CutFrames[2] > plan.Bridge.PositionFrames && !plan.Silenced[2], "An already-played OUT point must remain editable and restore the outgoing waveform immediately");
                var restored = new float[4800]; playback.Read(restored, 0, restored.Length);
                Check(Math.Abs(Frequency(restored, 16000, .025, .12) - 480) < 8, "Moving a played OUT point later must audibly restore its outgoing stem");
                Check((bool)Invoke(form, "EditAutoTransitionPoint", 2, true, 0.0) && plan.SwapFrames[2] == 0 && plan.SwapFrames[2] < plan.CutFrames[2] && plan.Switched[2], "IN may move before OUT, before tempo matching, and before the current cursor");
                var overlap = new float[8000]; playback.Read(overlap, 0, overlap.Length);
                Check(ToneAmplitude(overlap, 16000, 480) > .035 && ToneAmplitude(overlap, 16000, 700) > .035, "An early IN before OUT must actually play both songs' bass stems simultaneously");
                Check((bool)Invoke(form, "EditAutoTransitionPoint", 2, true, arrival / 16000.0) && !plan.Switched[2], "A played IN point may move back into the future and restore the outgoing source display");
                playback.Read(restored, 0, restored.Length);
                Check(ToneAmplitude(restored, 16000, 480) > .035 && ToneAmplitude(restored, 16000, 700) < .005, "Moving IN later must silence only the incoming audio path");
                cut = plan.CutFrames[2];
                while (plan.Bridge.PositionFrames < cut + 1600) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (cut + 1600 - plan.Bridge.PositionFrames) * 2));
                playback.Read(quiet, 0, quiet.Length); Invoke(form, "PollAutoTransition");
                Check(quiet.All(sample => Math.Abs(sample) < .003) && plan.Silenced[2] && !plan.Switched[2], "Independent points must still allow a silent gap after an overlap is removed");
                Check((bool)Invoke(form, "EditAutoTransitionPoint", 2, true, arrival / 16000.0 + .4), "A pending arrival must remain editable after the outgoing cut has played");
                arrival = plan.SwapFrames[2];
                while (plan.Bridge.PositionFrames < arrival + 800) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (arrival + 800 - plan.Bridge.PositionFrames) * 2));
                var audible = new float[4800]; playback.Read(audible, 0, audible.Length); Invoke(form, "PollAutoTransition");
                Check(Math.Abs(Frequency(audible, 16000, .025, .12) - 700) < 8 && plan.Switched[2], "The edited arrival must play the incoming bass at the selected frame");
                long overlapCut = plan.Bridge.PositionFrames + 6400;
                Check((bool)Invoke(form, "SetAutoTransitionPoint", 2, false, overlapCut / 16000.0, false), "A played OUT point may move after an already-audible IN point");
                playback.Read(overlap, 0, overlap.Length);
                Check(ToneAmplitude(overlap, 16000, 480) > .035 && ToneAmplitude(overlap, 16000, 700) > .035, "An overlap created during playback must retain both independent readers");
                while (plan.Bridge.PositionFrames < overlapCut + 1600) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (overlapCut + 1600 - plan.Bridge.PositionFrames) * 2));
                playback.Read(audible, 0, audible.Length);
                Check(ToneAmplitude(audible, 16000, 480) < .005 && ToneAmplitude(audible, 16000, 700) > .035, "A later OUT cut must silence only the original stem and keep the incoming stem playing");
                Check((bool)Invoke(form, "SetAutoTransitionPoint", 2, false, plan.Bridge.Duration, false) && plan.CutFrames[2] == plan.Timing.TotalFrames, "OUT must be placeable at the exact end of the transition range, later than IN");
                Check((bool)Invoke(form, "SetAutoTransitionPoint", 2, true, .12345, false) && Math.Abs(plan.SwapFrames[2] / 16000.0 - .12345) < 1 / 16000.0, "Free placement must support arbitrary sample positions and played IN points");
                playback.Read(overlap, 0, overlap.Length);
                Check(ToneAmplitude(overlap, 16000, 480) > .035 && ToneAmplitude(overlap, 16000, 700) > .035, "Moving a played OUT to the range end must restore overlap without stopping the incoming stem");
                while (playback.Current == plan.Bridge) playback.Read(buffer, 0, buffer.Length);
                Invoke(form, "PollAutoTransition"); Invoke(form, "UpdateTransport");
                Check(views.All(view => view.AutoMarkers == null) && Get(form, "zoomedAutoPlan") == null, "Completed transition markers and zoom references must clear before another song is prepared");
                viewSwitch.PerformClick();
                var manualSources = (StemSourceState)Get(form, "stemSources");
                var donor = new StemSourceBank { Song = original, Traits = new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }, Plan = new StemMatchPlan { Tempo = 1, MatchedBpm = 120 }, Files = Enumerable.Range(0, 4).Select(lane => sources.Original(lane)).ToArray(), Peaks = sources.OriginalPeaks, LoudnessGain = 1 };
                manualSources.Available = donor; Invoke(form, "UpdateSourceButtons"); long beforeSwitch = plan.Next.PositionFrames;
                buttons[2].PerformClick();
                Check(manualSources.Active[2] == donor && plan.Next.PositionFrames == beforeSwitch && (bool)Get(form, "autoPlayEnabled"), "The standard Auto play view must retain the previous manual source-switch feature");
                var switchedAudio = new float[6400]; playback.Read(switchedAudio, 0, switchedAudio.Length);
                Check(Math.Abs(Frequency(switchedAudio, 16000, .025, .15) - 480) < 8, "A standard-view source switch during Auto play must still change the actual stem audio");
                buttons[2].PerformClick(); Check(manualSources.Active[2] == null, "The standard-view source button must still return to the original stem");
                Invoke(form, "SetAutoPlay", false);
                Check(buttons.All(button => !button.DisplayOnly && button.Toggle && button.Subtitle == null) && views.All(view => view.TimingEnabled), "Disabling autoplay must restore ordinary stem toggles and timing gestures");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
        string phraseFile = Path.Combine(root, "phrases.wav"); DynamicWave(phraseFile, time => time >= 4 && time < 5.5 ? 0 : 1);
        var dynamics = AutoAudioDynamics.Read(phraseFile, CancellationToken.None);
        var timing = new AutoTransitionTiming(120, 120, 16000, new AutoPlaySettings { RampBars = 1, StemBars = 1 });
        long? phrase = AutoStemTransitions.Snap(4.26, 0, false, dynamics, timing, timing.RampFrames, timing.TotalFrames - 1600, new long[0]);
        Check(phrase.HasValue && phrase >= 4.0 * 16000 && phrase < 5.5 * 16000 && dynamics.BoundaryCost(phrase.Value / 16000.0, 0, 120) < 500, "Vocal edits must prefer nearby sustained phrase breaks over cutting through words");
        Check(AutoStemTransitions.SnapEditor(0, 0, false, dynamics, timing) == 0 && AutoStemTransitions.SnapEditor(timing.TotalFrames / 16000.0, 0, true, dynamics, timing) == timing.TotalFrames, "Editor snapping must include both range edges regardless of phrase avoidance");
        Check(AutoStemTransitions.SnapEditor(.721, 2, true, dynamics, timing, false) == 11536 && !AutoStemTransitions.SnapEditor(Double.PositiveInfinity, 2, true, dynamics, timing).HasValue, "Free placement must retain arbitrary timing while rejecting nonfinite coordinates");
    }
    private static void Seeking(string root)
    {
        root = Path.Combine(root, "seek-preservation"); Directory.CreateDirectory(root);
        SongInfo original = Song(root, "Seek original", 120), next = Song(root, "Seek next", 132, 660), later = Song(root, "Seek successor", 144, 880);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
            var native = new StemMixer(original.Audio, original.Folder); var playback = new PlayerPlayback(native);
            var sources = new StemSourceState(original.Folder); var views = (WaveformView[])Get(form, "waveforms");
            for (int lane = 0; lane < 4; lane++) { sources.OriginalPeaks[lane] = views[lane].Peaks = PlayerWaveforms.Read(sources.Original(lane), CancellationToken.None); views[lane].Duration = native.Duration; }
            Set(form, "current", original); Set(form, "mixer", native); Set(form, "playback", playback); Set(form, "stemSources", sources);
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", native.TotalFrames); Set(form, "nativeRate", 16000);
            Set(form, "songs", new List<SongInfo> { original, next, later }); Set(form, "autoSettings", new AutoPlaySettings { Above = 15, RampBars = 1, StemBars = 1 }); Set(form, "autoPlayEnabled", true);
            PlayerMix mix = (PlayerMix)Get(form, "mix"); mix.Solo[2] = true; native.ApplyMix(mix);
            Invoke(form, "FollowSessionSong", original); var queue = (PlaybackQueue)Get(form, "songQueue");
            var incoming = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", next, queue.Items.Count, false);
            Invoke(form, "AddQueuedSong", later, queue.Items.Count, false);
            var loading = new TaskCompletionSource<PreparedAutoSong>();
            var pending = new AutoSongPreload { QueueEntryId = incoming.Id, Identity = (string)Invoke(form, "AutoSongIdentity", next), Rate = 16000, Loading = loading.Task, Preparing = Task.FromResult<PreparedAutoTransition>(null), Source = new AutoTransitionSource { Mixer = native } };
            Set(form, "autoPreload", pending); Set(form, "autoPreloadTask", loading.Task);
            Task preparation = null;
            try
            {
                Invoke(form, "CreateOutput");
                preparation = (Task)Invoke(form, "PrepareAutoTransition"); Set(form, "autoPreparationTask", preparation);
                int ticket = (int)Get(form, "autoTicket"); var cancellation = (CancellationTokenSource)Get(form, "autoCancellation");
                Check(!preparation.IsCompleted && (bool)Get(form, "preparingAutoPlay"), "The seek regression must exercise an actual in-flight next-song load");
                foreach (double target in new[] { .5, 1.5, .25 })
                {
                    Invoke(form, "SeekTo", target);
                    Check(native.PositionFrames == (long)Math.Round(target * 16000) && playback.Current == native, "Seeking during loading must still move the current song forward and backward");
                    Check(!preparation.IsCompleted && (bool)Get(form, "preparingAutoPlay") && Get(form, "autoPreparationTask") == preparation && Get(form, "autoPreload") == pending && !pending.Cancellation.IsCancellationRequested && !cancellation.IsCancellationRequested && (int)Get(form, "autoTicket") == ticket, "Seeking must not cancel or restart an in-flight next-song load");
                }
                var ready = new PreparedAutoSong { Audio = next.Audio, Frames = 192000, Traits = new MusicTraits { Bpm = 132, Tonic = 9, Minor = true }, LoudnessGain = 1 };
                for (int lane = 0; lane < 4; lane++) { ready.Files[lane] = Path.Combine(next.Folder, StemMixer.StemNames[lane] + ".wav"); ready.Peaks[lane] = PlayerWaveforms.Read(ready.Files[lane], CancellationToken.None); }
                loading.SetResult(ready); Finish(preparation);
                var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Song == next && plan.NextFiles.SequenceEqual(ready.Files), "The original in-flight load must finish into the expected next-song transition");
                var successor = (AutoSongPreload)Get(form, "autoPreload"); Finish(successor.Loading);
                Invoke(form, "SetTransitionEditor", true);
                Invoke(form, "SetAutoTransitionPoint", 2, false, plan.Timing.RampSeconds + 3, false);
                Invoke(form, "SetAutoTransitionPoint", 2, true, .35, false);
                Invoke(form, "ZoomAutoWaveforms", 240, .63);
                long[] cuts = (long[])plan.CutFrames.Clone(), starts = (long[])plan.SwapFrames.Clone();
                float[][] peaks = plan.IncomingPeaks; StemSourceBank bank = plan.Bank; StemMixer bridge = plan.Bridge, nextMixer = plan.Next;
                double viewStart = views[2].AutoMarkers.ViewStart, viewSeconds = views[2].AutoMarkers.ViewSeconds;
                string[] files = plan.OutgoingFiles.Concat(plan.IncomingFiles).Concat(plan.NextFiles).Concat(new[] { plan.BridgeAudio, plan.NextAudio }).Distinct().ToArray();
                DateTime[] writes = files.Select(File.GetLastWriteTimeUtc).ToArray();
                ticket = (int)Get(form, "autoTicket");
                foreach (double target in new[] { 1.5, .15, plan.SourceStart + plan.Timing.SourceAt(1.2), .5 })
                {
                    Invoke(form, "SeekTo", target);
                    Check(Get(form, "autoTransition") == plan && plan.Bridge == bridge && plan.Next == nextMixer && plan.Bank == bank && plan.IncomingPeaks == peaks && (int)Get(form, "autoTicket") == ticket, "A ready transition must retain its loaded mixers, bank, waveforms and preparation ticket across seeks");
                    Check(plan.CutFrames.SequenceEqual(cuts) && plan.SwapFrames.SequenceEqual(starts) && views.All(view => view.AutoMarkers != null && view.AutoMarkers.ViewStart == viewStart && view.AutoMarkers.ViewSeconds == viewSeconds), "Seeking must preserve edited OUT/IN points and the shared editor zoom");
                    Check(Get(form, "autoPreload") == successor && !successor.Cancellation.IsCancellationRequested && files.Select(File.GetLastWriteTimeUtc).SequenceEqual(writes) && queue.Next.Id == incoming.Id, "Seeking must preserve the warmed successor and leave all prepared audio files untouched");
                    long expected = (long)Math.Round(plan.Timing.ElapsedAtSource(native.Seconds - plan.SourceStart) * 16000);
                    Check(Math.Abs(plan.Bridge.PositionFrames - expected) <= 1 && playback.Current == native && ((WaveOutEvent)Get(form, "output")).PlaybackState != PlaybackState.Playing, "A paused seek must reposition the existing bridge correctly without starting playback");
                }
                ((MixSlider)Get(form, "volume")).Value = 0; playback.Apply(0, mix);
                ((WaveOutEvent)Get(form, "output")).Play(); Invoke(form, "SeekTo", .2);
                Check(((WaveOutEvent)Get(form, "output")).PlaybackState == PlaybackState.Playing && Get(form, "autoTransition") == plan && plan.CutFrames.SequenceEqual(cuts) && plan.SwapFrames.SequenceEqual(starts), "Seeking a playing song must preserve playback and its edited transition");
                ((WaveOutEvent)Get(form, "output")).Stop(); ((MixSlider)Get(form, "volume")).Value = 70; playback.Apply(.7f, mix);
                double seekElapsed = 3;
                Invoke(form, "SeekTo", plan.SourceStart + plan.Timing.SourceAt(seekElapsed));
                var audio = new float[8000]; playback.Read(audio, 0, audio.Length); Invoke(form, "PollAutoTransition"); Invoke(form, "UpdateTransport");
                Check(playback.Current == bridge && Math.Abs(bridge.Seconds - (seekElapsed + .25)) < .0002 && Get(form, "autoTransition") == plan && plan.CutFrames.SequenceEqual(cuts) && plan.SwapFrames.SequenceEqual(starts), "Seeking into the transition must enter the existing bridge at the requested song time and retain edits");
                Check(ToneAmplitude(audio, 16000, 480) > .035 && ToneAmplitude(audio, 16000, 700) > .035, "The preserved early IN and later OUT must still create an audible overlap after seeking");
                long end = cuts[2] + 1600; var buffer = new float[4096];
                while (bridge.PositionFrames < end) playback.Read(buffer, 0, (int)Math.Min(buffer.Length, (end - bridge.PositionFrames) * 2));
                playback.Read(audio, 0, audio.Length);
                Check(ToneAmplitude(audio, 16000, 480) < .005 && ToneAmplitude(audio, 16000, 700) > .035, "The retained OUT point must cut only the outgoing stem at its edited time");
            }
            finally
            {
                Invoke(form, "StopAutoTimeline"); if (!loading.Task.IsCompleted) loading.SetResult(null);
                if (preparation != null && !preparation.IsCompleted) Finish(preparation);
                Invoke(form, "CloseAudio");
            }
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "sk-auto-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--replays")) { SmoothReplays(root); Console.WriteLine("Auto play musical replays: " + checks + " checks passed."); return 0; }
            if (args.Contains("--markers")) { MarkerEditing(root); Console.WriteLine("Auto transition markers: " + checks + " checks passed."); return 0; }
            if (args.Contains("--seeks")) { Seeking(root); Console.WriteLine("Auto play seeks: " + checks + " checks passed."); return 0; }
            if (args.Contains("--next")) { RequestedTiming(); SkippingTransitions(root); RequestedNext(root); RequestedPreloadedNext(root); RequestedSlowNext(root); RequestedPositionContinuity(root); Console.WriteLine("Auto play Next: " + checks + " checks passed."); return 0; }
            if (args.Contains("--handoff")) { Handoff(root); Console.WriteLine("Auto play handoff: " + checks + " checks passed."); return 0; }
            if (args.Contains("--preloading")) { QueueBuffer(root); SequentialPreloading(root); Preloading(root); MusicalLoops(root); SmoothReplays(root); ShortSongRestarts(root); Console.WriteLine("Auto play preloading: " + checks + " checks passed."); return 0; }
            var settings = new AutoPlaySettings { Below = 7, Above = 12 };
            SongInfo current = new SongInfo { Audio = "current", Bpm = 120, HasStems = true }, lower = new SongInfo { Audio = "lower", Bpm = 113, HasStems = true }, upper = new SongInfo { Audio = "upper", Bpm = 132, HasStems = true };
            var invalid = new[] { new SongInfo { Audio = "too-slow", Bpm = 112, HasStems = true }, new SongInfo { Audio = "too-fast", Bpm = 133, HasStems = true }, new SongInfo { Audio = "unfinished", Bpm = 120 }, new SongInfo { Audio = "unknown", HasStems = true } };
            var shuffle = new AutoShuffle(new Random(2)); SongInfo first = shuffle.Choose(invalid.Concat(new[] { current, lower, upper }), current, 120, settings); shuffle.Played(first);
            SongInfo second = shuffle.Choose(invalid.Concat(new[] { current, lower, upper }), current, 120, settings);
            Check((first == lower || first == upper) && (second == lower || second == upper) && first != second, "Shuffle must include range boundaries and avoid repeats while eligible unplayed songs remain");
            shuffle.Played(second);
            Check(shuffle.Choose(new[] { current, lower, upper }, current, 120, settings) == null, "Shuffle must not recycle played songs when the session's eligible pool is exhausted");
            shuffle.Reset(); Check(shuffle.Choose(new[] { current, lower, upper }, current, 120, settings) != null, "A new shuffle session must allow songs from earlier sessions");
            Check(shuffle.Choose(invalid.Concat(new[] { current }), current, 120, settings) == null, "Shuffle must never choose the current song, unknown BPM, unfinished stems, or out-of-range songs");
            var timing = new AutoTransitionTiming(120, 150, 48000, new AutoPlaySettings { RampBars = 8, StemBars = 4, Order = new[] { 2, 0, 1, 3 } });
            Check(timing.SourceRampSeconds == 16 && timing.IntervalSeconds == 6.4 && timing.Order.SequenceEqual(new[] { 2, 0, 1, 3 }), "Bars must mean four beats, using the current tempo for the ramp and the next tempo for stem spacing");
            Check(timing.BpmAt(0) == 120 && timing.BpmAt(timing.RampSeconds) == 150 && timing.SwapFrame(0) == timing.RampFrames && timing.SwapFrame(3) < timing.TotalFrames, "The tempo ramp must finish before any incoming stem is scheduled");
            bool rejected = false; try { new AutoPlaySettings { Order = new[] { 0, 0, 2, 3 } }.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Stem order settings must contain each stem exactly once");
            Check(AutoPlaySettings.Read(new Dictionary<string, object> { { "stemBars", -1 } }).StemBars == 4, "Invalid saved settings must fall back to usable defaults");
            var cutSettings = new AutoPlaySettings { RampBars = 8, StemBars = 4, EndCutBars = 4 };
            var cutTiming = new AutoTransitionTiming(120, 150, 48000, cutSettings);
            Check(Math.Abs(timing.SourceStart(180, .12) - cutTiming.SourceStart(180, .12) - 8) < .00001 && cutTiming.EndCutSeconds == 8, "Four end-cut bars at 120 BPM must move the transition earlier by eight seconds");
            Check(cutTiming.RampFrames == timing.RampFrames && cutTiming.IntervalFrames == timing.IntervalFrames && cutTiming.TotalFrames == timing.TotalFrames, "Cutting the ending must preserve tempo-ramp and stem-handoff durations");
            Check(new AutoTransitionTiming(60, 150, 48000, cutSettings).EndCutSeconds == 16, "The end cut must use outgoing playback BPM rather than incoming BPM");
            Check(cutSettings.Copy().EndCutBars == 4, "Copied transition settings must retain the end cut");
            var legacy = AutoPlaySettings.Read(new Dictionary<string, object> { { "below", 7 }, { "rampBars", 2 } });
            Check(legacy.EndCutBars == 0 && legacy.Below == 7 && legacy.RampBars == 2, "Existing saved preferences must default to no end cut without resetting other settings");
            Check(!legacy.EarlyStemTransitions && !new AutoPlaySettings().EarlyStemTransitions, "Existing and new preferences must default to the original fixed transition schedule");
            Check(new AutoPlaySettings { EarlyStemTransitions = true }.Copy().EarlyStemTransitions, "Preparation's settings snapshot must retain the early-transition toggle");
            foreach (double invalidCut in new[] { -1.0, 65, Double.NaN, Double.PositiveInfinity })
            {
                rejected = false; try { new AutoPlaySettings { EndCutBars = invalidCut }.Validate(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid end-cut settings must be rejected");
            }
            var shortTiming = new AutoTransitionTiming(120, 120, 48000, new AutoPlaySettings { EndCutBars = 64 });
            Check(shortTiming.SourceStart(3, 0) == 0 && shortTiming.SourceEnd(3) == 2 && shortTiming.SourceEnd(1) == 1, "An oversized end cut must retain one playable bar, or the entire track if it is shorter");
            Handoff(root); SkippingTransitions(root); EquivalentTempos(); EquivalentTransitions(root); Dynamics(root); PeakBoundaries(root); Rendering(root); Transition(root); EarlyTransitions(root); PhraseTransitions(root); QueueBuffer(root); SequentialPreloading(root); Preloading(root); MusicalLoops(root); SmoothReplays(root); ShortSongRestarts(root); RequestedTiming(); RequestedNext(root); RequestedPreloadedNext(root); RequestedSlowNext(root); RequestedPositionContinuity(root); ReplayQueue(root); MarkerEditing(root);
            Console.WriteLine("Auto play: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
