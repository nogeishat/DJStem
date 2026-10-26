using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class SongPlaylist
{
    public string Id, Name;
    public List<string> Items = new List<string>();
    internal bool Valid() { return !String.IsNullOrWhiteSpace(Id) && !String.IsNullOrWhiteSpace(Name) && Items != null && Items.All(item => !String.IsNullOrWhiteSpace(item)); }
}

internal sealed partial class PlayerForm
{
    private readonly List<SongPlaylist> songPlaylists = new List<SongPlaylist>();
    private readonly GlassButton createPlaylistButton = new GlassButton(), playlistAutoPlayButton = new GlassButton();
    private string playlistFolderId, autoPlaylistId;
    private bool startingPlaylistAuto;
    private const string MixPlaylistPrefix = "mix:";
    private const string AllMixesPlaylistId = "builtin:mixes";
    private SongPlaylist ViewedPlaylist()
    {
        return myMixesView ? new SongPlaylist { Id = AllMixesPlaylistId, Name = "My mixes", Items = savedMixes.Select(item => MixPlaylistPrefix + item.Id).ToList() } : songPlaylists.FirstOrDefault(item => item.Id == playlistFolderId);
    }

    private void InitializePlaylists()
    {
        SetupButton(createPlaylistButton, "New playlist", browser, "plus");
        createPlaylistButton.Click += delegate { CreatePlaylistInteractive(); };
        SetupButton(playlistAutoPlayButton, "Auto play", browser, null); playlistAutoPlayButton.Toggle = true; playlistAutoPlayButton.SelectedFill = PlayerTheme.Green;
        playlistAutoPlayButton.Click += async delegate
        {
            SongPlaylist selected = ViewedPlaylist();
            if (selected == null) return;
            if (autoPlayEnabled && autoPlaylistId == selected.Id) { SetAutoPlay(false); RenderBrowser(true); }
            else await StartPlaylistAutoPlay(selected);
        };
        createPlaylistButton.Visible = playlistAutoPlayButton.Visible = false;
    }
    private SongPlaylist StorePlaylist(string name)
    {
        if (String.IsNullOrWhiteSpace(name)) return null;
        var playlist = new SongPlaylist { Id = Guid.NewGuid().ToString("N"), Name = name.Trim() };
        songPlaylists.Add(playlist);
        try { WritePreferences(); RenderBrowser(true); return playlist; }
        catch (Exception error) { songPlaylists.Remove(playlist); ShowError(error); SavedMixStatus(error.Message, true); return null; }
    }
    private void CreatePlaylistInteractive()
    {
        string name = MixNameDialog("New playlist", ""); if (name == null) return;
        SongPlaylist playlist = StorePlaylist(name);
        if (playlist != null) { playlistsView = true; myMixesView = false; playlistFolderId = playlist.Id; librarySearch.Text = ""; RenderBrowser(true); }
    }
    private void ReadPlaylists(object value)
    {
        object[] entries = value as object[]; if (entries == null) return;
        var json = new JavaScriptSerializer();
        foreach (object entry in entries)
        {
            try
            {
                SongPlaylist playlist = json.ConvertToType<SongPlaylist>(entry);
                if (playlist == null || !playlist.Valid() || songPlaylists.Any(item => item.Id == playlist.Id)) continue;
                playlist.Items = playlist.Items.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); songPlaylists.Add(playlist);
            }
            catch { }
        }
    }
    private string CurrentPlaylistItem()
    {
        return loadedMixId != null && savedMixes.Any(item => item.Id == loadedMixId) ? MixPlaylistPrefix + loadedMixId : current == null ? null : current.Audio;
    }
    private bool AddPlaylistItem(SongPlaylist playlist, string item)
    {
        if (playlist == null || String.IsNullOrEmpty(item)) return false;
        if (playlist.Items.Contains(item, StringComparer.OrdinalIgnoreCase)) { message.Text = "Already in “" + playlist.Name + "”."; return false; }
        playlist.Items.Add(item);
        try
        {
            WritePreferences();
            SongInfo added = PlaylistSong(item); if (autoPlayEnabled && autoPlaylistId == playlist.Id && added != null) AddQueuedSong(added, songQueue.Items.Count);
            RenderBrowser(true); message.Text = "Added to “" + playlist.Name + "”."; message.ForeColor = PlayerTheme.Green; return true;
        }
        catch (Exception error) { playlist.Items.Remove(item); ShowError(error); return false; }
    }
    private void DeferPlaylistAction(Action action) { QueueArtistAction(delegate { action(); return Task.FromResult(0); }); }
    private void ChoosePlaylist()
    {
        string item = CurrentPlaylistItem(); if (item == null) return;
        var menu = new ContextMenuStrip { BackColor = PlayerTheme.Glass, ForeColor = PlayerTheme.Text, ShowImageMargin = false };
        foreach (SongPlaylist playlist in songPlaylists)
        {
            SongPlaylist selected = playlist;
            menu.Items.Add(selected.Name, null, delegate { DeferPlaylistAction(() => AddPlaylistItem(selected, item)); });
        }
        if (songPlaylists.Count != 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("New playlist…", null, delegate { DeferPlaylistAction(delegate
        {
            string name = MixNameDialog("New playlist", ""); if (name == null) return;
            SongPlaylist playlist = StorePlaylist(name); if (playlist != null) AddPlaylistItem(playlist, item);
        }); });
        menu.Closed += delegate { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(menu.Dispose)); else menu.Dispose(); };
        menu.Show(nowPlayingAddButton, new Point(nowPlayingAddButton.Width, 0), ToolStripDropDownDirection.AboveLeft);
    }
    private SongInfo PlaylistSong(string key)
    {
        if (String.IsNullOrEmpty(key)) return null;
        if (!key.StartsWith(MixPlaylistPrefix, StringComparison.OrdinalIgnoreCase)) return songs.FirstOrDefault(song => String.Equals(song.Audio, key, StringComparison.OrdinalIgnoreCase));
        SavedMix saved = savedMixes.FirstOrDefault(item => String.Equals(MixPlaylistPrefix + item.Id, key, StringComparison.OrdinalIgnoreCase));
        SongInfo original = saved == null ? null : songs.FirstOrDefault(song => String.Equals(song.Audio, saved.OriginalAudio, StringComparison.OrdinalIgnoreCase));
        if (original == null) return null;
        return new SongInfo { Audio = original.Audio, Folder = original.Folder, Cover = original.Cover, Title = saved.Name, Artist = saved.OriginalArtist, Bpm = saved.Bpm ?? original.Bpm, Key = original.Key, TuningHz = original.TuningHz, HasStems = original.HasStems, SavedMix = saved };
    }
    private IEnumerable<SongInfo> AutoPlaybackSongs()
    {
        if (autoPlaylistId == AllMixesPlaylistId) return savedMixes.Select(item => PlaylistSong(MixPlaylistPrefix + item.Id)).Where(song => song != null);
        SongPlaylist playlist = songPlaylists.FirstOrDefault(item => item.Id == autoPlaylistId);
        return autoPlaylistId == null ? songs : playlist == null ? Enumerable.Empty<SongInfo>() : playlist.Items.Select(PlaylistSong).Where(song => song != null);
    }
    private bool PlaylistEntryAvailable(SongInfo song)
    {
        if (song == null || !File.Exists(song.Audio)) return false;
        if (song.SavedMix == null) return true;
        try { SavedMixSong(song.SavedMix.OriginalAudio); foreach (SavedStemBank bank in song.SavedMix.Banks) SavedMixSong(bank.Audio); return true; }
        catch { return false; }
    }
    private async Task<bool> StartPlaylistAutoPlay(SongPlaylist playlist)
    {
        if (playlist == null || startingPlaylistAuto || loading || openingSavedMix || editingTuning || editingArtists || AutoTransitionActive) return false;
        var members = playlist.Items.Select(PlaylistSong).Where(PlaylistEntryAvailable).ToList();
        if (members.Count == 0) { SavedMixStatus("This playlist has no available songs or mixes. Add them using + in Now playing.", true); return false; }
        for (int index = members.Count - 1; index > 0; index--) { int other = normalPlaybackRandom.Next(index + 1); SongInfo swap = members[index]; members[index] = members[other]; members[other] = swap; }
        startingPlaylistAuto = true;
        try
        {
            SetAutoPlay(false); StopAutoTimeline(); EndQueueSession();
            SongInfo first = members[0];
            if (first.SavedMix != null) { if (!await OpenSavedMixSession(first.SavedMix, first, false)) return false; }
            else await LoadSong(first, false);
            if (mixer == null || current.PlaybackKey != first.PlaybackKey) return false;
            autoPlaylistId = playlist.Id; SetAutoPlay(true); FollowSessionSong(current);
            foreach (SongInfo song in members.Skip(1)) songQueue.Add(song.PlaybackKey, songQueue.Items.Count, true);
            RenderPlaybackQueue(); ShowLibrary(false); message.Text = "Auto play · “" + playlist.Name + "” · shuffled songs and mixes"; SavePreferences();
            return true;
        }
        catch (Exception error) { ShowError(error); return false; }
        finally { startingPlaylistAuto = false; if (!autoPlayEnabled) autoPlaylistId = null; if (!IsDisposed) { RenderBrowser(true); UpdateTransport(); } }
    }
    private void RenamePlaylist(SongPlaylist playlist)
    {
        string name = MixNameDialog("Rename playlist", playlist.Name); if (name == null) return;
        string old = playlist.Name; playlist.Name = name;
        try { WritePreferences(); RenderBrowser(true); } catch (Exception error) { playlist.Name = old; SavedMixStatus(error.Message, true); }
    }
    private void DeletePlaylist(SongPlaylist playlist)
    {
        int index = songPlaylists.IndexOf(playlist); if (index < 0) return;
        songPlaylists.RemoveAt(index);
        try { WritePreferences(); if (autoPlaylistId == playlist.Id) SetAutoPlay(false); if (playlistFolderId == playlist.Id) playlistFolderId = null; RenderBrowser(true); }
        catch (Exception error) { songPlaylists.Insert(index, playlist); SavedMixStatus(error.Message, true); }
    }
    private void RemovePlaylistItem(SongPlaylist playlist, string key)
    {
        int index = playlist.Items.FindIndex(item => String.Equals(item, key, StringComparison.OrdinalIgnoreCase)); if (index < 0) return;
        playlist.Items.RemoveAt(index);
        try
        {
            WritePreferences();
            if (autoPlaylistId == playlist.Id) foreach (PlaybackQueueEntry entry in songQueue.Items.Where(item => item.Audio == key && !QueueEntryLocked(item.Id)).ToArray()) RemoveQueuedSong(entry.Id);
            RenderBrowser(true);
        }
        catch (Exception error) { playlist.Items.Insert(index, key); SavedMixStatus(error.Message, true); }
    }
    private void RemapPlaylists(object[] mappings)
    {
        foreach (SongPlaylist playlist in songPlaylists) playlist.Items = playlist.Items.Select(item => item.StartsWith(MixPlaylistPrefix, StringComparison.OrdinalIgnoreCase) ? item : RemappedPath(item, mappings)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    private void LayoutPlaylistButtons(int width)
    {
        createPlaylistButton.Visible = playlistsView && !myMixesView;
        playlistAutoPlayButton.Visible = playlistsView && (myMixesView || playlistFolderId != null);
        createPlaylistButton.Bounds = new Rectangle(width - 304, 64, 138, 34);
        playlistAutoPlayButton.Bounds = new Rectangle(width - (myMixesView ? 304 : 442), 64, 128, 34);
        playlistAutoPlayButton.Selected = autoPlayEnabled && autoPlaylistId == (myMixesView ? AllMixesPlaylistId : playlistFolderId);
        playlistAutoPlayButton.Text = playlistAutoPlayButton.Selected ? "Stop Auto play" : "Auto play"; playlistAutoPlayButton.Invalidate();
    }
    private void RenderCustomPlaylists(string query)
    {
        SongPlaylist selected = songPlaylists.FirstOrDefault(item => item.Id == playlistFolderId);
        if (selected == null)
        {
            playlistFolderId = null;
            foreach (SongPlaylist playlist in songPlaylists.Where(item => item.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0))
            {
                SongPlaylist choice = playlist;
                var card = new LibraryCard { Folder = true, Title = playlist.Name, Subtitle = playlist.Items.Count + (playlist.Items.Count == 1 ? " item" : " items"), AccessibleName = "Open playlist: " + playlist.Name, Size = new Size(200, 258) };
                var menu = new ContextMenuStrip(); menu.Items.Add("Auto play", null, delegate { QueueArtistAction(() => StartPlaylistAutoPlay(choice)); }); menu.Items.Add("Rename", null, delegate { DeferPlaylistAction(() => RenamePlaylist(choice)); }); menu.Items.Add("Delete playlist", null, delegate { DeferPlaylistAction(() => DeletePlaylist(choice)); });
                card.ContextMenuStrip = menu; card.Disposed += delegate { menu.Dispose(); }; card.RenameFolder = delegate { RenamePlaylist(choice); };
                card.Click += delegate { playlistFolderId = choice.Id; myMixesView = false; librarySearch.Text = ""; RenderBrowser(true); }; libraryGrid.Controls.Add(card);
            }
            libraryCount.Text = (songPlaylists.Count + 1) + " playlists · " + savedMixes.Count + " saved mixes";
            return;
        }
        libraryHeading.Text = selected.Name; mixesBackButton.Visible = true;
        libraryCount.Text = selected.Items.Count + " items · Auto play shuffles this playlist with transitions";
        foreach (string key in selected.Items)
        {
            SongInfo song = PlaylistSong(key); string title = song == null ? "Unavailable song or mix" : song.Title;
            if ((title + " " + (song == null ? "" : song.Artist)).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
            var card = new LibraryCard { Tag = key, Title = title, Subtitle = song == null ? "Restore it to the library" : song.Artist, Details = song != null && song.SavedMix != null ? "Editable mix" : song == null ? "Unavailable" : song.MusicDetails, Artwork = song == null ? null : Thumbnail(song), AccessibleName = "Open " + title, ActionName = song != null && song.SavedMix != null ? "Open mix" : "Play song", Size = new Size(200, 284) };
            var menu = new ContextMenuStrip(); string captured = key; menu.Items.Add("Remove from playlist", null, delegate { DeferPlaylistAction(() => RemovePlaylistItem(selected, captured)); }); card.ContextMenuStrip = menu; card.Disposed += delegate { menu.Dispose(); };
            if (song != null && song.SavedMix == null)
            {
                menu.Items.Add("Add to queue", null, delegate { AddQueuedSong(song, songQueue.Items.Count); });
                menu.Items.Add("Delete song…", null, delegate { QueueArtistAction(() => DeleteSongInteractive(song)); });
            }
            card.Click += async delegate
            {
                if (song == null || loading || openingSavedMix) return;
                SetAutoPlay(false); EndQueueSession();
                if (song.SavedMix != null) await OpenSavedMixSession(song.SavedMix, song, false);
                else { ShowLibrary(false); await LoadSong(song, true); }
            }; libraryGrid.Controls.Add(card);
        }
        if (libraryGrid.Controls.Count == 0) libraryGrid.Controls.Add(new Label { Text = selected.Items.Count == 0 ? "Add songs or saved mixes using + in Now playing." : "No matching playlist items.", AutoSize = true, ForeColor = PlayerTheme.Muted, Margin = new Padding(8, 20, 0, 0) });
    }
}
