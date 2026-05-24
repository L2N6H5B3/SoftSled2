using System;
using System.Runtime.InteropServices;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Minimal P/Invoke surface for the bits of Direct3D 9 needed to back
    /// a WPF <c>D3DImage</c>. We don't take a NuGet dependency (SharpDX
    /// is end-of-life and Vortice requires .NET 5+) — D3D9 has a stable
    /// COM ABI that's straightforward to reach via raw function-pointer
    /// extraction from the COM vtable.
    ///
    /// What we use:
    /// <list type="bullet">
    ///   <item><c>Direct3DCreate9Ex</c> — entry point for IDirect3D9Ex (the
    ///   "Ex" variant is required for WDDM device-sharing semantics that
    ///   D3DImage prefers; falls back to plain IDirect3D9 if Ex isn't
    ///   available, but every Win 7+ system has Ex).</item>
    ///   <item><c>IDirect3D9Ex::CreateDeviceEx</c> — make an offscreen
    ///   render device (no swap chain, no real window — we just need a
    ///   device to own surfaces).</item>
    ///   <item><c>IDirect3DDevice9::CreateRenderTarget</c> — allocate a
    ///   lockable surface we can blit into and hand to D3DImage.</item>
    ///   <item><c>IDirect3DSurface9::LockRect / UnlockRect</c> — get a
    ///   CPU pointer to the surface memory; copy decoded BGRA32 there.</item>
    /// </list>
    ///
    /// We deliberately avoid declaring full <c>[ComImport]</c> interfaces
    /// (would require ~80 method declarations between IDirect3D9Ex and
    /// IDirect3DDevice9Ex), using <see cref="Marshal.GetDelegateForFunctionPointer{T}"/>
    /// on the vtable slot we want. This costs us ABI-fragility (the
    /// vtable indices are part of the API contract — they HAVE been
    /// stable since Vista, but we should document them for future-proofing).
    /// </summary>
    internal static unsafe class D3D9Interop {

        // D3D9 SDK version magic — must match the header value Microsoft
        // ships. The high bit (0x80000000) is the "debug" flag; we want
        // the release form. The low bits encode the SDK version number.
        public const uint D3D_SDK_VERSION = 32;
        public const int D3DADAPTER_DEFAULT = 0;

        // D3DDEVTYPE
        public const int D3DDEVTYPE_HAL = 1;

        // D3DCREATE_*  device behavior flags
        public const int D3DCREATE_SOFTWARE_VERTEXPROCESSING = 0x20;
        public const int D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x40;
        public const int D3DCREATE_MULTITHREADED = 0x4;
        public const int D3DCREATE_FPU_PRESERVE = 0x2;

        // D3DFORMAT (we only need ARGB32 / XRGB32 = BGRA / BGRX layout
        // in little-endian per the D3DFMT convention).
        public const int D3DFMT_X8R8G8B8 = 22;
        public const int D3DFMT_A8R8G8B8 = 21;

        // D3DPOOL
        public const int D3DPOOL_DEFAULT = 0;

        // D3DMULTISAMPLE
        public const int D3DMULTISAMPLE_NONE = 0;

        // LockRect flags
        public const int D3DLOCK_DISCARD       = 0x00002000;
        public const int D3DLOCK_NOOVERWRITE   = 0x00001000;
        public const int D3DLOCK_NOSYSLOCK     = 0x00000800;

        // D3DPRESENT_PARAMETERS (offscreen device — width/height ignored,
        // BackBufferFormat must still be a valid display format).
        [StructLayout(LayoutKind.Sequential)]
        public struct D3DPRESENT_PARAMETERS {
            public uint BackBufferWidth;
            public uint BackBufferHeight;
            public int  BackBufferFormat;
            public uint BackBufferCount;
            public int  MultiSampleType;
            public uint MultiSampleQuality;
            public int  SwapEffect;
            public IntPtr hDeviceWindow;
            public int  Windowed;
            public int  EnableAutoDepthStencil;
            public int  AutoDepthStencilFormat;
            public uint Flags;
            public uint FullScreen_RefreshRateInHz;
            public uint PresentationInterval;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DLOCKED_RECT {
            public int  Pitch;       // stride in bytes
            public IntPtr pBits;     // CPU pointer to surface memory
        }

        // ---- DllImport: D3D9 entry point ----

        [DllImport("d3d9.dll")]
        public static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr d3d9Ex);

        // ---- vtable indices ----
        // From the published d3d9.h interface declarations (Microsoft DDK).
        // Stable since Vista RTM.

        // IDirect3D9 (parent of IDirect3D9Ex). IUnknown takes 0/1/2.
        private const int IDirect3D9_CreateDevice = 16;
        // IDirect3D9Ex adds 4 methods after the base; CreateDeviceEx is the
        // last of those: 17+18+19 = GetAdapterModeCountEx/EnumAdapterModesEx/
        // GetAdapterDisplayModeEx, then 20 = CreateDeviceEx.
        private const int IDirect3D9Ex_CreateDeviceEx = 20;

        // IDirect3DDevice9. IUnknown takes 0/1/2.
        private const int IDirect3DDevice9_CreateRenderTarget = 28;

        // IDirect3DSurface9 inherits IDirect3DResource9 (slots 3-10) so
        // surface-specific methods start at 11.
        private const int IDirect3DSurface9_LockRect = 13;
        private const int IDirect3DSurface9_UnlockRect = 14;

        // ---- Delegate signatures for the vtable methods we call ----

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int CreateDeviceExFn(IntPtr self, uint adapter, int deviceType,
            IntPtr focusWindow, uint behaviorFlags, ref D3DPRESENT_PARAMETERS presentParameters,
            IntPtr fullscreenDisplayMode, out IntPtr device);

        // pSharedHandle is HANDLE* in the COM signature — a pointer
        // to HANDLE. C# 'ref IntPtr' marshals as exactly that. The
        // original signature passed IntPtr by value, which means we
        // were always sending NULL — telling D3D9 "I don't want a
        // shared surface". An unshared surface forces D3DImage to
        // fall back to software rendering: the WPF compositor reads
        // the surface contents back to system memory via Lock() and
        // re-uploads them through its own GPU device. That's 2 uploads
        // per frame + a CPU round-trip = exactly the kind of cost that
        // caps fps in the 10-fps range on a 1080p stream.
        //
        // With a real ref-IntPtr (and *handle = 0 going in), D3D9
        // creates a shared surface and writes its share handle back.
        // The WPF MIL compositor can then open the same surface in
        // its own context without any read-back.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int CreateRenderTargetFn(IntPtr self, uint width, uint height,
            int format, int multiSample, uint multiSampleQuality, int lockable,
            out IntPtr surface, ref IntPtr sharedHandle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int LockRectFn(IntPtr self, out D3DLOCKED_RECT lockedRect,
            IntPtr rect, int flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int UnlockRectFn(IntPtr self);

        // ---- Helpers ----

        /// <summary>Read the function pointer at the given vtable index
        /// of a COM object and wrap as a managed delegate.</summary>
        public static T GetVMethod<T>(IntPtr comObj, int slotIndex) where T : Delegate {
            IntPtr vtbl = Marshal.ReadIntPtr(comObj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slotIndex * IntPtr.Size);
            return (T)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        /// <summary>Wrapper for IDirect3D9Ex::CreateDeviceEx.</summary>
        public static int CreateDeviceEx(IntPtr d3d9Ex, uint adapter, int deviceType,
            IntPtr focusWindow, uint behaviorFlags, ref D3DPRESENT_PARAMETERS presentParameters,
            out IntPtr device) {
            var fn = GetVMethod<CreateDeviceExFn>(d3d9Ex, IDirect3D9Ex_CreateDeviceEx);
            return fn(d3d9Ex, adapter, deviceType, focusWindow, behaviorFlags,
                      ref presentParameters, IntPtr.Zero, out device);
        }

        /// <summary>
        /// Wrapper for IDirect3DDevice9::CreateRenderTarget that
        /// creates a SHARED surface — i.e. one whose contents the WPF
        /// MIL compositor can reference directly via the returned
        /// share handle, without read-back. This is required for
        /// D3DImage to use hardware rendering; an unshared surface
        /// makes D3DImage silently fall back to software composition.
        /// </summary>
        /// <param name="sharedHandle">On success, receives the OS
        /// share handle for the new surface. Pass to D3DImage and
        /// log so we can verify hardware mode is engaged
        /// (<c>IDirect3DSurface9</c> with a non-zero share handle =
        /// fast path).</param>
        public static int CreateRenderTarget(IntPtr device, uint width, uint height,
            int format, int multiSample, uint multiSampleQuality, bool lockable,
            out IntPtr surface, out IntPtr sharedHandle) {
            var fn = GetVMethod<CreateRenderTargetFn>(device, IDirect3DDevice9_CreateRenderTarget);
            // Going in, *pSharedHandle must be NULL — D3D9 fills it.
            sharedHandle = IntPtr.Zero;
            return fn(device, width, height, format, multiSample, multiSampleQuality,
                      lockable ? 1 : 0, out surface, ref sharedHandle);
        }

        /// <summary>Wrapper for IDirect3DSurface9::LockRect (full surface).</summary>
        public static int LockRect(IntPtr surface, out D3DLOCKED_RECT lockedRect, int flags) {
            var fn = GetVMethod<LockRectFn>(surface, IDirect3DSurface9_LockRect);
            return fn(surface, out lockedRect, IntPtr.Zero, flags);
        }

        /// <summary>Wrapper for IDirect3DSurface9::UnlockRect.</summary>
        public static int UnlockRect(IntPtr surface) {
            var fn = GetVMethod<UnlockRectFn>(surface, IDirect3DSurface9_UnlockRect);
            return fn(surface);
        }

        /// <summary>Release a COM object via IUnknown::Release (vtable slot
        /// 2). Returns the new ref count (0 = fully freed).</summary>
        public static int Release(IntPtr comObj) {
            if (comObj == IntPtr.Zero) return 0;
            IntPtr vtbl = Marshal.ReadIntPtr(comObj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size);
            var del = (ReleaseFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(ReleaseFn));
            return del(comObj);
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseFn(IntPtr self);

        // ---- HRESULT helper ----

        public const int S_OK = 0;
        public static bool Failed(int hr) => hr < 0;
        public static bool Succeeded(int hr) => hr >= 0;
    }
}
