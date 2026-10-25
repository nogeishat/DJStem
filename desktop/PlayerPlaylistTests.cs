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

internal static class PlayerPlaylistTests
{
    private static int checks;
    private const int Rate = 16000;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Get(object source, string field) { return source.GetType().GetField(field, Hidden).GetValue(source); }
    private static void Set(object source, string field, object value) { source.GetType().GetField(field, Hidden).SetValue(source, value); }
    private static object Invoke(object source, string method, params object[] args) { return source.GetType().GetMethod(method, Hidden).Invoke(source, args); }
    private static void Finish(Task task)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.ElapsedMilliseconds < 60000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Playlist preparation must finish while the interface remains responsive"); task.GetAwaiter().GetResult();
    }
    private static void Wave(string file, int seconds, float constant = 0)
    {
        using (var writer = new WaveFileWriter(file, new WaveFormat(Rate, 24, 2)))
            for (int frame = 0; frame < Rate * seconds; frame++) { float value = constant == 0 ? (float)(.08 * Math.Sin(2 * Math.PI * 220 * frame / Rate)) : constant; writer.WriteSample(value); writer.WriteSample(value); }
    }
    private static SongInfo Song(string root, string name, int seconds = 12)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder); string audio = Path.Combine(folder, "song.wav"); Wave(audio, seconds);
        foreach (string stem in StemMixer.StemNames) Wave(Path.Combine(folder, stem + ".wav"), seconds);
        File.WriteAllText(Path.Combine(folder, "metadata.json"), new JavaScriptSerializer().Serialize(new { bpm = 120, key = "A minor", analysis = new { version = 2, source = "mp3", beatOffset = .1, beatConfidence = .9, keyConfidence = .9 } }));
        string cover = Path.Combine(folder, "cover.png"); using (var bitmap = new Bitmap(160, 160)) { using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(name == "One" ? Color.Coral : Color.Teal); bitmap.Save(cover); }
        return new SongInfo { Audio = audio, Folder = folder, Title = name, Artist = "Playlist artist", Bpm = 120, HasStems = true, Cover = cover };
    }
    private sealed class PreviewHost : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    private static void Preview(Control control, string file)
    {
        Control parent = control.Parent; DockStyle dock = control.Dock; Rectangle bounds = control.Bounds;
        using (var host = new PreviewHost { Opacity = 0, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, ClientSize = control.Size })
        {
            host.Controls.Add(control); control.Dock = DockStyle.Fill; control.Visible = true; host.Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(file); }
            host.Controls.Remove(control); parent.Controls.Add(control); control.Dock = dock; control.Bounds = bounds;
        }
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }
    private static void ScheduledLevels(string root, SongInfo song)
    {
        string constant = Path.Combine(root, "constant.wav"); Wave(constant, 12, .1f);
        using (var mixer = new StemMixer(song.Audio, song.Folder))
        {
            var mix = new PlayerMix(); mix.Solo[0] = true; mix.Muted[1] = true; mixer.ApplyMix(mix); mixer.Volume = 0;
            mixer.ScheduleStem(1, Rate, constant, 1, .5f, .8f);
            var data = new float[Rate * 4]; Check(mixer.Read(data, 0, data.Length) == data.Length, "A scheduled mix must fill a callback spanning its source change");
            Check(data.Take(Rate * 2).All(sample => sample == 0), "Before the authored stem switch the old master and muted/solo settings must apply");
            Check(Math.Abs(data[Rate * 3] - .04) < .00001, "An incoming saved stem must apply its own level and volume even if the outgoing lane was muted or the outgoing master was zero");
        }
    }
    private static void Tests(string root, string preview)
    {
        SongInfo one = Song(root, "One"), two = Song(root, "Two", 18), outside = Song(root, "Outside"); var songs = new List<SongInfo> { one, two, outside };
        ScheduledLevels(root, one);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle; Set(form, "songs", songs);
            Finish((Task)Invoke(form, "LoadSong", one, false, 0.0, false)); Finish((Task)Get(form, "currentAnalysisTask"));
            Check(((GlassButton)Get(form, "saveMixButton")).Text == "Save mix" && !((GlassButton)Get(form, "saveMixCopyButton")).Visible, "A normal song's + menu must offer Save mix and hide new-copy actions");
            var export = (GlassButton)Get(form, "exportMixButton"); var actionsPanel = (Control)Get(form, "nowPlayingActions");
            Check(export.Text == "Export MP3" && export.Enabled && export.Bottom <= actionsPanel.Height - 12 && export.Top > ((GlassButton)Get(form, "saveMixButton")).Bottom, "The song's + menu must offer an enabled MP3 export below Save mix within the popup");
            Set(form, "editingTuning", true); Invoke(form, "UpdateSaveMixButton"); Check(!export.Enabled, "Export must wait for tempo and key preparation to finish"); Set(form, "editingTuning", false); Invoke(form, "UpdateSaveMixButton");
            SongPlaylist playlist = (SongPlaylist)Invoke(form, "StorePlaylist", "Evening transitions");
            Check(playlist != null && (bool)Invoke(form, "AddPlaylistItem", playlist, one.Audio), "A named custom playlist must accept songs");
            Check(!(bool)Invoke(form, "AddPlaylistItem", playlist, one.Audio.ToUpperInvariant()) && playlist.Items.Count == 1, "Playlist membership must reject duplicate songs independent of path casing");
            Finish((Task)Invoke(form, "ReplaceFromDrop", two, 0));
            byte[] exportCover = (byte[])Invoke(form, "CaptureExportCover");
            using (var stream = new MemoryStream(exportCover))
            using (var image = new Bitmap(stream))
            {
                Check(image.GetPixel(128, 256).R > 240 && image.GetPixel(128, 256).G > 110 && image.GetPixel(128, 256).G < 145 && image.GetPixel(384, 256).R < 12 && image.GetPixel(384, 256).G > 110 && image.GetPixel(384, 256).B > 110, "Export must capture the original cover on the left and displayed added cover on the right");
            }
            Finish((Task)Invoke(form, "ApplyTuning", (double?)150, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { one.Audio, 1 }, { two.Audio, -1 } }));
            var mixer = (StemMixer)Get(form, "mixer"); mixer.SetStemShift(0, -2); mixer.StartStemLoop(2, 2.1, 150, .08);
            var mix = (PlayerMix)Get(form, "mix"); mix.Solo[0] = true; mix.Levels[0] = .37f; mix.Muted[3] = true; ((MixSlider)Get(form, "volume")).Value = 48; Invoke(form, "ApplyMix");
            Check((bool)Invoke(form, "StoreMix", "One + Two", true), "The authored fixture mix must save");
            var saved = ((List<SavedMix>)Get(form, "savedMixes")).Single();
            Check((string)Invoke(form, "CurrentPlaylistItem") == "mix:" + saved.Id, "Adding while a saved mix is open must select the mix rather than its original song");
            Check((bool)Invoke(form, "AddPlaylistItem", playlist, (string)Invoke(form, "CurrentPlaylistItem")), "A custom playlist must accept saved mixes alongside songs");
            Check(((GlassButton)Get(form, "saveMixButton")).Text == "Save changes" && ((GlassButton)Get(form, "saveMixCopyButton")).Text == "New mix", "An opened mix's + menu must offer Save changes and New mix");
            Check(export.Enabled && export.Top > ((GlassButton)Get(form, "saveMixCopyButton")).Bottom && export.Bottom <= actionsPanel.Height - 12, "A saved mix's + menu must fit Export MP3 below New mix");
            using (var snapshot = mixer.ExportCopy())
            {
                Check(snapshot.TotalFrames == mixer.TotalFrames && snapshot.StemState(0).Shift == -2 && snapshot.StemState(2).Looping && snapshot.StemDuration(0) == mixer.StemDuration(0), "Exporting an edited mix must retain transformed tempo/key files, offsets, loops and donor tails");
            }
            Check((bool)Invoke(form, "StoreMix", "Different mix of One", true), "Two mixes of the same original must be independently savable");
            SavedMix second = ((List<SavedMix>)Get(form, "savedMixes")).Last(); Invoke(form, "AddPlaylistItem", playlist, "mix:" + second.Id); Invoke(form, "AddPlaylistItem", playlist, two.Audio);
            Invoke(form, "ShowLibrary", true); ((GlassButton)Get(form, "playlistsTab")).PerformClick();
            var grid = (Control)Get(form, "libraryGrid"); Check(grid.Controls.OfType<LibraryCard>().Count() == 2, "Playlists must show My mixes and every custom playlist");
            grid.Controls.OfType<LibraryCard>().Single(card => card.Title == playlist.Name).AccessibilityObject.DoDefaultAction();
            Check(grid.Controls.OfType<LibraryCard>().Count() == 4 && ((Label)Get(form, "libraryHeading")).Text == playlist.Name, "Opening a playlist must list both songs and distinct saved mixes");
            ((TextBox)Get(form, "librarySearch")).Text = "Different mix"; Check(grid.Controls.OfType<LibraryCard>().Single().Title == second.Name, "Custom playlist search must match saved mix names"); ((TextBox)Get(form, "librarySearch")).Text = "";
            Invoke(form, "UpdateTransport"); if (preview != null) Preview((Control)Get(form, "browser"), preview);
            Task<bool> start = (Task<bool>)Invoke(form, "StartPlaylistAutoPlay", playlist); Finish(start); Check(start.Result, "Playlist Auto play must load and start a shuffled session");
            ((WaveOutEvent)Get(form, "output")).Pause();
            var queue = (PlaybackQueue)Get(form, "songQueue");
            Check((bool)Get(form, "autoPlayEnabled") && (string)Get(form, "autoPlaylistId") == playlist.Id, "Auto play must retain the selected playlist's scope");
            Check(queue.Items.Select(item => item.Audio).OrderBy(item => item).SequenceEqual(playlist.Items.OrderBy(item => item)), "Shuffle must queue each playlist song and mix exactly once, including mixes sharing one original");
            Check(Invoke(form, "AddQueuedSong", outside, queue.Items.Count, false) == null && queue.Items.All(item => item.Audio != outside.Audio), "An active playlist session must not accept unrelated library songs");
            var settings = new AutoPlaySettings { RampBars = .25, StemBars = .25 }; Set(form, "autoSettings", settings);
            // Force one ordinary-to-authored transition independent of shuffle order.
            Finish((Task)Invoke(form, "LoadSong", one, false, 0.0, false));
            queue.Clear(); queue.Follow(one.Audio); PlaybackQueueEntry incoming = queue.Add("mix:" + saved.Id, 1, true);
            Finish((Task)Invoke(form, "PrepareAutoTransition"));
            var plan = (PreparedAutoTransition)Get(form, "autoTransition"); Check(plan != null && plan.QueueEntryId == incoming.Id && plan.PlaylistMix != null, "The transition engine must prepare a saved mix as the incoming playlist entry");
            Check(plan.PlaylistMix.MixSources.Active[0].Song.Audio == two.Audio && plan.Next.StemDuration(0) > plan.Next.Duration, "Incoming mix preparation must retain authored source choices and full donor tails");
            Check(plan.Traits.Bpm == 150 && plan.Traits.Tonic == 10 && plan.PlaylistMix.NativeTraits.Tonic == 9 && plan.Next.TotalFrames == 153600, "Mix transitions must use the saved tempo and transposed key while retaining the original traits for editing");
            Check(Math.Abs(plan.PlaylistMix.MixSources.Active[0].Plan.Pitch - Camelot.Pitch(-1)) < .000001, "Incoming donor stems must retain their independently saved key shift");
            var playback = (PlayerPlayback)Get(form, "playback"); var buffer = new float[4096]; int reads = 0;
            while (playback.Current != plan.Next && reads++ < 200) { Check(playback.Read(buffer, 0, buffer.Length) == buffer.Length, "A playlist transition must use uninterrupted audio callbacks"); Invoke(form, "PollAutoTransition"); }
            Invoke(form, "PollAutoTransition");
            Check(playback.Current == plan.Next && (string)Get(form, "loadedMixId") == saved.Id, "The authored mix must become the current editable mix after its handoff");
            mix = (PlayerMix)Get(form, "mix"); mixer = (StemMixer)Get(form, "mixer");
            Check(mix.Levels[0] == .37f && mix.Solo[0] && mix.Muted[3] && ((MixSlider)Get(form, "volume")).Value == 48, "Incoming saved volume, solo and mute settings must reach both playback and editing controls");
            Check((double?)Get(form, "sharedBpm") == 150 && (double)Get(form, "originalTempo") == 1.25 && (int)Invoke(form, "KeyShift", Get(form, "current")) == 1, "After promotion the mix must retain editable shared tempo and independent key controls");
            Check(Enumerable.Range(0, 4).All(lane => !mixer.StemState(lane).Changed) && saved.Timing[0].Shift == -2 && saved.Timing[2].LoopEnd > saved.Timing[2].LoopStart, "Auto play must reset live timing while preserving the saved mix's offsets and loops");
            Check((string)Get(form, "autoPlaylistId") == playlist.Id && queue.Current.Audio == "mix:" + saved.Id, "Promoting a mix must preserve playlist scope and its distinct queue identity");
            Finish((Task)Invoke(form, "LoadSong", one, false, 0.0, false));
            queue.Clear(); queue.Follow(one.Audio); queue.Add("mix:" + saved.Id, 1, true);
            Finish((Task)Invoke(form, "PrepareAutoTransition")); plan = (PreparedAutoTransition)Get(form, "autoTransition");
            playback = (PlayerPlayback)Get(form, "playback"); WaveOutEvent device = (WaveOutEvent)Get(form, "output"); reads = 0;
            while (playback.Current != plan.Bridge && reads++ < 200) playback.Read(buffer, 0, buffer.Length);
            Finish((Task)Invoke(form, "MoveSong", 1)); device.Pause();
            Check(Get(form, "autoTransition") == null && playback.Current == plan.Next && Get(form, "output") == device && (string)Get(form, "loadedMixId") == saved.Id,
                "Next during a playlist mix transition must promote its prepared mixer immediately through the same output");
            mix = (PlayerMix)Get(form, "mix");
            Check(mix.Levels[0] == .37f && mix.Solo[0] && mix.Muted[3] && ((MixSlider)Get(form, "volume")).Value == 48 && ((StemSourceState)Get(form, "stemSources")).Active[0].Song.Audio == two.Audio,
                "Skipping to an incoming saved mix must retain its authored sources, volume, mute and solo choices");
            Check((string)Get(form, "autoPlaylistId") == playlist.Id && queue.Current.Audio == "mix:" + saved.Id,
                "Skipping a playlist transition must retain its scope and incoming mix queue identity");
            Invoke(form, "StopAutoTimeline"); Task pending = Get(form, "autoPreloadTask") as Task; if (pending != null) Finish(pending);
            Invoke(form, "SetAutoPlay", false); Check(Get(form, "autoPlaylistId") == null, "Turning Auto play off must end the playlist session");
            Finish((Task)Invoke(form, "OpenSavedMix", saved)); Check(((StemMixer)Get(form, "mixer")).StemState(0).Shift == -2, "Opening a mix for manual editing after Auto play must restore its saved offsets");
            if (preview != null)
            {
                ((ToolStripDropDown)Get(form, "nowPlayingMenu")).Opacity = 0; Invoke(form, "ShowNowPlayingMenu"); Application.DoEvents();
                var popup = (ToolStripDropDown)Get(form, "nowPlayingMenu"); var plus = (Control)Get(form, "nowPlayingAddButton");
                Check(popup.Visible && popup.Bottom <= plus.PointToScreen(Point.Empty).Y, "The + action window must open above its button");
                Check(((GlassButton)Get(form, "addToPlaylistButton")).Visible && ((GlassButton)Get(form, "saveMixButton")).Visible && ((GlassButton)Get(form, "saveMixCopyButton")).Visible && export.Visible, "The popup must visibly show all four actions for an opened mix");
                var actions = (Control)Get(form, "nowPlayingActions"); using (var bitmap = new Bitmap(actions.Width, actions.Height)) { actions.DrawToBitmap(bitmap, actions.ClientRectangle); bitmap.Save(Path.Combine(Path.GetDirectoryName(preview), "playlist-plus-menu-preview.png")); }
                popup.Close();
            }
            Invoke(form, "RemovePlaylistItem", playlist, one.Audio); Check(!playlist.Items.Contains(one.Audio), "Removing a playlist item must retain the other entries");
            object[] mappings = { new Dictionary<string, object> { { "audio", two.Audio }, { "newAudio", two.Audio + ".renamed" } } }; Invoke(form, "RemapPlaylists", (object)mappings);
            Check(playlist.Items.Contains(two.Audio + ".renamed") && playlist.Items.Contains("mix:" + saved.Id), "Artist renames must update song paths and retain stable mix identities"); Invoke(form, "SavePreferences");
            Invoke(form, "CloseAudio");
        }
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle; Set(form, "songs", songs);
            var playlists = (List<SongPlaylist>)Get(form, "songPlaylists"); Check(playlists.Count == 1 && playlists[0].Items.Count == 3, "Custom playlists and mixed membership must survive a new player instance");
            Check(Get(form, "autoPlaylistId") == null, "A playlist session must not restart automatically after reopening the app");
            Invoke(form, "DeletePlaylist", playlists[0]); Check(playlists.Count == 0 && ((List<SavedMix>)Get(form, "savedMixes")).Count == 2, "Deleting a playlist must preserve its saved mixes and source songs");
            SongPlaylist empty = (SongPlaylist)Invoke(form, "StorePlaylist", "Empty"); Task<bool> open = (Task<bool>)Invoke(form, "StartPlaylistAutoPlay", empty); Finish(open); Check(!open.Result && !(bool)Get(form, "autoPlayEnabled"), "An empty playlist must explain how to add items without starting an unrelated song"); Invoke(form, "DeletePlaylist", empty);
            Set(form, "myMixesView", true); SongPlaylist allMixes = (SongPlaylist)Invoke(form, "ViewedPlaylist");
            Check(allMixes.Name == "My mixes" && allMixes.Items.Count == 2, "The My mixes collection must also provide a playlist Auto play target");
            open = (Task<bool>)Invoke(form, "StartPlaylistAutoPlay", allMixes); Finish(open); Check(open.Result, "My mixes Auto play must start a shuffled mix session"); ((WaveOutEvent)Get(form, "output")).Pause();
            var queue = (PlaybackQueue)Get(form, "songQueue"); Check(queue.Items.Count == 2 && queue.Items.All(item => item.Audio.StartsWith("mix:")), "My mixes Auto play must queue only saved mixes");
            SongInfo last = (SongInfo)Invoke(form, "PlaylistSong", queue.Items.Last().Audio); Finish((Task)Invoke(form, "LoadSong", last, false, 0.0, false)); queue.Select(queue.Items.Last().Id); Invoke(form, "FillAutoQueue");
            Check(queue.Items.Count == 2 && queue.Next == null, "Exhausting a playlist must not pull in songs from the full library");
            Invoke(form, "SetAutoPlay", false);
            SavedMix deleted = ((List<SavedMix>)Get(form, "savedMixes")).First(); SongPlaylist references = (SongPlaylist)Invoke(form, "StorePlaylist", "References"); Invoke(form, "AddPlaylistItem", references, "mix:" + deleted.Id); Invoke(form, "DeleteSavedMix", deleted);
            Check(references.Items.Count == 0 && ((List<SavedMix>)Get(form, "savedMixes")).Count == 1, "Deleting a saved mix must remove playlist references without deleting the other mixes");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "stemkit-playlist-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Tests(root, args.Length > 0 ? args[0] : null); Console.WriteLine("Playlists: " + checks + " checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "stemkit-playlist-tests-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
