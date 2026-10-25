using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;

internal static class PlayerTransitionScrollTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Rate = 4000, Seconds = 240;
    private static int checks;
    private static int scrollMilliseconds = 2500;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Get(object source, string name) { return source.GetType().GetField(name, Private).GetValue(source); }
    private static void Set(object source, string name, object value) { source.GetType().GetField(name, Private).SetValue(source, value); }
    private static object Invoke(object source, string name, params object[] arguments) { return source.GetType().GetMethod(name, Private).Invoke(source, arguments); }
    private static AutoReplayAnalysis Analysis()
    {
        var lanes = new AutoAudioDynamics[4];
        for (int lane = 0; lane < 4; lane++)
        {
            double resolution = lane == 1 ? .01 : .1;
            var levels = new double[(int)(Seconds / resolution)];
            var tones = lane >= 2 ? new float[levels.Length][] : null;
            for (int i = 0; i < levels.Length; i++)
            {
                double at = i * resolution;
                levels[i] = lane == 0 && at % 8 > 6.8 ? .0001 : .03 + .3 * Math.Exp(-(at % .5) * 18) + .02 * Math.Sin(at * 5 + lane);
                if (tones != null) { tones[i] = new float[12]; for (int note = 0; note < 12; note++) tones[i][note] = (float)Math.Abs(Math.Sin(at / 4 + note)); }
            }
            lanes[lane] = (AutoAudioDynamics)Activator.CreateInstance(typeof(AutoAudioDynamics), Private, null, new object[] { levels, resolution, tones }, null);
        }
        return (AutoReplayAnalysis)Activator.CreateInstance(typeof(AutoReplayAnalysis), Private, null, new object[] { lanes }, null);
    }
    private static void RepeatedPlanning(bool profile)
    {
        var analysis = Analysis();
        var clock = Stopwatch.StartNew();
        var first = analysis.Plan(120, .08, Rate, Seconds * Rate, 18 * Rate, 200);
        clock.Stop(); Console.WriteLine("First replay-boundary search: " + clock.ElapsedMilliseconds + " ms");
        clock.Restart(); AutoLoopTiming repeated = null;
        for (int tick = 0; tick < 10; tick++) repeated = analysis.Plan(120, .08, Rate, Seconds * Rate, 18 * Rate, 200);
        clock.Stop(); Console.WriteLine("Ten unchanged autoplay planning ticks: " + clock.ElapsedMilliseconds + " ms");
        if (profile) return;
        Check(Object.ReferenceEquals(first, repeated), "Unchanged incoming protection must reuse its musical replay plan instead of searching on every UI tick");
        Check(clock.ElapsedMilliseconds < 50, "Repeated replay planning must leave the UI thread available for scrolling");
        var moved = analysis.Plan(120, .08, Rate, Seconds * Rate, 19 * Rate, 200);
        Check(!Object.ReferenceEquals(first, moved), "Seeking must select a plan for the new position");
        Check(Object.ReferenceEquals(first, analysis.Plan(120, .08, Rate, Seconds * Rate, 18 * Rate, 200)), "Returning to a recent position must retain the same plan");
        Check(!Object.ReferenceEquals(first, analysis.Plan(120, .08, Rate, Seconds * Rate, 18 * Rate, 200, true)), "Late preparation must keep its distinct early-replay search");
        Check(!Object.ReferenceEquals(first, analysis.Plan(122, .08, Rate, Seconds * Rate, 18 * Rate, 200)), "Changing BPM must recompute musical boundaries");
        Check(!Object.ReferenceEquals(first, analysis.Plan(120, .12, Rate, Seconds * Rate, 18 * Rate, 200)), "Changing beat phase must recompute musical boundaries");
        Check(!Object.ReferenceEquals(first, analysis.Plan(120, .08, Rate, Seconds * Rate - Rate, 18 * Rate, 200)), "Changing the end cut must recompute musical boundaries");
        Check(!Object.ReferenceEquals(first, analysis.Plan(120, .08, Rate, Seconds * Rate, 18 * Rate, 190)), "A new successor transition time must recompute musical boundaries");
        Check(!Object.ReferenceEquals(first, analysis.Plan(120, .08, Rate * 2, Seconds * Rate * 2, 18 * Rate * 2, 200)), "A new output sample rate must recompute frame boundaries");
        for (int position = 20; position < 32; position++) analysis.Plan(120, .08, Rate, Seconds * Rate, position * Rate, 200);
        Check(((System.Collections.ICollection)Get(analysis, "plans")).Count <= 8, "Seeking through a long session must keep replay results bounded");
        var expected = (AutoLoopTiming)Invoke(analysis, "BuildPlan", 120.0, .08, Rate, (long)(Seconds * Rate), (long)(18 * Rate), 200.0, false);
        var actual = analysis.Plan(120, .08, Rate, Seconds * Rate, 18 * Rate, 200);
        Check(actual.StartFrame == expected.StartFrame && actual.EndFrame == expected.EndFrame && actual.VocalInFrame == expected.VocalInFrame && actual.VocalOutFrame == expected.VocalOutFrame, "Reusing plans must preserve the original musical splice and vocal phrase choices");
    }
    private static void TransitionPolling(string root, bool requested, bool profile)
    {
        string audio = Path.Combine(root, "audio.wav");
        using (var form = new PlayerForm(root, root))
        using (var reference = new StemMixer(audio, ""))
        using (var bridge = new StemMixer(audio, ""))
        using (var next = new StemMixer(audio, ""))
        using (var playback = new PlayerPlayback(reference))
        {
            var currentSong = new SongInfo { Audio = audio, Folder = root, Cover = audio + ".jpg", Title = "Current", Artist = "Test", Bpm = 120 };
            var incomingSong = new SongInfo { Audio = audio + ".next", Folder = root, Cover = audio + ".jpg", Title = "Incoming", Artist = "Test", Bpm = 120, HasStems = true };
            var successor = new SongInfo { Audio = audio + ".following", Folder = root, Cover = audio + ".jpg", Title = "Following", Artist = "Test", Bpm = 120, HasStems = false };
            var queue = (PlaybackQueue)Get(form, "songQueue"); queue.Follow(currentSong.Audio);
            var incomingEntry = queue.Add(incomingSong.Audio, 1); queue.Add(successor.Audio, 2);
            next.ReplayAnalysis = Analysis(); next.Seek(18);
            var timing = new AutoTransitionTiming(120, 120, Rate, new AutoPlaySettings());
            var plan = new PreparedAutoTransition { Reference = reference, Bridge = bridge, Next = next, Requested = requested,
                Song = incomingSong, QueueEntryId = incomingEntry.Id, Traits = new MusicTraits { Bpm = 120, BeatOffset = .08 }, Timing = timing,
                OutgoingFiles = Enumerable.Repeat(audio, 4).ToArray(), NextFiles = Enumerable.Repeat(audio, 4).ToArray(),
                OutgoingTiming = Enumerable.Range(0, 4).Select(i => new StemTiming()).ToArray(),
                OutgoingPeaks = Enumerable.Range(0, 4).Select(i => new float[1600]).ToArray(), SwapFrames = Enumerable.Range(0, 4).Select(i => timing.SwapFrame(i)).ToArray() };
            plan.Bank = new StemSourceBank { Song = incomingSong, Traits = plan.Traits, Plan = new StemMatchPlan { Tempo = 1, MatchedBpm = 120 }, Files = plan.NextFiles, Peaks = plan.OutgoingPeaks };
            Check(playback.Schedule(reference, requested ? 0 : Rate * 60, bridge, next), "Fixture must schedule a real automatic or requested handoff");
            Set(form, "current", currentSong); Set(form, "mixer", reference); Set(form, "playback", playback); Set(form, "autoTransition", plan);
            Set(form, "autoPlayEnabled", true); Set(form, "songs", new List<SongInfo> { currentSong, incomingSong, successor });
            try
            {
                if (!profile)
                {
                    reference.Seek(requested ? 0 : 60); playback.Read(new float[2], 0, 2);
                    Check(playback.Current == bridge, "Fixture must enter the actual transition bridge");
                    Invoke(form, "PollAutoTransition");
                    Check(plan.Started && Object.ReferenceEquals(Get(form, "mixer"), bridge), "The UI must observe the same transition-start branch as a real handoff");
                }
                successor.HasStems = true;
                Invoke(form, "RefreshAutoRestart", false);
                var loop = (AutoLoopTiming)Get(playback, "incomingLoop");
                var clock = Stopwatch.StartNew();
                for (int tick = 0; tick < 10; tick++) Invoke(form, "RefreshAutoRestart", false);
                clock.Stop(); Console.WriteLine((requested ? "Next button" : "Automatic transition") + " protection, ten ticks: " + clock.ElapsedMilliseconds + " ms");
                if (profile) return;
                Check(Object.ReferenceEquals(loop, Get(playback, "incomingLoop")), "Both Next and automatic handoffs must retain their prepared replay boundary between ticks");
                Check(clock.ElapsedMilliseconds < 50, "Prepared transition polling must not repeatedly stall the UI thread");
                using (var host = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(400, 300) })
                using (var list = new QueueList { Dock = DockStyle.Fill })
                using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
                {
                    for (int i = 0; i < 60; i++) list.Items.Add(new QueueRow { Entry = new PlaybackQueueEntry { Audio = "queue-" + i }, Song = new SongInfo { Title = "Song " + i, Artist = "Test" } });
                    host.Controls.Add(list); host.Show(); list.ScrollBy(1800);
                    int frames = 0, ticks = 0; long last = 0, maximum = 0;
                    clock.Restart();
                    list.Invalidated += delegate { long now = clock.ElapsedMilliseconds; if (last != 0) maximum = Math.Max(maximum, now - last); last = now; frames++; };
                    timer.Tick += delegate { Invoke(form, "PollAutoTransition"); Invoke(form, "RefreshAutoRestart", false); Invoke(form, "UpdateTransport"); ticks++; }; timer.Start();
                    while (clock.ElapsedMilliseconds < scrollMilliseconds)
                    {
                        if (Math.Abs(list.ScrollTarget - list.ScrollOffset) < 1) list.ScrollBy(list.ScrollOffset > 900 ? -1700 : 1700);
                        Application.DoEvents(); Thread.Sleep(1);
                    }
                    timer.Stop();
                    Console.WriteLine((requested ? "Next button" : "Automatic transition") + " scrolling: " + frames + " frames, " + ticks + " protection ticks, maximum gap " + maximum + " ms");
                    Check(frames > 60 && ticks >= 15 && maximum < 120, "Scrolling must continue while the complete incoming-song replay analysis is active");
                }
            }
            finally { Set(form, "autoTransition", null); Set(form, "mixer", null); Set(form, "playback", null); }
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "djstem-transition-scroll-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var writer = new WaveFileWriter(Path.Combine(root, "audio.wav"), new WaveFormat(Rate, 16, 2))) { var data = new byte[Rate * 4]; for (int i = 0; i < Seconds; i++) writer.Write(data, 0, data.Length); }
            if (args.Contains("--soak")) scrollMilliseconds = 30000;
            bool profile = args.Contains("--profile"); RepeatedPlanning(profile); TransitionPolling(root, false, profile); TransitionPolling(root, true, profile);
            Console.WriteLine("Transition scrolling: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
