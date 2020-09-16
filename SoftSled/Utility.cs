using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SoftSled
{
    class Utility
    {
        [DllImport("kernel32.dll")]
        static extern int WideCharToMultiByte(uint CodePage, uint dwFlags,
           [MarshalAs(UnmanagedType.LPWStr)] string lpWideCharStr, int cchWideChar,
           [MarshalAs(UnmanagedType.LPArray)] Byte[] lpMultiByteStr, int cbMultiByte, IntPtr lpDefaultChar,
           out bool lpUsedDefaultChar);

        [DllImport("kernel32.dll")]
        static extern int MultiByteToWideChar(uint CodePage, uint dwFlags, string
           lpMultiByteStr, int cbMultiByte, [Out, MarshalAs(UnmanagedType.LPWStr)]
            StringBuilder lpWideCharStr, int cchWideChar); 
        
        const uint CP_ACP = 0;
        const uint CP_OEMCP = 1;
        const uint CP_SYMBOL = 42;
        const uint CP_UTF7 = 65000;
        const uint CP_UTF8 = 65001;
        const uint CP_GB2312 = 936;
        const uint CP_BIG5 = 950;
        const uint CP_SHIFTJIS = 932;

        public static byte[] ConvertBase64ToUTF16(string base64String)
        {
            Int32 iNewDataLen = 0;
            Byte[] byNewData = null;
            bool bDefaultChar = false;

            Byte[] originalBytes = Convert.FromBase64String(base64String);

            base64String = System.Text.Encoding.Unicode.GetString(originalBytes);

            iNewDataLen = WideCharToMultiByte(CP_ACP, 0, base64String, base64String.Length, null, 0, IntPtr.Zero, out bDefaultChar);
            byNewData = new Byte[iNewDataLen];
            iNewDataLen = WideCharToMultiByte(CP_ACP, 0, base64String, base64String.Length, byNewData, iNewDataLen, IntPtr.Zero, out bDefaultChar);

            return byNewData;
        }

        public static X509Certificate2 ConvertBase64StringToCert(string base64Cert)
        {
            byte[] rawSource = Convert.FromBase64String(base64Cert);
            int copyLength = rawSource.Length - 6;
            byte[] raw = new byte[copyLength];
            Array.Copy(rawSource,6, raw,0,copyLength);

            return new X509Certificate2(raw);
        }

    }
}
