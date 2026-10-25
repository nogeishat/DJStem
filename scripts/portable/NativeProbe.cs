using System;
using System.IO;
using System.Linq;
using NAudio.Wave;

internal static class PortableProbe
{
    private static int Main(string[] args)
    {
        try
        {
            string project = Directory.GetParent(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)).FullName;
            string root = PlayerLibrary.ConfiguredRoot(project);
            var songs = PlayerLibrary.Scan(root);
            if (songs.Count > 0)
            {
                using (var audio = new MediaFoundationReader(songs[0].Audio))
                    if (audio.Read(new byte[8192], 0, 8192) == 0) throw new Exception("MP3 decoder returned no audio.");
                var song = songs.FirstOrDefault(item => item.HasStems);
                if (song != null)
                {
                    var transform = new PlaybackTransform(new StemMatchPlan { Tempo = 1.1, Pitch = 1.02, Offset = 0 }, 44100, 44100);
                    using (var stream = transform.Open(Path.Combine(song.Folder, "drums.wav")))
                        if (stream.Read(new byte[8192], 0, 8192) == 0) throw new Exception("Live stem processor returned no audio.");
                }
            }
            Console.WriteLine("Native player: " + songs.Count + " songs; " + songs.Count(song => song.HasStems) + " with stems. MP3 decoder and live audio DLLs passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
