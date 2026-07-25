using SoftSled.Components.Communication;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Utility;
using System;

namespace SoftSled.Components.VirtualChannel {
    class VirtualChannelMcxSessHandler {

        private Logger m_logger;

        public event EventHandler<VirtualChannelSendArgs> VirtualChannelSend;
        public event EventHandler<StatusChangedArgs> StatusChanged;

        private int DSMNServiceHandle;

        // RemoteCommand id → PS/2 Set-1 scan codes (with 0x100 = extended/E0),
        // captured from the RegisterRemoteCommandBindings table WMC sends at
        // session start. These are the exact codes to forward over the RDP
        // keyboard channel when the user presses the matching remote button.
        // Populated regardless of logging so remote forwarding works either way.
        private readonly System.Collections.Generic.Dictionary<int, int[]> _remoteCmdScanCodes
            = new System.Collections.Generic.Dictionary<int, int[]>();

        /// <summary>Look up the scan codes WMC bound to a RemoteCommand id (from
        /// the RegisterRemoteCommandBindings table). False if not received yet or
        /// the command has no binding.</summary>
        public bool TryGetRemoteCommandScanCodes(int cmdId, out int[] scanCodes)
            => _remoteCmdScanCodes.TryGetValue(cmdId, out scanCodes) && scanCodes != null && scanCodes.Length > 0;

        public VirtualChannelMcxSessHandler(Logger m_logger) {
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

                        switch (createServiceClassID.ToString()) {
                            // DSMN ClassID
                            case "a30dc60e-1e2c-44f2-bfd1-17e51c0cdf19":
                                DSMNServiceHandle = createServiceServiceHandle;
                                m_logger?.LogDebug($"MCXSESS: CreateService DSMN ({DSMNServiceHandle})");
                                // Create new StatusChangedArgs
                                StatusChangedArgs args = new StatusChangedArgs {
                                    // Set the StatusChangedArgs Response Data
                                    shellOpen = false,
                                    statusText = "Starting Experience..."
                                };
                                // Raise Response Event
                                StatusChanged(this, args);
                                break;
                            default:
                                m_logger?.LogDebug($"MCXSESS: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
                                break;
                        }

                        // Initialise CreateService Response
                        byte[] response = DSLRCommunication.CreateServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }
                    // DeleteService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get DeleteService Data
                        int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        // If this is the DSMN Service
                        if (deleteServiceServiceHandle == DSMNServiceHandle) {
                            m_logger?.LogDebug($"MCXSESS: DeleteService DSMN ({DSMNServiceHandle})");
                            // Clear the DSMN Service
                            DSMNServiceHandle = 0;
                        } else {
                            m_logger?.LogDebug($"MCXSESS: DeleteService Handle ({deleteServiceServiceHandle}) not found");
                        }

                        // Initialise DeleteService Response
                        byte[] response = DSLRCommunication.DeleteServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger?.LogDebug($"MCXSESS: Unknown DSLR Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                }
                // DSMN Service Handle
                else if (dispatchServiceHandle == DSMNServiceHandle) {

                    #region DSMN Service ##########################################

                    // ShellDisconnect Request
                    if (dispatchFunctionHandle == 0) {

                        m_logger?.LogDebug("MCXSESS: ShellDisconnect");

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
                            statusInt = ShellDisconnectPayloadDisconnectReason,
                            statusText = newStatusString
                        };
                        // Raise Response Event
                        StatusChanged(this, args);

                        m_logger?.LogInfo("Experience closed");

                        // Initialise ShellDisconnect Response
                        byte[] response = DSLRCommunication.ShellDisconnectResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the ShellDisconnect Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }
                    // Heartbeat Request
                    else if (dispatchFunctionHandle == 1) {

                        //m_logger?.LogDebug("MCXSESS: Heartbeat");

                        // Get Heartbeat Data
                        int HeartbeatPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int HeartbeatChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int HeartbeatPayloadScreensaverFlag = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        // Initialise Heartbeat Response
                        byte[] response = DSLRCommunication.HeartbeatResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the Heartbeat Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }
                    // ShellIsActive Request
                    else if (dispatchFunctionHandle == 2) {

                        //m_logger?.LogDebug("MCXSESS: ShellIsActive");

                        // Create new StatusChangedArgs
                        StatusChangedArgs args = new StatusChangedArgs {
                            // Set the StatusChangedArgs Response Data
                            shellOpen = true,
                            statusText = ""
                        };
                        // Raise Response Event
                        StatusChanged(this, args);

                        // Initialise ShellIsActive Response
                        byte[] response = DSLRCommunication.ShellIsActiveResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the ShellIsActive Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }

                    // GetQWaveSinkInfo Request
                    else if (dispatchFunctionHandle == 3) {

                        //m_logger?.LogDebug("MCXSESS: GetQWaveSinkInfo");

                        // Get GetQWaveSinkInfo Data
                        int GetQWaveSinkInfoPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int GetQWaveSinkInfoChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);

                        // Initialise GetQWaveSinkInfo Response
                        byte[] response = DSLRCommunication.GetQWaveSinkInfoResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the GetQWaveSinkInfo Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }
                    // RegisterRemoteCommandBindings Request — function handle 4 in
                    // the DSMN service. Sent once at session start. The payload is
                    // a fixed table the WMC server uses to tell the extender how to
                    // map WMC RemoteCommand IDs (e.g. PlayPause, RecordedTV) to the
                    // keystrokes the extender should generate when the user presses
                    // the corresponding button on their remote control. Decoded by
                    // reverse engineering the unknown-handle bytes captured in
                    // mcxsess-unknown.txt — confirmed against MSDN's published WMC
                    // keyboard shortcuts (CTRL+M = Music, CTRL+E = Recorded TV,
                    // ALT+F4 = Close, etc.).
                    //
                    // Wire layout (all multi-byte fields are big-endian, matching
                    // the rest of the DSLR/DSMN protocol):
                    //   bytes  0-3   version       (observed 0x00010001 → "1.1")
                    //   bytes  4-7   cmdCount      (observed 76 = 0x4C)
                    //   bytes  8-11  scanCodeTotal (observed 155 = 0x9B, equals
                    //                                Σ arity over the cmd table)
                    //   bytes 12..   76 × (cmdId u32, arity u32) pairs
                    //                  arity ∈ {0..3} = scan codes consumed by
                    //                  this command from the trailing pool
                    //   bytes ...    155 × scan code (u32) entries, sliced by
                    //                  the arity sequence to recover per-cmd
                    //                  keystroke shortcuts
                    else if (dispatchFunctionHandle == 4) {

                        try {
                            int regPayloadSize  = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                            int regChildCount   = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                            int regBodyStart    = 6 + dispatchPayloadSize + 4 + 2;

                            // Header — fixed 12 bytes
                            int version       = DataUtilities.Get4ByteInt(incomingBuff, regBodyStart);
                            int cmdCount      = DataUtilities.Get4ByteInt(incomingBuff, regBodyStart + 4);
                            int scanCodeTotal = DataUtilities.Get4ByteInt(incomingBuff, regBodyStart + 8);

                            // Sanity-bound check — the spec'd table size is
                            // 12 + cmdCount*8 + scanCodeTotal*4 bytes. If the
                            // received payload is smaller, the server has sent
                            // a malformed registry (or our offset arithmetic
                            // is wrong) and we should bail before reading
                            // garbage off the end of incomingBuff.
                            int expectedBodySize = 12 + (cmdCount * 8) + (scanCodeTotal * 4);
                            if (cmdCount < 0 || cmdCount > 1024
                                || scanCodeTotal < 0 || scanCodeTotal > 4096
                                || expectedBodySize > regPayloadSize) {
                                m_logger?.LogDebug($"MCXSESS: RegisterRemoteCommandBindings — header values out of bounds " +
                                                   $"(version=0x{version:X8} cmdCount={cmdCount} scanCodeTotal={scanCodeTotal} " +
                                                   $"payloadSize={regPayloadSize}); skipping body decode.");
                            } else {
                                int cmdTableStart   = regBodyStart + 12;
                                int scanTableStart  = cmdTableStart + (cmdCount * 8);

                                m_logger?.LogInfo($"MCXSESS: RegisterRemoteCommandBindings v{(version >> 16) & 0xFFFF}.{version & 0xFFFF} " +
                                                  $"({cmdCount} commands, {scanCodeTotal} scan codes)");

                                // Walk the cmd table, slicing the scan-code
                                // pool by per-cmd arity. Logged at DEBUG to
                                // keep INFO clean — this fires once per
                                // session and produces ~76 lines.
                                int scanIdx = 0;
                                for (int i = 0; i < cmdCount; i++) {
                                    int cmdId = DataUtilities.Get4ByteInt(incomingBuff, cmdTableStart + (i * 8));
                                    int arity = DataUtilities.Get4ByteInt(incomingBuff, cmdTableStart + (i * 8) + 4);
                                    if (arity < 0 || arity > 3) {
                                        m_logger?.LogDebug($"MCXSESS: RemoteCmd[{cmdId}] arity={arity} (out of range, skipping)");
                                        continue;
                                    }
                                    string keystroke = "";
                                    if (arity == 0) {
                                        keystroke = "(no binding)";
                                    } else {
                                        var codes = new int[arity];
                                        for (int k = 0; k < arity; k++) {
                                            int sc = DataUtilities.Get4ByteInt(
                                                incomingBuff, scanTableStart + ((scanIdx + k) * 4));
                                            codes[k] = sc;
                                            if (k > 0) keystroke += "+";
                                            keystroke += DecodeScanCode(sc);
                                        }
                                        // Store for remote-control forwarding (see
                                        // TryGetRemoteCommandScanCodes).
                                        _remoteCmdScanCodes[cmdId] = codes;
                                    }
                                    m_logger?.LogDebug($"MCXSESS: RemoteCmd[{cmdId,3}] → {keystroke}");
                                    scanIdx += arity;
                                }
                            }
                        } catch (Exception ex) {
                            m_logger?.LogDebug($"MCXSESS: RegisterRemoteCommandBindings decode failed: {ex.Message}");
                        }

                        // ACK with the standard generic OK — the WMC server
                        // doesn't wait for any structured response here, it
                        // just needs to know the call completed.
                        byte[] response = DSLRCommunication.GenericOKResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        //m_logger?.LogDebug($"MCXSESS: Unknown Function ({dispatchFunctionHandle})");

                        // Get Unknown Data
                        int UnknownPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnknownChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        byte[] UnknownPayloadData;
                        if (UnknownPayloadSize > 0) {
                            UnknownPayloadData = DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, UnknownPayloadSize);
                            m_logger?.LogDebug("MCXSESS: " + BitConverter.ToString(UnknownPayloadData));
                            System.Diagnostics.Debug.WriteLine("MCXSESS BYTES: " + BitConverter.ToString(UnknownPayloadData));
                            //System.Diagnostics.Debug.WriteLine("MCXSESS ASCII: " + Encoding.ASCII.GetString(UnknownPayloadData));
                            //System.Diagnostics.Debug.WriteLine("MCXSESS    UN: " + Encoding.Unicode.GetString(UnknownPayloadData));
                            //System.Diagnostics.Debug.WriteLine("MCXSESS  UTF32: " + Encoding.UTF32.GetString(UnknownPayloadData));
                            //System.Diagnostics.Debug.WriteLine("MCXSESS  UTF8: " + Encoding.UTF8.GetString(UnknownPayloadData));
                            //System.Diagnostics.Debug.WriteLine("MCXSESS  UTF7: " + Encoding.UTF7.GetString(UnknownPayloadData));
                            //System.Diagnostics.Debug.WriteLine("MCXSESS BE UN: " + Encoding.BigEndianUnicode.GetString(UnknownPayloadData));
                        }

                        // Initialise Generic Response
                        byte[] response = DSLRCommunication.GenericOKResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 10, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the Generic Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("McxSess", encapsulatedResponse));

                    }

                    #endregion ####################################################

                } else {

                    m_logger?.LogDebug($"MCXSESS: Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

                }
            }
            //else if (dispatchCallingConvention == 2) {

            //    m_logger?.LogDebug($"MCXSESS: Unknown CallingConvention '{dispatchCallingConvention}' with Function '{dispatchRequestHandle}' not implemented");

            //} 
            else {

                m_logger?.LogDebug($"MCXSESS: Unknown CallingConvention '{dispatchCallingConvention}' with Function '{dispatchRequestHandle}' not implemented");

            }
        }

        /// <summary>
        /// Translate a PS/2 Set-1 keyboard scan code (as carried in the
        /// MCXSESS RegisterRemoteCommandBindings table) into a short
        /// human-readable label for diagnostic logging. The wire scan
        /// codes match the BIOS-era IBM PC scan code set, with a 0x100
        /// bit ORed in for the "extended" (E0-prefixed) keys — numpad,
        /// arrow keys, GUI keys, and the digit/star buttons synthesised
        /// by the WMC remote's number-pad.
        ///
        /// Only the codes we've actually observed in the captured
        /// corpus are named; everything else falls through to a hex
        /// rendering so the diagnostic still reads cleanly when WMC
        /// emits a new shortcut in a future build.
        /// </summary>
        private static string DecodeScanCode(int sc) {
            // Strip + report the "extended" flag if present.
            bool ext = (sc & 0x100) != 0;
            int  k   = sc & 0xFF;
            string name;
            switch (k) {
                // Modifiers
                case 0x1D: name = ext ? "RCTRL"  : "LCTRL";  break;
                case 0x2A: name = "LSHIFT"; break;
                case 0x36: name = "RSHIFT"; break;
                case 0x38: name = ext ? "RALT"   : "LALT";   break;
                case 0x5B: name = "LWIN";   break;
                case 0x5C: name = "RWIN";   break;
                // Letters (PS/2 Set 1 scan codes)
                case 0x10: name = "Q"; break;
                case 0x11: name = "W"; break;
                case 0x12: name = "E"; break;
                case 0x13: name = "R"; break;
                case 0x14: name = "T"; break;
                case 0x15: name = "Y"; break;
                case 0x16: name = "U"; break;
                case 0x17: name = "I"; break;
                case 0x18: name = "O"; break;
                case 0x19: name = "P"; break;
                case 0x1E: name = "A"; break;
                case 0x1F: name = "S"; break;
                case 0x20: name = "D"; break;
                case 0x21: name = "F"; break;
                case 0x22: name = "G"; break;
                case 0x23: name = "H"; break;
                case 0x24: name = "J"; break;
                case 0x25: name = "K"; break;
                case 0x26: name = "L"; break;
                case 0x2C: name = "Z"; break;
                case 0x2D: name = "X"; break;
                case 0x2E: name = "C"; break;
                case 0x2F: name = "V"; break;
                case 0x30: name = "B"; break;
                case 0x31: name = "N"; break;
                case 0x32: name = "M"; break;
                // Digits (top row)
                case 0x02: name = "1"; break;
                case 0x03: name = "2"; break;
                case 0x04: name = "3"; break;
                case 0x05: name = "4"; break;
                case 0x06: name = "5"; break;
                case 0x07: name = "6"; break;
                case 0x08: name = "7"; break;
                case 0x09: name = "8"; break;
                case 0x0A: name = "9"; break;
                case 0x0B: name = "0"; break;
                // F-keys + common controls
                case 0x3B: name = "F1";  break;
                case 0x3C: name = "F2";  break;
                case 0x3D: name = "F3";  break;
                case 0x3E: name = "F4";  break;
                case 0x3F: name = "F5";  break;
                case 0x40: name = "F6";  break;
                case 0x41: name = "F7";  break;
                case 0x42: name = "F8";  break;
                case 0x43: name = "F9";  break;
                case 0x44: name = "F10"; break;
                case 0x57: name = "F11"; break;
                case 0x58: name = "F12"; break;
                case 0x01: name = "ESC"; break;
                case 0x1C: name = "ENTER"; break;
                case 0x39: name = "SPACE"; break;
                case 0x0E: name = "BACKSP"; break;
                case 0x4B: name = "LEFT";  break;
                case 0x4D: name = "RIGHT"; break;
                case 0x48: name = "UP";    break;
                case 0x50: name = "DOWN";  break;
                default:
                    // Unknown — fall back to hex so the log still
                    // tells us what the wire actually carried.
                    return $"0x{sc:X4}";
            }
            return ext ? "E0+" + name : name;
        }
    }

    class StatusChangedArgs : EventArgs {
        public bool shellOpen;
        public int? statusInt;
        public string statusText;
    }
}

