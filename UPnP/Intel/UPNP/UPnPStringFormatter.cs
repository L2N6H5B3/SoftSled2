namespace Intel.UPNP
{
    using System;

    public class UPnPStringFormatter
    {
        public static string EscapeString(string InString)
        {
            InString = InString.Replace("&", "&amp;");
            InString = InString.Replace("<", "&lt;");
            InString = InString.Replace(">", "&gt;");
            InString = InString.Replace("\"", "&quot;");
            InString = InString.Replace("'", "&apos;");
            return InString;
        }

        public static string GetURNPrefix(string urn)
        {
            DText text = new DText();
            text.ATTRMARK = ":";
            text[0] = urn;
            int length = text[text.DCOUNT()].Length;
            return urn.Substring(0, urn.Length - length);
        }

        public static string PartialEscapeString(string InString)
        {
            InString = InString.Replace("\"", "&quot;");
            InString = InString.Replace("'", "&apos;");
            return InString;
        }

        public static string UnEscapeString(string InString)
        {
            InString = InString.Replace("&lt;", "<");
            InString = InString.Replace("&gt;", ">");
            InString = InString.Replace("&quot;", "\"");
            InString = InString.Replace("&apos;", "'");
            InString = InString.Replace("&amp;", "&");
            return InString;
        }
    }
}

