using AxMSTSCLib;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SoftSled.Components {
    class VirtualChannelAvCtrlHandler {

        private Logger m_logger;
        private AxMsRdpClient7 rdpClient;
        private LibVLC _libVLC;
        private MediaPlayer _mp;

        private int DMCTServiceHandle;
        private int DSPAServiceHandle;
        private int DRMRIServiceHandle;
        private int DMCTRegisterMediaEventCallbackCookie;
        private string DMCTOpenMediaURL;
        private Media currentMedia;
        private int StubRequestHandleIter = 1;
        private int StubServiceHandleIter = 1;
        private Dictionary<int, StubRequestType> StubRequestHandleDict = new Dictionary<int, StubRequestType>();
        private Dictionary<int, int> ProxyRequestHandleDict = new Dictionary<int, int>();


        public VirtualChannelAvCtrlHandler(Logger m_logger, AxMsRdpClient7 rdpClient, LibVLC _libVLC, MediaPlayer _mp) {
            this.m_logger = m_logger;
            this.rdpClient = rdpClient;
            this._libVLC = _libVLC;
            this._mp = _mp;
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

            //// DEBUG PURPOSES ONLY
            //string byteArray = "";
            //foreach (byte b in incomingBuff) {
            //    byteArray += b.ToString("X2") + " ";
            //}
            //// DEBUG PURPOSES ONLY


            if (dispatchCallingConvention == 1) {

                int dispatchServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 14);
                int dispatchFunctionHandle = DataUtilities.Get4ByteInt(incomingBuff, 18);


                // DEBUG PURPOSES ONLY
                Debug.WriteLine("");
                Debug.WriteLine("--------------------");
                Debug.WriteLine($"AVCTRL ITER RECEIVED: {dispatchRequestHandle}");
                Debug.WriteLine($"AVCTRL ITER BYTES RECEIVED: {dispatchRequestHandleArray[0]} {dispatchRequestHandleArray[1]} {dispatchRequestHandleArray[2]} {dispatchRequestHandleArray[3]}");
                Debug.WriteLine($"ServiceHandle: {dispatchServiceHandle}");
                Debug.WriteLine($"FunctionHandle: {dispatchFunctionHandle}");

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

                        m_logger.LogDebug("AVCTRL: Request CreateService " + createServiceServiceHandle);

                        switch (createServiceClassID.ToString().ToLower()) {
                            // DMCT ClassID
                            case "18c7c708-c529-4639-a846-5847f31b1e83":
                                DMCTServiceHandle = createServiceServiceHandle;
                                break;
                            // DSPA ClassID
                            case "077bfd3a-7028-4913-bd14-53963dc37754":
                                DSPAServiceHandle = createServiceServiceHandle;
                                break;
                            // DRMRI ClassID
                            case "b707af79-ca99-42d1-8c60-469fe112001e":
                                DRMRIServiceHandle = createServiceServiceHandle;
                                break;
                            default:
                                Debug.WriteLine($"AVCTRL: DSLR Service ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
                                break;
                        }

                        // Initialise CreateService Response
                        byte[] response = Components.DSLRCommunication.CreateServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response CreateService " + dispatchRequestHandle);
                    }
                    // DeleteService Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get DeleteService Data
                        int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid deleteServiceClassID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        Guid deleteServiceServiceID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                        int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                        m_logger.LogDebug("AVCTRL: Request DeleteService " + deleteServiceServiceHandle);


                        // Send the DeleteService Response
                        //rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(response));

                        m_logger.LogDebug("AVCTRL: Sent Response DeleteService " + dispatchRequestHandle);
                    }
                    // Unknown Request
                    else {

                        System.Diagnostics.Debug.WriteLine($"Unknown DSLR Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                }
                // DMCT Service Handle
                else if (dispatchServiceHandle == DMCTServiceHandle) {

                    #region DMCT Service ##########################################

                    // OpenMedia Request
                    if (dispatchFunctionHandle == 0) {

                        // Get OpenMedia Data
                        int OpenMediaPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int OpenMediaChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int OpenMediaPayloadURLLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string OpenMediaPayloadURL = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, OpenMediaPayloadURLLength);
                        int OpenMediaPayloadSurfaceID = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + OpenMediaPayloadURLLength);
                        int OpenMediaPayloadTimeOut = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + OpenMediaPayloadURLLength + 4);

                        m_logger.LogDebug("AVCTRL: Request OpenMedia " + OpenMediaPayloadURL);

                        DMCTOpenMediaURL = OpenMediaPayloadURL;

                        System.Diagnostics.Debug.WriteLine(DMCTOpenMediaURL);

                        // Create Media Object
                        currentMedia = new Media(_libVLC, new Uri(DMCTOpenMediaURL));
                        currentMedia.Parse();

                        // Initialise OpenMedia Response
                        byte[] response = Components.DSLRCommunication.OpenMediaResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // DEBUG PURPOSES ONLY
                        string byteArray = "";
                        foreach (byte b in response) {
                            byteArray += b.ToString("X2") + " ";
                        }
                        // DEBUG PURPOSES ONLY

                        // Send the SetDWORDProperty Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response OpenMedia " + DMCTOpenMediaURL);

                    }
                    // CloseMedia Request
                    else if (dispatchFunctionHandle == 1) {

                        m_logger.LogDebug("AVCTRL: Request CloseMedia " + dispatchRequestHandle);

                        _mp.Pause();

                        // Initialise CloseMedia Response
                        byte[] response = Components.DSLRCommunication.CloseMediaResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CloseMedia Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response CloseMedia " + dispatchRequestHandle);

                    }
                    // Start Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get Start Data
                        int StartPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int StartChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        long StartPayloadStartTime = DataUtilities.Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        long StartPayloadUseOptimisedPreroll = DataUtilities.Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8);
                        int StartPayloadRequestedPlayRate = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8 + 8);
                        long StartPayloadAvailableBandwidth = DataUtilities.Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8 + 8 + 4);

                        m_logger.LogDebug("AVCTRL: Request Start " + dispatchRequestHandle);

                        _mp.Play(currentMedia);

                        // Initialise Start Response
                        byte[] response = Components.DSLRCommunication.StartResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                            StartPayloadRequestedPlayRate
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the Start Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response Start " + dispatchRequestHandle);

                    }
                    // Pause Request
                    else if (dispatchFunctionHandle == 3) {

                        m_logger.LogDebug("AVCTRL: Request Pause " + dispatchRequestHandle);

                        _mp.Pause();

                        // Initialise Pause Response
                        byte[] response = Components.DSLRCommunication.PauseResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the Pause Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response Pause " + dispatchRequestHandle);

                    }
                    // Stop Request
                    else if (dispatchFunctionHandle == 3) {

                        m_logger.LogDebug("AVCTRL: Request Stop " + dispatchRequestHandle);

                        _mp.Stop();

                        // Initialise Stop Response
                        byte[] response = Components.DSLRCommunication.StopResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the Stop Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response Stop " + dispatchRequestHandle);

                    }
                    // GetDuration Request
                    else if (dispatchFunctionHandle == 5) {

                        m_logger.LogDebug("AVCTRL: Request GetDuration " + dispatchRequestHandle);

                        long durationLongMili = Convert.ToInt64(currentMedia.Duration / 10);

                        // Initialise GetDuration Response
                        byte[] response = Components.DSLRCommunication.GetDurationResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                            durationLongMili
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the GetDuration Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response GetDuration " + durationLongMili);

                    }
                    // GetPosition Request
                    else if (dispatchFunctionHandle == 6) {

                        m_logger.LogDebug("AVCTRL: Request GetPosition " + dispatchRequestHandle);

                        long positionLongMili = Convert.ToInt64(_mp.Time / 10);

                        Debug.WriteLine($"Position After:  {positionLongMili}");

                        // Initialise GetPosition Response
                        byte[] response = Components.DSLRCommunication.GetPositionResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                            positionLongMili
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);
                        // Send the GetPosition Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response GetPosition " + dispatchRequestHandle);
                    }
                    // RegisterMediaEventCallback Request
                    else if (dispatchFunctionHandle == 8) {

                        // Get RegisterMediaEventCallback Data
                        int RegisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid RegisterMediaEventCallbackClassID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        Guid RegisterMediaEventCallbackServiceID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);

                        m_logger.LogDebug("AVCTRL: Request RegisterMediaEventCallback " + dispatchRequestHandle);

                        // Add Stub RequestHandle Iter to Dictionary
                        StubRequestHandleDict.Add(StubRequestHandleIter, StubRequestType.RegisterMediaEventCallback);

                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);

                        // Initialise RegisterMediaEventCallback CreateService Request
                        byte[] response = Components.DSLRCommunication.CreateServiceRequest(
                            StubRequestHandleIter,
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 16),
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16, 16),
                            StubServiceHandleIter
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;
                        // Increment Stub ServicesHandle Iter
                        StubServiceHandleIter++;

                        // Send the RegisterMediaEventCallback CreateService Request
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response RegisterMediaEventCallback " + dispatchRequestHandle);

                    }
                    // UnregisterMediaEventCallback Request
                    else if (dispatchFunctionHandle == 9) {

                        System.Diagnostics.Debug.WriteLine("UnregisterMediaEventCallback Request not implemented");

                    }
                    // Unknown Request
                    else {

                        System.Diagnostics.Debug.WriteLine($"Unknown DMCT Request {dispatchFunctionHandle} not implemented");

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

                        switch (GetStringPropertyPayloadPropertyName) {
                            // Property Bag Service
                            case "XspHostAddress":

                                m_logger.LogDebug("AVCTRL: Request GetStringProperty " + GetStringPropertyPayloadPropertyName);

                                // Initialise GetStringProperty Response
                                byte[] response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    SoftSledConfigManager.ReadConfig().RdpLoginHost
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                                // Send the GetStringProperty Response
                                rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                                m_logger.LogDebug("AVCTRL: Sent Response GetStringProperty " + GetStringPropertyPayloadPropertyName);

                                break;
                        }
                    }
                    // GetDWORDProperty Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get GetDWORDProperty Data
                        int GetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int GetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int GetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string GetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetDWORDPropertyPayloadLength);

                        switch (GetDWORDPropertyPayloadPropertyName) {
                            case "IsMuted":

                                m_logger.LogDebug("AVCTRL: Request GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                                // Initialise GetDWORDProperty Response
                                byte[] isMutedResponse = Components.DSLRCommunication.GetDWORDPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    0
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedIsMutedResponse = Components.DSLRCommunication.Encapsulate(isMutedResponse);

                                // Send the GetDWORDProperty Response
                                rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedIsMutedResponse));

                                m_logger.LogDebug("AVCTRL: Sent Response GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                                break;
                            case "Volume":

                                m_logger.LogDebug("AVCTRL: Request GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                                // Initialise GetDWORDProperty Response
                                byte[] volumeResponse = Components.DSLRCommunication.GetDWORDPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4),
                                    1
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedVolumeResponse = Components.DSLRCommunication.Encapsulate(volumeResponse);

                                // Send the GetDWORDProperty Response
                                rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedVolumeResponse));

                                m_logger.LogDebug("AVCTRL: Sent Response GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                                break;
                            default:
                                System.Diagnostics.Debug.WriteLine($"AVCTRL: DSPA {dispatchRequestHandle} with Property Name {GetDWORDPropertyPayloadPropertyName} not available");
                                break;
                        }
                    }
                    // SetDWORDProperty Request
                    else if (dispatchFunctionHandle == 3) {

                        // Get SetDWORDProperty Data
                        int SetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int SetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int SetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string SetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, SetDWORDPropertyPayloadLength);
                        int SetDWORDPropertyPayloadPropertyValue = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + SetDWORDPropertyPayloadLength);

                        switch (SetDWORDPropertyPayloadPropertyName) {
                            case "IsMuted":

                                m_logger.LogDebug("AVCTRL: Request SetDWORDProperty " + SetDWORDPropertyPayloadPropertyName);

                                // Initialise SetDWORDProperty Response
                                byte[] response = Components.DSLRCommunication.SetDWORDPropertyResponse(
                                    DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                                // Send the SetDWORDProperty Response
                                rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                                m_logger.LogDebug("AVCTRL: Sent Response SetDWORDProperty " + SetDWORDPropertyPayloadPropertyName);

                                break;
                        }
                    }
                    // Unknown Request
                    else {

                        System.Diagnostics.Debug.WriteLine($"Unknown DSPA Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                }
                // DRMRI Service Handle
                else if (dispatchServiceHandle == DRMRIServiceHandle) {

                    #region DRMRI Service #########################################

                    // RegisterTransmitterService Request
                    if (dispatchFunctionHandle == 0) {

                        // Get RegisterTransmitterService Data
                        int RegisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid RegisterTransmitterServiceClassID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: Request RegisterTransmitterService " + dispatchRequestHandle);

                        // Initialise RegisterTransmitterService Response
                        byte[] response = Components.DSLRCommunication.RegisterTransmitterServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the RegisterTransmitterService Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response RegisterTransmitterService " + dispatchRequestHandle);

                    }
                    // UnregisterTransmitterService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get UnregisterTransmitterService Data
                        int RegisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid RegisterTransmitterServiceClassID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: Request UnregisterTransmitterService " + dispatchRequestHandle);

                        // Initialise UnregisterTransmitterService Response
                        byte[] response = Components.DSLRCommunication.UnregisterTransmitterServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the UnregisterTransmitterService Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response UnregisterTransmitterService " + dispatchRequestHandle);

                    }
                    // InitiateRegistration Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get InitiateRegistration Data
                        int InitiateRegistrationPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int InitiateRegistrationChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);

                        m_logger.LogDebug("AVCTRL: Request InitiateRegistration " + dispatchRequestHandle);

                        // Initialise InitiateRegistration Response
                        byte[] response = Components.DSLRCommunication.InitiateRegistrationResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the InitiateRegistration Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response InitiateRegistration " + dispatchRequestHandle);

                    }
                    // Unknown Request
                    else {

                        System.Diagnostics.Debug.WriteLine($"Unknown DRMRI Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                } else {

                    System.Diagnostics.Debug.WriteLine($"Unknown AVCTRL {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

                }
            } else if (dispatchCallingConvention == 2) {

                switch (StubRequestHandleDict[dispatchRequestHandle]) {
                    // If the RequestHandle matches a RegisterMediaEventCallback Request
                    case StubRequestType.RegisterMediaEventCallback:

                        // Get RegisterMediaEventCallback CreateService Response Data
                        int RegisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int RegisterMediaEventCallbackPayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        DMCTRegisterMediaEventCallbackCookie = 14733;

                        // Initialise RegisterMediaEventCallback Response
                        byte[] response = Components.DSLRCommunication.RegisterMediaEventCallbackResponse(
                            ProxyRequestHandleDict[dispatchRequestHandle],
                            DMCTRegisterMediaEventCallbackCookie
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the RegisterMediaEventCallback Response
                        rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                        m_logger.LogDebug("AVCTRL: Sent Response RegisterMediaEventCallback " + dispatchRequestHandle);

                        break;
                    default:
                        System.Diagnostics.Debug.WriteLine($"Unknown Response RequestHandle {dispatchRequestHandle} - No matching requests made");
                        break;
                }

            } else {

                System.Diagnostics.Debug.WriteLine($"Unknown CallingConvention {dispatchCallingConvention} not implemented");

            }
        }
    }

    enum StubRequestType {
        RegisterMediaEventCallback
    }
}
