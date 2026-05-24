using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

namespace SoftSled.Components.Configuration {

    class SoftSledConfigManager {
        static string XML_Path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Config.xml";

        public static SoftSledConfig ReadConfig() {
            SoftSledConfig config;

            if (!File.Exists(XML_Path)) {
                // Config file does not exist, create a default one.

                config = new SoftSledConfig();
                WriteConfig(config);

            }

            using (TextReader textReader = new StreamReader(XML_Path)) {
                XmlSerializer xmlSerializer = new XmlSerializer(typeof(SoftSledConfig));
                config = (SoftSledConfig)xmlSerializer.Deserialize(textReader);
            }

            // PlaybackEngine migration. Old configs only have the
            // UseFfmeEngine bool; new code reads the PlaybackEngine
            // enum. If the XML didn't contain PlaybackEngine, the
            // deserializer left it at the default (Ffme = 0). Cross-
            // check against UseFfmeEngine — if the legacy bool says
            // "false" (= libav) but the enum is at default Ffme, it's
            // an old config and we honour the bool. Once new code
            // writes the config back, both fields stay in sync.
            if (config.PlaybackEngine == PlaybackEngineKind.Ffme && !config.UseFfmeEngine) {
                config.PlaybackEngine = PlaybackEngineKind.DirectLibAv;
            }

            return config;
        }

        public static void WriteConfig(SoftSledConfig config) {
            if (config == null)
                throw new ArgumentNullException("config");

            using (TextWriter textWriter = new StreamWriter(XML_Path, false)) {
                XmlSerializer xmlSerializer = new XmlSerializer(typeof(SoftSledConfig));
                xmlSerializer.Serialize(textWriter, config);
            }
        }

    }
}
