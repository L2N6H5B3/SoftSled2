namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Globalization;
    using System.Net;
    using System.Text;

    [Serializable]
    public sealed class HTTPMessage : ICloneable
    {
        private byte[] DataBuffer;
        internal bool DontShowContentLength;
        public IPEndPoint LocalEndPoint;
        private string Method;
        private string MethodData;
        public bool OverrideContentLength;
        public IPEndPoint RemoteEndPoint;
        private int ResponseCode;
        private string ResponseData;
        [NonSerialized]
        public object StateObject;
        private Hashtable TheHeaders;
        public string Version;

        public HTTPMessage() : this("1.1")
        {
        }

        public HTTPMessage(string version)
        {
            this.DontShowContentLength = false;
            this.OverrideContentLength = false;
            this.Version = "1.1";
            this.StateObject = null;
            InstanceTracker.Add(this);
            this.TheHeaders = new Hashtable();
            this.ResponseCode = -1;
            this.ResponseData = "";
            this.Method = "";
            this.MethodData = "";
            this.DataBuffer = new byte[0];
            this.Version = version;
        }

        public void AddTag(string TagName, string TagData)
        {
            //MOD
            //this.TheHeaders[TagName.ToUpper()] = TagData;
            this.TheHeaders[TagName] = TagData;
        }

        public void AppendTag(string TagName, string TagData)
        {
            if (!this.TheHeaders.ContainsKey(TagName.ToUpper()))
            {
                this.TheHeaders[TagName.ToUpper()] = TagData;
            }
            else
            {
                if (this.TheHeaders[TagName.ToUpper()].GetType() == typeof(string))
                {
                    ArrayList list = new ArrayList();
                    list.Add(this.TheHeaders[TagName.ToUpper()]);
                    this.TheHeaders[TagName.ToUpper()] = list;
                }
                ((ArrayList) this.TheHeaders[TagName.ToUpper()]).Add(TagData);
            }
        }

        private byte[] BuildPacket()
        {
            string str;
            if (this.DataBuffer == null)
            {
                this.DataBuffer = new byte[0];
            }
            if (((this.Version == "1.0") && (this.Method == "")) && (this.ResponseCode == -1))
            {
                return this.DataBuffer;
            }
            UTF8Encoding encoding = new UTF8Encoding();
            IDictionaryEnumerator enumerator = this.TheHeaders.GetEnumerator();
            enumerator.Reset();
            if (this.Method != "")
            {
                if (this.Version != "")
                {
                    str = this.Method + " " + EscapeString(this.MethodData) + " HTTP/" + this.Version + "\r\n";
                }
                else
                {
                    str = this.Method + " " + EscapeString(this.MethodData) + "\r\n";
                }
            }
            else
            {
                str = "HTTP/" + this.Version + " " + this.ResponseCode.ToString() + " " + this.ResponseData + "\r\n";
            }
            while (enumerator.MoveNext())
            {
                if ((((string) enumerator.Key) != "CONTENT-LENGTH") || this.OverrideContentLength)
                {
                    if (enumerator.Value.GetType() == typeof(string))
                    {
                        string str3 = str;
                        //MOD
                        //str = str3 + ((string) enumerator.Key) + ": " + ((string) enumerator.Value) + "\r\n";
                        str = str3 + ((string)enumerator.Key) + ":" + ((string)enumerator.Value) + "\r\n";
                    }
                    else
                    {
                        str = str + ((string) enumerator.Key) + ":";
                        foreach (string str2 in (ArrayList) enumerator.Value)
                        {
                            str = str + " " + str2 + "\r\n";
                        }
                    }
                }
            }
            if ((this.StatusCode == -1) && !this.DontShowContentLength)
            {
                str = str + "Content-Length: " + this.DataBuffer.Length.ToString() + "\r\n";
            }
            else if ((((this.Version != "1.0") && (this.Version != "0.9")) && ((this.Version != "") && !this.DontShowContentLength)) && !this.OverrideContentLength)
            {
                str = str + "Content-Length: " + this.DataBuffer.Length.ToString() + "\r\n";
            }
            str = str + "\r\n";
            byte[] bytes = new byte[encoding.GetByteCount(str) + this.DataBuffer.Length];
            encoding.GetBytes(str, 0, str.Length, bytes, 0);
            Array.Copy(this.DataBuffer, 0, bytes, bytes.Length - this.DataBuffer.Length, this.DataBuffer.Length);
            return bytes;
        }

        public object Clone()
        {
            object o = base.MemberwiseClone();
            InstanceTracker.Add(o);
            return o;
        }

        public static string EscapeString(string TheString)
        {
            byte[] bytes = new UTF8Encoding().GetBytes(TheString);
            StringBuilder builder = new StringBuilder();
            foreach (byte num in bytes)
            {
                if ((((num >= 0x3f) && (num <= 90)) || ((num >= 0x61) && (num <= 0x7a))) || (((num >= 0x2f) && (num <= 0x39)) || (((((num == 0x3b) || (num == 0x2f)) || ((num == 0x3f) || (num == 0x3a))) || (((num == 0x40) || (num == 0x3d)) || ((num == 0x2b) || (num == 0x24)))) || (((num == 0x2d) || (num == 0x5f)) || ((num == 0x2e) || (num == 0x2a))))))
                {
                    builder.Append((char) num);
                }
                else
                {
                    builder.Append("%" + num.ToString("X"));
                }
            }
            return builder.ToString();
        }

        public IDictionaryEnumerator GetHeaderEnumerator()
        {
            return this.TheHeaders.GetEnumerator();
        }

        public string GetTag(string TagName)
        {
            object obj2 = this.TheHeaders[TagName.ToUpper()];
            if (obj2 == null)
            {
                return "";
            }
            if (obj2.GetType() == typeof(string))
            {
                return ((string) obj2).Trim();
            }
            string str = "";
            foreach (string str2 in (ArrayList) obj2)
            {
                str = str + str2.Trim();
            }
            return str;
        }

        public bool HasTag(string TagName)
        {
            return this.TheHeaders.ContainsKey(TagName.ToUpper());
        }

        public static HTTPMessage ParseByteArray(byte[] buffer)
        {
            return ParseByteArray(buffer, 0, buffer.Length);
        }

        public static HTTPMessage ParseByteArray(byte[] buffer, int indx, int count)
        {
            byte[] buffer2;
            HTTPMessage message = new HTTPMessage();
            string str = new UTF8Encoding().GetString(buffer, indx, count);
            DText text = new DText();
            int index = str.IndexOf("\r\n\r\n");
            str = str.Substring(0, index);
            text.ATTRMARK = "\r\n";
            text.MULTMARK = ":";
            text[0] = str;
            string str3 = text[1];
            DText text2 = new DText();
            text2.ATTRMARK = " ";
            text2.MULTMARK = "/";
            text2[0] = str3;
            if (str3.ToUpper().StartsWith("HTTP/"))
            {
                message.ResponseCode = int.Parse(text2[2]);
                int startIndex = str3.IndexOf(" ");
                startIndex = str3.IndexOf(" ", (int) (startIndex + 1));
                message.ResponseData = UnEscapeString(str3.Substring(startIndex));
                try
                {
                    message.Version = text2[1, 2];
                }
                catch (Exception)
                {
                    message.Version = "0.9";
                }
            }
            else
            {
                message.Directive = text2[1];
                string theString = str3.Substring(str3.LastIndexOf(" ") + 1);
                if (!theString.ToUpper().StartsWith("HTTP/"))
                {
                    message.Version = "0.9";
                    message.DirectiveObj = UnEscapeString(theString);
                }
                else
                {
                    message.Version = theString.Substring(theString.IndexOf("/") + 1);
                    int num3 = str3.IndexOf(" ") + 1;
                    message.DirectiveObj = UnEscapeString(str3.Substring(num3, ((str3.Length - num3) - theString.Length) - 1));
                }
            }
            string tagName = "";
            string tagData = "";
            for (int i = 2; i <= text.DCOUNT(); i++)
            {
                if ((tagName != "") && text[i, 1].StartsWith(" "))
                {
                    tagData = text[i, 1].Substring(1);
                }
                else
                {
                    tagName = text[i, 1];
                    tagData = "";
                    for (int j = 2; j <= text.DCOUNT(i); j++)
                    {
                        if (tagData == "")
                        {
                            tagData = text[i, j];
                        }
                        else
                        {
                            tagData = tagData + text.MULTMARK + text[i, j];
                        }
                    }
                }
                message.AppendTag(tagName, tagData);
            }
            int length = 0;
            if (message.HasTag("Content-Length"))
            {
                try
                {
                    length = int.Parse(message.GetTag("Content-Length"));
                }
                catch (Exception)
                {
                    length = -1;
                }
            }
            else
            {
                length = -1;
            }
            if (length > 0)
            {
                buffer2 = new byte[length];
                if (((index + 4) + length) <= count)
                {
                    Array.Copy(buffer, index + 4, buffer2, 0, length);
                    message.DataBuffer = buffer2;
                }
            }
            switch (length)
            {
                case -1:
                    buffer2 = new byte[count - (index + 4)];
                    Array.Copy(buffer, index + 4, buffer2, 0, buffer2.Length);
                    message.DataBuffer = buffer2;
                    break;

                case 0:
                    message.DataBuffer = new byte[0];
                    break;
            }
            return message;
        }

        public void RemoveTag(string TagName)
        {
            try
            {
                this.TheHeaders.Remove(TagName.ToUpper());
            }
            catch (Exception)
            {
            }
        }

        public static string UnEscapeString(string TheString)
        {
            IEnumerator enumerator = TheString.GetEnumerator();
            ArrayList list = new ArrayList();
            UTF8Encoding encoding = new UTF8Encoding();
            while (enumerator.MoveNext())
            {
                if (((char) enumerator.Current) == '%')
                {
                    enumerator.MoveNext();
                    string str = new string((char) enumerator.Current, 1);
                    enumerator.MoveNext();
                    int num = int.Parse((str + new string((char) enumerator.Current, 1)).ToUpper(), NumberStyles.HexNumber);
                    list.Add((byte) num);
                }
                else
                {
                    list.Add((byte) ((char) enumerator.Current));
                }
            }
            return encoding.GetString((byte[]) list.ToArray(typeof(byte)));
        }

        public byte[] BodyBuffer
        {
            get
            {
                return this.DataBuffer;
            }
            set
            {
                this.DataBuffer = value;
            }
        }

        public string CharSet
        {
            get
            {
                string contentType = this.ContentType;
                DText text = new DText();
                text.ATTRMARK = ";";
                text.MULTMARK = "=";
                text[0] = contentType;
                string str2 = "";
                if (text.DCOUNT() > 1)
                {
                    for (int i = 1; i <= text.DCOUNT(); i++)
                    {
                        if (text[i, 1].Trim().ToUpper() == "CHARSET")
                        {
                            str2 = text[i, 2].Trim().ToUpper();
                            if (str2.StartsWith("\""))
                            {
                                str2 = str2.Substring(1);
                            }
                            if (str2.EndsWith("\""))
                            {
                                str2 = str2.Substring(0, str2.Length - 1);
                            }
                            return str2;
                        }
                    }
                    return str2;
                }
                return "";
            }
        }

        public string ContentType
        {
            get
            {
                return this.GetTag("Content-Type");
            }
            set
            {
                this.AddTag("Content-Type", value);
            }
        }

        public string Directive
        {
            get
            {
                return this.Method;
            }
            set
            {
                this.Method = value;
            }
        }

        public string DirectiveObj
        {
            get
            {
                return this.MethodData;
            }
            set
            {
                this.MethodData = value;
            }
        }

        public byte[] RawPacket
        {
            get
            {
                return this.BuildPacket();
            }
        }

        public int StatusCode
        {
            get
            {
                return this.ResponseCode;
            }
            set
            {
                this.ResponseCode = value;
            }
        }

        public string StatusData
        {
            get
            {
                return this.ResponseData;
            }
            set
            {
                this.ResponseData = value;
            }
        }

        public string StringBuffer
        {
            get
            {
                if (this.CharSet == "UTF-16")
                {
                    UnicodeEncoding encoding = new UnicodeEncoding();
                    return encoding.GetString(this.DataBuffer);
                }
                UTF8Encoding encoding2 = new UTF8Encoding();
                return encoding2.GetString(this.DataBuffer);
            }
            set
            {
                this.DataBuffer = new UTF8Encoding().GetBytes(value);
            }
        }

        public string StringPacket
        {
            get
            {
                UTF8Encoding encoding = new UTF8Encoding();
                return encoding.GetString(this.RawPacket);
            }
        }
    }
}

