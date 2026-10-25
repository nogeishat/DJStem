using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.Wave;

internal static class PlayerSongDeletionTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Get(object source, string field) { return source.GetType().GetField(field, Hidden).GetValue(source); }
    private static void Set(object source, string field, object value) { source.GetType().GetField(field, Hidden).SetValue(source, value); }
    private static object Invoke(object source, string method, params object[] args) { return source.GetType().GetMethod(method, Hidden).Invoke(source, args); }
    private static void Finish(Task task)
    {
        var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.ElapsedMilliseconds < 60000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Song deletion must finish while the interface remains responsive"); task.GetAwaiter().GetResult();
    }
    private static SongInfo Song(string root, string name, string id)
    {
        string audio = Path.Combine(root, "mp3", "Artist", name + ".mp3"), folder = Path.Combine(root, "stems", "Artist", name);
        Directory.CreateDirectory(Path.GetDirectoryName(audio)); Directory.CreateDirectory(folder);
        string wave = Path.Combine(folder, "vocals.wav");
        using (var writer = new WaveFileWriter(wave, new WaveFormat(16000, 24, 2)))
            for (int frame = 0; frame < 16000 * 8; frame++) { float value = (float)(.05 * Math.Sin(2 * Math.PI * 220 * frame / 16000)); writer.WriteSample(value); writer.WriteSample(value); }
        foreach (string stem in StemMixer.StemNames.Skip(1)) File.Copy(wave, Path.Combine(folder, stem + ".wav"));
        string ffmpeg = Environment.GetEnvironmentVariable("STEMKIT_FFMPEG");
        if (String.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = File.Exists(@"C:\ffmpeg\bin\ffmpeg.exe") ? @"C:\ffmpeg\bin\ffmpeg.exe" : "ffmpeg.exe";
        using (var encoder = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -nostdin -y -i \"" + wave + "\" \"" + audio + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
        { string errors = encoder.StandardError.ReadToEnd(); encoder.WaitForExit(); Check(encoder.ExitCode == 0, "The test song must encode: " + errors); }
        File.WriteAllText(Path.Combine(folder, "metadata.json"), new JavaScriptSerializer().Serialize(new { id = id, title = name, artist = "Artist", sourceUrl = "https://www.youtube.com/watch?v=" + id, bpm = 120, key = "A minor", analysis = new { version = 2, source = "mp3", beatOffset = 0, beatConfidence = .9, keyConfidence = .9 } }));
        File.WriteAllText(Path.Combine(folder, "separation.json"), "{}");
        string cover = Path.Combine(folder, "cover.jpg"); using (var image = new Bitmap(100, 100)) { using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Coral); image.Save(cover); }
        return new SongInfo { Audio = audio, Folder = folder, Title = name, Artist = "Artist", HasStems = true, Bpm = 120, Key = "A minor", Cover = cover, YouTubeId = id };
    }
    private static void Tests(string root, string source)
    {
        foreach (string file in new[] { "scripts/manage-song.mjs", "lib/song-delete.mjs", "lib/music.mjs", "lib/links.mjs" })
        { string destination = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(Path.Combine(source, file), destination); }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Environment.SetEnvironmentVariable("PORT", ((IPEndPoint)listener.LocalEndpoint).Port.ToString()); listener.Stop();
        string library = Path.Combine(root, "library"); SongInfo original = Song(library, "Original", "BaW_jenozKc"), donor = Song(library, "Donor", "YE7VzlLtp-4"), unrelated = Song(library, "Other", "abcdefghijk");
        var songs = new[] { original, donor, unrelated };
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "data", "queue.json"), new JavaScriptSerializer().Serialize(new { version = 1, paused = false, jobs = songs.Select(song => new { id = song.YouTubeId, videoId = song.YouTubeId, url = "https://www.youtube.com/watch?v=" + song.YouTubeId, filename = song.Audio, stemsFolder = song.Folder, status = "completed", outputType = "mp3" }).ToArray() }));
        File.WriteAllText(Path.Combine(root, "data", "links.txt"), String.Join("\n", songs.Select(song => "https://www.youtube.com/watch?v=" + song.YouTubeId)) + "\n");
        File.WriteAllText(Path.Combine(root, "data", "mp3-download-archive.txt"), String.Join("\n", songs.Select(song => "youtube " + song.YouTubeId)) + "\n");
        using (var form = new PlayerForm(library, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle; Set(form, "songs", songs.ToList());
            Finish((Task)Invoke(form, "LoadSong", original, false, 0.0, false)); Finish((Task)Get(form, "currentAnalysisTask")); Finish((Task)Get(form, "waveformTask"));
            Finish((Task)Invoke(form, "ReplaceFromDrop", donor, 0));
            var mixer = (StemMixer)Get(form, "mixer"); mixer.SetStemShift(0, -.5); mixer.SetStemShift(1, .25); mixer.StartStemLoop(2, 2, 120, 0); mixer.Seek(2);
            ((MixSlider)Get(form, "volume")).Value = 43;
            Check((bool)Invoke(form, "StoreMix", "Uses donor", true), "The fixture mix must save");
            var saved = ((List<SavedMix>)Get(form, "savedMixes")).Single(); var queue = (PlaybackQueue)Get(form, "songQueue"); queue.Follow(original.Audio); queue.Add(donor.Audio, 1); queue.Add("mix:" + saved.Id, 2); queue.Add(unrelated.Audio, 3);
            var playlist = (SongPlaylist)Invoke(form, "StorePlaylist", "Keep playlist"); Invoke(form, "AddPlaylistItem", playlist, donor.Audio); Invoke(form, "AddPlaylistItem", playlist, "mix:" + saved.Id); Invoke(form, "AddPlaylistItem", playlist, original.Audio);
            var button = (GlassButton)Get(form, "deleteSongButton"); var actions = (Control)Get(form, "nowPlayingActions");
            Check(button.Enabled && button.Bottom <= actions.Height - 12 && button.Top > ((GlassButton)Get(form, "exportMixButton")).Bottom, "The + menu must fit an enabled Delete song action below export");
            Invoke(form, "RenderBrowser", true); var cards = ((Control)Get(form, "libraryGrid")).Controls.OfType<LibraryCard>().ToList();
            Check(cards.All(card => card.ContextMenuStrip != null && card.ContextMenuStrip.Items.Cast<ToolStripItem>().Any(item => item.Text == "Delete song…")), "Every library song card must offer deletion");
            string configuredNode = Environment.GetEnvironmentVariable("STEMKIT_NODE"); Environment.SetEnvironmentVariable("STEMKIT_NODE", Path.Combine(root, "missing-node.exe"));
            Task<bool> failed = (Task<bool>)Invoke(form, "DeleteSong", donor); Finish(failed); Environment.SetEnvironmentVariable("STEMKIT_NODE", configuredNode);
            Check(!failed.Result && File.Exists(donor.Audio) && ((StemSourceState)Get(form, "stemSources")).Active[0] != null && Math.Abs(((StemMixer)Get(form, "mixer")).Seconds - 2) < .01, "Backend failure must preserve all files, restore donor playback and keep the transport position");
            Task<bool> deleted = (Task<bool>)Invoke(form, "DeleteSong", donor); Finish(deleted);
            mixer = (StemMixer)Get(form, "mixer"); var sources = (StemSourceState)Get(form, "stemSources");
            Check(deleted.Result && !File.Exists(donor.Audio) && !Directory.Exists(donor.Folder), "Deleting an active donor must remove its MP3 and complete stems folder");
            Check(sources.Available == null && sources.Active[0] == null && mixer.StemState(0).Shift == 0 && mixer.StemState(1).Shift == .25 && mixer.StemState(2).Looping, "Donor deletion must restore its lane while preserving other offsets and loops");
            Check(Math.Abs(mixer.Seconds - 2) < .01 && ((MixSlider)Get(form, "volume")).Value == 43 && mixer.HasStems, "The original song, position and volume must remain editable");
            Check(((List<SavedMix>)Get(form, "savedMixes")).Count == 0 && playlist.Items.SequenceEqual(new[] { original.Audio }) && queue.Items.All(item => item.Audio != donor.Audio && item.Audio != "mix:" + saved.Id), "Dependent mixes and all playlist/queue references must disappear");
            Check(!File.ReadAllText(Path.Combine(root, "data", "links.txt")).Contains(donor.YouTubeId) && !File.ReadAllText(Path.Combine(root, "data", "mp3-download-archive.txt")).Contains(donor.YouTubeId), "The player's delete action must remove the source link and duplicate archive ID");
            ((WaveOutEvent)Get(form, "output")).Play(); deleted = (Task<bool>)Invoke(form, "DeleteSong", unrelated); Finish(deleted);
            Check(deleted.Result && ((WaveOutEvent)Get(form, "output")).PlaybackState == PlaybackState.Playing && File.Exists(original.Audio), "Deleting another song must resume current playback");
            ((WaveOutEvent)Get(form, "output")).Pause(); deleted = (Task<bool>)Invoke(form, "DeleteSong", original); Finish(deleted);
            Check(deleted.Result && Get(form, "current") == null && Get(form, "mixer") == null && queue.Items.Count == 0 && playlist.Items.Count == 0, "Deleting the last current song must clear playback and its references");
            Check(!File.Exists(original.Audio) && !Directory.Exists(original.Folder) && !File.ReadAllText(Path.Combine(root, "data", "links.txt")).Contains(original.YouTubeId), "Current-song deletion must release native readers and remove files and link");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "stemkit-delete-player-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Tests(root, args[0]); Console.WriteLine("Player song deletion: " + checks + " checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "stemkit-delete-player-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
