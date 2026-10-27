using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace XvrPlayer
{
    // Spotify's official Web API, with the user's own Client ID (Settings → Library → Spotify): Muxevour's own
    // buttons (play, pause, skip, previous, seeking) and, later, the user's playlists, liked songs and search.
    // Signing in is Spotify's own page with PKCE (the way for desktop apps: no secret in Muxevour); the page comes
    // back to Muxevour on this PC (127.0.0.1:5589). The tokens are kept in Muxevour's data, locked to this Windows user.
    internal static class SpotifyWeb
    {
        public const string Redirect = "http://127.0.0.1:5589/callback";
        private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing playlist-read-private playlist-read-collaborative user-library-read user-library-modify user-read-recently-played user-top-read";

        public static string ClientId { get { return Settings.Get("spotifyclientid", "").Trim(); } }
        private static string TokenFile { get { return Path.Combine(SpotifyConnect.Folder, "web.bin"); } }

        private static string access, refresh;
        private static DateTime expires;
        private static readonly object gate = new object();
        public static string Problem;

        public static bool Connected { get { Load(); return refresh != null; } }

        // ---------- signing in ----------

        public static void SignIn(Action<string> done)
        {
            if (ClientId.Length == 0) { done("Set your Client ID first."); return; }
            string verifier = Base64Url(RandomBytes(48)), state = Base64Url(RandomBytes(12));
            string challenge;
            using (SHA256 sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string url = "https://accounts.spotify.com/authorize?response_type=code&client_id=" + Uri.EscapeDataString(ClientId)
                + "&scope=" + Uri.EscapeDataString(Scopes) + "&redirect_uri=" + Uri.EscapeDataString(Redirect)
                + "&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state;
            Thread t = new Thread(delegate ()
            {
                string result;
                try { result = WaitForCode(url, state, verifier); }
                catch (Exception ex) { result = ex.Message; Log.Error("spotify web sign-in", ex); }
                done(result);
            });
            t.IsBackground = true;
            t.Start();
        }

        // the page comes back to this PC with the code (a tiny listener, only on 127.0.0.1, only for this). It
        // keeps listening until Spotify's answer itself arrives: browsers often connect early or ask for a
        // favicon first, and those mustn't end it
        private static string WaitForCode(string url, string state, string verifier)
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 5589);
            try { l.Start(); }
            catch (SocketException) { return "Another sign-in is still waiting (or something else uses port 5589): close that tab, wait a moment and try again."; }
            try
            {
                System.Diagnostics.Process.Start(url);
                DateTime until = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < until)
                {
                    IAsyncResult ar = l.BeginAcceptTcpClient(null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(until - DateTime.UtcNow)) break;
                    using (TcpClient c = l.EndAcceptTcpClient(ar))
                    using (NetworkStream ns = c.GetStream())
                    {
                        ns.ReadTimeout = 3000;
                        string first;
                        try { first = new StreamReader(ns, Encoding.ASCII).ReadLine() ?? ""; } catch { continue; }   // (an early, empty connection)
                        string code = Query(first, "code"), back = Query(first, "state"), error = Query(first, "error");
                        if (code == null && error == null) { Reply(ns, 404, "Not here."); continue; }   // (a favicon and the like)
                        string result, page;
                        if (error != null) { result = "Spotify said: " + error; page = "Muxevour wasn't connected (" + error + "). You can close this tab."; }
                        else if (back != state) { result = "The sign-in didn't come back right; try again."; page = "Something went wrong. You can close this tab and try again in Muxevour."; }
                        else
                        {
                            result = Exchange("grant_type=authorization_code&code=" + Uri.EscapeDataString(code) + "&redirect_uri=" + Uri.EscapeDataString(Redirect) + "&client_id=" + Uri.EscapeDataString(ClientId) + "&code_verifier=" + verifier);
                            page = result == null ? "Muxevour is connected to Spotify. You can close this tab." : "Muxevour couldn't connect: " + result;
                        }
                        Reply(ns, 200, page);
                        return result;
                    }
                }
                return "The sign-in took too long; try again.";
            }
            finally { l.Stop(); }
        }

        private static void Reply(NetworkStream ns, int status, string text)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes("<html><body style=\"font-family:Segoe UI;background:#16161c;color:#eee;display:flex;align-items:center;justify-content:center;height:90vh\"><h2>" + WebUtility.HtmlEncode(text) + "</h2></body></html>");
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                ns.Write(head, 0, head.Length);
                ns.Write(body, 0, body.Length);
            }
            catch { }
        }
        private static string Query(string requestLine, string key)
        {
            int q = requestLine.IndexOf('?');
            if (q < 0) return null;
            int end = requestLine.IndexOf(' ', q);
            string qs = requestLine.Substring(q + 1, (end < 0 ? requestLine.Length : end) - q - 1);
            foreach (string kv in qs.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0 && kv.Substring(0, eq) == key) return Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' '));
            }
            return null;
        }

        // the code (or the refresh token) for an access token; null when it worked
        private static string Exchange(string form)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    string json = wc.UploadString("https://accounts.spotify.com/api/token", "POST", form);
                    Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    lock (gate)
                    {
                        access = d["access_token"] as string;
                        if (d.ContainsKey("refresh_token") && d["refresh_token"] is string) refresh = (string)d["refresh_token"];
                        expires = DateTime.UtcNow.AddSeconds(Convert.ToDouble(d["expires_in"]) - 60);
                        Save();
                    }
                    return null;
                }
            }
            catch (WebException ex) { return Body(ex) ?? ex.Message; }
            catch (Exception ex) { return ex.Message; }
        }

        public static void SignOut() { lock (gate) { access = refresh = null; try { File.Delete(TokenFile); } catch { } } }

        // ---------- the tokens, kept locked to this Windows user ----------

        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

        private static byte[] Crypt(byte[] data, bool protect)
        {
            Blob i = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) }, o;
            try
            {
                Marshal.Copy(data, 0, i.Data, data.Length);
                bool ok = protect ? CryptProtectData(ref i, "Muxevour Spotify", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out o) : CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out o);
                if (!ok) return null;
                byte[] r = new byte[o.Size];
                Marshal.Copy(o.Data, r, 0, o.Size);
                LocalFree(o.Data);
                return r;
            }
            finally { Marshal.FreeHGlobal(i.Data); }
        }

        private static bool loaded;
        private static void Load()
        {
            lock (gate)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    if (!File.Exists(TokenFile)) return;
                    byte[] b = Crypt(File.ReadAllBytes(TokenFile), false);
                    if (b == null) return;
                    string[] p = Encoding.UTF8.GetString(b).Split('\n');
                    if (p.Length >= 1 && p[0].Length > 0) refresh = p[0];
                }
                catch (Exception ex) { Log.Error("spotify web tokens", ex); }
            }
        }

        private static void Save()
        {
            try { if (refresh != null) File.WriteAllBytes(TokenFile, Crypt(Encoding.UTF8.GetBytes(refresh), true)); }
            catch (Exception ex) { Log.Error("save spotify web tokens", ex); }
        }

        private static string Token()
        {
            Load();
            lock (gate) if (access != null && DateTime.UtcNow < expires) return access;
            if (refresh == null) return null;
            string why = Exchange("grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refresh) + "&client_id=" + Uri.EscapeDataString(ClientId));
            if (why != null) { Problem = "Spotify controls: " + why; Log.Write("spotify web refresh: " + why); return null; }
            lock (gate) return access;
        }

        // ---------- asking ----------

        private static string Body(WebException ex)
        {
            try { using (StreamReader r = new StreamReader(ex.Response.GetResponseStream())) return r.ReadToEnd(); } catch { return null; }
        }

        // one call (on the calling thread); the answer's text, "" for none, null when it failed
        public static DateTime AskedAt = DateTime.MinValue;   // (when Muxevour last asked Spotify to play, skip, move...)
        public static string Call(string method, string path, string json = null)
        {
            if (method != "GET" && path.StartsWith("me/player") && !path.StartsWith("me/player/pause")) AskedAt = DateTime.UtcNow;
            if (method != "GET" && path.StartsWith("me/player")) Log.Write("spotify web " + method + " " + (path.Length > 60 ? path.Substring(0, 60) : path) + " (" + new System.Diagnostics.StackTrace(1, false).GetFrame(0).GetMethod().Name + ")");
            string tok = Token();
            if (tok == null) return null;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                using (WebClient wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + tok;
                    if (method == "GET") return wc.DownloadString("https://api.spotify.com/v1/" + path);
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    return wc.UploadString("https://api.spotify.com/v1/" + path, method, json ?? "");
                }
            }
            catch (WebException ex)
            {
                string body = Body(ex);
                Log.Write("spotify web " + method + " " + path + ": " + ex.Message + " " + (body != null && body.Length > 200 ? body.Substring(0, 200) : body));
                return null;
            }
        }

        // the same, off the window's thread
        public static void Do(string method, string path, string json = null)
        {
            if (!Connected) return;
            ThreadPool.QueueUserWorkItem(delegate { string r = Call(method, path, json); if (r == null && path.StartsWith("me/player/") && path != "me/player/devices") { string id = XvrDevice(); if (id != null) Call(method, path + (path.Contains("?") ? "&" : "?") + "device_id=" + id, json); } });
        }

        // the account's Spotify Connect devices: id, name, kind, active ("1") - this PC's Muxevour among them
        public static List<string[]> Devices()
        {
            List<string[]> l = new List<string[]>();
            string json = Call("GET", "me/player/devices");
            if (string.IsNullOrEmpty(json)) return l;
            try
            {
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                foreach (object o in (System.Collections.ArrayList)d["devices"])
                {
                    Dictionary<string, object> dev = (Dictionary<string, object>)o;
                    l.Add(new[] { dev["id"] as string, dev["name"] as string, dev["type"] as string, true.Equals(dev["is_active"]) ? "1" : "0" });
                }
            }
            catch (Exception ex) { Log.Error("spotify devices", ex); }
            return l;
        }

        // what's playing moves to another device (and goes on there)
        public static void MoveTo(string id) { if (Connected) ThreadPool.QueueUserWorkItem(delegate { Call("PUT", "me/player", "{\"device_ids\":[\"" + id + "\"],\"play\":true}"); }); }

        // Muxevour's own speaker in the account's list (by its name)
        public static string XvrDevice()
        {
            string json = Call("GET", "me/player/devices");
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                foreach (object o in (System.Collections.ArrayList)d["devices"])
                {
                    Dictionary<string, object> dev = (Dictionary<string, object>)o;
                    if (string.Equals(dev["name"] as string, Settings.Get("spotifyname", "Muxevour"), StringComparison.OrdinalIgnoreCase)) return dev["id"] as string;
                }
            }
            catch (Exception ex) { Log.Error("spotify devices", ex); }
            return null;
        }

        // ---------- the buttons ----------

        public static void Next() { Do("POST", "me/player/next"); }
        public static void Previous() { Do("POST", "me/player/previous"); }
        public static void Pause() { Do("PUT", "me/player/pause"); }
        public static void Resume() { Do("PUT", "me/player/play"); }
        public static void Seek(double seconds) { Do("PUT", "me/player/seek?position_ms=" + (long)(Math.Max(0, seconds) * 1000)); }

        // songs played on Muxevour's speaker, from the one picked (up to 100 at once: Spotify plays them in order)
        public static void PlayUris(IList<string> uris, int start)
        {
            if (!Connected || uris.Count == 0) return;
            List<string> part = new List<string>();
            for (int i = Math.Max(0, start); i < uris.Count && part.Count < 100; i++) part.Add("\"" + uris[i] + "\"");
            string body = "{\"uris\":[" + string.Join(",", part.ToArray()) + "]}";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string id = XvrDevice();
                for (int i = 0; i < 6 && id == null; i++) { Thread.Sleep(1000); id = XvrDevice(); }
                if (id == null) { Log.Write("spotify: the Muxevour speaker isn't in the account's list"); return; }
                Call("PUT", "me/player/play?device_id=" + id, body);
            });
        }

        // an album (or playlist) played whole on Muxevour's speaker
        public static void PlayContext(string uri)
        {
            if (!Connected || string.IsNullOrEmpty(uri)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string id = XvrDevice();
                for (int i = 0; i < 6 && id == null; i++) { Thread.Sleep(1000); id = XvrDevice(); }
                if (id != null) Call("PUT", "me/player/play?device_id=" + id, "{\"context_uri\":\"" + uri + "\"}");
            });
        }

        // a song added to Spotify's queue
        public static void Queue(string uri) { Do("POST", "me/player/queue?uri=" + Uri.EscapeDataString(uri)); }

        // ---------- liked songs (the heart) ----------

        private static HashSet<string> liked;
        private const string LikedList = "Liked Songs";   // (the playlist the library's reading made, in memory)

        public static bool IsLiked(string path)
        {
            if (liked == null)
            {
                liked = new HashSet<string>();
                foreach (string l in Playlists.Paths(LikedList)) if (l.StartsWith("spotify:")) liked.Add(l);
            }
            return liked.Contains(path);
        }

        // (after a sync: read again from the new Liked Songs, in the same set, so a like being sent isn't upset)
        public static void ForgetLiked()
        {
            if (liked == null) return;
            lock (liked)
            {
                liked.Clear();
                foreach (string l in Playlists.Paths(LikedList)) if (l.StartsWith("spotify:")) liked.Add(l);
            }
        }

        // liked or not on Spotify (shown at once; the Liked Songs playlist follows). Quick clicks don't race: one
        // request per song at a time, and when it's back the last wish is sent if Spotify doesn't have it yet
        private static readonly Dictionary<string, bool> sent = new Dictionary<string, bool>(), busy = new Dictionary<string, bool>();
        public static void Like(Track t, bool on, Action<string> failed)
        {
            IsLiked(t.Path);
            lock (liked) { if (on) liked.Add(t.Path); else liked.Remove(t.Path); if (busy.ContainsKey(t.Path)) return; busy[t.Path] = true; }
            ThreadPool.QueueUserWorkItem(delegate { Settle(t, failed); });
        }

        private static void Settle(Track t, Action<string> failed)
        {
            string id = t.Path.Substring(t.Path.LastIndexOf(':') + 1);
            while (true)
            {
                bool want;
                lock (liked)
                {
                    want = liked.Contains(t.Path);
                    bool was;
                    if (sent.TryGetValue(t.Path, out was) && was == want) { busy.Remove(t.Path); return; }
                }
                // (Spotify's 2026 library address: songs by their URI; the older me/tracks is refused now)
                string r = Call(want ? "PUT" : "DELETE", "me/library?uris=" + Uri.EscapeDataString("spotify:track:" + id));
                if (r == null)
                {
                    lock (liked) { if (want) liked.Remove(t.Path); else liked.Add(t.Path); busy.Remove(t.Path); }
                    if (failed != null) failed("Spotify didn''t take it.");
                    return;
                }
                lock (liked) sent[t.Path] = want;
                // (the Liked Songs playlist follows, newest first, in memory)
                List<string> paths = Playlists.Paths(LikedList);
                paths.RemoveAll(x => x == t.Path);
                if (want) paths.Insert(0, t.Path);
                Playlists.WritePaths(LikedList, paths);
            }
        }
        // what's playing moves to Muxevour (from the phone, the PC app...) and goes on
        public static void PlayHere()
        {
            if (!Connected) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string id = null;
                for (int i = 0; i < 10 && id == null; i++) { id = XvrDevice(); if (id == null) Thread.Sleep(1500); }
                if (id != null) Call("PUT", "me/player", "{\"device_ids\":[\"" + id + "\"],\"play\":true}");
                else Log.Write("spotify: the Muxevour speaker isn't in the account's list yet");
            });
        }

        private static byte[] RandomBytes(int n) { byte[] b = new byte[n]; using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        private static string Base64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    }
}
