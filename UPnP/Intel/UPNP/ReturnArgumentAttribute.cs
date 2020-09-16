namespace Intel.UPNP
{
    using System;

    [AttributeUsage(AttributeTargets.Method, Inherited=false, AllowMultiple=false)]
    public class ReturnArgumentAttribute : System.Attribute
    {
        private string _name;

        public ReturnArgumentAttribute(string val)
        {
            this._name = val;
        }

        public string Name
        {
            get
            {
                return this._name;
            }
        }
    }
}

