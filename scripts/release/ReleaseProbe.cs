using System;
using System.IO;
using NAudio.Wave;
internal static class ReleaseProbe
{
    private static int Main()
    {
        try {
            string project = Directory.GetParent(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)).FullName;
            string selected = Environment.GetEnvironmentVariable("STEMKIT_USER_DATA");
            if (PlayerLibrary.ConfiguredRoot(project) != selected || !PlayerData.Data(project,"player-settings.json").StartsWith(selected+Path.DirectorySeparatorChar)) throw new Exception("Native user storage is outside the selected library.");
            using (var form = new PlayerForm(selected,project)) { if (!form.ShowIcon || form.Icon == null) throw new Exception("The player icon is missing."); }
            string wave = PlayerData.Data(project,"native-probe.wav");
            try {
                using (var writer = new WaveFileWriter(wave,new WaveFormat(44100,16,2))) { byte[] silence = new byte[44100*4]; writer.Write(silence,0,silence.Length); }
                using (var reader = new MediaFoundationReader(wave)) if(reader.Read(new byte[4096],0,4096)==0) throw new Exception("Windows audio decoding failed.");
                var transform = new PlaybackTransform(new StemMatchPlan {Tempo=1.1,Pitch=1.02,Offset=0},44100,44100);
                using(var stream = transform.Open(wave)) if(stream.Read(new byte[4096],0,4096)==0) throw new Exception("Live audio processing failed.");
            } finally { if(File.Exists(wave)) File.Delete(wave); }
            Console.WriteLine("Native player, selected library, circle icon, audio decoder and live audio DLLs passed."); return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
