using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Intel.UPNP;
using Intel.Utilities;
using SoftSled.Components;

namespace SoftSled
{
    public partial class Form1 : Form
    {
        private Logger m_logger;
        private ExtenderDevice m_device;
        private UPnPDeviceWatcher m_deviceWatcher;
        private RSAEncoder m_rsaEncoder;

        public Form1()
        {
            InitializeComponent();
        }

       

        void InitialiseLogger()
        {
            // For now simply hardcode the logger.
            m_logger = new TextBoxLogger(txtLog, this);
        }


        private void Form1_Load(object sender, EventArgs e)
        {

            InitialiseLogger();

            m_logger.LogInfo("OpenSoftSled (http://www.codeplex.com/softsled");


            // For now we are passing the instance of the logger. Maybe we should place the logger into a global static class in the future?
            m_device = new ExtenderDevice(m_logger);

            #region Old
            //device = UPnPDevice.CreateRootDevice(1801, 1.0, "\\XD\\DeviceDescription.xml");
            //device.FriendlyName = "SoftSled Media Center Extender";
            //device.Manufacturer = "SoftSled Project";
            //device.ManufacturerURL = "http://softsled.net";
            //device.ModelName = "SoftSled Extender";
            //device.ModelURL = new Uri("http://softsled.net");
            //device.ModelDescription = "SoftSled Extender Software";
            //device.DeviceURN = "urn:schemas-microsoft-com:device:MediaCenterExtenderMFD:1";
            //device.HasPresentation = false;
            //device.LocationURL = "/XD/DeviceDescription.xml";
            //device.AddCustomFieldInDescription("X_deviceCategory", "MediaDevices", "http://schemas.microsoft.com/windows/pnpx/2005/11");

            //UPnPDevice subDevice1 = UPnPDevice.CreateEmbeddedDevice(1.0, "840a20cc-a078-4d53-ac54-56f4972851e5");
            //subDevice1.DeviceURN = "urn:schemas-microsoft-com:device:MediaCenterExtender:1";
            //subDevice1.FriendlyName = "SoftSled Media Center Extender";
            //subDevice1.Manufacturer = "SoftSled Project";
            //subDevice1.ModelName = "SoftSled Extender";
            //subDevice1.ModelURL = new Uri("http://softsled.net");
            //subDevice1.ModelDescription = "SoftSled Extender Software";
            //subDevice1.AddCustomFieldInDescription("X_compatibleId", "MICROSOFT_MCX_0001", "http://schemas.microsoft.com/windows/pnpx/2005/11");
            //subDevice1.AddCustomFieldInDescription("X_deviceCategory", "MediaDevices", "http://schemas.microsoft.com/windows/pnpx/2005/11");

            //subDevice1.AddService(new TrustAgreementService());

            //UTF8Encoding utf = new UTF8Encoding();

            //textBox1.Text = utf.GetString(subDevice1.Services[0].GetSCPDXml());

            //device.AddDevice(subDevice1);

            //device.AddService(new NullService());

            //subDevice1.Services[0].OnUPnPEvent += new UPnPService.UPnPEventHandler(Form1_OnUPnPEvent);

            //deviceWatcher = new UPnPDeviceWatcher(device);

            //deviceWatcher.OnSniff += new UPnPDeviceWatcher.SniffHandler(deviceWatcher_OnSniff);

            //deviceWatcher.OnSniffPacket += new UPnPDeviceWatcher.SniffPacketHandler(deviceWatcher_OnSniffPacket);
            #endregion

            m_rsaEncoder = new RSAEncoder();
            m_rsaEncoder.InitializeKey(RSA.Create());
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            m_device.Stop();
        }

        public void deviceWatcher_OnSniffPacket(HTTPMessage Packet)
        {
            textBox1.Text += Packet.StringPacket;
        }
        public void deviceWatcher_OnSniff(byte[] raw, int offset, int length)
        {
            textBox1.Text += "device sniffed : " + length + "\r\n";
        }
        void factory_OnDevice(UPnPDeviceFactory sender, UPnPDevice device, Uri URL)
        {
            device.Advertise();
        }

        private static string getServicesFromDevice(string firewallIP)
        {
            //To send a broadcast and get responses from all, send to 239.255.255.250
            string queryResponse = "";
            try
            {
                string query = "M-SEARCH * HTTP/1.1\r\n" +
                "Host:" + firewallIP + ":1900\r\n" +
                    //"ST:upnp:rootdevice\r\n" +
                "ST:ssdp:all\r\n" +
                "Man:\"ssdp:discover\"\r\n" +
                "MX:5\r\n" +
                "\r\n" +
                "\r\n";

                //use sockets instead of UdpClient so we can set a timeout easier
                Socket client = new Socket(AddressFamily.InterNetwork,
                SocketType.Dgram, ProtocolType.Udp);
                IPEndPoint endPoint = new
                IPEndPoint(IPAddress.Parse(firewallIP), 1900);

                //1.5 second timeout because firewall should be on same segment (fast)
                client.SetSocketOption(SocketOptionLevel.Socket,
                SocketOptionName.ReceiveTimeout, 4000);

                byte[] q = Encoding.ASCII.GetBytes(query);
                client.SendTo(q, q.Length, SocketFlags.None, endPoint);
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                EndPoint senderEP = (EndPoint)sender;

                byte[] data = new byte[1024];
                int recv = client.ReceiveFrom(data, ref senderEP);
                queryResponse = Encoding.ASCII.GetString(data);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }

            if (queryResponse.Length == 0)
                return "";


            /* QueryResult is somthing like this:
            *
            HTTP/1.1 200 OK
            Cache-Control:max-age=60
            Location:http://10.10.10.1:80/upnp/service/des_ppp.xml
            Server:NT/5.0 UPnP/1.0
            ST:upnp:rootdevice
            EXT:

            USN:uuid:upnp-InternetGatewayDevice-1_0-00095bd945a2::upnp:rootdevice
            */

            string location = "";
            string[] parts = queryResponse.Split(new string[] {
            System.Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (part.ToLower().StartsWith("location"))
                {
                    location = part.Substring(part.IndexOf(':') + 1);
                    break;
                }
            }
            if (location.Length == 0)
                return "";

            //then using the location url, we get more information:

            System.Net.WebClient webClient = new WebClient();
            try
            {
                string ret = webClient.DownloadString(location);
                Debug.WriteLine(ret);
                return ret;//return services
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                webClient.Dispose();
            }
            return "";
        }
        protected void Form1_OnUPnPEvent(UPnPService sender, long SEQ)
        {
            Debug.WriteLine(sender.ServiceID + " : " + SEQ);
            textBox1.Text += (sender.ServiceID + " : " + SEQ);
        }

       
        private void StartButton_Click(object sender, EventArgs e)
        {
            /*
            SSDP request = new SSDP(new IPEndPoint(new IPAddress(0x6900A8C0),3391),1801);
            HTTPMessage theMessage = new HTTPMessage();

            theMessage.Directive = "M-SEARCH";
            theMessage.AddTag("Host", textBox2.Text + ":1900");
            theMessage.AddTag("MX", "2");
            theMessage.AddTag("Man", "ssdp:discover");
            theMessage.AddTag("ST", "uuid:4e455447-4541-524e-4153-000da20110c6");
            textBox1.Text = theMessage.StringPacket;

            request.BroadcastData(theMessage);
             */
            //textBox1.Text = getServicesFromDevice(textBox2.Text);
            m_device.Start();

        }

        private void button1_Click(object sender, EventArgs e)
        {
            m_device.Stop();
        }

        private void DecryptButton_Click(object sender, EventArgs e)
        {
            string message = textBox1.Text;

            textBox3.Text = m_rsaEncoder.DecodeMessage(Encoding.ASCII.GetBytes(message));

            // Construct a formatter to demonstrate how to set each property.
            m_rsaEncoder.ConstructFormatter();

            // Construct a deformatter to demonstrate how to set each property.
            m_rsaEncoder.ConstructDeformatter();

        }

        private void EncryptButton_Click(object sender, EventArgs e)
        {
            string message = textBox3.Text;

            byte[] encodedMessage = m_rsaEncoder.EncodeMessage(message);

            textBox1.Text = "";
            foreach (byte theByte in encodedMessage)
            {
                textBox1.Text += " " +  Microsoft.VisualBasic.Conversion.Hex(theByte);
            }

            //textBox1.Text = Encoding.ASCII.GetString(encodedMessage);

            string decodedMessage = m_rsaEncoder.DecodeMessage(encodedMessage);

            // Construct a formatter to demonstrate how to set each property.
            m_rsaEncoder.ConstructFormatter();

            // Construct a deformatter to demonstrate how to set each property.
            m_rsaEncoder.ConstructDeformatter();
        }

        private void Base64DecryptButton_Click(object sender, EventArgs e)
        {
            textBox3.Text = Base64.Base64ToString(textBox1.Text);
        }

        private void DoStuffButton_Click(object sender, EventArgs e)
        {
            CspParameters csp = new CspParameters();

            RSACryptoServiceProvider rsaPublicOnly = new RSACryptoServiceProvider();
            RSACryptoServiceProvider rsaPrivateOnly = new RSACryptoServiceProvider();
            rsaPrivateOnly.FromXmlString(System.IO.File.ReadAllText(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\Certificates\\SoftSledPrivateKey.xml"));

            //Chilkat.PrivateKey privateKey = new Chilkat.PrivateKey();
            //bool success;
            //success = privateKey.LoadPemFile(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\Certificates\\SoftSled.key");
            //success = privateKey.SaveXmlFile(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\Certificates\\SoftSledPrivateKey.xml");

            //Host Certificate
            byte[] hostCertBytes = Convert.FromBase64String("AAABAANiMIIDXjCCAkagAwIBAgIQwbJUvtUqm5BN/V5oF+JvOjANBgkqhkiG9w0BAQUFADA3MTUwMwYDVQQDEyxNaWNyb3NvZnQgV2luZG93cyBNZWRpYSBDZW50ZXIgRXh0ZW5kZXIgSG9zdDAeFw0wODAxMTQwMjUzNDhaFw0zODAxMTMwOTE2NTBaMDcxNTAzBgNVBAMTLE1pY3Jvc29mdCBXaW5kb3dzIE1lZGlhIENlbnRlciBFeHRlbmRlciBIb3N0MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAtwJXbDjMejg+VKMLCkghR9tbpnLX0ni+tKsJ+nucZV6s/kABszxCVDeAxNiiasG26OmqEsSs3ZnrHaleIGe0b/dEnGMR3jlKrWt61KMKuItNMcXQckUU7+gAAfjdLxEicZGmorjN9ZmPYOpTZs9DL48XPM5fvASuQSXwGOe4ojdVyZ8i0zOFc1NgADGLKVqktfySYiE5yy0un7850tM2+p9JuM6XZy8B4nw9liGv7bd/iExF6uT1xka+HxhcfdLZql03ARESGtsQ5jXezyFtqfaYCaajRCtMPOrPBuQ6qhM6vB7Cg8LjXpJBap9/AB88b+z8bM4bJUkRHH5pxwU23QIDAQABo2YwZDA0BgNVHREELTArhil1dWlkOmU3MzY4MWViLTFjNjItNDA4My1iZDk5LWExYzQwOGQzMDQ1NzALBgNVHQ8EBAMCBPAwHwYDVR0lBBgwFgYIKwYBBQUHAwEGCisGAQQBgjcKBQwwDQYJKoZIhvcNAQEFBQADggEBAKZ9UbCic89BhSrfyuGo467v8mhD9yS/wJBTlnPScc5s+Ehhfsn0Va7UjkcRlEYh62sW0Kx8X/li/lBt0ldwgZZK7+NASAG7oP7udOmH+z/MGX82q4FCU0zAH9vZ+Nie++QE9/w7oxUz5zEx8GljxRAc0WKTQQyVGS56/Nt8kxYhZEQssBG7jxNZKi/sBT+DNJcgyYQM2/uu1zLF+vIYVPgigia03RLtY35lR9Mu6v5mGc0ViOVYyvLEVTDCpoXVidBR2vYPnrZ3Z+X82LJrwh+ay4HMZDHqv5cbVJnzAwugf6vVhp3BwXQCLkYFkvgjusYXFDH+DH0QIKe0ZtGnDp4=");
            byte[] hostAuthenticator = Convert.FromBase64String("8konzjVSsQbipg+YX9DJV7wEgxw=");
            byte[] hostValidateAuthenticator = Convert.FromBase64String("kyKV93eGx/2V/5Q2rDQ/HiejEKg=");
            //Device Certificate
            byte[] deviceCertBytes = Convert.FromBase64String("AAABAAPYMIID1DCCA0GgAwIBAgIHElqweu8AATAJBgUrDgMCHQUAMB0xGzAZBgNVBAMTEk1pY3Jvc29mdCBYQk9YIDM2MDAeFw0wNTExMTYxODQ0NDFaFw0yNTExMTYxNTAwMDNaMB0xGzAZBgNVBAMTEk1pY3Jvc29mdCBYQk9YIDM2MDCBnzANBgkqhkiG9w0BAQEFAAOBjQAwgYkCgYEAkdWAHtLr9wt7Tqn5SyLNpLpXeQeT6Dzas2N/GpIt3ZMtZbP6JTGMQiCEfrRKWzzPzM1qDu0zkm6yKF5YSqv0vvqw/i0clrYzJkhjh6ipLTPBMiKwrtRCAvAhUWvGU4n1MZj/QlFcznuGcFuFo0WKJaNkcqx6EtBN0tjQSYwhfvECAwEAAaOCAiQwggIgMDQGA1UdEQQtMCuGKXV1aWQ6MjAwMDAwMDAtMDAwMC0wMDAwLTAyMDAtMDAxMjVBQjA3QUVGMAsGA1UdDwQEAwIE8DAfBgNVHSUEGDAWBggrBgEFBQcDAgYKKwYBBAGCNwoFDDCCAbgGCisGAQQBgjc3AQEEggGoAagIGweu9Vg4MDM0MDQtMDAxAAAAAAAAAAAAAjA4LTIyLTA2AAEAAdLY0EmMIX7xo2RyrHoS0E2GcFuFo0WKJTGY/0JRXM578CFRa8ZTifXBMiKwrtRCAiZIY4eoqS0z+rD+LRyWtjOyKF5YSqv0vszNag7tM5JuIIR+tEpbPM8tZbP6JTGMQrNjfxqSLd2Tuld5B5PoPNp7Tqn5SyLNpJHVgB7S6/cLxZpjuBNxQ2ltX6Rzm5IXlDZ7Ae9jprHtgHCXbn7E93YleBrApSaa6v/IbeY0TAYWklBQYmjXSCgKqn51X6jiMw9k66BUfW3MZ3/OiPRSpiC0tp2wTvp2J9fL325Xq0q59Z76ufpkxs5Rt5P79UMDOWi4q+B6d2/8WQ1add1Ei2xa8R0d5GhprPjyCzmf+utanSUu5Jx0m7pGWn1ZPQGSiHC4ehw4QWuPH8X6wSxNJUm25bq6IvaT8drF+A+pLLTuLLL7mHBiLYDSCp8HdLKqAvLw/pKo56ga3rZN10n3sccYjhAUInrOD8yn5M2b0JbodPBGv5ED4GnVkU1rwaRrNjAJBgUrDgMCHQUAA4GBACYc9gZkJRrqf7IfJW7g9/GjyOGzAEN2ltXnwhHGYfrd2e9IOr189kc8K530JK4Jk/y4ZHo7pdyZj7xff3f7ByXcP5tNFMAkDG20wYyTxjN6dPXbPLBxQeyicPcrww792tntQXyf/nJVXMM0CvNFAG8qhK6wK66x+nUNnovxq8iv");
            byte[] deviceAuthenticator = Convert.FromBase64String("G1iojrtB5OaBsN61ZSiWLoCAbLc=");
            byte[] deviceValidateAuthenticator = Convert.FromBase64String("OFv3Wa2nNv9e7b4INqYUL6jMxWs=");

            byte[] hostCertBytesNew = new byte[hostCertBytes.Length - 6];
            Array.Copy(hostCertBytes, 6, hostCertBytesNew, 0, hostCertBytes.Length - 6);

            byte[] deviceCertBytesNew = new byte[deviceCertBytes.Length - 6];
            Array.Copy(deviceCertBytes, 6, deviceCertBytesNew, 0, deviceCertBytesNew.Length - 6);

            SHA1Managed sha1 = new SHA1Managed();
            byte[] hostHashValue = sha1.ComputeHash(hostCertBytesNew);
            byte[] deviceHashValue = sha1.ComputeHash(deviceCertBytesNew);

            X509Certificate2 hostCert = new X509Certificate2(hostCertBytesNew);
            X509Certificate2 deviceCert = new X509Certificate2(deviceCertBytesNew);
            System.Security.Cryptography.X509Certificates.X509Certificate2 cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\Certificates\\SoftSled.cer");
            rsaPublicOnly = (RSACryptoServiceProvider)cert.PublicKey.Key;
            byte[] encodedMessage = rsaPublicOnly.Encrypt(Encoding.UTF8.GetBytes("Test This"), false);
            byte[] decodedMessage = rsaPrivateOnly.Decrypt(encodedMessage, false);
            textBox3.Text = Encoding.UTF8.GetString(decodedMessage);
            
        }

        private void SaveCertButton_Click(object sender, EventArgs e)
        {
            FileStream fsCert = new FileStream(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Certificates\\Temp.cer",FileMode.Create);
            X509Certificate cert = Utility.ConvertBase64StringToCert(textBox1.Text);

            Byte[] byteArray = cert.Export(X509ContentType.Cert);

            fsCert.Write(byteArray, 0, byteArray.Length);
            fsCert.Close();
            cert = null;
            byteArray = null;
        }

    }
}
