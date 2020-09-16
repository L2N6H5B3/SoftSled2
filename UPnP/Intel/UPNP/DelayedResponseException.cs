namespace Intel.UPNP
{
    using System;

    public class DelayedResponseException : Exception
    {
        public DelayedResponseException() : base("ResponseWillReturnLater")
        {
        }
    }
}

