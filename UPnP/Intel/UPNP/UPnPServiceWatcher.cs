namespace Intel.UPNP
{
    using System;
    using System.Runtime.CompilerServices;

    public class UPnPServiceWatcher
    {
        private UPnPService _S;

        public event SniffHandler OnSniff;

        public event SniffPacketHandler OnSniffPacket;

        public UPnPServiceWatcher(UPnPService S, SniffHandler cb) : this(S, cb, null)
        {
        }

        public UPnPServiceWatcher(UPnPService S, SniffHandler cb, SniffPacketHandler pcb)
        {
            this.OnSniff = (SniffHandler) Delegate.Combine(this.OnSniff, cb);
            this.OnSniffPacket = (SniffPacketHandler) Delegate.Combine(this.OnSniffPacket, pcb);
            this._S = S;
            this._S.OnSniff += new UPnPService.SniffHandler(this.SniffSink);
            this._S.OnSniffPacket += new UPnPService.SniffPacketHandler(this.SniffPacketSink);
        }

        ~UPnPServiceWatcher()
        {
            this._S.OnSniff -= new UPnPService.SniffHandler(this.SniffSink);
            this._S.OnSniffPacket -= new UPnPService.SniffPacketHandler(this.SniffPacketSink);
        }

        protected void SniffPacketSink(UPnPService sender, HTTPMessage MSG)
        {
            if (this.OnSniffPacket != null)
            {
                this.OnSniffPacket(this, MSG);
            }
        }

        protected void SniffSink(byte[] raw, int offset, int length)
        {
            if (this.OnSniff != null)
            {
                this.OnSniff(this, raw, offset, length);
            }
        }

        public UPnPService ServiceThatIsBeingWatched
        {
            get
            {
                return this._S;
            }
        }

        public delegate void SniffHandler(UPnPServiceWatcher sender, byte[] raw, int offset, int length);

        public delegate void SniffPacketHandler(UPnPServiceWatcher sender, HTTPMessage MSG);
    }
}

