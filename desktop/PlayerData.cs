using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

internal static class PlayerData
{
    internal static string Root(string project) { return Environment.GetEnvironmentVariable("STEMKIT_USER_DATA") ?? project; }
    internal static string Data(string project, params string[] parts) { return Path.Combine(Path.Combine(Root(project), "data"), Path.Combine(parts)); }
    internal static string Settings(string project) { return Path.Combine(Root(project), "settings.json"); }
    internal static string SeparationDevice(string project)
    {
        try { var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Settings(project))); object value; return data.TryGetValue("separationDevice", out value) && Convert.ToString(value) == "cpu" ? "cpu" : "auto"; }
        catch { return "auto"; }
    }
    internal static void SaveSeparationDevice(string project, string device)
    {
        string file = Settings(project); var data = File.Exists(file) ? new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file)) : new Dictionary<string, object>();
        data["separationDevice"] = device == "cpu" ? "cpu" : "auto";
        Directory.CreateDirectory(Path.GetDirectoryName(file)); string temporary = file + "." + Guid.NewGuid().ToString("N") + ".pending";
        try { File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(data)); if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
