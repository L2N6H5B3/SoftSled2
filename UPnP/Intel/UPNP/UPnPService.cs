namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;
    using System.Xml;

    public sealed class UPnPService : ICloneable
    {
        internal string __controlurl;
        internal string __eventurl;
        internal Hashtable ComplexType_NamespacePrefix;
        private int ComplexType_NamespacePrefixIndex;
        internal Hashtable ComplexType_NamespaceTables;
        private Hashtable ComplexTypeTable;
        private string CurrentSID;
        private int CurrentTimeout;
        private Hashtable DelayedResponseTable;
        internal string EventCallbackURL;
        private Hashtable EventSessionTable;
        private int EventSID;
        private HTTPRequest InvocationPipeline;
        private Hashtable LocalMethodList;
        public int Major;
        public int Minor;
        private WeakEvent OnSniffEvent;
        private WeakEvent OnSniffPacketEvent;
        public UPnPDevice ParentDevice;
        private WeakEvent PeriodicRenewFailedEvent;
        private SortedList RemoteMethods;
        internal Hashtable SchemaURLS;
        internal string SCPDURL;
        private Hashtable SendEventTable;
        private string Service_ID;
        private object ServiceInstance;
        private string ServiceType;
        private object SIDLock;
        private int SniffCount;
        private int SniffPacketCount;
        private bool StandardService;
        private Hashtable StateVariables;
        private int SubscribeCounter;
        private static LifeTimeMonitor SubscribeCycle = new LifeTimeMonitor();
        private LifeTimeMonitor.LifeTimeHandler SubscribeCycleCallback;
        private object SubscribeLock;
        private Hashtable SubscribeRequestTable;
        private Hashtable SubscriberTable;
        private WeakEvent SubscriptionAddedEvent;
        private WeakEvent SubscriptionRemovedEvent;
        public object User;
        public object User2;
        private Hashtable VarAssociation;

        public event UPnPServiceInvokeErrorHandler OnInvokeError;

        public event UPnPServiceInvokeHandler OnInvokeResponse;

        public event PeriodicRenewFailedHandler OnPeriodicRenewFailed
        {
            add
            {
                this.PeriodicRenewFailedEvent.Register(value);
            }
            remove
            {
                this.PeriodicRenewFailedEvent.UnRegister(value);
            }
        }

        public event ServiceResetHandler OnServiceReset;

        internal event SniffHandler OnSniff
        {
            add
            {
                this.OnSniffEvent.Register(value);
                if (Interlocked.Increment(ref this.SniffCount) == 1)
                {
                    this.InvocationPipeline.OnSniff += new HTTPRequest.SniffHandler(this.SniffSink);
                    this.InvocationPipeline.SetSniffHandlers();
                }
            }
            remove
            {
                this.OnSniffEvent.UnRegister(value);
                if (Interlocked.Decrement(ref this.SniffCount) == 0)
                {
                    this.InvocationPipeline.OnSniff -= new HTTPRequest.SniffHandler(this.SniffSink);
                    this.InvocationPipeline.ReleaseSniffHandlers();
                }
            }
        }

        internal event SniffPacketHandler OnSniffPacket
        {
            add
            {
                this.OnSniffPacketEvent.Register(value);
                if (Interlocked.Increment(ref this.SniffPacketCount) == 1)
                {
                    this.InvocationPipeline.OnSniffPacket += new HTTPRequest.RequestHandler(this.SniffPacketSink);
                    this.InvocationPipeline.SetSniffHandlers();
                }
            }
            remove
            {
                this.OnSniffPacketEvent.UnRegister(value);
                if (Interlocked.Decrement(ref this.SniffPacketCount) == 0)
                {
                    this.InvocationPipeline.OnSniffPacket -= new HTTPRequest.RequestHandler(this.SniffPacketSink);
                    this.InvocationPipeline.ReleaseSniffHandlers();
                }
            }
        }

        public event UPnPEventSubscribeHandler OnSubscribe;

        public event OnSubscriptionHandler OnSubscriptionAdded
        {
            add
            {
                this.SubscriptionAddedEvent.Register(value);
            }
            remove
            {
                this.SubscriptionAddedEvent.UnRegister(value);
            }
        }

        public event OnSubscriptionHandler OnSubscriptionRemoved
        {
            add
            {
                this.SubscriptionRemovedEvent.Register(value);
            }
            remove
            {
                this.SubscriptionRemovedEvent.UnRegister(value);
            }
        }

        public event UPnPEventHandler OnUPnPEvent;

        internal UPnPService(double version)
        {
            this.User = null;
            this.User2 = null;
            this.ComplexTypeTable = Hashtable.Synchronized(new Hashtable());
            this.ComplexType_NamespacePrefix = Hashtable.Synchronized(new Hashtable());
            this.ComplexType_NamespaceTables = Hashtable.Synchronized(new Hashtable());
            this.SchemaURLS = new Hashtable();
            this.ComplexType_NamespacePrefixIndex = 0;
            this.PeriodicRenewFailedEvent = new WeakEvent();
            this.SubscriptionAddedEvent = new WeakEvent();
            this.SubscriptionRemovedEvent = new WeakEvent();
            this.SubscribeRequestTable = Hashtable.Synchronized(new Hashtable());
            this.SendEventTable = Hashtable.Synchronized(new Hashtable());
            this.InvocationPipeline = new HTTPRequest();
            this.DelayedResponseTable = new Hashtable();
            this.EventSessionTable = Hashtable.Synchronized(new Hashtable());
            this.OnSniffEvent = new WeakEvent();
            this.OnSniffPacketEvent = new WeakEvent();
            this.SniffPacketCount = 0;
            this.SniffCount = 0;
            this.SubscribeLock = new object();
            this.SubscribeCounter = 0;
            InstanceTracker.Add(this);
            this.InvocationPipeline.OnResponse += new HTTPRequest.RequestHandler(this.HandleInvokeRequest);
            this.SubscribeCycleCallback = new LifeTimeMonitor.LifeTimeHandler(this.SubscribeCycleSink);
            SubscribeCycle.OnExpired += this.SubscribeCycleCallback;
            this.VarAssociation = new Hashtable();
            this.LocalMethodList = new Hashtable();
            this.RemoteMethods = new SortedList();
            this.SIDLock = new object();
            this.EventSID = 0;
            this.StateVariables = Hashtable.Synchronized(new Hashtable());
            this.SubscriberTable = Hashtable.Synchronized(new Hashtable());
            this.CurrentSID = "";
            if (version == 0.0)
            {
                this.Major = 1;
                this.Minor = 0;
            }
            else
            {
                DText text = new DText();
                text.ATTRMARK = ".";
                text[0] = version.ToString();
                this.Major = int.Parse(text[1]);
                if (text.DCOUNT() == 2)
                {
                    this.Minor = int.Parse(text[2]);
                }
                else
                {
                    this.Minor = 0;
                }
            }
        }

        public UPnPService(double version, object InstanceObject) : this(version)
        {
            this.ServiceInstance = InstanceObject;
        }

        public UPnPService(double version, string serviceID, string serviceType, bool IsStandardService, object Instance) : this(version)
        {
            this.StandardService = IsStandardService;
            this.ServiceInstance = Instance;
            if (serviceID == "")
            {
                this.ServiceID = Guid.NewGuid().ToString();
            }
            else
            {
                this.ServiceID = serviceID;
            }
            this.ServiceURN = serviceType;
            this.SCPDURL = "_" + this.ServiceID + "_scpd.xml";
            this.ControlURL = "_" + this.ServiceID + "_control";
            this.EventURL = "_" + this.ServiceID + "_event";
        }

        internal void _CancelEvent(string SID)
        {
            if (this.SubscriberTable.ContainsKey(SID))
            {
                this.SubscriberTable.Remove(SID);
                this.SubscriptionRemovedEvent.Fire(this);
            }
        }

        internal bool _RenewEvent(string SID, string Timeout)
        {
            object obj2 = this.SubscriberTable[SID];
            if (obj2 == null)
            {
                return false;
            }
            SubscriberInfo info = (SubscriberInfo) obj2;
            int num = int.Parse(Timeout);
            if (num == 0)
            {
                info.Expires = -1L;
            }
            else
            {
                info.Expires = DateTime.Now.AddSeconds((double) num).Ticks;
            }
            this.SubscriberTable[SID] = info;
            return true;
        }

        internal HTTPMessage _SubscribeEvent(out string SID, string CallbackURL, string Timeout)
        {
            SubscriberInfo info = new SubscriberInfo();
            info.SID = this.GetNewSID();
            info.CallbackURL = CallbackURL;
            info.SEQ = 1L;
            int num = int.Parse(Timeout);
            if (num == 0)
            {
                info.Expires = -1L;
            }
            else
            {
                info.Expires = DateTime.Now.AddSeconds((double) num).Ticks;
            }
            this.SubscriberTable[info.SID] = info;
            HTTPMessage message = new HTTPMessage();
            message.Directive = "NOTIFY";
            message.AddTag("Content-Type", "text/xml");
            message.AddTag("NT", "upnp:event");
            message.AddTag("NTS", "upnp:propchange");
            message.AddTag("SID", info.SID);
            message.AddTag("SEQ", "0");
            message.AddTag("CONNECTION", "close");
            message.BodyBuffer = this.BuildEventXML();
            SID = info.SID;
            this.SubscriptionAddedEvent.Fire(this);
            return message;
        }

        internal void _TriggerEvent(string SID, long SEQ, string XML)
        {
            this.ParseEvents(XML);
            if (this.OnUPnPEvent != null)
            {
                this.OnUPnPEvent(this, SEQ);
            }
        }

        internal void _Update(UPnPService s)
        {
            this.__controlurl = s.__controlurl;
            this.__eventurl = s.__eventurl;
            this.EventCallbackURL = s.EventCallbackURL;
            this.EventURL = s.EventURL;
            if (this.SubscribeCounter != 0)
            {
                SubscribeCycle.Remove(this.GetHashCode());
                this.SubscribeCounter = 0;
                this.Subscribe(this.CurrentTimeout, null);
            }
            if (this.OnServiceReset != null)
            {
                this.OnServiceReset(this);
            }
        }

        private void AddAction(UPnPAction action)
        {
            action.ParentService = this;
            this.RemoteMethods[action.Name] = action;
            foreach (UPnPArgument argument in action.Arguments)
            {
                if (argument.__StateVariable == null)
                {
                    continue;
                }
                if (this.GetStateVariableObject(argument.__StateVariable.Name) == null)
                {
                    this.AddStateVariable(argument.__StateVariable);
                }
                argument.__StateVariable = null;
            }
        }

        public void AddComplexType(UPnPComplexType t)
        {
            t.ParentService = this;
            if (!this.ComplexType_NamespaceTables.Contains(t.Name_NAMESPACE))
            {
                this.ComplexType_NamespaceTables[t.Name_NAMESPACE] = new ArrayList();
                this.SchemaURLS[t.Name_NAMESPACE] = "http://www.vendor.org/Schemas/Sample.xsd";
            }
            ((ArrayList) this.ComplexType_NamespaceTables[t.Name_NAMESPACE]).Add(t);
            this.ComplexTypeTable[t.Name_LOCAL + ":" + t.Name_NAMESPACE] = t;
            if (!this.ComplexType_NamespacePrefix.ContainsKey(t.Name_NAMESPACE))
            {
                this.ComplexType_NamespacePrefixIndex++;
                this.ComplexType_NamespacePrefix[t.Name_NAMESPACE] = "ct" + this.ComplexType_NamespacePrefixIndex.ToString();
            }
        }

        public void AddMethod(UPnPAction action)
        {
            if ((action.Name == null) || (action.Name.Length == 0))
            {
                throw new Exception("Invalid action name");
            }
            action.ParentService = this;
            this.AddAction(action);
        }

        public void AddMethod(string MethodName)
        {
            UPnPStateVariable variable;
            UPnPArgument argument;
            string argumentName = "_ReturnValue";
            bool flag = false;
            UPnPStateVariable[] stateVariables = this.GetStateVariables();
            MethodInfo method = this.ServiceInstance.GetType().GetMethod(MethodName);
            if (method == null)
            {
                throw new Exception(MethodName + " does not exist in " + this.ServiceInstance.GetType().ToString());
            }
            flag = false;
            if (method.ReturnType.FullName != "System.Void")
            {
                if (method.GetCustomAttributes(true).Length > 0)
                {
                    foreach (System.Attribute attribute in method.GetCustomAttributes(true))
                    {
                        if (attribute.GetType() == typeof(ReturnArgumentAttribute))
                        {
                            argumentName = ((ReturnArgumentAttribute) attribute).Name;
                            break;
                        }
                    }
                }
                variable = new UPnPStateVariable("A_ARG_TYPE_" + MethodName + "_RetType", method.ReturnType, false);
                variable.AddAssociation(MethodName, argumentName);
                foreach (UPnPStateVariable variable2 in stateVariables)
                {
                    foreach (UPnPStateVariable.AssociationNode node in variable2.GetAssociations())
                    {
                        if ((node.ActionName == MethodName) && (node.ArgName == argumentName))
                        {
                            flag = true;
                        }
                    }
                }
                if (!flag)
                {
                    this.AddStateVariable(variable);
                }
            }
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                variable = new UPnPStateVariable("A_ARG_TYPE_" + MethodName + "_" + parameters[i].Name, parameters[i].ParameterType, false);
                variable.AddAssociation(MethodName, parameters[i].Name);
                flag = false;
                foreach (UPnPStateVariable variable3 in stateVariables)
                {
                    foreach (UPnPStateVariable.AssociationNode node2 in variable3.GetAssociations())
                    {
                        if ((node2.ActionName == MethodName) && (node2.ArgName == parameters[i].Name))
                        {
                            flag = true;
                        }
                    }
                }
                if (!flag)
                {
                    this.AddStateVariable(variable);
                }
            }
            UPnPAction action = new UPnPAction();
            action.Name = MethodName;
            action.ParentService = this;
            action.MethodPointer = method;
            if (method.ReturnType.FullName != "System.Void")
            {
                argument = new UPnPArgument(argumentName, "");
                argument.DataType = UPnPStateVariable.ConvertToUPnPType(method.ReturnType);
                argument.Direction = "out";
                argument.IsReturnValue = true;
                argument.ParentAction = action;
                argument.StateVarName = this.GetStateVariableObject(MethodName, argumentName).Name;
                action.AddArgument(argument);
            }
            foreach (ParameterInfo info2 in parameters)
            {
                argument = new UPnPArgument(info2.Name, "");
                argument.DataType = UPnPStateVariable.ConvertToUPnPType(info2.ParameterType);
                argument.Direction = (info2.Attributes == ParameterAttributes.Out) ? "out" : "in";
                argument.IsReturnValue = false;
                argument.ParentAction = action;
                argument.StateVarName = this.GetStateVariableObject(MethodName, info2.Name).Name;
                action.AddArgument(argument);
            }
            this.AddAction(action);
        }

        public void AddStateVariable(UPnPStateVariable NewVar)
        {
            NewVar.ParentService = this;
            this.StateVariables[NewVar.Name] = NewVar;
            UPnPStateVariable.AssociationNode[] associations = NewVar.GetAssociations();
            for (int i = 0; i < associations.Length; i++)
            {
                Hashtable hashtable = (Hashtable) this.VarAssociation[associations[i].ActionName];
                if (hashtable == null)
                {
                    hashtable = new Hashtable();
                }
                if (hashtable.ContainsKey(associations[i].ArgName))
                {
                    this.StateVariables.Remove(((UPnPStateVariable) hashtable[associations[i].ArgName]).Name);
                }
                hashtable[associations[i].ArgName] = NewVar;
                this.VarAssociation[associations[i].ActionName] = hashtable;
                UPnPAction action = this.GetAction(associations[i].ActionName);
                if (action != null)
                {
                    action.GetArg(associations[i].ArgName).StateVarName = NewVar.Name;
                }
            }
        }

        internal void AddVirtualDirectory(string VD)
        {
            this.SCPDURL = VD + "/" + this.SCPDURL;
            this.__controlurl = VD + "/" + this.__controlurl;
            this.__eventurl = VD + "/" + this.__eventurl;
        }

        private byte[] BuildEventXML()
        {
            return this.BuildEventXML(this.GetStateVariables());
        }

        private byte[] BuildEventXML(UPnPStateVariable[] vars)
        {
            MemoryStream w = new MemoryStream();
            XmlTextWriter xMLDoc = new XmlTextWriter(w, Encoding.UTF8);
            xMLDoc.Formatting = Formatting.Indented;
            xMLDoc.Indentation = 3;
            string prefix = "e";
            string ns = "urn:schemas-upnp-org:event-1-0";
            xMLDoc.WriteStartDocument();
            xMLDoc.WriteStartElement(prefix, "propertyset", ns);
            foreach (UPnPStateVariable variable in vars)
            {
                variable.BuildProperty(prefix, ns, xMLDoc);
            }
            xMLDoc.WriteEndElement();
            xMLDoc.WriteEndDocument();
            xMLDoc.Flush();
            byte[] buffer = new byte[w.Length - 3L];
            w.Seek(3L, SeekOrigin.Begin);
            w.Read(buffer, 0, buffer.Length);
            xMLDoc.Close();
            return buffer;
        }

        public object Clone()
        {
            UPnPService service = (UPnPService) base.MemberwiseClone();
            service.StateVariables = new Hashtable();
            foreach (UPnPStateVariable variable in this.GetStateVariables())
            {
                UPnPStateVariable variable2 = (UPnPStateVariable) variable.Clone();
                variable2.ParentService = service;
                service.StateVariables.Add(variable.Name, variable2);
            }
            service.RemoteMethods = new SortedList();
            foreach (UPnPAction action in this.Actions)
            {
                service.RemoteMethods[action.Name] = action.Clone();
            }
            return service;
        }

        public static object CreateObjectInstance(System.Type ObjectType, string data)
        {
            MethodInfo method;
            ConstructorInfo constructor;
            object obj2 = null;
            System.Type[] types = new System.Type[] { System.Type.GetType("System.String") };
            object[] parameters = new object[1];
            if (data == null)
            {
                switch (ObjectType.FullName)
                {
                    case "System.Object":
                        return "";

                    case "System.String":
                        return "";

                    case "System.Boolean":
                        return false;

                    case "System.Byte[]":
                        return new byte[0];

                    case "System.Char":
                        return '\0';

                    case "System.UInt16":
                        return (ushort) 0;

                    case "System.UInt32":
                        return (uint) 0; //MOD

                    case "System.Int32":
                        return 0;

                    case "System.Int16":
                        return (short) 0;

                    case "System.Long":
                        return 0L;

                    case "System.Double":
                        return 0.0;

                    case "System.Single":
                        return 0f;

                    case "System.Byte":
                        return (byte) 0;

                    case "System.SByte":
                        return (sbyte) 0;

                    case "System.DateTime":
                        return DateTime.Now;

                    case "System.Uri":
                        return new Uri("http://127.0.0.1/");
                }
                method = ObjectType.GetMethod("Parse", types);
                if (method != null)
                {
                    return method.Invoke(null, parameters);
                }
                constructor = ObjectType.GetConstructor(types);
                if (constructor != null)
                {
                    parameters[0] = "";
                    try
                    {
                        obj2 = constructor.Invoke(parameters);
                    }
                    catch (Exception)
                    {
                        throw new Exception("Could not instantiate " + ObjectType.FullName);
                    }
                    return obj2;
                }
                constructor = ObjectType.GetConstructor(System.Type.EmptyTypes);
                if (constructor != null)
                {
                    try
                    {
                        obj2 = constructor.Invoke(new object[0]);
                    }
                    catch (Exception)
                    {
                        throw new Exception("Could not instantiate " + ObjectType.FullName);
                    }
                }
                return obj2;
            }
            switch (ObjectType.FullName)
            {
                case "System.Byte[]":
                    return Base64.Decode(data);

                case "System.Uri":
                    if (data == "")
                    {
                        obj2 = null;
                    }
                    else
                    {
                        obj2 = new Uri(data);
                    }
                    return obj2;

                case "System.String":
                    return data;

                case "System.Object":
                    return data;

                case "System.Boolean":
                    if (((data == "True") || (data == "true")) || (data == "1"))
                    {
                        return true;
                    }
                    if ((!(data == "False") && !(data == "false")) && !(data == "0"))
                    {
                        throw new UPnPCustomException(0x192, data + " is not a valid Boolean");
                    }
                    return false;
            }
            parameters[0] = data;
            method = ObjectType.GetMethod("Parse", types);
            if (method != null)
            {
                try
                {
                    obj2 = method.Invoke(null, parameters);
                }
                catch (Exception)
                {
                    throw new UPnPTypeMismatchException("Invalid value: " + data);
                }
                return obj2;
            }
            constructor = ObjectType.GetConstructor(types);
            if (constructor == null)
            {
                throw new UPnPTypeMismatchException("Cannot instantiate " + ObjectType.FullName);
            }
            try
            {
                obj2 = constructor.Invoke(parameters);
            }
            catch (Exception)
            {
                throw new UPnPTypeMismatchException("Invalid value: " + data);
            }
            return obj2;
        }

        public void DelayedInvokeResponse(long id, object RetArg, UPnPArgument[] OutArgs, UPnPCustomException e)
        {
            UPnPDevice.InvokerInfoStruct struct2 = (UPnPDevice.InvokerInfoStruct) this.DelayedResponseTable[id];
            HTTPMessage packet = new HTTPMessage();
            if (e != null)
            {
                packet.StatusCode = 500;
                packet.StatusData = "Internal";
                packet.StringBuffer = this.ParentDevice.BuildErrorBody(e);
            }
            else
            {
                packet = this.ParentDevice.ParseInvokeResponse(struct2.MethodName, struct2.SOAPAction, this.ServiceURN, RetArg, OutArgs);
            }
            struct2.WebSession.Send(packet);
            struct2.WebSession.StartReading();
            lock (this.DelayedResponseTable)
            {
                this.DelayedResponseTable.Remove(id);
            }
        }

        public void DelayInvokeRespose(long ID, out UPnPArgument[] OutArgs)
        {
            UPnPDevice.InvokerInfoStruct struct2 = (UPnPDevice.InvokerInfoStruct) this.ParentDevice.InvokerInfo[Thread.CurrentThread.GetHashCode()];
            OutArgs = struct2.OutArgs;
            lock (this.DelayedResponseTable)
            {
                this.DelayedResponseTable[ID] = struct2;
            }
        }

        public void Dispose()
        {
            if (this.CurrentSID != "")
            {
                this.UnSubscribe(null);
            }
        }

        public static UPnPService FromSCPD(string SCPDXML)
        {
            UPnPService service = new UPnPService(1.0);
            service.ParseSCPD(SCPDXML);
            return service;
        }

        public UPnPAction GetAction(string ActionName)
        {
            if (!this.RemoteMethods.ContainsKey(ActionName))
            {
                return null;
            }
            return (UPnPAction) this.RemoteMethods[ActionName];
        }

        public UPnPAction[] GetActions()
        {
            UPnPAction[] actionArray = new UPnPAction[this.RemoteMethods.Count];
            IDictionaryEnumerator enumerator = this.RemoteMethods.GetEnumerator();
            for (int i = 0; enumerator.MoveNext(); i++)
            {
                actionArray[i] = (UPnPAction) enumerator.Value;
            }
            return actionArray;
        }

        private UPnPStateVariable GetAssociatedVar(string ActionName, string Arg)
        {
            Hashtable hashtable = (Hashtable) this.VarAssociation[ActionName];
            if (hashtable == null)
            {
                return null;
            }
            return (UPnPStateVariable) hashtable[Arg];
        }

        public IPEndPoint GetCaller()
        {
            UPnPDevice.InvokerInfoStruct struct2 = (UPnPDevice.InvokerInfoStruct) this.ParentDevice.InvokerInfo[Thread.CurrentThread.GetHashCode()];
            return struct2.WebSession.Remote;
        }

        public string GetComplexSchemaForNamespace(string ns)
        {
            MemoryStream w = new MemoryStream();
            XmlTextWriter x = new XmlTextWriter(w, Encoding.UTF8);
            ArrayList list = (ArrayList) this.ComplexType_NamespaceTables[ns];
            if (list != null)
            {
                x.WriteStartDocument();
                x.WriteStartElement("xs", "schema", "http://www.w3.org/2001/XMLSchema");
                x.WriteAttributeString("targetNamespace", ns);
                x.WriteAttributeString("xmlns", ns);
                foreach (UPnPComplexType type in list)
                {
                    type.GetComplexTypeSchemaPart(x);
                }
                x.WriteEndElement();
                x.WriteEndDocument();
                x.Flush();
                string str = new UTF8Encoding().GetString(w.GetBuffer(), 3, ((int) w.Length) - 3);
                x.Close();
                return str;
            }
            return null;
        }

        public UPnPComplexType GetComplexType(string ns, string localName)
        {
            return (UPnPComplexType) this.ComplexTypeTable[localName + ":" + ns];
        }

        public UPnPComplexType[] GetComplexTypeList()
        {
            int index = 0;
            IDictionaryEnumerator enumerator = this.ComplexTypeTable.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (enumerator.Value.GetType() == typeof(UPnPComplexType))
                {
                    index++;
                }
            }
            UPnPComplexType[] typeArray = new UPnPComplexType[index];
            enumerator.Reset();
            index = 0;
            while (enumerator.MoveNext())
            {
                if (enumerator.Value.GetType() == typeof(UPnPComplexType))
                {
                    typeArray[index] = (UPnPComplexType) enumerator.Value;
                    index++;
                }
            }
            return typeArray;
        }

        public UPnPComplexType.Group[] GetComplexTypeList_Group()
        {
            int index = 0;
            IDictionaryEnumerator enumerator = this.ComplexTypeTable.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (enumerator.Value.GetType() == typeof(UPnPComplexType.Group))
                {
                    index++;
                }
            }
            UPnPComplexType.Group[] groupArray = new UPnPComplexType.Group[index];
            enumerator.Reset();
            index = 0;
            while (enumerator.MoveNext())
            {
                if (enumerator.Value.GetType() == typeof(UPnPComplexType.Group))
                {
                    groupArray[index] = (UPnPComplexType.Group) enumerator.Value;
                    index++;
                }
            }
            return groupArray;
        }

        private string GetNewSID()
        {
            lock (this.SIDLock)
            {
                this.EventSID++;
                return ("uuid:" + this.ParentDevice.UniqueDeviceName + "-" + this.ServiceID + "-" + this.EventSID.ToString());
            }
        }

        public IPEndPoint GetReceiver()
        {
            UPnPDevice.InvokerInfoStruct struct2 = (UPnPDevice.InvokerInfoStruct) this.ParentDevice.InvokerInfo[Thread.CurrentThread.GetHashCode()];
            return struct2.WebSession.Source;
        }

        public string[] GetSchemaNamespaces()
        {
            ArrayList list = new ArrayList();
            IDictionaryEnumerator enumerator = this.ComplexType_NamespaceTables.GetEnumerator();
            while (enumerator.MoveNext())
            {
                list.Add(enumerator.Key.ToString());
            }
            return (string[]) list.ToArray(typeof(string));
        }

        public byte[] GetSCPDXml()
        {
            IDictionaryEnumerator enumerator = this.StateVariables.GetEnumerator();
            MemoryStream w = new MemoryStream();
            XmlTextWriter xDoc = new XmlTextWriter(w, Encoding.UTF8);
            xDoc.Formatting = Formatting.Indented;
            xDoc.Indentation = 3;
            xDoc.WriteStartDocument();
            xDoc.WriteStartElement("scpd", "urn:schemas-upnp-org:service-1-0");
            IDictionaryEnumerator enumerator2 = this.ComplexType_NamespacePrefix.GetEnumerator();
            while (enumerator2.MoveNext())
            {
                xDoc.WriteAttributeString("xmlns", enumerator2.Value.ToString(), null, enumerator2.Key.ToString());
                xDoc.WriteAttributeString(enumerator2.Value.ToString(), "schemaLocation", null, this.SchemaURLS[enumerator2.Key.ToString()].ToString());
            }
            xDoc.WriteStartElement("specVersion");
            DText text = new DText();
            text.ATTRMARK = ".";
            if (this.ParentDevice == null)
            {
                if (this.ComplexTypeTable.Count == 0)
                {
                    text[0] = "1.0";
                }
                else
                {
                    text[0] = "1.1";
                }
            }
            else
            {
                text[0] = this.ParentDevice.ArchitectureVersion;
            }
            xDoc.WriteElementString("major", text[1]);
            xDoc.WriteElementString("minor", text[2]);
            xDoc.WriteEndElement();
            xDoc.WriteStartElement("actionList");
            foreach (UPnPAction action in this.Actions)
            {
                action.GetXML(xDoc);
            }
            xDoc.WriteEndElement();
            xDoc.WriteStartElement("serviceStateTable");
            while (enumerator.MoveNext())
            {
                ((UPnPStateVariable) enumerator.Value).GetStateVariableXML(xDoc);
            }
            xDoc.WriteEndElement();
            xDoc.WriteEndElement();
            xDoc.WriteEndDocument();
            xDoc.Flush();
            byte[] buffer = new byte[w.Length - 3L];
            w.Seek(3L, SeekOrigin.Begin);
            w.Read(buffer, 0, buffer.Length);
            xDoc.Close();
            return buffer;
        }

        public void GetServiceXML(XmlTextWriter XDoc)
        {
            XDoc.WriteStartElement("service");
            XDoc.WriteElementString("serviceType", this.ServiceURN);
            XDoc.WriteElementString("serviceId", this.ServiceID);
            XDoc.WriteElementString("SCPDURL", this.SCPDURL);
            XDoc.WriteElementString("controlURL", this.__controlurl);
            XDoc.WriteElementString("eventSubURL", this.__eventurl);
            XDoc.WriteEndElement();
        }

        public object GetStateVariable(string VarName)
        {
            object obj2 = this.StateVariables[VarName];
            if (obj2 == null)
            {
                return null;
            }
            return ((UPnPStateVariable) obj2).Value;
        }

        public UPnPStateVariable GetStateVariableObject(string VarName)
        {
            object obj2 = this.StateVariables[VarName];
            if (obj2 == null)
            {
                return null;
            }
            return (UPnPStateVariable) obj2;
        }

        public UPnPStateVariable GetStateVariableObject(string MethodName, string ArgName)
        {
            UPnPStateVariable[] stateVariables = this.GetStateVariables();
            UPnPStateVariable variable = null;
            foreach (UPnPStateVariable variable2 in stateVariables)
            {
                foreach (UPnPStateVariable.AssociationNode node in variable2.GetAssociations())
                {
                    if ((node.ActionName == MethodName) && (node.ArgName == ArgName))
                    {
                        variable = variable2;
                        break;
                    }
                }
                if (variable != null)
                {
                    return variable;
                }
            }
            return variable;
        }

        public UPnPStateVariable[] GetStateVariables()
        {
            UPnPStateVariable[] variableArray = new UPnPStateVariable[this.StateVariables.Count];
            if (this.StateVariables.Count != 0)
            {
                IDictionaryEnumerator enumerator = this.StateVariables.GetEnumerator();
                for (int i = 0; enumerator.MoveNext(); i++)
                {
                    variableArray[i] = (UPnPStateVariable) enumerator.Value;
                }
            }
            return variableArray;
        }

        private System.Type GetTypeFromUnknown(string TypeName)
        {
            System.Type type2 = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            System.Type type = null;
            try
            {
                type2 = System.Type.GetType(TypeName, true);
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

        private HTTPSession GetWebSession()
        {
            UPnPDevice.InvokerInfoStruct struct2 = (UPnPDevice.InvokerInfoStruct) this.ParentDevice.InvokerInfo[Thread.CurrentThread.GetHashCode()];
            return struct2.WebSession;
        }

        private void HandleInvokeRequest(HTTPRequest sender, HTTPMessage response, object Tag)
        {
            AsyncInvokeInfo info = (AsyncInvokeInfo) Tag;
            if (response == null)
            {
                if (info.ErrorCB != null)
                {
                    info.ErrorCB(this, info.MethodName, info.Args, new UPnPInvokeException(info.MethodName, info.Args, "Could not connect to device"), info.Tag);
                }
                else if (this.OnInvokeError != null)
                {
                    this.OnInvokeError(this, info.MethodName, info.Args, new UPnPInvokeException(info.MethodName, info.Args, "Could not connect to device"), info.Tag);
                }
            }
            else if (response.StatusCode != 100)
            {
                UPnPAction action = (UPnPAction) this.RemoteMethods[info.MethodName];
                if (response.StatusCode != 200)
                {
                    if ((this.OnInvokeError != null) || (info.ErrorCB != null))
                    {
                        if ((response.StatusCode == 500) && (response.BodyBuffer.Length > 0))
                        {
                            UPnPCustomException e = null;
                            try
                            {
                                e = this.ParseErrorBody(response.StringBuffer);
                                EventLogger.Log(this, EventLogEntryType.Error, "UPnP Action <" + info.MethodName + "> Error [" + e.ErrorCode.ToString() + "] " + e.ErrorDescription);
                            }
                            catch
                            {
                                e = null;
                                EventLogger.Log(this, EventLogEntryType.Error, "HTTP Fault invoking " + info.MethodName + " : " + response.StatusData);
                            }
                            if (info.ErrorCB != null)
                            {
                                info.ErrorCB(this, action.Name, info.Args, new UPnPInvokeException(info.MethodName, info.Args, response.StatusData, e), info.Tag);
                            }
                            else if (this.OnInvokeError != null)
                            {
                                this.OnInvokeError(this, action.Name, info.Args, new UPnPInvokeException(info.MethodName, info.Args, response.StatusData, e), info.Tag);
                            }
                        }
                        else if (info.ErrorCB != null)
                        {
                            info.ErrorCB(this, action.Name, info.Args, new UPnPInvokeException(info.MethodName, info.Args, response.StatusData), info.Tag);
                        }
                        else if (this.OnInvokeError != null)
                        {
                            this.OnInvokeError(this, action.Name, info.Args, new UPnPInvokeException(info.MethodName, info.Args, response.StatusData), info.Tag);
                        }
                    }
                }
                else
                {
                    StringReader input = new StringReader(response.StringBuffer.Trim());
                    XmlTextReader reader2 = new XmlTextReader(input);
                    string localName = "";
                    ArrayList list = new ArrayList();
                    reader2.Read();
                    reader2.MoveToContent();
                    if (reader2.LocalName == "Envelope")
                    {
                        reader2.Read();
                        reader2.MoveToContent();
                        if (reader2.LocalName == "Body")
                        {
                            reader2.Read();
                            reader2.MoveToContent();
                            localName = reader2.LocalName;
                            reader2.Read();
                            reader2.MoveToContent();
                            if (reader2.LocalName != "Body")
                            {
                                while ((reader2.LocalName != localName) && !reader2.EOF)
                                {
                                    UPnPArgument argument = new UPnPArgument(reader2.Name, reader2.ReadString());
                                    list.Add(argument);
                                    if ((!reader2.IsStartElement() && (reader2.LocalName != localName)) || reader2.IsEmptyElement)
                                    {
                                        reader2.Read();
                                        reader2.MoveToContent();
                                    }
                                }
                            }
                        }
                    }
                    object returnValue = null;
                    UPnPArgument[] args = info.Args;
                    object[] objArray = new object[1];
                    System.Type[] typeArray = new System.Type[] { System.Type.GetType("System.String") };
                    int num = 0;
                    if (((UPnPAction) this.RemoteMethods[info.MethodName]).HasReturnValue)
                    {
                        returnValue = CreateObjectInstance(action.GetArg(((UPnPArgument) list[0]).Name).RelatedStateVar.GetNetType(), (string) ((UPnPArgument) list[0]).DataValue);
                        num = 1;
                    }
                    for (int i = num; i < list.Count; i++)
                    {
                        for (int j = 0; j < args.Length; j++)
                        {
                            if (args[j].Name == ((UPnPArgument) list[i]).Name)
                            {
                                UPnPArgument arg = action.GetArg(args[j].Name);
                                if ((arg.RelatedStateVar.GetNetType().FullName != "System.String") || (arg.RelatedStateVar.GetNetType().FullName != "System.Object"))
                                {
                                    args[j].DataValue = CreateObjectInstance(arg.RelatedStateVar.GetNetType(), (string) ((UPnPArgument) list[i]).DataValue);
                                }
                                else
                                {
                                    args[j].DataValue = ((UPnPArgument) list[i]).DataValue;
                                }
                                args[j].DataType = arg.RelatedStateVar.GetNetType().FullName;
                                break;
                            }
                        }
                    }
                    if (info.InvokeCB != null)
                    {
                        info.InvokeCB(this, info.MethodName, args, returnValue, info.Tag);
                    }
                    else if (this.OnInvokeResponse != null)
                    {
                        this.OnInvokeResponse(this, info.MethodName, args, returnValue, info.Tag);
                    }
                }
            }
        }

        private void HandleSendEvent(HTTPRequest R, HTTPMessage M, object Tag)
        {
            R.Dispose();
            this.SendEventTable.Remove(R);
        }

        private void HandleSubscribeResponse(HTTPRequest sender, HTTPMessage response, object Tag)
        {
            UPnPEventSubscribeHandler handler = (UPnPEventSubscribeHandler) Tag;
            this.SubscribeRequestTable.Remove(sender);
            sender.Dispose();
            if (response != null)
            {
                if (response.StatusCode != 200)
                {
                    if (handler != null)
                    {
                        handler(this, false);
                    }
                    else if (this.OnSubscribe != null)
                    {
                        this.OnSubscribe(this, false);
                    }
                }
                else
                {
                    this.CurrentSID = response.GetTag("SID");
                    if (handler != null)
                    {
                        handler(this, true);
                    }
                    else if (this.OnSubscribe != null)
                    {
                        this.OnSubscribe(this, true);
                    }
                    if (this.CurrentTimeout != 0)
                    {
                        EventLogger.Log(this, EventLogEntryType.SuccessAudit, "SUBSCRIBE [" + this.CurrentSID + "] Duration: " + this.CurrentTimeout.ToString() + " <" + DateTime.Now.ToLongTimeString() + ">");
                        SubscribeCycle.Add(this.GetHashCode(), (int) (this.CurrentTimeout / 2));
                    }
                }
            }
            else if (handler != null)
            {
                handler(this, false);
            }
            else if (this.OnSubscribe != null)
            {
                this.OnSubscribe(this, false);
            }
        }

        private void HandleUnsubscribeResponse(HTTPRequest sender, HTTPMessage response, object Tag)
        {
            this.SubscribeRequestTable.Remove(sender);
            sender.Dispose();
            SubscribeCycle.Remove(this.GetHashCode());
        }

        public void InvokeAsync(string MethodName, UPnPArgument[] InVarArr)
        {
            this.InvokeAsync(MethodName, InVarArr, null, null, null);
        }

        public void InvokeAsync(string MethodName, UPnPArgument[] InVarArr, object Tag, UPnPServiceInvokeHandler InvokeCallback, UPnPServiceInvokeErrorHandler ErrorCallback)
        {
            string str;
            string str2;
            int num;
            HTTPMessage mSG = new HTTPMessage();
            if (InVarArr == null)
            {
                InVarArr = new UPnPArgument[0];
            }
            UPnPAction action = (UPnPAction) this.RemoteMethods[MethodName];
            if (action == null)
            {
                throw new UPnPInvokeException(MethodName, InVarArr, MethodName + " is not currently defined in this object");
            }
            action.ValidateArgs(InVarArr);
            SSDP.ParseURL(this.__controlurl, out str, out num, out str2);
            IPEndPoint dest = new IPEndPoint(IPAddress.Parse(str), num);
            mSG.Directive = "POST";
            mSG.DirectiveObj = str2;
            mSG.AddTag("Host", str + ":" + num);
            mSG.AddTag("Content-Type", "text/xml ; charset=\"utf-8\"");
            mSG.AddTag("SoapAction", "\"" + this.ServiceURN + "#" + MethodName + "\"");
            MemoryStream w = new MemoryStream(0x1000);
            XmlTextWriter writer = new XmlTextWriter(w, Encoding.UTF8);
            writer.Formatting = Formatting.Indented;
            writer.Indentation = 3;
            writer.WriteStartDocument();
            string ns = "http://schemas.xmlsoap.org/soap/envelope/";
            writer.WriteStartElement("s", "Envelope", ns);
            writer.WriteAttributeString("s", "encodingStyle", ns, "http://schemas.xmlsoap.org/soap/encoding/");
            writer.WriteStartElement("s", "Body", ns);
            writer.WriteStartElement("u", MethodName, this.ServiceURN);
            for (int i = 0; i < InVarArr.Length; i++)
            {
                if (action.GetArg(InVarArr[i].Name).Direction == "in")
                {
                    writer.WriteElementString(InVarArr[i].Name, SerializeObjectInstance(InVarArr[i].DataValue));
                }
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
            byte[] buffer = new byte[w.Length - 3L];
            w.Seek(3L, SeekOrigin.Begin);
            w.Read(buffer, 0, buffer.Length);
            writer.Close();
            mSG.BodyBuffer = buffer;
            AsyncInvokeInfo tag = new AsyncInvokeInfo();
            tag.Args = InVarArr;
            tag.MethodName = MethodName;
            tag.Packet = mSG;
            tag.Tag = Tag;
            tag.InvokeCB = InvokeCallback;
            tag.ErrorCB = ErrorCallback;
            this.InvocationPipeline.PipelineRequest(dest, mSG, tag);
        }

        internal object InvokeLocal(string MethodName, ref ArrayList VarList)
        {
            if (MethodName == "QueryStateVariable")
            {
                UPnPArgument argument = (UPnPArgument) VarList[0];
                UPnPStateVariable variable = (UPnPStateVariable) this.StateVariables[argument.DataValue];
                if (variable == null)
                {
                    throw new UPnPCustomException(0x192, "Invalid Args: " + argument.DataValue);
                }
                return variable.Value;
            }
            foreach (UPnPArgument argument2 in VarList)
            {
                UPnPStateVariable stateVariableObject = this.GetStateVariableObject(MethodName, argument2.Name);
                try
                {
                    stateVariableObject.Validate(CreateObjectInstance(stateVariableObject.GetNetType(), (string) argument2.DataValue));
                    continue;
                }
                catch (Exception exception)
                {
                    throw new UPnPCustomException(0x192, "Argument [" + argument2.Name + "] : " + exception.Message);
                }
            }
            UPnPAction sender = this.GetAction(MethodName);
            if (sender == null)
            {
                throw new UPnPCustomException(0x191, "Invalid Action: " + MethodName);
            }
            if (sender.SpecialCase != null)
            {
                UPnPArgument[] argumentArray;
                object obj2;
                foreach (UPnPArgument argument3 in VarList)
                {
                    argument3.DataValue = CreateObjectInstance(sender.GetArg(argument3.Name).RelatedStateVar.GetNetType(), (string) argument3.DataValue);
                }
                sender.SpecialCase(sender, (UPnPArgument[]) VarList.ToArray(typeof(UPnPArgument)), out obj2, out argumentArray);
                VarList.Clear();
                foreach (UPnPArgument argument4 in argumentArray)
                {
                    VarList.Add(argument4);
                }
                if (sender.HasReturnValue)
                {
                    UPnPArgument argument5 = new UPnPArgument(sender.GetRetArg().Name, obj2);
                    argument5.IsReturnValue = true;
                    obj2 = argument5;
                }
                return obj2;
            }
            MethodInfo methodPointer = sender.MethodPointer;
            ParameterInfo[] parameters = methodPointer.GetParameters();
            object[] objArray = new object[parameters.Length];
            for (int i = 0; i < objArray.Length; i++)
            {
                if ((parameters[i].ParameterType.ToString() != "System.String") && (parameters[i].ParameterType.ToString() != "System.String&"))
                {
                    string typeName = parameters[i].ParameterType.ToString();
                    if (typeName.EndsWith("&"))
                    {
                        typeName = typeName.Substring(0, typeName.Length - 1);
                    }
                    System.Type typeFromUnknown = this.GetTypeFromUnknown(typeName);
                    objArray[i] = CreateObjectInstance(typeFromUnknown, null);
                }
                else
                {
                    objArray[i] = "";
                }
            }
            for (int j = 0; j < VarList.Count; j++)
            {
                for (int m = 0; m < parameters.Length; m++)
                {
                    if (parameters[m].Name == ((UPnPArgument) VarList[j]).Name)
                    {
                        if ((parameters[m].ParameterType.ToString() != "System.String") && (parameters[m].ParameterType.ToString() != "System.String&"))
                        {
                            objArray[m] = CreateObjectInstance(parameters[m].ParameterType, (string) ((UPnPArgument) VarList[j]).DataValue);
                        }
                        else
                        {
                            objArray[m] = ((UPnPArgument) VarList[j]).DataValue;
                        }
                        break;
                    }
                }
            }
            object val = null;
            val = methodPointer.Invoke(this.ServiceInstance, objArray);
            VarList.Clear();
            for (int k = 0; k < objArray.Length; k++)
            {
                if (parameters[k].Attributes == ParameterAttributes.Out)
                {
                    VarList.Add(new UPnPArgument(parameters[k].Name, objArray[k]));
                }
            }
            if (sender.HasReturnValue)
            {
                UPnPArgument argument6 = new UPnPArgument(sender.GetRetArg().Name, val);
                argument6.IsReturnValue = true;
                val = argument6;
            }
            return val;
        }

        public object InvokeSync(string MethodName, UPnPArgument[] InVarArr)
        {
            SyncInvokeAdapter adapter = new SyncInvokeAdapter();
            this.InvokeAsync(MethodName, InVarArr, null, adapter.InvokeHandler, adapter.InvokeErrorHandler);
            adapter.Result.WaitOne();
            if (adapter.InvokeException != null)
            {
                throw adapter.InvokeException;
            }
            for (int i = 0; i < InVarArr.Length; i++)
            {
                InVarArr[i] = adapter.Arguments[i];
            }
            return adapter.ReturnValue;
        }

        internal bool IsYourEvent(string MySID)
        {
            return (this.CurrentSID == MySID);
        }

        internal static UPnPService Parse(string XML)
        {
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            UPnPService service = new UPnPService(1.0);
            reader2.Read();
            reader2.MoveToContent();
            if (reader2.LocalName == "service")
            {
                if (reader2.AttributeCount > 0)
                {
                    for (int i = 0; i < reader2.AttributeCount; i++)
                    {
                        reader2.MoveToAttribute(i);
                        if (reader2.LocalName == "MaxVersion")
                        {
                            service.SetVersion(reader2.Value);
                        }
                    }
                    reader2.MoveToContent();
                    reader2.Read();
                }
                else
                {
                    reader2.Read();
                    reader2.MoveToContent();
                }
                while (reader2.LocalName != "service")
                {
                    switch (reader2.LocalName)
                    {
                        case "serviceType":
                            service.ServiceURN = reader2.ReadString();
                            goto Label_0148;

                        case "serviceId":
                            service.ServiceID = reader2.ReadString();
                            goto Label_0148;

                        case "SCPDURL":
                            service.SCPDURL = reader2.ReadString();
                            goto Label_0148;

                        case "controlURL":
                            service.ControlURL = reader2.ReadString();
                            break;

                        case "eventSubURL":
                            service.EventURL = reader2.ReadString();
                            break;
                    }
                Label_0148:
                    reader2.Read();
                    reader2.MoveToContent();
                }
            }
            return service;
        }

        private void ParseActionXml(string XML)
        {
            UPnPAction action = new UPnPAction();
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            reader2.Read();
            reader2.MoveToContent();
            reader2.Read();
            reader2.MoveToContent();
            while (reader2.LocalName != "action")
            {
                string localName = reader2.LocalName;
                if (localName != null)
                {
                    localName = string.IsInterned(localName);
                    if (localName == "name")
                    {
                        action.Name = reader2.ReadString().Trim();
                    }
                    else if ((localName == "argumentList") && !reader2.IsEmptyElement)
                    {
                        reader2.Read();
                        reader2.MoveToContent();
                        while ((reader2.LocalName != "argumentList") && !reader2.EOF)
                        {
                            if (reader2.LocalName == "argument")
                            {
                                UPnPArgument arg = new UPnPArgument();
                                reader2.Read();
                                reader2.MoveToContent();
                                while (reader2.LocalName != "argument")
                                {
                                    switch (reader2.LocalName)
                                    {
                                        case "name":
                                            arg.Name = reader2.ReadString().Trim();
                                            goto Label_0165;

                                        case "retval":
                                            arg.IsReturnValue = true;
                                            goto Label_0165;

                                        case "direction":
                                            arg.Direction = reader2.ReadString().Trim();
                                            break;

                                        case "relatedStateVariable":
                                            arg.StateVarName = reader2.ReadString().Trim();
                                            break;
                                    }
                                Label_0165:
                                    reader2.Read();
                                    reader2.MoveToContent();
                                }
                                action.AddArgument(arg);
                                reader2.Read();
                                reader2.MoveToContent();
                            }
                            else
                            {
                                reader2.Skip();
                            }
                        }
                    }
                }
                reader2.Read();
                reader2.MoveToContent();
            }
            this.AddAction(action);
        }

        private UPnPCustomException ParseErrorBody(string XML)
        {
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            UPnPCustomException exception = null;
            int num = 0;
            string str = "";
            reader2.Read();
            reader2.MoveToContent();
            if (reader2.LocalName == "Envelope")
            {
                reader2.Read();
                reader2.MoveToContent();
                while ((reader2.LocalName != "Envelope") && !reader2.EOF)
                {
                    string str2;
                    if (((str2 = reader2.LocalName) != null) && (string.IsInterned(str2) == "Body"))
                    {
                        reader2.Read();
                        reader2.MoveToContent();
                        while ((reader2.LocalName != "Body") && !reader2.EOF)
                        {
                            if (reader2.LocalName == "Fault")
                            {
                                reader2.Read();
                                reader2.MoveToContent();
                                while ((reader2.LocalName != "Fault") && !reader2.EOF)
                                {
                                    string str3;
                                    if (((str3 = reader2.LocalName) != null) && (string.IsInterned(str3) == "detail"))
                                    {
                                        reader2.Read();
                                        reader2.MoveToContent();
                                        while ((reader2.LocalName != "detail") && !reader2.EOF)
                                        {
                                            if (reader2.LocalName == "UPnPError")
                                            {
                                                reader2.Read();
                                                reader2.MoveToContent();
                                                while ((reader2.LocalName != "UPnPError") && !reader2.EOF)
                                                {
                                                    switch (reader2.LocalName)
                                                    {
                                                        case "errorCode":
                                                            num = int.Parse(reader2.ReadString());
                                                            break;

                                                        case "errorDescription":
                                                            str = reader2.ReadString();
                                                            break;
                                                    }
                                                    reader2.Read();
                                                    reader2.MoveToContent();
                                                }
                                                exception = new UPnPCustomException(num, str);
                                            }
                                            else
                                            {
                                                reader2.Skip();
                                            }
                                            reader2.Read();
                                            reader2.MoveToContent();
                                        }
                                    }
                                    else
                                    {
                                        reader2.Skip();
                                    }
                                    reader2.Read();
                                    reader2.MoveToContent();
                                }
                            }
                            else
                            {
                                reader2.Skip();
                            }
                            reader2.Read();
                            reader2.MoveToContent();
                        }
                    }
                    else
                    {
                        reader2.Skip();
                    }
                    reader2.Read();
                    reader2.MoveToContent();
                }
            }
            return exception;
        }

        private void ParseEvents(string XML)
        {
            StringReader input = new StringReader(XML);
            XmlTextReader reader2 = new XmlTextReader(input);
            Hashtable hashtable = new Hashtable();
            reader2.Read();
            reader2.MoveToContent();
            if (reader2.LocalName == "propertyset")
            {
                reader2.Read();
                reader2.MoveToContent();
                while ((reader2.LocalName != "propertyset") && !reader2.EOF)
                {
                    if (reader2.LocalName == "property")
                    {
                        reader2.Read();
                        reader2.MoveToContent();
                    }
                    string localName = reader2.LocalName;
                    string data = reader2.ReadString();
                    if (this.StateVariables.ContainsKey(localName))
                    {
                        UPnPStateVariable variable = (UPnPStateVariable) this.StateVariables[localName];
                        try
                        {
                            object obj2 = CreateObjectInstance(variable.GetNetType(), data);
                            variable.Value = obj2;
                            EventLogger.Log(this, EventLogEntryType.SuccessAudit, obj2.ToString());
                            this.StateVariables[localName] = variable;
                        }
                        catch (Exception exception)
                        {
                            EventLogger.Log(exception);
                        }
                    }
                    reader2.Read();
                    reader2.MoveToContent();
                    if ((reader2.LocalName == "property") && !reader2.IsStartElement())
                    {
                        reader2.Read();
                        reader2.MoveToContent();
                    }
                }
            }
        }

        private Uri[] ParseEventURL(string URLList)
        {
            DText text = new DText();
            ArrayList list = new ArrayList();
            text.ATTRMARK = ">";
            text[0] = URLList;
            int num = text.DCOUNT();
            for (int i = 1; i <= num; i++)
            {
                string uriString = text[i];
                try
                {
                    uriString = uriString.Substring(uriString.IndexOf("<") + 1);
                    list.Add(new Uri(uriString));
                }
                catch (Exception)
                {
                }
            }
            Uri[] uriArray = new Uri[list.Count];
            for (int j = 0; j < uriArray.Length; j++)
            {
                uriArray[j] = (Uri) list[j];
            }
            return uriArray;
        }

        internal void ParseSCPD(string XML)
        {
            bool flag = false;
            string str = "";
            if (XML != "")
            {
                string evented = "no";
                string multicasted = "no";
                StringReader input = new StringReader(XML);
                XmlTextReader xMLDoc = new XmlTextReader(input);
                xMLDoc.Read();
                xMLDoc.MoveToContent();
                if (xMLDoc.LocalName == "scpd")
                {
                    if (xMLDoc.HasAttributes)
                    {
                        for (int i = 0; i < xMLDoc.AttributeCount; i++)
                        {
                            xMLDoc.MoveToAttribute(i);
                            if (xMLDoc.Prefix == "xmlns")
                            {
                                flag = true;
                                str = xMLDoc.Value;
                            }
                        }
                        xMLDoc.MoveToElement();
                        if (flag)
                        {
                            OpenFileDialog dialog = new OpenFileDialog();
                            dialog.Multiselect = false;
                            dialog.Title = str;
                            if (dialog.ShowDialog() == DialogResult.OK)
                            {
                                FileStream stream = (FileStream) dialog.OpenFile();
                                UTF8Encoding encoding = new UTF8Encoding();
                                byte[] buffer = new byte[(int) stream.Length];
                                stream.Read(buffer, 0, buffer.Length);
                                UPnPComplexType[] typeArray = UPnPComplexType.Parse(encoding.GetString(buffer));
                                stream.Close();
                                foreach (UPnPComplexType type in typeArray)
                                {
                                    this.AddComplexType(type);
                                }
                            }
                        }
                    }
                    xMLDoc.Read();
                    xMLDoc.MoveToContent();
                    while ((xMLDoc.LocalName != "scpd") && !xMLDoc.EOF)
                    {
                        if ((xMLDoc.LocalName == "actionList") && !xMLDoc.IsEmptyElement)
                        {
                            xMLDoc.Read();
                            xMLDoc.MoveToContent();
                            while ((xMLDoc.LocalName != "actionList") && !xMLDoc.EOF)
                            {
                                if (xMLDoc.LocalName == "action")
                                {
                                    this.ParseActionXml("<action>\r\n" + xMLDoc.ReadInnerXml() + "</action>");
                                }
                                if (!xMLDoc.IsStartElement() && (xMLDoc.LocalName != "actionList"))
                                {
                                    xMLDoc.Read();
                                    xMLDoc.MoveToContent();
                                }
                            }
                        }
                        else if (xMLDoc.LocalName == "serviceStateTable")
                        {
                            xMLDoc.Read();
                            xMLDoc.MoveToContent();
                            while ((xMLDoc.LocalName != "serviceStateTable") && !xMLDoc.EOF)
                            {
                                if (xMLDoc.LocalName == "stateVariable")
                                {
                                    evented = "no";
                                    multicasted = "no";
                                    xMLDoc.MoveToAttribute("sendEvents");
                                    if (xMLDoc.LocalName == "sendEvents")
                                    {
                                        evented = xMLDoc.GetAttribute("sendEvents");
                                    }
                                    xMLDoc.MoveToAttribute("multicast");
                                    if (xMLDoc.LocalName == "multicast")
                                    {
                                        multicasted = xMLDoc.GetAttribute("multicast");
                                    }
                                    xMLDoc.MoveToContent();
                                    this.ParseStateVarXml(evented, multicasted, xMLDoc);
                                }
                                if (!xMLDoc.IsStartElement() && (xMLDoc.LocalName != "serviceStateTable"))
                                {
                                    xMLDoc.Read();
                                    xMLDoc.MoveToContent();
                                }
                            }
                        }
                        else
                        {
                            xMLDoc.Skip();
                        }
                        if (!xMLDoc.IsStartElement())
                        {
                            xMLDoc.Read();
                            xMLDoc.MoveToContent();
                        }
                    }
                    foreach (UPnPAction action in this.Actions)
                    {
                        foreach (UPnPArgument argument in action.Arguments)
                        {
                            if (argument.RelatedStateVar == null)
                            {
                                throw new InvalidRelatedStateVariableException("Action: " + action.Name + " Arg: " + argument.Name + " Contains invalid reference: " + argument.StateVarName);
                            }
                            argument.RelatedStateVar.AddAssociation(action.Name, argument.Name);
                        }
                    }
                }
            }
        }

        private void ParseStateVarXml(string evented, string multicasted, XmlTextReader XMLDoc)
        {
            UPnPStateVariable variable;
            string str = "";
            string prefix = "";
            UPnPComplexType cT = null;
            DText text = new DText();
            text.ATTRMARK = ":";
            string data = null;
            string str4 = null;
            string str5 = null;
            bool flag = false;
            string varName = "";
            string str7 = "";
            string str8 = null;
            ArrayList list = new ArrayList();
            string str9 = "";
            string str10 = "";
            bool flag2 = false;
            while (!flag2 && XMLDoc.Read())
            {
                bool flag3;
                bool flag4;
                switch (XMLDoc.NodeType)
                {
                    case XmlNodeType.Element:
                        str9 = XMLDoc.LocalName;
                        break;

                    case XmlNodeType.Attribute:
                    {
                        continue;
                    }
                    case XmlNodeType.Text:
                    {
                        string str13 = str9;
                        if (str13 == null)
                        {
                            continue;
                        }
                        str13 = string.IsInterned(str13);
                        if (str13 == "name")
                        {
                            varName = XMLDoc.Value.Trim();
                        }
                        else
                        {
                            if (str13 == "dataType")
                            {
                                goto Label_030C;
                            }
                            if (str13 == "defaultValue")
                            {
                                goto Label_031B;
                            }
                        }
                        goto Label_0328;
                    }
                    case XmlNodeType.EndElement:
                    {
                        if (XMLDoc.LocalName == "stateVariable")
                        {
                            flag2 = true;
                        }
                        continue;
                    }
                    default:
                    {
                        continue;
                    }
                }
                string localName = XMLDoc.LocalName;
                if (localName != null)
                {
                    localName = string.IsInterned(localName);
                    if (localName == "dataType")
                    {
                        if (XMLDoc.HasAttributes)
                        {
                            for (int i = 0; i < XMLDoc.AttributeCount; i++)
                            {
                                XMLDoc.MoveToAttribute(i);
                                if (XMLDoc.LocalName == "type")
                                {
                                    text[0] = XMLDoc.Value;
                                    if (text.DCOUNT() == 1)
                                    {
                                        str = text[1];
                                    }
                                    else
                                    {
                                        str = text[2];
                                        prefix = text[1];
                                    }
                                    cT = (UPnPComplexType) this.ComplexTypeTable[str + ":" + XMLDoc.LookupNamespace(prefix)];
                                }
                            }
                        }
                    }
                    else
                    {
                        if (localName == "allowedValueList")
                        {
                            flag3 = false;
                            goto Label_01D5;
                        }
                        if (localName == "allowedValueRange")
                        {
                            goto Label_01E6;
                        }
                    }
                }
                continue;
            Label_0171:
                switch (XMLDoc.NodeType)
                {
                    case XmlNodeType.Element:
                        str10 = XMLDoc.LocalName;
                        goto Label_01D5;

                    case XmlNodeType.Attribute:
                        goto Label_01D5;

                    case XmlNodeType.Text:
                        if (str10 == "allowedValue")
                        {
                            list.Add(XMLDoc.Value);
                        }
                        goto Label_01D5;

                    case XmlNodeType.EndElement:
                        if (XMLDoc.LocalName == "allowedValueList")
                        {
                            flag3 = true;
                        }
                        goto Label_01D5;
                }
            Label_01D5:
                if (!flag3 && XMLDoc.Read())
                {
                    goto Label_0171;
                }
                continue;
            Label_01E6:
                flag4 = false;
                while (!flag4 && XMLDoc.Read())
                {
                    switch (XMLDoc.NodeType)
                    {
                        case XmlNodeType.Element:
                        {
                            str10 = XMLDoc.LocalName;
                            continue;
                        }
                        case XmlNodeType.Attribute:
                        {
                            continue;
                        }
                        case XmlNodeType.Text:
                        {
                            string str12 = str10;
                            if (str12 != null)
                            {
                                str12 = string.IsInterned(str12);
                                if (str12 == "minimum")
                                {
                                    data = XMLDoc.Value;
                                }
                                else
                                {
                                    if (str12 == "maximum")
                                    {
                                        break;
                                    }
                                    if (str12 == "step")
                                    {
                                        goto Label_0286;
                                    }
                                }
                            }
                            continue;
                        }
                        case XmlNodeType.EndElement:
                        {
                            if (XMLDoc.LocalName == "allowedValueRange")
                            {
                                flag4 = true;
                            }
                            continue;
                        }
                        default:
                        {
                            continue;
                        }
                    }
                    str4 = XMLDoc.Value;
                    continue;
                Label_0286:
                    str5 = XMLDoc.Value;
                }
                continue;
            Label_030C:
                str7 = XMLDoc.Value.Trim();
                goto Label_0328;
            Label_031B:
                str8 = XMLDoc.Value;
                flag = true;
            Label_0328:;
            }
            if (cT == null)
            {
                variable = new UPnPStateVariable(varName);
            }
            else
            {
                variable = new UPnPStateVariable(varName, cT);
            }
            variable.ParentService = this;
            if (evented == "yes")
            {
                variable.SendEvent = true;
            }
            if (multicasted == "yes")
            {
                variable.MulticastEvent = true;
            }
            variable.VarType = str7;
            if (list.Count > 0)
            {
                variable.AllowedStringValues = (string[]) list.ToArray(typeof(string));
            }
            if (flag)
            {
                variable.DefaultValue = CreateObjectInstance(variable.GetNetType(), str8);
            }
            if ((data != null) && (str4 != null))
            {
                object obj3;
                object obj4;
                object step = null;
                if (str5 != null)
                {
                    try
                    {
                        step = CreateObjectInstance(variable.GetNetType(), str5);
                    }
                    catch (Exception)
                    {
                        step = null;
                    }
                }
                try
                {
                    obj3 = CreateObjectInstance(variable.GetNetType(), data);
                }
                catch (Exception)
                {
                    obj3 = variable.GetNetType().GetField("MinValue").GetValue(null);
                }
                try
                {
                    obj4 = CreateObjectInstance(variable.GetNetType(), str4);
                }
                catch (Exception)
                {
                    obj4 = variable.GetNetType().GetField("MaxValue").GetValue(null);
                }
                variable.SetRange(obj3, obj4, step);
            }
            this.StateVariables[varName] = variable;
        }

        public void RemoveComplexType(UPnPComplexType t)
        {
            this.ComplexTypeTable.Remove(t.Name_LOCAL + ":" + t.Name_NAMESPACE);
            ((ArrayList) this.ComplexType_NamespaceTables[t.Name_NAMESPACE]).Remove(t);
            t.ParentService = null;
        }

        public void RemoveMethod(UPnPAction action)
        {
            UPnPAction action2 = this.GetAction(action.Name);
            if (action2 != null)
            {
                foreach (UPnPArgument argument in action2.ArgumentList)
                {
                    argument.RelatedStateVar.RemoveAssociation(action2.Name, argument.Name);
                    if (argument.RelatedStateVar.GetAssociations().Length == 0)
                    {
                        this.RemoveStateVariable(argument.RelatedStateVar);
                    }
                }
                this.RemoteMethods.Remove(action2.Name);
            }
        }

        public void RemoveMethod(string MethodName)
        {
            UPnPAction action = new UPnPAction();
            action.Name = MethodName;
            this.RemoveMethod(action);
        }

        public void RemoveStateVariable(UPnPStateVariable stateVariable)
        {
            foreach (UPnPStateVariable.AssociationNode node in stateVariable.GetAssociations())
            {
                try
                {
                    if (this.GetAction(node.ActionName).GetArg(node.ArgName).StateVarName == stateVariable.Name)
                    {
                        throw new UPnPStateVariable.CannotRemoveException("Associated with " + node.ActionName + ":" + node.ArgName);
                    }
                }
                catch (NullReferenceException)
                {
                }
            }
            this.StateVariables.Remove(stateVariable.Name);
        }

        private void Renew()
        {
            string str;
            int num;
            string str2;
            HTTPMessage mSG = new HTTPMessage();
            string tagData = "Second-" + this.CurrentTimeout.ToString();
            SSDP.ParseURL(this.__eventurl, out str, out num, out str2);
            IPEndPoint dest = new IPEndPoint(IPAddress.Parse(str), num);
            mSG.Directive = "SUBSCRIBE";
            mSG.DirectiveObj = str2;
            mSG.AddTag("Host", str + ":" + num.ToString());
            mSG.AddTag("SID", this.CurrentSID);
            mSG.AddTag("Timeout", tagData);
            HTTPRequest request = new HTTPRequest();
            request.OnResponse += new HTTPRequest.RequestHandler(this.RenewSink);
            this.SendEventTable[request] = request;
            request.PipelineRequest(dest, mSG, null);
        }

        private void RenewSink(HTTPRequest sender, HTTPMessage M, object Tag)
        {
            if (M != null)
            {
                if (M.StatusCode != 200)
                {
                    EventLogger.Log(this, EventLogEntryType.SuccessAudit, "Renewal [" + this.CurrentSID + "] Error:" + M.StatusCode.ToString() + " <" + DateTime.Now.ToLongTimeString() + ">");
                    SubscribeCycle.Remove(this.GetHashCode());
                    this.SubscribeCounter = 0;
                    this.PeriodicRenewFailedEvent.Fire(this);
                }
                else
                {
                    EventLogger.Log(this, EventLogEntryType.SuccessAudit, "Renewal [" + this.CurrentSID + "] OK <" + DateTime.Now.ToLongTimeString() + ">");
                    SubscribeCycle.Add(this.GetHashCode(), (int) (this.CurrentTimeout / 2));
                }
            }
            else
            {
                EventLogger.Log(this, EventLogEntryType.SuccessAudit, "Renewal [" + this.CurrentSID + "] DeviceError <" + DateTime.Now.ToLongTimeString() + ">");
                SubscribeCycle.Remove(this.GetHashCode());
                this.SubscribeCounter = 0;
                this.PeriodicRenewFailedEvent.Fire(this);
            }
            this.SendEventTable.Remove(sender);
            sender.Dispose();
        }

        internal void SendEvents(UPnPStateVariable V)
        {
            ICollection keys = this.SubscriberTable.Keys;
            string[] array = new string[keys.Count];
            keys.CopyTo(array, 0);
            for (int i = 0; i < array.Length; i++)
            {
                object obj2 = this.SubscriberTable[array[i]];
                if (obj2 != null)
                {
                    SubscriberInfo info = (SubscriberInfo) obj2;
                    if ((info.Expires > DateTime.Now.Ticks) || (info.Expires == -1L))
                    {
                        Uri[] uriArray = this.ParseEventURL(info.CallbackURL);
                        for (int j = 0; j < uriArray.Length; j++)
                        {
                            try
                            {
                                HTTPMessage mSG = new HTTPMessage();
                                mSG.Directive = "NOTIFY";
                                mSG.AddTag("Content-Type", "text/xml");
                                mSG.AddTag("NT", "upnp:event");
                                mSG.AddTag("NTS", "upnp:propchange");
                                mSG.BodyBuffer = this.BuildEventXML(new UPnPStateVariable[] { V });
                                mSG.DirectiveObj = HTTPMessage.UnEscapeString(uriArray[j].PathAndQuery);
                                mSG.AddTag("Host", uriArray[j].Host + ":" + uriArray[j].Port.ToString());
                                mSG.AddTag("SID", info.SID);
                                mSG.AddTag("SEQ", info.SEQ.ToString());
                                mSG.AddTag("CONNECTION", "close");
                                info.SEQ += 1L;
                                this.SubscriberTable[array[i]] = info;
                                HTTPRequest request = new HTTPRequest();
                                this.SendEventTable[request] = request;
                                request.OnResponse += new HTTPRequest.RequestHandler(this.HandleSendEvent);
                                request.PipelineRequest(new IPEndPoint(IPAddress.Parse(uriArray[j].Host), uriArray[j].Port), mSG, null);
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                    else
                    {
                        this.SubscriberTable.Remove(info.SID);
                    }
                }
            }
        }

        public static string SerializeObjectInstance(object data)
        {
            if (data == null)
            {
                return "";
            }
            string fullName = data.GetType().FullName;
            string str2 = "";
            switch (fullName)
            {
                case "System.Byte[]":
                    return Base64.Encode((byte[]) data);

                case "System.Uri":
                    return ((Uri) data).AbsoluteUri;

                case "System.Boolean":
                    if ((bool) data)
                    {
                        str2 = "1";
                    }
                    else
                    {
                        str2 = "0";
                    }
                    return str2;

                case "System.DateTime":
                {
                    DateTimeFormatInfo info = new DateTimeFormatInfo();
                    DateTime time = (DateTime) data;
                    return time.ToString(info.SortableDateTimePattern);
                }
            }
            return data.ToString();
        }

        public void SetStateVariable(string VarName, object VarValue)
        {
            ((UPnPStateVariable) this.StateVariables[VarName]).Value = VarValue;
        }

        internal void SetVersion(string v)
        {
            DText text = new DText();
            if (v.IndexOf("-") == -1)
            {
                text.ATTRMARK = ".";
            }
            else
            {
                text.ATTRMARK = "-";
            }
            text[0] = v;
            string s = text[1];
            string str2 = text[2];
            if (s == "")
            {
                this.Major = 0;
            }
            else
            {
                this.Major = int.Parse(s);
            }
            if (str2 == "")
            {
                this.Minor = 0;
            }
            else
            {
                this.Minor = int.Parse(str2);
            }
        }

        private void SniffPacketSink(HTTPRequest sender, HTTPMessage MSG, object Tag)
        {
            this.OnSniffPacketEvent.Fire(this, MSG);
        }

        private void SniffSink(HTTPRequest sender, byte[] raw, int offset, int length)
        {
            this.OnSniffEvent.Fire(raw, offset, length);
        }

        public void Subscribe(int Timeout, UPnPEventSubscribeHandler CB)
        {
            bool flag = false;
            lock (this.SubscribeLock)
            {
                if (this.SubscribeCounter == 0)
                {
                    flag = true;
                }
                this.SubscribeCounter++;
            }
            if (!flag)
            {
                foreach (UPnPStateVariable variable in this.GetStateVariables())
                {
                    variable.InitialEvent();
                }
                if (CB != null)
                {
                    CB(this, true);
                }
            }
            else
            {
                string str;
                int num;
                string str2;
                string str3;
                foreach (UPnPStateVariable variable2 in this.GetStateVariables())
                {
                    if (variable2.SendEvent)
                    {
                        variable2.CurrentValue = null;
                    }
                }
                this.CurrentTimeout = Timeout;
                HTTPMessage mSG = new HTTPMessage();
                if (Timeout == 0)
                {
                    str3 = "Second-infinite";
                }
                else
                {
                    str3 = "Second-" + Timeout.ToString();
                }
                SSDP.ParseURL(this.__eventurl, out str, out num, out str2);
                IPEndPoint dest = new IPEndPoint(IPAddress.Parse(str), num);
                mSG.Directive = "SUBSCRIBE";
                mSG.DirectiveObj = str2;
                mSG.AddTag("Host", str + ":" + num.ToString());
                mSG.AddTag("Callback", "<" + this.EventCallbackURL + ">");
                mSG.AddTag("NT", "upnp:event");
                mSG.AddTag("Timeout", str3);
                HTTPRequest request = new HTTPRequest();
                this.SubscribeRequestTable[request] = request;
                request.OnResponse += new HTTPRequest.RequestHandler(this.HandleSubscribeResponse);
                request.PipelineRequest(dest, mSG, CB);
            }
        }

        private void SubscribeCycleSink(LifeTimeMonitor sender, object obj)
        {
            if (((int) obj) == this.GetHashCode())
            {
                this.Renew();
            }
        }

        public void UnSubscribe(UPnPEventHandler cb)
        {
            bool flag = false;
            lock (this.SubscribeLock)
            {
                this.SubscribeCounter--;
                if (this.SubscribeCounter <= 0)
                {
                    this.SubscribeCounter = 0;
                    flag = true;
                }
                if (cb == null)
                {
                    flag = true;
                    this.OnUPnPEvent = null;
                }
                else
                {
                    this.OnUPnPEvent = (UPnPEventHandler) Delegate.Remove(this.OnUPnPEvent, cb);
                }
            }
            if (flag)
            {
                string str;
                int num;
                string str2;
                HTTPMessage mSG = new HTTPMessage();
                SSDP.ParseURL(this.__eventurl, out str, out num, out str2);
                IPEndPoint dest = new IPEndPoint(IPAddress.Parse(str), num);
                mSG.Directive = "UNSUBSCRIBE";
                mSG.DirectiveObj = str2;
                mSG.AddTag("Host", str + ":" + num.ToString());
                mSG.AddTag("SID", this.CurrentSID);
                HTTPRequest request = new HTTPRequest();
                this.SubscribeRequestTable[request] = request;
                request.OnResponse += new HTTPRequest.RequestHandler(this.HandleUnsubscribeResponse);
                this.CurrentSID = "";
                request.PipelineRequest(dest, mSG, null);
            }
        }

        public IList Actions
        {
            get
            {
                ArrayList list = new ArrayList();
                IEnumerator enumerator = this.RemoteMethods.Values.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    list.Add(enumerator.Current);
                }
                return list;
            }
        }

        internal string ControlURL
        {
            get
            {
                if (this.__controlurl == null)
                {
                    return "";
                }
                int num = this.__controlurl.LastIndexOf("/");
                if (num == -1)
                {
                    return this.__controlurl;
                }
                return this.__controlurl.Substring(num + 1);
            }
            set
            {
                this.__controlurl = value;
            }
        }

        internal string EventURL
        {
            get
            {
                if (this.__eventurl == null)
                {
                    return "";
                }
                int num = this.__eventurl.LastIndexOf("/");
                if (num == -1)
                {
                    return this.__eventurl;
                }
                return this.__eventurl.Substring(num + 1);
            }
            set
            {
                this.__eventurl = value;
            }
        }

        internal string SCPDFile
        {
            get
            {
                int num = this.SCPDURL.LastIndexOf("/");
                if (num == -1)
                {
                    return this.SCPDURL;
                }
                return this.SCPDURL.Substring(num + 1);
            }
        }

        public string ServiceID
        {
            get
            {
                return this.Service_ID;
            }
            set
            {
                if (!value.ToUpper().StartsWith("URN:"))
                {
                    this.Service_ID = "urn:upnp-org:serviceId:" + value;
                }
                else
                {
                    this.Service_ID = value;
                }
            }
        }

        public string ServiceURN
        {
            get
            {
                return this.ServiceType;
            }
            set
            {
                if (!value.ToUpper().StartsWith("URN:SCHEMAS-UPNP-ORG:SERVICE:"))
                {
                    if (value.ToUpper().StartsWith("URN:"))
                    {
                        this.ServiceType = value;
                        DText text = new DText();
                        text.ATTRMARK = ":";
                        text[0] = value;
                        if ((this.Version != "1") && (text[text.DCOUNT()] == "1"))
                        {
                            text[text.DCOUNT()] = this.Version;
                            this.ServiceType = text[0];
                        }
                        else
                        {
                            this.SetVersion(text[text.DCOUNT()]);
                        }
                    }
                    else
                    {
                        this.ServiceType = "urn:schemas-upnp-org:service:" + value + ":" + this.Version;
                    }
                }
                else
                {
                    this.ServiceType = value;
                    DText text2 = new DText();
                    text2.ATTRMARK = ":";
                    text2[0] = value;
                    if ((this.Version != "1") && (text2[text2.DCOUNT()] == "1"))
                    {
                        text2[text2.DCOUNT()] = this.Version;
                        this.ServiceType = text2[0];
                    }
                    else
                    {
                        this.SetVersion(text2[text2.DCOUNT()]);
                    }
                    this.SetVersion(text2[text2.DCOUNT()]);
                }
            }
        }

        public string ServiceURN_Prefix
        {
            get
            {
                DText text = new DText();
                text.ATTRMARK = ":";
                text[0] = this.ServiceType;
                int length = text[text.DCOUNT()].Length;
                return this.ServiceType.Substring(0, this.ServiceType.Length - length);
            }
        }

        internal bool ValidationMode
        {
            set
            {
                foreach (UPnPStateVariable variable in this.GetStateVariables())
                {
                    variable.DO_VALIDATE = value;
                }
            }
        }

        public string Version
        {
            get
            {
                if (this.Minor == 0)
                {
                    return this.Major.ToString();
                }
                return (this.Major.ToString() + "-" + this.Minor.ToString());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AsyncInvokeInfo
        {
            public string MethodName;
            public UPnPArgument[] Args;
            public object Tag;
            public HTTPMessage Packet;
            public UPnPService.UPnPServiceInvokeHandler InvokeCB;
            public UPnPService.UPnPServiceInvokeErrorHandler ErrorCB;
        }

        public delegate void OnSubscriptionHandler(UPnPService sender);

        public delegate void PeriodicRenewFailedHandler(UPnPService sender);

        public delegate void ServiceResetHandler(UPnPService sender);

        internal delegate void SniffHandler(byte[] Raw, int offset, int length);

        internal delegate void SniffPacketHandler(UPnPService sender, HTTPMessage MSG);

        [StructLayout(LayoutKind.Sequential)]
        private struct SubscriberInfo
        {
            public string SID;
            public string CallbackURL;
            public long Expires;
            public long SEQ;
        }

        public delegate void UPnPEventHandler(UPnPService sender, long SEQ);

        public delegate void UPnPEventSubscribeHandler(UPnPService sender, bool SubscribeOK);

        public delegate void UPnPServiceInvokeErrorHandler(UPnPService sender, string MethodName, UPnPArgument[] Args, UPnPInvokeException e, object Tag);

        public delegate void UPnPServiceInvokeHandler(UPnPService sender, string MethodName, UPnPArgument[] Args, object ReturnValue, object Tag);
    }
}

