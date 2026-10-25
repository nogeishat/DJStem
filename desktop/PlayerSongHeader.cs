using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private readonly GlassPanel addedInfo = new GlassPanel(), addedArtwork = new GlassPanel();
    private readonly PictureBox addedCover = new PictureBox();
    private readonly Label addedTitle = new PlayerLabel(), addedArtist = new PlayerLabel(), addedTraits = new PlayerLabel();
    private SongInfo displayedAddedSong;
    private readonly Dictionary<string, Color> songColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

    private Color SongColor(SongInfo song, bool added = false)
    {
        if (song == null) return PlayerTheme.Purple;
        Color original = added && current != null ? SongColor(current) : Color.Empty;
        string cacheKey = song.Cover + (original.IsEmpty ? "" : "|against:" + original.ToArgb());
        Color color;
        if (songColors.TryGetValue(cacheKey, out color)) return color;
        Image image = Thumbnail(song);
        if (image == null) return PlayerTheme.Purple;
        color = PlayerSongColors.Select(image, original);
        songColors[cacheKey] = color;
        return color;
    }

    private void InitializeAddedSongHeader()
    {
        addedArtwork.Radius = artwork.Radius; addedArtwork.Fill = artwork.Fill;
        addedArtwork.Visible = addedInfo.Visible = false;
        content.Controls.Add(addedArtwork); content.Controls.Add(addedInfo);
        addedCover.BackColor = addedArtwork.Fill; addedCover.SizeMode = PictureBoxSizeMode.Zoom;
        addedCover.AccessibleName = "Added song cover"; addedCover.TabStop = false; addedArtwork.Controls.Add(addedCover);
        addedCover.Resize += delegate { Region old = addedCover.Region; using (System.Drawing.Drawing2D.GraphicsPath shape = PlayerTheme.Rounded(addedCover.ClientRectangle, 12)) addedCover.Region = new Region(shape); if (old != null) old.Dispose(); };
        ConfigureLabel(addedTitle, addedInfo, "", 18, PlayerTheme.Text, true); addedTitle.AutoEllipsis = true;
        ConfigureLabel(addedArtist, addedInfo, "", 10, PlayerTheme.Muted, false); addedArtist.AutoEllipsis = true;
        ConfigureLabel(addedTraits, addedInfo, "", 9, PlayerTheme.Muted, false); addedTraits.AutoEllipsis = true;
        artistLabel.AutoEllipsis = true;
        info.AccessibleName = "Original song information"; addedInfo.AccessibleName = "Added song information";
    }

    private void LayoutSongHeader(int width, int top)
    {
        bool paired = displayedAddedSong != null;
        artwork.Bounds = new Rectangle(24, top, 220, 220); cover.Bounds = new Rectangle(2, 2, 216, 216);
        int infoWidth = width - 236 - (paired ? 236 : 0);
        info.Bounds = new Rectangle(260, top, infoWidth, paired ? 104 : 220);
        // The same 220px header holds either one full card or two equal cards.
        songTitle.Bounds = new Rectangle(22, paired ? 10 : 20, infoWidth - 44, 34);
        artistLabel.Bounds = new Rectangle(22, paired ? 44 : 61, infoWidth - 44, 25);
        matchLabel.Bounds = new Rectangle(22, paired ? 73 : 105, infoWidth - 44, paired ? 22 : 28);
        addedInfo.Bounds = new Rectangle(260, top + 116, infoWidth, 104);
        addedTitle.Bounds = songTitle.Bounds; addedArtist.Bounds = artistLabel.Bounds; addedTraits.Bounds = matchLabel.Bounds;
        LayoutMusicDetails(currentBpm, currentKey, currentMusicSeparator, matchLabel.Bounds);
        LayoutMusicDetails(addedBpm, addedKey, addedMusicSeparator, addedTraits.Bounds);
        addedArtwork.Bounds = new Rectangle(24 + width - 220, top, 220, 220); addedCover.Bounds = new Rectangle(2, 2, 216, 216);
        addedArtwork.Visible = addedInfo.Visible = paired;
    }

    private static void LayoutMusicDetails(GlassButton bpm, GlassButton key, Label separator, Rectangle row)
    {
        int tempoWidth = TextRenderer.MeasureText(bpm.Text, bpm.Font, Size.Empty, TextFormatFlags.SingleLine).Width;
        bpm.Bounds = new Rectangle(row.Left, row.Top, tempoWidth, row.Height);
        separator.Bounds = new Rectangle(bpm.Right - 3, row.Top, 14, row.Height);
        key.Bounds = new Rectangle(separator.Right - 3, row.Top, Math.Max(1, row.Right - separator.Right + 3), row.Height);
    }

    private void ShowAddedSong(SongInfo song, MusicTraits traits = null, StemMatchPlan plan = null)
    {
        displayedAddedSong = song;
        // Thumbnails are owned by the shared cache and disposed when the player closes.
        addedCover.Image = song == null ? null : Thumbnail(song);
        addedTitle.Text = song == null ? "" : song.Title; addedArtist.Text = song == null ? "" : song.Artist;
        addedTitle.ForeColor = SongColor(song, true);
        addedTraits.Text = song == null ? "" : traits == null ? "Matching tempo…" : plan.MatchedBpm.ToString("0.00") + " BPM · " + traits.Key + " (estimated)";
        tip.SetToolTip(addedTitle, addedTitle.Text); tip.SetToolTip(addedArtist, addedArtist.Text);
        tip.SetToolTip(addedTraits, traits == null ? "" : "Source: " + traits.Bpm.ToString("0.00") + " BPM. Matched tempo: " + plan.MatchedBpm.ToString("0.00") + " BPM. Pitch and key are unchanged.");
        LayoutContent();
        UpdateProgressColors();
        UpdateTuningHeader();
    }
    private void UpdateProgressColors()
    {
        Color primary = SongColor(current), secondary = displayedAddedSong == null ? Color.Empty : SongColor(displayedAddedSong, true);
        seek.Accent = primary; seek.AccentEnd = secondary; seek.Gradient = !secondary.IsEmpty; seek.Invalidate();
        foreach (WaveformView waveform in waveforms)
        {
            if (waveform == null) continue;
            waveform.ProgressAccent = primary; waveform.ProgressEnd = secondary; waveform.Invalidate();
        }
    }
}
