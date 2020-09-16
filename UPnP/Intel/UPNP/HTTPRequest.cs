namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Runtime.CompilerServices;

    public sealed class HTTPRequest
    {
        private bool _PIPELINE = true;
        private IPEndPoint _Source;
        public bool IdleTimeout = true;
        private LifeTimeMonitor.LifeTimeHandler KeepAliveHandler;
        private static LifeTimeMonitor KeepAliveTimer = new LifeTimeMonitor();
        private HTTPMessage LastMessage;
        private Hashtable NotPipelinedTable = Hashtable.Synchronized(new Hashtable());
        public static bool PIPELINE = true;
        public IPEndPoint ProxySetting = null;
        private bool ReceivedFirstResponse = false;
        private HTTPSession s = null;
        private System.Collections.Queue TagQueue = new System.Collections.Queue();

        public event InactiveClosedHandler OnInactiveClosed;

        public event RequestHandler OnResponse;

        public event SniffHandler OnSniff;

        public event RequestHandler OnSniffPacket;

        public HTTPRequest()
        {
            this.KeepAliveHandler = new LifeTimeMonitor.LifeTimeHandler(this.KeepAliveSink);
            KeepAliveTimer.OnExpired += this.KeepAliveHandler;
            InstanceTracker.Add(this);
            this._PIPELINE = PIPELINE;
        }

        private void CloseSink(HTTPSession ss)
        {
            bool flag = false;
            string str = "";
            ss.CancelAllEvents();
            lock (this.TagQueue)
            {
                KeepAliveTimer.Remove(this.GetHashCode());
                if (this.TagQueue.Count > 0)
                {
                    EventLogger.Log(this, EventLogEntryType.Information, "Switching Pipeline Modes [" + ss.GetHashCode().ToString() + "]");
                    this._PIPELINE = false;
                    if (!this.ReceivedFirstResponse)
                    {
                        str = ((StateData) this.TagQueue.Peek()).Dest.ToString();
                    }
                }
                if (!this.ReceivedFirstResponse)
                {
                    EventLogger.Log(this, EventLogEntryType.Error, "Server[" + str + "] closed socket without answering");
                    flag = true;
                }
                while (this.TagQueue.Count > 0)
                {
                    StateData data = (StateData) this.TagQueue.Dequeue();
                    if (!flag)
                    {
                        HTTPRequest request = new HTTPRequest();
                        request.ProxySetting = this.ProxySetting;
                        request._PIPELINE = true;
                        if (this.OnSniff != null)
                        {
                            request.OnSniff = (SniffHandler) Delegate.Combine(request.OnSniff, new SniffHandler(this.NonPipelinedSniffSink));
                        }
                        if (this.OnSniffPacket != null)
                        {
                            request.OnSniffPacket = (RequestHandler) Delegate.Combine(request.OnSniffPacket, new RequestHandler(this.NonPipelinedSniffPacketSink));
                        }
                        request.OnResponse = (RequestHandler) Delegate.Combine(request.OnResponse, new RequestHandler(this.NonPipelinedResponseSink));
                        this.NotPipelinedTable[request] = request;
                        request.PipelineRequest(data.Dest, data.Request, data.Tag);
                    }
                    else if (this.OnResponse != null)
                    {
                        this.OnResponse(this, null, data.Tag);
                    }
                }
                this.s = null;
            }
        }

        private void ContinueRequest(IPEndPoint dest, string PQ, object Tag, HTTPMessage MSG)
        {
            HTTPMessage mSG = null;
            if (MSG == null)
            {
                mSG = new HTTPMessage();
                mSG.Directive = "GET";
                mSG.DirectiveObj = PQ;
                mSG.AddTag("Host", dest.ToString());
            }
            else
            {
                mSG = MSG;
            }
            lock (this.TagQueue)
            {
                this.IdleTimeout = false;
                KeepAliveTimer.Remove(this.GetHashCode());
                this.LastMessage = mSG;
                if ((!PIPELINE && !this._PIPELINE) || !this._PIPELINE)
                {
                    HTTPRequest request = new HTTPRequest();
                    request.ProxySetting = this.ProxySetting;
                    request._PIPELINE = true;
                    if (this.OnSniff != null)
                    {
                        request.OnSniff = (SniffHandler) Delegate.Combine(request.OnSniff, new SniffHandler(this.NonPipelinedSniffSink));
                    }
                    if (this.OnSniffPacket != null)
                    {
                        request.OnSniffPacket = (RequestHandler) Delegate.Combine(request.OnSniffPacket, new RequestHandler(this.NonPipelinedSniffPacketSink));
                    }
                    request.OnResponse = (RequestHandler) Delegate.Combine(request.OnResponse, new RequestHandler(this.NonPipelinedResponseSink));
                    this.NotPipelinedTable[request] = request;
                    request.PipelineRequest(dest, mSG, Tag);
                }
                else
                {
                    bool flag = this.TagQueue.Count == 0;
                    this.TagQueue.Enqueue(new StateData(mSG, dest, Tag, null));
                    if (this.s == null)
                    {
                        this.ReceivedFirstResponse = false;
                        if (this.ProxySetting != null)
                        {
                            this.s = new HTTPSession(new IPEndPoint(IPAddress.Any, 0), this.ProxySetting, new HTTPSession.SessionHandler(this.CreateSink), new HTTPSession.SessionHandler(this.CreateFailedSink), null);
                        }
                        else
                        {
                            this.s = new HTTPSession(new IPEndPoint(IPAddress.Any, 0), dest, new HTTPSession.SessionHandler(this.CreateSink), new HTTPSession.SessionHandler(this.CreateFailedSink), null);
                        }
                    }
                    else if (this.s.IsConnected && this.ReceivedFirstResponse)
                    {
                        try
                        {
                            if (this.ProxySetting == null)
                            {
                                this.s.Send(mSG);
                            }
                            else
                            {
                                HTTPMessage packet = (HTTPMessage) mSG.Clone();
                                packet.DirectiveObj = "http://" + dest.ToString() + packet.DirectiveObj;
                                packet.Version = "1.0";
                                this.s.Send(packet);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }

        private void CreateFailedSink(HTTPSession ss)
        {
            lock (this.TagQueue)
            {
                while (this.TagQueue.Count > 0)
                {
                    StateData data = (StateData) this.TagQueue.Dequeue();
                    EventLogger.Log(this, EventLogEntryType.Error, "Connection Attempt to [" + data.Dest.ToString() + "] Refused/Failed");
                    object tag = data.Tag;
                    if (data.HeaderCB != null)
                    {
                        data.HeaderCB(this, ss, null, null, tag);
                    }
                    else if (this.OnResponse != null)
                    {
                        this.OnResponse(this, null, tag);
                    }
                }
                this.s = null;
            }
        }

        private void CreateSink(HTTPSession ss)
        {
            lock (this.TagQueue)
            {
                ss.OnHeader += new HTTPSession.ReceiveHeaderHandler(this.HeaderSink);
                ss.OnReceive += new HTTPSession.ReceiveHandler(this.ReceiveSink);
                ss.OnClosed += new HTTPSession.SessionHandler(this.CloseSink);
                ss.OnStreamDone += new HTTPSession.StreamDoneHandler(this.StreamDoneSink);
                ss.OnRequestAnswered += new HTTPSession.SessionHandler(this.RequestAnsweredSink);
                if (this.OnSniff != null)
                {
                    ss.OnSniff += new HTTPSession.SniffHandler(this.SniffSink);
                }
                if (this.OnSniffPacket != null)
                {
                    ss.OnSniffPacket += new HTTPSession.ReceiveHandler(this.SniffPacketSink);
                }
                StateData data = (StateData) this.TagQueue.Peek();
                try
                {
                    if (this.ProxySetting == null)
                    {
                        ss.Send(data.Request);
                    }
                    else
                    {
                        HTTPMessage packet = (HTTPMessage) data.Request.Clone();
                        packet.DirectiveObj = "http://" + data.Dest.ToString() + packet.DirectiveObj;
                        packet.Version = "1.0";
                        ss.Send(packet);
                    }
                }
                catch (Exception exception)
                {
                    EventLogger.Log(exception);
                }
            }
        }

        public void Dispose()
        {
            lock (this.TagQueue)
            {
                HTTPSession s = this.s;
                if (s != null)
                {
                    s.Close();
                }
                this.s = null;
                this.TagQueue.Clear();
            }
        }

        internal void ForceCloseSession()
        {
            try
            {
                this.s.Close();
            }
            catch (Exception)
            {
            }
        }

        private void GetHostByNameSink(IAsyncResult result)
        {
            IPHostEntry entry = null;
            try
            {
                entry = Dns.EndGetHostByName(result);
            }
            catch (Exception)
            {
                return;
            }
            object[] asyncState = (object[]) result.AsyncState;
            Uri uri = (Uri) asyncState[0];
            object tag = asyncState[1];
            this.ContinueRequest(new IPEndPoint(entry.AddressList[0], uri.Port), HTTPMessage.UnEscapeString(uri.PathAndQuery), tag, null);
        }

        private void HeaderSink(HTTPSession sender, HTTPMessage header, Stream TheStream)
        {
            this._Source = sender.Source;
            StateData stateObject = null;
            if (TheStream != null)
            {
                stateObject = (StateData) sender.StateObject;
                object tag = stateObject.Tag;
                if (stateObject.HeaderCB != null)
                {
                    stateObject.HeaderCB(this, sender, header, TheStream, tag);
                }
                sender.StateObject = null;
                KeepAliveTimer.Add(this.GetHashCode(), 10);
            }
            else
            {
                lock (this.TagQueue)
                {
                    stateObject = (StateData) this.TagQueue.Dequeue();
                }
                sender.StateObject = stateObject;
                object obj3 = stateObject.Tag;
                if (stateObject.HeaderCB != null)
                {
                    stateObject.HeaderCB(this, sender, header, TheStream, obj3);
                    if ((sender.UserStream != null) && !sender.IsChunked)
                    {
                        sender.StateObject = null;
                    }
                }
            }
        }

        private void KeepAliveSink(LifeTimeMonitor sender, object obj)
        {
            if (this.IdleTimeout && (((int) obj) == this.GetHashCode()))
            {
                this.ForceCloseSession();
                if (this.OnInactiveClosed != null)
                {
                    this.OnInactiveClosed(this);
                }
            }
        }

        private void NonPipelinedResponseSink(HTTPRequest sender, HTTPMessage Response, object Tag)
        {
            this._Source = sender.Source;
            this.NotPipelinedTable.Remove(sender);
            sender.Dispose();
            if (this.OnResponse != null)
            {
                this.OnResponse(this, Response, Tag);
            }
        }

        private void NonPipelinedSniffPacketSink(HTTPRequest sender, HTTPMessage Response, object Tag)
        {
            if (this.OnSniffPacket != null)
            {
                this.OnSniffPacket(this, Response, Tag);
            }
        }

        private void NonPipelinedSniffSink(HTTPRequest sender, byte[] buffer, int offset, int count)
        {
            if (this.OnSniff != null)
            {
                this.OnSniff(this, buffer, offset, count);
            }
        }

        public void PipelineRequest(Uri Resource, object Tag)
        {
            object[] stateObject = new object[] { Resource, Tag };
            string host = Resource.Host;
            if (Resource.HostNameType == UriHostNameType.Dns)
            {
                Dns.BeginGetHostByName(host, new AsyncCallback(this.GetHostByNameSink), stateObject);
            }
            else
            {
                this.ContinueRequest(new IPEndPoint(IPAddress.Parse(Resource.Host), Resource.Port), HTTPMessage.UnEscapeString(Resource.PathAndQuery), Tag, null);
            }
        }

        public void PipelineRequest(IPEndPoint dest, HTTPMessage MSG, object Tag)
        {
            this.ContinueRequest(dest, "", Tag, MSG);
        }

        private void ReceiveSink(HTTPSession sender, HTTPMessage msg)
        {
            StateData stateObject = (StateData) sender.StateObject;
            object tag = stateObject.Tag;
            if ((msg.Version == "1.0") || (msg.Version == "0.9"))
            {
                sender.Close();
            }
            else if (msg.GetTag("Connection").ToUpper() == "CLOSE")
            {
                sender.Close();
            }
            if (this.OnResponse != null)
            {
                this.OnResponse(this, msg, tag);
            }
            sender.StateObject = null;
            lock (this.TagQueue)
            {
                if (this.TagQueue.Count == 0)
                {
                    this.IdleTimeout = true;
                    KeepAliveTimer.Add(this.GetHashCode(), 10);
                }
            }
        }

        public void ReleaseSniffHandlers()
        {
            if (this.s != null)
            {
                this.s.OnSniff -= new HTTPSession.SniffHandler(this.SniffSink);
                this.s.OnSniffPacket -= new HTTPSession.ReceiveHandler(this.SniffPacketSink);
            }
        }

        private void RequestAnsweredSink(HTTPSession ss)
        {
            lock (this.TagQueue)
            {
                if (!this.ReceivedFirstResponse)
                {
                    this.ReceivedFirstResponse = true;
                    foreach (StateData data in this.TagQueue)
                    {
                        try
                        {
                            if (this.ProxySetting == null)
                            {
                                ss.Send(data.Request);
                            }
                            else
                            {
                                HTTPMessage packet = (HTTPMessage) data.Request.Clone();
                                packet.DirectiveObj = "http://" + data.Dest.ToString() + packet.DirectiveObj;
                                packet.Version = "1.0";
                                ss.Send(packet);
                            }
                            continue;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }
                }
            }
        }

        public void SetSniffHandlers()
        {
            if (this.s != null)
            {
                this.s.OnSniff += new HTTPSession.SniffHandler(this.SniffSink);
                this.s.OnSniffPacket += new HTTPSession.ReceiveHandler(this.SniffPacketSink);
            }
        }

        private void SniffPacketSink(HTTPSession sender, HTTPMessage MSG)
        {
            if (this.OnSniffPacket != null)
            {
                if (sender.StateObject == null)
                {
                    this.OnSniffPacket(this, MSG, null);
                }
                else
                {
                    StateData stateObject = (StateData) sender.StateObject;
                    object tag = stateObject.Tag;
                    this.OnSniffPacket(this, MSG, tag);
                }
            }
        }

        private void SniffSink(byte[] buffer, int offset, int count)
        {
            if (this.OnSniff != null)
            {
                this.OnSniff(this, buffer, offset, count);
            }
        }

        private void StreamDoneSink(HTTPSession sender, Stream StreamObject)
        {
        }

        public IPEndPoint Source
        {
            get
            {
                return this._Source;
            }
        }

        public delegate void HeaderHandler(HTTPRequest sender, HTTPSession WebSession, HTTPMessage header, Stream StreamObj, object Tag);

        public delegate void InactiveClosedHandler(HTTPRequest sender);

        public delegate void RequestHandler(HTTPRequest sender, HTTPMessage Response, object Tag);

        public delegate void SniffHandler(HTTPRequest sender, byte[] buffer, int offset, int count);

        private class StateData
        {
            public IPEndPoint Dest = null;
            public HTTPRequest.HeaderHandler HeaderCB = null;
            public HTTPMessage Request = null;
            public object Tag = null;

            public StateData(HTTPMessage req, IPEndPoint d, object Tag, HTTPRequest.HeaderHandler HeaderCB)
            {
                this.Dest = d;
                this.Request = req;
                this.Tag = Tag;
                this.HeaderCB = HeaderCB;
            }
        }
    }
}

