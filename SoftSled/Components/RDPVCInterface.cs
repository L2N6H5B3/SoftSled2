using System;
using System.IO.Pipes;

namespace SoftSled.Components {
    public class RDPVCInterface {

        private Logger m_logger;
        public event EventHandler<DataReceived> DataReceived;
        private static string pipePrefix = "RDPVCManager_";
        NamedPipeServer mcxsessPipeServer { get; set; }
        NamedPipeServer devcapsPipeServer { get; set; }
        NamedPipeServer avctrlPipeServer { get; set; }

        public RDPVCInterface(Logger m_logger) {
            this.m_logger = m_logger;

            mcxsessPipeServer = new NamedPipeServer($"{pipePrefix}McxSess","McxSess");
            mcxsessPipeServer.OnReceivedMessage += new EventHandler<DataReceived>(Server_OnReceivedMessage);
            mcxsessPipeServer.Start();

            devcapsPipeServer = new NamedPipeServer($"{pipePrefix}devcaps", "devcaps");
            devcapsPipeServer.OnReceivedMessage += new EventHandler<DataReceived>(Server_OnReceivedMessage);
            devcapsPipeServer.Start();

            avctrlPipeServer = new NamedPipeServer($"{pipePrefix}avctrl", "avctrl");
            avctrlPipeServer.OnReceivedMessage += new EventHandler<DataReceived>(Server_OnReceivedMessage);
            avctrlPipeServer.Start();

            //// Create a thread to handle the named pipe for this channel
            //Thread pipeThread = new Thread(() => PipeThreadProc("McxSess"));
            //pipeThread.Start();

        }

        private void Server_OnReceivedMessage(object sender, DataReceived e) {
            // Raise Response Event
            DataReceived(this, e);
        }

        public bool SendOnVirtualChannel(string channelName, byte [] data) {
            switch (channelName) {
                case "McxSess":
                    // Write the data to the Virtual Channel Pipe
                    mcxsessPipeServer.Write(data);
                    break;
                case "devcaps":
                    // Write the data to the Virtual Channel Pipe
                    devcapsPipeServer.Write(data);
                    break;
                case "avctrl":
                    // Write the data to the Virtual Channel Pipe
                    avctrlPipeServer.Write(data);
                    break;
            }
            return true;
        }
    }
}
