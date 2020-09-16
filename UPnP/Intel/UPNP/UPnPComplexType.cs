namespace Intel.UPNP
{
    using System;
    using System.Collections;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Xml;

    public class UPnPComplexType
    {
        private string LocalName;
        private ArrayList m_CollectionList;
        private string NameSpace;
        internal UPnPService ParentService;

        public UPnPComplexType()
        {
            this.ParentService = null;
            this.m_CollectionList = new ArrayList();
        }

        public UPnPComplexType(string Name, string Namespace) : this()
        {
            this.NameSpace = Namespace;
            this.LocalName = Name;
        }

        public void AddContainer(GenericContainer c)
        {
            this.m_CollectionList.Add(c);
            c.ParentComplexType = this;
        }

        public void ClearCollections()
        {
            this.m_CollectionList.Clear();
        }

        public void GetComplexTypeSchemaPart(XmlTextWriter X)
        {
            X.WriteStartElement("xs", "complexType", null);
            X.WriteAttributeString("name", this.Name_LOCAL);
            foreach (GenericContainer container in this.Containers)
            {
                this.GetComplexTypeSchemaPart2(X, container);
            }
            X.WriteEndElement();
        }

        private void GetComplexTypeSchemaPart2(XmlTextWriter X, GenericContainer gc)
        {
            foreach (ItemCollection items in gc.Collections)
            {
                this.GetComplexTypeSchemaPart3(X, items);
            }
        }

        private void GetComplexTypeSchemaPart3(XmlTextWriter X, ItemCollection ic)
        {
            if (ic.GetType().Name != "ItemCollection")
            {
                X.WriteStartElement("xs", ic.GetType().Name.ToLower(), null);
            }
            foreach (ContentData data in ic.Items)
            {
                X.WriteStartElement("xs", data.GetType().Name.ToLower(), null);
                X.WriteAttributeString("name", data.Name);
                if (data.TypeNS == "http://www.w3.org/2001/XMLSchema")
                {
                    X.WriteAttributeString("type", "xs:" + data.Type);
                }
                else
                {
                    X.WriteAttributeString("type", data.Type);
                }
                if (data.MinOccurs != "")
                {
                    X.WriteAttributeString("minOccurs", data.MinOccurs);
                }
                if (data.MaxOccurs != "")
                {
                    X.WriteAttributeString("maxOccurs", data.MaxOccurs);
                }
                X.WriteEndElement();
            }
            if (ic.GetType().Name != "ItemCollection")
            {
                X.WriteEndElement();
            }
        }

        public Field[] GetFields()
        {
            return null;
        }

        public static UPnPComplexType[] Parse(string xml)
        {
            ArrayList list = new ArrayList();
            Hashtable hashtable = new Hashtable();
            StringReader input = new StringReader(xml);
            XmlTextReader x = new XmlTextReader(input);
            while (x.Read())
            {
                Group group;
                switch (x.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        switch (x.LocalName)
                        {
                            case "complexType":
                            {
                                UPnPComplexType type = ParseComplexType(x);
                                list.Add(type);
                                hashtable[type.Name_NAMESPACE + ":" + type.Name_LOCAL] = type;
                                break;
                            }
                            case "group":
                                goto Label_00B4;
                        }
                        continue;
                    }
                    case XmlNodeType.Attribute:
                    case XmlNodeType.Text:
                    case XmlNodeType.EndElement:
                    {
                        continue;
                    }
                }
                continue;
            Label_00B4:
                group = (Group) ParseComplexType(x, new Group());
                hashtable[group.Name_NAMESPACE + ":" + group.Name_LOCAL] = group;
                list.Add(group);
            }
            return (UPnPComplexType[]) list.ToArray(typeof(UPnPComplexType));
        }

        public static ItemCollection ParseCollection(XmlTextReader X)
        {
            return null;
        }

        private static UPnPComplexType ParseComplexType(XmlTextReader X)
        {
            return ParseComplexType(X, new UPnPComplexType());
        }

        private static UPnPComplexType ParseComplexType(XmlTextReader X, UPnPComplexType RetVal)
        {
            string localName = X.LocalName;
            int num = 0;
            bool flag = false;
            DText text = new DText();
            text.ATTRMARK = ":";
            RetVal.AddContainer(new GenericContainer());
            do
            {
                switch (X.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        string str = X.LocalName;
                        if (str != null)
                        {
                            str = string.IsInterned(str);
                            if ((str == "complexType") || (str == "group"))
                            {
                                num++;
                                if (X.HasAttributes)
                                {
                                    for (int i = 0; i < X.AttributeCount; i++)
                                    {
                                        X.MoveToAttribute(i);
                                        if (X.Name == "name")
                                        {
                                            text[0] = X.Value;
                                            if (text.DCOUNT() == 1)
                                            {
                                                RetVal.LocalName = X.Value;
                                                RetVal.NameSpace = X.LookupNamespace("");
                                            }
                                            else
                                            {
                                                RetVal.LocalName = text[2];
                                                RetVal.NameSpace = X.LookupNamespace(text[1]);
                                            }
                                        }
                                        else if (X.Name == "ref")
                                        {
                                        }
                                    }
                                    X.MoveToElement();
                                }
                            }
                            else
                            {
                                if ((str == "sequence") || (str == "choice"))
                                {
                                    RetVal.CurrentContainer.AddCollection(ParseComplexType_SequenceChoice(X));
                                    break;
                                }
                                if (str == "complexContent")
                                {
                                    RetVal.AddContainer(new ComplexContent());
                                    break;
                                }
                                if (str == "simpleContent")
                                {
                                    RetVal.AddContainer(new SimpleContent());
                                    break;
                                }
                                if (str == "restriction")
                                {
                                    Restriction restriction = new Restriction();
                                    if (RetVal.CurrentContainer.GetType() == typeof(ComplexContent))
                                    {
                                        ((ComplexContent) RetVal.CurrentContainer).RestExt = restriction;
                                    }
                                    else if (RetVal.CurrentContainer.GetType() == typeof(SimpleContent))
                                    {
                                        ((SimpleContent) RetVal.CurrentContainer).RestExt = restriction;
                                    }
                                    if (X.HasAttributes)
                                    {
                                        for (int j = 0; j < X.AttributeCount; j++)
                                        {
                                            X.MoveToAttribute(j);
                                            if (X.Name == "base")
                                            {
                                                text[0] = X.Value;
                                                if (text.DCOUNT() == 1)
                                                {
                                                    restriction.baseType = X.Value;
                                                    restriction.baseTypeNS = X.LookupNamespace("");
                                                }
                                                else
                                                {
                                                    restriction.baseType = text[2];
                                                    restriction.baseTypeNS = X.LookupNamespace(text[1]);
                                                }
                                            }
                                        }
                                        X.MoveToElement();
                                    }
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    case XmlNodeType.EndElement:
                        if (X.LocalName == localName)
                        {
                            num--;
                            if (num == 0)
                            {
                                flag = true;
                            }
                        }
                        break;
                }
            }
            while (!flag && X.Read());
            return RetVal;
        }

        private static ItemCollection ParseComplexType_SequenceChoice(XmlTextReader X)
        {
            bool flag = false;
            ItemCollection items = null;
            string localName = X.LocalName;
            DText text = new DText();
            text.ATTRMARK = ":";
            if (X.LocalName == "choice")
            {
                items = new Choice();
            }
            else
            {
                items = new Sequence();
            }
            if (X.HasAttributes)
            {
                for (int i = 0; i < X.AttributeCount; i++)
                {
                    X.MoveToAttribute(i);
                    switch (X.LocalName)
                    {
                        case "minOccurs":
                            items.MinOccurs = X.Value;
                            break;

                        case "maxOccurs":
                            items.MaxOccurs = X.Value;
                            break;
                    }
                }
                X.MoveToElement();
            }
            X.Read();
            do
            {
                switch (X.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        string str = X.LocalName;
                        if (str != null)
                        {
                            str = string.IsInterned(str);
                            if (str == "group")
                            {
                                if (X.HasAttributes)
                                {
                                    for (int j = 0; j < X.AttributeCount; j++)
                                    {
                                        string str5;
                                        X.MoveToAttribute(j);
                                        if (((str5 = X.LocalName) != null) && (string.IsInterned(str5) == "ref"))
                                        {
                                            string str2 = X.Value;
                                        }
                                    }
                                    X.MoveToElement();
                                }
                            }
                            else
                            {
                                if ((str == "sequence") || (str == "choice"))
                                {
                                    items.AddCollection(ParseComplexType_SequenceChoice(X));
                                    break;
                                }
                                if (str == "element")
                                {
                                    items.AddContentItem(new Element());
                                    if (X.HasAttributes)
                                    {
                                        for (int k = 0; k < X.AttributeCount; k++)
                                        {
                                            X.MoveToAttribute(k);
                                            switch (X.LocalName)
                                            {
                                                case "name":
                                                    items.CurrentItem.Name = X.Value;
                                                    break;

                                                case "type":
                                                    text[0] = X.Value;
                                                    if (text.DCOUNT() == 1)
                                                    {
                                                        items.CurrentItem.Type = X.Value;
                                                        items.CurrentItem.TypeNS = X.LookupNamespace("");
                                                    }
                                                    else
                                                    {
                                                        items.CurrentItem.Type = text[2];
                                                        items.CurrentItem.TypeNS = X.LookupNamespace(text[1]);
                                                    }
                                                    break;

                                                case "minOccurs":
                                                    items.CurrentItem.MinOccurs = X.Value;
                                                    break;

                                                case "maxOccurs":
                                                    items.CurrentItem.MaxOccurs = X.Value;
                                                    break;
                                            }
                                        }
                                        X.MoveToElement();
                                    }
                                    break;
                                }
                                if (str == "attribute")
                                {
                                }
                            }
                        }
                        break;
                    }
                    case XmlNodeType.EndElement:
                        if (X.LocalName == localName)
                        {
                            flag = true;
                        }
                        break;
                }
            }
            while (!flag && X.Read());
            return items;
        }

        public void RemoveContainer(GenericContainer c)
        {
            this.m_CollectionList.Remove(c);
            c.ParentComplexType = null;
        }

        public override string ToString()
        {
            return this.Name_LOCAL;
        }

        public GenericContainer[] Containers
        {
            get
            {
                return (GenericContainer[]) this.m_CollectionList.ToArray(typeof(GenericContainer));
            }
        }

        public GenericContainer CurrentContainer
        {
            get
            {
                if (this.m_CollectionList.Count == 0)
                {
                    return null;
                }
                return (GenericContainer) this.m_CollectionList[this.m_CollectionList.Count - 1];
            }
        }

        public string Name_LOCAL
        {
            get
            {
                return this.LocalName;
            }
        }

        public string Name_NAMESPACE
        {
            get
            {
                return this.NameSpace;
            }
        }

        public class Attribute : UPnPComplexType.ContentData
        {
        }

        public class Choice : UPnPComplexType.ItemCollection
        {
        }

        public class ComplexContent : UPnPComplexType.GenericContainer
        {
            public UPnPComplexType.RestrictionExtension RestExt;
        }

        public abstract class ContentData
        {
            public string MaxOccurs = "1";
            public string MinOccurs = "1";
            public string Name;
            public UPnPComplexType.ItemCollection Parent;
            public string Type;
            public string TypeNS;

            protected ContentData()
            {
            }

            public override string ToString()
            {
                return (base.GetType().Name + ": " + this.Name);
            }
        }

        public class Element : UPnPComplexType.ContentData
        {
            public ArrayList AttributeList;

            public UPnPComplexType.Attribute[] Attributes
            {
                get
                {
                    return (UPnPComplexType.Attribute[]) this.AttributeList.ToArray(typeof(UPnPComplexType.Attribute));
                }
            }
        }

        public class Extension : UPnPComplexType.RestrictionExtension
        {
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Field
        {
            public string Name;
            public string Type;
            public string TypeNS;
            public string MinOccurs;
            public string MaxOccurs;
            public ArrayList AttributeList;
        }

        public class GenericContainer
        {
            public ArrayList CollectionList = new ArrayList();
            public string documentation = "";
            public UPnPComplexType ParentComplexType;

            public void AddCollection(UPnPComplexType.ItemCollection e)
            {
                this.CollectionList.Add(e);
                e.ParentContainer = this;
            }

            public void RemoveCollection(UPnPComplexType.ItemCollection e)
            {
                this.CollectionList.Remove(e);
                e.ParentContainer = null;
            }

            public UPnPComplexType.ItemCollection[] Collections
            {
                get
                {
                    return (UPnPComplexType.ItemCollection[]) this.CollectionList.ToArray(typeof(UPnPComplexType.ItemCollection));
                }
            }
        }

        public class Group : UPnPComplexType
        {
        }

        public class ItemCollection
        {
            public ArrayList ItemList = new ArrayList();
            public string MaxOccurs = "1";
            public string MinOccurs = "1";
            public ArrayList NestedCollectionList = new ArrayList();
            public UPnPComplexType.ItemCollection ParentCollection;
            public UPnPComplexType.GenericContainer ParentContainer;

            public void AddCollection(UPnPComplexType.ItemCollection ic)
            {
                this.NestedCollectionList.Add(ic);
                ic.ParentCollection = this;
            }

            public void AddContentItem(UPnPComplexType.ContentData c)
            {
                c.Parent = this;
                this.ItemList.Add(c);
            }

            public void MoveContentItem_DOWN(UPnPComplexType.ContentData c)
            {
                int index = this.ItemList.IndexOf(c);
                if ((index >= 0) && ((index + 1) <= this.ItemList.Count))
                {
                    this.ItemList.Reverse(index, 2);
                }
            }

            public void MoveContentItem_UP(UPnPComplexType.ContentData c)
            {
                int index = this.ItemList.IndexOf(c);
                if (index >= 1)
                {
                    this.ItemList.Reverse(index - 1, 2);
                }
            }

            public void RemoveCollection(UPnPComplexType.ItemCollection ic)
            {
                this.NestedCollectionList.Remove(ic);
                ic.ParentCollection = null;
            }

            public void RemoveContentItem(UPnPComplexType.ContentData c)
            {
                c.Parent = null;
                this.ItemList.Remove(c);
            }

            public UPnPComplexType.ContentData CurrentItem
            {
                get
                {
                    if (this.ItemList.Count == 0)
                    {
                        return null;
                    }
                    return (UPnPComplexType.ContentData) this.ItemList[this.ItemList.Count - 1];
                }
            }

            public UPnPComplexType.ContentData[] Items
            {
                get
                {
                    return (UPnPComplexType.ContentData[]) this.ItemList.ToArray(typeof(UPnPComplexType.ContentData));
                }
            }

            public UPnPComplexType.ItemCollection[] NestedCollections
            {
                get
                {
                    return (UPnPComplexType.ItemCollection[]) this.NestedCollectionList.ToArray(typeof(UPnPComplexType.ItemCollection));
                }
            }
        }

        public class Restriction : UPnPComplexType.RestrictionExtension
        {
            public string PATTERN;
        }

        public abstract class RestrictionExtension
        {
            public string baseType;
            public string baseTypeNS;

            protected RestrictionExtension()
            {
            }
        }

        public class Sequence : UPnPComplexType.ItemCollection
        {
        }

        public class SimpleContent : UPnPComplexType.GenericContainer
        {
            public UPnPComplexType.RestrictionExtension RestExt;
        }
    }
}

