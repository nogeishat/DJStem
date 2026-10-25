using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private readonly GlassButton exportMixButton = new GlassButton();
    private CancellationTokenSource exportCancellation;
    private int exportProgress;

    private bool CanExportMix()
    {
        return !IsDisposed && current != null && mixer != null && !loading && !preparingReplacement && !editingTuning && !editingArtists && !openingSavedMix && !AutoTransitionActive && !StemGestureActive;
    }
    private byte[] CaptureExportCover() { return PlayerExport.Cover(cover.Image, displayedAddedSong == null ? null : addedCover.Image); }
    private async Task ExportMixInteractive()
    {
        if (exportCancellation != null) { exportCancellation.Cancel(); return; }
        if (!CanExportMix()) return;
        try
        {
            SavedMix saved = savedMixes.FirstOrDefault(item => item.Id == loadedMixId);
            string title = saved == null ? current.Title + " mix" : saved.Name, artist = current.Artist;
            byte[] artwork = CaptureExportCover();
            // Freeze the settings at the click, including when the save dialog stays open.
            using (StemMixer snapshot = mixer.ExportCopy())
            using (var dialog = new SaveFileDialog { Title = "Export mix as MP3", Filter = "MP3 audio (*.mp3)|*.mp3", DefaultExt = "mp3", AddExtension = true, OverwritePrompt = true, FileName = PlayerExport.SafeName(saved == null ? artist + " - " + title : title) + ".mp3" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed) return;
                string destination = Path.GetFullPath(dialog.FileName);
                if (songs.Concat(new[] { current }).Any(song => song != null && String.Equals(Path.GetFullPath(song.Audio), destination, StringComparison.OrdinalIgnoreCase))) throw new IOException("Choose a new filename to keep the library's source song.");
                using (var cancellation = new CancellationTokenSource())
                {
                    exportCancellation = cancellation; exportProgress = 0; UpdateSaveMixButton();
                    message.ForeColor = PlayerTheme.Muted; message.Text = "Exporting MP3… · " + Path.GetFileName(destination);
                    var progress = new Progress<int>(percent => { if (!IsDisposed && exportCancellation == cancellation) { exportProgress = percent; UpdateSaveMixButton(); } });
                    try
                    {
                        await Task.Run(() => PlayerExport.Mp3(snapshot, destination, title, artist, cancellation.Token, progress, artwork));
                        if (!IsDisposed) { message.ForeColor = PlayerTheme.Green; message.Text = "Exported MP3 · " + Path.GetFileName(destination); tip.SetToolTip(message, destination); }
                    }
                    catch (OperationCanceledException) { if (!IsDisposed) { message.ForeColor = PlayerTheme.Muted; message.Text = "MP3 export canceled."; } }
                    finally { exportCancellation = null; if (!IsDisposed) UpdateSaveMixButton(); }
                }
            }
        }
        catch (Exception error) { if (!IsDisposed) ShowError(error); }
    }
}
