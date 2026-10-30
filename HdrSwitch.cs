// HDR Switch – a tiny tray app: Windows HDR turns on while a game from your list runs
// and goes back to SDR a few seconds after it closes.
//
// Games are found automatically (Steam, Epic, GOG, Ubisoft and game folders on every drive) and caught
// as soon as their process starts – Steam games even earlier, the moment Play is pressed – so HDR is on
// before the game checks for it. Only HDR this app turned on is ever switched back off.
//
// A game can also have a competitive resolution (resolutions.txt): while it runs the desktop switches to a
// smaller mode shown 1:1 in the middle of the panel, and goes back to the saved mode after it closes. Same
// rules: temporary, and only a mode this app set is ever put back.
//
// Build with build.cmd (uses the C# compiler that ships with Windows / .NET Framework 4.8).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("HDR Switch")]
[assembly: System.Reflection.AssemblyProduct("HDR Switch")]
// AssemblyVersion: written by build.cmd into Version.g.cs

namespace HdrSwitch
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // --updated: started by the old version during an update – wait for it to exit instead of giving up.
            bool updated = args.Contains("--updated");
            using (var mutex = new Mutex(false, @"Local\HdrSwitch.SingleInstance"))
            {
                bool mine;
                try { mine = mutex.WaitOne(updated ? 15000 : 0); }
                catch (AbandonedMutexException) { mine = true; }
                if (!mine)
                {
                    MessageBox.Show("HDR Switch is already running – look for its icon in the tray.", "HDR Switch");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Application.Run(new TrayApp(updated, args.Contains("--open")));
                mutex.ReleaseMutex();
            }
        }
    }

    sealed partial class TrayApp : ApplicationContext
    {
        const int SdrDelayMs = 3000;   // wait after the last game closes (covers launchers that restart the game)
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "HDR Switch";
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrSwitch");
        static readonly string ListFile = Path.Combine(Dir, "games.txt");
        static readonly string ProfilesFile = Path.Combine(Dir, "resolutions.txt");
        static readonly string StateFile = Path.Combine(Dir, "resolution.state");   // the mode this app set – for crash recovery

        sealed class Running { public string Name; public RegisteredWaitHandle Wait; public WaitHandle Handle; }
        sealed class ActiveMode { public string Gdi; public Resolution.Mode Mode, Back; public string Game; }   // Back: the saved mode it returns to

        readonly SynchronizationContext ui = SynchronizationContext.Current;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Icon iconOn = MakeIcon(Color.FromArgb(255, 170, 0), Color.Black);
        readonly Icon iconOff = MakeIcon(Color.FromArgb(120, 120, 120), Color.White);
        readonly System.Windows.Forms.Timer sdrTimer = new System.Windows.Forms.Timer { Interval = SdrDelayMs };
        readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer { Interval = 1000 };     // catches games as their process starts
        readonly System.Windows.Forms.Timer steamTimeout = new System.Windows.Forms.Timer { Interval = 60000 }; // Play pressed, but no game appeared
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 300 };   // debounces edits to games.txt / resolutions.txt
        readonly System.Windows.Forms.Timer modeCheck = new System.Windows.Forms.Timer { Interval = 2000 };   // the display changed under us: still our mode?
        readonly FileSystemWatcher watcher;
        readonly SteamWatcher steam;
        readonly EventHandler onDisplayChanged;
        readonly HashSet<string> hinted = new HashSet<string>();      // games already told "restart if HDR is greyed out"
        readonly HashSet<string> resHinted = new HashSet<string>();   // games already told "restart if it doesn't fit"
        readonly HashSet<string> resFailed = new HashSet<string>();   // games already told their resolution doesn't exist
        bool resWarned;                                               // told once that the driver isn't centring

        HashSet<string> games = new HashSet<string>();
        HashSet<string> excluded = new HashSet<string>();                 // "-name.exe" lines: never switch, never re-added by a scan
        Dictionary<string, string> labels = new Dictionary<string, string>();   // name → note shown in the list ("Steam: Cyberpunk 2077")
        Dictionary<string, Resolution.Mode> profiles = new Dictionary<string, Resolution.Mode>();   // name → competitive resolution (Hz 0: current rate)
        Dictionary<string, string> profileLabels = new Dictionary<string, string>();               // notes from resolutions.txt, used if games.txt has none
        bool scanning;
        readonly Dictionary<int, Running> running = new Dictionary<int, Running>();
        readonly List<Hdr.Display> owned = new List<Hdr.Display>();   // displays this app switched to HDR
        ActiveMode res;                                               // the competitive mode this app set, while it is set
        Flyout flyout;
        Updater.Release update;   // a newer release, once found
        bool updating;

        public TrayApp(bool updated, bool open)   // --open: show the flyout right away (development, screenshots)
        {
            Directory.CreateDirectory(Dir);
            Updater.CleanUp();
            bool firstRun = !File.Exists(ListFile);
            LoadList();
            LoadProfiles();
            flyout = new Flyout(this);

            tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) flyout.Toggle(); };
            tray.Visible = true;

            sdrTimer.Tick += (s, e) => { sdrTimer.Stop(); Settle(); };
            scanTimer.Tick += (s, e) => Rescan();
            steamTimeout.Tick += (s, e) => { steamTimeout.Stop(); Settle(); };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); LoadList(); LoadProfiles(); ForgetRemoved(); Rescan(); ReapplyProfiles(); };
            modeCheck.Tick += (s, e) => { modeCheck.Stop(); OnDisplayChanged(false); };

            watcher = new FileSystemWatcher(Dir, "*.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (s, e) => ui.Post(_ => { reloadTimer.Stop(); reloadTimer.Start(); }, null);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            watcher.EnableRaisingEvents = true;

            steam = new SteamWatcher(id => ui.Post(_ => OnSteamStarted(id), null));
            onDisplayChanged = (s, e) => ui.Post(_ => OnDisplayChanged(true), null);
            SystemEvents.DisplaySettingsChanged += onDisplayChanged;

            RecoverResolution();   // a previous run was killed while a game had its resolution
            Rescan();              // games that were already running
            if (res != null && !running.Values.Any(r => profiles.ContainsKey(r.Name))) RevertResolution();
            scanTimer.Start();
            UpdateTray();

            // Look for newly installed games: right away on first run, otherwise once, 30 s after startup.
            if (firstRun) ScanInBackground(true);
            else
            {
                var later = new System.Windows.Forms.Timer { Interval = 30000 };
                later.Tick += (s, e) => { later.Dispose(); ScanInBackground(false); };
                later.Start();
            }

            var trim = new System.Windows.Forms.Timer { Interval = 5000 };
            trim.Tick += (s, e) => { trim.Dispose(); Trim(); };
            trim.Start();

            if (updated) Notify("Updated to version " + Updater.CurrentText + ".");
            if (open)
            {
                var t = new System.Windows.Forms.Timer { Interval = 500 };
                t.Tick += (s, e) => { t.Dispose(); flyout.Toggle(); };
                t.Start();
            }

            // Updates: first look 1 minute after startup, then once a day.
            if (Updater.Enabled)
            {
                var updates = new System.Windows.Forms.Timer { Interval = 60000 };
                updates.Tick += (s, e) => { updates.Interval = 24 * 60 * 60 * 1000; CheckForUpdate(); };
                updates.Start();
            }
        }

        // ---- updates ----

        void CheckForUpdate()
        {
            if (update != null || updating) return;
            new Thread(() =>
            {
                Updater.Release r = null;
                try { r = Updater.Latest(); } catch { }   // offline, rate-limited… try again tomorrow
                if (r != null) ui.Post(_ => OnUpdateFound(r), null);
            }) { IsBackground = true }.Start();
        }

        void OnUpdateFound(Updater.Release r)
        {
            update = r;
            flyout.Sync();
            Notify("Version " + r.VersionText + " is available – click the tray icon › Update.");
        }

        void InstallUpdate()
        {
            if (update == null || updating) return;
            updating = true;
            flyout.Sync();
            var r = update;
            new Thread(() =>
            {
                string error = null;
                try { Updater.Install(r); }
                catch (Exception e) { error = e.Message; }
                ui.Post(_ =>
                {
                    if (error == null) { ExitThread(); return; }   // the new version is starting
                    updating = false;
                    flyout.Sync();
                    Notify("Update failed: " + error);
                }, null);
            }) { IsBackground = true }.Start();
        }

        // ---- game tracking ----

        /// <summary>A game is followed if it is on the HDR list or has a competitive resolution.</summary>
        bool Followed(string name) { return games.Contains(name) || profiles.ContainsKey(name); }

        bool HdrRunning() { return running.Values.Any(r => games.Contains(r.Name)); }
        bool ProfiledRunning() { return running.Values.Any(r => profiles.ContainsKey(r.Name)); }

        /// <summary>Follows a running game: its resolution and/or HDR now, back once it closes.</summary>
        void Track(int pid, string name)
        {
            IntPtr h = Proc.OpenForWait(pid);
            var r = new Running { Name = name };
            if (h != IntPtr.Zero)   // otherwise its exit is noticed by the once-a-second process scan
            {
                r.Handle = new ProcessWaitHandle(h);
                r.Wait = ThreadPool.RegisterWaitForSingleObject(r.Handle,
                    (state, timedOut) => ui.Post(_ => Exited((int)state), null), pid, Timeout.Infinite, true);
            }
            running[pid] = r;

            sdrTimer.Stop();
            steamTimeout.Stop();
            // The mode first, HDR after it: a mode change is what could disturb HDR, not the other way round.
            if (profiles.ContainsKey(name) && ApplyResolution(name) && Proc.AgeSeconds(pid) > 5 && resHinted.Add(name))
                Notify(ResHint(name));
            if (games.Contains(name) && SwitchOn() && Proc.AgeSeconds(pid) > 5 && hinted.Add(name))
                Notify("HDR came on after " + DisplayName(name) + " had already started. "
                    + "If its HDR option is greyed out, restart the game.");
            if (NeedsSettling()) sdrTimer.Start();   // e.g. HDR was about to go off when a game that only has a resolution started
            UpdateTray();
        }

        /// <summary>Steam says Play was pressed: for a game from the list, the switch happens before the game even starts.</summary>
        void OnSteamStarted(int appId)
        {
            string label = GameScanner.SteamLabel(appId);
            if (label == null) return;
            string name = games.Concat(profiles.Keys).FirstOrDefault(g => LabelOf(g) == label);
            if (name == null) return;
            sdrTimer.Stop();
            if (profiles.ContainsKey(name)) ApplyResolution(name);
            if (games.Contains(name)) SwitchOn();
            steamTimeout.Stop();
            steamTimeout.Start();   // back to normal if the game doesn't show up within a minute
            UpdateTray();
        }

        void Exited(int pid)
        {
            Running r;
            if (!running.TryGetValue(pid, out r)) return;
            running.Remove(pid);
            if (r.Wait != null)
            {
                r.Wait.Unregister(null);
                r.Handle.Dispose();
            }
            if (NeedsSettling()) sdrTimer.Start();
            UpdateTray();
        }

        /// <summary>Something this app switched is no longer needed by a running game – and Steam isn't about to start one.</summary>
        bool NeedsSettling()
        {
            if (steamTimeout.Enabled) return false;
            return (owned.Count > 0 && !HdrRunning()) || (res != null && !ProfiledRunning());
        }

        /// <summary>After the grace period: whatever no running game needs any more goes back – SDR, the saved mode.</summary>
        void Settle()
        {
            if (!HdrRunning())
            {
                foreach (var d in owned) Hdr.Set(d, false);
                owned.Clear();
            }
            if (!ProfiledRunning()) RevertResolution();
            UpdateTray();
        }

        /// <summary>
        /// Once a second: spots games as soon as their process starts – while they are still loading, long before
        /// their window shows – so HDR is on by the time the game asks Windows about it. A process snapshot holds
        /// only names and ids, so this costs well under a millisecond.
        /// </summary>
        void Rescan()
        {
            bool unwaitable = running.Values.Any(r => r.Wait == null);
            if (games.Count == 0 && profiles.Count == 0 && !unwaitable) return;

            var alive = new HashSet<int>();
            foreach (var p in Proc.Snapshot())
            {
                alive.Add(p.Key);
                if (Followed(p.Value) && !running.ContainsKey(p.Key)) Track(p.Key, p.Value);
            }
            if (unwaitable)
                foreach (int pid in running.Where(kv => kv.Value.Wait == null && !alive.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                    Exited(pid);
        }

        /// <summary>Stops following running games that were taken off both lists.</summary>
        void ForgetRemoved()
        {
            foreach (int pid in running.Where(kv => !Followed(kv.Value.Name)).Select(kv => kv.Key).ToList())
                Exited(pid);
        }

        // ---- HDR ----

        /// <summary>True if HDR was switched on just now.</summary>
        bool SwitchOn()
        {
            var d = Hdr.Primary();
            if (d == null || d.On) return false;   // no HDR display, or HDR already on (not ours to switch back)
            if (!Hdr.Set(d, true)) return false;
            if (!owned.Any(o => o.Key == d.Key)) owned.Add(d);
            return true;
        }

        /// <summary>A mode change can take HDR down with it: put back what this app owns.</summary>
        void KeepHdr()
        {
            if (owned.Count == 0) return;
            var all = Hdr.All();
            foreach (var o in owned)
            {
                var d = all.FirstOrDefault(x => x.Key == o.Key);
                if (d != null && !d.On) Hdr.Set(d, true);
            }
        }

        // ---- competitive resolution ----

        /// <summary>The display the HDR feature uses; the main display if none can do HDR.</summary>
        static string TargetGdi()
        {
            var d = Hdr.Primary();
            return d != null && d.Gdi.Length > 0 ? d.Gdi : Screen.PrimaryScreen.DeviceName;
        }

        /// <summary>"2368 × 1332", or "2368 × 1332 @ 240 Hz" when the profile names a rate.</summary>
        static string ProfileText(Resolution.Mode p)
        {
            return p.Hz > 0 ? p.Text + " @ " + p.Hz + " Hz" : p.Text;
        }

        string ResHint(string name)
        {
            return "The screen switched to " + res.Mode.Text + " after " + DisplayName(name) + " had already started. "
                + "If the game doesn't fit the screen, restart it.";
        }

        /// <summary>
        /// Switches the desktop to a game's competitive resolution. The first profiled game to start sets it and it stays
        /// until no profiled game runs. True if the mode was switched just now.
        /// </summary>
        bool ApplyResolution(string name)
        {
            Resolution.Mode p;
            if (!profiles.TryGetValue(name, out p)) return false;
            if (res != null)
            {
                if (res.Game.Length == 0) res.Game = name;   // taken over from a previous run (see RecoverResolution)
                return false;
            }
            string gdi = TargetGdi();
            var now = Resolution.Current(gdi);
            if (now == null) return false;
            var mode = new Resolution.Mode { Width = p.Width, Height = p.Height, Hz = p.Hz > 0 ? p.Hz : now.Hz };
            if (mode.Same(now)) return false;   // already in that mode (set by hand): not ours to switch back
            if (!Resolution.Available(gdi, mode.Width, mode.Height, mode.Hz))
            {
                if (resFailed.Add(name))
                    Notify("No " + mode.Text + " at " + mode.Hz + " Hz for " + DisplayName(name) + ". Create " + mode.Width + "×" + mode.Height
                        + " at " + mode.Hz + " Hz as a custom resolution in NVIDIA Control Panel (Change resolution › Customize).");
                return false;
            }
            var before = Resolution.Read(gdi);
            if (!Resolution.Apply(gdi, mode.Width, mode.Height, mode.Hz))
            {
                if (resFailed.Add(name)) Notify("Windows didn't switch to " + mode.Text + " at " + mode.Hz + " Hz for " + DisplayName(name) + ".");
                return false;
            }
            res = new ActiveMode { Gdi = gdi, Mode = mode, Back = Resolution.Saved(gdi) ?? now, Game = name };
            SaveState();
            KeepHdr();

            // The panel should still get its native signal, with the smaller desktop centred in it.
            var after = Resolution.Read(gdi);
            if (!resWarned && before != null && after != null)
            {
                if (after.TargetW != before.TargetW || after.TargetH != before.TargetH)
                {
                    resWarned = true;
                    Notify("The monitor is scaling " + mode.Text + " itself. In NVIDIA Control Panel › Adjust desktop size and position, "
                        + "choose No scaling and Perform scaling on: GPU.");
                }
                else if (!after.Centered)
                {
                    resWarned = true;
                    Notify(mode.Text + " is being stretched. In NVIDIA Control Panel › Adjust desktop size and position, "
                        + "choose No scaling and untick Override the scaling mode.");
                }
            }
            return true;
        }

        /// <summary>Back to the saved mode – but only if the desktop is still in the mode this app set.</summary>
        void RevertResolution()
        {
            if (res == null) return;
            var r = res;
            res = null;
            DeleteState();
            if (!r.Mode.Same(Resolution.Current(r.Gdi))) return;   // changed meanwhile (settings, sleep, …): not ours any more
            Resolution.Restore(r.Gdi);
            KeepHdr();
        }

        /// <summary>
        /// Windows says the display settings changed. If the desktop isn't in our mode any more (display settings, sleep and
        /// resume…), it isn't ours to put back. Checked again 2 s later first, in case the change was a transient one.
        /// </summary>
        void OnDisplayChanged(bool first)
        {
            if (res != null && !res.Mode.Same(Resolution.Current(res.Gdi)))
            {
                if (first) { modeCheck.Stop(); modeCheck.Start(); return; }
                res = null;
                DeleteState();
            }
            UpdateTray();
        }

        /// <summary>Profiles changed while games run: a new or changed one applies at once, a removed one goes back.</summary>
        void ReapplyProfiles()
        {
            if (res != null)
            {
                Resolution.Mode p;
                bool keep = profiles.TryGetValue(res.Game, out p) && p.Width == res.Mode.Width && p.Height == res.Mode.Height && (p.Hz == 0 || p.Hz == res.Mode.Hz);
                if (!keep) RevertResolution();
            }
            foreach (var r in running.Values.ToList())
                if (profiles.ContainsKey(r.Name) && ApplyResolution(r.Name) && resHinted.Add(r.Name))
                    Notify(ResHint(r.Name));
            UpdateTray();
        }

        void SaveState()
        {
            try { File.WriteAllText(StateFile, res.Gdi + "|" + res.Mode.Width + "|" + res.Mode.Height + "|" + res.Mode.Hz); }
            catch (IOException) { }
        }

        void DeleteState()
        {
            try { File.Delete(StateFile); } catch (IOException) { }
        }

        /// <summary>
        /// A previous run was killed while a game had its resolution: if the desktop is still in that mode, take it over –
        /// the first Rescan adopts the running game, or the constructor puts the display back if the game is gone.
        /// (In practice Windows drops a CDS_FULLSCREEN mode the moment the process that set it dies, so this mostly
        /// just removes the stale file; it is here for the case where it doesn't.)
        /// </summary>
        void RecoverResolution()
        {
            string text;
            try
            {
                if (!File.Exists(StateFile)) return;
                text = File.ReadAllText(StateFile);
            }
            catch (IOException) { return; }
            string[] f = text.Trim().Split('|');
            int w, h, hz;
            if (f.Length != 4 || !int.TryParse(f[1], out w) || !int.TryParse(f[2], out h) || !int.TryParse(f[3], out hz)) { DeleteState(); return; }
            var mode = new Resolution.Mode { Width = w, Height = h, Hz = hz };
            if (!mode.Same(Resolution.Current(f[0]))) { DeleteState(); return; }   // a reboot or the user already put it back
            res = new ActiveMode { Gdi = f[0], Mode = mode, Back = Resolution.Saved(f[0]), Game = "" };
        }

        void LoadProfiles()
        {
            if (!File.Exists(ProfilesFile)) SaveProfiles();
            string[] lines;
            try { lines = File.ReadAllLines(ProfilesFile); }
            catch (IOException) { return; }   // file busy (being saved) – the watcher fires again

            var newProfiles = new Dictionary<string, Resolution.Mode>();
            var newLabels = new Dictionary<string, string>();
            foreach (string raw in lines)
            {
                string line = raw, label = "";
                int hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    label = line.Substring(hash + 1).Trim();
                    line = line.Substring(0, hash);
                }
                var m = ProfileLine.Match(line.Trim());
                if (!m.Success) continue;
                string name = Proc.Normalize(m.Groups[1].Value);
                if (name == null) continue;
                var p = new Resolution.Mode
                {
                    Width = int.Parse(m.Groups[2].Value), Height = int.Parse(m.Groups[3].Value),
                    Hz = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0
                };
                if (p.Width < 320 || p.Height < 200) continue;
                newProfiles[name] = p;
                if (label.Length > 0) newLabels[name] = label;
            }
            foreach (var kv in newProfiles)   // a changed line gets its "doesn't exist" notice again
            {
                Resolution.Mode old;
                if (!profiles.TryGetValue(kv.Key, out old) || !old.Same(kv.Value)) resFailed.Remove(kv.Key);
            }
            profiles = newProfiles;
            profileLabels = newLabels;
        }

        // "cs2.exe  2368x1332", "cs2.exe 2368 × 1332 @ 240" (the note after # is already gone)
        static readonly Regex ProfileLine = new Regex(@"^(.+?)\s+(\d{3,5})\s*[x×]\s*(\d{3,5})(?:\s*@\s*(\d{2,4})(?:\s*hz)?)?$", RegexOptions.IgnoreCase);

        void SaveProfiles()
        {
            var lines = new List<string>
            {
                "# HDR Switch - competitive resolutions. While one of these games runs, the screen switches to this",
                "# resolution, shown 1:1 in the middle with black borders, and goes back when the game closes.",
                "# One game per line: exe, resolution, optional @refresh rate (otherwise the current one). Text after # is a note.",
                "# The resolution must be in Windows' list: create it in NVIDIA Control Panel > Change resolution > Customize.",
                "# Easier: click the tray icon, then the monitor symbol next to a game. Changes apply as soon as you save.",
                ""
            };
            lines.AddRange(profiles.Keys.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                string line = (g + ".exe").PadRight(35) + " " + (profiles[g].Width + "x" + profiles[g].Height + (profiles[g].Hz > 0 ? "@" + profiles[g].Hz : "")).PadRight(16);
                string l = LabelOf(g);
                return l == g ? line.TrimEnd() : line + " # " + l;
            }));
            try { File.WriteAllLines(ProfilesFile, lines, Encoding.UTF8); } catch (IOException) { }
        }

        /// <summary>From the flyout: this game's resolution (null: off). Saved, and applied right away if the game runs.</summary>
        void SetProfile(string name, Resolution.Mode mode)
        {
            if (mode == null) profiles.Remove(name);
            else profiles[name] = mode;
            resFailed.Remove(name);
            SaveProfiles();
            ForgetRemoved();
            Rescan();
            ReapplyProfiles();
        }

        // ---- game list ----

        void LoadList()
        {
            if (!File.Exists(ListFile)) SaveList();
            string[] lines;
            try { lines = File.ReadAllLines(ListFile); }
            catch (IOException) { return; }   // file busy (being saved) – the watcher fires again

            var newGames = new HashSet<string>();
            var newExcluded = new HashSet<string>();
            var newLabels = new Dictionary<string, string>();
            foreach (string raw in lines)
            {
                string line = raw, label = "";
                int hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    label = line.Substring(hash + 1).Trim();
                    line = line.Substring(0, hash);
                }
                line = line.Trim();
                if (line.Length == 0) continue;
                bool off = line.StartsWith("-");
                string name = Proc.Normalize(off ? line.Substring(1) : line);
                if (name == null) continue;
                (off ? newExcluded : newGames).Add(name);
                if (label.Length > 0) newLabels[name] = label;
            }
            newGames.ExceptWith(newExcluded);
            games = newGames;
            excluded = newExcluded;
            labels = newLabels;
        }

        void SaveList()
        {
            var lines = new List<string>
            {
                "# HDR Switch - HDR turns on while one of these runs. One exe per line; text after # is just a note.",
                "# Games are found automatically (Steam, Epic, GOG, Ubisoft, and game folders on your drives).",
                "# Missing one? Add its exe on a new line. Changes apply as soon as you save.",
                "# To stop a game switching HDR, put a - in front of it (deleting the line lets the next scan add it again).",
                ""
            };
            lines.AddRange(games.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).Select(g => ListLine(g, false)));
            if (excluded.Count > 0)
            {
                lines.Add("");
                lines.Add("# Never switch HDR for these:");
                lines.AddRange(excluded.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).Select(g => ListLine(g, true)));
            }
            try { File.WriteAllLines(ListFile, lines, Encoding.UTF8); } catch (IOException) { }
        }

        string LabelOf(string name)
        {
            string l;
            if (labels.TryGetValue(name, out l) && l.Length > 0) return l;
            return profileLabels.TryGetValue(name, out l) && l.Length > 0 ? l : name;
        }

        string ListLine(string name, bool off)
        {
            string line = (off ? "-" : "") + name + ".exe";
            string l;
            return labels.TryGetValue(name, out l) && l.Length > 0 ? line.PadRight(35) + " # " + l : line;
        }

        void ScanInBackground(bool announce)
        {
            if (scanning) return;
            scanning = true;
            var t = new Thread(() =>
            {
                List<GameScanner.Found> found;
                try { found = GameScanner.Scan(); }
                catch { found = new List<GameScanner.Found>(); }
                ui.Post(_ => { scanning = false; MergeFound(found, announce); }, null);
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        void MergeFound(List<GameScanner.Found> found, bool announce)
        {
            var added = found.Where(f => !games.Contains(f.Name) && !excluded.Contains(f.Name)).ToList();
            foreach (var f in added)
            {
                games.Add(f.Name);
                labels[f.Name] = f.Label;
            }
            // Games already on the list without a note (added by hand) get the launcher's name.
            var unnamed = found.Where(f => games.Contains(f.Name) && (!labels.ContainsKey(f.Name) || labels[f.Name] == "added by hand")).ToList();
            foreach (var f in unnamed) labels[f.Name] = f.Label;
            if (unnamed.Count > 0 && added.Count == 0) SaveList();
            if (added.Count > 0)
            {
                SaveList();
                Rescan();
            }
            if (added.Count > 0 || announce)
                Notify(added.Count == 0 ? "No new games found."
                    : "Found " + added.Count + " new game" + (added.Count == 1 ? "" : "s") + ": "
                      + string.Join(", ", added.Select(f => f.Label).Take(3)) + (added.Count > 3 ? "…" : ""));
            flyout.Sync();
            Trim();
        }

        void Add(string name)
        {
            games.Add(name);
            excluded.Remove(name);
            if (!labels.ContainsKey(name)) labels[name] = "added by hand";
            SaveList();
            Rescan();
        }

        void Remove(string name)
        {
            games.Remove(name);
            excluded.Add(name);   // so the next scan doesn't add it again
            SaveList();
            ForgetRemoved();
        }

        // ---- tray ----

        /// <summary>
        /// "HDR on – Dying Light: The Beast", "Waiting for a game", … (the tray icon's tooltip), with a second line
        /// such as "2368 × 1332 – Counter-Strike 2" while a competitive resolution is set.
        /// </summary>
        string StatusText()
        {
            var displays = Hdr.All();
            var hdrGames = running.Values.Where(r => games.Contains(r.Name)).Select(r => DisplayName(r.Name)).Distinct().ToList();
            string hdr = !displays.Any(d => d.Supported) ? "No HDR display found"
                : hdrGames.Count > 0 ? "HDR on – " + string.Join(", ", hdrGames)
                : displays.Any(d => d.On) ? "HDR is on" : "Waiting for a game";
            if (res == null) return hdr;
            string mode = res.Mode.Text + " – " + DisplayName(res.Game);
            return hdrGames.Count > 0 || displays.Any(d => d.On) ? hdr + "\n" + mode : mode;   // "Waiting for a game" adds nothing here
        }

        /// <summary>"Dying Light: The Beast" (launcher name without "Steam: "), or "game.exe".</summary>
        string DisplayName(string name)
        {
            string l = LabelOf(name);
            if (l == name || l == "added by hand") return name + ".exe";
            int colon = l.IndexOf(": ");
            return colon > 0 && colon < 10 ? l.Substring(colon + 2) : l;
        }

        static IEnumerable<string> WindowedApps()
        {
            var skip = new HashSet<string> { "explorer", "hdrswitch", "applicationframehost", "textinputhost", "systemsettings",
                "shellexperiencehost", "searchhost", "startmenuexperiencehost", "lockapp", "notepad" };
            var names = new SortedSet<string>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        string n = p.ProcessName.ToLowerInvariant();
                        if (!skip.Contains(n) && p.MainWindowHandle != IntPtr.Zero) names.Add(n);
                    }
                    catch { }   // process ended meanwhile
                }
            }
            return names;
        }

        void UpdateTray()
        {
            tray.Icon = Hdr.All().Any(d => d.On) ? iconOn : iconOff;
            string text = "HDR Switch " + Updater.CurrentText + "\n" + StatusText();
            tray.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;   // NotifyIcon limit
            if (flyout != null) flyout.Sync();
        }

        void Notify(string text)
        {
            tray.ShowBalloonTip(3000, "HDR Switch", text, ToolTipIcon.None);
        }

        static bool IsStartup()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        static void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValue, false);
            }
        }

        static Icon MakeIcon(Color fill, Color text)
        {
            int s = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(s, s))
            using (var g = Graphics.FromImage(bmp))
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(fill))
            using (var pen = new SolidBrush(text))
            using (var font = new Font("Segoe UI", s * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int r = Math.Max(2, s / 4);
                path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                path.AddArc(s - 1 - r * 2, 0, r * 2, r * 2, 270, 90);
                path.AddArc(s - 1 - r * 2, s - 1 - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(0, s - 1 - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("H", font, pen, new RectangleF(0, 0, s, s + 1), fmt);
                return Icon.FromHandle(bmp.GetHicon());   // two icons for the app's lifetime – handles are never freed
            }
        }

        static void Trim()
        {
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        protected override void ExitThreadCore()
        {
            watcher.Dispose();
            steam.Dispose();
            SystemEvents.DisplaySettingsChanged -= onDisplayChanged;
            scanTimer.Stop();
            sdrTimer.Stop();
            steamTimeout.Stop();
            foreach (var r in running.Values.Where(x => x.Wait != null))
            {
                r.Wait.Unregister(null);
                r.Handle.Dispose();
            }
            running.Clear();
            Settle();   // SDR and the saved mode, whatever is still running
            flyout.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    /// <summary>
    /// Updates from this project's GitHub releases. Only api.github.com and the release downloads of this repository
    /// are ever contacted, and a download is only installed if its SHA256 matches the release's SHA256SUMS.txt.
    /// Install: the running exe is renamed to HdrSwitch.old.exe (Windows allows that), the new one takes its name
    /// and is started with --updated; it waits for this copy to exit and removes the old file.
    /// </summary>
    static class Updater
    {
        public const string Repo = "sxnfer/hdr-auto-switch";
        const string ExeName = "HdrSwitch.exe";
        static readonly string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

        public sealed class Release
        {
            public Version Version;
            public string ExeUrl, SumsUrl;
            public string VersionText { get { return Text(Version); } }
        }

        public static readonly Version Current = ThreeParts(typeof(Updater).Assembly.GetName().Version);
        public static string CurrentText { get { return Text(Current); } }

        /// <summary>Dev builds (0.0.0, built without a version) never update themselves.</summary>
        public static bool Enabled { get { return Current > new Version(0, 0, 0); } }

        /// <summary>The latest release if it is newer than this copy, otherwise null.</summary>
        public static Release Latest()
        {
            string json;
            using (var web = Client()) json = web.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            var data = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);

            Version v;
            string tag = data.ContainsKey("tag_name") ? data["tag_name"] as string : null;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            var r = new Release { Version = ThreeParts(v) };
            if (r.Version <= Current) return null;

            foreach (object o in (System.Collections.IEnumerable)data["assets"])
            {
                var a = o as Dictionary<string, object>;
                if (a == null) continue;
                string name = a["name"] as string, url = a["browser_download_url"] as string;
                if (url == null || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (name == ExeName) r.ExeUrl = url;
                else if (name == "SHA256SUMS.txt") r.SumsUrl = url;
            }
            return r.ExeUrl != null && r.SumsUrl != null ? r : null;
        }

        /// <summary>Downloads, checks and swaps in the new exe, then starts it. Throws with a short reason on failure.</summary>
        public static void Install(Release r)
        {
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            string fresh = Path.Combine(dir, "HdrSwitch.new.exe"), old = Path.Combine(dir, "HdrSwitch.old.exe");

            byte[] bytes;
            string sums;
            using (var web = Client())
            {
                sums = web.DownloadString(r.SumsUrl);
                bytes = web.DownloadData(r.ExeUrl);
            }

            var line = sums.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(" " + ExeName) || l.EndsWith("*" + ExeName));
            if (line == null) throw new Exception("the release has no checksum for " + ExeName + ".");
            string expected = line.Split(' ')[0].ToLowerInvariant();
            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new Exception("the download doesn't match its checksum – nothing was changed.");

            try
            {
                File.WriteAllBytes(fresh, bytes);
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
            }
            catch (UnauthorizedAccessException)
            {
                throw new Exception("no permission to write to " + dir + ".");
            }
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }   // put the running version back

            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = dir });
        }

        /// <summary>Removes what an update left behind (the previous exe can only go once it has exited).</summary>
        public static void CleanUp()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (string f in new[] { "HdrSwitch.old.exe", "HdrSwitch.new.exe" })
                try { File.Delete(Path.Combine(dir, f)); } catch { }
        }

        static System.Net.WebClient Client()
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var web = new System.Net.WebClient();
            web.Headers[System.Net.HttpRequestHeader.UserAgent] = "hdr-auto-switch/" + CurrentText;   // required by the GitHub API
            return web;
        }

        static Version ThreeParts(Version v)
        {
            return v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        static string Text(Version v)
        {
            return v.Major + "." + v.Minor + "." + v.Build;
        }
    }

    /// <summary>
    /// Steam marks a game as running (HKCU\Software\Valve\Steam\Apps\[id]\Running = 1) as soon as Play is pressed,
    /// before the game's process exists. Windows signals the registry change, so this costs nothing while idle.
    /// </summary>
    sealed class SteamWatcher : IDisposable
    {
        const string AppsKey = @"Software\Valve\Steam\Apps";
        const uint REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Action<int> onStarted;   // called on the watcher thread

        public SteamWatcher(Action<int> onStarted)
        {
            this.onStarted = onStarted;
            new Thread(Watch) { IsBackground = true, Name = "Steam watcher" }.Start();
        }

        void Watch()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AppsKey))
                using (var changed = new AutoResetEvent(false))
                {
                    if (key == null) return;   // no Steam
                    var wasRunning = RunningApps(key);
                    while (true)
                    {
                        if (RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true) != 0) return;
                        if (WaitHandle.WaitAny(new WaitHandle[] { stop, changed }) == 0) return;
                        var now = RunningApps(key);
                        foreach (int id in now)
                            if (!wasRunning.Contains(id)) onStarted(id);
                        wasRunning = now;
                    }
                }
            }
            catch { }   // Steam's key vanished or is unreadable: switching still works through the process scan
        }

        static HashSet<int> RunningApps(RegistryKey apps)
        {
            var ids = new HashSet<int>();
            foreach (string sub in apps.GetSubKeyNames())
            {
                int id;
                if (!int.TryParse(sub, out id)) continue;
                using (var k = apps.OpenSubKey(sub))
                {
                    object v = k == null ? null : k.GetValue("Running");
                    if (v is int && (int)v == 1) ids.Add(id);
                }
            }
            return ids;
        }

        public void Dispose()
        {
            stop.Set();
        }

        [DllImport("advapi32.dll")]
        static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree, uint filter, SafeWaitHandle ev, bool async);
    }

    sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr handle) { SafeWaitHandle = new SafeWaitHandle(handle, true); }
    }

    /// <summary>
    /// Finds installed games: launcher libraries first (Steam, Epic, GOG, Ubisoft), then the usual game folders
    /// on every fixed drive (XboxGames, Games, EA Games, …). For each game folder the largest exe that is not
    /// a setup / crash reporter / anti-cheat helper is taken as the game.
    /// </summary>
    public static class GameScanner
    {
        public sealed class Found { public string Name, Label; }

        static readonly string[] DriveRoots =
        {
            "Games", "XboxGames", "GOG Games", "Epic Games", @"Program Files\Epic Games", @"Program Files (x86)\GOG Galaxy\Games",
            @"Program Files\EA Games", @"Program Files (x86)\Origin Games", @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
            @"Program Files\Rockstar Games", @"SteamLibrary\steamapps\common", @"Program Files (x86)\Steam\steamapps\common",
            @"Program Files\Steam\steamapps\common"
        };

        // Not games: launcher/overlay tools that would turn HDR on whenever they are in front.
        static readonly Regex Tool = new Regex(@"Redistributable|Proton|Steam Linux Runtime|Steamworks|SteamVR|Wallpaper Engine|Lossless Scaling|Soundpad|Unreal Engine|Steam Controller Configs", RegexOptions.IgnoreCase);
        static readonly Regex SkipExe = new Regex(@"^(unins.*|setup.*|.*install.*|.*redist.*|dxsetup|ue\dprereq.*|.*crash.*|.*report.*|.*easyanticheat.*|eac(launcher)?|.*battleye.*|be_.*|.*update.*|.*helper.*|.*cef.*|.*browser.*|dotnet.*|vc_.*|.*touchup.*|.*cleanup.*|.*activation.*|quicksfv|.*benchmark.*|.*server.*|.*editor.*|.*diagnostic.*|.*dump.*|7z.*|python.*|.*register.*|vconsole.*|resourcecompiler|.*viewer|hlmv.*|.*tools?|steam|epicgameslauncher|galaxyclient|upc|ubisoftconnect|eadesktop|origin|rockstarservice|launcher|gamelaunchhelper)$", RegexOptions.IgnoreCase);
        static readonly Regex SkipDir = new Regex(@"^(_?commonredist|_?redist|redistributables?|directx|vcredist|dotnet.*|support|installers?|__installer|prereq.*|thirdparty|easyanticheat|battleye|crashpad|paks|movies?|videos?|audio|sounds?|localization|shaders?|__overlay|tools|mods?|\.git)$", RegexOptions.IgnoreCase);

        public static List<Found> Scan()
        {
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // game folder → label
            var exes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);      // exe named by a launcher → label
            Try(() => Steam(folders));
            Try(() => Epic(folders, exes));
            Try(() => Gog(folders, exes));
            Try(() => Ubisoft(folders));
            Try(() => Drives(folders));

            var found = new Dictionary<string, Found>();
            foreach (var kv in exes) Add(found, kv.Key, kv.Value);
            foreach (var kv in folders) Add(found, MainExe(kv.Key), kv.Value);
            return found.Values.ToList();
        }

        static void Add(Dictionary<string, Found> found, string exe, string label)
        {
            string name = exe == null ? null : Proc.Normalize(exe);
            if (name == null || SkipExe.IsMatch(name) || found.ContainsKey(name)) return;
            found[name] = new Found { Name = name, Label = label };
        }

        static void AddFolder(Dictionary<string, string> folders, string path, string label)
        {
            try
            {
                path = Path.GetFullPath(path).TrimEnd('\\');
                if (!folders.ContainsKey(path) && !Tool.IsMatch(label) && Directory.Exists(path)) folders[path] = label;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        /// <summary>Every Steam library's steamapps folder.</summary>
        static List<string> SteamApps()
        {
            var result = new List<string>();
            string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (steam == null) return result;
            var libs = new List<string> { steam };
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (string lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string apps = Path.Combine(lib, "steamapps");
                if (Directory.Exists(apps)) result.Add(apps);
            }
            return result;
        }

        /// <summary>"Steam: Dying Light: The Beast" for a Steam app id – the label the scan gives that game.</summary>
        public static string SteamLabel(int appId)
        {
            try
            {
                foreach (string apps in SteamApps())
                {
                    string acf = Path.Combine(apps, "appmanifest_" + appId + ".acf");
                    if (!File.Exists(acf)) continue;
                    string text = File.ReadAllText(acf);
                    return "Steam: " + (Vdf(text, "name") ?? Vdf(text, "installdir"));
                }
            }
            catch (IOException) { }
            return null;
        }

        static void Steam(Dictionary<string, string> folders)
        {
            foreach (string apps in SteamApps())
            {
                foreach (string acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(acf);
                    string dir = Vdf(text, "installdir");
                    if (dir != null) AddFolder(folders, Path.Combine(apps, "common", dir), "Steam: " + (Vdf(text, "name") ?? dir));
                }
            }
        }

        static void Epic(Dictionary<string, string> folders, Dictionary<string, string> exes)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return;
            foreach (string item in Directory.GetFiles(dir, "*.item"))
            {
                string json = File.ReadAllText(item);
                string loc = Json(json, "InstallLocation"), exe = Json(json, "LaunchExecutable");
                if (loc == null || json.IndexOf("\"games\"", StringComparison.OrdinalIgnoreCase) < 0) continue;   // AppCategories: games only
                string label = "Epic: " + (Json(json, "DisplayName") ?? Path.GetFileName(loc));
                AddFolder(folders, loc, label);
                if (!string.IsNullOrEmpty(exe) && !Tool.IsMatch(label)) exes[Path.Combine(loc, exe)] = label;
            }
        }

        static void Gog(Dictionary<string, string> folders, Dictionary<string, string> exes)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
            {
                if (key == null) return;
                foreach (string id in key.GetSubKeyNames())
                {
                    using (var g = key.OpenSubKey(id))
                    {
                        if (g == null || !string.IsNullOrEmpty(g.GetValue("dependsOn") as string)) continue;   // DLC: same folder as its game
                        string path = g.GetValue("path") as string, exe = g.GetValue("exe") as string;
                        string label = "GOG: " + (g.GetValue("gameName") as string ?? id);
                        if (path != null) AddFolder(folders, path, label);
                        if (exe != null) exes[exe] = label;
                    }
                }
            }
        }

        static void Ubisoft(Dictionary<string, string> folders)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launch\Installs"))
            {
                if (key == null) return;
                foreach (string id in key.GetSubKeyNames())
                {
                    using (var g = key.OpenSubKey(id))
                    {
                        string path = g == null ? null : g.GetValue("InstallDir") as string;
                        if (path != null) AddFolder(folders, path, "Ubisoft: " + Path.GetFileName(path.TrimEnd('\\', '/')));
                    }
                }
            }
        }

        static void Drives(Dictionary<string, string> folders)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                foreach (string root in DriveRoots)
                {
                    string dir = Path.Combine(drive.RootDirectory.FullName, root);
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (string game in Directory.GetDirectories(dir))
                            AddFolder(folders, game, Path.GetFileName(game));
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
        }

        /// <summary>The largest exe in a game folder (5 levels deep), skipping installers, redistributables and helpers.</summary>
        static string MainExe(string folder)
        {
            string best = null;
            long bestSize = 0;
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(folder, 0));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(cur.Key, "*.exe"))
                    {
                        if (SkipExe.IsMatch(Path.GetFileNameWithoutExtension(f))) continue;
                        long size = new FileInfo(f).Length;
                        if (size > bestSize) { best = f; bestSize = size; }
                    }
                    if (cur.Value < 5)
                        foreach (string d in Directory.EnumerateDirectories(cur.Key))
                            if (!SkipDir.IsMatch(Path.GetFileName(d))) stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return best;
        }

        static string Vdf(string text, string key)
        {
            var m = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static string Json(string text, string key)
        {
            var m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            try { return Regex.Unescape(m.Groups[1].Value); } catch (ArgumentException) { return m.Groups[1].Value; }
        }

        static void Try(Action a)
        {
            try { a(); } catch { }   // one broken library must not stop the rest of the scan
        }
    }

    static class Proc
    {
        const uint Synchronize = 0x100000;

        /// <summary>A handle to wait on for pid's exit, or IntPtr.Zero if Windows doesn't allow it.</summary>
        public static IntPtr OpenForWait(int pid)
        {
            return Native.OpenProcess(Synchronize, false, pid);
        }

        public static double AgeSeconds(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return (DateTime.Now - p.StartTime).TotalSeconds; }
            catch { return 0; }
        }

        /// <summary>All running processes as (pid, normalized name) – a light snapshot, no Process objects.</summary>
        public static List<KeyValuePair<int, string>> Snapshot()
        {
            var list = new List<KeyValuePair<int, string>>(400);
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == (IntPtr)(-1)) return list;
            try
            {
                var e = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    string name = e.ExeFile;
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    list.Add(new KeyValuePair<int, string>((int)e.ProcessId, name.ToLowerInvariant()));
                }
            }
            finally { Native.CloseHandle(snap); }
            return list;
        }

        const uint TH32CS_SNAPPROCESS = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);

        /// <summary>"C:\Games\Cyberpunk2077.exe" → "cyberpunk2077" (same form as Process.ProcessName).</summary>
        public static string Normalize(string s)
        {
            try
            {
                s = Path.GetFileName(s.Trim().Trim('"')).ToLowerInvariant();
                if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
                return s.Length > 0 ? s : null;
            }
            catch (ArgumentException) { return null; }
        }
    }

    /// <summary>Reads and switches Windows HDR per display (Windows display configuration API).</summary>
    public static class Hdr
    {
        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }

        public sealed class Display
        {
            public LUID Adapter;
            public uint Target;
            public string Gdi;       // \\.\DISPLAY1 – matches Screen.DeviceName
            public bool Supported;  // HDR can be turned on
            public bool On;         // HDR is on
            public string Key { get { return Adapter.High + ":" + Adapter.Low + ":" + Target; } }
        }

        public static List<Display> All()
        {
            var list = new List<Display>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint np, nm;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return list;
                var paths = new PathInfo[np];
                IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * 64);
                try
                {
                    int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                    if (r == ERROR_INSUFFICIENT_BUFFER) continue;   // a display was added meanwhile
                    if (r != 0) return list;
                    for (int i = 0; i < np; i++)
                    {
                        var src = new SourceName { H = Header(1, typeof(SourceName), paths[i].Source.Adapter, paths[i].Source.Id) };
                        var d = new Display
                        {
                            Adapter = paths[i].Target.Adapter,
                            Target = paths[i].Target.Id,
                            Gdi = DisplayConfigGetDeviceInfo(ref src) == 0 ? src.Gdi : ""
                        };
                        Read(d);
                        list.Add(d);
                    }
                    return list;
                }
                finally { Marshal.FreeHGlobal(modes); }
            }
            return list;
        }

        /// <summary>The main display if it can do HDR, otherwise the first one that can.</summary>
        public static Display Primary()
        {
            var hdr = All().Where(d => d.Supported).ToList();
            return hdr.FirstOrDefault(d => d.Gdi == Screen.PrimaryScreen.DeviceName) ?? hdr.FirstOrDefault();
        }

        public static bool Set(Display d, bool on)
        {
            // Windows 11 24H2+: SET_HDR_STATE. Older Windows: SET_ADVANCED_COLOR_STATE.
            var s = new SetState { H = Header(16, typeof(SetState), d.Adapter, d.Target), Value = on ? 1u : 0u };
            if (DisplayConfigSetDeviceInfo(ref s) == 0) return true;
            s.H.Type = 10;
            return DisplayConfigSetDeviceInfo(ref s) == 0;
        }

        static void Read(Display d)
        {
            var c2 = new ColorInfo2 { H = Header(15, typeof(ColorInfo2), d.Adapter, d.Target) };
            if (DisplayConfigGetDeviceInfo(ref c2) == 0)
            {
                d.Supported = (c2.Value & 0x10) != 0;   // highDynamicRangeSupported
                d.On = c2.ActiveColorMode == 2;         // DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR
                return;
            }
            var c = new ColorInfo { H = Header(9, typeof(ColorInfo), d.Adapter, d.Target) };
            if (DisplayConfigGetDeviceInfo(ref c) == 0)
            {
                d.Supported = (c.Value & 0x1) != 0 && (c.Value & 0x8) == 0;   // supported and not force-disabled
                d.On = (c.Value & 0x2) != 0;
            }
        }

        static DeviceInfoHeader Header(int type, Type structType, LUID adapter, uint id)
        {
            return new DeviceInfoHeader { Type = type, Size = Marshal.SizeOf(structType), Adapter = adapter, Id = id };
        }

        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)] struct PathSource { public LUID Adapter; public uint Id; public uint ModeIdx; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        struct PathTarget
        {
            public LUID Adapter; public uint Id; public uint ModeIdx; public int Technology; public int Rotation; public int Scaling;
            public uint RefreshNum, RefreshDen; public int ScanLineOrdering; public int Available; public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfoHeader { public int Type; public int Size; public LUID Adapter; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName { public DeviceInfoHeader H; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }
        [StructLayout(LayoutKind.Sequential)] struct ColorInfo { public DeviceInfoHeader H; public uint Value; public uint Encoding; public uint Bits; }
        [StructLayout(LayoutKind.Sequential)] struct ColorInfo2 { public DeviceInfoHeader H; public uint Value; public uint Encoding; public uint Bits; public uint ActiveColorMode; }
        [StructLayout(LayoutKind.Sequential)] struct SetState { public DeviceInfoHeader H; public uint Value; }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, IntPtr modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo2 info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SetState info);
    }

    static class Native
    {
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
