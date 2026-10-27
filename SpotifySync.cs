using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace XvrPlayer
{
    // Streaming mode's library: your Spotify liked songs, saved albums and playlists, brought into Muxevour's own
    // library (so Home, Your Library, Browse, Stats and search work as with files) - each song a Track whose
    // path is "spotify:track:<id>", each playlist an Muxevour playlist in Streaming's own folder. Plays, skips and
    // ratings stay Muxevour's own, kept across syncs.
    internal static class SpotifySync
    {
        public static volatile bool Running;
        public static string Status = "";
        private static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static readonly List<string> made = new List<string>();   // (the playlists the last reading made, in memory)

        public static void Start(Library lib, Action changed)
        {
            if (Running || !SpotifyWeb.Connected) return;
            Running = true;
            Thread t = new Thread(delegate ()
            {
                try { Run(lib, changed); }
                catch (Exception ex) { Log.Error("spotify sync", ex); Status = "The sync stopped: " + ex.Message; }
                finally { Running = false; if (changed != null) changed(); }
            });
            t.IsBackground = true;
            t.Name = "Muxevour spotify sync";
            t.Start();
        }

        private static Dictionary<string, object> Get(string path)
        {
            for (int tries = 0; tries < 3; tries++)
            {
                string s = SpotifyWeb.Call("GET", path);
                if (!string.IsNullOrEmpty(s)) return json.Deserialize<Dictionary<string, object>>(s);
                Thread.Sleep(1500 * (tries + 1));   // (busy, or too many asks at once: a moment later)
            }
            return null;
        }

        // every page of a list ("next" links), the items handed over one by one
        private static void Pages(string first, Action<Dictionary<string, object>> item, Action<int> progress = null)
        {
            string path = first;
            int n = 0;
            while (path != null)
            {
                Dictionary<string, object> page = Get(path);
                if (page == null) break;
                ArrayList items = page.ContainsKey("items") ? page["items"] as ArrayList : null;
                if (items != null) foreach (object o in items) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) item(d); n++; }
                if (progress != null) progress(n);
                string next = page.ContainsKey("next") ? page["next"] as string : null;
                path = next == null ? null : next.Replace("https://api.spotify.com/v1/", "");
            }
        }

        private static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as string : null; }
        private static Dictionary<string, object> D(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        private static int I(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v) : 0; }

        private static string Names(object artists)
        {
            ArrayList a = artists as ArrayList;
            if (a == null) return "";
            List<string> n = new List<string>();
            foreach (object o in a) { string s = S(o as Dictionary<string, object>, "name"); if (!string.IsNullOrEmpty(s)) n.Add(s); }
            return string.Join("; ", n.ToArray());
        }

        private static string Biggest(object images)
        {
            ArrayList a = images as ArrayList;
            if (a == null || a.Count == 0) return null;
            return S(a[0] as Dictionary<string, object>, "url");   // (Spotify lists the largest first)
        }

        // a Spotify track (and, for an album's own songs, its album) as an Muxevour Track
        private static Track Make(Dictionary<string, object> t, Dictionary<string, object> album, Dictionary<string, Track> all)
        {
            string id = S(t, "id");
            if (id == null || S(t, "type") == "episode") return null;
            Track have;
            if (all.TryGetValue(id, out have)) return have;
            if (album == null) album = D(t, "album");
            Track tr = new Track
            {
                Path = "spotify:track:" + id,
                Title = S(t, "name"),
                Artist = Names(t.ContainsKey("artists") ? t["artists"] : null),
                Album = S(album, "name"),
                AlbumArtist = Names(album != null && album.ContainsKey("artists") ? album["artists"] : null),
                Number = I(t, "track_number"),
                Disc = I(t, "disc_number"),
                Length = I(t, "duration_ms") / 1000.0,
                Bitrate = 320000,
                Rate = 44100,
            };
            string date = S(album, "release_date");
            int y;
            if (date != null && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out y)) tr.Year = y;
            string cover = Biggest(album != null && album.ContainsKey("images") ? album["images"] : null);
            if (cover != null) SpotifyConnect.RememberCover(tr.Path, cover);
            all[id] = tr;
            return tr;
        }

        // ---------- searching Spotify's whole catalogue ----------

        public sealed class Found { public List<Track> Songs = new List<Track>(); public List<string[]> Albums = new List<string[]>(); }   // album: name, artists, uri, cover url
        private static readonly Dictionary<string, Found> searched = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
        private static string asking;

        // what Spotify has for the words (null while it's being asked: 'ready' is called once it's in)
        public static Found Search(string q, Action ready)
        {
            q = (q ?? "").Trim();
            if (q.Length < 2 || !SpotifyWeb.Connected) return null;
            lock (searched) { Found f; if (searched.TryGetValue(q, out f)) return f; }
            asking = q;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(350);   // (only once the typing pauses)
                if (asking != q) return;
                Dictionary<string, object> d = Get("search?type=track,album&limit=6&q=" + Uri.EscapeDataString(q));
                Found f = new Found();
                Dictionary<string, Track> none = new Dictionary<string, Track>();
                ArrayList ts = d == null ? null : D(d, "tracks") != null ? D(d, "tracks")["items"] as ArrayList : null;
                if (ts != null) foreach (object o in ts) { Track t = Make(o as Dictionary<string, object>, null, none); if (t != null) f.Songs.Add(t); }
                ArrayList als = d == null ? null : D(d, "albums") != null ? D(d, "albums")["items"] as ArrayList : null;
                if (als != null) foreach (object o in als)
                {
                    Dictionary<string, object> a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    f.Albums.Add(new[] { S(a, "name"), Names(a.ContainsKey("artists") ? a["artists"] : null), S(a, "uri"), Biggest(a.ContainsKey("images") ? a["images"] : null) });
                }
                lock (searched) searched[q] = f;
                if (ready != null) ready();
            });
            return null;
        }

        private static DateTime When(Dictionary<string, object> item, string k)
        {
            DateTime d;
            return DateTime.TryParse(S(item, k), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out d) ? d.ToLocalTime() : DateTime.MinValue;
        }

        private static void Run(Library lib, Action changed)
        {
            Dictionary<string, Track> all = new Dictionary<string, Track>();
            Dictionary<string, List<Track>> lists = new Dictionary<string, List<Track>>();
            // liked songs (newest first, as Spotify lists them)
            List<Track> liked = new List<Track>();
            Pages("me/tracks?limit=50", delegate (Dictionary<string, object> it)
            {
                Track t = Make(D(it, "track"), null, all);
                if (t != null) { t.Added = When(it, "added_at"); liked.Add(t); }
            }, delegate (int n) { Status = "Liked songs: " + n + "..."; if (changed != null) changed(); });
            if (liked.Count > 0) lists["Liked Songs"] = liked;
            // saved albums, with their songs
            Pages("me/albums?limit=50", delegate (Dictionary<string, object> it)
            {
                Dictionary<string, object> al = D(it, "album");
                Dictionary<string, object> tracks = D(al, "tracks");
                ArrayList items = tracks == null ? null : tracks["items"] as ArrayList;
                if (items == null) return;
                DateTime added = When(it, "added_at");
                foreach (object o in items) { Track t = Make(o as Dictionary<string, object>, al, all); if (t != null && t.Added == DateTime.MinValue) t.Added = added; }
            }, delegate (int n) { Status = "Saved albums: " + n + "..."; if (changed != null) changed(); });
            // your playlists, each with its songs (the ones Spotify makes itself can't be read by other apps)
            List<KeyValuePair<string, string>> pls = new List<KeyValuePair<string, string>>();
            Pages("me/playlists?limit=50", delegate (Dictionary<string, object> it) { string id = S(it, "id"), name = S(it, "name"); if (id != null && !string.IsNullOrEmpty(name)) pls.Add(new KeyValuePair<string, string>(id, name)); });
            int done = 0;
            foreach (KeyValuePair<string, string> p in pls)
            {
                Status = "Playlists: " + (++done) + " of " + pls.Count + " (" + p.Value + ")...";
                if (changed != null) changed();
                List<Track> songs = new List<Track>();
                // (Spotify's newer address for a playlist's songs: each under "item"; the older "tracks" is refused now)
                Pages("playlists/" + p.Key + "/items?limit=100", delegate (Dictionary<string, object> it)
                {
                    Track t = Make(D(it, "item") ?? D(it, "track"), null, all);
                    if (t != null) { if (t.Added == DateTime.MinValue) t.Added = When(it, "added_at"); songs.Add(t); }
                });
                string name = Playlists.Clean(p.Value);
                if (name.Length == 0 || songs.Count == 0) continue;
                while (lists.ContainsKey(name)) name += " (2)";
                lists[name] = songs;
            }
            // recently played: when each was last heard
            Dictionary<string, DateTime> recent = new Dictionary<string, DateTime>();
            Dictionary<string, object> rp = Get("me/player/recently-played?limit=50");
            if (rp != null && rp["items"] is ArrayList)
                foreach (object o in (ArrayList)rp["items"])
                {
                    Dictionary<string, object> it = o as Dictionary<string, object>;
                    Track t = Make(D(it, "track"), null, all);
                    if (t == null) continue;
                    DateTime w = When(it, "played_at"), was;
                    if (!recent.TryGetValue(t.Path, out was) || w > was) recent[t.Path] = w;
                }
            foreach (Track t in all.Values) { DateTime w; if (recent.TryGetValue(t.Path, out w)) t.LastPlayed = w; if (t.Added == DateTime.MinValue) t.Added = DateTime.Now; }
            SpotifyConnect.SaveCovers();
            // into the library (Muxevour's plays kept) and the playlists (the last sync's ones replaced)
            lib.SyncStream(new List<Track>(all.Values));
            foreach (string old in made) if (!lists.ContainsKey(old)) Playlists.Delete(old);
            foreach (KeyValuePair<string, List<Track>> kv in lists) Playlists.Replace(kv.Key, kv.Value);
            made.Clear();
            made.AddRange(lists.Keys);
            // (0.12: the library is read live and kept in memory - the old sync's list of playlists goes)
            try { string old = Path.Combine(SpotifyConnect.Folder, "playlists.txt"); if (File.Exists(old)) File.Delete(old); } catch { }
            SpotifyWeb.ForgetLiked();   // (the heart reads the new Liked Songs)
            Settings.Set("spotifysynced", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            Settings.Save();
            Status = "Read: " + all.Count + " songs, " + lists.Count + " playlists.";
            Log.Write("spotify sync: " + all.Count + " songs, " + lists.Count + " playlists");
        }
    }
}
