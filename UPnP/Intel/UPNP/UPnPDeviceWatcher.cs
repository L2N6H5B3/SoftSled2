namespace Intel.UPNP
{
    using System;
    using System.Runtime.CompilerServices;

    public class UPnPDeviceWatcher
    {
        private WeakReference W;

        public event SniffHandler OnSniff;

        public event SniffPacketHandler OnSniffPacket;

        public UPnPDeviceWatcher(UPnPDevice d)
        {
            this.W = new WeakReference(d);
            d.OnSniff += new UPnPDevice.SniffHandler(this.SniffSink);
            d.OnSniffPacket += new UPnPDevice.SniffPacketHandler(this.SniffPacketSink);
        }

        private void SniffPacketSink(HTTPMessage Packet)
        {
            if (this.OnSniffPacket != null)
            {
                this.OnSniffPacket(Packet);
            }
        }

        private void SniffSink(byte[] raw, int offset, int length)
        {
            if (this.OnSniff != null)
            {
                this.OnSniff(raw, offset, length);
            }
        }

        public delegate void SniffHandler(byte[] raw, int offset, int length);

        public delegate void SniffPacketHandler(HTTPMessage Packet);
    }
}

