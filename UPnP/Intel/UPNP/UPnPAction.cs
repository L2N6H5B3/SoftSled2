namespace Intel.UPNP
{
    using System;
    using System.Collections;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Xml;

    public class UPnPAction : ICloneable
    {
        protected ArrayList ArgList;
        internal MethodInfo MethodPointer;
        public string Name;
        public UPnPService ParentService;
        public SpecialInvokeCase SpecialCase;

        public UPnPAction()
        {
            this.SpecialCase = null;
            this.ParentService = null;
            this.MethodPointer = null;
            this.ArgList = new ArrayList();
        }

        public UPnPAction(UPnPService parent)
        {
            this.SpecialCase = null;
            this.ParentService = null;
            this.MethodPointer = null;
            this.ArgList = new ArrayList();
            this.ParentService = parent;
        }

        internal void AddArgument(UPnPArgument Arg)
        {
            Arg.ParentAction = this;
            this.ArgList.Add(Arg);
        }

        public object Clone()
        {
            UPnPAction action = (UPnPAction) base.MemberwiseClone();
            action.ArgList = new ArrayList();
            foreach (UPnPArgument argument in this.ArgList)
            {
                action.ArgList.Add(argument.Clone());
            }
            return action;
        }

        public override bool Equals(object obj)
        {
            UPnPAction action = (UPnPAction) obj;
            if ((this.ParentService != null) && (action.ParentService != null))
            {
                return ((this.ParentService.ServiceURN == action.ParentService.ServiceURN) && (action.Name == this.Name));
            }
            return base.Equals(obj);
        }

        public UPnPArgument GetArg(string ArgName)
        {
            IEnumerator enumerator = this.ArgList.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (((UPnPArgument) enumerator.Current).Name == ArgName)
                {
                    return (UPnPArgument) enumerator.Current;
                }
            }
            return null;
        }

        public override int GetHashCode()
        {
            if (((this.ParentService != null) && (this.ParentService.ServiceURN != null)) && (this.Name != null))
            {
                return (this.ParentService.ServiceURN + ":" + this.Name).GetHashCode();
            }
            return this.ToString().GetHashCode();
        }

        public UPnPArgument GetRetArg()
        {
            IEnumerator enumerator = this.ArgList.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (((UPnPArgument) enumerator.Current).IsReturnValue)
                {
                    return (UPnPArgument) enumerator.Current;
                }
            }
            return null;
        }

        internal void GetXML(XmlTextWriter XDoc)
        {
            ArrayList list = new ArrayList();
            ArrayList list2 = new ArrayList();
            XDoc.WriteStartElement("action");
            XDoc.WriteElementString("name", this.Name);
            if ((this.ArgumentList.Length > 0) || this.HasReturnValue)
            {
                UPnPArgument argument;
                XDoc.WriteStartElement("argumentList");
                list.Clear();
                list2.Clear();
                if (this.HasReturnValue)
                {
                    list.Add(this.GetRetArg());
                }
                foreach (UPnPArgument argument2 in this.ArgumentList)
                {
                    if (!argument2.IsReturnValue)
                    {
                        if (argument2.Direction == "out")
                        {
                            list.Add(argument2);
                        }
                        else
                        {
                            list2.Add(argument2);
                        }
                    }
                }
                for (int i = 0; i < list2.Count; i++)
                {
                    argument = (UPnPArgument) list2[i];
                    XDoc.WriteStartElement("argument");
                    XDoc.WriteElementString("name", argument.Name);
                    XDoc.WriteElementString("direction", argument.Direction);
                    if (argument.IsReturnValue)
                    {
                        XDoc.WriteElementString("retval", "");
                    }
                    if (argument.RelatedStateVar != null)
                    {
                        XDoc.WriteElementString("relatedStateVariable", argument.RelatedStateVar.Name);
                    }
                    XDoc.WriteEndElement();
                }
                for (int j = 0; j < list.Count; j++)
                {
                    argument = (UPnPArgument) list[j];
                    XDoc.WriteStartElement("argument");
                    XDoc.WriteElementString("name", argument.Name);
                    XDoc.WriteElementString("direction", argument.Direction);
                    if (argument.IsReturnValue)
                    {
                        XDoc.WriteElementString("retval", "");
                    }
                    if (argument.RelatedStateVar != null)
                    {
                        XDoc.WriteElementString("relatedStateVariable", argument.RelatedStateVar.Name);
                    }
                    XDoc.WriteEndElement();
                }
                XDoc.WriteEndElement();
            }
            XDoc.WriteEndElement();
        }

        public override string ToString()
        {
            return this.Name;
        }

        public bool ValidateArgs(UPnPArgument[] Args)
        {
            int count = this.ArgList.Count;
            if (this.HasReturnValue)
            {
                count--;
            }
            if (Args.Length != count)
            {
                throw new UPnPInvokeException(this.Name, Args, "Incorrect number of Args");
            }
            for (int i = 0; i < Args.Length; i++)
            {
                UPnPArgument arg = this.GetArg(Args[i].Name);
                if (arg == null)
                {
                    throw new UPnPInvokeException(this.Name, Args, Args[i].Name + " was not found in action: " + this.Name);
                }
                if (arg.Direction == "in")
                {
                    try
                    {
                        arg.RelatedStateVar.Validate(Args[i].DataValue);
                    }
                    catch (Exception exception)
                    {
                        throw new UPnPInvokeException(this.Name, Args, exception.Message);
                    }
                }
            }
            return true;
        }

        public UPnPArgument[] ArgumentList
        {
            get
            {
                UPnPArgument[] argumentArray = new UPnPArgument[this.ArgList.Count];
                for (int i = 0; i < argumentArray.Length; i++)
                {
                    argumentArray[i] = (UPnPArgument) this.ArgList[i];
                }
                return argumentArray;
            }
        }

        public IList Arguments
        {
            get
            {
                return this.ArgList;
            }
            set
            {
                this.ArgList.Clear();
                foreach (UPnPArgument argument in value)
                {
                    this.ArgList.Add(argument);
                }
            }
        }

        public bool HasReturnValue
        {
            get
            {
                int count = this.ArgList.Count;
                for (int i = 0; i < count; i++)
                {
                    if (((UPnPArgument) this.ArgList[i]).IsReturnValue)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public delegate void SpecialInvokeCase(UPnPAction sender, UPnPArgument[] InArgs, out object RetVal, out UPnPArgument[] OutArgs);
    }
}

