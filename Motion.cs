// Motion for the island and the flyout: damped springs instead of fixed-length eases, so a target that changes
// half-way (a hover that leaves, an island that grows again) just bends the movement instead of restarting it.
//
// The driver steps once per composed frame: a background thread waits in DwmFlush – which returns at the compositor's
// next frame – and posts one step to the UI thread. It only lives while something moves; with every spring at rest
// there is no thread and no timer. Why not a WinForms timer: WM_TIMER comes at ~15.6 ms, uneven, and getting it
// finer needs timeBeginPeriod, which raises the timer interrupt rate (for the whole system on older Windows 10).
// That is kept only as the fallback for a desktop DwmFlush can't wait on.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace HdrSwitch
{
    /// <summary>A damped spring (mass 1), stepped by real elapsed time.</summary>
    sealed class Spring
    {
        public double Value, Velocity, Target;
        public double Stiffness, Damping;
        readonly double rest;   // closer to the target than this, and slow: it stops

        public Spring(double stiffness, double damping, double value, double rest)
        {
            Stiffness = stiffness;
            Damping = damping;
            Value = Target = value;
            this.rest = rest;
        }

        public bool Moving { get { return Value != Target || Velocity != 0; } }

        /// <summary>A new target. With Windows animations switched off it is simply there.</summary>
        public void To(double target)
        {
            Target = target;
            if (Motion.Reduced) Snap(target);
        }

        public void Snap(double value)
        {
            Value = Target = value;
            Velocity = 0;
        }

        /// <summary>Advances by dt seconds. True while it is still moving.</summary>
        public bool Step(double dt)
        {
            if (!Moving) return false;
            while (dt > 0)   // semi-implicit Euler in steps of 4 ms at most: stable for every spring used here
            {
                double h = Math.Min(dt, 0.004);
                Velocity += (Stiffness * (Target - Value) - Damping * Velocity) * h;
                Value += Velocity * h;
                dt -= h;
            }
            if (Math.Abs(Target - Value) < rest && Math.Abs(Velocity) < rest * 20) Snap(Target);
            return Moving;
        }
    }

    static class Motion
    {
        // The feel, in one place. Damping 2·√stiffness is critical: as fast as it goes without overshoot.
        /// <summary>The island's shape: a little bounce (damping ratio 0.68, ~5 % overshoot, ~0.36 s).</summary>
        public static Spring Shape(double v, double rest) { return new Spring(260, 22, v, rest); }
        /// <summary>Opacity, 0…1 (~0.25 s).</summary>
        public static Spring Fade(double v) { return new Spring(300, 35, v, 0.004); }
        /// <summary>Hover and press crossfades, 0…1 (~0.15 s).</summary>
        public static Spring Control(double v) { return new Spring(1600, 80, v, 0.004); }
        /// <summary>A switch's thumb and track, 0…1 (~0.22 s).</summary>
        public static Spring Thumb(double v) { return new Spring(700, 53, v, 0.004); }
        /// <summary>The flyout sliding in, 0…1 (~0.28 s); Closing makes the same spring leave faster (~0.2 s).</summary>
        public static Spring Open(double v) { return new Spring(420, 41, v, 0.002); }
        public static void Closing(Spring s) { s.Stiffness = 800; s.Damping = 57; }
        public static void Opening(Spring s) { s.Stiffness = 420; s.Damping = 41; }
        /// <summary>The list's scroll position, in pixels (~0.26 s).</summary>
        public static Spring Scroll(double v) { return new Spring(500, 45, v, 0.4); }

        /// <summary>Windows' "Animation effects" setting is off: everything snaps. Read again with Refresh.</summary>
        public static bool Reduced;

        public static void Refresh()
        {
            bool on;
            Reduced = Native.SystemParametersInfo(0x1042, 0, out on, 0) && !on;   // SPI_GETCLIENTAREAANIMATION
        }

        const double MinFrame = 0.006;   // at most ~165 steps a second, whatever the monitor's refresh rate

        static readonly List<Func<double, bool>> ticks = new List<Func<double, bool>>();   // UI thread only
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static SynchronizationContext ui;
        static volatile int generation;   // the pump that may run; an older one sees the change and ends
        static volatile bool running;
        static int posted;
        static double last;

        /// <summary>
        /// Calls tick(dt) on this (the UI) thread once per frame until it returns false. Adding one that is already
        /// running does nothing.
        /// </summary>
        public static void Run(Func<double, bool> tick)
        {
            if (!ticks.Contains(tick)) ticks.Add(tick);
            if (running) return;
            running = true;
            ui = SynchronizationContext.Current;
            last = clock.Elapsed.TotalSeconds;
            int mine = ++generation;
            new Thread(() => Pump(mine)) { IsBackground = true, Name = "Motion" }.Start();
        }

        static void Pump(int mine)
        {
            bool fine = false;   // timeBeginPeriod is in effect
            double sent = 0;
            while (running && generation == mine)
            {
                if (Native.DwmFlush() != 0)
                {
                    if (!fine) { Native.timeBeginPeriod(1); fine = true; }
                    Thread.Sleep(8);
                }
                double now = clock.Elapsed.TotalSeconds;
                if (now - sent < MinFrame) continue;
                sent = now;
                if (Interlocked.Exchange(ref posted, 1) == 0) ui.Post(Frame, null);   // never queue a second step behind a slow one
            }
            if (fine) Native.timeEndPeriod(1);
        }

        static void Frame(object unused)
        {
            posted = 0;
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - last);
            last = now;
            for (int i = ticks.Count - 1; i >= 0; i--)
                if (!ticks[i](dt)) ticks.RemoveAt(i);
            if (ticks.Count == 0) running = false;   // the pump ends; the next Run starts a new one
        }
    }
}
