using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WF = System.Windows.Forms;

namespace XvrPlayer
{
    // The Spotify plugin for Muxevour: Spotify as a music source (through librespot, an unofficial open-source
    // Spotify Connect speaker - not made, supported or approved by Spotify), and the import of Spotify's listening
    // history. Muxevour itself knows nothing of Spotify: everything that names it lives here.
    internal sealed class SpotifyPlugin : IPlugin
    {
        private static readonly SpotifySource source = new SpotifySource();
        public string Name { get { return "Spotify"; } }
        public string About { get { return "Spotify through librespot (unofficial): your liked songs, albums and playlists through Muxevour's sound. Settings → Library → Mode."; } }

        // (librespot runs Muxevour.exe for each event, with no arguments: written down for the open Muxevour)
        public bool HandleStart(string[] args) { return args.Length == 0 && SpotifyConnect.HandleEvent(); }

        public Action<Window> Settings { get { return null; } }

        public IList<KeyValuePair<string, Action<MainWindow>>> Imports
        {
            get { return new[] { new KeyValuePair<string, Action<MainWindow>>("Spotify", SpotifyImport.Run) }; }
        }

        public IMusicSource Source { get { return source; } }
    }

    internal sealed class SpotifySource : IMusicSource
    {
        private SpotifyConnect conn;
        private MainWindow app;
        public event Action Changed;

        public string Id { get { return "spotify"; } }
        public string Name { get { return "Spotify"; } }
        public bool Owns(string path) { return path != null && path.StartsWith("spotify:"); }

        // ---- the sound ----
        public string LivePath { get { return SpotifyConnect.LivePath; } }
        public NativeReader OpenReader() { return new SpotifyReader(); }
        public bool Running { get { return conn != null && conn.Running; } }
        public double Position { get { return conn != null ? conn.Position : 0; } }
        public double Length { get { return conn != null ? conn.Length : 0; } }

        // (the waiting sound is dropped and the bar moves at once; Spotify seeks meanwhile)
        public void Seek(double seconds)
        {
            if (conn != null) { conn.Flush(); conn.SeekShown(seconds); }
            SpotifyWeb.Seek(seconds);
        }

        // ---- what plays ----
        public Track Now { get { return conn != null ? conn.Now() : null; } }
        public bool Playing { get { return conn != null && conn.Playing; } }

        // ---- the controls ----
        public void Start(MainWindow app)
        {
            this.app = app;
            Speaker();
        }

        // librespot (re)started as Muxevour's speaker
        private void Speaker()
        {
            if (conn != null) conn.Dispose();
            conn = new SpotifyConnect(app.Dispatcher);
            conn.Changed += delegate { if (Changed != null) Changed(); };
            conn.Start();
        }

        public void Stop() { if (conn != null) conn.Dispose(); }

        // (without librespot nothing can play: said once, plainly, instead of asking Spotify for a speaker that
        // isn't there over and over)
        public bool Ready(MainWindow app)
        {
            if (SpotifyConnect.Exe != null) return true;
            app.Dispatcher.BeginInvoke((Action)delegate
            {
                Notice.Show(app, "Spotify needs librespot first", "Muxevour plays Spotify through librespot, which isn't set up on this PC yet. Settings → Library gets it for you (Get), or lets you choose a librespot.exe you have.", new[] { "Open Settings", "Close" }, delegate (int k) { if (k == 0) SettingsWindow.Open(app, "Library"); }, false);
            });
            return false;
        }

        public void Play(IList<string> paths, int at) { SpotifyWeb.PlayUris(new List<string>(paths), at); }

        // with the Client ID connected, Spotify itself pauses and goes on (nothing on Muxevour yet: what plays on the
        // account moves here); without it, the stream is held or let go and Spotify goes on when Muxevour does
        public void Toggle(Engine engine)
        {
            if (SpotifyWeb.Connected)
            {
                if (engine.Current != LivePath) engine.Play(LivePath);
                // (the sound stops or goes on at once; Spotify follows a moment later)
                if (conn == null || conn.TrackId == null) SpotifyWeb.PlayHere();
                else if (conn.Playing) { engine.Pause(); conn.Held(false); SpotifyWeb.Pause(); }
                else { if (engine.State == PlayState.Paused) engine.Resume(); conn.Held(true); SpotifyWeb.Resume(); }
                return;
            }
            if (engine.Current != LivePath) engine.Play(LivePath);
            else if (engine.State == PlayState.Playing) engine.Pause();
            else engine.Resume();
        }

        public void Next() { SpotifyWeb.Next(); }
        public void Previous() { SpotifyWeb.Previous(); }
        public void Flush() { if (conn != null) conn.Flush(); }
        public void Queue(string path) { SpotifyWeb.Queue(path); }

        // ---- the library ----
        public void Load(Library lib, Action done)
        {
            if (!SpotifyWeb.Connected) return;
            SpotifySync.Start(lib, done);
        }

        public SourceFound Search(string q, Action ready)
        {
            SpotifySync.Found f = SpotifySync.Search(q, ready);
            if (f == null) return null;
            SourceFound r = new SourceFound();
            r.Songs.AddRange(f.Songs);
            foreach (string[] al in f.Albums)
            {
                string[] a = al;
                string coverKey = "spotify:album:" + a[2];
                if (a[3] != null) SpotifyConnect.RememberCover(coverKey, a[3]);
                r.Albums.Add(new KeyValuePair<string[], Action>(new[] { a[0], a[1], coverKey }, delegate { Flush(); SpotifyWeb.PlayContext(a[2]); }));
            }
            return r;
        }

        // a song's cover at Spotify's size (kept in Muxevour's data, spotify\covers, named after the picture so an
        // album's songs share one)
        public System.Drawing.Bitmap Cover(string path, bool big)
        {
            string url = SpotifyConnect.CoverFor(path);
            if (url == null) return null;
            string file = Path.Combine(SpotifyConnect.Folder, "covers", url.Substring(url.LastIndexOf('/') + 1) + ".jpg");
            try
            {
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
                    using (System.Net.WebClient wc = new System.Net.WebClient()) wc.DownloadFile(url, file);
                }
                using (System.Drawing.Image img = System.Drawing.Image.FromFile(file)) return new System.Drawing.Bitmap(img);
            }
            catch (Exception ex) { Log.Error("spotify cover", ex); return null; }
        }

        public string Quality(Track t) { return "Spotify · Very High"; }   // (after Spotify's mark: 320 kbps, as Spotify names it)

        // ---- Spotify's design rules for its content ----
        // (Spotify's own mark, unaltered: the full logo where there's room, else the icon)
        public double Mark(DrawingContext dc, double x, double y, double height, Color text, out string line)
        {
            if (height >= 30) { line = "Very High"; return SpotifyBrand.Logo(dc, x, y + height * 0.12, height * 0.76, text) + height / 3; }
            line = null;
            return SpotifyBrand.Icon(dc, x, y, height, text) + height / 3;
        }

        public string LinkText { get { return SpotifyBrand.LinkText; } }
        public void Open(Track t) { SpotifyBrand.Open(t); }

        public bool CanLike(Track t) { return SpotifyWeb.Connected; }
        public bool IsLiked(Track t) { return SpotifyWeb.IsLiked(t.Path); }
        public void DrawLike(DrawingContext dc, Rect r, bool liked, bool hot) { SpotifyBrand.Like(dc, r, liked, hot); }

        public void Like(MainWindow mw, Track tr)
        {
            bool want = !SpotifyWeb.IsLiked(tr.Path);
            SpotifyBrand.Said(mw, want);
            SpotifyWeb.Like(tr, want, delegate (string why)
            {
                mw.Dispatcher.BeginInvoke((Action)delegate
                {
                    mw.RedrawAll();
                    // (usually the connection from before liking came in: connected again right here)
                    Notice.Show(mw, "The song wasn't liked", "Spotify didn't take it. Muxevour was most likely connected before liking came in: connect it again and Spotify asks you to allow liking.", new[] { "Connect again", "Close" }, delegate (int k)
                    {
                        if (k != 0) return;
                        SpotifyWeb.SignOut();
                        SpotifyWeb.SignIn(delegate (string err) { mw.Dispatcher.BeginInvoke((Action)delegate { if (err != null) Notice.Show(mw, "Spotify controls weren't connected", err, new[] { "Close" }, null, false); mw.RedrawAll(); }); });
                    }, false);
                });
            });
        }

        // (Spotify's artwork is never blurred or moved behind things: only its colours are)
        public Color? Background(Track t, System.Drawing.Bitmap cover) { return SpotifyBrand.Background(cover); }

        // the account's Spotify Connect devices, as Spotify's own device list - picking one moves what's playing
        // there (Muxevour's speaker is "this PC")
        public bool HasDevices { get { return true; } }
        public void DeviceMenu(MainWindow app)
        {
            if (!SpotifyWeb.Connected) { Notice.Show(app, "Spotify devices", "Connect Muxevour's Spotify controls first (Settings → Library).", new[] { "Close" }, null, false); return; }
            System.Drawing.Point at = WF.Cursor.Position;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string[]> devs = SpotifyWeb.Devices();
                app.Dispatcher.BeginInvoke((Action)delegate
                {
                    WF.ContextMenuStrip m = app.Menu();
                    m.Items.Add(new WF.ToolStripMenuItem("Spotify Connect") { Enabled = false });
                    m.Items.Add(new WF.ToolStripSeparator());
                    string mine = XvrPlayer.Settings.Get("spotifyname", "Muxevour");
                    if (devs.Count == 0) m.Items.Add(new WF.ToolStripMenuItem("No devices found (open Spotify on one)") { Enabled = false });
                    foreach (string[] d in devs)
                    {
                        string id = d[0];
                        bool here = string.Equals(d[1], mine, StringComparison.OrdinalIgnoreCase);
                        string kind = d[2] == "Computer" ? "computer" : d[2] == "Smartphone" ? "phone" : d[2] == "Speaker" ? "speaker" : d[2] == "TV" ? "TV" : (d[2] ?? "").ToLowerInvariant();
                        WF.ToolStripMenuItem it = new WF.ToolStripMenuItem((here ? d[1] + "  (this PC, through Muxevour)" : d[1]) + (kind.Length > 0 && !here ? "  ·  " + kind : ""), null, delegate { if (id != null) { SpotifyWeb.MoveTo(id); if (here && app.Engine.Current != LivePath) app.Engine.Play(LivePath); } });
                        it.Checked = d[3] == "1";
                        m.Items.Add(it);
                    }
                    m.Show(at);
                });
            });
        }

        // (pausing, skipping and seeking on Spotify answer quickly; the stream has its own short ring before it)
        public int MaxBufferMs { get { return 200; } }

        // ---- Settings ----
        public string SwitchNote { get { return "Muxevour restarts and plays Spotify (through librespot) instead of your music files. Your local library stays as it is, ready for when you switch back."; } }

        private const string Agreement = "librespot is an unofficial, open-source Spotify Connect speaker. It isn't made, supported or approved by Spotify, and using it may go against Spotify's terms of use.\n\nBy going on you agree that:\n- you use it only for yourself, with your own Spotify Premium account;\n- you won't use it to save, record or share Spotify's music;\n- you use it at your own risk (Spotify could limit or close an account that uses unofficial players), and Exevour and Muxevour aren't responsible for that.";
        private bool downloading;

        public void LibraryRows(ISettingsRows r)
        {
            Window window = r.Window;
            string exe = SpotifyConnect.Exe, speaker = XvrPlayer.Settings.Get("spotifyname", "Muxevour");
            r.Heading("Spotify");
            r.Info(exe == null ? "Set up librespot to play Spotify here: Muxevour then shows up as a speaker called \"" + speaker + "\" in the Spotify app (phone or PC). Spotify Premium is needed."
                : conn != null && conn.Problem != null ? conn.Problem
                : conn != null && conn.User != null ? "Signed in. Pick \"" + speaker + "\" as the speaker in the Spotify app and play: the sound comes through Muxevour (its equalizer, volume and output)."
                : "Ready. Pick \"" + speaker + "\" as the speaker in the Spotify app.");
            if (exe == null || downloading) r.Button("Get librespot", downloading ? "Downloading..." : "Downloads it for you from its own page (unofficial; you agree to its terms first)", downloading ? "..." : "Get", delegate
            {
                if (downloading) return;
                Notice.Show(window, "librespot - please read", Agreement, new[] { "I agree", "Cancel" }, delegate (int k)
                {
                    if (k != 0) return;
                    Agreed();
                    downloading = true;
                    r.Redraw();
                    SpotifyConnect.Download(delegate (string why)
                    {
                        r.App.Dispatcher.BeginInvoke((Action)delegate
                        {
                            downloading = false;
                            if (why != null) Notice.Show(window, "librespot couldn't be downloaded", why + "\n\nYou can also choose your own librespot.exe.", new[] { "Close" }, null, false);
                            else if (app != null) Speaker();
                            r.Redraw();
                        });
                    });
                }, false);
            });
            r.Button(exe == null ? "Your own librespot" : "librespot", exe == null ? "Or choose a librespot.exe you have" : "Set up (" + Path.GetDirectoryName(exe) + ")", exe == null ? "Choose..." : "Replace...", delegate
            {
                Notice.Show(window, "librespot - please read", Agreement, new[] { "I agree", "Cancel" }, delegate (int k) { if (k == 0) ChooseLibrespot(r); }, false);
            });
            if (SpotifyConnect.SignedIn) r.Button("Sign out", "Forget the Spotify sign-in on this PC; the sign-in page opens again next time", "Sign out", delegate
            {
                Notice.Show(window, "Sign out of Spotify?", "Muxevour restarts and the Spotify sign-in page opens.", new[] { "Sign out", "Cancel" }, delegate (int k)
                {
                    if (k != 0) return;
                    Stop();
                    SpotifyConnect.SignOut();
                    Streaming.SwitchTo(this, r.App);
                }, false);
            });
            r.Heading("Muxevour's Spotify controls");
            r.Info("With your own free Client ID (developer.spotify.com, redirect URI " + SpotifyWeb.Redirect + "), Muxevour's buttons skip, go back, seek and pause on Spotify itself, and play moves what's playing to Muxevour.");
            if (!SpotifyWeb.Connected) r.Button("Set up (guided)", "Four steps on one card: Spotify's page opens, Muxevour copies what to fill in and picks up your Client ID by itself", "Start", delegate { SpotifySetupWindow.Open(r.App, window, r.Redraw); });
            r.Button("Client ID", SpotifyWeb.ClientId.Length == 0 ? "Not set yet" : SpotifyWeb.ClientId, SpotifyWeb.ClientId.Length == 0 ? "Set..." : "Change...", delegate
            {
                string id = Prompt.Ask(null, "Your Spotify Client ID", SpotifyWeb.ClientId);
                if (id == null) return;
                XvrPlayer.Settings.Set("spotifyclientid", id.Trim());
                XvrPlayer.Settings.Save();
                SpotifyWeb.SignOut();
                r.Redraw();
            });
            if (SpotifyWeb.ClientId.Length > 0) r.Button(SpotifyWeb.Connected ? "Connected" : "Connect", SpotifyWeb.Connected ? "Muxevour's buttons work on Spotify" : (SpotifyWeb.Problem ?? "Spotify's sign-in page opens; approve Muxevour"), SpotifyWeb.Connected ? "Disconnect" : "Connect", delegate
            {
                if (SpotifyWeb.Connected) { SpotifyWeb.SignOut(); r.Redraw(); return; }
                SpotifyWeb.SignIn(delegate (string why) { r.App.Dispatcher.BeginInvoke((Action)delegate { SpotifyWeb.Problem = why; r.Redraw(); if (why != null) Notice.Show(window, "Spotify controls weren't connected", why, new[] { "Close" }, null, false); else r.App.RefreshSource(); }); });
            });
            if (SpotifyWeb.Connected)
            {
                r.Heading("Your Spotify library");
                r.Button("Refresh", SpotifySync.Running ? SpotifySync.Status : SpotifySync.Status.Length > 0 ? SpotifySync.Status : "Your liked songs, saved albums and playlists, read from Spotify each time Muxevour opens", SpotifySync.Running ? "Reading..." : "Refresh", delegate { if (!SpotifySync.Running) { r.App.RefreshSource(); r.Redraw(); } });
                r.Info("Your liked songs, saved albums and your own playlists show in Your Library and on Home, as your files do in Local mode (Spotify's own mixes can't be read by other apps). Click one to play it on Muxevour's speaker.");
            }
            r.Heading("Sound");
            r.Toggle("Spotify's volume levelling", "Spotify's own loudness evening between songs (takes effect the next time Muxevour opens)", "spotifynormalise", "on");
            r.Info("What Spotify plays here is 320 kbps (Spotify's \"Very High\"). Without the Client ID connected, skipping and seeking are done in the Spotify app, and Muxevour's play button holds and lets go of the stream.");
        }

        private static void Agreed() { XvrPlayer.Settings.Set("spotifyagreed", DateTime.Now.ToString("yyyy-MM-dd")); XvrPlayer.Settings.Save(); }

        private void ChooseLibrespot(ISettingsRows r)
        {
            Agreed();
            using (WF.OpenFileDialog d = new WF.OpenFileDialog { Filter = "librespot|librespot.exe", Title = "Your librespot.exe" })
            {
                if (d.ShowDialog() != WF.DialogResult.OK) return;
                try
                {
                    if (conn != null) conn.Dispose();
                    File.Copy(d.FileName, Path.Combine(SpotifyConnect.Folder, "librespot.exe"), true);
                    if (app != null) Speaker();
                }
                catch (Exception ex) { Notice.Show(r.Window, "librespot couldn't be set up", ex.Message, new[] { "Close" }, null, false); }
            }
            r.Redraw();
        }

        public void PlaybackRows(ISettingsRows r)
        {
            r.Heading("Volume levelling");
            r.Toggle("Spotify's volume levelling", "Spotify's own loudness evening between songs (takes effect the next time Muxevour opens)", "spotifynormalise", "on");
            r.Slider("Pre-amp", "Added on top of Spotify's levelling", "preamp", -24, 24, 0, delegate (int v) { return (v > 0 ? "+" : "") + (v / 2.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " dB"; });
            r.Toggle("Prevent clipping", "Turns the gain down when the sound would go over full scale", "clipguard", "on");
            r.Heading("Between songs");
            r.Segments("Crossfade", "Off with Spotify on Muxevour's speaker", "crossfade", "0", new string[] { "Off", "2 s", "4 s", "6 s", "8 s", "12 s" }, new string[] { "0", "2", "4", "6", "8", "12" }, true);
            r.Info("Spotify's transitions (Crossfade, Automix, Mix, AI DJ) play only in Spotify's own apps, never on a speaker like Muxevour's, whatever the plan: songs follow each other as Spotify sends them.");
            r.Heading("Playing");
            r.Segments("A song counts as played after", null, "playcount", "half", new string[] { "Half (Last.fm)", "30 s", "The whole song" }, new string[] { "half", "30s", "whole" });
            r.Segments("Volume steps (keys and wheel)", null, "volstep", "5", new string[] { "2%", "5%", "10%" }, new string[] { "2", "5", "10" });
        }
    }
}
