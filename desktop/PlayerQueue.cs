using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

internal sealed class PlaybackQueueEntry
{
    internal string Id = Guid.NewGuid().ToString("N"), Audio;
    internal bool Automatic;
}

internal sealed class PlaybackQueue
{
    internal readonly List<PlaybackQueueEntry> Items = new List<PlaybackQueueEntry>();
    private readonly HashSet<string> played = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal string CurrentId;
    internal int CurrentIndex { get { return Items.FindIndex(entry => entry.Id == CurrentId); } }
    internal PlaybackQueueEntry Current { get { int index = CurrentIndex; return index < 0 ? null : Items[index]; } }
    internal PlaybackQueueEntry Next { get { int index = CurrentIndex + 1; return index >= Items.Count ? null : Items[index]; } }
    internal PlaybackQueueEntry Upcoming { get { return Items.Skip(CurrentIndex + 1).FirstOrDefault(entry => !HasPlayed(entry.Audio)); } }
    internal PlaybackQueueEntry Previous { get { int index = CurrentIndex - 1; return index < 0 ? null : Items[index]; } }
    internal bool HasPlayed(string audio) { return played.Contains(audio); }
    internal bool CanAdd(string audio) { return !HasPlayed(audio) && !Items.Any(entry => String.Equals(entry.Audio, audio, StringComparison.OrdinalIgnoreCase)); }
    internal void Clear() { Items.Clear(); CurrentId = null; played.Clear(); }
    internal void Forget(string audio) { foreach (PlaybackQueueEntry entry in Items.Where(item => String.Equals(item.Audio, audio, StringComparison.OrdinalIgnoreCase)).ToArray()) Remove(entry.Id); played.Remove(audio); }
    internal bool Select(string id) { PlaybackQueueEntry entry = Items.FirstOrDefault(item => item.Id == id); if (entry == null) return false; CurrentId = id; played.Add(entry.Audio); return true; }
    internal void Follow(string audio)
    {
        PlaybackQueueEntry entry = Items.FirstOrDefault(item => String.Equals(item.Audio, audio, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { entry = new PlaybackQueueEntry { Audio = audio }; Items.Insert(CurrentIndex + 1, entry); }
        Select(entry.Id);
    }
    internal PlaybackQueueEntry Add(string audio, int slot, bool automatic = false)
    {
        if (!CanAdd(audio)) return null;
        var entry = new PlaybackQueueEntry { Audio = audio, Automatic = automatic };
        Items.Insert(Math.Max(0, Math.Min(Items.Count, slot)), entry); return entry;
    }
    internal bool Remove(string id)
    {
        int index = Items.FindIndex(entry => entry.Id == id); if (index < 0) return false;
        if (id == CurrentId) CurrentId = index > 0 ? Items[index - 1].Id : null;
        Items.RemoveAt(index); return true;
    }
    internal bool Move(string id, int slot)
    {
        int index = Items.FindIndex(item => item.Id == id); if (index < 0) return false;
        slot = Math.Max(0, Math.Min(Items.Count, slot)); if (slot > index) slot--;
        PlaybackQueueEntry entry = Items[index]; Items.RemoveAt(index); Items.Insert(slot, entry); entry.Automatic = false; return true;
    }
    internal void Remap(Func<string, string> remap)
    {
        string[] history = played.Select(remap).ToArray();
        played.Clear(); foreach (string audio in history) played.Add(audio);
        foreach (PlaybackQueueEntry entry in Items) entry.Audio = remap(entry.Audio);
    }
}

internal sealed class QueueRow
{
    internal PlaybackQueueEntry Entry;
    internal SongInfo Song;
    internal Image Artwork;
    internal bool Locked;
    internal bool Played;
    internal string Status;
    internal string DisplayIdentity;
    internal string Title { get { if (Song != null) return Song.Title; try { return Path.GetFileNameWithoutExtension(Entry.Audio); } catch { return "Unavailable song"; } } }
}

internal sealed partial class PlayerForm
{
    private readonly PlaybackQueue songQueue = new PlaybackQueue();
    private readonly GlassButton queueButton = new GlassButton();
    private readonly GlassPanel queuePanel = new GlassPanel();
    private readonly Label queueEmpty = new Label();
    private readonly QueueList queueList = new QueueList();
    private readonly Random normalPlaybackRandom = new Random();
    private readonly HashSet<string> completedDownloadsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool completedDownloadBaseline;
    private bool queueVisible, advancingQueue, queueAdvancePending;
    private string preparingQueueId;
    private const string QueueEntryDragFormat = "StemKit.PlaybackQueueEntry.v1";
    private string LockedQueueId { get { return AutoTransitionActive && autoTransition != null ? autoTransition.QueueEntryId : null; } }
    private PlaybackQueueEntry NextPlaybackEntry { get { return autoPlayEnabled ? songQueue.Next : songQueue.Upcoming; } }
    private bool QueueEntryLocked(string id) { return id != null && (id == LockedQueueId || id == songQueue.CurrentId); }
    private int EditableQueueStart { get { int locked = songQueue.Items.FindIndex(entry => entry.Id == LockedQueueId); return Math.Max(songQueue.CurrentIndex + 1, locked + 1); } }
    private int ManualQueueEnd
    {
        get
        {
            int automatic = songQueue.Items.FindIndex(EditableQueueStart, entry => entry.Automatic);
            return automatic < 0 ? songQueue.Items.Count : automatic;
        }
    }
    private bool CanManuallyQueueSong(SongInfo song)
    {
        if (song == null || songQueue.HasPlayed(song.PlaybackKey) || current != null && SameSong(current.PlaybackKey, song.PlaybackKey)) return false;
        PlaybackQueueEntry existing = songQueue.Items.FirstOrDefault(entry => SameSong(entry.Audio, song.PlaybackKey));
        return existing == null || existing.Automatic && !QueueEntryLocked(existing.Id);
    }
    private void EndQueueSession() { CancelAutoPreload(); if (playback != null) playback.ClearRestartProtection(); autoIncomingMixer = null; if (!startingPlaylistAuto) autoPlaylistId = null; songQueue.Clear(); autoShuffle.Reset(); RenderPlaybackQueue(); }
    private void FollowSessionSong(SongInfo song)
    {
        if (song == null || AutoTransitionActive || songQueue.Current != null && String.Equals(songQueue.Current.Audio, song.PlaybackKey, StringComparison.OrdinalIgnoreCase)) return;
        songQueue.Follow(song.PlaybackKey); RenderPlaybackQueue();
    }

    private void QueueCompletedDownloads(List<PlayerLibrary.CompletedDownload> downloads)
    {
        if (downloads == null) return;
        bool playing = completedDownloadBaseline && current != null && mixer != null && output != null && output.PlaybackState == PlaybackState.Playing;
        foreach (PlayerLibrary.CompletedDownload download in downloads)
        {
            if (!completedDownloadsSeen.Add(download.Id) || !playing) continue;
            if (autoPlaylistId != null && !AutoPlaybackSongs().Any(song => song.PlaybackKey == download.Song.PlaybackKey)) continue;
            if (!CanManuallyQueueSong(download.Song)) continue;
            // Downloads are user choices: append to the manual-priority group,
            // ahead of shuffle suggestions and behind any active handoff.
            AddQueuedSong(download.Song, ManualQueueEnd, false);
        }
        completedDownloadBaseline = true;
    }

    private void InitializeQueue()
    {
        SetupButton(queueButton, "Queue", transport, null); queueButton.Font = new Font("Segoe UI", 8.5f); queueButton.Radius = 13; queueButton.Toggle = true; queueButton.AccessibleName = "Open playback queue";
        queueButton.Click += delegate { ShowPlaybackQueue(!queueVisible); };
        queuePanel.Visible = false; content.Controls.Add(queuePanel);
        ConfigureLabel(queueEmpty, queuePanel, "No songs in this session", 10, PlayerTheme.Muted, false); queueEmpty.TextAlign = ContentAlignment.MiddleCenter;
        queuePanel.Controls.Add(queueList); queueList.AccessibleName = "Session songs";
        queueList.RemoveEntry = RemoveQueuedSong;
        queueList.BeginEntryDrag = delegate(QueueRow row) { if (row.Locked) return; DataObject data = new DataObject(); data.SetData(QueueEntryDragFormat, false, row.Entry.Id); queueList.DoDragDrop(data, DragDropEffects.Move); };
        queueList.MoveEntry = MoveQueuedSong;
        queueList.PlayEntry = async delegate(string id) { await PlayQueuedSong(songQueue.Items.FirstOrDefault(entry => entry.Id == id)); };
        foreach (Control surface in new Control[] { queuePanel, queueList, queueEmpty }) AttachQueueDrop(surface);
        tip.SetToolTip(queueButton, "Session songs · double-click to play; drag to reorder history or upcoming songs. Played songs stay dimmed and are not queued again. Toggling Auto play clears the session.");
    }
    private void ShowPlaybackQueue(bool show)
    {
        queueVisible = show; queueButton.Selected = show; queueButton.Invalidate();
        queueButton.AccessibleName = show ? "Close playback queue" : "Open playback queue";
        foreach (GlassPanel lane in lanes) lane.Visible = !show;
        queuePanel.Visible = show;
        LayoutContent();
        if (show) { FollowSessionSong(current); RenderPlaybackQueue(); queuePanel.BringToFront(); queueList.Focus(); ScrollQueueToCurrent(); }
        else UpdateTransport();
    }
    private void LayoutPlaybackQueue(int width)
    {
        queueButton.Bounds = new Rectangle(resetStems.Right + 8, 59, 76, 32);
        int top = lanes[0].Top;
        int height = Math.Max(180, message.Top - top - 12);
        queuePanel.Bounds = new Rectangle(24, top, width, height);
        queueList.Bounds = new Rectangle(12, 12, width - 24, height - 24);
        queueEmpty.Bounds = queueList.Bounds;
    }
    private SongInfo QueuedSong(PlaybackQueueEntry entry)
    {
        return entry == null ? null : PlaylistSong(entry.Audio);
    }
    private void ScrollQueueToCurrent()
    {
        if (!queueVisible || songQueue.CurrentIndex < 0) return;
        IntPtr handle = queueList.Handle;
        queueList.SelectedIndex = songQueue.CurrentIndex; queueList.TopIndex = songQueue.CurrentIndex;
    }
    private void RenderPlaybackQueue()
    {
        if (!queueVisible) return;
        PlaybackQueueEntry next = NextPlaybackEntry; string lockedId = LockedQueueId;
        var byAudio = new Dictionary<string, SongInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (SongInfo song in songs) if (!byAudio.ContainsKey(song.Audio)) byAudio.Add(song.Audio, song);
        var rows = new List<QueueRow>(songQueue.Items.Count);
        bool sameOrder = queueList.Items.Count == songQueue.Items.Count;
        for (int index = 0; index < songQueue.Items.Count; index++)
        {
            PlaybackQueueEntry entry = songQueue.Items[index]; bool locked = entry.Id == lockedId || entry.Id == songQueue.CurrentId;
            bool played = songQueue.HasPlayed(entry.Audio) && entry.Id != songQueue.CurrentId && entry.Id != lockedId;
            string status = entry.Id == lockedId ? "Transitioning" : entry.Id == songQueue.CurrentId ? "Playing" : entry.Id == preparingQueueId && preparingAutoPlay ? "Preparing" : played ? entry == next ? "Played · next" : "Played" : entry == next ? autoPlayEnabled ? "Up next" : "Next" : "";
            if (!played && entry.Automatic && entry == next && status != "Preparing" && status != "Transitioning") status = "Automatic · next";
            if (autoPreload != null && entry.Id == autoPreload.QueueEntryId && !autoPreload.Cancellation.IsCancellationRequested && status != "Transitioning" && status != "Preparing") status = !autoPreload.Preparing.IsCompleted ? "Preparing ahead" : autoPreload.Preparing.Result != null ? "Ready ahead" : status;
            SongInfo song;
            if (!byAudio.TryGetValue(entry.Audio, out song)) song = QueuedSong(entry);
            rows.Add(new QueueRow { Entry = entry, Song = song, Artwork = song == null ? null : Thumbnail(song), Locked = locked, Played = played, Status = status, DisplayIdentity = song == null ? entry.Audio : song.Title + "\0" + song.Artist + "\0" + song.MusicDetails });
            if (sameOrder && ((QueueRow)queueList.Items[index]).Entry.Id != entry.Id) sameOrder = false;
        }
        if (sameOrder)
        {
            // Status polling must leave selection, fractional scroll position
            // and in-flight wheel movement alone. Repaint only changed rows.
            for (int index = 0; index < rows.Count; index++)
            {
                QueueRow old = (QueueRow)queueList.Items[index], row = rows[index];
                bool changed = old.DisplayIdentity != row.DisplayIdentity || old.Artwork != row.Artwork || old.Locked != row.Locked || old.Played != row.Played || old.Status != row.Status;
                old.Entry = row.Entry; old.Song = row.Song; old.Artwork = row.Artwork; old.Locked = row.Locked; old.Played = row.Played; old.Status = row.Status; old.DisplayIdentity = row.DisplayIdentity;
                Rectangle bounds = queueList.GetItemRectangle(index);
                if (changed && bounds.IntersectsWith(queueList.ClientRectangle)) queueList.Invalidate(bounds);
            }
        }
        else
        {
            QueueRow selected = queueList.SelectedItem as QueueRow; string selection = selected == null || selected.Entry == null ? null : selected.Entry.Id;
            queueList.BeginUpdate(); queueList.Items.Clear();
            foreach (QueueRow row in rows) queueList.Items.Add(row);
            if (rows.Count > 0) queueList.SelectedIndex = Math.Max(0, rows.FindIndex(row => row.Entry.Id == selection));
            queueList.EndUpdate();
        }
        queueEmpty.Visible = rows.Count == 0; if (queueEmpty.Visible) queueEmpty.BringToFront();
    }
    private void QueueChanged(string previousHead, bool invalidate = true)
    {
        string nextHead = NextPlaybackEntry == null ? null : NextPlaybackEntry.Id;
        if (invalidate && previousHead != nextHead && !AutoTransitionActive) CancelAutoPreparation();
        UpdateAutoPreload();
        UpdateAutoRestart();
        RenderPlaybackQueue(); SavePreferences();
    }
    private PlaybackQueueEntry AddQueuedSong(SongInfo song, int slot, bool automatic = false)
    {
        if (song == null) return null; FollowSessionSong(current);
        if (autoPlaylistId != null && !AutoPlaybackSongs().Any(candidate => candidate.PlaybackKey == song.PlaybackKey)) { if (!automatic) message.Text = "This Auto play session is limited to its playlist."; return null; }
        if (automatic ? !songQueue.CanAdd(song.PlaybackKey) : !CanManuallyQueueSong(song)) { if (!automatic) message.Text = songQueue.HasPlayed(song.PlaybackKey) ? "Already played in this session. Double-click its queue row to replay it." : "This song is already in the queue."; return null; }
        string previous = NextPlaybackEntry == null ? null : NextPlaybackEntry.Id;
        slot = automatic ? Math.Max(ManualQueueEnd, slot) : Math.Min(ManualQueueEnd, slot);
        slot = Math.Max(EditableQueueStart, slot);
        PlaybackQueueEntry entry = automatic ? null : songQueue.Items.FirstOrDefault(item => SameSong(item.Audio, song.PlaybackKey));
        if (entry != null) songQueue.Move(entry.Id, slot);
        else entry = songQueue.Add(song.PlaybackKey, slot, automatic);
        QueueChanged(previous, !automatic); return entry;
    }
    private void RemoveQueuedSong(string id)
    {
        if (QueueEntryLocked(id)) return;
        PlaybackQueueEntry entry = songQueue.Items.FirstOrDefault(item => item.Id == id); if (entry == null) return;
        string previous = NextPlaybackEntry == null ? null : NextPlaybackEntry.Id; songQueue.Remove(id); QueueChanged(previous);
    }
    private void MoveQueuedSong(string id, int slot)
    {
        if (QueueEntryLocked(id)) return; string previous = NextPlaybackEntry == null ? null : NextPlaybackEntry.Id;
        PlaybackQueueEntry entry = songQueue.Items.FirstOrDefault(item => item.Id == id);
        if (entry != null && !songQueue.HasPlayed(entry.Audio) && slot >= EditableQueueStart) slot = Math.Min(ManualQueueEnd, slot);
        if (songQueue.Move(id, slot)) QueueChanged(previous);
    }
    private void SelectQueuedSong(string id)
    {
        if (id != null && songQueue.Select(id)) { RenderPlaybackQueue(); ScrollQueueToCurrent(); SavePreferences(); }
    }
    private bool QueueDropAllowed(IDataObject data)
    {
        if (data == null) return false;
        if (data.GetDataPresent(QueueEntryDragFormat, false)) { string id = data.GetData(QueueEntryDragFormat, false) as string; return !QueueEntryLocked(id) && songQueue.Items.Any(entry => entry.Id == id); }
        return CanManuallyQueueSong(DroppedSong(data));
    }
    private bool DropIntoQueue(IDataObject data, int slot)
    {
        if (!QueueDropAllowed(data)) return false;
        if (data.GetDataPresent(QueueEntryDragFormat, false)) MoveQueuedSong((string)data.GetData(QueueEntryDragFormat, false), slot);
        else { if (AddQueuedSong(DroppedSong(data), slot) == null) return false; acceptedSongDrop = true; }
        return true;
    }
    private void AttachQueueDrop(Control surface)
    {
        surface.AllowDrop = true;
        DragEventHandler over = delegate(object sender, DragEventArgs e)
        {
            bool accepted = QueueDropAllowed(e.Data); e.Effect = !accepted ? DragDropEffects.None : e.Data.GetDataPresent(QueueEntryDragFormat, false) ? DragDropEffects.Move : DragDropEffects.Copy;
            queueList.Insertion = accepted ? queueList.DragAt(queueList.PointToClient(new Point(e.X, e.Y))) : -1;
        };
        surface.DragEnter += over; surface.DragOver += over; surface.DragLeave += delegate { queueList.Insertion = -1; };
        surface.DragDrop += delegate(object sender, DragEventArgs e) { int slot = queueList.InsertionAt(queueList.PointToClient(new Point(e.X, e.Y))); bool accepted = DropIntoQueue(e.Data, slot); queueList.Insertion = -1; e.Effect = accepted ? e.Data.GetDataPresent(QueueEntryDragFormat, false) ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None; };
    }
    private async Task<bool> AdvanceQueuedSong()
    {
        if (!queueAdvancePending || advancingQueue || requestingAutoNext || loading || editingTuning || tuningDialogOpen || editingArtists || preparingReplacement || preparingAutoPlay || autoTransition != null || output == null || mixer == null || output.PlaybackState != PlaybackState.Stopped || mixer.PositionFrames < mixer.TotalFrames) return false;
        FollowSessionSong(current);
        if (NextPlaybackEntry != null) return await PlayQueuedSong(NextPlaybackEntry);
        if (autoPlayEnabled) return false;
        List<SongInfo> playable = songs.Where(song => File.Exists(song.Audio) && songQueue.CanAdd(song.Audio)).ToList();
        if (playable.Count == 0) { queueAdvancePending = false; message.Text = "Session complete · no unplayed songs remain."; return false; }
        SongInfo random = playable[normalPlaybackRandom.Next(playable.Count)];
        PlaybackQueueEntry entry = AddQueuedSong(random, songQueue.Items.Count, true);
        return await PlayQueuedSong(entry);
    }
    private async Task<bool> PlayQueuedSong(PlaybackQueueEntry entry)
    {
        if (advancingQueue || loading || editingTuning || editingArtists || entry == null) return false;
        SongInfo next = QueuedSong(entry);
        if (next == null || !File.Exists(next.Audio)) { message.Text = "Queued song is unavailable. Remove it from Queue or restore it to the library."; return false; }
        advancingQueue = true;
        try { await LoadSong(next, true); if (mixer != null && current == next) { SelectQueuedSong(entry.Id); RenderSongs(); return true; } return false; }
        finally { advancingQueue = false; }
    }
    private void RemapPlaybackQueue(object[] mappings)
    {
        songQueue.Remap(audio => RemappedPath(audio, mappings));
        RenderPlaybackQueue();
    }
}

internal sealed class QueueList : SmoothList
{
    private sealed class PaintedRow
    {
        internal Bitmap Image;
        internal string Title, Artist, Music, Status;
        internal Image Artwork;
        internal Color Background;
        internal bool Selected, Locked, Played;
        internal LinkedListNode<string> Node;
    }
    private readonly Dictionary<string, PaintedRow> paintedRows = new Dictionary<string, PaintedRow>();
    private readonly LinkedList<string> paintedOrder = new LinkedList<string>();
    private readonly Font titleFont = new Font("Segoe UI", 11, FontStyle.Bold), artistFont = new Font("Segoe UI", 10), detailsFont = new Font("Segoe UI", 9.5f), statusFont = new Font("Segoe UI", 8.5f), removeFont = new Font("Segoe UI", 16);
    private readonly Dictionary<Image, Bitmap> smallArtwork = new Dictionary<Image, Bitmap>();
    private Image QueueArtwork(Image source)
    {
        if (source.Width == 40 && source.Height == 40) return source;
        Bitmap image;
        if (smallArtwork.TryGetValue(source, out image)) return image;
        if (smallArtwork.Count >= 256) { foreach (Bitmap cached in smallArtwork.Values) cached.Dispose(); smallArtwork.Clear(); }
        image = new Bitmap(40, 40, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, 40, 40));
        }
        smallArtwork[source] = image; return image;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (PaintedRow row in paintedRows.Values) row.Image.Dispose(); paintedRows.Clear(); paintedOrder.Clear();
            foreach (Bitmap cached in smallArtwork.Values) cached.Dispose(); smallArtwork.Clear();
            titleFont.Dispose(); artistFont.Dispose(); detailsFont.Dispose(); statusFont.Dispose(); removeFont.Dispose();
        }
        base.Dispose(disposing);
    }
    internal Action<string> RemoveEntry;
    internal Action<string> PlayEntry;
    internal Action<string, int> MoveEntry;
    internal Action<QueueRow> BeginEntryDrag;
    private QueueRow pressed;
    private Rectangle threshold;
    private int insertion = -1;
    private int lastScroll = unchecked(Environment.TickCount - 200);
    internal int SongCount { get { return Items.Count; } }
    // Keep the last song reachable at the top, with empty space below it.
    protected override double MaximumScroll { get { return Math.Max(0, SongCount - 1) * (double)ItemHeight; } }
    internal int Insertion { get { return insertion; } set { if (insertion != value) { insertion = value; Invalidate(); } } }
    internal QueueList()
    {
        ItemHeight = 44;
        BackColor = PlayerTheme.Glass; ForeColor = PlayerTheme.Text; Font = new Font("Segoe UI", 9); TabStop = true;
    }
    internal int InsertionAt(Point point)
    {
        if (Items.Count == 0 || point.Y < 0) return 0;
        int index = IndexFromPoint(point); if (index < 0 || index >= SongCount) return SongCount;
        Rectangle row = GetItemRectangle(index); return index + (point.Y >= row.Top + row.Height / 2 ? 1 : 0);
    }
    internal int DragAt(Point point)
    {
        if (Items.Count > 0 && point.X >= 0 && point.X < ClientSize.Width && point.Y >= 0 && point.Y < ClientSize.Height && unchecked(Environment.TickCount - lastScroll) >= 120)
        {
            if (point.Y < 22 && ScrollTarget > 0) { ScrollBy(-ItemHeight); lastScroll = Environment.TickCount; }
            else if (point.Y > ClientSize.Height - 22 && TopIndex < SongCount - 1) { ScrollBy(ItemHeight); lastScroll = Environment.TickCount; }
        }
        return InsertionAt(point);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        pressed = null; int index = IndexFromPoint(e.Location);
        if (index >= SongCount) return;
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || index < 0) return;
        QueueRow row = Items[index] as QueueRow; if (row == null || row.Entry == null || row.Locked) return;
        if (e.X >= ClientSize.Width - 36) { if (RemoveEntry != null) RemoveEntry(row.Entry.Id); return; }
        pressed = row; Size size = SystemInformation.DragSize; threshold = new Rectangle(e.X - size.Width / 2, e.Y - size.Height / 2, size.Width, size.Height);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left && pressed != null && !threshold.Contains(e.Location)) { QueueRow row = pressed; pressed = null; if (BeginEntryDrag != null) BeginEntryDrag(row); }
    }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = null; base.OnMouseUp(e); }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e); pressed = null;
        int index = IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0 && index < SongCount && (e.X < ClientSize.Width - 36 || ((QueueRow)Items[index]).Locked) && PlayEntry != null) PlayEntry(((QueueRow)Items[index]).Entry.Id);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        QueueRow row = SelectedItem as QueueRow;
        if (row != null && row.Entry != null && e.KeyCode == Keys.Enter) { if (PlayEntry != null) PlayEntry(row.Entry.Id); e.Handled = e.SuppressKeyPress = true; }
        else if (row != null && row.Entry != null && !row.Locked && e.KeyCode == Keys.Delete) { if (RemoveEntry != null) RemoveEntry(row.Entry.Id); e.Handled = e.SuppressKeyPress = true; }
        else if (row != null && row.Entry != null && !row.Locked && e.Control && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)) { if (MoveEntry != null) MoveEntry(row.Entry.Id, SelectedIndex + (e.KeyCode == Keys.Up ? -1 : 2)); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Down && SelectedIndex >= SongCount - 1) e.Handled = e.SuppressKeyPress = true;
        base.OnKeyDown(e);
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return; QueueRow row = Items[e.Index] as QueueRow; if (row == null) return;
        if (row.Entry == null) return;
        string title = row.Title, artist = row.Song == null ? "Unavailable in library" : row.Song.Artist;
        string music = row.Song == null ? "BPM unknown · Key unknown" : row.Song.MusicDetails;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        PaintedRow painted;
        if (!paintedRows.TryGetValue(row.Entry.Id, out painted))
        {
            painted = new PaintedRow(); paintedRows.Add(row.Entry.Id, painted);
            painted.Node = paintedOrder.AddLast(row.Entry.Id);
        }
        else { paintedOrder.Remove(painted.Node); paintedOrder.AddLast(painted.Node); }
        if (painted.Image == null || painted.Image.Width != ClientSize.Width || painted.Image.Height != e.Bounds.Height || painted.Title != title || painted.Artist != artist || painted.Music != music || painted.Status != row.Status || painted.Artwork != row.Artwork || painted.Selected != selected || painted.Locked != row.Locked || painted.Played != row.Played || painted.Background != BackColor)
        {
            if (painted.Image != null) painted.Image.Dispose();
            painted.Image = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, e.Bounds.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(painted.Image)) PaintRow(graphics, row, painted.Image.Height, selected, title, artist, music);
            painted.Title = title; painted.Artist = artist; painted.Music = music; painted.Status = row.Status; painted.Artwork = row.Artwork;
            painted.Selected = selected; painted.Locked = row.Locked; painted.Played = row.Played; painted.Background = BackColor;
        }
        // Keep enough rows for the viewport and a small overscan, independent of
        // how much playback history has accumulated.
        int capacity = Math.Max(32, ClientSize.Height / ItemHeight + 8);
        while (paintedRows.Count > capacity)
        {
            string oldest = paintedOrder.First.Value; paintedRows[oldest].Image.Dispose(); paintedRows.Remove(oldest); paintedOrder.RemoveFirst();
        }
        e.Graphics.DrawImageUnscaled(painted.Image, e.Bounds.Location);
        if (insertion == e.Index || insertion == SongCount && e.Index == SongCount - 1) using (Pen marker = new Pen(PlayerTheme.Green, 2)) { int y = insertion == e.Index ? e.Bounds.Top + 1 : e.Bounds.Bottom - 2; e.Graphics.DrawLine(marker, 6, y, ClientSize.Width - 6, y); }
    }
    private void PaintRow(Graphics g, QueueRow row, int height, bool selected, string title, string artist, string music)
    {
        Rectangle bounds = new Rectangle(0, 0, ClientSize.Width, height);
        using (Brush fill = new SolidBrush(selected ? Color.FromArgb(38, 33, 52) : BackColor)) g.FillRectangle(fill, bounds);
        Rectangle artwork = new Rectangle(8, (height - 40) / 2, 40, 40);
        if (row.Artwork != null) g.DrawImageUnscaled(QueueArtwork(row.Artwork), artwork.Location); else PlayerTheme.Icon(g, "wave", artwork, PlayerTheme.Purple);
        int textX = artwork.Right + 8, statusX = ClientSize.Width - 152, musicX = statusX - 204;
        int textWidth = Math.Max(1, musicX - 12 - textX), titleWidth = (int)(textWidth * .6), artistX = textX + titleWidth + 12;
        TextFormatFlags textFlags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, title, titleFont, new Rectangle(textX, 0, titleWidth, height), PlayerTheme.Text, textFlags);
        TextRenderer.DrawText(g, artist, artistFont, new Rectangle(artistX, 0, Math.Max(1, musicX - 12 - artistX), height), PlayerTheme.Muted, textFlags);
        TextRenderer.DrawText(g, music, detailsFont, new Rectangle(musicX, 0, 192, height), PlayerTheme.Muted, textFlags);
        TextRenderer.DrawText(g, row.Status, statusFont, new Rectangle(statusX, 0, 112, height), row.Locked ? PlayerTheme.Purple : PlayerTheme.Green, textFlags);
        if (!row.Locked) TextRenderer.DrawText(g, "×", removeFont, new Rectangle(ClientSize.Width - 34, 0, 28, height), PlayerTheme.Muted, textFlags | TextFormatFlags.HorizontalCenter);
        using (Pen edge = new Pen(PlayerTheme.Border)) g.DrawLine(edge, textX, height - 1, ClientSize.Width - 10, height - 1);
        if (row.Played) using (Brush tint = new SolidBrush(Color.FromArgb(100, Color.Black))) g.FillRectangle(tint, bounds);
    }
}
