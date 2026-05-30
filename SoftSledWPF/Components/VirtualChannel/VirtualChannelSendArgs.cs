using System;

namespace SoftSled.Components.VirtualChannel {
    class VirtualChannelSendArgs : EventArgs {
        public string channelName;
        public byte[] data;
        public dynamic additional;

        public VirtualChannelSendArgs(string channelName, byte[] data, dynamic additional = null) {
            this.channelName = channelName;
            this.data = data;
            this.additional = additional;
        }
    }
}
