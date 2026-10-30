// Display modes for the competitive resolution: a smaller desktop shown 1:1 in the middle of the panel.
// Static P/Invoke wrapper in the style of Hdr, over the classic display settings API (EnumDisplaySettingsEx /
// ChangeDisplaySettingsEx) with the display configuration API (QueryDisplayConfig) to check the result.
//
// Why ChangeDisplaySettingsEx: with CDS_FULLSCREEN the change is temporary by design – Windows never writes it
// to the registry, so a reboot, sign-out or a NULL-mode call always comes back at the mode saved in display
// settings. DM_DISPLAYFIXEDOUTPUT = DMDFO_CENTER asks the driver to keep sending the panel its native signal and
// to show the smaller desktop centred, with black borders (what NVIDIA calls "No scaling").

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdrSwitch
{
    public static class Resolution
    {
        public sealed class Mode
        {
            public int Width, Height, Hz;
            public string Text { get { return Width + " × " + Height; } }
            public bool Same(Mode m) { return m != null && m.Width == Width && m.Height == Height && m.Hz == Hz; }
        }

        /// <summary>What the display path looks like: the desktop (source) and the signal the panel gets (target).</summary>
        public sealed class Picture
        {
            public int SourceW, SourceH, TargetW, TargetH;
            public int Scaling;   // DISPLAYCONFIG_SCALING: 1 identity, 2 centered, 3 stretched, 4 aspect ratio, 128 preferred
            public bool Centered { get { return Scaling == 2 || (Scaling == 1 && SourceW == TargetW && SourceH == TargetH); } }
        }

        /// <summary>The mode the desktop is in right now, or null.</summary>
        public static Mode Current(string gdi) { return Enum(gdi, ENUM_CURRENT_SETTINGS); }

        /// <summary>The mode saved in Windows display settings – what a reboot comes back at – or null.</summary>
        public static Mode Saved(string gdi) { return Enum(gdi, ENUM_REGISTRY_SETTINGS); }

        /// <summary>Every size the display offers at this refresh rate (any scaling variant), largest first.</summary>
        public static List<Mode> Modes(string gdi, int hz)
        {
            var seen = new HashSet<string>();
            var list = new List<Mode>();
            for (int i = 0; ; i++)
            {
                var dm = NewMode();
                if (!EnumDisplaySettingsExW(gdi, i, ref dm, 0)) break;
                if ((int)dm.dmDisplayFrequency != hz || dm.dmBitsPerPel != 32 || dm.dmDisplayOrientation != 0) continue;
                if (seen.Add(dm.dmPelsWidth + "x" + dm.dmPelsHeight))
                    list.Add(new Mode { Width = (int)dm.dmPelsWidth, Height = (int)dm.dmPelsHeight, Hz = hz });
            }
            return list.OrderByDescending(m => (long)m.Width * m.Height).ToList();
        }

        public static bool Available(string gdi, int w, int h, int hz)
        {
            return Modes(gdi, hz).Any(m => m.Width == w && m.Height == h);
        }

        /// <summary>
        /// Switches the desktop on this display to w×h at hz, centred on the panel, until Restore (or a reboot).
        /// Nothing is written to the registry.
        /// </summary>
        public static bool Apply(string gdi, int w, int h, int hz)
        {
            var dm = NewMode();
            dm.dmPelsWidth = (uint)w;
            dm.dmPelsHeight = (uint)h;
            dm.dmDisplayFrequency = (uint)hz;
            dm.dmDisplayFixedOutput = DMDFO_CENTER;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_DISPLAYFIXEDOUTPUT;
            return ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }

        /// <summary>Switches to this mode and saves it in display settings, as the Settings app does.</summary>
        public static bool Save(string gdi, Mode m)
        {
            var dm = NewMode();
            dm.dmPelsWidth = (uint)m.Width;
            dm.dmPelsHeight = (uint)m.Height;
            dm.dmDisplayFrequency = (uint)m.Hz;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            return ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }

        /// <summary>The desktop is in the mode saved in display settings.</summary>
        public static bool AtSaved(string gdi)
        {
            var now = Current(gdi);
            return now != null && now.Same(Saved(gdi));
        }

        /// <summary>Back to the mode saved in display settings, scaling included.</summary>
        public static bool Restore(string gdi)
        {
            return ChangeDisplaySettingsExW(gdi, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }

        /// <summary>Source and target size of this display's active path, or null if it isn't found.</summary>
        public static Picture Read(string gdi)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint np, nm;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return null;
                var paths = new PathInfo[np];
                IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * ModeSize);
                try
                {
                    int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                    if (r == ERROR_INSUFFICIENT_BUFFER) continue;
                    if (r != 0) return null;
                    for (int i = 0; i < np; i++)
                    {
                        var src = new SourceName { H = new DeviceInfoHeader { Type = 1, Size = Marshal.SizeOf(typeof(SourceName)), Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id } };
                        if (DisplayConfigGetDeviceInfo(ref src) != 0 || src.Gdi != gdi) continue;
                        if (paths[i].Source.ModeIdx >= nm || paths[i].Target.ModeIdx >= nm) return null;
                        // DISPLAYCONFIG_MODE_INFO: type, id, adapter (16 bytes), then the union.
                        // Source: width, height at 16. Target: videoSignalInfo at 16, whose activeSize is at +24.
                        IntPtr s = (IntPtr)((long)modes + paths[i].Source.ModeIdx * ModeSize);
                        IntPtr t = (IntPtr)((long)modes + paths[i].Target.ModeIdx * ModeSize);
                        return new Picture
                        {
                            SourceW = Marshal.ReadInt32(s, 16), SourceH = Marshal.ReadInt32(s, 20),
                            TargetW = Marshal.ReadInt32(t, 40), TargetH = Marshal.ReadInt32(t, 44),
                            Scaling = paths[i].Target.Scaling
                        };
                    }
                    return null;
                }
                finally { Marshal.FreeHGlobal(modes); }
            }
            return null;
        }

        /// <summary>The panel's diagonal in inches from the monitor's EDID, or 0 if it doesn't say.</summary>
        public static double Inches(string gdi)
        {
            try
            {
                var mon = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
                if (!EnumDisplayDevicesW(gdi, 0, ref mon, EDD_GET_DEVICE_INTERFACE_NAME)) return 0;
                // \\?\DISPLAY#DELA252#5&312b7513&0&UID4355#{guid} → Enum\DISPLAY\DELA252\5&312b7513&0&UID4355
                string[] parts = mon.DeviceId.Split('#');
                if (parts.Length < 3) return 0;
                byte[] edid;
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + @"\" + parts[2] + @"\Device Parameters"))
                    edid = k == null ? null : k.GetValue("EDID") as byte[];
                if (edid == null || edid.Length < 128) return 0;
                double wCm = edid[21], hCm = edid[22];
                if (edid[54] != 0 || edid[55] != 0)   // first descriptor is a detailed timing: image size in mm
                {
                    int wMm = edid[66] | ((edid[68] & 0xF0) << 4), hMm = edid[67] | ((edid[68] & 0x0F) << 8);
                    if (wMm > 0 && hMm > 0) { wCm = wMm / 10.0; hCm = hMm / 10.0; }
                }
                if (wCm <= 0 || hCm <= 0) return 0;
                return Math.Sqrt(wCm * wCm + hCm * hCm) / 2.54;
            }
            catch { return 0; }
        }

        static Mode Enum(string gdi, int which)
        {
            var dm = NewMode();
            if (!EnumDisplaySettingsExW(gdi, which, ref dm, 0) || dm.dmPelsWidth == 0) return null;
            return new Mode { Width = (int)dm.dmPelsWidth, Height = (int)dm.dmPelsHeight, Hz = (int)dm.dmDisplayFrequency };
        }

        static DevMode NewMode() { return new DevMode { dmSize = (ushort)Marshal.SizeOf(typeof(DevMode)) }; }

        const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2;
        const uint CDS_FULLSCREEN = 0x4, CDS_UPDATEREGISTRY = 0x1;
        const int DISP_CHANGE_SUCCESSFUL = 0;
        const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000, DM_DISPLAYFIXEDOUTPUT = 0x20000000;
        const uint DMDFO_CENTER = 2;
        const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const int ModeSize = 64;   // sizeof(DISPLAYCONFIG_MODE_INFO)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)] struct PathSource { public Hdr.LUID Adapter; public uint Id; public uint ModeIdx; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        struct PathTarget
        {
            public Hdr.LUID Adapter; public uint Id; public uint ModeIdx; public int Technology; public int Rotation; public int Scaling;
            public uint RefreshNum, RefreshDen; public int ScanLineOrdering; public int Available; public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfoHeader { public int Type; public int Size; public Hdr.LUID Adapter; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName { public DeviceInfoHeader H; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsExW(string device, int mode, ref DevMode dm, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string device, ref DevMode dm, IntPtr hwnd, uint flags, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice dd, uint flags);
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, IntPtr modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    }
}
