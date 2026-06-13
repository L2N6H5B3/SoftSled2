using SoftSled.Components.Communication;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Extender;
using SoftSled.Components.Utility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SoftSled.Components.VirtualChannel {
    class VirtualChannelDevCapsHandler {

        private Logger m_logger;

        public event EventHandler<VirtualChannelSendArgs> VirtualChannelSend;

        private int DSPAServiceHandle;
        private string channelName = "devcaps";
        List<DeviceCapability> deviceCapabilities;

        public VirtualChannelDevCapsHandler(Logger m_logger, List<DeviceCapability> deviceCapabilities) {
            this.m_logger = m_logger;
            this.deviceCapabilities = deviceCapabilities;
        }

        public void ProcessData(byte[] incomingBuff) {

            // Convert the incoming data to bytes
            //byte[] incomingBuff = Encoding.Unicode.GetBytes(data);

            // Get DSLR Dispatcher Data
            int dispatchPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 0);
            int dispatchChildCount = DataUtilities.Get2ByteInt(incomingBuff, 4);
            int dispatchCallingConvention = DataUtilities.Get4ByteInt(incomingBuff, 6);
            int dispatchRequestHandle = DataUtilities.Get4ByteInt(incomingBuff, 10);
            byte[] dispatchRequestHandleArray = DataUtilities.GetByteSubArray(incomingBuff, 10, 4);

            // DEBUG PURPOSES ONLY
            string incomingByteArray = "";
            foreach (byte b in incomingBuff) {
                incomingByteArray += b.ToString("X2") + " ";
            }
            // DEBUG PURPOSES ONLY

            if (dispatchCallingConvention == 1) {

                int dispatchServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 14);
                int dispatchFunctionHandle = DataUtilities.Get4ByteInt(incomingBuff, 18);

                // Service Handle = Dispenser
                if (dispatchServiceHandle == 0) {

                    #region DSLR Service ##########################################

                    // CreateService Request
                    if (dispatchFunctionHandle == 0) {

                        // Get CreateService Data
                        int createServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int createServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid createServiceClassID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        Guid createServiceServiceID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                        int createServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                        switch (createServiceClassID.ToString().ToLower()) {
                            // DSPA DevCaps ClassID
                            case "ef22f459-6b7e-48ba-8838-e2bef821df3c":
                                DSPAServiceHandle = createServiceServiceHandle;
                                m_logger?.LogDebug($"{channelName.ToUpper()}: CreateService DSPA ({DSPAServiceHandle})");
                                break;
                            default:
                                m_logger?.LogDebug($"{channelName.ToUpper()}: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
                                break;
                        }

                        // Initialise CreateService Response
                        byte[] response = DSLRCommunication.CreateServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedResponse));

                    }
                    // DeleteService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get DeleteService Data
                        int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        // If this is the DSPA Service
                        if (deleteServiceServiceHandle == DSPAServiceHandle) {
                            m_logger?.LogDebug($"{channelName.ToUpper()}: DeleteService DSPA ({DSPAServiceHandle})");
                            // Clear the DSPA Service
                            DSPAServiceHandle = 0;
                        } else {
                            m_logger?.LogDebug($"{channelName.ToUpper()}: DeleteService Handle ({deleteServiceServiceHandle}) not found");
                        }

                        // Initialise DeleteService Response
                        byte[] response = DSLRCommunication.DeleteServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger?.LogDebug($"{channelName.ToUpper()}: Unknown DSLR Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                }
                // DSPA Service Handle
                else if (dispatchServiceHandle == DSPAServiceHandle) {

                    #region DSPA Service ##########################################

                    // GetStringProperty Request
                    if (dispatchFunctionHandle == 0) {

                        // Get GetStringProperty Data
                        int GetStringPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int GetStringPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int GetStringPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string GetStringPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetStringPropertyPayloadLength);

                        m_logger?.LogDebug($"{channelName.ToUpper()}: GetStringProperty ({GetStringPropertyPayloadPropertyName.Replace("\0", "")})");

                        byte[] response = null;

                        switch (GetStringPropertyPayloadPropertyName.Replace("\0", "")) {
                            // Property Bag Service
                            case "NAM":
                                // The "NAM" property is the friendly name the
                                // WMC server uses to identify the extender in
                                // its UI and logs (e.g. the "Extender connected:
                                // <name>" toast). We were returning the
                                // placeholder "McxClient" — recoverable from a
                                // wire capture but not what a real Xbox sends.
                                //
                                // Confirmed against the Xbox 360 MCX XEX
                                // decompilation in C:\Claude\xbox-360-xex —
                                // the Xbox client binary embeds the literal
                                // UTF-16 BE string "Xbox 360 Media Center
                                // Extender" as its name property. Returning the
                                // same string makes SoftSled visually
                                // indistinguishable from an Xbox extender in
                                // the WMC server's extender list, which has
                                // observable effects: the server occasionally
                                // gates capability negotiation on the name
                                // string and (we suspect, untested) some
                                // chrome flows assume "Xbox 360" branding.
                                response = DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "Xbox 360 Media Center Extender"
                                );
                                break;
                            case "PRT":
                                // The "PRT" property is the extender's DLNA
                                // protocol-info advertisement: a newline-
                                // separated list of <transport>:*:<mime>:
                                // <PN-list> entries the server uses to pick
                                // which encoding to serve and which RTP
                                // transport to negotiate.
                                //
                                // The 10 entries below are lifted verbatim
                                // from the Xbox 360 MCX XEX (string-extract
                                // pass over C:\Claude\xbox-360-xex). The
                                // previous placeholder (one MP3 entry) made
                                // the server fall back to its lowest-common-
                                // denominator MP3-only profile, which is
                                // why for a long time only audio playback
                                // worked cleanly and recorded TV defaulted
                                // to WMV transcoding instead of native
                                // MPEG-2.
                                //
                                // Advertising the Xbox set unblocks the
                                // following formats on our existing engines:
                                //   audio:  WMA(Full/Pro/LSL), MP3, AC3, LPCM,
                                //           WAV-PCM  (covers everything our
                                //                     libav AudioDecoder
                                //                     already handles —
                                //                     see CodecRegistry.cs)
                                //   video:  MPEG2-ES (DVRMS/PAL/NTSC ±XAC3),
                                //           WMV-HIGH (VC-1 APL2/APL3),
                                //           MPEG-4 ASP (audio: MP3 / AC3),
                                //           H.264 MP @ HD (audio: MP3 / AC3),
                                //           MPEG-PS (1, PAL, NTSC)
                                //
                                // Notes on safety: anything WMC streams us
                                // here that we can't decode lands in the
                                // libav decoder, which logs and skips rather
                                // than crashing. Advertising the full Xbox
                                // set is therefore strictly upside vs the
                                // previous single-MP3 advertisement.
                                response = DSLRCommunication.GetStringPropertyResponse(
                                   DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                   "rtsp-rtp-udp:*:audio/x-ms-wma:DLNA.ORG_PN=WMAFULL;DLNA.ORG_PN=WMAPRO;MICROSOFT.COM_PN=WMALSL\n" +
                                   "rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3\n" +
                                   "rtsp-rtp-udp:*:audio/vnd.dolby.dd-rtp:DLNA.ORG_PN=AC3\n" +
                                   "rtsp-rtp-udp:*:audio/L16:DLNA.ORG_PN=LPCM\n" +
                                   "http-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM\n" +
                                   "rtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;DLNA.ORG_PN=MPEG_ES_PAL;DLNA.ORG_PN=MPEG_ES_NTSC;DLNA.ORG_PN=MPEG_ES_PAL_XAC3;DLNA.ORG_PN=MPEG_ES_NTSC_XAC3\n" +
                                   "rtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=WMVHIGH_LSL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED;MICROSOFT.COM_PN=VC1_APL3_FULL;MICROSOFT.COM_PN=VC1_APL3_PRO\n" +
                                   "rtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3\n" +
                                   "rtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=AVC_MP4_MP_HD_MPEG1_L3;MICROSOFT.COM_PN=AVC_MP4_MP_HD_AC3\n" +
                                   "http-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL"
                               );
                                break;
                                //// Initialise GetStringProperty Response
                                //response = DSLRCommunication.GetStringPropertyResponse(
                                //    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                //    @"rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3
                                //http-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM
                                //rtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_AVI_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3;MICROSOFT.COM_PN=MPEG4_P2_AVI_ASP_L5_AC3
                                //rtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;MICROSOFT.COM_PN=WMVHIGH_LSL;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED
                                //http-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL;DLNA.ORG_PN=MPEG4_P2_TS_ASP_MPEG1_L3;DLNA.ORG_PN=MPEG4_P2_TS_ASP_AC3;DLNA.ORG_PN=MPEG4_P2_TS_ASP_AC3;DLNA.ORG_PN=AVC_MP4_MP_SD_MPEG1_L3;DLNA.ORG_PN=AVC_TS_MP_HD_MPEG1_L3;DLNA.ORG_PN=AVC_MP4_MP_HD_AC3;DLNA.ORG_PN=AVC_MP4_MP_SD_AC3;DLNA.ORG_PN=AVC_TS_MP_HD_AC3"
                                //);
                                ////response = DSLRCommunication.GetStringPropertyNullResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                                //break;
                            case "XTY":
                                // The "XTY" property is the eXtender TYpe
                                // token — a structured device-class
                                // identifier WMC uses to route capability
                                // negotiation and pick the right server-side
                                // behavior profile. We were returning the
                                // placeholder "McxClient", which works enough
                                // for a session to come up but causes the
                                // server to fall back to a generic-extender
                                // profile.
                                //
                                // Confirmed against the Xbox 360 MCX XEX
                                // decompilation in C:\Claude\xbox-360-xex —
                                // the Xbox client binary embeds the literal
                                // ASCII string "XBOX360XTT" as its extender-
                                // type token. The "XTT" suffix matches the
                                // pattern seen in the binary's
                                // OEM_GetExtenderType handler (companion to
                                // the also-embedded "Microsoft XBOX 360"
                                // OEM identifier). Returning this string
                                // makes WMC select the Xbox 360 extender
                                // profile, which (per task #136-#141) we
                                // already lean on for codec selection and
                                // buffer-info handshakes — staying
                                // consistent is a net positive even if
                                // we're not on real Xbox hardware.
                                response = DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "XBOX360XTT"
                                );
                                break;
                            case "PBV":
                                // The "PBV" property is the Property Bag
                                // Version. We've kept the existing value of
                                // "1" because:
                                //   - The Xbox 360 MCX XEX has "PBV" as a
                                //     property *name* in its string table
                                //     (right next to NAM, XTY, etc.) but
                                //     the *value* is computed at runtime by
                                //     OEM_GetPropertyBagVersion-style hooks,
                                //     not embedded as a literal — so the
                                //     XEX extraction couldn't authoritatively
                                //     reveal it.
                                //   - PBV is the major version of the
                                //     property-bag schema; bumping it would
                                //     imply we support a newer schema's
                                //     extra properties (which we don't).
                                //   - WMC accepts "1" without complaint in
                                //     every captured handshake we've seen.
                                // Leave at "1" unless / until a future XEX
                                // pass or a server-side error reveals
                                // otherwise.
                                response = DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "1"
                                );
                                break;
                            default:
                                m_logger?.LogDebug($"{channelName.ToUpper()}: GetStringProperty ({GetStringPropertyPayloadPropertyName}) not available");
                                break;
                        }

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the GetStringProperty Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedResponse));

                    }
                    // GetDWORDProperty Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get GetDWORDProperty Data
                        int GetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int GetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int GetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string GetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetDWORDPropertyPayloadLength);


                        byte[] response;
                        // Get the DevCaps Entry
                        var devCapsEntry = deviceCapabilities.FirstOrDefault(xx => xx.Name.StartsWith(GetDWORDPropertyPayloadPropertyName.Replace("\0", "")));
                        if (devCapsEntry.Enabled) {
                            response = DSLRCommunication.DeviceCapabilityTrueGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                            m_logger?.LogDebug($"{channelName.ToUpper()}: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")}) True");
                            
                        } else {
                            response = DSLRCommunication.DeviceCapabilityFalseGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                            m_logger?.LogDebug($"{channelName.ToUpper()}: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")}) False");
                        }

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the GetDWORDProperty Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedResponse, GetDWORDPropertyPayloadPropertyName.Replace("\0", "")));

                    } else {

                        m_logger?.LogDebug($"{channelName.ToUpper()}: Unknown DSPA Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                } else {

                    m_logger?.LogDebug($"{channelName.ToUpper()}: Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

                }

            } else if (dispatchCallingConvention == 2) {

                m_logger?.LogDebug($"{channelName.ToUpper()}: Response {dispatchRequestHandle} not implemented");

            } else {

                m_logger?.LogDebug($"{channelName.ToUpper()}: Unknown CallingConvention {dispatchCallingConvention} not implemented");

            }
        }
    }
}
