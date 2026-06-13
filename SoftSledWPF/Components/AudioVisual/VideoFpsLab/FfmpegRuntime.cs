using System;
using System.IO;
using System.Reflection;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

    /// <summary>
    /// Resolves the bundled native FFmpeg DLL directory for the CURRENT
    /// process architecture. The DLLs ship under
    /// <c>&lt;exeDir&gt;\Tools\ffmpeg\x64</c> and <c>...\x86</c>; a 32-bit
    /// process must load the x86 set and a 64-bit process the x64 set, or
    /// FFmpeg.AutoGen fails to load avcodec/avutil with a "module not found".
    /// Mirrors the arch selection the native RDP loader uses in App.xaml.cs.
    /// </summary>
    internal static class FfmpegRuntime {
        public static string NativeDir() {
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            return Path.Combine(baseDir, "Tools", "ffmpeg", arch);
        }
    }
}
