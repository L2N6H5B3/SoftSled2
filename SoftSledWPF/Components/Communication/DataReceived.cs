using System;

namespace SoftSled.Components.Communication {

    public class DataReceived : EventArgs {
        public string channelName;
        public byte[] data;

        public DataReceived(string channelName, byte[] data) {
            this.channelName = channelName;
            this.data = data;
        }
    }
}
