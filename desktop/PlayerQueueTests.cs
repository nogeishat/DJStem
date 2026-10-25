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

internal static class PlayerQueueTests
{
    private static int checks;
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static object Get(object value, string name) { return value.GetType().GetField(name, Hidden).GetValue(value); }
    private static void Set(object value, string name, object item) { value.GetType().GetField(name, Hidden).SetValue(value, item); }
    private static object Invoke(object value, string name, params object[] arguments) { return value.GetType().GetMethod(name, Hidden).Invoke(value, arguments); }
    private static void Finish(Task task)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < 30000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Queue playback must complete without blocking the UI"); task.GetAwaiter().GetResult();
    }
    private static void FinishScroll(SmoothList list)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (Math.Abs(list.ScrollOffset - list.ScrollTarget) > .25 && watch.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(Math.Abs(list.ScrollOffset - list.ScrollTarget) <= .25, "Queue wheel movement must settle promptly");
    }
    private static DataObject SongData(string audio) { var data = new DataObject(); data.SetData("StemKit.LibrarySong.v1", false, audio); return data; }
    private static DataObject EntryData(string id) { var data = new DataObject(); data.SetData("StemKit.PlaybackQueueEntry.v1", false, id); return data; }
    private static void Model()
    {
        var queue = new PlaybackQueue();
        PlaybackQueueEntry one = queue.Add("one", 0), two = queue.Add("two", 1), repeat = queue.Add("three", 2);
        Check(queue.Add("ONE", 3) == null && queue.Items.Count == 3, "Queued songs must be unique regardless of path casing");
        queue.Move(repeat.Id, 0); Check(queue.Items.SequenceEqual(new[] { repeat, one, two }), "A queue entry must move independently to the front");
        queue.Move(repeat.Id, 3); Check(queue.Items.SequenceEqual(new[] { one, two, repeat }), "Dragging to the last insertion slot must move a row to the end");
        queue.Move(one.Id, 2); Check(queue.Items.SequenceEqual(new[] { two, one, repeat }), "Moving down one row must use insertion-slot coordinates correctly");
        queue.Remove(one.Id); Check(queue.Items.SequenceEqual(new[] { two, repeat }), "Removal must leave the other queued songs intact");
        PlaybackQueueEntry suggestion = queue.Add("shuffle", 0, true);
        Check(queue.Next == suggestion && queue.Previous == null, "A fresh queue must begin at its first entry");
        queue.Select(two.Id); Check(queue.Previous == suggestion && queue.Next == repeat && queue.Items.Count == 3, "Moving the cursor must retain played songs and expose both navigation directions");
        queue.Select(repeat.Id); Check(queue.Previous == two && queue.Next == null, "Reaching the last entry must keep the session history available");
        Check(queue.HasPlayed("TWO") && queue.Add("two", 3) == null, "Playing a song must prevent adding it again during the session");
        queue.Move(two.Id, 3); Check(queue.Upcoming == null, "Moving a played row after the current song must not make automatic playback repeat it");
        queue.Move(two.Id, 2);
        queue.Select(two.Id); queue.Move(suggestion.Id, 1); Check(!suggestion.Automatic && queue.Current == two, "Reordering must retain current-song identity");
        queue.Remove(two.Id); Check(queue.Add("two", queue.Items.Count) == null, "Deleting history must not let a played song reenter the session");
        queue.Remap(audio => "renamed/" + audio); Check(queue.HasPlayed("renamed/two") && !queue.CanAdd("renamed/two"), "Renaming paths must preserve the played ledger even for removed history");
        queue.Clear(); Check(queue.Items.Count == 0 && queue.Current == null && queue.Next == null && queue.Previous == null, "Ending a session must clear entries and navigation together");
        Check(queue.Add("renamed/two", 0) != null, "A new session must allow songs played in earlier sessions");
        PlaybackQueueEntry unplayed = queue.Add("unplayed", queue.Items.Count, true); queue.Remove(unplayed.Id);
        Check(!queue.HasPlayed("unplayed") && queue.CanAdd("UNPLAYED") && queue.Add("unplayed", queue.Items.Count) != null, "Removing an unplayed queued song must leave it unplayed and available to queue again");
    }
    private static void ManualPriority(string root)
    {
        foreach (bool autoPlay in new[] { false, true })
        using (var form = new PlayerForm(root, root))
        {
            SongInfo[] songs = Enumerable.Range(0, 6).Select(index => new SongInfo { Title = "Priority " + index, Artist = "Artist", Audio = Path.Combine(root, "priority-" + index + ".mp3"), Folder = root, Cover = Path.Combine(root, "missing.jpg") }).ToArray();
            Set(form, "songs", songs.ToList()); Set(form, "current", songs[0]); Set(form, "autoPlayEnabled", autoPlay);
            var queue = (PlaybackQueue)Get(form, "songQueue");
            var firstAuto = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[1], 0, true);
            var secondAuto = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[2], queue.Items.Count, true);
            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                Set(form, "autoCancellation", cancellation);
                Set(form, "autoTransition", new PreparedAutoTransition { Song = songs[1], QueueEntryId = firstAuto.Id });
                var firstManual = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[3], queue.Items.Count, false);
                Check(queue.Next == firstManual && queue.Items[2] == firstAuto && cancellation.IsCancellationRequested && Get(form, "autoTransition") == null, "A manual choice must take next priority and cancel a different pending transition with Auto play " + autoPlay);
                Set(form, "autoCancellation", null);
                var secondManual = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[4], queue.Items.Count, false);
                Check(queue.Items.Skip(1).SequenceEqual(new[] { firstManual, secondManual, firstAuto, secondAuto }), "Manual choices must retain their arrival order above the existing automatic queue");
                var laterAuto = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[5], 0, true);
                Check(queue.Items[1] == firstManual && queue.Items[2] == secondManual && laterAuto.Automatic, "A new automatic addition must stay below every manual choice");
                var promoted = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", new SongInfo { Audio = songs[2].Audio.ToUpperInvariant(), Title = songs[2].Title }, queue.Items.Count, false);
                Check(promoted == secondAuto && !promoted.Automatic && queue.Items[3] == promoted && queue.Items.Count == 6, "Manually choosing an automatic song must promote its existing row without duplicating it");
                Check(Invoke(form, "AddQueuedSong", songs[2], queue.Items.Count, true) == null && Invoke(form, "AddQueuedSong", songs[2], queue.Items.Count, false) == null, "Neither an automatic refill nor another manual click may duplicate a manual choice");
                Check((bool)Invoke(form, "DropIntoQueue", SongData(songs[1].Audio), queue.Items.Count) && !firstAuto.Automatic && queue.Items[4] == firstAuto, "Dragging an automatic suggestion from the library must promote the same row into the manual group");
                Invoke(form, "MoveQueuedSong", laterAuto.Id, queue.Items.Count);
                Check(!laterAuto.Automatic && queue.Items.Last() == laterAuto, "Manually moving an automatic row must turn it into a manual choice");
                queue.Select(firstManual.Id); queue.Select(secondManual.Id); Set(form, "current", songs[4]);
                Invoke(form, "RemoveQueuedSong", firstManual.Id);
                Check(queue.HasPlayed(songs[3].Audio) && !queue.CanAdd(songs[3].Audio) && Invoke(form, "AddQueuedSong", songs[3], queue.Items.Count, true) == null && Invoke(form, "AddQueuedSong", songs[3], queue.Items.Count, false) == null, "A played manual choice must remain excluded for the session even after its history row is removed");
                Invoke(form, "RemoveQueuedSong", firstAuto.Id);
                Check(!queue.HasPlayed(songs[1].Audio) && Invoke(form, "AddQueuedSong", songs[1], queue.Items.Count, true) != null, "Removing a manual choice before it plays must leave it eligible for future automatic selection");
                queue.Clear(); Set(form, "current", songs[0]);
                var preparedEntry = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[1], 0, true);
                var pending = new PreparedAutoTransition { Song = songs[1], QueueEntryId = preparedEntry.Id };
                Set(form, "autoTransition", pending);
                var sameHead = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[1], queue.Items.Count, false);
                Check(sameHead == preparedEntry && !sameHead.Automatic && queue.Next == sameHead && Get(form, "autoTransition") == pending, "Promoting the already prepared next song must keep its row identity and pending transition");
                Set(form, "autoTransition", null);
                SongInfo bridgeSong = Song(root, "Priority bridge " + autoPlay, 120);
                using (var bridge = new StemMixer(bridgeSong.Audio, ""))
                using (var audio = new PlayerPlayback(bridge))
                {
                    var active = new PreparedAutoTransition { Song = songs[1], QueueEntryId = preparedEntry.Id, Bridge = bridge, Reference = bridge, Next = bridge, Traits = new MusicTraits { Bpm = 120 }, Timing = new AutoTransitionTiming(120, 120, 16000, new AutoPlaySettings()) };
                    Set(form, "mixer", bridge); Set(form, "playback", audio); Set(form, "autoTransition", active);
                    try
                    {
                        var following = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", songs[2], queue.Items.Count, false);
                        Check(Get(form, "autoTransition") == active && audio.Current == bridge && queue.Items.IndexOf(following) == queue.Items.IndexOf(preparedEntry) + 1, "A manual choice during an active transition must become the following song without interrupting the current handoff");
                        Check(Invoke(form, "AddQueuedSong", songs[1], queue.Items.Count, false) == null && Get(form, "autoTransition") == active, "The song already transitioning must not be duplicated or moved by manual queueing");
                    }
                    finally { Set(form, "autoTransition", null); Set(form, "playback", null); Set(form, "mixer", null); }
                }
            }
        }
    }
    private static SongInfo Song(string root, string title, double bpm)
    {
        string file = Path.Combine(root, title + ".wav");
        using (WaveFileWriter writer = new WaveFileWriter(file, new WaveFormat(16000, 16, 2))) writer.Write(new byte[16000 * 4], 0, 16000 * 4);
        string artwork = Path.Combine(root, title + "-cover.png");
        using (var cover = new Bitmap(96, 96))
        using (Graphics graphics = Graphics.FromImage(cover))
        {
            graphics.Clear(Color.FromArgb(60 + (int)bpm % 90, 70, 160)); PlayerTheme.Icon(graphics, "wave", new Rectangle(14, 14, 68, 68), Color.White); cover.Save(artwork);
        }
        return new SongInfo { Audio = file, Folder = root, Cover = artwork, Title = title, Artist = "An artist", Bpm = bpm, Key = "A minor" };
    }
    private static void Interface(string root)
    {
        SongInfo one = Song(root, "First song", 120), two = Song(root, "Second song", 128), three = Song(root, "Third song", 132);
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); };
            IntPtr handle = form.Handle; Set(form, "songs", new List<SongInfo> { one, two, three }); Set(form, "current", one);
            var mixer = new StemMixer(one.Audio, ""); Set(form, "mixer", mixer); Set(form, "playback", new PlayerPlayback(mixer));
            var queue = (PlaybackQueue)Get(form, "songQueue"); var list = (QueueList)Get(form, "queueList");
            Invoke(Get(form, "queueButton"), "OnClick", EventArgs.Empty);
            Check((bool)Get(form, "queueVisible") && ((GlassButton)Get(form, "queueButton")).Parent == Get(form, "transport"), "The transport Queue button must open the upcoming songs panel");
            Check(((GlassPanel[])Get(form, "lanes")).All(lane => !lane.Visible), "The queue must use the stem area while keeping transport controls available");
            Check((bool)Invoke(form, "DropIntoQueue", SongData(two.Audio), 0) && (bool)Invoke(form, "DropIntoQueue", SongData(three.Audio), 2), "Library drag data must add songs after the current session song");
            Point drop = list.PointToScreen(new Point(100, 5)); var drag = new DragEventArgs(SongData(three.Audio), 0, drop.X, drop.Y, DragDropEffects.Copy, DragDropEffects.None);
            Invoke(list, "OnDragEnter", drag); Check(drag.Effect == DragDropEffects.None, "The queue drop target must reject songs already queued");
            Invoke(list, "OnDragDrop", drag); Check(queue.Items.Count == 3 && queue.Next.Audio == two.Audio, "Rejected duplicate drops must preserve queue order");
            Check(Get(form, "current") == one && Get(form, "mixer") == mixer, "Adding queue songs must leave current playback intact");
            Check(!(bool)Invoke(form, "DropIntoQueue", SongData("missing"), 0), "An unknown song drag must be rejected");
            PlaybackQueueEntry second = queue.Items[1], third = queue.Items[2];
            Invoke(form, "DropIntoQueue", EntryData(third.Id), 1);
            Check(queue.Items.Skip(1).SequenceEqual(new[] { third, second }), "Queue row drag data must reorder future rows");
            Check(((QueueRow)list.Items[0]).Status == "Playing" && ((QueueRow)list.Items[1]).Status == "Next" && ((QueueRow)list.Items[1]).Song.MusicDetails.Contains("132 BPM"), "Rows must distinguish current and next songs and retain BPM/key details");
            list.SelectedIndex = 1; list.ScrollBy(17);
            object[] beforeRefresh = list.Items.ToArray(); double offset = list.ScrollOffset, destination = list.ScrollTarget;
            int selectionChanges = 0; list.SelectedIndexChanged += delegate { selectionChanges++; };
            Invoke(form, "RenderPlaybackQueue");
            Check(list.Items.SequenceEqual(beforeRefresh) && selectionChanges == 0 && list.SelectedIndex == 1 && list.ScrollOffset == offset && list.ScrollTarget == destination, "Unchanged queue refreshes must reuse rows and preserve selection and unfinished scrolling");
            list.TopIndex = 0;
            int invalidations = 0; Rectangle dirty = Rectangle.Empty;
            list.Invalidated += delegate(object sender, InvalidateEventArgs args) { invalidations++; dirty = args.InvalidRect; };
            two.Bpm = 129; Invoke(form, "RenderPlaybackQueue");
            Check(list.Items.SequenceEqual(beforeRefresh) && invalidations == 1 && dirty == list.GetItemRectangle(2), "Changed queue metadata must repaint only its visible row without rebuilding the queue");
            two.Bpm = 128; Invoke(form, "RenderPlaybackQueue");
            list.SelectedIndex = 1; Invoke(list, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Down));
            Check(queue.Items.Skip(1).SequenceEqual(new[] { second, third }), "Keyboard reordering must use the same queue as drag-and-drop");
            Check(!(bool)Invoke(form, "DropIntoQueue", SongData(two.Audio), 3) && queue.Items.Count == 3, "Library drags must not insert duplicate songs");
            list.SelectedIndex = 1; Invoke(list, "OnKeyDown", new KeyEventArgs(Keys.Delete));
            PlaybackQueueEntry repeat = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", two, 3, false);
            Check(!queue.HasPlayed(two.Audio) && repeat != null && repeat.Id != second.Id, "An unplayed removed row must be queueable again in the same session");
            Check(queue.Items.Skip(1).SequenceEqual(new[] { third, repeat }), "Delete must remove only the selected future occurrence");
            IntPtr listHandle = list.Handle;
            Check(list.InsertionAt(new Point(100, 5)) == 0 && list.InsertionAt(new Point(100, list.ItemHeight - 5)) == 1 && list.InsertionAt(new Point(100, list.Height - 1)) == queue.Items.Count, "Drops must identify insertion before, after, and below rows");
            Rectangle row = list.GetItemRectangle(1); Invoke(list, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, list.ClientSize.Width - 12, row.Top + 20, 0));
            Check(queue.Items.Count == 2 && queue.Next == repeat, "The row's remove icon must remove that future row");
            var extra = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", three, 2, false);
            // Artist rename mappings cover folder prefixes, just as the backend does.
            string renamedRoot = Path.Combine(root, "renamed");
            object[] mappings = { new Dictionary<string, object> { { "directory", root }, { "newDirectory", renamedRoot } } };
            Invoke(form, "RemapPlaybackQueue", (object)mappings);
            Check(queue.Items.All(item => item.Audio.StartsWith(renamedRoot, StringComparison.OrdinalIgnoreCase)), "Artist renaming must remap every queued song path");
            queue.Current.Audio = one.Audio; repeat.Audio = two.Audio; extra.Audio = three.Audio; Invoke(form, "RenderPlaybackQueue");
            Set(form, "refreshing", true); form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show();
            ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
            ((Label)Get(form, "songTitle")).Text = one.Title; ((Label)Get(form, "artistLabel")).Text = one.Artist;
            Set(form, "currentNativeTraits", new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }); Invoke(form, "UpdateTuningHeader"); Invoke(form, "RenderSongs");
            using (Bitmap preview = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                string file = Path.Combine(Directory.GetCurrentDirectory(), "data", "queue-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(file)); preview.Save(file);
            }
            form.Hide(); Set(form, "refreshing", false);
            form.ClientSize = new Size(1104, 701); Invoke(form, "LayoutContent");
            GlassButton button = (GlassButton)Get(form, "queueButton"); MixSlider volume = (MixSlider)Get(form, "volume");
            Check(button.Right + 8 <= button.Parent.Controls["VolumeIcon"].Left && volume.Right <= button.Parent.Width, "Queue and volume controls must fit at the minimum window size: queue=" + button.Bounds + ", icon=" + button.Parent.Controls["VolumeIcon"].Bounds + ", volume=" + volume.Bounds + ", transport=" + button.Parent.Size);
            Invoke(Get(form, "queueButton"), "OnClick", EventArgs.Empty); Check(!(bool)Get(form, "queueVisible"), "Queue button must return to the stem controls");
            Invoke(form, "SavePreferences"); Invoke(form, "CloseAudio");
            var saved = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root, "data", "player-settings.json")));
            Check(!saved.ContainsKey("playbackQueue"), "Session queues must not be written into saved preferences");
        }
        using (PlayerForm restored = new PlayerForm(root, root))
        {
            var queue = (PlaybackQueue)Get(restored, "songQueue");
            Check(queue.Items.Count == 0 && queue.CurrentId == null, "Reopening the player must start with an empty session queue");
        }
    }
    private static void Playback(string root)
    {
        Directory.CreateDirectory(root); SongInfo one = Song(root, "Current", 120), two = Song(root, "Queued", 132), three = Song(root, "Fresh next", 140), four = Song(root, "Normal fallback", 145), five = Song(root, "Missing next", 150);
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); };
            IntPtr handle = form.Handle; Set(form, "songs", new List<SongInfo> { one, two, three, four, five }); ((MixSlider)Get(form, "volume")).Value = 0;
            Finish((Task)Invoke(form, "LoadSong", one, false, 0.0, false));
            var queue = (PlaybackQueue)Get(form, "songQueue"); var entry = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", two, 0, false);
            StemMixer mixer = (StemMixer)Get(form, "mixer"); mixer.Seek(mixer.Duration);
            Task<bool> stopped = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(stopped);
            Check(!stopped.Result && Get(form, "current") == one, "Stopped or paused playback at the end must not advance without a natural finish");
            mixer.Seek(0); var output = (WaveOutEvent)Get(form, "output"); output.Play(); Stopwatch ending = Stopwatch.StartNew();
            while ((output.PlaybackState != PlaybackState.Stopped || !(bool)Get(form, "queueAdvancePending")) && ending.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            Check(output.PlaybackState == PlaybackState.Stopped && (bool)Get(form, "queueAdvancePending"), "An actual audio-device natural finish must signal queue advancement");
            Task<bool> natural = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(natural);
            Check(natural.Result && Get(form, "current") == two && queue.Items.Count == 2 && queue.Current == entry, "A natural finish must load the next song while retaining the session queue with Auto play off");
            Check(((WaveOutEvent)Get(form, "output")).PlaybackState == PlaybackState.Playing, "The queued song must start automatically");
            ((WaveOutEvent)Get(form, "output")).Pause();
            Invoke(form, "AddQueuedSong", one, 0, false); Invoke(form, "AddQueuedSong", one, 1, false);
            Check(queue.Items.Count == 2, "Already played songs must not be appended through manual queueing");
            Invoke(form, "AddQueuedSong", three, queue.Items.Count, false);
            Finish((Task)Invoke(form, "MoveSong", 1));
            Check(Get(form, "current") == three && queue.Items.Count == 3 && queue.Next == null, "Next song must honor the unique queued order");
            ((WaveOutEvent)Get(form, "output")).Pause();
            Invoke(form, "AddQueuedSong", four, queue.Items.Count, false);
            Set(form, "autoPlayEnabled", true); Finish((Task)Invoke(form, "PrepareAutoTransition"));
            Check(Get(form, "autoTransition") == null && queue.Items.Count == 4, "Auto play must keep a queued song that cannot use a stem transition");
            ((StemMixer)Get(form, "mixer")).Seek(((StemMixer)Get(form, "mixer")).Duration); Set(form, "queueAdvancePending", true); ((WaveOutEvent)Get(form, "output")).Stop();
            Task<bool> fallback = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(fallback);
            Check(fallback.Result && Get(form, "current") == four && queue.Items.Count == 4 && queue.Next == null, "Auto play must advance normally to a fresh queued song when stems are unavailable");
            ((WaveOutEvent)Get(form, "output")).Pause(); Invoke(form, "AddQueuedSong", five, queue.Items.Count, false);
            queue.Next.Audio = Path.Combine(root, "missing.wav"); Set(form, "queueAdvancePending", true); ((StemMixer)Get(form, "mixer")).Seek(((StemMixer)Get(form, "mixer")).Duration);
            ((WaveOutEvent)Get(form, "output")).Stop(); Task<bool> missing = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(missing);
            Check(!missing.Result && queue.Items.Count == 5 && Get(form, "current") == four, "Unavailable queue entries must remain removable without replacing current audio");
            Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio");
        }
    }
    private static void HistoryInterface(string root)
    {
        Directory.CreateDirectory(root);
        var songs = Enumerable.Range(0, 16).Select(index => Song(root, "Session song " + index, 120)).ToList();
        using (var form = new PlayerForm(root, root))
        {
            IntPtr handle = form.Handle; Set(form, "songs", songs); Set(form, "current", songs[12]);
            var queue = (PlaybackQueue)Get(form, "songQueue"); var list = (QueueList)Get(form, "queueList");
            foreach (SongInfo song in songs) queue.Add(song.Audio, queue.Items.Count);
            for (int index = 0; index <= 12; index++) queue.Select(queue.Items[index].Id);
            string currentId = queue.CurrentId, historyId = queue.Items[0].Id, nextId = queue.Items[13].Id;
            Invoke(form, "ShowPlaybackQueue", true);
            Check(list.TopIndex == queue.CurrentIndex && list.GetItemRectangle(queue.CurrentIndex).Top == 0, "Opening the queue must put the current song at the top, with history above it: top=" + list.TopIndex + ", current=" + queue.CurrentIndex + ", bounds=" + list.GetItemRectangle(queue.CurrentIndex) + ", height=" + list.Height + ", count=" + list.Items.Count);
            Check((GetWindowLong(list.Handle, -16) & 0x00300000) == 0, "The queue must hide native scrollbar tracks even when its rows overflow");
            int scroll = list.TopIndex;
            Point wheelAt = list.PointToScreen(new Point(40, 40)); IntPtr wheelPosition = new IntPtr((wheelAt.X & 0xffff) | (wheelAt.Y << 16));
            SendMessage(list.Handle, 0x020A, new IntPtr(120 << 16), wheelPosition);
            FinishScroll(list);
            Check(list.TopIndex < scroll && (GetWindowLong(list.Handle, -16) & 0x00300000) == 0, "Mouse-wheel scrolling must reach history while the scrollbar remains hidden");
            scroll = list.TopIndex; SendMessage(list.Handle, 0x020A, new IntPtr(-120 << 16), wheelPosition);
            FinishScroll(list);
            Check(list.TopIndex > scroll && (GetWindowLong(list.Handle, -16) & 0x00300000) == 0, "Mouse-wheel scrolling must also move down toward upcoming songs without showing the scrollbar");
            list.TopIndex = 0;
            Check(((QueueRow)list.Items[0]).Played && !((QueueRow)list.Items[0]).Locked && !((QueueRow)list.Items[12]).Played, "Played rows must be dimmed and draggable while the current song stays bright");
            string dragged = null; list.BeginEntryDrag = row => dragged = row.Entry.Id;
            Invoke(list, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 100, 20, 0)); Invoke(list, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, 120, 20, 0));
            Check(dragged == historyId, "The native row gesture must allow dragging previously played songs");
            Check((bool)Invoke(form, "DropIntoQueue", EntryData(historyId), 2) && queue.Items[1].Id == historyId && queue.CurrentId == currentId, "History drag-and-drop must reorder earlier songs without changing current playback");
            Invoke(form, "MoveQueuedSong", historyId, queue.Items.Count);
            Check(queue.Items.Last().Id == historyId && queue.Upcoming.Id == nextId && ((QueueRow)list.Items[queue.Items.Count - 1]).Played, "Moving history into the future must retain its tint and keep automatic playback on an unplayed song");
            var playedRow = (QueueRow)list.Items[queue.Items.Count - 1];
            using (var bright = new Bitmap(list.Width, list.ItemHeight))
            using (var dark = new Bitmap(list.Width, list.ItemHeight))
            {
                playedRow.Played = false;
                using (Graphics g = Graphics.FromImage(bright)) Invoke(list, "OnDrawItem", new DrawItemEventArgs(g, list.Font, new Rectangle(0, 0, list.Width, list.ItemHeight), queue.Items.Count - 1, DrawItemState.None));
                playedRow.Played = true;
                using (Graphics g = Graphics.FromImage(dark)) Invoke(list, "OnDrawItem", new DrawItemEventArgs(g, list.Font, new Rectangle(0, 0, list.Width, list.ItemHeight), queue.Items.Count - 1, DrawItemState.None));
                Color before = bright.GetPixel(20, 20), after = dark.GetPixel(20, 20);
                Check(after.R < before.R && after.G < before.G && after.B < before.B, "Played rows must visibly darken their artwork with the tint overlay");
            }
            Invoke(form, "RemoveQueuedSong", historyId);
            Check(!(bool)Invoke(form, "DropIntoQueue", SongData(songs[0].Audio), queue.Items.Count) && queue.HasPlayed(songs[0].Audio), "Removing a played history row must not permit queueing the song again");
            queue.Select(queue.Items.Last().Id); Set(form, "current", songs[15]); Invoke(form, "ShowPlaybackQueue", false); Invoke(form, "ShowPlaybackQueue", true);
            Check(list.TopIndex == queue.CurrentIndex && list.GetItemRectangle(queue.CurrentIndex).Top == 0, "The final queue song must also open at the top rather than being clamped to the last full page");
            Invoke(form, "SelectQueuedSong", currentId); Set(form, "current", songs[12]);
            Set(form, "refreshing", true); form.ShowInTaskbar = false; form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show(); ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
            list.TopIndex = Math.Max(0, queue.CurrentIndex - 2);
            using (var preview = new Bitmap(list.Width, list.Height)) { list.DrawToBitmap(preview, list.ClientRectangle); preview.Save(Path.Combine(Directory.GetCurrentDirectory(), "data", "queue-history-preview.png")); }
            form.Hide();
        }
    }
    private static void Navigation(string root)
    {
        Directory.CreateDirectory(root);
        SongInfo one = Song(root, "Navigation first", 120), two = Song(root, "Navigation second", 132), three = Song(root, "Navigation third", 140);
        foreach (SongInfo song in new[] { one, two, three })
        using (var writer = new WaveFileWriter(song.Audio, new WaveFormat(16000, 16, 2))) writer.Write(new byte[16000 * 4 * 6], 0, 16000 * 4 * 6);
        using (PlayerForm form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            Set(form, "songs", new List<SongInfo> { one, two, three }); ((MixSlider)Get(form, "volume")).Value = 0;
            Finish((Task)Invoke(form, "LoadSong", one, false, 0.0, false));
            var queue = (PlaybackQueue)Get(form, "songQueue");
            Invoke(form, "AddQueuedSong", two, 1, false); Invoke(form, "AddQueuedSong", three, 2, false);
            string[] ids = queue.Items.Select(entry => entry.Id).ToArray();
            try
            {
                Finish((Task)Invoke(form, "MoveSong", 1)); ((WaveOutEvent)Get(form, "output")).Pause();
                ((StemMixer)Get(form, "mixer")).Seek(4.5); Finish((Task)Invoke(form, "MoveSong", -1)); ((WaveOutEvent)Get(form, "output")).Pause();
                Check(Get(form, "current") == one && queue.CurrentIndex == 0, "Previous with Auto play off must load the previous song after more than three seconds, rather than restart the current song");
                Check(queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Next/Previous with Auto play off must preserve every session entry and its order");
                Finish((Task)Invoke(form, "MoveSong", 1)); ((WaveOutEvent)Get(form, "output")).Pause();
                Set(form, "autoPlayEnabled", true); Finish((Task)Invoke(form, "MoveSong", 1)); ((WaveOutEvent)Get(form, "output")).Pause();
                Check(Get(form, "current") == three && queue.CurrentIndex == 2, "Requested Next with unavailable stems must load the queued next song immediately with Auto play on");
                ((StemMixer)Get(form, "mixer")).Seek(4.5); Finish((Task)Invoke(form, "MoveSong", -1)); ((WaveOutEvent)Get(form, "output")).Pause();
                Check(Get(form, "current") == two && queue.CurrentIndex == 1 && (bool)Get(form, "autoPlayEnabled"), "Previous with Auto play on must load the previous song while retaining Auto play");
                Check(queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Requested Next and Previous with Auto play on must retain the complete session list");
                Invoke(form, "ShowPlaybackQueue", true); var list = (QueueList)Get(form, "queueList"); Rectangle target = list.GetItemRectangle(2);
                Invoke(list, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Left, 2, 100, target.Top + 20, 0));
                Stopwatch jump = Stopwatch.StartNew(); while (((bool)Get(form, "loading") || (bool)Get(form, "advancingQueue") || Get(form, "current") != three) && jump.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(Get(form, "current") == three && ((WaveOutEvent)Get(form, "output")).PlaybackState == PlaybackState.Playing && queue.CurrentIndex == 2, "Double-clicking a queue song must immediately play from that queue position");
                ((WaveOutEvent)Get(form, "output")).Pause(); list.TopIndex = 0; target = list.GetItemRectangle(0);
                Invoke(list, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Left, 2, 100, target.Top + 20, 0));
                jump.Restart(); while (((bool)Get(form, "loading") || (bool)Get(form, "advancingQueue") || Get(form, "current") != one) && jump.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(Get(form, "current") == one && queue.CurrentIndex == 0 && queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Double-clicking history must replay its existing row and preserve the session order: current=" + ((SongInfo)Get(form, "current")).Title + ", index=" + queue.CurrentIndex + ", target=" + target + ", scroll=" + list.TopIndex);
                ((WaveOutEvent)Get(form, "output")).Pause();
                Invoke(form, "SetAutoPlay", false);
                Check(queue.Items.Count == 0 && queue.CurrentId == null, "Turning Auto play off must end the session and clear history and upcoming songs");
                Invoke(form, "AddQueuedSong", three, 0, false); Invoke(form, "SetAutoPlay", true);
                Check(queue.Items.Count == 1 && queue.Current.Audio == one.Audio && !queue.Items.Any(entry => ids.Contains(entry.Id)), "Turning Auto play on must begin a fresh session at the current song and exclude unavailable shuffle suggestions");
                Invoke(form, "AddQueuedSong", one, 0, false);
                // Simulate legacy preferences from before queues became session-only.
                File.WriteAllText(Path.Combine(root, "data", "player-settings.json"), new JavaScriptSerializer().Serialize(new { playbackQueue = new[] { new { audio = three.Audio } } }));
                using (PlayerForm restored = new PlayerForm(root, root)) Check(((PlaybackQueue)Get(restored, "songQueue")).Items.Count == 0, "A new player must ignore legacy persisted queue entries");
                Invoke(form, "OnFormClosed", new FormClosedEventArgs(CloseReason.UserClosing));
                Check(queue.Items.Count == 0 && queue.CurrentId == null, "Closing the app must clear the live session queue");
            }
            finally { Invoke(form, "StopAutoTimeline"); Invoke(form, "CloseAudio"); }
        }
    }
    private static void RandomPlayback(string root)
    {
        Directory.CreateDirectory(root); SongInfo one = Song(root, "Random first", 120), two = Song(root, "Random second", 132);
        var missing = new SongInfo { Audio = Path.Combine(root, "missing.wav"), Cover = Path.Combine(root, "missing.jpg"), Title = "Unavailable", Artist = "Artist" };
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            Set(form, "songs", new List<SongInfo> { one, missing, two }); ((MixSlider)Get(form, "volume")).Value = 0;
            Finish((Task)Invoke(form, "LoadSong", one, false, 0.0, false)); var queue = (PlaybackQueue)Get(form, "songQueue");
            ((StemMixer)Get(form, "mixer")).Seek(((StemMixer)Get(form, "mixer")).Duration);
            Task<bool> idle = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(idle);
            Check(!idle.Result && queue.Items.Count == 1 && Get(form, "current") == one, "An idle device at EOF must not start random playback without a natural finish");
            ((StemMixer)Get(form, "mixer")).Seek(0); var output = (WaveOutEvent)Get(form, "output"); output.Play(); var clock = Stopwatch.StartNew();
            while ((output.PlaybackState != PlaybackState.Stopped || !(bool)Get(form, "queueAdvancePending")) && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
            Check((bool)Get(form, "queueAdvancePending") && output.PlaybackState == PlaybackState.Stopped, "A normal song's natural finish must signal random advancement");
            Task<bool> random = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(random); ((WaveOutEvent)Get(form, "output")).Pause();
            Check(random.Result && Get(form, "current") == two && !(bool)Get(form, "autoPlayEnabled") && Get(form, "autoTransition") == null, "With Auto play off and no queued next song, a natural ending must play a different available song normally");
            Check(queue.Items.Count == 2 && queue.Previous.Audio == one.Audio && queue.Current.Audio == two.Audio && queue.Next == null, "The random successor must be appended to session history for Previous/Next");
            string[] firstIds = queue.Items.Select(entry => entry.Id).ToArray(); Finish((Task)Invoke(form, "MoveSong", -1)); ((WaveOutEvent)Get(form, "output")).Pause();
            Check(Get(form, "current") == one && queue.Items.Select(entry => entry.Id).SequenceEqual(firstIds), "Previous must revisit the random session without rebuilding its list");
            ((StemMixer)Get(form, "mixer")).Seek(((StemMixer)Get(form, "mixer")).Duration); Set(form, "queueAdvancePending", true); ((WaveOutEvent)Get(form, "output")).Stop();
            Task<bool> upcoming = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(upcoming);
            Check(!upcoming.Result && Get(form, "current") == one && queue.Items.Select(entry => entry.Id).SequenceEqual(firstIds), "Automatic advancement must skip already played entries after revisiting history");
            Finish((Task)Invoke(form, "PlayQueuedSong", queue.Next)); ((WaveOutEvent)Get(form, "output")).Pause();
            Check(Get(form, "current") == two && queue.Items.Select(entry => entry.Id).SequenceEqual(firstIds), "An explicit queue jump may replay history without adding another occurrence");
            ((StemMixer)Get(form, "mixer")).Seek(((StemMixer)Get(form, "mixer")).Duration); Set(form, "queueAdvancePending", true); ((WaveOutEvent)Get(form, "output")).Stop();
            Task<bool> repeat = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(repeat); ((WaveOutEvent)Get(form, "output")).Pause();
            Check(!repeat.Result && Get(form, "current") == two && queue.Items.Count == 2, "Exhausting the library must stop rather than append a repeated song");
            ((StemMixer)Get(form, "mixer")).Seek(((StemMixer)Get(form, "mixer")).Duration); Set(form, "queueAdvancePending", true);
            Task<bool> paused = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(paused);
            Check(!paused.Result && Get(form, "current") == two && queue.Items.Count == 2, "Pausing must prevent random advancement even if a finish is pending");
            Set(form, "autoPlayEnabled", true); ((WaveOutEvent)Get(form, "output")).Stop();
            Task<bool> auto = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(auto);
            Check(!auto.Result && queue.Items.Count == 2, "Auto play must continue to use its own shuffled transition policy instead of the normal-playback fallback");
            Set(form, "autoPlayEnabled", false); Set(form, "songs", new List<SongInfo> { missing });
            Task<bool> unavailable = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(unavailable);
            Check(!unavailable.Result && queue.Items.Count == 2, "A library without playable files must not add an unusable random entry");
            Set(form, "songs", new List<SongInfo> { one });
            Task<bool> single = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(single); ((WaveOutEvent)Get(form, "output")).Pause();
            Check(!single.Result && Get(form, "current") == two && queue.Items.Count == 2, "A one-song library must not repeat a song already played in this session");
        }
    }
    private static void AutoHistoryFallback(string root)
    {
        Directory.CreateDirectory(root);
        SongInfo one = Song(root, "Fallback history first", 120), two = Song(root, "Fallback history next", 132), fresh = Song(root, "Unqueued alternative", 125);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            Set(form, "songs", new List<SongInfo> { one, two, fresh }); ((MixSlider)Get(form, "volume")).Value = 0;
            var queue = (PlaybackQueue)Get(form, "songQueue"); queue.Follow(one.Audio); queue.Follow(two.Audio);
            string[] ids = queue.Items.Select(entry => entry.Id).ToArray();
            Set(form, "autoPlayEnabled", true);
            Finish((Task)Invoke(form, "PlayQueuedSong", queue.Items[0])); ((WaveOutEvent)Get(form, "output")).Stop();
            ((StemMixer)Get(form, "mixer")).Seek(((StemMixer)Get(form, "mixer")).Duration); Set(form, "queueAdvancePending", true);
            Task<bool> advance = (Task<bool>)Invoke(form, "AdvanceQueuedSong"); Finish(advance);
            Check(advance.Result && Get(form, "current") == two && queue.CurrentIndex == 1 && queue.Items.Select(entry => entry.Id).SequenceEqual(ids), "Autoplay without transition-ready stems must also follow the next played queue row after a history replay");
        }
    }
    private static void CompletedDownloads(string root)
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "data"));
        SongInfo current = Song(root, "Playing", 120);
        var json = new JavaScriptSerializer(); var jobs = new List<Dictionary<string, object>>();
        Action save = () => File.WriteAllText(Path.Combine(root, "data", "queue.json"), json.Serialize(new { version = 1, jobs = jobs }));
        Func<string, bool, Dictionary<string, object>> download = (title, completed) =>
        {
            string folder = Path.Combine(root, "stems", "Artist", title), audio = Path.Combine(root, "mp3", "Artist", title + ".mp3");
            Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.GetDirectoryName(audio)); File.Copy(current.Audio, audio);
            foreach (string stem in StemMixer.StemNames) File.Copy(current.Audio, Path.Combine(folder, stem + ".wav"));
            File.WriteAllText(Path.Combine(folder, "separation.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "metadata.json"), json.Serialize(new { id = title, title = title, artist = "Artist", bpm = (double?)null, key = (string)null,
                analysis = new { version = 2, source = "mp3", beatOffset = 0, beatConfidence = 0, keyConfidence = 0 } }));
            var job = new Dictionary<string, object> { { "id", Guid.NewGuid().ToString("N") }, { "videoId", title }, { "outputType", "mp3" },
                { "filename", Path.Combine("mp3", "Artist", title + ".mp3") }, { "status", completed ? "completed" : "separating" },
                { "analysisStatus", "completed" }, { "stemStatus", completed ? "completed" : "processing" }, { "completedAt", DateTime.UtcNow.ToString("o") } };
            jobs.Add(job); save(); return job;
        };
        download("Already complete", true); var pending = download("New song", false);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            Set(form, "songs", new List<SongInfo> { current }); ((MixSlider)Get(form, "volume")).Value = 0;
            Finish((Task)Invoke(form, "LoadSong", current, false, 0.0, false));
            var queue = (PlaybackQueue)Get(form, "songQueue"); var mixer = (StemMixer)Get(form, "mixer"); var output = (WaveOutEvent)Get(form, "output");
            ((PlayerPlayback)Get(form, "playback")).SetOutroLoop(0, mixer.TotalFrames);
            Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 1, "The initial library refresh must not enqueue old completed downloads");
            SongInfo manual = ((List<SongInfo>)Get(form, "songs")).First(song => song.Title == "Already complete");
            var existing = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", manual, queue.Items.Count, false); string[] before = queue.Items.Select(entry => entry.Id).ToArray();
            output.Play(); pending["status"] = "completed"; save(); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 2, "An MP3 marked downloaded must wait until stem processing completes");
            pending["stemStatus"] = "completed"; pending["analysisStatus"] = "processing"; save(); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 2, "Completed stems must still wait for music analysis to finish");
            pending["analysisStatus"] = "completed"; save(); string folder = Path.Combine(root, "stems", "Artist", "New song");
            File.Delete(Path.Combine(folder, "vocals.wav")); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 2, "A completion record with a missing stem must not enter the queue");
            File.WriteAllText(Path.Combine(folder, "vocals.wav"), "unfinished"); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 2, "An incomplete WAV must not enter the queue even when the status says complete");
            File.Copy(current.Audio, Path.Combine(folder, "vocals.wav"), true);
            File.WriteAllText(Path.Combine(folder, "separation.json"), "unfinished"); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 2, "A partial separation marker must wait for the next successful refresh");
            File.WriteAllText(Path.Combine(folder, "separation.json"), "{}");
            string metadataFile = Path.Combine(folder, "metadata.json"), metadata = File.ReadAllText(metadataFile); File.WriteAllText(metadataFile, "{}");
            Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 2, "A song with missing processed metadata must not be queued");
            File.WriteAllText(metadataFile, metadata);
            var transition = new PreparedAutoTransition { Bridge = mixer, Reference = mixer, QueueEntryId = existing.Id }; Set(form, "autoTransition", transition);
            Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 3 && queue.Items.Take(2).Select(entry => entry.Id).SequenceEqual(before) && queue.Items.Last().Audio.EndsWith("New song.mp3"), "A fully processed new download must append behind the existing queue while music plays");
            Check(Get(form, "autoTransition") == transition && queue.Next == existing && Get(form, "current") == current && output.PlaybackState == PlaybackState.Playing, "Adding a download during an active transition must preserve the handoff, current audio, and next entry");
            Check(queue.Items.Last().Automatic, "Completed downloads must remain automatic additions below manual choices, including songs with unknown BPM or key");
            Set(form, "autoTransition", null); Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 3, "Repeated refreshes must not add the same download again");
            Invoke(form, "RemoveQueuedSong", queue.Items.Last().Id); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 2, "A removed completed download must stay removed on later refreshes");
            Set(form, "autoPlayEnabled", true); download("Auto play download", true); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 3 && queue.Items.Last().Audio.EndsWith("Auto play download.mp3"), "New processed downloads must also append when Auto play is enabled");
            output.Pause(); download("Finished while paused", true); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 3, "Downloads completing during paused playback must not be queued");
            output.Play(); Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 3, "Resuming must not backfill songs already observed while paused");
            var alreadyQueued = download("Manually queued", false); Finish((Task)Invoke(form, "RefreshLibrary"));
            SongInfo queued = ((List<SongInfo>)Get(form, "songs")).First(song => song.Title == "Manually queued");
            var manualEntry = (PlaybackQueueEntry)Invoke(form, "AddQueuedSong", queued, queue.Items.Count, false); manualEntry.Audio = manualEntry.Audio.ToUpperInvariant();
            alreadyQueued["status"] = "completed"; alreadyQueued["stemStatus"] = "completed"; save(); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 4 && queue.Items[2] == manualEntry && queue.Items.Last().Automatic, "Completion must not duplicate a manually queued song with different path casing or move it below automatic additions");
            download("Queue read retry", true); File.WriteAllText(Path.Combine(root, "data", "queue.json"), "unfinished");
            Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 4, "An unreadable download queue must leave playback entries intact");
            save(); Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 5 && queue.Items.Last().Audio.EndsWith("Queue read retry.mp3"), "A later valid queue read must detect the completion once");
            Invoke(form, "EndQueueSession"); Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 0, "A new session must not re-add downloads already observed in earlier sessions");
            download("New session download", true); Finish((Task)Invoke(form, "RefreshLibrary"));
            Check(queue.Items.Count == 2 && queue.Current.Audio == current.Audio && queue.Next.Audio.EndsWith("New session download.mp3"), "A fresh completion after the session resets must follow the playing song in the new queue");
            output.Stop(); download("Finished while stopped", true); Finish((Task)Invoke(form, "RefreshLibrary")); Check(queue.Items.Count == 2, "Stopped playback must not enqueue completed downloads");
            Invoke(form, "CloseAudio");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.Combine(Path.GetTempPath(), "stemkit-queue-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--priority")) { ManualPriority(root); Console.WriteLine("Manual queue priority: " + checks + " checks passed."); return 0; }
            Model(); ManualPriority(Path.Combine(root, "priority")); Interface(root); HistoryInterface(Path.Combine(root, "history")); Playback(Path.Combine(root, "playback")); Navigation(Path.Combine(root, "navigation")); RandomPlayback(Path.Combine(root, "random-playback")); AutoHistoryFallback(Path.Combine(root, "auto-history-fallback")); CompletedDownloads(Path.Combine(root, "completed-downloads")); Console.WriteLine("Playback queue: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
