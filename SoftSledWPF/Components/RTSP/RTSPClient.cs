using FFmpeg.AutoGen;
using Rtsp.Messages;
using SoftSled.Components.AudioVisual;
using SoftSled.Components.AudioVisual.FormatStructures;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SoftSled.Components.RTSP {
    class RTSPClient {

        private string UserAgent = "User-Agent: MCExtender/1.50.X.090522.00"; // Assume Xbox 360?
        //private string UserAgent = "User-Agent: MCExtender/1.50.0.0";
        //private string UserAgent = "User-Agent: MCExtender/1.0.0.0"; // Linksys Extender (Doesn't support H.264?)

        private string AcceptHeader = "Accept: application/sdp";
        private string LanguageHeader = "Accept-Language: en-us, *;q=0.1";
        //describe_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
        private string SupportedHeader = "Supported: com.microsoft.wm.srvppair, com.microsoft.wm.sswitch, com.microsoft.wm.eosmsg, com.microsoft.wm.predstrm, com.microsoft.wm.fastcache, com.microsoft.wm.locid, com.microsoft.wm.rtp.asf, dlna.announce, dlna.rtx, dlna.rtx-dup, com.microsoft.wm.startupprofile";


        // Events that applications can receive
        public event Received_SPS_PPS_Delegate Received_SPS_PPS;
        public event Received_VPS_SPS_PPS_Delegate Received_VPS_SPS_PPS;
        public event Received_NALs_Delegate Received_NALs;
        public event Received_G711_Delegate Received_G711;
        public event Received_AMR_Delegate Received_AMR;
        public event Received_AAC_Delegate Received_AAC;

        // Delegated functions (essentially the function prototype)
        public delegate void Received_SPS_PPS_Delegate(byte[] sps, byte[] pps); // H264
        public delegate void Received_VPS_SPS_PPS_Delegate(byte[] vps, byte[] sps, byte[] pps); // H265
        public delegate void Received_NALs_Delegate(List<byte[]> nal_units); // H264 or H265
        public delegate void Received_G711_Delegate(String format, List<byte[]> g711);
        public delegate void Received_AMR_Delegate(String format, List<byte[]> amr);
        public delegate void Received_AAC_Delegate(String format, List<byte[]> aac, uint ObjectType, uint FrequencyIndex, uint ChannelConfiguration);

        public enum RTP_TRANSPORT { UDP, TCP, MULTICAST, UNKNOWN };
        public enum MEDIA_REQUEST { VIDEO_ONLY, AUDIO_ONLY, VIDEO_AND_AUDIO };
        private enum RTSP_STATUS { WaitingToConnect, Connecting, ConnectFailed, Connected };

        Rtsp.RtspTcpTransport rtsp_socket = null;               // RTSP connection
        volatile RTSP_STATUS rtsp_socket_status = RTSP_STATUS.WaitingToConnect;
        Rtsp.RtspListener rtsp_client = null;                   // this wraps around a the RTSP tcp_socket stream
        RTP_TRANSPORT rtp_transport = RTP_TRANSPORT.UNKNOWN;    // Mode, either RTP over UDP or RTP over TCP using the RTSP socket
        Rtsp.UDPSocket video_udp_pair = null;                   // Pair of UDP ports used in RTP over UDP mode or in MULTICAST mode
        Rtsp.UDPSocket audio_udp_pair = null;                   // Pair of UDP ports used in RTP over UDP mode or in MULTICAST mode
        string url = "";                                        // RTSP URL (username & password will be stripped out
        string base_url = "";                                   // RTSP Base URL
        string username = "";                                   // Username
        string password = "";                                   // Password
        string hostname = "";                                   // RTSP Server hostname or IP address
        int port = 0;                                           // RTSP Server TCP Port number
        string session = "";                                    // RTSP Session
        string auth_type = null;                                // cached from most recent WWW-Authenticate reply
        string realm = null;                                    // cached from most recent WWW-Authenticate reply
        string nonce = null;                                    // cached from most recent WWW-Authenticate reply
        uint ssrc = 12345;
        // Per-stream client SSRCs for RTCP reporter/sender fields. Xbox uses
        // distinct SSRCs per stream (audio=0xC252C62D, video=0x6043AC81).
        // Random per-session, but stable for the life of the session.
        private readonly uint _audioReporterSsrc = (uint)(0xC2520000u | ((uint)Guid.NewGuid().GetHashCode() & 0xFFFFu));
        private readonly uint _videoReporterSsrc = (uint)(0x60430000u | ((uint)Guid.NewGuid().GetHashCode() & 0xFFFFu));
        bool client_wants_video = false;                        // Client wants to receive Video
        bool client_wants_audio = false;                        // Client wants to receive Audio

        Uri video_uri = null;                                   // URI used for the Video Track
        int video_payload = -1;                                 // Payload Type for the Video. (often 96 which is the first dynamic payload value. Bosch use 35)
        int video_data_channel = -1;                            // RTP Channel Number used for the video RTP stream or the UDP port number
        int video_rtcp_channel = -1;                            // RTP Channel Number used for the video RTCP status report messages OR the UDP port number
        bool h264_sps_pps_fired = false;                        // True if the SDP included a sprop-Parameter-Set for H264 video
        bool h265_vps_sps_pps_fired = false;                    // True if the SDP included a sprop-vps, sprop-sps and sprop_pps for H265 video
        string video_codec = "";                                // Codec used with Payload Types 96..127 (eg "H264")

        Uri audio_uri = null;                                   // URI used for the Audio Track
        int audio_payload = -1;                                 // Payload Type for the Video. (often 96 which is the first dynamic payload value)
        int audio_data_channel = -1;                            // RTP Channel Number used for the audio RTP stream or the UDP port number
        int audio_rtcp_channel = -1;                            // RTP Channel Number used for the audio RTCP status report messages OR the UDP port number
        string audio_codec = "";                                // Codec used with Payload Types (eg "PCMA" or "AMR")

        bool server_supports_get_parameter = false;             // Used with RTSP keepalive
        bool server_supports_set_parameter = false;             // Used with RTSP keepalive
        System.Timers.Timer keepalive_timer = null;             // Used with RTSP keepalive

        // Annex B start code (0x00 0x00 0x00 0x01)
        private static readonly byte[] AnnexBStartCode = { 0x00, 0x00, 0x00, 0x01 };

        Rtsp.H264Payload h264Payload = null;
        Rtsp.H265Payload h265Payload = null;
        Rtsp.G711Payload g711Payload = new Rtsp.G711Payload();
        Rtsp.AMRPayload amrPayload = new Rtsp.AMRPayload();
        Rtsp.AACPayload aacPayload = null;

        Dictionary<int, WMFPayloadData> wmfPayloadDataDict = new Dictionary<int, WMFPayloadData>();

        // Phase-0c finding: WMC's WMPNss server picks an x-wmf-pf audio PT
        // whose fmtp config is `audio/vnd.wave;codec=1;config=...` — i.e. a
        // raw PCM WAVEFORMATEX inside the WMRTP wrapper. The depacketizer
        // already strips the wrapper; this sink takes the resulting PCM
        // MAUs and feeds NAudio for actual playback. Null until SDP
        // processing identifies a usable PT (see ProcessAudioPcmSink call
        // after the SDP for-loop).
        private WmrptPcmNAudioSink _pcmAudioSink;

        // WMPNss also negotiates plain MPEG-Audio-over-RTP (RFC 2250) when
        // the source is an MP3 file — rtpmap MPA/90000/N, payload type 96,
        // with a 4-byte RFC 2250 §3.5 sub-header (MBZ+Frag_offset) preceding
        // raw MP3 frames. NOT wrapped in WMRTP, so the depacketizer path is
        // bypassed; we feed packets directly to this sink which strips the
        // sub-header, peels MP3 frames, decodes via NAudio's ACM wrapper.
        //
        // The SAME sink is reused for WMC's recorded-TV MPEG-ES mode, which
        // advertises `vnd.ms.wm-MPA/90000/N` instead. In that mode the MPEG
        // audio frames are wrapped in WMRTP (so we route through the audio
        // depacketizer first) and there is no RFC 2250 sub-header on the
        // resulting MAU. _mpaSinkMode tracks which path is active.
        private Mp3OverRtpAudioSink _mpaAudioSink;
        private MpaSinkMode         _mpaSinkMode;

        // WMC recorded-TV MPEG-ES video-only path (vnd.ms.wm-MPV, no
        // matching audio). The videoDepacketizer's NalUnitReady emits one
        // MPEG video access unit per MAU; we enqueue them into a codec-
        // agnostic producer and hand the producer to FFME via
        // AsfFfmeInputStream forced to "mpegvideo". Active ONLY when audio
        // is absent or unsupported — when both wm-MPV video AND wm-MPA
        // audio are present, the PS pipeline below takes priority.
        private AsfStreamProducer  _mpvVideoProducer;
        private AsfFfmeInputStream _mpvVideoInputStream;
        private long _mpvMauCount;
        private long _mpvBytesSubmitted;
        private long _mpvSyncPointsSeen;
        private System.IO.StreamWriter _mpvDiagLog;
        private readonly Stopwatch _mpvDiagSw = new Stopwatch();

        // Wire-codec tracking. SDP advertises multiple PTs per direction;
        // the server picks at PLAY time. We commit to a specific pipeline
        // on the first observed RTP packet per direction. Once committed,
        // subsequent codec changes on the wire are ignored (which shouldn't
        // happen mid-session anyway). The committed codec is the string
        // from the SDP rtpmap entry (e.g. "VND.MS.WM-MPA", "X-WMF-PF").
        private string _wireAudioCodec;       // null until first audio packet
        private string _wireVideoCodec;       // null until first video packet
        private readonly object _commitLock = new object();

        // Cached RTP-Info anchoring values from the PLAY response. The
        // PS muxer is created LATER (lazily, on first MPA+MPV wire
        // packets observed), so we can't apply these immediately. Stored
        // here and replayed when TrySetupMpegPsPipeline creates the
        // muxer. uint? to distinguish "not seen" from "seen 0".
        private uint? _pendingAudioRtptime;
        private uint? _pendingVideoRtptime;

        // Modern WMC audio path: X-WMF-PF audio/vnd.wave with raw PCM. The
        // WMRTP depacketizer produces clean s16le samples per MAU which
        // we pump into FFME via a separate IMediaInputStream. Replaces the
        // NAudio-direct pipeline for media playback — user preference is
        // to keep NAudio for the WMC UI fast-path audio only, and have all
        // media (recorded TV, music, etc.) route through FFME for clock
        // consistency and codec-agnostic decoding.
        private AsfStreamProducer  _pcmFfmeProducer;
        private AsfFfmeInputStream _pcmFfmeInputStream;
        private long _pcmFfmeChunks;
        private long _pcmFfmeBytesPushed;
        private int  _pcmSampleRate;
        private int  _pcmChannels;
        private int  _pcmBitsPerSample;
        private System.IO.StreamWriter _pcmFfmeDiagLog;
        private readonly Stopwatch _pcmFfmeDiagSw = new Stopwatch();

        // Modern WMC video path: X-WMF-PF video/vnd.avi with codec="H264"
        // — used by live TV / DVR-MS captures that pair PCM audio with
        // H.264 video. The depacketizer emits H264 NAL units (SPS/PPS/AUD
        // are in-band so ffmpeg's H264 parser can pick them up without
        // extradata). Push into AsfStreamProducer + AsfFfmeInputStream
        // with forcedInputFormat="h264". Active when wmfPayloadDataDict
        // contains an X-WMF-PF video PT whose fmtp identifies H264, in
        // which case TrySetupMpegPsPipeline declines and we render video
        // here while PCM sink renders audio in parallel (independent
        // timelines — same trade-off as the original wm-MPA + wm-MPV
        // pre-PS pipeline state).
        private AsfStreamProducer  _h264VideoProducer;
        private AsfFfmeInputStream _h264VideoInputStream;
        private long _h264MauCount;
        private long _h264BytesSubmitted;
        private long _h264SyncPointsSeen;
        private System.IO.StreamWriter _h264DiagLog;
        private readonly Stopwatch _h264DiagSw = new Stopwatch();

        // MPEG Program Stream pipeline (preferred path when SDP has both
        // vnd.ms.wm-MPV video AND vnd.ms.wm-MPA audio). Muxes both streams
        // into a single PS feed to FFME, which then handles A/V sync via
        // its master clock — solves both the Layer-I-decode-needed problem
        // (libav decodes via FFmpeg) and the "two independent FFME elements
        // drift" problem (one pipeline, one clock).
        private MpegPsMuxer        _psMuxer;
        private AsfStreamProducer  _psProducer;
        private AsfFfmeInputStream _psInputStream;
        private long _psVideoMaus;
        private long _psAudioMaus;
        private long _psBytesPushed;
        private System.IO.StreamWriter _psDiagLog;
        private readonly Stopwatch _psDiagSw = new Stopwatch();

        // MPEG Transport Stream pipeline. Forward-looking unified container
        // for modern codecs (H264 + LPCM today; MPA/MPV/AC3 to follow).
        // Single FFME input stream → single libav clock → A/V sync without
        // the dual-MediaElement race the PCM-FFME + H264-producer path hit.
        private MpegTsMuxer        _tsMuxer;
        private AsfStreamProducer  _tsProducer;
        private AsfFfmeInputStream _tsInputStream;
        private long _tsVideoMaus;
        private long _tsAudioMaus;
        private long _tsBytesPushed;
        private System.IO.StreamWriter _tsDiagLog;
        private readonly Stopwatch _tsDiagSw = new Stopwatch();

        // Audio-only MP3 pipeline through FFME. Used when SDP advertises
        // an MPA or vnd.ms.wm-MPA audio codec WITHOUT a corresponding
        // video codec — i.e. music-file playback. libav handles all MPEG
        // layers (I/II/III) and FFME provides position / duration that
        // reflect the actual track. Replaces the legacy Mp3OverRtpAudioSink
        // → NAudio path for these sessions; the sink class itself stays
        // around as an opt-in fallback (env var SOFTSLED_AUDIO_VIA_NAUDIO=1).
        private AsfStreamProducer  _mp3FfmeProducer;
        private AsfFfmeInputStream _mp3FfmeInputStream;
        private long _mp3FfmeChunks;
        private long _mp3FfmeBytesPushed;
        private System.IO.StreamWriter _mp3FfmeDiagLog;
        private readonly Stopwatch _mp3FfmeDiagSw = new Stopwatch();
        private bool _mp3FfmeNeedsFrameStrip;  // true for RFC 2250 (4 B MBZ/Frag);
                                                // false for WMRTP-wrapped wm-MPA.

        /// <summary>
        /// Fired once the MPEG-ES video producer + FFME input stream are
        /// constructed and the depacketizer is wired. The handler (typically
        /// AvCtrlHandler → MainWindow) should call
        /// <c>Media.Open(IMediaInputStream)</c> on the WPF dispatcher.
        /// FFME's Read will block until media data starts flowing after PLAY.
        /// </summary>
        public event Action<Unosquare.FFME.Common.IMediaInputStream> VideoPipelineReady;

        /// <summary>
        /// Fired once an audio-only FFME pipeline is constructed (currently
        /// only the X-WMF-PF raw-PCM path). The host UI subscribes to it
        /// and opens a *separate* FFME element for audio so the H264 video
        /// element and the PCM audio element can render in parallel.
        /// </summary>
        public event Action<Unosquare.FFME.Common.IMediaInputStream> AudioPipelineReady;

        private enum MpaSinkMode {
            None        = 0,
            /// <summary>rtpmap MPA — RFC 2250 with 4-byte MBZ+Frag prefix per packet.</summary>
            Rfc2250     = 1,
            /// <summary>rtpmap vnd.ms.wm-MPA — WMRTP-wrapped, MAU = raw MPEG frames.</summary>
            WmrtpWrapped = 2,
        }

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
        private bool   _audioSeqInit;
        private uint   _audioSeqCycles;            // upper 16 bits of extended highest seq
        private ushort _videoBaseSeq;
        private ushort _videoHighestSeq;
        private bool   _videoSeqInit;
        private uint   _videoSeqCycles;
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
        private const ushort BfrTdMs        = 2000;     // matches SETUP Buffer-Info TD=2000

        WmrptVideoDepacketizer videoDepacketizer = null;
        WmrptAudioDepacketizer audioDepacketizer = null;
        List<Rtsp.Messages.RtspRequestSetup> setup_messages = new List<Rtsp.Messages.RtspRequestSetup>(); // setup messages still to send

        // Constructor. The session has two depacketizers (audio + video) that
        // produce MAUs; each MAU is routed by codec to one of the in-process
        // sinks set up after SDP processing:
        //   * x-wmf-pf audio (vnd.wave PCM)      → WmrptPcmNAudioSink
        //   * MPA / vnd.ms.wm-MPA audio          → Mp3OverRtpAudioSink
        //   * vnd.ms.wm-MPV video                → AsfStreamProducer → FFME
        // No external processes are spawned. The previous external-ffmpeg /
        // ffplay pipe path (AvPipeMuxer) has been removed.
        public RTSPClient() {
            videoDepacketizer = new WmrptVideoDepacketizer();
            audioDepacketizer = new WmrptAudioDepacketizer();

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

                // MPEG-TS unified container (preferred for modern codecs).
                // Same idea as the PS path but uses 188-byte TS packets
                // with per-stream PIDs + PCR + PAT/PMT — supports H264 and
                // raw-LE PCM in one stream, future-proof for everything
                // else WMC sends.
                if (_tsMuxer != null && _tsProducer != null) {
                    byte[] muxed = _tsMuxer.MuxVideoAccessUnit(eventData.data,
                                                               eventData.timestamp,
                                                               eventData.SyncPoint);
                    _tsProducer.SubmitChunk(muxed);
                    _tsVideoMaus++;
                    _tsBytesPushed += muxed.Length;
                    if ((_tsVideoMaus % 100) == 1) {
                        Trace.WriteLine($"[ts] vid#{_tsVideoMaus} len={eventData.data.Length} " +
                                        $"muxed={muxed.Length} pts(rtp)={eventData.timestamp} " +
                                        $"producerQ={_tsProducer.QueueDepth}");
                        WriteTsDiagRow();
                    }
                    return;
                }

                // MPEG-PS combined path (preferred when audio is also present).
                // Mux video access unit into a PES packet stamped with the RTP
                // timestamp as PTS, push into the shared producer. FFME's
                // libav decodes both streams and syncs them on its own clock.
                if (_psMuxer != null && _psProducer != null) {
                    byte[] muxed = _psMuxer.MuxVideoAccessUnit(eventData.data,
                                                               eventData.timestamp,
                                                               eventData.SyncPoint);
                    _psProducer.SubmitChunk(muxed);
                    _psVideoMaus++;
                    _psBytesPushed += muxed.Length;
                    if ((_psVideoMaus % 100) == 1) {
                        Trace.WriteLine($"[ps] vid#{_psVideoMaus} len={eventData.data.Length} " +
                                        $"muxed={muxed.Length} pts(rtp)={eventData.timestamp} " +
                                        $"producerQ={_psProducer.QueueDepth}");
                        WritePsDiagRow();
                    }
                    return;
                }

                // Modern H264 video-only path (X-WMF-PF video/vnd.avi
                // codec="H264"): the depacketizer emits H264 access units
                // with in-band SPS/PPS/AUD NALUs. Push raw bytes — ffmpeg's
                // H264 parser handles framing. Active in parallel with the
                // PCM audio sink for current-WMC TV recordings.
                if (_h264VideoProducer != null) {
                    if (eventData.SyncPoint) _h264SyncPointsSeen++;
                    _h264VideoProducer.SubmitChunk(eventData.data);
                    _h264MauCount++;
                    _h264BytesSubmitted += eventData.data.Length;
                    if ((_h264MauCount % 100) == 1) {
                        Trace.WriteLine($"[h264] mau#{_h264MauCount} len={eventData.data.Length} " +
                                        $"sync={eventData.SyncPoint} producerQ={_h264VideoProducer.QueueDepth} " +
                                        $"bytesProduced={_h264VideoProducer.BytesProduced}");
                        if (_h264DiagLog != null) {
                            try {
                                _h264DiagLog.WriteLine(
                                    $"{_h264DiagSw.Elapsed.TotalSeconds,7:F2} " +
                                    $"{_h264MauCount,7} {_h264SyncPointsSeen,5} {_h264BytesSubmitted,10} " +
                                    $"{_h264VideoProducer.QueueDepth,5} " +
                                    $"{_h264VideoProducer.BytesProduced,10} " +
                                    $"{_h264VideoProducer.BytesConsumed,10} " +
                                    $"{_h264VideoProducer.DroppedChunks,5}");
                            } catch { /* ignore */ }
                        }
                    }
                    return;
                }

                // MPEG-ES video-only path: each MAU is a complete MPEG-1/2
                // video access unit pushed into the producer feeding FFME.
                if (_mpvVideoProducer != null) {
                    if (eventData.SyncPoint) _mpvSyncPointsSeen++;
                    _mpvVideoProducer.SubmitChunk(eventData.data);
                    _mpvMauCount++;
                    _mpvBytesSubmitted += eventData.data.Length;
                    if ((_mpvMauCount % 100) == 1) {
                        Trace.WriteLine($"[mpv] mau#{_mpvMauCount} len={eventData.data.Length} " +
                                        $"sync={eventData.SyncPoint} producerQ={_mpvVideoProducer.QueueDepth} " +
                                        $"bytesProduced={_mpvVideoProducer.BytesProduced}");
                        if (_mpvDiagLog != null) {
                            try {
                                _mpvDiagLog.WriteLine(
                                    $"{_mpvDiagSw.Elapsed.TotalSeconds,7:F2} " +
                                    $"{_mpvMauCount,7} {_mpvSyncPointsSeen,5} {_mpvBytesSubmitted,10} " +
                                    $"{_mpvVideoProducer.QueueDepth,5} " +
                                    $"{_mpvVideoProducer.BytesProduced,10} " +
                                    $"{_mpvVideoProducer.BytesConsumed,10} " +
                                    $"{_mpvVideoProducer.DroppedChunks,5}");
                            } catch { /* ignore */ }
                        }
                    }
                    return;
                }

                // No sink for this video stream — codec not yet supported.
                // (H.264 / x-wmf-pf video MAUs land here. Wire up an FFME
                // producer with forcedInputFormat="h264" to handle.)
                if ((_videoDroppedNoSink & 0xFF) == 0) {
                    Trace.WriteLine($"[video] MAU dropped (no sink configured): " +
                                    $"len={eventData.data.Length}");
                }
                _videoDroppedNoSink++;
            };

            audioDepacketizer.AudioDataReady += async (s, eventData) => {
                // PTS monitor (Layer 4f) + decoder-open stopwatch arm
                // (Layer 4g(a)). See the symmetric block in
                // videoDepacketizer.NalUnitReady above.
                MonitorPts(isAudio: true, rtpTs: eventData.timestamp);
                ArmDecoderOpenStopwatch();

                // MPEG-TS unified path (preferred for modern codecs).
                // PCM samples go in as raw LE bytes; muxer applies any
                // codec-specific framing (Blu-ray LPCM header etc.) and
                // wraps into PES then TS packets on the audio PID.
                if (_tsMuxer != null && _tsProducer != null) {
                    byte[] muxed = _tsMuxer.MuxAudioFrame(eventData.data, eventData.timestamp);
                    _tsProducer.SubmitChunk(muxed);
                    _tsAudioMaus++;
                    _tsBytesPushed += muxed.Length;
                    if ((_tsAudioMaus % 100) == 1) {
                        Trace.WriteLine($"[ts] aud#{_tsAudioMaus} len={eventData.data.Length} " +
                                        $"muxed={muxed.Length} pts(rtp)={eventData.timestamp} " +
                                        $"producerQ={_tsProducer.QueueDepth}");
                        WriteTsDiagRow();
                    }
                    return;
                }

                // MPEG-PS combined path (takes priority): mux audio frames
                // into a PES packet with PTS = RTP timestamp, push into the
                // shared producer. FFME's libav handles Layer I/II/III alike,
                // and the shared PS clock gives us A/V sync.
                if (_psMuxer != null && _psProducer != null) {
                    byte[] muxed = _psMuxer.MuxAudioFrame(eventData.data, eventData.timestamp);
                    _psProducer.SubmitChunk(muxed);
                    _psAudioMaus++;
                    _psBytesPushed += muxed.Length;
                    if ((_psAudioMaus % 100) == 1) {
                        Trace.WriteLine($"[ps] aud#{_psAudioMaus} len={eventData.data.Length} " +
                                        $"muxed={muxed.Length} pts(rtp)={eventData.timestamp} " +
                                        $"producerQ={_psProducer.QueueDepth}");
                        WritePsDiagRow();
                    }
                    return;
                }

                // WMRTP-wrapped MPEG audio (vnd.ms.wm-MPA), audio-only mode.
                // MAU = clean run of MPEG audio frame bytes (no RFC 2250
                // sub-header). Prefer the FFME / libav pipeline (handles
                // Layer I/II/III + emits accurate position/duration); fall
                // back to NAudio MP3 sink when SOFTSLED_AUDIO_VIA_NAUDIO=1
                // is set (legacy path).
                if (_mp3FfmeProducer != null) {
                    byte[] chunk = eventData.data;
                    _mp3FfmeProducer.SubmitChunk(chunk);
                    _mp3FfmeChunks++;
                    _mp3FfmeBytesPushed += chunk.Length;
                    if ((_mp3FfmeChunks % 100) == 1) {
                        Trace.WriteLine($"[mp3-ffme] mau#{_mp3FfmeChunks} len={chunk.Length} " +
                                        $"producerQ={_mp3FfmeProducer.QueueDepth}");
                        WriteMp3FfmeDiagRow();
                    }
                    return;
                }
                if (_mpaAudioSink != null && _mpaSinkMode == MpaSinkMode.WmrtpWrapped) {
                    _mpaAudioSink.SubmitRawMpegAudioFrames(eventData.data, 0, eventData.data.Length);
                    return;
                }
                // Raw-PCM x-wmf-pf audio (audio/vnd.wave fmtp) — preferred
                // path: push samples into the FFME audio pipeline.
                // libavformat's s16le demuxer with SDP-derived sample_rate
                // and channels options decodes via libav, so the user gets
                // FFME-clock consistency across audio + video and we keep
                // NAudio reserved for the WMC fast-path UI audio.
                if (_pcmFfmeProducer != null) {
                    byte[] chunk = eventData.data;
                    _pcmFfmeProducer.SubmitChunk(chunk);
                    _pcmFfmeChunks++;
                    _pcmFfmeBytesPushed += chunk.Length;
                    if ((_pcmFfmeChunks % 100) == 1) {
                        Trace.WriteLine($"[pcm-ffme] mau#{_pcmFfmeChunks} len={chunk.Length} " +
                                        $"producerQ={_pcmFfmeProducer.QueueDepth}");
                        if (_pcmFfmeDiagLog != null) {
                            try {
                                _pcmFfmeDiagLog.WriteLine(
                                    $"{_pcmFfmeDiagSw.Elapsed.TotalSeconds,7:F2} " +
                                    $"{_pcmFfmeChunks,7} {_pcmFfmeBytesPushed,10} " +
                                    $"{_pcmFfmeProducer.QueueDepth,5} " +
                                    $"{_pcmFfmeProducer.BytesProduced,12} " +
                                    $"{_pcmFfmeProducer.BytesConsumed,12} " +
                                    $"{_pcmFfmeProducer.DroppedChunks,5}");
                            } catch { /* ignore */ }
                        }
                    }
                    return;
                }
                // Legacy fallback: raw-PCM x-wmf-pf audio straight to NAudio
                // with the SDP-derived WAVEFORMATEX. Kept for ops debugging
                // (flip SOFTSLED_AUDIO_VIA_NAUDIO=1 to route here).
                if (_pcmAudioSink != null) {
                    _pcmAudioSink.Submit(eventData.data);
                    return;
                }

                // No sink for this audio stream.
                if ((_audioDroppedNoSink & 0xFF) == 0) {
                    Trace.WriteLine($"[audio] MAU dropped (no sink configured): " +
                                    $"len={eventData.data.Length}");
                }
                _audioDroppedNoSink++;
            };
        }

        // Drop counters for MAUs that arrive when no sink has been set up
        // for the corresponding codec — log throttled at every 256 to avoid
        // spam. Useful for telling apart "depacketizer dead" vs "no sink".
        private long _videoDroppedNoSink;
        private long _audioDroppedNoSink;


        public void Connect(string url, RTP_TRANSPORT rtp_transport, MEDIA_REQUEST media_request = MEDIA_REQUEST.VIDEO_AND_AUDIO) {

            Rtsp.RtspUtils.RegisterUri();

            System.Diagnostics.Debug.WriteLine("Connecting to " + url);
            this.url = url;

            // Use URI to extract username and password
            // and to make a new URL without the username and password
            try {
                Uri uri = new Uri(this.url);
                hostname = uri.Host;
                port = uri.Port;

                if (uri.UserInfo.Length > 0) {
                    username = uri.UserInfo.Split(new char[] { ':' })[0];
                    password = uri.UserInfo.Split(new char[] { ':' })[1];
                    this.url = uri.GetComponents((UriComponents.AbsoluteUri & ~UriComponents.UserInfo),
                                                 UriFormat.UriEscaped);
                }
            } catch {
                username = null;
                password = null;
            }

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
            Rtsp.IRtspTransport listenerTransport =
                SoftSled.Components.Diagnostics.RtspWireDumper.MaybeWrap(rtsp_socket, null);

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


            //// Send OPTIONS
            //// In the Received Message handler we will send DESCRIBE, SETUP and PLAY
            //Rtsp.Messages.RtspRequest options_message = new Rtsp.Messages.RtspRequestOptions();
            //options_message.RtspUri = new Uri(this.url);
            //options_message.AddHeader(LanguageHeader);
            //options_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
            //options_message.AddHeader(UserAgent);
            //rtsp_client.SendMessage(options_message);

            // Send DESCRIBE
            Rtsp.Messages.RtspRequest describe_message = new Rtsp.Messages.RtspRequestDescribe();
            describe_message.RtspUri = new Uri(url);
            describe_message.AddHeader(AcceptHeader);
            describe_message.AddHeader(LanguageHeader);
            //describe_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
            describe_message.AddHeader(SupportedHeader);
            describe_message.AddHeader(UserAgent);
            rtsp_client.SendMessage(describe_message);
        }

        // return true if this connection failed, or if it connected but is no longer connected.
        public bool StreamingFinished() {
            if (rtsp_socket_status == RTSP_STATUS.ConnectFailed) return true;
            if (rtsp_socket_status == RTSP_STATUS.Connected && rtsp_socket.Connected == false) return true;
            else return false;
        }


        public void Pause() {
            if (rtsp_client != null) {
                // Send PAUSE
                Rtsp.Messages.RtspRequest pause_message = new Rtsp.Messages.RtspRequestPause();
                pause_message.RtspUri = new Uri(url);
                pause_message.Session = session;
                pause_message.AddHeader(LanguageHeader);
                //pause_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
                pause_message.AddHeader(SupportedHeader);
                pause_message.AddHeader(UserAgent);
                if (auth_type != null) {
                    AddAuthorization(pause_message, username, password, auth_type, realm, nonce, url);
                }
                rtsp_client.SendMessage(pause_message);
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
            // -1 startMs is "use cached" — equate to the last-sent
            // startMs for the purposes of this check so a pure rate
            // change with no seek doesn't get artificially differentiated.
            long candidateStart = startMs >= 0 ? startMs : _lastSentStartMs;
            if (_lastSentStartMs == candidateStart &&
                System.Math.Abs(_lastSentRate - rate) < 0.0001) {
                Debug.WriteLine($"[rtsp] Play({startMs},{rate}) suppressed — " +
                                $"wire state already (start={_lastSentStartMs}, rate={_lastSentRate})");
                return;
            }

            // Post-auto-PLAY: mid-session change. Send a fresh PLAY
            // that atomically replaces the prior playback params.
            SendPlayMessage(startMs, rate);
        }

        /// <summary>Most recent (startMs, rate) actually written to the
        /// RTSP wire by <see cref="SendPlayMessage"/>. Used to suppress
        /// no-op re-PLAYs from WMC's repeating Start commands. -1 / 1.0
        /// means "no PLAY sent yet this session".</summary>
        private long   _lastSentStartMs = -1;
        private double _lastSentRate    = 1.0;

        /// <summary>
        /// Send an RTSP PLAY directly to the wire, bypassing the
        /// auto-PLAY-deferral guard in <see cref="Play"/>. Called from
        /// (a) the auto-PLAY trigger inside Rtsp_MessageReceived after
        /// the last SETUP response, and (b) the mid-session Play()/
        /// SetRate() path once <see cref="_initialPlayFired"/> is set.
        /// </summary>
        private void SendPlayMessage(long startMs, double rate) {
            if (rtsp_client == null) return;

            RtspRequest play_message = new Rtsp.Messages.RtspRequestPlay();
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

            if (auth_type != null) {
                AddAuthorization(play_message, username, password, auth_type, realm, nonce, url);
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
            _lastSentRate    = rate;

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
                rate:    double.IsNaN(_currentRateRequested) ? 1.0 : _currentRateRequested);
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
                var msg = new Rtsp.Messages.RtspRequestSetParameter();
                msg.RtspUri = new Uri(url);
                msg.Session = session;
                msg.AddHeader(LanguageHeader);
                msg.AddHeader(SupportedHeader);
                msg.AddHeader(UserAgent);
                msg.AddHeader(BuildBufferInfoHeader(bandwidthBps, optimisedPreroll));
                if (auth_type != null) {
                    AddAuthorization(msg, username, password, auth_type, realm, nonce, url);
                }
                rtsp_client.SendMessage(msg);
            } catch (Exception ex) {
                Debug.WriteLine($"[rtsp] SetBufferInfo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Build the <c>Buffer-Info.dlna.org</c> request-line header value
        /// from a bandwidth hint and the OptimisedPreroll flag.
        ///
        /// Capture-observed default (Example 1 etc.):
        /// <c>dejitter=6624000;CDB=6553600;BTM=0;TD=2000;BFR=0</c>
        ///
        /// Our adjustments:
        /// <list type="bullet">
        ///   <item>dejitter scales inversely with bandwidth — clamp to the
        ///   capture-observed 6624000 (≈6.6 MB) ceiling, drop to 16384
        ///   floor.</item>
        ///   <item>BTM=0 and BFR=0 when optimisedPreroll, else the
        ///   capture defaults (BTM=0;BFR=0 was already the default in
        ///   the corpus, but if the server later prefers a non-zero
        ///   ramp we expose the knob here).</item>
        ///   <item>CDB/TD held at capture defaults.</item>
        /// </list>
        /// </summary>
        internal static string BuildBufferInfoHeader(long bandwidthBps, bool optimisedPreroll) {
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
            int bfr = optimisedPreroll ? 0 : 0;        // unchanged (capture default)
            const int cdb = 6553600;
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

        // ====================================================================
        //  Spec-event surface (Phase 2)
        //  -------------------------------------------------------------------
        //  These three events are consumed by FfmeMediaController and relayed
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

        // --- internal helpers used by 4d-4g of the playback plan ------------
        internal void RaiseDisconnected(Exception ex) {
            try { Disconnected?.Invoke(ex); }
            catch (Exception subEx) {
                Debug.WriteLine($"[rtsp] Disconnected handler threw: {subEx.Message}");
            }
        }
        internal void RaisePtsError(PtsErrorInfo info) {
            try { PtsError?.Invoke(info); }
            catch (Exception subEx) {
                Debug.WriteLine($"[rtsp] PtsError handler threw: {subEx.Message}");
            }
        }
        internal void RaiseUnrecoverableSkew(SkewInfo info) {
            try { UnrecoverableSkew?.Invoke(info); }
            catch (Exception subEx) {
                Debug.WriteLine($"[rtsp] UnrecoverableSkew handler threw: {subEx.Message}");
            }
        }

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
        private int  _avSkewFiredForThisSeek;   // 0 = not yet, 1 = fired
        // UNRECOVERABLE_SKEW decoder-open state (Layer 4g(a)).
        private readonly Stopwatch _decoderOpenSw = new Stopwatch();
        private int _decoderOpenArmed;          // 0 = not armed, 1 = armed (stopwatch running)
        private int _decoderOpenFired;          // 0 = not fired, 1 = fired
        // SDP-derived clock-Hz per stream (set in SDP processing).
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
                Rtsp.Messages.RtspRequest teardown_message = new Rtsp.Messages.RtspRequestTeardown();
                teardown_message.RtspUri = new Uri(url);
                teardown_message.Session = session;
                teardown_message.AddHeader(LanguageHeader);
                //teardown_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
                teardown_message.AddHeader(SupportedHeader);
                teardown_message.AddHeader(UserAgent);
                if (auth_type != null) {
                    AddAuthorization(teardown_message, username, password, auth_type, realm, nonce, url);
                }
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

            // Tear down the PCM audio sink (frees the NAudio output device).
            try { _pcmAudioSink?.Dispose(); } catch { }
            _pcmAudioSink = null;

            // Tear down the MPA (MP3-over-RTP) audio sink.
            try { _mpaAudioSink?.Dispose(); } catch { }
            _mpaAudioSink = null;
            _mpaSinkMode  = MpaSinkMode.None;

            // Tear down the audio-only MP3 FFME pipeline if active.
            try {
                if (_mp3FfmeDiagLog != null) {
                    long prod = _mp3FfmeProducer?.BytesProduced ?? 0;
                    long cons = _mp3FfmeProducer?.BytesConsumed ?? 0;
                    long drop = _mp3FfmeProducer?.DroppedChunks ?? 0;
                    _mp3FfmeDiagLog.WriteLine();
                    _mp3FfmeDiagLog.WriteLine(
                        $"# Pipeline disposed: chunks={_mp3FfmeChunks} " +
                        $"bytesPushed={_mp3FfmeBytesPushed} producerProduced={prod} " +
                        $"producerConsumed={cons} drops={drop} " +
                        $"wall={_mp3FfmeDiagSw.Elapsed.TotalSeconds:F2}s");
                    _mp3FfmeDiagLog.Dispose();
                }
            } catch { }
            _mp3FfmeDiagLog = null;
            _mp3FfmeDiagSw.Reset();
            try { _mp3FfmeProducer?.Complete(); } catch { }
            try { _mp3FfmeProducer?.Dispose(); } catch { }
            _mp3FfmeProducer    = null;
            _mp3FfmeInputStream = null;
            _mp3FfmeNeedsFrameStrip = false;
            _mp3FfmeChunks = 0; _mp3FfmeBytesPushed = 0;

            // Tear down the combined MPEG-PS pipeline if active.
            try {
                if (_psDiagLog != null) {
                    long prod = _psProducer?.BytesProduced ?? 0;
                    long cons = _psProducer?.BytesConsumed ?? 0;
                    long drop = _psProducer?.DroppedChunks ?? 0;
                    _psDiagLog.WriteLine();
                    _psDiagLog.WriteLine(
                        $"# Pipeline disposed: vidMAUs={_psVideoMaus} audMAUs={_psAudioMaus} " +
                        $"bytesPushed={_psBytesPushed} producerProduced={prod} " +
                        $"producerConsumed={cons} drops={drop} " +
                        $"wall={_psDiagSw.Elapsed.TotalSeconds:F2}s");
                    _psDiagLog.Dispose();
                }
            } catch { }
            _psDiagLog = null;
            _psDiagSw.Reset();
            try {
                if (_psProducer != null && _psMuxer != null) {
                    // Push the PS End Code so FFME hits clean EOF.
                    _psProducer.SubmitChunk(_psMuxer.EndCode());
                }
            } catch { }
            try { _psProducer?.Complete(); } catch { }
            try { _psProducer?.Dispose(); } catch { }
            _psProducer    = null;
            _psInputStream = null;
            _psMuxer       = null;
            Debug.WriteLine($"[ps-ffme] teardown: vidMAUs={_psVideoMaus} audMAUs={_psAudioMaus} " +
                            $"bytesPushed={_psBytesPushed}");
            _psVideoMaus = 0; _psAudioMaus = 0; _psBytesPushed = 0;

            // Tear down the MPEG-TS unified pipeline if active.
            try {
                if (_tsDiagLog != null) {
                    long prod = _tsProducer?.BytesProduced ?? 0;
                    long cons = _tsProducer?.BytesConsumed ?? 0;
                    long drop = _tsProducer?.DroppedChunks ?? 0;
                    _tsDiagLog.WriteLine();
                    _tsDiagLog.WriteLine(
                        $"# Pipeline disposed: vidMAUs={_tsVideoMaus} audMAUs={_tsAudioMaus} " +
                        $"bytesPushed={_tsBytesPushed} producerProduced={prod} " +
                        $"producerConsumed={cons} drops={drop} " +
                        $"wall={_tsDiagSw.Elapsed.TotalSeconds:F2}s");
                    _tsDiagLog.Dispose();
                }
            } catch { }
            _tsDiagLog = null;
            _tsDiagSw.Reset();
            try {
                if (_tsProducer != null && _tsMuxer != null) {
                    _tsProducer.SubmitChunk(_tsMuxer.EndCode());
                }
            } catch { }
            try { _tsProducer?.Complete(); } catch { }
            try { _tsProducer?.Dispose(); } catch { }
            _tsProducer    = null;
            _tsInputStream = null;
            _tsMuxer       = null;
            _tsVideoMaus = 0; _tsAudioMaus = 0; _tsBytesPushed = 0;

            // Tear down the MPEG-ES video producer. Calling Complete() lets
            // FFME's Read return 0 (EOF) so it can close cleanly; the host
            // UI subscribes to VideoPipelineReady and is responsible for
            // calling Media.Close on the WPF dispatcher.
            try {
                if (_mpvDiagLog != null) {
                    long producedNow  = _mpvVideoProducer?.BytesProduced ?? 0;
                    long consumedNow  = _mpvVideoProducer?.BytesConsumed ?? 0;
                    long droppedNow   = _mpvVideoProducer?.DroppedChunks ?? 0;
                    _mpvDiagLog.WriteLine();
                    _mpvDiagLog.WriteLine(
                        $"# Producer disposed: MAUs={_mpvMauCount} syncPts={_mpvSyncPointsSeen} " +
                        $"bytesSubmitted={_mpvBytesSubmitted} producerProduced={producedNow} " +
                        $"producerConsumed={consumedNow} drops={droppedNow} " +
                        $"wall={_mpvDiagSw.Elapsed.TotalSeconds:F2}s");
                    _mpvDiagLog.Dispose();
                }
            } catch { }
            _mpvDiagLog = null;
            _mpvDiagSw.Reset();
            try { _mpvVideoProducer?.Complete(); } catch { }
            try { _mpvVideoProducer?.Dispose(); } catch { }
            _mpvVideoProducer    = null;
            _mpvVideoInputStream = null;
            Debug.WriteLine($"[mpv-ffme] teardown: maus={_mpvMauCount} " +
                            $"bytesSubmitted={_mpvBytesSubmitted} syncPts={_mpvSyncPointsSeen}");
            _mpvMauCount = 0; _mpvBytesSubmitted = 0; _mpvSyncPointsSeen = 0;

            // Tear down the PCM-FFME audio pipeline.
            try {
                if (_pcmFfmeDiagLog != null) {
                    long prod = _pcmFfmeProducer?.BytesProduced ?? 0;
                    long cons = _pcmFfmeProducer?.BytesConsumed ?? 0;
                    long drop = _pcmFfmeProducer?.DroppedChunks ?? 0;
                    _pcmFfmeDiagLog.WriteLine();
                    _pcmFfmeDiagLog.WriteLine(
                        $"# Pipeline disposed: chunks={_pcmFfmeChunks} " +
                        $"bytesPushed={_pcmFfmeBytesPushed} producerProduced={prod} " +
                        $"producerConsumed={cons} drops={drop} " +
                        $"wall={_pcmFfmeDiagSw.Elapsed.TotalSeconds:F2}s");
                    _pcmFfmeDiagLog.Dispose();
                }
            } catch { }
            _pcmFfmeDiagLog = null;
            _pcmFfmeDiagSw.Reset();
            try { _pcmFfmeProducer?.Complete(); } catch { }
            try { _pcmFfmeProducer?.Dispose(); } catch { }
            _pcmFfmeProducer    = null;
            _pcmFfmeInputStream = null;
            _pcmFfmeChunks = 0; _pcmFfmeBytesPushed = 0;
            _pcmSampleRate = 0; _pcmChannels = 0; _pcmBitsPerSample = 0;

            // Reset wire-commit state so the next session re-detects
            // codecs on its own RTP packets.
            _wireAudioCodec = null;
            _wireVideoCodec = null;
            _pipelinesCommitted = false;
            _pendingAudioRtptime = null;
            _pendingVideoRtptime = null;
            try { _commitTimer?.Dispose(); } catch { }
            _commitTimer = null;
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
            _bufferInfoBandwidthBps = -1;
            _bufferInfoOptimisedPreroll = false;

            // Tear down the H264 video producer. Mirrors the MPV teardown.
            try {
                if (_h264DiagLog != null) {
                    long producedNow  = _h264VideoProducer?.BytesProduced ?? 0;
                    long consumedNow  = _h264VideoProducer?.BytesConsumed ?? 0;
                    long droppedNow   = _h264VideoProducer?.DroppedChunks ?? 0;
                    _h264DiagLog.WriteLine();
                    _h264DiagLog.WriteLine(
                        $"# Producer disposed: MAUs={_h264MauCount} syncPts={_h264SyncPointsSeen} " +
                        $"bytesSubmitted={_h264BytesSubmitted} producerProduced={producedNow} " +
                        $"producerConsumed={consumedNow} drops={droppedNow} " +
                        $"wall={_h264DiagSw.Elapsed.TotalSeconds:F2}s");
                    _h264DiagLog.Dispose();
                }
            } catch { }
            _h264DiagLog = null;
            _h264DiagSw.Reset();
            try { _h264VideoProducer?.Complete(); } catch { }
            try { _h264VideoProducer?.Dispose(); } catch { }
            _h264VideoProducer    = null;
            _h264VideoInputStream = null;
            Debug.WriteLine($"[h264-ffme] teardown: maus={_h264MauCount} " +
                            $"bytesSubmitted={_h264BytesSubmitted} syncPts={_h264SyncPointsSeen}");
            _h264MauCount = 0; _h264BytesSubmitted = 0; _h264SyncPointsSeen = 0;

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
        // Diagnostic log for the RTCP send loop. Written to %TEMP%\softsled-rtcp-debug.log
        // so we have ground-truth visibility into whether the timer fires, what
        // the guards see, whether sends succeed, etc. — independent of Debug.WriteLine.
        private System.IO.StreamWriter _rtcpDiagLog;
        private void RtcpDiagInit() {
            if (_rtcpDiagLog != null) return;
            try {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
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
                try { SendRtcpReceiverReport(); }
                catch (Exception ex) {
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
                                $"w3={ComputeBfrW3Fill()}ms " +
                                $"firstBytes={BitConverter.ToString(pkt, 0, Math.Min(16, pkt.Length))}");
                    if (tick <= 3 || (tick % 10) == 0)
                        Debug.WriteLine($"[rtcp] #{tick} audio RR+SDES{(_audioBfrEnabled ? "+BFR" : "")} " +
                                        $"{pkt.Length}B → {hostname}:{_audioServerRtcpPort} " +
                                        $"(extHigh={_audioSeqCycles << 16 | _audioHighestSeq})");
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
                                $"w3={ComputeBfrW3Fill()}ms " +
                                $"firstBytes={BitConverter.ToString(pkt, 0, Math.Min(16, pkt.Length))}");
                    if (tick <= 3 || (tick % 10) == 0)
                        Debug.WriteLine($"[rtcp] #{tick} video RR+SDES{(_videoBfrEnabled ? "+BFR" : "")} " +
                                        $"{pkt.Length}B → {hostname}:{_videoServerRtcpPort}");
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
        ///   W3 [8..11]: <c>high-16 = buffer fill in ms</c> (saturates near TD=2000);
        ///               low-16 = 0xFFFF sentinel.
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
            sdes[3] = (byte)((sdesWords     ) & 0xFF);
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
            ushort bufferFillMs = ComputeBfrW3Fill();
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
        /// Compute the W3 buffer-fill value for the BFR FCI, expressed as
        /// milliseconds of "buffered media". Returns a value in
        /// <c>[0, BfrTdMs]</c>. Ramps linearly from 0 to TD over the first
        /// <see cref="BfrRampDurationMs"/> wall-clock ms after the ramp
        /// stopwatch starts, then holds at TD. Matches the Xbox-360
        /// capture pattern.
        /// </summary>
        private ushort ComputeBfrW3Fill() {
            if (!_bfrRampStopwatch.IsRunning) return 0;
            long ms = _bfrRampStopwatch.ElapsedMilliseconds;
            if (ms <= 0) return 0;
            if (ms >= BfrRampDurationMs) return BfrTdMs;
            // Linear ramp 0 → BfrTdMs over BfrRampDurationMs.
            return (ushort)((ms * BfrTdMs) / BfrRampDurationMs);
        }

        private static void WriteU32Be(byte[] buf, int off, uint value) {
            buf[off + 0] = (byte)((value >> 24) & 0xFF);
            buf[off + 1] = (byte)((value >> 16) & 0xFF);
            buf[off + 2] = (byte)((value >> 8)  & 0xFF);
            buf[off + 3] = (byte)((value >> 0)  & 0xFF);
        }

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
                    _pendingAudioRtptime = rtptime;
                    // Apply to whichever combined muxer is up. Both branches
                    // use the first-anchor-vs-reanchor split so a mid-session
                    // PLAY-with-Range (seek) or PLAY-with-Scale (trick play)
                    // takes the monotonicity-preserving Reanchor path,
                    // while the very first PLAY response uses the one-shot
                    // SetXxxBaseTs.
                    if (_psMuxer != null) {
                        if (_psMuxer.AudioAnchored) {
                            _psMuxer.Reanchor(newAudioRtpTs: rtptime, newVideoRtpTs: null);
                            Debug.WriteLine($"[rtp-info] audio REANCHOR rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info AUDIO reanchor=rtptime={rtptime}");
                        } else {
                            _psMuxer.SetAudioBaseTs(rtptime);
                            Debug.WriteLine($"[rtp-info] audio base set to rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info AUDIO base=rtptime={rtptime}");
                        }
                    } else if (_tsMuxer != null) {
                        if (_tsMuxer.AudioAnchored) {
                            _tsMuxer.Reanchor(newAudioRtpTs: rtptime, newVideoRtpTs: null);
                            Debug.WriteLine($"[rtp-info] (ts) audio REANCHOR rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info AUDIO ts-reanchor=rtptime={rtptime}");
                        } else {
                            _tsMuxer.SetAudioBaseTs(rtptime);
                            Debug.WriteLine($"[rtp-info] (ts) audio base set to rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info AUDIO ts-base=rtptime={rtptime}");
                        }
                    } else {
                        // No combined muxer yet — wire-commit defers
                        // setup until first packets observed. Stored
                        // and replayed by TrySetupMpegPsPipeline /
                        // TrySetupMpegTsPipeline.
                        Debug.WriteLine($"[rtp-info] audio rtptime={rtptime} cached for later muxer");
                        RtcpDiagLog($"rtp-info AUDIO cached=rtptime={rtptime}");
                    }
                } else if (isVideo) {
                    _pendingVideoRtptime = rtptime;
                    if (_psMuxer != null) {
                        if (_psMuxer.VideoAnchored) {
                            _psMuxer.Reanchor(newAudioRtpTs: null, newVideoRtpTs: rtptime);
                            // Post-seek / post-rate-change: the depacketizer's
                            // sequence-number tracker would otherwise see the
                            // new server stream as a long stretch of packet loss
                            // and gate-keep MAUs until the next IDR. Clear the
                            // tracker so the first post-reanchor frame is
                            // accepted unconditionally.
                            try { videoDepacketizer?.ResetPostLossState(); } catch { }
                            Debug.WriteLine($"[rtp-info] video REANCHOR rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info VIDEO reanchor=rtptime={rtptime}");
                        } else {
                            _psMuxer.SetVideoBaseTs(rtptime);
                            Debug.WriteLine($"[rtp-info] video base set to rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info VIDEO base=rtptime={rtptime}");
                        }
                    } else if (_tsMuxer != null) {
                        if (_tsMuxer.VideoAnchored) {
                            _tsMuxer.Reanchor(newAudioRtpTs: null, newVideoRtpTs: rtptime);
                            try { videoDepacketizer?.ResetPostLossState(); } catch { }
                            Debug.WriteLine($"[rtp-info] (ts) video REANCHOR rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info VIDEO ts-reanchor=rtptime={rtptime}");
                        } else {
                            _tsMuxer.SetVideoBaseTs(rtptime);
                            Debug.WriteLine($"[rtp-info] (ts) video base set to rtptime={rtptime}");
                            RtcpDiagLog($"rtp-info VIDEO ts-base=rtptime={rtptime}");
                        }
                    } else {
                        Debug.WriteLine($"[rtp-info] video rtptime={rtptime} cached for later muxer");
                        RtcpDiagLog($"rtp-info VIDEO cached=rtptime={rtptime}");
                    }
                } else {
                    RtcpDiagLog($"rtp-info UNKNOWN-URL entry='{trimmed}'");
                }
            }
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

        /// <summary>
        /// 16-bit modular sequence comparison (RFC 1982 SerialNumberArithmetic
        /// scaled to 16 bits). Returns true if `a` is more recent than `b`,
        /// handling wrap-around.
        /// </summary>
        private static bool IsSeqGreater(ushort a, ushort b) {
            int diff = (a - b) & 0xFFFF;
            return diff != 0 && diff < 0x8000;
        }

        /// <summary>
        /// Inspect the SDP-derived <see cref="wmfPayloadDataDict"/> for an
        /// x-wmf-pf audio entry whose fmtp config is <c>audio/vnd.wave</c>
        /// (raw PCM via WMRTP). On match, parse the embedded WAVEFORMATEX
        /// and stand up <see cref="_pcmAudioSink"/>. The audioDepacketizer
        /// event handler (constructor lambda) checks this field and routes
        /// MAUs accordingly.
        ///
        /// We pick the FIRST matching PT in dictionary insertion order. The
        /// SDP loop adds entries in rtpmap-declaration order, which mirrors
        /// the m= line's PT ordering — and per the wire dump, WMPNss picks
        /// that first PT (110 in our test). If a future server flips to a
        /// later PT we'd see no audio; the fix would be a richer dispatcher
        /// keyed on observed packet PTs at runtime.
        /// </summary>
        private void TrySetupPcmAudioSink() {
            // The PCM-via-FFME pipeline is the preferred path. Only set up
            // the NAudio sink when the user has explicitly opted in via
            // env var SOFTSLED_AUDIO_VIA_NAUDIO=1.
            if (_pcmFfmeProducer != null) return;
            if (!string.Equals(Environment.GetEnvironmentVariable("SOFTSLED_AUDIO_VIA_NAUDIO"),
                               "1", StringComparison.Ordinal)) {
                Debug.WriteLine("[wmrpt-pcm-naudio] skipped — FFME path is preferred " +
                                "(set SOFTSLED_AUDIO_VIA_NAUDIO=1 to opt back into NAudio)");
                return;
            }
            if (_pcmAudioSink != null) return;  // already set up
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (entry.Type != MediaType.Audio) continue;
                if (string.IsNullOrEmpty(entry.Codec) ||
                    !entry.Codec.Equals("X-WMF-PF", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(entry.FormatParameter)) continue;

                // FormatParameter looks like:
                //   "audio/vnd.wave;codec=1;config=GUID/0/0/0/GUID/HEX;extsys=..."
                // We only care about audio/vnd.wave entries (raw PCM); the
                // application/vnd.asf variants need a different sink.
                string fp = entry.FormatParameter;
                if (fp.IndexOf("audio/vnd.wave", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Extract the config= value. AttributFmtp's GetParameter is
                // private at that scope; do a manual split here to avoid the
                // need to thread the parsed AttributFmtp through.
                string configValue = ExtractFmtpParameter(fp, "config");
                if (string.IsNullOrEmpty(configValue)) continue;

                NAudio.Wave.WaveFormat fmt =
                    WmrptPcmNAudioSink.WaveFormatExFromConfig(configValue);
                if (fmt == null) {
                    Debug.WriteLine($"[wmrpt-pcm-naudio] PT {entry.PayloadNumber} fmtp " +
                                    $"audio/vnd.wave but WAVEFORMATEX parse failed");
                    continue;
                }

                Debug.WriteLine($"[wmrpt-pcm-naudio] standing up sink for PT={entry.PayloadNumber}");
                _pcmAudioSink = new WmrptPcmNAudioSink(fmt, /*log:*/ null);
                return;
            }
            Debug.WriteLine("[wmrpt-pcm-naudio] no x-wmf-pf audio/vnd.wave PT in SDP — " +
                            "PCM sink not stood up (other codec paths may handle this stream)");
        }

        /// <summary>
        /// Stand up the FFME audio pipeline for X-WMF-PF audio/vnd.wave
        /// raw PCM streams. The depacketizer emits clean little-endian
        /// PCM samples per MAU; we feed them into an <see cref="AsfStreamProducer"/>
        /// and wrap with <see cref="AsfFfmeInputStream"/> forced to
        /// <c>s16le</c> with explicit sample_rate + channels (libavformat
        /// can't probe these from headerless PCM bytes).
        ///
        /// Replaces <see cref="TrySetupPcmAudioSink"/> as the default audio
        /// path so all media playback runs through FFME / libav.
        /// </summary>
        private void TrySetupPcmFfmePipeline() {
            if (_pcmFfmeProducer != null) return;
            if (_psMuxer != null)         return; // PS path owns audio

            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (entry.Type != MediaType.Audio) continue;
                if (string.IsNullOrEmpty(entry.Codec) ||
                    !entry.Codec.Equals("X-WMF-PF", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(entry.FormatParameter)) continue;
                if (entry.FormatParameter.IndexOf("audio/vnd.wave", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string configValue = ExtractFmtpParameter(entry.FormatParameter, "config");
                if (string.IsNullOrEmpty(configValue)) continue;

                NAudio.Wave.WaveFormat fmt =
                    WmrptPcmNAudioSink.WaveFormatExFromConfig(configValue);
                if (fmt == null) continue;

                _pcmSampleRate    = fmt.SampleRate;
                _pcmChannels      = fmt.Channels;
                _pcmBitsPerSample = fmt.BitsPerSample;

                // libavformat raw PCM input format selection — limited to
                // the integer-PCM variants WMC actually sends in this profile.
                string fmtName;
                if (fmt.BitsPerSample == 16) {
                    fmtName = "s16le"; // matches WAVEFORMATEX PCM little-endian
                } else if (fmt.BitsPerSample == 24) {
                    fmtName = "s24le";
                } else if (fmt.BitsPerSample == 8) {
                    fmtName = "u8";
                } else {
                    Debug.WriteLine($"[pcm-ffme] unsupported bits/sample {fmt.BitsPerSample} for PT={entry.PayloadNumber}");
                    return;
                }

                var extraOpts = new System.Collections.Generic.Dictionary<string, string> {
                    ["sample_rate"] = fmt.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["channels"]    = fmt.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };

                Debug.WriteLine($"[pcm-ffme] standing up s16le FFME audio pipeline " +
                                $"for PT={entry.PayloadNumber} fmt={fmtName} sr={fmt.SampleRate} ch={fmt.Channels}");
                _pcmFfmeProducer    = new AsfStreamProducer(/*log:*/ null);
                _pcmFfmeInputStream = new AsfFfmeInputStream(_pcmFfmeProducer,
                                                              /*log:*/ null,
                                                              forcedInputFormat: fmtName,
                                                              extraOptions: extraOpts);

                try {
                    string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                            "softsled-pcm-ffme-stats.log");
                    _pcmFfmeDiagLog = new System.IO.StreamWriter(
                        new System.IO.FileStream(logPath,
                            System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                        System.Text.Encoding.ASCII) { AutoFlush = true };
                    _pcmFfmeDiagLog.WriteLine("# WMRPT X-WMF-PF PCM FFME audio pipeline diagnostic log");
                    _pcmFfmeDiagLog.WriteLine($"# PT={entry.PayloadNumber} fmt={fmtName} sr={fmt.SampleRate} ch={fmt.Channels} bps={fmt.BitsPerSample}");
                    _pcmFfmeDiagLog.WriteLine("# columns: wall(s) chunks bytesPushed producerQueued producerBytesProduced producerBytesConsumed drops");
                    _pcmFfmeDiagSw.Restart();
                } catch (Exception ex) {
                    Debug.WriteLine($"[pcm-ffme] diag log open failed: {ex.Message}");
                }

                try {
                    AudioPipelineReady?.Invoke(_pcmFfmeInputStream);
                } catch (Exception ex) {
                    Debug.WriteLine($"[pcm-ffme] AudioPipelineReady handler threw: {ex.Message}");
                }
                return;
            }
        }

        /// <summary>
        /// Scan <see cref="wmfPayloadDataDict"/> for an audio entry with codec
        /// MPA (MPEG Audio over RTP, RFC 2250) and stand up
        /// <see cref="_mpaAudioSink"/>. WMPNss picks this when the source file
        /// is plain MP3 — payload type 96 with <c>rtpmap MPA/90000/N</c>, no
        /// WMRTP wrapper. Format (sample rate / channels / bitrate) is
        /// discovered lazily from the first decoded MP3 frame rather than
        /// from SDP fmtp, which is more robust against fmtp variations.
        /// </summary>
        private void TrySetupMpaAudioSink() {
            if (_mpaAudioSink != null) return;
            // The MP3-via-FFME pipeline takes priority for music files.
            // Only stand up the NAudio sink when neither FFME pipeline
            // (PS or MP3) is handling audio.
            if (_mp3FfmeProducer != null || _psMuxer != null) return;
            // Modern PCM pipeline claimed the audio stream.
            if (_pcmFfmeProducer != null) return;

            // Prefer RFC 2250 MPA if both are advertised (music-file case). If
            // only WMRTP-wrapped vnd.ms.wm-MPA is present (recorded-TV MPEG-ES
            // case), set up in WmrtpWrapped mode. Codec strings in the dict
            // are upper-cased at SDP parse time, so compare against upper.
            int rfcPt = -1, wmPt = -1;
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (entry.Type != MediaType.Audio) continue;
                if (string.IsNullOrEmpty(entry.Codec)) continue;

                if (rfcPt < 0 && entry.Codec.Equals("MPA",
                                                    StringComparison.OrdinalIgnoreCase))
                    rfcPt = entry.PayloadNumber;
                else if (wmPt < 0 && entry.Codec.Equals("VND.MS.WM-MPA",
                                                        StringComparison.OrdinalIgnoreCase))
                    wmPt = entry.PayloadNumber;
            }

            if (rfcPt >= 0) {
                Debug.WriteLine($"[mpa-rtp-naudio] standing up RFC 2250 MP3 sink " +
                                $"for PT={rfcPt}");
                _mpaAudioSink = new Mp3OverRtpAudioSink(/*log:*/ null);
                _mpaSinkMode  = MpaSinkMode.Rfc2250;
            } else if (wmPt >= 0) {
                if (_psMuxer != null) {
                    Debug.WriteLine($"[mpa-rtp-naudio] skipping WMRTP-wrapped sink " +
                                    $"for PT={wmPt} — combined MPEG-PS pipeline is active");
                } else {
                    Debug.WriteLine($"[mpa-rtp-naudio] standing up WMRTP-wrapped " +
                                    $"MPEG-audio sink for PT={wmPt} (vnd.ms.wm-MPA, " +
                                    $"audio-only — NAudio ACM Layer III decoder)");
                    _mpaAudioSink = new Mp3OverRtpAudioSink(/*log:*/ null);
                    _mpaSinkMode  = MpaSinkMode.WmrtpWrapped;
                }
            }
        }

        /// <summary>
        /// Scan <see cref="wmfPayloadDataDict"/> for a video entry with codec
        /// VND.MS.WM-MPV (WMRTP-wrapped MPEG-1/2 video, used by WMC recorded-TV
        /// MPEG-ES profiles). If present, stand up an <see cref="AsfStreamProducer"/>
        /// (used as a codec-agnostic byte-chunk producer) and the matching
        /// <see cref="AsfFfmeInputStream"/> forced to <c>"mpegvideo"</c>, then
        /// raise <see cref="VideoPipelineReady"/> so the host UI can call
        /// <c>Media.Open</c> on FFME's dispatcher.
        /// </summary>
        /// <summary>
        /// Stand up an audio-only MP3 pipeline through FFME when SDP
        /// advertises an MPA / vnd.ms.wm-MPA codec WITHOUT a matching
        /// video codec (music files). Returns true if the pipeline was
        /// created — in which case the legacy NAudio MP3 sink should be
        /// skipped. The PS pipeline takes priority above this method,
        /// so the call order in the SDP-finalize block matters.
        ///
        /// Both RFC 2250 (MPA) and WMRTP-wrapped (vnd.ms.wm-MPA) inputs
        /// reduce to the same downstream pipeline: raw MPEG audio frame
        /// bytes pushed into <c>AsfStreamProducer</c>. The difference is
        /// upstream: RFC 2250 carries a 4-byte MBZ/Frag sub-header on each
        /// RTP packet that we strip in the dispatch branch; vnd.ms.wm-MPA
        /// goes through the WMRTP depacketizer first and arrives at
        /// <c>AudioDataReady</c> as a clean MAU.
        /// </summary>
        private bool TrySetupMp3FfmePipeline() {
            if (_mp3FfmeProducer != null) return true;
            if (_psMuxer != null) return false;  // PS pipeline owns the audio
            // Modern PCM pipeline claimed the audio stream — having both
            // active causes whichever fires first in dispatch to receive
            // PCM bytes as if they were MPA, which libav rejects.
            if (_pcmFfmeProducer != null) return false;

            // Allow rolling back to the NAudio path without rebuild for
            // diagnostic purposes — flip the env var and we keep the
            // legacy behaviour for music files.
            if (string.Equals(Environment.GetEnvironmentVariable("SOFTSLED_AUDIO_VIA_NAUDIO"),
                              "1", StringComparison.Ordinal)) {
                Debug.WriteLine("[mp3-ffme] disabled via SOFTSLED_AUDIO_VIA_NAUDIO=1");
                return false;
            }

            int rfcPt = -1, wmPt = -1;
            bool hasVideoCodec = false;
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null || string.IsNullOrEmpty(entry.Codec)) continue;
                if (entry.Type == MediaType.Video) {
                    // If video is advertised, the MPV producer or PS muxer
                    // will pick it up — audio shares that pipeline.
                    if (entry.Codec.Equals("VND.MS.WM-MPV", StringComparison.OrdinalIgnoreCase) ||
                        entry.Codec.Equals("X-WMF-PF",     StringComparison.OrdinalIgnoreCase)) {
                        hasVideoCodec = true;
                    }
                    continue;
                }
                if (entry.Type != MediaType.Audio) continue;
                if (rfcPt < 0 && entry.Codec.Equals("MPA",
                                                    StringComparison.OrdinalIgnoreCase))
                    rfcPt = entry.PayloadNumber;
                else if (wmPt < 0 && entry.Codec.Equals("VND.MS.WM-MPA",
                                                        StringComparison.OrdinalIgnoreCase))
                    wmPt = entry.PayloadNumber;
            }

            if (hasVideoCodec) return false;          // video sessions use PS/MPV path
            if (rfcPt < 0 && wmPt < 0) return false;  // no MPA codec at all

            int activePt = rfcPt >= 0 ? rfcPt : wmPt;
            _mp3FfmeNeedsFrameStrip = (rfcPt >= 0);

            Debug.WriteLine($"[mp3-ffme] standing up audio-only MP3 pipeline " +
                            $"for PT={activePt} ({(rfcPt >= 0 ? "RFC 2250" : "vnd.ms.wm-MPA")})");
            _mp3FfmeProducer    = new AsfStreamProducer(/*log:*/ null);
            _mp3FfmeInputStream = new AsfFfmeInputStream(_mp3FfmeProducer,
                                                         /*log:*/ null,
                                                         forcedInputFormat: "mp3");

            try {
                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                        "softsled-mp3-ffme-stats.log");
                _mp3FfmeDiagLog = new System.IO.StreamWriter(
                    new System.IO.FileStream(logPath,
                        System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                    System.Text.Encoding.ASCII) { AutoFlush = true };
                _mp3FfmeDiagLog.WriteLine("# Audio-only MP3 pipeline diagnostic log (FFME / libav decode)");
                _mp3FfmeDiagLog.WriteLine($"# PT={activePt}  source={(rfcPt >= 0 ? "RFC 2250" : "vnd.ms.wm-MPA")}");
                _mp3FfmeDiagLog.WriteLine("# columns: wall(s) chunks bytesPushed producerQueued producerBytesProduced producerBytesConsumed drops");
                _mp3FfmeDiagSw.Restart();
            } catch (Exception ex) {
                Debug.WriteLine($"[mp3-ffme] diag log open failed: {ex.Message}");
            }

            try {
                VideoPipelineReady?.Invoke(_mp3FfmeInputStream);
            } catch (Exception ex) {
                Debug.WriteLine($"[mp3-ffme] VideoPipelineReady handler threw: {ex.Message}");
            }
            return true;
        }

        private void WriteMp3FfmeDiagRow() {
            if (_mp3FfmeDiagLog == null || _mp3FfmeProducer == null) return;
            try {
                _mp3FfmeDiagLog.WriteLine(
                    $"{_mp3FfmeDiagSw.Elapsed.TotalSeconds,7:F2} " +
                    $"{_mp3FfmeChunks,7} {_mp3FfmeBytesPushed,12} " +
                    $"{_mp3FfmeProducer.QueueDepth,5} " +
                    $"{_mp3FfmeProducer.BytesProduced,12} " +
                    $"{_mp3FfmeProducer.BytesConsumed,12} " +
                    $"{_mp3FfmeProducer.DroppedChunks,5}");
            } catch { /* ignore */ }
        }

        /// <summary>
        /// Stand up the combined MPEG-PS pipeline (video + audio muxed into
        /// one FFME input) when SDP advertises both wm-MPV video AND wm-MPA
        /// audio. Returns true if the pipeline was created (in which case
        /// the single-stream MPV producer and MPA sink should be skipped).
        /// Solves both:
        ///   * audio decode: libav handles Layer I/II/III natively
        ///     (NAudio's ACM only handles Layer III)
        ///   * A/V sync: FFME's master clock presents both streams together
        /// </summary>
        private bool TrySetupMpegPsPipeline() {
            CommitDiagLog($"TrySetupMpegPsPipeline: entered (_psMuxer null? {_psMuxer == null}, dict count={wmfPayloadDataDict.Count})");
            if (_psMuxer != null) return true;

            // Pre-wire-commit code declined here when X-WMF-PF was also
            // advertised in the SDP. Obsolete now — callers are only the
            // wire-commit FinalizePipelineSetup path, which calls us
            // only after observing actual MPA + MPV packets. The SDP
            // alternatives are irrelevant once the server's choice is
            // visible on the wire.
            int vidPt = -1, audPt = -1;
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                CommitDiagLog($"TrySetupMpegPsPipeline: dict[{kv.Key}] type={entry?.Type} codec={entry?.Codec ?? "(null)"}");
                if (entry == null || string.IsNullOrEmpty(entry.Codec)) continue;
                if (entry.Type == MediaType.Video && entry.Codec.Equals("VND.MS.WM-MPV",
                                                                       StringComparison.OrdinalIgnoreCase))
                    vidPt = entry.PayloadNumber;
                else if (entry.Type == MediaType.Audio && entry.Codec.Equals("VND.MS.WM-MPA",
                                                                             StringComparison.OrdinalIgnoreCase))
                    audPt = entry.PayloadNumber;
            }
            CommitDiagLog($"TrySetupMpegPsPipeline: search result vidPt={vidPt} audPt={audPt}");
            if (vidPt < 0 || audPt < 0) return false;

            Debug.WriteLine($"[ps-ffme] standing up MPEG-PS pipeline " +
                            $"(audio PT={audPt}, video PT={vidPt})");
            _psMuxer       = new MpegPsMuxer();
            // Apply any RTP-Info anchor values that arrived BEFORE the
            // muxer was created (the PLAY response runs at SETUP/PLAY
            // time; we now create the muxer lazily on first wire packet).
            if (_pendingAudioRtptime.HasValue) {
                _psMuxer.SetAudioBaseTs(_pendingAudioRtptime.Value);
                Debug.WriteLine($"[rtp-info] applied cached audio rtptime={_pendingAudioRtptime.Value} to new muxer");
                RtcpDiagLog($"rtp-info AUDIO replay-applied=rtptime={_pendingAudioRtptime.Value}");
            }
            if (_pendingVideoRtptime.HasValue) {
                _psMuxer.SetVideoBaseTs(_pendingVideoRtptime.Value);
                Debug.WriteLine($"[rtp-info] applied cached video rtptime={_pendingVideoRtptime.Value} to new muxer");
                RtcpDiagLog($"rtp-info VIDEO replay-applied=rtptime={_pendingVideoRtptime.Value}");
            }
            _psProducer    = new AsfStreamProducer(/*log:*/ null);
            _psInputStream = new AsfFfmeInputStream(_psProducer,
                                                    /*log:*/ null,
                                                    forcedInputFormat: "mpeg");

            // Diagnostic log for the combined pipeline. Reuses the format
            // pattern from softsled-video-mau-stats.log.
            try {
                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                        "softsled-ps-pipeline-stats.log");
                _psDiagLog = new System.IO.StreamWriter(
                    new System.IO.FileStream(logPath,
                        System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                    System.Text.Encoding.ASCII) { AutoFlush = true };
                _psDiagLog.WriteLine("# MPEG-PS combined audio+video pipeline diagnostic log");
                _psDiagLog.WriteLine($"# audio PT={audPt} (vnd.ms.wm-MPA)  video PT={vidPt} (vnd.ms.wm-MPV)");
                _psDiagLog.WriteLine($"# forcedFmt=mpeg  target=FFME");
                _psDiagLog.WriteLine("# columns: wall(s) vidMAUs audMAUs bytesPushed producerQueued producerBytesProduced producerBytesConsumed drops");
                _psDiagSw.Restart();
            } catch (Exception ex) {
                Debug.WriteLine($"[ps-ffme] diag log open failed: {ex.Message}");
            }

            try {
                VideoPipelineReady?.Invoke(_psInputStream);
            } catch (Exception ex) {
                Debug.WriteLine($"[ps-ffme] VideoPipelineReady handler threw: {ex.Message}");
            }
            return true;
        }

        private void WritePsDiagRow() {
            if (_psDiagLog == null || _psProducer == null) return;
            try {
                // Append per-stream muxer state at end of row so we can
                // confirm RTP-Info-based anchoring fired AND watch for any
                // creeping A/V offset over the session (vidPtsMs - audPtsMs).
                string muxerState = _psMuxer == null ? "no-mux" :
                    $"audBase={_psMuxer.AudioBaseTs}({(_psMuxer.AudioAnchored ? "set" : "unset")}) " +
                    $"vidBase={_psMuxer.VideoBaseTs}({(_psMuxer.VideoAnchored ? "set" : "unset")}) " +
                    $"audPts={_psMuxer.LatestAudioPtsMs:F1}ms " +
                    $"vidPts={_psMuxer.LatestVideoPtsMs:F1}ms " +
                    $"av-skew={_psMuxer.LatestAudioPtsMs - _psMuxer.LatestVideoPtsMs:+0.0;-0.0;0}ms";
                _psDiagLog.WriteLine(
                    $"{_psDiagSw.Elapsed.TotalSeconds,7:F2} " +
                    $"{_psVideoMaus,7} {_psAudioMaus,7} {_psBytesPushed,12} " +
                    $"{_psProducer.QueueDepth,5} " +
                    $"{_psProducer.BytesProduced,12} " +
                    $"{_psProducer.BytesConsumed,12} " +
                    $"{_psProducer.DroppedChunks,5}  " +
                    muxerState);
            } catch { /* ignore */ }
        }

        /// <summary>
        /// Stand up the MPEG-TS unified pipeline for modern codecs
        /// (H264 video + raw-LE PCM audio today). Replaces the dual-FFME
        /// hack for those streams — single muxed input means a single
        /// FFME / libav clock, so audio and video stay in sync naturally.
        /// </summary>
        private bool TrySetupMpegTsPipeline() {
            if (_tsMuxer != null) return true;
            if (_psMuxer != null) return false; // PS path owns the streams

            // Look up the audio PT for sample-rate / channels / bps —
            // muxer needs these to advertise the right format to libav.
            // Also harvest the per-stream RTP clock rate from rtpmap so
            // the muxer can convert RTP timestamps to canonical 90 kHz
            // PTS (x-wmf-pf advertises 1000 Hz; wm-MPA/MPV use 90 kHz).
            int sampleRate = 48000, channels = 2, bps = 16;
            int audioClockHz = 90000, videoClockHz = 90000;
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.Codec)) continue;
                if (!entry.Codec.Equals("X-WMF-PF", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(entry.FormatParameter)) continue;

                if (entry.Type == MediaType.Audio
                    && entry.FormatParameter.IndexOf("audio/vnd.wave", StringComparison.OrdinalIgnoreCase) >= 0) {
                    string configValue = ExtractFmtpParameter(entry.FormatParameter, "config");
                    if (!string.IsNullOrEmpty(configValue)) {
                        var fmt = WmrptPcmNAudioSink.WaveFormatExFromConfig(configValue);
                        if (fmt != null) {
                            sampleRate = fmt.SampleRate;
                            channels   = fmt.Channels;
                            bps        = fmt.BitsPerSample;
                        }
                    }
                    audioClockHz = entry.ClockHz;
                } else if (entry.Type == MediaType.Video
                    && entry.FormatParameter.IndexOf("video/vnd.avi", StringComparison.OrdinalIgnoreCase) >= 0
                    && entry.FormatParameter.IndexOf("codec=\"H264\"", StringComparison.OrdinalIgnoreCase) >= 0) {
                    videoClockHz = entry.ClockHz;
                }
            }

            _tsMuxer = new MpegTsMuxer();
            _tsMuxer.ConfigureVideo(MpegTsMuxer.VideoCodec.H264);
            _tsMuxer.ConfigureAudio(MpegTsMuxer.AudioCodec.BluRayLpcm, sampleRate, channels, bps);
            _tsMuxer.SetAudioClockRate((uint)audioClockHz);
            _tsMuxer.SetVideoClockRate((uint)videoClockHz);

            // Replay RTP-Info anchors if they arrived before the muxer existed.
            if (_pendingAudioRtptime.HasValue) {
                _tsMuxer.SetAudioBaseTs(_pendingAudioRtptime.Value);
                RtcpDiagLog($"rtp-info AUDIO ts-replay-applied=rtptime={_pendingAudioRtptime.Value}");
            }
            if (_pendingVideoRtptime.HasValue) {
                _tsMuxer.SetVideoBaseTs(_pendingVideoRtptime.Value);
                RtcpDiagLog($"rtp-info VIDEO ts-replay-applied=rtptime={_pendingVideoRtptime.Value}");
            }

            _tsProducer    = new AsfStreamProducer(/*log:*/ null);
            // Tell libavformat to expect MPEG-TS. The mpegts demuxer
            // probes PAT/PMT and per-PID streams from our muxed bytes.
            // No PrivateOptions needed beyond the format — extra
            // codec-override options only become necessary if the
            // private-data PCM PID isn't auto-decoded; if so we flip
            // ConfigureAudio above to BluRayLpcm and re-run.
            _tsInputStream = new AsfFfmeInputStream(_tsProducer,
                                                     /*log:*/ null,
                                                     forcedInputFormat: "mpegts");

            try {
                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                        "softsled-ts-pipeline-stats.log");
                _tsDiagLog = new System.IO.StreamWriter(
                    new System.IO.FileStream(logPath,
                        System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                    System.Text.Encoding.ASCII) { AutoFlush = true };
                _tsDiagLog.WriteLine("# MPEG-TS combined audio+video pipeline diagnostic log");
                _tsDiagLog.WriteLine($"# video=H264 (rtpClock={videoClockHz}Hz)  audio=BluRayLpcm {sampleRate}Hz {channels}ch {bps}bps (rtpClock={audioClockHz}Hz)");
                _tsDiagLog.WriteLine($"# forcedFmt=mpegts target=FFME");
                _tsDiagLog.WriteLine("# columns: wall(s) vidMAUs audMAUs bytesPushed producerQueued producerBytesProduced producerBytesConsumed drops audPts vidPts skew");
                _tsDiagSw.Restart();
            } catch (Exception ex) {
                Debug.WriteLine($"[ts-ffme] diag log open failed: {ex.Message}");
            }

            try {
                VideoPipelineReady?.Invoke(_tsInputStream);
            } catch (Exception ex) {
                Debug.WriteLine($"[ts-ffme] VideoPipelineReady handler threw: {ex.Message}");
            }
            Debug.WriteLine($"[ts-ffme] standing up MPEG-TS pipeline (H264 + LPCM-PES-private, audio cfg={sampleRate}/{channels}/{bps})");
            return true;
        }

        private void WriteTsDiagRow() {
            if (_tsDiagLog == null || _tsProducer == null || _tsMuxer == null) return;
            try {
                _tsDiagLog.WriteLine(
                    $"{_tsDiagSw.Elapsed.TotalSeconds,7:F2} " +
                    $"{_tsVideoMaus,7} {_tsAudioMaus,7} {_tsBytesPushed,12} " +
                    $"{_tsProducer.QueueDepth,5} " +
                    $"{_tsProducer.BytesProduced,12} " +
                    $"{_tsProducer.BytesConsumed,12} " +
                    $"{_tsProducer.DroppedChunks,5}  " +
                    $"audPts={_tsMuxer.LatestAudioPtsMs:F1}ms " +
                    $"vidPts={_tsMuxer.LatestVideoPtsMs:F1}ms " +
                    $"skew={_tsMuxer.LatestAudioPtsMs - _tsMuxer.LatestVideoPtsMs:+0.0;-0.0;0}ms");
            } catch { /* ignore */ }
        }

        private void TrySetupMpvVideoProducer() {
            if (_mpvVideoProducer != null) return;
            // Skip if the combined PS pipeline is already handling video.
            if (_psMuxer != null) return;
            // Modern H264 producer claimed the video stream — having both
            // pipelines active routes wire MAUs (per NalUnitReady) to
            // whichever fires first in the dispatch order, which silently
            // breaks when the server sends bytes for the OTHER codec.
            if (_h264VideoProducer != null) return;
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (entry.Type != MediaType.Video) continue;
                if (string.IsNullOrEmpty(entry.Codec)) continue;
                if (!entry.Codec.Equals("VND.MS.WM-MPV", StringComparison.OrdinalIgnoreCase))
                    continue;

                Debug.WriteLine($"[mpv-ffme] standing up MPEG-ES video producer " +
                                $"for PT={entry.PayloadNumber}");
                _mpvVideoProducer    = new AsfStreamProducer(/*log:*/ null);
                _mpvVideoInputStream = new AsfFfmeInputStream(_mpvVideoProducer,
                                                              /*log:*/ null,
                                                              forcedInputFormat: "mpegvideo");

                // Diagnostic log: lets us tell apart "depacketizer emitted
                // zero MAUs" from "FFME never consumed anything" by writing
                // periodic counters and a final summary line. Matches the
                // pattern used by softsled-audio-mau-stats.log.
                try {
                    string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                            "softsled-video-mau-stats.log");
                    _mpvDiagLog = new System.IO.StreamWriter(
                        new System.IO.FileStream(logPath,
                            System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                        System.Text.Encoding.ASCII) { AutoFlush = true };
                    _mpvDiagLog.WriteLine("# WMRPT MPEG-ES (vnd.ms.wm-MPV) video producer diagnostic log");
                    _mpvDiagLog.WriteLine($"# PT={entry.PayloadNumber} forcedFmt=mpegvideo target=FFME");
                    _mpvDiagLog.WriteLine("# columns: wall(s) MAUs syncPts bytesSubmitted producerQueued producerBytesProduced producerBytesConsumed drops");
                    _mpvDiagSw.Restart();
                } catch (Exception ex) {
                    Debug.WriteLine($"[mpv-ffme] diag log open failed: {ex.Message}");
                }

                // Fire the pipeline-ready event so the host can wire FFME.
                try {
                    VideoPipelineReady?.Invoke(_mpvVideoInputStream);
                } catch (Exception ex) {
                    Debug.WriteLine($"[mpv-ffme] VideoPipelineReady handler threw: {ex.Message}");
                }
                return;
            }
        }

        /// <summary>
        /// Scan <see cref="wmfPayloadDataDict"/> for an X-WMF-PF video PT
        /// whose fmtp config identifies the inner codec as H264. If
        /// present, stand up an <see cref="AsfStreamProducer"/> + an
        /// <see cref="AsfFfmeInputStream"/> with
        /// <c>forcedInputFormat="h264"</c>, then raise
        /// <see cref="VideoPipelineReady"/>. Used by modern WMC live-TV /
        /// DVR-MS recordings that pair PCM audio with H.264 video.
        /// </summary>
        private void TrySetupH264VideoProducer() {
            if (_h264VideoProducer != null) return;
            if (_psMuxer != null)            return; // PS pipeline is handling video
            if (_mpvVideoProducer != null)   return; // Legacy MPV path already wired

            int h264Pt = -1;
            foreach (var kv in wmfPayloadDataDict) {
                var entry = kv.Value;
                if (entry == null) continue;
                if (entry.Type != MediaType.Video) continue;
                if (string.IsNullOrEmpty(entry.Codec) ||
                    !entry.Codec.Equals("X-WMF-PF", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(entry.FormatParameter)) continue;
                if (entry.FormatParameter.IndexOf("video/vnd.avi", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (entry.FormatParameter.IndexOf("codec=\"H264\"", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                h264Pt = entry.PayloadNumber;
                break;
            }
            if (h264Pt < 0) return;

            Debug.WriteLine($"[h264-ffme] standing up H264 video producer for PT={h264Pt}");
            _h264VideoProducer    = new AsfStreamProducer(/*log:*/ null);
            _h264VideoInputStream = new AsfFfmeInputStream(_h264VideoProducer,
                                                           /*log:*/ null,
                                                           forcedInputFormat: "h264");

            try {
                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                        "softsled-h264-mau-stats.log");
                _h264DiagLog = new System.IO.StreamWriter(
                    new System.IO.FileStream(logPath,
                        System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                    System.Text.Encoding.ASCII) { AutoFlush = true };
                _h264DiagLog.WriteLine("# WMRPT X-WMF-PF H264 video producer diagnostic log");
                _h264DiagLog.WriteLine($"# PT={h264Pt} forcedFmt=h264 target=FFME");
                _h264DiagLog.WriteLine("# columns: wall(s) MAUs syncPts bytesSubmitted producerQueued producerBytesProduced producerBytesConsumed drops");
                _h264DiagSw.Restart();
            } catch (Exception ex) {
                Debug.WriteLine($"[h264-ffme] diag log open failed: {ex.Message}");
            }

            try {
                VideoPipelineReady?.Invoke(_h264VideoInputStream);
            } catch (Exception ex) {
                Debug.WriteLine($"[h264-ffme] VideoPipelineReady handler threw: {ex.Message}");
            }
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
                Debug.WriteLine($"[wire-commit] audio codec={codec}");
                CommitDiagLog($"COMMIT audio={codec} (videoSoFar={_wireVideoCodec ?? "(none)"})");
                TryFinalizePipelineSetup();
            }
        }

        private void CommitVideoPipelineForWireCodec(string codec) {
            if (codec == null) return;
            lock (_commitLock) {
                if (_wireVideoCodec != null) return;
                _wireVideoCodec = codec;
                Debug.WriteLine($"[wire-commit] video codec={codec}");
                CommitDiagLog($"COMMIT video={codec} (audioSoFar={_wireAudioCodec ?? "(none)"})");
                TryFinalizePipelineSetup();
            }
        }

        // Diagnostic log for the wire-commit / FinalizePipelineSetup /
        // TrySetupMpegPsPipeline chain — written unconditionally to
        // %TEMP%\softsled-commit-debug.log. Helps diagnose "RTP arriving
        // but FFME never opens" bugs by tracing exactly which branch
        // the pipeline-setup state machine takes.
        private System.IO.StreamWriter _commitDiagLog;
        private void CommitDiagLog(string line) {
            try {
                if (_commitDiagLog == null) {
                    string path = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
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

            string aud = _wireAudioCodec ?? "";
            string vid = _wireVideoCodec ?? "";
            bool isAudMpa = aud.Equals("VND.MS.WM-MPA", StringComparison.OrdinalIgnoreCase)
                         || aud.Equals("MPA",          StringComparison.OrdinalIgnoreCase);
            bool isVidMpv = vid.Equals("VND.MS.WM-MPV", StringComparison.OrdinalIgnoreCase);
            bool isAudPcm = aud.Equals("X-WMF-PF",     StringComparison.OrdinalIgnoreCase);
            bool isVidH264 = vid.Equals("X-WMF-PF",    StringComparison.OrdinalIgnoreCase);

            // 1. Combined PS pipeline for legacy MPA+MPV — gives FFME-clock
            //    A/V sync via a single muxed input stream. Returns false
            //    if it can't find both PTs in the SDP dict; fall through
            //    to dual-FFME in that case rather than leaving the
            //    session with no pipeline.
            CommitDiagLog($"FinalizePipelineSetup: isAudMpa={isAudMpa} isVidMpv={isVidMpv} isAudPcm={isAudPcm} isVidH264={isVidH264}");
            if (isAudMpa && isVidMpv) {
                CommitDiagLog("FinalizePipelineSetup: calling TrySetupMpegPsPipeline");
                if (TrySetupMpegPsPipeline()) {
                    CommitDiagLog("FinalizePipelineSetup: TrySetupMpegPsPipeline returned TRUE");
                    return;
                }
                CommitDiagLog("FinalizePipelineSetup: TrySetupMpegPsPipeline returned FALSE — falling back to dual FFME");
                Debug.WriteLine("[wire-commit] PS pipeline declined despite MPA+MPV wire codecs — falling back to dual FFME");
            }

            // 2. MPEG-TS unified pipeline for modern PCM + H264 — single
            //    libav clock keeps A/V in sync the same way PS does for
            //    the legacy stack. This is the forward-looking unified
            //    container; future codec combos will be added here.
            if (isAudPcm && isVidH264) {
                if (TrySetupMpegTsPipeline()) return;
                Debug.WriteLine("[wire-commit] TS pipeline declined for PCM+H264 — falling back to dual FFME");
            }

            // 3. Dual-FFME paths (used when no combined muxer matches).
            if (isAudPcm) TrySetupPcmFfmePipeline();
            else if (isAudMpa) TrySetupMp3FfmePipeline();
            // AC-3 etc. not yet implemented.

            if (isVidH264) TrySetupH264VideoProducer();
            else if (isVidMpv) TrySetupMpvVideoProducer();
        }

        /// <summary>
        /// Find <paramref name="key"/>=value in a semicolon-separated fmtp
        /// FormatParameter string. Returns the value (without quotes) or
        /// null if absent. Whitespace-tolerant.
        /// </summary>
        private static string ExtractFmtpParameter(string fmtp, string key) {
            foreach (string token in fmtp.Split(';')) {
                string t = token.Trim();
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(t.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase)) {
                    string val = t.Substring(eq + 1).Trim();
                    if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                        val = val.Substring(1, val.Length - 2);
                    return val;
                }
            }
            return null;
        }


        int rtp_count = 0; // used for statistics
        private int _vidRecvLoggedOnce;
        private int _audRecvLoggedOnce;
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
                            (uint)(e.Message.Data[packetIndex + 8]  << 24) |
                            (uint)(e.Message.Data[packetIndex + 9]  << 16) |
                            (uint)(e.Message.Data[packetIndex + 10] << 8)  |
                            (uint)(e.Message.Data[packetIndex + 11]);
                        UInt32 ntp_lsw_fractions =
                            (uint)(e.Message.Data[packetIndex + 12] << 24) |
                            (uint)(e.Message.Data[packetIndex + 13] << 16) |
                            (uint)(e.Message.Data[packetIndex + 14] << 8)  |
                            (uint)(e.Message.Data[packetIndex + 15]);
                        UInt32 rtp_timestamp_sr =
                            (uint)(e.Message.Data[packetIndex + 16] << 24) |
                            (uint)(e.Message.Data[packetIndex + 17] << 16) |
                            (uint)(e.Message.Data[packetIndex + 18] << 8)  |
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
                        bool dropsAudio = (rtcp_ssrc == _audioServerDataSsrc &&
                                           _audioServerDataSsrc != 0);
                        bool dropsVideo = (rtcp_ssrc == _videoServerDataSsrc &&
                                           _videoServerDataSsrc != 0);
                        if (dropsAudio || dropsVideo) {
                            RaiseDisconnected(new System.IO.IOException(
                                $"RTCP BYE for {(dropsAudio ? "audio" : "video")} stream " +
                                $"ssrc=0x{rtcp_ssrc:X8}"));
                        } else {
                            Debug.WriteLine($"[rtcp] BYE for unrelated ssrc=0x{rtcp_ssrc:X8}");
                        }
                    }
                    // PT 201 RR / 202 SDES / 204 APP / 207 XR — log-only.

                    packetIndex = packetIndex + ((rtcp_length + 1) * 4);
                }
                return;
            }

            if (data_received.Channel == video_data_channel || data_received.Channel == audio_data_channel) {
                // Received some Video or Audio Data on the correct channel.

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
                //                   + " PT=" + rtp_payload_type
                //                   + " Seq=" + rtp_sequence_number
                //                   + " Timestamp=" + rtp_timestamp / 90 // convert from 90kHZ clock to ms
                //                   + " SSRC=" + rtp_ssrc);

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
                    videoDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
                    return;
                }

                // Handle Audio with X-WMF-PF Payload
                if (data_received.Channel == audio_data_channel && wmfPayloadDataDict[rtp_payload_type].Codec.Equals("X-WMF-PF")) {
                    CommitAudioPipelineForWireCodec("X-WMF-PF");
                    byte[] rtp_payload = new byte[rtp_payload_len];
                    Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                    UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                    audioDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
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
                if (data_received.Channel == audio_data_channel
                    && wmfPayloadDataDict.ContainsKey(rtp_payload_type)
                    && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec,
                                     "VND.MS.WM-MPA", StringComparison.OrdinalIgnoreCase)) {
                    CommitAudioPipelineForWireCodec("VND.MS.WM-MPA");
                    byte[] rtp_payload = new byte[rtp_payload_len];
                    Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                    UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                    audioDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
                    return;
                }

                // Handle Video with VND.MS.WM-MPV Payload (WMRTP-wrapped MPEG
                // video — MPEG-1/2 ES, same recorded-TV profile). The
                // resulting MAU is one MPEG video access unit (start-code
                // prefixed). videoDepacketizer.NalUnitReady is hooked to push
                // bytes into the MPEG-ES producer when one is set up.
                if (data_received.Channel == video_data_channel
                    && wmfPayloadDataDict.ContainsKey(rtp_payload_type)
                    && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec,
                                     "VND.MS.WM-MPV", StringComparison.OrdinalIgnoreCase)) {
                    CommitVideoPipelineForWireCodec("VND.MS.WM-MPV");
                    byte[] rtp_payload = new byte[rtp_payload_len];
                    Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                    UpdateRtcpSeqTracking(isAudio: false, (ushort)rtp_sequence_number);
                    videoDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
                    return;
                }

                // Handle Audio with MPA (MPEG Audio over RTP, RFC 2250) Payload.
                // WMPNss negotiates this for plain .mp3 source files — no
                // WMRTP wrapper, 4-byte MBZ/Frag sub-header per packet.
                // Strip the sub-header and push to whichever pipeline is
                // active: FFME (preferred) or NAudio sink (legacy fallback).
                if (data_received.Channel == audio_data_channel
                    && wmfPayloadDataDict.ContainsKey(rtp_payload_type)
                    && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec,
                                     "MPA", StringComparison.OrdinalIgnoreCase)) {
                    UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                    if (_mp3FfmeProducer != null && rtp_payload_len > 4) {
                        // RFC 2250 §3.5: skip [16 bits MBZ][16 bits Frag_offset].
                        int frameLen = rtp_payload_len - 4;
                        byte[] frame = new byte[frameLen];
                        Array.Copy(e.Message.Data, rtp_payload_start + 4, frame, 0, frameLen);
                        _mp3FfmeProducer.SubmitChunk(frame);
                        _mp3FfmeChunks++;
                        _mp3FfmeBytesPushed += frameLen;
                        if ((_mp3FfmeChunks % 100) == 1) {
                            Trace.WriteLine($"[mp3-ffme] chunk#{_mp3FfmeChunks} len={frameLen} " +
                                            $"producerQ={_mp3FfmeProducer.QueueDepth}");
                            WriteMp3FfmeDiagRow();
                        }
                    } else if (_mpaAudioSink != null) {
                        _mpaAudioSink.SubmitRtpPayload(e.Message.Data,
                                                       rtp_payload_start,
                                                       rtp_payload_len);
                    }
                    return;
                }

                //// Check the payload type in the RTP packet matches the Payload Type value from the SDP
                //else if (data_received.Channel == audio_data_channel && rtp_payload_type != audio_payload) {
                //    System.Diagnostics.Debug.WriteLine("Ignoring this Audio RTP payload");
                //    return; // ignore this data
                //} else if (data_received.Channel == video_data_channel
                //           && rtp_payload_type == video_payload
                //           //&& rtp_payload_type == 2
                //           && video_codec.Equals("H264")
                //           ) {
                //    // H264 RTP Packet

                //    // If rtp_marker is '1' then this is the final transmission for this packet.
                //    // If rtp_marker is '0' we need to accumulate data with the same timestamp

                //    // ToDo - Check Timestamp
                //    // Add the RTP packet to the tempoary_rtp list until we have a complete 'Frame'

                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> nal_units = h264Payload.Process_H264_RTP_Packet(rtp_payload, rtp_marker); // this will cache the Packets until there is a Frame

                //    if (nal_units == null) {
                //        // we have not passed in enough RTP packets to make a Frame of video
                //    } else {
                //        // If we did not have a SPS and PPS in the SDP then search for the SPS and PPS
                //        // in the NALs and fire the Received_SPS_PPS event.
                //        // We assume the SPS and PPS are in the same Frame.
                //        if (h264_sps_pps_fired == false) {

                //            // Check this frame for SPS and PPS
                //            byte[] sps = null;
                //            byte[] pps = null;
                //            foreach (byte[] nal_unit in nal_units) {
                //                if (nal_unit.Length > 0) {
                //                    int nal_ref_idc = (nal_unit[0] >> 5) & 0x03;
                //                    int nal_unit_type = nal_unit[0] & 0x1F;

                //                    if (nal_unit_type == 7) {
                //                        sps = nal_unit; // SPS
                //                    }
                //                    if (nal_unit_type == 8) {
                //                        pps = nal_unit; // PPS
                //                    }
                //                }
                //            }
                //            if (sps != null && pps != null) {
                //                // Fire the Event
                //                if (Received_SPS_PPS != null) {
                //                    Received_SPS_PPS(sps, pps);
                //                }
                //                h264_sps_pps_fired = true;
                //            }
                //        }


                //        //nalHandler.ProcessFrameNalUnitsAsync(nal_units).Wait();

                //        //// we have a frame of NAL Units. Write them to the file
                //        //if (Received_NALs != null) {
                //        //    //Received_NALs(nal_units);
                //        //}
                //    }
                //} else if (data_received.Channel == video_data_channel
                //           && rtp_payload_type == video_payload
                //           && video_codec.Equals("H265")) {
                //    // H265 RTP Packet

                //    // If rtp_marker is '1' then this is the final transmission for this packet.
                //    // If rtp_marker is '0' we need to accumulate data with the same timestamp

                //    // Add the RTP packet to the tempoary_rtp list until we have a complete 'Frame'

                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> nal_units = h265Payload.Process_H265_RTP_Packet(rtp_payload, rtp_marker); // this will cache the Packets until there is a Frame

                //    if (nal_units == null) {
                //        // we have not passed in enough RTP packets to make a Frame of video
                //    } else {
                //        // If we did not have a VPS, SPS and PPS in the SDP then search for the VPS SPS and PPS
                //        // in the NALs and fire the Received_VPS_SPS_PPS event.
                //        // We assume the VPS, SPS and PPS are in the same Frame.
                //        if (h265_vps_sps_pps_fired == false) {

                //            // Check this frame for VPS, SPS and PPS
                //            byte[] vps = null;
                //            byte[] sps = null;
                //            byte[] pps = null;
                //            foreach (byte[] nal_unit in nal_units) {
                //                if (nal_unit.Length > 0) {
                //                    int nal_unit_type = (nal_unit[0] >> 1) & 0x3F;

                //                    if (nal_unit_type == 32) vps = nal_unit; // VPS
                //                    if (nal_unit_type == 33) sps = nal_unit; // SPS
                //                    if (nal_unit_type == 34) pps = nal_unit; // PPS
                //                }
                //            }
                //            if (vps != null && sps != null && pps != null) {
                //                // Fire the Event
                //                if (Received_VPS_SPS_PPS != null) {
                //                    Received_VPS_SPS_PPS(vps, sps, pps);
                //                }
                //                h265_vps_sps_pps_fired = true;
                //            }
                //        }

                //        // we have a frame of NAL Units. Write them to the file
                //        if (Received_NALs != null) {
                //            Received_NALs(nal_units);
                //        }
                //    }
                //} else if (data_received.Channel == audio_data_channel && (rtp_payload_type == 0 || rtp_payload_type == 8 || audio_codec.Equals("PCMA") || audio_codec.Equals("PCMU"))) {
                //    // G711 PCMA or G711 PCMU
                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> audio_frames = g711Payload.Process_G711_RTP_Packet(rtp_payload, rtp_marker);

                //    if (audio_frames == null) {
                //        // some error
                //    } else {
                //        // Write the audio frames to the file
                //        if (Received_G711 != null) {
                //            Received_G711(audio_codec, audio_frames);
                //        }
                //    }
                //} else if (data_received.Channel == audio_data_channel
                //            && rtp_payload_type == audio_payload
                //            && audio_codec.Equals("AMR")) {
                //    // AMR
                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> audio_frames = amrPayload.Process_AMR_RTP_Packet(rtp_payload, rtp_marker);

                //    if (audio_frames == null) {
                //        // some error
                //    } else {
                //        // Write the audio frames to the file
                //        if (Received_AMR != null) {
                //            Received_AMR(audio_codec, audio_frames);
                //        }
                //    }
                //} else if (data_received.Channel == audio_data_channel
                //           && rtp_payload_type == audio_payload
                //           && audio_codec.Equals("MPEG4-GENERIC")
                //          && aacPayload != null) {
                //    // AAC
                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> audio_frames = aacPayload.Process_AAC_RTP_Packet(rtp_payload, rtp_marker);

                //    if (audio_frames == null) {
                //        // some error
                //    } else {
                //        // Write the audio frames to the file
                //        if (Received_AAC != null) {
                //            Received_AAC(audio_codec, audio_frames, aacPayload.ObjectType, aacPayload.FrequencyIndex, aacPayload.ChannelConfiguration);
                //        }
                //    }
                //} else if (data_received.Channel == video_data_channel && rtp_payload_type == 26) {
                //    System.Diagnostics.Debug.WriteLine("No parser has been written for JPEG RTP packets. Please help write one");
                //    return; // ignore this data
                //}
                else {
                    System.Diagnostics.Debug.WriteLine("No parser for RTP payload " + rtp_payload_type);
                }
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
                            (uint)(e.Message.Data[packetIndex + 8]  << 24) |
                            (uint)(e.Message.Data[packetIndex + 9]  << 16) |
                            (uint)(e.Message.Data[packetIndex + 10] << 8)  |
                            (uint)(e.Message.Data[packetIndex + 11]);
                        UInt32 ntp_lsw_fractions =
                            (uint)(e.Message.Data[packetIndex + 12] << 24) |
                            (uint)(e.Message.Data[packetIndex + 13] << 16) |
                            (uint)(e.Message.Data[packetIndex + 14] << 8)  |
                            (uint)(e.Message.Data[packetIndex + 15]);
                        UInt32 rtp_timestamp_sr =
                            (uint)(e.Message.Data[packetIndex + 16] << 24) |
                            (uint)(e.Message.Data[packetIndex + 17] << 16) |
                            (uint)(e.Message.Data[packetIndex + 18] << 8)  |
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
                        bool dropsAudio = (rtcp_ssrc == _audioServerDataSsrc &&
                                           _audioServerDataSsrc != 0);
                        bool dropsVideo = (rtcp_ssrc == _videoServerDataSsrc &&
                                           _videoServerDataSsrc != 0);
                        if (dropsAudio || dropsVideo) {
                            RaiseDisconnected(new System.IO.IOException(
                                $"RTCP BYE for {(dropsAudio ? "audio" : "video")} stream " +
                                $"ssrc=0x{rtcp_ssrc:X8}"));
                        } else {
                            Debug.WriteLine($"[rtcp] BYE for unrelated ssrc=0x{rtcp_ssrc:X8}");
                        }
                    }
                    // PT 201 RR / 202 SDES / 204 APP / 207 XR — log-only.

                    packetIndex = packetIndex + ((rtcp_length + 1) * 4);
                }
                return;
            }

            if (data_received.Channel == video_data_channel || data_received.Channel == audio_data_channel) {
                // Received some Video or Audio Data on the correct channel.

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
                //                   + " PT=" + rtp_payload_type
                //                   + " Seq=" + rtp_sequence_number
                //                   + " Timestamp=" + rtp_timestamp / 90 // convert from 90kHZ clock to ms
                //                   + " SSRC=" + rtp_ssrc);

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
                    videoDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
                    return;
                }

                // Handle Audio with X-WMF-PF Payload
                if (data_received.Channel == audio_data_channel && wmfPayloadDataDict[rtp_payload_type].Codec.Equals("X-WMF-PF")) {
                    CommitAudioPipelineForWireCodec("X-WMF-PF");
                    byte[] rtp_payload = new byte[rtp_payload_len];
                    Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                    UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                    audioDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
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
                if (data_received.Channel == audio_data_channel
                    && wmfPayloadDataDict.ContainsKey(rtp_payload_type)
                    && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec,
                                     "VND.MS.WM-MPA", StringComparison.OrdinalIgnoreCase)) {
                    CommitAudioPipelineForWireCodec("VND.MS.WM-MPA");
                    byte[] rtp_payload = new byte[rtp_payload_len];
                    Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                    UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                    audioDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
                    return;
                }

                // Handle Video with VND.MS.WM-MPV Payload (WMRTP-wrapped MPEG
                // video — MPEG-1/2 ES, same recorded-TV profile). The
                // resulting MAU is one MPEG video access unit (start-code
                // prefixed). videoDepacketizer.NalUnitReady is hooked to push
                // bytes into the MPEG-ES producer when one is set up.
                if (data_received.Channel == video_data_channel
                    && wmfPayloadDataDict.ContainsKey(rtp_payload_type)
                    && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec,
                                     "VND.MS.WM-MPV", StringComparison.OrdinalIgnoreCase)) {
                    CommitVideoPipelineForWireCodec("VND.MS.WM-MPV");
                    byte[] rtp_payload = new byte[rtp_payload_len];
                    Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload_len);
                    UpdateRtcpSeqTracking(isAudio: false, (ushort)rtp_sequence_number);
                    videoDepacketizer.ProcessWmrptPayload(rtp_payload, rtp_payload_len, rtp_ssrc,
                                                          (ushort)rtp_sequence_number, rtp_timestamp,
                                                          rtp_marker == 1);
                    return;
                }

                // Handle Audio with MPA (MPEG Audio over RTP, RFC 2250) Payload.
                // WMPNss negotiates this for plain .mp3 source files — no
                // WMRTP wrapper, 4-byte MBZ/Frag sub-header per packet.
                // Strip the sub-header and push to whichever pipeline is
                // active: FFME (preferred) or NAudio sink (legacy fallback).
                if (data_received.Channel == audio_data_channel
                    && wmfPayloadDataDict.ContainsKey(rtp_payload_type)
                    && string.Equals(wmfPayloadDataDict[rtp_payload_type].Codec,
                                     "MPA", StringComparison.OrdinalIgnoreCase)) {
                    UpdateRtcpSeqTracking(isAudio: true, (ushort)rtp_sequence_number);
                    if (_mp3FfmeProducer != null && rtp_payload_len > 4) {
                        // RFC 2250 §3.5: skip [16 bits MBZ][16 bits Frag_offset].
                        int frameLen = rtp_payload_len - 4;
                        byte[] frame = new byte[frameLen];
                        Array.Copy(e.Message.Data, rtp_payload_start + 4, frame, 0, frameLen);
                        _mp3FfmeProducer.SubmitChunk(frame);
                        _mp3FfmeChunks++;
                        _mp3FfmeBytesPushed += frameLen;
                        if ((_mp3FfmeChunks % 100) == 1) {
                            Trace.WriteLine($"[mp3-ffme] chunk#{_mp3FfmeChunks} len={frameLen} " +
                                            $"producerQ={_mp3FfmeProducer.QueueDepth}");
                            WriteMp3FfmeDiagRow();
                        }
                    } else if (_mpaAudioSink != null) {
                        _mpaAudioSink.SubmitRtpPayload(e.Message.Data,
                                                       rtp_payload_start,
                                                       rtp_payload_len);
                    }
                    return;
                }

                //// Check the payload type in the RTP packet matches the Payload Type value from the SDP
                //else if (data_received.Channel == audio_data_channel && rtp_payload_type != audio_payload) {
                //    System.Diagnostics.Debug.WriteLine("Ignoring this Audio RTP payload");
                //    return; // ignore this data
                //} else if (data_received.Channel == video_data_channel
                //           && rtp_payload_type == video_payload
                //           //&& rtp_payload_type == 2
                //           && video_codec.Equals("H264")
                //           ) {
                //    // H264 RTP Packet

                //    // If rtp_marker is '1' then this is the final transmission for this packet.
                //    // If rtp_marker is '0' we need to accumulate data with the same timestamp

                //    // ToDo - Check Timestamp
                //    // Add the RTP packet to the tempoary_rtp list until we have a complete 'Frame'

                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> nal_units = h264Payload.Process_H264_RTP_Packet(rtp_payload, rtp_marker); // this will cache the Packets until there is a Frame

                //    if (nal_units == null) {
                //        // we have not passed in enough RTP packets to make a Frame of video
                //    } else {
                //        // If we did not have a SPS and PPS in the SDP then search for the SPS and PPS
                //        // in the NALs and fire the Received_SPS_PPS event.
                //        // We assume the SPS and PPS are in the same Frame.
                //        if (h264_sps_pps_fired == false) {

                //            // Check this frame for SPS and PPS
                //            byte[] sps = null;
                //            byte[] pps = null;
                //            foreach (byte[] nal_unit in nal_units) {
                //                if (nal_unit.Length > 0) {
                //                    int nal_ref_idc = (nal_unit[0] >> 5) & 0x03;
                //                    int nal_unit_type = nal_unit[0] & 0x1F;

                //                    if (nal_unit_type == 7) {
                //                        sps = nal_unit; // SPS
                //                    }
                //                    if (nal_unit_type == 8) {
                //                        pps = nal_unit; // PPS
                //                    }
                //                }
                //            }
                //            if (sps != null && pps != null) {
                //                // Fire the Event
                //                if (Received_SPS_PPS != null) {
                //                    Received_SPS_PPS(sps, pps);
                //                }
                //                h264_sps_pps_fired = true;
                //            }
                //        }


                //        //nalHandler.ProcessFrameNalUnitsAsync(nal_units).Wait();

                //        //// we have a frame of NAL Units. Write them to the file
                //        //if (Received_NALs != null) {
                //        //    //Received_NALs(nal_units);
                //        //}
                //    }
                //} else if (data_received.Channel == video_data_channel
                //           && rtp_payload_type == video_payload
                //           && video_codec.Equals("H265")) {
                //    // H265 RTP Packet

                //    // If rtp_marker is '1' then this is the final transmission for this packet.
                //    // If rtp_marker is '0' we need to accumulate data with the same timestamp

                //    // Add the RTP packet to the tempoary_rtp list until we have a complete 'Frame'

                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> nal_units = h265Payload.Process_H265_RTP_Packet(rtp_payload, rtp_marker); // this will cache the Packets until there is a Frame

                //    if (nal_units == null) {
                //        // we have not passed in enough RTP packets to make a Frame of video
                //    } else {
                //        // If we did not have a VPS, SPS and PPS in the SDP then search for the VPS SPS and PPS
                //        // in the NALs and fire the Received_VPS_SPS_PPS event.
                //        // We assume the VPS, SPS and PPS are in the same Frame.
                //        if (h265_vps_sps_pps_fired == false) {

                //            // Check this frame for VPS, SPS and PPS
                //            byte[] vps = null;
                //            byte[] sps = null;
                //            byte[] pps = null;
                //            foreach (byte[] nal_unit in nal_units) {
                //                if (nal_unit.Length > 0) {
                //                    int nal_unit_type = (nal_unit[0] >> 1) & 0x3F;

                //                    if (nal_unit_type == 32) vps = nal_unit; // VPS
                //                    if (nal_unit_type == 33) sps = nal_unit; // SPS
                //                    if (nal_unit_type == 34) pps = nal_unit; // PPS
                //                }
                //            }
                //            if (vps != null && sps != null && pps != null) {
                //                // Fire the Event
                //                if (Received_VPS_SPS_PPS != null) {
                //                    Received_VPS_SPS_PPS(vps, sps, pps);
                //                }
                //                h265_vps_sps_pps_fired = true;
                //            }
                //        }

                //        // we have a frame of NAL Units. Write them to the file
                //        if (Received_NALs != null) {
                //            Received_NALs(nal_units);
                //        }
                //    }
                //} else if (data_received.Channel == audio_data_channel && (rtp_payload_type == 0 || rtp_payload_type == 8 || audio_codec.Equals("PCMA") || audio_codec.Equals("PCMU"))) {
                //    // G711 PCMA or G711 PCMU
                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> audio_frames = g711Payload.Process_G711_RTP_Packet(rtp_payload, rtp_marker);

                //    if (audio_frames == null) {
                //        // some error
                //    } else {
                //        // Write the audio frames to the file
                //        if (Received_G711 != null) {
                //            Received_G711(audio_codec, audio_frames);
                //        }
                //    }
                //} else if (data_received.Channel == audio_data_channel
                //            && rtp_payload_type == audio_payload
                //            && audio_codec.Equals("AMR")) {
                //    // AMR
                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> audio_frames = amrPayload.Process_AMR_RTP_Packet(rtp_payload, rtp_marker);

                //    if (audio_frames == null) {
                //        // some error
                //    } else {
                //        // Write the audio frames to the file
                //        if (Received_AMR != null) {
                //            Received_AMR(audio_codec, audio_frames);
                //        }
                //    }
                //} else if (data_received.Channel == audio_data_channel
                //           && rtp_payload_type == audio_payload
                //           && audio_codec.Equals("MPEG4-GENERIC")
                //          && aacPayload != null) {
                //    // AAC
                //    byte[] rtp_payload = new byte[e.Message.Data.Length - rtp_payload_start]; // payload with RTP header removed
                //    System.Array.Copy(e.Message.Data, rtp_payload_start, rtp_payload, 0, rtp_payload.Length); // copy payload

                //    List<byte[]> audio_frames = aacPayload.Process_AAC_RTP_Packet(rtp_payload, rtp_marker);

                //    if (audio_frames == null) {
                //        // some error
                //    } else {
                //        // Write the audio frames to the file
                //        if (Received_AAC != null) {
                //            Received_AAC(audio_codec, audio_frames, aacPayload.ObjectType, aacPayload.FrequencyIndex, aacPayload.ChannelConfiguration);
                //        }
                //    }
                //} else if (data_received.Channel == video_data_channel && rtp_payload_type == 26) {
                //    System.Diagnostics.Debug.WriteLine("No parser has been written for JPEG RTP packets. Please help write one");
                //    return; // ignore this data
                //}
                else {
                    System.Diagnostics.Debug.WriteLine("No parser for RTP payload " + rtp_payload_type);
                }
            }
        }


        // RTSP Messages are OPTIONS, DESCRIBE, SETUP, PLAY etc
        private void Rtsp_MessageReceived(object sender, Rtsp.RtspChunkEventArgs e) {
            Rtsp.Messages.RtspResponse message = e.Message as Rtsp.Messages.RtspResponse;

            //System.Diagnostics.Debug.WriteLine("Received RTSP Message " + message.OriginalRequest.ToString());

            // If message has a 401 - Unauthorised Error, then we re-send the message with Authorization
            // using the most recently received 'realm' and 'nonce'
            if (message.IsOk == false) {
                System.Diagnostics.Debug.WriteLine("Got Error in RTSP Reply " + message.ReturnCode + " " + message.ReturnMessage);

                if (message.ReturnCode == 401 && (message.OriginalRequest.Headers.ContainsKey(RtspHeaderNames.Authorization) == true)) {
                    // the authorization failed.
                    Stop();
                    return;
                }

                //// Check if the Reply has an Authenticate header.
                //if (message.ReturnCode == 401 && message.Headers.ContainsKey(RtspHeaderNames.WWWAuthenticate)) {

                //    // Process the WWW-Authenticate header
                //    // EG:   Basic realm="AProxy"
                //    // EG:   Digest realm="AXIS_WS_ACCC8E3A0A8F", nonce="000057c3Y810622bff50b36005eb5efeae118626a161bf", stale=FALSE
                //    // EG:   Digest realm="IP Camera(21388)", nonce="534407f373af1bdff561b7b4da295354", stale="FALSE"

                //    string www_authenticate = message.Headers[RtspHeaderNames.WWWAuthenticate];
                //    string auth_params = "";

                //    if (www_authenticate.StartsWith("basic", StringComparison.InvariantCultureIgnoreCase)) {
                //        auth_type = "Basic";
                //        auth_params = www_authenticate.Substring(5);
                //    }
                //    if (www_authenticate.StartsWith("digest", StringComparison.InvariantCultureIgnoreCase)) {
                //        auth_type = "Digest";
                //        auth_params = www_authenticate.Substring(6);
                //    }

                //    string[] items = auth_params.Split(new char[] { ',' }); // NOTE, does not handle Commas in Quotes

                //    foreach (string item in items) {
                //        // Split on the = symbol and update the realm and nonce
                //        string[] parts = item.Trim().Split(new char[] { '=' }, 2); // max 2 parts in the results array
                //        if (parts.Count() >= 2 && parts[0].Trim().Equals("realm")) {
                //            realm = parts[1].Trim(new char[] { ' ', '\"' }); // trim space and quotes
                //        } else if (parts.Count() >= 2 && parts[0].Trim().Equals("nonce")) {
                //            nonce = parts[1].Trim(new char[] { ' ', '\"' }); // trim space and quotes
                //        }
                //    }

                //    System.Diagnostics.Debug.WriteLine("WWW Authorize parsed for " + auth_type + " " + realm + " " + nonce);
                //}

                RtspMessage resend_message = message.OriginalRequest.Clone() as RtspMessage;

                if (auth_type != null) {
                    AddAuthorization(resend_message, username, password, auth_type, realm, nonce, url);
                }

                rtsp_client.SendMessage(resend_message);

                return;

            }


            // If we get a reply to OPTIONS then start the Keepalive Timer and send DESCRIBE
            if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestOptions) {

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
                    keepalive_timer.Elapsed += Timer_Elapsed;
                    keepalive_timer.Interval = 20 * 1000;
                    keepalive_timer.Enabled = true;

                    // Send DESCRIBE
                    RtspRequest describe_message = new Rtsp.Messages.RtspRequestDescribe();
                    describe_message.RtspUri = new Uri(url);
                    describe_message.AddHeader(AcceptHeader);
                    describe_message.AddHeader(LanguageHeader);
                    //describe_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
                    describe_message.AddHeader(SupportedHeader);
                    describe_message.AddHeader(UserAgent);
                    if (auth_type != null) {
                        AddAuthorization(describe_message, username, password, auth_type, realm, nonce, url);
                    }
                    rtsp_client.SendMessage(describe_message);
                } else {
                    // If the Keepalive Timer was not null, the OPTIONS reply may have come from a Keepalive
                    // So no need to generate a DESCRIBE message
                    // do nothing
                }
            }


            // If we get a reply to DESCRIBE (which was our second command), then prosess SDP and send the SETUP
            if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestDescribe) {

                // Got a reply for DESCRIBE
                if (message.IsOk == false) {
                    Debug.WriteLine("Got Error in DESCRIBE Reply " + message.ReturnCode + " " + message.ReturnMessage);
                    return;
                }


                // ADDED TEMPORARILY //

                if (keepalive_timer == null) {
                    // Start a Timer to send an Keepalive RTSP command every 20 seconds
                    keepalive_timer = new System.Timers.Timer();
                    keepalive_timer.Elapsed += Timer_Elapsed;
                    keepalive_timer.Interval = 20 * 1000;
                    keepalive_timer.Enabled = true;
                }

                // END ADDED TEMPORARILY //




                // Examine the SDP

                //System.Diagnostics.Debug.WriteLine(System.Text.Encoding.UTF8.GetString(message.Data));

                Rtsp.Sdp.SdpFile sdp_data;
                String control = "";  // the "track" or "stream id"
                using (StreamReader sdp_stream = new StreamReader(new MemoryStream(message.Data))) {
                    sdp_data = Rtsp.Sdp.SdpFile.Read(sdp_stream);

                    // Find the base RTSP Server URL
                    foreach (Rtsp.Sdp.Attribut attrib in sdp_data.Attributs) {
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
                // But, a Panasonic decides to use different channels in the reply.
                int next_free_rtp_channel = 0;
                int next_free_rtcp_channel = 1;

                // Process each 'Media' Attribute in the SDP (each sub-stream)

                for (int x = 0; x < sdp_data.Medias.Count; x++) {

                    using (StreamReader sdp_stream = new StreamReader(new MemoryStream(message.Data))) {
                        sdp_data = Rtsp.Sdp.SdpFile.Read(sdp_stream);

                        // Find the base RTSP Server URL
                        foreach (Rtsp.Sdp.Attribut attrib in sdp_data.Attributs) {
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

                    if (video && video_payload != -1) continue; // have already matched a video payload. don't match another
                    if (audio && audio_payload != -1) continue; // have already matched an audio payload. don't match another

                    if (audio && (client_wants_audio == false)) continue; // client does not want audio from the RTSP server
                    if (video && (client_wants_video == false)) continue; // client does not want video from the RTSP server

                    if (video) video_uri = new Uri(control);
                    if (audio) audio_uri = new Uri(control);

                    if (audio || video) {

                        // search the attributes for control, rtpmap and fmtp
                        // (fmtp only applies to video)
                        Rtsp.Sdp.AttributFmtp fmtp = null; // holds SPS and PPS in base64 (h264 video)
                        foreach (Rtsp.Sdp.Attribut attrib in sdp_data.Medias[x].Attributs) {
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
                                fmtp = attrib as Rtsp.Sdp.AttributFmtp;
                                if (wmfPayloadDataDict.ContainsKey(fmtp.PayloadNumber)) {
                                    // Set the Format Parameter in the Payload Data Dictionary
                                    wmfPayloadDataDict[fmtp.PayloadNumber].FormatParameter = fmtp.FormatParameter;
                                    // Split the Format Parameter into Individual Segments
                                    string[] fmtpFormatParameterSegments = fmtp.FormatParameter.Split(';');
                                    // Iterate over all Format Parameter Segments
                                    foreach (string fmtpFormatParameterSegment in fmtpFormatParameterSegments) {
                                        // Split the Format Parameter Segment into Element=Data
                                        string[] fmtpFormatParameterSegmentElementData = fmtpFormatParameterSegment.Split('=');
                                        // If this is Segment is a Config Element
                                        if (fmtpFormatParameterSegmentElementData[0] == "config") {
                                            // Set the AM_MEDIA_FORMAT with this Segment Data
                                            wmfPayloadDataDict[fmtp.PayloadNumber].AM_Media_Format = new AM_Media_Format(fmtpFormatParameterSegmentElementData[1]);
                                        }

                                    }
                                } else {
                                    wmfPayloadDataDict.Add(fmtp.PayloadNumber, new WMFPayloadData {
                                        PayloadNumber = fmtp.PayloadNumber,
                                        FormatParameter = fmtp.FormatParameter
                                    });
                                    // Split the Format Parameter into Individual Segments
                                    string[] fmtpFormatParameterSegments = fmtp.FormatParameter.Split(';');
                                    // Iterate over all Format Parameter Segments
                                    foreach (string fmtpFormatParameterSegment in fmtpFormatParameterSegments) {
                                        // Split the Format Parameter Segment into Element=Data
                                        string[] fmtpFormatParameterSegmentElementData = fmtpFormatParameterSegment.Split('=');
                                        // If this is Segment is a Config Element
                                        if (fmtpFormatParameterSegmentElementData[0] == "config") {
                                            // Set the AM_MEDIA_FORMAT with this Segment Data
                                            wmfPayloadDataDict[fmtp.PayloadNumber].AM_Media_Format = new AM_Media_Format(fmtpFormatParameterSegmentElementData[1]);
                                        }

                                    }
                                }
                            }
                            if (attrib.Key.Equals("rtpmap")) {
                                Rtsp.Sdp.AttributRtpMap rtpmap = attrib as Rtsp.Sdp.AttributRtpMap;

                                // Check if the Codec Used (EncodingName) is one we support
                                //String[] valid_video_codecs = { "H264", "H265", "X-WMF-PF" };
                                String[] valid_video_codecs = { "H264", "H265", "VND.MS.WM-MPV", "X-WMF-PF" };
                                //String[] valid_audio_codecs = { "PCMA", "PCMU", "AMR", "MPA", "MPEG4-GENERIC", "X-WMF-PF" /* for aac */}; // Note some are "mpeg4-generic" lower case
                                String[] valid_audio_codecs = { "PCMA", "PCMU", "AMR", "MPA", "MPEG4-GENERIC", "VND.MS.WM-MPA", "X-WMF-PF" /* for aac */}; // Note some are "mpeg4-generic" lower case

                                //if (video && video_payload == -1 && Array.IndexOf(valid_video_codecs, rtpmap.EncodingName.ToUpper()) >= 0) {
                                // Parse the rtpmap ClockRate (string, e.g. "90000"
                                // or "1000") once and reuse for both branches.
                                // Default 90 kHz if missing/unparseable.
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
                                    video_codec = rtpmap.EncodingName.ToUpper();
                                    //video_payload = sdp_data.Medias[x].PayloadType;
                                    video_payload = rtpmap.PayloadNumber;
                                }
                                //if (audio && audio_payload == -1 && Array.IndexOf(valid_audio_codecs, rtpmap.EncodingName.ToUpper()) >= 0) {
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
                                    audio_codec = rtpmap.EncodingName.ToUpper();
                                    //audio_payload = sdp_data.Medias[x].PayloadType;
                                    audio_payload = rtpmap.PayloadNumber;
                                }
                            }
                        }

                        // Create H264 RTP Parser
                        if (video && (video_codec.Contains("H264") || video_codec.ToUpper().Contains("X-WMF-PF"))) {
                            h264Payload = new Rtsp.H264Payload();
                        }

                        // If the rtpmap contains H264 then split the fmtp to get the sprop-parameter-sets which hold the SPS and PPS in base64
                        if (video && (video_codec.Contains("H264") || video_codec.ToUpper().Contains("X-WMF-PF")) && fmtp != null) {
                            var param = Rtsp.Sdp.H264Parameters.Parse(fmtp.FormatParameter);
                            var sps_pps = param.SpropParameterSets;
                            if (sps_pps.Count() >= 2) {
                                byte[] sps = sps_pps[0];
                                byte[] pps = sps_pps[1];
                                if (Received_SPS_PPS != null) {
                                    Received_SPS_PPS(sps, pps);
                                }
                                h264_sps_pps_fired = true;
                            }
                        }

                        // Create H265 RTP Parser
                        if (video && video_codec.Contains("H265")) {
                            // TODO - check if DONL is being used
                            bool has_donl = false;
                            h265Payload = new Rtsp.H265Payload(has_donl);
                        }

                        // If the rtpmap contains H265 then split the fmtp to get the sprop-vps, sprop-sps and sprop-pps
                        // The RFC makes the VPS, SPS and PPS OPTIONAL so they may not be present. In which we pass back NULL values
                        if (video && video_codec.Contains("H265") && fmtp != null) {
                            var param = Rtsp.Sdp.H265Parameters.Parse(fmtp.FormatParameter);
                            var vps_sps_pps = param.SpropParameterSets;
                            if (vps_sps_pps.Count() >= 3) {
                                byte[] vps = vps_sps_pps[0];
                                byte[] sps = vps_sps_pps[1];
                                byte[] pps = vps_sps_pps[2];
                                if (Received_VPS_SPS_PPS != null) {
                                    Received_VPS_SPS_PPS(vps, sps, pps);
                                }
                                h265_vps_sps_pps_fired = true;
                            }
                        }

                        // Create AAC RTP Parser
                        // Example fmtp is "96 profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1490"
                        // Example fmtp is ""96 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210"
                        if (audio && audio_codec.Contains("MPEG4-GENERIC") && fmtp.GetParameter("mode").ToLower().Equals("aac-hbr")) {
                            // Extract config (eg 0x1490 or 0x1210)
                            aacPayload = new Rtsp.AACPayload(fmtp.GetParameter("config"));
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
                        setup_message.AddHeader("Buffer-Info.dlna.org: dejitter=6624000;CDB=6553600;BTM=0;TD=2000;BFR=0");
                        //setup_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
                        setup_message.AddHeader(SupportedHeader);
                        setup_message.AddHeader(UserAgent);
                        if (auth_type != null) {
                            AddAuthorization(setup_message, username, password, auth_type, realm, nonce, url);
                        }

                        // Add SETUP message to list of messages to send
                        setup_messages.Add(setup_message);

                    }
                }

                // Phase-0c: now that the SDP loop has populated wmfPayloadDataDict
                // with all rtpmap+fmtp entries, identify the audio PT the server
                // will most likely deliver and stand up a matching NAudio sink.
                // Server-picked-PT logic: pick the FIRST x-wmf-pf audio entry
                // whose fmtp config is `audio/vnd.wave` (raw PCM via WMRTP).
                // The wire dump confirmed PT=110 (first such entry) is what
                // WMPNss actually emits; if a future server picks a different
                // PT we'd see no audio and need to extend dispatch (multi-PT
                // sinks). Keep simple for now.
                // Pipeline-selection priority (first match wins; downstream
                // setups check for their predecessors and skip):
                //   1. MPEG-PS — combined wm-MPV video + wm-MPA audio
                //   2. MP3-via-FFME — audio-only MPA / vnd.ms.wm-MPA
                //   3. PCM via NAudio — x-wmf-pf vnd.wave (no FFME analogue
                //      yet; could be unified later)
                //   4. NAudio MP3 sink — legacy MPA fallback (only fires
                //      if FFME paths are disabled via env var)
                //   5. Video-only MPEG-ES — wm-MPV with no audio codec
                // No pipeline setup here. The SDP advertises multiple
                // codecs (legacy MPA/MPV + modern X-WMF-PF PCM/H264) and
                // the server picks one at PLAY time — we can't tell from
                // SDP alone. We defer pipeline setup until the first
                // wire RTP packet arrives in each direction and commit
                // based on the actual codec for that PT.
                // See CommitAudioPipelineForWireCodec /
                //     CommitVideoPipelineForWireCodec further down.

                // Send the FIRST SETUP message and remove it from the list of Setup Messages
                rtsp_client.SendMessage(setup_messages[0]);
                setup_messages.RemoveAt(0);
            }


            // If we get a reply to SETUP (which was our third command), then we
            // (i) check if the Interleaved Channel numbers have been modified by the camera (eg Panasonic cameras)
            // (ii) check if we have any more SETUP commands to send out (eg if we are doing SETUP for Video and Audio)
            // (iii) send a PLAY command if all the SETUP command have been sent
            if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestSetup) {
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
                        String multicast_address = transport.Destination;
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


                // Check if we have another SETUP command to send, then remote it from the list
                if (setup_messages.Count > 0) {
                    // send the next SETUP message, after adding in the 'session'
                    RtspRequestSetup next_setup = setup_messages[0];
                    next_setup.Session = session;
                    next_setup.AddHeader(LanguageHeader);
                    next_setup.AddHeader("Buffer-Info.dlna.org: dejitter=6624000;CDB=6553600;BTM=0;TD=2000;BFR=0");
                    //next_setup.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
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
            if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestPlay) {
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

                // Phase-0c follow-up: start sending RTCP Receiver Reports.
                // WMPNss paces ~20% real-time without RR feedback. We send
                // a minimal 8-byte RR every 1 second for each stream that
                // has a known server RTCP port. First send is immediate so
                // the server sees feedback before its dejitter buffer
                // exhausts.
                StartRtcpReceiverReports();

                System.Diagnostics.Debug.WriteLine("Got reply from Play  " + message.Command);
            }

        }

        // Flag set by Stop() so the keepalive miss-counter (and any other
        // background path that observes a socket exception) knows the
        // disconnect is user-initiated and shouldn't escalate to a
        // Disconnected event.
        private volatile bool _stopRequested;
        private int _keepaliveConsecutiveFailures;

        void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e) {
            // Send Keepalive message
            // The ONVIF Standard uses SET_PARAMETER as "an optional method to keep an RTSP session alive"
            // RFC 2326 (RTSP Standard) says "GET_PARAMETER with no entity body may be used to test client or server liveness("ping")"

            // This code uses GET_PARAMETER (unless OPTIONS report it is not supported, and then it sends OPTIONS as a keepalive)

            if (_stopRequested) return;

            try {
                if (server_supports_get_parameter) {

                    Rtsp.Messages.RtspRequest getparam_message = new Rtsp.Messages.RtspRequestGetParameter();
                    getparam_message.RtspUri = new Uri(url);
                    getparam_message.Session = session;
                    getparam_message.AddHeader(LanguageHeader);
                    //getparam_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
                    getparam_message.AddHeader(SupportedHeader);
                    getparam_message.AddHeader(UserAgent);
                    if (auth_type != null) {
                        AddAuthorization(getparam_message, username, password, auth_type, realm, nonce, url);
                    }
                    rtsp_client.SendMessage(getparam_message);

                } else {

                    Rtsp.Messages.RtspRequest options_message = new Rtsp.Messages.RtspRequestOptions();
                    options_message.RtspUri = new Uri(url);
                    options_message.AddHeader(LanguageHeader);
                    //options_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
                    options_message.AddHeader(SupportedHeader);
                    options_message.AddHeader(UserAgent);
                    if (auth_type != null) {
                        AddAuthorization(options_message, username, password, auth_type, realm, nonce, url);
                    }
                    rtsp_client.SendMessage(options_message);
                }
                // Reset miss counter on every successful send.
                System.Threading.Interlocked.Exchange(ref _keepaliveConsecutiveFailures, 0);
            } catch (Exception ex) {
                int fails = System.Threading.Interlocked.Increment(ref _keepaliveConsecutiveFailures);
                Debug.WriteLine($"[rtsp] keepalive send failed ({fails} consecutive): {ex.Message}");
                // Layer 4d: after 2 consecutive keepalive misses, treat
                // the session as disconnected. (Single misses can be
                // transient — TCP retransmit cycle, brief CPU stall, etc.)
                if (fails >= 2 && !_stopRequested) {
                    RaiseDisconnected(ex);
                }
            }
        }

        // Generate Basic or Digest Authorization
        public void AddAuthorization(RtspMessage message, string username, string password,
            string auth_type, string realm, string nonce, string url) {

            if (username == null || username.Length == 0) return;
            if (password == null || password.Length == 0) return;
            if (realm == null || realm.Length == 0) return;
            if (auth_type.Equals("Digest") && (nonce == null || nonce.Length == 0)) return;

            if (auth_type.Equals("Basic")) {
                byte[] credentials = System.Text.Encoding.UTF8.GetBytes(username + ":" + password);
                String credentials_base64 = Convert.ToBase64String(credentials);
                String basic_authorization = "Basic " + credentials_base64;

                message.Headers.Add(RtspHeaderNames.Authorization, basic_authorization);

                return;
            } else if (auth_type.Equals("Digest")) {

                string method = message.Method; // DESCRIBE, SETUP, PLAY etc

                MD5 md5 = System.Security.Cryptography.MD5.Create();
                String hashA1 = CalculateMD5Hash(md5, username + ":" + realm + ":" + password);
                String hashA2 = CalculateMD5Hash(md5, method + ":" + url);
                String response = CalculateMD5Hash(md5, hashA1 + ":" + nonce + ":" + hashA2);

                const String quote = "\"";
                String digest_authorization = "Digest username=" + quote + username + quote + ", "
                    + "realm=" + quote + realm + quote + ", "
                    + "nonce=" + quote + nonce + quote + ", "
                    + "uri=" + quote + url + quote + ", "
                    + "response=" + quote + response + quote;

                message.Headers.Add(RtspHeaderNames.Authorization, digest_authorization);

                return;
            } else {
                return;
            }

        }

        // MD5 (lower case)
        public string CalculateMD5Hash(MD5 md5_session, string input) {
            byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
            byte[] hash = md5_session.ComputeHash(inputBytes);

            StringBuilder output = new StringBuilder();
            for (int i = 0; i < hash.Length; i++) {
                output.Append(hash[i].ToString("x2"));
            }

            return output.ToString();
        }

        //public void AddWMFPayloadData()

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
                    _audioClock = new StreamClock { SSRC = senderSsrc, ClockHz = _audioClockHz };
                }
                _audioClock.AnchorNtp = ntpTimestamp;
                _audioClock.AnchorRtp = rtpTimestamp;
                _audioClock.HasAnchor = true;
            } else if (senderSsrc == _videoServerDataSsrc && _videoServerDataSsrc != 0) {
                if (_videoClock == null) {
                    _videoClock = new StreamClock { SSRC = senderSsrc, ClockHz = _videoClockHz };
                }
                _videoClock.AnchorNtp = ntpTimestamp;
                _videoClock.AnchorRtp = rtpTimestamp;
                _videoClock.HasAnchor = true;
            }
            // else: SR for an unknown SSRC (e.g. a stream we didn't SETUP) — ignore.
        }

        /// <summary>
        /// Monitor a freshly-committed sample's PTS for the spec thresholds.
        /// Called from inside the audio + video depacketizer callbacks
        /// (Layer 4f). The check is best-effort — if no SR has anchored
        /// the per-stream clock yet, we return without firing.
        /// </summary>
        private void MonitorPts(bool isAudio, uint rtpTs) {
            StreamClock clock = isAudio ? _audioClock : _videoClock;
            if (clock == null || !clock.HasAnchor) return;

            long ptsMs = clock.PtsMs(rtpTs);
            long prev = isAudio ? _lastAudioPtsMs : _lastVideoPtsMs;

            // First-sample-after-seek capture — used by Layer 4g(b).
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
        /// Layer 4g(b): once both first-sample PTS values are captured
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
        /// Layer 4g(a): start the decoder-open stopwatch the first time
        /// a sample is handed to FFME. Called from the producer-submit
        /// path of whichever pipeline commits first.
        /// </summary>
        internal void ArmDecoderOpenStopwatch() {
            if (System.Threading.Interlocked.Exchange(ref _decoderOpenArmed, 1) == 0) {
                _decoderOpenSw.Restart();
            }
        }

        /// <summary>
        /// Layer 4g(a): called by the host (via FfmeMediaController) when
        /// FFME raises MediaOpened. Stops the stopwatch and fires
        /// UNRECOVERABLE_SKEW if open took longer than 500ms.
        /// </summary>
        public void NotifyDecoderOpened() {
            if (_decoderOpenArmed == 0) return;
            if (System.Threading.Interlocked.Exchange(ref _decoderOpenFired, 1) != 0) return;
            _decoderOpenSw.Stop();
            long openMs = _decoderOpenSw.ElapsedMilliseconds;
            if (openMs > 500) {
                RaiseUnrecoverableSkew(new SkewInfo(SkewInfo.Cause.DecoderOpenTooSlow, openMs));
            }
        }

    }

    public class WMFPayloadData {
        public MediaType Type { get; set; }
        public string Codec { get; set; }
        public int PayloadNumber { get; set; }
        public string FormatParameter { get; set; }
        /// <summary>
        /// RTP timestamp clock in Hz, parsed from the rtpmap entry
        /// (e.g. <c>vnd.ms.wm-MPA/90000</c> → 90000, <c>x-wmf-pf/1000</c> → 1000).
        /// Used by container muxers to convert per-stream RTP timestamps
        /// to a canonical PTS clock. Defaults to 90000 if unknown.
        /// </summary>
        public int ClockHz { get; set; } = 90000;
        public string EncodingParameters { get; set; }

        public AM_Media_Format AM_Media_Format { get; set; }

    }

    public enum MediaType {
        Video,
        Audio
    }
}