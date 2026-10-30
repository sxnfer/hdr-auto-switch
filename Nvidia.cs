// The NVIDIA driver's scaling default ("Adjust desktop size and position" › Scaling in NVIDIA Control Panel), read and
// switched through NVAPI – the documented NvAPI_DISP_GetDisplayConfig / NvAPI_DISP_SetDisplayConfig, with function IDs and
// structure layouts from NVIDIA's public nvapi.h and nvapi_interface.h.
//
// Why: a game in exclusive fullscreen sets its own display mode without saying how to scale it, so the driver's default
// decides. With "Full screen" the competitive resolution would be stretched over the whole panel; with "No scaling" it
// stays centred. HDR Switch switches the default to No scaling only while a game with a competitive resolution runs.
//
// The switch only takes hold as the default when it is saved (NV_DISPLAYCONFIG_SAVE_TO_PERSISTENCE, as Control Panel
// does), so it survives a crash or reboot: TrayApp keeps the original value in its state file and puts it back.
// nvapi64.dll ships with the NVIDIA driver; it is loaded only for each call and unloaded right after, so there is no idle
// cost and nothing happens on other GPUs.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HdrSwitch
{
    public static class Nvidia
    {
        public const int FullScreen = 2;   // NV_SCALING_GPU_SCALING_TO_NATIVE: Force GPU – Full screen
        public const int NoScaling = 3;    // NV_SCALING_GPU_SCANOUT_TO_NATIVE: Force GPU – Centred / No scaling

        /// <summary>True for the scaling settings that already keep a smaller mode 1:1 in the middle.</summary>
        public static bool Centres(int scaling) { return scaling == 3 || scaling == 7; }   // GPU / Balanced – No scaling

        /// <summary>The driver's scaling setting for this display, or -1 (no NVIDIA driver, another GPU's display, error).</summary>
        public static int Scaling(string gdi)
        {
            int result = -1;
            Session(api =>
            {
                using (var c = api.Read())
                {
                    IntPtr d = c.Details(api.DisplayId(gdi));
                    if (d != IntPtr.Zero) result = Marshal.ReadInt32(d, 8);
                }
            });
            return result;
        }

        /// <summary>Saves a new scaling setting for this display, as Control Panel does. True if the driver took it.</summary>
        public static bool SetScaling(string gdi, int scaling)
        {
            bool ok = false;
            Session(api =>
            {
                using (var c = api.Read())
                {
                    IntPtr d = c.Details(api.DisplayId(gdi));
                    if (d == IntPtr.Zero) return;
                    Marshal.WriteInt32(d, 8, scaling);
                    ok = api.Set(c.Count, c.Paths, SAVE_TO_PERSISTENCE) == 0;
                }
            });
            return ok;
        }

        // ---- NVAPI session: load, initialise, call, unload ----

        static void Session(Action<Api> work)
        {
            IntPtr lib = LoadLibraryW(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
            if (lib == IntPtr.Zero) return;   // no NVIDIA driver
            try
            {
                IntPtr qi = GetProcAddress(lib, "nvapi_QueryInterface");
                if (qi == IntPtr.Zero) return;
                var api = new Api((QueryFn)Marshal.GetDelegateForFunctionPointer(qi, typeof(QueryFn)));
                if (api.Init == null || api.Init() != 0) return;
                try { work(api); }
                catch (Exception) { }   // a driver that answers differently than documented: leave everything as it is
                finally { if (api.Unload != null) api.Unload(); }
            }
            finally { FreeLibrary(lib); }
        }

        sealed class Api
        {
            public readonly InitFn Init, Unload;
            public readonly GetCfgFn Get;
            public readonly SetCfgFn Set;
            readonly IdFn id;

            public Api(QueryFn q)
            {
                Init = Fn<InitFn>(q, 0x0150e828);
                Unload = Fn<InitFn>(q, 0xd22bdd7e);
                Get = Fn<GetCfgFn>(q, 0x11abccf8);
                Set = Fn<SetCfgFn>(q, 0x5d8cf8de);
                id = Fn<IdFn>(q, 0xae457190);
            }

            static T Fn<T>(QueryFn q, uint fid) where T : class
            {
                IntPtr p = q(fid);
                return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
            }

            /// <summary>NVAPI's display id for \\.\DISPLAYn, or 0.</summary>
            public uint DisplayId(string gdi)
            {
                uint d;
                return id != null && id(gdi, out d) == 0 ? d : 0;
            }

            public Config Read()
            {
                if (Get == null || Set == null) throw new InvalidOperationException();
                return new Config(Get);
            }
        }

        /// <summary>The current display configuration in unmanaged memory, read in NVAPI's three passes.</summary>
        sealed class Config : IDisposable
        {
            static readonly int P = IntPtr.Size;
            static readonly int PathSize = P == 8 ? 48 : 28, TargetSize = P == 8 ? 24 : 12;
            static readonly int PathTargets = P == 8 ? 16 : 12, PathSource = PathTargets + P;   // offsets in NV_DISPLAYCONFIG_PATH_INFO_V2
            static readonly int TargetDetails = P;                                               // offset in NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2
            const int SourceSize = 32, DetailsSize = 128;                                        // NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1, ADVANCED_TARGET_INFO_V1
            static readonly uint PathVer = (uint)PathSize | (2u << 16);
            const uint DetailsVer = DetailsSize | (1u << 16);

            public uint Count;
            public IntPtr Paths;
            readonly List<IntPtr> blocks = new List<IntPtr>();

            public Config(GetCfgFn get)
            {
                if (get(ref Count, IntPtr.Zero) != 0 || Count == 0) throw new InvalidOperationException();
                Paths = Alloc((int)Count * PathSize);
                for (int i = 0; i < Count; i++) Marshal.WriteInt32(Path(i), 0, (int)PathVer);
                if (get(ref Count, Paths) != 0) throw new InvalidOperationException();
                for (int i = 0; i < Count; i++)
                {
                    IntPtr p = Path(i);
                    int targets = Marshal.ReadInt32(p, 8);
                    Marshal.WriteIntPtr(p, PathSource, Alloc(SourceSize));
                    IntPtr t = Alloc(Math.Max(1, targets) * TargetSize);
                    Marshal.WriteIntPtr(p, PathTargets, t);
                    for (int j = 0; j < targets; j++)
                    {
                        IntPtr d = Alloc(DetailsSize);
                        Marshal.WriteInt32(d, 0, (int)DetailsVer);
                        Marshal.WriteIntPtr((IntPtr)((long)t + j * TargetSize), TargetDetails, d);
                    }
                }
                if (get(ref Count, Paths) != 0) throw new InvalidOperationException();
            }

            IntPtr Path(int i) { return (IntPtr)((long)Paths + i * PathSize); }

            /// <summary>The advanced target info of this display's path, or IntPtr.Zero.</summary>
            public IntPtr Details(uint displayId)
            {
                if (displayId == 0) return IntPtr.Zero;
                for (int i = 0; i < Count; i++)
                {
                    IntPtr p = Path(i), t = Marshal.ReadIntPtr(p, PathTargets);
                    int targets = Marshal.ReadInt32(p, 8);
                    for (int j = 0; j < targets; j++)
                    {
                        IntPtr target = (IntPtr)((long)t + j * TargetSize);
                        if ((uint)Marshal.ReadInt32(target, 0) == displayId) return Marshal.ReadIntPtr(target, TargetDetails);
                    }
                }
                return IntPtr.Zero;
            }

            IntPtr Alloc(int size)
            {
                IntPtr p = Marshal.AllocHGlobal(size);
                for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
                blocks.Add(p);
                return p;
            }

            public void Dispose()
            {
                foreach (var b in blocks) Marshal.FreeHGlobal(b);
                blocks.Clear();
            }
        }

        const uint SAVE_TO_PERSISTENCE = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetCfgFn(ref uint count, IntPtr paths);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetCfgFn(uint count, IntPtr paths, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdFn([MarshalAs(UnmanagedType.LPStr)] string name, out uint id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr lib, string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr lib);
    }
}
