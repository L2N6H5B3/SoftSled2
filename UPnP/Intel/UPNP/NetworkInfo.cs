namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;

    public sealed class NetworkInfo
    {
        private ArrayList AddressTable;
        private string HostName;
        private LifeTimeMonitor InterfacePoller;
        public static int NetworkPollSeconds = 3;
        private WeakEvent OnInterfaceDisabledEvent;
        private WeakEvent OnNewInterfaceEvent;

        public event InterfaceHandler OnInterfaceDisabled
        {
            add
            {
                this.OnInterfaceDisabledEvent.Register(value);
            }
            remove
            {
                this.OnInterfaceDisabledEvent.UnRegister(value);
            }
        }

        public event InterfaceHandler OnNewInterface
        {
            add
            {
                this.OnNewInterfaceEvent.Register(value);
            }
            remove
            {
                this.OnNewInterfaceEvent.UnRegister(value);
            }
        }

        public NetworkInfo() : this(null)
        {
        }

        public NetworkInfo(InterfaceHandler onNewInterfaceSink)
        {
            this.OnNewInterfaceEvent = new WeakEvent();
            this.OnInterfaceDisabledEvent = new WeakEvent();
            this.InterfacePoller = new LifeTimeMonitor();
            this.AddressTable = new ArrayList();
            InstanceTracker.Add(this);
            this.InterfacePoller.OnExpired += new LifeTimeMonitor.LifeTimeHandler(this.PollInterface);
            this.HostName = Dns.GetHostName();
            IPHostEntry hostByName = Dns.GetHostByName(this.HostName);
            this.AddressTable = new ArrayList(hostByName.AddressList);
            if (!this.AddressTable.Contains(IPAddress.Loopback))
            {
                this.AddressTable.Add(IPAddress.Loopback);
            }
            if (onNewInterfaceSink != null)
            {
                this.OnNewInterface += onNewInterfaceSink;
                foreach (IPAddress address in this.AddressTable)
                {
                    this.OnNewInterfaceEvent.Fire(this, address);
                }
            }
            this.InterfacePoller.Add(this, NetworkPollSeconds);
        }

        public static int GetFreePort(int LowRange, int UpperRange, IPAddress OnThisIP)
        {
            int num;
            Random random = new Random();
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            while (true)
            {
                num = random.Next(LowRange, UpperRange);
                IPEndPoint localEP = new IPEndPoint(OnThisIP, num);
                try
                {
                    socket.Bind(localEP);
                }
                catch (Exception)
                {
                }
                break;
            }
            socket.Close();
            return num;
        }

        public IPAddress[] GetLocalAddresses()
        {
            return (IPAddress[]) this.AddressTable.ToArray(typeof(IPAddress));
        }

        private void PollInterface(LifeTimeMonitor sender, object obj)
        {
            try
            {
                ArrayList list = new ArrayList(Dns.GetHostByName(this.HostName).AddressList);
                ArrayList addressTable = this.AddressTable;
                this.AddressTable = list;
                if (!this.AddressTable.Contains(IPAddress.Loopback))
                {
                    this.AddressTable.Add(IPAddress.Loopback);
                }
                foreach (IPAddress address in list)
                {
                    if (!addressTable.Contains(address))
                    {
                        this.OnNewInterfaceEvent.Fire(this, address);
                    }
                }
                foreach (IPAddress address2 in addressTable)
                {
                    if (!list.Contains(address2))
                    {
                        this.OnInterfaceDisabledEvent.Fire(this, address2);
                    }
                }
            }
            catch (Exception exception)
            {
                EventLogger.Log(exception);
            }
            this.InterfacePoller.Add(this, NetworkPollSeconds);
        }

        public delegate void InterfaceHandler(NetworkInfo sender, IPAddress address);
    }
}

