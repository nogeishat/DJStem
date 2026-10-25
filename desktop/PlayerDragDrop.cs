using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private const string SongDragFormat = "StemKit.LibrarySong.v1";
    private bool draggingLibrarySong, acceptedSongDrop;

    private bool CanReplace(SongInfo song)
    {
        return !loading && !editingTuning && !AutoTransitionActive && mixer != null && mixer.HasStems && current != null && song != null && song.HasStems && !String.Equals(current.Audio, song.Audio, StringComparison.OrdinalIgnoreCase);
    }
    private SongInfo DroppedSong(IDataObject data)
    {
        if (data == null || !data.GetDataPresent(SongDragFormat, false)) return null;
        string audio = data.GetData(SongDragFormat, false) as string;
        foreach (SongInfo song in songs) if (String.Equals(song.Audio, audio, StringComparison.OrdinalIgnoreCase)) return song;
        return null;
    }
    private void BeginSongDrag(SongInfo song)
    {
        if (song == null || loading) return;
        bool fromBrowser = browser.Visible; acceptedSongDrop = false; draggingLibrarySong = true;
        try
        {
            DataObject data = new DataObject(); data.SetData(SongDragFormat, false, song.Audio);
            // The same form owns the drag, so switching views keeps the drag alive.
            if (fromBrowser) ShowLibrary(false);
            DoDragDrop(data, DragDropEffects.Copy);
        }
        finally
        {
            draggingLibrarySong = false; ClearDropHighlight(); RenderSongs();
            if (fromBrowser && !acceptedSongDrop) ShowLibrary(true);
        }
    }
    private void InitializeLibraryDragging()
    {
        list.BeginSongDrag = BeginSongDrag;
        list.SelectionCommitted += async delegate
        {
            if (rendering || list.SelectedItem == null) return;
            SongInfo selected = (SongInfo)list.SelectedItem;
            if (current == null || !String.Equals(current.Audio, selected.Audio, StringComparison.OrdinalIgnoreCase)) await LoadSong(selected, false);
        };
        tip.SetToolTip(list, "Drag a song into Queue to add it, or onto a stem to replace it. Click a song to load it.");
        for (int i = 0; i < 4; i++) AttachStemDrop(lanes[i], i);
    }
    private void AttachStemDrop(Control target, int index)
    {
        target.AllowDrop = true;
        DragEventHandler hover = delegate(object sender, DragEventArgs e)
        {
            bool accepted = CanReplace(DroppedSong(e.Data)); e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            ClearDropHighlight(); if (accepted) { lanes[index].Fill = PlayerTheme.Blend(PlayerTheme.Glass, PlayerTheme.Stems[index], .16f); lanes[index].Outline = PlayerTheme.Stems[index]; lanes[index].Invalidate(); }
        };
        target.DragEnter += hover; target.DragOver += hover;
        target.DragLeave += delegate { if (!lanes[index].ClientRectangle.Contains(lanes[index].PointToClient(Cursor.Position))) ClearDropHighlight(); };
        target.DragDrop += async delegate(object sender, DragEventArgs e)
        {
            SongInfo song = DroppedSong(e.Data); ClearDropHighlight();
            if (!CanReplace(song)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy; acceptedSongDrop = true; await ReplaceFromDrop(song, index);
        };
        foreach (Control child in target.Controls) AttachStemDrop(child, index);
    }
    private void ClearDropHighlight()
    {
        foreach (GlassPanel lane in lanes) if (lane != null) { lane.Fill = PlayerTheme.Glass; lane.Outline = PlayerTheme.Border; lane.Invalidate(); }
    }
}

// Delay loading a clicked song until release, so pressing a library row before
// a drag cannot replace the currently playing reference song.
internal sealed class SongList : SmoothList
{
    internal bool PointerSelecting;
    internal Action<SongInfo> BeginSongDrag;
    internal event EventHandler SelectionCommitted;
    private Rectangle dragThreshold;
    private SongInfo pressedSong;
    private bool dragged;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0201 || m.Msg == 0x0203) PointerSelecting = true; // left down / double click
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            PointerSelecting = true;
            int index = IndexFromPoint(e.Location); pressedSong = index < 0 ? null : Items[index] as SongInfo;
            Size size = SystemInformation.DragSize; dragThreshold = new Rectangle(e.X - size.Width / 2, e.Y - size.Height / 2, size.Width, size.Height); dragged = false;
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left || pressedSong == null || dragged || dragThreshold.Contains(e.Location) || BeginSongDrag == null) return;
        dragged = true; SongInfo song = pressedSong; pressedSong = null;
        try { BeginSongDrag(song); }
        finally { PointerSelecting = false; Capture = false; }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); PointerSelecting = false;
        if (e.Button == MouseButtons.Left && !dragged && pressedSong != null && SelectionCommitted != null) SelectionCommitted(this, EventArgs.Empty);
        pressedSong = null;
    }
}
