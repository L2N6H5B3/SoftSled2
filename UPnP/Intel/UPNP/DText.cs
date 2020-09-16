namespace Intel.UPNP
{
    using System;
    using System.Collections;
    using System.Reflection;
    using System.Text;

    public class DText
    {
        private ArrayList ATTRLIST;
        public string ATTRMARK;
        public string MULTMARK;
        public string SUBVMARK;

        public DText()
        {
            this.ATTRMARK = "\x0080";
            this.MULTMARK = "\x0081";
            this.SUBVMARK = "\x0082";
            this.ATTRLIST = new ArrayList();
        }

        public DText(string STR)
        {
            this.ATTRMARK = "\x0080";
            this.MULTMARK = "\x0081";
            this.SUBVMARK = "\x0082";
            this.ATTRLIST = new ArrayList();
            this.ParseString(STR);
        }

        public int DCOUNT()
        {
            return this.ATTRLIST.Count;
        }

        public int DCOUNT(int A)
        {
            if (A == 0)
            {
                return this.DCOUNT();
            }
            if (this.ATTRLIST.Count < A)
            {
                return 0;
            }
            return ((ArrayList) this.ATTRLIST[A - 1]).Count;
        }

        public int DCOUNT(int A, int M)
        {
            if (M == 0)
            {
                return this.DCOUNT(A);
            }
            if (this.ATTRLIST.Count < A)
            {
                return 0;
            }
            if (((ArrayList) this.ATTRLIST[A - 1]).Count < M)
            {
                return 0;
            }
            return ((ArrayList) ((ArrayList) this.ATTRLIST[A - 1])[M - 1]).Count;
        }

        private ArrayList ParseString(string STR)
        {
            if (STR.Length == 0)
            {
                ArrayList list = new ArrayList();
                list.Add(new ArrayList());
                ((ArrayList) list[0]).Add(new ArrayList());
                return list;
            }
            int num = 1;
            int num2 = 1;
            int num3 = 1;
            StringBuilder builder = new StringBuilder();
            ArrayList list2 = new ArrayList();
            for (int i = 0; i < STR.Length; i++)
            {
                while (list2.Count < num)
                {
                    list2.Add(new ArrayList());
                }
                while (((ArrayList) list2[num - 1]).Count < num2)
                {
                    ((ArrayList) list2[num - 1]).Add(new ArrayList());
                }
                while (((ArrayList) ((ArrayList) list2[num - 1])[num2 - 1]).Count < num3)
                {
                    ((ArrayList) ((ArrayList) list2[num - 1])[num2 - 1]).Add(new ArrayList());
                }
                string str = STR.Substring(i, 1);
                if (((str == this.ATTRMARK.Substring(0, 1)) || (str == this.MULTMARK.Substring(0, 1))) || (str == this.SUBVMARK.Substring(0, 1)))
                {
                    bool flag = false;
                    bool flag2 = false;
                    bool flag3 = false;
                    if (((i + this.ATTRMARK.Length) <= STR.Length) && (STR.Substring(i, this.ATTRMARK.Length) == this.ATTRMARK))
                    {
                        flag = true;
                        i += this.ATTRMARK.Length - 1;
                    }
                    if (((i + this.MULTMARK.Length) <= STR.Length) && (STR.Substring(i, this.MULTMARK.Length) == this.MULTMARK))
                    {
                        flag2 = true;
                        i += this.MULTMARK.Length - 1;
                    }
                    if (((i + this.SUBVMARK.Length) <= STR.Length) && (STR.Substring(i, this.SUBVMARK.Length) == this.SUBVMARK))
                    {
                        flag3 = true;
                        i += this.SUBVMARK.Length - 1;
                    }
                    if ((flag || flag2) || flag3)
                    {
                        ((ArrayList) ((ArrayList) list2[num - 1])[num2 - 1])[num3 - 1] = builder.ToString();
                        builder = new StringBuilder();
                        if (flag)
                        {
                            num++;
                            num2 = 1;
                            num3 = 1;
                        }
                        if (flag2)
                        {
                            num2++;
                            num3 = 1;
                        }
                        if (flag3)
                        {
                            num3++;
                        }
                    }
                    else
                    {
                        builder.Append(str);
                    }
                }
                else
                {
                    builder.Append(str);
                }
            }
            if (builder.Length > 0)
            {
                ((ArrayList) ((ArrayList) list2[num - 1])[num2 - 1])[num3 - 1] = builder.ToString();
            }
            else
            {
                while (list2.Count < num)
                {
                    list2.Add(new ArrayList());
                }
                while (((ArrayList) list2[num - 1]).Count < num2)
                {
                    ((ArrayList) list2[num - 1]).Add(new ArrayList());
                }
                while (((ArrayList) ((ArrayList) list2[num - 1])[num2 - 1]).Count < num3)
                {
                    ((ArrayList) ((ArrayList) list2[num - 1])[num2 - 1]).Add(new ArrayList());
                }
                ((ArrayList) ((ArrayList) list2[num - 1])[num2 - 1])[num3 - 1] = "";
            }
            return list2;
        }

        public string this[int A, int M, int V]
        {
            get
            {
                if (V == 0)
                {
                    return this[A, M];
                }
                try
                {
                    return (string) ((ArrayList) ((ArrayList) this.ATTRLIST[A - 1])[M - 1])[V - 1];
                }
                catch (Exception)
                {
                    return "";
                }
            }
            set
            {
                if (V == 0)
                {
                    this[A, M] = value;
                }
                else
                {
                    while (this.ATTRLIST.Count < A)
                    {
                        this.ATTRLIST.Add(new ArrayList());
                    }
                    while (((ArrayList) this.ATTRLIST[A - 1]).Count < M)
                    {
                        ((ArrayList) this.ATTRLIST[A - 1]).Add(new ArrayList());
                    }
                    while (((ArrayList) ((ArrayList) this.ATTRLIST[A - 1])[M - 1]).Count < V)
                    {
                        ((ArrayList) ((ArrayList) this.ATTRLIST[A - 1])[M - 1]).Add(new ArrayList());
                    }
                    ((ArrayList) ((ArrayList) this.ATTRLIST[A - 1])[M - 1])[V - 1] = value;
                }
            }
        }

        public string this[int A, int M]
        {
            get
            {
                if (M == 0)
                {
                    return this[A];
                }
                StringBuilder builder = new StringBuilder();
                int num = this.DCOUNT(A, M);
                for (int i = 1; i <= num; i++)
                {
                    if (i != 1)
                    {
                        builder.Append(this.SUBVMARK);
                    }
                    builder.Append(this[A, M, i]);
                }
                return builder.ToString();
            }
            set
            {
                if (M == 0)
                {
                    this[A] = value;
                }
                else
                {
                    while (this.ATTRLIST.Count < A)
                    {
                        this.ATTRLIST.Add(new ArrayList());
                    }
                    while (((ArrayList) this.ATTRLIST[A - 1]).Count < M)
                    {
                        ((ArrayList) this.ATTRLIST[A - 1]).Add(new ArrayList());
                    }
                    ArrayList list = this.ParseString(value);
                    if (list.Count > 1)
                    {
                        this.ATTRLIST.Insert(A - 1, list);
                    }
                    else if (((ArrayList) list[0]).Count > 1)
                    {
                        ((ArrayList) this.ATTRLIST[A - 1]).Insert(M - 1, list[0]);
                    }
                    else
                    {
                        ((ArrayList) this.ATTRLIST[A - 1])[M - 1] = (ArrayList) ((ArrayList) list[0])[0];
                    }
                }
            }
        }

        public string this[int A]
        {
            get
            {
                StringBuilder builder = new StringBuilder();
                if (A > 0)
                {
                    int num = this.DCOUNT(A);
                    for (int j = 1; j <= num; j++)
                    {
                        if (j != 1)
                        {
                            builder.Append(this.MULTMARK);
                        }
                        builder.Append(this[A, j]);
                    }
                    return builder.ToString();
                }
                int num3 = this.DCOUNT();
                for (int i = 1; i <= num3; i++)
                {
                    if (i != 1)
                    {
                        builder.Append(this.ATTRMARK);
                    }
                    builder.Append(this[i]);
                }
                return builder.ToString();
            }
            set
            {
                if (A == 0)
                {
                    this.ATTRLIST = this.ParseString(value);
                }
                else
                {
                    while (this.ATTRLIST.Count < A)
                    {
                        this.ATTRLIST.Add(new ArrayList());
                    }
                    ArrayList list = this.ParseString(value);
                    if (list.Count > 1)
                    {
                        this.ATTRLIST.Insert(A - 1, list);
                    }
                    else if (this.ATTRLIST.Count < (A - 1))
                    {
                        this.ATTRLIST.Insert(A - 1, list[0]);
                    }
                    else
                    {
                        this.ATTRLIST[A - 1] = list[0];
                    }
                }
            }
        }
    }
}

