using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XvrPlayer
{
    // Spotify's design rules for apps that use its content (developer.spotify.com/documentation/design), kept
    // in one place: its own logo and icon, unaltered, in white or black (the full logo where there's room, the
    // icon where there isn't); its artwork never blurred, moved or covered - behind a Spotify song the
    // background is a colour taken from the cover instead (or Spotify's own #191414); every Spotify song
    // linked back to Spotify ("Play on Spotify", or "Get Spotify free" without the app); and liking with its
    // own + icon and its own words ("Added to Liked Songs" / "Removed from Liked Songs").
    internal static class SpotifyBrand
    {
        public static bool Is(Track t) { return t != null && t.Path != null && t.Path.StartsWith("spotify:"); }

        private static readonly Dictionary<string, BitmapSource> images = new Dictionary<string, BitmapSource>();
        private static BitmapSource Image(string name)
        {
            BitmapSource b;
            if (images.TryGetValue(name, out b)) return b;
            try
            {
                using (Stream s = typeof(SpotifyBrand).Assembly.GetManifestResourceStream(name))
                {
                    if (s != null)
                    {
                        BitmapImage bi = new BitmapImage();
                        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = s; bi.EndInit(); bi.Freeze();
                        b = bi;
                    }
                }
            }
            catch { }
            images[name] = b;
            return b;
        }

        // (white on dark, black on light: the only versions besides green Spotify allows on a background)
        private static bool Dark(Color text) { return text.R * 0.3 + text.G * 0.59 + text.B * 0.11 > 128; }

        // the icon, 'size' square; returns its width
        public static double Icon(DrawingContext dc, double x, double y, double size, Color text)
        {
            BitmapSource b = Image(Dark(text) ? "spotify-icon-white.png" : "spotify-icon-black.png");
            if (b != null) dc.DrawImage(b, new Rect(x, y, size, size));
            return size;
        }

        // the full logo (icon and wordmark), 'height' tall; returns its width
        public static double Logo(DrawingContext dc, double x, double y, double height, Color text)
        {
            BitmapSource b = Image(Dark(text) ? "spotify-logo-white.png" : "spotify-logo-black.png");
            if (b == null) return Icon(dc, x, y, height, text);
            double w = height * b.PixelWidth / b.PixelHeight;
            dc.DrawImage(b, new Rect(x, y, w, height));
            return w;
        }

        // the + Like icon (liked: Spotify's green tick)
        public static void Like(DrawingContext dc, Rect r, bool liked, bool hot)
        {
            BitmapSource b = Image(liked ? "spotify-liked.png" : "spotify-like.png");
            if (b == null) return;
            if (!liked) dc.PushOpacity(hot ? 1 : 0.75);
            dc.DrawImage(b, r);
            if (!liked) dc.Pop();
        }

        // the background behind a Spotify song: the cover's own colour, deepened (never the cover itself)
        public static Color Background(System.Drawing.Bitmap cover)
        {
            if (cover == null) return Color.FromRgb(0x19, 0x14, 0x14);
            long r = 0, g = 0, b = 0, n = 0;
            try
            {
                int step = Math.Max(1, Math.Min(cover.Width, cover.Height) / 24);
                for (int y = 0; y < cover.Height; y += step)
                    for (int x = 0; x < cover.Width; x += step) { System.Drawing.Color c = cover.GetPixel(x, y); r += c.R; g += c.G; b += c.B; n++; }
            }
            catch { return Color.FromRgb(0x19, 0x14, 0x14); }
            if (n == 0) return Color.FromRgb(0x19, 0x14, 0x14);
            double R = r / (double)n, G = g / (double)n, B = b / (double)n, l = R * 0.3 + G * 0.59 + B * 0.11;
            double k = l > 1 ? Math.Min(1.4, 70 / l) : 1;   // (about as dark as the Dark look, whatever the cover)
            return Color.FromRgb((byte)Math.Min(255, R * k), (byte)Math.Min(255, G * k), (byte)Math.Min(255, B * k));
        }

        // ---------- the link back to Spotify ----------

        public static bool AppInstalled
        {
            get
            {
                try { using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("spotify")) return k != null; }
                catch { return false; }
            }
        }

        // as Spotify words it: "Play on Spotify" with the app, "Get Spotify free" without
        public static string LinkText { get { return AppInstalled ? "Play on Spotify" : "Get Spotify free"; } }

        public static void Open(Track t)
        {
            try
            {
                if (AppInstalled) System.Diagnostics.Process.Start(t.Path);   // (spotify:track:... opens the Spotify app on it)
                else System.Diagnostics.Process.Start("https://www.spotify.com/download/");
            }
            catch (Exception ex) { Log.Error("open in spotify", ex); }
        }

        // Spotify's own words after a like or an unlike, for a moment in the top bar
        public static void Said(MainWindow app, bool liked)
        {
            app.Status = liked ? "Added to Liked Songs" : "Removed from Liked Songs";
            app.TopBar.Redraw();
            string was = app.Status;
            System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t.Tick += delegate { t.Stop(); if (app.Status == was) { app.Status = ""; app.TopBar.Redraw(); } };
            t.Start();
        }
    }
}
