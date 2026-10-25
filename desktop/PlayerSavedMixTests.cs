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

internal static class PlayerSavedMixTests
{
    private static int checks;
    private const int Rate = 16000;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Get(object target, string field) { return target.GetType().GetField(field, Private).GetValue(target); }
    private static void Set(object target, string field, object value) { target.GetType().GetField(field, Private).SetValue(target, value); }
    private static object Invoke(object target, string method, params object[] values) { return target.GetType().GetMethod(method, Private).Invoke(target, values); }
    private static void Finish(Task task)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 60000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Saved mix preparation must finish while processing UI messages"); task.GetAwaiter().GetResult();
    }
    private static SongInfo Song(string root, string name, int seconds)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        string audio = Path.Combine(folder, "song.wav");
        foreach (string file in new[] { audio }.Concat(StemMixer.StemNames.Select(stem => Path.Combine(folder, stem + ".wav"))))
            using (var writer = new WaveFileWriter(file, new WaveFormat(Rate, 24, 2)))
                for (int frame = 0; frame < seconds * Rate; frame++) { float value = (float)(.08 * Math.Sin(2 * Math.PI * 220 * frame / Rate) + .02 * Math.Sin(2 * Math.PI * 37 * frame / Rate)); writer.WriteSample(value); writer.WriteSample(value); }
        File.WriteAllText(Path.Combine(folder, "metadata.json"), new JavaScriptSerializer().Serialize(new { bpm = 120, key = "A minor", analysis = new { version = 2, source = "mp3", beatOffset = .1, beatConfidence = .9, keyConfidence = .9 } }));
        string cover = Path.Combine(folder, "cover.png");
        using (var image = new Bitmap(160, 160)) { using (var graphics = Graphics.FromImage(image)) graphics.Clear(name == "Original" ? Color.Coral : Color.Teal); image.Save(cover); }
        return new SongInfo { Audio = audio, Folder = folder, Title = name, Artist = "Test artist", HasStems = true, Cover = cover, Bpm = 120, TuningHz = 440 };
    }
    private static float[] Samples(StemMixer mixer)
    {
        mixer.Seek(3); var samples = new float[Rate / 2]; mixer.Read(samples, 0, samples.Length); return samples;
    }
    private sealed class PreviewHost : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    private static void Preview(Control control, string file)
    {
        Control parent = control.Parent; DockStyle dock = control.Dock; Rectangle bounds = control.Bounds; bool visible = control.Visible;
        using (var host = new PreviewHost { ShowInTaskbar = false, Opacity = 0, FormBorderStyle = FormBorderStyle.None, ClientSize = control.Size })
        {
            host.Controls.Add(control); control.Dock = DockStyle.Fill; control.Visible = true; host.Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(file); }
            host.Controls.Remove(control); parent.Controls.Add(control); control.Dock = dock; control.Bounds = bounds; control.Visible = visible;
        }
    }
    private static void Tests(string root, string preview)
    {
        SongInfo original = Song(root, "Original", 10), donor = Song(root, "Added", 18), older = Song(root, "Earlier donor", 16);
        var library = new List<SongInfo> { original, donor, older };
        SavedMix saved;
        float[] expected;
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); };
            IntPtr handle = form.Handle; Set(form, "songs", library);
            Set(form, "tuningReferenceHz", (double?)432);
            Finish((Task)Invoke(form, "LoadSong", original, false, 0.0, false));
            Finish((Task)Get(form, "waveformTask")); Finish((Task)Get(form, "currentAnalysisTask"));
            Finish((Task)Invoke(form, "ReplaceFromDrop", older, 0)); Finish((Task)Invoke(form, "ReplaceFromDrop", donor, 1));
            var shifts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { original.Audio, 1 }, { donor.Audio, -1 } };
            Finish((Task)Invoke(form, "ApplyTuning", (double?)150, shifts));
            var mixer = (StemMixer)Get(form, "mixer"); var mix = (PlayerMix)Get(form, "mix");
            mixer.SetStemShift(0, -4); mixer.SetStemShift(1, .8); mixer.StartStemLoop(2, 2, 150, .08); mixer.ExtendStemLoop(2, 5.8, 150);
            mix.Muted[3] = true; mix.Solo[0] = mix.Solo[1] = true; mix.Levels[0] = .37f; mix.Levels[1] = .82f; mix.Levels[2] = .63f;
            ((MixSlider)Get(form, "volume")).Value = 54; Invoke(form, "ApplyMix");
            expected = Samples(mixer);
            Check((bool)Invoke(form, "StoreMix", "First editable mix", true), "The current editable mix must save successfully");
            var records = (List<SavedMix>)Get(form, "savedMixes"); saved = records.Single();
            Check(saved.Sources[0] == older.Audio && saved.Sources[1] == donor.Audio && saved.Sources[2] == null, "A saved mix must retain older active sources as well as the latest added song");
            Check(saved.AddedAudio == donor.Audio && saved.Banks.Count == 2 && saved.Banks.All(bank => !bank.Audio.Contains("matched-stems")), "The staged song and all banks must use source identities, not rendered cache paths");
            Check(saved.Bpm == 150 && saved.OriginalTempo == 1.25 && saved.KeyShifts[original.Audio] == 1 && saved.KeyShifts[donor.Audio] == -1, "Tempo and independent key changes must be part of the mix");
            mix.Levels[0] = 1; mix.Muted[3] = false; mixer.SetStemShift(0, 0); shifts.Clear();
            Check(saved.Levels[0] == .37f && saved.Muted[3] && saved.Timing[0].Shift == -4 && saved.KeyShifts.Count == 2, "Further editing must not mutate the saved snapshot before Save changes");
            string[] preferenceFiles = Directory.GetFiles(Path.Combine(root, "data"), "player-settings*");
            Check(preferenceFiles.Length == 1, "Saving must retain one internal preference store without per-mix exports or temporary leftovers: " + String.Join(", ", preferenceFiles.Select(Path.GetFileName)));
            Check(Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly).Length == 0, "Saving must not export any mix file");
            Invoke(form, "CloseAudio");
        }
        // Mix records must outlive cache cleanup, rather than reference renders.
        string cache = PlayerMatchedCache.Folder(root); Check(PlayerLibrary.Inside(root, cache), "Test cache cleanup must stay inside its isolated root");
        Directory.CreateDirectory(cache);
        string[] renders = Directory.GetFiles(cache, "*.wav"); Check(renders.Length == 0, "Matching and tuning must use live streams without creating full-song render files");
        foreach (string file in renders) File.Delete(file);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); };
            IntPtr handle = form.Handle; Set(form, "songs", library);
            var records = (List<SavedMix>)Get(form, "savedMixes"); Check(records.Count == 1, "Saved mixes must survive a fresh player instance"); saved = records.Single();
            Set(form, "autoPlayEnabled", true);
            Task<bool> open = (Task<bool>)Invoke(form, "OpenSavedMix", saved); Finish(open); Check(open.Result, "An app-persisted mix must reopen successfully");
            Check(!(bool)Get(form, "autoPlayEnabled") && ((WaveformView[])Get(form, "waveforms")).All(view => view.TimingEnabled), "Opening a saved mix must turn off Auto play and allow timing edits");
            Check(((WaveOutEvent)Get(form, "output")).PlaybackState != PlaybackState.Playing, "Opening must load for editing without starting before the user presses Play");
            var mixer = (StemMixer)Get(form, "mixer"); var mix = (PlayerMix)Get(form, "mix"); var sources = (StemSourceState)Get(form, "stemSources");
            Check(sources.Active[0].Song.Audio == older.Audio && sources.Active[1].Song.Audio == donor.Audio && sources.Active[2] == null && sources.Available.Song.Audio == donor.Audio, "Opening must restore every source and the staged added song");
            Check(Directory.GetFiles(cache, "*.wav").Length == 0 && sources.Active[0].Tuning != null, "Opening must restore live transforms from source songs without rebuilding full-song render files");
            var referenceTransform = (PlaybackTransform)Get(form, "originalPlaybackTransform");
            Check(Math.Abs(referenceTransform.Pitch - 432.0 / 440 * Camelot.Pitch(1)) < .000001 && Math.Abs(sources.Active[1].Plan.Pitch - 432.0 / 440 * Camelot.Pitch(-1)) < .000001, "Saved mixes must restore the chosen reference once, together with independent key edits");
            Check(mix.Muted.SequenceEqual(saved.Muted) && mix.Solo.SequenceEqual(saved.Solo) && mix.Levels.SequenceEqual(saved.Levels) && ((MixSlider)Get(form, "volume")).Value == saved.Volume, "Opening must restore mute, solo, all stem volumes and master volume");
            for (int lane = 0; lane < 4; lane++)
            {
                StemTiming actual = mixer.StemState(lane), wanted = saved.Timing[lane].Live();
                Check(actual.Shift == wanted.Shift && actual.LoopStart == wanted.LoopStart && actual.LoopEnd == wanted.LoopEnd && actual.LoopAnchor == wanted.LoopAnchor && actual.LoopShift == wanted.LoopShift, "Every lane's offsets and loop coordinates must round-trip");
            }
            float[] actualSamples = Samples(mixer); Check(expected.Zip(actualSamples, (a, b) => Math.Abs(a - b)).Max() < .000001, "The restored mix must produce the same audio samples");
            Check(mixer.StemDuration(0) > mixer.Duration && mixer.StemDuration(1) > mixer.Duration, "Restoring a mix must retain the full longer donor tails");
            Check(((GlassButton[])Get(form, "sourceButtons")).All(button => button.Enabled), "Source controls must be editable after opening");
            Check(((GlassButton)Get(form, "saveMixButton")).Text == "Save changes" && ((GlassButton)Get(form, "saveMixButton")).Enabled, "A reopened mix must expose an enabled Save changes action");
            if (preview != null) Preview((Control)Get(form, "content"), Path.Combine(Path.GetDirectoryName(preview), "saved-mix-player-preview.png"));
            Task<bool> canceled = (Task<bool>)Invoke(form, "OpenSavedMix", saved);
            ((CancellationTokenSource)Get(form, "savedMixCancellation")).Cancel(); Finish(canceled);
            Check(!canceled.Result && Object.ReferenceEquals(mixer, Get(form, "mixer")) && !((bool)Get(form, "openingSavedMix")), "Canceling a pending open must retain the current session and unlock the interface");
            Invoke(form, "ToggleStemSource", 2); Check(sources.Active[2] == sources.Available, "The saved added song must remain switchable on previously original lanes");
            mixer.SetStemShift(1, -1.6); mix.Levels[1] = .29f;
            Check((bool)Invoke(form, "StoreMix", saved.Name, false) && records.Count == 1, "Save changes must update one existing mix");
            saved = records.Single(); Check(saved.Timing[1].Shift == -1.6 && saved.Levels[1] == .29f && saved.Sources[2] == donor.Audio, "Updating must persist newly edited sources, offsets and volume");
            Check((bool)Invoke(form, "StoreMix", "Second mix", true) && records.Count == 2 && records[0].Id != records[1].Id, "Save as new must create a separately editable copy");
            SavedMix beforeFailure = records[1];
            using (var locked = File.Open(Path.Combine(root, "data", "player-settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!(bool)Invoke(form, "StoreMix", "Cannot write", false) && Object.ReferenceEquals(records[1], beforeFailure), "A failed persistent update must retain the prior saved record");
            Check(!File.Exists(Path.Combine(root, "data", "player-settings.json.pending")), "Failed saves must clean up temporary store writes");
            var browser = (Control)Get(form, "browser"); var grid = (Control)Get(form, "libraryGrid");
            Invoke(form, "ShowLibrary", true); ((GlassButton)Get(form, "playlistsTab")).PerformClick();
            Check(grid.Controls.OfType<LibraryCard>().Single().Title == "My mixes" && ((Label)Get(form, "libraryHeading")).Text == "Playlists", "The library must expose the My mixes playlist under Playlists");
            grid.Controls.OfType<LibraryCard>().Single().AccessibilityObject.DoDefaultAction();
            Check(grid.Controls.OfType<LibraryCard>().Count() == 2 && ((Label)Get(form, "libraryHeading")).Text == "My mixes", "My mixes must list every saved mix");
            Check(grid.Controls.OfType<LibraryCard>().All(card => card.AccessibilityObject.DefaultAction == "Open mix"), "Mix cards must have an accessible editable-open action");
            if (preview != null) Preview(browser, preview);
            ((TextBox)Get(form, "librarySearch")).Text = "Second"; Check(grid.Controls.OfType<LibraryCard>().Single().Title == "Second mix", "Playlist search must filter by mix name");
            ((TextBox)Get(form, "librarySearch")).Text = "";
            ((GlassButton)Get(form, "allSongsTab")).PerformClick();
            Check(!(bool)Get(form, "playlistsView") && grid.Top == 254 && grid.Controls.OfType<LibraryCard>().All(card => card.Tag is SongInfo), "Switching to songs must restore song filters and cards");
            string missing = donor.Audio + ".unavailable"; File.Move(donor.Audio, missing);
            ((GlassButton)Get(form, "playlistsTab")).PerformClick();
            open = (Task<bool>)Invoke(form, "OpenSavedMix", saved); Finish(open);
            Check(!open.Result && Object.ReferenceEquals(mixer, Get(form, "mixer")) && Object.ReferenceEquals(sources, Get(form, "stemSources")), "A missing saved song must fail without replacing the editable session"); File.Move(missing, donor.Audio);
            Check(((Label)Get(form, "libraryCount")).Text.Contains("missing") && ((Label)Get(form, "libraryCount")).ForeColor == PlayerTheme.Stems[1], "Opening errors must be visible in the library where the mix was selected");
            string newAudio = Path.Combine(root, "renamed", "song.wav");
            var mappings = new object[] { new Dictionary<string, object> { { "audio", donor.Audio }, { "newAudio", newAudio } } };
            Invoke(form, "RemapSavedMixes", (object)mappings);
            Check(records.All(item => item.AddedAudio == newAudio && item.Sources[1] == newAudio && item.Banks.Any(bank => bank.Audio == newAudio) && item.KeyShifts.ContainsKey(newAudio)), "Saved mix references must follow artist-folder audio renames");
            Invoke(form, "DeleteSavedMix", records[1]); Check(records.Count == 1 && (string)Get(form, "loadedMixId") == null, "Deleting an open saved mix must preserve playback and clear its update identity");
            Invoke(form, "CloseAudio");
        }
        using (var form = new PlayerForm(root, root))
        {
            var records = (List<SavedMix>)Get(form, "savedMixes"); Check(records.Count == 1 && records[0].Timing[1].Shift == -1.6, "Updates and deletion must survive reopening the player");
            var raw = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root, "data", "player-settings.json"))) as Dictionary<string, object>;
            object[] stored = (object[])raw["savedMixes"]; var malformed = new Dictionary<string, object> { { "Name", "Broken" } }; Invoke(form, "ReadSavedMixes", (object)new object[] { malformed, stored[0] });
            Check(records.Count == 1, "Malformed and duplicate records must not hide valid mixes or duplicate the playlist");
            Set(form, "songs", library); form.Disposed += delegate { Invoke(form, "CloseAudio"); };
            Finish((Task)Invoke(form, "LoadSong", original, false, 0.0, false));
            Invoke(form, "ChangeMode", true); Check((bool)Invoke(form, "StoreMix", "Original only", true), "A mix without an added song must also be savable");
            SavedMix originalOnly = records.Last(); Check(originalOnly.Banks.Count == 0 && originalOnly.AddedAudio == null && originalOnly.OriginalMode, "Original-only mixes must retain their playback mode without inventing a donor");
            Task<bool> open = (Task<bool>)Invoke(form, "OpenSavedMix", originalOnly); Finish(open);
            Check(open.Result && ((StemMixer)Get(form, "mixer")).OriginalMode && ((StemSourceState)Get(form, "stemSources")).Available == null, "Original-only mixes must reopen with the original mode and editable stem choices intact");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "stemkit-saved-mix-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Tests(root, args.Length > 0 ? args[0] : null); Console.WriteLine("Saved mixes: " + checks + " checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "stemkit-saved-mix-tests-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
