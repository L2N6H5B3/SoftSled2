namespace Intel.UPNP
{
    using System;

    public class UPnPAlreadySubscribedException : Exception
    {
        public UPnPAlreadySubscribedException(string msg) : base(msg)
        {
        }
    }
}

