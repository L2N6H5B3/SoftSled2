using System;
using System.Diagnostics;
using System.IO.Pipes;

namespace SoftSled.Components {
    public class NamedPipeClient : PipeStreamWrapperBase<NamedPipeClientStream> {
        public NamedPipeClient(string pipeName, string channelName) : base(pipeName, channelName) {

        }

        protected override NamedPipeClientStream CreateStream() {
            var stream = new NamedPipeClientStream(".",
                             PipeName,
                             PipeDirection.InOut,
                             PipeOptions.Asynchronous);
            stream.Connect();
            stream.ReadMode = PipeTransmissionMode.Message;
            return stream;
        }

        protected override bool AutoFlushPipeWriter {
            get { return true; }
        }

        protected override void ReadFromPipe(object state) {
            try {
                while (Pipe != null && m_stopRequested == false) {
                    if (Pipe.IsConnected == true) {
                        byte[] msg = ReadMessage(Pipe);

                        ThrowOnReceivedMessage(msg);
                    }
                }
            } catch (Exception ex) {
                Debug.WriteLine(ex.ToString());
            } finally {
                Debug.WriteLine("Client.Run() is exiting.");
            }
        }
    }
}
