using AxMSTSCLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SoftSled.Components {
    class VirtualChannelDevCapsHandler {

        private Logger m_logger;
        private AxMsRdpClient7 rdpClient;

        private int DSPAServiceHandle;
        private List<string> DevCapsDisabledCapabilities = new List<string> {
            "PH", // PHO - Are advanced photo features allowed?
            //"EX", // EXT - Are Extender Settings allowed?
            "MA", // MAR - Are over-scan margins needed?
            "PO", // POP - Are Pop ups allowed?
            "ZO", // ZOM - Is video zoom mode allowed?
            "NL", // NLZ - Is nonlinear zoom supported?
            "RS", // RSZ - Is raw stretched zoom supported?
            "WI", // WID - Is wide screen enabled?
            "H1", // H10 - Is 10 feet help allowed? 
            "WE", // WEB - Is 10 feet web content allowed? 
            "H0", // H02 - Is 2 feet help allowed? 
            "WE", // WE2 - Is 2 feet web content allowed? 
            //"AUD", // AUD - Is audio allowed?
            //"AUR", // AUR - Is audio Non WMP?
            "AR", // ARA - Is auto restart allowed?
            "BL", // BLB - Is black letters box needed?
            "CC", // CCC - Is CC rendered by the client?
            "CR", // CRC - Is CD burning allowed?
            "CP", // CPY - Is CD copying allowed?
            "CD", // CDA - Is CD playback allowed?
            "CL", // CLO - Is the close button shown?
            "DR", // DRC - Is DVD burning allowed?
            "DV", // DVD - Is DVD playback allowed?
            "FP", // FPD - Is FPD allowed?
            //"GD", // GDI - Is GDI renderer used?
            //"HDV", // HDV - Is HD content allowed?
            //"HDN", // HDN - Is HD content allowed by the network?
            //"SD", // SDN - Is SD content allowed by the network?
            //"RE", // REM - Is input treated as if from a remote?
            "AN", // ANI - Is intensive animation allowed?
            "2D", // 2DA - Is 2D animation allowed?
            "HT", // HTM - Is HTML supported?
            "DE", // DES - Is MCE a Windows shell?
            "DO", // DOC - Is my Documents populated?
            "SC", // SCR - Is a native screensaver required?
            "ON", // ONS - Is online spotlight allowed?
            //"SU", // SUP - Is RDP super bit allowed?
            "BI", // BIG - Is remote UI renderer big-endian?
            "RU", // RUI - Is remote UI rendering supported?
            "SD", // SDM - Is a screen data mode workaround needed?
            "TB", // TBA - Is a Toolbar allowed?
            "SY", // SYN - Is transfer to a device allowed?
            "AP", // APP - Is tray applet allowed?
            "TV", // TVS - Is a TV skin used?
            //"SO", // SOU - Is UI sound supported?
            //"VID", // VID - Is video allowed?
            "W3", // W32 - Is Win32 content allowed?
            "WI", // WIN - Is window mode allowed?
            //"VIZ", // VIZ - Is WMP visualisation allowed?
            //"VO", // VOL - Is volume UI allowed?
            //"MU" // MUT - Is mute ui allowed?
        };

        public VirtualChannelDevCapsHandler(Logger m_logger, AxMsRdpClient7 rdpClient) {
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
            int dispatchServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 14);
            int dispatchFunctionHandle = DataUtilities.Get4ByteInt(incomingBuff, 18);

            // DEBUG PURPOSES ONLY
            string incomingByteArray = "";
            foreach (byte b in incomingBuff) {
                incomingByteArray += b.ToString("X2") + " ";
            }
            // DEBUG PURPOSES ONLY

            // Service Handle = Dispenser
            if (dispatchServiceHandle == 0) {

                #region DSLR Service ##########################################

                // CreateService Request
                if (dispatchFunctionHandle == 0) {

                    // Get CreateService Data
                    int createServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int createServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid createServiceClassID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid createServiceServiceID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                    int createServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                    m_logger.LogDebug("DEVCAPS: Request CreateService " + createServiceServiceHandle);

                    switch (createServiceClassID.ToString().ToLower()) {
                        // DSPA DevCaps ClassID
                        case "ef22f459-6b7e-48ba-8838-e2bef821df3c":
                            DSPAServiceHandle = createServiceServiceHandle;
                            break;
                        default:
                            System.Diagnostics.Debug.WriteLine($"DEVCAPS: DSLR Service ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
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

                    m_logger.LogDebug("DEVCAPS: Sent Response CreateService " + dispatchRequestHandle);
                }
                // DeleteService Request
                else if (dispatchFunctionHandle == 2) {

                    // Get DeleteService Data
                    int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid deleteServiceClassID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid deleteServiceServiceID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                    int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                    m_logger.LogDebug("DEVCAPS: Request DeleteService " + deleteServiceServiceHandle);


                    // Send the DeleteService Response
                    //rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(response));

                    m_logger.LogDebug("DEVCAPS: Sent Response DeleteService " + dispatchRequestHandle);
                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"DEVCAPS: Unknown DSLR Request {dispatchFunctionHandle} not implemented");

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

                    m_logger.LogDebug("DEVCAPS: Request GetStringProperty " + GetStringPropertyPayloadPropertyName.Replace("\0", ""));

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
                                "rtsp-rtp-udp:*:audio/x-ms-wma:DLNA.ORG_PN=WMAFULL;DLNA.ORG_PN=WMAPRO;MICROSOFT.COM_PN=WMALSL" +
                                "rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3" +
                                "http-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM" +
                                "rtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2" +
                                "rtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;MICROSOFT.COM_PN=WMVHIGH_LSL;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED" +
                                "http-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL"
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
                            Debug.WriteLine("DEVCAPS: DSPA GetStringProperty PropertyValue not supported");
                            break;
                    }

                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the GetStringProperty Response
                    rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("DEVCAPS: Sent Response GetStringProperty " + GetStringPropertyPayloadPropertyName.Replace("\0", ""));

                }
                // GetDWORDProperty Request
                else if (dispatchFunctionHandle == 2) {

                    // Get GetDWORDProperty Data
                    int GetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int GetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int GetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    string GetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetDWORDPropertyPayloadLength);

                    m_logger.LogDebug("DEVCAPS: Request GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName.Replace("\0", ""));

                    byte[] response;

                    if (DevCapsDisabledCapabilities.Contains(GetDWORDPropertyPayloadPropertyName.Replace("\0", ""))) {
                        response = Components.DSLRCommunication.DeviceCapabilityFalseGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                    } else {
                        response = Components.DSLRCommunication.DeviceCapabilityTrueGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                    }

                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the GetDWORDProperty Response
                    rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("DEVCAPS: Sent Response GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName.Replace("\0", ""));

                }
                //// SetDWORDProperty Request
                //else if (dispatchFunctionHandle == 3) {

                //    // Get SetDWORDProperty Data
                //    int SetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                //    int SetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                //    int SetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                //    string SetDWORDPropertyPayloadPropertyName = GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, SetDWORDPropertyPayloadLength);
                //    int SetDWORDPropertyPayloadPropertyValue = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + SetDWORDPropertyPayloadLength);

                //    switch (SetDWORDPropertyPayloadPropertyName) {
                //        case "IsMuted":

                //            m_logger.LogDebug("DEVCAPS: Request SetDWORDProperty " + SetDWORDPropertyPayloadPropertyName);

                //            // Initialise SetDWORDProperty Response
                //            byte[] response = VChan.DSLR.SetDWORDPropertyResponse(
                //                DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                //            );
                //            // Encapsulate the Response (Doesn't seem to work without this?)
                //            byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                //            // Send the SetDWORDProperty Response
                //            rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(encapsulatedResponse));

                //            m_logger.LogDebug("DEVCAPS: Sent Response SetDWORDProperty " + SetDWORDPropertyPayloadPropertyName);

                //            break;
                //    }
                //}
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"DEVCAPS: Unknown DSPA Request {dispatchFunctionHandle} not implemented");

                }

                #endregion ####################################################

            } else {

                System.Diagnostics.Debug.WriteLine($"DEVCAPS: Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

            }
        }
    }
}
