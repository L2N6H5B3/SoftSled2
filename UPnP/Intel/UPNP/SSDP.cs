namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Globalization;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public sealed class SSDP
    {
        private IPEndPoint boundto;
        private Intel.UPNP.LifeTimeMonitor.LifeTimeHandler LifeTimeHandler;
        private byte[] MainBuffer;
        private SafeTimer NotifyTimer = new SafeTimer();
        private EndPoint R_Ep;
        private Random RandomGenerator = new Random();
        private IAsyncResult Receiver;
        private Socket ReceiveSocket;
        private Random RND = new Random();
        private LifeTimeMonitor SearchTimer = new LifeTimeMonitor();
        public int SSDP_EXPIRATION;
        private Socket UnicastSendSocket;

        public event NotifyHandler OnNotify;

        public event RefreshHandler OnRefresh;

        public event SearchHandler OnSearch;

        public event SnifferHandler OnSniffPacket;

        public SSDP(IPEndPoint LocalEP, int Expiration)
        {
            InstanceTracker.Add(this);
            this.LifeTimeHandler = new Intel.UPNP.LifeTimeMonitor.LifeTimeHandler(this.SearchTimerSink);
            this.SearchTimer.OnExpired += this.LifeTimeHandler;
            this.boundto = LocalEP;
            this.SSDP_EXPIRATION = Expiration;
            int minValue = (int) ((this.SSDP_EXPIRATION * 0.25) * 1000.0);
            int maxValue = (int) ((this.SSDP_EXPIRATION * 0.45) * 1000.0);
            this.MainBuffer = new byte[0x1000];
            this.R_Ep = new IPEndPoint(0L, 0);
            this.ReceiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            this.UnicastSendSocket = this.ReceiveSocket;
            this.ReceiveSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            this.ReceiveSocket.Bind(LocalEP);
            this.ReceiveSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse("239.255.255.250"), this.LocalIPEndPoint.Address));
            this.NotifyTimer.OnElapsed += new SafeTimer.TimeElapsedHandler(this.__NotifyCheck);
            this.NotifyTimer.Interval = this.RND.Next(minValue, maxValue);
            this.NotifyTimer.AutoReset = true;
            this.NotifyTimer.Start();
            this.Receiver = this.ReceiveSocket.BeginReceiveFrom(this.MainBuffer, 0, 0x1000, SocketFlags.None, ref this.R_Ep, new AsyncCallback(this.HandleReceive), null);
        }

        private void __NotifyCheck()
        {
            if (this.OnRefresh != null)
            {
                this.OnRefresh();
            }
            int minValue = (int) ((this.SSDP_EXPIRATION * 0.25) * 1000.0);
            int maxValue = (int) ((this.SSDP_EXPIRATION * 0.45) * 1000.0);
            this.NotifyTimer.Interval = this.RND.Next(minValue, maxValue);
        }

        public void BroadcastData(HTTPMessage Packet)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(this.boundto.Address, 0));
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, 1);
            }
            catch (Exception)
            {
            }
            string str = ((IPEndPoint) socket.LocalEndPoint).Address.ToString() + ":" + ((IPEndPoint) socket.LocalEndPoint).Port.ToString();
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 0x76c);
            byte[] rawPacket = Packet.RawPacket;
            MulticastOption optionValue = new MulticastOption(remoteEP.Address);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, optionValue);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, (int) this.boundto.Address.Address);
            }
            catch (Exception)
            {
                return;
            }
            socket.SendTo(rawPacket, rawPacket.Length, SocketFlags.None, remoteEP);
            socket.SendTo(rawPacket, rawPacket.Length, SocketFlags.None, remoteEP);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, optionValue);
            socket.Close();
        }

        public void Dispose()
        {
            this.ReceiveSocket.Close();
            this.UnicastSendSocket.Close();
            this.OnNotify = null;
            this.OnRefresh = null;
            this.OnSearch = null;
            this.OnSniffPacket = null;
        }

        ~SSDP()
        {
            this.Dispose();
        }

        private void HandleReceive(IAsyncResult result)
        {
            try
            {
                int count = this.ReceiveSocket.EndReceiveFrom(result, ref this.R_Ep);
                HTTPMessage msg = HTTPMessage.ParseByteArray(this.MainBuffer, 0, count);
                msg.LocalEndPoint = this.LocalIPEndPoint;
                msg.RemoteEndPoint = new IPEndPoint(((IPEndPoint) this.R_Ep).Address, ((IPEndPoint) this.R_Ep).Port);
                this.ProcessPacket(msg, msg.RemoteEndPoint);
            }
            catch (Exception)
            {
            }
            try
            {
                this.ReceiveSocket.BeginReceiveFrom(this.MainBuffer, 0, 0x1000, SocketFlags.None, ref this.R_Ep, new AsyncCallback(this.HandleReceive), null);
            }
            catch (Exception)
            {
            }
        }

        public static void ParseURL(string ServiceURL, out string WebIP, out int Port, out string ServiceName)
        {
            Uri uri = new Uri(ServiceURL);
            WebIP = uri.Host;
            if (uri.HostNameType == UriHostNameType.Dns)
            {
                WebIP = Dns.GetHostByName(WebIP).AddressList[0].ToString();
            }
            Port = uri.Port;
            ServiceName = HTTPMessage.UnEscapeString(uri.PathAndQuery);
        }

        private void ProcessPacket(HTTPMessage msg, IPEndPoint src)
        {
            if (this.OnSniffPacket != null)
            {
                this.OnSniffPacket(src, null, msg);
            }
            DText text = new DText();
            text.ATTRMARK = "::";
            bool isAlive = false;
            string tag = msg.GetTag("USN");
            text[0] = tag;
            string uSN = text[1];
            uSN = uSN.Substring(uSN.IndexOf(":") + 1);
            string sT = text[2];
            int maxAge = 0;
            if (msg.GetTag("NTS").ToUpper() == "SSDP:ALIVE")
            {
                isAlive = true;
                string str5 = msg.GetTag("Cache-Control").Trim();
                if (str5 != "")
                {
                    text.ATTRMARK = ",";
                    text.MULTMARK = "=";
                    text[0] = str5;
                    for (int i = 1; i <= text.DCOUNT(); i++)
                    {
                        if (text[i, 1].Trim().ToUpper() == "MAX-AGE")
                        {
                            maxAge = int.Parse(text[i, 2].Trim());
                            break;
                        }
                    }
                }
            }
            else
            {
                isAlive = false;
            }
            if ((msg.Directive == "NOTIFY") && (this.OnNotify != null))
            {
                Uri uri;
                try
                {
                    uri = new Uri(msg.GetTag("Location"));
                }
                catch (Exception)
                {
                    uri = null;
                }
                this.OnNotify(src, msg.LocalEndPoint, uri, isAlive, uSN, sT, maxAge, msg);
            }
            if (msg.Directive == "M-SEARCH")
            {
                try
                {
                    this.ValidateSearchPacket(msg);
                }
                catch (InvalidSearchPacketException)
                {
                    return;
                }
                if (this.OnSearch != null)
                {
                    int maxValue = int.Parse(msg.GetTag("MX"));
                    SearchStruct struct2 = new SearchStruct();
                    struct2.ST = msg.GetTag("ST");
                    struct2.Source = src;
                    struct2.Local = this.LocalIPEndPoint;
                    this.SearchTimer.Add(struct2, this.RandomGenerator.Next(0, maxValue));
                }
            }
        }

        private void SearchTimerSink(LifeTimeMonitor sender, object obj)
        {
            SearchStruct struct2 = (SearchStruct) obj;
            if (this.OnSearch != null)
            {
                this.OnSearch(struct2.ST, struct2.Source, struct2.Local);
            }
        }

        public void UnicastData(HTTPMessage msg, IPEndPoint dest)
        {
            if (this.OnSniffPacket != null)
            {
                this.OnSniffPacket(null, dest, msg);
            }
            byte[] rawPacket = msg.RawPacket;
            this.UnicastSendSocket.SendTo(rawPacket, rawPacket.Length, SocketFlags.None, dest);
        }

        private void ValidateSearchPacket(HTTPMessage msg)
        {
            if (msg.GetTag("MAN") != "\"ssdp:discover\"")
            {
                throw new InvalidSearchPacketException("Invalid MAN");
            }
            if (msg.DirectiveObj != "*")
            {
                throw new InvalidSearchPacketException("Expected * in RequestLine");
            }
            if (double.Parse(msg.Version, new CultureInfo("en-US").NumberFormat) < 1.1)
            {
                throw new InvalidSearchPacketException("Version must be at least 1.1");
            }
            int num = 0;
            string tag = msg.GetTag("MX");
            if (tag == "")
            {
                throw new InvalidSearchPacketException("Missing MX");
            }
            try
            {
                num = int.Parse(tag);
            }
            catch (Exception)
            {
                throw new InvalidSearchPacketException("MX must be an integer");
            }
            if (num <= 0)
            {
                throw new InvalidSearchPacketException("MX must be a positive integer");
            }
        }

        public IPEndPoint LocalIPEndPoint
        {
            get
            {
                return this.boundto;
            }
        }

        public class InvalidSearchPacketException : Exception
        {
            public InvalidSearchPacketException(string x) : base(x)
            {
            }
        }

        public delegate void NotifyHandler(IPEndPoint source, IPEndPoint local, Uri LocationURL, bool IsAlive, string USN, string ST, int MaxAge, HTTPMessage Packet);

        public delegate void PacketHandler(IPEndPoint source, HTTPMessage Packet);

        public delegate void RefreshHandler();

        public delegate void SearchHandler(string SearchTarget, IPEndPoint src, IPEndPoint local);

        private class SearchStruct
        {
            public IPEndPoint Local;
            public IPEndPoint Source;
            public string ST;
        }

        public delegate void SnifferHandler(IPEndPoint source, IPEndPoint dest, HTTPMessage Packet);
    }
}

