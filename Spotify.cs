using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace XvrPlayer
{
    // Spotify through librespot (an open-source Spotify Connect speaker, not made by Spotify): Muxevour starts it
    // hidden as a speaker called "Muxevour", takes its sound straight from it (so it goes through Muxevour's equalizer,
    // volume and output, exclusive and ASIO too) and learns the song, its cover and where it is from its
    // events. librespot isn't part of Muxevour: the user gets it (Settings → Library) and signs in with their own
    // Premium account on Spotify's own page.
    internal sealed class SpotifyConnect : IDisposable
    {
        public static string Folder { get { string f = Path.Combine(Log.Folder, "spotify"); try { Directory.CreateDirectory(f); } catch { } return f; } }
        public static string EventsFile { get { return Path.Combine(Folder, "events.txt"); } }
        public const string LivePath = "spotify:live";

        // where librespot is: beside Muxevour.exe, or in Muxevour's data (where "Get librespot" puts it)
        public static string Exe
        {
            get
            {
                foreach (string p in new[] { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "librespot.exe"), Path.Combine(Folder, "librespot.exe") })
                    if (File.Exists(p)) return p;
                return null;
            }
        }

        public static bool SignedIn { get { return File.Exists(Path.Combine(Folder, "cache", "credentials.json")); } }

        // ---------- librespot's events, through Muxevour.exe itself ----------

        // librespot runs Muxevour.exe on each event, with the event in its environment: written down for
        // the Muxevour that's open, then this one ends at once (App.Main asks before anything else)
        public static bool HandleEvent()
        {
            string ev = Environment.GetEnvironmentVariable("PLAYER_EVENT");
            if (string.IsNullOrEmpty(ev)) return false;
            try
            {
                StringBuilder sb = new StringBuilder("PLAYER_EVENT=" + ev);
                foreach (string k in new[] { "TRACK_ID", "URI", "NAME", "ARTISTS", "ALBUM", "ALBUM_ARTISTS", "COVERS", "DURATION_MS", "POSITION_MS", "NUMBER", "DISC_NUMBER", "VOLUME", "USER_NAME", "SHUFFLE", "REPEAT", "ITEM_TYPE" })
                {
                    string v = Environment.GetEnvironmentVariable(k);
                    if (v != null) sb.Append('\t').Append(k).Append('=').Append(v.Replace("\r", "").Replace("\n", "\u0001").Replace("\t", " "));
                }
                sb.Append('\t').Append("AT=").Append(DateTime.UtcNow.Ticks);
                for (int tries = 0; tries < 20; tries++)
                {
                    try { using (FileStream fs = new FileStream(EventsFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { byte[] b = Encoding.UTF8.GetBytes(sb.ToString() + "\n"); fs.Write(b, 0, b.Length); } break; }
                    catch (IOException) { Thread.Sleep(10); }
                }
            }
            catch { }
            return true;
        }

        // ---------- the now playing ----------

        public string TrackId, Title, Artists, Album, AlbumArtists, CoverUrl, User;
        public double Length;          // seconds
        private double posAt;          // the position at 'posStamp' (seconds)
        private long posStamp;         // when (UTC ticks)
        public bool Playing;
        public bool Running { get { return proc != null && !proc.HasExited; } }
        public string Problem;         // why it isn't playing, for the window
        public event Action Changed;   // (on the window's thread)

        public double Position
        {
            get
            {
                double p = posAt + (Playing ? (DateTime.UtcNow.Ticks - posStamp) / 1e7 : 0);
                return Math.Max(0, Length > 0 ? Math.Min(Length, p) : p);
            }
        }

        private static readonly Dictionary<string, string> covers = new Dictionary<string, string>();   // track path -> cover URL
        private static bool coversLoaded;
        private static string CoversFile { get { return Path.Combine(Folder, "covers.tsv"); } }
        public static string CoverFor(string path)
        {
            lock (covers)
            {
                if (!coversLoaded)
                {
                    coversLoaded = true;
                    try { if (File.Exists(CoversFile)) foreach (string l in File.ReadAllLines(CoversFile, Encoding.UTF8)) { int tab = l.IndexOf('\t'); if (tab > 0) covers[l.Substring(0, tab)] = l.Substring(tab + 1); } } catch { }
                }
                string u; return covers.TryGetValue(path, out u) ? u : null;
            }
        }
        public static void RememberCover(string path, string url) { CoverFor(path); lock (covers) covers[path] = url; }
        public static void SaveCovers()
        {
            lock (covers)
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in covers) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                try { File.WriteAllText(CoversFile, sb.ToString(), new UTF8Encoding(false)); } catch (Exception ex) { Log.Error("spotify covers", ex); }
            }
        }

        // the song as a Track (its path "spotify:track:<id>": the cover and the stats go by it)
        public Track Now()
        {
            if (TrackId == null) return null;
            return new Track { Path = "spotify:track:" + TrackId, Title = Title, Artist = Artists, AlbumArtist = AlbumArtists, Album = Album, Length = Length };
        }

        // ---------- the process and its sound ----------

        private Process proc;
        private readonly byte[] ring = new byte[44100 * 4];   // a second of 16-bit stereo (against dropouts; a skip or a seek empties it, so it doesn't slow the buttons)
        private int head, count;
        private readonly object gate = new object();
        private volatile bool closing;
        private readonly System.Windows.Threading.Dispatcher ui;
        private System.Windows.Threading.DispatcherTimer poll;
        private long eventsRead;
        public static SpotifyConnect Current;

        public SpotifyConnect(System.Windows.Threading.Dispatcher ui) { this.ui = ui; Current = this; }

        // librespot cuts --onevent at spaces ("C:\Program Files\..." runs "C:\Program"), so it gets a path without
        // any: Windows' short name for Muxevour, or else a copy of Muxevour in C:\ProgramData\Muxevour (it only notes the event)
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetShortPathName(string path, StringBuilder shortPath, int size);
        private static string EventProgram()
        {
            string me = Process.GetCurrentProcess().MainModule.FileName;
            if (me.IndexOf(' ') < 0) return me;
            try { StringBuilder sb = new StringBuilder(1024); if (GetShortPathName(me, sb, sb.Capacity) > 0 && sb.ToString().IndexOf(' ') < 0) return sb.ToString(); } catch { }
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Muxevour");
                Directory.CreateDirectory(dir);
                string copy = Path.Combine(dir, "Muxevour-events.exe");
                FileInfo a = new FileInfo(me), b = new FileInfo(copy);
                if (!b.Exists || b.Length != a.Length || b.LastWriteTimeUtc != a.LastWriteTimeUtc) { File.Copy(me, copy, true); File.SetLastWriteTimeUtc(copy, a.LastWriteTimeUtc); }
                if (copy.IndexOf(' ') < 0) return copy;
            }
            catch (Exception ex) { Log.Error("librespot events program", ex); }
            Log.Write("librespot: Muxevour's path has spaces and no way round them was found; songs won't follow");
            return me;
        }
        public void Start()
        {
            string exe = Exe;
            if (exe == null) { Problem = "librespot isn't set up yet: Settings → Library → Spotify."; Fire(); return; }
            try { File.WriteAllText(EventsFile, ""); } catch { }
            eventsRead = 0;
            startedAt = DateTime.UtcNow;
            string me = EventProgram();
            string args = "--name \"" + Settings.Get("spotifyname", "Muxevour") + "\" --backend pipe --bitrate 320 --format S16"
                + " --initial-volume 100 --volume-ctrl fixed --enable-oauth --oauth-port 5588"
                + " --cache \"" + Path.Combine(Folder, "cache") + "\" --onevent \"" + me + "\"";
            if (Settings.Get("spotifynormalise", "on") == "on") args += " --enable-volume-normalisation";
            ProcessStartInfo si = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            try { proc = Process.Start(si); }
            catch (Exception ex) { Problem = "librespot couldn't start: " + ex.Message; Log.Error("librespot", ex); Fire(); return; }
            Log.Write("librespot started (" + exe + ")");
            Problem = SignedIn ? null : "Sign in to Spotify in the browser window that opened.";
            new Thread(ReadSound) { IsBackground = true, Name = "Muxevour spotify sound" }.Start();
            new Thread(ReadLog) { IsBackground = true, Name = "Muxevour spotify log" }.Start();
            poll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(80) };
            poll.Tick += delegate { ReadEvents(); if (proc != null && proc.HasExited && !closing) { poll.Stop(); Problem = "librespot stopped (code " + proc.ExitCode + ")."; Playing = false; Fire(); } };
            poll.Start();
            Fire();
        }

        // librespot's sound, kept in a short ring; when the ring is full librespot waits (so it plays at
        // Muxevour's speed, and pausing Muxevour holds it)
        private void ReadSound()
        {
            byte[] buf = new byte[8192];
            Stream s = proc.StandardOutput.BaseStream;
            try
            {
                while (!closing)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    int off = 0;
                    lock (gate)
                        while (off < n && !closing)
                        {
                            while (count == ring.Length && !closing) Monitor.Wait(gate, 200);
                            int tail = (head + count) % ring.Length, room = Math.Min(ring.Length - count, ring.Length - tail);
                            int take = Math.Min(room, n - off);
                            Buffer.BlockCopy(buf, off, ring, tail, take);
                            count += take; off += take;
                            Monitor.PulseAll(gate);
                        }
                }
            }
            catch (Exception ex) { if (!closing) Log.Error("librespot sound", ex); }
        }

        // the sound waiting in the ring thrown away (a skip or a seek: the old song mustn't play on)
        public void Flush() { lock (gate) { head = 0; count = 0; Monitor.PulseAll(gate); } }

        // Muxevour's own buttons: shown at once, Spotify told meanwhile
        public void SeekShown(double seconds) { posAt = seconds; posStamp = DateTime.UtcNow.Ticks; Fire(); }

        public void Held(bool playing) { posAt = Position; posStamp = DateTime.UtcNow.Ticks; Playing = playing; Fire(); }

        // the next stretch of sound as floats (silence when nothing comes, so the output keeps running)
        public float[] Take()
        {
            byte[] chunk;
            int got;
            lock (gate)
            {
                if (count < 4) Monitor.Wait(gate, 15);
                got = Math.Min(count, 4096 * 4) & ~3;
                chunk = new byte[got];
                for (int i = 0; i < got; )
                {
                    int part = Math.Min(got - i, ring.Length - head);
                    Buffer.BlockCopy(ring, head, chunk, i, part);
                    head = (head + part) % ring.Length; count -= part; i += part;
                }
                Monitor.PulseAll(gate);
            }
            if (got == 0) return new float[441 * 2];   // (10 ms of silence)
            float[] f = new float[got / 2];
            for (int i = 0; i < f.Length; i++) f[i] = (short)(chunk[i * 2] | chunk[i * 2 + 1] << 8) / 32768f;
            return f;
        }

        // librespot's log: the sign-in page opens by itself the first time; problems are kept for the window
        private void ReadLog()
        {
            try
            {
                string line;
                while (!closing && (line = proc.StandardError.ReadLine()) != null)
                {
                    if (line.StartsWith("Browse to: "))
                    {
                        string url = line.Substring(11).Trim();
                        Log.Write("librespot asks to sign in");
                        try { Process.Start(url); } catch { }
                        Problem = "Sign in to Spotify in the browser window that opened.";
                        Fire();
                    }
                    else if (line.Contains("Authenticated as")) { Problem = null; Log.Write("librespot signed in"); Fire(); }
                    else if (line.Contains(" ERROR ")) Log.Write("librespot: " + (line.Length > 220 ? line.Substring(0, 220) : line));
                    // (Spotify couldn't be reached as it started - the network a moment late: started again, a few times)
                    if (line.Contains("starting dealer failed") && restarts < 3)
                    {
                        restarts++;
                        Log.Write("librespot: Spotify unreachable at the start, trying again (" + restarts + ")");
                        Process old = proc;
                        ThreadPool.QueueUserWorkItem(delegate { try { old.Kill(); } catch { } Thread.Sleep(4000 * restarts); if (!closing) ui.BeginInvoke((Action)Start); });
                        return;
                    }
                }
            }
            catch { }
        }
        private int restarts;
        private DateTime startedAt = DateTime.UtcNow;

        private void ReadEvents()
        {
            try
            {
                if (!File.Exists(EventsFile)) return;
                using (FileStream fs = new FileStream(EventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < eventsRead) eventsRead = 0;
                    if (fs.Length == eventsRead) return;
                    fs.Position = eventsRead;
                    byte[] b = new byte[fs.Length - eventsRead];
                    int n = fs.Read(b, 0, b.Length);
                    string text = Encoding.UTF8.GetString(b, 0, n);
                    int last = text.LastIndexOf('\n');
                    if (last < 0) return;
                    eventsRead += Encoding.UTF8.GetByteCount(text.Substring(0, last + 1));
                    foreach (string line in text.Substring(0, last).Split('\n')) if (line.Length > 0) OnEvent(line);
                }
            }
            catch (Exception ex) { Log.Error("spotify events", ex); }
        }

        private void OnEvent(string line)
        {
            Dictionary<string, string> e = new Dictionary<string, string>();
            foreach (string part in line.Split('\t')) { int eq = part.IndexOf('='); if (eq > 0) e[part.Substring(0, eq)] = part.Substring(eq + 1); }
            string ev = Get(e, "PLAYER_EVENT");
            long at; long.TryParse(Get(e, "AT"), out at);
            double pos; bool hasPos = double.TryParse(Get(e, "POSITION_MS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pos);
            switch (ev)
            {
                case "track_changed":
                    TrackId = Get(e, "TRACK_ID");
                    Title = Get(e, "NAME");
                    Artists = Join(Get(e, "ARTISTS"));
                    AlbumArtists = Join(Get(e, "ALBUM_ARTISTS"));
                    Album = Get(e, "ALBUM");
                    double d; if (double.TryParse(Get(e, "DURATION_MS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) Length = d / 1000;
                    // (the biggest cover is listed first)
                    string cov = Get(e, "COVERS");
                    CoverUrl = string.IsNullOrEmpty(cov) ? null : cov.Split('\u0001')[0];
                    if (TrackId != null && CoverUrl != null) RememberCover("spotify:track:" + TrackId, CoverUrl);
                    Log.Write("spotify: " + Artists + " - " + Title);
                    break;
                case "playing":
                    Playing = true; if (hasPos) Place(pos / 1000, at);
                    // (Spotify carrying on by itself as Muxevour's speaker comes back - its autoplay - before anything was
                    // asked in Muxevour: paused, so Muxevour never starts making sound on its own)
                    if ((DateTime.UtcNow - startedAt).TotalSeconds < 15 && SpotifyWeb.AskedAt < startedAt && SpotifyWeb.Connected)
                    {
                        Log.Write("spotify started playing by itself as Muxevour opened: paused");
                        SpotifyWeb.Pause();
                    }
                    break;
                case "paused": Playing = false; if (hasPos) Place(pos / 1000, at); break;
                case "seeked": case "position_correction": if (hasPos) Place(pos / 1000, at); break;
                case "stopped": Playing = false; break;
                case "session_connected": User = Get(e, "USER_NAME"); Problem = null; break;
                case "session_disconnected": Playing = false; break;
            }
            Fire();
        }

        private void Place(double seconds, long at)
        {
            posAt = seconds;
            posStamp = at > 0 ? at : DateTime.UtcNow.Ticks;
        }

        private static string Get(Dictionary<string, string> e, string k) { string v; return e.TryGetValue(k, out v) ? v : null; }
        private static string Join(string many) { return string.IsNullOrEmpty(many) ? many : string.Join("; ", many.Split(new[] { '\u0001' }, StringSplitOptions.RemoveEmptyEntries)); }

        private void Fire() { if (Changed == null) return; try { ui.BeginInvoke((Action)delegate { if (Changed != null) Changed(); }); } catch { } }

        public void Dispose()
        {
            // (playing as Muxevour closes: Spotify is paused first, or it would carry on - with its next song - the
            // moment Muxevour's speaker came back at the next start)
            if (Playing && !closing && SpotifyWeb.Connected && !Settings.ReadOnly)
                try { SpotifyWeb.Call("PUT", "me/player/pause"); Log.Write("spotify paused as Muxevour closed"); } catch { }
            closing = true;
            if (poll != null) poll.Stop();
            lock (gate) Monitor.PulseAll(gate);
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            if (Current == this) Current = null;
        }

        // ---------- "Get librespot": the latest build from its own repository (never shipped with Muxevour) ----------
        public const string LibrespotRepo = "Exevour/muxevour-librespot";

        // (a thread of its own - never queued behind other waiting work - and every request with a time limit,
        // so it always ends in "done": downloaded, or why not)
        private sealed class TimedClient : System.Net.WebClient
        {
            protected override System.Net.WebRequest GetWebRequest(Uri address)
            {
                System.Net.WebRequest r = base.GetWebRequest(address);
                r.Timeout = 30000;
                System.Net.HttpWebRequest h = r as System.Net.HttpWebRequest;
                if (h != null) h.ReadWriteTimeout = 30000;
                return r;
            }
        }

        public static void Download(Action<string> done)
        {
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
                    Log.Write("get librespot: asking " + LibrespotRepo);
                    using (System.Net.WebClient wc = new TimedClient())
                    {
                        wc.Headers["User-Agent"] = "Muxevour";
                        string json = wc.DownloadString("https://api.github.com/repos/" + LibrespotRepo + "/releases/latest");
                        Dictionary<string, object> rel = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                        // (librespot.exe.gz, packed small; or a plain librespot.exe)
                        string url = null;
                        bool packed = false;
                        foreach (object o in (System.Collections.ArrayList)rel["assets"])
                        {
                            Dictionary<string, object> a = (Dictionary<string, object>)o;
                            string n = a["name"] as string;
                            if (string.Equals(n, "librespot.exe.gz", StringComparison.OrdinalIgnoreCase)) { url = a["browser_download_url"] as string; packed = true; }
                            else if (url == null && string.Equals(n, "librespot.exe", StringComparison.OrdinalIgnoreCase)) url = a["browser_download_url"] as string;
                        }
                        if (url == null) { done("The download page has no librespot yet."); return; }
                        string tmp = Path.Combine(Folder, "librespot.download"), exe = Path.Combine(Folder, "librespot.new");
                        wc.Headers["User-Agent"] = "Muxevour";
                        wc.DownloadFile(url, tmp);
                        if (packed)
                        {
                            using (FileStream i = File.OpenRead(tmp)) using (System.IO.Compression.GZipStream g = new System.IO.Compression.GZipStream(i, System.IO.Compression.CompressionMode.Decompress)) using (FileStream o = File.Create(exe)) g.CopyTo(o);
                            File.Delete(tmp);
                        }
                        else { if (File.Exists(exe)) File.Delete(exe); File.Move(tmp, exe); }
                        string to = Path.Combine(Folder, "librespot.exe");
                        if (File.Exists(to)) File.Delete(to);
                        File.Move(exe, to);
                        Log.Write("librespot downloaded (" + (rel.ContainsKey("tag_name") ? rel["tag_name"] : "?") + ")");
                        done(null);
                    }
                }
                catch (Exception ex) { Log.Error("get librespot", ex); done(ex.Message); }
            });
            th.IsBackground = true;
            th.Name = "Muxevour get librespot";
            th.Start();
        }

        // signing out: the kept sign-in removed (librespot asks again next time)
        public static void SignOut()
        {
            try { string c = Path.Combine(Folder, "cache"); if (Directory.Exists(c)) Directory.Delete(c, true); } catch (Exception ex) { Log.Error("spotify sign out", ex); }
        }
    }

    // the engine's view of the Spotify stream: an endless song at 44.1 kHz, 16-bit stereo
    internal sealed class SpotifyReader : NativeReader
    {
        private readonly SpotifyConnect source;
        public SpotifyReader()
        {
            source = SpotifyConnect.Current;
            if (source == null) throw new InvalidOperationException("Spotify isn't running");
            Rate = 44100; Channels = 2; Bits = 16; Duration = 0;
        }
        public override float[] Read() { return source.Running ? source.Take() : null; }
        public override void Seek(double seconds) { }
        public override void Dispose() { }
    }
}
