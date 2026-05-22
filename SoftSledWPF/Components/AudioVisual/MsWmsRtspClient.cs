using Rtsp;
using Rtsp.Messages;
using Rtsp.Sdp;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Slim Microsoft Windows Media Services (WMS) RTSP client. Replaces
    /// the old IP-camera-shaped <c>RTSPClient</c> for the SoftSled use
    /// case: WMC streams content using the WMV/WMA RTP Payload Format
    /// (<c>com.microsoft.wm.rtp.asf</c> mode), where each MAU is an ASF
    /// Data Packet rather than a raw codec elementary stream.
    ///
    /// State machine:
    ///
    ///   OpenAsync():  TCP-connect → OPTIONS → DESCRIBE → parse SDP →
    ///                 extract ASF Header Object → SETUP each stream →
    ///                 fire <see cref="AsfHeaderReady"/>.
    ///   PlayAsync():  PLAY (with optional npt= range).
    ///   PauseAsync(): PAUSE.
    ///   StopAsync():  TEARDOWN + close socket.
    ///
    /// Threading: all RTSP I/O happens on the <see cref="Rtsp.RtspListener"/>'s
    /// background reader thread; <see cref="OpenAsync"/> et al expose
    /// task-based completion via per-CSeq TaskCompletionSources keyed off
    /// the listener's MessageReceived event. AvCtrl callers therefore
    /// never block the virtual-channel callback thread.
    /// </summary>
    internal sealed class MsWmsRtspClient : IDisposable {

        // WMC client identity. The original RTSPClient claimed
        // "MCExtender/1.50.X.090522.00" (Xbox 360 build); the WMC server
        // gates protocol features on this UA so we keep the same string.
        private const string DefaultUserAgent = "MCExtender/1.50.X.090522.00";

        // Required Supported: token list. Drop com.microsoft.wm.rtp.asf
        // and the server falls back to a different payload mode (raw
        // codec data instead of ASF) — that's the wrong-shape input that
        // broke the old client.
        private const string SupportedTokens =
            "com.microsoft.wm.srvppair, com.microsoft.wm.sswitch, " +
            "com.microsoft.wm.eosmsg, com.microsoft.wm.predstrm, " +
            "com.microsoft.wm.fastcache, com.microsoft.wm.locid, " +
            "com.microsoft.wm.rtp.asf, dlna.announce, dlna.rtx, " +
            "dlna.rtx-dup, com.microsoft.wm.startupprofile";

        private readonly Logger _log;
        private readonly AsfStreamProducer _asfSink;

        private string _rtspUrl;
        private RtspTcpTransport _socket;
        private RtspListener _listener;
        private string _sessionId;

        // CSeq -> awaiter for the matching RtspResponse. The base RtspListener
        // already correlates response.OriginalRequest, but it doesn't expose
        // a Task-shape wait API — we layer that on top here.
        private readonly Dictionary<int, TaskCompletionSource<RtspResponse>> _pending
            = new Dictionary<int, TaskCompletionSource<RtspResponse>>();
        private readonly object _pendingLock = new object();

        public event Action<string> Error;
        public event Action AsfHeaderReady;

        public MsWmsRtspClient(Logger log, AsfStreamProducer asfSink) {
            _log = log;
            _asfSink = asfSink ?? throw new ArgumentNullException(nameof(asfSink));
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Connect, negotiate session, extract ASF header, prepare media
        /// streams. Does NOT start playback — caller follows with
        /// <see cref="PlayAsync"/>.
        /// </summary>
        public async Task<bool> OpenAsync(string rtspUrl, CancellationToken ct) {
            if (string.IsNullOrEmpty(rtspUrl))
                throw new ArgumentNullException(nameof(rtspUrl));

            _rtspUrl = rtspUrl;
            _log?.LogInfo($"[wms-rtsp] OPEN {rtspUrl}");

            try {
                Uri uri = new Uri(rtspUrl);
                int port = uri.Port > 0 ? uri.Port : 554;

                // TCP connect. Wrap in the wire-dumper if SOFTSLED_RTSP_WIRE_DUMP=1.
                _socket = new RtspTcpTransport(uri.Host, port);
                if (!_socket.Connected) {
                    _log?.LogError("[wms-rtsp] TCP connect failed");
                    return false;
                }
                IRtspTransport transport = RtspWireDumper.MaybeWrap(_socket, _log);

                _listener = new RtspListener(transport);
                _listener.AutoReconnect = false;
                _listener.MessageReceived += OnMessageReceived;
                // DataReceived is for TCP-interleaved RTP — UDP-only mode
                // means nothing comes through this channel. Phase 1 stops here.
                _listener.Start();

                // OPTIONS first to satisfy servers that reject DESCRIBE
                // before OPTIONS, and to discover GET_PARAMETER support
                // for keepalive selection.
                RtspResponse optionsResp = await SendAsync(NewOptions(), ct).ConfigureAwait(false);
                if (optionsResp == null || !optionsResp.IsOk) {
                    _log?.LogError("[wms-rtsp] OPTIONS failed");
                    return false;
                }

                RtspResponse describeResp = await SendAsync(NewDescribe(), ct).ConfigureAwait(false);
                if (describeResp == null || !describeResp.IsOk) {
                    _log?.LogError("[wms-rtsp] DESCRIBE failed");
                    return false;
                }

                if (!ProcessDescribeResponse(describeResp)) {
                    _log?.LogError("[wms-rtsp] DESCRIBE response did not yield a usable ASF header");
                    return false;
                }

                AsfHeaderReady?.Invoke();
                _log?.LogInfo("[wms-rtsp] Phase 1 skeleton complete (SETUP/PLAY pending)");
                // SETUP + PLAY + UDP RTP pump are written in a follow-up
                // change once the DESCRIBE capture confirms ASF-header
                // strategy. Stub returns true so the surface is testable.
                return true;
            } catch (Exception ex) {
                _log?.LogError($"[wms-rtsp] OpenAsync exception: {ex.Message}");
                Error?.Invoke(ex.Message);
                return false;
            }
        }

        public Task<bool> PlayAsync(long startTimeUnits, CancellationToken ct) {
            // TODO Phase 1 follow-up: send PLAY with Range: npt=<startTime>-
            _log?.LogInfo($"[wms-rtsp] PlayAsync (start={startTimeUnits}) — stub");
            return Task.FromResult(true);
        }

        public Task PauseAsync(CancellationToken ct) {
            // TODO Phase 1 follow-up: send PAUSE
            _log?.LogInfo("[wms-rtsp] PauseAsync — stub");
            return Task.FromResult(0);
        }

        public async Task StopAsync(CancellationToken ct) {
            try {
                if (_listener != null && _socket != null && _socket.Connected) {
                    RtspRequest teardown = NewTeardown();
                    await SendAsync(teardown, ct).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                _log?.LogError($"[wms-rtsp] TEARDOWN exception: {ex.Message}");
            } finally {
                try { _listener?.Stop(); } catch { }
                try { _socket?.Close(); } catch { }
                _listener = null;
                _socket = null;
            }
        }

        // ------------------------------------------------------------------

        /// <summary>Build OPTIONS with the WMC-required headers.</summary>
        private RtspRequest NewOptions() {
            var req = new RtspRequestOptions();
            req.RtspUri = new Uri(_rtspUrl);
            ApplyCommonHeaders(req);
            return req;
        }

        /// <summary>Build DESCRIBE; same WMC headers + SDP accept.</summary>
        private RtspRequest NewDescribe() {
            var req = new RtspRequestDescribe();
            req.RtspUri = new Uri(_rtspUrl);
            req.AddHeader("Accept: application/sdp");
            req.AddHeader("Accept-Language: en-us, *;q=0.1");
            req.AddHeader("Supported: " + SupportedTokens);
            ApplyCommonHeaders(req);
            return req;
        }

        private RtspRequest NewTeardown() {
            var req = new RtspRequestTeardown();
            req.RtspUri = new Uri(_rtspUrl);
            ApplyCommonHeaders(req);
            if (!string.IsNullOrEmpty(_sessionId)) {
                req.AddHeader("Session: " + _sessionId);
            }
            return req;
        }

        private void ApplyCommonHeaders(RtspRequest req) {
            req.AddHeader("User-Agent: " + DefaultUserAgent);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Send a request and await its matching response. CSeq assignment
        /// is done by RtspListener.SendMessage; we capture it back via a
        /// CSeq-keyed completion source registered before the send.
        /// </summary>
        private Task<RtspResponse> SendAsync(RtspRequest request, CancellationToken ct) {
            var tcs = new TaskCompletionSource<RtspResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // RtspListener.SendMessage clones the request and assigns a
            // fresh CSeq to the clone. We don't see that CSeq directly
            // from here, so we register the completion source under the
            // post-send CSeq read from the cloned message — but the API
            // doesn't give us back the clone. Workaround: pre-set our own
            // CSeq high enough to avoid collision and let SendMessage
            // overwrite. The OnMessageReceived dispatch matches by
            // OriginalRequest reference identity instead of by CSeq.
            //
            // Cleaner alternative: track responses by the request object
            // ref. RtspListener stores OriginalRequest = original (the one
            // passed to SendMessage) in the response — see RtspListener.cs
            // line 254. So response.OriginalRequest == request, exact ref
            // match.
            lock (_pendingLock) {
                // Use the Object.GetHashCode of the request as the dict key
                // — fast, unique per-request.
                _pending[System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(request)] = tcs;
            }

            ct.Register(() => {
                lock (_pendingLock) {
                    _pending.Remove(System.Runtime.CompilerServices
                        .RuntimeHelpers.GetHashCode(request));
                }
                tcs.TrySetCanceled(ct);
            });

            try {
                if (!_listener.SendMessage(request)) {
                    tcs.TrySetResult(null);
                }
            } catch (Exception ex) {
                tcs.TrySetException(ex);
            }
            return tcs.Task;
        }

        private void OnMessageReceived(object sender, RtspChunkEventArgs e) {
            var resp = e.Message as RtspResponse;
            if (resp == null) return;

            int key = System.Runtime.CompilerServices
                .RuntimeHelpers.GetHashCode(resp.OriginalRequest);
            TaskCompletionSource<RtspResponse> tcs;
            lock (_pendingLock) {
                if (!_pending.TryGetValue(key, out tcs)) return;
                _pending.Remove(key);
            }

            // Capture Session header from the first response that has it;
            // SETUP responses establish it, subsequent requests echo it.
            if (string.IsNullOrEmpty(_sessionId) &&
                resp.Headers.TryGetValue("Session", out string sessionHeader)) {
                _sessionId = sessionHeader.Split(';')[0].Trim();
                _log?.LogInfo($"[wms-rtsp] Session: {_sessionId}");
            }

            tcs.TrySetResult(resp);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Parse SDP body from DESCRIBE. Identify per-stream control URL
        /// and rtpmap, then locate the ASF Header Object (Phase 0 strategy).
        /// </summary>
        private bool ProcessDescribeResponse(RtspResponse resp) {
            byte[] body = resp.Data;
            if (body == null || body.Length == 0) {
                _log?.LogError("[wms-rtsp] DESCRIBE response had no SDP body");
                return false;
            }

            SdpFile sdp;
            try {
                using (var ms = new MemoryStream(body))
                using (var sr = new StreamReader(ms)) {
                    sdp = SdpFile.Read(sr);
                }
            } catch (Exception ex) {
                _log?.LogError($"[wms-rtsp] SDP parse failed: {ex.Message}");
                return false;
            }

            _log?.LogInfo($"[wms-rtsp] SDP: {sdp.Medias.Count} media stream(s)");

            // Phase 0 strategy: try SDP-level pgmpu first, then per-media.
            // If the user's capture proves a different layout, this is the
            // single point to adjust.
            byte[] asfHeader = TryExtractAsfHeader(sdp);
            if (asfHeader == null || asfHeader.Length == 0) {
                _log?.LogError("[wms-rtsp] no a=pgmpu or a=stream:data attribute carrying ASF header — " +
                               "in-band first-MAU strategy will be needed (Phase 0 follow-up)");
                return false;
            }

            _asfSink.SubmitAsfHeader(asfHeader);
            return true;
        }

        /// <summary>
        /// Look for the ASF Header Object as base64 in either a session-
        /// level <c>a=pgmpu:data:application/vnd.ms.wms-hdr.asfv1;base64,...</c>
        /// or a per-media <c>a=stream:data:...;base64,...</c>. Returns the
        /// decoded bytes, or null if no header attribute is present (in
        /// which case the caller should fall back to in-band detection).
        /// </summary>
        private byte[] TryExtractAsfHeader(SdpFile sdp) {
            // Session-level attributes first (most common per [MS-RTSP] §2.2.1.5.1).
            byte[] fromSession = ScanAttributes(sdp.Attributs);
            if (fromSession != null) return fromSession;

            // Per-media attributes (some servers attach the header to the
            // first stream media block instead of session-level).
            foreach (var media in sdp.Medias) {
                byte[] fromMedia = ScanAttributes(media.Attributs);
                if (fromMedia != null) return fromMedia;
            }
            return null;
        }

        private byte[] ScanAttributes(IList<Attribut> attrs) {
            foreach (var a in attrs) {
                if (a == null || a.Value == null) continue;

                // a=pgmpu:data:application/vnd.ms.wms-hdr.asfv1;base64,<...>
                if (string.Equals(a.Key, "pgmpu", StringComparison.OrdinalIgnoreCase)) {
                    byte[] decoded = TryDecodeDataUrl(a.Value);
                    if (decoded != null) {
                        _log?.LogInfo($"[wms-rtsp] ASF header from pgmpu: {decoded.Length}B");
                        return decoded;
                    }
                }

                // Some MS-WMSP variants use a=stream: with embedded data:.
                if (string.Equals(a.Key, "stream", StringComparison.OrdinalIgnoreCase)
                    && a.Value.IndexOf("data:", StringComparison.OrdinalIgnoreCase) >= 0) {
                    int dataIdx = a.Value.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
                    byte[] decoded = TryDecodeDataUrl(a.Value.Substring(dataIdx));
                    if (decoded != null) {
                        _log?.LogInfo($"[wms-rtsp] ASF header from stream-data: {decoded.Length}B");
                        return decoded;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Parse a "data:..." URL (RFC 2397 subset) and return the decoded
        /// payload bytes when a base64 specifier is present.
        /// Format: <c>data:[mime];base64,XXXXX</c>
        /// </summary>
        private static byte[] TryDecodeDataUrl(string value) {
            // Strip optional "data:" prefix.
            string s = value.Trim();
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(5);
            int comma = s.IndexOf(',');
            if (comma < 0) return null;
            string meta = s.Substring(0, comma);
            string payload = s.Substring(comma + 1);
            if (meta.IndexOf("base64", StringComparison.OrdinalIgnoreCase) < 0) {
                // Non-base64 data URLs aren't expected for a binary header.
                return null;
            }
            try {
                return Convert.FromBase64String(payload);
            } catch {
                return null;
            }
        }

        // ------------------------------------------------------------------

        public void Dispose() {
            try { StopAsync(CancellationToken.None).Wait(2000); } catch { }
            _listener?.Dispose();
            (_socket as IDisposable)?.Dispose();
        }
    }
}
