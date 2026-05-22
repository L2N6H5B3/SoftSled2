using SoftSled.Components.Communication;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Splash;
using SoftSled.Components.Utility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SoftSled.Components.VirtualChannel {

    /// <summary>
    /// Thin VC adapter for the MS-RRSP2 (Xbox-360-style UI) <c>splash</c>
    /// channel. Responsibilities now scoped down to:
    /// <list type="bullet">
    ///   <item><description>Send the <c>RemoteClientInformation</c>
    ///   handshake (12 B, network byte order) <b>proactively</b> as soon
    ///   as <see cref="StartHandshake"/> is called by MainWindow on VC open
    ///   — the previous behaviour of waiting for the first inbound byte
    ///   deadlocked whenever DSPA <c>BIG=False</c> (server never sends an
    ///   unsolicited kickoff in that mode).</description></item>
    ///   <item><description>Forward every inbound chunk to the
    ///   <see cref="SplashController"/>, which owns the framing
    ///   reassembler, payload dispatcher, object registry, and renderer
    ///   host. All that work runs in <c>Components/Splash/</c>.</description></item>
    /// </list>
    /// </summary>
    class VirtualChannelSplashHandler {

        private readonly Logger m_logger;
        private const string channelName = "splash";

        public event EventHandler<VirtualChannelSendArgs> VirtualChannelSend;

        private SplashController _controller;
        private bool             _handshakeSent;

        public VirtualChannelSplashHandler(Logger logger) {
            m_logger = logger;
        }

        /// <summary>Attach the controller (constructed in MainWindow once the host is in the visual tree).</summary>
        public void AttachController(SplashController controller) {
            _controller = controller;
        }

        /// <summary>
        /// Send the <c>RemoteClientInformation</c> message immediately.
        /// Call this once the splash VC has been opened, regardless of
        /// whether any bytes have arrived from the server yet — the spec
        /// (sec 2.2.1) says the client sends first.
        /// </summary>
        public void StartHandshake() {
            if (_handshakeSent) return;
            _handshakeSent = true;

            byte[] dwVersion = DataUtilities.GetInt4Byte(0x00010006);
            byte[] dwMagic   = DataUtilities.GetInt4Byte(0x19740721);
            byte[] cbSize    = DataUtilities.GetInt4Byte(4 + dwVersion.Length + dwMagic.Length);

            byte[] response = new byte[0]
                .Concat(cbSize)
                .Concat(dwVersion)
                .Concat(dwMagic)
                .ToArray();

            m_logger?.LogDebug($"{channelName.ToUpper()}: sending RemoteClientInformation (proactive)");
            VirtualChannelSend?.Invoke(this, new VirtualChannelSendArgs(channelName, response));
        }

        /// <summary>
        /// Send a raw byte run on the splash VC. Used by
        /// <see cref="SplashController"/> to emit Command + BufferInfo +
        /// payload responses back to the WMC server (e.g. the
        /// Context_ForwardMessage "callback" the spec sequence diagram
        /// requires after the server's initial batch).
        /// </summary>
        public void SendBytes(byte[] bytes) {
            if (bytes == null || bytes.Length == 0) return;
            VirtualChannelSend?.Invoke(this, new VirtualChannelSendArgs(channelName, bytes));
        }

        /// <summary>Called by MainWindow whenever the splash VC delivers bytes.</summary>
        public void ProcessData(byte[] incomingBuff) {
            if (incomingBuff == null || incomingBuff.Length == 0) return;

            // Belt-and-braces: if the VC delivered server data before
            // StartHandshake fired, send the client info now so the server
            // doesn't time out waiting for our reply. Idempotent.
            if (!_handshakeSent) StartHandshake();

            if (_controller == null) {
                m_logger?.LogDebug($"{channelName.ToUpper()}: {incomingBuff.Length} B dropped (controller not attached yet)");
                return;
            }
            _controller.PushVcData(incomingBuff);
        }
    }
}
