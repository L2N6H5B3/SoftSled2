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
