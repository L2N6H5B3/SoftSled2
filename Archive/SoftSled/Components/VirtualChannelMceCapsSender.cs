using System;
using System.Text;

namespace SoftSled.Components {
    class VirtualChannelMceCapsSender {

        private Logger m_logger;

        public event EventHandler<VirtualChannelSendArgs> VirtualChannelSend;

        private int DSPAServiceHandle;
        private string channelName = "splash";

        private int StubRequestHandleIter = 0;
        private int StubServiceHandleIter = 0;

        public VirtualChannelMceCapsSender(Logger m_logger) {
            this.m_logger = m_logger;
        }

        public void ConfigureDSPA() {
            byte[] createServiceClassID = DataUtilities.GuidToArray(new Guid("EF22F459-6B7E-48ba-8838-E2BEF821DF3C"));
            byte[] createServiceServiceID = DataUtilities.GuidToArray(new Guid("1EEEDA73-2B68-4d6f-8041-52336CF46072"));
            byte[] createServiceRequest = Components.DSLRCommunication.CreateServiceRequest(StubRequestHandleIter, createServiceClassID, createServiceServiceID, 0);

            StubRequestHandleIter++;

            // Encapsulate the Request (Doesn't seem to work without this?)
            byte[] encapsulatedRequest = Components.DSLRCommunication.Encapsulate(createServiceRequest);

            // Send the GetStringProperty Response
            VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedRequest));

            //switch (createServiceClassID.ToString().ToLower()) {
            //    // DSPA DevCaps ClassID
            //    case "ef22f459-6b7e-48ba-8838-e2bef821df3c":
            //        DSPAServiceHandle = createServiceServiceHandle;
            //        m_logger.LogDebug($"{channelName.ToUpper()}: CreateService DSPA ({DSPAServiceHandle})");
            //        break;
            //    default:
            //        m_logger.LogDebug($"{channelName.ToUpper()}: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
            //        break;
            //}

            //// Initialise CreateService Response
            //byte[] response = Components.DSLRCommunication.CreateServiceResponse(
            //    DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
            //);
            //// Encapsulate the Response (Doesn't seem to work without this?)
            //byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

            //// Send the CreateService Response
            //VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedResponse));
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
                                m_logger.LogDebug($"{channelName.ToUpper()}: CreateService DSPA ({DSPAServiceHandle})");
                                break;
                            default:
                                m_logger.LogDebug($"{channelName.ToUpper()}: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
                                break;
                        }

                        // Initialise CreateService Response
                        byte[] response = Components.DSLRCommunication.CreateServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

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
                            m_logger.LogDebug($"{channelName.ToUpper()}: DeleteService DSPA ({DSPAServiceHandle})");
                            // Clear the DSPA Service
                            DSPAServiceHandle = 0;
                        } else {
                            m_logger.LogDebug($"{channelName.ToUpper()}: DeleteService Handle ({deleteServiceServiceHandle}) not found");
                        }

                        // Initialise DeleteService Response
                        byte[] response = Components.DSLRCommunication.DeleteServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger.LogDebug($"{channelName.ToUpper()}: Unknown DSLR Request {dispatchFunctionHandle} not implemented");

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

                        m_logger.LogDebug($"{channelName.ToUpper()}: GetStringProperty ({GetStringPropertyPayloadPropertyName.Replace("\0", "")})");

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
                                m_logger.LogDebug($"{channelName.ToUpper()}: GetStringProperty ({GetStringPropertyPayloadPropertyName}) not available");
                                break;
                        }

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

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

                        m_logger.LogDebug($"{channelName.ToUpper()}: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")})");

                        byte[] response;

                        //if (DevCapsDisabledCapabilities.Contains(GetDWORDPropertyPayloadPropertyName.Replace("\0", ""))) {
                            //response = Components.DSLRCommunication.DeviceCapabilityFalseGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                        //} else {
                            response = Components.DSLRCommunication.DeviceCapabilityTrueGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                        //}

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the GetDWORDProperty Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs(channelName, encapsulatedResponse));

                    } else {

                        m_logger.LogDebug($"{channelName.ToUpper()}: Unknown DSPA Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                } else {

                    m_logger.LogDebug($"{channelName.ToUpper()}: Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

                }

            } else if (dispatchCallingConvention == 2) {

                 m_logger.LogDebug($"{channelName.ToUpper()}: Response {dispatchRequestHandle} not implemented");

            }
        }
    }
}
