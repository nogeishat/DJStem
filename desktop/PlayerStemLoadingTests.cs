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

internal static class PlayerStemLoadingTests
{
    private static int checks;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Get(object source, string name) { return source.GetType().GetField(name, Fields).GetValue(source); }
    private static void Set(object source, string name, object value) { source.GetType().GetField(name, Fields).SetValue(source, value); }
    private static object Invoke(object source, string name, params object[] args) { return source.GetType().GetMethod(name, Fields).Invoke(source, args); }
    private static void Finish(Task task, Action poll = null)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 45000) { Application.DoEvents(); if (poll != null) poll(); Thread.Sleep(2); }
        Check(task.IsCompleted, "Preparation must finish with a responsive UI"); task.GetAwaiter().GetResult();
    }
    private static SongInfo Song(string root, string name, double bpm)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        string audio = Path.Combine(folder, "song.wav");
        using (var writer = new WaveFileWriter(audio, new WaveFormat(16000, 24, 2)))
        for (int frame = 0; frame < 192000; frame++) { float sample = (float)(.1 * Math.Sin(frame * 2 * Math.PI * 440 / 16000)); writer.WriteSample(sample); writer.WriteSample(sample); }
        foreach (string stem in StemMixer.StemNames) File.Copy(audio, Path.Combine(folder, stem + ".wav"));
        File.WriteAllText(Path.Combine(folder, "metadata.json"), new JavaScriptSerializer().Serialize(new { bpm, key = "A minor", analysis = new { version = 2, source = "mp3", beatOffset = 0, beatConfidence = .9, keyConfidence = .9 } }));
        string cover = Path.Combine(folder, "cover.png");
        using (var bitmap = new Bitmap(96, 96)) { using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(bpm == 120 ? Color.MediumSeaGreen : Color.Coral); bitmap.Save(cover); }
        return new SongInfo { Audio = audio, Folder = folder, Cover = cover, Title = name, Artist = "Test artist", Bpm = bpm, HasStems = true };
    }
    private static void Painting()
    {
        using (var parent = new Panel { BackColor = PlayerTheme.Glass, Size = new Size(180, 60) })
        using (var button = new GlassButton { Parent = parent, Size = new Size(160, 36), Swatch = Color.Orange, LoadingColor = Color.Cyan, Enabled = false })
        using (var bitmap = new Bitmap(160, 36))
        {
            button.LoadingProgress = 0; button.DrawToBitmap(bitmap, button.ClientRectangle);
            Check(bitmap.GetPixel(80, 18).A == 0, "A waiting stem must be clear inside, including when disabled");
            Check(bitmap.GetPixel(80, 0).B > parent.BackColor.B + 80, "A waiting stem must have the song-colored outline");
            button.LoadingProgress = .25; button.DrawToBitmap(bitmap, button.ClientRectangle);
            Check(bitmap.GetPixel(20, 18).ToArgb() == Color.Cyan.ToArgb() && bitmap.GetPixel(65, 18).A == 0, "The fill must represent the actual fraction with a clear unfilled area");
            Check(button.AccessibilityObject.Value == "25% loaded", "Assistive technology must expose progress");
            button.LoadingProgress = .75; button.DrawToBitmap(bitmap, button.ClientRectangle);
            Check(bitmap.GetPixel(100, 18).ToArgb() == Color.Cyan.ToArgb() && bitmap.GetPixel(140, 18).A == 0, "The progress fill must move left to right");
            button.LoadingProgress = -1; button.DrawToBitmap(bitmap, button.ClientRectangle);
            Check(bitmap.GetPixel(80, 18).ToArgb() == Color.Orange.ToArgb(), "Completed and idle buttons must retain the existing source swatch");
        }
    }
    private static void Lifecycle(PlayerForm form)
    {
        var buttons = (GlassButton[])Get(form, "sourceButtons");
        Invoke(form, "UpdateSourceButtons"); Color currentColor = buttons[0].Swatch;
        var progress = new StemLoadingProgress(); Set(form, "replacementLoadingProgress", progress); Set(form, "preparingReplacement", true);
        Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons.All(button => button.LoadingProgress == 0 && button.LoadingColor == currentColor), "Every pending lane must use the current song's color");
        progress.Report(0, .5); progress.Report(1, 1); progress.Report(0, .2); Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons[0].LoadingProgress == .5 && buttons[1].LoadingProgress == -1 && buttons[2].LoadingProgress == 0, "Each lane must advance independently, never regress, and restore its default when done");
        Invoke(form, "CancelMatching"); progress.Report(2, .8); Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons.All(button => button.LoadingProgress == -1), "Cancellation and stale background reports must restore every default button");
        var completion = new TaskCompletionSource<PreparedAutoTransition>();
        var preload = new AutoSongPreload { Preparing = completion.Task }; preload.Progress.Report(2, .6);
        Set(form, "autoPlayEnabled", true); Set(form, "autoPreload", preload); Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons.All(button => button.LoadingProgress == -1), "Preparing the second upcoming song must leave every stem button in its normal style");
        var foreground = new StemLoadingProgress(); foreground.Report(2, .2);
        Set(form, "preparingAutoPlay", true); Set(form, "autoLoadingProgress", foreground); Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons[2].LoadingProgress == .2, "The immediate Auto play song must take priority over a speculative successor");
        Set(form, "autoLoadingProgress", preload.Progress); Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons[2].LoadingProgress == .6, "A preload that becomes the immediate next song must expose its existing progress");
        Set(form, "preparingAutoPlay", false); Set(form, "autoLoadingProgress", null); Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons.All(button => button.LoadingProgress == -1), "Finishing foreground preparation must hide any remaining preparation further ahead");
        completion.SetResult(null); Invoke(form, "UpdateStemLoadingButtons");
        Check(buttons.All(button => button.LoadingProgress == -1), "Completed or failed background preparation must not leave loading buttons");
        Invoke(form, "CancelAutoPreparation"); Set(form, "autoPlayEnabled", false);
    }
    private static void Manual(PlayerForm form, SongInfo donor)
    {
        var buttons = (GlassButton[])Get(form, "sourceButtons");
        bool sawProgress = false, sawFinishedLane = false;
        InvalidateEventHandler observe = delegate
        {
            sawProgress |= buttons.Any(button => button.LoadingProgress > 0 && button.LoadingProgress < 1);
            sawFinishedLane |= buttons.Any(button => button.LoadingProgress == -1) && buttons.Any(button => button.LoadingProgress >= 0);
        };
        foreach (var button in buttons) button.Invalidated += observe;
        Task task = (Task)Invoke(form, "ReplaceFromDrop", donor, 2);
        Check(buttons.All(button => button.LoadingProgress >= 0), "A real song drop must start loading indicators before awaiting file work");
        Finish(task, () => Invoke(form, "UpdateStemLoadingButtons"));
        foreach (var button in buttons) button.Invalidated -= observe;
        Check(sawProgress && sawFinishedLane, "A real replacement must report progress and complete lanes independently");
        Check(((StemSourceState)Get(form, "stemSources")).Active[2].Song == donor && buttons.All(button => button.LoadingProgress == -1), "A loaded drop must activate its lane and restore all normal styles");
        Task cached = (Task)Invoke(form, "ReplaceFromDrop", donor, 0); Finish(cached);
        Check(buttons.All(button => button.LoadingProgress == -1), "Cached waveforms must also clear their loading indicators");
        var broken = new SongInfo { Audio = donor.Audio, Folder = Path.Combine(donor.Folder, "missing"), Cover = donor.Cover, Title = "Missing stems", Artist = donor.Artist, HasStems = true };
        Finish((Task)Invoke(form, "ReplaceFromDrop", broken, 0));
        Check(buttons.All(button => button.LoadingProgress == -1), "Failed replacement preparation must clear every indicator");
    }
    private static void Rendering(string root, SongInfo song)
    {
        var values = new List<double>();
        string rendered = null;
        Finish(Task.Run(async () => rendered = await PlayerMatching.PrepareFiltered(root, song.Audio, "aresample=22050", 22050, 264600, CancellationToken.None, progress: value => { lock (values) values.Add(value); })));
        Check(File.Exists(rendered) && values.Any(value => value > 0 && value < 1) && values.Last() == 1, "FFmpeg must report measured render progress before successful completion");
        values.Clear();
        Finish(Task.Run(async () => await PlayerMatching.PrepareFiltered(root, song.Audio, "aresample=22050", 22050, 264600, CancellationToken.None, progress: value => values.Add(value))));
        Check(values.Count == 1 && values[0] == 1, "A cached render must report completion immediately");
    }
    private static void Preview(PlayerForm form, string file)
    {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show();
        var clock = Stopwatch.StartNew();
        while ((bool)Get(form, "refreshing") && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
        ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
        Invoke(form, "ShowLibrary", false);
        var song = (SongInfo)Get(form, "current");
        ((Label)Get(form, "songTitle")).Text = song.Title; ((Label)Get(form, "artistLabel")).Text = song.Artist;
        ((PictureBox)Get(form, "cover")).Image = new Bitmap(song.Cover);
        Invoke(form, "SetEnabled", true);
        var progress = new StemLoadingProgress(); progress.Report(1, .28); progress.Report(2, .62); progress.Report(3, 1);
        Set(form, "preparingReplacement", true); Set(form, "replacementLoadingProgress", progress);
        Invoke(form, "UpdateSourceButtons");
        ((Label)Get(form, "songTitle")).ForeColor = ((GlassButton[])Get(form, "sourceButtons"))[0].LoadingColor;
        ((Label)Get(form, "message")).ForeColor = PlayerTheme.Muted; ((Label)Get(form, "message")).Text = "Preparing four stems from Replacement…";
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(file); }
        Invoke(form, "CancelMatching");
        Image image = ((PictureBox)Get(form, "cover")).Image; ((PictureBox)Get(form, "cover")).Image = null; image.Dispose();
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "sk-loading-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root);
        try
        {
            Painting(); SongInfo original = Song(root, "Current song", 120), donor = Song(root, "Replacement", 132);
            using (var form = new PlayerForm(root, root))
            {
                IntPtr handle = form.Handle; ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
                foreach (var button in (GlassButton[])Get(form, "sourceButtons")) { IntPtr buttonHandle = button.Handle; }
                Set(form, "current", original); Set(form, "mixer", new StemMixer(original.Audio, original.Folder)); Set(form, "stemSources", new StemSourceState(original.Folder));
                Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true });
                try { Lifecycle(form); Manual(form, donor); if (args.Length > 0) Preview(form, args[0]); }
                finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CancelMatching"); Invoke(form, "CloseAudio"); }
            }
            Rendering(root, original);
            Console.WriteLine("Stem loading: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
