namespace Intel.UPNP
{
    using System;
    using System.Threading;

    public class LockRMWO
    {
        private ReaderWriterLock RWLock = new ReaderWriterLock();

        public void EndRead()
        {
            this.RWLock.ReleaseReaderLock();
        }

        public void EndWrite()
        {
            this.RWLock.ReleaseWriterLock();
        }

        public void StartRead()
        {
            this.RWLock.AcquireReaderLock(0x7530);
        }

        public void StartWrite()
        {
            this.RWLock.AcquireWriterLock(0x7530);
        }
    }
}

