using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

internal static class PlayerScrollTests
{
    private static int checks;
    private static int playbackScrollMilliseconds = 2000;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Invoke(object control, string method, params object[] arguments) { control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, arguments); }
    private static SmoothScroll Motion(SmoothList list) { return (SmoothScroll)typeof(SmoothList).GetField("scroll", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(list); }
    private static void PumpUntil(Func<bool> done)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(done(), "Animated scrolling must finish promptly on the UI timer");
    }
    private static void Model()
    {
        int painted = 0;
        using (var scroll = new SmoothScroll(() => 600, offset => painted = offset))
        {
            scroll.MoveTo(180); Check(painted == 0 && scroll.Target == 180, "Wheel input must set a destination without jumping there");
            scroll.Advance(.016); Check(painted > 0 && painted < 180, "The first frame must move only partway toward the destination");
            double previous = scroll.Position;
            for (int frame = 0; frame < 40; frame++)
            {
                scroll.Advance(.016); Check(scroll.Position >= previous && scroll.Position <= 180, "Frames must follow a monotonic path without overshooting"); previous = scroll.Position;
            }
            Check(scroll.Position == 180 && !scroll.Moving, "The animation must settle exactly and stop its timer");
            scroll.MoveTo(400); scroll.Advance(.016); previous = scroll.Position;
            scroll.Wheel(15, 300); scroll.Advance(.016);
            Check(scroll.Position < previous && scroll.Target < previous, "Reversing direction must immediately reverse visible movement");
            scroll.JumpTo(0); scroll.Wheel(-15, 300);
            Check(scroll.Target > 0 && scroll.Target < SmoothScroll.WheelDistance(-120, 300), "High-resolution wheel deltas must work without waiting for a full notch");
            scroll.MoveTo(10000); for (int i = 0; i < 60; i++) scroll.Advance(.016);
            Check(painted == 600, "Long wheel bursts must clamp at the lower boundary");
            scroll.MoveTo(-10000); for (int i = 0; i < 60; i++) scroll.Advance(.016);
            Check(painted == 0, "Scrolling back must clamp at the upper boundary");
        }
    }
    private static void Lists()
    {
        using (var queue = new QueueList { Size = new Size(883, 220) })
        {
            for (int i = 0; i < 20; i++) queue.Items.Add(new QueueRow { Entry = new PlaybackQueueEntry { Audio = "song-" + i }, Song = new SongInfo { Title = "Song " + i, Artist = "Artist", Bpm = 120, Key = "A minor" } });
            IntPtr handle = queue.Handle; queue.TopIndex = 5;
            Check((bool)typeof(SmoothList).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(queue, new object[] { Keys.Enter }), "Enter must reach the queue's playback handler as an input key");
            Invoke(queue, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 30, 30, -15));
            SmoothScroll scroll = Motion(queue); scroll.Advance(.016);
            Check(queue.ScrollOffset > 220 && queue.ScrollOffset < queue.ScrollTarget, "Queue scrolling must move by pixels within a row");
            int offset = (int)Math.Round(queue.ScrollOffset);
            for (int y = 0; y < 220; y += 9) Check(queue.IndexFromPoint(new Point(100, y)) == (y + offset) / 44, "Hit testing must follow partially scrolled rows");
            Rectangle row = queue.GetItemRectangle(6);
            Check(queue.InsertionAt(new Point(100, row.Top + 2)) == 6 && queue.InsertionAt(new Point(100, row.Bottom - 2)) == 7, "Drag insertion must track visible row halves during scrolling");
            double before = queue.ScrollOffset, target = queue.ScrollTarget;
            object[] entries = queue.Items.ToArray(); queue.BeginUpdate(); queue.Items.Clear(); foreach (object entry in entries) queue.Items.Add(entry); queue.SelectedIndex = 5; queue.EndUpdate();
            Check(queue.ScrollOffset == before && queue.ScrollTarget == target, "Queue status refreshes must preserve fractional position and unfinished motion");
            int clicked = queue.IndexFromPoint(new Point(100, 32)); Invoke(queue, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 100, 32, 0));
            Check(queue.SelectedIndex == clicked && !scroll.Moving, "Clicking during motion must freeze and select the song under the pointer");
            queue.TopIndex = 19; Check(queue.GetItemRectangle(19).Top == 0, "The final queue song must still reach the top of the view");
            queue.TopIndex = 5; queue.ScrollBy(17); scroll.Advance(.016);
            using (var artwork = new Bitmap(40, 40))
            using (var bitmap = new Bitmap(queue.Width, queue.Height))
            {
                using (Graphics g = Graphics.FromImage(artwork)) g.Clear(Color.Coral);
                foreach (QueueRow entry in queue.Items) entry.Artwork = artwork;
                queue.DrawToBitmap(bitmap, queue.ClientRectangle);
                Check(bitmap.GetPixel(20, 10).ToArgb() == Color.Coral.ToArgb(), "The partially visible cover must render at the same pixel offset as its row");
                foreach (QueueRow entry in queue.Items) entry.Artwork = null;
            }
            Check(queue.AccessibilityObject.GetChildCount() == 20 && queue.AccessibilityObject.GetChild(6).Name.Contains("Song 6"), "Screen readers must retain song entries after pixel scrolling");
        }
        using (var sidebar = new SongList { Size = new Size(240, 264), ItemHeight = 66 })
        {
            for (int i = 0; i < 30; i++) sidebar.Items.Add(new SongInfo { Title = "Song " + i, Artist = "Artist" });
            IntPtr handle = sidebar.Handle; sidebar.TopIndex = 8;
            Invoke(sidebar, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 20, 20, -15)); Motion(sidebar).Advance(.016);
            Check(sidebar.ScrollOffset > 8 * 66 && sidebar.ScrollOffset < 9 * 66, "Sidebar movement must also support partial rows");
            sidebar.SelectedIndex = 10; int loads = 0; sidebar.SelectionCommitted += delegate { loads++; };
            int clicked = sidebar.IndexFromPoint(new Point(100, 80)); Invoke(sidebar, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 100, 80, 0));
            Check(sidebar.SelectedIndex == clicked && loads == 0 && sidebar.PointerSelecting, "Pressing a sidebar row must defer loading until release");
            Invoke(sidebar, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 100, 80, 0)); Check(loads == 1 && !sidebar.PointerSelecting, "Releasing a sidebar row must still commit its selection");
            Invoke(sidebar, "OnKeyDown", new KeyEventArgs(Keys.End)); PumpUntil(() => Math.Abs(sidebar.ScrollTarget - sidebar.ScrollOffset) < .25);
            Check(sidebar.SelectedIndex == 29 && sidebar.GetItemRectangle(29).Bottom <= sidebar.Height, "Keyboard navigation must keep the last selected song visible");
            sidebar.Items.Clear(); Check(sidebar.SelectedIndex == -1 && sidebar.ScrollOffset == 0, "Clearing a view must safely reset its selection and scroll range");
        }
    }
    private static void Grids()
    {
        using (var host = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(800, 400) })
        using (var grid = new LibraryGridPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true })
        {
            host.Controls.Add(grid);
            foreach (bool listView in new[] { false, true })
            {
                grid.Controls.Clear(); grid.FlowDirection = listView ? FlowDirection.TopDown : FlowDirection.LeftToRight; grid.WrapContents = !listView;
                for (int i = 0; i < 30; i++) grid.Controls.Add(new LibraryCard { Title = "Card " + i, Subtitle = "Artist", Folder = !listView, ListRow = listView, Size = listView ? new Size(760, 72) : new Size(200, 258) });
                if (!listView)
                {
                    IntPtr handle = grid.Handle; grid.PerformLayout();
                    Invoke(grid, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 40, -15));
                    PumpUntil(() => grid.AutoScrollPosition.Y < 0);
                    Check(grid.AutoScrollPosition.Y < 0, "Scrolling must update a panel whose handle exists before its parent is shown");
                }
                host.Show(); grid.PerformLayout(); grid.AutoScrollPosition = Point.Empty;
                Control first = grid.Controls[0]; int top = first.Top;
                Invoke(grid, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 40, -15));
                Check(grid.AutoScrollPosition.Y == 0 && grid.ScrollTarget > 0, "Library wheel input must animate without an initial jump");
                PumpUntil(() => grid.ScrollOffset > 0);
                Check(grid.AutoScrollPosition.Y < 0 && first.Top < top, "Library cards and song rows must visibly move on animation frames: list=" + listView + ", offset=" + grid.ScrollOffset + ", target=" + grid.ScrollTarget + ", native=" + grid.AutoScrollPosition.Y + ", top=" + first.Top + ", original=" + top);
                PumpUntil(() => Math.Abs(grid.ScrollTarget - grid.ScrollOffset) < .25);
                Check(first.Top == top + grid.AutoScrollPosition.Y, "Card positions and the scrolling viewport must remain aligned");
                Invoke(grid, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 40, -120));
                grid.AutoScrollPosition = Point.Empty; Application.DoEvents();
                Check(grid.ScrollTarget == 0 && grid.AutoScrollPosition.Y == 0, "Switching library folders must cancel unfinished scrolling from the previous view");
            }
            grid.Controls.Clear(); grid.AutoScrollPosition = Point.Empty;
            Invoke(grid, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 20, 20, -120)); Check(grid.ScrollTarget == 0, "Empty library views must not scroll into blank space");
        }
    }
    private static long PaintHash(Control control)
    {
        using (var bitmap = new Bitmap(control.Width, control.Height))
        {
            control.DrawToBitmap(bitmap, control.ClientRectangle);
            long hash = 17;
            for (int y = 0; y < bitmap.Height; y += 3) for (int x = 0; x < bitmap.Width; x += 3) hash = unchecked(hash * 397 ^ bitmap.GetPixel(x, y).ToArgb());
            return hash;
        }
    }
    private static void CachedRendering()
    {
        using (var red = new Bitmap(256, 256))
        using (var green = new Bitmap(256, 256))
        {
            using (Graphics g = Graphics.FromImage(red)) g.Clear(Color.Coral);
            using (Graphics g = Graphics.FromImage(green)) g.Clear(Color.ForestGreen);
            using (var card = new LibraryCard { Size = new Size(200, 284), Title = "Initial song", Subtitle = "Artist", Details = "120 BPM", Artwork = red })
            {
                long first = PaintHash(card); Check(PaintHash(card) == first, "Reusing a library render must preserve its appearance");
                card.Title = "A different song title"; long title = PaintHash(card); Check(title != first, "Editing a library title must refresh its cached text");
                card.Artwork = green; long cover = PaintHash(card); Check(cover != title, "Replacing library artwork must refresh its cached cover");
                Invoke(card, "OnMouseEnter", EventArgs.Empty); long hover = PaintHash(card); Check(hover != cover, "Hover highlighting must stay responsive with cached library cards");
                Invoke(card, "OnMouseLeave", EventArgs.Empty); Check(PaintHash(card) == cover, "Leaving a card must restore its normal appearance");
                card.ListRow = true; card.Size = new Size(800, 72); first = PaintHash(card);
                card.Details = "132 BPM · F major"; Check(PaintHash(card) != first, "Library list rows must refresh changed BPM and key details");
            }
            using (var queue = new QueueList { Size = new Size(1000, 44) })
            {
                var song = new SongInfo { Title = "Queue song", Artist = "Artist", Bpm = 120, Key = "A minor" };
                var row = new QueueRow { Entry = new PlaybackQueueEntry { Audio = "queue-song" }, Song = song, Artwork = red, Status = "Next" };
                queue.Items.Add(row);
                long first = PaintHash(queue); Check(PaintHash(queue) == first, "Reusing a queue render must preserve its appearance");
                row.Status = "Preparing ahead"; long status = PaintHash(queue); Check(status != first, "Preparation status changes must invalidate a cached queue row");
                song.Title = "Updated queue title"; long title = PaintHash(queue); Check(title != status, "In-place song metadata edits must refresh a cached queue title");
                song.Bpm = 132; long music = PaintHash(queue); Check(music != title, "BPM changes must refresh cached queue details");
                row.Artwork = green; long cover = PaintHash(queue); Check(cover != music, "Artwork changes must refresh a cached queue cover");
                row.Played = true; long played = PaintHash(queue); Check(played != cover, "Played history must retain its darker appearance");
                row.Locked = true; Check(PaintHash(queue) != played, "Transition locking must refresh the status and remove button");
                queue.SelectedIndex = 0; first = PaintHash(queue); queue.SelectedIndex = -1; Check(PaintHash(queue) != first, "Queue selection changes must redraw the row background");
                queue.Size = new Size(1120, 44); first = PaintHash(queue); Check(PaintHash(queue) == first, "Resizing must produce a reusable render at the new queue width");
                for (int index = 0; index < 80; index++) { row.Entry = new PlaybackQueueEntry { Audio = "history-" + index }; PaintHash(queue); }
                var cached = (System.Collections.IDictionary)typeof(QueueList).GetField("paintedRows", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(queue);
                Check(cached.Count <= 32, "Scrolling through a long history must keep the queue rendering cache bounded to the viewport budget");
            }
        }
    }
    private static object Field(object source, string name) { return source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source); }
    private static void SetField(object source, string name, object value) { source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(source, value); }
    private static void DuringAudioWork(object gate, Action update, string message)
    {
        using (var entered = new ManualResetEventSlim())
        {
            var audio = new Thread(() => { lock (gate) { entered.Set(); Thread.Sleep(300); } });
            audio.Start(); entered.Wait();
            var clock = Stopwatch.StartNew(); update(); clock.Stop(); audio.Join();
            Console.WriteLine(message + ": " + clock.ElapsedMilliseconds + " ms");
            Check(clock.ElapsedMilliseconds < 100, message + " must not wait for an audio callback or decoder");
        }
    }
    private sealed class SlowClosingReader : WaveStream
    {
        private readonly WaveStream source;
        internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim(), Release = new ManualResetEventSlim();
        internal SlowClosingReader(WaveStream reader) { source = reader; }
        public override WaveFormat WaveFormat { get { return source.WaveFormat; } }
        public override long Length { get { return source.Length; } }
        public override long Position { get { return source.Position; } set { source.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) { return source.Read(buffer, offset, count); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Entered.Set(); Release.Wait(5000); source.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private static void SteadyDisplay(PlayerForm form, StemMixer mixer)
    {
        var views = (WaveformView[])Field(form, "waveforms");
        int invalidations = 0;
        foreach (var view in views) { IntPtr handle = view.Handle; view.Invalidated += delegate { invalidations++; }; }
        Invoke(form, "UpdateTransport"); invalidations = 0;
        for (int tick = 0; tick < 600; tick++) Invoke(form, "UpdateTransport");
        Check(invalidations == 0, "An unchanged paused display must not redraw the four waveforms on every transport tick");
        mixer.Seek(.5); Invoke(form, "UpdateTransport");
        Check(views.All(view => Math.Abs(view.Progress - .5) < .001), "A snapshot must still update every lane after a seek");
        var browser = (Control)Field(form, "browser"); browser.Visible = true;
        // Visible is inherited from the parent; show the form transparently so
        // this exercises the actual library visibility branch without playing sound.
        form.ShowInTaskbar = false; form.Opacity = 0; form.Show();
        ((System.Windows.Forms.Timer)Field(form, "timer")).Stop();
        invalidations = 0; mixer.Seek(.75); Invoke(form, "UpdateTransport");
        Check(invalidations == 0, "Browsing the library must not repaint the hidden stem waveforms");
        Invoke(form, "ShowLibrary", false);
        Check(views.All(view => Math.Abs(view.Progress - .75) < .001), "Returning to Now playing must immediately catch up the waveforms");
        Invoke(form, "ShowPlaybackQueue", true); invalidations = 0;
        mixer.Seek(.9); Invoke(form, "UpdateTransport");
        Check(invalidations == 0, "Viewing the queue must not repaint hidden stem waveforms");
        Invoke(form, "ShowPlaybackQueue", false);
        Check(views.All(view => Math.Abs(view.Progress - .9) < .001), "Closing the queue must immediately catch up the waveforms");
        form.Hide();
    }
    private static void ScrollingDuringPlayback(PlayerForm form, StemMixer mixer, PlayerPlayback playback)
    {
        using (var host = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(900, 600) })
        using (var queue = new QueueList { Bounds = new Rectangle(250, 310, 650, 290) })
        using (var sidebar = new SongList { Bounds = new Rectangle(0, 0, 240, 600), ItemHeight = 66 })
        using (var grid = new LibraryGridPanel { Bounds = new Rectangle(250, 0, 650, 300), AutoScroll = true })
        using (var refresh = new System.Windows.Forms.Timer { Interval = 25 })
        using (var stop = new ManualResetEventSlim())
        {
            for (int i = 0; i < 80; i++)
            {
                var song = new SongInfo { Title = "Playback " + i, Artist = "Artist" };
                queue.Items.Add(new QueueRow { Entry = new PlaybackQueueEntry { Audio = "playback-" + i }, Song = song }); sidebar.Items.Add(song);
                grid.Controls.Add(new LibraryCard { Title = song.Title, Subtitle = song.Artist, Size = new Size(200, 258) });
            }
            host.Controls.Add(queue); host.Controls.Add(sidebar); host.Controls.Add(grid); host.Show();
            queue.ScrollBy(2000); sidebar.ScrollBy(2000);
            Invoke(grid, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 40, -1200));
            int frames = 0, sidebarFrames = 0, gridFrames = 0, updates = 0; long maximumGap = 0;
            var clock = Stopwatch.StartNew(); long lastFrame = 0;
            queue.Invalidated += delegate { if (queue.ScrollOffset == 0) return; long now = clock.ElapsedMilliseconds; if (lastFrame != 0) maximumGap = Math.Max(maximumGap, now - lastFrame); lastFrame = now; frames++; };
            sidebar.Invalidated += delegate { sidebarFrames++; };
            grid.Controls[0].LocationChanged += delegate { gridFrames++; };
            refresh.Tick += delegate { Invoke(form, "UpdateTransport"); updates++; };
            // Real decoding and mixing run concurrently with UI timers. The
            // repeated seeks simulate a long playback session in a short test.
            Task audio = Task.Run(() =>
            {
                var samples = new float[320];
                while (!stop.IsSet) { if (playback.Read(samples, 0, samples.Length) == 0) mixer.Seek(0); Thread.Sleep(1); }
            });
            refresh.Start();
            try
            {
                while (clock.ElapsedMilliseconds < playbackScrollMilliseconds)
                {
                    if (Math.Abs(queue.ScrollTarget - queue.ScrollOffset) < 1) queue.ScrollBy(queue.ScrollOffset > 1000 ? -1800 : 1800);
                    if (Math.Abs(sidebar.ScrollTarget - sidebar.ScrollOffset) < 1) sidebar.ScrollBy(sidebar.ScrollOffset > 1000 ? -1800 : 1800);
                    if (Math.Abs(grid.ScrollTarget - grid.ScrollOffset) < 1) Invoke(grid, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 40, grid.ScrollOffset > 800 ? 1200 : -1200));
                    Application.DoEvents(); Thread.Sleep(1);
                }
            }
            finally { refresh.Stop(); stop.Set(); audio.GetAwaiter().GetResult(); }
            Console.WriteLine("Concurrent playback scrolling (" + playbackScrollMilliseconds + " ms): " + frames + " queue frames, " + sidebarFrames + " sidebar frames, " + updates + " refreshes, largest queue frame gap " + maximumGap + " ms");
            Check(frames >= 30 && updates >= 20 && maximumGap < 150, "Scrolling and transport timers must keep advancing during repeated decoding and seeks");
            Check(sidebarFrames >= 30 && gridFrames >= 30, "Sidebar and library scrolling must also keep moving throughout playback");
        }
    }
    private static void ReaderCleanup(PlayerForm form, string audio)
    {
        using (var old = new StemMixer(audio, ""))
        using (var bridge = new StemMixer(audio, ""))
        using (var next = new StemMixer(audio, ""))
        using (var playback = new PlayerPlayback(old))
        {
            var readers = (WaveStream[])Field(old, "readers"); var reader = new SlowClosingReader(readers[0]); readers[0] = reader;
            Check(playback.Schedule(old, old.TotalFrames, bridge, next), "Cleanup test must schedule a real song handoff");
            var samples = new float[3200]; while (playback.Current != next) playback.Read(samples, 0, samples.Length);
            SetField(form, "mixer", next); SetField(form, "playback", playback);
            try
            {
                var clock = Stopwatch.StartNew(); Invoke(form, "UpdateTransport"); clock.Stop();
                Check(clock.ElapsedMilliseconds < 100 && reader.Entered.Wait(2000), "Retired decoder shutdown must run off the UI thread after a song handoff");
                Check(playback.Read(samples, 0, samples.Length) > 0, "Playback must continue while an old decoder is still closing");
            }
            finally { reader.Release.Set(); SetField(form, "mixer", null); SetField(form, "playback", null); }
        }
    }
    private static void PlaybackResponsiveness()
    {
        string root = Path.Combine(Path.GetTempPath(), "djstem-scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string audio = Path.Combine(root, "audio.wav");
            using (var writer = new WaveFileWriter(audio, new WaveFormat(16000, 16, 2))) writer.Write(new byte[64000], 0, 64000);
            using (var form = new PlayerForm(root, root))
            using (var mixer = new StemMixer(audio, ""))
            using (var playback = new PlayerPlayback(mixer))
            {
                SetField(form, "mixer", mixer); SetField(form, "playback", playback);
                Invoke(form, "UpdateTransport");
                DuringAudioWork(Field(mixer, "gate"), () => Invoke(form, "UpdateTransport"), "Transport refresh during decoding");
                DuringAudioWork(Field(playback, "gate"), () => Invoke(form, "UpdateTransport"), "Transport refresh during timeline processing");
                DuringAudioWork(Field(mixer, "gate"), () => { long position = mixer.PositionFrames; }, "Transport position observation");
                DuringAudioWork(Field(playback, "gate"), () => { StemMixer current = playback.Current; }, "Timeline observation");
                using (var bridge = new StemMixer(audio, ""))
                using (var next = new StemMixer(audio, ""))
                {
                    SetField(form, "autoTransition", new PreparedAutoTransition { Reference = mixer, Bridge = bridge, Next = next, Timing = new AutoTransitionTiming(120, 120, 16000, new AutoPlaySettings()), Traits = new MusicTraits { Bpm = 120 } });
                    DuringAudioWork(Field(playback, "gate"), () => ((Task)typeof(PlayerForm).GetMethod("TickAutoPlay", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null)).GetAwaiter().GetResult(), "Autoplay polling during timeline processing");
                    SetField(form, "autoTransition", null);
                }
                SteadyDisplay(form, mixer);
                ScrollingDuringPlayback(form, mixer, playback);
                SetField(form, "mixer", null); SetField(form, "playback", null);
                ReaderCleanup(form, audio);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--soak")) playbackScrollMilliseconds = 60000;
        try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Model(); Lists(); Grids(); CachedRendering(); PlaybackResponsiveness(); Console.WriteLine("Smooth scrolling: " + checks + " checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
