using System;
using System.Runtime.InteropServices;

namespace SoftSled.Components.Communication {

    /// <summary>
    /// P/Invoke surface of softsled-rdp.dll — the thin native shim that fronts
    /// FreeRDP (libfreerdp / winpr / libfreerdp-client) for SoftSled. Wrapped by
    /// <see cref="FreeRdpClient"/>; not expected to be used directly outside.
    /// </summary>
    public static class SoftSledNative {
        public const string Dll = "softsled-rdp.dll";

        public enum State : int {
            Disconnected = 0,
            Connecting   = 1,
            Active       = 2,
            Failed       = 3,
        }

        public enum Result : int {
            Ok               =  0,
            InvalidArg       = -1,
            InvalidState     = -2,
            NoMemory         = -3,
            FreeRdpError     = -4,
            NotImplemented   = -5,
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void StateCallback(IntPtr user, State state, int detail);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ChannelCallback(IntPtr user, IntPtr name, IntPtr data, UIntPtr length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PaintCallback(IntPtr user, int x, int y, int w, int h);

        /// <summary>Mirror of <c>softsled_rect</c> — one dirty sub-rect per call to the
        /// multi-rect paint callback. Coordinates are framebuffer pixels.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect {
            public int X;
            public int Y;
            public int W;
            public int H;
        }

        /// <summary>Multi-rect paint callback. <paramref name="rects"/> points at
        /// shim-owned memory valid only for the duration of the call — copy out
        /// before returning if you need it longer.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PaintRectsCallback(IntPtr user, IntPtr rects, uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FastpathCallback(IntPtr user, byte updateCode, IntPtr data, UIntPtr length);

        /// <summary>Mirror of <c>softsled_framebuffer_info</c>. Pointer remains valid until disconnect.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FramebufferInfo {
            public IntPtr Pixels;
            public uint   Width;
            public uint   Height;
            public uint   Stride;
            public uint   PixelFormat;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr softsled_client_new();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void softsled_client_free(IntPtr c);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int softsled_set_server(IntPtr c, string host, ushort port);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int softsled_set_credentials(IntPtr c, string user, string password);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_set_security_rdp_only(IntPtr c);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_set_cert_ignore(IntPtr c);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int softsled_register_channel(IntPtr c, string name);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void softsled_set_state_callback(IntPtr c, StateCallback cb, IntPtr user);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void softsled_set_channel_callback(IntPtr c, ChannelCallback cb, IntPtr user);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_connect(IntPtr c);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_disconnect(IntPtr c);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int softsled_send_channel_data(IntPtr c, string name,
                                                             byte[] data, UIntPtr length);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void softsled_set_paint_callback(IntPtr c, PaintCallback cb, IntPtr user);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_set_decode_enabled(IntPtr c, int enabled);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_set_initial_desktop_size(IntPtr c, uint width, uint height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_get_framebuffer_info(IntPtr c, out FramebufferInfo info);

        // Keyboard flags (mirror SOFTSLED_KBD_FLAGS_* in softsled.h, which mirror freerdp/input.h).
        public const ushort KBD_FLAGS_EXTENDED  = 0x0100;
        public const ushort KBD_FLAGS_EXTENDED1 = 0x0200;
        public const ushort KBD_FLAGS_DOWN      = 0x4000;
        public const ushort KBD_FLAGS_RELEASE   = 0x8000;

        // Mouse pointer flags (mirror SOFTSLED_PTR_FLAGS_* / freerdp/input.h PTR_FLAGS_*).
        public const ushort PTR_FLAGS_HWHEEL         = 0x0400;
        public const ushort PTR_FLAGS_WHEEL          = 0x0200;
        public const ushort PTR_FLAGS_WHEEL_NEGATIVE = 0x0100;
        public const ushort PTR_FLAGS_MOVE           = 0x0800;
        public const ushort PTR_FLAGS_DOWN           = 0x8000;
        public const ushort PTR_FLAGS_BUTTON1        = 0x1000; // left
        public const ushort PTR_FLAGS_BUTTON2        = 0x2000; // right
        public const ushort PTR_FLAGS_BUTTON3        = 0x4000; // middle
        public const ushort PTR_WHEEL_ROTATION_MASK  = 0x01FF;

        // Extended pointer flags for X1/X2 (side) buttons.
        public const ushort PTR_XFLAGS_DOWN    = 0x8000;
        public const ushort PTR_XFLAGS_BUTTON1 = 0x0001; // X1 (back)
        public const ushort PTR_XFLAGS_BUTTON2 = 0x0002; // X2 (forward)

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_send_keyboard_event(IntPtr c, ushort flags, byte code);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_send_mouse_event(IntPtr c, ushort flags, ushort x, ushort y);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int softsled_send_extended_mouse_event(IntPtr c, ushort flags, ushort x, ushort y);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void softsled_set_paint_rects_callback(IntPtr c, PaintRectsCallback cb, IntPtr user);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void softsled_set_fastpath_callback(IntPtr c, FastpathCallback cb, IntPtr user);
    }
}
