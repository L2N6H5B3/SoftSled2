namespace Intel.UPNP
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Text;

    public sealed class SSDPSession
    {
        private MemoryStream Buffer;
        private byte[] MainBuffer;
        private AsyncSocket MainSocket;
        public object StateObject;
        private bool UNICAST;

        public event SessionHandler OnClosed;

        public event ReceiveHandler OnReceive;

        public event AsyncSocket.OnSendReadyHandler OnSendReady;

        public SSDPSession(Socket TheSocket, ReceiveHandler RequestCallback) : this(TheSocket, RequestCallback, false)
        {
        }

        public SSDPSession(Socket TheSocket, ReceiveHandler RequestCallback, bool Unicast)
        {
            this.UNICAST = false;
            this.MainBuffer = new byte[0x1000];
            this.Buffer = new MemoryStream();
            if (RequestCallback != null)
            {
                this.OnReceive = (ReceiveHandler) Delegate.Combine(this.OnReceive, RequestCallback);
            }
            this.MainSocket = new AsyncSocket(0x2000);
            this.MainSocket.Attach(TheSocket);
            this.MainSocket.SetTTL(4);
            this.MainSocket.OnReceive += new AsyncSocket.OnReceiveHandler(this.HandleReceive);
            this.MainSocket.OnDisconnect += new AsyncSocket.ConnectHandler(this.HandleDisconnect);
            this.MainSocket.OnSendReady += new AsyncSocket.OnSendReadyHandler(this.HandleReady);
            if (!Unicast)
            {
                this.UNICAST = true;
                if (((IPEndPoint) TheSocket.LocalEndPoint).Address.ToString() != "127.0.0.1")
                {
                    this.MainSocket.AddMembership((IPEndPoint) TheSocket.LocalEndPoint, IPAddress.Parse("239.255.255.250"));
                }
            }
            this.MainSocket.Begin();
        }

        public void CancelAllEvents()
        {
            this.OnReceive = null;
        }

        public void Close()
        {
            try
            {
                if (!this.UNICAST)
                {
                    this.MainSocket.DropMembership(IPAddress.Parse("239.255.255.250"));
                }
                this.MainSocket.Close();
            }
            catch (Exception)
            {
            }
            this.MainSocket = null;
        }

        private void HandleDisconnect(AsyncSocket sender)
        {
            if (this.OnClosed != null)
            {
                this.OnClosed(this);
            }
        }

        private void HandleReady(object Tag)
        {
            if (this.OnSendReady != null)
            {
                this.OnSendReady(Tag);
            }
        }

        private void HandleReceive(AsyncSocket sender, byte[] buffer, int HeadPointer, int BufferSize, int BytesRead, IPEndPoint source, IPEndPoint remote)
        {
            HTTPMessage msg = HTTPMessage.ParseByteArray(buffer, 0, BufferSize);
            msg.LocalEndPoint = source;
            msg.RemoteEndPoint = remote;
            sender.BufferBeginPointer = BufferSize;
            if (this.OnReceive != null)
            {
                this.OnReceive(this, msg);
            }
        }

        public void SendTo(HTTPMessage Packet, IPEndPoint dest)
        {
            Packet.DontShowContentLength = true;
            byte[] rawPacket = Packet.RawPacket;
            this.MainSocket.Send(rawPacket, 0, rawPacket.Length, dest, null);
        }

        public void SendTo(string Packet, IPEndPoint dest)
        {
            byte[] bytes = new UTF8Encoding().GetBytes(Packet);
            this.MainSocket.Send(bytes, 0, bytes.Length, dest, null);
        }

        public IPEndPoint Remote
        {
            get
            {
                return (IPEndPoint) this.MainSocket.RemoteEndPoint;
            }
        }

        public IPEndPoint Source
        {
            get
            {
                return (IPEndPoint) this.MainSocket.LocalEndPoint;
            }
        }

        public delegate void ReceiveHandler(SSDPSession Sender, HTTPMessage msg);

        public delegate void SessionHandler(SSDPSession TheSession);
    }
}

