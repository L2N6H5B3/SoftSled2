using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Rtsp.Messages
{
    /// <summary>
    /// RTSP SET_PARAMETER request — used for mid-session parameter
    /// updates such as <c>Buffer-Info.dlna.org</c> when the
    /// AvailableBandwidth or OptimisedPreroll hint from WMC changes.
    /// </summary>
    public class RtspRequestSetParameter : RtspRequest
    {
        public RtspRequestSetParameter()
        {
            Command = "SET_PARAMETER * RTSP/1.0";
        }
    }
}
