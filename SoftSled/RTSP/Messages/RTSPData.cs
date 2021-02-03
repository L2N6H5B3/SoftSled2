using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Rtsp.Messages
{
    /// <summary>
    /// Message wich represent data. ($ limited message)
    /// </summary>
    public class RtspData : RtspChunk
    {

        /// <summary>
        /// Logs the message to debug.
        /// </summary>
        public override void LogMessage()
        {
            System.Diagnostics.Debug.WriteLine("Data message");
            if (Data == null)
                System.Diagnostics.Debug.WriteLine("Data : null");
            else
                System.Diagnostics.Debug.WriteLine($"Data length :-{Data.Length}-");
        }

        public int Channel { get; set; }

        /// <summary>
        /// Clones this instance.
        /// <remarks>Listner is not cloned</remarks>
        /// </summary>
        /// <returns>a clone of this instance</returns>
        public override object Clone()
        {
            RtspData result = new RtspData();
            result.Channel = this.Channel;
            if (this.Data != null)
                result.Data = this.Data.Clone() as byte[];
            result.SourcePort = this.SourcePort;
            return result;
        }
    }
}
