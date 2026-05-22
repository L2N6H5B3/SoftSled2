using System;

namespace SoftSled.Components {
    class VirtualChannelSendArgs : EventArgs {
        public string channelName;
        public byte[] data;

        public VirtualChannelSendArgs(string channelName, byte[] data) {
            this.channelName = channelName;
            this.data = data;
        }
    }
}
