﻿using AxMSTSCLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SoftSled.Components {
    class VirtualChannelDevCapsHandler {

        private Logger m_logger;
        private AxMsRdpClient7 rdpClient;

        private int DSPAServiceHandle;
        private List<string> DevCapsEnabledCapabilities = new List<string> {
            //"2D", // 2DA - Is 2D animation allowed?
            //"AN", // ANI - Is intensive animation allowed?
            //"AP", // APP - Is tray applet allowed?
            //"AR", // ARA - Is auto restart allowed?
            "AU", // AUD - Is audio allowed?
            "AU", // AUR - Is audio Non WMP?
            //"BI", // BIG - Is remote UI renderer big-endian?
            //"BL", // BLB - Is black letters box needed?
            //"CC", // CCC - Is CC rendered by the client?
            //"CD", // CDA - Is CD playback allowed?
            //"CL", // CLO - Is the close button shown?
            //"CP", // CPY - Is CD copying allowed?
            //"CR", // CRC - Is CD burning allowed?
            //"DE", // DES - Is MCE a Windows shell?
            //"DO", // DOC - Is my Documents populated?
            //"DR", // DRC - Is DVD burning allowed?
            //"DV", // DVD - Is DVD playback allowed?
            //"EX", // EXT - Are Extender Settings allowed?
            //"FP", // FPD - Is FPD allowed?
            "GD", // GDI - Is GDI renderer used?
            "H0", // H02 - Is 2 feet help allowed? 
            "H1", // H10 - Is 10 feet help allowed? 
            "HD", // HDN - Is HD content allowed by the network?
            "HD", // HDV - Is HD content allowed?
            "HT", // HTM - Is HTML supported?
            //"MA", // MAR - Are over-scan margins needed?
            "MU", // MUT - Is mute ui allowed?
            "NL", // NLZ - Is nonlinear zoom supported?
            //"ON", // ONS - Is online spotlight allowed?
            //"PH", // PHO - Are advanced photo features allowed?
            "PO", // POP - Are Pop ups allowed?
            "RE", // REM - Is input treated as if from a remote?
            "RS", // RSZ - Is raw stretched zoom supported?
            //"RU", // RUI - Is remote UI rendering supported?
            //"SC", // SCR - Is a native screensaver required?
            //"SD", // SDM - Is a screen data mode workaround needed?
            "SD", // SDN - Is SD content allowed by the network?
            "SO", // SOU - Is UI sound supported?
            //"SU", // SUP - Is RDP super blt allowed?
            //"SY", // SYN - Is transfer to a device allowed?
            //"TB", // TBA - Is a Toolbar allowed?
            //"TV", // TVS - Is a TV skin used?
			"VI", // VID - Is video allowed?
            "VI", // VIZ - Is WMP visualisation allowed?
            "VO", // VOL - Is volume UI allowed?
            "W3", // W32 - Is Win32 content allowed?
            "WE", // WE2 - Is 2 feet web content allowed? 
            "WE", // WEB - Is 10 feet web content allowed? 
            //"WI", // WID - Is wide screen enabled?
            //"WI", // WIN - Is window mode allowed?
            "ZO" // ZOM - Is video zoom mode allowed?
        };

        public VirtualChannelDevCapsHandler(Logger m_logger, AxMsRdpClient5 rdpClient) {
            this.m_logger = m_logger;
            this.rdpClient = rdpClient;
        }

        public void ProcessData(string data) {

            // Convert the incoming data to bytes
            byte[] incomingBuff = Encoding.Unicode.GetBytes(data);

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
                        rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(encapsulatedResponse));

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
                        rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(encapsulatedResponse));

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
                            case "NA":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
                                );
                                break;
                            case "PR":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    @"rtsp-rtp-udp:*:audio/x-ms-wma:DLNA.ORG_PN=WMAFULL;DLNA.ORG_PN=WMAPRO;MICROSOFT.COM_PN=WMALSL
rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3
http-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM
rtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_AVI_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3;MICROSOFT.COM_PN=MPEG4_P2_AVI_ASP_L5_AC3
rtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;MICROSOFT.COM_PN=WMVHIGH_LSL;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED
http-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL;DLNA.ORG_PN=MPEG4_P2_TS_ASP_MPEG1_L3;DLNA.ORG_PN=MPEG4_P2_TS_ASP_AC3;DLNA.ORG_PN=MPEG4_P2_TS_ASP_AC3;DLNA.ORG_PN=AVC_MP4_MP_SD_MPEG1_L3;DLNA.ORG_PN=AVC_TS_MP_HD_MPEG1_L3;DLNA.ORG_PN=AVC_MP4_MP_HD_AC3;DLNA.ORG_PN=AVC_MP4_MP_SD_AC3;DLNA.ORG_PN=AVC_TS_MP_HD_AC3"
                                );
                                break;
                            case "XT":
                                // Initialise GetStringProperty Response
                                response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
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
                        rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(encapsulatedResponse));

                    }
                    // GetDWORDProperty Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get GetDWORDProperty Data
                        int GetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int GetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int GetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string GetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetDWORDPropertyPayloadLength);

                        m_logger.LogDebug($"DEVCAPS: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")})");

                        byte[] response;

                        if (DevCapsEnabledCapabilities.Contains(GetDWORDPropertyPayloadPropertyName.Replace("\0", ""))) {
                            response = Components.DSLRCommunication.DeviceCapabilityTrueGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                        } else {
                            response = Components.DSLRCommunication.DeviceCapabilityFalseGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                        }

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the GetDWORDProperty Response
                        rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(encapsulatedResponse));

                    } else {

                        m_logger.LogDebug($"DEVCAPS: Unknown DSPA Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                } else {

                    m_logger.LogDebug($"DEVCAPS: Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

                }

            } else if (dispatchCallingConvention == 2) {

                m_logger.LogDebug($"DEVCAPS: Response {dispatchRequestHandle} not implemented");

            }
        }
    }
}
