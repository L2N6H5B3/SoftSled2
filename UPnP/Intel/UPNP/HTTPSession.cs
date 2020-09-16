namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    public sealed class HTTPSession
    {
        private int _Counter;
        private int BodySize;
        public static bool CHUNK_ENABLED = true;
        private int ChunkDataSize;
        private bool ChunkedHeadersAdded;
        private int ChunkState;
        private bool Connected;
        internal bool ConnectionCloseSpecified;
        public static int DATA_CHUNK = 0x38;
        private long EndPosition;
        public static int FIN_CHUNK_CRLF = 60;
        private bool FinishedHeader;
        public static int FOOTER_DATA = 0x3a;
        private HTTPMessage Headers;
        internal object InternalStateObject;
        internal bool IsChunked;
        private bool IsLegacy;
        private IPEndPoint local_ep;
        private AsyncSocket MainSocket;
        public ActivityMonitor Monitor;
        private bool NeedToWaitToClose;
        public static int NO_CHUNK = 0;
        private WeakEvent OnClosedEvent;
        private WeakEvent OnCreateFailedEvent;
        private WeakEvent OnCreateSessionEvent;
        private WeakEvent OnHeaderEvent;
        private WeakEvent OnReceiveEvent;
        private WeakEvent OnRequestAnsweredEvent;
        private WeakEvent OnSendReadyEvent;
        private WeakEvent OnSniffEvent;
        private WeakEvent OnSniffPacketEvent;
        private WeakEvent OnStreamDoneEvent;
        private Stream PostStream;
        private byte[] SendBuffer;
        private System.Collections.Queue SendQueue;
        private bool SetRequestAnswered;
        private MemoryStream SocketStream;
        public static int START_CHUNK = 0x37;
        public static int START_FOOTER_CHUNK = 0x39;
        public object StateObject;
        private System.Collections.Queue StateQueue;
        private object StreamLock;
        private byte[] StreamSendBuffer;
        private static UTF8Encoding U = new UTF8Encoding();
        public Stream UserStream;

        public event SessionHandler OnClosed
        {
            add
            {
                this.OnClosedEvent.Register(value);
            }
            remove
            {
                this.OnClosedEvent.UnRegister(value);
            }
        }

        public event SessionHandler OnCreateFailed
        {
            add
            {
                this.OnCreateFailedEvent.Register(value);
            }
            remove
            {
                this.OnCreateFailedEvent.UnRegister(value);
            }
        }

        public event SessionHandler OnCreateSession
        {
            add
            {
                this.OnCreateSessionEvent.Register(value);
            }
            remove
            {
                this.OnCreateSessionEvent.UnRegister(value);
            }
        }

        public event ReceiveHeaderHandler OnHeader
        {
            add
            {
                this.OnHeaderEvent.Register(value);
            }
            remove
            {
                this.OnHeaderEvent.UnRegister(value);
            }
        }

        public event ReceiveHandler OnReceive
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

        public event SessionHandler OnRequestAnswered
        {
            add
            {
                this.OnRequestAnsweredEvent.Register(value);
            }
            remove
            {
                this.OnRequestAnsweredEvent.UnRegister(value);
            }
        }

        public event AsyncSocket.OnSendReadyHandler OnSendReady
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

        internal event SniffHandler OnSniff
        {
            add
            {
                this.OnSniffEvent.Register(value);
            }
            remove
            {
                this.OnSniffEvent.UnRegister(value);
            }
        }

        public event ReceiveHandler OnSniffPacket
        {
            add
            {
                this.OnSniffPacketEvent.Register(value);
            }
            remove
            {
                this.OnSniffPacketEvent.UnRegister(value);
            }
        }

        public event StreamDoneHandler OnStreamDone
        {
            add
            {
                this.OnStreamDoneEvent.Register(value);
            }
            remove
            {
                this.OnStreamDoneEvent.UnRegister(value);
            }
        }

        public HTTPSession(IPEndPoint Local, Socket TheSocket)
        {
            this._Counter = 0;
            this.Monitor = new ActivityMonitor();
            this.SetRequestAnswered = false;
            this.IsLegacy = false;
            this.StateQueue = new System.Collections.Queue();
            this.PostStream = null;
            this.UserStream = null;
            this.SendQueue = new System.Collections.Queue();
            this.SendBuffer = new byte[0x1000];
            this.Connected = false;
            this.SocketStream = new MemoryStream();
            this.FinishedHeader = false;
            this.IsChunked = false;
            this.ConnectionCloseSpecified = false;
            this.BodySize = 0;
            this.ChunkDataSize = 0;
            this.Headers = null;
            this.ChunkedHeadersAdded = false;
            this.ChunkState = NO_CHUNK;
            this.local_ep = null;
            this.NeedToWaitToClose = false;
            this.OnReceiveEvent = new WeakEvent();
            this.OnSniffPacketEvent = new WeakEvent();
            this.OnHeaderEvent = new WeakEvent();
            this.OnRequestAnsweredEvent = new WeakEvent();
            this.OnStreamDoneEvent = new WeakEvent();
            this.OnCreateSessionEvent = new WeakEvent();
            this.OnCreateFailedEvent = new WeakEvent();
            this.OnClosedEvent = new WeakEvent();
            this.OnSendReadyEvent = new WeakEvent();
            this.OnSniffEvent = new WeakEvent();
            this.StreamSendBuffer = new byte[0x8000];
            this.EndPosition = 0L;
            this.InternalStateObject = null;
            this.StreamLock = new object();
            InstanceTracker.Add(this);
            this.Connected = TheSocket.Connected;
            this.local_ep = Local;
            this.MainSocket = new AsyncSocket(0x1000); //0x1000
            this.MainSocket.Attach(TheSocket);
            this.MainSocket.OnReceive += new AsyncSocket.OnReceiveHandler(this.HandleReceive);
            this.MainSocket.OnDisconnect += new AsyncSocket.ConnectHandler(this.HandleDisconnect);
            this.MainSocket.OnSendReady += new AsyncSocket.OnSendReadyHandler(this.HandleReady);
            this.MainSocket.BufferReadLength = 0x400;
        }

        public HTTPSession(Socket TheSocket, ReceiveHeaderHandler HeaderCallback, ReceiveHandler RequestCallback) : this((IPEndPoint) TheSocket.LocalEndPoint, TheSocket, HeaderCallback, RequestCallback)
        {
        }

        public HTTPSession(IPEndPoint Local, Socket TheSocket, ReceiveHeaderHandler HeaderCallback, ReceiveHandler RequestCallback) : this(Local, TheSocket)
        {
            if (HeaderCallback != null)
            {
                this.OnHeader += HeaderCallback;
            }
            if (RequestCallback != null)
            {
                this.OnReceive += RequestCallback;
            }
            this.MainSocket.Begin();
        }

        public HTTPSession(IPEndPoint Local, IPEndPoint Remote, SessionHandler CreateCallback, SessionHandler CreateFailedCallback, object State)
        {
            this._Counter = 0;
            this.Monitor = new ActivityMonitor();
            this.SetRequestAnswered = false;
            this.IsLegacy = false;
            this.StateQueue = new System.Collections.Queue();
            this.PostStream = null;
            this.UserStream = null;
            this.SendQueue = new System.Collections.Queue();
            this.SendBuffer = new byte[0x1000];
            this.Connected = false;
            this.SocketStream = new MemoryStream();
            this.FinishedHeader = false;
            this.IsChunked = false;
            this.ConnectionCloseSpecified = false;
            this.BodySize = 0;
            this.ChunkDataSize = 0;
            this.Headers = null;
            this.ChunkedHeadersAdded = false;
            this.ChunkState = NO_CHUNK;
            this.local_ep = null;
            this.NeedToWaitToClose = false;
            this.OnReceiveEvent = new WeakEvent();
            this.OnSniffPacketEvent = new WeakEvent();
            this.OnHeaderEvent = new WeakEvent();
            this.OnRequestAnsweredEvent = new WeakEvent();
            this.OnStreamDoneEvent = new WeakEvent();
            this.OnCreateSessionEvent = new WeakEvent();
            this.OnCreateFailedEvent = new WeakEvent();
            this.OnClosedEvent = new WeakEvent();
            this.OnSendReadyEvent = new WeakEvent();
            this.OnSniffEvent = new WeakEvent();
            this.StreamSendBuffer = new byte[0x8000];
            this.EndPosition = 0L;
            this.InternalStateObject = null;
            this.StreamLock = new object();
            InstanceTracker.Add(this);
            this.local_ep = Local;
            this.OnCreateSession += CreateCallback;
            this.OnCreateFailed += CreateFailedCallback;
            this.StateObject = State;
            this.MainSocket = new AsyncSocket(0x1000);
            this.MainSocket.Attach(Local, ProtocolType.Tcp);
            this.MainSocket.OnConnect += new AsyncSocket.ConnectHandler(this.HandleConnect);
            this.MainSocket.OnConnectFailed += new AsyncSocket.ConnectHandler(this.HandleConnectFailed);
            this.MainSocket.OnDisconnect += new AsyncSocket.ConnectHandler(this.HandleDisconnect);
            this.MainSocket.OnSendReady += new AsyncSocket.OnSendReadyHandler(this.HandleReady);
            this.MainSocket.Connect(Remote);
        }

        private void BeginChunk(int StartPosition)
        {
            this.MainSocket.BufferReadLength = 0x400;
            this.MainSocket.BufferBeginPointer = StartPosition;
            this.ChunkState = START_CHUNK;
        }

        private void BeginHeader()
        {
            if (this.MainSocket != null)
            {
                this.BeginHeader(this.MainSocket.BufferSize);
            }
        }

        private void BeginHeader(int StartPointer)
        {
            if (this.MainSocket != null)
            {
                this.MainSocket.BufferBeginPointer = StartPointer;
                this.MainSocket.BufferReadLength = 0x400;
                this.FinishedHeader = false;
                this.IsChunked = false;
                this.ChunkedHeadersAdded = false;
                this.ConnectionCloseSpecified = false;
                this.UserStream = null;
                this.SocketStream = new MemoryStream();
            }
        }

        public void CancelAllEvents()
        {
            this.OnClosedEvent.UnRegisterAll();
            this.OnCreateFailedEvent.UnRegisterAll();
            this.OnCreateSessionEvent.UnRegisterAll();
            this.OnHeaderEvent.UnRegisterAll();
            this.OnSendReadyEvent.UnRegisterAll();
            this.OnSniffEvent.UnRegisterAll();
            this.OnSniffPacketEvent.UnRegisterAll();
            this.OnStreamDoneEvent.UnRegisterAll();
            this.OnRequestAnsweredEvent.UnRegisterAll();
            this.OnReceiveEvent.UnRegisterAll();
        }

        public void Close()
        {
            try
            {
                if (this.MainSocket != null)
                {
                    this.MainSocket.Close();
                }
            }
            catch (Exception)
            {
            }
            this.MainSocket = null;
        }

        public void CloseStreamObject(Stream stream)
        {
            stream.Close();
            lock (this.StreamLock)
            {
                if (this.PostStream == stream)
                {
                    this.PostStream = null;
                }
            }
            lock (this.StateQueue)
            {
                if (this.StateQueue.Count > 0)
                {
                    Info info2 = (Info) this.StateQueue.Peek();
                    if (info2.CurrentStreamObject == stream)
                    {
                        Info info = (Info) this.StateQueue.Peek();
                        info.CurrentStreamObject = null;
                    }
                }
            }
        }

        private void DONE_ReadingPacket()
        {
            this.OnRequestAnsweredEvent.Fire(this);
        }

        ~HTTPSession()
        {
            if (this.MainSocket != null)
            {
                this.MainSocket.Close();
                this.MainSocket = null;
            }
        }

        private void FinishChunked(object Tag)
        {
            lock (this)
            {
                if (this.MainSocket != null)
                {
                    this.MainSocket.Send(U.GetBytes("0\r\n\r\n"), Tag);
                }
            }
        }

        public void FinishedProcessing()
        {
            this.MainSocket.Begin();
        }

        private void HandleConnect(AsyncSocket sender)
        {
            this.MainSocket.OnReceive += new AsyncSocket.OnReceiveHandler(this.HandleReceive);
            this.MainSocket.BufferReadLength = 0x400;
            this.Connected = true;
            this.OnCreateSessionEvent.Fire(this);
            this.MainSocket.Begin();
        }

        private void HandleConnectFailed(AsyncSocket sender)
        {
            this.OnCreateFailedEvent.Fire(this);
        }

        private void HandleDisconnect(AsyncSocket sender)
        {
            Info info;
            this.Connected = false;
            lock (this)
            {
                this.MainSocket = null;
            }
            if (this.NeedToWaitToClose)
            {
                if (this.UserStream == null)
                {
                    this.SocketStream.Flush();
                    this.Headers.BodyBuffer = this.SocketStream.ToArray();
                    if (EventLogger.Enabled)
                    {
                        EventLogger.Log(this, EventLogEntryType.Information, this.Headers.StringPacket);
                    }
                    this.OnSniffPacketEvent.Fire(this, this.Headers);
                    this.OnReceiveEvent.Fire(this, this.Headers);
                }
                this.DONE_ReadingPacket();
            }
            if (this.PostStream != null)
            {
                this.OnStreamDoneEvent.Fire(this, this.PostStream);
            }
            info.CurrentStreamObject = null;
            lock (this.StateQueue)
            {
                if (this.StateQueue.Count > 0)
                {
                    info = (Info) this.StateQueue.Peek();
                }
            }
            if (info.CurrentStreamObject != null)
            {
                this.OnStreamDoneEvent.Fire(this, info.CurrentStreamObject);
            }
            this.OnClosedEvent.Fire(this);
        }

        private void HandleReady(object Tag)
        {
            Stream state = null;
            Info info;
            info.RangeList = null;
            info.RangeIndex = 0;
            lock (this.StateQueue)
            {
                if (this.StateQueue.Count > 0)
                {
                    info = (Info) this.StateQueue.Peek();
                }
            }
            if (Tag != null)
            {
                if (Tag.GetType().FullName == "System.Boolean")
                {
                    if ((bool) Tag)
                    {
                        this.ParseQueue();
                    }
                }
                else
                {
                    state = (Stream) Tag;
                    if (this.IsConnected)
                    {
                        int count = 0x1000;
                        if (info.RangeList != null)
                        {
                            if (info.RangeIndex == info.RangeList.Length)
                            {
                                count = 0;
                            }
                            else if (info.RangeList[info.RangeIndex].RangeLeft < 0x1000L)
                            {
                                count = (int) info.RangeList[info.RangeIndex].RangeLeft;
                            }
                        }
                        state.BeginRead(this.SendBuffer, 0, count, new AsyncCallback(this.StreamSendCallback), state);
                    }
                }
            }
            this.OnSendReadyEvent.Fire(Tag);
        }

        private void HandleReceive(AsyncSocket sender, byte[] buffer, int BeginPointer, int BufferSize, int BytesRead, IPEndPoint source, IPEndPoint remote)
        {
            if (BytesRead != 0)
            {
                this.OnSniffEvent.Fire(buffer, BufferSize - BytesRead, BytesRead);
            }
            if (this.FinishedHeader)
            {
                if (!this.IsChunked)
                {
                    if (!this.NeedToWaitToClose && (BufferSize > this.BodySize))
                    {
                        BufferSize = this.BodySize;
                    }
                    if (this.UserStream != null)
                    {
                        this.UserStream.Write(buffer, 0, BufferSize);
                    }
                    else
                    {
                        this.SocketStream.Write(buffer, 0, BufferSize);
                    }
                    if (!this.NeedToWaitToClose)
                    {
                        this.BodySize -= BufferSize;
                        if (this.BodySize > 0)
                        {
                            sender.BufferBeginPointer = BufferSize;
                            if (this.BodySize < 0x1000)
                            {
                                sender.BufferReadLength = this.BodySize;
                            }
                            else
                            {
                                sender.BufferReadLength = 0x1000;
                            }
                        }
                        else
                        {
                            if (this.UserStream == null)
                            {
                                this.SocketStream.Flush();
                                this.Headers.BodyBuffer = this.SocketStream.ToArray();
                                if (EventLogger.Enabled)
                                {
                                    EventLogger.Log(this, EventLogEntryType.Information, this.Headers.StringPacket);
                                }
                                this.OnSniffPacketEvent.Fire(this, this.Headers);
                                this.OnReceiveEvent.Fire(this, this.Headers);
                            }
                            else
                            {
                                this.UserStream.Flush();
                                this.OnStreamDoneEvent.Fire(this, this.UserStream);
                            }
                            this.DONE_ReadingPacket();
                            this.BeginHeader(BufferSize);
                        }
                    }
                    else
                    {
                        sender.BufferReadLength = 0x1000;
                        sender.BufferBeginPointer = BufferSize;
                    }
                }
                else
                {
                    this.ProcessChunk(buffer, BufferSize);
                }
                return;
            }
            if (BufferSize < 4)
            {
                sender.BufferReadLength = 1;
                sender.BufferBeginPointer = 0;
                return;
            }
            for (int i = 4; i < (BufferSize - 4); i++)
            {
                if (((buffer[i - 4] == 13) && (buffer[i - 3] == 10)) && ((buffer[i - 2] == 13) && (buffer[i - 1] == 10)))
                {
                    BufferSize = i;
                    break;
                }
            }
            //MOD this is checking for \r\n\r\n
            byte b4_13 = buffer[BufferSize - 4];
            byte b3_10 = buffer[BufferSize - 3];
            byte b2_13 = buffer[BufferSize - 2];
            byte b1_10 = buffer[BufferSize - 1];
            //if (((b4_13 != 13) || (b3_10 != 10)) || ((b2_13 != 13) || (b1_10 != 10)))
            if (((b4_13 != 13) || (b3_10 != 10)) || ((b2_13 != 13) || (b1_10 != 10)))
            {
                if ((b3_10 != 13) || (b2_13 != 10))
                {
                    sender.BufferBeginPointer = 0;
                    sender.BufferReadLength = 1;
                    return; //MOD pictures requests all return here
                }
                else
                {
                    //BufferSize = BufferSize - 1;
                    //byte[] tempBuff = new byte[BufferSize];
                    //Array.Copy(buffer, 0, tempBuff, 0, BufferSize);
                    //buffer = tempBuff;
                }
            }
            this.Headers = HTTPMessage.ParseByteArray(buffer, 0, BufferSize);
            if (this.Headers.StatusCode != -1)
            {
                if ((this.Headers.StatusCode >= 100) && (this.Headers.StatusCode <= 0xc7))
                {
                    if (EventLogger.Enabled)
                    {
                        EventLogger.Log(this, EventLogEntryType.Information, "<<IGNORING>>\r\n" + this.Headers.StringPacket);
                    }
                    this.OnSniffPacketEvent.Fire(this, this.Headers);
                    this.BeginHeader(BufferSize);
                }
                else
                {
                    if ((this.Headers.StatusCode != 0xcc) && (this.Headers.StatusCode != 0x130))
                    {
                        goto Label_01C7;
                    }
                    if (EventLogger.Enabled)
                    {
                        EventLogger.Log(this, EventLogEntryType.Information, this.Headers.StringPacket);
                    }
                    this.OnHeaderEvent.Fire(this, this.Headers, this.UserStream);
                    this.OnSniffPacketEvent.Fire(this, this.Headers);
                    this.OnReceiveEvent.Fire(this, this.Headers);
                    this.DONE_ReadingPacket();
                    this.BeginHeader(BufferSize);
                }
                return;
            }
            this.SET_START_OF_REQUEST();
        Label_01C7:
            this.FinishedHeader = true;
            if (this.Headers.GetTag("Content-Length") == "")
            {
                if (this.Headers.GetTag("Transfer-Encoding").ToUpper() == "CHUNKED")
                {
                    this.IsChunked = true;
                }
                else if (this.Headers.StatusCode != -1)
                {
                    this.NeedToWaitToClose = true;
                }
            }
            else if (this.Headers.GetTag("Transfer-Encoding").ToUpper() == "CHUNKED")
            {
                this.IsChunked = true;
            }
            else
            {
                this.BodySize = int.Parse(this.Headers.GetTag("Content-Length"));
            }
            if (this.Headers.GetTag("Connection").ToUpper() == "CLOSE")
            {
                this.ConnectionCloseSpecified = true;
            }
            if (((!this.IsChunked && this.NeedToWaitToClose) && (!this.ConnectionCloseSpecified && !this.IsLegacy)) && (this.Headers.Version != "1.0"))
            {
                this.NeedToWaitToClose = false;
                this.BodySize = 0;
            }
            this.OnHeaderEvent.Fire(this, this.Headers, this.UserStream);
            if (this.NeedToWaitToClose)
            {
                sender.BufferBeginPointer = BufferSize;
                sender.BufferReadLength = 0x1000;
            }
            else if (this.IsChunked)
            {
                this.BeginChunk(BufferSize);
            }
            //else if (this.BodySize == 0) //MOD HACK HACK HACK
            else if (this.BodySize == 0 || this.BodySize == 3)
            {
                if (EventLogger.Enabled)
                {
                    EventLogger.Log(this, EventLogEntryType.Information, this.Headers.StringPacket);
                }
                this.OnSniffPacketEvent.Fire(this, this.Headers);
                this.OnReceiveEvent.Fire(this, this.Headers);
                if (this.UserStream != null)
                {
                    this.UserStream.Flush();
                    this.OnStreamDoneEvent.Fire(this, this.UserStream);
                }
                this.DONE_ReadingPacket();
                this.BeginHeader(BufferSize);
            }
            else if (this.BodySize <= 0x1000)
            {
                sender.BufferBeginPointer = BufferSize;
                sender.BufferReadLength = this.BodySize;
            }
            else
            {
                sender.BufferBeginPointer = BufferSize;
                sender.BufferReadLength = 0x1000;
            }
        }

        private void HandleStreamReady(object Tag)
        {
            if (this.PostStream != null)
            {
                try
                {
                    if (this.PostStream.Position == this.EndPosition)
                    {
                        this.OnStreamDoneEvent.Fire(this, this.PostStream);
                    }
                    else
                    {
                        int length = this.PostStream.Read(this.StreamSendBuffer, 0, this.StreamSendBuffer.Length);
                        if (length > 0)
                        {
                            this.MainSocket.Send(this.StreamSendBuffer, 0, length, Tag);
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void ParseQueue()
        {
            lock (this.SendQueue)
            {
                this.SendQueue.Dequeue();
                if (this.SendQueue.Count > 0)
                {
                    object obj2 = this.SendQueue.Peek();
                    if (obj2.GetType().FullName == "Intel.UPNP.HTTPMessage")
                    {
                        this.MainSocket.Send(((HTTPMessage) obj2).RawPacket, true);
                        if (((HTTPMessage) obj2).StatusCode >= 200)
                        {
                            this.SetRequestAnswered = true;
                        }
                    }
                    else
                    {
                        object[] objArray = (object[]) obj2;
                        Stream tag = (Stream) objArray[0];
                        string s = (string) objArray[1];
                        this.MainSocket.Send(U.GetBytes(s), tag);
                    }
                }
                else if (this.SetRequestAnswered)
                {
                    this.SET_REQUEST_ANSWERED();
                }
            }
        }

        public void PostStreamObject(Stream SObject, string PostWhat, string ContentType)
        {
            this.PostStream = SObject;
            this.OnSendReady += new AsyncSocket.OnSendReadyHandler(this.HandleStreamReady);
            string s = (("POST " + HTTPMessage.EscapeString(PostWhat) + " HTTP/1.1\r\n") + "Server: Intel CEL / CLR MiniWebServer\r\n") + "Content-Type:" + ContentType + "\r\n";
            long num = SObject.Length - SObject.Position;
            this.EndPosition = SObject.Position + num;
            if (this.EndPosition > SObject.Length)
            {
                this.EndPosition = SObject.Length;
                num = SObject.Length - SObject.Position;
            }
            s = s + "Content-Length:" + num.ToString() + "\r\n\r\n";
            this.MainSocket.Send(U.GetBytes(s));
        }

        private void ProcessChunk(byte[] buffer, int BufferSize)
        {
            if (this.ChunkState == FIN_CHUNK_CRLF)
            {
                if (BufferSize >= 2)
                {
                    this.BeginChunk(2);
                }
                else
                {
                    this.MainSocket.BufferReadLength = 2 - BufferSize;
                }
            }
            else
            {
                if (this.ChunkState != START_CHUNK)
                {
                    if (this.ChunkState == DATA_CHUNK)
                    {
                        if (BufferSize > this.ChunkDataSize)
                        {
                            BufferSize = this.ChunkDataSize;
                        }
                        if (this.UserStream != null)
                        {
                            this.UserStream.Write(buffer, 0, BufferSize);
                        }
                        else
                        {
                            this.SocketStream.Write(buffer, 0, BufferSize);
                        }
                        this.ChunkDataSize -= BufferSize;
                        if (this.ChunkDataSize == 0)
                        {
                            this.ChunkState = FIN_CHUNK_CRLF;
                            this.MainSocket.BufferReadLength = 2;
                            this.MainSocket.BufferBeginPointer = BufferSize;
                        }
                        else
                        {
                            if (this.ChunkDataSize < 0x1000)
                            {
                                this.MainSocket.BufferReadLength = this.ChunkDataSize;
                            }
                            else
                            {
                                this.MainSocket.BufferReadLength = 0x1000;
                            }
                            this.MainSocket.BufferBeginPointer = BufferSize;
                        }
                        return;
                    }
                    if (this.ChunkState != START_FOOTER_CHUNK)
                    {
                        return;
                    }
                    if (BufferSize < 2)
                    {
                        this.MainSocket.BufferBeginPointer = 0;
                        this.MainSocket.BufferReadLength = 1;
                        return;
                    }
                    for (int i = 2; i < BufferSize; i++)
                    {
                        if ((buffer[i - 2] == 13) && (buffer[i - 1] == 10))
                        {
                            BufferSize = i;
                            break;
                        }
                    }
                }
                else
                {
                    if (BufferSize < 3)
                    {
                        this.MainSocket.BufferReadLength = 1;
                        this.MainSocket.BufferBeginPointer = 0;
                        return;
                    }
                    for (int j = 2; j < BufferSize; j++)
                    {
                        if ((buffer[j - 2] == 13) && (buffer[j - 1] == 10))
                        {
                            BufferSize = j;
                            break;
                        }
                    }
                    if ((buffer[BufferSize - 2] == 13) && (buffer[BufferSize - 1] == 10))
                    {
                        string str = U.GetString(buffer, 0, BufferSize - 2);
                        if (str.IndexOf(";") != -1)
                        {
                            str = str.Substring(0, str.IndexOf(";"));
                        }
                        this.ChunkDataSize = int.Parse(str.ToUpper(), NumberStyles.HexNumber);
                        if (this.ChunkDataSize != 0)
                        {
                            this.ChunkState = DATA_CHUNK;
                            this.MainSocket.BufferBeginPointer = BufferSize;
                            if (this.ChunkDataSize < 0x1000)
                            {
                                this.MainSocket.BufferReadLength = this.ChunkDataSize;
                            }
                            else
                            {
                                this.MainSocket.BufferReadLength = 0x1000;
                            }
                        }
                        else
                        {
                            this.ChunkState = START_FOOTER_CHUNK;
                            this.MainSocket.BufferBeginPointer = BufferSize;
                            this.MainSocket.BufferReadLength = 1;
                        }
                    }
                    else
                    {
                        this.MainSocket.BufferReadLength = 1;
                        this.MainSocket.BufferBeginPointer = 0;
                    }
                    return;
                }
                if ((buffer[BufferSize - 2] == 13) && (buffer[BufferSize - 1] == 10))
                {
                    if (BufferSize == 2)
                    {
                        if (this.UserStream == null)
                        {
                            this.Headers.BodyBuffer = this.SocketStream.ToArray();
                            this.Headers.RemoveTag("Transfer-Encoding");
                            if (EventLogger.Enabled)
                            {
                                EventLogger.Log(this, EventLogEntryType.Information, this.Headers.StringPacket);
                            }
                            this.OnSniffPacketEvent.Fire(this, this.Headers);
                            this.OnReceiveEvent.Fire(this, this.Headers);
                            this.DONE_ReadingPacket();
                            this.BeginHeader(BufferSize);
                        }
                        else
                        {
                            this.UserStream.Flush();
                            this.OnHeaderEvent.Fire(this, this.Headers, this.UserStream);
                            this.OnStreamDoneEvent.Fire(this, this.UserStream);
                            this.DONE_ReadingPacket();
                            this.BeginHeader(BufferSize);
                        }
                    }
                    else
                    {
                        string str2 = U.GetString(buffer, 0, BufferSize - 2);
                        this.ChunkedHeadersAdded = true;
                        this.Headers.AddTag(str2.Substring(0, str2.IndexOf(":")).Trim(), str2.Substring(str2.IndexOf(":") + 1).Trim());
                        this.MainSocket.BufferBeginPointer = BufferSize;
                        this.MainSocket.BufferReadLength = 1;
                    }
                }
                else
                {
                    this.MainSocket.BufferBeginPointer = 0;
                    this.MainSocket.BufferReadLength = 1;
                }
            }
        }

        public void Send(HTTPMessage Packet)
        {
            this.OnSniffEvent.Fire(Packet.RawPacket, 0, Packet.RawPacket.Length);
            this.OnSniffPacketEvent.Fire(this, (HTTPMessage) Packet.Clone());
            if (EventLogger.Enabled)
            {
                EventLogger.Log(this, EventLogEntryType.Information, Packet.StringPacket);
            }
            if (Packet.Version == "1.0")
            {
                this.IsLegacy = true;
            }
            else
            {
                this.IsLegacy = false;
            }
            lock (this.SendQueue)
            {
                if (this.SendQueue.Count == 0)
                {
                    if (this.MainSocket != null)
                    {
                        this.MainSocket.Send(Packet.RawPacket);
                        if (Packet.StatusCode >= 200)
                        {
                            this.SET_REQUEST_ANSWERED();
                        }
                    }
                }
                else
                {
                    this.SendQueue.Enqueue(Packet);
                }
            }
        }

        private void SendChunked(byte[] buffer, int offset, int count, object Tag)
        {
            if (count != 0)
            {
                lock (this)
                {
                    if (this.MainSocket != null)
                    {
                        this.MainSocket.Send(U.GetBytes(count.ToString("X") + "\r\n"), false);
                    }
                    if (this.MainSocket != null)
                    {
                        this.MainSocket.Send(buffer, offset, count, false);
                    }
                    if (this.MainSocket != null)
                    {
                        this.MainSocket.Send(U.GetBytes("\r\n"), Tag);
                    }
                }
            }
        }

        public void SendChunkedPacketBody(byte[] buffer, int offset, int count, object Tag)
        {
            this.SendChunked(buffer, offset, count, Tag);
        }

        public void SendChunkedPacketHeaders(HTTPMessage Header)
        {
            string s = Header.Directive + " " + Header.DirectiveObj + " HTTP/" + Header.Version + "\r\n";
            IDictionaryEnumerator headerEnumerator = Header.GetHeaderEnumerator();
            while (headerEnumerator.MoveNext())
            {
                if (((string) headerEnumerator.Key).ToUpper() != "CONTENT-LENGTH")
                {
                    s = s + ((string) headerEnumerator.Value) + "\r\n";
                }
            }
            s = s + "Transfer-Encoding: Chunked\r\n\r\n";
            this.MainSocket.Send(U.GetBytes(s));
        }

        public void SendEndChunkPacket(object Tag)
        {
            this.FinishChunked(Tag);
        }

        //MOD
        //public void SendStreamObject(Stream SObject, string ContentType)
        //{
        //    this.SendStreamObject(SObject, (Range[]) null, ContentType, 0);
        //}

        public void SendStreamObject(Stream SObject, long length, string ContentType)
        {
            Info info = new Info();
            info.CurrentStreamObject = SObject;
            if (!CHUNK_ENABLED)
            {
                this.Headers.Version = "1.0";
            }
            string s = "";
            if (this.Headers.Version == "1.0")
            {
                s = "HTTP/1.0 200 OK\r\n";
                if (length > 0L)
                {
                    s = s + "Content-Length: " + length.ToString() + "\r\n";
                }
            }
            else
            {
                s = "HTTP/1.1 200 OK\r\n";
                s = s + "Transfer-Encoding: Chunked\r\n"; //WMV broke when i took this out
                //s = s + "Content-Length: " + length.ToString() + "\r\n"; //MOD
                s = s + "Accept-Ranges: bytes\r\n"; //MOD
            }
            s = (s + "Server: Intel CEL / CLR MiniWebServer\r\n") + "Content-Type: " + ContentType + "\r\n\r\n";
            info.StringPacket = s;
            lock (this.StateQueue)
            {
                this.StateQueue.Enqueue(info);
            }
            lock (this.SendQueue)
            {
                this.SendQueue.Enqueue(info);
                if (this.SendQueue.Count == 1)
                {
                    info.StringPacket = null;
                    this.MainSocket.Send(U.GetBytes(s), SObject);
                }
            }
        }

        //MOD
        //public void SendStreamObject(Stream SObject, Range[] Ranges, string ContentType)
        public void SendStreamObject(Stream SObject, Range[] Ranges, string ContentType, long length)
        {
            Info info = new Info();
            info.CurrentStreamObject = SObject;
            if ((Ranges != null) && (Ranges.Length > 1))
            {
                info.RangeSeparator = "**" + Guid.NewGuid().ToString() + "**";
                info.RangeContentType = ContentType;
            }
            string s = "";
            if ((Ranges == null) && (this.Headers.Version == "1.0"))
            {
                s = "HTTP/1.0 200 OK\r\n";
            }
            else if (Ranges == null)
            {
                s = "HTTP/1.1 200 OK\r\n";
                s = s + "Transfer-Encoding: Chunked\r\n"; //WMV broke when i took this out
            }
            if (Ranges != null)
            {
                s = "HTTP/1.1 206 Partial Content\r\n";
                info.RangeList = Ranges;
                try
                {
                    SObject.Seek(info.RangeList[0].Position, SeekOrigin.Begin);
                }
                catch (Exception)
                {
                }
                if ((SObject.Length - SObject.Position) < info.RangeList[0].Length)
                {
                    info.RangeList[0].Length = SObject.Length - SObject.Position;
                }
                if (info.RangeList.Length == 1)
                {
                    string str2 = s;
                    string[] strArray = new string[] { str2, "Content-Range: bytes ", info.RangeList[0].Position.ToString(), "-", ((info.RangeList[0].Position + info.RangeList[0].Length) - 1L).ToString(), "/", SObject.Length.ToString(), "\r\nContent-Length: ", info.RangeList[0].Length.ToString(), "\r\n" };
                    s = string.Concat(strArray) + "Content-Type: " + ContentType + "\r\n";
                }
                else
                {
                    s = s + "Content-type: multipart/byteranges; boundary=" + info.RangeSeparator + "\r\n";
                }
            }
            else
            {
                s = s + "Content-Type: " + ContentType + "\r\n";
            }
            //s = s + "Content-Length: " + length.ToString() + "\r\n";
            s = s + "Server: Intel CEL / CLR MiniWebServer\r\n" + "\r\n";
            info.StringPacket = s;
            lock (this.StateQueue)
            {
                this.StateQueue.Enqueue(info);
            }
            lock (this.SendQueue)
            {
                this.SendQueue.Enqueue(info);
                if (this.SendQueue.Count == 1)
                {
                    info.StringPacket = null;
                    this.MainSocket.Send(U.GetBytes(s), SObject);
                }
            }
        }

        private void SET_REQUEST_ANSWERED()
        {
            this.OnRequestAnsweredEvent.Fire(this);
            if (Interlocked.Decrement(ref this._Counter) == 0)
            {
                if ((this.Headers.Version == "1.0") || (this.Headers.Version == "0.9"))
                {
                    this.Close();
                }
                else if (this.Headers.GetTag("connection").ToUpper() == "CLOSE")
                {
                    this.Close();
                }
                this.Monitor.SetInactive();
            }
        }

        private void SET_START_OF_REQUEST()
        {
            Interlocked.Increment(ref this._Counter);
            this.SetRequestAnswered = false;
            this.Monitor.SetActive();
        }

        public void StartReading()
        {
            this.MainSocket.Begin();
        }

        public void StopReading()
        {
            this.MainSocket.StopReading();
        }

        private void StreamSendCallback(IAsyncResult result)
        {
            Info info;
            Stream asyncState = (Stream) result.AsyncState;
            lock (this.StateQueue)
            {
                info = (Info) this.StateQueue.Peek();
            }
            int length = 0;
            try
            {
                length = asyncState.EndRead(result);
            }
            catch
            {
            }
            if (length > 0)
            {
                if (info.RangeList == null)
                {
                    if (this.MainSocket != null)
                    {
                        if (this.Headers.Version == "1.0")
                        {
                            lock (this)
                            {
                                if (this.MainSocket != null)
                                {
                                    this.MainSocket.Send(this.SendBuffer, 0, length, asyncState);
                                }
                            }
                        }
                        else
                        {
                            this.SendChunked(this.SendBuffer, 0, length, asyncState);
                        }
                    }
                }
                else
                {
                    if ((info.RangeList.Length > 1) && (info.RangeList[info.RangeIndex].RangeLeft == info.RangeList[info.RangeIndex].Length))
                    {
                        lock (this)
                        {
                            if (this.MainSocket != null)
                            {
                                this.MainSocket.Send(U.GetBytes(info.RangeSeparator));
                                this.MainSocket.Send(U.GetBytes("Content-Type: " + info.RangeContentType + "\r\nContent-range: bytes " + info.RangeList[info.RangeIndex].Position.ToString() + "-" + info.RangeList[info.RangeIndex].Length.ToString() + "/" + asyncState.Length.ToString() + "\r\n"));
                            }
                        }
                    }
                    lock (this)
                    {
                        if (this.MainSocket != null)
                        {
                            if (info.RangeList.Length == 1)
                            {
                                this.MainSocket.Send(this.SendBuffer, 0, length, asyncState);
                            }
                            else
                            {
                                this.MainSocket.Send(this.SendBuffer, 0, length, asyncState);
                            }
                        }
                    }
                    info.RangeList[info.RangeIndex].RangeLeft -= length;
                    if (info.RangeList[info.RangeIndex].RangeLeft == 0L)
                    {
                        info.RangeIndex++;
                        if (info.RangeIndex != info.RangeList.Length)
                        {
                            try
                            {
                                asyncState.Seek(info.RangeList[info.RangeIndex].Position, SeekOrigin.Begin);
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }
            }
            else
            {
                if (info.RangeList == null)
                {
                    if (this.Headers.Version != "1.0")
                    {
                        this.FinishChunked(false);
                    }
                }
                else if (info.RangeList.Length != 1)
                {
                }
                this.OnStreamDoneEvent.Fire(this, asyncState);
                lock (this.StateQueue)
                {
                    this.StateQueue.Dequeue();
                }
                lock (this.SendQueue)
                {
                    this.SendQueue.Dequeue();
                    if (this.SendQueue.Count > 0)
                    {
                        object obj2 = this.SendQueue.Peek();
                        if (obj2.GetType() == typeof(Info))
                        {
                            lock (this)
                            {
                                if (this.MainSocket != null)
                                {
                                    Info info2 = (Info) obj2;
                                    Info info3 = (Info) obj2;
                                    this.MainSocket.Send(U.GetBytes(info2.StringPacket), info3.CurrentStreamObject);
                                }
                            }
                        }
                    }
                    else
                    {
                        this.SET_REQUEST_ANSWERED();
                    }
                }
            }
        }

        public bool ChunkedHeadersWereAdded
        {
            get
            {
                return this.ChunkedHeadersAdded;
            }
        }

        public bool IsConnected
        {
            get
            {
                return this.Connected;
            }
        }

        public IPEndPoint Remote
        {
            get
            {
                return (IPEndPoint) this.MainSocket.RemoteEndPoint;
            }
        }

        public int SessionID
        {
            get
            {
                return this.GetHashCode();
            }
        }

        public IPEndPoint Source
        {
            get
            {
                if (this.MainSocket.LocalEndPoint != null)
                {
                    return (IPEndPoint) this.MainSocket.LocalEndPoint;
                }
                return this.local_ep;
            }
        }

        public class ActivityMonitor
        {
            private bool Active = true;
            private DateTime TimeStamp;

            public bool IsTimeout()
            {
                lock (this)
                {
                    return (!this.Active && (this.TimeStamp.AddSeconds(10.0).CompareTo(DateTime.Now) <= 0));
                }
            }

            public void SetActive()
            {
                lock (this)
                {
                    this.Active = true;
                }
            }

            public void SetInactive()
            {
                lock (this)
                {
                    this.Active = false;
                    this.TimeStamp = DateTime.Now;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Info
        {
            public Stream CurrentStreamObject;
            public string StringPacket;
            public HTTPSession.Range[] RangeList;
            public int RangeIndex;
            public string RangeSeparator;
            public string RangeContentType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Range
        {
            public long Position;
            public long Length;
            public long RangeLeft;
            public Range(long position, long length)
            {
                this.Position = position;
                this.Length = length;
                this.RangeLeft = length;
            }
        }

        public delegate void ReceiveHandler(HTTPSession sender, HTTPMessage msg);

        public delegate void ReceiveHeaderHandler(HTTPSession sender, HTTPMessage Headers, Stream StreamObj);

        public delegate void SessionHandler(HTTPSession TheSession);

        internal delegate void SniffHandler(byte[] Raw, int offset, int length);

        public delegate void StreamDoneHandler(HTTPSession sender, Stream StreamObject);
    }
}

