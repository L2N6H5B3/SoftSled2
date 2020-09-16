namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public sealed class UPnPControlPoint
    {
        private static int _mx = 5;
        private Hashtable CreateTable;
        private LifeTimeMonitor Lifetime;
        private NetworkInfo NetInfo;
        private Hashtable SSDPSessions;
        private Hashtable SSDPTable;
        private ArrayList SyncData;
        private DeviceNode SyncDevice;

        public event CreateDeviceHandler OnCreateDevice;

        public event SSDP.NotifyHandler OnNotify;

        public event SearchHandler OnSearch;

        public UPnPControlPoint()
        {
            this.CreateTable = Hashtable.Synchronized(new Hashtable());
            this.SSDPTable = Hashtable.Synchronized(new Hashtable());
            this.NetInfo = new NetworkInfo(new NetworkInfo.InterfaceHandler(this.NewInterface));
            this.SyncData = ArrayList.Synchronized(new ArrayList());
            this.SSDPSessions = Hashtable.Synchronized(new Hashtable());
            this.Lifetime = new LifeTimeMonitor();
            this.Lifetime.OnExpired += new LifeTimeMonitor.LifeTimeHandler(this.HandleExpired);
        }

        public UPnPControlPoint(NetworkInfo ni)
        {
            this.CreateTable = Hashtable.Synchronized(new Hashtable());
            this.SSDPTable = Hashtable.Synchronized(new Hashtable());
            this.NetInfo = ni;
            foreach (IPAddress address in this.NetInfo.GetLocalAddresses())
            {
                this.NewInterface(this.NetInfo, address);
            }
            this.NetInfo.OnNewInterface += new NetworkInfo.InterfaceHandler(this.NewInterface);
            this.SyncData = ArrayList.Synchronized(new ArrayList());
            this.SSDPSessions = Hashtable.Synchronized(new Hashtable());
            this.Lifetime = new LifeTimeMonitor();
            this.Lifetime.OnExpired += new LifeTimeMonitor.LifeTimeHandler(this.HandleExpired);
        }

        public void CreateDeviceAsync(Uri DescriptionURL, int LifeTime)
        {
            UPnPDeviceFactory factory = new UPnPDeviceFactory(DescriptionURL, LifeTime, new UPnPDeviceFactory.UPnPDeviceHandler(this.HandleDeviceCreation), null);
            this.CreateTable[factory] = factory;
            this.Lifetime.Add(factory, 30);
        }

        private void CreateSyncCallback(UPnPDevice Device, Uri URL)
        {
            this.SyncDevice.TheDevice = Device;
            this.SyncDevice.URL = URL;
        }

        public void Dispose()
        {
            IDictionaryEnumerator enumerator = this.SSDPTable.GetEnumerator();
            while (enumerator.MoveNext())
            {
                ((SSDP) enumerator.Value).Dispose();
            }
        }

        ~UPnPControlPoint()
        {
        }

        public void FindDeviceAsync(string SearchTarget)
        {
            HTTPMessage packet = new HTTPMessage();
            packet.Directive = "M-SEARCH";
            packet.DirectiveObj = "*";
            packet.AddTag("ST", SearchTarget);
            packet.AddTag("MX", MX.ToString());
            packet.AddTag("MAN", "\"ssdp:discover\"");
            packet.AddTag("HOST", "239.255.255.250:1900");
            IPAddress[] localAddresses = this.NetInfo.GetLocalAddresses();
            IPEndPoint dest = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 0x76c);
            for (int i = 0; i < localAddresses.Length; i++)
            {
                try
                {
                    Socket theSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    theSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                    theSocket.Bind(new IPEndPoint(localAddresses[i], 0));
                    SSDPSession session = new SSDPSession(theSocket, new SSDPSession.ReceiveHandler(this.HandleAsyncSearch));
                    this.SSDPSessions[session] = session;
                    session.SendTo(packet, dest);
                    session.SendTo(packet, dest);
                    this.Lifetime.Add(session, 7);
                }
                catch (Exception exception)
                {
                    EventLogger.Log(this, EventLogEntryType.Error, "CP Failure: " + localAddresses[i].ToString());
                    EventLogger.Log(exception);
                }
            }
        }

        private void HandleAlert(object sender, object RObj)
        {
        }

        private void HandleAsyncSearch(SSDPSession sender, HTTPMessage msg)
        {
            DText text = new DText();
            string tag = msg.GetTag("Location");
            int maxAge = 0;
            string str2 = msg.GetTag("Cache-Control").Trim();
            if (str2 != "")
            {
                text.ATTRMARK = ",";
                text.MULTMARK = "=";
                text[0] = str2;
                for (int i = 1; i <= text.DCOUNT(); i++)
                {
                    if (text[i, 1].Trim().ToUpper() == "MAX-AGE")
                    {
                        maxAge = int.Parse(text[i, 2].Trim());
                        break;
                    }
                }
            }
            str2 = msg.GetTag("USN");
            string uSN = str2.Substring(str2.IndexOf(":") + 1);
            string searchTarget = msg.GetTag("ST");
            if (uSN.IndexOf("::") != -1)
            {
                uSN = uSN.Substring(0, uSN.IndexOf("::"));
            }
            EventLogger.Log(this, EventLogEntryType.SuccessAudit, msg.RemoteEndPoint.ToString());
            if (this.OnSearch != null)
            {
                this.OnSearch(msg.RemoteEndPoint, msg.LocalEndPoint, new Uri(tag), uSN, searchTarget, maxAge);
            }
        }

        private void HandleDeviceCreation(UPnPDeviceFactory Factory, UPnPDevice device, Uri URL)
        {
            Factory.Shutdown();
            if (this.OnCreateDevice != null)
            {
                this.OnCreateDevice(device, URL);
            }
        }

        private void HandleExpired(LifeTimeMonitor sender, object Obj)
        {
            if (Obj.GetType().FullName == "Intel.UPNP.SSDPSession")
            {
                ((SSDPSession) Obj).Close();
                this.SSDPSessions.Remove(Obj);
            }
            if (Obj.GetType().FullName == "Intel.UPNP.UPnPDeviceFactory")
            {
                ((UPnPDeviceFactory) Obj).Shutdown();
                this.CreateTable.Remove(Obj);
            }
        }

        private void HandleNotify(IPEndPoint source, IPEndPoint local, Uri LocationURL, bool IsAlive, string USN, string ST, int MaxAge, HTTPMessage Packet)
        {
            if (IsAlive && (LocationURL != null))
            {
                EventLogger.Log(this, EventLogEntryType.SuccessAudit, LocationURL.ToString());
            }
            if (this.OnNotify != null)
            {
                this.OnNotify(source, local, LocationURL, IsAlive, USN, ST, MaxAge, Packet);
            }
        }

        private void NewInterface(NetworkInfo sender, IPAddress Intfce)
        {
            try
            {
                SSDP ssdp = new SSDP(new IPEndPoint(Intfce, 0x76c), 0xffff);
                ssdp.OnNotify += new SSDP.NotifyHandler(this.HandleNotify);
                this.SSDPTable[Intfce.ToString()] = ssdp;
            }
            catch (Exception)
            {
            }
        }

        private void PreProcessNotify(IPEndPoint source, string LocationURL, bool IsAlive, string USN, string ST, int MaxAge)
        {
        }

        public static int MX
        {
            get
            {
                return _mx;
            }
            set
            {
                if (value > 0)
                {
                    _mx = value;
                }
            }
        }

        public delegate void CreateDeviceHandler(UPnPDevice Device, Uri DescriptionURL);

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceNode
        {
            public UPnPDevice TheDevice;
            public Uri URL;
        }

        public delegate void SearchHandler(IPEndPoint ResponseFromEndPoint, IPEndPoint ResponseReceivedOnEndPoint, Uri DescriptionLocation, string USN, string SearchTarget, int MaxAge);
    }
}

