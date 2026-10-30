// The panel that opens from the tray icon. Drawn by hand in the style of the Windows 11 flyouts
// (quick settings, notifications): rounded dark cards, tiles, toggle switches, the system accent colour.
// Nothing here changes any state itself – every click calls back into TrayApp and rebuilds the view.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrSwitch
{
    sealed partial class TrayApp
    {
        sealed class Flyout : Form
        {
            // ---- Windows 11 dark palette ----
            static readonly Color Bg = Color.FromArgb(32, 32, 32);
            static readonly Color CardFill = Color.FromArgb(43, 43, 43);
            static readonly Color CardLine = Color.FromArgb(58, 58, 58);
            static readonly Color TileFill = Color.FromArgb(61, 61, 61);
            static readonly Color TileHover = Color.FromArgb(70, 70, 70);
            static readonly Color TilePress = Color.FromArgb(54, 54, 54);
            static readonly Color RowHover = Color.FromArgb(55, 55, 55);
            static readonly Color RowPress = Color.FromArgb(50, 50, 50);
            static readonly Color Text1 = Color.White;
            static readonly Color Text2 = Color.FromArgb(204, 204, 204);
            static readonly Color Text3 = Color.FromArgb(145, 145, 145);
            static readonly Color Disabled = Color.FromArgb(110, 110, 110);
            static readonly Color HdrOn = Color.FromArgb(255, 170, 0);
            static readonly Color HdrOff = Color.FromArgb(120, 120, 120);
            static readonly bool Win11 = Environment.OSVersion.Version.Build >= 22000;

            const int SlideMs = 160;

            /// <summary>One painted thing: a card, a tile, a row, a button. Click == null: not interactive.</summary>
            sealed class Item
            {
                public Rectangle Rect;
                public Action<Graphics, Rectangle, int> Paint;   // state: 0 normal, 1 hover, 2 pressed
                public Action Click;
                public bool Scrolls;   // lives in the scrolling list
                public string Tip;
            }

            readonly TrayApp app;
            readonly float k;   // DPI scale
            readonly Color accent, onAccent;
            readonly Font body, bodyStrong, caption, titleFont, badgeFont, glyph, glyphSmall;
            readonly ToolTip tip = new ToolTip { InitialDelay = 600 };
            readonly List<Item> items = new List<Item>();
            readonly TextFormatFlags TfLeft, TfCenter, TfCaption;   // TfCaption: right-aligned, never shortened
            Rectangle listClip;
            int scroll, scrollMax, rowH;
            int hover = -1, pressed = -1;
            bool picking;     // the list shows running programs to add, instead of the games
            string choosing;  // the list shows the resolutions for this game, instead of the games
            int hiddenAt = -100000;
            readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 10 };
            Stopwatch clock;
            Point from, final;

            public Flyout(TrayApp app)
            {
                this.app = app;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Bg;
                Text = "HDR Switch";
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                k = DeviceDpi / 96f;

                accent = Accent();
                onAccent = Luma(accent) > 0.5 ? Color.Black : Color.White;

                var installed = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name));
                string text = installed.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
                string strong = installed.Contains("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";
                string display = installed.Contains("Segoe UI Variable Display Semib") ? "Segoe UI Variable Display Semib" : "Segoe UI Semibold";
                string icons = installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                body = Px(text, 14);
                bodyStrong = Px(strong, 14);
                caption = Px(text, 12);
                titleFont = Px(display, 18);
                badgeFont = Px("Segoe UI Semibold", 22);
                glyph = Px(icons, 16);
                glyphSmall = Px(icons, 13);

                const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                TfLeft = Base | TextFormatFlags.Left;
                TfCenter = Base | TextFormatFlags.HorizontalCenter;
                TfCaption = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;

                anim.Tick += (s, e) => Slide();
            }

            // ---- open / close ----

            /// <summary>Tray icon clicked: open, or close if it is open.</summary>
            public void Toggle()
            {
                if (Visible) { Dismiss(); return; }
                if (Environment.TickCount - hiddenAt < 300) return;   // the click that closed it (Deactivate fires first)
                Open();
            }

            void Open()
            {
                picking = false;
                choosing = null;
                scroll = 0;
                Rebuild();
                Point offset;
                final = Corner(out offset);
                from = new Point(final.X + offset.X, final.Y + offset.Y);
                Location = from;
                clock = Stopwatch.StartNew();
                anim.Start();
                Show();
                Activate();
                Native.SetForegroundWindow(Handle);
            }

            void Slide()
            {
                double t = Math.Min(1, clock.ElapsedMilliseconds / (double)SlideMs);
                double e = 1 - Math.Pow(1 - t, 3);   // ease out
                Location = new Point((int)Math.Round(from.X + (final.X - from.X) * e), (int)Math.Round(from.Y + (final.Y - from.Y) * e));
                if (t >= 1) anim.Stop();
            }

            void Dismiss()
            {
                anim.Stop();
                tip.Hide(this);
                Hide();
                hiddenAt = Environment.TickCount;
                Trim();
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
                if (Visible) Rebuild();
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
                v = CardLine.R | (CardLine.G << 8) | (CardLine.B << 16);
                Native.DwmSetWindowAttribute(Handle, 34, ref v, 4);              // DWMWA_BORDER_COLOR
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                if (!Win11 && IsHandleCreated)   // Windows 10: DWM doesn't round the corners, so clip them ourselves
                    using (var p = RoundPath(new Rectangle(0, 0, Width, Height), S(8))) Region = new Region(p);
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                if (Visible) Dismiss();
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (keyData == Keys.Escape) { Dismiss(); return true; }
                return base.ProcessDialogKey(keyData);
            }

            // ---- layout: builds the item list from the app's state ----

            int S(double v) { return (int)Math.Round(v * k); }

            void Rebuild()
            {
                items.Clear();
                int W = S(376), M = S(12), CP = S(16);
                int y = M;

                // Status card: badge, state, what is running; the update button when there is one.
                string title, sub;
                bool on;
                Status(out title, out sub, out on);
                bool hasUpdate = app.update != null;
                int h1 = CP + S(42) + CP + (hasUpdate ? S(12) + S(32) : 0);
                var c1 = new Rectangle(M, y, W - 2 * M, h1);
                AddCard(c1);
                int textX = CP + S(40) + S(12);
                items.Add(new Item { Rect = c1, Paint = (g, rc, st) =>
                {
                    var badge = new Rectangle(rc.X + CP, rc.Y + CP, S(40), S(40));
                    FillRound(g, badge, S(10), on ? HdrOn : HdrOff);
                    Draw(g, "H", badgeFont, on ? Color.Black : Color.White, badge, TfCenter);
                    Draw(g, title, titleFont, Text1, new Rectangle(rc.X + textX, rc.Y + CP - S(1), rc.Width - textX - CP, S(24)), TfLeft);
                    Draw(g, sub, caption, Text2, new Rectangle(rc.X + textX, rc.Y + CP + S(24), rc.Width - textX - CP, S(18)), TfLeft);
                } });
                if (hasUpdate)
                {
                    bool busy = app.updating;
                    string label = busy ? "Updating…" : "Update to version " + app.update.VersionText;
                    items.Add(new Item
                    {
                        Rect = new Rectangle(c1.X + CP, c1.Y + CP + S(42) + S(12), c1.Width - 2 * CP, S(32)),
                        Click = busy ? null : (Action)(() => { app.InstallUpdate(); Rebuild(); }),
                        Paint = (g, rc, st) =>
                        {
                            FillRound(g, rc, S(4), busy ? TileFill : st == 2 ? Shade(accent, -0.1f) : st == 1 ? Shade(accent, 0.08f) : accent);
                            Draw(g, label, bodyStrong, busy ? Disabled : onAccent, rc, TfCenter);
                        }
                    });
                }
                y = c1.Bottom + M;

                // Tiles, like quick settings.
                int tileH = S(52), gap = S(8);
                var c2 = new Rectangle(M, y, W - 2 * M, CP + tileH + S(6) + S(32) + S(12));
                AddCard(c2);
                int tw = (c2.Width - 2 * CP - 2 * gap) / 3;
                bool startup = IsStartup();
                AddTile(new Rectangle(c2.X + CP, c2.Y + CP, tw, tileH), "", "Start with Windows", startup, true, false,
                    () => { SetStartup(!IsStartup()); Rebuild(); });
                AddTile(new Rectangle(c2.X + CP + tw + gap, c2.Y + CP, tw, tileH), "", app.scanning ? "Looking…" : "Look for games", false, !app.scanning, false,
                    () => { app.ScanInBackground(true); Rebuild(); });
                AddTile(new Rectangle(c2.X + CP + 2 * (tw + gap), c2.Y + CP, tw, tileH), "", "Add a program", false, true, true,
                    () => { picking = true; scroll = 0; Rebuild(); });
                y = c2.Bottom + M;

                // The list: games with a switch each, running programs to add, or the resolutions for one game.
                int headH = S(44);
                rowH = S(40);
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
                    modes.Add(null);
                    if (full != null)
                        modes.AddRange(Resolution.Modes(gdi, hz)
                            .Where(m => m.Width < full.Width || m.Height < full.Height)
                            .OrderBy(m => SameAspect(m, full) ? 0 : 1).ThenByDescending(m => (long)m.Width * m.Height));
                }
                else names = app.games.Concat(app.excluded).Concat(app.profiles.Keys).Distinct().OrderBy(app.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                int count = choosing != null ? modes.Count : names.Count;
                int visible = Math.Max(1, Math.Min(6, count));
                int footH = choosing != null ? S(44) : S(6);
                var c3 = new Rectangle(M, y, W - 2 * M, headH + visible * rowH + footH);
                AddCard(c3);
                if (picking || choosing != null)
                {
                    var back = new Rectangle(c3.X + S(10), c3.Y + S(6), S(32), S(32));
                    items.Add(new Item { Rect = back, Click = () => { picking = false; choosing = null; scroll = 0; Rebuild(); }, Paint = (g, rc, st) =>
                    {
                        if (st > 0) FillRound(g, rc, S(4), st == 2 ? RowPress : RowHover);
                        Draw(g, "", glyph, Text1, rc, TfCenter);
                    } });
                    string head = picking ? "Add a running program" : app.DisplayName(choosing);
                    string headRight = picking ? "" : "at " + hz + " Hz";
                    items.Add(new Item { Rect = c3, Paint = (g, rc, st) =>
                    {
                        Draw(g, head, bodyStrong, Text1, new Rectangle(rc.X + S(50), rc.Y, rc.Width - S(50) - CP - S(70), headH), TfLeft);
                        Draw(g, headRight, caption, Text3, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP, headH), TfLeft | TextFormatFlags.Right);
                    } });
                }
                else
                {
                    int onCount = app.games.Count;
                    items.Add(new Item { Rect = c3, Paint = (g, rc, st) =>
                    {
                        Draw(g, "Games", bodyStrong, Text1, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP, headH), TfLeft);
                        Draw(g, onCount + " on", caption, Text3, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP, headH), TfLeft | TextFormatFlags.Right);
                    } });
                }
                listClip = new Rectangle(c3.X + 1, c3.Y + headH, c3.Width - 2, visible * rowH);
                scrollMax = Math.Max(0, count * rowH - listClip.Height);
                scroll = Math.Max(0, Math.Min(scroll, scrollMax));
                if (count == 0)
                {
                    string msg = picking ? "Nothing else with a window is running" : app.scanning ? "Looking for games…" : "No games found";
                    items.Add(new Item { Rect = listClip, Paint = (g, rc, st) => Draw(g, msg, body, Text3, rc, TfCenter) });
                }
                for (int i = 0; i < count; i++)
                {
                    var rr = new Rectangle(c3.X, listClip.Y + i * rowH, c3.Width, rowH);
                    if (choosing != null)
                    {
                        var m = modes[i];
                        string game = choosing;
                        int keepHz = chosen != null ? chosen.Hz : 0;
                        bool isChosen = m == null ? chosen == null : chosen != null && m.Width == chosen.Width && m.Height == chosen.Height;
                        string label = m == null ? "Off" : m.Text;
                        string detail = m == null ? (full == null ? "" : full.Text + " · the whole screen") : SizeText(m, full, inches);
                        items.Add(new Item { Rect = rr, Scrolls = true, Click = () =>
                        {
                            choosing = null; scroll = 0;
                            app.SetProfile(game, m == null ? null : new Resolution.Mode { Width = m.Width, Height = m.Height, Hz = keepHz });
                            Rebuild();
                        }, Paint = (g, rc, st) =>
                        {
                            RowBg(g, rc, st);
                            Draw(g, label, body, Text1, new Rectangle(rc.X + CP, rc.Y, S(120), rowH), TfLeft);
                            Draw(g, detail, caption, Text3, new Rectangle(rc.X + CP + S(120), rc.Y, rc.Width - 2 * CP - S(120) - S(28), rowH), TfLeft | TextFormatFlags.Right);
                            if (isChosen) Draw(g, "", glyphSmall, accent, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                        } });
                        continue;
                    }
                    string name = names[i];
                    if (picking)
                        items.Add(new Item { Rect = rr, Scrolls = true, Click = () => { app.Add(name); picking = false; scroll = 0; Rebuild(); }, Paint = (g, rc, st) =>
                        {
                            RowBg(g, rc, st);
                            Draw(g, name + ".exe", body, Text1, new Rectangle(rc.X + CP, rc.Y, rc.Width - 2 * CP - S(24), rowH), TfLeft);
                            Draw(g, "", glyphSmall, st > 0 ? Text1 : Text3, new Rectangle(rc.Right - CP - S(20), rc.Y, S(20), rowH), TfCenter);
                        } });
                    else
                    {
                        bool isOn = app.games.Contains(name);
                        Resolution.Mode prof;
                        app.profiles.TryGetValue(name, out prof);
                        string cap = prof == null ? null : ProfileText(prof);
                        int capW = cap == null ? 0 : TextRenderer.MeasureText(cap, caption, new Size(int.MaxValue, rowH), TfCaption).Width + S(10);
                        int monX = rr.Right - CP - S(40) - S(8) - S(32);   // the monitor symbol, left of the switch
                        var mon = new Rectangle(monX, rr.Y + (rowH - S(32)) / 2, S(32), S(32));
                        items.Add(new Item { Rect = rr, Scrolls = true, Click = () => { if (app.games.Contains(name)) app.Remove(name); else app.Add(name); Rebuild(); }, Paint = (g, rc, st) =>
                        {
                            RowBg(g, rc, st);
                            int right = rc.X + (monX - rr.X) - S(4);
                            if (cap != null) Draw(g, cap, caption, Text3, new Rectangle(right - capW, rc.Y, capW, rowH), TfCaption);
                            Draw(g, app.DisplayName(name), body, isOn ? Text1 : Text2, new Rectangle(rc.X + CP, rc.Y, right - capW - rc.X - CP, rowH), TfLeft);
                            Switch(g, new Rectangle(rc.Right - CP - S(40), rc.Y + (rowH - S(20)) / 2, S(40), S(20)), isOn, st);
                        } });
                        // The monitor symbol opens the game's resolution view; lit when it has one.
                        items.Add(new Item { Rect = mon, Scrolls = true, Tip = "Competitive resolution", Click = () => { choosing = name; scroll = 0; Rebuild(); }, Paint = (g, rc, st) =>
                        {
                            if (st > 0) FillRound(g, rc, S(4), st == 2 ? RowPress : RowHover);
                            Draw(g, "", glyph, prof != null ? accent : st > 0 ? Text1 : Text3, rc, TfCenter);
                        } });
                    }
                }
                if (choosing != null)
                {
                    string note = "Missing one? Create it as a custom resolution in NVIDIA Control Panel (Change resolution › Customize), at " + hz + " Hz.";
                    items.Add(new Item { Rect = new Rectangle(c3.X + CP, listClip.Bottom + S(2), c3.Width - 2 * CP, footH - S(6)), Paint = (g, rc, st) =>
                        Draw(g, note, caption, Text3, rc, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.VerticalCenter) });
                }
                y = c3.Bottom + M;

                // Bottom bar: version, edit the list, exit.
                var c4 = new Rectangle(M, y, W - 2 * M, S(48));
                AddCard(c4);
                string version = "HDR Switch " + Updater.CurrentText;
                items.Add(new Item { Rect = c4, Paint = (g, rc, st) =>
                    Draw(g, version, caption, Text3, new Rectangle(rc.X + CP, rc.Y, rc.Width / 2, rc.Height), TfLeft) });
                int exitW = S(12) + S(16) + S(6) + TextRenderer.MeasureText("Exit", body, Size.Empty, TfLeft).Width + S(14);
                var exit = new Rectangle(c4.Right - CP + S(4) - exitW, c4.Y + S(8), exitW, S(32));
                items.Add(new Item { Rect = exit, Click = () => app.ExitThread(), Paint = (g, rc, st) =>
                {
                    FillRound(g, rc, S(4), st == 2 ? TilePress : st == 1 ? TileHover : TileFill);
                    Draw(g, "", glyphSmall, Text1, new Rectangle(rc.X + S(12), rc.Y, S(16), rc.Height), TfCenter);
                    Draw(g, "Exit", body, Text1, new Rectangle(rc.X + S(12) + S(16) + S(6), rc.Y, rc.Width, rc.Height), TfLeft);
                } });
                var edit = new Rectangle(exit.X - S(6) - S(32), exit.Y, S(32), S(32));
                string file = choosing != null ? ProfilesFile : ListFile;
                items.Add(new Item { Rect = edit, Tip = choosing != null ? "Edit the resolutions (resolutions.txt)" : "Edit the game list (games.txt)", Click = () =>
                {
                    Dismiss();
                    try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { }
                }, Paint = (g, rc, st) =>
                {
                    if (st > 0) FillRound(g, rc, S(4), st == 2 ? TilePress : TileHover);
                    Draw(g, "", glyph, st > 0 ? Text1 : Text2, rc, TfCenter);
                } });
                y = c4.Bottom + M;

                ClientSize = new Size(W, y);
                if (Visible)
                {
                    Point offset;
                    final = Corner(out offset);
                    if (!anim.Enabled) Location = final;
                    SetHover(HitTest(PointToClient(Cursor.Position)));
                }
                else hover = -1;
                pressed = -1;
                Invalidate();
            }

            void Status(out string title, out string sub, out bool on)
            {
                var displays = Hdr.All();
                on = displays.Any(d => d.On);
                var hdrGames = app.running.Values.Where(r => app.games.Contains(r.Name)).Select(r => app.DisplayName(r.Name)).Distinct().ToList();
                var res = app.res;
                if (!displays.Any(d => d.Supported)) { title = "No HDR display found"; sub = "Turn HDR on in Settings › System › Display"; }
                else if (hdrGames.Count > 0) { title = "HDR on"; sub = string.Join(", ", hdrGames); }
                else if (res != null) { title = res.Mode.Text; sub = app.DisplayName(res.Game); }
                else if (on) { title = "HDR is on"; sub = "Turned on elsewhere, so it is left alone"; }
                else { title = "Waiting for a game"; sub = "HDR turns on the moment a game starts"; }
                if (res != null && hdrGames.Count > 0) sub += " · " + res.Mode.Text;
                else if (res != null && res.Back != null) sub += " · " + res.Back.Text + " after it closes";
            }

            /// <summary>"92.5 % · 24.6″": how much of the panel a mode covers when shown 1:1 in the middle.</summary>
            static string SizeText(Resolution.Mode m, Resolution.Mode full, double inches)
            {
                if (full == null || full.Width == 0) return "";
                double ratio = Math.Sqrt((double)m.Width * m.Width + (double)m.Height * m.Height) / Math.Sqrt((double)full.Width * full.Width + (double)full.Height * full.Height);
                string s = (ratio * 100).ToString("0.0") + " %";
                return inches > 0 ? s + " · " + (inches * ratio).ToString("0.0") + "″" : s;
            }

            static bool SameAspect(Resolution.Mode m, Resolution.Mode full)
            {
                return Math.Abs((long)m.Width * full.Height - (long)m.Height * full.Width) * 100 < (long)full.Width * full.Height;
            }

            void AddCard(Rectangle r)
            {
                items.Add(new Item { Rect = r, Paint = (g, rc, st) => { FillRound(g, rc, S(8), CardFill); StrokeRound(g, rc, S(8), CardLine); } });
            }

            void AddTile(Rectangle r, string icon, string label, bool on, bool enabled, bool split, Action click)
            {
                items.Add(new Item { Rect = r, Click = enabled ? click : null, Paint = (g, rc, st) =>
                {
                    Color fill = !enabled ? TileFill
                        : on ? (st == 2 ? Shade(accent, -0.1f) : st == 1 ? Shade(accent, 0.08f) : accent)
                        : st == 2 ? TilePress : st == 1 ? TileHover : TileFill;
                    FillRound(g, rc, S(4), fill);
                    Color fg = !enabled ? Disabled : on ? onAccent : Text1;
                    if (split)
                    {
                        int div = rc.Right - S(32);
                        Draw(g, icon, glyph, fg, new Rectangle(rc.X, rc.Y, div - rc.X, rc.Height), TfCenter);
                        using (var pen = new Pen(Color.FromArgb(on ? 60 : 30, on ? Color.Black : Color.White)))
                            g.DrawLine(pen, div, rc.Y + S(8), div, rc.Bottom - S(8));
                        Draw(g, "", glyphSmall, fg, new Rectangle(div, rc.Y, rc.Right - div, rc.Height), TfCenter);
                    }
                    else Draw(g, icon, glyph, fg, rc, TfCenter);
                    Draw(g, label, caption, enabled ? Text1 : Disabled, new Rectangle(rc.X - S(4), rc.Bottom + S(6), rc.Width + S(8), S(32)),
                        TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                } });
            }

            // ---- painting ----

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Bg);
                for (int i = 0; i < items.Count; i++)
                    if (!items[i].Scrolls) items[i].Paint(g, items[i].Rect, State(i));
                // The list is painted through its own bitmap: GDI text (TextRenderer) ignores a GDI+ clip,
                // so this is what keeps a half-scrolled row inside the card.
                if (listClip.Width > 0 && listClip.Height > 0)
                {
                    using (var bmp = new Bitmap(listClip.Width, listClip.Height))
                    using (var lg = Graphics.FromImage(bmp))
                    {
                        lg.SmoothingMode = SmoothingMode.AntiAlias;
                        lg.Clear(CardFill);
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (!items[i].Scrolls) continue;
                            var r = OnScreen(items[i]);
                            r.Offset(-listClip.X, -listClip.Y);
                            if (r.Bottom > 0 && r.Top < listClip.Height) items[i].Paint(lg, r, State(i));
                        }
                        g.DrawImageUnscaled(bmp, listClip.Location);
                    }
                }
                if (scrollMax > 0)   // thin scroll indicator, as in the system lists
                {
                    int content = listClip.Height + scrollMax;
                    int trackH = listClip.Height - S(8);
                    int thumbH = Math.Max(S(24), trackH * listClip.Height / content);
                    int thumbY = listClip.Y + S(4) + (trackH - thumbH) * scroll / scrollMax;
                    FillRound(g, new Rectangle(listClip.Right - S(7), thumbY, S(3), thumbH), S(1), Color.FromArgb(130, 130, 130));
                }
            }

            int State(int i) { return i == hover ? (i == pressed ? 2 : 1) : 0; }

            Rectangle OnScreen(Item it) { return it.Scrolls ? new Rectangle(it.Rect.X, it.Rect.Y - scroll, it.Rect.Width, it.Rect.Height) : it.Rect; }

            void RowBg(Graphics g, Rectangle rc, int st)
            {
                if (st > 0) FillRound(g, new Rectangle(rc.X + S(6), rc.Y + S(2), rc.Width - S(12), rc.Height - S(4)), S(4), st == 2 ? RowPress : RowHover);
            }

            /// <summary>A Windows 11 toggle switch.</summary>
            void Switch(Graphics g, Rectangle r, bool on, int st)
            {
                int radius = r.Height / 2;
                if (on)
                {
                    FillRound(g, r, radius, st == 2 ? Shade(accent, -0.1f) : st == 1 ? Shade(accent, 0.08f) : accent);
                    int d = S(14);
                    using (var b = new SolidBrush(onAccent)) g.FillEllipse(b, r.Right - S(3) - d, r.Y + (r.Height - d) / 2, d, d);
                }
                else
                {
                    if (st > 0) FillRound(g, r, radius, Color.FromArgb(st == 2 ? 20 : 12, Color.White));
                    StrokeRound(g, r, radius, st > 0 ? Color.FromArgb(200, 200, 200) : Color.FromArgb(160, 160, 160));
                    int d = S(st == 2 ? 14 : 12);
                    using (var b = new SolidBrush(Color.FromArgb(208, 208, 208))) g.FillEllipse(b, r.X + (r.Height - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                }
            }

            static void Draw(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
            {
                TextRenderer.DrawText(g, s, f, r, c, flags);
            }

            static void FillRound(Graphics g, Rectangle r, int radius, Color c)
            {
                using (var p = RoundPath(r, radius))
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }

            static void StrokeRound(Graphics g, Rectangle r, int radius, Color c)
            {
                using (var p = RoundPath(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius))
                using (var pen = new Pen(c)) g.DrawPath(pen, p);
            }

            static GraphicsPath RoundPath(Rectangle r, int radius)
            {
                var p = new GraphicsPath();
                int d = Math.Max(1, radius * 2);
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            Font Px(string family, float px) { return new Font(family, px * k, GraphicsUnit.Pixel); }

            /// <summary>The user's accent colour, lightened the way Windows does for dark theme controls.</summary>
            static Color Accent()
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                    {
                        object v = key == null ? null : key.GetValue("AccentColor");
                        if (v is int)
                        {
                            int c = (int)v;   // AABBGGRR
                            return Shade(Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF), 0.3f);
                        }
                    }
                }
                catch { }
                return Shade(Color.FromArgb(0, 120, 212), 0.3f);
            }

            /// <summary>Towards white (f > 0) or black (f < 0).</summary>
            static Color Shade(Color c, float f)
            {
                Func<int, int> ch = v => f > 0 ? v + (int)((255 - v) * f) : (int)(v * (1 + f));
                return Color.FromArgb(ch(c.R), ch(c.G), ch(c.B));
            }

            static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255; }

            // ---- mouse ----

            int HitTest(Point p)
            {
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
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHover(HitTest(e.Location)); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(-1); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                pressed = HitTest(e.Location);
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                int hit = HitTest(e.Location);
                var click = pressed >= 0 && hit == pressed ? items[pressed].Click : null;
                pressed = -1;
                Invalidate();
                if (click != null) click();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (scrollMax == 0 || !listClip.Contains(e.Location)) return;
                scroll = Math.Max(0, Math.Min(scrollMax, scroll - Math.Sign(e.Delta) * rowH * 2));
                hover = -1;
                SetHover(HitTest(e.Location));
                Invalidate();
            }
        }
    }
}
