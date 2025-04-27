using System;
using System.IO.Pipes;

namespace SoftSled.Components.Communication {
    public class NamedPipeServer : PipeStreamWrapperBase<NamedPipeServerStream> {
        public NamedPipeServer(string pipeName, string channelName) : base(pipeName, channelName) {

        }

        ~NamedPipeServer() {
            if (Pipe != null) Pipe.Dispose();
        }

        protected override bool AutoFlushPipeWriter {
            get { return true; }
        }

        protected override NamedPipeServerStream CreateStream() {
            return new NamedPipeServerStream(PipeName,
                       PipeDirection.InOut,
                       1,
                       PipeTransmissionMode.Message,
                       PipeOptions.Asynchronous,
                       BUFFER_SIZE,
                       BUFFER_SIZE);
        }

        protected override void ReadFromPipe(object state) {
            try {
                while (Pipe != null && m_stopRequested == false) {
                    if (Pipe.IsConnected == false) Pipe.WaitForConnection();

                    byte[] msg = ReadMessage(Pipe);

                    ThrowOnReceivedMessage(msg);
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
            }
        }
    }
}
