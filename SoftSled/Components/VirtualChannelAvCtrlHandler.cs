using AxMSTSCLib;
using LibVLCSharp.Shared;
using SoftSled.Components.RTSP;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SoftSled.Components {
    class VirtualChannelAvCtrlHandler {

        private Logger m_logger;

        public event EventHandler<VirtualChannelSendArgs> VirtualChannelSend;

        private string channelName = "avctrl";

        private LibVLC _libVLC;
        private MediaPlayer _mp;
        private AxWMPLib.AxWindowsMediaPlayer wmp;
        private RTSPClient rtspClient;

        private int DMCTServiceHandle;
        private int DSPAServiceHandle;
        private int DRMRIServiceHandle;
        private int DMCTStubRequestCookie = 14724;
        private string DMCTOpenMediaURL;
        private Media currentMedia;
        private WMPLib.IWMPMedia iWMPMedia;
        private int PreviousVolume = -1;
        private int StubRequestHandleIter = 1;
        private int StubServiceHandleIter = 1;
        private Dictionary<int, StubRequestType> StubRequestTypeDict = new Dictionary<int, StubRequestType>();
        private Dictionary<StubService, int> StubServiceHandleDict = new Dictionary<StubService, int>();
        private Dictionary<int, int> StubRequestCookieDict = new Dictionary<int, int>();
        private Dictionary<int, int> ProxyRequestHandleDict = new Dictionary<int, int>();
        private Dictionary<int, int> ProxyServiceHandleDict = new Dictionary<int, int>();

        private static Guid RegisterTransmitterServiceClassID = new Guid("b707af79-ca99-42d1-8c60-469fe112001e");
        private static Guid RegisterTransmitterServiceServiceID = new Guid("acb96f70-e61f-45cb-9745-86c47dcbb156");

        public VirtualChannelAvCtrlHandler(Logger m_logger, LibVLC _libVLC, MediaPlayer _mp, AxWMPLib.AxWindowsMediaPlayer wmp) {
            this.m_logger = m_logger;
            this._libVLC = _libVLC;
            this._mp = _mp;
            this.wmp = wmp;
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
            string byteArray = "";
            foreach (byte b in incomingBuff) {
                byteArray += b.ToString("X2") + " ";
            }
            // DEBUG PURPOSES ONLY


            if (dispatchCallingConvention == 1) {

                int dispatchServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 14);
                int dispatchFunctionHandle = DataUtilities.Get4ByteInt(incomingBuff, 18);

                //Debug.WriteLine("AVCTRL: Bytes: " + BitConverter.ToString(incomingBuff));

                //// DEBUG PURPOSES ONLY
                //Debug.WriteLine("");
                //Debug.WriteLine("--------------------");
                //Debug.WriteLine($"AVCTRL ITER RECEIVED: {dispatchRequestHandle}");
                //Debug.WriteLine($"AVCTRL ITER BYTES RECEIVED: {dispatchRequestHandleArray[0]} {dispatchRequestHandleArray[1]} {dispatchRequestHandleArray[2]} {dispatchRequestHandleArray[3]}");
                //Debug.WriteLine($"ServiceHandle: {dispatchServiceHandle}");
                //Debug.WriteLine($"FunctionHandle: {dispatchFunctionHandle}");

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
                            // DMCT ClassID
                            case "18c7c708-c529-4639-a846-5847f31b1e83":
                                DMCTServiceHandle = createServiceServiceHandle;
                                m_logger.LogDebug($"AVCTRL: CreateService DMCT  ({DMCTServiceHandle})");
                                break;
                            // DSPA ClassID
                            case "077bfd3a-7028-4913-bd14-53963dc37754":
                                DSPAServiceHandle = createServiceServiceHandle;
                                m_logger.LogDebug($"AVCTRL: CreateService DSPA ({DSPAServiceHandle})");
                                break;
                            // DRMRI ClassID
                            case "b707af79-ca99-42d1-8c60-469fe112001e":
                                DRMRIServiceHandle = createServiceServiceHandle;
                                m_logger.LogDebug($"AVCTRL: CreateService DRMRI ({DRMRIServiceHandle})");
                                break;
                            default:
                                m_logger.LogDebug($"AVCTRL: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
                                break;
                        }

                        // Initialise CreateService Response
                        byte[] response = Components.DSLRCommunication.CreateServiceResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // DeleteService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get DeleteService Data
                        int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        // If this is the DMCT Service
                        if (deleteServiceServiceHandle == DMCTServiceHandle) {
                        m_logger.LogDebug($"MCXSESS: DeleteService DMCT ({DMCTServiceHandle})");
                            // Clear the DMCT Service
                            DMCTServiceHandle = 0;
                        }
                        // If this is the DSPA Service
                        else if (deleteServiceServiceHandle == DSPAServiceHandle) {
                        m_logger.LogDebug($"MCXSESS: DeleteService DSPA ({DSPAServiceHandle})");
                            // Clear the DSPA Service
                            DSPAServiceHandle = 0;
                        }
                        // If this is the DRMRI Service
                        else if (deleteServiceServiceHandle == DRMRIServiceHandle) {
                        m_logger.LogDebug($"MCXSESS: DeleteService DRMRI ({DRMRIServiceHandle})");
                            // Clear the DRMRI Service
                            DRMRIServiceHandle = 0;
                        }

                        // Initialise DeleteService Response
                        byte[] response = Components.DSLRCommunication.DeleteServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger.LogDebug($"Unknown DSLR Request {dispatchFunctionHandle} not implemented");

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

                        m_logger.LogDebug($"AVCTRL: OpenMedia ({OpenMediaPayloadURL})");

                        DMCTOpenMediaURL = OpenMediaPayloadURL;
                        Debug.WriteLine(DMCTOpenMediaURL);

                        rtspClient = new RTSPClient();
                        rtspClient.Connect(DMCTOpenMediaURL, RTSPClient.RTP_TRANSPORT.UDP, RTSPClient.MEDIA_REQUEST.VIDEO_AND_AUDIO);

                        


                        //RTSPHandler.StartRtsp(DMCTOpenMediaURL);


                        //iWMPMedia = wmp.newMedia(DMCTOpenMediaURL);
                        //wmp.currentMedia = iWMPMedia;

                        //wmp.launchURL(DMCTOpenMediaURL);
                        //wmp.openPlayer(DMCTOpenMediaURL);

                        //// Create Media Object
                        //currentMedia = new Media(_libVLC, new Uri(DMCTOpenMediaURL));
                        //currentMedia.Parse();

                        //_mp.NetworkCaching = 1000;
                        //_mp.Media = currentMedia;



                        // Initialise OpenMedia Response
                        byte[] response = Components.DSLRCommunication.OpenMediaResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the SetDWORDProperty Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // CloseMedia Request
                    else if (dispatchFunctionHandle == 1) {

                        m_logger.LogDebug("AVCTRL: CloseMedia");

                        //_mp.Stop();
                        //wmp.Ctlcontrols.stop();
                        rtspClient.Stop();

                        // Initialise CloseMedia Response
                        byte[] response = Components.DSLRCommunication.CloseMediaResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the CloseMedia Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

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

                        m_logger.LogDebug("AVCTRL: Start");

                        //_mp.Play(currentMedia);
                        //wmp.Ctlcontrols.play();

                        // Initialise Start Response
                        byte[] response = Components.DSLRCommunication.StartResponse(
                            dispatchRequestHandleArray,
                            1
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the Start Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Pause Request
                    else if (dispatchFunctionHandle == 3) {

                        m_logger.LogDebug("AVCTRL: Pause");

                        //_mp.Pause();
                        //wmp.Ctlcontrols.pause();

                        // Initialise Pause Response
                        byte[] response = Components.DSLRCommunication.PauseResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the Pause Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Stop Request
                    else if (dispatchFunctionHandle == 4) {

                        m_logger.LogDebug("AVCTRL: Stop");

                        rtspClient.Stop();

                        //_mp.Stop();
                        //wmp.Ctlcontrols.stop();

                        // Initialise Stop Response
                        byte[] response = Components.DSLRCommunication.StopResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the Stop Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // GetDuration Request
                    else if (dispatchFunctionHandle == 5) {

                        //double duration = iWMPMedia.duration;

                        //long durationLongMili = Convert.ToInt64(duration * 100);
                        //long durationLongMili = Convert.ToInt64(currentMedia.Duration / 10);
                        long durationLongMili = Convert.ToInt64(60 / 10);

                        m_logger.LogDebug($"AVCTRL: GetDuration ({durationLongMili})");

                        // Initialise GetDuration Response
                        byte[] response = Components.DSLRCommunication.GetDurationResponse(
                            dispatchRequestHandleArray,
                            durationLongMili
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the GetDuration Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // GetPosition Request
                    else if (dispatchFunctionHandle == 6) {

                        double currentPosition = wmp.Ctlcontrols.currentPosition;

                        long positionLongMili = Convert.ToInt64(currentPosition * 100);
                        //long positionLongMili = Convert.ToInt64(_mp.Time / 10);
                        //long positionLongMili = Convert.ToInt64(5 / 10);

                        //m_logger.LogDebug($"AVCTRL: GetPosition ({positionLongMili})");

                        // Initialise GetPosition Response
                        byte[] response = Components.DSLRCommunication.GetPositionResponse(
                            dispatchRequestHandleArray,
                            positionLongMili
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);
                        // Send the GetPosition Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // RegisterMediaEventCallback Request
                    else if (dispatchFunctionHandle == 8) {

                        // Get RegisterMediaEventCallback Data
                        int RegisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid RegisterMediaEventCallbackClassID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        Guid RegisterMediaEventCallbackServiceID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);

                        m_logger.LogDebug("AVCTRL: RegisterMediaEventCallback");

                        
                        // Add Stub RequestType to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.RegisterMediaEventCallback);
                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);
                        // Add Proxy Service Handle to Dictionary
                        ProxyServiceHandleDict.Add(StubRequestHandleIter, StubServiceHandleIter);

                        // If the StubService Match Dictionary Contains this StubService
                        if (StubServiceHandleDict.ContainsKey(StubService.MediaEventCallback)) {
                            // Overwrite the StubService Entry in Match Dictionary
                            StubServiceHandleDict[StubService.MediaEventCallback] = StubServiceHandleIter;
                        } else {
                            // Add Stub Service Handle to StubService Match Dictionary
                            StubServiceHandleDict.Add(StubService.MediaEventCallback, StubServiceHandleIter);
                        }

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
                        // Increment Stub ServiceHandle Iter
                        StubServiceHandleIter++;

                        // Send the RegisterMediaEventCallback CreateService Request
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // UnregisterMediaEventCallback Request
                    else if (dispatchFunctionHandle == 9) {

                        // Get UnregisterMediaEventCallback Data
                        int UnregisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterMediaEventCallbackPayloadCookie = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: UnregisterMediaEventCallback");

                        // Add Stub RequestHandle Iter to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.UnregisterMediaEventCallback);

                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);

                        // Initialise UnregisterMediaEventCallback DeleteService Request
                        byte[] response = Components.DSLRCommunication.DeleteServiceRequest(
                            StubRequestHandleIter,
                            StubRequestCookieDict[UnregisterMediaEventCallbackPayloadCookie]
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;

                        // Send the UnregisterMediaEventCallback CreateService Request
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger.LogDebug($"Unknown DMCT Request {dispatchFunctionHandle} not implemented");

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

                        m_logger.LogDebug($"AVCTRL: GetStringProperty ({GetStringPropertyPayloadPropertyName})");

                        switch (GetStringPropertyPayloadPropertyName) {
                            // Property Bag Service
                            case "XspHostAddress":
                                // Initialise GetStringProperty Response
                                byte[] response = Components.DSLRCommunication.GetStringPropertyResponse(
                                    dispatchRequestHandleArray,
                                    SoftSledConfigManager.ReadConfig().RdpLoginHost
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                                // Send the GetStringProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                                break;
                            default:
                                m_logger.LogDebug($"AVCTRL: GetStringProperty ({GetStringPropertyPayloadPropertyName}) not available");
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

                        m_logger.LogDebug($"AVCTRL: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName})");

                        switch (GetDWORDPropertyPayloadPropertyName) {
                            case "IsMuted":
                                bool isMuted = SystemAudio.WindowsSystemAudio.GetMute();
                                // Initialise GetDWORDProperty Response
                                byte[] isMutedResponse = Components.DSLRCommunication.GetDWORDPropertyResponse(
                                    dispatchRequestHandleArray,
                                    isMuted ? 1 : 0 
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedIsMutedResponse = Components.DSLRCommunication.Encapsulate(isMutedResponse);

                                // Send the GetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedIsMutedResponse));

                                break;
                            case "Volume":
                                // Get the System Volume
                                decimal currentVolume = SystemAudio.WindowsSystemAudio.GetVolume();
                                int sendVolume = 0;

                                m_logger.LogDebug($"AVCTRL: CurrentVolume ({currentVolume})");

                                // If the Volume is greater than 0
                                if (currentVolume > 0) {
                                    m_logger.LogDebug($"AVCTRL: CurrentVolume Div ({(currentVolume / 100)})");
                                    sendVolume = (int)Math.Floor((65535 / (currentVolume / 100)) / 100);
                                }
                               
                                m_logger.LogDebug($"AVCTRL: SendVolume ({sendVolume})");

                                // Initialise GetDWORDProperty Response
                                byte[] volumeResponse = Components.DSLRCommunication.GetDWORDPropertyResponse(
                                    dispatchRequestHandleArray,
                                    sendVolume
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedVolumeResponse = Components.DSLRCommunication.Encapsulate(volumeResponse);

                                // Send the GetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedVolumeResponse));
                                break;
                            case "WmvTrickModesSupported":
                                // Initialise GetDWORDProperty Response
                                byte[] trickModeResponse = Components.DSLRCommunication.GetDWORDPropertyResponse(
                                    dispatchRequestHandleArray,
                                    0
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedTrickModeResponse = Components.DSLRCommunication.Encapsulate(trickModeResponse);

                                // Send the GetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedTrickModeResponse));
                                break;
                            default:
                                m_logger.LogDebug($"AVCTRL: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName}) not available");
                                break;
                        }
                    }
                    // SetDWORDProperty Request
                    else if (dispatchFunctionHandle == 3) {

                        // Set SetDWORDProperty Data
                        int SetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int SetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int SetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string SetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, SetDWORDPropertyPayloadLength);
                        int SetDWORDPropertyPayloadPropertyValue = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + SetDWORDPropertyPayloadLength);

                        m_logger.LogDebug($"AVCTRL: SetDWORDProperty ({SetDWORDPropertyPayloadPropertyName})");

                        switch (SetDWORDPropertyPayloadPropertyName) {
                            case "IsMuted":

                                if (SetDWORDPropertyPayloadPropertyValue == 1) {
                                    //SystemAudio.WindowsSystemAudio.SetMute(true);
                                } else {
                                    //SystemAudio.WindowsSystemAudio.SetMute(false);
                                }

                                // Initialise SetDWORDProperty Response
                                byte[] response = Components.DSLRCommunication.SetDWORDPropertyResponse(
                                    dispatchRequestHandleArray
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                                // Send the SetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                                break;
                            case "Volume":

                                int setVolume = (int)Math.Floor((SetDWORDPropertyPayloadPropertyValue / 65535.00) * 100);

                                m_logger.LogDebug($"AVCTRL: SetVolume ({setVolume})");
                                //SystemAudio.WindowsSystemAudio.SetVolume(setVolume);

                                // Initialise SetDWORDProperty Response
                                byte[] volumeResponse = Components.DSLRCommunication.SetDWORDPropertyResponse(
                                    dispatchRequestHandleArray
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedVolumeResponse = Components.DSLRCommunication.Encapsulate(volumeResponse);

                                // Send the SetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedVolumeResponse));
                                break;
                            default:
                                m_logger.LogDebug($"AVCTRL: SetDWORDProperty ({SetDWORDPropertyPayloadPropertyName}) not available");
                                break;
                        }
                    }
                    // Unknown Request
                    else {

                        m_logger.LogDebug($"Unknown DSPA Request {dispatchFunctionHandle} not implemented");

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
                        //Guid RegisterTransmitterServiceClassID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        byte[] RegisterTransmitterServiceClassIdBytes = DataUtilities.GuidToArray(RegisterTransmitterServiceClassID);
                        byte[] RegisterTransmitterServiceServiceIdBytes = DataUtilities.GuidToArray(RegisterTransmitterServiceServiceID);

                        m_logger.LogDebug("AVCTRL: RegisterTransmitterService");

                        // Add Stub RequestType to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.RegisterTransmitterService);
                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);
                        // Add Proxy Service Handle to Dictionary
                        ProxyServiceHandleDict.Add(StubRequestHandleIter, StubServiceHandleIter);

                        // Initialise RegisterMediaEventCallback CreateService Request
                        byte[] response = Components.DSLRCommunication.CreateServiceRequest(
                            StubRequestHandleIter,
                            RegisterTransmitterServiceClassIdBytes,
                            RegisterTransmitterServiceServiceIdBytes,
                            StubServiceHandleIter
                        );
                        
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;
                        // Increment Stub ServiceHandle Iter
                        StubServiceHandleIter++;

                        // Send the RegisterMediaEventCallback CreateService Request
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // UnregisterTransmitterService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get UnregisterTransmitterService Data
                        int UnregisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterTransmitterServicePayloadCookie = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: UnregisterTransmitterService");

                        // Add Stub RequestHandle Iter to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.UnregisterTransmitterService);

                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);

                        // Initialise UnregisterMediaEventCallback DeleteService Request
                        byte[] response = Components.DSLRCommunication.DeleteServiceRequest(
                            StubRequestHandleIter,
                            0
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;

                        // Send the UnregisterTransmitterService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // InitiateRegistration Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get InitiateRegistration Data
                        int InitiateRegistrationPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int InitiateRegistrationChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);

                        m_logger.LogDebug("AVCTRL: InitiateRegistration");

                        // Initialise InitiateRegistration Response
                        byte[] response = Components.DSLRCommunication.InitiateRegistrationResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the InitiateRegistration Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger.LogDebug($"Unknown DRMRI Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                } else {

                    m_logger.LogDebug($"Unknown AVCTRL {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

                }
            } else if (dispatchCallingConvention == 2) {

                byte[] response = null;
                byte[] encapsulatedResponse = null;

                switch (StubRequestTypeDict[dispatchRequestHandle]) {
                    // If the RequestHandle matches RegisterMediaEventCallback Request
                    case StubRequestType.RegisterMediaEventCallback:

                        // Get RegisterMediaEventCallback CreateService Response Data
                        int RegisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int RegisterMediaEventCallbackPayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: RegisterMediaEventCallbackResponse");

                        if (RegisterMediaEventCallbackPayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger.LogDebug("AVCTRL: RegisterMediaEventCallback Request Failed - 0x" + errorByteArray);
                        }

                        // Get Proxy Service Handle from Dictionary and Add with Cookie
                        StubRequestCookieDict.Add(DMCTStubRequestCookie, ProxyServiceHandleDict[dispatchRequestHandle]);

                        // Initialise RegisterMediaEventCallback Response
                        response = Components.DSLRCommunication.RegisterMediaEventCallbackResponse(
                            ProxyRequestHandleDict[dispatchRequestHandle],
                            DMCTStubRequestCookie,
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the RegisterMediaEventCallback Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        // Increment Cookie
                        DMCTStubRequestCookie++;

                        break;
                    // If the RequestHandle matches UnregisterMediaEventCallback Request
                    case StubRequestType.UnregisterMediaEventCallback:

                        // Get UnregisterMediaEventCallback CreateService Response Data
                        int UnregisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterMediaEventCallbackPayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: UnregisterMediaEventCallbackResponse");

                        if (UnregisterMediaEventCallbackPayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger.LogDebug("AVCTRL: UnregisterMediaEventCallback Request Failed - 0x" + errorByteArray);
                        }

                        // Initialise UnregisterMediaEventCallback Response
                        response = Components.DSLRCommunication.UnregisterMediaEventCallbackResponse(
                            ProxyRequestHandleDict[dispatchRequestHandle],
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the RegisterMediaEventCallback Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        break;
                    // If the RequestHandle matches RegisterTransmitterService Request
                    case StubRequestType.RegisterTransmitterService:

                        // Get RegisterMediaEventCallback CreateService Response Data
                        int RegisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int RegisterTransmitterServicePayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: RegisterTransmitterServiceResponse");

                        if (RegisterTransmitterServicePayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + "";
                            }
                            m_logger.LogDebug("AVCTRL: RegisterTransmitterService Request Failed - 0x" + errorByteArray);
                        }

                        // Initialise RegisterTransmitterService Response
                        response = Components.DSLRCommunication.RegisterTransmitterServiceResponse(
                            DataUtilities.GetInt4Byte(ProxyRequestHandleDict[dispatchRequestHandle]),
                            RegisterTransmitterServicePayloadResult == 0
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the RegisterTransmitterService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        break;
                    // If the RequestHandle matches UnregisterTransmitterService Request
                    case StubRequestType.UnregisterTransmitterService:

                        // Get UnregisterMediaEventCallback CreateService Response Data
                        int UnregisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterTransmitterServicePayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: UnregisterTransmitterServiceResponse");

                        if (UnregisterTransmitterServicePayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger.LogDebug("AVCTRL: UnregisterTransmitterService Request Failed - 0x" + errorByteArray);
                        }

                        // Initialise UnregisterTransmitterService Response
                        response = Components.DSLRCommunication.UnregisterTransmitterServiceResponse(
                            DataUtilities.GetInt4Byte(ProxyRequestHandleDict[dispatchRequestHandle])
                        );

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                        // Send the UnregisterTransmitterService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        break;
                    // If the RequestHandle matches OnMediaEvent Request
                    case StubRequestType.OnMediaEvent:

                        // Get UnregisterMediaEventCallback CreateService Response Data
                        int OnMediaEventPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int OnMediaEventChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int OnMediaEventPayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger.LogDebug("AVCTRL: OnMediaEventResponse");

                        if (OnMediaEventPayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger.LogDebug("AVCTRL: OnMediaEventResponse Request Failed - 0x" + errorByteArray);
                        }

                        break;
                    default:
                        m_logger.LogDebug($"Unknown Response RequestHandle {dispatchRequestHandle} - No matching requests made");
                        break;
                }

            } else {

                m_logger.LogDebug($"AVCTRL: Unknown CallingConvention {dispatchCallingConvention} not implemented");

            }
        }

        public void OnMediaEvent(MediaEvent mediaEvent) {           

            m_logger.LogDebug($"AVCTRL: OnMediaEvent ({mediaEvent})");

            // Initialise RegisterMediaEventCallback CreateService Request
            byte[] response = Components.DSLRCommunication.OnMediaEventRequest(
                StubRequestHandleIter,
                StubServiceHandleDict[StubService.MediaEventCallback],
                DataUtilities.GetInt4Byte(0), // TODO: Add Error Codes
                DataUtilities.GetInt4Byte((int)mediaEvent)
            );
            // Encapsulate the Response (Doesn't seem to work without this?)
            byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

            // Add Stub RequestType to Dictionary
            StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.OnMediaEvent);

            // Increment Stub RequestHandle Iter
            StubRequestHandleIter++;


            // Send the RegisterMediaEventCallback CreateService Request
            VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));
        }
    }

    enum StubService {
        MediaEventCallback
    }

    enum StubRequestType {
        RegisterMediaEventCallback,
        UnregisterMediaEventCallback,
        RegisterTransmitterService,
        UnregisterTransmitterService,
        OnMediaEvent,
    }

    enum MediaEvent {
        BUFFERING_STOP = 1,
        END_OF_MEDIA = 2,
        RTSP_DISCONNECT = 3,
        PTS_ERROR = 5,
        UNRECOVERABLE_SKEW = 6,
        DRM_LICENSE_ERROR = 11,
        DRM_LICENSE_CLEAR = 14,
        DRM_HDCP_ERROR = 15,
        FIRMWARE_UPDATE = 17
    }
}
