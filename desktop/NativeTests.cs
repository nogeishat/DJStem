using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class NativeTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static int Main()
    {
        try
        {
            string url = "https://www.youtube.com/watch?v=BaW_jenozKc";
            DataObject uri = new DataObject();
            uri.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes(url + "\0")));
            uri.SetData(DataFormats.UnicodeText, url);
            Check(DropBox.ExtractDropText(uri) == url, "Unicode browser URI streams should be accepted once");
            DataObject batch = new DataObject();
            batch.SetData(DataFormats.UnicodeText, url + "\nhttps://youtu.be/YE7VzlLtp-4");
            Check(DropBox.ExtractDropText(batch).Split('\n').Length == 2, "Multiple plain text links should be preserved");
            DataObject html = new DataObject();
            html.SetData(DataFormats.Html, "<a href=\"" + url + "&amp;t=20\"><img src=\"cover.jpg\"></a>");
            Check(DropBox.ExtractDropText(html) == url + "&t=20", "Dragged HTML thumbnails should supply decoded hrefs");
            string soundcloudUrl = "https://soundcloud.com/artist/song?si=shared";
            DataObject soundcloud = new DataObject();
            soundcloud.SetData("UniformResourceLocator", new MemoryStream(Encoding.UTF8.GetBytes(soundcloudUrl + "\0")));
            soundcloud.SetData(DataFormats.Html, "<a href=\"" + soundcloudUrl + "\"><img src=\"artwork.jpg\"></a>");
            Check(DropBox.ExtractDropText(soundcloud) == soundcloudUrl, "SoundCloud track and artwork drops should supply the URL once");
            DataObject sharedTrack = new DataObject(); sharedTrack.SetData("text/uri-list", "https://on.soundcloud.com/abc123\r\n" + url);
            Check(DropBox.ExtractDropText(sharedTrack).Split('\n').Length == 2, "Mixed SoundCloud share links and YouTube URI lists should be preserved");
            DataObject unsupported = new DataObject(); unsupported.SetData(DataFormats.Text, "A video title with no URL");
            Check(DropBox.ExtractDropText(unsupported) == "", "Unlinked text should be rejected");
            var state = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject("{\"jobs\":[{\"status\":\"queued\"}],\"paused\":false}");
            Check(state["jobs"] is object[], "Native polling must deserialize the server's job array");
            Check(((object[])state["jobs"])[0] is Dictionary<string, object>, "Native polling must deserialize jobs as dictionaries");
            Console.WriteLine("Native drop formats and polling: 8 checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
