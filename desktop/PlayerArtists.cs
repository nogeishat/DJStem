using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private readonly GlassButton manageArtist = new GlassButton();
    private readonly Dictionary<string, string> artistCovers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly ContextMenuStrip artistActionsMenu = new ContextMenuStrip();
    private string artistMenuName;
    private bool editingArtists;
    private Task waveformTask, currentAnalysisTask;

    private void ReadArtistCovers()
    {
        artistCovers.Clear();
        string file = Path.Combine(library, ".artist-folders.json");
        if (!File.Exists(file)) return;
        try
        {
            var data = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file));
            var covers = data["covers"] as Dictionary<string, object>;
            if (covers == null) return;
            foreach (var cover in covers)
            {
                string image = Path.GetFullPath(Path.Combine(library, Convert.ToString(cover.Value)));
                if (PlayerLibrary.Inside(Path.Combine(library, ".artist-covers"), image)) artistCovers[cover.Key] = image;
            }
        }
        catch (Exception error) { ShowError(error); }
    }
    private Image ArtistCover(string name)
    {
        string file;
        return artistCovers.TryGetValue(name.Normalize(), out file) ? Thumbnail(new SongInfo { Cover = file }) : null;
    }
    private ContextMenuStrip ArtistMenu(string name)
    {
        if (!artistActionsMenu.Visible) artistMenuName = name;
        if (artistActionsMenu.Items.Count == 0)
        {
            artistActionsMenu.Items.Add("Rename artist folder…", null, delegate { string selected = artistMenuName; QueueArtistAction(() => RenameArtistFolder(selected)); });
            artistActionsMenu.Items.Add("Choose cover picture…", null, delegate { string selected = artistMenuName; QueueArtistAction(() => ChooseArtistCover(selected)); });
            artistActionsMenu.Items.Add("Remove cover picture", null, delegate { string selected = artistMenuName; QueueArtistAction(() => EditArtistCover(selected, null)); });
            artistActionsMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                Control source = artistActionsMenu.SourceControl;
                artistMenuName = source == manageArtist ? artistFolder : source == null ? artistMenuName : source.Tag as string;
                e.Cancel = String.IsNullOrEmpty(artistMenuName);
                artistActionsMenu.Items[2].Enabled = !e.Cancel && artistCovers.ContainsKey(artistMenuName.Normalize());
            };
            Disposed += delegate { artistActionsMenu.Dispose(); };
        }
        if (!artistActionsMenu.Visible) artistActionsMenu.Items[2].Enabled = artistCovers.ContainsKey(name.Normalize());
        return artistActionsMenu;
    }
    private void QueueArtistAction(Func<Task> action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        // ToolStrip must finish dismissing the menu before a dialog opens or a
        // library refresh removes the card that opened it.
        BeginInvoke((MethodInvoker)(async delegate
        {
            if (IsDisposed || Disposing) return;
            try { await action(); }
            catch (Exception error) { if (!IsDisposed) ShowError(error); }
        }));
    }
    private bool CanEditArtist()
    {
        if (loading || refreshing || preparingReplacement || editingArtists || editingTuning || AutoTransitionActive)
        {
            MessageBox.Show(this, "Wait for the current library or stem operation to finish, then try again.", "Artist folder", MessageBoxButtons.OK, MessageBoxIcon.Information); return false;
        }
        return true;
    }
    private Task<Dictionary<string, object>> ArtistOperation(object request) { return LibraryOperation(request, "manage-artist.mjs"); }
    private async Task<Dictionary<string, object>> LibraryOperation(object request, string helper)
    {
        string node = Environment.GetEnvironmentVariable("STEMKIT_NODE");
        if (String.IsNullOrWhiteSpace(node))
        {
            node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (!File.Exists(node)) node = "node.exe";
        }
        string script = Path.Combine(project, "scripts", helper);
        var start = new ProcessStartInfo(node, "\"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = project, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (Process process = new Process { StartInfo = start })
        {
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            string json = new JavaScriptSerializer().Serialize(request);
            await process.StandardInput.WriteAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(json))); process.StandardInput.Close();
            await Task.Run(() => process.WaitForExit());
            string result = await output, error = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
            return (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(result);
        }
    }
    private async Task ChooseArtistCover(string name)
    {
        if (!CanEditArtist()) return;
        using (OpenFileDialog picker = new OpenFileDialog { Title = "Choose cover picture for " + name, Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", CheckFileExists = true })
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            string temporary = PlayerData.Data(project, "artist-cover-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temporary));
                using (Image source = Image.FromFile(picker.FileName)) using (Bitmap cover = SquareCover(source, 512)) cover.Save(temporary, ImageFormat.Png);
                await EditArtistCover(name, temporary);
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Artist cover", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private async Task EditArtistCover(string name, string image)
    {
        if (!CanEditArtist()) return;
        editingArtists = true; browser.Enabled = false;
        try { await ArtistOperation(new { operation = "cover", root = library, name = name, image = image }); ReadArtistCovers(); RenderBrowser(true); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Artist cover", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { editingArtists = false; if (!IsDisposed) browser.Enabled = true; }
    }
    private static string RemappedPath(string file, object[] mappings)
    {
        if (String.IsNullOrEmpty(file)) return file;
        foreach (object value in mappings)
        {
            var mapping = value as Dictionary<string, object>; if (mapping == null) continue;
            foreach (string field in new[] { "audio", "folder", "directory" })
            {
                object source, target;
                string targetField = field == "audio" ? "newAudio" : field == "folder" ? "newFolder" : "newDirectory";
                if (!mapping.TryGetValue(field, out source) || !mapping.TryGetValue(targetField, out target)) continue;
                string oldPath = Convert.ToString(source);
                if (String.Equals(file, oldPath, StringComparison.OrdinalIgnoreCase) || field != "audio" && PlayerLibrary.Inside(oldPath, file)) return Convert.ToString(target) + file.Substring(oldPath.Length);
            }
        }
        return file;
    }
    private async Task RenameArtistFolder(string name)
    {
        if (!CanEditArtist()) return;
        string newName;
        using (Form dialog = new Form { Text = "Rename artist folder", ClientSize = new Size(440, 185), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = PlayerTheme.Background, ForeColor = PlayerTheme.Text })
        {
            Label label = new Label { Text = "Artist folder name", Bounds = new Rectangle(20, 16, 400, 22) };
            TextBox input = new TextBox { Text = name, MaxLength = 60, Bounds = new Rectangle(20, 44, 400, 26), AccessibleName = "New artist folder name" };
            Label hint = new Label { Text = "An existing artist folder with this name will be merged.\nSongs with matching filenames are both kept.", Bounds = new Rectangle(20, 80, 400, 43), ForeColor = PlayerTheme.Muted };
            Button save = new Button { Text = "Rename", DialogResult = DialogResult.OK, Bounds = new Rectangle(230, 137, 90, 30) };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(330, 137, 90, 30) };
            dialog.Controls.AddRange(new Control[] { label, input, hint, save, cancel }); dialog.AcceptButton = save; dialog.CancelButton = cancel;
            dialog.Shown += delegate { input.Focus(); input.SelectAll(); };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            newName = input.Text.Trim();
        }
        if (newName == name || !CanEditArtist()) return;
        editingArtists = true; browser.Enabled = sidebar.Enabled = false;
        SongInfo previous = current; StemSourceState previousSources = stemSources;
        bool playing = output != null && output.PlaybackState == NAudio.Wave.PlaybackState.Playing;
        double position = mixer == null ? 0 : mixer.Seconds;
        StemTiming[] previousTiming = StemTimingSnapshot();
        try
        {
            CancelAutoPreparation(); CancelMatching(); if (waveformCancellation != null) waveformCancellation.Cancel();
            if (autoPreparationTask != null) await autoPreparationTask;
            if (autoPreloadTask != null) await autoPreloadTask;
            if (autoPreloadPreparationTask != null) await autoPreloadPreparationTask;
            if (waveformTask != null) await waveformTask; if (currentAnalysisTask != null) await currentAnalysisTask;
            CloseAudio(); SetEnabled(false);
            var result = await ArtistOperation(new { operation = "rename", root = library, name = name, newName = newName });
            var mappings = result["mappings"] as object[] ?? new object[0];
            string target = Convert.ToString(result["name"]);
            preferredSong = RemappedPath(preferredSong, mappings);
            RemapTuning(mappings);
            RemapPlaybackQueue(mappings);
            RemapSavedMixes(mappings);
            RemapPlaylists(mappings);
            if (originalPlaybackAudio != null) originalPlaybackAudio = RemappedPath(originalPlaybackAudio, mappings);
            if (originalPlaybackFiles != null) for (int i = 0; i < 4; i++) originalPlaybackFiles[i] = RemappedPath(originalPlaybackFiles[i], mappings);
            if (previous != null) { previous.Audio = RemappedPath(previous.Audio, mappings); previous.Folder = RemappedPath(previous.Folder, mappings); previous.Cover = RemappedPath(previous.Cover, mappings); }
            if (previousSources != null)
            {
                var banks = previousSources.Active.Concat(new[] { previousSources.Available }).Where(bank => bank != null).Distinct();
                foreach (StemSourceBank bank in banks)
                {
                    bank.Song.Audio = RemappedPath(bank.Song.Audio, mappings); bank.Song.Folder = RemappedPath(bank.Song.Folder, mappings); bank.Song.Cover = RemappedPath(bank.Song.Cover, mappings);
                    for (int lane = 0; lane < bank.Files.Length; lane++) bank.Files[lane] = RemappedPath(bank.Files[lane], mappings);
                }
            }
            if (artistFolder != null && (String.Equals(artistFolder, name, StringComparison.OrdinalIgnoreCase) || String.Equals(artistFolder, newName, StringComparison.OrdinalIgnoreCase))) artistFolder = target;
            songs = await Task.Run(() => PlayerLibrary.Scan(library)); ReadArtistCovers();
            if (previous != null) previous = songs.FirstOrDefault(song => String.Equals(song.Audio, previous.Audio, StringComparison.OrdinalIgnoreCase)) ?? previous;
            if (previousSources != null) foreach (StemSourceBank bank in previousSources.Active.Concat(new[] { previousSources.Available }).Where(bank => bank != null).Distinct()) bank.Song = songs.FirstOrDefault(song => String.Equals(song.Audio, bank.Song.Audio, StringComparison.OrdinalIgnoreCase)) ?? bank.Song;
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Rename artist folder", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        try
        {
            if (!IsDisposed)
            {
                if (previous != null && mixer == null)
                {
                    await LoadSong(previous, false, position, true);
                    if (mixer != null && previousSources != null && mixer.HasStems)
                    {
                        stemSources.Available = previousSources.Available;
                        for (int i = 0; i < 4; i++) if (previousSources.Active[i] != null)
                        {
                            StemSourceBank bank = previousSources.Active[i]; mixer.ReplaceStem(i, bank.Files[i], bank.LoudnessGain, bank.Tuning == null ? null : bank.Tuning[i]); stemSources.Active[i] = bank; playingStemFiles[i] = bank.Files[i]; waveforms[i].Peaks = bank.Peaks[i];
                        }
                        if (stemSources.Available != null) ShowAddedSong(stemSources.Available.Song, stemSources.Available.Traits, stemSources.Available.Plan);
                        UpdateSourceButtons();
                    }
                    if (mixer != null && previousTiming != null) { mixer.RestoreStemTiming(previousTiming); mixer.Seek(position); UpdateTransport(); }
                    if (playing && output != null) output.Play();
                }
            }
        }
        catch (Exception error) { if (!IsDisposed) ShowError(error); }
        finally { editingArtists = false; if (!IsDisposed) { browser.Enabled = sidebar.Enabled = true; UpdateSourceButtons(); RenderSongs(); RenderBrowser(true); SavePreferences(); } }
    }
}
