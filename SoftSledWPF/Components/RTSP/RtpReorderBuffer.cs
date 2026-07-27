using System;

namespace SoftSled.Components.RTSP {
    /// <summary>
    /// Per-stream RTP resequencing buffer that sits in front of a WMRTP
    /// depacketizer.
    ///
    /// <para><b>Why:</b> on a wired LAN, adjacent RTP packets frequently arrive
    /// SWAPPED (NIC receive-side scaling / multi-queue, switch behaviour) —
    /// confirmed in log 20260727-193301, where sequence 25685-86-87 arrived as
    /// 25686, 25685, 25687. The depacketizer processes strictly in arrival order
    /// and treats any out-of-sequence packet as LOSS: it drops the
    /// fragment-reassembly buffer and flags the MAU post-loss, corrupting the
    /// frame → visible smearing/artifacts. A real extender (WMC) resequences by
    /// RTP sequence number first, so it decodes the SAME stream cleanly. This
    /// closes that gap.</para>
    ///
    /// <para><b>How:</b> release packets to the depacketizer in strict sequence
    /// order, holding a briefly-out-of-order packet until its predecessor catches
    /// up (bounded by <see cref="MaxHold"/> packets). A gap that persists past the
    /// hold is declared genuine loss and skipped — so real loss still surfaces to
    /// the depacketizer's own detection, just without the phantom losses that
    /// reordering used to manufacture (two per adjacent swap).</para>
    ///
    /// <para>Single-threaded: each stream's UDP receive thread is the only caller,
    /// so no locking. Sequence arithmetic is 16-bit wrap-safe (signed-short diff).</para>
    /// </summary>
    internal sealed class RtpReorderBuffer {

        /// <summary>In-order delivery to the depacketizer.</summary>
        public delegate void Deliver(byte[] payload, int len, uint ssrc, ushort seq, uint ts, bool marker);

        private readonly Deliver _deliver;
        private readonly Action<string> _log;
        private readonly string _name;

        // Window is a power of two and comfortably larger than MaxHold so a held
        // gap can never wrap into live slots.
        private const int Window = 512;
        private const int WindowMask = Window - 1;
        // Packets we tolerate ahead of a still-missing seq before declaring it
        // lost. ~64 packets ≈ tens of ms at HD rates — long enough to absorb the
        // observed reordering, short enough that genuine loss recovers quickly.
        // The downstream jitter buffer (seconds deep) hides this added latency.
        private const int MaxHold = 64;

        private struct Slot {
            public bool Present;
            public byte[] Payload;
            public int Len;
            public uint Ssrc;
            public ushort Seq;
            public uint Ts;
            public bool Marker;
        }
        private readonly Slot[] _slots = new Slot[Window];

        private bool _init;
        private ushort _nextSeq;
        private int _buffered;
        // Set from another thread (the RTSP control thread, on seek/PLAY); honoured
        // on the UDP receive thread at the top of Accept so all mutation of the
        // buffer stays single-threaded.
        private volatile bool _resetPending;

        // Diagnostics (see MaybeLog).
        private long _delivered, _reorderedAbsorbed, _lateDropped, _forcedSkips, _lostPkts;
        private long _lastLogDelivered;

        public RtpReorderBuffer(string name, Deliver deliver, Action<string> log) {
            _name = name;
            _deliver = deliver;
            _log = log;
        }

        /// <summary>Request a reset from ANY thread (seek / stream discontinuity).
        /// The actual clear happens on the receive thread at the next
        /// <see cref="Accept"/>, so the buffer stays single-threaded. The next
        /// packet re-seeds the expected sequence. (A big sequence jump also
        /// self-resyncs without this, but resetting drops stale pre-seek packets.)</summary>
        public void RequestReset() { _resetPending = true; }

        public void Accept(byte[] payload, int len, uint ssrc, ushort seq, uint ts, bool marker) {
            if (_resetPending) {
                _resetPending = false;
                for (int i = 0; i < _slots.Length; i++) { _slots[i].Present = false; _slots[i].Payload = null; }
                _buffered = 0;
                _init = false;
            }
            if (!_init) {
                _init = true;
                _nextSeq = seq;
                Emit(payload, len, ssrc, seq, ts, marker);
                _nextSeq++;
                DrainContiguous();
                return;
            }

            int diff = (short)(seq - _nextSeq);   // wrap-safe signed distance

            if (diff == 0) {
                // In order — the common case.
                Emit(payload, len, ssrc, seq, ts, marker);
                _nextSeq++;
                DrainContiguous();
                MaybeLog();
                return;
            }
            if (diff < 0) {
                // Behind the release point: a late reorder for a gap we already
                // passed, or a duplicate. Too late to use — drop.
                _lateDropped++;
                MaybeLog();
                return;
            }
            if (diff >= Window) {
                // Too far ahead to hold — a big real-loss run, or a discontinuity
                // (seek / SSRC change). Flush what we hold in order, then resync.
                FlushInOrder();
                _nextSeq = seq;
                Emit(payload, len, ssrc, seq, ts, marker);
                _nextSeq++;
                DrainContiguous();
                MaybeLog();
                return;
            }

            // A future packet within the window — buffer it and wait for the gap
            // to fill.
            int idx = seq & WindowMask;
            if (!(_slots[idx].Present && _slots[idx].Seq == seq)) _buffered++;
            _slots[idx] = new Slot {
                Present = true, Payload = payload, Len = len,
                Ssrc = ssrc, Seq = seq, Ts = ts, Marker = marker,
            };
            // Held too far past a still-missing seq → that gap is real loss.
            if (diff >= MaxHold) ForceSkipToEarliest();
            MaybeLog();
        }

        // Release any buffered packets that are now contiguous from _nextSeq.
        private void DrainContiguous() {
            while (true) {
                int idx = _nextSeq & WindowMask;
                if (!_slots[idx].Present || _slots[idx].Seq != _nextSeq) break;
                var s = _slots[idx];
                _slots[idx].Present = false; _slots[idx].Payload = null; _buffered--;
                _reorderedAbsorbed++;   // it arrived early and waited — a reorder we hid
                Emit(s.Payload, s.Len, s.Ssrc, s.Seq, s.Ts, s.Marker);
                _nextSeq++;
            }
        }

        // _nextSeq is missing and won't come: skip to the earliest buffered seq,
        // counting the skipped range as genuine loss.
        private void ForceSkipToEarliest() {
            for (int i = 1; i < Window; i++) {
                ushort seq = (ushort)(_nextSeq + i);
                int idx = seq & WindowMask;
                if (_slots[idx].Present && _slots[idx].Seq == seq) {
                    _forcedSkips++;
                    _lostPkts += i;   // _nextSeq .. seq-1 genuinely missing
                    _log?.Invoke($"[rtp-reorder] {_name}: unrecovered gap of {i} packet(s) at seq {_nextSeq} " +
                                 $"— real loss, skipping to {seq}");
                    _nextSeq = seq;
                    DrainContiguous();
                    return;
                }
            }
        }

        // Deliver everything buffered, lowest seq first (used on a big
        // discontinuity where holding makes no sense).
        private void FlushInOrder() {
            for (int i = 0; i < Window && _buffered > 0; i++) {
                ushort seq = (ushort)(_nextSeq + i);
                int idx = seq & WindowMask;
                if (_slots[idx].Present && _slots[idx].Seq == seq) {
                    var s = _slots[idx];
                    _slots[idx].Present = false; _slots[idx].Payload = null; _buffered--;
                    Emit(s.Payload, s.Len, s.Ssrc, s.Seq, s.Ts, s.Marker);
                }
            }
            if (_buffered != 0) {   // clear any stragglers outside the scan
                for (int i = 0; i < _slots.Length; i++) { _slots[i].Present = false; _slots[i].Payload = null; }
                _buffered = 0;
            }
        }

        private void Emit(byte[] payload, int len, uint ssrc, ushort seq, uint ts, bool marker) {
            _delivered++;
            _deliver(payload, len, ssrc, seq, ts, marker);
        }

        private void MaybeLog() {
            if (_log == null) return;
            if (_delivered - _lastLogDelivered < 2000) return;
            _lastLogDelivered = _delivered;
            _log($"[rtp-reorder] {_name}: delivered={_delivered} reordersAbsorbed={_reorderedAbsorbed} " +
                 $"realLostPkts={_lostPkts} forcedSkips={_forcedSkips} lateDropped={_lateDropped} held={_buffered}");
        }
    }
}
