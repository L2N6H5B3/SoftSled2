using System.Collections.Generic;

namespace SoftSled.Components.Configuration {
    public class SoftSledConfig {
        public bool IsPaired = false;
        public string DeviceUDN = "";
        public string RdpLoginHost = "";
        public string RdpLoginUserName = "";
        public string RdpLoginPassword = "";
        public bool EnableRemoteRendering = true;        
        public bool EnableOverscanMargin = false;
        public bool Enable2DAnimations = true;
        public bool EnableIntenseAnimations = true;
    }
}
