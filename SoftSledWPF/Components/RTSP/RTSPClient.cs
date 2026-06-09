using Rtsp.Messages;
using SoftSled.Components.AudioVisual;
using SoftSled.Components.AudioVisual.FormatStructures;
using SoftSled.Components.Communication;
using SoftSledWPF.Components.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SoftSled.Components.RTSP {
    class RTSPClient {

        #region Enums #########################################################

        public enum RTP_TRANSPORT { UDP, TCP, MULTICAST, UNKNOWN };
        public enum MEDIA_REQUEST { VIDEO_ONLY, AUDIO_ONLY, VIDEO_AND_AUDIO };
        private enum RTSP_STATUS { WaitingToConnect, Connecting, ConnectFailed, Connected };

        #endregion ############################################################


        #region Variables #####################################################

        private string UserAgent = "User-Agent: MCExtender/1.50.X.090522.00"; // Assume Xbox 360?
        //private string UserAgent = "User-Agent: MCExtender/1.50.0.0";
        //private string UserAgent = "User-Agent: MCExtender/1.0.0.0"; // Linksys Extender (Doesn't support H.264?)

        private string AcceptHeader = "Accept: application/sdp";
        private string LanguageHeader = "Accept-Language: en-us, *;q=0.1";
        private string SupportedHeader = "Supported: com.microsoft.wm.srvppair, com.microsoft.wm.sswitch, com.microsoft.wm.eosmsg, com.microsoft.wm.predstrm, com.microsoft.wm.fastcache, com.microsoft.wm.locid, com.microsoft.wm.rtp.asf, dlna.announce, dlna.rtx, dlna.rtx-dup, com.microsoft.wm.startupprofile";

        Rtsp.RtspTcpTransport rtsp_socket = null;               // RTSP connection
        volatile RTSP_STATUS rtsp_socket_status = RTSP_STATUS.WaitingToConnect;
        Rtsp.RtspListener rtsp_client = null;                   // this wraps around a the RTSP tcp_socket stream
        RTP_TRANSPORT rtp_transport = RTP_TRANSPORT.UNKNOWN;    // Mode, either RTP over UDP or RTP over TCP using the RTSP socket
        Rtsp.UDPSocket video_udp_pair = null;                   // Pair of UDP ports used in RTP over UDP mode or in MULTICAST mode
        Rtsp.UDPSocket audio_udp_pair = null;                   // Pair of UDP ports used in RTP over UDP mode or in MULTICAST mode
        string url = "";                                        // RTSP URL (username & password will be stripped out
        string hostname = "";                                   // RTSP Server hostname or IP address
        int port = 0;                                           // RTSP Server TCP Port number
        string session = "";                                    // RTSP Session
        // Per-stream client SSRCs for RTCP reporter/sender fields.
        // Random per-session, but stable for the life of the session.
        private readonly uint _audioReporterSsrc = (uint)(0xC2520000u | ((uint)Guid.NewGuid().GetHashCode() & 0xFFFFu));
        private readonly uint _videoReporterSsrc = (uint)(0x60430000u | ((uint)Guid.NewGuid().GetHashCode() & 0xFFFFu));
        bool client_wants_video = false;                        // Client wants to receive Video
        bool client_wants_audio = false;                        // Client wants to receive Audio

        Uri video_uri = null;                                   // URI used for the Video Track
        Uri audio_uri = null;                                   // URI used for the Audio Track
        int video_payload = -1;                                 // Payload Type for the Video. (often 96 which is the first dynamic payload value. Bosch use 35)
        int audio_payload = -1;                                 // Payload Type for the Video. (often 96 which is the first dynamic payload value)
        int video_data_channel = -1;                            // RTP Channel Number used for the video RTP stream or the UDP port number
        int audio_data_channel = -1;                            // RTP Channel Number used for the audio RTP stream or the UDP port number
        int video_rtcp_channel = -1;                            // RTP Channel Number used for the video RTCP status report messages OR the UDP port number
        int audio_rtcp_channel = -1;                            // RTP Channel Number used for the audio RTCP status report messages OR the UDP port number

        bool server_supports_get_parameter = false;             // Used with RTSP keepalive
        bool server_supports_set_parameter = false;             // Used with RTSP keepalive
        System.Timers.Timer keepalive_timer = null;             // Used with RTSP keepalive

        List<RtspRequestSetup> setup_messages = new List<RtspRequestSetup>(); // Setup messages still to send

        Dictionary<int, WMFPayloadData> wmfPayloadDataDict = new Dictionary<int, WMFPayloadData>();

        // Wire-codec tracking. SDP advertises multiple PTs per direction;
        // the server picks at PLAY time. We commit to a specific pipeline
        // on the first observed RTP packet per direction. Once committed,
        // subsequent codec changes on the wire are ignored (which shouldn't
        // happen mid-session anyway). The committed codec is the string
        // from the SDP rtpmap entry (e.g. "VND.MS.WM-MPA", "X-WMF-PF").
        private string _wireAudioCodec;       // null until first audio packet
        private string _wireVideoCodec;       // null until first video packet
        private readonly object _commitLock = new object();

        // RTCP Receiver Report state. Phase-0c follow-up: WMPNss paces audio
        // at ~20% real-time. The first RR attempt (8-byte empty form) had no
        // effect — we now send a RFC 3550 §6.4.2 conformant RR with one
        // reception report block (32 bytes) + an SDES CNAME packet (most
        // RTCP implementations treat a bare RR as incomplete and ignore it).
        private int _audioServerRtcpPort = -1;
        private int _videoServerRtcpPort = -1;
        private uint _audioServerDataSsrc;        // server's data-stream SSRC, parsed from Transport
        private uint _videoServerDataSsrc;

        // Per-stream sender SSRC for outbound RTCP (RR / SDES / BFR). MS-RTSP
        // servers pre-assign this via the Transport response header
        // `rtcp-fb-ssrc=<hex>`; using the server-assigned value lets the
        // server correlate our feedback packets to the right session.
        // Falls back to the random Xbox-prefix-style _audio/videoReporterSsrc
        // (declared near the top of this file) when the server doesn't
        // advertise an rtcp-fb-ssrc value.
        private uint _audioRtcpSenderSsrc;
        private uint _videoRtcpSenderSsrc;
        private System.Threading.Timer _rtcpTimer;
        private int _rtcpTickCount;                // diagnostic — how many RRs we've sent
        private long _audioPktsReceived;
        private long _videoPktsReceived;
        private ushort _audioBaseSeq;              // first RTP seq seen on audio (for ext-highest-seq)
        private ushort _audioHighestSeq;
        private bool _audioSeqInit;
        private uint _audioSeqCycles;            // upper 16 bits of extended highest seq
        private ushort _videoBaseSeq;
        private ushort _videoHighestSeq;
        private bool _videoSeqInit;
        private uint _videoSeqCycles;
        // Random CNAME (per-session) is enough for an SDES item; format is
        // typically "user@host" but raw GUID is accepted by WMPNss.
        private readonly string _rtcpCname = "softsled-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // BFR (Buffer Fullness Report) feedback flags. Set true per-stream
        // when the SDP contains `a=rtcp-fb:* bfr ...` or `a=rtcp-fb:<pt> bfr`.
        // Tied to a specific WMPNss server pacing issue: without BFR feedback,
        // WMRTP-PCM streams are throttled to ~17% real-time. See
        // BuildBfrPacket for the reverse-engineered packet format.
        private bool _audioBfrEnabled;
        private bool _videoBfrEnabled;

        // BFR W3 (buffer-fill ms) ramp clock. Xbox 360 captures showed W3
        // ramping from 0 → TD (2000 ms) over the first ~few seconds of the
        // session, then holding at TD for steady-state playback. We do the
        // same: start at 0 on session connect, ramp linearly to TD over
        // 2 s wall-clock, hold at TD afterwards. Sending a static 0 forever
        // (our previous behaviour) may be why the server stops delivery
        // after its initial pre-roll: it interprets "buffer starving at
        // t=80s" as "client is permanently broken, stop trying".
        private readonly Stopwatch _bfrRampStopwatch = new Stopwatch();
        private const int BfrRampDurationMs = 2000;     // 0 → TD over 2 s
        private const ushort BfrTdMs = 2000;     // matches SETUP Buffer-Info TD=2000

        /// <summary>Optional provider of the REAL current audio buffer
        /// occupancy (ms). When set, the audio BFR W3 field carries this
        /// honest telemetry instead of the synthetic ramp-then-hold-at-TD
        /// value. The Xbox-360 capture shows the real client reports its
        /// genuine, dynamically-changing buffer level here (audio ramps
        /// 0→~TD then wiggles just under it, dipping when under-fed) — that
        /// dip is what makes WMPNss speed up to refill. Our flat value pegged
        /// at TD never signalled a drain, so the server settled at ~94% audio
        /// delivery. Set by ExternalSyncMediaController from the renderer.</summary>
        private Func<int> _audioBufferMsProvider;
        public void SetAudioBufferOccupancyProvider(Func<int> audioBufferMs) {
            _audioBufferMsProvider = audioBufferMs;
        }

        /// <summary>Same as the audio provider but for the VIDEO BFR W3 — the
        /// pacer's real buffered-frame span (ms). Without it the video W3 sat
        /// flat at TD and the server under-delivered video ~3%, slowly draining
        /// the jitter buffer until it starved (video froze mid-playback).</summary>
        private Func<int> _videoBufferMsProvider;
        public void SetVideoBufferOccupancyProvider(Func<int> videoBufferMs) {
            _videoBufferMsProvider = videoBufferMs;
        }

        // Initialise Variables to hold Depacketizers
        WmrptVideoDepacketizer videoDepacketizer = null;
        WmrptAudioDepacketizer audioDepacketizer = null;
        private bool _wmaFirstPayloadLogged;   // one-shot WMA payload dump (framing diagnostic)

        int rtp_count = 0; // used for statistics
        private int _vidRecvLoggedOnce;
        private int _audRecvLoggedOnce;


        // Flag set by Stop() so the keepalive miss-counter (and any other
        // background path that observes a socket exception) knows the
        // disconnect is user-initiated and shouldn't escalate to a
        // Disconnected event.
        private volatile bool _stopRequested;
        private int _keepaliveConsecutiveFailures;


        // First video MAU's wire-side RTP timestamp (90kHz units,
        // stored as long so -1 can mean "not yet set" without
        // colliding with valid 32-bit RTP values). Used by
        // ExternalSyncMediaController in Phase 2.5 to compute the
        // wireOffset between audio and video first-MAU times,
        // letting it target the server's intended sync rather than
        // the post-startup skew. Volatile-ish access via Interlocked
        // because it's written on the depacketizer thread and read
        // on the sync-controller dispatcher thread.
        private long _firstVideoMauWirePtsRaw = -1L;

        // Per-stream play-point RTP timestamps from the PLAY response's
        // RTP-Info header (-1 = not seen). Each is its OWN stream's RTP clock.
        private long _audioRtpInfoRtptime = -1L;
        private long _videoRtpInfoRtptime = -1L;

        // Incremented every time a PLAY response's RTP-Info is parsed (initial
        // PLAY + every seek / trick-play PLAY). Lets the controller tell post-
        // seek MAUs from pre-seek in-flight MAUs: after a seek it waits for this
        // to advance before re-anchoring, so it doesn't latch the offset onto a
        // stale pre-seek MAU using the old RTP-Info (which left A/V out of sync
        // after a scrub).
        private long _rtpInfoGeneration;
        public long RtpInfoGeneration => System.Threading.Interlocked.Read(ref _rtpInfoGeneration);


        // Drop counters for MAUs that arrive when no sink has been set up
        // for the corresponding codec — log throttled at every 256 to avoid
        // spam. Useful for telling apart "depacketizer dead" vs "no sink".
        private long _videoDroppedNoSink;
        private long _audioDroppedNoSink;


        #endregion ############################################################


        #region Constructor ###################################################

        public RTSPClient() {
            videoDepacketizer = new WmrptVideoDepacketizer();
            audioDepacketizer = new WmrptAudioDepacketizer();
            // Dump the WMRTP per-MAU timing fields (header ts vs Send Time vs
            // Correspondence NTP↔RTP vs Decode/Presentation/NPT) for the first
            // few MAUs to the always-on diag log, so we can see which timeline
            // THIS server populates for x-wmf-pf (the RTP header timestamp may
            // not be the reliable presentation clock).
            videoDepacketizer.DiagLog = msg => RtcpDiagLog(msg);
            audioDepacketizer.DiagLog = msg => RtcpDiagLog(msg);
            // Correspondence-offset cross-check (logged only; does NOT drive sync).
            videoDepacketizer.TimingSample = (ntpSec, hdrRaw) => FeedCorrSample(true, ntpSec, hdrRaw);
            audioDepacketizer.TimingSample = (ntpSec, hdrRaw) => FeedCorrSample(false, ntpSec, hdrRaw);

            videoDepacketizer.NalUnitReady += async (s, eventData) => {
                // Discard encrypted MAUs — DRM is not supported.
                if (eventData.Encrypted) return;

                // Discard non-sync-point video MAUs after a packet loss until
                // we hit an IDR. Otherwise the decoder gets P/B frames with a
                // missing reference.
                if (eventData.PostLoss && !eventData.SyncPoint) {
                    Trace.WriteLine($"Skipping post-loss non-keyframe MAU (len={eventData.data.Length})");
                    return;
                }

                // PTS monitor (Layer 4f) — runs before SubmitChunk so the
                // event fires the moment we observe the bad PTS, regardless
                // of which pipeline accepts the sample below. Decoder-open
                // stopwatch (Layer 4g(a)) is armed at the first submit
                // attempt so we measure FFME's open latency from this point.
                MonitorPts(isAudio: false, rtpTs: eventData.timestamp);
                ArmDecoderOpenStopwatch();

                // Capture the first video MAU's wire-side RTP timestamp
                // for the external-sync controller (Phase 2.5). It uses
                // the difference between audioFirstWirePts and
                // videoFirstWirePts to target the server's intended
                // sync (which is the authoritative truth across the
                // RTCP-SR mapping), rather than just holding whatever
                // skew the streams happened to have at sync-baseline.
                // Idempotent — only the first packet counts.
                if (System.Threading.Interlocked.CompareExchange(ref _firstVideoMauWirePtsRaw, unchecked((long)(uint)eventData.timestamp), -1L) == -1L) {
                    Debug.WriteLine($"[ext-sync-anchor] first video MAU rtpTs={eventData.timestamp}");
                }

                // Diagnostic tap for the Video FPS Lab — forwards the video
                // elementary stream to a libav+D3DImage benchmark consumer when
                // one is attached. Cheap no-op (one null check) otherwise, so it
                // stays out of the way in normal sessions.
                if (AudioVisual.VideoFpsLab.LiveVideoTap.HasSubscribers) {
                    AudioVisual.VideoFpsLab.LiveVideoTap.Publish(eventData.data, eventData.timestamp, _wireVideoCodec);
                }

                // External-video consumer (the libav + D3DImage player).
                // When attached it owns video decoding + presentation, so the
                // FFME producer branches below are skipped. Commit the wire
                // codec on the first MAU so the consumer can pick the decoder.
                if (_externalVideoMauArrived != null) {
                    if (!_externalVideoCodecFired) {
                        _externalVideoCodecFired = true;
                        try {
                            _externalVideoCodecCommit?.Invoke(_wireVideoCodec ?? "VND.MS.WM-MPV");
                        } catch (Exception ex) {
                            Debug.WriteLine($"[ext-video] codecCommit threw: {ex.Message}");
                        }
                    }
                    try {
                        _externalVideoMauArrived(eventData.data, eventData.timestamp);
                    } catch (Exception ex) {
                        Debug.WriteLine($"[ext-video] mauArrived threw: {ex.Message}");
                    }
                    return;
                }

                // No external video consumer attached — drop the MAU. Video is
                // owned by the external controller (libav + D3DImage), which
                // wires the consumer before PLAY, so this only fires during a
                // brief startup window (if at all).
                if ((_videoDroppedNoSink & 0xFF) == 0) {
                    Trace.WriteLine($"[video] MAU dropped (no external consumer yet): len={eventData.data.Length}");
                }
                _videoDroppedNoSink++;
            };

            audioDepacketizer.AudioDataReady += async (s, eventData) => {
                // PTS monitor (Layer 4f) + decoder-open stopwatch arm
                // (Layer 4g(a)). See the symmetric block in
                // videoDepacketizer.NalUnitReady above.
                MonitorPts(isAudio: true, rtpTs: eventData.timestamp);
                ArmDecoderOpenStopwatch();

                // External-audio consumer (Phase 2/3 of the FFME-bypass).
                // When ExternalSyncMediaController is attached it owns
                // audio decoding + rendering directly. The
                // depacketized MAU here (WMRTP unwrapped: raw MP2/MP3
                // frame bytes for VND.MS.WM-MPA, raw PCM samples for
                // X-WMF-PF, etc.) is handed over and the muxer cascade
                // is skipped. First MAU also commits the codec — wire
                // codec is read from _wireAudioCodec which was set by
                // the codec-specific path's CommitAudioPipelineForWireCodec
                // call earlier in this same Rtp_AudioDataReceived
                // invocation, so it's reliably populated by now.
                if (_externalAudioMauArrived != null) {
                    if (!_externalAudioCodecFired) {
                        _externalAudioCodecFired = true;
                        string wc = _wireAudioCodec ?? "VND.MS.WM-MPA";
                        try {
                            _externalAudioCodecCommit?.Invoke(BuildExternalAudioFormat(wc));
                        } catch (Exception ex) {
                            Debug.WriteLine($"[ext-audio] codecCommit threw: {ex.Message}");
                        }
                    }
                    try {
                        _externalAudioMauArrived(eventData.data, eventData.timestamp);
                    } catch (Exception ex) {
                        Debug.WriteLine($"[ext-audio] mauArrived threw: {ex.Message}");
                    }
                    return;
                }

                // No external audio consumer attached yet — drop the MAU.
                // Audio is always owned by the external controller, which
                // wires the consumer in AttachRtspClient before PLAY, so
                // this only fires during a brief startup window (if at all).
                if ((_audioDroppedNoSink & 0xFF) == 0) {
                    Trace.WriteLine($"[audio] MAU dropped (no external consumer yet): len={eventData.data.Length}");
                }
                _audioDroppedNoSink++;
            };
        }

        #endregion ############################################################


        /// <summary>
        /// Cross-stream A/V offset (ms) from the PLAY response's RTP-Info,
        /// which gives each stream's RTP timestamp at the play point. The
        /// offset is the video pre-play padding minus the audio pre-play
        /// padding — each computed WITHIN one stream's clock, so it's epoch-
        /// free and needs no RTCP SR (WMPNss doesn't send SRs). Available as
        /// soon as both RTP-Info values + first MAUs are known (immediately,
        /// since RTP-Info arrives with the PLAY response before any data).
        /// Logs all inputs to the always-on RTCP debug file.
        /// </summary>
        public bool TryGetRtpInfoAvOffsetMs(uint firstAudioRtp, uint firstVideoRtp, out long offsetMs) {
            offsetMs = 0;
            long aInfo = System.Threading.Interlocked.Read(ref _audioRtpInfoRtptime);
            long vInfo = System.Threading.Interlocked.Read(ref _videoRtpInfoRtptime);
            if (aInfo < 0 || vInfo < 0) return false;
            long aClk = _audioClockHz > 0 ? _audioClockHz : 90000L;
            long vClk = _videoClockHz > 0 ? _videoClockHz : 90000L;
            // Wrap-safe signed deltas within each stream's own RTP clock.
            long vPadTicks = (int)((uint)vInfo - firstVideoRtp);   // video frames before play point
            long aPadTicks = (int)((uint)aInfo - firstAudioRtp);   // audio before play point
            long vPadMs = vPadTicks * 1000L / vClk;
            long aPadMs = aPadTicks * 1000L / aClk;
            offsetMs = vPadMs - aPadMs;
            RtcpDiagLog($"rtpinfo-offset vInfo={vInfo} V0={firstVideoRtp} vPad={vPadMs}ms " +
                        $"aInfo={aInfo} A0={firstAudioRtp} aPad={aPadMs}ms vClk={vClk} aClk={aClk} " +
                        $"→ offset={offsetMs}ms");
            return true;
        }


        #region External Audio Consumer #######################################

        // External-audio consumer.
        // When set, audio MAUs are routed to these callbacks
        // INSTEAD of the muxer cascade — ExternalSyncMediaController
        // uses this to feed its libav-decoder + NAudio renderer.
        // _externalAudioCodecCommit fires once on the first audio
        // packet (the codec is known from SDP at that point);
        // _externalAudioMauArrived fires for each MAU.
        private Action<SoftSled.Components.AudioVisual.ExternalSync.ExternalAudioFormat> _externalAudioCodecCommit;
        private Action<byte[], uint> _externalAudioMauArrived;
        private bool _externalAudioCodecFired;

        /// <summary>
        /// Hand audio MAU routing to an external consumer. When set,
        /// audio MAUs bypass the PS/TS muxer + mp3FfmeProducer
        /// cascade entirely. The codecCommit callback receives a
        /// rich <see cref="SoftSled.Components.AudioVisual.ExternalSync.ExternalAudioFormat"/>
        /// (wire codec + MPEG layer + sample-rate hint) parsed from
        /// SDP — lets the consumer pick the right libav decoder ID.
        /// Pass <c>null</c> for both to detach. Idempotent.
        /// </summary>
        public void SetExternalAudioConsumer(
            Action<SoftSled.Components.AudioVisual.ExternalSync.ExternalAudioFormat> codecCommit,
            Action<byte[], uint> mauArrived) {
            _externalAudioCodecCommit = codecCommit;
            _externalAudioMauArrived = mauArrived;
            _externalAudioCodecFired = false;   // re-arm for a new consumer
        }

        #endregion ############################################################


        #region External Video Consumer #######################################

        // External-video consumer — the video counterpart of the audio
        // consumer above. When set, video MAUs (H.264 / MPEG-1/2 elementary
        // stream) are routed to these callbacks instead of the FFME producer
        // path, so ExternalSyncMediaController can decode them with libav and
        // present via D3DImage. codecCommit fires once on the first MAU with
        // the committed wire codec string; mauArrived fires per MAU.
        private Action<string> _externalVideoCodecCommit;
        private Action<byte[], uint> _externalVideoMauArrived;
        private bool _externalVideoCodecFired;

        /// <summary>
        /// Hand video MAU routing to an external consumer. When set, video
        /// MAUs bypass the FFME producer path entirely. <paramref name="codecCommit"/>
        /// receives the committed wire codec (e.g. "X-WMF-PF" = H.264,
        /// "VND.MS.WM-MPV" = MPEG-1/2 video) on the first MAU; <paramref name="mauArrived"/>
        /// fires per MAU. Pass <c>null</c> for both to detach. Idempotent.
        /// </summary>
        public void SetExternalVideoConsumer(
            Action<string> codecCommit,
            Action<byte[], uint> mauArrived) {
            _externalVideoCodecCommit = codecCommit;
            _externalVideoMauArrived = mauArrived;
            _externalVideoCodecFired = false;   // re-arm for a new consumer
        }

        #endregion ############################################################


        #region External Audio Format #########################################

        /// <summary>
        /// Build an <see cref="SoftSled.Components.AudioVisual.ExternalSync.ExternalAudioFormat"/>
        /// from the SDP entry for the given wire codec. Looks up the
        /// PT in <c>wmfPayloadDataDict</c>, finds the first audio
        /// entry whose Codec matches, and parses fmtp for
        /// <c>layer=</c> / <c>samplerate=</c> / <c>mode=</c> hints.
        /// Returns a populated DTO even if the lookup fails (with
        /// just the wire codec set) so the consumer always gets a
        /// non-null commit notification.
        /// </summary>
        private SoftSled.Components.AudioVisual.ExternalSync.ExternalAudioFormat
            BuildExternalAudioFormat(string wireCodec) {
            var fmt = new SoftSled.Components.AudioVisual.ExternalSync.ExternalAudioFormat {
                WireCodec = wireCodec,
            };
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (entry.Type != MediaType.Audio) continue;
                if (!string.Equals(entry.Codec, wireCodec, StringComparison.OrdinalIgnoreCase)) continue;
                string fp = entry.FormatParameter ?? "";
                // Parse the fmtp token list. Recognised keys:
                //   layer=N (MPA family — MPEG audio layer 1/2/3)
                //   samplerate=N (audio sample rate Hz)
                //   mode=mono|stereo
                //   channels=N (X-WMF-PF PCM)
                //   bitspersample=N (X-WMF-PF PCM, 8/16/24/32)
                //   codec=pcm_s16be|pcm_s16le (X-WMF-PF PCM endian hint)
                //   bitrate=N (informational, not used here)
                foreach (string token in fp.Split(';')) {
                    string t = token.Trim();
                    if (t.Length == 0) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = t.Substring(eq + 1).Trim();
                    if (k == "layer" && int.TryParse(v, out int layer)) fmt.MpegLayer = layer;
                    else if (k == "samplerate" && int.TryParse(v, out int sr)) fmt.SampleRateHint = sr;
                    else if (k == "channels" && int.TryParse(v, out int ch)) fmt.ChannelsHint = ch;
                    else if (k == "bitspersample" && int.TryParse(v, out int bps)) fmt.BitsPerSampleHint = bps;
                    // WMA fmtp: blocksize→block_align, bitrate→bit_rate,
                    // config→codec extradata (hex). samplesize is the PCM
                    // bits/sample of the SOURCE (not the wire) — informational.
                    else if (k == "blocksize" && int.TryParse(v, out int ba)) fmt.BlockAlign = ba;
                    else if (k == "bitrate" && int.TryParse(v, out int br)) fmt.BitRate = br;
                    else if (k == "config") fmt.ExtraData = HexToBytes(v);
                    else if (k == "codec") {
                        // For PCM the codec hint usually carries
                        // endianness: pcm_s16be vs pcm_s16le.
                        if (v.IndexOf("be", StringComparison.OrdinalIgnoreCase) >= 0) fmt.PcmBigEndian = true;
                    } else if (k == "mode") {
                        if (v.Equals("mono", StringComparison.OrdinalIgnoreCase)) fmt.ChannelsHint = 1;
                        else if (v.Equals("stereo", StringComparison.OrdinalIgnoreCase)) fmt.ChannelsHint = 2;
                    }
                }
                // For X-WMF-PF audio the fmtp tokens above are usually
                // empty — the actual format is in the WAVEFORMATEX
                // blob hex-encoded inside `config=`. SDP parsing
                // already turned it into a parsed
                // <see cref="WAVEFORMATEX"/> on
                // <c>entry.AM_Media_Format.FormatData</c>. Pull rate
                // / channels / bits-per-sample from there. (Don't
                // overwrite if the fmtp explicitly carried them —
                // the MPA family uses them, only PCM omits them.)
                try {
                    var wfx = entry.AM_Media_Format?.FormatData as SoftSled.Components.AudioVisual.FormatStructures.WAVEFORMATEX;
                    if (wfx != null) {
                        if (fmt.SampleRateHint == 0 && wfx.nSamplesPerSec > 0)
                            fmt.SampleRateHint = wfx.nSamplesPerSec;
                        if (fmt.ChannelsHint == 0 && wfx.nChannels > 0)
                            fmt.ChannelsHint = wfx.nChannels;
                        if (fmt.BitsPerSampleHint == 0 && wfx.wBitsPerSample > 0)
                            fmt.BitsPerSampleHint = wfx.wBitsPerSample;
                    }
                } catch { /* AM_Media_Format may be null or FormatData absent */ }

                // Last-resort fallback for sample rate ONLY: rtpmap
                // ClockHz. For X-WMF-PF this is 1000 (a 1ms tick,
                // not the audio sample rate) and is intentionally
                // NOT used unless WAVEFORMATEX was unavailable —
                // setting decoder sample_rate=1000 against a real
                // 48kHz wire produces audio starvation (decoder
                // produces 1/48 the expected samples, NAudio
                // drains instantly). For MPA the ClockHz IS the
                // audio rate (typically 90000 for MPA which is
                // wrong but acceptable since MPA decoders read
                // from the frame header anyway).
                if (fmt.SampleRateHint == 0 && entry.ClockHz > 0) {
                    bool isXWmfPf = string.Equals(wireCodec, "X-WMF-PF", StringComparison.OrdinalIgnoreCase);
                    bool isWma    = string.Equals(wireCodec, "WMA", StringComparison.OrdinalIgnoreCase);
                    // WMA rtpmap clock is 1000 (a 1ms tick, NOT the audio rate),
                    // same trap as X-WMF-PF — don't use ClockHz as the rate.
                    if (!isXWmfPf && !isWma) fmt.SampleRateHint = entry.ClockHz;
                }
                // WMA channels come from the rtpmap "wma/1000/<ch>" encoding
                // parameter (e.g. "2") when the fmtp didn't carry channels=.
                if (fmt.ChannelsHint == 0
                    && string.Equals(wireCodec, "WMA", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse((entry.EncodingParameters ?? "").Trim(), out int wch) && wch > 0) {
                    fmt.ChannelsHint = wch;
                }
                break;
            }
            return fmt;
        }

        /// <summary>Decode an even-length hex string (e.g. the WMA fmtp
        /// <c>config=</c> blob "008800000f00e55c0000") to bytes. Returns null
        /// on malformed input.</summary>
        private static byte[] HexToBytes(string hex) {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.Trim();
            if ((hex.Length & 1) != 0) return null;
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) {
                if (!byte.TryParse(hex.Substring(i * 2, 2),
                                   System.Globalization.NumberStyles.HexNumber,
                                   System.Globalization.CultureInfo.InvariantCulture, out b[i]))
                    return null;
            }
            return b;
        }

        #endregion ############################################################


        #region Main Methods ##################################################

        public void Connect(string url, RTP_TRANSPORT rtp_transport, MEDIA_REQUEST media_request = MEDIA_REQUEST.VIDEO_AND_AUDIO) {

            Rtsp.RtspUtils.RegisterUri();

            System.Diagnostics.Debug.WriteLine("Connecting to " + url);
            this.url = url;

            Uri uri = new Uri(this.url);
            hostname = uri.Host;
            port = uri.Port;

            // We can ask the RTSP server for Video, Audio or both. If we don't want audio we don't need to SETUP the audio channal or receive it
            client_wants_video = false;
            client_wants_audio = false;
            if (media_request == MEDIA_REQUEST.VIDEO_ONLY || media_request == MEDIA_REQUEST.VIDEO_AND_AUDIO) client_wants_video = true;
            if (media_request == MEDIA_REQUEST.AUDIO_ONLY || media_request == MEDIA_REQUEST.VIDEO_AND_AUDIO) client_wants_audio = true;

            // Connect to a RTSP Server. The RTSP session is a TCP connection
            rtsp_socket_status = RTSP_STATUS.Connecting;
            try {
                rtsp_socket = new Rtsp.RtspTcpTransport(hostname, port);
            } catch {
                rtsp_socket_status = RTSP_STATUS.ConnectFailed;
                System.Diagnostics.Debug.WriteLine("Error - did not connect");
                return;
            }

            if (rtsp_socket.Connected == false) {
                rtsp_socket_status = RTSP_STATUS.ConnectFailed;
                System.Diagnostics.Debug.WriteLine("Error - did not connect");
                return;
            }

            rtsp_socket_status = RTSP_STATUS.Connected;

            // Phase-0 wire dump: when SOFTSLED_RTSP_WIRE_DUMP=1 every byte
            // read/written on this socket is teed into %TEMP%\softsled-rtsp-wire.log
            // so we can extract the DESCRIBE response and decide between SDP
            // pgmpu and in-band ASF header strategies. No-op when env var is unset.
            Rtsp.IRtspTransport listenerTransport = SoftSled.Components.Diagnostics.RtspWireDumper.MaybeWrap(rtsp_socket, null);

            // Connect a RTSP Listener to the RTSP Socket (or other Stream) to send RTSP messages and listen for RTSP replies
            rtsp_client = new Rtsp.RtspListener(listenerTransport);
            rtsp_client.AutoReconnect = false;
            rtsp_client.MessageReceived += Rtsp_MessageReceived;
            rtsp_client.DataReceived += Rtp_VideoDataReceived;
            rtsp_client.Start(); // start listening for messages from the server (messages fire the MessageReceived event)


            // Check the RTP Transport
            // If the RTP transport is TCP then we interleave the RTP packets in the RTSP stream
            // If the RTP transport is UDP, we initialise two UDP sockets (one for video, one for RTCP status messages)
            // If the RTP transport is MULTICAST, we have to wait for the SETUP message to get the Multicast Address from the RTSP server
            this.rtp_transport = rtp_transport;
            if (rtp_transport == RTP_TRANSPORT.UDP) {
                video_udp_pair = new Rtsp.UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
                // Phase-0c UDP RTP tap: parses RTP header per packet and dumps
                // first 128B of payload to %TEMP%\softsled-rtp-wire.log when
                // SOFTSLED_RTSP_WIRE_DUMP=1. Capped at 200 packets/label so a
                // long playback doesn't fill disk.
                SoftSled.Components.Diagnostics.RtspWireDumper.AttachUdpTap(video_udp_pair, "video", null);
                video_udp_pair.DataReceived += Rtp_VideoDataReceived;
                video_udp_pair.Start(); // start listening for data on the UDP ports
                audio_udp_pair = new Rtsp.UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
                SoftSled.Components.Diagnostics.RtspWireDumper.AttachUdpTap(audio_udp_pair, "audio", null);
                audio_udp_pair.DataReceived += Rtp_AudioDataReceived;
                audio_udp_pair.Start(); // start listening for data on the UDP ports
            }
            if (rtp_transport == RTP_TRANSPORT.TCP) {
                // Nothing to do. Data will arrive in the RTSP Listener
            }
            if (rtp_transport == RTP_TRANSPORT.MULTICAST) {
                // Nothing to do. Will open Multicast UDP sockets after the SETUP command
            }

            // Send DESCRIBE
            RtspRequest describe_message = new RtspRequestDescribe();
            describe_message.RtspUri = new Uri(url);
            describe_message.AddHeader(AcceptHeader);
            describe_message.AddHeader(LanguageHeader);
            describe_message.AddHeader(SupportedHeader);
            describe_message.AddHeader(UserAgent);
            rtsp_client.SendMessage(describe_message);
        }

        public void Pause() {
            if (rtsp_client != null) {
                // Send PAUSE
                RtspRequest pause_message = new RtspRequestPause();
                pause_message.RtspUri = new Uri(url);
                pause_message.Session = session;
                pause_message.AddHeader(LanguageHeader);
                pause_message.AddHeader(SupportedHeader);
                pause_message.AddHeader(UserAgent);
                rtsp_client.SendMessage(pause_message);

                // After PAUSE the server stops sending RTP. The next Play()
                // MUST hit the wire even if (startMs, rate) match what was
                // cached from the most recent pre-PAUSE PLAY — otherwise
                // the wire-idempotency check at the top of Play() will
                // suppress the resume PLAY and the server stays paused
                // forever (observed: video frame stuck at pause-time
                // position, audio continues from buffered samples but
                // never gets new ones either).
                _resumeNextPlay = true;
                Debug.WriteLine("[rtsp] PAUSE sent — armed _resumeNextPlay " +
                                "so the next Play() bypasses wire-idempotency");
            }
        }

        public void Play() {
            Play(/*startMs*/ -1L, /*rate*/ 1.0);
        }

        /// <summary>
        /// Send an RTSP PLAY with optional <c>Range:</c> seek and
        /// <c>Scale:</c>/<c>Speed:</c> rate. Replaces the parameterless
        /// overload as the canonical implementation; the old one delegates
        /// here with -1 / 1.0 sentinels for "no change".
        ///
        /// IMPORTANT lifecycle note: RTSP requires DESCRIBE → SETUP →
        /// PLAY. The existing message handler at
        /// <see cref="Rtsp_MessageReceived"/> already sends an automatic
        /// PLAY once the last SETUP completes. Callers that hit this
        /// method *before* the initial PLAY has been issued (typical:
        /// AvCtrlHandler.Start fires almost the same instant as
        /// OpenMedia, well before SETUP completes) would otherwise
        /// stack up a flood of premature PLAY requests on the wire and
        /// most servers respond with 455 "Method not valid in this
        /// state" or simply ignore them. The fix: until the auto-PLAY
        /// fires we only *cache* the requested startMs/rate — the
        /// auto-PLAY then picks them up via
        /// <see cref="SendPlayWithCachedParams"/> and we end up sending
        /// exactly one PLAY with the right Range/Scale/Speed.
        /// </summary>
        /// <param name="startMs">Absolute media position to seek to in
        /// milliseconds, or -1 to omit the Range header (server resumes
        /// from the current position).</param>
        /// <param name="rate">Playback rate. 1.0 = omit Scale/Speed.
        /// Any other value (positive or negative) is written into both
        /// <c>Scale:</c> and <c>Speed:</c> headers — servers vary in
        /// which one they actually honour, so we send both. The captures
        /// show PLAY responses always echo Scale and Speed when
        /// requested, even if the value is rounded.</param>
        public void Play(long startMs, double rate) {
            // Always update the cached request — used both as the
            // params for the auto-PLAY (if it hasn't fired yet) and
            // for mid-session re-PLAYs (SetRate, seek). Only overwrite
            // the cached startMs when the caller actually specified
            // one; -1 means "resume / no Range" and should NOT be
            // remembered as "seek to 0" — that would force every
            // subsequent SetRate to rewind the server.
            _currentRateRequested = rate;
            if (startMs >= 0) _currentStartMsRequested = startMs;

            if (rtsp_client == null) return;

            // Pre-auto-PLAY: defer. The auto-PLAY in Rtsp_MessageReceived
            // (fired after the last SETUP response) will pick up the
            // cached params and send the single PLAY of the session.
            if (!_initialPlayFired) {
                Debug.WriteLine($"[rtsp] Play({startMs},{rate}) deferred — " +
                                $"awaiting initial SETUP-completion PLAY");
                return;
            }

            // Wire-level idempotency. WMC repeats Start commands
            // aggressively — on remote-control retransmits, held FF
            // buttons, etc. Captured pattern: three Start(rate=3) within
            // 31ms when the user tapped fast-forward once. Each PLAY
            // restarts the server's pacing engine; the second-and-third
            // restarts arriving while the first is still propagating
            // throws WMPNss into a "frozen stream" state until the next
            // PAUSE/PLAY cycle. Skip the wire write if the requested
            // (startMs, rate) exactly matches what we last shipped.
            //
            // EXCEPTION: a Pause() in between invalidates the cached
            // wire state — the server has stopped sending RTP and needs
            // a fresh PLAY to resume, even with identical params. The
            // _resumeNextPlay flag set by Pause() forces this first
            // post-PAUSE Play() through. After that, the suppression
            // logic re-engages normally (so the second of two back-to-
            // back WMC resume-Starts still gets suppressed).
            //
            // -1 startMs is "use cached" — equate to the last-sent
            // startMs for the purposes of this check so a pure rate
            // change with no seek doesn't get artificially differentiated.
            long candidateStart = startMs >= 0 ? startMs : _lastSentStartMs;
            if (!_resumeNextPlay &&
                _lastSentStartMs == candidateStart &&
                System.Math.Abs(_lastSentRate - rate) < 0.0001) {
                Debug.WriteLine($"[rtsp] Play({startMs},{rate}) suppressed — " +
                                $"wire state already (start={_lastSentStartMs}, rate={_lastSentRate})");
                return;
            }
            if (_resumeNextPlay) {
                Debug.WriteLine($"[rtsp] Play({startMs},{rate}) bypassing idempotency — " +
                                $"resume-after-PAUSE armed");
                _resumeNextPlay = false;
            }

            // Post-auto-PLAY: mid-session change. Send a fresh PLAY
            // that atomically replaces the prior playback params.
            SendPlayMessage(startMs, rate);
        }

        /// <summary>Set by <see cref="Pause"/>, consumed (and cleared)
        /// by the next <see cref="Play"/>. Forces that one Play through
        /// the wire-idempotency check so resume-after-pause always hits
        /// the server with a fresh PLAY — without this, identical
        /// (startMs, rate) before and after a Pause/Resume cycle would
        /// be suppressed and the server would stay paused.</summary>
        private bool _resumeNextPlay;

        /// <summary>Most recent (startMs, rate) actually written to the
        /// RTSP wire by <see cref="SendPlayMessage"/>. Used to suppress
        /// no-op re-PLAYs from WMC's repeating Start commands. -1 / 1.0
        /// means "no PLAY sent yet this session".</summary>
        private long _lastSentStartMs = -1;
        private double _lastSentRate = 1.0;

        #endregion ############################################################


        /// <summary>
        /// Send an RTSP PLAY directly to the wire, bypassing the
        /// auto-PLAY-deferral guard in <see cref="Play"/>. Called from
        /// (a) the auto-PLAY trigger inside Rtsp_MessageReceived after
        /// the last SETUP response, and (b) the mid-session Play()/
        /// SetRate() path once <see cref="_initialPlayFired"/> is set.
        /// </summary>
        private void SendPlayMessage(long startMs, double rate) {
            if (rtsp_client == null) return;

            RtspRequest play_message = new RtspRequestPlay();
            play_message.RtspUri = new Uri(url);
            play_message.Session = session;
            play_message.AddHeader(LanguageHeader);
            play_message.AddHeader(SupportedHeader);
            play_message.AddHeader(UserAgent);

            // Range: npt=<sec>-  — sent whenever the caller asked for an
            // explicit seek. WMC's "resume" sentinel arrives as -1 and we
            // drop the header to let the server keep its own position.
            if (startMs >= 0) {
                double sec = startMs / 1000.0;
                play_message.AddHeader(
                    "Range: npt=" + sec.ToString("0.000",
                        System.Globalization.CultureInfo.InvariantCulture) + "-");
            }

            // Scale: / Speed: — only when rate ≠ 1.0. Captured McxDMS
            // responses always show Scale and Speed echoed when requested.
            // Some servers prefer Scale, some Speed; sending both is the
            // recommended belt-and-braces approach (RFC 7826 §18.46/§18.50).
            if (System.Math.Abs(rate - 1.0) > 0.0001) {
                string rateStr = rate.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture);
                play_message.AddHeader("Scale: " + rateStr);
                play_message.AddHeader("Speed: " + rateStr);
            }

            // First-sample-after-seek arm: clear the per-stream "first
            // PTS captured" flags so the UNRECOVERABLE_SKEW first-sample
            // A/V skew check (Layer 4g(b)) re-runs on this seek.
            ArmFirstSampleAfterSeek();

            // Mark initial-PLAY fired so subsequent public Play()
            // calls actually reach the wire. Record what we sent so
            // the wire-idempotency check in Play() can suppress
            // duplicate Starts from WMC.
            _initialPlayFired = true;
            _lastSentStartMs = startMs >= 0 ? startMs : _lastSentStartMs;
            _lastSentRate = rate;

            // Clear the cached start position after sending. Once the
            // server has consumed this PLAY's Range and started
            // streaming, "where we are" is the SERVER's current position,
            // not the original seek. Subsequent SetRate() calls would
            // re-fire PLAY with the same stale Range — observed: every
            // PLAY in a session repeated Range=npt=156.850 (the open-
            // time seek position) on every FF/RW, causing the server
            // to restart playback at 156.850s for every speed change.
            // -1 means "no Range header, continue from current server
            // position" — exactly what mid-session rate changes want.
            // Explicit seeks always call Play(startMs, ...) directly
            // and supply a fresh startMs so this clear is safe.
            if (startMs >= 0) {
                _currentStartMsRequested = -1;
            }

            rtsp_client.SendMessage(play_message);
        }

        /// <summary>
        /// Used by the auto-PLAY trigger in Rtsp_MessageReceived (after
        /// the last SETUP response) to send a PLAY that honours any
        /// startMs/rate that WMC requested via Start while the SETUP
        /// handshake was still in flight.
        /// </summary>
        internal void SendPlayWithCachedParams() {
            SendPlayMessage(
                startMs: _currentStartMsRequested >= 0 ? _currentStartMsRequested : -1,
                rate: double.IsNaN(_currentRateRequested) ? 1.0 : _currentRateRequested);
        }

        /// <summary>
        /// Mid-session rate change. Issues a PLAY at the current position
        /// with the new Scale/Speed. RTSP semantics: a new PLAY atomically
        /// replaces the prior playback parameters, so this works whether
        /// or not playback is currently paused.
        ///
        /// If the initial auto-PLAY hasn't fired yet, this just caches
        /// the rate and the auto-PLAY (when it fires) will include it —
        /// avoiding a duplicate request on the wire.
        /// </summary>
        public void SetRate(double rate) {
            // Re-PLAY at the most recently-requested start time so the
            // server resumes where we were rather than rewinding to 0.
            // (Callers that want to combine seek + rate change should
            // call Play(startMs, rate) directly.)
            long resumeMs = _currentStartMsRequested >= 0
                            ? _currentStartMsRequested : -1;
            Play(resumeMs, rate);
        }

        /// <summary>Set to true the first time we actually send a PLAY
        /// message to the wire. Until this is true, Play()/SetRate()
        /// calls are stored to <see cref="_currentStartMsRequested"/>
        /// and <see cref="_currentRateRequested"/> but not transmitted —
        /// the auto-PLAY in Rtsp_MessageReceived (after the last SETUP
        /// response) is the one that actually fires the first PLAY and
        /// picks up whatever cached params accumulated during the
        /// DESCRIBE/SETUP phase. Reset in Stop() / Connect().</summary>
        private volatile bool _initialPlayFired;

        /// <summary>
        /// Update the <c>Buffer-Info.dlna.org</c> hint mid-session.
        /// Sent as SET_PARAMETER. If no session is established yet the
        /// values are stashed and applied on the next SETUP. Fire-and-
        /// forget — servers commonly ignore mid-session Buffer-Info,
        /// in which case we just keep using the SETUP-time value.
        /// </summary>
        public void SetBufferInfo(long bandwidthBps, bool optimisedPreroll) {
            // Stash so the next SETUP picks them up even if there's no
            // session yet.
            _bufferInfoBandwidthBps = bandwidthBps;
            _bufferInfoOptimisedPreroll = optimisedPreroll;

            if (rtsp_client == null || string.IsNullOrEmpty(session)) {
                // No live session — nothing to send right now.
                return;
            }

            try {
                var msg = new RtspRequestSetParameter();
                msg.RtspUri = new Uri(url);
                msg.Session = session;
                msg.AddHeader(LanguageHeader);
                msg.AddHeader(SupportedHeader);
                msg.AddHeader(UserAgent);
                msg.AddHeader(BuildBufferInfoHeader(bandwidthBps, optimisedPreroll));
                rtsp_client.SendMessage(msg);
            } catch (Exception ex) {
                Debug.WriteLine($"[rtsp] SetBufferInfo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Build the <c>Buffer-Info.dlna.org</c> request-line header value
        /// from a bandwidth hint and the OptimisedPreroll flag.
        ///
        /// <para>Capture-observed Xbox 360 values (May 2026 capture from
        /// a real Xbox playing the same NTSC_XAC3 recorded-TV stream
        /// that WMPNss was under-feeding us at ~21% of real-time):</para>
        /// <list type="bullet">
        ///   <item>Audio SETUP:
        ///   <c>dejitter=6624000;CDB=6553600;BTM=0;TD=2000;BFR=1</c></item>
        ///   <item>Video SETUP:
        ///   <c>dejitter=6624000;CDB=39321600;BTM=0;TD=2000;BFR=1</c></item>
        /// </list>
        ///
        /// <para>Two important differences vs the previous SoftSled
        /// values (which both used <c>CDB=6553600;BFR=0</c>):</para>
        /// <list type="number">
        ///   <item><b>BFR=1</b> (was 0). The Buffer-Info BFR field
        ///   advertises that the client WILL send Buffer Fullness
        ///   Report RTCP feedback (PT=205 FMT=3) — which we have
        ///   always done per the <c>_audioBfrEnabled</c>/
        ///   <c>_videoBfrEnabled</c> path. The previous BFR=0 was a
        ///   self-inflicted lie that contradicted the actual BFR
        ///   packets going out on RTCP. WMPNss appears to use BFR as
        ///   a pacing-mode hint: BFR=0 → conservative throttle (~20%
        ///   real-time), BFR=1 → rate-adaptive pacing using the BFR
        ///   feedback (full real-time delivery as observed on
        ///   Xbox).</item>
        ///   <item><b>Per-stream CDB</b>: video SETUP uses
        ///   <c>CDB=39321600</c> (≈37.5 MB, 6× the audio value) to
        ///   match Xbox. CDB is the Content Data Buffer the client
        ///   reserves for this stream; a larger video CDB lets the
        ///   server burst more before pausing. Audio CDB stays at
        ///   <c>6553600</c> (≈6.25 MB) per the Xbox capture.</item>
        /// </list>
        ///
        /// <para>The <paramref name="isVideo"/> flag selects which CDB.
        /// SET_PARAMETER mid-session defaults to the larger video CDB
        /// (re-buffering scenarios typically target the bigger video
        /// stream).</para>
        ///
        /// <para>dejitter sizing is unchanged: scales inversely with
        /// bandwidth, capped at 6624000 (≈6.6 MB ≈ 1 s at 50 Mbps),
        /// floor at 16384. BTM held at capture default (0).</para>
        /// </summary>
        internal static string BuildBufferInfoHeader(long bandwidthBps, bool optimisedPreroll,
                                                     bool isVideo = true) {
            // dejitter inverse-scales with bandwidth: at 50 Mbps we can
            // get away with a small dejitter (~0.13s of 8-byte audio);
            // at 1 Mbps we need much more headroom. Cap at the captured
            // 6624000 (≈6.6 MB ≈ 1 s at 50 Mbps), floor at 16384.
            long dejitter;
            if (bandwidthBps <= 0) {
                dejitter = 6624000; // unknown — use the captured default
            } else {
                // Heuristic: 1 second of bandwidth, divided by 8 (×8 b/byte)
                // gives bytes-per-second, then we keep ~1 s headroom.
                dejitter = bandwidthBps / 8;
                if (dejitter > 6624000) dejitter = 6624000;
                if (dejitter < 16384) dejitter = 16384;
            }
            int btm = optimisedPreroll ? 0 : 0;        // unchanged (capture default)
            int bfr = 1;                                // see method doc — Xbox=1, was 0
            int cdb = isVideo ? 39321600 : 6553600;    // per-stream CDB per Xbox capture
            const int td = 2000;
            return "Buffer-Info.dlna.org: dejitter=" + dejitter +
                   ";CDB=" + cdb +
                   ";BTM=" + btm +
                   ";TD=" + td +
                   ";BFR=" + bfr;
        }

        /// <summary>Cached so SETUP can pick them up if SetBufferInfo
        /// fired before the session was established.</summary>
        private long _bufferInfoBandwidthBps = -1;
        private bool _bufferInfoOptimisedPreroll = false;

        /// <summary>Cached so SetRate can re-PLAY at the same position.</summary>
        private long _currentStartMsRequested = -1;
        private double _currentRateRequested = 1.0;


        #region MS-DMCT Playback Events #######################################

        // ====================================================================
        //  These three events are consumed by MediaController and relayed
        //  on to VirtualChannelAvCtrlHandler.OnMediaEvent as MS-DMCT codes
        //  RTSP_DISCONNECT (3), PTS_ERROR (5), UNRECOVERABLE_SKEW (6).
        // ====================================================================

        /// <summary>Fires when the RTSP socket disconnects unexpectedly
        /// (socket exception, UDP receive failure, server BYE on the
        /// data stream, keepalive miss-count exceeded). The exception
        /// carries whatever the failing layer threw so the controller
        /// can derive a reasonable HRESULT.</summary>
        public event Action<Exception> Disconnected;

        /// <summary>Fires when a PTS jump in the depacketizer exceeds
        /// the spec thresholds (delta &lt; -200ms or &gt; 2000ms vs the
        /// previous PTS on the same stream).</summary>
        public event Action<PtsErrorInfo> PtsError;

        /// <summary>Fires when the decoder takes more than 500ms to
        /// open after the first SubmitChunk, OR when the audio↔video
        /// first-sample PTS skew exceeds 3500ms.</summary>
        public event Action<SkewInfo> UnrecoverableSkew;

        /// <summary>Fires when the server pushes an RTSP ANNOUNCE carrying a
        /// DLNA end-of-stream event (<c>Event-Type.dlna.org: 2000</c>), i.e.
        /// the content reached its natural end. This is the WMC
        /// <c>com.microsoft.wm.eosmsg</c> feature surfaced over RTSP. The
        /// controller re-raises this as <c>MediaEnded</c>, which the AVCTRL
        /// virtual-channel handler maps to the MS-DMCT END_OF_MEDIA event so
        /// WMC tears the session down / advances the playlist.</summary>
        public event Action EndOfStream;

        // --- internal helpers used by 4d-4g of the playback plan ------------
        internal void RaiseDisconnected(Exception ex) {
            try { Disconnected?.Invoke(ex); } catch (Exception subEx) {
                Debug.WriteLine($"[rtsp] Disconnected handler threw: {subEx.Message}");
            }
        }
        internal void RaisePtsError(PtsErrorInfo info) {
            try { PtsError?.Invoke(info); } catch (Exception subEx) {
                Debug.WriteLine($"[rtsp] PtsError handler threw: {subEx.Message}");
            }
        }
        internal void RaiseUnrecoverableSkew(SkewInfo info) {
            try { UnrecoverableSkew?.Invoke(info); } catch (Exception subEx) {
                Debug.WriteLine($"[rtsp] UnrecoverableSkew handler threw: {subEx.Message}");
            }
        }
        internal void RaiseEndOfStream() {
            try { EndOfStream?.Invoke(); } catch (Exception subEx) {
                Debug.WriteLine($"[rtsp] EndOfStream handler threw: {subEx.Message}");
            }
        }

        #endregion ############################################################


        /// <summary>Re-arms the "first sample after seek" detector used
        /// by both the PTS monitor and the UNRECOVERABLE_SKEW first-
        /// sample-A/V-skew check. Called at every PLAY (seek).</summary>
        private void ArmFirstSampleAfterSeek() {
            System.Threading.Interlocked.Exchange(ref _firstAudioPtsCapturedMs, long.MinValue);
            System.Threading.Interlocked.Exchange(ref _firstVideoPtsCapturedMs, long.MinValue);
            _avSkewFiredForThisSeek = 0;
        }

        // PTS monitor state (Layer 4f) — last-PTS-per-stream + first-after-seek.
        private long _lastAudioPtsMs;
        private long _lastVideoPtsMs;
        private long _firstAudioPtsCapturedMs = long.MinValue;
        private long _firstVideoPtsCapturedMs = long.MinValue;
        private int _avSkewFiredForThisSeek;   // 0 = not yet, 1 = fired
        // UNRECOVERABLE_SKEW decoder-open state (Layer 4g(a)).
        private readonly Stopwatch _decoderOpenSw = new Stopwatch();
        private int _decoderOpenArmed;          // 0 = not armed, 1 = armed (stopwatch running)
        private int _decoderOpenFired;          // 0 = not fired, 1 = fired
        // Per-stream RTP timestamp clock rate (Hz), resolved from the SDP
        // rtpmap of the codec the server actually picked. Set at wire-codec
        // commit (first RTP data packet); feeds the RTCP-SR StreamClock.
        private uint _audioClockHz;
        private uint _videoClockHz;
        // RTCP-SR-anchored NTP↔RTP clocks (Layer 4e(iv)).
        private StreamClock _audioClock;
        private StreamClock _videoClock;

        public void Stop() {
            // Flag set first so the keepalive miss-counter and other
            // background paths know any subsequent socket exception is
            // expected (user requested teardown) and don't escalate it
            // to a Disconnected event.
            _stopRequested = true;

            if (rtsp_client != null) {
                // Send TEARDOWN
                RtspRequest teardown_message = new RtspRequestTeardown();
                teardown_message.RtspUri = new Uri(url);
                teardown_message.Session = session;
                teardown_message.AddHeader(LanguageHeader);
                teardown_message.AddHeader(SupportedHeader);
                teardown_message.AddHeader(UserAgent);
                rtsp_client.SendMessage(teardown_message);
            }

            // Stop the keepalive timer
            if (keepalive_timer != null) keepalive_timer.Stop();

            // clear up any UDP sockets
            if (video_udp_pair != null) video_udp_pair.Stop();
            if (audio_udp_pair != null) audio_udp_pair.Stop();

            // Drop the RTSP session
            if (rtsp_client != null) {
                rtsp_client.Stop();
            }

            // Reset wire-commit state so the next session re-detects
            // codecs on its own RTP packets.
            _wireAudioCodec = null;
            _wireVideoCodec = null;
            _audioClockHz = 0;
            _videoClockHz = 0;
            System.Threading.Interlocked.Exchange(ref _firstVideoMauWirePtsRaw, -1L);
            System.Threading.Interlocked.Exchange(ref _audioRtpInfoRtptime, -1L);
            System.Threading.Interlocked.Exchange(ref _videoRtpInfoRtptime, -1L);
            _pipelinesCommitted = false;
            try { _commitTimer?.Dispose(); } catch { }
            _commitTimer = null;

            // Reset the RTCP-SR-anchored PTS clocks + monitor state so the
            // next session's PTS_ERROR / skew detection starts clean.
            _audioClock = null;
            _videoClock = null;
            _lastAudioPtsMs = 0;
            _lastVideoPtsMs = 0;
            _firstAudioPtsCapturedMs = long.MinValue;
            _firstVideoPtsCapturedMs = long.MinValue;
            _vidRecvLoggedOnce = 0;
            _audRecvLoggedOnce = 0;
            try { _commitDiagLog?.Dispose(); } catch { }
            _commitDiagLog = null;

            // Reset playback-state caches so the next session's auto-PLAY
            // starts from defaults (no carry-over Range/Scale/Speed) and
            // the first Play()/SetRate() call against the new session
            // again routes through the auto-PLAY deferral path.
            _initialPlayFired = false;
            _currentStartMsRequested = -1;
            _currentRateRequested = 1.0;
            _lastSentStartMs = -1;
            _lastSentRate = 1.0;
            _resumeNextPlay = false;
            _bufferInfoBandwidthBps = -1;
            _bufferInfoOptimisedPreroll = false;

            // Stop the RTCP RR timer so we don't send to a closed socket.
            try { _rtcpTimer?.Dispose(); } catch { }
            _rtcpTimer = null;

            // Flush + close the RTCP diagnostic log.
            try {
                if (_rtcpDiagLog != null) {
                    _rtcpDiagLog.WriteLine($"# RTCP diag closed at {DateTime.Now:HH:mm:ss.fff}, " +
                                           $"total ticks fired: {_rtcpTickCount}");
                    _rtcpDiagLog.Dispose();
                }
            } catch { }
            _rtcpDiagLog = null;
        }


        #region TEMP - Correspondence Testing #################################

        // ---- Correspondence-offset cross-check (LOGGED ONLY — does not change
        // sync). Per stream, accumulate a steady-state (post-warmup) least-squares
        // fit of header-RTP-ts (ms) vs the Correspondence NTP wallclock, then
        // evaluate both streams at a common NTP to derive the cross-stream A/V
        // offset and log it beside the RTP-Info offset. If, over several sessions,
        // this is consistently more stable/accurate than RTP-Info, it becomes a
        // candidate to drive sync. The header ts is converted to ms via the
        // per-stream clock (x-wmf-pf=1 kHz so ts is already ms). Reset per media
        // (RTSPClient is recreated per OpenMedia). ----
        private readonly object _corrLock = new object();
        private const double CorrWarmupSec = 15.0;       // skip preroll burst + buffer-refill transient
        private double _corrFirstNtpV = -1, _corrFirstNtpA = -1;
        private long _corrFirstHdrV = -1, _corrFirstHdrA = -1;
        private double _vN, _vSx, _vSy, _vSxx, _vSxy;     // video least-squares sums (steady-state)
        private double _aN, _aSx, _aSy, _aSxx, _aSxy;     // audio sums
        private double _corrLastLogNtp = -1;
        // Convergence tracking → fires CorrespondenceOffsetReady once the estimate
        // settles, so the controller can slew the live offset to it (auto sync,
        // no per-file trim). Stable = consecutive estimates within tolerance.
        private double _corrLastEstimate = double.NaN;
        private int _corrStableCount;
        private double _corrFiredOffset = double.NaN;
        // Tightened (2026-06-04): the estimate DRIFTS as the per-stream hdr/ntp
        // slopes settle (observed +1265→-1458ms over one session), so a loose gate
        // could fire on a still-ramping value. Require many consecutive estimates
        // within a tight band so only a genuinely-settled value applies — which
        // also means it stays out of the way when RTP-Info is already correct and
        // only steps in as a safety net if it stabilises on a clearly different value.
        private const double CorrStableTolMs = 40;    // estimates within this = "stable"
        private const int CorrStableNeeded = 4;     // this many stable in a row → fire
        private const double CorrRefireDeltaMs = 200;   // re-fire if it later shifts more than this
        private const double CorrMaxPlausibleMs = 8000; // ignore implausible values
        /// <summary>Fires (once converged) with the Correspondence-derived
        /// cross-stream A/V offset in ms. Logged-cross-check value promoted to a
        /// live driver: the controller slews the pacer to it.</summary>
        public event Action<long> CorrespondenceOffsetReady;

        /// <summary>Clear the Correspondence-offset estimator's accumulated fit.
        /// Called on seek: the seek changes both streams' timeline, so pre-seek
        /// (ntp,hdr) samples would pollute the running least-squares fit.</summary>
        public void ResetCorrespondenceEstimator() {
            lock (_corrLock) {
                _corrFirstNtpV = _corrFirstNtpA = -1;
                _corrFirstHdrV = _corrFirstHdrA = -1;
                _vN = _vSx = _vSy = _vSxx = _vSxy = 0;
                _aN = _aSx = _aSy = _aSxx = _aSxy = 0;
                _corrLastLogNtp = -1;
                _corrLastEstimate = double.NaN;
                _corrStableCount = 0;
                _corrFiredOffset = double.NaN;
            }
        }

        private void FeedCorrSample(bool isVideo, double ntpSec, long hdrRaw) {
            // Convert header ts to ms via the per-stream clock (1 kHz → already ms).
            long clk = isVideo ? (_videoClockHz > 0 ? (long)_videoClockHz : 1000L)
                               : (_audioClockHz > 0 ? (long)_audioClockHz : 1000L);
            long hdrMs = hdrRaw * 1000L / clk;
            lock (_corrLock) {
                if (isVideo) {
                    if (_corrFirstNtpV < 0) { _corrFirstNtpV = ntpSec; _corrFirstHdrV = hdrMs; }
                    if (ntpSec - _corrFirstNtpV < CorrWarmupSec) return;
                    _vN++; _vSx += ntpSec; _vSy += hdrMs; _vSxx += ntpSec * ntpSec; _vSxy += ntpSec * hdrMs;
                } else {
                    if (_corrFirstNtpA < 0) { _corrFirstNtpA = ntpSec; _corrFirstHdrA = hdrMs; }
                    if (ntpSec - _corrFirstNtpA < CorrWarmupSec) return;
                    _aN++; _aSx += ntpSec; _aSy += hdrMs; _aSxx += ntpSec * ntpSec; _aSxy += ntpSec * hdrMs;
                }
                if (_vN >= 10 && _aN >= 10 && (_corrLastLogNtp < 0 || ntpSec - _corrLastLogNtp >= 5.0)
                    && CorrFit(_vN, _vSx, _vSy, _vSxx, _vSxy, out double vSlope, out double vInt)
                    && CorrFit(_aN, _aSx, _aSy, _aSxx, _aSxy, out double aSlope, out double aInt)) {
                    _corrLastLogNtp = ntpSec;
                    double cv = vSlope * ntpSec + vInt;
                    double ca = aSlope * ntpSec + aInt;
                    double cvMinusCa = cv - ca;
                    double firstHdrDiff = _corrFirstHdrV - _corrFirstHdrA;
                    double corrOffset = cvMinusCa - firstHdrDiff;
                    string rtpInfo = "n/a";
                    if (_corrFirstHdrA >= 0 && _corrFirstHdrV >= 0
                        && TryGetRtpInfoAvOffsetMs((uint)_corrFirstHdrA, (uint)_corrFirstHdrV, out long rtpOff))
                        rtpInfo = $"{rtpOff}ms (Δcorr-rtpinfo={Math.Round(corrOffset - rtpOff)}ms)";
                    RtcpDiagLog($"corr-offset-estimate: vSlope={vSlope:F4} aSlope={aSlope:F4} " +
                                $"(Cv-Ca)={cvMinusCa:F0}ms firstHdrDiff={firstHdrDiff:F0}ms " +
                                $"→ corrOffset={corrOffset:F0}ms; RTP-Info={rtpInfo} " +
                                $"(vN={_vN:F0} aN={_aN:F0})");

                    // Convergence → promote to a live offset (controller slews to it).
                    if (Math.Abs(corrOffset) <= CorrMaxPlausibleMs) {
                        if (!double.IsNaN(_corrLastEstimate)
                            && Math.Abs(corrOffset - _corrLastEstimate) <= CorrStableTolMs)
                            _corrStableCount++;
                        else
                            _corrStableCount = 0;
                        _corrLastEstimate = corrOffset;

                        bool firstFire = double.IsNaN(_corrFiredOffset);
                        bool shifted = !firstFire && Math.Abs(corrOffset - _corrFiredOffset) > CorrRefireDeltaMs;
                        if (_corrStableCount >= CorrStableNeeded && (firstFire || shifted)) {
                            _corrFiredOffset = corrOffset;
                            long val = (long)Math.Round(corrOffset);
                            RtcpDiagLog($"corr-offset CONVERGED → {val}ms (firing to controller)");
                            var h = CorrespondenceOffsetReady;
                            if (h != null) { try { h(val); } catch { } }
                        }
                    } else {
                        _corrStableCount = 0;
                        _corrLastEstimate = corrOffset;
                    }
                }
            }
        }

        private static bool CorrFit(double n, double sx, double sy, double sxx, double sxy,
                                    out double slope, out double intercept) {
            slope = 0; intercept = 0;
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9) return false;
            slope = (n * sxy - sx * sy) / denom;
            intercept = (sy - slope * sx) / n;
            return true;
        }
        #endregion ############################################################


        #region RTCP Methods ##################################################

        /// <summary>
        /// Spin up the periodic RTCP Receiver Report transmitter. WMPNss
        /// paces media ~5x slower than real time when no client RR is
        /// received. Even a minimal empty RR (RC=0, just header + sender
        /// SSRC) is enough to convince the server we're keeping up. Cadence:
        /// every 1 second for the first ~5 seconds, then sticking with 1s
        /// forever (light traffic, ~30 bytes/s).
        ///
        /// We bind via the UDP control socket each stream already opened
        /// (Rtsp.UDPSocket.SendRtcpToServer), which sends from the local
        /// client_port+1 — what the server expects as the RTCP feedback
        /// source.
        /// </summary>

        private void StartRtcpReceiverReports() {
            if (_rtcpTimer != null) return;  // already running
            RtcpDiagInit();
            // Anchor the BFR W3 ramp clock here. From this moment the W3
            // value in outbound BFR packets ramps linearly 0 → TD over
            // BfrRampDurationMs wall-clock, then holds at TD.
            _bfrRampStopwatch.Restart();
            RtcpDiagLog($"timer-start due=250ms period=1000ms " +
                        $"audio_rtcp={_audioServerRtcpPort} video_rtcp={_videoServerRtcpPort} " +
                        $"audio_bfr={_audioBfrEnabled} video_bfr={_videoBfrEnabled} " +
                        $"audio_ssrc=0x{_audioServerDataSsrc:X8} video_ssrc=0x{_videoServerDataSsrc:X8} " +
                        $"host={hostname} " +
                        $"bfr_ramp={BfrRampDurationMs}ms→TD={BfrTdMs}ms");
            // Cadence: every ~470 ms to match Xbox's observed BFR rate (~2 Hz).
            // Sending too slowly may keep server in conservative-pacing mode;
            // sending too fast risks RTCP-bandwidth violations (RFC 3550 §6.2)
            // but at 100B/packet * 2 Hz * 2 streams = 400 B/s we're well below
            // any sensible threshold.
            _rtcpTimer = new System.Threading.Timer(_ => {
                try { SendRtcpReceiverReport(); } catch (Exception ex) {
                    Debug.WriteLine($"[rtcp] tick exception: {ex.Message}");
                    RtcpDiagLog($"tick-exception: {ex.Message}");
                }
            }, null, dueTime: 200, period: 470);
            Debug.WriteLine("[rtcp] receiver-report timer started (~2 Hz)");
        }

        private void SendRtcpReceiverReport() {
            int tick = System.Threading.Interlocked.Increment(ref _rtcpTickCount);
            RtcpDiagLog($"#{tick} tick-fire " +
                        $"audio[pair={(audio_udp_pair != null)},rtcp={_audioServerRtcpPort},seqInit={_audioSeqInit},bfr={_audioBfrEnabled}] " +
                        $"video[pair={(video_udp_pair != null)},rtcp={_videoServerRtcpPort},seqInit={_videoSeqInit},bfr={_videoBfrEnabled}]");

            // Build and send RR+SDES per stream. Each compound packet:
            //   RR (32 bytes): V=2, P=0, RC=1, PT=201, length=7
            //                  sender_ssrc, source_ssrc, fraction_lost,
            //                  cumulative_lost (24-bit), ext_highest_seq,
            //                  interarrival_jitter, lsr=0, dlsr=0
            //   SDES (variable): V=2, P=0, SC=1, PT=202, length=N
            //                    sender_ssrc, CNAME item (type=1, len, text),
            //                    null terminator, 32-bit padding
            // RFC 3550 §6 requires RR and SDES be combined in a compound
            // packet; many servers ignore lone RRs (which may be exactly
            // what's happening with WMPNss).
            //
            // Phase-0c/BFR: relax the _audioSeqInit guard — without it, the
            // first RTCP packet doesn't go out until after we've received an
            // audio RTP packet, which is a chicken-and-egg with the throttle
            // hypothesis (server may not send any RTP until it gets BFR).
            // OK to send a zero-state RR — server just sees us "starting".

            if (audio_udp_pair != null && _audioServerRtcpPort > 0) {
                // Use the SETUP-advertised rtcp-fb-ssrc value when the server
                // gave us one (typical for WMPNss). Falls back to the random
                // reporter SSRC for servers that don't advertise it.
                uint audioSender = _audioRtcpSenderSsrc != 0 ? _audioRtcpSenderSsrc : _audioReporterSsrc;
                byte[] pkt = BuildRtcpCompound(audioSender, _audioServerDataSsrc,
                                                _audioBaseSeq,
                                                _audioHighestSeq, _audioSeqCycles,
                                                _audioPktsReceived,
                                                includeBfr: _audioBfrEnabled,
                                                bfrAudioStream: true);
                try {
                    audio_udp_pair.SendRtcpToServer(pkt, hostname, _audioServerRtcpPort);
                    RtcpDiagLog($"#{tick} audio-send OK {pkt.Length}B → {hostname}:{_audioServerRtcpPort} " +
                                $"extHigh={_audioSeqCycles << 16 | _audioHighestSeq} " +
                                $"w3={ComputeBfrW3Fill(true)}ms " +
                                $"firstBytes={BitConverter.ToString(pkt, 0, Math.Min(16, pkt.Length))}");
                    //if (tick <= 3 || (tick % 10) == 0)
                    //    Debug.WriteLine($"[rtcp] #{tick} audio RR+SDES{(_audioBfrEnabled ? "+BFR" : "")} " +
                    //                    $"{pkt.Length}B → {hostname}:{_audioServerRtcpPort} " +
                    //                    $"(extHigh={_audioSeqCycles << 16 | _audioHighestSeq})");
                } catch (Exception ex) {
                    Debug.WriteLine($"[rtcp] audio RR send failed: {ex.Message}");
                    RtcpDiagLog($"#{tick} audio-send EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                }
            } else {
                RtcpDiagLog($"#{tick} audio-skip pair={(audio_udp_pair != null)} rtcp={_audioServerRtcpPort}");
            }
            if (video_udp_pair != null && _videoServerRtcpPort > 0) {
                uint videoSender = _videoRtcpSenderSsrc != 0 ? _videoRtcpSenderSsrc : _videoReporterSsrc;
                byte[] pkt = BuildRtcpCompound(videoSender, _videoServerDataSsrc,
                                                _videoBaseSeq,
                                                _videoHighestSeq, _videoSeqCycles,
                                                _videoPktsReceived,
                                                includeBfr: _videoBfrEnabled,
                                                bfrAudioStream: false);
                try {
                    video_udp_pair.SendRtcpToServer(pkt, hostname, _videoServerRtcpPort);
                    RtcpDiagLog($"#{tick} video-send OK {pkt.Length}B → {hostname}:{_videoServerRtcpPort} " +
                                $"w3={ComputeBfrW3Fill(false)}ms " +
                                $"firstBytes={BitConverter.ToString(pkt, 0, Math.Min(16, pkt.Length))}");
                    //if (tick <= 3 || (tick % 10) == 0)
                    //    Debug.WriteLine($"[rtcp] #{tick} video RR+SDES{(_videoBfrEnabled ? "+BFR" : "")} " +
                    //                    $"{pkt.Length}B → {hostname}:{_videoServerRtcpPort}");
                } catch (Exception ex) {
                    Debug.WriteLine($"[rtcp] video RR send failed: {ex.Message}");
                    RtcpDiagLog($"#{tick} video-send EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                }
            } else {
                RtcpDiagLog($"#{tick} video-skip pair={(video_udp_pair != null)} rtcp={_videoServerRtcpPort}");
            }
        }

        /// <summary>
        /// Build a compound RTCP packet: RR(one reception report block) + SDES(CNAME)
        /// + optionally BFR (Buffer Fullness Report, PT=205 FMT=3).
        ///
        /// BFR is a Microsoft/DLNA RTCP feedback extension advertised in SDP via
        /// <c>a=rtcp-fb:* bfr 101</c>. The packet format is NOT publicly documented;
        /// it was reverse-engineered from an Xbox 360 ↔ WMPNss Wireshark capture.
        /// The server uses BFR feedback to decide pacing — without it, WMPNss
        /// throttles WMRTP-PCM streams to ~17% of real-time.
        ///
        /// FCI (28 bytes, 7 32-bit words, big-endian) per Xbox observation:
        ///   W1 [0..3]:  Unknown — varies; we copy stream-type-keyed values seen in capture.
        ///   W2 [4..7]:  Unknown — audio constant 0x03E80000 (rtpmap 1000 in high-16?);
        ///               video varies (~0x1770xxxx). We copy a stream-type-keyed value.
        ///   W3 [8..11]: <c>high-16 = buffer fill in ms</c>; low-16 = 0xFFFF
        ///               sentinel. The Xbox reports its REAL per-stream buffer
        ///               occupancy here (audio ramps 0→~TD then wiggles just
        ///               under it; video sits well above TD ~3200-4200). We now
        ///               carry the renderer's true audio occupancy for the audio
        ///               stream (see ComputeBfrW3Fill); video keeps the synthetic
        ///               ramp-to-TD.
        ///   W4 [12..15]: <c>high-16 = next expected RTP seq</c> (= highest received + 1);
        ///                low-16 = 0.
        ///   W5/W6 [16..23]: zero (reserved).
        ///   W7 [24..27]: high-16 = 0xFFFF; <c>low-16 = TD in ms</c> (we sent TD=2000).
        ///
        /// Returns a byte array ready for UDP send.
        /// </summary>
        private byte[] BuildRtcpCompound(uint reporterSsrc, uint sourceSsrc,
                                          ushort baseSeq, ushort highestSeq,
                                          uint seqCycles, long packetsReceived,
                                          bool includeBfr, bool bfrAudioStream) {
            // --- RR ---
            // length field is in 32-bit words minus 1. RR header (8B) + 1 RRB (24B) = 32B = 8 words.
            // So length = 7.
            byte[] rr = new byte[32];
            rr[0] = 0x81;   // V=2, P=0, RC=1
            rr[1] = 201;    // PT = RR
            rr[2] = 0; rr[3] = 7;
            WriteU32Be(rr, 4, reporterSsrc);        // sender SSRC (us, per-stream)
            WriteU32Be(rr, 8, sourceSsrc);          // source SSRC being reported (server)
            rr[12] = 0;                             // fraction lost (0 — no loss reported)
            rr[13] = 0; rr[14] = 0; rr[15] = 0;     // cumulative number of packets lost (24-bit)
            uint extHigh = (seqCycles << 16) | highestSeq;
            WriteU32Be(rr, 16, extHigh);            // extended highest sequence number received
            WriteU32Be(rr, 20, 0);                  // interarrival jitter (we don't compute — 0 is legal)
            WriteU32Be(rr, 24, 0);                  // last SR timestamp (none received → 0)
            WriteU32Be(rr, 28, 0);                  // delay since last SR (0)

            // --- SDES (one chunk, one item) ---
            byte[] cnameBytes = System.Text.Encoding.ASCII.GetBytes(_rtcpCname);
            // Chunk: SSRC(4) + item: type(1) + len(1) + text(N) + null terminator(1)
            // Then pad chunk to 32-bit boundary with zeros.
            int sdesPayloadLen = 4 + 1 + 1 + cnameBytes.Length + 1;
            int pad = (4 - (sdesPayloadLen % 4)) % 4;
            int sdesBodyLen = sdesPayloadLen + pad;
            byte[] sdes = new byte[4 + sdesBodyLen];   // 4-byte SDES header + body
            sdes[0] = 0x81;   // V=2, P=0, SC=1
            sdes[1] = 202;    // PT = SDES
            int sdesWords = (sdes.Length / 4) - 1;
            sdes[2] = (byte)((sdesWords >> 8) & 0xFF);
            sdes[3] = (byte)((sdesWords) & 0xFF);
            WriteU32Be(sdes, 4, reporterSsrc);         // per-stream reporter SSRC
            sdes[8] = 1;                              // CNAME item type
            sdes[9] = (byte)cnameBytes.Length;
            Array.Copy(cnameBytes, 0, sdes, 10, cnameBytes.Length);
            sdes[10 + cnameBytes.Length] = 0;         // terminator

            // --- BFR (PT=205, FMT=3) — optional Microsoft/DLNA buffer-fullness feedback ---
            byte[] bfr = null;
            if (includeBfr) {
                bfr = BuildBfrPacket(reporterSsrc, sourceSsrc, highestSeq, seqCycles, bfrAudioStream);
            }

            int totalLen = rr.Length + sdes.Length + (bfr?.Length ?? 0);
            byte[] combined = new byte[totalLen];
            int off = 0;
            Buffer.BlockCopy(rr, 0, combined, off, rr.Length); off += rr.Length;
            Buffer.BlockCopy(sdes, 0, combined, off, sdes.Length); off += sdes.Length;
            if (bfr != null) {
                Buffer.BlockCopy(bfr, 0, combined, off, bfr.Length);
            }
            return combined;
        }

        /// <summary>
        /// Build a 40-byte BFR (Buffer Fullness Report) RTCP feedback message.
        /// Format reverse-engineered from Xbox 360 ↔ WMPNss capture. See
        /// <see cref="BuildRtcpCompound"/> for the field-level rationale.
        /// </summary>
        private byte[] BuildBfrPacket(uint senderSsrc, uint mediaSsrc, ushort highestSeq,
                                       uint seqCycles, bool isAudio) {
            byte[] pkt = new byte[40];
            // Header
            pkt[0] = 0x83;            // V=2, P=0, FMT=3
            pkt[1] = 205;             // PT = RTPFB
            pkt[2] = 0; pkt[3] = 9;   // length 9 → 40 bytes
            WriteU32Be(pkt, 4, senderSsrc);       // sender SSRC (us, per-stream)
            WriteU32Be(pkt, 8, mediaSsrc);        // media SSRC (server stream)

            // FCI starts at offset 12 (28 bytes total).
            // W1, W2: unknown semantics — we use template values from the Xbox
            //         capture so the server sees something it recognizes.
            //         Audio capture: W1 ≈ 0x044F5200, W2 = 0x03E80000
            //         Video capture: W1 ≈ 0x17BD922B, W2 = 0x17700000
            if (isAudio) {
                WriteU32Be(pkt, 12, 0x044F5200u);
                WriteU32Be(pkt, 16, 0x03E80000u);
            } else {
                WriteU32Be(pkt, 12, 0x17BD922Bu);
                WriteU32Be(pkt, 16, 0x17700000u);
            }

            // W3: high-16 = buffer fill in ms; low-16 = 0xFFFF.
            // Xbox captures show this ramps 0 → TD (2000 ms) over the
            // first few seconds, then holds at TD for steady-state. We
            // replicate that pattern via a session-local stopwatch — the
            // server appears to interpret a persistent W3=0 as "client
            // permanently starving" and stops feeding after pre-roll.
            // Holding at TD says "I have a healthy buffer, keep streaming
            // at media rate". Stopwatch is started on first BFR-eligible
            // RTCP tick (StartRtcpReceiverReports).
            ushort bufferFillMs = ComputeBfrW3Fill(isAudio);
            pkt[20] = (byte)((bufferFillMs >> 8) & 0xFF);
            pkt[21] = (byte)(bufferFillMs & 0xFF);
            pkt[22] = 0xFF;
            pkt[23] = 0xFF;

            // W4: high-16 = next expected seq (highest_received + 1); low-16 = 0.
            ushort nextSeq = (ushort)(highestSeq + 1);
            pkt[24] = (byte)((nextSeq >> 8) & 0xFF);
            pkt[25] = (byte)(nextSeq & 0xFF);
            pkt[26] = 0; pkt[27] = 0;

            // W5, W6: zero (already zero from new byte[])
            // pkt[28..35] = 0

            // W7: 0xFFFF + TD in ms (we sent TD=2000 in our SETUP Buffer-Info).
            pkt[36] = 0xFF; pkt[37] = 0xFF;
            pkt[38] = (byte)((BfrTdMs >> 8) & 0xFF);
            pkt[39] = (byte)(BfrTdMs & 0xFF);

            return pkt;
        }

        /// <summary>
        /// Compute the W3 buffer-fill value (ms) for the BFR FCI.
        ///
        /// <para>With a wired occupancy provider (audio AND video): report the
        /// REAL current buffer level for that stream. The Xbox-360 capture shows
        /// the genuine client reports its true, dynamically-changing buffer here
        /// (it dips whenever the server under-delivers); that dip is the signal
        /// WMPNss uses to speed up and refill. A flat ramp-then-peg-at-TD never
        /// shows a drain, so the server settles a few % under real-time and the
        /// buffer slowly starves — observed on AUDIO (~94%, fixed first) and then
        /// on VIDEO (~97%, drained the jitter buffer over ~3 min until it froze).
        /// Honest per-stream reporting closes both loops.</para>
        ///
        /// <para>Fallback (no provider wired): the synthetic ramp 0 → TD over
        /// <see cref="BfrRampDurationMs"/> then hold at TD.</para>
        /// </summary>
        private ushort ComputeBfrW3Fill(bool isAudio) {
            var provider = isAudio ? _audioBufferMsProvider : _videoBufferMsProvider;
            if (provider != null) {
                int ms;
                try { ms = provider(); } catch { ms = 0; }
                if (ms < 0) ms = 0;
                if (ms > 65535) ms = 65535;
                return (ushort)ms;
            }
            if (!_bfrRampStopwatch.IsRunning) return 0;
            long el = _bfrRampStopwatch.ElapsedMilliseconds;
            if (el <= 0) return 0;
            if (el >= BfrRampDurationMs) return BfrTdMs;
            // Linear ramp 0 → BfrTdMs over BfrRampDurationMs.
            return (ushort)((el * BfrTdMs) / BfrRampDurationMs);
        }

        /// <summary>
        /// Parse the MS-RTSP <c>rtcp-fb-ssrc=XXXXXXXX</c> parameter from a
        /// Transport response header. This is the SSRC the server expects
        /// us to put in the "SSRC of packet sender" field of outbound RTCP
        /// packets so it can correlate feedback to the right session.
        /// Returns 0 if the parameter isn't present (in which case the
        /// caller should fall back to a locally-chosen SSRC).
        /// </summary>
        private static uint ParseRtcpFbSsrcFromTransport(string transportHeader) {
            if (string.IsNullOrEmpty(transportHeader)) return 0;
            foreach (string part in transportHeader.Split(';')) {
                string p = part.Trim();
                if (p.StartsWith("rtcp-fb-ssrc=", StringComparison.OrdinalIgnoreCase)) {
                    string v = p.Substring("rtcp-fb-ssrc=".Length).Trim();
                    if (uint.TryParse(v, System.Globalization.NumberStyles.HexNumber,
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      out uint result))
                        return result;
                }
            }
            return 0;
        }

        /// <summary>
        /// Update per-stream RTP sequence-number tracking used by RTCP RR
        /// reception-report blocks. Tracks base sequence (first seen),
        /// highest sequence seen, and 16-bit-cycles count for the extended
        /// highest sequence number. Wrap detection per RFC 3550 §A.1.
        /// </summary>
        private void UpdateRtcpSeqTracking(bool isAudio, ushort seq) {
            if (isAudio) {
                if (!_audioSeqInit) {
                    _audioBaseSeq = seq;
                    _audioHighestSeq = seq;
                    _audioSeqInit = true;
                } else if (IsSeqGreater(seq, _audioHighestSeq)) {
                    // Detect 16-bit wrap (highest near 0xFFFF, new near 0).
                    if (seq < _audioHighestSeq) _audioSeqCycles++;
                    _audioHighestSeq = seq;
                }
                System.Threading.Interlocked.Increment(ref _audioPktsReceived);
            } else {
                if (!_videoSeqInit) {
                    _videoBaseSeq = seq;
                    _videoHighestSeq = seq;
                    _videoSeqInit = true;
                } else if (IsSeqGreater(seq, _videoHighestSeq)) {
                    if (seq < _videoHighestSeq) _videoSeqCycles++;
                    _videoHighestSeq = seq;
                }
                System.Threading.Interlocked.Increment(ref _videoPktsReceived);
            }
        }

        #endregion ############################################################


        /// <summary>
        /// Parse the <c>ssrc=XXXXXXXX</c> parameter from a Transport header
        /// value. Returns 0 if not present (RtspTransport doesn't expose it).
        /// </summary>
        private static uint ParseSsrcFromTransport(string transportHeader) {
            if (string.IsNullOrEmpty(transportHeader)) return 0;
            // Find "ssrc=" — must be at a token boundary (after ; or whitespace)
            // and not "rtcp-fb-ssrc=" which has the same suffix.
            foreach (string part in transportHeader.Split(';')) {
                string p = part.Trim();
                if (p.StartsWith("ssrc=", StringComparison.OrdinalIgnoreCase)) {
                    string v = p.Substring(5).Trim();
                    if (uint.TryParse(v, System.Globalization.NumberStyles.HexNumber,
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      out uint result))
                        return result;
                }
            }
            return 0;
        }

        /// <summary>
        /// Parse the PLAY-response <c>RTP-Info</c> header and anchor the
        /// PS muxer's per-stream PTS bases. Format (RFC 2326 §12.33):
        /// <code>
        /// RTP-Info: url=...audio;seq=N;rtptime=T1, url=...video;seq=M;rtptime=T2
        /// </code>
        /// The url may be absolute (<c>rtsp://host/path/audio</c>) OR
        /// relative (<c>audio</c> — what WMPNss actually sends). We
        /// identify the stream by inspecting the LAST path segment of the
        /// url ("audio" or "video"), which works for both forms. Missing
        /// or unparseable entries are silently skipped; the muxer will
        /// then auto-anchor on first MAU — which is exactly the bug we
        /// hit before this fix landed.
        /// </summary>
        private void ParseRtpInfoAndAnchorMuxer(string rtpInfo) {
            foreach (string entry in rtpInfo.Split(',')) {
                string trimmed = entry.Trim();
                bool isAudio = false, isVideo = false;
                uint rtptime = 0;
                bool haveRtptime = false;
                foreach (string param in trimmed.Split(';')) {
                    string p = param.Trim();
                    if (p.StartsWith("url=", StringComparison.OrdinalIgnoreCase)) {
                        string url = p.Substring(4).Trim();
                        // Last path segment — works for both "audio" and
                        // "rtsp://host/path/audio".
                        int lastSlash = url.LastIndexOf('/');
                        string tail = lastSlash >= 0 ? url.Substring(lastSlash + 1) : url;
                        if (tail.Equals("audio", StringComparison.OrdinalIgnoreCase)) isAudio = true;
                        else if (tail.Equals("video", StringComparison.OrdinalIgnoreCase)) isVideo = true;
                    } else if (p.StartsWith("rtptime=", StringComparison.OrdinalIgnoreCase)) {
                        if (uint.TryParse(p.Substring("rtptime=".Length).Trim(),
                                          System.Globalization.NumberStyles.Integer,
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          out uint t)) {
                            rtptime = t;
                            haveRtptime = true;
                        }
                    }
                }
                if (!haveRtptime) continue;
                if (isAudio) {
                    // Store the audio play-point RTP timestamp for the
                    // cross-stream offset (see TryGetRtpInfoAvOffsetMs).
                    System.Threading.Interlocked.Exchange(ref _audioRtpInfoRtptime, rtptime);
                    RtcpDiagLog($"rtp-info AUDIO rtptime={rtptime}");
                } else if (isVideo) {
                    // Store the video play-point RTP timestamp for the offset.
                    System.Threading.Interlocked.Exchange(ref _videoRtpInfoRtptime, rtptime);
                    // On a mid-session PLAY (seek / trick play) the
                    // depacketizer's sequence tracker would otherwise see
                    // the new server stream as a long stretch of packet
                    // loss and gate-keep MAUs until the next IDR. Clear it
                    // so the first post-PLAY frame is accepted. Harmless on
                    // the first PLAY (nothing to reset).
                    try { videoDepacketizer?.ResetPostLossState(); } catch { }
                    RtcpDiagLog($"rtp-info VIDEO rtptime={rtptime}");
                } else {
                    RtcpDiagLog($"rtp-info UNKNOWN-URL entry='{trimmed}'");
                }
            }
            // New RTP-Info parsed (initial or post-seek) → bump generation so
            // the controller knows the post-seek play-point timestamps are now
            // available and it can safely re-anchor.
            System.Threading.Interlocked.Increment(ref _rtpInfoGeneration);
        }


        /// <summary>
        /// Record the codec observed on the first wire packet of this
        /// direction. Defers actual pipeline setup until BOTH directions
        /// commit (or 300ms timeout) so we can correctly choose between
        /// PS-pipeline (MPA+MPV) and dual-FFME (anything else) without
        /// racing two parallel FFME Open commands.
        /// </summary>
        private void CommitAudioPipelineForWireCodec(string codec) {
            if (codec == null) return;
            lock (_commitLock) {
                if (_wireAudioCodec != null) return;
                _wireAudioCodec = codec;
                // Capture the RTP timestamp clock for this stream so the
                // RTCP-SR StreamClock can convert RTP timestamps to PTS.
                _audioClockHz = ClockHzForWireCodec(MediaType.Audio, codec);
                Debug.WriteLine($"[wire-commit] audio codec={codec} clockHz={_audioClockHz}");
                CommitDiagLog($"COMMIT audio={codec} clockHz={_audioClockHz} (videoSoFar={_wireVideoCodec ?? "(none)"})");
                TryFinalizePipelineSetup();
            }
        }

        private void CommitVideoPipelineForWireCodec(string codec) {
            if (codec == null) return;
            lock (_commitLock) {
                if (_wireVideoCodec != null) return;
                _wireVideoCodec = codec;
                _videoClockHz = ClockHzForWireCodec(MediaType.Video, codec);
                Debug.WriteLine($"[wire-commit] video codec={codec} clockHz={_videoClockHz}");
                CommitDiagLog($"COMMIT video={codec} clockHz={_videoClockHz} (audioSoFar={_wireAudioCodec ?? "(none)"})");
                TryFinalizePipelineSetup();
            }
        }

        /// <summary>
        /// Resolve the RTP timestamp clock rate (Hz) for the committed wire
        /// codec from the SDP rtpmap data cached in
        /// <see cref="wmfPayloadDataDict"/>. Used to give the RTCP-SR
        /// <see cref="StreamClock"/> the right divisor for RTP→PTS
        /// conversion (e.g. 90000 for vnd.ms.wm-MPA/MPV, 1000 for x-wmf-pf).
        /// Returns 0 if no matching SDP entry is found.
        /// </summary>
        private uint ClockHzForWireCodec(MediaType type, string codec) {
            // Return 0 if Codec not provided
            if (string.IsNullOrEmpty(codec)) return 0;
            // Iterate over WMFPayloadData Dict
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null || entry.Type != type) continue;
                if (string.IsNullOrEmpty(entry.Codec)) continue;
                if (!entry.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.ClockHz > 0) return (uint)entry.ClockHz;
            }
            return 0;
        }


        // Lock-held caller. Either commits NOW if both codecs known, or
        // starts a 300ms safety-net timer that commits with whatever
        // codecs have been observed by then. (Audio-only sessions never
        // see a video packet and would otherwise hang forever.)
        private bool _pipelinesCommitted;
        private System.Threading.Timer _commitTimer;

        private void TryFinalizePipelineSetup() {
            if (_pipelinesCommitted) return;
            if (_wireAudioCodec != null && _wireVideoCodec != null) {
                CommitDiagLog("TryFinalize: both codecs known, finalizing now");
                FinalizePipelineSetup();
                return;
            }
            // Fast path for sessions where the SDP has only one media
            // stream (the other side will never commit). Most common:
            // an audio-only MP3 session — the SDP carries m=audio with
            // no m=video, so waiting 300 ms for a video commit just
            // delays the pipeline setup and drops the first ~7 audio
            // MAUs (the RFC 2250 MPA path doesn't buffer via the
            // depacketizer, so dropped early MAUs are gone).
            //
            // Detect "no other side coming" by scanning the SDP-derived
            // payload dict for any entry of the missing media type. If
            // none, finalize immediately.
            bool needAudio = _wireAudioCodec == null;
            bool needVideo = _wireVideoCodec == null;
            bool sdpHasMissingSide = false;
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (needAudio && entry.Type == MediaType.Audio) { sdpHasMissingSide = true; break; }
                if (needVideo && entry.Type == MediaType.Video) { sdpHasMissingSide = true; break; }
            }
            if (!sdpHasMissingSide) {
                CommitDiagLog($"TryFinalize: only have audio={_wireAudioCodec ?? "(none)"} video={_wireVideoCodec ?? "(none)"} and SDP has no entry for the missing side — finalizing now");
                FinalizePipelineSetup();
                return;
            }
            if (_commitTimer == null) {
                CommitDiagLog($"TryFinalize: only have audio={_wireAudioCodec ?? "(none)"} video={_wireVideoCodec ?? "(none)"} — arming 300ms timer");
                _commitTimer = new System.Threading.Timer(_ => {
                    lock (_commitLock) {
                        CommitDiagLog("TryFinalize: timer fired");
                        FinalizePipelineSetup();
                    }
                }, null, 300, System.Threading.Timeout.Infinite);
            }
        }

        // Lock-held caller. Sets up the appropriate pipeline(s) based on
        // whatever wire codecs have been observed. Idempotent.
        private void FinalizePipelineSetup() {
            if (_pipelinesCommitted) return;
            _pipelinesCommitted = true;
            try { _commitTimer?.Dispose(); } catch { }
            _commitTimer = null;
            Debug.WriteLine($"[wire-commit] finalize: audio={_wireAudioCodec ?? "(none)"} video={_wireVideoCodec ?? "(none)"}");
            CommitDiagLog($"FinalizePipelineSetup: audio={_wireAudioCodec ?? "(none)"} video={_wireVideoCodec ?? "(none)"}");
            // Both audio and video are owned by the external controller
            // (libav decode → NAudio / D3DImage), fed via the external
            // audio/video consumers. Nothing to set up here anymore — this
            // just commits the wire codecs and tears down the commit timer.
        }

        #region RTP Methods ###################################################

        // RTP packet (or RTCP packet) has been received.
        public void Rtp_VideoDataReceived(object sender, Rtsp.RtspChunkEventArgs e) {

            RtspData data_received = e.Message as RtspData;
            try {
                if (System.Threading.Interlocked.Exchange(ref _vidRecvLoggedOnce, 1) == 0) {
                    int pt = e.Message.Data.Length > 1 ? (e.Message.Data[1] & 0x7F) : -1;
                    CommitDiagLog($"Rtp_VideoDataReceived: FIRST chan={data_received.Channel} pt={pt} " +
                                  $"video_data_channel={video_data_channel} video_rtcp_channel={video_rtcp_channel} " +
                                  $"audio_data_channel={audio_data_channel} dictHasPt={(pt >= 0 && wmfPayloadDataDict.ContainsKey(pt))} " +
                                  $"dictCodec={(pt >= 0 && wmfPayloadDataDict.ContainsKey(pt) ? wmfPayloadDataDict[pt].Codec ?? "(null)" : "(no-entry)")}");
                }
            } catch { }

            // Check which channel the Data was received on.
            // eg the Video Channel, the Video Control Channel (RTCP)
            // the Audio Channel or the Audio Control Channel (RTCP)

            if (data_received.Channel == video_rtcp_channel || data_received.Channel == audio_rtcp_channel) {
                // Handle RTCP Data
                Rtcp_HandleData(data_received, e);
            }

            if (data_received.Channel == video_data_channel || data_received.Channel == audio_data_channel) {
                // Handle RTP Data
                Rtp_HandleData(data_received, e);
            }
        }

        // RTP packet (or RTCP packet) has been received.
        public void Rtp_AudioDataReceived(object sender, Rtsp.RtspChunkEventArgs e) {

            RtspData data_received = e.Message as RtspData;
            try {
                if (System.Threading.Interlocked.Exchange(ref _audRecvLoggedOnce, 1) == 0) {
                    int pt = e.Message.Data.Length > 1 ? (e.Message.Data[1] & 0x7F) : -1;
                    CommitDiagLog($"Rtp_AudioDataReceived: FIRST chan={data_received.Channel} pt={pt} " +
                                  $"audio_data_channel={audio_data_channel} audio_rtcp_channel={audio_rtcp_channel} " +
                                  $"video_data_channel={video_data_channel} dictHasPt={(pt >= 0 && wmfPayloadDataDict.ContainsKey(pt))} " +
                                  $"dictCodec={(pt >= 0 && wmfPayloadDataDict.ContainsKey(pt) ? wmfPayloadDataDict[pt].Codec ?? "(null)" : "(no-entry)")}");
                }
            } catch { }

            // Check which channel the Data was received on.
            // eg the Video Channel, the Video Control Channel (RTCP)
            // the Audio Channel or the Audio Control Channel (RTCP)

            if (data_received.Channel == video_rtcp_channel || data_received.Channel == audio_rtcp_channel) {
                // Handle RTCP Data
                Rtcp_HandleData(data_received, e);
            }

            if (data_received.Channel == video_data_channel || data_received.Channel == audio_data_channel) {
                // Handle RTP Data
                Rtp_HandleData(data_received, e);
            }
        }

        private void Rtcp_HandleData(RtspData data_received, Rtsp.RtspChunkEventArgs e) {
            Debug.WriteLine("Received a RTCP message on channel " + data_received.Channel);

            // RTCP Packet
            // - Version, Padding and Receiver Report Count
            // - Packet Type
            // - Length
            // - SSRC
            // - payload

            // There can be multiple RTCP packets transmitted together. Loop ever each one

            long packetIndex = 0;
            while (packetIndex < e.Message.Data.Length) {

                int rtcp_version = (e.Message.Data[packetIndex + 0] >> 6);
                int rtcp_padding = (e.Message.Data[packetIndex + 0] >> 5) & 0x01;
                int rtcp_reception_report_count = (e.Message.Data[packetIndex + 0] & 0x1F);
                byte rtcp_packet_type = e.Message.Data[packetIndex + 1]; // Values from 200 to 207
                uint rtcp_length = (uint)(e.Message.Data[packetIndex + 2] << 8) + (uint)(e.Message.Data[packetIndex + 3]); // number of 32 bit words
                uint rtcp_ssrc = (uint)(e.Message.Data[packetIndex + 4] << 24) + (uint)(e.Message.Data[packetIndex + 5] << 16)
                    + (uint)(e.Message.Data[packetIndex + 6] << 8) + (uint)(e.Message.Data[packetIndex + 7]);

                // 200 = SR = Sender Report
                // 201 = RR = Receiver Report
                // 202 = SDES = Source Description
                // 203 = Bye = Goodbye
                // 204 = APP = Application Specific Method
                // 207 = XR = Extended Reports

                System.Diagnostics.Debug.WriteLine("RTCP Data. PacketType=" + rtcp_packet_type
                                  + " SSRC=" + rtcp_ssrc);

                if (rtcp_packet_type == 200) {
                    // SR (Sender Report). Carries the per-stream NTP↔RTP
                    // anchor we need for absolute A/V timing.
                    //
                    //  off 4..7   sender SSRC
                    //  off 8..15  NTP timestamp (64-bit fixed-point)
                    //  off 16..19 RTP timestamp paired with above NTP

                    UInt32 ntp_msw_seconds =
                        (uint)(e.Message.Data[packetIndex + 8] << 24) |
                        (uint)(e.Message.Data[packetIndex + 9] << 16) |
                        (uint)(e.Message.Data[packetIndex + 10] << 8) |
                        (uint)(e.Message.Data[packetIndex + 11]);
                    UInt32 ntp_lsw_fractions =
                        (uint)(e.Message.Data[packetIndex + 12] << 24) |
                        (uint)(e.Message.Data[packetIndex + 13] << 16) |
                        (uint)(e.Message.Data[packetIndex + 14] << 8) |
                        (uint)(e.Message.Data[packetIndex + 15]);
                    UInt32 rtp_timestamp_sr =
                        (uint)(e.Message.Data[packetIndex + 16] << 24) |
                        (uint)(e.Message.Data[packetIndex + 17] << 16) |
                        (uint)(e.Message.Data[packetIndex + 18] << 8) |
                        (uint)(e.Message.Data[packetIndex + 19]);

                    ulong ntp_full = ((ulong)ntp_msw_seconds << 32) | ntp_lsw_fractions;

                    // Plant the SR-derived anchor on the matching
                    // per-stream clock (Layer 4e(iv)). The PTS monitor
                    // (Layer 4f) reads this when committing each sample
                    // to detect the MS-DMCT >200ms-behind / >2000ms-ahead
                    // thresholds.
                    ApplyRtcpSenderReport(rtcp_ssrc, ntp_full, rtp_timestamp_sr);

                    // Periodic RR is already paced by StartRtcpReceiverReports
                    // (~2 Hz timer in _rtcpTimer) for both UDP and TCP transports,
                    // so we no longer ad-hoc send an extra RR per inbound SR.
                } else if (rtcp_packet_type == 203) {
                    // BYE — RFC 3550 §6.6. Server is dropping the stream
                    // for the SSRCs listed in the packet. Log and (for
                    // a stream we depend on) surface as Disconnected
                    // so AVCTRL can emit RTSP_DISCONNECT.
                    bool dropsAudio = (rtcp_ssrc == _audioServerDataSsrc && _audioServerDataSsrc != 0);
                    bool dropsVideo = (rtcp_ssrc == _videoServerDataSsrc && _videoServerDataSsrc != 0);
                    if (dropsAudio || dropsVideo) {
                        RaiseDisconnected(new System.IO.IOException( $"RTCP BYE for {(dropsAudio ? "audio" : "video")} stream ssrc=0x{rtcp_ssrc:X8}"));
                    } else {
                        Debug.WriteLine($"[rtcp] BYE for unrelated ssrc=0x{rtcp_ssrc:X8}");
                    }
                }
                // PT 201 RR / 202 SDES / 204 APP / 207 XR — log-only.

                packetIndex = packetIndex + ((rtcp_length + 1) * 4);
            }
            return;
        }

        private void Rtp_HandleData(RtspData data_received, Rtsp.RtspChunkEventArgs e) {

            // RTP Packet Header
            // 0 - Version, P, X, CC, M, PT and Sequence Number
            //32 - Timestamp
            //64 - SSRC
            //96 - CSRCs (optional)
            //nn - Extension ID and Length
            //nn - Extension header

            int rtp_version = (e.Message.Data[0] >> 6);
            int rtp_padding = (e.Message.Data[0] >> 5) & 0x01;
            int rtp_extension = (e.Message.Data[0] >> 4) & 0x01;
            int rtp_csrc_count = (e.Message.Data[0] >> 0) & 0x0F;
            int rtp_marker = (e.Message.Data[1] >> 7) & 0x01;
            int rtp_payload_type = (e.Message.Data[1] >> 0) & 0x7F;
            uint rtp_sequence_number = ((uint)e.Message.Data[2] << 8) + (uint)(e.Message.Data[3]);
            uint rtp_timestamp = ((uint)e.Message.Data[4] << 24) + (uint)(e.Message.Data[5] << 16) + (uint)(e.Message.Data[6] << 8) + (uint)(e.Message.Data[7]);
            uint rtp_ssrc = ((uint)e.Message.Data[8] << 24) + (uint)(e.Message.Data[9] << 16) + (uint)(e.Message.Data[10] << 8) + (uint)(e.Message.Data[11]);

            int rtp_payload_start = 4 // V,P,M,SEQ
                                + 4 // time stamp
                                + 4 // ssrc
                                + (4 * rtp_csrc_count); // zero or more csrcs

            uint rtp_extension_id = 0;
            uint rtp_extension_size = 0;
            if (rtp_extension == 1) {
                rtp_extension_id = ((uint)e.Message.Data[rtp_payload_start + 0] << 8) | (uint)e.Message.Data[rtp_payload_start + 1];
                // RFC 3550 §5.3.1: extension length is 16-bit big-endian and counted in 32-bit words.
                // Previous code had operator-precedence bug: `(hi<<8) + lo*4` instead of `((hi<<8)|lo)*4`.
                rtp_extension_size = (((uint)e.Message.Data[rtp_payload_start + 2] << 8) | (uint)e.Message.Data[rtp_payload_start + 3]) * 4u;
                rtp_payload_start += 4 + (int)rtp_extension_size;  // extension header and extension payload
            }

            //Debug.WriteLine("RTP Data"
            //                   + " V=" + rtp_version
            //                   + " P=" + rtp_padding
            //                   + " X=" + rtp_extension
            //                   + " CC=" + rtp_csrc_count
            //                   + " M=" + rtp_marker
            //                   + " PT=" + rtp_payload_type
            //                   + " Seq=" + rtp_sequence_number
            //                   + " Time (MS)=" + rtp_timestamp / 90 // convert from 90kHZ clock to ms
            //                   + " SSRC=" + rtp_ssrc
            //                   + " Size=" + e.Message.Data.Length);

            //Debug.WriteLine("RTP Data"
            //                       + " PT=" + rtp_payload_type
            //                       + " Seq=" + rtp_sequence_number
            //                       + " Timestamp=" + rtp_timestamp
            //                       + " SSRC=" + rtp_ssrc);

            // RFC 3550 §5.1: when the P bit is set, the LAST byte of the packet contains the
            // padding count (including the count byte itself), and those bytes are NOT payload.
            // Trim before handing to the depacketizer; otherwise stray bytes leak past BF1.
            int rtp_payload_end = e.Message.Data.Length;
            if (rtp_padding == 1) {
                int padCount = e.Message.Data[rtp_payload_end - 1];
                if (padCount > 0 && rtp_payload_end - padCount >= rtp_payload_start)
                    rtp_payload_end -= padCount;
            }
            int rtp_payload_len = rtp_payload_end - rtp_payload_start;

            // Handle Video with X-WMF-PF Payload
            if (data_received.Channel == video_data_channel && wmfPayloadDataDict[rtp_payload_type].Codec.Equals("X-WMF-PF")) {
                CommitVideoPipelineForWireCodec("X-WMF-PF");
                byte[] rtp_payload = new byte[rtp_payload_len];
                Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                UpdateRtcpSeqTracking(isAudio: false, (ushort)rtp_sequence_number);
                // Marker bit propagated for cross-checking F-field fragmentation completion
                // (WMRTP spec line 643).
                videoDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc, (ushort)rtp_sequence_number, rtp_timestamp, rtp_marker == 1);
                return;
            }

            // Handle Audio with X-WMF-PF Payload
            if (data_received.Channel == audio_data_channel && wmfPayloadDataDict[rtp_payload_type].Codec.Equals("X-WMF-PF")) {
                CommitAudioPipelineForWireCodec("X-WMF-PF");
                byte[] rtp_payload = new byte[rtp_payload_len];
                Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                audioDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc, (ushort)rtp_sequence_number, rtp_timestamp, rtp_marker == 1);
                return;
            }

            // Handle Audio with VND.MS.WM-MPA Payload (WMRTP-wrapped MPEG
            // audio — MPEG-1/2 Layer I/II/III, used by WMC recorded-TV
            // MPEG-ES profiles like WMDRMND_MPEG_ES_NTSC_XAC3). The WMRTP
            // wrapper is parsed by audioDepacketizer; the resulting MAU is
            // a clean run of MPEG audio frame bytes (no RFC 2250 sub-
            // header). The AudioDataReady handler picks the routing:
            // PS muxer (if combined pipeline is up) > NAudio MP3 sink.
            // Dispatch is therefore gated purely on the SDP codec match —
            // NOT on the sink fields — so the PS pipeline gets to see
            // audio MAUs even though _mpaAudioSink is intentionally null
            // there.
            if (data_received.Channel == audio_data_channel && wmfPayloadDataDict.ContainsKey(rtp_payload_type) && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec, "VND.MS.WM-MPA", StringComparison.OrdinalIgnoreCase)) {
                CommitAudioPipelineForWireCodec("VND.MS.WM-MPA");
                byte[] rtp_payload = new byte[rtp_payload_len];
                Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                audioDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc, (ushort)rtp_sequence_number, rtp_timestamp, rtp_marker == 1);
                return;
            }

            // Handle Video with VND.MS.WM-MPV Payload (WMRTP-wrapped MPEG
            // video — MPEG-1/2 ES, same recorded-TV profile). The
            // resulting MAU is one MPEG video access unit (start-code
            // prefixed). videoDepacketizer.NalUnitReady is hooked to push
            // bytes into the MPEG-ES producer when one is set up.
            if (data_received.Channel == video_data_channel && wmfPayloadDataDict.ContainsKey(rtp_payload_type) && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec, "VND.MS.WM-MPV", StringComparison.OrdinalIgnoreCase)) {
                CommitVideoPipelineForWireCodec("VND.MS.WM-MPV");
                byte[] rtp_payload = new byte[rtp_payload_len];
                Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                UpdateRtcpSeqTracking(isAudio: false, (ushort)rtp_sequence_number);
                videoDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc, (ushort)rtp_sequence_number, rtp_timestamp, rtp_marker == 1);
                return;
            }

            // Handle Audio with MPA (MPEG Audio over RTP, RFC 2250) Payload.
            // WMPNss negotiates this for plain .mp3 source files — no
            // WMRTP wrapper, 4-byte MBZ/Frag sub-header per packet.
            // Strip the sub-header and push to whichever pipeline is
            // active: FFME (preferred) or NAudio sink (legacy fallback).
            if (data_received.Channel == audio_data_channel && wmfPayloadDataDict.ContainsKey(rtp_payload_type) && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec, "MPA", StringComparison.OrdinalIgnoreCase)) {
                // Commit the wire codec on FIRST packet — without
                // this, TrySetupMp3FfmePipeline never runs, the
                // producer stays null, and every MAU below
                // silently drops. The matching X-WMF-PF /
                // VND.MS.WM-MPA branches all call this; the
                // original RFC 2250 branch was missing it (audio-
                // only MP3 sessions produced no audio as a result).
                CommitAudioPipelineForWireCodec("MPA");
                UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                if (rtp_payload_len <= 4) return;
                // RFC 2250 §3.5: skip [16 bits MBZ][16 bits Frag_offset].
                int frameLen = rtp_payload_len - 4;
                byte[] frame = new byte[frameLen];
                Array.Copy(e.Message.Data, rtp_payload_start + 4, frame, 0, frameLen);

                // External-audio consumer (Phase 1 FFME-bypass).
                // When ExternalSyncMediaController is the active
                // controller it owns audio decoding + rendering
                // directly via libav + NAudio. We hand it the
                // raw MP3 frame; the muxer / FFME producers
                // below are skipped.
                if (_externalAudioMauArrived != null) {
                    if (!_externalAudioCodecFired) {
                        _externalAudioCodecFired = true;
                        try { _externalAudioCodecCommit?.Invoke(BuildExternalAudioFormat("MPA")); } catch (Exception ex) {
                            Debug.WriteLine($"[ext-audio] codecCommit threw: {ex.Message}");
                        }
                    }
                    try { _externalAudioMauArrived(frame, rtp_timestamp); } catch (Exception ex) {
                        Debug.WriteLine($"[ext-audio] mauArrived threw: {ex.Message}");
                    }
                    return;
                }

                // No external audio consumer attached — drop the frame.
                // Audio is always owned by the external controller, which
                // wires the consumer before PLAY.
                return;
            }

            // Handle Audio with WMA (Windows Media Audio). Same WMRTP payload
            // framing (BF1/BF2/BF3) as the other WM codecs (X-WMF-PF,
            // VND.MS.WM-MPA) — the audio depacketizer unwraps it to a WMA
            // data-unit MAU, which the external controller decodes via libav
            // (AV_CODEC_ID_WMAV2 + the SDP fmtp config= as extradata). The
            // AudioDataReady handler fires the codec commit using _wireAudioCodec
            // (= "WMA", set by CommitAudioPipelineForWireCodec just below), so it
            // builds the correct WMA ExternalAudioFormat. DIAGNOSTIC: dump the
            // first payload's bytes so we can verify this WMRTP-framing
            // assumption against a real capture if decode fails (if it's NOT
            // WMRTP-framed, the first byte won't look like a BF1 and the WMA
            // decoder will error — the dump tells us the real framing).
            if (data_received.Channel == audio_data_channel && wmfPayloadDataDict.ContainsKey(rtp_payload_type) && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec, "WMA", StringComparison.OrdinalIgnoreCase)) {
                CommitAudioPipelineForWireCodec("WMA");
                byte[] rtp_payload = new byte[rtp_payload_len];
                Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                if (!_wmaFirstPayloadLogged) {
                    _wmaFirstPayloadLogged = true;
                    RtcpDiagLog($"wma-first-payload len={rtp_payload_len} marker={rtp_marker} " +
                                $"firstBytes={BitConverter.ToString(rtp_payload, 0, Math.Min(24, rtp_payload_len))}");
                }
                UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                audioDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc, (ushort)rtp_sequence_number, rtp_timestamp, rtp_marker == 1);
                return;
            }

            // If we get here, there is no Parser for this Payload Type
            System.Diagnostics.Debug.WriteLine("No parser for RTP payload " + rtp_payload_type);
        }

        #endregion ############################################################


        #region RTSP Methods ##################################################

        // RTSP Messages are OPTIONS, DESCRIBE, SETUP, PLAY, ANNOUNCE, etc
        /// <summary>
        /// Handle a server-initiated RTSP request (server→client). RTSP allows
        /// either peer to send requests; WMPNss/McxDMS pushes session events
        /// this way. There are two end-of-stream conventions, both observed
        /// from WMPNss servers and BOTH documented in the specs shipped under
        /// Tools/Documents:
        ///
        ///   (a) DLNA variant — an <c>ANNOUNCE</c> with the
        ///       <c>Event-Type.dlna.org</c> header. [MS-DLNHND] §4.2 shows the
        ///       exact message: after the DMS sends the last RTP packet it
        ///       ANNOUNCEs with <c>Event-Type.dlna.org: 2000</c> (= end of
        ///       stream). The full numeric enumeration is owned by the external
        ///       [DLNA] Networked Device Interoperability Guidelines (guideline
        ///       7.4.261); 2000 is the only value Microsoft documents/sends.
        ///
        ///   (b) Native Windows Media variant — a server→client
        ///       <c>SET_PARAMETER</c> with the <c>X-Notice</c> header
        ///       ([MS-RTSP] §2.2.7.3 "EndOfStream"): <c>X-Notice: 2101
        ///       "End-of-Stream Reached"</c>, Content-Type
        ///       <c>application/x-wms-extension-cmd</c>, and a self-describing
        ///       US-ASCII body — <c>EOF: true</c> plus optionally one of
        ///       <c>AdministrativeDisconnection</c> / <c>End-Of-Playlist-Entry</c>
        ///       / <c>RecedingEos</c>.
        ///
        /// Every server request MUST be acknowledged with a 200 OK echoing the
        /// CSeq (+ Session) or the server stalls / retransmits. We then log the
        /// event verbosely (the body is human-readable text — see HandleAnnounce)
        /// and, for either EOS form, raise <see cref="EndOfStream"/>.
        /// </summary>
        private void HandleServerRequest(RtspRequest request) {
            string method = request.Method ?? "(none)";
            int cseq = request.CSeq;
            Debug.WriteLine($"[rtsp] server request: {method} CSeq={cseq}");
            CommitDiagLog($"SERVER-REQ {method} CSeq={cseq}");

            // Acknowledge with 200 OK (CreateResponse copies CSeq + Session).
            // Send it before acting on the event so the server isn't kept
            // waiting on the round-trip.
            try {
                RtspResponse ack = request.CreateResponse();
                rtsp_client.SendMessage(ack);
            } catch (Exception ex) {
                Debug.WriteLine($"[rtsp] failed to ACK server request {method}: {ex.Message}");
                CommitDiagLog($"SERVER-REQ {method} ACK FAILED: {ex.Message}");
            }

            // ANNOUNCE (DLNA) and server-initiated SET_PARAMETER (native MS-RTSP
            // EndOfStream) both carry events; route both through the same parser.
            if (string.Equals(method, "ANNOUNCE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(method, "SET_PARAMETER", StringComparison.OrdinalIgnoreCase)) {
                HandleAnnounce(request);
            }
        }

        /// <summary>
        /// Interpret + log a server event request (ANNOUNCE or SET_PARAMETER).
        /// Surfaces end-of-stream (either the DLNA <c>Event-Type.dlna.org: 2000</c>
        /// header or the MS-RTSP <c>X-Notice: 2101</c> / <c>EOF: true</c> body)
        /// as <see cref="EndOfStream"/> → controller MediaEnded → MS-DMCT
        /// END_OF_MEDIA. The event body is US-ASCII and self-describing, so it is
        /// logged verbatim as TEXT (this is how unknown event types are
        /// identified). We do NOT tear down locally — WMC drives
        /// teardown / playlist-advance off END_OF_MEDIA.
        /// </summary>
        private void HandleAnnounce(RtspRequest request) {
            string method     = request.Method ?? "(none)";
            string eventType  = request.Headers.TryGetValue("Event-Type.dlna.org", out string et) ? et?.Trim() : null;
            string xNotice    = request.Headers.TryGetValue("X-Notice", out string xn) ? xn?.Trim() : null;
            string rtpInfo    = request.Headers.TryGetValue("RTP-Info", out string ri) ? ri : "(none)";
            string contentType= request.Headers.TryGetValue("Content-Type", out string ct) ? ct : "(none)";

            // The x-wms-extension-cmd body is US-ASCII text per [MS-RTSP]
            // §2.2.7.3 (e.g. "Session: …\r\nEOF: true\r\nEnd-Of-Playlist-Entry: true").
            // Decode + log it so any event can be identified from the log.
            int bodyLen = request.Data?.Length ?? 0;
            string bodyText = bodyLen > 0
                ? System.Text.Encoding.ASCII.GetString(request.Data).Replace("\r", "\\r").Replace("\n", "\\n")
                : "";

            string summary = $"{method} event-type={eventType ?? "(none)"} x-notice={xNotice ?? "(none)"} " +
                             $"content-type={contentType} rtp-info=[{rtpInfo}] bodyLen={bodyLen}";
            Debug.WriteLine($"[rtsp] {summary}");
            CommitDiagLog(summary);
            if (bodyLen > 0) CommitDiagLog($"{method} body(text): \"{bodyText}\"");

            // End-of-stream detection across both conventions:
            //   DLNA:     Event-Type.dlna.org: 2000
            //   MS-RTSP:  X-Notice: 2101 ...   OR body contains "EOF: true"
            bool dlnaEos    = eventType == "2000";
            bool noticeEos  = xNotice != null && xNotice.StartsWith("2101");
            bool bodyEos    = bodyLen > 0 &&
                              System.Text.Encoding.ASCII.GetString(request.Data)
                                  .IndexOf("EOF: true", StringComparison.OrdinalIgnoreCase) >= 0;

            if (dlnaEos || noticeEos || bodyEos) {
                string why = dlnaEos ? "DLNA Event-Type 2000"
                           : noticeEos ? "MS-RTSP X-Notice 2101"
                           : "body EOF: true";
                Debug.WriteLine($"[rtsp] {method} end-of-stream ({why}) → raising EndOfStream");
                CommitDiagLog($"{method} → EndOfStream ({why})");
                RaiseEndOfStream();
            }
        }

        private void Rtsp_MessageReceived(object sender, Rtsp.RtspChunkEventArgs e) {
            // RTSP lets the SERVER push requests to the client, not just
            // respond. WMPNss/McxDMS uses ANNOUNCE for that — most importantly
            // to push the DLNA end-of-stream event. Server-initiated requests
            // arrive here as RtspRequest (RtspRequestAnnounce etc.), NOT
            // RtspResponse, so the "as RtspResponse" cast below would yield
            // null and NRE on message.IsOk. Handle (and acknowledge) them
            // first, then return.
            if (e.Message is RtspRequest serverRequest) {
                HandleServerRequest(serverRequest);
                return;
            }

            RtspResponse message = e.Message as RtspResponse;
            if (message == null) {
                System.Diagnostics.Debug.WriteLine(
                    $"[rtsp] ignoring unrecognised message: {e.Message?.GetType().Name ?? "(null)"}");
                return;
            }

            if (message.IsOk == false) {
                System.Diagnostics.Debug.WriteLine("Got Error in RTSP Reply " + message.ReturnCode + " " + message.ReturnMessage);
                RtspMessage resend_message = message.OriginalRequest.Clone() as RtspMessage;
                rtsp_client.SendMessage(resend_message);
                return;
            }


            // If we get a reply to OPTIONS then start the Keepalive Timer and send DESCRIBE
            if (message.OriginalRequest != null && message.OriginalRequest is RtspRequestOptions) {

                // Check the capabilities returned by OPTIONS
                // The Public: header contains the list of commands the RTSP server supports
                // Eg   DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE, OPTIONS, ANNOUNCE, RECORD, GET_PARAMETER]}
                if (message.Headers.ContainsKey(RtspHeaderNames.Public)) {
                    string[] parts = message.Headers[RtspHeaderNames.Public].Split(',');
                    foreach (String part in parts) {
                        if (part.Trim().ToUpper().Equals("GET_PARAMETER")) server_supports_get_parameter = true;
                        if (part.Trim().ToUpper().Equals("SET_PARAMETER")) server_supports_set_parameter = true;
                    }
                }

                if (keepalive_timer == null) {
                    // Start a Timer to send an Keepalive RTSP command every 20 seconds
                    keepalive_timer = new System.Timers.Timer();
                    keepalive_timer.Elapsed += KeepaliveTimer_Elapsed;
                    keepalive_timer.Interval = 20 * 1000;
                    keepalive_timer.Enabled = true;

                    // Send DESCRIBE
                    RtspRequest describe_message = new RtspRequestDescribe();
                    describe_message.RtspUri = new Uri(url);
                    describe_message.AddHeader(AcceptHeader);
                    describe_message.AddHeader(LanguageHeader);
                    describe_message.AddHeader(SupportedHeader);
                    describe_message.AddHeader(UserAgent);
                    rtsp_client.SendMessage(describe_message);
                } else {
                    // If the Keepalive Timer was not null, the OPTIONS reply may have come from a Keepalive
                    // So no need to generate a DESCRIBE message
                    // do nothing
                }
            }


            // If we get a reply to DESCRIBE (which was our second command), then prosess SDP and send the SETUP
            if (message.OriginalRequest != null && message.OriginalRequest is RtspRequestDescribe) {

                // Got a reply for DESCRIBE
                if (message.IsOk == false) {
                    Debug.WriteLine("Got Error in DESCRIBE Reply " + message.ReturnCode + " " + message.ReturnMessage);
                    return;
                }

                // If Keepalive Timer hasn't been configured
                if (keepalive_timer == null) {
                    // Start a Timer to send an Keepalive RTSP command every 20 seconds
                    keepalive_timer = new System.Timers.Timer();
                    keepalive_timer.Elapsed += KeepaliveTimer_Elapsed;
                    keepalive_timer.Interval = 20 * 1000;
                    keepalive_timer.Enabled = true;
                }

                // Get the SDP Data
                Rtsp.Sdp.SdpFile sdp_data;
                String control = "";  // the "track" or "stream id"
                using (StreamReader sdp_stream = new StreamReader(new MemoryStream(message.Data))) {
                    sdp_data = Rtsp.Sdp.SdpFile.Read(sdp_stream);

                    // Find the base RTSP Server URL
                    foreach (Rtsp.Sdp.Attribute attrib in sdp_data.Attributs) {
                        // If this is the Control attribute
                        if (attrib.Key.Equals("control")) {
                            string sdp_control = attrib.Value;
                            if (sdp_control.ToLower().StartsWith("rtsp://")) {
                                control = sdp_control; //absolute path
                            } else {
                                control = url + "/" + sdp_control; // relative path
                            }
                        }
                    }
                }

                // RTP and RTCP 'channels' are used in TCP Interleaved mode (RTP over RTSP)
                // These are the channels we request. The camera confirms the channel in the SETUP Reply.
                int next_free_rtp_channel = 0;
                int next_free_rtcp_channel = 1;

                // Process each 'Media' Attribute in the SDP (each sub-stream)

                for (int x = 0; x < sdp_data.Medias.Count; x++) {

                    using (StreamReader sdp_stream = new StreamReader(new MemoryStream(message.Data))) {
                        sdp_data = Rtsp.Sdp.SdpFile.Read(sdp_stream);

                        // Find the base RTSP Server URL
                        foreach (Rtsp.Sdp.Attribute attrib in sdp_data.Attributs) {
                            // If this is the Control attribute
                            if (attrib.Key.Equals("control")) {
                                string sdp_control = attrib.Value;
                                if (sdp_control.ToLower().StartsWith("rtsp://")) {
                                    control = sdp_control; //absolute path
                                } else {
                                    control = url + "/" + sdp_control; // relative path
                                }
                            }
                        }
                    }

                    bool audio = (sdp_data.Medias[x].MediaType == Rtsp.Sdp.Media.MediaTypes.audio);
                    bool video = (sdp_data.Medias[x].MediaType == Rtsp.Sdp.Media.MediaTypes.video);

                    if (video && video_payload != -1) continue; // have already matched video payload, don't match another
                    if (audio && audio_payload != -1) continue; // have already matched audio payload, don't match another

                    if (audio && (client_wants_audio == false)) continue; // client does not want audio from the RTSP server
                    if (video && (client_wants_video == false)) continue; // client does not want video from the RTSP server

                    if (video) video_uri = new Uri(control);
                    if (audio) audio_uri = new Uri(control);

                    if (audio || video) {

                        // search the attributes for control, rtpmap and fmtp
                        // (fmtp only applies to video)
                        Rtsp.Sdp.AttributeFmtp fmtp = null;
                        foreach (Rtsp.Sdp.Attribute attrib in sdp_data.Medias[x].Attributs) {
                            if (attrib.Key.Equals("control")) {
                                String sdp_control = attrib.Value;
                                if (sdp_control.ToLower().StartsWith("rtsp://")) {
                                    control = sdp_control; //absolute path
                                } else {
                                    // If the control URL starts with a Slash
                                    if (control.EndsWith("/")) {
                                        // Don't add a Slash
                                        control = control + sdp_control; // relative path
                                    } else {
                                        // Add a Slash
                                        control = control + "/" + sdp_control; // relative path
                                    }
                                }

                            }
                            // BFR (Buffer Fullness Report) feedback detection.
                            // SDP form: `a=rtcp-fb:<pt-or-*> bfr <param>` (param often "101").
                            // The third-party SDP parser exposes generic attribs by
                            // key "rtcp-fb"; the raw value includes everything after
                            // the colon. We only need to know whether BFR is requested
                            // for this media (audio/video) — per-PT granularity isn't
                            // useful given the server picks the PT.
                            if (attrib.Key.Equals("rtcp-fb", StringComparison.OrdinalIgnoreCase)) {
                                string fbValue = attrib.Value ?? "";
                                if (fbValue.IndexOf(" bfr", StringComparison.OrdinalIgnoreCase) >= 0
                                    || fbValue.StartsWith("bfr", StringComparison.OrdinalIgnoreCase)) {
                                    if (audio) {
                                        _audioBfrEnabled = true;
                                        Debug.WriteLine($"[rtcp-bfr] audio BFR feedback requested: '{fbValue}'");
                                    } else if (video) {
                                        _videoBfrEnabled = true;
                                        Debug.WriteLine($"[rtcp-bfr] video BFR feedback requested: '{fbValue}'");
                                    }
                                }
                            }
                            if (attrib.Key.Equals("fmtp")) {
                                fmtp = attrib as Rtsp.Sdp.AttributeFmtp;
                                if (wmfPayloadDataDict.ContainsKey(fmtp.PayloadNumber)) {
                                    // Set the Format Parameter in the Payload Data Dictionary
                                    wmfPayloadDataDict[fmtp.PayloadNumber].FormatParameter = fmtp.FormatParameter;
                                } else {
                                    // Add the Payload Data Dictionary with Format Parameter
                                    wmfPayloadDataDict.Add(fmtp.PayloadNumber, new WMFPayloadData {
                                        PayloadNumber = fmtp.PayloadNumber,
                                        FormatParameter = fmtp.FormatParameter
                                    });
                                }

                                // Split the Format Parameter into Individual Segments
                                string[] fmtpFormatParameterSegments = fmtp.FormatParameter.Split(';');
                                // Iterate over all Format Parameter Segments
                                foreach (string fmtpFormatParameterSegment in fmtpFormatParameterSegments) {
                                    // Split the Format Parameter Segment into Element=Data
                                    string[] fmtpFormatParameterSegmentElementData = fmtpFormatParameterSegment.Split('=');
                                    // If this is Segment is a Config Element
                                    if (fmtpFormatParameterSegmentElementData[0] == "config") {
                                        // Two WMA config encodings exist in the wild:
                                        //   (a) legacy slash-delimited AM_MEDIA_TYPE
                                        //       ("major/.../waveformatex-hex") — parsed
                                        //       into AM_Media_Format.
                                        //   (b) plain codec-private hex blob, e.g.
                                        //       "008800000f00e55c0000" — NOT slash
                                        //       delimited; AM_Media_Format would throw
                                        //       (formatSegments[1] out of range). This
                                        //       case is handled later as libav extradata
                                        //       via BuildExternalAudioFormat/HexToBytes,
                                        //       so just skip AM_Media_Format here.
                                        string cfg = fmtpFormatParameterSegmentElementData.Length > 1
                                                     ? fmtpFormatParameterSegmentElementData[1] : "";
                                        if (cfg.Contains("/")) {
                                            try {
                                                wmfPayloadDataDict[fmtp.PayloadNumber].AM_Media_Format = new AM_Media_Format(cfg);
                                            } catch (Exception ex) {
                                                Debug.WriteLine($"[sdp] AM_Media_Format parse failed: {ex.Message}");
                                            }
                                        }
                                    }

                                }
                            }
                            if (attrib.Key.Equals("rtpmap")) {
                                Rtsp.Sdp.AttributeRtpMap rtpmap = attrib as Rtsp.Sdp.AttributeRtpMap;

                                // Check if the Codec Used (EncodingName) is one we support
                                string[] valid_video_codecs = { "H264", "H265", "VND.MS.WM-MPV", "X-WMF-PF" };
                                string[] valid_audio_codecs = { "PCMA", "PCMU", "AMR", "MPA", "MPEG4-GENERIC", "VND.MS.WM-MPA", "VND.MS.WM-AC3", "X-WMF-PF", "WMA" /* for aac */}; // Note some are "mpeg4-generic" lower case

                                int rtpClockHz = 90000;
                                if (!string.IsNullOrEmpty(rtpmap.ClockRate)
                                    && int.TryParse(rtpmap.ClockRate,
                                                    System.Globalization.NumberStyles.Integer,
                                                    System.Globalization.CultureInfo.InvariantCulture,
                                                    out int parsedHz)
                                    && parsedHz > 0) {
                                    rtpClockHz = parsedHz;
                                }

                                if (video && Array.IndexOf(valid_video_codecs, rtpmap.EncodingName.ToUpper()) >= 0) {
                                    if (wmfPayloadDataDict.ContainsKey(rtpmap.PayloadNumber)) {
                                        wmfPayloadDataDict[rtpmap.PayloadNumber].Type = MediaType.Video;
                                        wmfPayloadDataDict[rtpmap.PayloadNumber].Codec = rtpmap.EncodingName.ToUpper();
                                        wmfPayloadDataDict[rtpmap.PayloadNumber].ClockHz = rtpClockHz;
                                        //wmfPayloadDataDict[rtpmap.PayloadNumber].EncodingParameters = rtpmap.EncodingParameters;
                                    } else {
                                        wmfPayloadDataDict.Add(rtpmap.PayloadNumber, new WMFPayloadData {
                                            Type = MediaType.Video,
                                            Codec = rtpmap.EncodingName.ToUpper(),
                                            PayloadNumber = rtpmap.PayloadNumber,
                                            ClockHz = rtpClockHz,
                                            //EncodingParameters = rtpmap.EncodingParameters
                                        });
                                    }
                                    video_payload = rtpmap.PayloadNumber;
                                }
                                if (audio && Array.IndexOf(valid_audio_codecs, rtpmap.EncodingName.ToUpper()) >= 0) {
                                    if (wmfPayloadDataDict.ContainsKey(rtpmap.PayloadNumber)) {
                                        wmfPayloadDataDict[rtpmap.PayloadNumber].Type = MediaType.Audio;
                                        wmfPayloadDataDict[rtpmap.PayloadNumber].Codec = rtpmap.EncodingName.ToUpper();
                                        wmfPayloadDataDict[rtpmap.PayloadNumber].ClockHz = rtpClockHz;
                                        wmfPayloadDataDict[rtpmap.PayloadNumber].EncodingParameters = rtpmap.EncodingParameters;
                                    } else {
                                        wmfPayloadDataDict.Add(rtpmap.PayloadNumber, new WMFPayloadData {
                                            Type = MediaType.Audio,
                                            Codec = rtpmap.EncodingName.ToUpper(),
                                            PayloadNumber = rtpmap.PayloadNumber,
                                            ClockHz = rtpClockHz,
                                            EncodingParameters = rtpmap.EncodingParameters
                                        });
                                    }
                                    audio_payload = rtpmap.PayloadNumber;
                                }
                            }
                        }

                        // Send the SETUP RTSP command if we have a matching Payload Decoder
                        if (video && video_payload == -1) continue;
                        if (audio && audio_payload == -1) continue;

                        RtspTransport transport = null;

                        if (rtp_transport == RTP_TRANSPORT.TCP) {
                            // Server interleaves the RTP packets over the RTSP connection
                            // Example for TCP mode (RTP over RTSP)   Transport: RTP/AVP/TCP;interleaved=0-1
                            if (video) {
                                video_data_channel = next_free_rtp_channel;
                                video_rtcp_channel = next_free_rtcp_channel;
                            }
                            if (audio) {
                                audio_data_channel = next_free_rtp_channel;
                                audio_rtcp_channel = next_free_rtcp_channel;
                            }
                            transport = new RtspTransport() {
                                LowerTransport = RtspTransport.LowerTransportType.TCP,
                                Interleaved = new PortCouple(next_free_rtp_channel, next_free_rtcp_channel), // Eg Channel 0 for RTP video data. Channel 1 for RTCP status reports
                            };

                            next_free_rtp_channel += 2;
                            next_free_rtcp_channel += 2;
                        }

                        if (rtp_transport == RTP_TRANSPORT.UDP) {
                            int rtp_port = 0;
                            int rtcp_port = 0;
                            // Server sends the RTP packets to a Pair of UDP Ports (one for data, one for rtcp control messages)
                            // Example for UDP mode                   Transport: RTP/AVP;unicast;client_port=8000-8001
                            if (video) {
                                video_data_channel = video_udp_pair.data_port;     // Used in DataReceived event handler
                                video_rtcp_channel = video_udp_pair.control_port;  // Used in DataReceived event handler
                                rtp_port = video_udp_pair.data_port;
                                rtcp_port = video_udp_pair.control_port;
                            }
                            if (audio) {
                                audio_data_channel = audio_udp_pair.data_port;     // Used in DataReceived event handler
                                audio_rtcp_channel = audio_udp_pair.control_port;  // Used in DataReceived event handler
                                rtp_port = audio_udp_pair.data_port;
                                rtcp_port = audio_udp_pair.control_port;
                            }
                            transport = new RtspTransport() {
                                LowerTransport = RtspTransport.LowerTransportType.UDP,
                                Profile = RtspTransport.ProfileType.AVPF,
                                IsMulticast = false,
                                ClientPort = new PortCouple(rtp_port, rtcp_port), // a UDP Port for data (video or audio). a UDP Port for RTCP status reports
                            };
                        }

                        if (rtp_transport == RTP_TRANSPORT.MULTICAST) {
                            // Server sends the RTP packets to a Pair of UDP ports (one for data, one for rtcp control messages)
                            // using Multicast Address and Ports that are in the reply to the SETUP message
                            // Example for MULTICAST mode     Transport: RTP/AVP;multicast
                            if (video) {
                                video_data_channel = 0; // we get this information in the SETUP message reply
                                video_rtcp_channel = 0; // we get this information in the SETUP message reply
                            }
                            if (audio) {
                                audio_data_channel = 0; // we get this information in the SETUP message reply
                                audio_rtcp_channel = 0; // we get this information in the SETUP message reply
                            }
                            transport = new RtspTransport() {
                                LowerTransport = RtspTransport.LowerTransportType.UDP,
                                IsMulticast = true
                            };
                        }

                        // Generate SETUP messages
                        RtspRequestSetup setup_message = new RtspRequestSetup();
                        setup_message.RtspUri = new Uri(control);
                        setup_message.AddTransport(transport);
                        setup_message.AddHeader(LanguageHeader);
                        // Per-stream Buffer-Info: video gets the larger CDB
                        // (matches Xbox 360 capture — see
                        // BuildBufferInfoHeader doc). The `video` bool is
                        // set by the outer m= line loop and is in scope.
                        setup_message.AddHeader(BuildBufferInfoHeader(
                            _bufferInfoBandwidthBps, _bufferInfoOptimisedPreroll,
                            isVideo: video));
                        setup_message.AddHeader(SupportedHeader);
                        setup_message.AddHeader(UserAgent);
                        // Add SETUP message to list of messages to send
                        setup_messages.Add(setup_message);
                    }
                }

                // Send the FIRST SETUP message and remove it from the list of Setup Messages
                rtsp_client.SendMessage(setup_messages[0]);
                setup_messages.RemoveAt(0);
            }


            // If we get a reply to SETUP (which was our third command), then we
            // (i) check if the Interleaved Channel numbers have been modified by the camera (eg Panasonic cameras)
            // (ii) check if we have any more SETUP commands to send out (eg if we are doing SETUP for Video and Audio)
            // (iii) send a PLAY command if all the SETUP command have been sent
            if (message.OriginalRequest != null && message.OriginalRequest is RtspRequestSetup) {
                // Got Reply to SETUP
                if (message.IsOk == false) {
                    Debug.WriteLine("Got Error in SETUP Reply " + message.ReturnCode + " " + message.ReturnMessage);
                    return;
                }

                Debug.WriteLine("Got reply from Setup. Session is " + message.Session);

                session = message.Session; // Session value used with Play, Pause, Teardown and and additional Setups
                if (message.Timeout > 0 && message.Timeout > keepalive_timer.Interval / 1000) {
                    keepalive_timer.Interval = message.Timeout * 1000 / 2;
                }

                // Check the Transport header
                if (message.Headers.ContainsKey(RtspHeaderNames.Transport)) {

                    RtspTransport transport = RtspTransport.Parse(message.Headers[RtspHeaderNames.Transport]);

                    // Check if Transport header includes Multicast
                    if (transport.IsMulticast) {
                        string multicast_address = transport.Destination;
                        video_data_channel = transport.Port.First;
                        video_rtcp_channel = transport.Port.Second;

                        // Create the Pair of UDP Sockets in Multicast mode
                        video_udp_pair = new Rtsp.UDPSocket(multicast_address, video_data_channel, multicast_address, video_rtcp_channel);
                        video_udp_pair.DataReceived += Rtp_VideoDataReceived;
                        video_udp_pair.Start();

                        // TODO - Need to set audio_udp_pair for Multicast
                    }

                    // check if the requested Interleaved channels have been modified by the camera
                    // in the SETUP Reply (Panasonic have a camera that does this)
                    if (transport.LowerTransport == RtspTransport.LowerTransportType.TCP) {
                        if (message.OriginalRequest.RtspUri == video_uri) {
                            video_data_channel = transport.Interleaved.First;
                            video_rtcp_channel = transport.Interleaved.Second;
                        }
                        if (message.OriginalRequest.RtspUri == audio_uri) {
                            audio_data_channel = transport.Interleaved.First;
                            audio_rtcp_channel = transport.Interleaved.Second;
                        }

                    }

                    // Capture server's RTCP port from `server_port=N-N+1` in
                    // the Transport response. Used by SendRtcpReceiverReport
                    // to deliver feedback to the right endpoint. Without this
                    // WMPNss paces media at ~20% real-time.
                    //
                    // Diagnostic note (May 2026): the original code guarded with
                    // `message.OriginalRequest.RtspUri == audio_uri` (C# Uri
                    // equality), and silently never matched — leaving
                    // _audioServerRtcpPort=-1 and dropping all RTCP feedback.
                    // Replaced with raw-string path-tail matching, which is what
                    // we actually care about ("did this SETUP target /audio or
                    // /video?").
                    RtcpDiagInit();
                    string reqUriStr = message.OriginalRequest?.RtspUri?.ToString() ?? "<null>";
                    string audioUriStr = audio_uri?.ToString() ?? "<null>";
                    string videoUriStr = video_uri?.ToString() ?? "<null>";
                    string transportRaw = message.Headers.ContainsKey(RtspHeaderNames.Transport)
                                          ? message.Headers[RtspHeaderNames.Transport] : "<no-transport-hdr>";
                    RtcpDiagLog($"setup-resp lower={transport.LowerTransport} " +
                                $"serverPort={(transport.ServerPort != null ? transport.ServerPort.ToString() : "null")} " +
                                $"reqUri='{reqUriStr}' audioUri='{audioUriStr}' videoUri='{videoUriStr}' " +
                                $"eqAudio={reqUriStr == audioUriStr} eqVideo={reqUriStr == videoUriStr} " +
                                $"transport='{transportRaw}'");

                    if (transport.LowerTransport == RtspTransport.LowerTransportType.UDP &&
                        transport.ServerPort != null) {
                        // Also capture server's data-stream SSRC by string-
                        // scanning the raw Transport header — RtspTransport
                        // doesn't expose ssrc= as a parsed field. The SSRC
                        // is what we report on in the RR's reception report
                        // block (RFC 3550 §6.4.1 "SSRC_1 (source identifier)").
                        string transportHeader = message.Headers[RtspHeaderNames.Transport];
                        uint serverSsrc = ParseSsrcFromTransport(transportHeader);
                        uint rtcpFbSsrc = ParseRtcpFbSsrcFromTransport(transportHeader);

                        // Match on path tail rather than full Uri equality.
                        // Cheaper than worrying about C# Uri canonicalisation
                        // edge cases (default-port handling, case folding, etc).
                        bool isAudio = reqUriStr.EndsWith("/audio", StringComparison.OrdinalIgnoreCase);
                        bool isVideo = reqUriStr.EndsWith("/video", StringComparison.OrdinalIgnoreCase);

                        if (isAudio) {
                            _audioServerRtcpPort = transport.ServerPort.Second;
                            _audioServerDataSsrc = serverSsrc;
                            // Honour the server-advertised rtcp-fb-ssrc if it
                            // gave one; otherwise fall back to the random
                            // session-local reporter SSRC.
                            _audioRtcpSenderSsrc = rtcpFbSsrc != 0 ? rtcpFbSsrc : _audioReporterSsrc;
                            Debug.WriteLine($"[rtcp] audio: server_rtcp={_audioServerRtcpPort} " +
                                            $"server_ssrc=0x{serverSsrc:X8} " +
                                            $"rtcp-fb-ssrc=0x{rtcpFbSsrc:X8} → sender SSRC = 0x{_audioRtcpSenderSsrc:X8}");
                            RtcpDiagLog($"setup-resp ASSIGN audio server_rtcp={_audioServerRtcpPort} " +
                                        $"ssrc=0x{serverSsrc:X8} rtcp-fb-ssrc=0x{rtcpFbSsrc:X8} " +
                                        $"sender=0x{_audioRtcpSenderSsrc:X8}");
                        } else if (isVideo) {
                            _videoServerRtcpPort = transport.ServerPort.Second;
                            _videoServerDataSsrc = serverSsrc;
                            _videoRtcpSenderSsrc = rtcpFbSsrc != 0 ? rtcpFbSsrc : _videoReporterSsrc;
                            Debug.WriteLine($"[rtcp] video: server_rtcp={_videoServerRtcpPort} " +
                                            $"server_ssrc=0x{serverSsrc:X8} " +
                                            $"rtcp-fb-ssrc=0x{rtcpFbSsrc:X8} → sender SSRC = 0x{_videoRtcpSenderSsrc:X8}");
                            RtcpDiagLog($"setup-resp ASSIGN video server_rtcp={_videoServerRtcpPort} " +
                                        $"ssrc=0x{serverSsrc:X8} rtcp-fb-ssrc=0x{rtcpFbSsrc:X8} " +
                                        $"sender=0x{_videoRtcpSenderSsrc:X8}");
                        } else {
                            RtcpDiagLog($"setup-resp NO-ASSIGN reqUri ends in neither /audio nor /video: '{reqUriStr}'");
                        }
                    } else {
                        RtcpDiagLog($"setup-resp SKIP lower={transport.LowerTransport} serverPort-null={transport.ServerPort == null}");
                    }
                }


                // Check if we have another SETUP command to send, then remove it from the list
                if (setup_messages.Count > 0) {
                    // send the next SETUP message, after adding in the 'session'
                    RtspRequestSetup next_setup = setup_messages[0];
                    next_setup.Session = session;
                    next_setup.AddHeader(LanguageHeader);
                    // Per-stream Buffer-Info: video gets the larger CDB
                    // (matches Xbox 360 capture). Derive stream type
                    // from the SETUP URI's path tail (m= line control
                    // URLs end in /audio or /video — see SDP loop).
                    string nextSetupUriStr = next_setup.RtspUri?.ToString() ?? "";
                    bool nextIsVideo = nextSetupUriStr.EndsWith("/video", StringComparison.OrdinalIgnoreCase);
                    next_setup.AddHeader(BuildBufferInfoHeader(_bufferInfoBandwidthBps, _bufferInfoOptimisedPreroll, isVideo: nextIsVideo));
                    next_setup.AddHeader(SupportedHeader);
                    next_setup.AddHeader(UserAgent);
                    rtsp_client.SendMessage(next_setup);

                    setup_messages.RemoveAt(0);
                } else {
                    // Send the auto-PLAY — the single PLAY that initial
                    // playback hangs off, fired after every SETUP has
                    // its response back. Routes through SendPlayWithCachedParams
                    // so any startMs/rate that WMC requested via Start
                    // while we were still doing SETUPs gets folded into
                    // this PLAY's Range:/Scale:/Speed: headers. Avoids
                    // the multi-PLAY storm we were producing when Start
                    // fired a separate Play() before the auto-PLAY.
                    SendPlayWithCachedParams();
                }
            }

            // If we get a reply to PLAY (which was our fourth command), then we should have video being received
            if (message.OriginalRequest != null && message.OriginalRequest is RtspRequestPlay) {
                // Got Reply to PLAY
                if (message.IsOk == false) {
                    Debug.WriteLine("Got Error in PLAY Reply " + message.ReturnCode + " " + message.ReturnMessage);
                    return;
                }

                // RTP-Info gives per-stream (seq, rtptime) for the first
                // packet the server will send on each track. The rtptime
                // values are in each stream's 90 kHz clock and correspond
                // to the SAME media moment per the server — exactly what
                // the PS muxer needs to anchor its PTS bases so audio and
                // video PTS = 0 refer to the same wall instant. Without
                // this, the muxer anchors on whichever stream's first
                // MAU arrives first, producing tens of ms of A/V drift.
                try {
                    string rtpInfoHeader = null;
                    if (message.Headers.TryGetValue("RTP-Info", out string viaIndex)) rtpInfoHeader = viaIndex;
                    else if (message.Headers.TryGetValue("Rtp-Info", out string lower)) rtpInfoHeader = lower;
                    if (!string.IsNullOrEmpty(rtpInfoHeader)) {
                        // ParseRtpInfoAndAnchorMuxer now caches if _psMuxer
                        // is null and replays when the muxer is created
                        // via TrySetupMpegPsPipeline.
                        ParseRtpInfoAndAnchorMuxer(rtpInfoHeader);
                    }
                } catch (Exception ex) {
                    Debug.WriteLine($"[rtp-info] parse failed: {ex.Message}");
                }

                // Start sending RTCP Receiver Reports.
                // WMPNss paces ~20% real-time without RR feedback. We send
                // a minimal 8-byte RR every 1 second for each stream that
                // has a known server RTCP port. First send is immediate so
                // the server sees feedback before its dejitter buffer
                // exhausts.
                StartRtcpReceiverReports();

                System.Diagnostics.Debug.WriteLine("Got reply from Play  " + message.Command);
            }

        }

        #endregion ############################################################


        #region RTSP Keepalive ################################################

        void KeepaliveTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e) {
            // Send Keepalive message
            // This code uses GET_PARAMETER (unless OPTIONS report it is not supported, and then it sends OPTIONS as a keepalive)

            if (_stopRequested) return;

            try {
                if (server_supports_get_parameter) {
                    RtspRequest getparam_message = new RtspRequestGetParameter();
                    getparam_message.RtspUri = new Uri(url);
                    getparam_message.Session = session;
                    getparam_message.AddHeader(LanguageHeader);
                    getparam_message.AddHeader(SupportedHeader);
                    getparam_message.AddHeader(UserAgent);
                    rtsp_client.SendMessage(getparam_message);
                } else {
                    RtspRequest options_message = new RtspRequestOptions();
                    options_message.RtspUri = new Uri(url);
                    options_message.AddHeader(LanguageHeader);
                    options_message.AddHeader(SupportedHeader);
                    options_message.AddHeader(UserAgent);
                    rtsp_client.SendMessage(options_message);
                }
                // Reset miss counter on every successful send.
                System.Threading.Interlocked.Exchange(ref _keepaliveConsecutiveFailures, 0);
            } catch (Exception ex) {
                int fails = System.Threading.Interlocked.Increment(ref _keepaliveConsecutiveFailures);
                Debug.WriteLine($"[rtsp] keepalive send failed ({fails} consecutive): {ex.Message}");
                // After 2 consecutive keepalive misses, treat the session as disconnected
                if (fails >= 2 && !_stopRequested) {
                    RaiseDisconnected(ex);
                }
            }
        }

        #endregion ############################################################


        // ===================================================================
        //  StreamClock — per-stream RTP↔NTP anchor (Layer 4e(iv))
        //  ------------------------------------------------------------------
        //  Set on every RTCP SR (PT=200) for a known SSRC. Read by the
        //  depacketizers when committing a sample to get the absolute
        //  presentation time in milliseconds.
        // ===================================================================
        private sealed class StreamClock {
            public uint SSRC;
            public uint ClockHz;            // from rtpmap; 90000 video / variable audio
            public ulong AnchorNtp;         // RFC 3550: middle 32 bits of NTP fixed-point
                                            //   actually we store the FULL 64-bit value
            public uint AnchorRtp;          // RTP timestamp paired with AnchorNtp
            public bool HasAnchor;

            /// <summary>Convert an RTP timestamp into an absolute wall-clock
            /// presentation time in milliseconds. Returns 0 if no SR has
            /// been received yet for this stream.</summary>
            public long PtsMs(uint rtpTs) {
                if (!HasAnchor || ClockHz == 0) return 0;
                // signed delta handles RTP-timestamp wraparound (~13 h @ 90 kHz)
                int delta = unchecked((int)(rtpTs - AnchorRtp));
                long deltaMs = (long)delta * 1000L / ClockHz;
                return NtpToWallMs(AnchorNtp) + deltaMs;
            }

            /// <summary>Convert a 64-bit NTP timestamp (RFC 5905 §6) to
            /// milliseconds since the Unix epoch.</summary>
            public static long NtpToWallMs(ulong ntp) {
                // Upper 32 bits = seconds since 1900-01-01 (NTP epoch).
                // Lower 32 bits = fractional seconds (×2^32).
                const long unixToNtpSeconds = 2208988800L; // diff between 1900 and 1970 epochs
                uint secsSinceNtpEpoch = (uint)(ntp >> 32);
                uint frac = (uint)(ntp & 0xFFFFFFFFu);
                long unixSecs = (long)secsSinceNtpEpoch - unixToNtpSeconds;
                long fracMs = (long)((frac / 4294967296.0) * 1000.0);
                return unixSecs * 1000L + fracMs;
            }
        }

        /// <summary>
        /// Apply an incoming RTCP SR (PT=200) to the matching StreamClock.
        /// Called from the RTCP compound-packet parser (Layer 4e(iii)).
        /// </summary>
        internal void ApplyRtcpSenderReport(uint senderSsrc, ulong ntpTimestamp, uint rtpTimestamp) {
            if (senderSsrc == _audioServerDataSsrc && _audioServerDataSsrc != 0) {
                if (_audioClock == null) {
                    _audioClock = new StreamClock { SSRC = senderSsrc };
                }
                // Refresh the clock rate every SR: the wire codec commit
                // (first RTP data packet) may land before OR after the
                // first SR, so we can't rely on _audioClockHz being set
                // when the clock is first created. PtsMs returns 0 while
                // ClockHz is still 0, so PTS monitoring stays dormant
                // (no false errors) until the rate is known.
                if (_audioClockHz != 0) _audioClock.ClockHz = _audioClockHz;
                _audioClock.AnchorNtp = ntpTimestamp;
                _audioClock.AnchorRtp = rtpTimestamp;
                _audioClock.HasAnchor = true;
            } else if (senderSsrc == _videoServerDataSsrc && _videoServerDataSsrc != 0) {
                if (_videoClock == null) {
                    _videoClock = new StreamClock { SSRC = senderSsrc };
                }
                if (_videoClockHz != 0) _videoClock.ClockHz = _videoClockHz;
                _videoClock.AnchorNtp = ntpTimestamp;
                _videoClock.AnchorRtp = rtpTimestamp;
                _videoClock.HasAnchor = true;
            }
            // else: SR for an unknown SSRC (e.g. a stream we didn't SETUP) — ignore.
        }

        /// <summary>
        /// Monitor a freshly-committed sample's PTS for the spec thresholds.
        /// Called from inside the audio + video depacketizer callbacks
        /// The check is best-effort — if no SR has anchored
        /// the per-stream clock yet, we return without firing.
        /// </summary>
        private void MonitorPts(bool isAudio, uint rtpTs) {
            StreamClock clock = isAudio ? _audioClock : _videoClock;
            if (clock == null || !clock.HasAnchor) return;

            long ptsMs = clock.PtsMs(rtpTs);
            long prev = isAudio ? _lastAudioPtsMs : _lastVideoPtsMs;

            // First-sample-after-seek capture
            if (isAudio) {
                if (System.Threading.Interlocked.CompareExchange(
                        ref _firstAudioPtsCapturedMs, ptsMs, long.MinValue) == long.MinValue) {
                    CheckFirstSampleAvSkew();
                }
            } else {
                if (System.Threading.Interlocked.CompareExchange(
                        ref _firstVideoPtsCapturedMs, ptsMs, long.MinValue) == long.MinValue) {
                    CheckFirstSampleAvSkew();
                }
            }

            // MS-DMCT 2.2.2.1.2.4: behind by >200ms OR ahead by >2000ms vs.
            // the previous sample on this stream fires PTS_ERROR.
            if (prev > 0) {
                long delta = ptsMs - prev;
                if (delta < -200 || delta > 2000) {
                    RaisePtsError(new PtsErrorInfo(isAudio, prev, ptsMs));
                }
            }

            if (isAudio) _lastAudioPtsMs = ptsMs;
            else _lastVideoPtsMs = ptsMs;
        }

        /// <summary>
        /// Once both first-sample PTS values are captured
        /// after a seek, compare and fire UNRECOVERABLE_SKEW if the gap
        /// exceeds 3500ms. Only fires once per seek.
        /// </summary>
        private void CheckFirstSampleAvSkew() {
            long a = System.Threading.Interlocked.Read(ref _firstAudioPtsCapturedMs);
            long v = System.Threading.Interlocked.Read(ref _firstVideoPtsCapturedMs);
            if (a == long.MinValue || v == long.MinValue) return;  // still waiting
            if (System.Threading.Interlocked.Exchange(ref _avSkewFiredForThisSeek, 1) != 0) return;
            long skew = System.Math.Abs(v - a);
            if (skew > 3500) {
                RaiseUnrecoverableSkew(new SkewInfo(SkewInfo.Cause.FirstSampleAvSkew, skew));
            }
        }

        /// <summary>
        /// start the decoder-open stopwatch the first time
        /// a sample is handed to MediaController. Called from the producer-submit
        /// path of whichever pipeline commits first.
        /// </summary>
        internal void ArmDecoderOpenStopwatch() {
            if (System.Threading.Interlocked.Exchange(ref _decoderOpenArmed, 1) == 0) {
                _decoderOpenSw.Restart();
            }
        }

        #region Utility #######################################################

        // Resolve the directory for RTSP diagnostic logs: an "rtsp" subfolder
        // under the configured main log directory (SoftSledConfig.LogFileDirectory,
        // default %LocalAppData%/SoftSled/Logs) so they sit alongside the
        // session logs instead of in %TEMP%. Falls back to %TEMP% if config
        // resolution or directory creation fails, so diagnostics are never lost.
        private static string ResolveRtspDiagDir() {
            try {
                string dir = null;
                try { dir = SoftSled.Components.Configuration.SoftSledConfigManager.ReadConfig()?.LogFileDirectory; } catch { }
                if (string.IsNullOrWhiteSpace(dir)) {
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SoftSled", "Logs");
                }
                dir = System.IO.Path.Combine(dir, "rtsp");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            } catch {
                return System.IO.Path.GetTempPath();
            }
        }

        // Diagnostic log for the wire-commit / FinalizePipelineSetup /
        // TrySetupMpegPsPipeline chain — written unconditionally to
        // <configured-log-dir>/rtsp/softsled-commit-debug.log. Helps diagnose
        // "RTP arriving but pipeline never commits" bugs by tracing exactly
        // which branch the pipeline-setup state machine takes.
        private System.IO.StreamWriter _commitDiagLog;
        private void CommitDiagLog(string line) {
            try {
                if (_commitDiagLog == null) {
                    string path = System.IO.Path.Combine(
                        ResolveRtspDiagDir(),
                        "softsled-commit-debug.log");
                    _commitDiagLog = new System.IO.StreamWriter(
                        new System.IO.FileStream(path,
                            System.IO.FileMode.Create,
                            System.IO.FileAccess.Write,
                            System.IO.FileShare.Read),
                        System.Text.Encoding.ASCII) { AutoFlush = true };
                    _commitDiagLog.WriteLine($"# commit-debug, started {DateTime.Now:HH:mm:ss.fff}");
                }
                _commitDiagLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            } catch { /* never break the wire path because diag fails */ }
        }

        // Diagnostic log for the RTCP send loop + WMRTP timing dumps. Written to
        // <configured-log-dir>/rtsp/softsled-rtcp-debug.log so we have ground-truth
        // visibility into the timer/guards/sends — independent of Debug.WriteLine.
        private System.IO.StreamWriter _rtcpDiagLog;
        private void RtcpDiagInit() {
            if (_rtcpDiagLog != null) return;
            try {
                string path = System.IO.Path.Combine(ResolveRtspDiagDir(),
                                                     "softsled-rtcp-debug.log");
                _rtcpDiagLog = new System.IO.StreamWriter(
                    new System.IO.FileStream(path, System.IO.FileMode.Create,
                                             System.IO.FileAccess.Write,
                                             System.IO.FileShare.Read),
                    System.Text.Encoding.ASCII);
                _rtcpDiagLog.AutoFlush = true;
                _rtcpDiagLog.WriteLine($"# RTCP send-loop diagnostic, started {DateTime.Now:HH:mm:ss.fff}");
                _rtcpDiagLog.WriteLine($"# columns: timestamp tick event details");
            } catch (Exception ex) {
                Debug.WriteLine($"[rtcp-diag] log open failed: {ex.Message}");
            }
        }
        private void RtcpDiagLog(string line) {
            try { _rtcpDiagLog?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}"); } catch { }
        }

        /// <summary>
        /// 16-bit modular sequence comparison (RFC 1982 SerialNumberArithmetic
        /// scaled to 16 bits). Returns true if `a` is more recent than `b`,
        /// handling wrap-around.
        /// </summary>
        private static bool IsSeqGreater(ushort a, ushort b) {
            int diff = (a - b) & 0xFFFF;
            return diff != 0 && diff < 0x8000;
        }

        private static void WriteU32Be(byte[] buf, int off, uint value) {
            buf[off + 0] = (byte)((value >> 24) & 0xFF);
            buf[off + 1] = (byte)((value >> 16) & 0xFF);
            buf[off + 2] = (byte)((value >> 8) & 0xFF);
            buf[off + 3] = (byte)((value >> 0) & 0xFF);
        }

        #endregion ############################################################
    }
}