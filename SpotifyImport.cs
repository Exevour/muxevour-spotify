using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using WF = System.Windows.Forms;

namespace XvrPlayer
{
    // Spotify's listening history into Muxevour's library (Settings → Library → Import): the user's own data
    // download from Spotify. Part of the Spotify plugin, as everything that names Spotify.
    internal static class SpotifyImport
    {
        // ---------- Spotify: the user's own data download (Account > Privacy > Download your data) ----------

        public static void Run(MainWindow app)
        {
            using (WF.FolderBrowserDialog d = new WF.FolderBrowserDialog { Description = "The unzipped folder of your Spotify data (with StreamingHistory... or Streaming_History_Audio... files)", ShowNewFolderButton = false })
            {
                if (d.ShowDialog() != WF.DialogResult.OK) return;
                try
                {
                    string[] history = HistoryFiles(d.SelectedPath);
                    if (history.Length == 0) { WF.MessageBox.Show("No listening history was found in that folder.\n\nLook for files named StreamingHistory_music_0.json (Account data) or Streaming_History_Audio_....json (Extended streaming history) in the folder Spotify sent.", "Muxevour"); return; }
                    string sig = string.Join("|", Array.ConvertAll(history, f => Path.GetFileName(f) + new FileInfo(f).Length));
                    if (!Importers.FirstTime(sig, true)) return;
                    List<Library.Imported> rows = ReadSpotifyHistory(history);
                    Dictionary<string, List<Library.Imported>> lists = ReadSpotifyPlaylists(d.SelectedPath);
                    Importers.Finish(app, "Spotify", sig, rows, lists, true);
                }
                catch (Exception ex) { Log.Error("Spotify import", ex); WF.MessageBox.Show("Spotify's files couldn't be read: " + ex.Message, "Muxevour"); }
            }
        }

        private static string[] HistoryFiles(string folder)
        {
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (n.StartsWith("streaminghistory_music") || n.StartsWith("streaminghistory") && !n.Contains("podcast") && !n.Contains("video") || n.StartsWith("streaming_history_audio")) files.Add(f);
            }
            // (with the extended history there, the shorter account-data one would count the same plays twice)
            if (files.Exists(f => Path.GetFileName(f).ToLowerInvariant().StartsWith("streaming_history_audio")))
                files.RemoveAll(f => !Path.GetFileName(f).ToLowerInvariant().StartsWith("streaming_history_audio"));
            return files.ToArray();
        }

        // each song's plays (30 seconds or more, as Spotify counts them), skips and last listen
        private static List<Library.Imported> ReadSpotifyHistory(string[] files)
        {
            Dictionary<string, Library.Imported> songs = new Dictionary<string, Library.Imported>();
            JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (string f in files)
            {
                object[] list = js.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as object[];
                if (list == null) continue;
                foreach (object o in list)
                {
                    Dictionary<string, object> e = o as Dictionary<string, object>;
                    if (e == null) continue;
                    string artist = S(e, "artistName") ?? S(e, "master_metadata_album_artist_name"), title = S(e, "trackName") ?? S(e, "master_metadata_track_name"), album = S(e, "master_metadata_album_album_name");
                    if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title)) continue;
                    long ms = e.ContainsKey("msPlayed") ? Convert.ToInt64(e["msPlayed"]) : e.ContainsKey("ms_played") ? Convert.ToInt64(e["ms_played"]) : 0;
                    string when = S(e, "endTime") ?? S(e, "ts");
                    DateTime at;
                    DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
                    string key = artist.ToLowerInvariant() + "\u0001" + title.ToLowerInvariant();
                    Library.Imported r;
                    if (!songs.TryGetValue(key, out r)) songs[key] = r = new Library.Imported { Artist = artist, Title = title, Album = album };
                    bool skipped = e.ContainsKey("skipped") && e["skipped"] is bool && (bool)e["skipped"];
                    if (ms >= 30000 && !skipped) r.Plays++;
                    else if (skipped || ms < 30000 && ms > 0) r.Skips++;
                    if (at.Year > 2000) { DateTime local = at.ToLocalTime(); if (local > r.LastPlayed) r.LastPlayed = local; }
                }
            }
            return new List<Library.Imported>(songs.Values);
        }

        private static Dictionary<string, List<Library.Imported>> ReadSpotifyPlaylists(string folder)
        {
            Dictionary<string, List<Library.Imported>> lists = new Dictionary<string, List<Library.Imported>>();
            JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (string f in Directory.GetFiles(folder, "Playlist*.json", SearchOption.AllDirectories))
            {
                Dictionary<string, object> root = js.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                object pl;
                if (root == null || !root.TryGetValue("playlists", out pl) || !(pl is object[])) continue;
                foreach (object o in (object[])pl)
                {
                    Dictionary<string, object> p = o as Dictionary<string, object>;
                    if (p == null) continue;
                    string name = S(p, "name");
                    object items;
                    if (string.IsNullOrEmpty(name) || !p.TryGetValue("items", out items) || !(items is object[])) continue;
                    List<Library.Imported> songs = new List<Library.Imported>();
                    foreach (object io in (object[])items)
                    {
                        Dictionary<string, object> it = io as Dictionary<string, object>;
                        object tr;
                        if (it == null || !it.TryGetValue("track", out tr) || !(tr is Dictionary<string, object>)) continue;
                        Dictionary<string, object> t = (Dictionary<string, object>)tr;
                        songs.Add(new Library.Imported { Title = S(t, "trackName"), Artist = S(t, "artistName"), Album = S(t, "albumName") });
                    }
                    if (songs.Count > 0) lists[name] = songs;
                }
            }
            return lists;
        }

        private static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as string : null; }

    }
}
