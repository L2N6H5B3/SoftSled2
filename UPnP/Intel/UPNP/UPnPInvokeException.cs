namespace Intel.UPNP
{
    using System;

    public class UPnPInvokeException : Exception
    {
        protected UPnPCustomException _Inner;
        public UPnPArgument[] Arguments;
        public string MethodName;

        public UPnPInvokeException(string Method_Name, UPnPArgument[] Args, string msg) : base(msg)
        {
            this._Inner = null;
            this.MethodName = Method_Name;
            this.Arguments = Args;
        }

        public UPnPInvokeException(string Method_Name, UPnPArgument[] Args, string msg, UPnPCustomException e) : base(msg)
        {
            this._Inner = null;
            this.MethodName = Method_Name;
            this.Arguments = Args;
            this._Inner = e;
        }

        public UPnPCustomException UPNP
        {
            get
            {
                return this._Inner;
            }
        }
    }
}

