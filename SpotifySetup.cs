using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XvrPlayer
{
    // The guided setup for Muxevour's Spotify controls (the Client ID): four steps on one card instead of a
    // page of instructions. Spotify's page opens on "Create app" with the redirect address already copied;
    // each field has a Copy button; and the Client ID needs no pasting - Muxevour sees it on the clipboard
    // the moment it's copied from Spotify's page, keeps it, and offers Connect.
    internal sealed class SpotifySetupWindow : Window
    {
        private static SpotifySetupWindow current;
        private const double Shadow = 22;

        public static void Open(MainWindow app, Window over, Action done)
        {
            if (current != null) { current.Activate(); return; }
            current = new SpotifySetupWindow(app, over, done);
            current.Show();
        }

        private SpotifySetupWindow(MainWindow app, Window over, Action done)
        {
            Owner = over ?? app;
            Title = "Connect Spotify";
            Width = 640 + 2 * Shadow; Height = 560 + 2 * Shadow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ShowInTaskbar = false;
            Background = Brushes.Transparent;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            SpotifySetupView view = new SpotifySetupView(this, app);
            System.Windows.Controls.Grid card = new System.Windows.Controls.Grid { Margin = new Thickness(Shadow) };
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.55, Color = Colors.Black };
            card.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Gfx.LightLook ? Color.FromRgb(240, 240, 244) : Color.FromRgb(24, 22, 30)), RadiusX = 18, RadiusY = 18 });
            card.Children.Add(view);
            card.Children.Add(new System.Windows.Shapes.Rectangle { Stroke = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), StrokeThickness = 1, RadiusX = 18, RadiusY = 18, IsHitTestVisible = false });
            Content = card;
            PreviewKeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
            Closed += delegate { current = null; view.Stop(); if (done != null) done(); };
        }
    }

    internal sealed class SpotifySetupView : FrameworkElement
    {
        private readonly Window window;
        private readonly MainWindow app;
        private readonly List<SceneView.Hit> hits = new List<SceneView.Hit>();
        private readonly DispatcherTimer watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private string hover, note = "", copied;
        private bool connecting;
        private static readonly Regex IdShape = new Regex("^[0-9a-f]{32}$");
        private static readonly Fnt big = new Fnt("Montserrat", 20, FontWeights.Bold);
        private static readonly Fnt stepF = new Fnt("Montserrat", 12.5, FontWeights.SemiBold);
        private static readonly Fnt textF = new Fnt("Montserrat", 11.5, FontWeights.Normal);
        private static readonly Fnt btnF = new Fnt("Montserrat", 11, FontWeights.SemiBold);
        private static readonly Fnt numF = new Fnt("Montserrat", 11, FontWeights.Bold);

        public const string CreatePage = "https://developer.spotify.com/dashboard/create";

        public SpotifySetupView(Window window, MainWindow app)
        {
            this.window = window;
            this.app = app;
            // (the Client ID, picked up from the clipboard as soon as it's copied on Spotify's page)
            watch.Tick += delegate
            {
                string t = null;
                try { if (Clipboard.ContainsText()) t = Clipboard.GetText().Trim(); } catch { }
                if (t == null || !IdShape.IsMatch(t) || t == SpotifyWeb.ClientId) return;
                Settings.Set("spotifyclientid", t);
                Settings.Save();
                SpotifyWeb.SignOut();
                note = "Got your Client ID. Now step 4: Connect.";
                InvalidateVisual();
            };
            watch.Start();
        }

        public void Stop() { watch.Stop(); }

        private Color T1 { get { return Gfx.LightLook ? Gfx.Rgb(20, 20, 26) : Colors.White; } }
        private Color T2 { get { return Gfx.LightLook ? Gfx.Rgb(80, 80, 92) : Gfx.Rgb(178, 178, 190); } }

        private void Copy(string what, string label)
        {
            try { Clipboard.SetText(what); copied = label; note = "Copied: " + what; } catch { note = "Couldn't copy - type it in: " + what; }
            InvalidateVisual();
        }

        // a small pill button; returns its width
        private double Pill(DrawingContext dc, string text, double x, double y, string id, Action click, bool strong = false)
        {
            FormattedText t = Gfx.Format(text, btnF, strong ? Colors.White : T1, 0);
            Rect r = new Rect(x, y, t.Width + 26, 28);
            bool hot = hover == id;
            dc.DrawRoundedRectangle(Gfx.B(strong ? (hot ? Gfx.Lerp(Gfx.Accent, Colors.White, 0.15) : Gfx.Accent) : Gfx.A((byte)(hot ? 60 : 32), T1)), null, r, 14, 14);
            dc.DrawText(t, new Point(r.X + 13, r.Y + (r.Height - t.Height) / 2));
            hits.Add(new SceneView.Hit { R = r, Id = id, Click = click });
            return r.Width;
        }

        private double Step(DrawingContext dc, int n, string title, string text, double y, bool done)
        {
            double x = 34, w = ActualWidth - 68;
            Point c = new Point(x + 12, y + 12);
            dc.DrawEllipse(Gfx.B(done ? Color.FromRgb(30, 215, 96) : Gfx.A(200, Gfx.Accent)), null, c, 12, 12);
            FormattedText nt = Gfx.Format(done ? "✓" : n.ToString(), numF, Colors.White, 0);
            dc.DrawText(nt, new Point(c.X - nt.Width / 2, c.Y - nt.Height / 2));
            Gfx.Text(dc, title, stepF, new Rect(x + 36, y + 2, w - 36, 20), T1, 0, false);
            FormattedText d = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, textF.Face, textF.Size, Gfx.B(T2), Gfx.Dpi) { MaxTextWidth = w - 36, LineHeight = textF.Size * 1.5 };
            dc.DrawText(d, new Point(x + 36, y + 24));
            return y + 24 + d.Height + 8;
        }

        protected override void OnRender(DrawingContext dc)
        {
            hits.Clear();
            double w = ActualWidth, h = ActualHeight, x = 34;
            Gfx.Text(dc, "Connect Spotify", big, new Rect(x, 24, w - 120, 30), T1, 0, false);
            Gfx.Text(dc, "About two minutes, once: your own free key for Muxevour's buttons and your library.", textF, new Rect(x, 56, w - 68, 18), T2, 0, false);
            Rect close = new Rect(w - 44, 20, 26, 26);
            Gfx.Light(dc, new Point(close.X + 13, close.Y + 13), 8, 2, hover == "close");
            hits.Add(new SceneView.Hit { R = close, Id = "close", Click = window.Close });
            bool haveId = SpotifyWeb.ClientId.Length > 0, connected = SpotifyWeb.Connected;
            double y = 92;
            y = Step(dc, 1, "Open Spotify's developer page", "Sign in with your Spotify account if it asks. The address Muxevour needs is already copied for you.", y, haveId);
            Pill(dc, "Open Spotify's page", x + 36, y, "open", delegate
            {
                Copy(SpotifyWeb.Redirect, "redirect");
                try { System.Diagnostics.Process.Start(CreatePage); } catch { }
                note = "Spotify's page is open in your browser. The redirect address is copied.";
                InvalidateVisual();
            }, !haveId);
            y += 44;
            y = Step(dc, 2, "Create the app", "Fill in Spotify's form: any name and description; under Redirect URIs paste the address and press Add; tick Web API; agree to Spotify's terms; Save.", y, haveId);
            double bx = x + 36;
            bx += Pill(dc, copied == "name" ? "Name copied" : "Copy a name", bx, y, "cname", delegate { Copy("Muxevour", "name"); }) + 8;
            bx += Pill(dc, copied == "desc" ? "Description copied" : "Copy a description", bx, y, "cdesc", delegate { Copy("My own music player", "desc"); }) + 8;
            Pill(dc, copied == "redirect" ? "Address copied" : "Copy the redirect address", bx, y, "credir", delegate { Copy(SpotifyWeb.Redirect, "redirect"); });
            y += 44;
            y = Step(dc, 3, "Copy the Client ID", haveId ? "Got it: " + SpotifyWeb.ClientId.Substring(0, 6) + "… (kept on this PC). To use another one, just copy it." : "On your new app's page, copy the Client ID (not the secret). Muxevour picks it up by itself - no pasting.", y, haveId);
            y += 4;
            y = Step(dc, 4, "Connect", connected ? "Connected. Your liked songs, albums and playlists are coming in." : "Spotify's sign-in page opens: approve Muxevour, and you're done.", y, connected);
            if (haveId && !connected)
                Pill(dc, connecting ? "Waiting for Spotify…" : "Connect", x + 36, y, "connect", delegate
                {
                    if (connecting) return;
                    connecting = true; note = "Approve Muxevour on Spotify's page in your browser.";
                    InvalidateVisual();
                    SpotifyWeb.SignIn(delegate (string why)
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            connecting = false;
                            SpotifyWeb.Problem = why;
                            if (why == null) { note = "Connected."; app.RefreshSource(); }
                            else note = why.Contains("redirect") || why.Contains("INVALID_CLIENT") ? "Spotify refused it: check the redirect address is exactly " + SpotifyWeb.Redirect + " (step 2)." : why;
                            InvalidateVisual();
                        });
                    });
                }, true);
            else if (connected) Pill(dc, "Done", x + 36, y, "done", window.Close, true);
            if (note.Length > 0) Gfx.Text(dc, note, textF, new Rect(x, h - 40, w - 68, 20), T2, 0, false);
        }

        private SceneView.Hit At(Point p) { for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i]; return null; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SceneView.Hit hit = At(e.GetPosition(this));
            string id = hit == null ? null : hit.Id;
            Cursor = id != null ? Cursors.Hand : null;
            if (id != hover) { hover = id; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            SceneView.Hit hit = At(e.GetPosition(this));
            if (hit != null) hit.Click(); else window.DragMove();
        }
    }
}
