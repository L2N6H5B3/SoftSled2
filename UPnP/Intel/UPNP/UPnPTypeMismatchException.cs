namespace Intel.UPNP
{
    using System;

    public class UPnPTypeMismatchException : Exception
    {
        public UPnPTypeMismatchException(string msg) : base(msg)
        {
        }
    }
}

