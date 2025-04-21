using AxMSTSCLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace SoftSled.Components {
    class VirtualChannelDevCapsHandler {

        private Logger m_logger;

        public event EventHandler<VirtualChannelSendArgs> VirtualChannelSend;

        private int DSPAServiceHandle;
        private List<string> DevCapsEnabledCapabilities = new List<string> {
            //"2DA", // 2DA - Is 2D animation allowed?
            //"ANI", // ANI - Is intensive animation allowed?
            //"APP", // APP - Is tray applet allowed?
            //"ARA", // ARA - Is auto restart allowed?
            "AUD", // AUD - Is audio allowed?
            "AUR", // AUR - Is audio Non WMP?
            //"BIG", // BIG - Is remote UI renderer big-endian?
            //"BLB", // BLB - Is black letters box needed?
            //"CCC", // CCC - Is CC rendered by the client?
            //"CDA", // CDA - Is CD playback allowed?
            //"CLO", // CLO - Is the close button shown?
            //"CPY", // CPY - Is CD copying allowed?
            //"CRC", // CRC - Is CD burning allowed?
            //"DES", // DES - Is MCE a Windows shell?
            //"DOC", // DOC - Is my Documents populated?
            //"DRC", // DRC - Is DVD burning allowed?
            //"DVD", // DVD - Is DVD playback allowed?
            "EXT", // EXT - Are Extender Settings allowed?
            //"FPD", // FPD - Is FPD allowed?
            "GDI", // GDI - Is GDI renderer used?
            "H02", // H02 - Is 2 feet help allowed? 
            "H10", // H10 - Is 10 feet help allowed? 
            "HDN", // HDN - Is HD content allowed by the network?
            "HDV", // HDV - Is HD content allowed?
            //"HTM", // HTM - Is HTML supported?
            //"MAR", // MAR - Are over-scan margins needed?
            "MUT", // MUT - Is mute ui allowed?
            //"NLZ", // NLZ - Is nonlinear zoom supported?
            //"ONS", // ONS - Is online spotlight allowed?
            "PHO", // PHO - Are advanced photo features allowed?
            "POP", // POP - Are Pop ups allowed?
            "REM", // REM - Is input treated as if from a remote?
            //"RSZ", // RSZ - Is raw stretched zoom supported?
            //"RUI", // RUI - Is remote UI rendering supported?
            "SCR", // SCR - Is a native screensaver required?
            //"SDM", // SDM - Is a screen data mode workaround needed? (Not Supported on Win7+)
            "SDN", // SDN - Is SD content allowed by the network?
            //"SOU", // SOU - Is UI sound supported?
            //"SUP", // SUP - Is RDP super blt allowed?
            //"SYN", // SYN - Is transfer to a device allowed?
            "TBA", // TBA - Is a Toolbar allowed?
            //"TVS", // TVS - Is a TV skin used?
			"VID", // VID - Is video allowed?
            //"VIZ", // VIZ - Is WMP visualisation allowed?
            //"VOL", // VOL - Is volume UI allowed?
            //"W32", // W32 - Is Win32 content allowed?
            "WE2", // WE2 - Is 2 feet web content allowed? 
            "WEB", // WEB - Is 10 feet web content allowed? 
            "WID", // WID - Is wide screen enabled?
            //"WIN", // WIN - Is window mode allowed?
            //"ZOM" // ZOM - Is video zoom mode allowed?
        };

        public VirtualChannelDevCapsHandler(Logger m_logger) {
            this.m_logger = m_logger;
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
                                m_logger.LogDebug($"DEVCAPS: CreateService DSPA ({DSPAServiceHandle})");
                                break;
                            default:
                                m_logger.LogDebug($"DEVCAPS: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
                                break;
                        }

                        // Initialise CreateService Response
                        byte[] response = Components.DSLRCommunication.CreateServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("devcaps", encapsulatedResponse));

                    }
                    // DeleteService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get DeleteService Data
                        int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        // If this is the DSPA Service
                        if (deleteServiceServiceHandle == DSPAServiceHandle) {
                            m_logger.LogDebug($"DEVCAPS: DeleteService DSPA ({DSPAServiceHandle})");
                            // Clear the DSPA Service
                            DSPAServiceHandle = 0;
                        } else {
                            m_logger.LogDebug($"DEVCAPS: DeleteService Handle ({deleteServiceServiceHandle}) not found");
                        }

                        // Initialise DeleteService Response
                        byte[] response = Components.DSLRCommunication.DeleteServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("devcaps", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger.LogDebug($"DEVCAPS: Unknown DSLR Request {dispatchFunctionHandle} not implemented");

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

                        m_logger.LogDebug($"DEVCAPS: GetStringProperty ({GetStringPropertyPayloadPropertyName.Replace("\0", "")})");

                        byte[] response = null;

                        switch (GetStringPropertyPayloadPropertyName.Replace("\0", "")) {
                            // Property Bag Service
                            case "NAM":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
                                );
                                break;
                            case "NA":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
                                );
                                break;
                            case "PRT":
                                m_logger.LogDebug($"DEVCAPS: PRT String");
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                   DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                   @"rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3"
                               );
                                //response = Components.DSLRCommunication.GetStringPropertyNullResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                                break;
                            case "PR":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    @"rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3
                                http-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM
                                rtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_AVI_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3;MICROSOFT.COM_PN=MPEG4_P2_AVI_ASP_L5_AC3
                                rtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;MICROSOFT.COM_PN=WMVHIGH_LSL;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED
                                http-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL;DLNA.ORG_PN=MPEG4_P2_TS_ASP_MPEG1_L3;DLNA.ORG_PN=MPEG4_P2_TS_ASP_AC3;DLNA.ORG_PN=MPEG4_P2_TS_ASP_AC3;DLNA.ORG_PN=AVC_MP4_MP_SD_MPEG1_L3;DLNA.ORG_PN=AVC_TS_MP_HD_MPEG1_L3;DLNA.ORG_PN=AVC_MP4_MP_HD_AC3;DLNA.ORG_PN=AVC_MP4_MP_SD_AC3;DLNA.ORG_PN=AVC_TS_MP_HD_AC3"
                                );
                                //response = Components.DSLRCommunication.GetStringPropertyNullResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                                break;
                            case "XTY":
                                m_logger.LogDebug($"DEVCAPS: XTY String");
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
                                );
                                break;
                            case "XT":
                                m_logger.LogDebug($"DEVCAPS: XTY String");
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
                                );
                                break;
                            case "PBV":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "1"
                                );
                                break;
                            case "PB":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "1"
                                );
                                break;
                            default:
                                m_logger.LogDebug($"DEVCAPS: GetStringProperty ({GetStringPropertyPayloadPropertyName}) not available");
                                break;
                        }

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the GetStringProperty Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("devcaps", encapsulatedResponse));

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
                        var devCapsEntry = DevCapsEnabledCapabilities.FirstOrDefault(xx => xx.StartsWith(GetDWORDPropertyPayloadPropertyName.Replace("\0", "")));
                        if (devCapsEntry != null) {
                            response = Components.DSLRCommunication.DeviceCapabilityTrueGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                            m_logger.LogDebug($"DEVCAPS: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")}) True");
                            
                        } else {
                            response = Components.DSLRCommunication.DeviceCapabilityFalseGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                            m_logger.LogDebug($"DEVCAPS: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")}) False");
                        }

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the GetDWORDProperty Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("devcaps", encapsulatedResponse));

                    } else {

                        m_logger.LogDebug($"DEVCAPS: Unknown DSPA Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                } else {

                    m_logger.LogDebug($"DEVCAPS: Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

                }

            } else if (dispatchCallingConvention == 2) {

                m_logger.LogDebug($"DEVCAPS: Response {dispatchRequestHandle} not implemented");

            } else {

                m_logger.LogDebug($"DEVCAPS: Unknown CallingConvention {dispatchCallingConvention} not implemented");

            }
        }
    }
}
