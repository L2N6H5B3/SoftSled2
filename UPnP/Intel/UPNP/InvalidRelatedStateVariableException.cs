namespace Intel.UPNP
{
    using System;

    public class InvalidRelatedStateVariableException : SCPDException
    {
        public InvalidRelatedStateVariableException(string msg) : base(msg)
        {
        }
    }
}

