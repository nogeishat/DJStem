using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private readonly BackdropPanel browser = new BackdropPanel();
    private readonly LibraryGridPanel libraryGrid = new LibraryGridPanel();
    private readonly TextBox librarySearch = new TextBox();
    private readonly GlassPanel librarySearchBox = new GlassPanel(), libraryFooter = new GlassPanel();
    private readonly GlassButton libraryTab = new GlassButton();
    private readonly GlassButton browserPlayerTab = new GlassButton();
    private readonly GlassButton allSongsTab = new GlassButton(), artistsTab = new GlassButton(), libraryPlay = new GlassButton();
    private readonly Label libraryHeading = new PlayerLabel(), libraryCount = new PlayerLabel(), libraryNowTitle = new PlayerLabel(), libraryNowArtist = new PlayerLabel(), libraryClock = new PlayerLabel();
    private readonly PictureBox libraryNowCover = new PictureBox();
    private string artistFolder, browserFingerprint;
    private bool artistsView, fullscreen;
    private Rectangle normalBounds;
    private FormWindowState normalState;

    private void InitializeBrowser()
    {
        SetupButton(libraryTab, "Library", sidebar, null); libraryTab.Bounds = new Rectangle(16, 20, 72, 32); libraryTab.AccessibleName = "Open library"; libraryTab.Click += delegate { ShowLibrary(true); };
        browser.Dock = DockStyle.Fill; browser.Visible = false; Controls.Add(browser);
        browserPlayerTab.Bounds = new Rectangle(24, 13, 118, 32); SetupButton(browserPlayerTab, "Now playing", browser, null); browserPlayerTab.Click += delegate { ShowLibrary(false); };
        ConfigureLabel(libraryHeading, browser, "Your library", 24, PlayerTheme.Text, true);
        ConfigureLabel(libraryCount, browser, "", 10, PlayerTheme.Muted, false);
        SetupButton(allSongsTab, "All songs", browser, null); SetupButton(artistsTab, "Artist folders", browser, null);
        allSongsTab.Click += delegate { playlistsView = false; artistsView = false; artistFolder = null; RenderBrowser(true); };
        artistsTab.Click += delegate { playlistsView = false; artistsView = true; artistFolder = null; RenderBrowser(true); };
        SetupButton(manageArtist, "Manage artist", browser, null); manageArtist.Visible = false;
        manageArtist.Click += delegate { if (artistFolder != null) ArtistMenu(artistFolder).Show(manageArtist, new Point(0, manageArtist.Height)); };
        librarySearchBox.Fill = Color.FromArgb(23, 23, 31); librarySearchBox.Radius = 9; browser.Controls.Add(librarySearchBox);
        IconBadge searchIcon = new IconBadge { Glyph = "search", Accent = PlayerTheme.Muted, Bounds = new Rectangle(4, 3, 29, 29) }; librarySearchBox.Controls.Add(searchIcon);
        librarySearch.Bounds = new Rectangle(39, 7, 225, 22); librarySearch.BorderStyle = BorderStyle.None; librarySearch.BackColor = librarySearchBox.Fill; librarySearch.ForeColor = PlayerTheme.Text; librarySearch.AccessibleName = "Search full library"; librarySearchBox.Controls.Add(librarySearch);
        tip.SetToolTip(librarySearch, "Search songs, artist folders, playlists or saved mixes · Ctrl+F"); librarySearch.TextChanged += delegate { RenderBrowser(true); };
        InitializeLibraryFilters();
        libraryGrid.AutoScroll = true; libraryGrid.WrapContents = true; libraryGrid.BackColor = PlayerTheme.Background; libraryGrid.Padding = Padding.Empty; browser.Controls.Add(libraryGrid);
        libraryGrid.Resize += delegate { LayoutLibraryItems(); };
        browser.Controls.Add(libraryFooter); libraryFooter.Radius = 14;
        libraryNowCover.SizeMode = PictureBoxSizeMode.Zoom; libraryNowCover.BackColor = PlayerTheme.Glass; libraryNowCover.AccessibleName = "Now playing square cover"; libraryFooter.Controls.Add(libraryNowCover);
        ConfigureLabel(libraryNowTitle, libraryFooter, "Choose a song", 11, PlayerTheme.Text, true); libraryNowTitle.AutoEllipsis = true;
        ConfigureLabel(libraryNowArtist, libraryFooter, "Music keeps playing while you browse", 9, PlayerTheme.Muted, false); libraryNowArtist.AutoEllipsis = true;
        ConfigureLabel(libraryClock, libraryFooter, "0:00 / 0:00", 9, PlayerTheme.Muted, false); libraryClock.TextAlign = ContentAlignment.MiddleRight;
        SetupButton(libraryPlay, "", libraryFooter, "play"); libraryPlay.Radius = 20; libraryPlay.Selected = true; libraryPlay.Click += delegate { TogglePlay(); };
        GlassButton openPlayer = MakeButton("Open player", libraryFooter, 0, 0, 108, 34, null); openPlayer.Name = "OpenPlayer"; openPlayer.Click += delegate { ShowLibrary(false); };
        browser.Resize += delegate { LayoutBrowser(); };
        LayoutBrowser();
    }

    private void ShowLibrary(bool visible)
    {
        SuspendLayout(); browser.Visible = visible; main.Visible = sidebar.Visible = !visible;
        if (visible) { browser.BringToFront(); LayoutBrowser(); RenderBrowser(false); browserPlayerTab.Focus(); }
        else { main.BringToFront(); LayoutContent(); libraryTab.Focus(); }
        ResumeLayout(true); libraryGrid.PerformLayout();
    }

    private void LayoutBrowser()
    {
        int w = browser.ClientSize.Width, h = browser.ClientSize.Height;
        if (w < 200 || h < 200) return;
        libraryHeading.Bounds = new Rectangle(24, 69, w - (playlistsView ? myMixesView ? 350 : playlistFolderId == null ? 330 : 490 : 210), 40);
        manageArtist.Bounds = new Rectangle(w - 166, 75, 142, 32);
        libraryCount.Bounds = new Rectangle(26, 113, w - 52, 24);
        allSongsTab.Bounds = new Rectangle(24, 150, 100, 34); artistsTab.Bounds = new Rectangle(132, 150, 126, 34);
        playlistsTab.Bounds = new Rectangle(266, 150, 100, 34);
        mixesBackButton.Bounds = new Rectangle(w - 166, 75, 142, 32);
        LayoutPlaylistButtons(w);
        librarySearchBox.Bounds = new Rectangle(w - 304, 150, 280, 35);
        libraryBpmBar.Bounds = new Rectangle(24, 198, w - 48, 42);
        libraryGridView.Bounds = new Rectangle(libraryBpmBar.Width - 180, 5, 80, 30);
        libraryListView.Bounds = new Rectangle(libraryBpmBar.Width - 92, 5, 80, 30);
        libraryBpmBar.Visible = !playlistsView;
        libraryGrid.Bounds = new Rectangle(24, playlistsView ? 198 : 254, w - 48, Math.Max(100, h - (playlistsView ? 308 : 364)));
        libraryFooter.Bounds = new Rectangle(24, h - 90, w - 48, 70);
        libraryNowCover.Bounds = new Rectangle(10, 10, 50, 50);
        libraryNowTitle.Bounds = new Rectangle(76, 12, w - 465, 24); libraryNowArtist.Bounds = new Rectangle(76, 37, w - 465, 22);
        libraryClock.Bounds = new Rectangle(w - 378, 23, 136, 24);
        libraryPlay.Bounds = new Rectangle(w - 219, 15, 40, 40);
        libraryFooter.Controls["OpenPlayer"].Bounds = new Rectangle(w - 166, 18, 108, 34);
        LayoutLibraryItems();
    }

    private void LayoutLibraryItems()
    {
        if (!LibraryUsesListView) return;
        int width = Math.Max(100, libraryGrid.ClientSize.Width - 2);
        foreach (LibraryCard card in libraryGrid.Controls.OfType<LibraryCard>()) card.Width = width;
    }

    private static string ArtistDirectory(SongInfo song) { return Path.GetFileName(Path.GetDirectoryName(song.Audio)); }
    private void RenderBrowser(bool force)
    {
        if (!browser.Visible && !force) return;
        if (playlistsView) { RenderMixPlaylists(); return; }
        playlistsTab.Selected = false; playlistsTab.Invalidate(); mixesBackButton.Visible = false; LayoutBrowser();
        libraryCount.ForeColor = PlayerTheme.Muted; tip.SetToolTip(libraryCount, "");
        ReadArtistCovers();
        string fingerprint = String.Join("\n", songs.Select(s => s.Audio + "|" + s.Title + "|" + s.Artist + "|" + s.HasStems + "|" + s.MusicDetails + "|" + s.Bpm)) + "\n" + String.Join("\n", artistCovers.Select(cover => cover.Key + "|" + cover.Value)) + "\n" + librarySearch.Text + "|" + artistsView + "|" + artistFolder + "|" + libraryBpmSort.SelectedIndex + "|" + libraryBpmRange.Checked + "|" + libraryBpmFrom.Value + "|" + libraryBpmTo.Value + "|" + libraryListMode;
        if (!force && fingerprint == browserFingerprint) return;
        browserFingerprint = fingerprint;
        List<SongInfo> matching = MatchingLibrarySongs();
        bool songView = !artistsView || artistFolder != null;
        bool listView = LibraryUsesListView;
        libraryBpmSort.Enabled = !artistsView || artistFolder != null;
        libraryGridView.Visible = libraryListView.Visible = songView;
        libraryGrid.SuspendLayout();
        libraryGrid.FlowDirection = listView ? FlowDirection.TopDown : FlowDirection.LeftToRight;
        libraryGrid.WrapContents = !listView;
        libraryGridView.Selected = !listView; libraryListView.Selected = listView;
        libraryGridView.Invalidate(); libraryListView.Invalidate();
        foreach (Control child in libraryGrid.Controls.Cast<Control>().ToArray()) child.Dispose();
        allSongsTab.Selected = !artistsView; artistsTab.Selected = artistsView; allSongsTab.Invalidate(); artistsTab.Invalidate();
        libraryHeading.Text = artistFolder ?? "Your library";
        manageArtist.Visible = artistFolder != null;
        if (artistsView && artistFolder == null)
        {
            var groups = matching.GroupBy(ArtistDirectory, StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
            libraryCount.Text = groups.Count + " artist folders · " + matching.Count + " songs";
            foreach (var group in groups)
            {
                string folder = group.Key;
                LibraryCard card = new LibraryCard { Folder = true, Title = folder, Artwork = ArtistCover(folder), Subtitle = group.Count() + (group.Count() == 1 ? " song" : " songs"), AccessibleName = "Artist folder: " + folder, Size = new Size(200, 258) };
                card.Tag = folder; card.ContextMenuStrip = ArtistMenu(folder);
                card.RenameFolder = async delegate { await RenameArtistFolder(folder); };
                tip.SetToolTip(card, folder + "\nRight-click to rename or choose a cover picture. F2 renames.");
                card.Click += delegate { artistFolder = folder; RenderBrowser(true); }; libraryGrid.Controls.Add(card);
            }
        }
        else
        {
            libraryCount.Text = matching.Count + (matching.Count == 1 ? " song" : " songs") + (artistFolder == null ? " · " + songs.Select(ArtistDirectory).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() + " artist folders" : " · artist folder");
            foreach (SongInfo song in matching)
            {
                SongInfo selected = song;
                LibraryCard card = new LibraryCard { Tag = song, Title = song.Title, Subtitle = song.Artist, Details = song.MusicDetails, Artwork = Thumbnail(song), AccessibleName = "Play " + song.Title + " by " + song.Artist + ", " + song.MusicDetails, Size = new Size(200, 284) };
                card.BeginSongDrag = delegate { BeginSongDrag(selected); };
                card.ContextMenuStrip = songActionsMenu;
                tip.SetToolTip(card, song.Title + "\n" + song.Artist + "\n" + song.MusicDetails + "\nDrag onto a Now playing stem to prepare replacements.");
                card.Click += async delegate
                {
                    if (loading) return;
                    ShowLibrary(false);
                    if (current != null && String.Equals(current.Audio, selected.Audio, StringComparison.OrdinalIgnoreCase) && mixer != null) { if (output != null && output.PlaybackState != NAudio.Wave.PlaybackState.Playing) TogglePlay(); }
                    else { await LoadSong(selected, true); RenderSongs(); }
                };
                libraryGrid.Controls.Add(card);
            }
        }
        if (libraryGrid.Controls.Count == 0)
        {
            Label empty = new Label { Text = songs.Count == 0 ? "Your songs will appear here. Use Add songs to open the drop box." : "No matching songs or artist folders.", ForeColor = PlayerTheme.Muted, AutoSize = true, Margin = new Padding(8, 20, 0, 0) }; libraryGrid.Controls.Add(empty);
        }
        foreach (LibraryCard card in libraryGrid.Controls.OfType<LibraryCard>())
        {
            card.ListRow = listView;
            if (listView) { card.Height = 72; card.Margin = new Padding(0, 0, 0, 8); }
        }
        LayoutLibraryItems();
        libraryGrid.AutoScrollPosition = Point.Empty; libraryGrid.ResumeLayout(true); libraryGrid.PerformLayout();
    }

    private void UpdateBrowserSong()
    {
        libraryNowTitle.Text = current == null ? "Choose a song" : current.Title;
        libraryNowArtist.Text = current == null ? "Music keeps playing while you browse" : current.Artist;
        libraryNowCover.Image = current == null ? null : Thumbnail(current);
    }
    private void UpdateBrowserTransport(bool playing, double position, double length)
    {
        libraryPlay.Enabled = mixer != null && !loading;
        string glyph = playing ? "pause" : "play";
        if (libraryPlay.Glyph != glyph) { libraryPlay.Glyph = glyph; libraryPlay.Invalidate(); }
        libraryPlay.AccessibleName = playing ? "Pause library playback" : "Play library playback";
        libraryClock.Text = Clock(position) + " / " + Clock(length);
    }
    private void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            normalState = WindowState; normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; fullscreen = true;
        }
        else
        {
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.Sizable; Bounds = normalBounds; WindowState = normalState; fullscreen = false;
        }
    }
    private static Bitmap SquareCover(Image source, int size)
    {
        Bitmap result = new Bitmap(size, size); int side = Math.Min(source.Width, source.Height);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size), new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        }
        return result;
    }
}

internal sealed class LibraryCard : Control
{
    internal bool Folder;
    internal bool ListRow;
    internal string Title, Subtitle, Details;
    internal string ActionName;
    internal Image Artwork;
    internal Action BeginSongDrag;
    internal Action RenameFolder;
    private bool hovered;
    private Rectangle dragThreshold;
    private bool dragged;
    private bool rightClick;
    private Bitmap painted;
    private string paintedTitle, paintedSubtitle, paintedDetails;
    private Image paintedArtwork;
    private bool paintedFolder, paintedListRow, paintedHover, paintedFocus;
    private LinkedListNode<LibraryCard> paintNode;
    private static readonly LinkedList<LibraryCard> paintCache = new LinkedList<LibraryCard>();
    private static long paintCacheBytes;
    private void ClearPaintCache()
    {
        if (painted != null) { paintCacheBytes -= (long)painted.Width * painted.Height * 4; painted.Dispose(); painted = null; }
        if (paintNode != null) { paintCache.Remove(paintNode); paintNode = null; }
    }
    private void TouchPaintCache()
    {
        if (paintNode != null) paintCache.Remove(paintNode);
        paintNode = paintCache.AddLast(this);
        while (paintCache.Count > 1 && (paintCache.Count > 64 || paintCacheBytes > 24L * 1024 * 1024)) paintCache.First.Value.ClearPaintCache();
    }
    internal LibraryCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.Opaque, true);
        Margin = new Padding(0, 0, 16, 16); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
        MouseEnter += delegate { hovered = true; Invalidate(); }; MouseLeave += delegate { hovered = false; Invalidate(); };
        GotFocus += delegate { Invalidate(); }; LostFocus += delegate { Invalidate(); };
    }
    protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.F2 || base.IsInputKey(keyData); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; dragged = rightClick = false; OnClick(EventArgs.Empty); } else if (e.KeyCode == Keys.F2 && RenameFolder != null) { e.Handled = e.SuppressKeyPress = true; RenameFolder(); } base.OnKeyDown(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus(); dragged = false; rightClick = e.Button != MouseButtons.Left; Size size = SystemInformation.DragSize; dragThreshold = new Rectangle(e.X - size.Width / 2, e.Y - size.Height / 2, size.Width, size.Height); base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left && !dragged && !Folder && BeginSongDrag != null && !dragThreshold.Contains(e.Location)) { dragged = true; BeginSongDrag(); }
    }
    protected override void OnClick(EventArgs e) { if (!dragged && !rightClick) base.OnClick(e); }
    protected override AccessibleObject CreateAccessibilityInstance() { return new CardAccessibility(this); }
    private sealed class CardAccessibility : ControlAccessibleObject
    {
        private readonly LibraryCard card;
        internal CardAccessibility(LibraryCard owner) : base(owner) { card = owner; }
        public override string DefaultAction { get { return card.ActionName ?? (card.Folder ? "Open folder" : "Play song"); } }
        public override void DoDefaultAction() { if (card.Enabled) { card.dragged = card.rightClick = false; card.OnClick(EventArgs.Empty); } }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (painted == null || painted.Size != Size || paintedTitle != Title || paintedSubtitle != Subtitle || paintedDetails != Details || paintedArtwork != Artwork || paintedFolder != Folder || paintedListRow != ListRow || paintedHover != hovered || paintedFocus != Focused)
        {
            ClearPaintCache();
            painted = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            paintCacheBytes += (long)Width * Height * 4;
            using (Graphics graphics = Graphics.FromImage(painted)) PaintCard(graphics);
            paintedTitle = Title; paintedSubtitle = Subtitle; paintedDetails = Details; paintedArtwork = Artwork;
            paintedFolder = Folder; paintedListRow = ListRow; paintedHover = hovered; paintedFocus = Focused;
        }
        TouchPaintCache(); e.Graphics.DrawImageUnscaled(painted, Point.Empty);
    }
    protected override void Dispose(bool disposing) { if (disposing) ClearPaintCache(); base.Dispose(disposing); }
    protected override void OnParentChanged(EventArgs e) { if (Parent == null) ClearPaintCache(); base.OnParentChanged(e); }
    private void PaintCard(Graphics g)
    {
        g.Clear(PlayerTheme.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath shape = PlayerTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), ListRow ? 9 : 14))
        {
            using (Brush brush = new SolidBrush(hovered ? Color.FromArgb(34, 33, 46) : PlayerTheme.Glass)) g.FillPath(brush, shape);
            using (Pen pen = new Pen(Focused ? PlayerTheme.Purple : PlayerTheme.Border)) g.DrawPath(pen, shape);
        }
        if (ListRow) { PaintListRow(g); return; }
        int titleY;
        if (Folder && Artwork == null)
        {
            Rectangle square = new Rectangle(12, 12, Width - 24, Width - 24);
            using (Brush background = new SolidBrush(Color.FromArgb(35, 30, 49))) using (GraphicsPath shape = PlayerTheme.Rounded(square, 9)) g.FillPath(background, shape);
            using (Brush brush = new SolidBrush(Color.FromArgb(46, 39, 64)))
            { g.FillRectangle(brush, 53, 66, 38, 14); g.FillRectangle(brush, 53, 78, 94, 60); }
            using (Pen pen = new Pen(PlayerTheme.Purple, 2)) g.DrawRectangle(pen, 53, 78, 94, 60);
            titleY = Width + 1;
        }
        else
        {
            Rectangle square = new Rectangle(12, 12, Width - 24, Width - 24);
            using (GraphicsPath clip = PlayerTheme.Rounded(square, 9))
            {
                GraphicsState state = g.Save(); g.SetClip(clip);
                if (Artwork != null) g.DrawImage(Artwork, square);
                else { using (Brush brush = new SolidBrush(Color.FromArgb(35, 30, 49))) g.FillRectangle(brush, square); PlayerTheme.Icon(g, "wave", new Rectangle(64, 64, 70, 70), PlayerTheme.Purple); }
                g.Restore(state);
            }
            titleY = Width + 1;
        }
        using (Font font = new Font("Segoe UI", 10, FontStyle.Bold)) TextRenderer.DrawText(g, Title, font, new Rectangle(14, titleY, Width - 28, 24), PlayerTheme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using (Font font = new Font("Segoe UI", 9)) TextRenderer.DrawText(g, Subtitle, font, new Rectangle(14, titleY + 26, Width - 28, 22), PlayerTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!String.IsNullOrEmpty(Details)) using (Font font = new Font("Segoe UI", 8.5f)) TextRenderer.DrawText(g, Details, font, new Rectangle(14, titleY + 50, Width - 28, 20), PlayerTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
    private void PaintListRow(Graphics g)
    {
        Rectangle square = new Rectangle(4, 4, Height - 8, Height - 8);
        using (GraphicsPath clip = PlayerTheme.Rounded(square, 6))
        {
            GraphicsState state = g.Save(); g.SetClip(clip);
            using (Brush background = new SolidBrush(Color.FromArgb(35, 30, 49))) g.FillRectangle(background, square);
            if (Artwork != null) g.DrawImage(Artwork, square);
            else if (Folder)
            {
                using (Pen pen = new Pen(PlayerTheme.Purple, 1.5f))
                { g.DrawRectangle(pen, 16, 29, 40, 27); g.DrawLines(pen, new[] { new Point(16, 29), new Point(16, 22), new Point(33, 22), new Point(39, 29) }); }
            }
            else PlayerTheme.Icon(g, "wave", new Rectangle(16, 16, 40, 40), PlayerTheme.Purple);
            g.Restore(state);
        }
        int detailsWidth = Folder ? 150 : 240;
        int titleX = square.Right + 16, detailsX = Width - detailsWidth - 16;
        int titleWidth = Folder ? detailsX - titleX - 12 : Math.Max(100, (detailsX - titleX - 12) * 3 / 5);
        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        using (Font titleFont = new Font("Segoe UI", Folder ? 10 : 20, FontStyle.Bold))
            TextRenderer.DrawText(g, Title, titleFont, new Rectangle(titleX, 4, titleWidth, Height - 8), PlayerTheme.Text, flags);
        using (Font font = new Font("Segoe UI", 9))
        {
            if (!Folder) TextRenderer.DrawText(g, Subtitle, font, new Rectangle(titleX + titleWidth + 12, 4, Math.Max(1, detailsX - titleX - titleWidth - 24), Height - 8), PlayerTheme.Muted, flags);
            TextRenderer.DrawText(g, Folder ? Subtitle : Details, font, new Rectangle(detailsX, 4, detailsWidth, Height - 8), PlayerTheme.Muted, flags | TextFormatFlags.Right);
        }
    }
}
