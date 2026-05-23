using System;
using System.Runtime.InteropServices;

namespace SoftSled {
    // Define the IObjectSafety Interface GUID
    [ComImport, Guid("CB5BDC81-93C1-11CF-8F20-00805F2CD064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectSafety {
        [PreserveSig]
        int GetInterfaceSafetyOptions(ref Guid riid,
                                      [MarshalAs(UnmanagedType.U4)] out int pdwSupportedOptions,
                                      [MarshalAs(UnmanagedType.U4)] out int pdwEnabledOptions);

        [PreserveSig]
        int SetInterfaceSafetyOptions(ref Guid riid,
                                      [MarshalAs(UnmanagedType.U4)] int dwOptionSetMask,
                                      [MarshalAs(UnmanagedType.U4)] int dwEnabledOptions);
    }

    // Constants for safety options
    public class ObjectSafetyConstants {
        public const int INTERFACESAFE_FOR_UNTRUSTED_CALLER = 0x00000001; // Safe for Scripting
        public const int INTERFACESAFE_FOR_UNTRUSTED_DATA = 0x00000002; // Safe for Initialization
        public const int S_OK = 0; // COM Success HRESULT
        public const int E_FAIL = unchecked((int)0x80004005); // COM Failure HRESULT
        public const int E_NOINTERFACE = unchecked((int)0x80004002); // COM No Interface HRESULT
    }
}
