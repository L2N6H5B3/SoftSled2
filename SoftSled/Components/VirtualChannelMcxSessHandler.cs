using AxMSTSCLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SoftSled.Components {
    class VirtualChannelMcxSessHandler {

        private Logger m_logger;
        private AxMsRdpClient7 rdpClient;

        public event EventHandler<StatusChangedArgs> StatusChanged;

        private int DSMNServiceHandle;

        public VirtualChannelMcxSessHandler(Logger m_logger, AxMsRdpClient7 rdpClient) {
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

                    m_logger.LogDebug("MCXSESS: Request CreateService " + createServiceServiceHandle);

                    switch (createServiceClassID.ToString()) {
                        // DSMN ClassID
                        case "a30dc60e-1e2c-44f2-bfd1-17e51c0cdf19":
                            DSMNServiceHandle = createServiceServiceHandle;
                            // Create new StatusChangedArgs
                            StatusChangedArgs args = new StatusChangedArgs {
                                // Set the StatusChangedArgs Response Data
                                shellOpen = false,
                                statusText = "Starting Experience..."
                            };
                            // Raise Response Event
                            StatusChanged(this, args);
                            break;
                    }

                    // Initialise CreateService Response
                    byte[] response = Components.DSLRCommunication.CreateServiceResponse(
                        DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the CreateService Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response CreateService " + dispatchRequestHandle);
                }
                // DeleteService Request
                else if (dispatchFunctionHandle == 2) {

                    // Get DeleteService Data
                    int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid deleteServiceClassID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid deleteServiceServiceID = DataUtilities.GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                    int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                    m_logger.LogDebug("MCXSESS: Request DeleteService " + deleteServiceServiceHandle);


                    // Send the DeleteService Response
                    //rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(response));

                    m_logger.LogDebug("MCXSESS: Sent Response DeleteService " + dispatchRequestHandle);
                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"MCXSESS: Unknown DSLR Request {dispatchFunctionHandle} not implemented");

                }

                #endregion ####################################################

            }
            // DSMN Service Handle
            else if (dispatchServiceHandle == DSMNServiceHandle) {

                #region DSMN Service ##########################################

                // ShellDisconnect Request
                if (dispatchFunctionHandle == 0) {

                    m_logger.LogDebug("MCXSESS: Request ShellDisconnect " + dispatchServiceHandle);

                    // Get ShellDisconnect Data
                    int ShellDisconnectPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int ShellDisconnectChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int ShellDisconnectPayloadDisconnectReason = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    string newStatusString = "";

                    // Set status according to Disconnect Reason
                    switch (ShellDisconnectPayloadDisconnectReason) {
                        case 0:
                            newStatusString = "Disconnected: Shell exited unexpectedly";
                            break;
                        case 1:
                            newStatusString = "Disconnected: Unknown error";
                            break;
                        case 2:
                            newStatusString = "Disconnected: Initialisation error";
                            break;
                        case 3:
                            newStatusString = "Disconnected: Shell is not responding";
                            break;
                        case 4:
                            newStatusString = "Disconnected: Unauthorised UI in the session";
                            break;
                        case 5:
                            newStatusString = "Disconnected: User is not allowed - the remote device was disabled on the host";
                            break;
                        case 6:
                            newStatusString = "Disconnected: Certificate is invalid";
                            break;
                        case 7:
                            newStatusString = "Disconnected: Shell cannot be started";
                            break;
                        case 8:
                            newStatusString = "Disconnected: Shell monitor thread cannot be started";
                            break;
                        case 9:
                            newStatusString = "Disconnected: Message window cannot be created";
                            break;
                        case 10:
                            newStatusString = "Disconnected: Terminal Services session cannot be started";
                            break;
                        case 11:
                            newStatusString = "Disconnected: Plug and Play (PNP) failed";
                            break;
                        case 12:
                            newStatusString = "Disconnected: Certificate is not trusted";
                            break;
                        case 13:
                            newStatusString = "Disconnected: Product regstration is expired";
                            break;
                        case 14:
                            newStatusString = "Disconnected: PC gone to Sleep / Shut Down";
                            break;
                        case 15:
                            newStatusString = "Disconnected: User closed the session";
                            break;
                    }

                    // Create new StatusChangedArgs
                    StatusChangedArgs args = new StatusChangedArgs {
                        // Set the StatusChangedArgs Response Data
                        shellOpen = false,
                        statusText = newStatusString
                    };
                    // Raise Response Event
                    StatusChanged(this, args);
                    
                    m_logger.LogInfo("Experience closed");

                    // Initialise ShellDisconnect Response
                    byte[] response = Components.DSLRCommunication.ShellDisconnectResponse(
                        DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the ShellDisconnect Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response ShellDisconnect " + dispatchServiceHandle);

                }
                // ShellIsActive Request
                else if (dispatchFunctionHandle == 2) {

                    m_logger.LogDebug("MCXSESS: Request ShellIsActive " + dispatchServiceHandle);

                    // Create new StatusChangedArgs
                    StatusChangedArgs args = new StatusChangedArgs {
                        // Set the StatusChangedArgs Response Data
                        shellOpen = true,
                        statusText = ""
                    };
                    // Raise Response Event
                    StatusChanged(this, args);

                    // Initialise ShellIsActive Response
                    byte[] response = Components.DSLRCommunication.ShellIsActiveResponse(
                        DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the ShellIsActive Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response ShellIsActive " + dispatchServiceHandle);

                }
                // Heartbeat Request
                else if (dispatchFunctionHandle == 1) {

                    m_logger.LogDebug("MCXSESS: Request Heartbeat " + dispatchServiceHandle);

                    // Get Heartbeat Data
                    int HeartbeatPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int HeartbeatChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int HeartbeatPayloadScreensaverFlag = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    // Initialise Heartbeat Response
                    byte[] response = Components.DSLRCommunication.HeartbeatResponse(
                        DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the Heartbeat Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response Heartbeat " + dispatchServiceHandle);

                }
                // GetQWaveSinkInfo Request
                else if (dispatchFunctionHandle == 3) {

                    m_logger.LogDebug("MCXSESS: Request GetQWaveSinkInfo " + dispatchServiceHandle);

                    // Get GetQWaveSinkInfo Data
                    int GetQWaveSinkInfoPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int GetQWaveSinkInfoChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int GetQWaveSinkInfoPayloadScreensaverFlag = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    // Initialise GetQWaveSinkInfo Response
                    byte[] response = Components.DSLRCommunication.GetQWaveSinkInfoResponse(
                        DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the GetQWaveSinkInfo Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response GetQWaveSinkInfo " + dispatchServiceHandle);

                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"MCXSESS: Unknown DSMN Request {dispatchFunctionHandle} not implemented");

                    // Get Unknown Data
                    int UnknownPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int UnknownChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);

                    if (UnknownPayloadSize > 0) {
                        byte[] UnknownPayloadData = DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, UnknownPayloadSize);
                    }

                    // Initialise Generic Response
                    byte[] response = Components.DSLRCommunication.GenericOKResponse(
                        DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = Components.DSLRCommunication.Encapsulate(response);

                    // Send the Generic Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Generic Response " + dispatchFunctionHandle);

                }

                #endregion ####################################################

            } else {

                System.Diagnostics.Debug.WriteLine($"MCXSESS: Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

            }
        }
    }

    class StatusChangedArgs : EventArgs {
        public bool shellOpen;
        public string statusText;
    }
}
