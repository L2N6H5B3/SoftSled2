namespace Intel.UPNP
{
    using System;

    public class UPnPCustomException : Exception
    {
        protected int _EC;
        protected string _ED;

        public UPnPCustomException(int _ErrorCode, string _ErrorDescription) : base(_ErrorDescription)
        {
            this._EC = _ErrorCode;
            this._ED = _ErrorDescription;
        }

        public UPnPCustomException(int _ErrorCode, string _ErrorDescription, Exception innerException) : base(_ErrorDescription, innerException)
        {
            this._EC = _ErrorCode;
            this._ED = _ErrorDescription;
        }

        public int ErrorCode
        {
            get
            {
                return this._EC;
            }
        }

        public string ErrorDescription
        {
            get
            {
                return this._ED;
            }
        }
    }
}

