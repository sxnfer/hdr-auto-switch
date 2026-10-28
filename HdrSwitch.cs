// HDR Switch – a tiny tray app: Windows HDR turns on while a game from your list runs
// and goes back to SDR a few seconds after it closes.
//
// Games are found automatically (Steam, Epic, GOG, Ubisoft and game folders on every drive) and caught
// as soon as their process starts – Steam games even earlier, the moment Play is pressed – so HDR is on
// before the game checks for it. Only HDR this app turned on is ever switched back off.
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
        static void Main()
        {
            bool first;
            using (new Mutex(true, @"Local\HdrSwitch.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show("HDR Switch is already running – look for its icon in the tray.", "HDR Switch");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Application.Run(new TrayApp());
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const int SdrDelayMs = 3000;   // wait after the last game closes (covers launchers that restart the game)
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "HDR Switch";
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrSwitch");
        static readonly string ListFile = Path.Combine(Dir, "games.txt");

        sealed class Running { public string Name; public RegisteredWaitHandle Wait; public WaitHandle Handle; }

        readonly SynchronizationContext ui = SynchronizationContext.Current;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Icon iconOn = MakeIcon(Color.FromArgb(255, 170, 0), Color.Black);
        readonly Icon iconOff = MakeIcon(Color.FromArgb(120, 120, 120), Color.White);
        readonly System.Windows.Forms.Timer sdrTimer = new System.Windows.Forms.Timer { Interval = SdrDelayMs };
        readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer { Interval = 1000 };     // catches games as their process starts
        readonly System.Windows.Forms.Timer steamTimeout = new System.Windows.Forms.Timer { Interval = 60000 }; // Play pressed, but no game appeared
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 300 };   // debounces edits to games.txt
        readonly FileSystemWatcher watcher;
        readonly SteamWatcher steam;
        readonly HashSet<string> hinted = new HashSet<string>();   // games already told "restart if HDR is greyed out"

        HashSet<string> games = new HashSet<string>();
        HashSet<string> excluded = new HashSet<string>();                 // "-name.exe" lines: never switch, never re-added by a scan
        Dictionary<string, string> labels = new Dictionary<string, string>();   // name → note shown in the list ("Steam: Cyberpunk 2077")
        bool scanning;
        readonly Dictionary<int, Running> running = new Dictionary<int, Running>();
        readonly List<Hdr.Display> owned = new List<Hdr.Display>();   // displays this app switched to HDR
        ToolStripMenuItem status, gamesMenu, startup;
        static readonly object GameToggle = new object();   // Tag of the game tick items
        bool keepOpen;

        public TrayApp()
        {
            Directory.CreateDirectory(Dir);
            bool firstRun = !File.Exists(ListFile);
            LoadList();
            BuildMenu();

            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            sdrTimer.Tick += (s, e) => { sdrTimer.Stop(); BackToSdr(); };
            scanTimer.Tick += (s, e) => Rescan();
            steamTimeout.Tick += (s, e) => { steamTimeout.Stop(); if (running.Count == 0) BackToSdr(); };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); LoadList(); ForgetRemoved(); Rescan(); };

            watcher = new FileSystemWatcher(Dir, "games.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (s, e) => ui.Post(_ => { reloadTimer.Stop(); reloadTimer.Start(); }, null);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            watcher.EnableRaisingEvents = true;

            steam = new SteamWatcher(id => ui.Post(_ => OnSteamStarted(id), null));

            Rescan();   // games that were already running
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
        }

        // ---- game tracking ----

        /// <summary>Follows a running game from the list: HDR on now, back to SDR once it closes.</summary>
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
            if (SwitchOn() && Proc.AgeSeconds(pid) > 5 && hinted.Add(name))
                Notify("HDR came on after " + DisplayName(name) + " had already started. "
                    + "If its HDR option is greyed out, restart the game.");
            UpdateTray();
        }

        /// <summary>Steam says Play was pressed: for a game from the list, HDR goes on before the game even starts.</summary>
        void OnSteamStarted(int appId)
        {
            string label = GameScanner.SteamLabel(appId);
            if (label == null || !games.Any(g => LabelOf(g) == label)) return;
            sdrTimer.Stop();
            SwitchOn();
            steamTimeout.Stop();
            steamTimeout.Start();   // back to SDR if the game doesn't show up within a minute
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
            if (running.Count == 0) sdrTimer.Start();
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
            if (games.Count == 0 && !unwaitable) return;

            var alive = new HashSet<int>();
            foreach (var p in Proc.Snapshot())
            {
                alive.Add(p.Key);
                if (games.Contains(p.Value) && !running.ContainsKey(p.Key)) Track(p.Key, p.Value);
            }
            if (unwaitable)
                foreach (int pid in running.Where(kv => kv.Value.Wait == null && !alive.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                    Exited(pid);
        }

        /// <summary>Stops following running games that were taken off the list.</summary>
        void ForgetRemoved()
        {
            foreach (int pid in running.Where(kv => !games.Contains(kv.Value.Name)).Select(kv => kv.Key).ToList())
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

        void BackToSdr()
        {
            if (running.Count > 0) return;
            foreach (var d in owned) Hdr.Set(d, false);
            owned.Clear();
            UpdateTray();
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
            return labels.TryGetValue(name, out l) && l.Length > 0 ? l : name;
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

        void BuildMenu()
        {
            status = new ToolStripMenuItem { Enabled = false };
            gamesMenu = new ToolStripMenuItem("Games");
            gamesMenu.DropDownItems.Add("…");   // placeholder so the submenu arrow shows
            gamesMenu.DropDownOpening += (s, e) => FillGames();
            startup = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!startup.Checked));

            menu.Items.AddRange(new ToolStripItem[]
            {
                status, new ToolStripSeparator(),
                gamesMenu, startup, new ToolStripSeparator(),
                new ToolStripMenuItem("Exit", null, (s, e) => ExitThread())
            });
            menu.Opening += (s, e) => RefreshMenu();
            menu.Closed += (s, e) => Trim();

            // Ticking games keeps the menu open, so several can be changed in one go.
            gamesMenu.DropDown.ItemClicked += (s, e) => keepOpen = e.ClickedItem.Tag == GameToggle;
            menu.ItemClicked += (s, e) => keepOpen = false;
            ToolStripDropDownClosingEventHandler stayOpen = (s, e) =>
            {
                if (keepOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            gamesMenu.DropDown.Closing += stayOpen;
            menu.Closing += stayOpen;
        }

        /// <summary>"HDR on – Dying Light: The Beast", "Waiting for a game", …</summary>
        string StatusText()
        {
            var displays = Hdr.All();
            if (!displays.Any(d => d.Supported)) return "No HDR display found";
            if (running.Count > 0) return "HDR on – " + string.Join(", ", running.Values.Select(r => DisplayName(r.Name)).Distinct());
            return displays.Any(d => d.On) ? "HDR is on" : "Waiting for a game";
        }

        /// <summary>"Dying Light: The Beast" (launcher name without "Steam: "), or "game.exe".</summary>
        string DisplayName(string name)
        {
            string l = LabelOf(name);
            if (l == name || l == "added by hand") return name + ".exe";
            int colon = l.IndexOf(": ");
            return colon > 0 && colon < 10 ? l.Substring(colon + 2) : l;
        }

        void RefreshMenu()
        {
            status.Text = StatusText();
            gamesMenu.Text = "Games (" + games.Count + ")";
            startup.Checked = IsStartup();
        }

        void FillGames()
        {
            var items = gamesMenu.DropDownItems;
            foreach (var old in items.Cast<ToolStripItem>().ToList()) old.Dispose();

            // Ticked: HDR switches for it. Unticked games stay listed so a slip is one click to undo.
            foreach (string g in games.Concat(excluded).OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                string name = g;
                var item = new ToolStripMenuItem(DisplayName(name)) { Checked = games.Contains(name), Tag = GameToggle };
                item.Click += (s, e) =>
                {
                    if (games.Contains(name)) Remove(name);
                    else Add(name);
                    item.Checked = games.Contains(name);
                };
                items.Add(item);
            }
            if (games.Count + excluded.Count == 0) items.Add(new ToolStripMenuItem(scanning ? "Looking for games…" : "No games found") { Enabled = false });
            items.Add(new ToolStripSeparator());

            var add = new ToolStripMenuItem("Add a running program");
            foreach (string n in WindowedApps().Where(n => !games.Contains(n)))
            {
                string name = n;
                add.DropDownItems.Add(name + ".exe", null, (s, e) => Add(name));
            }
            add.Enabled = add.DropDownItems.Count > 0;
            items.Add(add);
            items.Add(new ToolStripMenuItem(scanning ? "Looking for games…" : "Look for new games", null, (s, e) => ScanInBackground(true)) { Enabled = !scanning });
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
            string text = "HDR Switch\n" + StatusText();
            tray.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;   // NotifyIcon limit
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
            scanTimer.Stop();
            foreach (var r in running.Values.Where(x => x.Wait != null))
            {
                r.Wait.Unregister(null);
                r.Handle.Dispose();
            }
            running.Clear();
            BackToSdr();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
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
    }
}
