using System.Collections.Generic;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// Tracks MS-RRSP2 object handles for the splash channel.
    ///
    /// Two namespaces:
    /// <list type="bullet">
    ///   <item><description><b>Classes</b> — created via
    ///   <c>Broker_CreateClass(idObjectClass, stClassName)</c>. The
    ///   <c>idObjectClass</c> is a handle that subsequent
    ///   <c>Broker_CreateObject</c> messages refer to in order to identify
    ///   *what kind of thing* to instantiate. We resolve it to a
    ///   <see cref="SplashClassKind"/> enum so the rest of the renderer can
    ///   <c>switch</c> on a strongly-typed value rather than the raw u32.</description></item>
    ///   <item><description><b>Objects</b> — instances of those classes,
    ///   created via <c>Broker_CreateObject(idObjectClass, idObjectNew, ...)</c>
    ///   and identified thereafter by their <c>idObjectNew</c> handle in
    ///   every payload message's <c>_idObjectSubject</c> field.</description></item>
    /// </list>
    ///
    /// The class-name → kind mapping is *string-based* on purpose: the
    /// MS-RRSP2 spec defines class names like "Visual", "Window",
    /// "RenderBuilder" etc, but the numeric ID is server-assigned at
    /// connection time and not stable across sessions.
    /// </summary>
    internal sealed class SplashObjectRegistry {

        private readonly Dictionary<uint, ClassRecord>  _classes = new Dictionary<uint, ClassRecord>();
        private readonly Dictionary<uint, ISplashObject> _objects = new Dictionary<uint, ISplashObject>();

        public int ObjectCount => _objects.Count;
        public int ClassCount  => _classes.Count;

        public void RegisterClass(uint id, string name) {
            _classes[id] = new ClassRecord {
                Id   = id,
                Name = name ?? string.Empty,
                Kind = ClassifyByName(name),
            };
        }

        public bool TryGetClass(uint id, out ClassRecord rec) =>
            _classes.TryGetValue(id, out rec);

        public void RegisterObject(uint handle, ISplashObject obj) {
            _objects[handle] = obj;
        }

        public bool TryGetObject(uint handle, out ISplashObject obj) =>
            _objects.TryGetValue(handle, out obj);

        /// <summary>
        /// Enumerate all handles that resolve to a Surface object.
        /// Used by the media playback Surface Router to broadcast
        /// "host resized" notifications to every currently-registered
        /// video surface ID.
        /// </summary>
        public IEnumerable<uint> EnumerateSurfaceHandles() {
            foreach (var kv in _objects) {
                if (kv.Value is SoftSled.Components.Splash.Objects.SplashSurface) {
                    yield return kv.Key;
                }
            }
        }

        public void RemoveObject(uint handle) {
            if (_objects.TryGetValue(handle, out var obj)) {
                obj.OnDestroyed();
                _objects.Remove(handle);
            }
        }

        public void Clear() {
            foreach (var o in _objects.Values) {
                try { o.OnDestroyed(); } catch { }
            }
            _objects.Clear();
            _classes.Clear();
        }

        private static SplashClassKind ClassifyByName(string name) {
            if (string.IsNullOrEmpty(name)) return SplashClassKind.Unknown;
            // WMC sends fully-qualified C++ names like
            // "Splash::Messaging::Context" or
            // "Splash::Rendering::Visual". Take the segment after the
            // last "::" to get the bare class name.
            int sep = name.LastIndexOf("::", System.StringComparison.Ordinal);
            string n = sep >= 0 ? name.Substring(sep + 2) : name;
            // Some implementations prefix with "C" (e.g. "CWindow") —
            // strip if present.
            if (n.StartsWith("C") && n.Length > 1 && char.IsUpper(n[1])) n = n.Substring(1);
            switch (n) {
                case "Broker":                return SplashClassKind.Broker;
                case "Context":               return SplashClassKind.Context;
                case "Window":                return SplashClassKind.Window;
                case "Visual":                return SplashClassKind.Visual;
                case "RenderBuilder":         return SplashClassKind.RenderBuilder;
                case "Device":                return SplashClassKind.Device;
                case "Surface":               return SplashClassKind.Surface;
                case "SurfacePool":           return SplashClassKind.SurfacePool;
                case "VideoPool":             return SplashClassKind.VideoPool;
                case "Rasterizer":            return SplashClassKind.Rasterizer;
                case "Gradient":              return SplashClassKind.Gradient;
                case "Line":                  return SplashClassKind.Line;
                case "Animation":             return SplashClassKind.Animation;
                case "AnimationManager":      return SplashClassKind.AnimationManager;
                case "ParticleSystem":        return SplashClassKind.ParticleSystem;
                case "NullDevice":            return SplashClassKind.NullDevice;
                case "DataBuffer":            return SplashClassKind.DataBuffer;
                case "ContextRelay":          return SplashClassKind.ContextRelay;
                case "WaitCursor":            return SplashClassKind.WaitCursor;
                case "DynamicSurfaceFactory": return SplashClassKind.DynamicSurfaceFactory;
                case "SoundBuffer":           return SplashClassKind.SoundBuffer;
                case "Sound":                 return SplashClassKind.Sound;
                case "SoundDevice":           return SplashClassKind.SoundDevice;
                case "XeDevice":              return SplashClassKind.XeDevice;
                case "HostWindow":            return SplashClassKind.HostWindow;
                case "XAudSoundDevice":       return SplashClassKind.XAudSoundDevice;
                case "Dx9Device":             return SplashClassKind.Dx9Device;
                default:                      return SplashClassKind.Unknown;
            }
        }

        public struct ClassRecord {
            public uint            Id;
            public string          Name;
            public SplashClassKind Kind;
        }
    }

    internal enum SplashClassKind {
        Unknown,
        Broker, Context, Window, Visual, RenderBuilder, Device,
        Surface, SurfacePool, VideoPool, Rasterizer, Gradient, Line,
        Animation, AnimationManager, DataBuffer, ContextRelay,
        WaitCursor, DynamicSurfaceFactory, ParticleSystem, NullDevice,
        SoundBuffer, Sound, SoundDevice, XeDevice, HostWindow,
        XAudSoundDevice, Dx9Device,
    }

    /// <summary>
    /// Minimum contract every splash object satisfies — destruction hook
    /// so the registry can clean WPF resources when an object is removed
    /// or the whole splash session ends.
    /// </summary>
    internal interface ISplashObject {
        uint            Handle   { get; }
        SplashClassKind Kind     { get; }
        string          ClassName { get; }
        void OnDestroyed();
    }
}
