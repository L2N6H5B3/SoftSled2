namespace Intel.UPNP
{
    using System;
    using System.Runtime.CompilerServices;

    public class HTTPSessionWatcher
    {
        private WeakReference W;

        public event SniffHandler OnSniff;

        public HTTPSessionWatcher(HTTPSession s)
        {
            this.W = new WeakReference(s);
            s.OnSniff += new HTTPSession.SniffHandler(this.SniffSink);
        }

        private void SniffSink(byte[] raw, int offset, int length)
        {
            if (this.OnSniff != null)
            {
                this.OnSniff(raw, offset, length);
            }
        }

        public delegate void SniffHandler(byte[] raw, int offset, int length);
    }
}

