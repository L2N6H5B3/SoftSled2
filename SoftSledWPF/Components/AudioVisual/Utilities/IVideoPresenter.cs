using System;
using System.Windows.Media;

namespace SoftSled.Components.AudioVisual.Utilities {

    /// <summary>
    /// Video-plane sink the playback pipeline draws into. Decoded BGRA frames
    /// arrive via <see cref="SubmitFrame"/> (from the pacer, on any thread) and
    /// are composited by whatever surface <see cref="Image"/> exposes.
    ///
    /// <para>Two implementations exist: <see cref="D3DImagePresenter"/> (GPU,
    /// Direct3D9Ex-backed <c>D3DImage</c> — the "3D acceleration" path) and
    /// <see cref="WriteableBitmapPresenter"/> (software, plain WPF
    /// <c>WriteableBitmap</c> — the fallback when 3D acceleration is disabled or
    /// the D3D device can't be created). The session picks one at start and
    /// binds <see cref="Image"/> to the video plane; the controller only ever
    /// calls <see cref="SubmitFrame"/>, so the two are interchangeable.</para>
    /// </summary>
    internal interface IVideoPresenter : IDisposable {

        /// <summary>The ImageSource to bind to the video plane
        /// (<c>VideoImage.Source</c>). For the GPU path this is a stable
        /// <c>D3DImage</c>; for the software path it is a <c>WriteableBitmap</c>
        /// that is re-allocated on a resolution change — hence
        /// <see cref="ImageChanged"/>.</summary>
        ImageSource Image { get; }

        /// <summary>Raised (on the UI thread) when <see cref="Image"/> has been
        /// replaced with a new instance and the video plane must re-bind its
        /// Source. Never fires for the GPU presenter (its <c>D3DImage</c> is
        /// stable); the software presenter fires it when the frame size
        /// changes.</summary>
        event Action ImageChanged;

        /// <summary>Upload one BGRA frame. <paramref name="src"/> points at
        /// <paramref name="srcStride"/>×<paramref name="h"/> bytes of BGRA.
        /// Safe from any thread.</summary>
        void SubmitFrame(IntPtr src, int srcStride, int w, int h);

        /// <summary>Paint the surface opaque black, discarding the last decoded
        /// frame (called when media closes so a stale frame doesn't linger
        /// behind the WMC menu). Safe from any thread.</summary>
        void Blank();

        /// <summary>Frames handed to <see cref="SubmitFrame"/> (the pacer's
        /// release count). Diagnostic (av-timing).</summary>
        long FramesSubmitted { get; }

        /// <summary>Frames actually composited on screen. Diagnostic
        /// (av-timing); <c>FramesSubmitted - FramesPresented</c> is the
        /// present-layer coalescing backlog.</summary>
        long FramesPresented { get; }
    }
}
