namespace Intel.UPNP
{
    using System;
    using System.Collections;

    public sealed class UPnPDeviceComparer_Type : IComparer
    {
        public int Compare(object x, object y)
        {
            UPnPDevice device = (UPnPDevice) x;
            UPnPDevice device2 = (UPnPDevice) y;
            return string.Compare(device.DeviceURN, device2.DeviceURN);
        }
    }
}

