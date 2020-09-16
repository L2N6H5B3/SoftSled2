namespace Intel.UPNP
{
    using System;
    using System.Collections;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public sealed class UPnPSmartControlPoint
    {
        internal static UPnPInternalSmartControlPoint iSCP = new UPnPInternalSmartControlPoint();
        private double[] MinimumVersion;
        private bool MultiFilter;
        private string[] PartialMatchFilters;

        public event DeviceHandler OnAddedDevice;

        public event ServiceHandler OnAddedService;

        public event DeviceHandler OnRemovedDevice;

        public event ServiceHandler OnRemovedService;

        public UPnPSmartControlPoint() : this(null)
        {
        }

        public UPnPSmartControlPoint(DeviceHandler OnAddedDeviceSink) : this(OnAddedDeviceSink, "upnp:rootdevice")
        {
        }

        public UPnPSmartControlPoint(DeviceHandler OnAddedDeviceSink, string DevicePartialMatchFilter) : this(OnAddedDeviceSink, null, DevicePartialMatchFilter)
        {
        }

        public UPnPSmartControlPoint(DeviceHandler OnAddedDeviceSink, ServiceHandler OnAddedServiceSink, string DevicePartialMatchFilter) : this(OnAddedDeviceSink, OnAddedServiceSink, new string[] { DevicePartialMatchFilter })
        {
            this.MultiFilter = false;
        }

        public UPnPSmartControlPoint(DeviceHandler OnAddedDeviceSink, ServiceHandler OnAddedServiceSink, string[] Filters)
        {
            this.MultiFilter = true;
            this.PartialMatchFilters = new string[] { "upnp:rootdevice" };
            this.MinimumVersion = new double[] { 1.0 };
            this.PartialMatchFilters = new string[Filters.Length];
            this.MinimumVersion = new double[Filters.Length];
            for (int i = 0; i < this.PartialMatchFilters.Length; i++)
            {
                if ((Filters[i].Length > 15) && (Filters[i].Length > UPnPStringFormatter.GetURNPrefix(Filters[i]).Length))
                {
                    this.PartialMatchFilters[i] = UPnPStringFormatter.GetURNPrefix(Filters[i]);
                    try
                    {
                        this.MinimumVersion[i] = double.Parse(Filters[i].Substring(this.PartialMatchFilters[i].Length), new CultureInfo("en-US").NumberFormat);
                    }
                    catch
                    {
                        this.MinimumVersion[i] = 1.0;
                    }
                }
                else
                {
                    this.PartialMatchFilters[i] = Filters[i];
                    this.MinimumVersion[i] = 1.0;
                }
            }
            if (OnAddedDeviceSink != null)
            {
                this.OnAddedDevice = (DeviceHandler) Delegate.Combine(this.OnAddedDevice, OnAddedDeviceSink);
            }
            if (OnAddedServiceSink != null)
            {
                this.OnAddedService = (ServiceHandler) Delegate.Combine(this.OnAddedService, OnAddedServiceSink);
            }
            iSCP.OnAddedDevice += new UPnPInternalSmartControlPoint.DeviceHandler(this.HandleAddedDevice);
            iSCP.OnDeviceExpired += new UPnPInternalSmartControlPoint.DeviceHandler(this.HandleExpiredDevice);
            iSCP.OnRemovedDevice += new UPnPInternalSmartControlPoint.DeviceHandler(this.HandleRemovedDevice);
            iSCP.OnUpdatedDevice += new UPnPInternalSmartControlPoint.DeviceHandler(this.HandleUpdatedDevice);
            IEnumerator enumerator = iSCP.GetCurrentDevices().GetEnumerator();
            if (((OnAddedDeviceSink != null) || (OnAddedServiceSink != null)) && (enumerator != null))
            {
                while (enumerator.MoveNext())
                {
                    this.HandleAddedDevice(null, (UPnPDevice) enumerator.Current);
                }
            }
        }

        private bool CheckDeviceAgainstFilter(string filter, double Version, UPnPDevice device, out object[] MatchingObject)
        {
            ArrayList list = new ArrayList();
            if (device == null)
            {
                MatchingObject = new object[0];
                return false;
            }
            if ((filter == "upnp:rootdevice") && device.Root)
            {
                MatchingObject = new object[] { device };
                return true;
            }
            if (!device.Root)
            {
                foreach (UPnPDevice device2 in device.EmbeddedDevices)
                {
                    object[] objArray;
                    if (this.CheckDeviceAgainstFilter(filter, Version, device2, out objArray))
                    {
                        foreach (object obj2 in objArray)
                        {
                            list.Add(obj2);
                        }
                    }
                }
            }
            else
            {
                foreach (UPnPDevice device3 in device.EmbeddedDevices)
                {
                    object[] objArray2;
                    this.CheckDeviceAgainstFilter(filter, Version, device3, out objArray2);
                    foreach (object obj3 in objArray2)
                    {
                        list.Add(obj3);
                    }
                }
            }
            if ((device.UniqueDeviceName == filter) || ((device.DeviceURN_Prefix == filter) && (double.Parse(device.Version) >= Version)))
            {
                list.Add(device);
            }
            else
            {
                for (int i = 0; i < device.Services.Length; i++)
                {
                    if ((device.Services[i].ServiceID == filter) || ((device.Services[i].ServiceURN_Prefix == filter) && (double.Parse(device.Services[i].Version) >= Version)))
                    {
                        list.Add(device.Services[i]);
                    }
                }
            }
            if (list.Count == 0)
            {
                MatchingObject = new object[0];
                return false;
            }
            MatchingObject = (object[]) list.ToArray(typeof(object));
            return true;
        }

        public void ForceDisposeDevice(UPnPDevice root)
        {
            while (root.ParentDevice != null)
            {
                root = root.ParentDevice;
            }
            iSCP.SSDPNotifySink(null, null, null, false, root.UniqueDeviceName, "upnp:rootdevice", 0, null);
        }

        private void HandleAddedDevice(UPnPInternalSmartControlPoint sender, UPnPDevice device)
        {
            if ((this.OnAddedDevice != null) || (this.OnAddedService != null))
            {
                ArrayList list = new ArrayList();
                ArrayList list2 = new ArrayList();
                Hashtable hashtable = new Hashtable();
                bool flag = true;
                for (int i = 0; i < this.PartialMatchFilters.Length; i++)
                {
                    object[] objArray;
                    string filter = this.PartialMatchFilters[i];
                    double version = this.MinimumVersion[i];
                    if (!this.CheckDeviceAgainstFilter(filter, version, device, out objArray))
                    {
                        flag = false;
                        break;
                    }
                    foreach (object obj2 in objArray)
                    {
                        if (obj2.GetType().FullName == "Intel.UPNP.UPnPDevice")
                        {
                            list.Add((UPnPDevice) obj2);
                            if ((this.PartialMatchFilters.Length == 1) && (this.OnAddedDevice != null))
                            {
                                this.OnAddedDevice(this, (UPnPDevice) obj2);
                            }
                        }
                        else
                        {
                            list2.Add((UPnPService) obj2);
                            if (this.PartialMatchFilters.Length == 1)
                            {
                                if (!this.MultiFilter)
                                {
                                    if (this.OnAddedService != null)
                                    {
                                        this.OnAddedService(this, (UPnPService) obj2);
                                    }
                                }
                                else if (this.OnAddedDevice != null)
                                {
                                    this.OnAddedDevice(this, ((UPnPService) obj2).ParentDevice);
                                }
                            }
                        }
                    }
                }
                if (flag)
                {
                    if (this.PartialMatchFilters.Length == 1)
                    {
                        return;
                    }
                    foreach (UPnPDevice device2 in list)
                    {
                        bool flag2 = true;
                        foreach (string str2 in this.PartialMatchFilters)
                        {
                            if ((device2.GetDevices(str2).Length == 0) && (device2.GetServices(str2).Length == 0))
                            {
                                flag2 = false;
                                goto Label_01B4;
                            }
                        }
                    Label_01B4:
                        if (flag2)
                        {
                            hashtable[device2] = device2;
                        }
                    }
                    foreach (UPnPService service in list2)
                    {
                        bool flag3 = true;
                        foreach (string str3 in this.PartialMatchFilters)
                        {
                            if (service.ParentDevice.GetServices(str3).Length == 0)
                            {
                                flag3 = false;
                                break;
                            }
                        }
                        if (flag3 && !hashtable.ContainsKey(service.ParentDevice))
                        {
                            hashtable[service.ParentDevice] = service.ParentDevice;
                        }
                    }
                }
                IDictionaryEnumerator enumerator = hashtable.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (this.OnAddedDevice != null)
                    {
                        this.OnAddedDevice(this, (UPnPDevice) enumerator.Value);
                    }
                }
            }
        }

        private void HandleExpiredDevice(UPnPInternalSmartControlPoint sender, UPnPDevice device)
        {
            this.HandleRemovedDevice(sender, device);
        }

        private void HandleRemovedDevice(UPnPInternalSmartControlPoint sender, UPnPDevice device)
        {
            if ((this.OnRemovedDevice != null) || (this.OnRemovedService != null))
            {
                ArrayList list = new ArrayList();
                ArrayList list2 = new ArrayList();
                Hashtable hashtable = new Hashtable();
                bool flag = true;
                for (int i = 0; i < this.PartialMatchFilters.Length; i++)
                {
                    object[] objArray;
                    string filter = this.PartialMatchFilters[i];
                    double version = this.MinimumVersion[i];
                    if (!this.CheckDeviceAgainstFilter(filter, version, device, out objArray))
                    {
                        flag = false;
                        break;
                    }
                    foreach (object obj2 in objArray)
                    {
                        if (obj2.GetType().FullName == "Intel.UPNP.UPnPDevice")
                        {
                            list.Add((UPnPDevice) obj2);
                            if ((this.PartialMatchFilters.Length == 1) && (this.OnRemovedDevice != null))
                            {
                                this.OnRemovedDevice(this, (UPnPDevice) obj2);
                            }
                        }
                        else
                        {
                            list2.Add((UPnPService) obj2);
                            if ((this.PartialMatchFilters.Length == 1) && (this.OnRemovedDevice != null))
                            {
                                this.OnRemovedDevice(this, (UPnPDevice) obj2);
                            }
                        }
                    }
                }
                if (flag)
                {
                    if (this.PartialMatchFilters.Length == 1)
                    {
                        if (this.OnRemovedService != null)
                        {
                            foreach (UPnPService service in list2)
                            {
                                this.OnRemovedService(this, service);
                            }
                        }
                        return;
                    }
                    foreach (UPnPDevice device2 in list)
                    {
                        bool flag2 = true;
                        foreach (string str2 in this.PartialMatchFilters)
                        {
                            if ((device2.GetDevices(str2).Length == 0) && (device2.GetServices(str2).Length == 0))
                            {
                                flag2 = false;
                                goto Label_01D8;
                            }
                        }
                    Label_01D8:
                        if (flag2)
                        {
                            hashtable[device2] = device2;
                        }
                    }
                    foreach (UPnPService service2 in list2)
                    {
                        bool flag3 = true;
                        foreach (string str3 in this.PartialMatchFilters)
                        {
                            if (service2.ParentDevice.GetServices(str3).Length == 0)
                            {
                                flag3 = false;
                                break;
                            }
                        }
                        if (flag3 && !hashtable.ContainsKey(service2.ParentDevice))
                        {
                            hashtable[service2.ParentDevice] = service2.ParentDevice;
                        }
                    }
                }
                IDictionaryEnumerator enumerator = hashtable.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (this.OnRemovedDevice != null)
                    {
                        this.OnRemovedDevice(this, (UPnPDevice) enumerator.Value);
                    }
                }
            }
        }

        private void HandleUpdatedDevice(UPnPInternalSmartControlPoint sender, UPnPDevice device)
        {
        }

        public void Rescan()
        {
            iSCP.Rescan();
        }

        public ArrayList Devices
        {
            get
            {
                ArrayList list = new ArrayList();
                ArrayList list2 = new ArrayList();
                Hashtable hashtable = new Hashtable();
                bool flag = false;
                UPnPDevice[] currentDevices = iSCP.GetCurrentDevices();
                for (int i = 0; i < currentDevices.Length; i++)
                {
                    flag = true;
                    for (int j = 0; j < this.PartialMatchFilters.Length; j++)
                    {
                        object[] objArray;
                        string filter = this.PartialMatchFilters[j];
                        double version = this.MinimumVersion[j];
                        if (!this.CheckDeviceAgainstFilter(filter, version, currentDevices[i], out objArray))
                        {
                            flag = false;
                            break;
                        }
                        foreach (object obj2 in objArray)
                        {
                            if (obj2.GetType().FullName == "Intel.UPNP.UPnPDevice")
                            {
                                list.Add((UPnPDevice) obj2);
                            }
                            else
                            {
                                list2.Add((UPnPService) obj2);
                            }
                        }
                    }
                    if (flag)
                    {
                        foreach (UPnPDevice device in list)
                        {
                            bool flag2 = true;
                            foreach (string str2 in this.PartialMatchFilters)
                            {
                                if ((device.GetDevices(str2).Length == 0) && (device.GetServices(str2).Length == 0))
                                {
                                    flag2 = false;
                                    break;
                                }
                            }
                            if (flag2)
                            {
                                hashtable[device] = device;
                            }
                        }
                    }
                }
                ArrayList list3 = new ArrayList();
                IDictionaryEnumerator enumerator = hashtable.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    list3.Add(enumerator.Value);
                }
                return list3;
            }
        }

        public delegate void DeviceHandler(UPnPSmartControlPoint sender, UPnPDevice device);

        public delegate void ServiceHandler(UPnPSmartControlPoint sender, UPnPService service);
    }
}

