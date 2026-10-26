using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.Wave;

internal sealed partial class PlayerForm
{
    private readonly List<SavedMix> savedMixes = new List<SavedMix>();
    private readonly GlassButton saveMixButton = new GlassButton(), saveMixCopyButton = new GlassButton();
    private readonly Label savedMixLabel = new PlayerLabel();
    private readonly GlassButton nowPlayingAddButton = new GlassButton(), addToPlaylistButton = new GlassButton();
    private readonly GlassPanel nowPlayingActions = new GlassPanel();
    private readonly ToolStripDropDown nowPlayingMenu = new ToolStripDropDown();
    private readonly GlassButton playlistsTab = new GlassButton(), mixesBackButton = new GlassButton();
    private string loadedMixId;
    private bool playlistsView, myMixesView, openingSavedMix;
    private CancellationTokenSource savedMixCancellation;

    private void InitializeSavedMixes()
    {
        SetupButton(nowPlayingAddButton, "", content, "plus"); nowPlayingAddButton.Radius = 18; nowPlayingAddButton.AccessibleName = "Song actions";
        nowPlayingAddButton.Click += delegate { ShowNowPlayingMenu(); }; tip.SetToolTip(nowPlayingAddButton, "Add to playlist, save mix, export MP3 or delete song");
        nowPlayingActions.Size = new Size(220, 146); nowPlayingActions.Fill = PlayerTheme.Glass; nowPlayingActions.Radius = 12;
        SetupButton(addToPlaylistButton, "Add to playlist", nowPlayingActions, null);
        addToPlaylistButton.Click += delegate { nowPlayingMenu.Close(); ChoosePlaylist(); };
        SetupButton(saveMixButton, "Save mix", nowPlayingActions, null); saveMixButton.AccessibleName = "Save editable mix";
        saveMixButton.Click += delegate { nowPlayingMenu.Close(); SaveMixInteractive(false); };
        SetupButton(saveMixCopyButton, "New mix", nowPlayingActions, null); saveMixCopyButton.Click += delegate { nowPlayingMenu.Close(); SaveMixInteractive(true); };
        SetupButton(exportMixButton, "Export MP3", nowPlayingActions, null); exportMixButton.AccessibleName = "Export current mix as MP3";
        exportMixButton.Click += async delegate { nowPlayingMenu.Close(); await ExportMixInteractive(); };
        InitializeSongDeletion();
        nowPlayingMenu.AutoSize = false; nowPlayingMenu.Padding = Padding.Empty; nowPlayingMenu.BackColor = PlayerTheme.Glass;
        nowPlayingMenu.Items.Add(new ToolStripControlHost(nowPlayingActions) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = nowPlayingActions.Size });
        ConfigureLabel(savedMixLabel, content, "", 10, PlayerTheme.Muted, false); savedMixLabel.AutoEllipsis = true;
        SetupButton(playlistsTab, "Playlists", browser, null);
        playlistsTab.Click += delegate { playlistsView = true; myMixesView = false; playlistFolderId = null; artistFolder = null; RenderBrowser(true); };
        SetupButton(mixesBackButton, "‹ Playlists", browser, null); mixesBackButton.Visible = false;
        mixesBackButton.Click += delegate { myMixesView = false; playlistFolderId = null; RenderBrowser(true); };
        InitializePlaylists();
        Disposed += delegate { if (savedMixCancellation != null) savedMixCancellation.Cancel(); if (exportCancellation != null) exportCancellation.Cancel(); nowPlayingMenu.Dispose(); };
        UpdateSaveMixButton();
    }

    private bool CanSaveMix()
    {
        return !IsDisposed && current != null && mixer != null && mixer.HasStems && stemSources != null && !loading && !preparingReplacement && !editingTuning && !editingArtists && !openingSavedMix && !AutoTransitionActive && !StemGestureActive;
    }
    private void UpdateSaveMixButton()
    {
        SavedMix loaded = savedMixes.FirstOrDefault(item => item.Id == loadedMixId);
        saveMixButton.Enabled = saveMixCopyButton.Enabled = CanSaveMix();
        nowPlayingAddButton.Enabled = !IsDisposed && current != null && mixer != null && !loading && !openingSavedMix;
        addToPlaylistButton.Enabled = nowPlayingAddButton.Enabled;
        exportMixButton.Enabled = exportCancellation != null || CanExportMix();
        exportMixButton.Text = exportCancellation == null ? "Export MP3" : "Cancel export (" + exportProgress + "%)";
        deleteSongButton.Enabled = current != null && CanDeleteSong();
        saveMixButton.Text = loaded == null ? "Save mix" : "Save changes";
        saveMixCopyButton.Visible = loaded != null;
        addToPlaylistButton.Bounds = new Rectangle(12, 12, 196, 34); saveMixButton.Bounds = new Rectangle(12, 54, 196, 34); saveMixCopyButton.Bounds = new Rectangle(12, 96, 196, 34);
        exportMixButton.Bounds = new Rectangle(12, loaded == null ? 96 : 138, 196, 34);
        deleteSongButton.Bounds = new Rectangle(12, exportMixButton.Bottom + 8, 196, 34);
        nowPlayingActions.Height = loaded == null ? 184 : 226;
        ToolStripControlHost host = (ToolStripControlHost)nowPlayingMenu.Items[0]; host.Size = nowPlayingActions.Size; nowPlayingMenu.Size = nowPlayingActions.Size;
        string mixName = loaded == null ? "" : loaded.Name;
        bool nameChanged = savedMixLabel.Text != mixName;
        savedMixLabel.Text = mixName; savedMixLabel.Visible = loaded != null;
        if (nameChanged) LayoutContent();
        tip.SetToolTip(saveMixButton, loaded == null ? "Save songs, stem sources, volumes, offsets and loops to Library → Playlists → My mixes." : "Update “" + loaded.Name + "” with your current edits.");
        tip.SetToolTip(savedMixLabel, savedMixLabel.Text);
        tip.SetToolTip(exportMixButton, exportCancellation != null ? "Cancel the current MP3 export." : "Export the full song with the current sources, mute/solo, volumes, offsets, loops and tempo/key edits.");
    }
    private void LayoutSavedMixes(int width)
    {
        nowPlayingAddButton.Bounds = new Rectangle(24 + width - 36, message.Top - 8, 36, 36);
        transitionEditorButton.Bounds = new Rectangle(nowPlayingAddButton.Left - 162, nowPlayingAddButton.Top + 3, 154, 30);
        message.Width = Math.Max(1, (autoPlayEnabled ? transitionEditorButton.Left : nowPlayingAddButton.Left) - message.Left - 12);
        savedMixLabel.Bounds = new Rectangle(24, 13, Math.Max(1, width), 25);
    }
    private void ShowNowPlayingMenu()
    {
        if (!nowPlayingAddButton.Enabled) return;
        if (nowPlayingMenu.Visible) { nowPlayingMenu.Close(); return; }
        UpdateSaveMixButton();
        nowPlayingMenu.Show(nowPlayingAddButton, new Point(nowPlayingAddButton.Width, 0), ToolStripDropDownDirection.AboveLeft);
    }
    private SavedMix CaptureMix(string name, string id)
    {
        if (!CanSaveMix()) throw new InvalidOperationException("Wait for the songs and stem edits to finish before saving this mix.");
        var banks = stemSources.Active.Concat(new[] { stemSources.Available }).Where(bank => bank != null).GroupBy(bank => bank.Song.Audio, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
        return new SavedMix
        {
            Id = id, Name = name, OriginalAudio = current.Audio, OriginalTitle = current.SavedMix == null ? current.Title : current.SavedMix.OriginalTitle, OriginalArtist = current.SavedMix == null ? current.Artist : current.SavedMix.OriginalArtist,
            AddedAudio = stemSources.Available == null ? null : stemSources.Available.Song.Audio,
            AddedTitle = stemSources.Available == null ? null : stemSources.Available.Song.Title,
            Sources = stemSources.Active.Select(bank => bank == null ? null : bank.Song.Audio).ToArray(),
            Banks = banks.Select(bank => new SavedStemBank { Audio = bank.Song.Audio, Tempo = bank.Plan.Tempo, Offset = bank.Plan.Offset, Pitch = bank.Plan.Pitch, MatchedBpm = bank.Plan.MatchedBpm }).ToList(),
            Muted = (bool[])mix.Muted.Clone(), Solo = (bool[])mix.Solo.Clone(), Levels = (float[])mix.Levels.Clone(),
            Timing = Enumerable.Range(0, 4).Select(lane => SavedStemTiming.From(mixer.StemState(lane))).ToArray(),
            Volume = volume.Value, OriginalMode = preferOriginal, OriginalTempo = originalTempo, Bpm = sharedBpm,
            KeyShifts = new Dictionary<string, int>(keyShifts, StringComparer.OrdinalIgnoreCase)
        };
    }
    private bool StoreMix(string name, bool copy)
    {
        if (String.IsNullOrWhiteSpace(name)) return false;
        SavedMix previous = copy ? null : savedMixes.FirstOrDefault(item => item.Id == loadedMixId);
        int index = previous == null ? savedMixes.Count : savedMixes.IndexOf(previous);
        try
        {
            SavedMix saved = CaptureMix(name.Trim(), previous == null ? Guid.NewGuid().ToString("N") : previous.Id);
            if (!saved.Valid()) throw new InvalidOperationException("The mix contains incomplete stem settings.");
            if (previous == null) savedMixes.Add(saved); else savedMixes[index] = saved;
            try { WritePreferences(); }
            catch { if (previous == null) savedMixes.RemoveAt(index); else savedMixes[index] = previous; throw; }
            loadedMixId = saved.Id; UpdateSaveMixButton(); RenderBrowser(true);
            message.ForeColor = PlayerTheme.Green; message.Text = "Saved “" + saved.Name + "” · Library → Playlists → My mixes";
            return true;
        }
        catch (Exception error) { ShowError(error); return false; }
    }
    private void SaveMixInteractive(bool copy)
    {
        if (!CanSaveMix()) return;
        SavedMix previous = savedMixes.FirstOrDefault(item => item.Id == loadedMixId);
        if (!copy && previous != null) { StoreMix(previous.Name, false); return; }
        string suggestion = previous == null ? current.Title + (displayedAddedSong == null ? " mix" : " + " + displayedAddedSong.Title) : previous.Name + " copy";
        string name = MixNameDialog("Save mix", suggestion);
        if (name != null) StoreMix(name, true);
    }
    private string MixNameDialog(string title, string value)
    {
        using (var dialog = new Form { Text = title, ClientSize = new Size(480, 166), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = PlayerTheme.Background, ForeColor = PlayerTheme.Text, Font = Font })
        using (var entry = new TextBox { Text = value, MaxLength = 120, Bounds = new Rectangle(20, 49, 440, 27), BackColor = PlayerTheme.Glass, ForeColor = PlayerTheme.Text, BorderStyle = BorderStyle.FixedSingle })
        {
            var label = new Label { Text = title.Contains("playlist") ? "Playlist name" : "Mix name", Bounds = new Rectangle(20, 19, 440, 25) };
            var save = new Button { Text = title == "New playlist" ? "Create" : "Save", DialogResult = DialogResult.OK, Bounds = new Rectangle(266, 111, 92, 30), Enabled = !String.IsNullOrWhiteSpace(value) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(368, 111, 92, 30) };
            entry.TextChanged += delegate { save.Enabled = !String.IsNullOrWhiteSpace(entry.Text); };
            dialog.Controls.AddRange(new Control[] { label, entry, save, cancel }); dialog.AcceptButton = save; dialog.CancelButton = cancel;
            dialog.Shown += delegate { entry.Focus(); entry.SelectAll(); };
            return dialog.ShowDialog(this) == DialogResult.OK ? entry.Text.Trim() : null;
        }
    }
    private void ReadSavedMixes(object value)
    {
        object[] entries = value as object[];
        if (entries == null) return;
        var json = new JavaScriptSerializer();
        foreach (object entry in entries)
        {
            try { SavedMix saved = json.ConvertToType<SavedMix>(entry); if (saved != null && saved.Valid() && !savedMixes.Any(item => item.Id == saved.Id)) savedMixes.Add(saved); }
            catch { } // One damaged record must not hide the rest of the playlist.
        }
    }
    private SongInfo SavedMixSong(string audio)
    {
        SongInfo song = songs.FirstOrDefault(item => String.Equals(item.Audio, audio, StringComparison.OrdinalIgnoreCase));
        if (song == null || !File.Exists(song.Audio)) throw new FileNotFoundException("A song in this mix is missing from the library: " + Path.GetFileName(audio));
        if (!song.HasStems || StemMixer.StemNames.Any(stem => !File.Exists(Path.Combine(song.Folder, stem + ".wav")))) throw new InvalidOperationException("The stems for “" + song.Title + "” are not available yet.");
        return song;
    }
    private void SavedMixStatus(string text, bool error = false)
    {
        message.Text = text; message.ForeColor = error ? PlayerTheme.Stems[1] : PlayerTheme.Muted;
        if (playlistsView) { libraryCount.Text = text; libraryCount.ForeColor = message.ForeColor; tip.SetToolTip(libraryCount, text); }
    }
    private Task<bool> OpenSavedMix(SavedMix saved) { return OpenSavedMixSession(saved, null, false); }
    private async Task<bool> OpenSavedMixSession(SavedMix saved, SongInfo sessionSong, bool preserveSession)
    {
        if (openingSavedMix || loading || refreshing || editingTuning || editingArtists || preparingReplacement || AutoTransitionActive || StemGestureActive) return false;
        StemMixer prepared = null;
        CancellationTokenSource cancellation = null;
        try
        {
            if (saved == null || !saved.Valid()) throw new InvalidOperationException("This saved mix has incomplete settings.");
            SongInfo original = SavedMixSong(saved.OriginalAudio);
            double? referenceHz = tuningReferenceHz;
            var donors = saved.Banks.ToDictionary(bank => bank.Audio, bank => SavedMixSong(bank.Audio), StringComparer.OrdinalIgnoreCase);
            openingSavedMix = true; main.Enabled = sidebar.Enabled = browser.Enabled = false; UpdateSaveMixButton();
            if (!preserveSession) SetAutoPlay(false); StopAutoTimeline();
            cancellation = new CancellationTokenSource(); savedMixCancellation = cancellation; CancellationToken token = cancellation.Token;
            SavedMixStatus("Opening “" + saved.Name + "”…");
            long sourceFrames; int rate;
            using (var source = new StemMixer(original.Audio, original.Folder)) { sourceFrames = source.TotalFrames; rate = source.WaveFormat.SampleRate; }
            long frames = Math.Max(1, (long)Math.Round(sourceFrames / saved.OriginalTempo));
            int shift; saved.KeyShifts.TryGetValue(original.Audio, out shift);
            var originalPlan = new StemMatchPlan { Tempo = saved.OriginalTempo, Pitch = TuningReference.Pitch(original.TuningHz, referenceHz, shift), MatchedBpm = saved.Bpm ?? 0 };
            bool changed = Math.Abs(saved.OriginalTempo - 1) > .0000001 || Math.Abs(originalPlan.Pitch - 1) > .0000001;
            string[] originalFiles = new string[4]; var sources = new StemSourceState(original.Folder, originalFiles);
            string originalAudio = original.Audio;
            PlaybackTransform audioTransform = changed ? new PlaybackTransform(originalPlan, rate, frames) : null;
            var originalTransforms = new PlaybackTransform[4]; sources.OriginalTuning = originalTransforms;
            for (int lane = 0; lane < 4; lane++)
            {
                string source = Path.Combine(original.Folder, StemMixer.StemNames[lane] + ".wav");
                originalFiles[lane] = source;
                originalTransforms[lane] = changed ? new PlaybackTransform(originalPlan, rate, frames, lane) : null;
                float[] peaks = await Task.Run(() => PlayerWaveforms.Read(source, token));
                sources.NativePeaks[lane] = peaks;
                sources.OriginalPeaks[lane] = changed ? originalTransforms[lane].Peaks(peaks, sourceFrames / (double)rate) : peaks;
            }
            prepared = await Task.Run(() => new StemMixer(originalAudio, original.Folder, originalFiles, audioTransform, originalTransforms));
            prepared.SetLoudness(await PlayerLoudness.Gain(project, original.Audio, token));
            var banks = new Dictionary<string, StemSourceBank>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedStemBank settings in saved.Banks)
            {
                SongInfo donor = donors[settings.Audio];
                var bank = new StemSourceBank { Song = donor, Plan = settings.Plan(), Traits = await PlayerMatching.Analyze(project, donor, token), LoudnessGain = await PlayerLoudness.Gain(project, donor.Audio, token), Files = new string[4], Peaks = new float[4][], Tuning = new PlaybackTransform[4] };
                saved.KeyShifts.TryGetValue(donor.Audio, out shift); bank.Plan.Pitch = TuningReference.Pitch(donor.TuningHz, referenceHz, shift);
                for (int lane = 0; lane < 4; lane++)
                {
                    SavedMixStatus("Opening “" + saved.Name + "” · " + donor.Title + " · " + (lane + 1) + " / 4 stems");
                    bank.Files[lane] = Path.Combine(donor.Folder, StemMixer.StemNames[lane] + ".wav");
                    string file = bank.Files[lane]; bank.Tuning[lane] = PlayerMatching.LiveTransform(file, bank.Plan, rate, frames, lane);
                    float[] peaks = await Task.Run(() => PlayerWaveforms.Read(file, token));
                    using (var reader = new WaveFileReader(file)) bank.Peaks[lane] = bank.Tuning[lane].Peaks(peaks, reader.TotalTime.TotalSeconds);
                }
                banks.Add(settings.Audio, bank);
            }
            for (int lane = 0; lane < 4; lane++) if (!String.IsNullOrEmpty(saved.Sources[lane]))
            {
                StemSourceBank bank = sources.Active[lane] = banks[saved.Sources[lane]];
                prepared.ReplaceStem(lane, bank.Files[lane], bank.LoudnessGain, bank.Tuning[lane]);
            }
            sources.Available = String.IsNullOrEmpty(saved.AddedAudio) ? null : banks[saved.AddedAudio];
            if (!autoPlayEnabled) prepared.RestoreStemTiming(saved.Timing.Select(timing => timing.Live()).ToArray());
            token.ThrowIfCancellationRequested(); if (IsDisposed) throw new OperationCanceledException();
            // All source preparation succeeds before replacing the editable session.
            generation++; if (waveformCancellation != null) waveformCancellation.Cancel();
            CancelMatching(); if (!preserveSession) EndQueueSession(); CloseAudio(); ResetTuning();
            current = sessionSong ?? original; preferredSong = original.Audio; mixer = prepared; prepared = null; stemSources = sources;
            nativeFrames = sourceFrames; nativeRate = rate; originalTempo = saved.OriginalTempo; sharedBpm = saved.Bpm;
            loadedTuningHz = referenceHz;
            originalPlaybackAudio = originalAudio; originalPlaybackFiles = originalFiles;
            originalPlaybackTransform = audioTransform; originalPlaybackTransforms = originalTransforms;
            foreach (var pair in saved.KeyShifts) keyShifts[pair.Key] = pair.Value;
            Array.Copy(saved.Muted, mix.Muted, 4); Array.Copy(saved.Solo, mix.Solo, 4); Array.Copy(saved.Levels, mix.Levels, 4); mix.Preset = "custom";
            readingPreferences = true;
            try { volume.Value = saved.Volume; for (int lane = 0; lane < 4; lane++) faders[lane].Value = (int)Math.Round(saved.Levels[lane] * 100); }
            finally { readingPreferences = false; }
            preferOriginal = saved.OriginalMode; mixer.OriginalMode = preferOriginal; mixer.Volume = volume.Value / 100f; mixer.ApplyMix(mix);
            songTitle.Text = original.Title; songTitle.ForeColor = SongColor(original); artistLabel.Text = original.Artist;
            tip.SetToolTip(songTitle, original.Title); tip.SetToolTip(artistLabel, original.Artist);
            if (cover.Image != null) { cover.Image.Dispose(); cover.Image = null; }
            try { if (File.Exists(original.Cover)) using (Image image = Image.FromFile(original.Cover)) cover.Image = SquareCover(image, 512); } catch { }
            StemSourceBank added = sources.Available; ShowAddedSong(added == null ? null : added.Song, added == null ? null : added.Traits, added == null ? null : added.Plan);
            for (int lane = 0; lane < 4; lane++) { waveforms[lane].ResetGesture(); waveforms[lane].Duration = mixer.Duration; ShowSource(lane); }
            waveformTask = null; waveformCancellation = new CancellationTokenSource(); currentAnalysisTask = AnalyzeCurrent(original, generation, waveformCancellation.Token);
            loadedMixId = saved.Id; CreateOutput(); FollowSessionSong(current); UpdateBrowserSong(); RenderSongs();
            StyleModes(); SetEnabled(true); UpdateTransport(); ShowLibrary(false); SavePreferences();
            message.ForeColor = PlayerTheme.Green; message.Text = "Opened “" + saved.Name + "” · edit the stems and use Save changes to update it.";
            return true;
        }
        catch (OperationCanceledException) { if (!IsDisposed) SavedMixStatus("Opening mix canceled."); return false; }
        catch (Exception error) { if (!IsDisposed) { ShowError(error); SavedMixStatus(error.Message, true); } return false; }
        finally
        {
            if (prepared != null) prepared.Dispose();
            if (savedMixCancellation == cancellation) savedMixCancellation = null;
            if (cancellation != null) cancellation.Dispose();
            openingSavedMix = false;
            if (!IsDisposed) { main.Enabled = sidebar.Enabled = browser.Enabled = true; SetEnabled(mixer != null); UpdateSaveMixButton(); }
        }
    }
    private void RemapSavedMixes(object[] mappings)
    {
        foreach (SavedMix saved in savedMixes)
        {
            saved.OriginalAudio = RemappedPath(saved.OriginalAudio, mappings); saved.AddedAudio = RemappedPath(saved.AddedAudio, mappings);
            for (int lane = 0; lane < 4; lane++) saved.Sources[lane] = RemappedPath(saved.Sources[lane], mappings);
            foreach (SavedStemBank bank in saved.Banks) bank.Audio = RemappedPath(bank.Audio, mappings);
            saved.KeyShifts = saved.KeyShifts.ToDictionary(pair => RemappedPath(pair.Key, mappings), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
    private void RenameSavedMix(SavedMix saved)
    {
        string name = MixNameDialog("Rename mix", saved.Name); if (name == null) return;
        string old = saved.Name; saved.Name = name;
        try { WritePreferences(); UpdateSaveMixButton(); RenderBrowser(true); }
        catch (Exception error) { saved.Name = old; ShowError(error); SavedMixStatus(error.Message, true); }
    }
    private void DeleteSavedMix(SavedMix saved)
    {
        int index = savedMixes.IndexOf(saved); if (index < 0) return;
        var previousItems = songPlaylists.ToDictionary(playlist => playlist, playlist => playlist.Items.ToList());
        string key = MixPlaylistPrefix + saved.Id;
        foreach (SongPlaylist playlist in songPlaylists) playlist.Items.RemoveAll(item => item == key);
        savedMixes.RemoveAt(index);
        try
        {
            WritePreferences();
            foreach (PlaybackQueueEntry entry in songQueue.Items.Where(item => item.Audio == key && !QueueEntryLocked(item.Id)).ToArray()) RemoveQueuedSong(entry.Id);
            if (loadedMixId == saved.Id) loadedMixId = null; UpdateSaveMixButton(); RenderBrowser(true);
        }
        catch (Exception error) { savedMixes.Insert(index, saved); foreach (var pair in previousItems) pair.Key.Items = pair.Value; ShowError(error); SavedMixStatus(error.Message, true); }
    }
    private void RenderMixPlaylists()
    {
        browserFingerprint = null; LayoutBrowser(); libraryGrid.SuspendLayout();
        libraryGrid.FlowDirection = FlowDirection.LeftToRight; libraryGrid.WrapContents = true;
        foreach (Control child in libraryGrid.Controls.Cast<Control>().ToArray()) child.Dispose();
        allSongsTab.Selected = artistsTab.Selected = false; playlistsTab.Selected = true;
        allSongsTab.Invalidate(); artistsTab.Invalidate(); playlistsTab.Invalidate(); manageArtist.Visible = false;
        libraryCount.ForeColor = PlayerTheme.Muted; tip.SetToolTip(libraryCount, "");
        mixesBackButton.Visible = myMixesView || playlistFolderId != null; libraryHeading.Text = myMixesView ? "My mixes" : "Playlists";
        string query = librarySearch.Text.Trim();
        List<SavedMix> matching = savedMixes.Where(item => (item.Name + " " + item.OriginalTitle + " " + item.OriginalArtist + " " + item.AddedTitle).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        libraryCount.Text = myMixesView ? matching.Count + (matching.Count == 1 ? " saved mix" : " saved mixes") + " · open a mix to keep editing" : "1 playlist · " + savedMixes.Count + " saved mixes";
        if (!myMixesView && playlistFolderId == null)
        {
            var folder = new LibraryCard { Folder = true, Title = "My mixes", Subtitle = savedMixes.Count + (savedMixes.Count == 1 ? " saved mix" : " saved mixes"), AccessibleName = "Open My mixes playlist", Size = new Size(200, 258) };
            folder.Click += delegate { myMixesView = true; RenderBrowser(true); }; libraryGrid.Controls.Add(folder);
        }
        if (!myMixesView) RenderCustomPlaylists(query);
        else foreach (SavedMix saved in matching)
        {
            SongInfo original = songs.FirstOrDefault(song => String.Equals(song.Audio, saved.OriginalAudio, StringComparison.OrdinalIgnoreCase));
            var card = new LibraryCard { Tag = saved, Title = saved.Name, Subtitle = saved.OriginalTitle + (String.IsNullOrEmpty(saved.AddedTitle) ? "" : " + " + saved.AddedTitle), Details = "Editable mix", Artwork = original == null ? null : Thumbnail(original), AccessibleName = "Open editable mix: " + saved.Name, ActionName = "Open mix", Size = new Size(200, 284) };
            var menu = new ContextMenuStrip(); menu.Items.Add("Open mix", null, delegate { QueueArtistAction(() => OpenSavedMix(saved)); });
            menu.Items.Add("Rename", null, delegate { DeferPlaylistAction(() => RenameSavedMix(saved)); }); menu.Items.Add("Delete saved mix", null, delegate { DeferPlaylistAction(() => DeleteSavedMix(saved)); }); card.ContextMenuStrip = menu;
            card.Disposed += delegate { menu.Dispose(); }; card.RenameFolder = delegate { RenameSavedMix(saved); };
            card.Click += async delegate { await OpenSavedMix(saved); };
            tip.SetToolTip(card, saved.Name + "\n" + card.Subtitle + "\nOpen to edit songs, stem sources, volumes and timing.\nRight-click to rename or delete. F2 renames."); libraryGrid.Controls.Add(card);
        }
        if (myMixesView && matching.Count == 0) libraryGrid.Controls.Add(new Label { Text = savedMixes.Count == 0 ? "Save a mix from Now playing. Your editable mixes will appear here." : "No matching mixes.", ForeColor = PlayerTheme.Muted, AutoSize = true, Margin = new Padding(8, 20, 0, 0) });
        LayoutBrowser(); libraryGrid.AutoScrollPosition = Point.Empty; libraryGrid.ResumeLayout(true); libraryGrid.PerformLayout();
    }
}
