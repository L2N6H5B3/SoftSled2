namespace Intel.UPNP
{
    using System;

    public class UPnPArgument : ICloneable
    {
        internal UPnPStateVariable __StateVariable;
        public string DataType;
        public object DataValue;
        public string Direction;
        public bool IsReturnValue;
        public string Name;
        internal UPnPAction parentAction;
        internal string StateVarName;

        internal UPnPArgument()
        {
            this.__StateVariable = null;
            this.StateVarName = null;
            this.parentAction = null;
        }

        public UPnPArgument(string name, object val)
        {
            this.__StateVariable = null;
            this.StateVarName = null;
            this.parentAction = null;
            this.Name = name;
            this.DataValue = val;
            if (val != null)
            {
                this.DataType = val.GetType().ToString();
            }
            else
            {
                this.DataType = "System.Void";
            }
            this.IsReturnValue = false;
        }

        public object Clone()
        {
            return base.MemberwiseClone();
        }

        public override string ToString()
        {
            return this.Name;
        }

        public UPnPAction ParentAction
        {
            get
            {
                return this.parentAction;
            }
            set
            {
                this.parentAction = value;
            }
        }

        public UPnPStateVariable RelatedStateVar
        {
            get
            {
                if (this.StateVarName == null)
                {
                    return null;
                }
                return this.parentAction.ParentService.GetStateVariableObject(this.StateVarName);
            }
            set
            {
                if (value == null)
                {
                    this.StateVarName = null;
                }
                else
                {
                    this.StateVarName = value.Name;
                    this.__StateVariable = value;
                }
            }
        }
    }
}

