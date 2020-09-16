namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;

    public sealed class UPnPDeviceFactory
    {
        private object CBLock;
        private Hashtable CreateTable;
        private string DUrl;
        private LifeTimeMonitor.LifeTimeHandler ExpiredHandler;
        private LifeTimeMonitor Lifetime;
        private int MaxAge;
        private HTTPRequest r;
        private int ServiceNum;
        private UPnPDevice TempDevice;

        public event UPnPDeviceHandler OnDevice;

        public event UPnPDeviceFailedHandler OnFailed;

        public UPnPDeviceFactory()
        {
            this.Lifetime = new LifeTimeMonitor();
            InstanceTracker.Add(this);
            this.CreateTable = Hashtable.Synchronized(new Hashtable());
        }

        public UPnPDeviceFactory(Uri DescLocation, int MaxSeconds, UPnPDeviceHandler deviceCB, UPnPDeviceFailedHandler failedCB)
        {
            this.Lifetime = new LifeTimeMonitor();
            InstanceTracker.Add(this);
            this.r = new HTTPRequest();
            this.r.OnResponse += new HTTPRequest.RequestHandler(this.HandleRequest);
            this.CBLock = new object();
            this.OnDevice = (UPnPDeviceHandler) Delegate.Combine(this.OnDevice, deviceCB);
            this.OnFailed = (UPnPDeviceFailedHandler) Delegate.Combine(this.OnFailed, failedCB);
            this.DUrl = DescLocation.ToString();
            this.MaxAge = MaxSeconds;
            this.ExpiredHandler = new LifeTimeMonitor.LifeTimeHandler(this.HandleTimeout);
            this.Lifetime.OnExpired += this.ExpiredHandler;
            this.Lifetime.Add(DateTime.Now, 30);
            this.r.PipelineRequest(DescLocation, null);
        }

        public void CreateDevice(Uri DescLocation, int MaxSeconds)
        {
            lock (this.CreateTable)
            {
                UPnPDeviceFactory factory = new UPnPDeviceFactory(DescLocation, MaxSeconds, new UPnPDeviceHandler(this.HandleFactory), new UPnPDeviceFailedHandler(this.FactoryFailedSink));
                this.CreateTable[factory] = factory;
            }
        }

        private void FactoryFailedSink(UPnPDeviceFactory sender, Uri URL, Exception e)
        {
            lock (this.CreateTable)
            {
                this.CreateTable.Remove(sender);
                sender.Shutdown();
            }
        }

        private int FetchServiceCount(UPnPDevice device)
        {
            int length = 0;
            length = device.Services.Length;
            if (device.EmbeddedDevices.Length > 0)
            {
                for (int i = 0; i < device.EmbeddedDevices.Length; i++)
                {
                    length += this.FetchServiceCount(device.EmbeddedDevices[i]);
                }
            }
            return length;
        }

        private void FetchServiceDocuments(UPnPDevice device)
        {
            for (int i = 0; i < device.Services.Length; i++)
            {
                HTTPMessage message = new HTTPMessage();
                Uri resource = new Uri(device.Services[i].SCPDURL);
                message.Directive = "GET";
                message.DirectiveObj = HTTPMessage.UnEscapeString(resource.PathAndQuery);
                message.AddTag("Host", resource.Host + ":" + resource.Port.ToString());
                this.r.PipelineRequest(resource, device.Services[i]);
            }
            if (device.EmbeddedDevices.Length > 0)
            {
                for (int j = 0; j < device.EmbeddedDevices.Length; j++)
                {
                    this.FetchServiceDocuments(device.EmbeddedDevices[j]);
                }
            }
        }

        private void HandleFactory(UPnPDeviceFactory Factory, UPnPDevice device, Uri URL)
        {
            lock (this.CreateTable)
            {
                this.CreateTable.Remove(Factory);
            }
            Factory.Shutdown();
            if (this.OnDevice != null)
            {
                this.OnDevice(this, device, URL);
            }
        }

        private void HandleRequest(HTTPRequest sender, HTTPMessage msg, object Tag)
        {
            if (Tag != null)
            {
                this.HandleService(sender, msg, Tag);
            }
            else if (msg != null)
            {
                if (msg.StatusCode == 200)
                {
                    try
                    {
                        this.TempDevice = UPnPDevice.Parse(msg.StringBuffer, new Uri(this.DUrl), sender.Source.Address);
                    }
                    catch (Exception)
                    {
                        EventLogger.Log(this, EventLogEntryType.Error, "Invalid Device Description XML @" + this.DUrl);
                        return;
                    }
                    if (this.TempDevice == null)
                    {
                        EventLogger.Log(this, EventLogEntryType.Error, "Invalid Device Description XML @" + this.DUrl);
                    }
                    else
                    {
                        this.TempDevice.LocationURL = this.DUrl;
                        this.TempDevice.ExpirationTimeout = this.MaxAge;
                        if (this.TempDevice != null)
                        {
                            this.ServiceNum = this.FetchServiceCount(this.TempDevice);
                            if ((this.ServiceNum == 0) && (this.OnDevice != null))
                            {
                                this.Lifetime.Remove(this);
                                this.OnDevice(this, this.TempDevice, new Uri(this.DUrl));
                                this.TempDevice = null;
                            }
                            else
                            {
                                this.FetchServiceDocuments(this.TempDevice);
                            }
                        }
                    }
                }
                else if (this.OnFailed != null)
                {
                    this.OnFailed(this, new Uri(this.DUrl), new Exception("Device returned HTTP fault: " + msg.StatusData));
                }
            }
            else if (this.OnFailed != null)
            {
                this.OnFailed(this, new Uri(this.DUrl), new Exception("Could not connect to target"));
            }
        }

        private void HandleService(HTTPRequest sender, HTTPMessage msg, object Tag)
        {
            bool flag = false;
            if (msg == null)
            {
                EventLogger.Log(this, EventLogEntryType.Error, "Could not connect to device to fetch SCPD: " + ((UPnPService) Tag).SCPDURL);
                sender.Dispose();
                this.TempDevice = null;
                this.OnFailed(this, new Uri(this.DUrl), new Exception("HTTP Connection Refused/Failed"));
            }
            else
            {
                bool flag2 = false;
                lock (this.CBLock)
                {
                    if (msg.StatusCode == 200)
                    {
                        try
                        {
                            ((UPnPService) Tag).ParseSCPD(msg.StringBuffer);
                        }
                        catch (Exception exception)
                        {
                            EventLogger.Log(this, EventLogEntryType.Error, "Invalid SCPD XML on device:\r\n   Friendly: " + this.TempDevice.FriendlyName + "\r\n   Service: " + ((UPnPService) Tag).ServiceURN + "\r\n   @" + this.TempDevice.LocationURL);
                            EventLogger.Log(exception);
                            return;
                        }
                        this.ServiceNum--;
                        if ((this.ServiceNum == 0) && (this.OnDevice != null))
                        {
                            flag2 = true;
                        }
                    }
                    else
                    {
                        EventLogger.Log(this, EventLogEntryType.Error, "Device returned Error Code: " + msg.StatusCode.ToString() + " while fetching SCPD: " + ((UPnPService) Tag).SCPDURL);
                        sender.Dispose();
                        flag = true;
                    }
                }
                if (flag2)
                {
                    this.TempDevice.descXmlLocation = new Uri(this.DUrl);
                    this.OnDevice(this, this.TempDevice, new Uri(this.DUrl));
                    this.TempDevice = null;
                }
                if (flag && (this.OnFailed != null))
                {
                    this.TempDevice = null;
                    this.OnFailed(this, new Uri(this.DUrl), new Exception("HTTP[" + msg.StatusCode.ToString() + "] Error"));
                }
            }
        }

        private void HandleTimeout(LifeTimeMonitor sender, object obj)
        {
            if (this.OnFailed != null)
            {
                this.OnFailed(this, new Uri(this.DUrl), new Exception("Timeout occured trying to fetch description documents"));
            }
        }

        public void Shutdown()
        {
            this.r.Dispose();
        }

        public delegate void UPnPDeviceFailedHandler(UPnPDeviceFactory sender, Uri URL, Exception e);

        public delegate void UPnPDeviceHandler(UPnPDeviceFactory sender, UPnPDevice device, Uri URL);
    }
}

