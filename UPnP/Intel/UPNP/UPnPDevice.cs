
namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Xml;

    public sealed class UPnPDevice
    {
        private string __DeviceURN;
        private string _BootID;
        private Image _icon;
        private Image _icon2;
        private string _PresentationURL;
        private int Arch_Major;
        private int Arch_Minor;
        public Uri BaseURL;
        private bool ControlPointOnly;
        private Hashtable CP_RegisteredInterfaces;
        private static NetworkInfo CPNetworkInfo;
        private static Hashtable CPWebServerTable;
        private Hashtable CustomField;
        internal Uri descXmlLocation;
        private static long DeviceCount = 0L;
        public UPnPDevice[] EmbeddedDevices;
        public int ExpirationTimeout;
        public string FriendlyName;
        public bool HasPresentation;
        private Hashtable InitialEventTable;
        public IPAddress InterfaceToHost;
        internal Hashtable InvokerInfo;
        private bool IsRoot;
        public string LocationURL;
        public int Major;
        private IPEndPoint ManualIPEndPoint;
        public string Manufacturer;
        public string ManufacturerURL;
        public int Minor;
        public string ModelDescription;
        public string ModelName;
        public string ModelNumber;
        public Uri ModelURL;
        private NetworkInfo NetInfo;
        internal bool NoSSDP;
        private UPnPDevice parent;
        public string ProductCode;
        public string ProprietaryDeviceType;
        public object Reserved;
        private string RootPath;
        public string SerialNumber;
        public UPnPService[] Services;
        private Hashtable SSDPServerTable;
        public string UniqueDeviceName;
        private Hashtable UpdateTable;
        public object User;
        public object User2;
        public object User3;
        private int UseThisPort;
        private Hashtable VirtualDir_Header_Table;
        private Hashtable VirtualDir_Table;
        private Hashtable WebServerTable;

        public event OnRemovedHandler OnRemoved;

        internal event SniffHandler OnSniff;

        internal event SniffPacketHandler OnSniffPacket;

        internal UPnPDevice()
        {
            this.User = null;
            this.User2 = null;
            this.User3 = null;
            this.Reserved = null;
            this._BootID = "";
            this.Arch_Major = 1;
            this.Arch_Minor = 0;
            this.CP_RegisteredInterfaces = new Hashtable();
            this.UpdateTable = new Hashtable();
            this.CustomField = new Hashtable();
            this.NoSSDP = false;
            this.UseThisPort = 0;
            this.InitialEventTable = Hashtable.Synchronized(new Hashtable());
            this.ManualIPEndPoint = null;
            this.descXmlLocation = null;
            this.InvokerInfo = Hashtable.Synchronized(new Hashtable());
            this.IsRoot = false;
            this._icon = null;
            this._icon2 = null;
            this.EmbeddedDevices = new UPnPDevice[0];
            InstanceTracker.Add(this);
            this.parent = null;
            this.ControlPointOnly = true;
            this.Services = new UPnPService[0];
            this.HasPresentation = false;
            this.VirtualDir_Table = new Hashtable();
            this.VirtualDir_Header_Table = new Hashtable();
            lock (typeof(UPnPDevice))
            {
                if (DeviceCount == 0L)
                {
                    CPWebServerTable = new Hashtable();
                    CPNetworkInfo = new NetworkInfo(new NetworkInfo.InterfaceHandler(this.NewCPInterface));
                }
                else
                {
                    IPAddress[] localAddresses = CPNetworkInfo.GetLocalAddresses();
                    for (int i = 0; i < localAddresses.Length; i++)
                    {
                        this.CP_RegisteredInterfaces[localAddresses[i].ToString()] = true;
                        ((MiniWebServer) CPWebServerTable[localAddresses[i].ToString()]).OnReceive += new MiniWebServer.HTTPReceiveHandler(this.HandleWebRequest);
                        ((MiniWebServer) CPWebServerTable[localAddresses[i].ToString()]).OnHeader += new MiniWebServer.HTTPReceiveHandler(this.HandleHeaderRequest);
                    }
                    CPNetworkInfo.OnNewInterface += new NetworkInfo.InterfaceHandler(this.NewCPInterface);
                }
                DeviceCount += 1L;
            }
        }

        internal UPnPDevice(double version, string UDN)
        {
            this.User = null;
            this.User2 = null;
            this.User3 = null;
            this.Reserved = null;
            this._BootID = "";
            this.Arch_Major = 1;
            this.Arch_Minor = 0;
            this.CP_RegisteredInterfaces = new Hashtable();
            this.UpdateTable = new Hashtable();
            this.CustomField = new Hashtable();
            this.NoSSDP = false;
            this.UseThisPort = 0;
            this.InitialEventTable = Hashtable.Synchronized(new Hashtable());
            this.ManualIPEndPoint = null;
            this.descXmlLocation = null;
            this.InvokerInfo = Hashtable.Synchronized(new Hashtable());
            this.IsRoot = false;
            this._icon = null;
            this._icon2 = null;
            this.EmbeddedDevices = new UPnPDevice[0];
            InstanceTracker.Add(this);
            this.IsRoot = false;
            this.VirtualDir_Table = new Hashtable();
            this.VirtualDir_Header_Table = new Hashtable();
            this.parent = null;
            this.HasPresentation = true;
            this.ControlPointOnly = false;
            this.RootPath = "";
            if (version == 0.0)
            {
                this.Major = 1;
                this.Minor = 0;
            }
            else
            {
                DText text = new DText();
                text.ATTRMARK = ".";
                text[0] = version.ToString();
                this.Major = int.Parse(text[1]);
                if (text.DCOUNT() == 2)
                {
                    this.Minor = int.Parse(text[2]);
                }
                else
                {
                    this.Minor = 0;
                }
            }
            this.Services = new UPnPService[0];
            if (UDN == "")
            {
                this.UniqueDeviceName = Guid.NewGuid().ToString();
            }
            else
            {
                this.UniqueDeviceName = UDN;
            }
        }

        internal UPnPDevice(int DeviceExpiration, double version, string RootDir)
        {
            this.User = null;
            this.User2 = null;
            this.User3 = null;
            this.Reserved = null;
            this._BootID = "";
            this.Arch_Major = 1;
            this.Arch_Minor = 0;
            this.CP_RegisteredInterfaces = new Hashtable();
            this.UpdateTable = new Hashtable();
            this.CustomField = new Hashtable();
            this.NoSSDP = false;
            this.UseThisPort = 0;
            this.InitialEventTable = Hashtable.Synchronized(new Hashtable());
            this.ManualIPEndPoint = null;
            this.descXmlLocation = null;
            this.InvokerInfo = Hashtable.Synchronized(new Hashtable());
            this.IsRoot = false;
            this._icon = null;
            this._icon2 = null;
            this.EmbeddedDevices = new UPnPDevice[0];
            InstanceTracker.Add(this);
            this.IsRoot = true;
            this.parent = null;
            this.HasPresentation = true;
            this.ControlPointOnly = false;
            this.RootPath = RootDir;
            this.ExpirationTimeout = DeviceExpiration;
            this.WebServerTable = Hashtable.Synchronized(new Hashtable());
            this.SSDPServerTable = Hashtable.Synchronized(new Hashtable());
            this.VirtualDir_Table = new Hashtable();
            this.VirtualDir_Header_Table = new Hashtable();
            if (version == 0.0)
            {
                this.Major = 1;
                this.Minor = 0;
            }
            else
            {
                DText text = new DText();
                text.ATTRMARK = ".";
                text[0] = version.ToString();
                this.Major = int.Parse(text[1]);
                if (text.DCOUNT() == 2)
                {
                    this.Minor = int.Parse(text[2]);
                }
                else
                {
                    this.Minor = 0;
                }
            }
            this.Services = new UPnPService[0];
            this.UniqueDeviceName = Guid.NewGuid().ToString();
        }

        public void AddCustomFieldInDescription(string FieldName, string FieldValue, string Namespace)
        {
            if (!this.CustomField.ContainsKey(Namespace))
            {
                this.CustomField[Namespace] = new Hashtable();
            }
            ((Hashtable) this.CustomField[Namespace])[FieldName] = FieldValue;
        }

        public void AddDevice(IUPnPDevice device)
        {
            this.AddDevice(device.GetUPnPDevice());
        }

        public void AddDevice(UPnPDevice device)
        {
            device.ExpirationTimeout = this.ExpirationTimeout;
            device.parent = this;
            device.IsRoot = false;
            this.SetInterfaceToHost(device);
            UPnPDevice[] destinationArray = new UPnPDevice[this.EmbeddedDevices.Length + 1];
            Array.Copy(this.EmbeddedDevices, 0, destinationArray, 0, this.EmbeddedDevices.Length);
            destinationArray[this.EmbeddedDevices.Length] = device;
            Array.Sort(destinationArray, new UPnPDeviceComparer_Type());
            this.EmbeddedDevices = destinationArray;
            if (!this.ControlPointOnly)
            {
                device.AddSubVirtualDirectory(device.UniqueDeviceName);
                this.AddVirtualDirectory(device.UniqueDeviceName, new VirtualDirectoryHandler(device.HandleParent_Header), new VirtualDirectoryHandler(device.HandleParent));
            }
            else
            {
                this.AddVirtualDirectory(device.UniqueDeviceName, null, new VirtualDirectoryHandler(device.EventProcesser));
                this.ProcessDevice_EVENTCALLBACK(this);
            }
        }

        public void AddService(IUPnPService service)
        {
            this.AddService(service.GetUPnPService());
        }

        public void AddService(UPnPService service)
        {
            if (this.ControlPointOnly)
            {
                string absoluteUri;
                string str2;
                int num;
                string str3;
                if (this.BaseURL == null)
                {
                    absoluteUri = "";
                }
                else
                {
                    absoluteUri = this.BaseURL.AbsoluteUri;
                }
                if (!absoluteUri.EndsWith("/"))
                {
                    absoluteUri = absoluteUri + "/";
                }
                if (!service.__controlurl.StartsWith("http://"))
                {
                    if (service.__controlurl.StartsWith("/"))
                    {
                        service.ControlURL = "http://" + this.BaseURL.Host + ":" + this.BaseURL.Port.ToString() + service.__controlurl;
                    }
                    else
                    {
                        service.ControlURL = absoluteUri + service.__controlurl;
                    }
                }
                if (!service.SCPDURL.StartsWith("http://"))
                {
                    if (service.SCPDURL.StartsWith("/"))
                    {
                        service.SCPDURL = "http://" + this.BaseURL.Host + ":" + this.BaseURL.Port.ToString() + service.SCPDURL;
                    }
                    else
                    {
                        service.SCPDURL = absoluteUri + service.SCPDURL;
                    }
                }
                if (!service.__eventurl.StartsWith("http://"))
                {
                    if (service.__eventurl.StartsWith("/"))
                    {
                        service.EventURL = "http://" + this.BaseURL.Host + ":" + this.BaseURL.Port.ToString() + service.__eventurl;
                    }
                    else
                    {
                        service.EventURL = absoluteUri + service.__eventurl;
                    }
                }
                SSDP.ParseURL(service.__eventurl, out str2, out num, out str3);
                if (this.InterfaceToHost != null)
                {
                    try
                    {
                        string str4 = this.InterfaceToHost.ToString();
                        MiniWebServer server = (MiniWebServer) CPWebServerTable[str4];
                        UPnPDevice parent = this;
                        while (parent.parent != null)
                        {
                            parent = parent.parent;
                        }
                        parent.AddVirtualDirectory(this.UniqueDeviceName, null, new VirtualDirectoryHandler(this.EventProcesser));
                        service.EventCallbackURL = "http://" + this.InterfaceToHost.ToString() + ":" + server.LocalIPEndPoint.Port.ToString() + "/" + this.UniqueDeviceName + "/" + service.ServiceID;
                    }
                    catch (Exception exception)
                    {
                        EventLogger.Log(exception);
                    }
                }
            }
            else
            {
                if ((service.SCPDURL == "") || (service.SCPDURL == null))
                {
                    service.SCPDURL = "_" + service.ServiceID + "_scpd.xml";
                }
                if (service.ControlURL == "")
                {
                    service.ControlURL = "_" + service.ServiceID + "_control";
                }
                if (service.EventURL == "")
                {
                    service.EventURL = "_" + service.ServiceID + "_event";
                }
            }
            service.ParentDevice = this;
            SortedList list = new SortedList();
            foreach (UPnPService service2 in this.Services)
            {
                list.Add(service2.ServiceURN + "[[" + service2.ServiceID, service2);
            }
            list.Add(service.ServiceURN + "[[" + service.ServiceID, service);
            UPnPService[] array = new UPnPService[this.Services.Length + 1];
            list.Values.CopyTo(array, 0);
            this.Services = array;
        }

        internal void AddSubVirtualDirectory(string vd)
        {
            for (int i = 0; i < this.Services.Length; i++)
            {
                this.Services[i].AddVirtualDirectory(vd);
            }
            foreach (UPnPDevice device in this.EmbeddedDevices)
            {
                device.AddSubVirtualDirectory(vd);
            }
        }

        public void AddVirtualDirectory(string dir, VirtualDirectoryHandler HeaderCallback, VirtualDirectoryHandler PacketCallback)
        {
            if (!dir.StartsWith("/"))
            {
                dir = "/" + dir;
            }
            this.VirtualDir_Table[dir] = PacketCallback;
            this.VirtualDir_Header_Table[dir] = HeaderCallback;
        }

        public void Advertise()
        {
            this.SendNotify();
        }

        private HTTPMessage[] BuildByePacket()
        {
            ArrayList byeList = new ArrayList();
            HTTPMessage message = new HTTPMessage();
            message.Directive = "NOTIFY";
            message.DirectiveObj = "*";
            message.AddTag("Host", "239.255.255.250:1900");
            message.AddTag("NT", "upnp:rootdevice");
            message.AddTag("NTS", "ssdp:byebye");
            message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::upnp:rootdevice");
            byeList.Add(message);
            this.BuildByePacket2(byeList);
            foreach (UPnPDevice device in this.EmbeddedDevices)
            {
                device.BuildByePacket2(byeList);
            }
            return (HTTPMessage[]) byeList.ToArray(typeof(HTTPMessage));
        }

        private void BuildByePacket2(ArrayList ByeList)
        {
            HTTPMessage message;
            for (int i = 0; i < this.Services.Length; i++)
            {
                message = new HTTPMessage();
                message.Directive = "NOTIFY";
                message.DirectiveObj = "*";
                message.AddTag("Host", "239.255.255.250:1900");
                message.AddTag("NT", this.Services[i].ServiceURN);
                message.AddTag("NTS", "ssdp:byebye");
                message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + this.Services[i].ServiceURN);
                ByeList.Add(message);
            }
            message = new HTTPMessage();
            message.Directive = "NOTIFY";
            message.DirectiveObj = "*";
            message.AddTag("Host", "239.255.255.250:1900");
            message.AddTag("NT", this.DeviceURN);
            message.AddTag("NTS", "ssdp:byebye");
            message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + this.DeviceURN);
            ByeList.Add(message);
            message = new HTTPMessage();
            message.Directive = "NOTIFY";
            message.DirectiveObj = "*";
            message.AddTag("Host", "239.255.255.250:1900");
            message.AddTag("NT", "uuid:" + this.UniqueDeviceName);
            message.AddTag("NTS", "ssdp:byebye");
            message.AddTag("USN", "uuid:" + this.UniqueDeviceName);
            ByeList.Add(message);
        }

        internal string BuildErrorBody(UPnPCustomException e)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("<s:Envelope\r\n");
            builder.Append("   xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"\r\n");
            builder.Append("   s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n");
            builder.Append("   <s:Body>\r\n");
            builder.Append("      <s:Fault>\r\n");
            builder.Append("         <faultcode>s:Client</faultcode>\r\n");
            builder.Append("         <faultstring>UPnPError</faultstring>\r\n");
            builder.Append("            <detail>\r\n");
            builder.Append("               <UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">\r\n");
            builder.Append("                  <errorCode>" + e.ErrorCode.ToString() + "</errorCode>\r\n");
            builder.Append("                  <errorDescription>" + UPnPStringFormatter.EscapeString(e.ErrorDescription) + "</errorDescription>\r\n");
            builder.Append("               </UPnPError>\r\n");
            builder.Append("            </detail>\r\n");
            builder.Append("      </s:Fault>\r\n");
            builder.Append("   </s:Body>\r\n");
            builder.Append("</s:Envelope>");
            return builder.ToString();
        }

        private HTTPMessage[] BuildNotifyPacket(IPAddress local)
        {
            ArrayList notifyList = new ArrayList();
            IPEndPoint localIPEndPoint = null;
            try
            {
                localIPEndPoint = ((MiniWebServer) this.WebServerTable[local.ToString()]).LocalIPEndPoint;
            }
            catch (Exception)
            {
                return new HTTPMessage[0];
            }
            string tagData = "http://" + localIPEndPoint.Address.ToString() + ":" + localIPEndPoint.Port.ToString() + "/";
            HTTPMessage message = new HTTPMessage();
            message.Directive = "NOTIFY";
            message.DirectiveObj = "*";
            message.AddTag("Host", "239.255.255.250:1900");
            message.AddTag("NT", "upnp:rootdevice");
            message.AddTag("NTS", "ssdp:alive");
            message.AddTag("Location", tagData);
            message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::upnp:rootdevice");
            message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
            message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
            notifyList.Add(message);
            this.BuildNotifyPacket2(tagData, notifyList);
            foreach (UPnPDevice device in this.EmbeddedDevices)
            {
                device.BuildNotifyPacket2(tagData, notifyList);
            }
            return (HTTPMessage[]) notifyList.ToArray(typeof(HTTPMessage));
        }

        private void BuildNotifyPacket2(string BaseURL, ArrayList NotifyList)
        {
            HTTPMessage message;
            for (int i = 0; i < this.Services.Length; i++)
            {
                message = new HTTPMessage();
                message.Directive = "NOTIFY";
                message.DirectiveObj = "*";
                message.AddTag("Host", "239.255.255.250:1900");
                message.AddTag("NT", this.Services[i].ServiceURN);
                message.AddTag("NTS", "ssdp:alive");
                message.AddTag("Location", BaseURL);
                message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + this.Services[i].ServiceURN);
                message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                NotifyList.Add(message);
            }
            message = new HTTPMessage();
            message.Directive = "NOTIFY";
            message.DirectiveObj = "*";
            message.AddTag("Host", "239.255.255.250:1900");
            message.AddTag("NT", this.DeviceURN);
            message.AddTag("NTS", "ssdp:alive");
            message.AddTag("Location", BaseURL);
            message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + this.DeviceURN);
            message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
            message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
            NotifyList.Add(message);
            message = new HTTPMessage();
            message.Directive = "NOTIFY";
            message.DirectiveObj = "*";
            message.AddTag("Host", "239.255.255.250:1900");
            message.AddTag("NT", "uuid:" + this.UniqueDeviceName);
            message.AddTag("NTS", "ssdp:alive");
            message.AddTag("Location", BaseURL);
            message.AddTag("USN", "uuid:" + this.UniqueDeviceName);
            message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
            message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
            NotifyList.Add(message);
        }

        private void CancelEvent(string MethodData, string SID)
        {
            for (int i = 0; i < this.Services.Length; i++)
            {
                if (this.Services[i].EventURL == MethodData)
                {
                    this.Services[i]._CancelEvent(SID);
                    break;
                }
            }
        }

        public void ClearCustomFieldsInDescription()
        {
            this.CustomField.Clear();
        }

        public void ContainsSearchTarget(string ST, string Location, ArrayList ResponseList)
        {
            HTTPMessage message;
            if (ST == "ssdp:all")
            {
                message = new HTTPMessage();
                message.StatusCode = 200;
                message.StatusData = "OK";
                message.AddTag("ST", "uuid:" + this.UniqueDeviceName);
                message.AddTag("USN", "uuid:" + this.UniqueDeviceName);
                message.AddTag("Location", Location);
                message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                message.AddTag("EXT", "");
                message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                ResponseList.Add(message);
                message = new HTTPMessage();
                message.StatusCode = 200;
                message.StatusData = "OK";
                message.AddTag("ST", this.DeviceURN);
                message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + this.DeviceURN);
                message.AddTag("Location", Location);
                message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                message.AddTag("EXT", "");
                message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                ResponseList.Add(message);
            }
            if (("uuid:" + this.UniqueDeviceName) == ST)
            {
                message = new HTTPMessage();
                message.StatusCode = 200;
                message.StatusData = "OK";
                message.AddTag("ST", ST);
                message.AddTag("USN", "uuid:" + this.UniqueDeviceName);
                message.AddTag("Location", Location);
                message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                message.AddTag("EXT", "");
                message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                ResponseList.Add(message);
            }
            if (this.DeviceURN == ST)
            {
                message = new HTTPMessage();
                message.StatusCode = 200;
                message.StatusData = "OK";
                message.AddTag("ST", this.DeviceURN);
                message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + this.DeviceURN);
                message.AddTag("Location", Location);
                message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                message.AddTag("EXT", "");
                message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                ResponseList.Add(message);
            }
            foreach (UPnPService service in this.Services)
            {
                if (ST == "ssdp:all")
                {
                    message = new HTTPMessage();
                    message.StatusCode = 200;
                    message.StatusData = "OK";
                    message.AddTag("ST", service.ServiceURN);
                    message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + service.ServiceURN);
                    message.AddTag("Location", Location);
                    message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                    message.AddTag("EXT", "");
                    message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                    ResponseList.Add(message);
                }
                if (service.ServiceURN == ST)
                {
                    //MOD
                    message = new HTTPMessage();
                    message.StatusCode = 200;
                    message.StatusData = "OK";
                    /*
                    message.AddTag("ST", service.ServiceURN);
                    message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + service.ServiceURN);
                    message.AddTag("Location", Location);
                    message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                    message.AddTag("EXT", "");
                    message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                    */
                    message.AddTag("ST", service.ServiceURN);
                    message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::" + service.ServiceURN);
                    message.AddTag("Location", Location);
                    //message.AddTag("OPT", "\"http://schemas.upnp.org/upnp/1/0/\"; ns=01");
                    //message.AddTag("01-NLS", "61e19ba739c777be7796cbd67b08c224");
                    message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                    message.AddTag("Server", "Microsoft-Windows-NT/5.1 UPnP/1.0 UPnP-Device-Host/1.0");
                    message.AddTag("Ext", "");
                    message.DontShowContentLength = true;
                    ResponseList.Add(message);
                }
            }
            foreach (UPnPDevice device in this.EmbeddedDevices)
            {
                device.ContainsSearchTarget(ST, Location, ResponseList);
            }
        }

        public static UPnPDevice CreateEmbeddedDevice(double version, string UDN)
        {
            return new UPnPDevice(version, UDN);
        }

        public static UPnPDevice CreateRootDevice(int DeviceExpiration, double version, string RootDir)
        {
            return new UPnPDevice(DeviceExpiration, version, RootDir);
        }

        private void DisabledInterface(NetworkInfo sender, IPAddress ip)
        {
            this.SendNotify();
            try
            {
                SSDP ssdp = (SSDP) this.SSDPServerTable[ip.ToString()];
                if (ssdp != null)
                {
                    ssdp.Dispose();
                }
                this.SSDPServerTable[ip.ToString()] = null;
            }
            catch (Exception)
            {
            }
            try
            {
                MiniWebServer server = (MiniWebServer) this.WebServerTable[ip.ToString()];
                if (server != null)
                {
                    server.Dispose();
                }
                this.WebServerTable[ip.ToString()] = null;
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (!this.ControlPointOnly && this.IsRoot)
            {
                this.StopDevice();
            }
        }

        private void DisposeAllServers()
        {
            object[] array = new object[this.SSDPServerTable.Keys.Count];
            object[] objArray2 = new object[this.WebServerTable.Keys.Count];
            this.SSDPServerTable.Keys.CopyTo(array, 0);
            this.WebServerTable.Keys.CopyTo(objArray2, 0);
            for (int i = 0; i < array.Length; i++)
            {
                SSDP ssdp = (SSDP) this.SSDPServerTable[array[i]];
                this.SSDPServerTable.Remove(array[i]);
                if (ssdp != null)
                {
                    ssdp.Dispose();
                }
            }
            for (int j = 0; j < objArray2.Length; j++)
            {
                MiniWebServer server = (MiniWebServer) this.WebServerTable[objArray2[j]];
                this.WebServerTable.Remove(objArray2[j]);
                if (server != null)
                {
                    server.Dispose();
                }
            }
        }

        private void EventProcesser(UPnPDevice sender, HTTPMessage msg, HTTPSession WebSession, string VirtualDir)
        {
            if (this.ControlPointOnly)
            {
                string directive = msg.Directive;
                HTTPMessage packet = new HTTPMessage();
                if (directive != "NOTIFY")
                {
                    packet.StatusCode = 0x195;
                    packet.StatusData = directive + " not supported";
                    WebSession.Send(packet);
                }
                else if (directive == "NOTIFY")
                {
                    for (int i = 0; i < this.Services.Length; i++)
                    {
                        if (this.Services[i].IsYourEvent(msg.GetTag("SID")))
                        {
                            packet.StatusCode = 200;
                            packet.StatusData = "OK";
                            WebSession.Send(packet);
                            this.Services[i]._TriggerEvent(msg.GetTag("SID"), long.Parse(msg.GetTag("SEQ")), msg.StringBuffer);
                            break;
                        }
                    }
                }
            }
        }

        internal void FetchIcon(Uri IconUri)
        {
            HTTPRequest request = new HTTPRequest();
            this.InitialEventTable[request] = request;
            request.OnResponse += new HTTPRequest.RequestHandler(this.HandleIcon);
            request.PipelineRequest(IconUri, null);
        }

        ~UPnPDevice()
        {
            this.Dispose();
        }

        //MOD
        private IContentHandler contentHandler;
        public IContentHandler ContentHandler
        {
            get { return contentHandler; }
            set { contentHandler = value; }
        }

        //MOD
        //private HTTPMessage Get(string GetWhat, IPEndPoint local)
        private HTTPMessage Get(string GetWhat, IPEndPoint local, HTTPMessage msg, HTTPSession WebSession)
        {
            HTTPMessage message2;
            HTTPMessage message = new HTTPMessage();
            //MOD
            //if (GetWhat == "/")
            if (GetWhat == "/" || GetWhat == "/device.xml")
            {
                message.StatusCode = 200;
                message.StatusData = "OK";
                message.AddTag("Content-Type", "text/xml");
                message.BodyBuffer = this.GetRootDeviceXML(local);
                return message;
            }
            GetWhat = GetWhat.Substring(1);
            if ((GetWhat == "icon.png") && (this._icon != null))
            {
                lock (this._icon)
                {
                    MemoryStream stream = new MemoryStream();
                    this._icon.Save(stream, ImageFormat.Png);
                    message.StatusCode = 200;
                    message.StatusData = "OK";
                    message.ContentType = "image/png";
                    message.BodyBuffer = stream.ToArray();
                    stream.Close();
                }
                return message;
            }
            bool flag = false;
            for (int i = 0; i < this.Services.Length; i++)
            {
                if (GetWhat == this.Services[i].SCPDFile)
                {
                    flag = true;
                    message.StatusCode = 200;
                    message.StatusData = "OK";
                    message.AddTag("Content-Type", "text/xml");
                    message.BodyBuffer = this.Services[i].GetSCPDXml();
                    break;
                }
            }
            if (flag)
            {
                return message;
            }
            try
            {
                //MOD //TODO get rid of hardcoded values
                if (GetWhat.ToLower().StartsWith("pictures") == true
                    || GetWhat.ToLower().StartsWith("music") == true
                    || GetWhat.ToLower().StartsWith("videos") == true)
                {
                    message2 = null; //Send in app code
                    contentHandler.HandleContent(GetWhat, local, msg, WebSession);
                }
                else
                {
                    FileStream stream2 = new FileStream(this.RootPath + GetWhat, FileMode.Open, FileAccess.Read, FileShare.Read);
                    byte[] buffer = new byte[(int)stream2.Length];
                    stream2.Read(buffer, 0, (int)stream2.Length);
                    stream2.Close();
                    message.StatusCode = 200;
                    message.StatusData = "OK";
                    string tagData = "application/octet-stream";
                    if (GetWhat.EndsWith(".html") || GetWhat.EndsWith(".htm"))
                    {
                        tagData = "text/html";
                    }
                    else if (GetWhat.EndsWith(".xml"))
                    {
                        tagData = "text/xml";
                    }
                    message.AddTag("Content-Type", tagData);
                    message.BodyBuffer = buffer;
                    message2 = message;
                }
            }
            catch (Exception)
            
            {
                throw new UPnPCustomException(0x194, "File Not Found");
            }
            return message2;
        }

        public string GetCustomFieldFromDescription(string FieldName, string Namespace)
        {
            if (this.CustomField.ContainsKey(Namespace) && ((Hashtable) this.CustomField[Namespace]).ContainsKey(FieldName))
            {
                return (string) ((Hashtable) this.CustomField[Namespace])[FieldName];
            }
            return null;
        }

        public UPnPDevice GetDevice(string UDN)
        {
            if (this.UniqueDeviceName == UDN)
            {
                return this;
            }
            UPnPDevice device = null;
            foreach (UPnPDevice device2 in this.EmbeddedDevices)
            {
                device = device2.GetDevice(UDN);
                if (device != null)
                {
                    return device;
                }
            }
            return null;
        }

        public UPnPDevice[] GetDevices(string urn)
        {
            ArrayList list = new ArrayList();
            if (this.DeviceURN.ToLower().StartsWith(urn.ToLower()))
            {
                list.Add(this);
            }
            foreach (UPnPDevice device in this.EmbeddedDevices)
            {
                if (device.DeviceURN.ToLower().StartsWith(urn.ToLower()))
                {
                    list.Add(device);
                }
            }
            return (UPnPDevice[]) list.ToArray(typeof(UPnPDevice));
        }

        public override int GetHashCode()
        {
            if (this.ControlPointOnly)
            {
                if (this.BaseURL.Host == "127.0.0.1")
                {
                    return this.descXmlLocation.GetHashCode();
                }
                return this.BaseURL.GetHashCode();
            }
            return base.GetHashCode();
        }

        private void GetNonRootDeviceXML(IPEndPoint local, XmlTextWriter XDoc)
        {
            IDictionaryEnumerator enumerator = this.CustomField.GetEnumerator();
            DText text = new DText();
            text.ATTRMARK = ":";
            XDoc.WriteStartElement("device");
            //MOD
            while (enumerator.MoveNext())
            {
                IDictionaryEnumerator enumerator2 = ((Hashtable) enumerator.Value).GetEnumerator();
                while (enumerator2.MoveNext())
                {
                    string key = (string) enumerator2.Key;
                    string str2 = (string) enumerator2.Value;
                    string ns = (string) enumerator.Key;
                    text[0] = key;
                    if (text.DCOUNT() == 2)
                    {
                        XDoc.WriteStartElement(text[1], text[2], ns);
                        XDoc.WriteString(str2);
                        XDoc.WriteEndElement();
                    }
                    else
                    {
                        if (ns != "")
                        {
                            XDoc.WriteElementString(key, ns, str2);
                            continue;
                        }
                        XDoc.WriteElementString(key, str2);
                    }
                }
            }
            XDoc.WriteElementString("deviceType", this.DeviceURN);
            XDoc.WriteElementString("friendlyName", this.FriendlyName);
            if (this.Manufacturer != null)
            {
                XDoc.WriteElementString("manufacturer", this.Manufacturer);
            }
            if (this.ManufacturerURL != null)
            {
                XDoc.WriteElementString("manufacturerURL", this.ManufacturerURL);
            }
            if (this.ModelDescription != null)
            {
                XDoc.WriteElementString("modelDescription", this.ModelDescription);
            }
            if (this.ModelName != null)
            {
                XDoc.WriteElementString("modelName", this.ModelName);
            }
            if (this.ModelNumber != null)
            {
                XDoc.WriteElementString("modelNumber", this.ModelNumber);
            }
            if (this.ModelURL != null)
            {
                XDoc.WriteElementString("modelURL", HTTPMessage.UnEscapeString(this.ModelURL.AbsoluteUri));
            }
            if (this.SerialNumber != null)
            {
                XDoc.WriteElementString("serialNumber", this.SerialNumber);
            }
            XDoc.WriteElementString("UDN", "uuid:" + this.UniqueDeviceName);
            if (this.ProductCode != null)
            {
                XDoc.WriteElementString("UPC", this.ProductCode);
            }
            if (this.HasPresentation)
            {
                XDoc.WriteElementString("presentationURL", this.PresentationURL);
            }
            if (this._icon != null)
            {
                lock (this._icon)
                {
                    XDoc.WriteStartElement("iconList");
                    XDoc.WriteStartElement("icon");
                    XDoc.WriteElementString("mimetype", "image/png");
                    XDoc.WriteElementString("width", this._icon.Width.ToString());
                    XDoc.WriteElementString("height", this._icon.Height.ToString());
                    XDoc.WriteElementString("depth", Image.GetPixelFormatSize(this._icon.PixelFormat).ToString());
                    XDoc.WriteElementString("url", "/icon.png");
                    XDoc.WriteEndElement();
                    XDoc.WriteStartElement("icon");
                    XDoc.WriteElementString("mimetype", "image/jpg");
                    XDoc.WriteElementString("width", this._icon.Width.ToString());
                    XDoc.WriteElementString("height", this._icon.Height.ToString());
                    XDoc.WriteElementString("depth", Image.GetPixelFormatSize(this._icon.PixelFormat).ToString());
                    XDoc.WriteElementString("url", "/icon.jpg");
                    XDoc.WriteEndElement();
                    if (this._icon2 != null)
                    {
                        XDoc.WriteStartElement("icon");
                        XDoc.WriteElementString("mimetype", "image/png");
                        XDoc.WriteElementString("width", this._icon2.Width.ToString());
                        XDoc.WriteElementString("height", this._icon2.Height.ToString());
                        XDoc.WriteElementString("depth", Image.GetPixelFormatSize(this._icon.PixelFormat).ToString());
                        XDoc.WriteElementString("url", "/icon2.png");
                        XDoc.WriteEndElement();
                        XDoc.WriteStartElement("icon");
                        XDoc.WriteElementString("mimetype", "image/jpg");
                        XDoc.WriteElementString("width", this._icon2.Width.ToString());
                        XDoc.WriteElementString("height", this._icon2.Height.ToString());
                        XDoc.WriteElementString("depth", Image.GetPixelFormatSize(this._icon2.PixelFormat).ToString());
                        XDoc.WriteElementString("url", "/icon2.jpg");
                        XDoc.WriteEndElement();
                    }
                    XDoc.WriteEndElement();
                }
            }
            if (this.Services.Length > 0)
            {
                XDoc.WriteStartElement("serviceList");
                for (int i = 0; i < this.Services.Length; i++)
                {
                    this.Services[i].GetServiceXML(XDoc);
                }
                XDoc.WriteEndElement();
            }
            if (this.EmbeddedDevices.Length > 0)
            {
                XDoc.WriteStartElement("deviceList");
                for (int j = 0; j < this.EmbeddedDevices.Length; j++)
                {
                    this.EmbeddedDevices[j].GetNonRootDeviceXML(local, XDoc);
                }
                XDoc.WriteEndElement();
            }
            XDoc.WriteEndElement();
        }

        public byte[] GetRootDeviceXML(IPEndPoint local)
        {
            MemoryStream w = new MemoryStream();
            XmlTextWriter xDoc = new XmlTextWriter(w, Encoding.UTF8);
            xDoc.Formatting = Formatting.Indented;
            xDoc.Indentation = 3;
            xDoc.WriteStartDocument();
            xDoc.WriteStartElement("root", "urn:schemas-upnp-org:device-1-0");
            if (this._BootID != "")
            {
                xDoc.WriteAttributeString("configId", this._BootID);
            }
            xDoc.WriteStartElement("specVersion");
            xDoc.WriteElementString("major", this.Arch_Major.ToString());
            xDoc.WriteElementString("minor", this.Arch_Minor.ToString());
            xDoc.WriteEndElement();
            this.GetNonRootDeviceXML(local, xDoc);
            xDoc.WriteEndElement();
            xDoc.WriteEndDocument();
            xDoc.Flush();
            byte[] buffer = new byte[w.Length - 3L];
            w.Seek(3L, SeekOrigin.Begin);
            w.Read(buffer, 0, buffer.Length);
            xDoc.Close();
            //return buffer;
            //MOD
            byte[] buffer2 = new byte[buffer.Length + 2];
            Array.Copy(buffer, buffer2, buffer.Length);
            buffer2[buffer2.Length - 2] = (byte)'\r';
            buffer2[buffer2.Length - 1] = (byte)'\n';
            return buffer2;
        }

        public UPnPService GetService(string ServiceID)
        {
            if (!ServiceID.ToUpper().StartsWith("URN:"))
            {
                ServiceID = "urn:upnp-org:serviceId:" + ServiceID;
            }
            foreach (UPnPService service in this.Services)
            {
                if (service.ServiceID == ServiceID)
                {
                    return service;
                }
            }
            return null;
        }

        public UPnPService[] GetServices(string urn)
        {
            ArrayList list = new ArrayList();
            foreach (UPnPService service in this.Services)
            {
                if (service.ServiceURN.ToLower().StartsWith(urn.ToLower()))
                {
                    list.Add(service);
                }
            }
            return (UPnPService[]) list.ToArray(typeof(UPnPService));
        }

        private void HandleEventReceive(HTTPSession TheSession, HTTPMessage msg)
        {
            TheSession.Close();
        }

        private void HandleHeaderRequest(HTTPMessage msg, HTTPSession WebSession)
        {
            DText text = new DText();
            HTTPMessage message = new HTTPMessage();
            string directive = msg.Directive;
            string directiveObj = msg.DirectiveObj;
            VirtualDirectoryHandler handler = null;
            VirtualDirectoryHandler handler2 = null;
            string key = "";
            string str4 = "";
            try
            {
                int index = directiveObj.IndexOf("/", 1);
                if (index != -1)
                {
                    str4 = directiveObj.Substring(index);
                    key = directiveObj.Substring(0, index);
                    if (this.VirtualDir_Header_Table.ContainsKey(key) && (this.VirtualDir_Header_Table[key] != null))
                    {
                        handler = (VirtualDirectoryHandler) this.VirtualDir_Header_Table[key];
                    }
                    if (this.VirtualDir_Table.ContainsKey(key) && (this.VirtualDir_Table[key] != null))
                    {
                        handler2 = (VirtualDirectoryHandler) this.VirtualDir_Table[key];
                    }
                }
            }
            catch (Exception)
            {
            }
            if ((handler != null) || (handler2 != null))
            {
                HTTPMessage message2 = (HTTPMessage) msg.Clone();
                message2.DirectiveObj = str4;
                WebSession.InternalStateObject = new object[] { key, str4, handler2 };
                if (handler != null)
                {
                    handler(this, message2, WebSession, key);
                }
            }
        }

        private void HandleIcon(HTTPRequest sender, HTTPMessage response, object Tag)
        {
            Image image = null;
            this.InitialEventTable.Remove(sender);
            if (response.StatusCode == 200)
            {
                MemoryStream stream = new MemoryStream(response.BodyBuffer);
                image = Image.FromStream(stream);
                if (image != null)
                {
                    this._icon = image;
                }
            }
            sender.Dispose();
        }

        private void HandleInitialEvent(HTTPRequest R, HTTPMessage M, object Tag)
        {
            R.Dispose();
            this.InitialEventTable.Remove(R);
        }

        private void HandleParent(UPnPDevice sender, HTTPMessage msg, HTTPSession WebSession, string VD)
        {
            this.HandleWebRequest(msg, WebSession);
        }

        private void HandleParent_Header(UPnPDevice sender, HTTPMessage msg, HTTPSession WebSession, string VD)
        {
            this.HandleHeaderRequest(msg, WebSession);
        }

        private void HandleSearch(string ST, IPEndPoint src, IPEndPoint local)
        {
            string str = src.Address.ToString();
            ArrayList responseList = new ArrayList();
            //MOD
            //string tagData = "http://" + local.Address.ToString() + ":" + ((MiniWebServer) this.WebServerTable[local.Address.ToString()]).LocalIPEndPoint.Port.ToString() + "/";
            string tagData = "http://" + local.Address.ToString() + ":" + ((MiniWebServer)this.WebServerTable[local.Address.ToString()]).LocalIPEndPoint.Port.ToString() + "/device.xml";
            if ((ST == "upnp:rootdevice") || (ST == "ssdp:all"))
            {
                HTTPMessage message = new HTTPMessage();
                message.StatusCode = 200;
                message.StatusData = "OK";
                message.AddTag("ST", "upnp:rootdevice");
                message.AddTag("USN", "uuid:" + this.UniqueDeviceName + "::upnp:rootdevice");
                message.AddTag("Location", tagData);
                message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                message.AddTag("EXT", "");
                message.AddTag("Cache-Control", "max-age=" + this.ExpirationTimeout.ToString());
                responseList.Add(message);
            }
            this.ContainsSearchTarget(ST, tagData, responseList);
            foreach (HTTPMessage message2 in responseList)
            {
                try
                {
                    ((SSDP) this.SSDPServerTable[local.Address.ToString()]).UnicastData(message2, src);
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }
            }
        }

        private void HandleWebRequest(HTTPMessage msg, HTTPSession WebSession)
        {
            DText text = new DText();
            HTTPMessage packet = new HTTPMessage();
            HTTPMessage mSG = null;
            string directive = msg.Directive;
            string directiveObj = msg.DirectiveObj;
            if (WebSession.InternalStateObject != null)
            {
                HTTPMessage message3 = (HTTPMessage) msg.Clone();
                object[] internalStateObject = (object[]) WebSession.InternalStateObject;
                message3.DirectiveObj = (string) internalStateObject[1];
                VirtualDirectoryHandler handler = (VirtualDirectoryHandler) internalStateObject[2];
                WebSession.InternalStateObject = null;
                handler(this, message3, WebSession, (string) internalStateObject[0]);
            }
            else if ((((directive != "GET") && (directive != "HEAD")) && ((directive != "POST") && (directive != "SUBSCRIBE"))) && ((directive != "UNSUBSCRIBE") && (directive != "NOTIFY")))
            {
                packet.StatusCode = 0x195;
                packet.StatusData = directive + " not supported";
                WebSession.Send(packet);
            }
            else
            {
                if ((directive == "GET") || (directive == "HEAD"))
                {
                    try
                    {
                        packet = this.Get(directiveObj, WebSession.Source, msg, WebSession); //MOD
                    }
                    catch (UPnPCustomException exception)
                    {
                        packet.StatusCode = exception.ErrorCode;
                        packet.StatusData = exception.ErrorDescription;
                        WebSession.Send(packet);
                        return;
                    }
                    catch (Exception exception2)
                    {
                        packet.StatusCode = 500;
                        packet.StatusData = "Internal";
                        packet.StringBuffer = exception2.ToString();
                    }
                    if (packet != null) //MOD
                    {
                        if (directive == "HEAD")
                        {
                            packet.BodyBuffer = null;
                        }
                    }
                    WebSession.Send(packet);
                }
                if (directive == "POST")
                {
                    try
                    {
                        packet = this.Post(directiveObj, msg.StringBuffer, msg.GetTag("SOAPACTION"), WebSession);
                    }
                    catch (DelayedResponseException)
                    {
                        this.InvokerInfo.Remove(Thread.CurrentThread.GetHashCode());
                        WebSession.StopReading();
                        return;
                    }
                    catch (UPnPCustomException exception3)
                    {
                        EventLogger.Log(this, EventLogEntryType.Error, "UPnP Error [" + exception3.ErrorCode.ToString() + "] " + exception3.ErrorDescription);
                        packet.StatusCode = 500;
                        packet.StatusData = "Internal";
                        packet.StringBuffer = this.BuildErrorBody(exception3);
                        WebSession.Send(packet);
                        this.InvokerInfo.Remove(Thread.CurrentThread.GetHashCode());
                        return;
                    }
                    catch (UPnPInvokeException exception4)
                    {
                        packet.StatusCode = 500;
                        packet.StatusData = "Internal";
                        if (exception4.UPNP != null)
                        {
                            EventLogger.Log(this, EventLogEntryType.Error, "UPnP Error [" + exception4.UPNP.ErrorCode.ToString() + "] " + exception4.UPNP.ErrorDescription);
                            packet.StringBuffer = this.BuildErrorBody(exception4.UPNP);
                        }
                        else
                        {
                            EventLogger.Log(this, EventLogEntryType.Error, "UPnP Invocation Error [" + exception4.MethodName + "] " + exception4.Message);
                            packet.StringBuffer = this.BuildErrorBody(new UPnPCustomException(500, exception4.Message));
                        }
                        WebSession.Send(packet);
                        this.InvokerInfo.Remove(Thread.CurrentThread.GetHashCode());
                        return;
                    }
                    catch (UPnPTypeMismatchException exception5)
                    {
                        packet.StatusCode = 500;
                        packet.StatusData = "Internal";
                        packet.StringBuffer = this.BuildErrorBody(new UPnPCustomException(0x192, exception5.Message));
                        WebSession.Send(packet);
                        this.InvokerInfo.Remove(Thread.CurrentThread.GetHashCode());
                        return;
                    }
                    catch (UPnPStateVariable.OutOfRangeException exception6)
                    {
                        packet.StatusCode = 500;
                        packet.StatusData = "Internal";
                        packet.StringBuffer = this.BuildErrorBody(new UPnPCustomException(0x192, exception6.Message));
                        WebSession.Send(packet);
                        this.InvokerInfo.Remove(Thread.CurrentThread.GetHashCode());
                        return;
                    }
                    catch (TargetInvocationException exception7)
                    {
                        Exception innerException = exception7.InnerException;
                        while ((innerException.InnerException != null) && !typeof(UPnPCustomException).IsInstanceOfType(innerException))
                        {
                            innerException = innerException.InnerException;
                        }
                        if (typeof(UPnPCustomException).IsInstanceOfType(innerException))
                        {
                            UPnPCustomException e = (UPnPCustomException) innerException;
                            EventLogger.Log(this, EventLogEntryType.Error, "UPnP Error [" + e.ErrorCode.ToString() + "] " + e.ErrorDescription);
                            packet.StatusCode = 500;
                            packet.StatusData = "Internal";
                            packet.StringBuffer = this.BuildErrorBody(e);
                            WebSession.Send(packet);
                            this.InvokerInfo.Remove(Thread.CurrentThread.GetHashCode());
                        }
                        else
                        {
                            packet.StatusCode = 500;
                            packet.StatusData = "Internal";
                            packet.StringBuffer = this.BuildErrorBody(new UPnPCustomException(500, innerException.ToString()));
                            WebSession.Send(packet);
                            EventLogger.Log(innerException);
                        }
                        return;
                    }
                    catch (Exception exception10)
                    {
                        packet.StatusCode = 500;
                        packet.StatusData = "Internal";
                        packet.StringBuffer = this.BuildErrorBody(new UPnPCustomException(500, exception10.ToString()));
                        WebSession.Send(packet);
                        EventLogger.Log(exception10);
                        return;
                    }
                    WebSession.Send(packet);
                    this.InvokerInfo.Remove(Thread.CurrentThread.GetHashCode());
                }
                else
                {
                    if (directive == "SUBSCRIBE")
                    {
                        string tag = msg.GetTag("SID");
                        string str4 = msg.GetTag("NT");
                        string timeout = msg.GetTag("Timeout");
                        string callbackURL = msg.GetTag("Callback");
                        if (timeout == "")
                        {
                            timeout = "7200";
                        }
                        else
                        {
                            timeout = timeout.Substring(timeout.IndexOf("-") + 1).Trim().ToUpper();
                            if (timeout == "INFINITE")
                            {
                                timeout = "0";
                            }
                        }
                        if (tag != "")
                        {
                            this.RenewEvents(directiveObj.Substring(1), tag, timeout);
                        }
                        else
                        {
                            try
                            {
                                mSG = this.SubscribeEvents(ref tag, directiveObj.Substring(1), callbackURL, timeout);
                            }
                            catch (Exception exception11)
                            {
                                HTTPMessage message4 = new HTTPMessage();
                                message4.StatusCode = 500;
                                message4.StatusData = exception11.Message;
                                WebSession.Send(message4);
                                return;
                            }
                        }
                        if (timeout == "0")
                        {
                            timeout = "Second-infinite";
                        }
                        else
                        {
                            timeout = "Second-" + timeout;
                        }
                        packet.StatusCode = 200;
                        packet.StatusData = "OK";
                        packet.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
                        packet.AddTag("SID", tag);
                        packet.AddTag("Timeout", timeout);
                        WebSession.Send(packet);
                        if (mSG != null)
                        {
                            Uri[] uriArray = this.ParseEventURL(callbackURL);
                            for (int i = 0; i < uriArray.Length; i++)
                            {
                                mSG.DirectiveObj = HTTPMessage.UnEscapeString(uriArray[i].PathAndQuery);
                                mSG.AddTag("Host", uriArray[i].Host + ":" + uriArray[i].Port.ToString());
                                IPEndPoint dest = new IPEndPoint(IPAddress.Parse(uriArray[i].Host), uriArray[i].Port);
                                HTTPRequest request = new HTTPRequest();
                                request.OnResponse += new HTTPRequest.RequestHandler(this.HandleInitialEvent);
                                this.InitialEventTable[request] = request;
                                request.PipelineRequest(dest, mSG, null);
                            }
                        }
                    }
                    if (directive == "UNSUBSCRIBE")
                    {
                        this.CancelEvent(directiveObj.Substring(1), msg.GetTag("SID"));
                        packet.StatusCode = 200;
                        packet.StatusData = "OK";
                        WebSession.Send(packet);
                    }
                }
            }
        }

        private HTTPMessage Invoke(string Control, string XML, string SOAPACTION, HTTPSession WebSession)
        {
            string actionName = "";
            ArrayList varList = new ArrayList();
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            reader2.Read();
            reader2.MoveToContent();
            if (reader2.LocalName == "Envelope")
            {
                reader2.Read();
                reader2.MoveToContent();
                if (reader2.LocalName == "Body")
                {
                    reader2.Read();
                    reader2.MoveToContent();
                    actionName = reader2.LocalName;
                    reader2.Read();
                    reader2.MoveToContent();
                    while (((reader2.LocalName != actionName) && (reader2.LocalName != "Envelope")) && (reader2.LocalName != "Body"))
                    {
                        UPnPArgument argument = new UPnPArgument(reader2.LocalName, reader2.ReadString());
                        //MOD for xbox 360
                        if (actionName == "Browse" && argument.Name == "ContainerID")
                        {
                            argument.Name = "ObjectID";
                        }
                        varList.Add(argument);
                        if (((reader2.LocalName == "") || !reader2.IsStartElement()) || reader2.IsEmptyElement)
                        {
                            reader2.Read();
                            reader2.MoveToContent();
                        }
                    }
                }
            }
            object retVal = "";
            bool flag = false;
            int index = 0;
            index = 0;
            while (index < this.Services.Length)
            {
                if (this.Services[index].ControlURL == Control)
                {
                    if (actionName != "QueryStateVariable")
                    {
                        UPnPAction action = this.Services[index].GetAction(actionName);
                        if (action == null)
                        {
                            break;
                        }
                        ArrayList list2 = new ArrayList();
                        InvokerInfoStruct struct2 = new InvokerInfoStruct();
                        struct2.WebSession = WebSession;
                        struct2.MethodName = actionName;
                        struct2.SOAPAction = SOAPACTION;
                        foreach (UPnPArgument argument2 in action.Arguments)
                        {
                            if (argument2.IsReturnValue)
                            {
                                struct2.RetArg = (UPnPArgument) argument2.Clone();
                            }
                            if (argument2.Direction == "out")
                            {
                                list2.Add(argument2.Clone());
                            }
                        }
                        struct2.OutArgs = (UPnPArgument[]) list2.ToArray(typeof(UPnPArgument));
                        this.InvokerInfo[Thread.CurrentThread.GetHashCode()] = struct2;
                    }
                    retVal = this.Services[index].InvokeLocal(actionName, ref varList);
                    flag = true;
                    break;
                }
                index++;
            }
            if (!flag)
            {
                throw new UPnPCustomException(0x191, "Invalid Action: " + actionName);
            }
            return this.ParseInvokeResponse(actionName, SOAPACTION, this.Services[index].ServiceURN, retVal, (UPnPArgument[]) varList.ToArray(typeof(UPnPArgument)));
        }

        private void ManualNewDeviceInterface(IPAddress ip, int Port)
        {
            if (!this.NoSSDP)
            {
                SSDP ssdp = new SSDP(new IPEndPoint(ip, 0x76c), this.ExpirationTimeout);
                ssdp.OnRefresh += new SSDP.RefreshHandler(this.SendNotify);
                ssdp.OnSearch += new SSDP.SearchHandler(this.HandleSearch);
                this.SSDPServerTable[ip.ToString()] = ssdp;
            }
            string str = ip.ToString();
            MiniWebServer server = new MiniWebServer(new Uri("http://" + ip.ToString() + ":" + Port.ToString() + "/"));
            if ((this.OnSniff != null) || (this.OnSniffPacket != null))
            {
                server.OnSession += new MiniWebServer.NewSessionHandler(this.SniffSessionSink);
            }
            server.OnReceive += new MiniWebServer.HTTPReceiveHandler(this.HandleWebRequest);
            server.OnHeader += new MiniWebServer.HTTPReceiveHandler(this.HandleHeaderRequest);
            this.WebServerTable[ip.ToString()] = server;
            this.SendNotify(ip);
        }

        private void NewCPInterface(NetworkInfo sender, IPAddress ip)
        {
            lock (typeof(UPnPDevice))
            {
                if (!CPWebServerTable.ContainsKey(ip.ToString()))
                {
                    MiniWebServer server = new MiniWebServer(new Uri(string.Concat(new object[] { "http://", ip.ToString(), ":", NetworkInfo.GetFreePort(0x1388, 0x2710, ip), "/" })));
                    server.OnReceive += new MiniWebServer.HTTPReceiveHandler(this.HandleWebRequest);
                    server.OnHeader += new MiniWebServer.HTTPReceiveHandler(this.HandleHeaderRequest);
                    CPWebServerTable[ip.ToString()] = server;
                }
                else if (!this.CP_RegisteredInterfaces.ContainsKey(ip.ToString()))
                {
                    this.CP_RegisteredInterfaces[ip.ToString()] = true;
                    ((MiniWebServer) CPWebServerTable[ip.ToString()]).OnReceive += new MiniWebServer.HTTPReceiveHandler(this.HandleWebRequest);
                    ((MiniWebServer) CPWebServerTable[ip.ToString()]).OnHeader += new MiniWebServer.HTTPReceiveHandler(this.HandleHeaderRequest);
                }
            }
        }

        private void NewDeviceInterface(NetworkInfo sender, IPAddress ip)
        {
            try
            {
                MiniWebServer server;
                if (!this.NoSSDP)
                {
                    SSDP ssdp = new SSDP(new IPEndPoint(ip, 0x76c), this.ExpirationTimeout);
                    ssdp.OnRefresh += new SSDP.RefreshHandler(this.SendNotify);
                    ssdp.OnSearch += new SSDP.SearchHandler(this.HandleSearch);
                    this.SSDPServerTable[ip.ToString()] = ssdp;
                }
                string str = ip.ToString();
                if (this.UseThisPort == 0)
                {
                    server = new MiniWebServer(new Uri(string.Concat(new object[] { "http://", ip.ToString(), ":", NetworkInfo.GetFreePort(0xc350, 0xffdc, ip), "/" })));
                }
                else
                {
                    server = new MiniWebServer(new Uri("http://" + ip.ToString() + ":" + this.UseThisPort.ToString() + "/"));
                }
                if ((this.OnSniff != null) || (this.OnSniffPacket != null))
                {
                    server.OnSession += new MiniWebServer.NewSessionHandler(this.SniffSessionSink);
                }
                server.OnReceive += new MiniWebServer.HTTPReceiveHandler(this.HandleWebRequest);
                server.OnHeader += new MiniWebServer.HTTPReceiveHandler(this.HandleHeaderRequest);
                this.WebServerTable[ip.ToString()] = server;
                this.SendNotify(ip);
            }
            catch (SocketException exception)
            {
                EventLogger.Log(exception, "UPnPDevice: " + this.FriendlyName + " @" + ip.ToString());
            }
        }

        internal static UPnPDevice Parse(string XML, Uri source, IPAddress Intfce)
        {
            string absoluteUri = source.AbsoluteUri;
            absoluteUri = absoluteUri.Substring(0, absoluteUri.LastIndexOf("/"));
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            UPnPDevice retVal = new UPnPDevice();
            retVal.InterfaceToHost = Intfce;
            retVal.IsRoot = true;
            bool flag2 = false;
            string xML = "";
            try
            {
                reader2.Read();
                reader2.MoveToContent();
            }
            catch
            {
                return null;
            }
            if (reader2.LocalName == "root")
            {
                reader2.Read();
                reader2.MoveToContent();
                while ((reader2.LocalName != "root") && !reader2.EOF)
                {
                    bool flag = false;
                    switch (reader2.LocalName)
                    {
                        case "specVersion":
                            reader2.Read();
                            reader2.MoveToContent();
                            retVal.Arch_Major = int.Parse(reader2.ReadString());
                            reader2.Read();
                            reader2.MoveToContent();
                            retVal.Arch_Minor = int.Parse(reader2.ReadString());
                            reader2.Read();
                            reader2.MoveToContent();
                            break;

                        case "URLBase":
                            retVal.BaseURL = new Uri(reader2.ReadString());
                            break;

                        case "device":
                            if (retVal.BaseURL == null)
                            {
                                flag2 = true;
                                xML = reader2.ReadOuterXml();
                            }
                            else
                            {
                                ParseDevice("<device>\r\n" + reader2.ReadInnerXml() + "</device>", ref retVal);
                            }
                            break;

                        default:
                            reader2.Skip();
                            flag = true;
                            break;
                    }
                    if (!flag)
                    {
                        reader2.Read();
                    }
                }
                if (flag2)
                {
                    if (retVal.BaseURL == null)
                    {
                        retVal.BaseURL = new Uri(absoluteUri);
                    }
                    ParseDevice(xML, ref retVal);
                }
                return retVal;
            }
            return null;
        }

        private static void ParseDevice(string XML, ref UPnPDevice RetVal)
        {
            DText text = new DText();
            TextReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            reader2.Read();
            reader2.MoveToContent();
            if (reader2.LocalName == "device")
            {
                if (reader2.AttributeCount > 0)
                {
                    for (int i = 0; i < reader2.AttributeCount; i++)
                    {
                        reader2.MoveToAttribute(i);
                        if (reader2.LocalName == "MaxVersion")
                        {
                            RetVal.SetVersion(reader2.Value);
                        }
                    }
                    reader2.MoveToContent();
                    reader2.Read();
                }
                else
                {
                    reader2.Read();
                    reader2.MoveToContent();
                }
                while ((reader2.LocalName != "device") && !reader2.EOF)
                {
                    bool flag2;
                    bool flag = false;
                    switch (reader2.LocalName)
                    {
                        case "deviceList":
                            ParseDeviceList(reader2.ReadOuterXml(), ref RetVal);
                            goto Label_04B9;

                        case "URLBase":
                            RetVal.BaseURL = new Uri(reader2.ReadString());
                            goto Label_04B9;

                        case "deviceType":
                            RetVal.DeviceURN = reader2.ReadString();
                            goto Label_04B9;

                        case "friendlyName":
                            RetVal.FriendlyName = reader2.ReadString();
                            goto Label_04B9;

                        case "manufacturer":
                            RetVal.Manufacturer = reader2.ReadString();
                            goto Label_04B9;

                        case "manufacturerURL":
                            RetVal.ManufacturerURL = reader2.ReadString();
                            goto Label_04B9;

                        case "modelDescription":
                            RetVal.ModelDescription = reader2.ReadString();
                            goto Label_04B9;

                        case "modelName":
                            RetVal.ModelName = reader2.ReadString();
                            goto Label_04B9;

                        case "modelNumber":
                            RetVal.ModelNumber = reader2.ReadString();
                            goto Label_04B9;

                        case "modelURL":
                            try
                            {
                                RetVal.ModelURL = new Uri(reader2.ReadString());
                            }
                            catch
                            {
                            }
                            goto Label_04B9;

                        case "serialNumber":
                            RetVal.SerialNumber = reader2.ReadString();
                            goto Label_04B9;

                        case "UDN":
                            RetVal.UniqueDeviceName = reader2.ReadString().Substring(5);
                            goto Label_04B9;

                        case "UPC":
                            RetVal.ProductCode = reader2.ReadString();
                            goto Label_04B9;

                        case "presentationURL":
                            RetVal.HasPresentation = true;
                            RetVal.PresentationURL = reader2.ReadString();
                            goto Label_04B9;

                        case "serviceList":
                            if (!reader2.IsEmptyElement)
                            {
                                break;
                            }
                            goto Label_04B9;

                        case "iconList":
                            flag2 = false;
                            goto Label_045E;

                        default:
                            if (reader2.LocalName != "")
                            {
                                string prefix = reader2.Prefix;
                                string localName = reader2.LocalName;
                                string str4 = reader2.LookupNamespace(prefix);
                                string fieldValue = reader2.ReadInnerXml();
                                RetVal.AddCustomFieldInDescription(localName, fieldValue, str4);
                            }
                            else
                            {
                                reader2.Skip();
                                flag = true;
                            }
                            goto Label_04B9;
                    }
                    reader2.Read();
                    reader2.MoveToContent();
                    while (reader2.LocalName != "serviceList")
                    {
                        if (reader2.LocalName == "service")
                        {
                            UPnPService service = UPnPService.Parse(reader2.ReadOuterXml());
                            RetVal.AddService(service);
                        }
                        if (!reader2.IsStartElement() && (reader2.LocalName != "serviceList"))
                        {
                            reader2.Read();
                            reader2.MoveToContent();
                        }
                    }
                    goto Label_04B9;
                Label_03F2:
                    switch (reader2.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (reader2.LocalName == "icon")
                            {
                                ParseIconXML(RetVal, reader2.ReadOuterXml());
                                if ((reader2.NodeType == XmlNodeType.EndElement) && (reader2.LocalName == "iconList"))
                                {
                                    flag2 = true;
                                }
                            }
                            break;

                        case XmlNodeType.EndElement:
                            if (reader2.LocalName == "iconList")
                            {
                                flag2 = true;
                            }
                            goto Label_045E;
                    }
                Label_045E:
                    if (!flag2 && reader2.Read())
                    {
                        goto Label_03F2;
                    }
                Label_04B9:
                    if (!flag && !reader2.IsStartElement())
                    {
                        reader2.Read();
                    }
                }
            }
        }

        private static void ParseDeviceList(string XML, ref UPnPDevice RetVal)
        {
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            UPnPDevice retVal = null;
            reader2.Read();
            reader2.MoveToContent();
            if (reader2.LocalName == "deviceList")
            {
                reader2.Read();
                reader2.MoveToContent();
                while ((reader2.LocalName != "deviceList") && !reader2.EOF)
                {
                    if (reader2.LocalName == "device")
                    {
                        retVal = new UPnPDevice();
                        retVal.IsRoot = false;
                        retVal.BaseURL = RetVal.BaseURL;
                        ParseDevice("<device>\r\n" + reader2.ReadInnerXml() + "</device>", ref retVal);
                        RetVal.AddDevice(retVal);
                    }
                    if (!reader2.IsStartElement() && (reader2.LocalName != "deviceList"))
                    {
                        reader2.Read();
                        reader2.MoveToContent();
                    }
                }
            }
        }

        private Uri[] ParseEventURL(string URLList)
        {
            DText text = new DText();
            text.ATTRMARK = ">";
            ArrayList list = new ArrayList();
            text[0] = URLList;
            int num = text.DCOUNT();
            for (int i = 1; i <= num; i++)
            {
                string uriString = text[i];
                try
                {
                    uriString = uriString.Substring(uriString.IndexOf("<") + 1);
                    list.Add(new Uri(uriString));
                }
                catch (Exception)
                {
                }
            }
            Uri[] uriArray = new Uri[list.Count];
            for (int j = 0; j < uriArray.Length; j++)
            {
                uriArray[j] = (Uri) list[j];
            }
            return uriArray;
        }

        private static void ParseIconXML(UPnPDevice d, string XML)
        {
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            string uriString = null;
            reader2.Read();
            reader2.MoveToContent();
            if (reader2.LocalName == "icon")
            {
                reader2.Read();
                reader2.MoveToContent();
                while (reader2.LocalName != "icon")
                {
                    if (reader2.LocalName == "url")
                    {
                        uriString = reader2.ReadString();
                    }
                    else
                    {
                        reader2.Skip();
                    }
                    reader2.Read();
                    reader2.MoveToContent();
                }
            }
            if (uriString != null)
            {
                if (!uriString.ToUpper().StartsWith("HTTP://"))
                {
                    if (uriString.StartsWith("/"))
                    {
                        uriString = "http://" + d.BaseURL.Host + ":" + d.BaseURL.Port.ToString() + uriString;
                    }
                    else
                    {
                        uriString = HTTPMessage.UnEscapeString(d.BaseURL.AbsoluteUri + uriString);
                    }
                }
                d.FetchIcon(new Uri(uriString));
            }
        }

        internal HTTPMessage ParseInvokeResponse(string MethodTag, string SOAPACTION, string urn, object RetVal, UPnPArgument[] OutArgs)
        {
            HTTPMessage message = new HTTPMessage();
            MemoryStream w = new MemoryStream(0x1000);
            XmlTextWriter writer = new XmlTextWriter(w, Encoding.UTF8);
            writer.Formatting = Formatting.Indented;
            writer.Indentation = 3;
            writer.WriteStartDocument();
            string ns = "http://schemas.xmlsoap.org/soap/envelope/";
            writer.WriteStartElement("s", "Envelope", ns);
            writer.WriteAttributeString("s", "encodingStyle", ns, "http://schemas.xmlsoap.org/soap/encoding/");
            writer.WriteStartElement("s", "Body", ns);
            if (!SOAPACTION.EndsWith("#QueryStateVariable\""))
            {
                writer.WriteStartElement("u", MethodTag + "Response", urn);
                if (RetVal != null)
                {
                    writer.WriteElementString(((UPnPArgument) RetVal).Name, UPnPService.SerializeObjectInstance(((UPnPArgument) RetVal).DataValue));
                }
                foreach (UPnPArgument argument in OutArgs)
                {
                    writer.WriteElementString(argument.Name, UPnPService.SerializeObjectInstance(argument.DataValue));
                }
            }
            else
            {
                string str2 = "urn:schemas-upnp-org:control-1-0";
                writer.WriteStartElement("u", MethodTag + "Response", str2);
                writer.WriteElementString("return", UPnPStringFormatter.EscapeString(UPnPService.SerializeObjectInstance(RetVal)));
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
            byte[] buffer = new byte[w.Length - 3L];
            w.Seek(3L, SeekOrigin.Begin);
            w.Read(buffer, 0, buffer.Length);
            writer.Close();
            message.StatusCode = 200;
            message.StatusData = "OK";
            message.AddTag("Content-Type", "text/xml ; charset=\"utf-8\"");
            message.AddTag("EXT", "");
            message.AddTag("Server", "Windows NT/5.0, UPnP/1.0, Intel CLR SDK/1.0");
            message.BodyBuffer = buffer;
            return message;
        }

        private HTTPMessage Post(string MethodData, string XML, string SOAPACTION, HTTPSession WebSession)
        {
            return this.Invoke(MethodData.Substring(1), XML, SOAPACTION, WebSession);
        }

        private void ProcessDevice_EVENTCALLBACK(UPnPDevice d)
        {
            foreach (UPnPDevice device in d.EmbeddedDevices)
            {
                this.ProcessDevice_EVENTCALLBACK(device);
            }
            foreach (UPnPService service in d.Services)
            {
                if (this.InterfaceToHost != null)
                {
                    MiniWebServer server = (MiniWebServer) CPWebServerTable[this.InterfaceToHost.ToString()];
                    service.EventCallbackURL = "http://" + this.InterfaceToHost.ToString() + ":" + server.LocalIPEndPoint.Port.ToString() + "/" + d.UniqueDeviceName + "/" + service.ServiceID;
                }
            }
        }

        internal void Removed()
        {
            IPAddress[] localAddresses = CPNetworkInfo.GetLocalAddresses();
            for (int i = 0; i < localAddresses.Length; i++)
            {
                this.CP_RegisteredInterfaces[localAddresses[i].ToString()] = true;
                ((MiniWebServer) CPWebServerTable[localAddresses[i].ToString()]).OnReceive -= new MiniWebServer.HTTPReceiveHandler(this.HandleWebRequest);
                ((MiniWebServer) CPWebServerTable[localAddresses[i].ToString()]).OnHeader -= new MiniWebServer.HTTPReceiveHandler(this.HandleHeaderRequest);
            }
            if (this.OnRemoved != null)
            {
                this.OnRemoved(this);
            }
        }

        public bool RemoveService(IUPnPService service)
        {
            return this.RemoveService(service.GetUPnPService());
        }

        public bool RemoveService(UPnPService service)
        {
            int index = 0;
            int num2 = 0;
            UPnPService[] serviceArray = new UPnPService[this.Services.Length - 1];
            num2 = 0;
            while (num2 < this.Services.Length)
            {
                if (this.Services[num2] != service)
                {
                    serviceArray[index] = this.Services[num2];
                    index++;
                }
                num2++;
            }
            if (num2 == (index + 1))
            {
                this.Services = serviceArray;
                return true;
            }
            return false;
        }

        private bool RenewEvents(string MethodData, string SID, string Timeout)
        {
            bool flag = false;
            for (int i = 0; i < this.Services.Length; i++)
            {
                if (this.Services[i].EventURL == MethodData)
                {
                    if (!this.Services[i]._RenewEvent(SID, Timeout))
                    {
                        throw new Exception(SID + " is not a valid SID");
                    }
                    flag = true;
                    break;
                }
            }
            if (!flag)
            {
                throw new Exception(MethodData + " is not a valid Event location");
            }
            return true;
        }

        private void SendBye(IPAddress local)
        {
            if (!this.NoSSDP)
            {
                SSDP ssdp = (SSDP) this.SSDPServerTable[local.ToString()];
                if (ssdp != null)
                {
                    HTTPMessage[] messageArray = this.BuildByePacket();
                    for (int i = 0; i < messageArray.Length; i++)
                    {
                        ssdp.BroadcastData(messageArray[i]);
                    }
                }
            }
        }

        private void SendNotify()
        {
            if (this.NetInfo == null)
            {
                if (this.ManualIPEndPoint != null)
                {
                    this.SendNotify(this.ManualIPEndPoint.Address);
                }
            }
            else
            {
                foreach (IPAddress address in this.NetInfo.GetLocalAddresses())
                {
                    this.SendNotify(address);
                }
            }
        }

        private void SendNotify(IPAddress local)
        {
            if (!this.NoSSDP)
            {
                HTTPMessage[] messageArray = this.BuildNotifyPacket(local);
                for (int i = 0; i < messageArray.Length; i++)
                {
                    ((SSDP) this.SSDPServerTable[local.ToString()]).BroadcastData(messageArray[i]);
                }
            }
        }

        private void SetInterfaceToHost(UPnPDevice d)
        {
            d.InterfaceToHost = this.InterfaceToHost;
            foreach (UPnPDevice device in d.EmbeddedDevices)
            {
                this.SetInterfaceToHost(device);
            }
        }

        private void SetVersion(string v)
        {
            DText text = new DText();
            if (v.IndexOf("-") == -1)
            {
                text.ATTRMARK = ".";
            }
            else
            {
                text.ATTRMARK = "-";
            }
            text[0] = v;
            string s = text[1];
            string str2 = text[2];
            if (s == "")
            {
                this.Major = 0;
            }
            else
            {
                this.Major = int.Parse(s);
            }
            if (str2 == "")
            {
                this.Minor = 0;
            }
            else
            {
                this.Minor = int.Parse(str2);
            }
        }

        private void SniffSessionSink(MiniWebServer Sender, HTTPSession s)
        {
            if (this.OnSniff != null)
            {
                s.OnSniff += new HTTPSession.SniffHandler(this.SniffSessionSink2);
            }
            else if (this.OnSniffPacket == null)
            {
                Sender.OnSession -= new MiniWebServer.NewSessionHandler(this.SniffSessionSink);
            }
            if (this.OnSniffPacket != null)
            {
                s.OnSniffPacket += new HTTPSession.ReceiveHandler(this.SniffSessionSink3);
            }
        }

        private void SniffSessionSink2(byte[] raw, int offset, int length)
        {
            if (this.OnSniff != null)
            {
                this.OnSniff(raw, offset, length);
            }
        }

        private void SniffSessionSink3(HTTPSession sender, HTTPMessage msg)
        {
            if (this.OnSniffPacket != null)
            {
                this.OnSniffPacket((HTTPMessage) msg.Clone());
            }
        }

        public void StartDevice()
        {
            if (this.ControlPointOnly)
            {
                throw new Exception("Cannot Start/Stop a Device instantiated by a Control Point");
            }
            if (!this.IsRoot)
            {
                throw new Exception("Cannot Start/Stop a Non-Root Device directly");
            }
            this.NetInfo = new NetworkInfo(new NetworkInfo.InterfaceHandler(this.NewDeviceInterface));
            this.NetInfo.OnInterfaceDisabled += new NetworkInfo.InterfaceHandler(this.DisabledInterface);
        }

        public void StartDevice(int PortNumber)
        {
            this.UseThisPort = PortNumber;
            this.StartDevice();
        }

        public void StartDevice(IPEndPoint Manual)
        {
            if (this.ControlPointOnly)
            {
                throw new Exception("Cannot Start/Stop a Device instantiated by a Control Point");
            }
            if (!this.IsRoot)
            {
                throw new Exception("Cannot Start/Stop a Non-Root Device directly");
            }
            this.ManualIPEndPoint = Manual;
            this.ManualNewDeviceInterface(Manual.Address, Manual.Port);
            this.Advertise();
        }

        public void StopDevice()
        {
            if (this.ControlPointOnly)
            {
                throw new Exception("Cannot Start/Stop a Device instantiated by a Control Point");
            }
            if (!this.IsRoot)
            {
                throw new Exception("Cannot Start/Stop a Non-Root Device Directly");
            }
            if (this.NetInfo != null)
            {
                IPAddress[] localAddresses = this.NetInfo.GetLocalAddresses();
                for (int j = 0; j < localAddresses.Length; j++)
                {
                    this.SendBye(localAddresses[j]);
                }
            }
            else if (this.ManualIPEndPoint != null)
            {
                this.SendBye(this.ManualIPEndPoint.Address);
            }
            for (int i = 0; i < this.Services.Length; i++)
            {
                this.Services[i].Dispose();
            }
            this.DisposeAllServers();
        }

        private HTTPMessage SubscribeEvents(ref string SID, string MethodData, string CallbackURL, string Timeout)
        {
            bool flag = false;
            HTTPMessage message = new HTTPMessage();
            for (int i = 0; i < this.Services.Length; i++)
            {
                if (this.Services[i].EventURL == MethodData)
                {
                    message = this.Services[i]._SubscribeEvent(out SID, CallbackURL, Timeout);
                    flag = true;
                    break;
                }
            }
            if (!flag)
            {
                throw new Exception(MethodData + " is not a valid Event location");
            }
            return message;
        }

        public override string ToString()
        {
            return this.FriendlyName;
        }

        public void UpdateDevice(Uri LocationUri, IPAddress HostInterface)
        {
            lock (this.UpdateTable)
            {
                UPnPDeviceFactory factory = new UPnPDeviceFactory(LocationUri, 250, new UPnPDeviceFactory.UPnPDeviceHandler(this.UpdateDeviceSink), null);
                this.UpdateTable[factory] = factory;
            }
        }

        private void UpdateDeviceSink(UPnPDeviceFactory sender, UPnPDevice d, Uri LocationUri)
        {
            lock (this.UpdateTable)
            {
                this.UpdateTable.Remove(sender);
            }
            this.BaseURL = d.BaseURL;
            this.InterfaceToHost = d.InterfaceToHost;
            foreach (UPnPDevice device in this.EmbeddedDevices)
            {
                this.SetInterfaceToHost(device);
            }
            this.UpdateDeviceSink2(d);
        }

        private void UpdateDeviceSink2(UPnPDevice d)
        {
            foreach (UPnPService service in d.Services)
            {
                UPnPService service2 = this.GetService(service.ServiceID);
                if (service2 != null)
                {
                    service2._Update(service);
                }
            }
            foreach (UPnPDevice device in d.EmbeddedDevices)
            {
                this.UpdateDeviceSink2(device);
            }
        }

        public string ArchitectureVersion
        {
            get
            {
                return (this.Arch_Major.ToString() + "." + this.Arch_Minor.ToString());
            }
            set
            {
                DText text = new DText();
                text.ATTRMARK = ".";
                text[0] = value;
                this.Arch_Major = int.Parse(text[1]);
                this.Arch_Minor = int.Parse(text[2]);
                foreach (UPnPDevice device in this.EmbeddedDevices)
                {
                    device.ArchitectureVersion = value;
                }
            }
        }

        public string BootID
        {
            set
            {
                this._BootID = value;
            }
        }

        public string DeviceURN
        {
            get
            {
                return this.__DeviceURN;
            }
            set
            {
                this.__DeviceURN = value;
                DText text = new DText();
                text.ATTRMARK = ":";
                text[0] = value;
                if (((int.Parse(this.Version) > 0) && (this.Version != "1")) && (text[text.DCOUNT()] == "1"))
                {
                    text[text.DCOUNT()] = this.Version;
                    this.__DeviceURN = text[0];
                }
                else
                {
                    this.SetVersion(text[text.DCOUNT()]);
                }
            }
        }

        public string DeviceURN_Prefix
        {
            get
            {
                DText text = new DText();
                text.ATTRMARK = ":";
                text[0] = this.__DeviceURN;
                int length = text[text.DCOUNT()].Length;
                return this.__DeviceURN.Substring(0, this.__DeviceURN.Length - length);
            }
        }

        public Image Icon
        {
            get
            {
                return this._icon;
            }
            set
            {
                if (!this.ControlPointOnly)
                {
                    this._icon = value;
                }
            }
        }

        public Image Icon2
        {
            get
            {
                return this._icon2;
            }
            set
            {
                if (!this.ControlPointOnly)
                {
                    this._icon2 = value;
                }
            }
        }

        public IPEndPoint[] LocalIPEndPoints
        {
            get
            {
                ArrayList list = new ArrayList();
                foreach (IPAddress address in this.NetInfo.GetLocalAddresses())
                {
                    MiniWebServer server = (MiniWebServer) this.WebServerTable[address.ToString()];
                    if (server != null)
                    {
                        list.Add(server.LocalIPEndPoint);
                    }
                }
                return (IPEndPoint[]) list.ToArray(typeof(IPEndPoint));
            }
        }

        public UPnPDevice ParentDevice
        {
            get
            {
                return this.parent;
            }
        }

        public string PresentationURL
        {
            get
            {
                return this._PresentationURL;
            }
            set
            {
                this._PresentationURL = value;
            }
        }

        public IPEndPoint RemoteEndPoint
        {
            get
            {
                string host = this.BaseURL.Host;
                if (this.BaseURL.HostNameType == UriHostNameType.Dns)
                {
                    host = Dns.GetHostEntry(host).AddressList[0].ToString();
                }
                return new IPEndPoint(IPAddress.Parse(host), this.BaseURL.Port);
            }
        }

        public bool Root
        {
            get
            {
                return this.IsRoot;
            }
        }

        public string StandardDeviceType
        {
            get
            {
                return "";
            }
            set
            {
                this.DeviceURN = "urn:schemas-upnp-org:device:" + value + ":" + this.Version;
            }
        }

        public string Version
        {
            get
            {
                if (this.Minor == 0)
                {
                    return this.Major.ToString();
                }
                return (this.Major.ToString() + "-" + this.Minor.ToString());
            }
        }

        public delegate void DeviceUpdateHandler(UPnPDevice device);

        public class FrindlyNameComparer : IComparer
        {
            public int Compare(object o1, object o2)
            {
                UPnPDevice device = (UPnPDevice) o1;
                UPnPDevice device2 = (UPnPDevice) o2;
                return string.Compare(device.FriendlyName, device2.FriendlyName);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct InvokerInfoStruct
        {
            public HTTPSession WebSession;
            public UPnPArgument[] OutArgs;
            public UPnPArgument RetArg;
            public string MethodName;
            public string SOAPAction;
        }

        public delegate void OnRemovedHandler(UPnPDevice sender);

        internal delegate void SniffHandler(byte[] Raw, int offset, int length);

        internal delegate void SniffPacketHandler(HTTPMessage Packet);

        public delegate void VirtualDirectoryHandler(UPnPDevice sender, HTTPMessage msg, HTTPSession WebSession, string VirtualDir);
    }
}

