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

internal static class PlayerGestureTests
{
    private static int checks;
    private static string previewFile;
    private const int Rate = 16000;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Get(object source, string field) { return source.GetType().GetField(field, Private).GetValue(source); }
    private static void Set(object source, string field, object value) { source.GetType().GetField(field, Private).SetValue(source, value); }
    private static object Invoke(object source, string method, params object[] args) { return source.GetType().GetMethod(method, Private).Invoke(source, args); }
    private sealed class PreviewHost : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    private static void SavePreview(Control control, string file)
    {
        Control parent = control.Parent; Rectangle bounds = control.Bounds; bool visible = control.Visible;
        using (var host = new PreviewHost { ShowInTaskbar = false, Opacity = 0, FormBorderStyle = FormBorderStyle.None, ClientSize = control.Size })
        {
            host.Controls.Add(control); control.Location = Point.Empty; control.Visible = true; host.Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(file); }
            host.Controls.Remove(control); parent.Controls.Add(control); control.Bounds = bounds; control.Visible = visible;
        }
    }
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static void Finish(Task task)
    {
        var clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.ElapsedMilliseconds < 50000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Preparation must complete without blocking UI messages"); task.GetAwaiter().GetResult();
    }
    private static void Mouse(WaveformView view, string method, MouseButtons button, int x, int y = 35) { Invoke(view, method, new MouseEventArgs(button, 1, x, y, 0)); }
    private static void Wave(string file, float level, double seconds = 12)
    {
        using (var writer = new WaveFileWriter(file, new WaveFormat(Rate, 24, 2)))
            for (int frame = 0; frame < Rate * seconds; frame++) { float value = level + frame / (float)Rate * .01f; writer.WriteSample(value); writer.WriteSample(value); }
    }
    private static SongInfo Song(string root, string name)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder); string audio = Path.Combine(folder, "song.wav"); Wave(audio, .1f);
        for (int lane = 0; lane < 4; lane++) Wave(Path.Combine(folder, StemMixer.StemNames[lane] + ".wav"), .1f + lane * .02f);
        File.WriteAllText(Path.Combine(folder, "metadata.json"), new JavaScriptSerializer().Serialize(new { bpm = 120, key = "A minor", analysis = new { version = 2, source = "mp3", beatOffset = 0, beatConfidence = .9, keyConfidence = .9 } }));
        return new SongInfo { Audio = audio, Folder = folder, HasStems = true, Bpm = 120, Artist = "Artist", Title = name, Cover = Path.Combine(folder, "missing.jpg") };
    }
    private static float[] Read(StemMixer mixer, double seconds)
    {
        var samples = new float[(int)Math.Round(seconds * Rate) * 2]; Check(mixer.Read(samples, 0, samples.Length) == samples.Length, "A lane edit must preserve the global sample count"); return samples;
    }
    private static float At(float[] samples, double seconds) { return samples[(int)Math.Round(seconds * Rate) * 2]; }
    private static void Near(float actual, double expected, string message) { Check(Math.Abs(actual - expected) < .00003, message + " (" + actual + " / " + expected + ")"); }
    private static void Grid()
    {
        Check(StemTiming.SnapShift(1.12, 120) == 1 && StemTiming.SnapShift(-1.12, 120) == -1, "Drag directions must snap symmetrically to whole beats");
        Check(StemTiming.SnapShift(.25, 120) == .5 && StemTiming.SnapShift(-.25, 120) == -.5, "Halfway releases must snap deterministically");
        var timing = new StemTiming { Shift = .5 }; timing.StartLoop(4.1, .9, 120, .2, 12);
        Check(Math.Abs(timing.LoopStart - 4.2) < .00001 && Math.Abs(timing.LoopEnd - 6.2) < .00001 && Math.Abs(timing.LoopAnchor - 2.2) < .00001, "Loop endpoints and entry must honor analyzed beat offset");
        long remaining; Check(timing.SourceFrame(16000, Rate, out remaining) == 8000, "The pending loop must retain the previous stem shift until its downbeat");
        Check(timing.SourceFrame(35200, Rate, out remaining) == 67200, "Loop entry must begin at the chosen source bar");
        timing.ExtendLoop(10.1, 120, 12); Check(Math.Abs(timing.LoopEnd - 10.2) < .00001, "Loop handles must resize by whole bars");
        timing.ExtendLoop(100, 120, 12); Check(timing.LoopEnd <= 12 && Math.Abs(timing.LoopEnd - 10.2) < .00001, "Loop ends must stay within complete available bars");
        timing.ExtendLoop(-100, 120, 12); Check(Math.Abs(timing.LoopEnd - 6.2) < .00001, "Dragging backwards must retain at least one bar");
        Check(Math.Abs(StemTiming.BarStart(11.9, 120, .2, 12) - 8.2) < .00001 && Math.Abs(StemTiming.BarStart(0, 120, .2, 12) - .2) < .00001, "Clicks near song edges must select a complete first or last bar");
        bool rejected = false; try { StemTiming.BarStart(0, 120, 0, 1); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A song shorter than a bar must refuse an invalid loop");
        StemTiming scaled = timing.Copy(.8); Check(scaled.LoopStart == timing.LoopStart * .8 && scaled.Shift == timing.Shift * .8 && scaled.LoopAnchor == timing.LoopAnchor * .8 && scaled.LoopShift == timing.LoopShift * .8, "Tempo conversion must scale every source-time coordinate");
    }
    private static void Audio(SongInfo song)
    {
        using (var mixer = new StemMixer(song.Audio, song.Folder))
        {
            var mix = new PlayerMix(); mix.Solo[0] = true; mixer.Volume = 1; mixer.ApplyMix(mix); mixer.Seek(4);
            mixer.SetStemShift(0, .5); var samples = Read(mixer, .1);
            Near(At(samples, .05), .1355, "Dragging right must delay only the selected source");
            Check(mixer.PositionFrames == (long)(4.1 * Rate), "The transport must keep its position during a stem drag");
            mixer.SetStemShift(0, -.5); samples = Read(mixer, .1); Near(At(samples, .05), .1465, "Dragging left must advance the source");
            mix.Solo[0] = false; mix.Solo[1] = true; mixer.ApplyMix(mix); mixer.Seek(4.25); samples = Read(mixer, .1);
            Near(At(samples, .05), .163, "Other stems must retain their original source time");
            mix.Solo[1] = false; mix.Solo[0] = true; mixer.ApplyMix(mix); mixer.SetStemShift(0, 0); mixer.Seek(4.25);
            mixer.StartStemLoop(0, 4.1, 120, 0); StemTiming state = mixer.StemState(0);
            Check(state.LoopStart == 4 && state.LoopEnd == 6 && state.LoopAnchor == 6, "A live loop must wait for the next downbeat");
            samples = Read(mixer, 6);
            Near(At(samples, .5), .1475, "The source must continue normally before loop entry");
            Near(At(samples, 2), .1425, "After entry the selected bar must start from its source location");
            Near(At(samples, 4), .1425, "The loop must wrap accurately within a single audio callback");
            Check(mixer.Seconds == 10.25, "Repeating source bars must never rewind the song transport");
            mixer.Seek(8.25); samples = Read(mixer, .1); Near(At(samples, .05), .143, "Seeking must preserve a running loop at the proper phase");
            mixer.ExtendStemLoop(0, 8.2, 120); Check(mixer.StemState(0).LoopEnd == 8, "Audio resizing must select complete bars");
            mixer.Seek(8.25); samples = Read(mixer, .1); Near(At(samples, .05), .163, "A longer loop must read the newly included bars");
            mix.Solo[0] = false; mix.Solo[1] = true; mixer.ApplyMix(mix); mixer.Seek(8.25); samples = Read(mixer, .1); Near(At(samples, .05), .203, "Unlooped stems must continue through the loop on the shared song time");
            mix.Solo[1] = false; mix.Solo[0] = true; mixer.ApplyMix(mix); mixer.SetStemShift(0, .5); mixer.Seek(8.25); samples = Read(mixer, .1); Near(At(samples, .05), .158, "A shifted loop must retain whole-beat phase relative to the other stems");
            mixer.Seek(11.9); var end = new float[Rate * 2]; Check(mixer.Read(end, 0, end.Length) == 3200 && mixer.Read(end, 0, end.Length) == 0, "Loops must stop exactly at the global song ending");
        }
        using (var mixer = new StemMixer(song.Audio, song.Folder))
        {
            var mix = new PlayerMix(); mix.Solo[0] = true; mixer.Volume = 1; mixer.ApplyMix(mix); mixer.SetStemShift(0, 2); mixer.Seek(0);
            Check(Read(mixer, 1).All(sample => sample == 0), "Positive shifts before source zero must pad with silence rather than restart the source early");
            mixer.Seek(2.1); Near(At(Read(mixer, .1), .05), .1015, "Delayed audio must begin at its original source position");
            mixer.SetStemShift(0, -2); mixer.Seek(11); Check(Read(mixer, .5).All(sample => sample == 0), "Negative shifts past source EOF must not shorten the other stems' transport");
        }
    }
    private static void Gestures(string root, SongInfo song, SongInfo next)
    {
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var mixer = new StemMixer(song.Audio, song.Folder); mixer.Seek(.25);
            Set(form, "current", song); Set(form, "mixer", mixer); Set(form, "stemSources", new StemSourceState(song.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", mixer.TotalFrames); Set(form, "nativeRate", Rate);
            Set(form, "songs", new List<SongInfo> { song, next }); Invoke(form, "SetEnabled", true);
            var view = ((WaveformView[])Get(form, "waveforms"))[0]; view.Size = new Size(500, 70); view.Duration = 12; view.Peaks = Enumerable.Repeat(.5f, 1600).ToArray(); IntPtr viewHandle = view.Handle;
            Invoke(form, "UpdateTransport");
            Mouse(view, "OnMouseDown", MouseButtons.Right, 200); Pump(150); Mouse(view, "OnMouseUp", MouseButtons.Right, 200);
            Check(!mixer.StemState(0).Looping && mixer.StemState(0).Shift == 0 && mixer.Seconds == .25, "A short right click must neither loop nor seek");
            Mouse(view, "OnMouseDown", MouseButtons.Right, 200); Mouse(view, "OnMouseMove", MouseButtons.Right, 247);
            Check(Math.Abs(mixer.StemState(0).Shift - 47 * 12.0 / 499) < .00001, "Right drag must preview only this stem's continuous offset");
            Pump(1600); Check(!mixer.StemState(0).Looping, "Moving must cancel the long-hold timer");
            Mouse(view, "OnMouseUp", MouseButtons.Right, 247); Check(mixer.StemState(0).Shift == 1 && !view.Gesturing, "Release must snap the preview to whole beats and release mouse capture");
            Mouse(view, "OnMouseDown", MouseButtons.Right, 208); Pump(1400); Check(!mixer.StemState(0).Looping, "Long hold must wait for the full 1.5 seconds");
            Pump(180); Check(mixer.StemState(0).LoopStart == 4 && mixer.StemState(0).LoopEnd == 6 && mixer.StemState(0).LoopAnchor == 2, "A stationary 1.5-second right press must loop the nearest logical source bar");
            Mouse(view, "OnMouseMove", MouseButtons.Right, 291); Mouse(view, "OnMouseUp", MouseButtons.Right, 291);
            Check(mixer.StemState(0).LoopEnd == 8, "Continuing to drag after the hold must extend the loop in bars");
            int edge = (int)Math.Round(9.0 / 12 * 499);
            Mouse(view, "OnMouseDown", MouseButtons.Left, edge); Mouse(view, "OnMouseMove", MouseButtons.Left, edge + 83); Mouse(view, "OnMouseUp", MouseButtons.Left, edge + 83);
            Check(mixer.StemState(0).LoopEnd == 10 && mixer.Seconds == .25, "The highlighted loop end must resize with left drag without seeking the transport");
            if (previewFile != null) using (var bitmap = new Bitmap(view.Width, view.Height)) { view.DrawToBitmap(bitmap, view.ClientRectangle); bitmap.Save(previewFile); }
            Mouse(view, "OnMouseDown", MouseButtons.Right, 200); Mouse(view, "OnMouseMove", MouseButtons.Right, 224); view.Capture = false;
            Check(mixer.StemState(0).Shift == 1.5 && !view.Gesturing, "Losing capture must finish and quantize a drag");
            StemTiming state = mixer.StemState(0); Mouse(view, "OnMouseDown", MouseButtons.Right, 50); view.Enabled = false; Pump(1600);
            Check(!view.Gesturing && mixer.StemState(0).LoopStart == state.LoopStart, "Disabled waveforms must cancel a pending hold"); view.Enabled = true;
            Set(form, "currentNativeTraits", null); Mouse(view, "OnMouseDown", MouseButtons.Right, 100);
            Check(!view.Gesturing && ((Label)Get(form, "message")).Text.Contains("analysis"), "Unknown beat phase must wait for analysis rather than invent an alignment");
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true });
            double keyboardSeek = -1; view.Seek += at => keyboardSeek = at;
            mixer.Seek(8.25); Invoke(form, "UpdateTransport"); Invoke(view, "OnKeyDown", new KeyEventArgs(Keys.Right));
            Check(keyboardSeek == 12 && view.Progress == 8.25 / 12 && view.CursorProgress != view.Progress, "Keyboard seeking and accessibility must use transport time while the loop cursor follows its source");
            mixer.Seek(.25);
            Finish((Task)Invoke(form, "ApplyTuning", (double?)150, new Dictionary<string, int>())); mixer = (StemMixer)Get(form, "mixer");
            Check(Math.Abs(mixer.StemState(0).Shift - 1.2) < .00001 && Math.Abs(mixer.StemState(0).LoopStart - 3.2) < .00001 && Math.Abs(mixer.StemState(0).LoopEnd - 8) < .00001, "Tempo changes must retain loop bars and snapped beat offsets");
            Invoke(form, "CreateOutput"); // This fixture installed its mixer without opening a device.
            ((MixSlider)Get(form, "volume")).Value = 0; Invoke(form, "SetAutoPlay", true); ((WaveOutEvent)Get(form, "output")).Pause();
            Check(Enumerable.Range(0, 4).All(lane => !mixer.StemState(lane).Changed) && !view.TimingEnabled, "Enabling Auto play must reset every offset and loop and disable timing gestures");
            Check(((Label[])Get(form, "stemLoopLabels")).All(label => label.Text == "") && ((Label[])Get(form, "stemOffsetLabels")).All(label => label.Text == ""), "Auto play must clear the waveform timing labels immediately");
            Set(form, "autoSettings", new AutoPlaySettings { Below = 40, RampBars = 1, StemBars = .25, EarlyStemTransitions = true });
            Finish((Task)Invoke(form, "PrepareAutoTransition")); object prepared = Get(form, "autoTransition"); Check(prepared != null, "Autoplay must prepare after clearing manual stem edits: " + ((Label)Get(form, "message")).Text);
            Mouse(view, "OnMouseDown", MouseButtons.Right, 50);
            Mouse(view, "OnMouseMove", MouseButtons.Right, 100); Pump(1600);
            Check(Get(form, "autoTransition") == prepared && !view.Gesturing && !mixer.StemState(0).Changed, "Right drag and long hold must remain disabled for all of Auto play and leave its prepared transition intact");
            Mouse(view, "OnMouseUp", MouseButtons.Right, 50);
            Invoke(form, "SetAutoPlay", false);
            Check(view.TimingEnabled && Get(form, "autoTransition") == null, "Turning Auto play off must restore timing edits and cancel its pending transition");
            Mouse(view, "OnMouseDown", MouseButtons.Right, 100); Mouse(view, "OnMouseMove", MouseButtons.Right, 150); Mouse(view, "OnMouseUp", MouseButtons.Right, 150);
            Check(mixer.StemState(0).Shift != 0, "Stem offsets must work again after Auto play is disabled");
            Mouse(view, "OnMouseDown", MouseButtons.Right, 50);
            Check(view.Gesturing, "A manual hold must be active before enabling Auto play");
            WaveOutEvent pausedOutput = (WaveOutEvent)Get(form, "output"); Set(form, "output", null);
            long before = mixer.PositionFrames; Invoke(form, "SetAutoPlay", true); Set(form, "output", pausedOutput); Pump(1600);
            Check(!view.Gesturing && !mixer.StemState(0).Changed && mixer.PositionFrames == before, "Enabling Auto play during a hold must cancel capture and its timer without changing the transport");
            Finish((Task)Invoke(form, "LoadSong", next, false, 0.0, false)); mixer = (StemMixer)Get(form, "mixer");
            Check(Enumerable.Range(0, 4).All(lane => !mixer.StemState(lane).Changed), "Loading another song must clear every loop and timing offset");
        }
    }
    private static void OffsetIsolation(string root, SongInfo song)
    {
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var mixer = new StemMixer(song.Audio, song.Folder); mixer.Volume = 1;
            Set(form, "current", song); Set(form, "mixer", mixer); Set(form, "stemSources", new StemSourceState(song.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Invoke(form, "SetEnabled", true);
            var views = (WaveformView[])Get(form, "waveforms"); var mix = (PlayerMix)Get(form, "mix");
            for (int lane = 0; lane < 4; lane++)
            {
                views[lane].Size = new Size(500, 70); views[lane].Duration = 12; views[lane].Peaks = Enumerable.Repeat(.5f, 1600).ToArray(); IntPtr viewHandle = views[lane].Handle;
            }
            for (int lane = 0; lane < 4; lane++) foreach (int direction in new[] { 1, -1 })
            {
                for (int other = 0; other < 4; other++) { mixer.SetStemShift(other, 0); mix.Muted[other] = mix.Solo[other] = false; mix.Levels[other] = 1; }
                mixer.ApplyMix(mix); mixer.OriginalMode = true; mixer.Seek(4); Invoke(form, "UpdateTransport"); long before = mixer.PositionFrames;
                Mouse(views[lane], "OnMouseDown", MouseButtons.Right, 200); Mouse(views[lane], "OnMouseMove", MouseButtons.Right, 200 + direction * 47); Mouse(views[lane], "OnMouseUp", MouseButtons.Right, 200 + direction * 47);
                Check(mixer.StemState(lane).Shift == direction, "Releasing each lane's drag must retain its own nonzero beat-aligned offset");
                Check(Enumerable.Range(0, 4).Where(other => other != lane).All(other => mixer.StemState(other).Shift == 0), "Dragging a lane must leave the other three source offsets unchanged");
                Check(mixer.PositionFrames == before && !mixer.OriginalMode, "Right drag must select the stem mix without seeking the shared transport");
                Near(At(Read(mixer, .1), .05), .52 + 4.05 * .04 - direction * .01, "The complete audible mix must include the selected stem's retained lead or delay");
                mix.Muted[lane] = true; mixer.ApplyMix(mix);
                Near(At(Read(mixer, .1), .05), .52 - (.1 + lane * .02) + 4.15 * .03, "Muting the dragged stem must reveal the unchanged source time of all other stems");
                for (int other = 0; other < 4; other++) mix.Muted[other] = other != lane; mixer.ApplyMix(mix);
                Near(At(Read(mixer, .1), .05), .1 + lane * .02 + (4.25 - direction) * .01, "The grabbed stem alone must play at its new source time after release");
                Check(((Label)Get(form, "message")).Text.Contains(direction > 0 ? "2 beats later" : "2 beats earlier"), "The release feedback must describe the retained offset relative to the other stems");
                Mouse(views[lane], "OnMouseDown", MouseButtons.Right, 200); Mouse(views[lane], "OnMouseMove", MouseButtons.Right, 200 + direction * 47); Mouse(views[lane], "OnMouseUp", MouseButtons.Right, 200 + direction * 47);
                Check(mixer.StemState(lane).Shift == direction * 2, "A second drag must add to the selected stem's existing offset");
            }
        }
    }
    private static float SourceSample(string file, double seconds, PlaybackTransform transform = null, double readOffset = 0)
    {
        using (var reader = PlaybackTransform.OpenFile(file, transform))
        {
            reader.Position = (long)Math.Round(seconds * reader.WaveFormat.SampleRate) * reader.WaveFormat.BlockAlign;
            var sample = new float[((int)Math.Round(readOffset * reader.WaveFormat.SampleRate) + 1) * 2];
            Check(StemMixer.Samples(reader).Read(sample, 0, sample.Length) == sample.Length, "The retained donor tail must contain readable samples"); return sample[sample.Length - 2];
        }
    }
    private static void LongerDonors(string root, SongInfo song)
    {
        SongInfo donor = Song(root, "Long donor"); Wave(donor.Audio, .4f, 36);
        for (int lane = 0; lane < 4; lane++) Wave(Path.Combine(donor.Folder, StemMixer.StemNames[lane] + ".wav"), .4f + lane * .02f, 36);
        foreach (double tempo in new[] { .75, 1.25 }) foreach (double offset in new[] { -.125, .125 })
        {
            var plan = new StemMatchPlan { Tempo = tempo, Offset = offset };
            var rendering = PlayerMatching.Prepare(root, donor, 0, plan, Rate, Rate * 12, CancellationToken.None); Finish(rendering);
            using (var reader = new WaveFileReader(rendering.Result))
                Check(reader.Length / reader.WaveFormat.BlockAlign >= (long)Math.Ceiling(36 / tempo * Rate) + (long)Math.Round(offset * Rate), "Matched donor audio must retain its full tail after speeding up, slowing down, and either beat-alignment offset");
        }
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var mixer = new StemMixer(song.Audio, song.Folder);
            Set(form, "current", song); Set(form, "mixer", mixer); Set(form, "stemSources", new StemSourceState(song.Folder));
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "nativeFrames", mixer.TotalFrames); Set(form, "nativeRate", Rate);
            Set(form, "songs", new List<SongInfo> { song, donor }); Invoke(form, "SetEnabled", true);
            var view = ((WaveformView[])Get(form, "waveforms"))[0]; view.Size = new Size(500, 70); view.Duration = 12; IntPtr viewHandle = view.Handle;
            Finish((Task)Invoke(form, "ReplaceFromDrop", donor, 0)); var sources = (StemSourceState)Get(form, "stemSources");
            Check(sources.Active[0] != null && sources.Active[0].Song == donor && Enumerable.Range(0, 4).All(lane => sources.Available.Files[lane] != null), "Dropping a longer donor must prepare all four full stems and select the dropped lane");
            Check(mixer.Duration == 12 && mixer.StemDuration(0) >= 36 && view.SourceDuration == mixer.StemDuration(0), "A longer replacement must keep its own source duration without extending the shared transport");
            view.Size = new Size(500, 70); view.Duration = mixer.Duration;
            var mix = (PlayerMix)Get(form, "mix"); mix.Solo[0] = true; mixer.ApplyMix(mix); mixer.Volume = 1; mixer.SetLoudness(1);
            Mouse(view, "OnMouseDown", MouseButtons.Right, 250); Mouse(view, "OnMouseMove", MouseButtons.Right, -748); Mouse(view, "OnMouseUp", MouseButtons.Right, -748);
            Check(mixer.StemState(0).Shift == -24 && view.SourceDuration > view.Duration, "Dragging farther than the original song's length must bring the longer donor's later audio onto the timeline");
            mixer.Seek(4); Near(At(Read(mixer, .1), .05), SourceSample(sources.Active[0].Files[0], 28.05), "Advancing the donor must audibly recover samples beyond the original cutoff");
            mixer.SetStemShift(0, 0); mixer.Seek(4); mixer.StartStemLoop(0, 24, 120, 0); mixer.ExtendStemLoop(0, 32, 120);
            Check(mixer.StemState(0).LoopStart == 24 && mixer.StemState(0).LoopEnd == 32, "A shifted donor's loop may use bars beyond the original song's ending");
            mixer.Seek(8.25); Near(At(Read(mixer, .1), .05), SourceSample(sources.Active[0].Files[0], 28.3), "Loop playback and seeking must read the retained tail using the donor's actual frame count");
            mixer.RestoreStemTiming(new[] { new StemTiming(), new StemTiming(), new StemTiming(), new StemTiming() }); mixer.SetStemShift(0, -30); mixer.Seek(8);
            Check(Read(mixer, .1).All(sample => sample == 0), "Advancing past the donor's actual end must still produce silence");
            mix.Solo[0] = false; mix.Solo[1] = true; mixer.ApplyMix(mix); mixer.Seek(4);
            Near(At(Read(mixer, .1), .05), .1605, "A longer donor must leave the other stems on the original timeline");
            mixer.SetStemShift(0, -24); mixer.Seek(4); Finish((Task)Invoke(form, "ApplyTuning", (double?)150, new Dictionary<string, int>()));
            mixer = (StemMixer)Get(form, "mixer"); sources = (StemSourceState)Get(form, "stemSources");
            Check(Math.Abs(mixer.StemDuration(0) - 28.8) < .001 && Math.Abs(mixer.Duration - 9.6) < .001 && Math.Abs(mixer.StemState(0).Shift + 19.2) < .001, "Changing shared BPM must retain the full donor tail and scale its offset independently of the original duration");
            mix.Solo[1] = false; mix.Solo[0] = true; mixer.ApplyMix(mix); mixer.Volume = 1; mixer.SetLoudness(1); mixer.Seek(3.2);
            Near(At(Read(mixer, .1), .05), SourceSample(sources.Active[0].Files[0], 22.4, sources.Active[0].Tuning[0], .05), "The retained tail must remain audible after shared tempo changes");
            Finish((Task)Invoke(form, "ApplyTuning", (double?)150, new Dictionary<string, int> { { donor.Audio, 2 } })); mixer = (StemMixer)Get(form, "mixer");
            Check(Math.Abs(mixer.StemDuration(0) - 28.8) < .001 && Math.Abs(mixer.StemState(0).Shift + 19.2) < .001, "Changing the added song's key must not trim its retained source or discard its timing");
        }
        using (var view = new WaveformView { Size = new Size(500, 70), Duration = 12, SourceDuration = 36, Accent = PlayerTheme.Green, Peaks = new float[1600] })
        using (var before = new Bitmap(500, 70)) using (var after = new Bitmap(500, 70))
        {
            for (int bin = 1240; bin < 1260; bin++) view.Peaks[bin] = 1;
            view.DrawToBitmap(before, view.ClientRectangle); view.ShowTiming(new StemTiming { Shift = -24 }); view.DrawToBitmap(after, view.ClientRectangle);
            Check(before.GetPixel(175, 5).ToArgb() != after.GetPixel(175, 5).ToArgb(), "The waveform must reveal the donor's later peaks when advanced, using the full source length rather than stretching it into the original duration");
        }
    }
    private static void ResetStems(string root, SongInfo song, SongInfo donor)
    {
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var mixer = new StemMixer(song.Audio, song.Folder); mixer.Volume = .5f; mixer.Seek(8.25);
            var sources = new StemSourceState(song.Folder);
            Set(form, "current", song); Set(form, "mixer", mixer); Set(form, "stemSources", sources);
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Invoke(form, "SetEnabled", true);
            var button = (GlassButton)Get(form, "resetStems"); var views = (WaveformView[])Get(form, "waveforms");
            Check(!button.Enabled, "Reset must be disabled when sources and timing are already original");
            for (int scenario = 0; scenario < 3; scenario++)
            {
                if (scenario == 2)
                {
                    var bank = new StemSourceBank { Song = donor, Plan = new StemMatchPlan(), Files = StemMixer.StemNames.Select(name => Path.Combine(donor.Folder, name + ".wav")).ToArray(), Peaks = new float[4][] };
                    for (int lane = 0; lane < 4; lane++) { mixer.ReplaceStem(lane, bank.Files[lane]); sources.Stage(bank, lane); }
                }
                for (int lane = 0; lane < 4; lane++)
                {
                    if (scenario != 1) mixer.SetStemShift(lane, lane % 2 == 0 ? 1 : -.5);
                    if (scenario != 0) mixer.StartStemLoop(lane, 4, 120, 0);
                }
                Invoke(form, "UpdateTransport"); long position = mixer.PositionFrames;
                Check(button.Enabled, "Offsets alone, loops alone, and replacement stems with timing edits must each enable Reset stems");
                Invoke(button, "OnClick", EventArgs.Empty);
                Check(Enumerable.Range(0, 4).All(lane => !mixer.StemState(lane).Changed && !((StemTiming)Get(views[lane], "timing")).Changed), "Reset stems must immediately clear all audio and waveform offsets and loops");
                Check(((Label[])Get(form, "stemLoopLabels")).All(label => label.Text == "") && ((Label[])Get(form, "stemOffsetLabels")).All(label => label.Text == ""), "Reset stems must immediately clear every timing label");
                Check(sources.Available == null && sources.Active.All(bank => bank == null) && !button.Enabled, "Reset stems must restore all original sources and disable itself after clearing edits");
                Check(mixer.PositionFrames == position, "Reset stems must preserve the shared transport position");
                Near(At(Read(mixer, .1), .05), (.52 + (position / (double)Rate + .05) * .04) * .5, "Reset audio must play all four stems aligned to the current transport at the chosen master volume");
            }
        }
    }
    private static void TimingLabels(string root, SongInfo song)
    {
        using (var form = new PlayerForm(root, root))
        using (var native = new StemMixer(song.Audio, song.Folder))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            native.Seek(.25); native.SetStemShift(0, 1); native.StartStemLoop(0, 4, 120, 0); native.ExtendStemLoop(0, 10, 120);
            var bridge = new StemMixer(song.Audio, song.Folder); Set(form, "mixer", bridge); Set(form, "playback", new PlayerPlayback(bridge)); Set(form, "current", song);
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Set(form, "sharedBpm", (double?)132);
            var plan = new PreparedAutoTransition { Reference = native, Bridge = bridge, Started = true, Timing = new AutoTransitionTiming(120, 132, Rate, new AutoPlaySettings()), OutgoingTiming = Enumerable.Range(0, 4).Select(lane => native.StemState(lane)).ToArray() };
            Set(form, "autoTransition", plan); Invoke(form, "UpdateTransport");
            var loops = (Label[])Get(form, "stemLoopLabels"); var offsets = (Label[])Get(form, "stemOffsetLabels"); var faders = (MixSlider[])Get(form, "faders");
            Check(loops[0].Text == "Loop: 3 bars" && offsets[0].Text == "Offset: +2 beats", "Rendered outgoing loops and offsets must stay visible beside volume throughout the tempo ramp");
            Check(Enumerable.Range(1, 3).All(lane => loops[lane].Text == "" && offsets[lane].Text == ""), "Unedited stems must have no timing details");
            form.ClientSize = new Size(1120, 700); Invoke(form, "LayoutContent");
            for (int lane = 0; lane < 4; lane++) Check(loops[lane].Bottom <= faders[lane].Top && offsets[lane].Top >= faders[lane].Bottom && loops[lane].Top >= 0 && offsets[lane].Bottom <= faders[lane].Parent.Height && loops[lane].Left == faders[lane].Left && offsets[lane].Width == faders[lane].Width, "Loop above and offset below the stem volume must fit without overlapping at the minimum lane height");
            string preview = Environment.GetEnvironmentVariable("STEMKIT_STEM_PREVIEW");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                var views = (WaveformView[])Get(form, "waveforms"); views[0].Peaks = Enumerable.Repeat(.5f, 1600).ToArray(); views[0].Duration = 12;
                SavePreview(faders[0].Parent, preview);
            }
            plan.Switched[0] = true; Invoke(form, "UpdateTransport");
            Check(loops[0].Text == "" && offsets[0].Text == "", "Timing details must clear when the incoming stem replaces the rendered loop");
            Set(form, "autoTransition", null); bridge.SetStemShift(2, -.5); Invoke(form, "UpdateTransport");
            Check(loops[2].Text == "" && offsets[2].Text == "Offset: " + (-1.1).ToString("0.#") + " beats", "A non-looped offset must appear independently below its volume control: " + offsets[2].Text);
            bridge.RestoreStemTiming(new[] { new StemTiming(), new StemTiming(), new StemTiming(), new StemTiming() }); Invoke(form, "UpdateTransport");
            Check(offsets.All(label => label.Text == ""), "Cleared timing must remove the details immediately");
        }
    }
    private static KeyEventArgs StemKey(PlayerForm form, Keys key, bool release = true)
    {
        var args = new KeyEventArgs(key); Invoke(form, "OnKeyDown", args);
        if (release) Invoke(form, "OnKeyUp", new KeyEventArgs(key));
        return args;
    }
    private static void KeyboardShortcuts(string root)
    {
        root = Path.Combine(root, "keyboard"); Directory.CreateDirectory(root);
        SongInfo song = Song(root, "Keyboard original"), donor = Song(root, "Keyboard donor");
        for (int lane = 0; lane < 4; lane++) Wave(Path.Combine(donor.Folder, StemMixer.StemNames[lane] + ".wav"), .4f + lane * .02f);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            var mixer = new StemMixer(song.Audio, song.Folder) { Volume = 1 }; var state = new StemSourceState(song.Folder);
            var bank = new StemSourceBank { Song = donor, Traits = new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }, Plan = new StemMatchPlan { Tempo = 1, MatchedBpm = 120 }, Files = StemMixer.StemNames.Select(stem => Path.Combine(donor.Folder, stem + ".wav")).ToArray(), Peaks = Enumerable.Range(0, 4).Select(lane => Enumerable.Repeat(.5f, 1600).ToArray()).ToArray() };
            state.Available = bank;
            Set(form, "current", song); Set(form, "mixer", mixer); Set(form, "stemSources", state); Set(form, "currentNativeTraits", bank.Traits); Invoke(form, "SetEnabled", true);
            form.ActiveControl = ((GlassButton[])Get(form, "mutes"))[0];
            var mix = (PlayerMix)Get(form, "mix"); var sources = (GlassButton[])Get(form, "sourceButtons"); var mutes = (GlassButton[])Get(form, "mutes");
            Keys[] switches = { Keys.Q, Keys.W, Keys.E, Keys.R }, muting = { Keys.A, Keys.S, Keys.D, Keys.F };
            try
            {
                for (int lane = 0; lane < 4; lane++)
                {
                    mix.Solo[lane] = true; Invoke(form, "ApplyMix"); mixer.Seek(1); long before = mixer.PositionFrames;
                    KeyEventArgs key = StemKey(form, switches[lane]);
                    Check(key.Handled && key.SuppressKeyPress && state.Active[lane] == bank && state.Active.Where((active, index) => index != lane).All(active => active == null) && sources[lane].Selected && mixer.PositionFrames == before, "Q/W/E/R must switch only the requested lane through its existing button without moving playback");
                    Near(At(Read(mixer, .15), .1), .4 + lane * .02 + 1.1 * .01, "A source shortcut must replace the audible stem samples");
                    StemKey(form, switches[lane]); Check(state.Active[lane] == null && !sources[lane].Selected, "Pressing a source shortcut again must return that stem to its original source");
                    key = StemKey(form, muting[lane]);
                    Check(key.Handled && key.SuppressKeyPress && mix.Muted[lane] && !mix.Solo[lane] && mutes[lane].Selected && mix.Muted.Where((muted, index) => index != lane).All(muted => !muted), "A/S/D/F must mute only the requested lane and retain the button's solo-clearing behavior");
                    mixer.Seek(2);
                    double expected = Enumerable.Range(0, 4).Where(index => index != lane).Sum(index => .1 + index * .02 + 2.05 * .01);
                    Near(At(Read(mixer, .1), .05), expected, "A mute shortcut must remove that stem from the audible mix");
                    StemKey(form, muting[lane]); Check(!mix.Muted[lane] && !mutes[lane].Selected, "Pressing a mute shortcut again must unmute the same stem");
                }
                StemKey(form, Keys.A, false); StemKey(form, Keys.A, false);
                Check(mix.Muted[0], "Holding a shortcut must not repeatedly flip its state on key repeat");
                Invoke(form, "OnKeyUp", new KeyEventArgs(Keys.A)); StemKey(form, Keys.A);
                Check(!mix.Muted[0], "Releasing a key must allow its next press to toggle again");
                StemKey(form, Keys.A, false); Invoke(form, "OnDeactivate", EventArgs.Empty); StemKey(form, Keys.A);
                Check(!mix.Muted[0], "Leaving the app while a key is held must not leave that shortcut stuck");
                foreach (string field in new[] { "search", "librarySearch" })
                {
                    form.ActiveControl = (Control)Get(form, field);
                    KeyEventArgs key = StemKey(form, Keys.Q);
                    Check(!key.Handled && !key.SuppressKeyPress && state.Active[0] == null, "Search fields must retain normal letters without switching stems");
                    key = StemKey(form, Keys.S);
                    Check(!key.Handled && !key.SuppressKeyPress && !mix.Muted[1], "Search fields must retain normal letters without muting drums");
                }
                using (var editor = new NumericUpDown())
                using (var selector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList })
                {
                    form.Controls.Add(editor); form.Controls.Add(selector);
                    foreach (Control control in new Control[] { editor, selector })
                    {
                        form.ActiveControl = control; KeyEventArgs key = StemKey(form, Keys.A);
                        Check(!key.SuppressKeyPress && !mix.Muted[0], "Numeric editors and dropdowns must retain their normal keyboard input");
                    }
                }
                form.ActiveControl = mutes[0];
                foreach (Keys modifier in new[] { Keys.Control, Keys.Alt, Keys.Shift })
                {
                    KeyEventArgs key = StemKey(form, modifier | Keys.Q);
                    Check(!key.SuppressKeyPress && state.Active[0] == null, "Modified key combinations must not invoke a plain stem shortcut");
                }
                KeyEventArgs find = StemKey(form, Keys.Control | Keys.F);
                Check(find.SuppressKeyPress && !mix.Muted[3], "Ctrl+F must retain search behavior without muting the other stem");
                form.ActiveControl = mutes[0]; state.Available = null; Invoke(form, "UpdateSourceButtons");
                Check(StemKey(form, Keys.Q).SuppressKeyPress && state.Active[0] == null, "An unavailable switch must be a no-op and must not send its letter to song selection");
                Invoke(form, "SetEnabled", false);
                Check(StemKey(form, Keys.A).SuppressKeyPress && !mix.Muted[0], "Disabled mute buttons must also remain unchanged when their shortcuts are pressed");
                state.Available = bank; Set(form, "playback", new PlayerPlayback(mixer)); Set(form, "autoTransition", new PreparedAutoTransition { Bridge = mixer }); Invoke(form, "SetEnabled", true);
                StemKey(form, Keys.W);
                Check(state.Active[1] == null && !sources[1].Enabled, "Source shortcuts must respect the active autoplay transition's switch lock");
                StemKey(form, Keys.S);
                Check(mix.Muted[1] && mutes[1].Selected, "Mute shortcuts must remain available during an active autoplay transition");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void Rendering(string project, SongInfo song)
    {
        foreach (double shift in new[] { 0.0, .5, -.5 })
        {
            var timing = new StemTiming { Shift = shift }; timing.StartLoop(4, 4.25, 120, 0, 12); timing.ExtendLoop(8, 120, 12); timing.Shift += .5;
            string rendered = null; Task<string> task = PlayerMatching.PrepareFiltered(project, Path.Combine(song.Folder, "vocals.wav"), timing.Filter(Rate, Rate * 12), Rate, Rate * 12, CancellationToken.None); Finish(task); rendered = task.Result;
            using (var reader = new WaveFileReader(rendered))
            {
                Check(reader.Length / reader.WaveFormat.BlockAlign == Rate * 12, "The autoplay filter must preserve the song frame count");
                foreach (double at in new[] { 1.25, 6.25, 8.25, 10.25, 11.75 })
                {
                    reader.CurrentTime = TimeSpan.FromSeconds(at); var source = StemMixer.Samples(reader); float[] sample = new float[2]; source.Read(sample, 0, 2);
                    long remaining; long frame = timing.SourceFrame((long)Math.Round(at * Rate), Rate, out remaining);
                    Near(sample[0], frame < 0 || frame >= Rate * 12 ? 0 : .1 + frame / (double)Rate * .01, "Rendered autoplay audio must match the live source-time mapping");
                }
            }
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "stemkit-gesture-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            previewFile = args.Length > 0 ? args[0] : null;
            Grid(); SongInfo song = Song(root, "Original"), next = Song(root, "Next"); Audio(song); OffsetIsolation(root, song); ResetStems(root, song, next); TimingLabels(root, song); Rendering(root, song); LongerDonors(root, song); Gestures(root, song, next); KeyboardShortcuts(root);
            Console.WriteLine("Stem gestures: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
