// The island: a small pill that drops in at the top-centre of the screen when something happens – HDR came on,
// a game closed, an update is there – and leaves again by itself. Compact (a dot and a few words), expanded (a title
// and a second line), or split (the pill and a small bubble beside it).
//
// It must never get in a game's way, so:
//  - It is a layered window drawn through UpdateLayeredWindow: smooth edges, truly transparent around the pill, and no
//    painting messages. It never takes focus (WS_EX_NOACTIVATE, MA_NOACTIVATE) and has no taskbar or Alt+Tab entry.
//  - It only shows while Windows says notifications are welcome (SHQueryUserNotificationState). With a game in
//    exclusive fullscreen, any fullscreen window in front, or presentation mode it stays away: what it had to say
//    waits up to 10 s for a better moment, then goes out as an ordinary notification instead, if it has one.
//  - While a game runs, clicks go straight through it (WS_EX_TRANSPARENT).
// Hidden means hidden: no window on screen, no timer, no bitmap.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace HdrSwitch
{
    sealed class Island : Form
    {
        /// <summary>One thing to show. The newest is on screen; when it ends, the one before it comes back.</summary>
        public sealed class Act
        {
            public string Id;               // starting another with the same Id replaces this one
            public string Title, Sub;       // expanded, held for 2.5 s first – Sub == null: compact from the start
            public string Text;             // compact
            public Func<string> Live;       // compact text that changes while it shows ("SDR in 3s")
            public Color Dot = Theme.Text1;
            public bool Split;              // compact, with a bubble beside the pill
            public Color Bubble = Theme.Grey;
            public bool Hold;               // stays until End(Id)
            public double Seconds = 3;      // how long it stays compact otherwise
            public string Balloon;          // said as a notification instead, if the island can't show in time
            internal bool Big;
            internal double Left;
            internal int Waited;
        }

        /// <summary>What is drawn: a snapshot of an Act, so the previous one can fade out as the next fades in.</summary>
        sealed class View
        {
            public bool Big, Split;
            public string Title, Sub, Text;
            public Color Dot, Bubble;
        }

        const double BigSeconds = 2.5;
        const int WaitSeconds = 10;   // Poll comes once a second

        readonly Action click, hidden;
        readonly Func<bool> inGame;
        readonly Func<Screen> where;
        readonly Action<string> balloon;
        readonly List<Act> stack = new List<Act>(), pending = new List<Act>();
        readonly System.Windows.Forms.Timer life = new System.Windows.Forms.Timer { Interval = 200 };   // runs only while it shows
        readonly Stopwatch lifeClock = new Stopwatch();
        readonly Spring w = Motion.Shape(0, 0.25), h = Motion.Shape(0, 0.25), r = Motion.Shape(0, 0.25), drop = Motion.Shape(0, 0.25);
        readonly Spring bubble = Motion.Shape(0, 0.004), alpha = Motion.Fade(0), content = Motion.Fade(1);
        readonly Func<double, bool> tick;
        readonly StringFormat fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };

        float k = 1;
        Theme.Fonts fonts;
        Size size;                       // the window: wide enough for the largest pill, as tall as it plus its way in
        IntPtr dc, dib, oldBitmap;       // the 32-bit premultiplied surface UpdateLayeredWindow shows
        Bitmap surface;
        Graphics g;
        View cur, prev;
        bool leaving, hovering, through = true;

        /// <summary>
        /// click: the island was clicked (never while inGame says a game runs). where: the screen to show on.
        /// balloon: says a text as a notification. hidden: it has just left the screen.
        /// </summary>
        public Island(Action click, Func<bool> inGame, Func<Screen> where, Action<string> balloon, Action hidden)
        {
            this.click = click;
            this.inGame = inGame;
            this.where = where;
            this.balloon = balloon;
            this.hidden = hidden;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Display Switch";
            fmt.FormatFlags |= StringFormatFlags.NoWrap;
            tick = Tick;
            life.Tick += (s, e) => Live();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style = unchecked((int)0x80000000);   // WS_POPUP
                cp.ExStyle |= 0x80000 | 0x08000000 | 0x80 | 0x8 | 0x20;   // WS_EX_LAYERED, NOACTIVATE, TOOLWINDOW, TOPMOST, TRANSPARENT
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE → MA_NOACTIVATE: a click never takes focus
            base.WndProc(ref m);
        }

        // ---- activities ----

        /// <summary>Shows this now, on top of whatever is showing – or keeps it for a better moment (see the top of this file).</summary>
        public void Start(Act a)
        {
            Remove(a.Id);
            a.Big = a.Sub != null;
            a.Left = a.Big ? BigSeconds : a.Seconds;
            if (!Welcome())
            {
                a.Waited = 0;
                pending.Add(a);
                return;
            }
            stack.Add(a);
            Present();
        }

        /// <summary>Ends an activity. If it was the one showing, the one before it returns, or the island leaves.</summary>
        public void End(string id)
        {
            bool top = stack.Count > 0 && stack[stack.Count - 1].Id == id;
            Remove(id);
            if (!top) return;
            if (stack.Count > 0) Present();
            else Depart();
        }

        /// <summary>Drops everything: the island was switched off.</summary>
        public void Clear()
        {
            stack.Clear();
            pending.Clear();
            Depart();
        }

        /// <summary>Called once a second by the app's process scan: no timer of its own while hidden.</summary>
        public void Poll()
        {
            if (pending.Count == 0) return;
            if (Welcome())
            {
                stack.AddRange(pending);
                pending.Clear();
                Present();
                return;
            }
            foreach (var a in pending.ToArray())
            {
                if (++a.Waited < WaitSeconds) continue;
                pending.Remove(a);
                if (a.Balloon != null && balloon != null) balloon(a.Balloon);
            }
        }

        /// <summary>The display changed (a competitive resolution, a monitor): move to the new top-centre.</summary>
        public void Relayout()
        {
            if (!Visible || leaving || stack.Count == 0) return;
            Place();
            Present();
        }

        void Remove(string id)
        {
            stack.RemoveAll(a => a.Id == id);
            pending.RemoveAll(a => a.Id == id);
        }

        /// <summary>Windows would show a notification right now: nothing fullscreen in front, no presentation, a user at the desk.</summary>
        static bool Welcome()
        {
            int state;
            try { if (Native.SHQueryUserNotificationState(out state) != 0) return true; }
            catch (EntryPointNotFoundException) { return true; }
            return state == 5 || state == 6;   // QUNS_ACCEPTS_NOTIFICATIONS, QUNS_QUIET_TIME (only toasts are held back)
        }

        // ---- showing, morphing, leaving ----

        /// <summary>Puts the newest activity on screen: the pill morphs to its shape while the old content fades into the new.</summary>
        void Present()
        {
            var a = stack[stack.Count - 1];
            if (a.Live != null) a.Text = a.Live();
            bool wasVisible = Visible;
            if (!wasVisible)
            {
                Motion.Refresh();
                Place();
            }
            var v = new View { Big = a.Big, Split = a.Split && !a.Big, Title = a.Title, Sub = a.Sub, Text = a.Text ?? a.Title, Dot = a.Dot, Bubble = a.Bubble };
            float tw, th, tr;
            Measure(v, out tw, out th, out tr);
            if (!wasVisible)
            {
                // It grows out of a small pill that slides down from the screen's edge.
                w.Snap(tw * 0.6); h.Snap(th); r.Snap(tr);
                drop.Snap(-S(28)); alpha.Snap(0); bubble.Snap(0);
                prev = null;
            }
            else prev = leaving ? null : cur;
            cur = v;
            leaving = false;
            w.To(tw); h.To(th); r.To(tr); drop.To(0); alpha.To(1);
            bubble.To(v.Split ? 1 : 0);
            content.Snap(prev == null ? 1 : 0);
            content.To(1);
            if (!wasVisible)
            {
                Visible = true;   // SW_SHOWNOACTIVATE, see ShowWithoutActivation
                lifeClock.Restart();
                life.Start();
            }
            SetThrough(inGame());
            Render();
            Motion.Run(tick);
        }

        void Depart()
        {
            if (!Visible) return;
            leaving = true;
            alpha.To(0);
            drop.To(-S(20));
            w.To(w.Value * 0.7);
            if (Motion.Reduced) Vanish();
            else Motion.Run(tick);
        }

        void Vanish()
        {
            leaving = false;
            hovering = false;
            life.Stop();
            Visible = false;
            FreeSurface();
            cur = prev = null;
            if (hidden != null) hidden();
        }

        bool Tick(double dt)
        {
            if (!Visible) return false;
            bool moving = w.Step(dt) | h.Step(dt) | r.Step(dt) | drop.Step(dt) | bubble.Step(dt) | alpha.Step(dt) | content.Step(dt);
            if (leaving && alpha.Value < 0.02) { Vanish(); return false; }
            Render();
            return moving;
        }

        /// <summary>
        /// Five times a second while it shows: counts the showing activity down (not while the mouse rests on it),
        /// refreshes a live text, and leaves at once if something went fullscreen meanwhile.
        /// </summary>
        void Live()
        {
            double dt = lifeClock.Elapsed.TotalSeconds;
            lifeClock.Restart();
            if (leaving || stack.Count == 0) return;
            if (!Welcome()) { stack.Clear(); Depart(); return; }
            SetThrough(inGame());
            var a = stack[stack.Count - 1];
            if (a.Live != null && a.Live() != cur.Text) Present();
            if (hovering && !through) return;
            a.Left -= dt;
            if (a.Left > 0) return;
            if (a.Big) { a.Big = false; a.Left = a.Seconds; Present(); }
            else if (!a.Hold) End(a.Id);
        }

        void SetThrough(bool on)
        {
            if (on == through || !IsHandleCreated) return;
            through = on;
            int ex = Native.GetWindowLong(Handle, -20);   // GWL_EXSTYLE
            Native.SetWindowLong(Handle, -20, on ? ex | 0x20 : ex & ~0x20);
            if (on) hovering = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            through = true;   // as created, see CreateParams
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || through) return;
            stack.Clear();
            Depart();
            if (click != null) click();
        }

        // ---- layout and drawing ----

        int S(double v) { return (int)Math.Round(v * k); }

        /// <summary>Top-centre of the screen, under a taskbar that sits at the top. Fonts and the surface follow the screen's scale.</summary>
        void Place()
        {
            var scr = where() ?? Screen.PrimaryScreen;
            var b = scr.Bounds;
            float scale = Theme.Scale(new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
            if (fonts == null || scale != k)
            {
                if (fonts != null) fonts.Dispose();
                k = scale;
                fonts = new Theme.Fonts(k);
            }
            var s = new Size(Math.Min(b.Width, S(540)), S(10) + S(64) + S(12));
            Bounds = new Rectangle(b.X + (b.Width - s.Width) / 2, Math.Max(b.Top, scr.WorkingArea.Top), s.Width, s.Height);
            if (s != size || surface == null)
            {
                size = s;
                CreateSurface();
            }
        }

        void Measure(View v, out float tw, out float th, out float tr)
        {
            float max = size.Width - S(36) - S(6) - S(8);
            if (v.Big)
            {
                float text = Math.Max(TextWidth(v.Title, fonts.IslandTitle), TextWidth(v.Sub, fonts.IslandSub));
                tw = Math.Min(max, Math.Max(S(300), S(18) + S(10) + S(14) + text + S(24)));
                th = S(64);
                tr = S(28);
            }
            else
            {
                tw = Math.Min(max, S(12) + S(8) + S(8) + TextWidth(v.Text, fonts.IslandText) + S(16));
                th = S(36);
                tr = S(18);
            }
        }

        float TextWidth(string s, Font f) { return (float)Math.Ceiling(g.MeasureString(s ?? "", f, 100000, fmt).Width) + 1; }

        void Render()
        {
            if (g == null) return;
            g.Clear(Color.Transparent);
            float W = (float)w.Value, H = (float)h.Value, R = (float)Math.Min(r.Value, H / 2), b = (float)Math.Max(0, bubble.Value);
            float bd = S(36), gap = S(6);
            float x = (size.Width - W - b * (gap + bd)) / 2, y = S(10) + (float)drop.Value;
            var pill = new RectangleF(x, y, W, H);
            Theme.Fill(g, pill, R, Theme.Glass);
            Theme.Stroke(g, pill, R, Theme.GlassLine);
            using (var clip = Theme.Round(pill, R))
            {
                g.SetClip(clip);   // text that is still too wide for a growing pill stays inside it
                double c = Math.Max(0, Math.Min(1, content.Value));
                if (prev != null && c < 1) Draw(prev, pill, 1 - c);
                Draw(cur, pill, c);
                g.ResetClip();
            }
            if (b > 0.02f)
            {
                float d = bd * Math.Min(1, b);
                var br = new RectangleF(pill.Right + gap * Math.Min(1, b), y + (S(36) - d) / 2, d, d);
                Theme.Fill(g, br, d / 2, Theme.Glass);
                Theme.Stroke(g, br, d / 2, Theme.GlassLine);
                Theme.Dot(g, br.X + d / 2, br.Y + d / 2, S(10) * Math.Min(1, b), cur.Bubble);
            }
            g.Flush(FlushIntention.Sync);

            var blend = new Native.BlendFunction { SourceConstantAlpha = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha.Value))), AlphaFormat = 1 };   // AC_SRC_ALPHA
            Point dst = Location, src = Point.Empty;
            Size sz = size;
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref sz, dc, ref src, 0, ref blend, 2);   // ULW_ALPHA
        }

        void Draw(View v, RectangleF pill, double a)
        {
            if (a <= 0.01) return;
            if (v.Big)
            {
                float tx = pill.X + S(18) + S(10) + S(14), tw = pill.Right - S(24) - tx;
                Theme.Dot(g, pill.X + S(18) + S(5), pill.Y + pill.Height / 2, S(10), Theme.Fade(v.Dot, a));
                using (var b1 = new SolidBrush(Theme.Fade(Theme.Text1, a)))
                using (var b2 = new SolidBrush(Theme.Fade(Color.FromArgb(180, 180, 187), a)))
                {
                    g.DrawString(v.Title, fonts.IslandTitle, b1, new RectangleF(tx, pill.Y + S(11), tw, S(22)), fmt);
                    g.DrawString(v.Sub, fonts.IslandSub, b2, new RectangleF(tx, pill.Y + S(33), tw, S(20)), fmt);
                }
            }
            else
            {
                float tx = pill.X + S(12) + S(8) + S(8), tw = pill.Right - S(14) - tx;
                Theme.Dot(g, pill.X + S(12) + S(4), pill.Y + pill.Height / 2, S(8), Theme.Fade(v.Dot, a));
                using (var b1 = new SolidBrush(Theme.Fade(Theme.Text1, a)))
                    g.DrawString(v.Text, fonts.IslandText, b1, new RectangleF(tx, pill.Y, tw, pill.Height), fmt);
            }
        }

        /// <summary>A top-down 32-bit DIB that GDI+ draws into directly and UpdateLayeredWindow reads from: no copy per frame.</summary>
        void CreateSurface()
        {
            FreeSurface();
            IntPtr bits;
            var bi = new Native.BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            dc = Native.CreateCompatibleDC(IntPtr.Zero);
            dib = Native.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBitmap = Native.SelectObject(dc, dib);
            surface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
            g = Graphics.FromImage(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // ClearType has no meaning on a transparent surface
        }

        void FreeSurface()
        {
            if (g != null) { g.Dispose(); g = null; }
            if (surface != null) { surface.Dispose(); surface = null; }
            if (dc != IntPtr.Zero)
            {
                Native.SelectObject(dc, oldBitmap);
                Native.DeleteObject(dib);
                Native.DeleteDC(dc);
                dc = dib = oldBitmap = IntPtr.Zero;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                life.Dispose();
                FreeSurface();
                if (fonts != null) fonts.Dispose();
                fmt.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- preview ----

        /// <summary>
        /// --island-demo: every state in turn on the screen the mouse is on, three rounds; a click on the island ends
        /// it early. Nothing else of the app runs – no tray icon, no HDR switching – so it can run beside an installed copy.
        /// </summary>
        public static void Demo()
        {
            int step = 0;
            var until = Stopwatch.StartNew();
            var island = new Island(Application.Exit, () => false, () => Screen.FromPoint(Cursor.Position), null, null);
            var next = new System.Windows.Forms.Timer { Interval = 500 };
            next.Tick += (s, e) =>
            {
                switch (step++ % 7)
                {
                    case 0:
                        island.Start(new Act { Id = "hdr", Title = "Cyberpunk 2077", Sub = "HDR on · 2368 × 1332", Text = "HDR on", Dot = Theme.Amber });
                        next.Interval = 6500;
                        break;
                    case 1:
                        until.Restart();
                        island.Start(new Act { Id = "sdr", Hold = true, Dot = Theme.Amber, Live = () => "SDR in " + Math.Max(1, 3 - (int)until.Elapsed.TotalSeconds) + "s" });
                        next.Interval = 3000;
                        break;
                    case 2:
                        island.Start(new Act { Id = "sdr", Text = "SDR", Dot = Theme.Grey, Seconds = 1.5 });
                        next.Interval = 2800;
                        break;
                    case 3:
                        island.Start(new Act { Id = "rec", Text = "OBS Studio · SDR", Dot = Theme.Grey, Split = true, Bubble = Theme.Rec, Seconds = 4 });
                        next.Interval = 5000;
                        break;
                    case 4:
                        island.Start(new Act { Id = "update", Title = "Version 1.5.0 is available", Sub = "Click to update", Text = "Update available" });
                        next.Interval = 6500;
                        break;
                    case 5:
                        island.Start(new Act { Id = "hdr", Title = "Counter-Strike 2", Sub = "2368 × 1332", Text = "2368 × 1332" });
                        next.Interval = 3500;   // the next one arrives while this is still up: it morphs instead of leaving
                        break;
                    case 6:
                        island.Start(new Act { Id = "scan", Text = "3 new games found", Balloon = "Found 3 new games." });
                        next.Interval = 4500;
                        if (step >= 21) { next.Stop(); var end = new System.Windows.Forms.Timer { Interval = 4000 }; end.Tick += (s2, e2) => Application.Exit(); end.Start(); }
                        break;
                }
            };
            next.Start();
            Application.Run();
            island.Dispose();
        }
    }
}
