// The panel that opens from the tray icon. Drawn by hand in the same look as the island (Theme.cs): near-black,
// borderless cards with soft corners, capsule buttons, sliding switches, amber for HDR and nothing else.
// Nothing here changes any state itself – every click calls back into TrayApp and rebuilds the view.
//
// It stays an ordinary window, not a layered one like the island: that keeps ClearType text and the system's
// shadow and rounded corners on Windows 11 (Windows 10 gets square corners and a hairline, like its own flyouts).
// The price is that the window's own corner radius is Windows' and that it can slide and fade, but not scale.
// No backdrop material either: Mica needs the client area on glass, where GDI text loses its alpha.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HdrSwitch
{
    sealed partial class TrayApp
    {
        sealed class Flyout : Form
        {
            /// <summary>How an item looks right now: each runs 0 → 1 on a spring.</summary>
            sealed class Look
            {
                public double Hover, Press, On;
                public static readonly Look None = new Look();
            }

            /// <summary>The springs behind a Look. Kept by the item's Key, so they survive a rebuild of the item list.</summary>
            sealed class Anim
            {
                public readonly Spring Hover = Motion.Control(0), Press = Motion.Control(0), On = Motion.Thumb(0);
                public readonly Look Look = new Look();
                public bool Seen;
            }

            /// <summary>One painted thing: a card, a tile, a row, a button. Click == null: not interactive.</summary>
            sealed class Item
            {
                public Rectangle Rect;
                public Action<Graphics, Rectangle, Look> Paint;
                public Action Click;
                public bool Scrolls;   // lives in the scrolling list
                public string Tip;
                public string Key;     // unique; without one the item never animates
                public bool On;        // a switch, a lit tile: where Look.On is heading
            }

            readonly TrayApp app;
            float k = 1;   // DPI scale of the monitor it opened on
            Theme.Fonts fonts;
            readonly ToolTip tip = new ToolTip { InitialDelay = 600 };
            readonly List<Item> items = new List<Item>();
            readonly Dictionary<string, Anim> anims = new Dictionary<string, Anim>();
            readonly TextFormatFlags TfLeft, TfCenter, TfCaption, TfWrap;   // TfCaption: right-aligned, never shortened
            Rectangle listClip;
            Bitmap listBmp;   // the list is painted through it; kept while the flyout is open
            int scroll, scrollMax, rowH;
            int hover = -1, pressed = -1;
            bool picking;     // the list shows running programs to add, instead of the games
            string choosing;  // the list shows the resolutions for this game, instead of the games
            bool stretching;  // choosing: the list shows the game's own resolutions, to stretch over its competitive one
            readonly Spring open = Motion.Open(0);           // 0 hidden → 1 in place: slide and fade
            readonly Spring scrollTo = Motion.Scroll(0);     // the wheel moves its target, the list follows
            readonly Spring thumb = Motion.Fade(0);          // the scroll bar: there while scrolling, gone 0.8 s later
            readonly System.Windows.Forms.Timer thumbRest = new System.Windows.Forms.Timer { Interval = 800 };
            readonly Func<double, bool> tick;
            bool closing;
            int closedAt = -100000;
            Point final, slide;   // where it sits when open, and the offset it slides in from

            public Flyout(TrayApp app)
            {
                this.app = app;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.Bg;
                Text = "Display Switch";
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

                const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                TfLeft = Base | TextFormatFlags.Left;
                TfCenter = Base | TextFormatFlags.HorizontalCenter;
                TfCaption = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;
                TfWrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top;

                tick = Tick;
                thumbRest.Tick += (s, e) => { thumbRest.Stop(); thumb.To(0); Motion.Run(tick); };
            }

            // ---- open / close ----

            /// <summary>Tray icon clicked: open, or close if it is open.</summary>
            public void Toggle()
            {
                // The click that closed it: Deactivate comes first and starts the close, then the tray icon reports the click.
                if (Environment.TickCount - closedAt < 300) return;
                if (Visible && !closing) { Dismiss(false); return; }
                Open();
            }

            void Open()
            {
                picking = false;
                choosing = null;
                stretching = false;
                Motion.Refresh();
                float scale = Theme.Scale(Cursor.Position);
                if (fonts == null || scale != k)
                {
                    if (fonts != null) fonts.Dispose();
                    k = scale;
                    fonts = new Theme.Fonts(k);
                }
                bool wasVisible = Visible;   // caught while closing: it turns round from where it is
                closing = false;
                ScrollHome();
                thumb.Snap(0);
                Rebuild();
                final = Corner(out slide);
                Motion.Opening(open);
                if (!wasVisible) open.Snap(0);
                open.To(1);
                Place();
                if (!wasVisible) Show();
                Activate();
                Native.SetForegroundWindow(Handle);
                Motion.Run(tick);
            }

            /// <summary>Closes it: sliding and fading out, or at once (now) when something else is about to take the screen.</summary>
            void Dismiss(bool now)
            {
                if (!Visible) return;
                tip.Hide(this);
                closedAt = Environment.TickCount;
                if (now || Motion.Reduced) { Finish(); return; }
                if (closing) return;
                closing = true;
                Motion.Closing(open);
                open.To(0);
                Motion.Run(tick);
            }

            void Finish()
            {
                Hide();
                closing = false;   // after Hide: the Deactivate it causes must still see the close as under way
                open.Snap(0);
                hover = pressed = -1;
                foreach (var a in anims.Values) { a.Hover.Snap(0); a.Press.Snap(0); }
                thumbRest.Stop();
                if (listBmp != null) { listBmp.Dispose(); listBmp = null; }
                Trim();
            }

            /// <summary>The window follows the open spring: from the slide offset to its place, fading in on the way.</summary>
            void Place()
            {
                double o = Math.Max(0, Math.Min(1, open.Value));
                var p = new Point(final.X + (int)Math.Round(slide.X * (1 - o)), final.Y + (int)Math.Round(slide.Y * (1 - o)));
                if (Location != p) Location = p;
                double opacity = Math.Min(1, o * 1.5);
                if (Opacity != opacity) Opacity = opacity;
            }

            /// <summary>One frame of motion: the window, the list's scrolling and every item's hover, press and on/off.</summary>
            bool Tick(double dt)
            {
                if (!Visible) return false;
                bool moving = open.Step(dt) | scrollTo.Step(dt) | thumb.Step(dt);
                foreach (var a in anims.Values) moving |= a.Hover.Step(dt) | a.Press.Step(dt) | a.On.Step(dt);
                Place();
                if (closing && !open.Moving) { Finish(); return false; }
                int s = (int)Math.Round(scrollTo.Value);
                if (s != scroll)
                {
                    scroll = s;
                    SetHover(HitTest(PointToClient(Cursor.Position)));   // another row is under the mouse now
                }
                Invalidate();
                return moving;
            }

            /// <summary>Where it sits when open: in the corner by the taskbar, 12 px in, like the system flyouts.</summary>
            Point Corner(out Point slideFrom)
            {
                var scr = Screen.FromPoint(Cursor.Position);
                var wa = scr.WorkingArea;
                var b = scr.Bounds;
                int m = S(12), d = S(24);
                int x = wa.Right - m - Width, y = wa.Bottom - m - Height;
                slideFrom = new Point(0, d);                                          // taskbar at the bottom
                if (wa.Top > b.Top) { y = wa.Top + m; slideFrom = new Point(0, -d); }   // at the top
                else if (wa.Left > b.Left) { x = wa.Left + m; slideFrom = new Point(-d, 0); }
                else if (wa.Right < b.Right) slideFrom = new Point(d, 0);
                return new Point(x, y);
            }

            /// <summary>Something changed while it is open (a game started, a scan finished…).</summary>
            public void Sync()
            {
                if (Visible && !closing) Rebuild();
            }

            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }   // WS_EX_TOOLWINDOW: no Alt+Tab entry
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                int v = 2; Native.DwmSetWindowAttribute(Handle, 33, ref v, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = round
                v = 1; Native.DwmSetWindowAttribute(Handle, 20, ref v, 4);       // DWMWA_USE_IMMERSIVE_DARK_MODE
                Color line = Theme.GlassLineFlat;
                v = line.R | (line.G << 8) | (line.B << 16);
                Native.DwmSetWindowAttribute(Handle, 34, ref v, 4);              // DWMWA_BORDER_COLOR
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                if (Visible && !closing) Dismiss(false);
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (keyData == Keys.Escape) { Dismiss(false); return true; }
                return base.ProcessDialogKey(keyData);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    thumbRest.Dispose();
                    tip.Dispose();
                    if (listBmp != null) listBmp.Dispose();
                    if (fonts != null) fonts.Dispose();
                }
                base.Dispose(disposing);
            }

            // ---- layout: builds the item list from the app's state ----

            int S(double v) { return (int)Math.Round(v * k); }

            void ScrollHome()
            {
                scrollTo.Snap(0);
                scroll = 0;
            }

            void Rebuild()
            {
                items.Clear();
                int W = S(376), M = S(16), G = S(12), CP = S(16);
                int y = M;

                // Status card: the island at rest – dot, state, what is running, the app's mark. Below that the latest
                // setup hint, and the update button when there is one.
                string title, sub;
                bool on;
                Status(out title, out sub, out on);
                bool hasUpdate = app.update != null;
                string notice = app.notice, error = hasUpdate && app.updateError != null ? "Update failed: " + app.updateError : null;
                int textW = W - 2 * M - 2 * CP;
                int noticeH = notice == null ? 0 : TextRenderer.MeasureText(notice, fonts.Caption, new Size(textW, 2000), TfWrap).Height;
                int errorH = error == null ? 0 : TextRenderer.MeasureText(error, fonts.Caption, new Size(textW, 2000), TfWrap).Height;
                int noticeY = CP + S(44) + S(10);
                int updateY = CP + S(44) + (notice != null ? S(10) + noticeH : 0) + S(14);
                int h1 = (hasUpdate ? updateY + S(36) + (error != null ? S(8) + errorH : 0) : updateY - S(14)) + CP;
                var c1 = new Rectangle(M, y, W - 2 * M, h1);
                items.Add(new Item { Rect = c1, Key = "status", On = on, Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, S(22), Theme.GlassFlat);
                    Theme.Stroke(g, rc, S(22), Theme.GlassLineFlat);
                    Theme.Dot(g, rc.X + CP + S(9), rc.Y + CP + S(22), S(10), Theme.Mix(Theme.Grey, Theme.Amber, lk.On));
                    int tx = rc.X + CP + S(30), tw = rc.Right - CP - S(36) - S(12) - tx;
                    Draw(g, title, fonts.Title, Theme.Text1, new Rectangle(tx, rc.Y + CP - S(1), tw, S(26)), TfLeft);
                    Draw(g, sub, fonts.Caption, Theme.Text2, new Rectangle(tx, rc.Y + CP + S(26), tw, S(18)), TfLeft);
                    Theme.Badge(g, new RectangleF(rc.Right - CP - S(36), rc.Y + CP + S(4), S(36), S(36)), lk.On);
                    if (notice != null) Draw(g, notice, fonts.Caption, Theme.Text2, new Rectangle(rc.X + CP, rc.Y + noticeY, textW, noticeH), TfWrap);
                    if (error != null) Draw(g, error, fonts.Caption, Theme.Text2, new Rectangle(rc.X + CP, rc.Y + updateY + S(36) + S(8), textW, errorH), TfWrap);
                } });
                if (hasUpdate)
                {
                    bool busy = app.updating;
                    string label = busy ? "Updating…" : "Update to " + app.update.VersionText;
                    items.Add(new Item
                    {
                        Rect = new Rectangle(c1.X + CP, c1.Y + updateY, c1.Width - 2 * CP, S(36)), Key = "update",
                        Click = busy ? null : (Action)(() => { app.InstallUpdate(); Rebuild(); }),
                        Paint = (g, rc, lk) =>
                        {
                            Color fill = busy ? Theme.Raised : Theme.Mix(Theme.Mix(Color.White, Color.FromArgb(218, 218, 223), lk.Hover), Color.FromArgb(182, 182, 188), lk.Press);
                            Theme.Fill(g, rc, rc.Height / 2f, fill);
                            Draw(g, label, fonts.Strong, busy ? Theme.Disabled : Color.Black, rc, TfCenter);
                        }
                    });
                }
                y = c1.Bottom + G;

                // Tiles for the three settings: lit (white) when on.
                int tileH = S(76), tg = S(8), tw3 = (W - 2 * M - 2 * tg) / 3;
                AddTile(new Rectangle(M, y, tw3, tileH), "startup", "", "Start with Windows", IsStartup(), null,
                    () => { SetStartup(!IsStartup()); Rebuild(); });
                AddTile(new Rectangle(M + tw3 + tg, y, tw3, tileH), "recording", "", "SDR while recording", app.sdrWhileRecording, null,
                    () => { app.SetSdrWhileRecording(!app.sdrWhileRecording); Rebuild(); });
                AddTile(new Rectangle(W - M - tw3, y, tw3, tileH), "island", null, "Island", app.islandOn, "Pill at the top of the screen",
                    () => { app.SetIsland(!app.islandOn); Rebuild(); });
                y += tileH + G;

                // The list: games with a switch each, running programs to add, or the resolutions for one game.
                int headH = S(44);
                rowH = S(44);
                var names = new List<string>();
                var modes = new List<Resolution.Mode>();   // choosing: null for "Off", then the display's sizes
                Resolution.Mode chosen = null, full = null;
                double inches = 0;
                int hz = 0;
                if (picking) names = WindowedApps().Where(n => !app.games.Contains(n)).ToList();
                else if (choosing != null)
                {
                    string gdi = TargetGdi();
                    var cur = Resolution.Current(gdi);
                    app.profiles.TryGetValue(choosing, out chosen);
                    hz = chosen != null && chosen.Hz > 0 ? chosen.Hz : cur != null ? cur.Hz : 60;
                    var pic = Resolution.Read(gdi);   // the panel's own size is the target of the display path
                    full = pic != null ? new Resolution.Mode { Width = pic.TargetW, Height = pic.TargetH, Hz = hz } : cur;
                    inches = Resolution.Inches(gdi);
                    if (chosen == null) stretching = false;   // nothing to stretch over
                    if (stretching)
                    {
                        // The game's own resolution: the sizes that lie inside the competitive one, which the GPU stretches over it.
                        modes.Add(null);
                        modes.AddRange(Resolution.Modes(gdi, hz).Where(m => Fits(m, chosen)).OrderByDescending(m => (long)m.Width * m.Height));
                    }
                    else
                    {
                        if (chosen != null) modes.Add(StretchRow);
                        modes.Add(null);
                    }
                    if (!stretching && full != null)
                        modes.AddRange(Resolution.Modes(gdi, hz)
                            .Where(m => m.Width < full.Width || m.Height < full.Height)
                            .OrderBy(m => SameAspect(m, full) ? 0 : 1).ThenByDescending(m => (long)m.Width * m.Height));
                }
                else names = app.games.Concat(app.excluded).Concat(app.profiles.Keys).Distinct().OrderBy(app.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                int count = choosing != null ? modes.Count : names.Count;
                int visible = Math.Max(1, Math.Min(6, count));
                int footH = choosing != null ? S(52) : S(8);
                var c3 = new Rectangle(M, y, W - 2 * M, headH + visible * rowH + footH);
                items.Add(new Item { Rect = c3, Paint = (g, rc, lk) => Theme.Fill(g, rc, S(16), Theme.Card) });
                if (picking || choosing != null)
                {
                    var back = new Rectangle(c3.X + S(8), c3.Y + S(6), S(32), S(32));
                    items.Add(new Item { Rect = back, Key = "back", Click = () => { if (stretching) stretching = false; else { picking = false; choosing = null; } ScrollHome(); Rebuild(); },
                        Paint = (g, rc, lk) => Button(g, rc, lk, Theme.Raised, "", fonts.GlyphSmall, Theme.Text1) });
                    string head = picking ? "Add a program" : app.DisplayName(choosing);
                    string headRight = picking ? "" : stretching ? "inside " + chosen.Text : "at " + hz + " Hz";
                    items.Add(new Item { Rect = c3, Paint = (g, rc, lk) =>
                    {
                        Draw(g, head, fonts.Strong, Theme.Text1, new Rectangle(rc.X + S(50), rc.Y, rc.Width - S(50) - CP - S(84), headH), TfLeft);
                        Draw(g, headRight, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP, headH), TfLeft | TextFormatFlags.Right);
                    } });
                }
                else
                {
                    string onCount = app.games.Count + " on";
                    int gamesW = TextRenderer.MeasureText("Games", fonts.Strong, Size.Empty, TfLeft).Width + S(6);   // measured a little short for semibold
                    items.Add(new Item { Rect = c3, Paint = (g, rc, lk) =>
                    {
                        Draw(g, "Games", fonts.Strong, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, gamesW, headH), TfLeft);
                        Draw(g, onCount, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP + gamesW + S(8), rc.Y + S(1), S(100), headH), TfLeft);
                    } });
                    // The two things to do with the list sit in its header: scan again, add something by hand.
                    var add = new Rectangle(c3.Right - S(10) - S(30), c3.Y + S(7), S(30), S(30));
                    var look = new Rectangle(add.X - S(6) - S(30), add.Y, S(30), S(30));
                    bool scanning = app.scanning;
                    items.Add(new Item { Rect = look, Key = "look", Tip = "Look for games", Click = scanning ? null : (Action)(() => { app.ScanInBackground(true); Rebuild(); }),
                        Paint = (g, rc, lk) => Button(g, rc, lk, Theme.Raised, "", fonts.GlyphSmall, scanning ? Theme.Disabled : Theme.Text1) });
                    items.Add(new Item { Rect = add, Key = "add", Tip = "Add a program", Click = () => { picking = true; ScrollHome(); Rebuild(); },
                        Paint = (g, rc, lk) => Button(g, rc, lk, Theme.Raised, "", fonts.GlyphSmall, Theme.Text1) });
                }
                // A little narrower than the card, so the list's square bitmap stays clear of the card's round corners.
                listClip = new Rectangle(c3.X + S(4), c3.Y + headH, c3.Width - S(8), visible * rowH);
                scrollMax = Math.Max(0, count * rowH - listClip.Height);
                if (scrollTo.Target > scrollMax) scrollTo.Snap(scrollMax);
                scroll = (int)Math.Round(scrollTo.Value);
                if (count == 0)
                {
                    string msg = picking ? "No programs" : app.scanning ? "Looking…" : "No games";
                    items.Add(new Item { Rect = listClip, Paint = (g, rc, lk) => Draw(g, msg, fonts.Body, Theme.Text3, rc, TfCenter) });
                }
                for (int i = 0; i < count; i++)
                {
                    var rr = new Rectangle(c3.X, listClip.Y + i * rowH, c3.Width, rowH);
                    if (choosing != null)
                    {
                        var m = modes[i];
                        string game = choosing;
                        if (ReferenceEquals(m, StretchRow))
                        {
                            // Opens the list of resolutions the game itself can run at, stretched over this one.
                            Resolution.Mode ownNow = app.StretchOf(game);
                            string ownText = ownNow == null ? "Not stretched" : ownNow.Text + ", stretched";
                            items.Add(new Item { Rect = rr, Scrolls = true, Key = "mode:stretch", Click = () => { stretching = true; ScrollHome(); Rebuild(); }, Paint = (g, rc, lk) =>
                            {
                                RowBg(g, rc, lk);
                                Draw(g, "Game resolution", fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(130), rowH), TfLeft);
                                Draw(g, ownText, fonts.Caption, ownNow != null ? Theme.Text1 : Theme.Text3, new Rectangle(rc.X + CP + S(130), rc.Y, rc.Width - 2 * CP - S(130) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                                Draw(g, "", fonts.GlyphSmall, Theme.Text3, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                            } });
                            continue;
                        }
                        string key = "mode:" + (stretching ? "own:" : "") + (m == null ? "off" : m.Width + "x" + m.Height);
                        if (stretching)
                        {
                            Resolution.Mode ownNow = app.StretchOf(game);
                            bool isOwn = m == null ? ownNow == null : ownNow != null && m.Width == ownNow.Width && m.Height == ownNow.Height;
                            string ownLabel = m == null ? "Not stretched" : m.Text;
                            string ownDetail = m == null ? "" : AspectText(m);
                            items.Add(new Item { Rect = rr, Scrolls = true, Key = key, Click = () =>
                            {
                                stretching = false; choosing = null; ScrollHome();
                                app.SetStretch(game, m);
                                Rebuild();
                            }, Paint = (g, rc, lk) =>
                            {
                                RowBg(g, rc, lk);
                                Draw(g, ownLabel, fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(120), rowH), TfLeft);
                                Draw(g, ownDetail, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP + S(120), rc.Y, rc.Width - 2 * CP - S(120) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                                if (isOwn) Draw(g, "", fonts.GlyphSmall, Theme.Text1, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                            } });
                            continue;
                        }
                        int keepHz = chosen != null ? chosen.Hz : 0;
                        bool isChosen = m == null ? chosen == null : chosen != null && m.Width == chosen.Width && m.Height == chosen.Height;
                        string label = m == null ? "Off" : m.Text;
                        string detail = m == null ? (full == null ? "" : "Full screen") : SizeText(m, full, inches);
                        items.Add(new Item { Rect = rr, Scrolls = true, Key = key, Click = () =>
                        {
                            choosing = null; ScrollHome();
                            app.SetProfile(game, m == null ? null : new Resolution.Mode { Width = m.Width, Height = m.Height, Hz = keepHz });
                            Rebuild();
                        }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk);
                            Draw(g, label, fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, S(120), rowH), TfLeft);
                            Draw(g, detail, fonts.Caption, Theme.Text3, new Rectangle(rc.X + CP + S(120), rc.Y, rc.Width - 2 * CP - S(120) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                            if (isChosen) Draw(g, "", fonts.GlyphSmall, Theme.Text1, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                        } });
                        continue;
                    }
                    string name = names[i];
                    if (picking)
                        items.Add(new Item { Rect = rr, Scrolls = true, Key = "pick:" + name, Click = () => { app.Add(name); picking = false; ScrollHome(); Rebuild(); }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk);
                            Draw(g, name + ".exe", fonts.Body, Theme.Text1, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP - S(24), rowH), TfLeft);
                            Draw(g, "", fonts.GlyphSmall, Theme.Mix(Theme.Text3, Theme.Text1, lk.Hover), new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                        } });
                    else
                    {
                        bool isOn = app.games.Contains(name);
                        Resolution.Mode prof;
                        app.profiles.TryGetValue(name, out prof);
                        Resolution.Mode own = prof == null ? null : app.StretchOf(name);
                        string cap = prof == null ? null : own == null ? ProfileText(prof) : own.Text + " stretched";
                        int capW = cap == null ? 0 : TextRenderer.MeasureText(cap, fonts.Caption, new Size(int.MaxValue, rowH), TfCaption).Width + S(10);
                        int monX = rr.Right - CP - S(44) - S(6) - S(32);   // the monitor symbol, left of the switch
                        var mon = new Rectangle(monX, rr.Y + (rowH - S(32)) / 2, S(32), S(32));
                        items.Add(new Item { Rect = rr, Scrolls = true, Key = "row:" + name, On = isOn, Click = () => { if (app.games.Contains(name)) app.Remove(name); else app.Add(name); Rebuild(); }, Paint = (g, rc, lk) =>
                        {
                            RowBg(g, rc, lk);
                            int right = rc.X + (monX - rr.X) - S(4);
                            if (cap != null) Draw(g, cap, fonts.Caption, Theme.Text3, new Rectangle(right - capW, rc.Y, capW, rowH), TfCaption);
                            Draw(g, app.DisplayName(name), fonts.Body, Theme.Mix(Theme.Text2, Theme.Text1, lk.On), new Rectangle(rc.X + CP, rc.Y, right - capW - rc.X - CP, rowH), TfLeft);
                            Theme.Switch(g, new RectangleF(rc.Right - CP - S(44), rc.Y + (rowH - S(26)) / 2f, S(44), S(26)), lk.On, lk.Hover);
                        } });
                        // The monitor symbol opens the game's resolution view; white when it has one.
                        items.Add(new Item { Rect = mon, Scrolls = true, Key = "mon:" + name, Tip = "Resolution", Click = () => { choosing = name; stretching = false; ScrollHome(); Rebuild(); },
                            Paint = (g, rc, lk) => Button(g, rc, lk, Theme.Card, "", fonts.Glyph, prof != null ? Theme.Text1 : Theme.Mix(Theme.Text3, Theme.Text1, lk.Hover)) });
                    }
                }
                if (choosing != null)
                {
                    string note = stretching
                        ? "For borderless games. Stretched over " + chosen.Text + "."
                        : "Missing one? Create it in NVIDIA Control Panel, at " + hz + " Hz.";
                    items.Add(new Item { Rect = new Rectangle(c3.X + CP, listClip.Bottom + S(4), c3.Width - 2 * CP, footH - S(12)), Paint = (g, rc, lk) =>
                        Draw(g, note, fonts.Caption, Theme.Text3, rc, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.VerticalCenter) });
                }
                y = c3.Bottom + G;

                // Bottom bar, straight on the window: version, edit the list, exit.
                string version = "Display Switch " + Updater.CurrentText;
                int exitW = TextRenderer.MeasureText("Exit", fonts.Body, Size.Empty, TfLeft).Width + S(32);
                var exit = new Rectangle(W - M - exitW, y, exitW, S(32));
                items.Add(new Item { Rect = new Rectangle(M + S(4), y, exit.X - M - S(48), S(32)), Paint = (g, rc, lk) => Draw(g, version, fonts.Caption, Theme.Text3, rc, TfLeft) });
                items.Add(new Item { Rect = exit, Key = "exit", Click = () => app.ExitThread(), Paint = (g, rc, lk) =>
                {
                    Theme.Fill(g, rc, rc.Height / 2f, Theme.Mix(Theme.Mix(Theme.Raised, Theme.Hover, lk.Hover), Theme.Press, lk.Press));
                    Draw(g, "Exit", fonts.Body, Theme.Text1, rc, TfCenter);
                } });
                var edit = new Rectangle(exit.X - S(8) - S(32), y, S(32), S(32));
                string file = choosing != null ? ProfilesFile : ListFile;
                items.Add(new Item { Rect = edit, Key = "edit", Tip = choosing != null ? "Edit resolutions.txt" : "Edit games.txt", Click = () =>
                {
                    Dismiss(true);   // at once: Notepad is about to come to the front
                    try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { }
                }, Paint = (g, rc, lk) => Button(g, rc, lk, Theme.Bg, "", fonts.Glyph, Theme.Mix(Theme.Text2, Theme.Text1, lk.Hover)) });
                y += S(32) + M;

                // The springs follow the new items: kept by key, new ones start where they belong, old ones go.
                foreach (var a in anims.Values) a.Seen = false;
                foreach (var it in items)
                {
                    if (it.Key == null) continue;
                    Anim a;
                    if (!anims.TryGetValue(it.Key, out a))
                    {
                        a = new Anim();
                        a.On.Snap(it.On ? 1 : 0);
                        anims[it.Key] = a;
                    }
                    a.Seen = true;
                    if (Visible) a.On.To(it.On ? 1 : 0);
                    else a.On.Snap(it.On ? 1 : 0);
                }
                foreach (string gone in anims.Where(kv => !kv.Value.Seen).Select(kv => kv.Key).ToList()) anims.Remove(gone);

                ClientSize = new Size(W, y);
                pressed = -1;
                if (Visible)
                {
                    final = Corner(out slide);
                    Place();
                    hover = HitTest(PointToClient(Cursor.Position));
                    tip.SetToolTip(this, hover >= 0 ? items[hover].Tip : null);
                }
                else hover = -1;
                Aim();
            }

            void Status(out string title, out string sub, out bool on)
            {
                var displays = Hdr.All();
                on = displays.Any(d => d.On);
                var hdrGames = app.running.Values.Where(r => app.games.Contains(r.Name)).Select(r => app.DisplayName(r.Name)).Distinct().ToList();
                var res = app.res;
                if (!displays.Any(d => d.Supported)) { title = "No HDR display"; sub = "Check Windows display settings"; }
                else if (app.recorder != null && !on)
                {
                    title = "SDR";
                    sub = TrayApp.Recorders[app.recorder] + " running" + (hdrGames.Count > 0 ? " · " + string.Join(", ", hdrGames) : "");
                }
                else if (hdrGames.Count > 0) { title = "HDR on"; sub = string.Join(", ", hdrGames); }
                else if (res != null) { title = res.Mode.Text; sub = app.DisplayName(res.Game); }
                else if (on) { title = "HDR is on"; sub = "Turned on elsewhere"; }
                else { title = "Waiting for a game"; sub = "HDR off"; }
                if (res != null && hdrGames.Count > 0) sub += " · " + res.Text;
                else if (res != null && res.Area != null) sub += app.StretchLost() ? " · not stretched" : " · stretched";
            }

            /// <summary>"92.5 % · 24.6″": how much of the panel a mode covers when shown 1:1 in the middle.</summary>
            static string SizeText(Resolution.Mode m, Resolution.Mode full, double inches)
            {
                if (full == null || full.Width == 0) return "";
                double ratio = Math.Sqrt((double)m.Width * m.Width + (double)m.Height * m.Height) / Math.Sqrt((double)full.Width * full.Width + (double)full.Height * full.Height);
                string s = (ratio * 100).ToString("0.0") + " %";
                return inches > 0 ? s + " · " + (inches * ratio).ToString("0.0") + "″" : s;
            }

            static readonly Resolution.Mode StretchRow = new Resolution.Mode();   // stands for the "Game resolution" row among the modes

            /// <summary>"4:3", "16:10", "16:9" … or "1.78:1" for a shape without a short name.</summary>
            static string AspectText(Resolution.Mode m)
            {
                int a = m.Width, b = m.Height;
                while (b != 0) { int t = a % b; a = b; b = t; }
                int w = m.Width / a, h = m.Height / a;
                if (w == 8 && h == 5) return "16:10";
                return w <= 21 && h <= 10 ? w + ":" + h : ((double)m.Width / m.Height).ToString("0.00") + ":1";
            }

            static bool SameAspect(Resolution.Mode m, Resolution.Mode full)
            {
                return Math.Abs((long)m.Width * full.Height - (long)m.Height * full.Width) * 100 < (long)full.Width * full.Height;
            }

            /// <summary>A setting as a rounded square: symbol above, name below; white with dark content when it is on.</summary>
            void AddTile(Rectangle r, string key, string icon, string label, bool on, string tipText, Action click)
            {
                items.Add(new Item { Rect = r, Key = "tile:" + key, On = on, Tip = tipText, Click = click, Paint = (g, rc, lk) =>
                {
                    Color rest = Theme.Mix(Theme.Raised, Color.White, lk.On);
                    Color over = Theme.Mix(Theme.Hover, Color.FromArgb(222, 222, 227), lk.On), down = Theme.Mix(Theme.Press, Color.FromArgb(190, 190, 196), lk.On);
                    Theme.Fill(g, rc, S(12), Theme.Mix(Theme.Mix(rest, over, lk.Hover), down, lk.Press));
                    Color fg = Theme.Mix(Theme.Text1, Color.Black, lk.On);
                    if (icon != null) Draw(g, icon, fonts.Glyph, fg, new Rectangle(rc.X + S(11), rc.Y + S(11), S(20), S(20)), TfCenter);
                    else   // the island's own symbol: a small pill
                        using (var pen = new Pen(fg, Math.Max(1f, 1.5f * k)))
                        using (var p = Theme.Round(new RectangleF(rc.X + S(12), rc.Y + S(15), S(20), S(11)), S(11) / 2f)) g.DrawPath(pen, p);
                    // The state in words as well: three lit tiles side by side don't say by themselves that lit means on.
                    Draw(g, on ? "On" : "Off", fonts.Caption, Theme.Mix(Theme.Text2, Color.FromArgb(60, 60, 67), lk.On), new Rectangle(rc.Right - S(12) - S(40), rc.Y + S(11), S(40), S(20)), TfCaption);
                    Draw(g, label, fonts.Label, fg, new Rectangle(rc.X + S(12), rc.Bottom - S(10) - S(32), rc.Width - S(20), S(32)),
                        TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Bottom);
                } });
            }

            // ---- painting ----

            protected override void OnPaint(PaintEventArgs e)
            {
                if (fonts == null) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(Theme.Bg);
                for (int i = 0; i < items.Count; i++)
                    if (!items[i].Scrolls) items[i].Paint(g, items[i].Rect, LookOf(i));
                // The list is painted through its own bitmap: GDI text (TextRenderer) ignores a GDI+ clip,
                // so this is what keeps a half-scrolled row inside the card.
                if (listClip.Width > 0 && listClip.Height > 0)
                {
                    if (listBmp == null || listBmp.Size != listClip.Size)
                    {
                        if (listBmp != null) listBmp.Dispose();
                        listBmp = new Bitmap(listClip.Width, listClip.Height);
                    }
                    using (var lg = Graphics.FromImage(listBmp))
                    {
                        lg.SmoothingMode = SmoothingMode.AntiAlias;
                        lg.PixelOffsetMode = PixelOffsetMode.Half;
                        lg.Clear(Theme.Card);
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (!items[i].Scrolls) continue;
                            var r = OnScreen(items[i]);
                            r.Offset(-listClip.X, -listClip.Y);
                            if (r.Bottom > 0 && r.Top < listClip.Height) items[i].Paint(lg, r, LookOf(i));
                        }
                    }
                    g.DrawImageUnscaled(listBmp, listClip.Location);
                }
                if (scrollMax > 0 && thumb.Value > 0.01)   // a thin bar that is only there while the list moves
                {
                    int content = listClip.Height + scrollMax;
                    int trackH = listClip.Height - S(8);
                    int thumbH = Math.Max(S(24), trackH * listClip.Height / content);
                    float thumbY = listClip.Y + S(4) + (trackH - thumbH) * (float)Math.Max(0, Math.Min(scrollMax, scrollTo.Value)) / scrollMax;
                    Theme.Fill(g, new RectangleF(listClip.Right - S(5), thumbY, S(3), thumbH), S(3) / 2f, Theme.Fade(Color.FromArgb(150, 150, 156), thumb.Value));
                }
                if (!Theme.Win11)   // Windows 10 draws no border around a borderless window
                    using (var pen = new Pen(Theme.GlassLineFlat))
                    {
                        g.SmoothingMode = SmoothingMode.None;
                        g.PixelOffsetMode = PixelOffsetMode.Default;
                        g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                    }
            }

            Look LookOf(int i)
            {
                Anim a;
                if (items[i].Key == null || !anims.TryGetValue(items[i].Key, out a)) return Look.None;
                a.Look.Hover = a.Hover.Value;
                a.Look.Press = a.Press.Value;
                a.Look.On = a.On.Value;
                return a.Look;
            }

            /// <summary>Points every item's hover and press springs at where the mouse is now.</summary>
            void Aim()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Anim a;
                    if (items[i].Key == null || !anims.TryGetValue(items[i].Key, out a)) continue;
                    a.Hover.To(i == hover ? 1 : 0);
                    a.Press.To(i == hover && i == pressed ? 1 : 0);
                }
                Invalidate();
                if (Visible) Motion.Run(tick);
            }

            Rectangle OnScreen(Item it) { return it.Scrolls ? new Rectangle(it.Rect.X, it.Rect.Y - scroll, it.Rect.Width, it.Rect.Height) : it.Rect; }

            /// <summary>A row lights up as a rounded bar under the mouse.</summary>
            void RowBg(Graphics g, Rectangle rc, Look lk)
            {
                if (lk.Hover < 0.01) return;
                Color c = Theme.Mix(Theme.Card, Theme.Mix(Theme.Raised, Theme.Press, lk.Press), lk.Hover);
                Theme.Fill(g, new RectangleF(rc.X + S(8), rc.Y + S(2), rc.Width - S(16), rc.Height - S(4)), S(10), c);
            }

            /// <summary>A round button with a symbol. rest: its fill when idle – the colour behind it for one without a fill.</summary>
            void Button(Graphics g, Rectangle rc, Look lk, Color rest, string symbol, Font font, Color fg)
            {
                Theme.Fill(g, rc, rc.Height / 2f, Theme.Mix(Theme.Mix(rest, Theme.Hover, lk.Hover), Theme.Press, lk.Press));
                Draw(g, symbol, font, fg, rc, TfCenter);
            }

            static void Draw(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
            {
                TextRenderer.DrawText(g, s, f, r, c, flags);
            }

            // ---- mouse ----

            int HitTest(Point p)
            {
                if (closing) return -1;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var it = items[i];
                    if (it.Click == null) continue;
                    if (it.Scrolls && !listClip.Contains(p)) continue;
                    if (OnScreen(it).Contains(p)) return i;
                }
                return -1;
            }

            void SetHover(int i)
            {
                if (i == hover) return;
                hover = i;
                tip.SetToolTip(this, i >= 0 ? items[i].Tip : null);
                Aim();
            }

            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHover(HitTest(e.Location)); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(-1); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                pressed = HitTest(e.Location);
                Aim();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                int hit = HitTest(e.Location);
                var click = pressed >= 0 && hit == pressed ? items[pressed].Click : null;
                pressed = -1;
                Aim();
                if (click != null) click();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (scrollMax == 0 || closing || !listClip.Contains(e.Location)) return;
                scrollTo.To(Math.Max(0, Math.Min(scrollMax, scrollTo.Target - Math.Sign(e.Delta) * rowH * 2)));
                thumb.To(1);
                thumbRest.Stop();
                thumbRest.Start();
                Motion.Run(tick);
                if (Motion.Reduced) Tick(0);   // nothing animates: take the new position now
            }
        }
    }
}
