using SoftSled.Components.Diagnostics;
using System;
using System.IO;
using SoftSled.Components.AudioVisual;

namespace SoftSled.Components.AudioVisual.Playback.MediaFoundation {

    /// <summary>
    /// Adapter that exposes an <see cref="AsfStreamProducer"/> as a
    /// <see cref="System.IO.Stream"/>. We do this because SharpDX's
    /// <c>SharpDX.MediaFoundation.ByteStream</c> constructor accepts a
    /// <c>Stream</c>, and the MediaEngine layer drives reads through
    /// that ByteStream — so wrapping the producer is the cleanest
    /// hand-off without re-implementing Media Foundation's
    /// <c>IMFByteStream</c> COM interface ourselves.
    ///
    /// <para>Non-seekable. The wire is live RTSP, so any seek attempt
    /// from Media Foundation must fail. SharpDX's wrapper translates
    /// the Stream API into IMFByteStream calls — and Media Foundation's
    /// source resolver eagerly probes <c>Length</c>, <c>Position</c>,
    /// <c>Seek</c>, <c>Flush</c> and <c>SetLength</c> during open,
    /// regardless of <c>CanSeek</c>. The original implementation
    /// threw <see cref="NotSupportedException"/> from each of those,
    /// which propagated up through the COM marshaller as
    /// <c>E_FAIL</c> / <c>COR_E_INVALIDOPERATION</c> and broke the
    /// open path. To stay friendly to MF, every method here either
    /// returns a "live, unknown size, no-op" sentinel or throws an
    /// <see cref="IOException"/> (which MF treats as a recoverable
    /// I/O error rather than a programming bug).</para>
    /// </summary>
    internal sealed class MediaFoundationProducerStream : Stream {

        private readonly AsfStreamProducer _producer;
        private readonly Logger _log;
        private long _positionBytes;
        private bool _eosReached;

        public MediaFoundationProducerStream(AsfStreamProducer producer)
            : this(producer, log: null) { }

        public MediaFoundationProducerStream(AsfStreamProducer producer, Logger log) {
            _producer = producer ?? throw new ArgumentNullException(nameof(producer));
            _log = log;
        }

        // The producer is what's authoritative on bytes consumed, but
        // for diagnostics we track the position the *consumer*
        // (MediaEngine) thinks it has read.
        public long BytesReadByConsumer => _positionBytes;

        // ----- Stream surface -------------------------------------------

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        // Length=0 is the conventional "unknown / live stream" sentinel
        // for IMFByteStream. Returning 0 lets MF treat the stream as
        // live and skip any seek-to-end probing it would otherwise do
        // for length-anchored containers. Throwing here breaks
        // MediaEngine's source-resolver open path even though
        // CanSeek=false should be enough on its own — but it isn't.
        public override long Length => 0;

        // Position get is informational only — used by MF's diagnostic
        // logging, not for seeking. Returning the byte counter is fine.
        // Position set: live byte stream, can't seek; silently accept
        // when the value equals the current position (some MF probes
        // do a "reset to where we are" no-op set), and treat any other
        // value as an IOException ("the request was an I/O error"
        // rather than "you called the wrong method").
        public override long Position {
            get => _positionBytes;
            set {
                if (value == _positionBytes) return;
                _log?.LogDebug($"[mf-stream] Position.set({value}) on non-seekable — failing");
                throw new IOException("non-seekable live stream");
            }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            if (_eosReached) return 0;
            int n;
            try {
                n = _producer.Read(buffer, offset, count);
            } catch (Exception ex) {
                // Producer disposed / cancelled — treat as EOS rather
                // than propagating, so MediaEngine can wind down its
                // demuxer cleanly. Log once at the transition.
                _log?.LogInfo($"[mf-stream] producer Read threw ({ex.GetType().Name}: {ex.Message}) — emitting EOS");
                _eosReached = true;
                return 0;
            }
            if (n > 0) _positionBytes += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) {
            // Live source — can't seek. MF interprets an IOException
            // here as a recoverable byte-stream error and either
            // continues without the seek or aborts the source; either
            // is fine. NotSupportedException, by contrast, gets
            // translated to E_FAIL up the COM boundary and confuses
            // the source resolver.
            //
            // Special case: a seek of 0 from current is a no-op probe
            // some sources do; report current position without error.
            if (origin == SeekOrigin.Current && offset == 0) return _positionBytes;
            if (origin == SeekOrigin.Begin && offset == _positionBytes) return _positionBytes;
            _log?.LogDebug($"[mf-stream] Seek({origin}, {offset}) on non-seekable — failing");
            throw new IOException("non-seekable live stream");
        }

        public override void SetLength(long value) {
            // Silent no-op. MF occasionally calls this with the
            // current length-hint during init; nothing for us to do.
        }

        public override void Write(byte[] buffer, int offset, int count) {
            // Read-only. Returning silently keeps MF happy if any
            // pre-Open probe tries to write (it shouldn't, but
            // defensive parity with the other "no-op" overrides).
        }

        public override void Flush() { /* no-op — nothing buffered on our side */ }

        protected override void Dispose(bool disposing) {
            // Don't dispose the producer — the RTSPClient owns its
            // lifetime; we're just a Stream-shaped view on it.
            base.Dispose(disposing);
        }
    }
}
