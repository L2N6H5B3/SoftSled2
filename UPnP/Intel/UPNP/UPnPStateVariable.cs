namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Xml;

    public class UPnPStateVariable : ICloneable
    {
        protected UPnPComplexType _ComplexType;
        protected string[] Allowed;
        protected ArrayList AssociationList;
        internal object CurrentValue;
        protected object DefValue;
        internal bool DO_VALIDATE;
        protected object MaxVal;
        protected object MinVal;
        public bool MulticastEvent;
        internal UPnPService ParentService;
        public bool SendEvent;
        protected object StepVal;
        protected string VariableName;
        internal string VarType;

        public event ModifiedHandler OnModified;

        internal UPnPStateVariable(string VarName)
        {
            this._ComplexType = null;
            this.MinVal = null;
            this.MaxVal = null;
            this.StepVal = null;
            this.ParentService = null;
            this.DO_VALIDATE = true;
            this.DefValue = null;
            this.CurrentValue = null;
            this.VariableName = VarName;
            this.Allowed = null;
            this.SendEvent = false;
            this.AssociationList = new ArrayList();
        }

        public UPnPStateVariable(string VarName, UPnPComplexType CT)
        {
            this._ComplexType = null;
            this.MinVal = null;
            this.MaxVal = null;
            this.StepVal = null;
            this.ParentService = null;
            this.DO_VALIDATE = true;
            this.SendEvent = false;
            this.DefValue = null;
            this.CurrentValue = null;
            this.VariableName = VarName;
            this.Allowed = null;
            this.AssociationList = new ArrayList();
            this.VarType = "string";
            this._ComplexType = CT;
        }

        public UPnPStateVariable(string VarName, object VarValue) : this(VarName, VarValue, null)
        {
        }

        public UPnPStateVariable(string VarName, object VarValue, string[] AllowedValues)
        {
            this._ComplexType = null;
            this.MinVal = null;
            this.MaxVal = null;
            this.StepVal = null;
            this.ParentService = null;
            this.DO_VALIDATE = true;
            this.DefValue = VarValue;
            this.CurrentValue = VarValue;
            this.VariableName = VarName;
            this.Allowed = AllowedValues;
            this.SendEvent = true;
            this.VarType = ConvertToUPnPType(VarValue.GetType());
            if (this.VarType == "boolean")
            {
                VarValue = ((string) VarValue).ToLower();
            }
            this.AssociationList = new ArrayList();
        }

        public UPnPStateVariable(string VarName, Type VType, bool SendEvents)
        {
            this._ComplexType = null;
            this.MinVal = null;
            this.MaxVal = null;
            this.StepVal = null;
            this.ParentService = null;
            this.DO_VALIDATE = true;
            this.SendEvent = SendEvents;
            this.DefValue = null;
            this.CurrentValue = null;
            this.VariableName = VarName;
            this.Allowed = null;
            this.SendEvent = SendEvents;
            this.AssociationList = new ArrayList();
            this.VarType = ConvertToUPnPType(VType);
        }

        public void AddAssociation(string ActionName, string ArgumentName)
        {
            AssociationNode node = new AssociationNode();
            node.ActionName = ActionName;
            node.ArgName = ArgumentName;
            this.AssociationList.Add(node);
        }

        internal string BuildProperty()
        {
            string str = "";
            if (!this.SendEvent)
            {
                return "";
            }
            string str3 = str + "<e:property>\n";
            return ((str3 + "<" + this.Name + ">" + UPnPService.SerializeObjectInstance(this.Value) + "</" + this.Name + ">\n") + "</e:property>\n");
        }

        internal void BuildProperty(string prefix, string ns, XmlTextWriter XMLDoc)
        {
            if (this.SendEvent)
            {
                XMLDoc.WriteStartElement(prefix, "property", ns);
                XMLDoc.WriteStartElement(this.Name);
                XMLDoc.WriteRaw(UPnPService.SerializeObjectInstance(this.Value));
                XMLDoc.WriteEndElement();
                XMLDoc.WriteEndElement();
            }
        }

        public void Clear()
        {
            this.CurrentValue = null;
        }

        public object Clone()
        {
            return base.MemberwiseClone();
        }

        public static Type ConvertFromUPnPType(string TheType)
        {
            switch (TheType)
            {
                case "string":
                    return typeof(string);

                case "boolean":
                    return typeof(bool);

                case "uri":
                    return typeof(Uri);

                case "ui1":
                    return typeof(byte);

                case "ui2":
                    return typeof(ushort);

                case "ui4":
                    return typeof(uint);

                case "int":
                    return typeof(int);

                case "i4":
                    return typeof(int);

                case "i2":
                    return typeof(short);

                case "i1":
                    return typeof(sbyte);

                case "r4":
                    return typeof(float);

                case "r8":
                    return typeof(double);

                case "number":
                    return typeof(double);

                case "float":
                    return typeof(float);

                case "char":
                    return typeof(char);

                case "bin.base64":
                    return typeof(byte[]);

                case "dateTime":
                    return typeof(DateTime);
            }
            return typeof(object);
        }

        public static string ConvertToUPnPType(Type TheType)
        {
            string fullName = TheType.FullName;
            if (fullName.EndsWith("&"))
            {
                fullName = fullName.Substring(0, fullName.Length - 1);
            }
            switch (fullName)
            {
                case "System.Char":
                    return "char";

                case "System.String":
                    return "string";

                case "System.Boolean":
                    return "boolean";

                case "System.Uri":
                    return "uri";

                case "System.Byte":
                    return "ui1";

                case "System.UInt16":
                    return "ui2";

                case "System.UInt32":
                    return "ui4";

                case "System.Int32":
                    return "i4";

                case "System.Int16":
                    return "i2";

                case "System.SByte":
                    return "ui1";

                case "System.Single":
                    return "r4";

                case "System.Double":
                    return "r8";

                case "System.Byte[]":
                    return "bin.base64";

                case "System.DateTime":
                    return "dateTime";
            }
            return TheType.FullName;
        }

        internal AssociationNode[] GetAssociations()
        {
            return (AssociationNode[]) this.AssociationList.ToArray(typeof(AssociationNode));
        }

        public Type GetNetType()
        {
            return ConvertFromUPnPType(this.VarType);
        }

        internal void GetStateVariableXML(XmlTextWriter XDoc)
        {
            XDoc.WriteStartElement("stateVariable");
            if (this.SendEvent)
            {
                XDoc.WriteAttributeString("sendEvents", "yes");
            }
            else
            {
                XDoc.WriteAttributeString("sendEvents", "no");
            }
            if (this.MulticastEvent)
            {
                XDoc.WriteAttributeString("multicast", "yes");
            }
            XDoc.WriteElementString("name", this.VariableName);
            if (this.ComplexType != null)
            {
                XDoc.WriteStartElement("dataType");
                XDoc.WriteAttributeString("type", this.OwningService.ComplexType_NamespacePrefix[this.ComplexType.Name_NAMESPACE].ToString() + ":" + this.ComplexType.Name_LOCAL);
                XDoc.WriteString("string");
                XDoc.WriteEndElement();
            }
            else
            {
                XDoc.WriteElementString("dataType", this.ValueType);
            }
            if (this.Allowed != null)
            {
                XDoc.WriteStartElement("allowedValueList");
                for (int i = 0; i < this.Allowed.Length; i++)
                {
                    XDoc.WriteElementString("allowedValue", this.Allowed[i]);
                }
                XDoc.WriteEndElement();
            }
            if (this.DefValue != null)
            {
                XDoc.WriteElementString("defaultValue", UPnPService.SerializeObjectInstance(this.DefValue));
            }
            if ((this.MinVal != null) && (this.MaxVal != null))
            {
                XDoc.WriteStartElement("allowedValueRange");
                XDoc.WriteElementString("minimum", this.MinVal.ToString());
                XDoc.WriteElementString("maximum", this.MaxVal.ToString());
                if (this.StepVal != null)
                {
                    XDoc.WriteElementString("step", this.StepVal.ToString());
                }
                XDoc.WriteEndElement();
            }
            XDoc.WriteEndElement();
        }

        protected Type GetTypeFromUnknown(string TypeName)
        {
            Type type2 = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type type = null;
            try
            {
                type2 = Type.GetType(TypeName, true);
            }
            catch
            {
            }
            return type2;
            for (int i = 0; i < assemblies.Length; i++)
            {
                Module[] modules = assemblies[i].GetModules();
                for (int j = 0; j < modules.Length; j++)
                {
                    type = modules[j].GetType(TypeName);
                    if (type != null)
                    {
                        break;
                    }
                }
                if (type != null)
                {
                    break;
                }
            }
            if (type == null)
            {
                throw new Exception("Type: " + TypeName + " could not be loaded");
            }
            return type;
        }

        internal void InitialEvent()
        {
            if ((this.SendEvent && (this.Value != null)) && (this.OnModified != null))
            {
                this.OnModified(this, this.Value);
            }
        }

        public void RemoveAssociation(string ActionName, string ArgumentName)
        {
            AssociationNode node = new AssociationNode();
            node.ActionName = ActionName;
            node.ArgName = ArgumentName;
            this.AssociationList.Remove(node);
        }

        public void SetRange(object Min, object Max, object Step)
        {
            this.MinVal = Min;
            this.MaxVal = Max;
            this.StepVal = Step;
        }

        public override string ToString()
        {
            return this.Name;
        }

        internal void Validate(object NewVal)
        {
            if (this.DO_VALIDATE && (NewVal != null))
            {
                if (NewVal.GetType().FullName != this.GetNetType().FullName)
                {
                    EventLogger.Log(this, EventLogEntryType.Error, "Type Checking Failed: " + ConvertFromUPnPType(this.VarType).FullName + " expected, not " + NewVal.GetType().FullName);
                    throw new UPnPTypeMismatchException(ConvertFromUPnPType(this.VarType).FullName + " expected, not " + NewVal.GetType().FullName);
                }
                if (this.AllowedStringValues != null)
                {
                    bool flag = false;
                    foreach (string str in this.AllowedStringValues)
                    {
                        if (((string) NewVal) == str)
                        {
                            flag = true;
                            break;
                        }
                    }
                    if (!flag)
                    {
                        EventLogger.Log(this, EventLogEntryType.Error, "Type Checking Failed: " + NewVal.ToString() + " NOT in allowed value list");
                        throw new UPnPTypeMismatchException(NewVal.ToString() + " NOT in allowed value list");
                    }
                }
                if ((this.MinVal != null) || (this.MaxVal != null))
                {
                    if ((this.MinVal != null) && (((IComparable) NewVal).CompareTo(this.MinVal) < 0))
                    {
                        EventLogger.Log(this, EventLogEntryType.Error, "Type Checking Failed: Specified value: " + NewVal.ToString() + " must be >= " + this.MinVal.ToString());
                        throw new OutOfRangeException("Specified value: " + NewVal.ToString() + " must be >= " + this.MinVal.ToString());
                    }
                    if ((this.MaxVal != null) && (((IComparable) NewVal).CompareTo(this.MaxVal) > 0))
                    {
                        EventLogger.Log(this, EventLogEntryType.Error, "Type Checking Failed: Specified value: " + NewVal.ToString() + " must be <= " + this.MaxVal.ToString());
                        throw new OutOfRangeException("Specified value: " + NewVal.ToString() + " must be <= " + this.MaxVal.ToString());
                    }
                }
            }
        }

        public string[] AllowedStringValues
        {
            get
            {
                return this.Allowed;
            }
            set
            {
                this.Allowed = value;
            }
        }

        public UPnPComplexType ComplexType
        {
            get
            {
                return this._ComplexType;
            }
        }

        public object DefaultValue
        {
            get
            {
                return this.DefValue;
            }
            set
            {
                this.DefValue = value;
            }
        }

        public object Maximum
        {
            get
            {
                return this.MaxVal;
            }
            set
            {
                this.MaxVal = value;
            }
        }

        public object Minimum
        {
            get
            {
                return this.MinVal;
            }
            set
            {
                this.MinVal = value;
            }
        }

        public string Name
        {
            get
            {
                return this.VariableName;
            }
        }

        public UPnPService OwningService
        {
            get
            {
                return this.ParentService;
            }
        }

        public object Step
        {
            get
            {
                return this.StepVal;
            }
            set
            {
                this.StepVal = value;
            }
        }

        public virtual object Value
        {
            get
            {
                return this.CurrentValue;
            }
            set
            {
                bool flag = false;
                this.Validate(value);
                if (this.CurrentValue != null)
                {
                    if (!this.CurrentValue.Equals(value))
                    {
                        flag = true;
                    }
                }
                else
                {
                    flag = true;
                }
                this.CurrentValue = value;
                if (this.SendEvent && (this.ParentService != null))
                {
                    this.ParentService.SendEvents(this);
                }
                if ((this.OnModified != null) && flag)
                {
                    this.OnModified(this, value);
                }
            }
        }

        public string ValueType
        {
            get
            {
                return this.VarType;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AssociationNode
        {
            public string ActionName;
            public string ArgName;
            public override bool Equals(object j)
            {
                UPnPStateVariable.AssociationNode node = (UPnPStateVariable.AssociationNode) j;
                return ((node.ActionName == this.ActionName) && (node.ArgName == this.ArgName));
            }

            public override int GetHashCode()
            {
                return this.ActionName.GetHashCode();
            }
        }

        public class CannotRemoveException : Exception
        {
            public CannotRemoveException(string msg) : base(msg)
            {
            }
        }

        public delegate void ModifiedHandler(UPnPStateVariable sender, object NewValue);

        public class OutOfRangeException : Exception
        {
            public OutOfRangeException(string msg) : base(msg)
            {
            }
        }
    }
}

