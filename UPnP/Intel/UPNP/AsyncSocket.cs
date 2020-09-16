namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Threading;

    public sealed class AsyncSocket
    {
        private Stream _WriteStream;
        public int BufferBeginPointer;
        internal int BufferEndPointer;
        public int BufferReadLength;
        public int BufferSize;
        private AsyncCallback ConnectCB;
        private object CountLock;
        private IPEndPoint endpoint_local;
        private EndPoint LocalEP;
        private byte[] MainBuffer;
        private Socket MainSocket;
        private WeakEvent OnConnectEvent;
        private WeakEvent OnConnectFailedEvent;
        private WeakEvent OnDisconnectEvent;
        private WeakEvent OnReceiveEvent;
        private WeakEvent OnSendReadyEvent;
        private int PendingBytesSent;
        private AsyncCallback ReceiveCB;
        private EndPoint RemoteEP;
        private EndPoint rEP;
        private AsyncCallback SendCB;
        private object SendLock;
        private System.Collections.Queue SendQueue;
        private bool SentDisconnect;
        private Thread StopThread;
        private long TotalBytesSent;

        public event ConnectHandler OnConnect
        {
            add
            {
                this.OnConnectEvent.Register(value);
            }
            remove
            {
                this.OnConnectEvent.UnRegister(value);
            }
        }

        public event ConnectHandler OnConnectFailed
        {
            add
            {
                this.OnConnectFailedEvent.Register(value);
            }
            remove
            {
                this.OnConnectFailedEvent.UnRegister(value);
            }
        }

        public event ConnectHandler OnDisconnect
        {
            add
            {
                this.OnDisconnectEvent.Register(value);
            }
            remove
            {
                this.OnDisconnectEvent.UnRegister(value);
            }
        }

        public event OnReceiveHandler OnReceive
        {
            add
            {
                this.OnReceiveEvent.Register(value);
            }
            remove
            {
                this.OnReceiveEvent.UnRegister(value);
            }
        }

        public event OnSendReadyHandler OnSendReady
        {
            add
            {
                this.OnSendReadyEvent.Register(value);
            }
            remove
            {
                this.OnSendReadyEvent.UnRegister(value);
            }
        }

        public AsyncSocket(int BufferSize)
        {
            this.StopThread = null;
            this.SentDisconnect = false;
            this.BufferReadLength = 0;
            this.BufferBeginPointer = 0;
            this.BufferEndPointer = 0;
            this.BufferSize = 0;
            this.OnReceiveEvent = new WeakEvent();
            this.OnSendReadyEvent = new WeakEvent();
            this.OnConnectEvent = new WeakEvent();
            this.OnConnectFailedEvent = new WeakEvent();
            this.OnDisconnectEvent = new WeakEvent();
            this._WriteStream = null;
            this.MainBuffer = new byte[BufferSize];
        }

        public AsyncSocket(Stream WriteStream)
        {
            this.StopThread = null;
            this.SentDisconnect = false;
            this.BufferReadLength = 0;
            this.BufferBeginPointer = 0;
            this.BufferEndPointer = 0;
            this.BufferSize = 0;
            this.OnReceiveEvent = new WeakEvent();
            this.OnSendReadyEvent = new WeakEvent();
            this.OnConnectEvent = new WeakEvent();
            this.OnConnectFailedEvent = new WeakEvent();
            this.OnDisconnectEvent = new WeakEvent();
            this._WriteStream = null;
            this._WriteStream = WriteStream;
            this.MainBuffer = new byte[0x1000];
        }

        public void AddMembership(IPEndPoint local, IPAddress MulticastAddress)
        {
            try
            {
                this.MainSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, 1);
            }
            catch (Exception)
            {
            }
            try
            {
                this.MainSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress));
            }
            catch (Exception)
            {
                EventLogger.Log(this, EventLogEntryType.Error, "Cannot AddMembership to IPAddress: " + MulticastAddress.ToString());
            }
            try
            {
                this.MainSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, (int) local.Address.Address);
            }
            catch (Exception)
            {
                EventLogger.Log(this, EventLogEntryType.Error, "Cannot Set Multicast Interface to IPAddress: " + local.Address.ToString());
            }
        }

        public void Attach(Socket UseThisSocket)
        {
            this.endpoint_local = (IPEndPoint) UseThisSocket.LocalEndPoint;
            this.TotalBytesSent = 0L;
            this.LocalEP = UseThisSocket.LocalEndPoint;
            if (UseThisSocket.SocketType == SocketType.Stream)
            {
                this.RemoteEP = UseThisSocket.RemoteEndPoint;
                this.endpoint_local = (IPEndPoint) UseThisSocket.LocalEndPoint;
            }
            else
            {
                this.RemoteEP = null;
            }
            this.MainSocket = UseThisSocket;
            PropertyInfo property = this.MainSocket.GetType().GetProperty("UseOnlyOverlappedIO");
            if (property != null)
            {
                property.SetValue(this.MainSocket, true, null);
            }
            this.Init();
        }

        public void Attach(IPEndPoint local, ProtocolType PType)
        {
            this.endpoint_local = local;
            this.TotalBytesSent = 0L;
            this.LocalEP = local;
            this.Init();
            this.MainSocket = null;
            if (PType == ProtocolType.Tcp)
            {
                this.MainSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, PType);
            }
            if (PType == ProtocolType.Udp)
            {
                this.MainSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, PType);
                this.MainSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            }
            if (this.MainSocket == null)
            {
                throw new Exception(PType.ToString() + " not supported");
            }
            this.MainSocket.Bind(local);
            PropertyInfo property = this.MainSocket.GetType().GetProperty("UseOnlyOverlappedIO");
            if (property != null)
            {
                property.SetValue(this.MainSocket, true, null);
            }
        }

        public void Begin()
        {
            IPEndPoint localEndPoint;
            IPEndPoint remoteEndPoint;
            bool flag = false;
            if (this.MainSocket.SocketType == SocketType.Stream)
            {
                remoteEndPoint = (IPEndPoint) this.MainSocket.RemoteEndPoint;
            }
            else
            {
                remoteEndPoint = (IPEndPoint) this.rEP;
            }
            try
            {
                localEndPoint = (IPEndPoint) this.MainSocket.LocalEndPoint;
            }
            catch (Exception)
            {
                localEndPoint = new IPEndPoint(IPAddress.Any, 0);
            }
            while ((this.BufferBeginPointer != 0) && (this.BufferBeginPointer != this.BufferEndPointer))
            {
                Array.Copy(this.MainBuffer, this.BufferBeginPointer, this.MainBuffer, 0, this.BufferEndPointer - this.BufferBeginPointer);
                this.BufferEndPointer -= this.BufferBeginPointer;
                this.BufferBeginPointer = 0;
                this.BufferSize = this.BufferEndPointer;
                try
                {
                    this.OnReceiveEvent.Fire(this, this.MainBuffer, this.BufferBeginPointer, this.BufferSize, 0, localEndPoint, remoteEndPoint);
                }
                catch (StopReadException)
                {
                    return;
                }
                if ((this.StopThread != null) && (Thread.CurrentThread.GetHashCode() == this.StopThread.GetHashCode()))
                {
                    this.StopThread = null;
                    return;
                }
            }
            try
            {
                if (this.MainSocket.SocketType == SocketType.Stream)
                {
                    this.MainSocket.BeginReceive(this.MainBuffer, this.BufferEndPointer, this.BufferReadLength, SocketFlags.None, this.ReceiveCB, null);
                }
                else
                {
                    this.MainSocket.BeginReceiveFrom(this.MainBuffer, this.BufferEndPointer, this.BufferReadLength, SocketFlags.None, ref this.rEP, this.ReceiveCB, null);
                }
            }
            catch (Exception)
            {
                flag = true;
            }
            if (flag)
            {
                bool flag2 = false;
                lock (this)
                {
                    if (!this.SentDisconnect)
                    {
                        flag2 = true;
                        this.SentDisconnect = true;
                    }
                }
                if (flag2)
                {
                    this.MainSocket = null;
                }
                if (flag && flag2)
                {
                    this.OnDisconnectEvent.Fire(this);
                }
            }
        }

        public void Close()
        {
            if (this.MainSocket != null)
            {
                try
                {
                    this.MainSocket.Shutdown(SocketShutdown.Both);
                    this.MainSocket.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        public void Connect(IPEndPoint Remote)
        {
            if (this.MainSocket.SocketType != SocketType.Stream)
            {
                throw new Exception("Cannot connect a non StreamSocket");
            }
            PropertyInfo property = this.MainSocket.GetType().GetProperty("UseOnlyOverlappedIO");
            if (property != null)
            {
                property.SetValue(this.MainSocket, true, null);
            }
            this.MainSocket.BeginConnect(Remote, this.ConnectCB, null);
        }

        public void DropMembership(IPAddress MulticastAddress)
        {
            this.MainSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(MulticastAddress));
        }

        private void HandleConnect(IAsyncResult result)
        {
            bool flag = false;
            try
            {
                this.MainSocket.EndConnect(result);
                flag = true;
                this.RemoteEP = this.MainSocket.RemoteEndPoint;
            }
            catch (Exception)
            {
            }
            if (flag && this.MainSocket.Connected)
            {
                this.OnConnectEvent.Fire(this);
            }
            else
            {
                this.OnConnectFailedEvent.Fire(this);
            }
        }

        private void HandleReceive(IAsyncResult result)
        {
            IPEndPoint remoteEndPoint;
            int num = 0;
            bool flag = false;
            try
            {
                if (this.MainSocket.SocketType == SocketType.Stream)
                {
                    remoteEndPoint = (IPEndPoint) this.MainSocket.RemoteEndPoint;
                    num = this.MainSocket.EndReceive(result);
                }
                else
                {
                    num = this.MainSocket.EndReceiveFrom(result, ref this.rEP);
                    remoteEndPoint = (IPEndPoint) this.rEP;
                }
            }
            catch (Exception ex)
            {
                bool flag2 = false;
                lock (this)
                {
                    if (!this.SentDisconnect)
                    {
                        flag2 = true;
                        this.SentDisconnect = true;
                    }
                }
                if (flag2)
                {
                    this.MainSocket = null;
                }
                if (flag2)
                {
                    this.OnDisconnectEvent.Fire(this);
                }
                return;
            }
            if (num <= 0)
            {
                flag = true;
            }
            if (num != 0)
            {
                IPEndPoint localEndPoint;
                try
                {
                    localEndPoint = (IPEndPoint) this.MainSocket.LocalEndPoint;
                }
                catch (Exception)
                {
                    localEndPoint = new IPEndPoint(IPAddress.Any, 0);
                }
                this.BufferEndPointer += num;
                this.BufferSize = this.BufferEndPointer - this.BufferBeginPointer;
                this.BufferReadLength = this.MainBuffer.Length - this.BufferEndPointer;
                if (this._WriteStream == null)
                {
                    try
                    {
                        this.OnReceiveEvent.Fire(this, this.MainBuffer, this.BufferBeginPointer, this.BufferSize, num, localEndPoint, remoteEndPoint);
                    }
                    catch (StopReadException)
                    {
                        return;
                    }
                }
                else
                {
                    this._WriteStream.Write(this.MainBuffer, 0, num);
                    this.BufferBeginPointer = this.BufferEndPointer;
                    this.BufferReadLength = this.MainBuffer.Length;
                }
                while ((this.BufferBeginPointer != 0) && (this.BufferBeginPointer != this.BufferEndPointer))
                {
                    Array.Copy(this.MainBuffer, this.BufferBeginPointer, this.MainBuffer, 0, this.BufferEndPointer - this.BufferBeginPointer);
                    this.BufferEndPointer -= this.BufferBeginPointer;
                    this.BufferBeginPointer = 0;
                    this.BufferSize = this.BufferEndPointer;
                    try
                    {
                        this.OnReceiveEvent.Fire(this, this.MainBuffer, this.BufferBeginPointer, this.BufferSize, 0, localEndPoint, remoteEndPoint);
                    }
                    catch (StopReadException)
                    {
                        return;
                    }
                    if ((this.StopThread != null) && (Thread.CurrentThread.GetHashCode() == this.StopThread.GetHashCode()))
                    {
                        this.StopThread = null;
                        return;
                    }
                }
                if (this.BufferBeginPointer == this.BufferEndPointer)
                {
                    this.BufferBeginPointer = 0;
                    this.BufferEndPointer = 0;
                }
                if ((this.StopThread != null) && (Thread.CurrentThread.GetHashCode() == this.StopThread.GetHashCode()))
                {
                    this.StopThread = null;
                    return;
                }
                try
                {
                    if (this.MainSocket != null)
                    {
                        if (this.MainSocket.SocketType == SocketType.Stream)
                        {
                            this.MainSocket.BeginReceive(this.MainBuffer, this.BufferEndPointer, this.BufferReadLength, SocketFlags.None, this.ReceiveCB, this.MainSocket);
                        }
                        else
                        {
                            this.MainSocket.BeginReceiveFrom(this.MainBuffer, this.BufferEndPointer, this.BufferReadLength, SocketFlags.None, ref this.rEP, this.ReceiveCB, this.MainSocket);
                        }
                    }
                    else
                    {
                        flag = true;
                    }
                }
                catch (Exception)
                {
                    flag = true;
                }
            }
            if (flag)
            {
                bool flag3 = false;
                lock (this)
                {
                    if (!this.SentDisconnect)
                    {
                        flag3 = true;
                        this.SentDisconnect = true;
                    }
                }
                if (flag3)
                {
                    this.MainSocket = null;
                }
                if (flag && flag3)
                {
                    this.OnDisconnectEvent.Fire(this);
                }
            }
        }

        private void HandleSend(IAsyncResult result)
        {
            int num = 0;
            bool flag = false;
            bool flag2 = false;
            try
            {
                lock (this.SendLock)
                {
                    try
                    {
                        if (this.MainSocket.SocketType == SocketType.Stream)
                        {
                            num = this.MainSocket.EndSend(result);
                        }
                        else
                        {
                            num = this.MainSocket.EndSendTo(result);
                        }
                    }
                    catch (Exception)
                    {
                        flag2 = true;
                    }
                    lock (this.CountLock)
                    {
                        this.PendingBytesSent -= num;
                        this.TotalBytesSent += num;
                    }
                    if (this.SendQueue.Count > 0)
                    {
                        SendInfo info = (SendInfo) this.SendQueue.Dequeue();
                        try
                        {
                            if (this.MainSocket.SocketType == SocketType.Stream)
                            {
                                this.MainSocket.BeginSend(info.buffer, info.offset, info.count, SocketFlags.None, this.SendCB, info.Tag);
                            }
                            else
                            {
                                this.MainSocket.BeginSendTo(info.buffer, info.offset, info.count, SocketFlags.None, info.dest, this.SendCB, info.Tag);
                            }
                        }
                        catch (Exception)
                        {
                            EventLogger.Log(this, EventLogEntryType.Error, "Send Failure [Normal for non-pipelined connection]");
                            flag2 = true;
                        }
                    }
                    else
                    {
                        flag = true;
                    }
                }
                if (flag2)
                {
                    bool flag3 = false;
                    lock (this)
                    {
                        if (!this.SentDisconnect)
                        {
                            flag3 = true;
                            this.SentDisconnect = true;
                        }
                    }
                    if (flag3)
                    {
                        this.MainSocket = null;
                    }
                    if (flag3)
                    {
                        this.OnDisconnectEvent.Fire(this);
                    }
                }
                else if (flag)
                {
                    this.OnSendReadyEvent.Fire(result.AsyncState);
                }
            }
            catch (Exception exception)
            {
                EventLogger.Log(exception);
            }
        }

        private void Init()
        {
            this.BufferReadLength = this.MainBuffer.Length;
            this.CountLock = new object();
            this.PendingBytesSent = 0;
            this.SendLock = new object();
            this.SendQueue = new System.Collections.Queue();
            this.ReceiveCB = new AsyncCallback(this.HandleReceive);
            this.SendCB = new AsyncCallback(this.HandleSend);
            this.ConnectCB = new AsyncCallback(this.HandleConnect);
            this.rEP = new IPEndPoint(0L, 0);
        }

        public void Send(byte[] buffer)
        {
            this.Send(buffer, null);
        }

        public void Send(byte[] buffer, object Tag)
        {
            this.Send(buffer, 0, buffer.Length, Tag);
        }

        public void Send(byte[] buffer, int offset, int length, IPEndPoint dest)
        {
            this.Send(buffer, offset, length, dest, null);
        }

        public void Send(byte[] buffer, int offset, int length, object Tag)
        {
            this.Send(buffer, offset, length, null, Tag);
        }

        public void Send(byte[] buffer, int offset, int length, IPEndPoint dest, object Tag)
        {
            bool flag = false;
            lock (this.SendLock)
            {
                lock (this.CountLock)
                {
                    if (this.PendingBytesSent > 0)
                    {
                        SendInfo info = new SendInfo();
                        info.buffer = buffer;
                        info.offset = offset;
                        info.count = length;
                        info.dest = dest;
                        info.Tag = Tag;
                        this.SendQueue.Enqueue(info);
                    }
                    else
                    {
                        this.PendingBytesSent += length;
                        try
                        {
                            if (this.MainSocket.SocketType == SocketType.Stream)
                            {
                                this.MainSocket.BeginSend(buffer, offset, length, SocketFlags.None, this.SendCB, Tag);
                            }
                            else
                            {
                                this.MainSocket.BeginSendTo(buffer, offset, length, SocketFlags.None, dest, this.SendCB, Tag);
                            }
                        }
                        catch
                        {
                            EventLogger.Log(this, EventLogEntryType.Error, "Send Failure [Normal for non-pipelined connection]");
                            flag = true;
                        }
                    }
                }
            }
            if (flag)
            {
                bool flag2 = false;
                lock (this)
                {
                    if (!this.SentDisconnect)
                    {
                        flag2 = true;
                        this.SentDisconnect = true;
                    }
                }
                if (flag2)
                {
                    this.MainSocket = null;
                    this.OnDisconnectEvent.Fire(this);
                }
            }
        }

        public void SetTTL(int TTL)
        {
            this.MainSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, TTL);
        }

        public void StopReading()
        {
            this.StopThread = Thread.CurrentThread;
        }

        public EndPoint LocalEndPoint
        {
            get
            {
                if (this.MainSocket.LocalEndPoint != null)
                {
                    return (IPEndPoint) this.MainSocket.LocalEndPoint;
                }
                return this.endpoint_local;
            }
        }

        public int Pending
        {
            get
            {
                return this.PendingBytesSent;
            }
        }

        public EndPoint RemoteEndPoint
        {
            get
            {
                return this.MainSocket.RemoteEndPoint;
            }
        }

        public long Total
        {
            get
            {
                return this.TotalBytesSent;
            }
        }

        public delegate void ConnectHandler(AsyncSocket sender);

        public delegate void OnReceiveHandler(AsyncSocket sender, byte[] buffer, int HeadPointer, int BufferSize, int BytesRead, IPEndPoint source, IPEndPoint remote);

        public delegate void OnSendReadyHandler(object Tag);

        [StructLayout(LayoutKind.Sequential)]
        private struct SendInfo
        {
            public byte[] buffer;
            public int offset;
            public int count;
            public object Tag;
            public IPEndPoint dest;
        }

        public class StopReadException : Exception
        {
            public StopReadException() : base("User initiated StopRead")
            {
            }
        }
    }
}

