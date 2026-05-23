using System;
using System.Collections.Generic;

namespace SoftSled.Components {
    public class RDPVCInterface {

        private Logger m_logger;
        public event EventHandler<DataReceived> DataReceived;
        private static string[] channelNames = { "McxSess", "devcaps", "avctrl", "MCECaps", "VCHD", "splash" };
        private static string pipePrefix = "RDPVCManager_";
        private static Dictionary<string, NamedPipeServer> pipeServers = new Dictionary<string, NamedPipeServer>();

        public RDPVCInterface(Logger m_logger) {
            this.m_logger = m_logger;

            foreach (string channelName in channelNames) {
                NamedPipeServer pipeServer = new NamedPipeServer($"{pipePrefix}{channelName}", channelName);
                pipeServer.OnReceivedMessage += new EventHandler<DataReceived>(Server_OnReceivedMessage);
                pipeServer.Start();
                pipeServers.Add(channelName, pipeServer);
            }
        }

        private void Server_OnReceivedMessage(object sender, DataReceived e) {
            // Raise Response Event
            DataReceived(this, e);
        }

        public bool SendOnVirtualChannel(string channelName, byte[] data) {
            NamedPipeServer pipeServer = pipeServers[channelName];
            if (pipeServer != null) {
                // Write the data to the Virtual Channel Pipe
                pipeServer.Write(data);
                return true;
            } else {
                return false;
            }
        }
    }
}
