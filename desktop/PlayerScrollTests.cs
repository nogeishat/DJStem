using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class PlayerScrollTests
{
    private static int checks;
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
    [STAThread]
    private static int Main()
    {
        try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Model(); Lists(); Grids(); CachedRendering(); Console.WriteLine("Smooth scrolling: " + checks + " checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
