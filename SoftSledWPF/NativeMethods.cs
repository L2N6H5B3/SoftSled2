using System;
using System.Runtime.InteropServices;
using System.Drawing; // For System.Drawing.Color

internal static class NativeMethods {
    // Window Styles
    public const int GWL_EXSTYLE = -20;
    public const uint WS_EX_LAYERED = 0x00080000;

    // SetLayeredWindowAttributes flags
    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    // --- GetWindowLong / GetWindowLongPtr ---
    // Helper to call the correct GetWindowLongPtr depending on pointer size
    public static IntPtr GetWindowLongPtrHelper(IntPtr hWnd, int nIndex) {
        if (IntPtr.Size == 8) // 64-bit
            return GetWindowLongPtr64(hWnd, nIndex);
        else // 32-bit
            return new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);


    // --- SetWindowLong / SetWindowLongPtr ---
    // Helper to call the correct SetWindowLongPtr based on pointer size
    public static IntPtr SetWindowLongPtrHelper(IntPtr hWnd, int nIndex, IntPtr dwNewLong) {
        if (IntPtr.Size == 8) // 64-bit
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        else // 32-bit
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);


    // --- SetLayeredWindowAttributes ---
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // Helper to convert System.Drawing.Color to COLORREF used by Win32
    public static uint ToWin32Color(Color color) {
        // COLORREF is 0x00bbggrr
        return (uint)(((color.B << 16) | (color.G << 8) | color.R) & 0xffffff);
    }
}