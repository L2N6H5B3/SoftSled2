using System;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Common surface for the two video renderer implementations
    /// (<see cref="WpfVideoRenderer"/> — software <c>WriteableBitmap</c>
    /// path; <see cref="D3DImageVideoRenderer"/> — GPU-backed
    /// <c>D3DImage</c> path). The engine picks one at construction based
    /// on <c>SoftSledConfig.EnableD3DImage</c> and only talks to it
    /// through this interface, so the per-renderer plumbing differences
    /// are contained at the implementation level.
    ///
    /// Both implementations are fed identical <see cref="VideoFrameSample"/>
    /// instances by <see cref="SoftSledPlaybackEngine"/> and are
    /// expected to release the sample's pooled buffer once it has been
    /// presented or dropped.
    /// </summary>
    internal interface IVideoRenderer : IDisposable {
        /// <summary>Enqueue a decoded frame for presentation. Safe from
        /// any thread.</summary>
        void EnqueueFrame(VideoFrameSample sample);
    }
}
