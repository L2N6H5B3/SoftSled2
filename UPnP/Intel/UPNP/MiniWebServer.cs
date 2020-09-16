namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;

    public sealed class MiniWebServer
    {
        private IPEndPoint endpoint_local;
        public bool IdleTimeout;
        private LifeTimeMonitor KeepAliveTimer;
        private Socket MainSocket;
        private WeakEvent OnHeaderEvent;
        private WeakEvent OnReceiveEvent;
        private WeakEvent OnSessionEvent;
        private Hashtable SessionTable;
        private LifeTimeMonitor SessionTimer;

        public event HTTPReceiveHandler OnHeader
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

        public event HTTPReceiveHandler OnReceive
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

        public event NewSessionHandler OnSession
        {
            add
            {
                this.OnSessionEvent.Register(value);
            }
            remove
            {
                this.OnSessionEvent.UnRegister(value);
            }
        }

        public MiniWebServer(IPEndPoint local)
        {
            this.IdleTimeout = true;
            this.KeepAliveTimer = new LifeTimeMonitor();
            this.SessionTimer = new LifeTimeMonitor();
            this.SessionTable = new Hashtable();
            this.OnSessionEvent = new WeakEvent();
            this.OnReceiveEvent = new WeakEvent();
            this.OnHeaderEvent = new WeakEvent();
            InstanceTracker.Add(this);
            this.SessionTimer.OnExpired += new LifeTimeMonitor.LifeTimeHandler(this.SessionTimerSink);
            this.KeepAliveTimer.OnExpired += new LifeTimeMonitor.LifeTimeHandler(this.KeepAliveSink);
            this.endpoint_local = local;
            this.MainSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.MainSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            this.MainSocket.Bind(local);
            if (this.MainSocket.LocalEndPoint != null)
            {
                this.endpoint_local = (IPEndPoint) this.MainSocket.LocalEndPoint;
            }
            this.MainSocket.Listen(0x19);
            this.MainSocket.BeginAccept(new AsyncCallback(this.Accept), null);
            this.KeepAliveTimer.Add(false, 7);
        }

        public MiniWebServer(Uri BaseURL)
        {
            this.IdleTimeout = true;
            this.KeepAliveTimer = new LifeTimeMonitor();
            this.SessionTimer = new LifeTimeMonitor();
            this.SessionTable = new Hashtable();
            this.OnSessionEvent = new WeakEvent();
            this.OnReceiveEvent = new WeakEvent();
            this.OnHeaderEvent = new WeakEvent();
            InstanceTracker.Add(this);
            this.SessionTimer.OnExpired += new LifeTimeMonitor.LifeTimeHandler(this.SessionTimerSink);
            this.KeepAliveTimer.OnExpired += new LifeTimeMonitor.LifeTimeHandler(this.KeepAliveSink);
            string host = BaseURL.Host;
            if (BaseURL.HostNameType == UriHostNameType.Dns)
            {
                throw new MiniWebServerException("Uri must explicitly define IP Address");
            }
            IPEndPoint localEP = new IPEndPoint(IPAddress.Parse(host), BaseURL.Port);
            this.endpoint_local = localEP;
            this.MainSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.MainSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            this.MainSocket.Bind(localEP);
            this.MainSocket.Listen(0x19);
            this.MainSocket.BeginAccept(new AsyncCallback(this.Accept), null);
            this.KeepAliveTimer.Add(false, 7);
        }

        private void Accept(IAsyncResult result)
        {
            HTTPSession session = null;
            try
            {
                Socket theSocket = this.MainSocket.EndAccept(result);
                lock (this.SessionTable)
                {
                    session = new HTTPSession(this.LocalIPEndPoint, theSocket);
                    session.OnClosed += new HTTPSession.SessionHandler(this.CloseSink);
                    session.OnHeader += new HTTPSession.ReceiveHeaderHandler(this.HandleHeader);
                    session.OnReceive += new HTTPSession.ReceiveHandler(this.HandleRequest);
                    this.SessionTable[session] = session;
                }
                this.SessionTimer.Add(session, 3);
                this.OnSessionEvent.Fire(this, session);
                session.StartReading();
            }
            catch (Exception exception)
            {
                if (exception.GetType() != typeof(ObjectDisposedException))
                {
                    EventLogger.Log(exception);
                }
            }
            try
            {
                this.MainSocket.BeginAccept(new AsyncCallback(this.Accept), null);
            }
            catch (Exception)
            {
            }
        }

        private void CloseSink(HTTPSession s)
        {
            lock (this.SessionTable)
            {
                this.SessionTable.Remove(s);
            }
        }

        public void Dispose()
        {
            this.MainSocket.Close();
        }

        ~MiniWebServer()
        {
            this.Dispose();
        }

        private void HandleHeader(HTTPSession sender, HTTPMessage Header, Stream StreamObject)
        {
            this.SessionTimer.Remove(sender);
            this.OnHeaderEvent.Fire(Header, sender);
        }

        private void HandleRequest(HTTPSession WebSession, HTTPMessage request)
        {
            this.OnReceiveEvent.Fire(request, WebSession);
        }

        private void KeepAliveSink(LifeTimeMonitor sender, object obj)
        {
            if (this.IdleTimeout)
            {
                ArrayList list = new ArrayList();
                lock (this.SessionTable)
                {
                    IDictionaryEnumerator enumerator = this.SessionTable.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        if (((HTTPSession) enumerator.Value).Monitor.IsTimeout())
                        {
                            list.Add(enumerator.Value);
                        }
                    }
                }
                foreach (HTTPSession session in list)
                {
                    session.Close();
                }
                this.KeepAliveTimer.Add(false, 7);
            }
        }

        private void SessionTimerSink(LifeTimeMonitor sender, object obj)
        {
            ((HTTPSession) obj).Close();
        }

        public IPEndPoint LocalIPEndPoint
        {
            get
            {
                return this.endpoint_local;
            }
        }

        public delegate void HTTPReceiveHandler(HTTPMessage msg, HTTPSession WebSession);

        public delegate void NewSessionHandler(MiniWebServer sender, HTTPSession session);
    }
}

