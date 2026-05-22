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
                                m_logger.LogDebug($"{channelName.ToUpper()}: CreateService DSPA ({DSPAServiceHandle})");
                                break;
                            default:
                                m_logger.LogDebug($"{channelName.ToUpper()}: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
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
                            m_logger.LogDebug($"{channelName.ToUpper()}: DeleteService DSPA ({DSPAServiceHandle})");
                            // Clear the DSPA Service
                            DSPAServiceHandle = 0;
                        } else {
                            m_logger.LogDebug($"{channelName.ToUpper()}: DeleteService Handle ({deleteServiceServiceHandle}) not found");
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
                            case "NAM":
                                // Initialise GetStringProperty Response
                                response = DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
                                );
                                break;
                            case "PRT":
                                m_logger.LogDebug($"{channelName.ToUpper()}: PRT String");
                                // Initialise GetStringProperty Response
                                response = DSLRCommunication.GetStringPropertyResponse(
                                   DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                   @"rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3"
                               );
                                //response = DSLRCommunication.GetStringPropertyNullResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
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
                                m_logger.LogDebug($"{channelName.ToUpper()}: XTY String");
                                // Initialise GetStringProperty Response
                                response = DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "McxClient"
                                );
                                break;
                            case "PBV":
                                // Initialise GetStringProperty Response
                                response = DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    "1"
                                );
                                break;
                            default:
                                m_logger.LogDebug($"{channelName.ToUpper()}: GetStringProperty ({GetStringPropertyPayloadPropertyName}) not available");
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
                            m_logger.LogDebug($"{channelName.ToUpper()}: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")}) True");
                            
                        } else {
                            response = DSLRCommunication.DeviceCapabilityFalseGetDWORDPropertyResponse(DataUtilities.GetByteSubArray(incomingBuff, 10, 4));
                            m_logger.LogDebug($"{channelName.ToUpper()}: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName.Replace("\0", "")}) False");
                        }

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

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

            } else {

                m_logger.LogDebug($"{channelName.ToUpper()}: Unknown CallingConvention {dispatchCallingConvention} not implemented");

            }
        }
    }
}
