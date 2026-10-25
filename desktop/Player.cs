using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal sealed partial class PlayerForm : Form
{
    private readonly string library, project, preferenceFile;
    private readonly BackdropPanel main = new BackdropPanel(), content = new BackdropPanel();
    private readonly Panel sidebar = new Panel();
    private readonly GlassPanel info = new GlassPanel(), artwork = new GlassPanel(), transport = new GlassPanel();
    private readonly SongList list = new SongList();
    private readonly TextBox search = new TextBox();
    private readonly PictureBox cover = new PictureBox();
    private readonly Label songTitle = new PlayerLabel(), artistLabel = new PlayerLabel(), message = new PlayerLabel(), countLabel = new PlayerLabel(), elapsed = new PlayerLabel(), duration = new PlayerLabel(), volumeLabel = new PlayerLabel();
    private readonly MixSlider seek = new MixSlider(), volume = new MixSlider();
    private readonly GlassButton play = new GlassButton(), previous = new GlassButton(), next = new GlassButton(), mixMode = new GlassButton(), originalMode = new GlassButton();
    private readonly GlassPanel[] lanes = new GlassPanel[4];
    private readonly GlassButton[] mutes = new GlassButton[4], solos = new GlassButton[4];
    private readonly MixSlider[] faders = new MixSlider[4];
    private readonly WaveformView[] waveforms = new WaveformView[4];
    private readonly Label[] stemLoopLabels = new Label[4], stemOffsetLabels = new Label[4];
    private readonly PlayerMix mix = new PlayerMix();
    private const string StemSwitchKeys = "QWER", StemMuteKeys = "ASDF";
    private readonly HashSet<Keys> stemShortcutDown = new HashSet<Keys>();
    private readonly Dictionary<string, Image> thumbnails = new Dictionary<string, Image>();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly ToolTip tip = new ToolTip { Active = false };
    private List<SongInfo> songs = new List<SongInfo>();
    private SongInfo current;
    private StemMixer mixer;
    private WaveOutEvent output;
    private CancellationTokenSource waveformCancellation;
    private bool loading, refreshing, rendering, readingPreferences;
    private int generation, refreshTicks;
    private string preferredSong;
    private bool preferOriginal;

    internal PlayerForm(string root, string projectFolder)
    {
        library = Path.GetFullPath(root); project = projectFolder; preferenceFile = PlayerData.Data(project, "player-settings.json");
        Text = "DJStem"; ClientSize = new Size(1220, 790); MinimumSize = new Size(1120, 740);
        StartPosition = FormStartPosition.CenterScreen; ShowIcon = true; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); BackColor = PlayerTheme.Background;
        Font = new Font("Segoe UI", 10); KeyPreview = true; DoubleBuffered = true;
        sidebar.Dock = DockStyle.Left; sidebar.Width = 264; sidebar.BackColor = PlayerTheme.Sidebar;
        main.Dock = DockStyle.Fill; content.Margin = Padding.Empty; main.Controls.Add(content); Controls.Add(main); Controls.Add(sidebar);
        sidebar.Paint += delegate(object sender, PaintEventArgs e) { using (Pen edge = new Pen(PlayerTheme.Border)) e.Graphics.DrawLine(edge, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height); };

        GlassButton add = MakeButton("", sidebar, 217, 20, 30, 32, "plus"); add.AccessibleName = "Add songs"; tip.SetToolTip(add, "Open the link drop box to add songs"); add.Click += delegate { OpenDropBox(); };
        GlassPanel searchBox = new GlassPanel { Bounds = new Rectangle(16, 64, 232, 35), Radius = 9, Fill = Color.FromArgb(19, 19, 27) }; sidebar.Controls.Add(searchBox);
        IconBadge searchIcon = new IconBadge { Glyph = "search", Accent = PlayerTheme.Muted, Bounds = new Rectangle(4, 3, 29, 29) }; searchBox.Controls.Add(searchIcon);
        search.Bounds = new Rectangle(39, 7, 181, 22); search.BorderStyle = BorderStyle.None; search.BackColor = searchBox.Fill; search.ForeColor = PlayerTheme.Text; search.Font = new Font("Segoe UI", 10);
        search.AccessibleName = "Search songs"; tip.SetToolTip(search, "Search by song or artist · Ctrl+F"); searchBox.Controls.Add(search);
        list.Bounds = new Rectangle(12, 114, 240, 580); list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        list.BackColor = PlayerTheme.Sidebar; list.ForeColor = PlayerTheme.Text; list.ItemHeight = 66;
        list.AccessibleName = "Downloaded songs"; list.DrawItem += DrawSong; sidebar.Controls.Add(list);
        countLabel.Bounds = new Rectangle(22, 704, 224, 24); countLabel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; countLabel.ForeColor = PlayerTheme.Muted; countLabel.Font = new Font("Segoe UI", 9); sidebar.Controls.Add(countLabel);
        GlassButton refresh = MakeButton("Refresh", sidebar, 174, 738, 74, 36, null); refresh.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; refresh.Click += async delegate { await RefreshLibrary(); };
        GlassButton open = MakeButton("Open music folder", sidebar, 16, 738, 150, 36, null); open.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        open.Click += delegate { try { Process.Start("explorer.exe", "\"" + library + "\""); } catch (Exception e) { ShowError(e); } }; tip.SetToolTip(open, library);

        InitializeBrowser();
        InitializeAutoPlay();
        artwork.Radius = 14; artwork.Fill = Color.FromArgb(7, 7, 12); content.Controls.Add(artwork);
        cover.Bounds = new Rectangle(1, 1, 298, 218); cover.BackColor = artwork.Fill; cover.SizeMode = PictureBoxSizeMode.Zoom; cover.AccessibleName = "Saved song cover"; cover.TabStop = false; artwork.Controls.Add(cover);
        cover.Resize += delegate { Region old = cover.Region; using (System.Drawing.Drawing2D.GraphicsPath shape = PlayerTheme.Rounded(cover.ClientRectangle, 12)) cover.Region = new Region(shape); if (old != null) old.Dispose(); };
        content.Controls.Add(info);
        ConfigureLabel(songTitle, info, "Choose a song", 18, PlayerTheme.Text, true); songTitle.AutoEllipsis = true;
        ConfigureLabel(artistLabel, info, "Your downloaded music", 10, PlayerTheme.Muted, false);
        InitializeMatching();
        InitializeAddedSongHeader();
        InitializeTuning();
        SetupButton(mixMode, "Stem mix", transport, null); SetupButton(originalMode, "Original MP3", transport, null);
        mixMode.Font = originalMode.Font = new Font("Segoe UI", 8.5f); mixMode.Radius = originalMode.Radius = 13;
        mixMode.Toggle = originalMode.Toggle = true;
        mixMode.Click += delegate { ChangeMode(false); }; originalMode.Click += delegate { ChangeMode(true); };

        content.Controls.Add(transport);
        ConfigureLabel(elapsed, transport, "0:00", 9, PlayerTheme.Muted, false); elapsed.TextAlign = ContentAlignment.MiddleRight;
        ConfigureLabel(duration, transport, "0:00", 9, PlayerTheme.Muted, false);
        seek.Maximum = 10000; seek.Accent = PlayerTheme.Purple; seek.GradientFollowsFill = true; seek.AccessibleName = "Playback position"; seek.AccessibleDescription = "Drag to seek; arrow keys move one percent; Home restarts.";
        seek.Committed += delegate { if (mixer != null) SeekTo(seek.Value / 10000.0 * mixer.Duration); }; transport.Controls.Add(seek);
        SetupButton(play, "", transport, "play"); play.AccessibleName = "Play"; play.Selected = true; play.Radius = 22; tip.SetToolTip(play, "Play / pause · Space"); play.Click += delegate { TogglePlay(); };
        SetupButton(previous, "", transport, "previous"); previous.AccessibleName = "Previous song"; previous.Plain = true; previous.Click += async delegate { await MoveSong(-1); }; tip.SetToolTip(previous, "Load the previous song in this session");
        SetupButton(next, "", transport, "next"); next.AccessibleName = "Next song"; next.Plain = true; next.Click += async delegate { await MoveSong(1); }; tip.SetToolTip(next, "Next song");
        volume.Maximum = 100; volume.Value = 70; volume.AccessibleName = "Master volume"; transport.Controls.Add(volume);
        ConfigureLabel(volumeLabel, transport, "70%", 8.5f, PlayerTheme.Muted, false); volumeLabel.TextAlign = ContentAlignment.MiddleRight;
        IconBadge volumeIcon = new IconBadge { Glyph = "volume", Accent = PlayerTheme.Muted, Bounds = new Rectangle(0, 0, 28, 28), Name = "VolumeIcon" }; transport.Controls.Add(volumeIcon);
        volume.Changed += delegate { volumeLabel.Text = volume.Value + "%"; tip.SetToolTip(volume, "Master volume " + volume.Value + "%"); if (playback != null) playback.Apply(volume.Value / 100f, mix); else if (mixer != null) mixer.Volume = volume.Value / 100f; SavePreferences(); };

        for (int i = 0; i < 4; i++)
        {
            int index = i; string stem = StemMixer.StemNames[i];
            GlassPanel lane = lanes[i] = new GlassPanel { Radius = 12 }; content.Controls.Add(lane);
            IconBadge icon = new IconBadge { Glyph = stem, Accent = PlayerTheme.Stems[i], Bounds = new Rectangle(13, 20, 34, 34) }; lane.Controls.Add(icon);
            InitializeSourceButton(i);
            mutes[i] = MakeButton("M", lane, 145, 24, 26, 26, null); mutes[i].SelectedFill = PlayerTheme.Stems[1]; mutes[i].Radius = 7; mutes[i].Font = new Font("Segoe UI", 8.5f, FontStyle.Bold); mutes[i].AccessibleName = "Mute " + stem; tip.SetToolTip(mutes[i], "Mute " + stem + " · " + StemMuteKeys[i]);
            mutes[i].Click += delegate { mix.ToggleMute(index); SelectMix(); ApplyMix(); };
            mutes[i].Toggle = true;
            solos[i] = MakeButton("S", lane, 178, 24, 26, 26, null); solos[i].SelectedFill = Color.FromArgb(252, 211, 77); solos[i].Radius = 7; solos[i].Font = mutes[i].Font; solos[i].AccessibleName = "Solo " + stem; tip.SetToolTip(solos[i], "Solo " + stem + " (multiple solos allowed)");
            solos[i].Click += delegate { mix.ToggleSolo(index); SelectMix(); ApplyMix(); };
            solos[i].Toggle = true;
            waveforms[i] = new WaveformView { Accent = PlayerTheme.Stems[i], AccessibleName = TitleCase(stem) + " waveform" }; waveforms[i].Seek += SeekTo; InitializeStemGestures(i); lane.Controls.Add(waveforms[i]);
            faders[i] = new MixSlider { Accent = volume.Accent, Gradient = volume.Gradient, Value = 100, AccessibleName = TitleCase(stem) + " volume" }; lane.Controls.Add(faders[i]);
            stemLoopLabels[i] = AddLabel(lane, "", 0, 0, 128, 16, 8, PlayerTheme.Muted); stemLoopLabels[i].TextAlign = ContentAlignment.MiddleCenter; stemLoopLabels[i].AutoEllipsis = true; stemLoopLabels[i].Visible = false; stemLoopLabels[i].AccessibleName = TitleCase(stem) + " loop length";
            stemOffsetLabels[i] = AddLabel(lane, "", 0, 0, 128, 16, 8, PlayerTheme.Muted); stemOffsetLabels[i].TextAlign = ContentAlignment.MiddleCenter; stemOffsetLabels[i].AutoEllipsis = true; stemOffsetLabels[i].Visible = false; stemOffsetLabels[i].AccessibleName = TitleCase(stem) + " beat offset";
            faders[i].Changed += delegate { if (readingPreferences) return; mix.Levels[index] = faders[index].Value / 100f; mix.Preset = "custom"; SelectMix(); ApplyMix(); };
        }
        ConfigureLabel(message, content, "Select a song from your library to begin.", 9, PlayerTheme.Muted, false); message.AutoEllipsis = true;
        InitializeLibraryDragging();
        InitializeQueue();
        InitializeSavedMixes();
        InitializeTransitionEditor();
        main.Resize += delegate { LayoutContent(); }; search.TextChanged += delegate { RenderSongs(); };
        list.SelectedIndexChanged += async delegate { if (!rendering && !list.PointerSelecting && !draggingLibrarySong && list.SelectedItem != null) await LoadSong((SongInfo)list.SelectedItem, false); };
        list.DoubleClick += delegate { if (!loading && output != null && output.PlaybackState != PlaybackState.Playing) TogglePlay(); };
        timer.Interval = 100; timer.Tick += async delegate { await TickAutoPlay(); UpdateTransport(); if (++refreshTicks >= 150) { refreshTicks = 0; await RefreshLibrary(); } };
        IDisposable matchedCacheMaintenance = null;
        Shown += async delegate { matchedCacheMaintenance = PlayerMatchedCache.StartMaintenance(project); await RefreshLibrary(); timer.Start(); };
        Disposed += delegate { if (matchedCacheMaintenance != null) matchedCacheMaintenance.Dispose(); };
        FormClosed += delegate { timer.Stop(); generation++; StopAutoTimeline(); EndQueueSession(); CancelMatching(); CancelTuning(); if (waveformCancellation != null) waveformCancellation.Cancel(); SavePreferences(); CloseAudio(); if (cover.Image != null) cover.Image.Dispose(); foreach (Image image in thumbnails.Values) image.Dispose(); tip.Dispose(); };
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (HandleStemShortcut(e)) return;
            if (e.Control && e.KeyCode == Keys.F) { TextBox target = browser.Visible ? librarySearch : search; target.Focus(); target.SelectAll(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.F11) { ToggleFullscreen(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { if (fullscreen) ToggleFullscreen(); else if (browser.Visible) ShowLibrary(false); else if (queueVisible) ShowPlaybackQueue(false); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Space && !e.Alt && !e.Control && !search.Focused && !librarySearch.Focused) { e.SuppressKeyPress = true; TogglePlay(); }
        };
        KeyUp += delegate(object sender, KeyEventArgs e) { stemShortcutDown.Remove(e.KeyCode); };
        Deactivate += delegate { stemShortcutDown.Clear(); };
        ReadPreferences(); volumeLabel.Text = volume.Value + "%"; StyleMix(); StyleModes(); LayoutContent(); SetEnabled(false);
    }

    private bool HandleStemShortcut(KeyEventArgs e)
    {
        if (e.Modifiers != Keys.None) return false;
        Control focused = ActiveControl;
        while (focused is ContainerControl && ((ContainerControl)focused).ActiveControl != null) focused = ((ContainerControl)focused).ActiveControl;
        if (focused is TextBoxBase || focused is ComboBox || focused is UpDownBase) return false;
        int lane = StemSwitchKeys.IndexOf((char)e.KeyCode); bool mute = false;
        if (lane < 0) { lane = StemMuteKeys.IndexOf((char)e.KeyCode); mute = true; }
        if (lane < 0) return false;
        // A held key acts once. Consume unavailable controls too, so a letter
        // cannot unexpectedly select a different song in the library list.
        if (stemShortcutDown.Add(e.KeyCode)) (mute ? mutes[lane] : sourceButtons[lane]).PerformClick();
        e.Handled = e.SuppressKeyPress = true;
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); int color = 11 | (11 << 8) | (16 << 16); DwmSetWindowAttribute(Handle, 35, ref color, 4); } catch { }
    }
    private static string TitleCase(string value) { return Char.ToUpperInvariant(value[0]) + value.Substring(1); }
    private void ConfigureLabel(Label label, Control parent, string text, float size, Color color, bool bold)
    {
        PlayerLabel playerLabel = label as PlayerLabel; if (playerLabel != null) playerLabel.HoverInfo = tip;
        label.Text = text; label.UseMnemonic = false; label.ForeColor = color; label.BackColor = Color.Transparent; label.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular); parent.Controls.Add(label);
    }
    private Label AddLabel(Control parent, string text, int x, int y, int w, int h, float size, Color color)
    {
        Label label = new PlayerLabel { Bounds = new Rectangle(x, y, w, h) }; ConfigureLabel(label, parent, text, size, color, false); return label;
    }
    private static void SetupButton(GlassButton button, string text, Control parent, string glyph) { button.Text = text; button.Glyph = glyph; parent.Controls.Add(button); }
    private static GlassButton MakeButton(string text, Control parent, int x, int y, int w, int h, string glyph)
    {
        GlassButton button = new GlassButton { Bounds = new Rectangle(x, y, w, h) }; SetupButton(button, text, parent, glyph); return button;
    }
    private void LayoutContent()
    {
        if (lanes[0] == null) return;
        content.Size = new Size(Math.Max(1, main.ClientSize.Width - 1), Math.Max(700, main.ClientSize.Height - 1));
        int w = content.Width - 48;
        // Reserve a name row only while editing a saved mix.
        LayoutSongHeader(w, String.IsNullOrEmpty(savedMixLabel.Text) ? 18 : 50);
        transport.Bounds = new Rectangle(24, artwork.Bottom + 16, w, 112);
        elapsed.Bounds = new Rectangle(12, 13, 43, 23); duration.Bounds = new Rectangle(w - 55, 13, 43, 23); seek.Bounds = new Rectangle(64, 12, w - 126, 26);
        previous.Bounds = new Rectangle(14, 58, 27, 34); play.Bounds = new Rectangle(47, 53, 44, 44); next.Bounds = new Rectangle(97, 58, 27, 34);
        int volumeWidth = Math.Max(80, Math.Min(180, w - 628));
        transport.Controls["VolumeIcon"].Bounds = new Rectangle(w - volumeWidth - 76, 61, 26, 26);
        volume.Bounds = new Rectangle(w - volumeWidth - 48, 60, volumeWidth, 30); volumeLabel.Bounds = new Rectangle(w - 45, 64, 32, 21);
        int modesX = Math.Max(140, Math.Min((w - 396) / 2, w - volumeWidth - 76 - 16 - 396));
        mixMode.Bounds = new Rectangle(modesX, 59, 89, 32); originalMode.Bounds = new Rectangle(modesX + 97, 59, 110, 32); resetStems.Bounds = new Rectangle(modesX + 215, 59, 97, 32);
        const int laneGap = 8, bottomSpace = 47;
        int lanesTop = transport.Bottom + 16;
        int laneHeight = Math.Max(50, (content.Height - lanesTop - 3 * laneGap - bottomSpace) / 4);
        for (int i = 0; i < 4; i++)
        {
            lanes[i].Bounds = new Rectangle(24, lanesTop + i * (laneHeight + laneGap), w, laneHeight);
            lanes[i].Controls[0].Bounds = new Rectangle(13, (laneHeight - 34) / 2, 34, 34);
            int extra = AutoEditorEnabled ? 64 : 0, sourceHeight = AutoEditorEnabled ? 42 : 28;
            sourceButtons[i].Bounds = new Rectangle(58, (laneHeight - sourceHeight) / 2, 84 + extra, sourceHeight);
            mutes[i].Bounds = new Rectangle(145 + extra, (laneHeight - 26) / 2, 26, 26); solos[i].Bounds = new Rectangle(178 + extra, (laneHeight - 26) / 2, 26, 26);
            int faderWidth = 128;
            waveforms[i].Bounds = new Rectangle(218 + extra, 9, Math.Max(40, w - 218 - extra - faderWidth - 40), laneHeight - 18);
            int faderTop = (laneHeight - 18) / 2;
            faders[i].Bounds = new Rectangle(w - faderWidth - 14, faderTop, faderWidth, 18);
            stemLoopLabels[i].Bounds = new Rectangle(faders[i].Left, faderTop - 16, faderWidth, 16);
            stemOffsetLabels[i].Bounds = new Rectangle(faders[i].Left, faders[i].Bottom, faderWidth, 16);
        }
        message.Bounds = new Rectangle(26, lanes[3].Bottom + 12, w - 54, 22);
        LayoutSavedMixes(w);
        LayoutPlaybackQueue(w);
        content.Invalidate(true);
    }
    private void StyleMix()
    {
        for (int i = 0; i < 4; i++)
        {
            mutes[i].Selected = mix.Muted[i]; solos[i].Selected = mix.Solo[i]; mutes[i].Invalidate(); solos[i].Invalidate();
            mutes[i].AccessibleDescription = mix.Muted[i] ? "Muted" : "Unmuted"; solos[i].AccessibleDescription = mix.Solo[i] ? "Solo enabled" : "Solo disabled";
            bool audible = mix.Audible(i) && !preferOriginal;
            waveforms[i].Audible = audible; waveforms[i].Invalidate();
            tip.SetToolTip(faders[i], TitleCase(StemMixer.StemNames[i]) + " volume " + faders[i].Value + "%");
        }
    }
    private void StyleModes()
    {
        mixMode.Selected = !preferOriginal && mixer != null && mixer.HasStems; originalMode.Selected = preferOriginal || (mixer != null && !mixer.HasStems); mixMode.Invalidate(); originalMode.Invalidate(); StyleMix();
    }
    private void ApplyMix() { if (playback != null) playback.Apply(volume.Value / 100f, mix); else if (mixer != null) mixer.ApplyMix(mix); StyleMix(); UpdateMessage(); SavePreferences(); }
    private void SetEnabled(bool loaded)
    {
        play.Enabled = loaded; seek.Enabled = loaded && !AutoTransitionActive; previous.Enabled = next.Enabled = loaded && !editingTuning;
        bool ready = loaded && mixer != null && mixer.HasStems;
        mixMode.Enabled = ready && !AutoTransitionActive; originalMode.Enabled = loaded && !AutoTransitionActive;
        for (int i = 0; i < 4; i++) { mutes[i].Enabled = solos[i].Enabled = faders[i].Enabled = ready; waveforms[i].Enabled = ready && !editingTuning && (!AutoTransitionActive || AutoEditorEnabled && AutoMarkersReady); }
        UpdateSourceButtons();
    }
    private void ChangeMode(bool original)
    {
        if (mixer == null || AutoTransitionActive || !original && !mixer.HasStems) return;
        preferOriginal = original; mixer.OriginalMode = original; StyleModes(); UpdateMessage(); SavePreferences();
    }
    private void SelectMix() { if (mixer != null && mixer.HasStems) { mixer.OriginalMode = false; preferOriginal = false; StyleModes(); } }
    private void UpdateMessage()
    {
        if (mixer == null) return;
        message.ForeColor = PlayerTheme.Muted;
        message.Text = !mixer.HasStems ? "Original MP3 available. Stem controls unlock when separation finishes." : mixer.OriginalMode ? "Playing the original MP3." : "Q W E R switch stems · A S D F mute · Drag a library song onto a stem";
    }
    private Image Thumbnail(SongInfo song)
    {
        Image image;
        if (thumbnails.TryGetValue(song.Cover, out image)) return image;
        try { if (File.Exists(song.Cover)) using (Image source = Image.FromFile(song.Cover)) { image = SquareCover(source, 256); thumbnails[song.Cover] = image; return image; } } catch { }
        return null;
    }
    private void DrawSong(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        SongInfo song = (SongInfo)list.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
        using (Brush background = new SolidBrush(PlayerTheme.Sidebar)) e.Graphics.FillRectangle(background, e.Bounds);
        Rectangle row = new Rectangle(e.Bounds.X, e.Bounds.Y + 2, e.Bounds.Width - 2, e.Bounds.Height - 4);
        if (selected) using (System.Drawing.Drawing2D.GraphicsPath shape = PlayerTheme.Rounded(row, 11)) using (Brush fill = new SolidBrush(Color.FromArgb(36, 35, 46))) e.Graphics.FillPath(fill, shape);
        Image thumb = Thumbnail(song); Rectangle imageRect = new Rectangle(row.X + 10, row.Y + 7, 48, 48);
        if (thumb != null) e.Graphics.DrawImage(thumb, imageRect); else PlayerTheme.Icon(e.Graphics, "wave", imageRect, PlayerTheme.Purple);
        using (Font titleFont = new Font("Segoe UI", 9.5f)) TextRenderer.DrawText(e.Graphics, song.Title, titleFont, new Rectangle(row.X + 68, row.Y + 10, row.Width - 76, 23), PlayerTheme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using (Font subFont = new Font("Segoe UI", 8.5f)) TextRenderer.DrawText(e.Graphics, song.Artist, subFont, new Rectangle(row.X + 68, row.Y + 32, row.Width - 76, 20), PlayerTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
    private async Task RefreshLibrary()
    {
        if (refreshing || loading || editingArtists || editingTuning || openingSavedMix || draggingLibrarySong || IsDisposed) return; refreshing = true;
        try
        {
            var refreshed = await Task.Run(() =>
            {
                var scanned = PlayerLibrary.Scan(library);
                return new { Songs = scanned, Downloads = PlayerLibrary.CompletedDownloads(project, library, scanned) };
            });
            if (!IsDisposed)
            {
                songs = refreshed.Songs;
                QueueCompletedDownloads(refreshed.Downloads);
                RenderSidebarSongs(true); RenderBrowser(false); RenderPlaybackQueue();
                if (!AutoTransitionActive && current != null && mixer != null && !mixer.HasStems)
                    foreach (SongInfo updated in songs) if (updated.Audio == current.Audio && updated.HasStems)
                    {
                        double position = mixer.Seconds; bool playing = output != null && output.PlaybackState == PlaybackState.Playing;
                        double? target = sharedBpm; var shifts = new Dictionary<string, int>(keyShifts, StringComparer.OrdinalIgnoreCase);
                        bool tuned = target.HasValue || shifts.Values.Any(shift => shift != 0);
                        double nativePosition = position * originalTempo;
                        await LoadSong(updated, tuned ? false : playing, tuned ? nativePosition : position);
                        if (tuned) { await ApplyTuning(target, shifts); if (playing && output != null) output.Play(); }
                        break;
                    }
            }
        }
        catch (Exception error) { if (!IsDisposed) ShowError(error); }
        finally { refreshing = false; }
    }
    private void RenderSongs() { RenderSidebarSongs(false); }
    private static bool SameSidebarSong(SongInfo previous, SongInfo updated)
    {
        return previous.Audio == updated.Audio && previous.Title == updated.Title && previous.Artist == updated.Artist &&
            previous.Folder == updated.Folder && previous.Cover == updated.Cover && previous.HasStems == updated.HasStems &&
            previous.YouTubeId == updated.YouTubeId && previous.AddedAt == updated.AddedAt && previous.Bpm == updated.Bpm &&
            previous.Key == updated.Key && previous.TuningHz == updated.TuningHz && previous.SavedMix == updated.SavedMix;
    }
    private void RenderSidebarSongs(bool preserveScroll)
    {
        string query = search.Text.Trim();
        var matching = songs.Where(song => query.Length == 0 || song.ToString().IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        int selection = -1; string wanted = current != null ? current.Audio : preferredSong;
        for (int i = 0; i < matching.Count; i++) if (matching[i].Audio == wanted) selection = i;
        if (selection < 0 && current == null && matching.Count > 0) selection = 0;
        // Scans create new SongInfo objects even when nothing changed. Leave the
        // displayed rows untouched in that case so polling cannot flash them.
        if (preserveScroll && current != null && list.SelectedIndex == selection && list.Items.Count == matching.Count &&
            Enumerable.Range(0, matching.Count).All(i => SameSidebarSong((SongInfo)list.Items[i], matching[i]))) return;
        int topIndex = list.TopIndex;
        string topAudio = topIndex >= 0 && topIndex < list.Items.Count ? ((SongInfo)list.Items[topIndex]).Audio : null;
        rendering = true; list.BeginUpdate(); list.Items.Clear();
        foreach (SongInfo song in matching) list.Items.Add(song);
        list.SelectedIndex = selection;
        // Keep the visible song and its partial-row offset during refreshes,
        // including any wheel movement still easing toward its destination.
        if (preserveScroll && list.Items.Count > 0)
        {
            int anchor = -1;
            for (int i = 0; i < list.Items.Count; i++) if (((SongInfo)list.Items[i]).Audio == topAudio) { anchor = i; break; }
            list.ShiftScroll(((anchor >= 0 ? anchor : Math.Min(Math.Max(0, topIndex), list.Items.Count - 1)) - topIndex) * (double)list.ItemHeight);
        }
        else if (selection < 0) list.TopIndex = 0;
        list.EndUpdate(); rendering = false;
        if (!preserveScroll) list.EnsureSelectionVisible();
        countLabel.Text = list.Items.Count + (list.Items.Count == 1 ? " song" : " songs");
        if (current == null && selection >= 0) { var pending = LoadSong((SongInfo)list.Items[selection], false); }
        else if (songs.Count == 0) message.Text = "Your downloaded songs will appear here. Use + to open the link drop box.";
    }
    private async Task LoadSong(SongInfo song, bool autoplay, double startAt = 0, bool preserveTuning = false)
    {
        if (editingTuning || openingSavedMix) return;
        if (song.SavedMix != null) { if (await OpenSavedMixSession(song.SavedMix, song, true) && autoplay && output != null) output.Play(); return; }
        if (autoPlaylistId != null && !AutoPlaybackSongs().Any(candidate => candidate.PlaybackKey == song.PlaybackKey)) EndQueueSession();
        loadedMixId = null; UpdateSaveMixButton();
        StopAutoTimeline();
        if (!preserveTuning) { ResetTuning(); loadedTuningHz = tuningReferenceHz; }
        int ticket = ++generation; loading = true;
        if (waveformCancellation != null) waveformCancellation.Cancel();
        waveformCancellation = new CancellationTokenSource(); CancellationToken cancellation = waveformCancellation.Token;
        CloseAudio(); current = song; preferredSong = song.Audio;
        ResetSourceLabels(song);
        SetEnabled(false); songTitle.Text = song.Title; songTitle.ForeColor = SongColor(song); artistLabel.Text = song.Artist; message.ForeColor = PlayerTheme.Muted; message.Text = "Loading music…";
        tip.SetToolTip(songTitle, song.Title); tip.SetToolTip(artistLabel, song.Artist);
        for (int i = 0; i < 4; i++) { waveforms[i].ResetGesture(); waveforms[i].ShowTiming(new StemTiming()); waveforms[i].Peaks = null; waveforms[i].Progress = 0; waveforms[i].CursorProgress = -1; UpdateStemTimingLabels(i, new StemTiming(), 0); }
        if (cover.Image != null) { cover.Image.Dispose(); cover.Image = null; }
        try { if (File.Exists(song.Cover)) using (Image image = Image.FromFile(song.Cover)) cover.Image = SquareCover(image, 512); } catch { }
        UpdateBrowserSong();
        try
        {
            float loudness = await PlayerLoudness.Gain(project, song.Audio, cancellation);
            StemMixer loaded = await Task.Run(() => new StemMixer(preserveTuning && originalPlaybackAudio != null ? originalPlaybackAudio : song.Audio, song.HasStems ? song.Folder : "", preserveTuning ? originalPlaybackFiles : null, preserveTuning ? originalPlaybackTransform : null, preserveTuning ? originalPlaybackTransforms : null));
            if (IsDisposed || ticket != generation) { loaded.Dispose(); return; }
            if (!preserveTuning && Math.Abs(PlaybackPitch(song, 0) - 1) > .0000001)
            {
                int rate = loaded.WaveFormat.SampleRate; long frames = loaded.TotalFrames;
                var plan = new StemMatchPlan { Tempo = 1, Pitch = PlaybackPitch(song, 0) };
                originalPlaybackAudio = song.Audio; originalPlaybackTransform = new PlaybackTransform(plan, rate, frames);
                originalPlaybackFiles = song.HasStems ? StemMixer.StemNames.Select(stem => Path.Combine(song.Folder, stem + ".wav")).ToArray() : null;
                originalPlaybackTransforms = song.HasStems ? Enumerable.Range(0, 4).Select(lane => new PlaybackTransform(plan, rate, frames, lane)).ToArray() : null;
                loaded.Dispose();
                string[] tunedFiles = originalPlaybackFiles; PlaybackTransform tunedAudio = originalPlaybackTransform; PlaybackTransform[] tunedStems = originalPlaybackTransforms;
                loaded = await Task.Run(() => new StemMixer(song.Audio, song.HasStems ? song.Folder : "", tunedFiles, tunedAudio, tunedStems));
                if (IsDisposed || ticket != generation) { loaded.Dispose(); return; }
            }
            mixer = loaded; mixer.SetLoudness(loudness); mixer.Volume = volume.Value / 100f; mixer.ApplyMix(mix); mixer.OriginalMode = preferOriginal || !mixer.HasStems;
            stemSources.OriginalTuning = originalPlaybackTransforms;
            if (!preserveTuning) { nativeFrames = loaded.TotalFrames; nativeRate = loaded.WaveFormat.SampleRate; }
            mixer.Seek(startAt);
            for (int i = 0; i < 4; i++) waveforms[i].Duration = mixer.Duration;
            CreateOutput(); StyleModes(); UpdateMessage(); SetEnabled(true);
            FollowSessionSong(song);
            if (autoplay) output.Play(); UpdateTransport(); SavePreferences();
            waveformTask = mixer.HasStems ? LoadWaveforms(stemSources, ticket, cancellation) : null;
            currentAnalysisTask = AnalyzeCurrent(song, ticket, cancellation);
        }
        catch (Exception error) { if (!IsDisposed && ticket == generation) { CloseAudio(); ShowError(error); } }
        finally { if (ticket == generation) { loading = false; UpdateSourceButtons(); } }
    }
    private async Task LoadWaveforms(StemSourceState sourceState, int ticket, CancellationToken cancellation)
    {
        try
        {
            for (int i = 0; i < 4; i++)
            {
                string file = sourceState.Original(i);
                float[] peaks = await Task.Run(() => PlayerWaveforms.Read(file, cancellation));
                sourceState.NativePeaks[i] = peaks;
                PlaybackTransform transform = sourceState.OriginalTuning == null ? null : sourceState.OriginalTuning[i];
                if (transform != null) using (var reader = new WaveFileReader(file)) peaks = transform.Peaks(peaks, reader.TotalTime.TotalSeconds);
                if (IsDisposed || ticket != generation || cancellation.IsCancellationRequested) return;
                stemSources.OriginalPeaks[i] = peaks;
                if (playingStemFiles[i] != file) continue;
                waveforms[i].Peaks = peaks; waveforms[i].Invalidate();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed && ticket == generation) { message.Text = "Waveform unavailable: " + error.Message; tip.SetToolTip(message, error.ToString()); } }
    }
    private void CreateOutput()
    {
        WaveOutEvent device = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 }; output = device;
        device.PlaybackStopped += delegate(object sender, StoppedEventArgs args)
        {
            if (IsDisposed || output != device) return;
            if (args.Exception != null) ShowError(args.Exception);
            else if (playback != null && playback.Current.PositionFrames >= playback.Current.TotalFrames) queueAdvancePending = true;
            UpdateTransport();
        };
        if (playback == null || playback.Current != mixer) { if (playback != null) { playback.CancelPending(); playback.ReleaseRetired(); } playback = new PlayerPlayback(mixer); }
        device.Init(new SampleToWaveProvider16(playback));
    }
    private void CloseAudio() { WaveOutEvent old = output; output = null; if (old != null) { old.Stop(); old.Dispose(); } if (playback != null) { playback.Dispose(); playback = null; } else if (mixer != null) mixer.Dispose(); mixer = null; }
    private void TogglePlay()
    {
        if (output == null || loading) return;
        try { if (output.PlaybackState == PlaybackState.Playing) output.Pause(); else { if (mixer.PositionFrames >= mixer.TotalFrames) { mixer.RestoreStemTiming(new[] { new StemTiming(), new StemTiming(), new StemTiming(), new StemTiming() }); SeekTo(0); } output.Play(); } UpdateTransport(); } catch (Exception error) { ShowError(error); }
    }
    private void SeekTo(double seconds)
    {
        if (output == null || mixer == null || Double.IsNaN(seconds) || Double.IsInfinity(seconds)) return;
        if (AutoTransitionActive) return;
        queueAdvancePending = false;
        try
        {
            StemMixer requestedMixer = mixer;
            bool playing = output.PlaybackState == PlaybackState.Playing; WaveOutEvent old = output; output = null; old.Stop(); old.Dispose();
            // Stop the callback before seeking; it may have entered the bridge
            // since the initial check. Do not seek a new song using an old time.
            PollAutoTransition();
            if (mixer == requestedMixer && !AutoTransitionActive && !SeekPreparedAutoTransition(seconds))
            {
                // Keep an in-flight Auto play load too. Its queue identity and
                // cancellation ticket are independent of the playback cursor.
                if (!autoPlayEnabled || autoTransition != null) CancelAutoPreparation();
                mixer.Seek(seconds);
                UpdateAutoReplayAfterSeek();
            }
            CreateOutput(); if (playing) output.Play(); UpdateTransport();
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task MoveSong(int direction)
    {
        if (loading || advancingQueue || editingTuning || editingArtists) return;
        PollAutoTransition();
        FollowSessionSong(current);
        if (direction > 0 && autoPlayEnabled) { await RequestAutoNext(); return; }
        PlaybackQueueEntry entry = direction < 0 ? songQueue.Previous : songQueue.Next;
        if (entry != null) { await PlayQueuedSong(entry); return; }
        if (direction < 0 && AutoTransitionActive) { await LoadSong(current, true); RenderSongs(); return; }
        if (songs.Count == 0) return;
        int index = songs.FindIndex(song => current != null && String.Equals(song.Audio, current.Audio, StringComparison.OrdinalIgnoreCase));
        SongInfo selected = songs[(index + direction + songs.Count) % songs.Count];
        if (direction < 0 && songQueue.CurrentIndex == 0)
        {
            entry = songQueue.Items.FirstOrDefault(item => String.Equals(item.Audio, selected.Audio, StringComparison.OrdinalIgnoreCase));
            if (entry != null) { await PlayQueuedSong(entry); return; }
            entry = songQueue.Add(selected.Audio, 0, true); await PlayQueuedSong(entry); return;
        }
        FollowSessionSong(current);
        selected = Enumerable.Range(1, songs.Count).Select(step => songs[(index + step + songs.Count) % songs.Count]).FirstOrDefault(song => File.Exists(song.Audio) && songQueue.CanAdd(song.Audio));
        if (selected == null) { message.Text = "Session complete · no unplayed songs remain."; return; }
        entry = songQueue.Add(selected.Audio, songQueue.Items.Count, true); await PlayQueuedSong(entry);
    }
    private static string Clock(double seconds) { int total = Math.Max(0, (int)seconds); return (total / 60) + ":" + (total % 60).ToString("00"); }
    private void UpdateTransport()
    {
        if (playback != null) playback.ReleaseRetired();
        bool playing = output != null && output.PlaybackState == PlaybackState.Playing;
        double position = mixer == null ? 0 : Math.Max(0, mixer.Seconds - (playing ? 0.1 : 0)); double length = mixer == null ? 0 : mixer.Duration;
        if (!seek.Dragging) seek.Value = length <= 0 ? 0 : (int)(position / length * 10000);
        elapsed.Text = Clock(position); duration.Text = Clock(length); play.Glyph = playing ? "pause" : "play"; play.AccessibleName = playing ? "Pause" : "Play"; play.Invalidate();
        for (int i = 0; i < 4; i++) UpdateStemWaveform(i, position, length);
        UpdateAutoMarkers();
        UpdateStemLoadingButtons();
        UpdateResetStemsButton();
        UpdateSaveMixButton();
        UpdateBrowserTransport(playing, position, length);
    }
    private void OpenDropBox() { try { string launcher = Path.Combine(project, "DJStem.exe"); if (File.Exists(launcher)) Process.Start(new ProcessStartInfo(launcher, "--downloader --library " + PlayerMatching.Quote(library)) { UseShellExecute = false, WorkingDirectory = project }); else Process.Start("wscript.exe", "\"" + Path.Combine(project, "Start-StemKit.vbs") + "\""); } catch (Exception error) { ShowError(error); } }
    private void ShowError(Exception error) { message.ForeColor = PlayerTheme.Stems[1]; message.Text = error.Message; tip.SetToolTip(message, error.ToString()); }
    private void ReadPreferences()
    {
        readingPreferences = true;
        try
        {
            var state = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(File.ReadAllText(preferenceFile)); object value;
            tip.Active = state.TryGetValue("hoverInfoEnabled", out value) && Convert.ToBoolean(value);
            if (state.TryGetValue("savedMixes", out value)) ReadSavedMixes(value);
            if (state.TryGetValue("playlists", out value)) ReadPlaylists(value);
            if (state.TryGetValue("volume", out value)) volume.Value = Math.Max(0, Math.Min(100, Convert.ToInt32(value)));
            if (state.TryGetValue("audio", out value)) preferredSong = Convert.ToString(value);
            if (state.TryGetValue("original", out value)) preferOriginal = Convert.ToBoolean(value);
            if (state.TryGetValue("stems", out value)) { object[] flags = value as object[]; if (flags != null && flags.Length == 4) for (int i = 0; i < 4; i++) mix.Muted[i] = !Convert.ToBoolean(flags[i]); }
            if (state.TryGetValue("muted", out value)) { object[] flags = value as object[]; if (flags != null && flags.Length == 4) for (int i = 0; i < 4; i++) mix.Muted[i] = Convert.ToBoolean(flags[i]); }
            if (state.TryGetValue("solo", out value)) { object[] flags = value as object[]; if (flags != null && flags.Length == 4) for (int i = 0; i < 4; i++) mix.Solo[i] = !mix.Muted[i] && Convert.ToBoolean(flags[i]); }
            if (state.TryGetValue("levels", out value)) { object[] levels = value as object[]; if (levels != null && levels.Length == 4) for (int i = 0; i < 4; i++) mix.Levels[i] = Math.Max(0, Math.Min(1, Convert.ToSingle(levels[i]))); }
            for (int i = 0; i < 4; i++) faders[i].Value = (int)(mix.Levels[i] * 100);
            if (state.TryGetValue("preset", out value)) mix.Preset = Convert.ToString(value); else if (Array.Exists(mix.Muted, flag => flag)) mix.Preset = "custom";
            if (state.TryGetValue("autoPlay", out value)) autoPlayEnabled = Convert.ToBoolean(value);
            if (state.TryGetValue("autoPlaySettings", out value)) autoSettings = AutoPlaySettings.Read(value as Dictionary<string, object>);
            if (state.TryGetValue("tuningReferenceHz", out value) && value != null) { double hz = Convert.ToDouble(value); if (TuningReference.ValidTarget(hz)) tuningReferenceHz = hz; }
            autoPlayButton.Selected = browserAutoPlayButton.Selected = autoPlayEnabled;
            RenderPlaybackQueue();
        }
        catch { }
        finally { readingPreferences = false; UpdateStemGestureMode(); }
    }
    private void SavePreferences()
    {
        if (readingPreferences) return;
        try { WritePreferences(); }
        catch { }
    }
    private void WritePreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(preferenceFile));
        string serialized = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Serialize(new { volume = volume.Value, audio = preferredSong, original = preferOriginal, muted = mix.Muted, solo = mix.Solo, levels = mix.Levels, preset = mix.Preset, hoverInfoEnabled = tip.Active, autoPlay = autoPlayEnabled, autoPlaySettings = autoSettings.Saved(), tuningReferenceHz = tuningReferenceHz, savedMixes = savedMixes, playlists = songPlaylists });
        string temporary = preferenceFile + ".pending";
        try { File.WriteAllText(temporary, serialized); if (File.Exists(preferenceFile)) File.Replace(temporary, preferenceFile, null); else File.Move(temporary, preferenceFile); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); bool fresh;
        using (Mutex mutex = new Mutex(true, "Local\\StemKitMusicPlayer", out fresh))
        {
            if (!fresh) { foreach (Process process in Process.GetProcessesByName("StemKitPlayer")) if (process.Id != Process.GetCurrentProcess().Id && process.MainWindowHandle != IntPtr.Zero) { RestoreWindow(process.MainWindowHandle, 9); BringWindow(process.MainWindowHandle); } return 0; }
            try { string project = Directory.GetParent(Path.GetDirectoryName(Application.ExecutablePath)).FullName; string root = args.Length > 0 ? args[0] : PlayerLibrary.ConfiguredRoot(project); Application.Run(new PlayerForm(root, project)); return 0; }
            catch (Exception error) { MessageBox.Show(error.Message, "StemKit Player", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool RestoreWindow(IntPtr window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool BringWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
