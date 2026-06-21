using SoftSled.Components.Configuration;
using System.Collections.Generic;
using System.Configuration;
using System.Security.Cryptography;

namespace SoftSled.Components.Extender {
    public class ExtenderCapabilities {

        private readonly WMCRenderMode WMCRenderMode;
        private readonly List<DeviceCapability> Capabilities;
        
        public ExtenderCapabilities() {

            // Create List to hold Device Capabilities
            Capabilities = new List<DeviceCapability>();

            // Read Config
            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            WMCRenderMode = config.EnableRemoteRendering ? WMCRenderMode.RUI : WMCRenderMode.GDI;


            #region Animation #################################################

            Capabilities.Add(new DeviceCapability("2DA", "Is 2D animation allowed?", "Animation", config.EnableRemoteRendering ? true : config.Enable2DAnimations));
            Capabilities.Add(new DeviceCapability("ANI", "Is intensive animation allowed?", "Animation", config.EnableRemoteRendering ? true : config.EnableIntenseAnimations));

            #endregion ########################################################

            #region No Category ###############################################

            // No Category
            Capabilities.Add(new DeviceCapability("APP", "Is tray applet allowed?", "None", false));
            Capabilities.Add(new DeviceCapability("ARA", "Is auto restart allowed?", "None", false));
            Capabilities.Add(new DeviceCapability("CLO", "Is the close button shown?", "None", false));
            Capabilities.Add(new DeviceCapability("DES", "Is MCE a Windows shell?", "None", false));
            Capabilities.Add(new DeviceCapability("DOC", "Is my Documents populated?", "None", false));

            #endregion ########################################################

            #region Audio #####################################################

            // Audio
            Capabilities.Add(new DeviceCapability("AUD", "Is audio allowed?", "Audio", true));
            Capabilities.Add(new DeviceCapability("AUR", "Is audio Non WMP?", "Audio", true));

            #endregion ########################################################

            #region Captions ##################################################

            // Captions
            Capabilities.Add(new DeviceCapability("BLB", "Is black letters box needed?", "Captions", false));
            Capabilities.Add(new DeviceCapability("CCC", "Is CC rendered by the client?", "Captions", false));

            #endregion ########################################################

            #region CD ########################################################

            // CD
            Capabilities.Add(new DeviceCapability("CDA", "Is CD playback allowed?", "CD", false));
            Capabilities.Add(new DeviceCapability("CPY", "Is CD copying allowed?", "CD", false));
            Capabilities.Add(new DeviceCapability("CRC", "Is CD burning allowed?", "CD", false));
            #endregion ########################################################

            #region DVD #######################################################

            // DVD
            Capabilities.Add(new DeviceCapability("DRC", "Is DVD burning allowed?", "DVD", false));
            Capabilities.Add(new DeviceCapability("DVD", "Is DVD playback allowed?", "DVD", true));
            #endregion ########################################################

            #region Extender ##################################################

            // Extender
            Capabilities.Add(new DeviceCapability("EXT", "Are Extender Settings allowed?", "Extender", true));

            #endregion ########################################################

            #region Unknown ###################################################

            // Unknown
            Capabilities.Add(new DeviceCapability("FPD", "Is FPD allowed?", "Unknown", false));

            #endregion ########################################################

            #region 2-Feet Content ############################################

            Capabilities.Add(new DeviceCapability("H02", "Is 2 feet help allowed?", "2FootContent", true));
            Capabilities.Add(new DeviceCapability("WE2", "Is 2 feet web content allowed?", "2FootContent", true));

            #endregion ########################################################

            #region 10-Feet Content ###########################################

            Capabilities.Add(new DeviceCapability("H10", "Is 10 feet help allowed?", "10FootContent", true));
            Capabilities.Add(new DeviceCapability("WEB", "Is 10 feet web content allowed? ", "10FootContent", true));

            #endregion ########################################################

            #region ContentSupport ############################################

            // ContentSupport
            Capabilities.Add(new DeviceCapability("SDN", "Is SD content allowed by the network?", "ContentSupport", true));
            Capabilities.Add(new DeviceCapability("HDN", "Is HD content allowed by the network?", "ContentSupport", true));
            Capabilities.Add(new DeviceCapability("HDV", "Is HD content allowed?", "ContentSupport", config.EnableHdContent));
            Capabilities.Add(new DeviceCapability("HTM", "Is HTML supported?", "ContentSupport", false));
            Capabilities.Add(new DeviceCapability("VIZ", "Is WMP visualisation allowed?", "ContentSupport", true));
            Capabilities.Add(new DeviceCapability("W32", "Is Win32 content allowed?", "ContentSupport", false));
            Capabilities.Add(new DeviceCapability("ONS", "Is online spotlight allowed?", "ContentSupport", false));
            Capabilities.Add(new DeviceCapability("SYN", "Is transfer to a device allowed?", "ContentSupport", false));

            #endregion ########################################################

            #region Display ###################################################

            Capabilities.Add(new DeviceCapability("MAR", "Are over-scan margins needed?", "Display", config.EnableOverscanMargin));
            Capabilities.Add(new DeviceCapability("SCR", "Is a native screensaver required?", "Display", true));
            Capabilities.Add(new DeviceCapability("WID", "Is wide screen enabled?", "Display", (float)config.SessionWidth/(float)config.SessionHeight > 1.5));
            Capabilities.Add(new DeviceCapability("WIN", "Is window mode allowed?", "Display", false));
            Capabilities.Add(new DeviceCapability("SDM", "Is a screen data mode workaround needed? (Not Supported on Win7+)", "Display", false));


            #endregion ########################################################

            #region Photos ####################################################

            Capabilities.Add(new DeviceCapability("PHO", "Are advanced photo features allowed?", "Photos", true));

            #endregion ########################################################

            #region UI ########################################################

            Capabilities.Add(new DeviceCapability("MUT", "Is mute ui allowed?", "UI", true));
            Capabilities.Add(new DeviceCapability("VOL", "Is volume UI allowed?", "UI", true));
            Capabilities.Add(new DeviceCapability("POP", "Are Pop ups allowed?", "UI", config.EnablePopups));
            Capabilities.Add(new DeviceCapability("SOU", "Is UI sound supported?", "UI", config.EnableUiSounds));
            Capabilities.Add(new DeviceCapability("TBA", "Is a Toolbar allowed?", "UI", config.EnableToolbar));
            Capabilities.Add(new DeviceCapability("TBP", "Is Toolbar persistent?", "UI", false));
            Capabilities.Add(new DeviceCapability("TVS", "Is a TV skin used?", "UI", config.UseTvSkin));


            #endregion ########################################################

            #region Video #####################################################

            Capabilities.Add(new DeviceCapability("VID", "Is video allowed?", "Video", true));
            Capabilities.Add(new DeviceCapability("ZOM", "Is video zoom mode allowed?", "Video", true));
            Capabilities.Add(new DeviceCapability("NLZ", "Is nonlinear zoom supported?", "Video", true));
            Capabilities.Add(new DeviceCapability("RSZ", "Is raw stretched zoom supported?", "Video", true));

            #endregion ########################################################

            #region Input #####################################################

            Capabilities.Add(new DeviceCapability("REM", "Is input treated as if from a remote?", "Input", true));

            #endregion ########################################################

            #region Rendering #################################################

            Capabilities.Add(new DeviceCapability("HCS", "Is High Contrast modes supported?", "Rendering", true));
            Capabilities.Add(new DeviceCapability("SUP", "Is RDP super blt allowed?", "Rendering", true));
            Capabilities.Add(new DeviceCapability("GDI", "Is GDI renderer used?", "Rendering", !config.EnableRemoteRendering));
            Capabilities.Add(new DeviceCapability("RUI", "Is remote UI rendering supported?", "Rendering", config.EnableRemoteRendering));
            Capabilities.Add(new DeviceCapability("BIG", "Is remote UI renderer big-endian?", "Rendering", config.EnableRemoteRendering));

            #endregion ########################################################

        }

        public List<DeviceCapability> GetDeviceCapabilities() {
            return Capabilities;
        }

        public WMCRenderMode GetRenderMode() {
            return WMCRenderMode;
        }
    }

    public class DeviceCapability {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public bool Enabled { get; set; }

        public DeviceCapability(string Name, string Description, string Category, bool Enabled) {
            this.Name = Name;
            this.Description = Description;
            this.Category = Category;
            this.Enabled = Enabled;
        }
    }

    public enum WMCRenderMode {
        GDI, // GDI Renderer via RDP Console
        RUI  // Remote UI Renderer via RRSP2
    }
}
