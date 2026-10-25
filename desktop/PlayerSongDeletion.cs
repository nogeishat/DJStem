using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

internal sealed partial class PlayerForm
{
    private readonly GlassButton deleteSongButton = new GlassButton();
    private readonly ContextMenuStrip songActionsMenu = new ContextMenuStrip();
    private SongInfo songMenuTarget;

    private bool CanDeleteSong()
    {
        return !IsDisposed && !loading && !refreshing && !editingArtists && !editingTuning && !openingSavedMix && !preparingReplacement && !advancingQueue && !AutoTransitionActive && !StemGestureActive && exportCancellation == null;
    }
    private void InitializeSongDeletion()
    {
        SetupButton(deleteSongButton, "Delete song…", nowPlayingActions, null);
        deleteSongButton.ForeColor = PlayerTheme.Stems[1]; deleteSongButton.AccessibleName = "Delete song, MP3, stems and saved source link";
        deleteSongButton.Click += delegate { nowPlayingMenu.Close(); SongInfo selected = current; QueueArtistAction(() => DeleteSongInteractive(selected)); };
        songActionsMenu.BackColor = PlayerTheme.Glass; songActionsMenu.ForeColor = PlayerTheme.Text; songActionsMenu.ShowImageMargin = false;
        songActionsMenu.Items.Add("Add to queue", null, delegate { AddQueuedSong(songMenuTarget, songQueue.Items.Count); });
        ToolStripItem deleteSong = songActionsMenu.Items.Add("Delete song…", null, delegate { SongInfo selected = songMenuTarget; QueueArtistAction(() => DeleteSongInteractive(selected)); });
        songActionsMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Control source = songActionsMenu.SourceControl;
            if (source == list)
            {
                int index = list.IndexFromPoint(list.PointToClient(Cursor.Position));
                songMenuTarget = (index >= 0 ? list.Items[index] : list.SelectedItem) as SongInfo;
            }
            else if (source != null) songMenuTarget = source.Tag as SongInfo;
            e.Cancel = songMenuTarget == null; deleteSong.Enabled = CanDeleteSong();
        };
        list.ContextMenuStrip = songActionsMenu;
        Disposed += delegate { songActionsMenu.Dispose(); };
    }
    private static bool SameSong(string a, string b) { return !String.IsNullOrEmpty(a) && String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    private bool MixUsesSong(SavedMix saved, string audio)
    {
        return SameSong(saved.OriginalAudio, audio) || SameSong(saved.AddedAudio, audio) || saved.Sources.Any(file => SameSong(file, audio)) || saved.Banks.Any(bank => SameSong(bank.Audio, audio));
    }
    private async Task DeleteSongInteractive(SongInfo song)
    {
        if (song == null) return;
        if (!CanDeleteSong()) { ShowError(new InvalidOperationException("Wait for the current song operation or MP3 export to finish before deleting.")); return; }
        song = songs.FirstOrDefault(item => SameSong(item.Audio, song.Audio)) ?? song;
        int dependent = savedMixes.Count(saved => MixUsesSong(saved, song.Audio));
        string detail = "Delete “" + song.Title + "” by " + song.Artist + "?\n\nThis permanently deletes its MP3, stems and artwork, saved YouTube/SoundCloud link, and download record. It also removes it from playlists and the queue.";
        if (dependent != 0) detail += "\n\n" + dependent + (dependent == 1 ? " saved mix uses" : " saved mixes use") + " this song and will also be deleted.";
        if (MessageBox.Show(this, detail, "Delete song", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await DeleteSong(song);
    }
    private void RemoveDeletedSongReferences(string audio)
    {
        string[] mixIds = savedMixes.Where(saved => MixUsesSong(saved, audio)).Select(saved => saved.Id).ToArray();
        savedMixes.RemoveAll(saved => mixIds.Contains(saved.Id));
        foreach (SongPlaylist playlist in songPlaylists) playlist.Items.RemoveAll(item => SameSong(item, audio) || mixIds.Any(id => item == MixPlaylistPrefix + id));
        songQueue.Forget(audio); foreach (string id in mixIds) songQueue.Forget(MixPlaylistPrefix + id);
        if (mixIds.Contains(loadedMixId)) loadedMixId = null;
        if (current != null && current.SavedMix != null && mixIds.Contains(current.SavedMix.Id))
        {
            current.Title = current.SavedMix.OriginalTitle; current.Artist = current.SavedMix.OriginalArtist; current.SavedMix = null;
        }
        keyShifts.Remove(audio); autoFailedSongs.Remove(audio);
        if (SameSong(preferredSong, audio)) preferredSong = null;
    }
    private async Task<bool> DeleteSong(SongInfo song)
    {
        if (song == null || !CanDeleteSong()) return false;
        SongInfo previous = current; StemSourceState sources = stemSources;
        double position = mixer == null ? 0 : mixer.Seconds; float loudness = mixer == null ? 1 : mixer.OriginalLoudnessGain;
        StemTiming[] timing = StemTimingSnapshot();
        bool playing = output != null && output.PlaybackState == PlaybackState.Playing, resumeTimer = timer.Enabled;
        bool deleted = false, released = false;
        editingArtists = true; main.Enabled = sidebar.Enabled = browser.Enabled = false; timer.Stop();
        if (playing) output.Pause();
        try
        {
            WritePreferences(); generation++; CancelAutoPreparation(); CancelMatching();
            if (waveformCancellation != null) waveformCancellation.Cancel();
            if (autoPreparationTask != null) await autoPreparationTask;
            if (autoPreloadTask != null) await autoPreloadTask;
            if (autoPreloadPreparationTask != null) await autoPreloadPreparationTask;
            if (waveformTask != null) await waveformTask; if (currentAnalysisTask != null) await currentAnalysisTask;
            CloseAudio(); released = true;
            var result = await LibraryOperation(new { operation = "delete", root = library, audio = song.Audio }, "manage-song.mjs");
            deleted = true; RemoveDeletedSongReferences(song.Audio);
            if (sources != null)
            {
                if (sources.Available != null && SameSong(sources.Available.Song.Audio, song.Audio)) sources.Available = null;
                for (int lane = 0; lane < 4; lane++) if (sources.Active[lane] != null && SameSong(sources.Active[lane].Song.Audio, song.Audio)) { sources.Active[lane] = null; if (timing != null) timing[lane] = new StemTiming(); }
            }
            songs = await Task.Run(() => PlayerLibrary.Scan(library));
            if (previous != null && SameSong(previous.Audio, song.Audio))
            {
                previous = null; current = null; stemSources = null; loadedMixId = null; ResetTuning(); ShowAddedSong(null);
                if (cover.Image != null) { cover.Image.Dispose(); cover.Image = null; }
                songTitle.Text = "Select a song"; artistLabel.Text = "";
                for (int lane = 0; lane < 4; lane++) { playingStemFiles[lane] = null; waveforms[lane].Peaks = null; waveforms[lane].ResetGesture(); }
                SetEnabled(false); UpdateBrowserSong();
            }
            bool pending = result.ContainsKey("cleanupPending") && Convert.ToBoolean(result["cleanupPending"]);
            message.ForeColor = pending ? PlayerTheme.Stems[1] : PlayerTheme.Green;
            message.Text = pending ? "Song removed. Some files are still in use: " + Convert.ToString(result["remainingFiles"]) : "Deleted “" + song.Title + "” · MP3, stems and source link removed.";
            return true;
        }
        catch (Exception error) { if (!IsDisposed) ShowError(error); return deleted; }
        finally
        {
            if (!IsDisposed)
            {
                try
                {
                    if (released && previous != null && File.Exists(previous.Audio))
                    {
                        mixer = new StemMixer(originalPlaybackAudio ?? previous.Audio, previous.Folder, originalPlaybackFiles, originalPlaybackTransform, originalPlaybackTransforms); mixer.SetLoudness(loudness);
                        stemSources = sources;
                        if (sources != null && mixer.HasStems) for (int lane = 0; lane < 4; lane++)
                        {
                            StemSourceBank bank = sources.Active[lane]; if (bank != null) mixer.ReplaceStem(lane, bank.Files[lane], bank.LoudnessGain, bank.Tuning == null ? null : bank.Tuning[lane]);
                            playingStemFiles[lane] = sources.File(lane); waveforms[lane].Peaks = sources.Peaks(lane);
                        }
                        if (timing != null) mixer.RestoreStemTiming(timing);
                        mixer.Volume = volume.Value / 100f; mixer.ApplyMix(mix); mixer.OriginalMode = preferOriginal; mixer.Seek(position); CreateOutput();
                        StemSourceBank added = sources == null ? null : sources.Available;
                        ShowAddedSong(added == null ? null : added.Song, added == null ? null : added.Traits, added == null ? null : added.Plan);
                        if (playing) output.Play();
                        FollowSessionSong(current);
                    }
                }
                catch (Exception error) { ShowError(error); }
                if (!released && playing && output != null) output.Play();
                editingArtists = false; main.Enabled = sidebar.Enabled = browser.Enabled = true;
                UpdateSourceButtons(); UpdateTransport(); RenderPlaybackQueue(); RenderSongs(); RenderBrowser(true); SavePreferences();
                if (resumeTimer) timer.Start();
            }
        }
    }
}
