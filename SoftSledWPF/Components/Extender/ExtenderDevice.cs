// Intel's UPnP .NET Framework Device Stack, Device Module
// Intel Device Builder Build#1.0.2777.24761

using Intel.UPNP;
using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SoftSled.Components.Extender {
    /// <summary>
    /// Main implementation of the MCX Key Exchange.
    /// </summary>
    class ExtenderDevice {
        private readonly UPnPDevice device;
        private Logger m_logger;
        private X509Certificate2 _HostCertificate;
        private string _HostCertificateString;
        private string _HostID;
        private string _HostConfirmAuthenticator;
        private string _HostValidateAuthenticator;
        private byte[] _DeviceConfirmNonce;
        private byte[] _DeviceValidateNonce;
        private int _TrustState = 1;
        private readonly string _OneTimePassword = "1234";
        private string _OneTimePasswordIter;
        private byte _Iterations;
        private byte _Iter;
        private readonly string _DeviceCertificateString;
        private readonly string _DeviceID;
        private readonly bool _UseManagedSHA1 = false;

        public ExtenderDevice(Logger logger) {

            #region Get Logger ################################################

            // Add Logger
            m_logger = logger ?? throw new ArgumentNullException("logger");

            #endregion ########################################################


            #region Get Certificate Device ID #################################

            X509Certificate2 deviceCert = new X509Certificate2(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Certificates\\Linksys2200.cer");
            string certDeviceId = "b8771bb8-5496-498b-a6c7-f2d67a1b0d96"; // default if cert doesn't contain an alternative name
            foreach (X509Extension certExtension in deviceCert.Extensions) {
                if (certExtension.Oid.FriendlyName != null && certExtension.Oid.FriendlyName.Equals("Subject Alternative Name")) {
                    certDeviceId = Encoding.ASCII.GetString(certExtension.RawData).Remove(0, 9);
                    m_logger.LogInfo("UUID from cert: " + certDeviceId);
                    break;
                }
            }

            #endregion ########################################################


            #region Create Certificate String #################################

            // Convert the device certificate to Base64 string
            byte[] deviceCertBytes = deviceCert.RawData;
            byte[] newDeviceCertBytes = new byte[deviceCertBytes.Length + 6];
            newDeviceCertBytes[0] = 0x0;
            newDeviceCertBytes[1] = 0x0;
            newDeviceCertBytes[2] = 0x1;
            newDeviceCertBytes[3] = 0x0;
            newDeviceCertBytes[4] = BitConverter.GetBytes(deviceCertBytes.Length)[1];
            newDeviceCertBytes[5] = BitConverter.GetBytes(deviceCertBytes.Length)[0];
            deviceCertBytes.CopyTo(newDeviceCertBytes, 6);
            _DeviceCertificateString = Convert.ToBase64String(newDeviceCertBytes);

            #endregion ########################################################


            #region Create Embedded Device ####################################

            UPnPDevice device1 = UPnPDevice.CreateEmbeddedDevice(1, certDeviceId);
            device1.FriendlyName = "SoftSled Media Center Extender";
            device1.Manufacturer = "SoftSled Project";
            device1.ManufacturerURL = "http://www.codeplex.com/softsled";
            device1.ModelName = "SoftSled";
            device1.ModelDescription = "SoftSled Media Center Extender";
            device1.ModelNumber = "";
            device1.HasPresentation = false;
            device1.DeviceURN = "urn:schemas-microsoft-com:device:MediaCenterExtender:1";
            device1.AddCustomFieldInDescription("X_compatibleId", "MICROSOFT_MCX_0001", "http://schemas.microsoft.com/windows/pnpx/2005/11");
            device1.AddCustomFieldInDescription("X_deviceCategory", "MediaDevices", "http://schemas.microsoft.com/windows/pnpx/2005/11");
            device1.AddCustomFieldInDescription("pakVersion", "dv2.0.0", "http://schemas.microsoft.com/windows/mcx/2007/06");
            device1.AddCustomFieldInDescription("supportedHostVersions", "pc2.0.0", "http://schemas.microsoft.com/windows/mcx/2007/06");
            device1.ContentHandler = new ContentHandler(m_logger);

            #region Add MSTA Service ##########################################

            TrustAgreementService TrustAgreementService = new TrustAgreementService(m_logger) {
                TrustState = 4,
                A_ARG_TYPE_Iteration = Convert.ToByte(5)
            };
            TrustAgreementService.External_Exchange = new TrustAgreementService.Delegate_Exchange(TrustAgreementService_Exchange);
            TrustAgreementService.External_Commit = new TrustAgreementService.Delegate_Commit(TrustAgreementService_Commit);
            TrustAgreementService.External_Validate = new TrustAgreementService.Delegate_Validate(TrustAgreementService_Validate);
            TrustAgreementService.External_Confirm = new TrustAgreementService.Delegate_Confirm(TrustAgreementService_Confirm);
            device1.AddService(TrustAgreementService);

            #endregion ########################################################


            #region Add MSRX Service ##########################################

            RemotedExperienceService RemotedExperienceService = new RemotedExperienceService(m_logger);
            RemotedExperienceService.External_AcquireNonce = new RemotedExperienceService.Delegate_AcquireNonce(RemotedExperienceService_AcquireNonce);
            RemotedExperienceService.External_Advertise = new RemotedExperienceService.Delegate_Advertise(RemotedExperienceService_Advertise);
            RemotedExperienceService.External_Inhibit = new RemotedExperienceService.Delegate_Inhibit(RemotedExperienceService_Inhibit);
            device1.AddService(RemotedExperienceService);

            #endregion ########################################################

            #endregion ########################################################


            #region Create Root Device ########################################

            device = UPnPDevice.CreateRootDevice(1800, 1.0, "\\XD");
            device.UniqueDeviceName = "68c4b624-e1a0-42c5-94b8-4f5fa6fec622"; // W7P01
            //device.UniqueDeviceName = "b8501007-688e-4939-b4a2-fd7c649cdaac";
            //device.UniqueDeviceName = "20000000-0000-0000-0200-0022483E33F6";
            //device.UniqueDeviceName = "1B19160E-0B19-433B-9315-40AD98C6F5E0";
            device.FriendlyName = "SoftSled Media Center Extender";
            device.Manufacturer = "SoftSled Project";
            device.ManufacturerURL = "http://www.codeplex.com/softsled";
            device.ModelName = "SoftSled";
            device.ModelDescription = "SoftSled Media Center Extender";
            device.ModelNumber = "";
            device.ModelURL = new Uri("http://www.codeplex.com/softsled");
            device.HasPresentation = false;
            device.DeviceURN = "urn:schemas-microsoft-com:device:MediaCenterExtenderMFD:1";
            device.AddCustomFieldInDescription("X_deviceCategory", "MediaDevices", "http://schemas.microsoft.com/windows/pnpx/2005/11");
            device.ProductCode = "";
            device.SerialNumber = "";
            device.ContentHandler = new ContentHandler(m_logger);
            device.AddService(new NullService());

            // Add Embedded Device to Root Device
            device.AddDevice(device1);

            #endregion ########################################################


            //Get the device id to use in the communication...
            _DeviceID = "uuid:" + device1.UniqueDeviceName;

            // Setting the initial value of evented variables
        }

        public void Start() {
            device.StartDevice(3391);

            m_logger.LogInfo("Started Device Broadcasting");
        }

        public void Stop() {
            device.StopDevice();

            m_logger.LogInfo("Stopped Device Broadcasting");
        }

        #region SOAP RXAD Procedures ##########################################

        private byte[] _RES_DeviceNonce = null;
        private byte[] _RES_HostNonce = null;
        private string _RES_ApplicationId = null;
        private string _RES_ApplicationVersion = null;
        private string _RES_ExperienceEndpointUri = null;
        private string _RES_ExperienceEndpointData = null;
        private string _RES_SignatureAlgorithm = null;
        private string _RES_Signature = null;
        private string _RES_HostCertificate = null;

        public void RemotedExperienceService_AcquireNonce(string HostId, out uint Nonce, out string SupportedSignatureAlgorithms, out bool AttachCertificate) {
            _RES_DeviceNonce = GenerateNonce();
            Nonce = BitConverter.ToUInt32(_RES_DeviceNonce, 0);
            SupportedSignatureAlgorithms = "rSASSA-PSS-Default-Identifier";
            AttachCertificate = false;

            m_logger.LogInfo("RemotedExperienceService_AcquireNonce(" + HostId.ToString() + ")");
        }

        public void RemotedExperienceService_Advertise(uint Nonce, string HostId, string ApplicationId, string ApplicationVersion, string ApplicationData, string HostFriendlyName, string ExperienceFriendlyName, string ExperienceIconUri, string ExperienceEndpointUri, string ExperienceEndpointData, string SignatureAlgorithm, string Signature, string HostCertificate) {
            _RES_HostNonce = BitConverter.GetBytes(Nonce);
            _RES_ApplicationId = ApplicationId;
            _RES_ApplicationVersion = ApplicationVersion;
            _RES_ExperienceEndpointUri = ExperienceEndpointUri;
            _RES_ExperienceEndpointData = ExperienceEndpointData;
            _RES_SignatureAlgorithm = SignatureAlgorithm;
            _RES_Signature = Signature;
            _RES_HostCertificate = HostCertificate;
            m_logger.LogInfo("RemotedExperienceService_Advertise(" + Nonce.ToString() + HostId.ToString() + ApplicationId.ToString() + ApplicationVersion.ToString() + ApplicationData.ToString() + HostFriendlyName.ToString() + ExperienceFriendlyName.ToString() + ExperienceIconUri.ToString() + ExperienceEndpointUri.ToString() + ExperienceEndpointData.ToString() + SignatureAlgorithm.ToString() + Signature.ToString() + HostCertificate.ToString() + ")");

            // parse endpoint data 
            Dictionary<String, String> endpointData = new Dictionary<string, string>();
            foreach (string part in ExperienceEndpointData.Split(';')) {
                string[] nameValuePair = part.Split(new char[] { '=' }, 2);
                endpointData.Add(nameValuePair[0], nameValuePair[1]);
            }

            // decrypt MCX user password with cert's signing key (private key)
            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(File.ReadAllText(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Certificates\\SoftSledPrivateKey.xml"));

            byte[] cryptedPass = Convert.FromBase64String(endpointData["encryptedpassword"]);
            string rdpPass;
            try {
                rdpPass = Encoding.ASCII.GetString(rsa.Decrypt(cryptedPass, true));
            } catch (Exception ex) {
                m_logger.LogInfo("RSA decryption of encrypted password failed " + ex.Message);
                m_logger.LogInfo("Extender Experience Pairing has failed!");
                rdpPass = "mcxpw123";
            }

            string rdpHost = ExperienceEndpointUri.Substring(6, ExperienceEndpointUri.Length - 12);
            string rdpUser = endpointData["user"];

            m_logger.LogInfo("RDP host: " + rdpHost);
            m_logger.LogInfo("RDP clear text Password: " + rdpPass);
            m_logger.LogInfo("RDP user: " + rdpUser);

            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            config.IsPaired = true;
            config.RdpLoginHost = rdpHost;
            config.RdpLoginUserName = rdpUser;
            config.RdpLoginPassword = rdpPass;
            SoftSledConfigManager.WriteConfig(config);

            m_logger.LogInfo("Extender Experience data exchanged!");
        }

        public void RemotedExperienceService_Inhibit(uint Nonce, string HostId, string ApplicationId, string ApplicationVersion, string ApplicationData, uint ReasonCode, string ReasonMessage, string SignatureAlgorithm, string Signature, string HostCertificate) {
            m_logger.LogDebug("RemotedExperienceService_Inhibit(" + Nonce.ToString() + HostId.ToString() + ApplicationId.ToString() + ApplicationVersion.ToString() + ApplicationData.ToString() + ReasonCode.ToString() + ReasonMessage.ToString() + SignatureAlgorithm.ToString() + Signature.ToString() + HostCertificate.ToString() + ")");
        }

        #endregion ############################################################


        #region SOAP MSTA Procedures ##########################################

        public void TrustAgreementService_Exchange(string HostID, string HostCertificate, byte IterationsRequired, string HostConfirmAuthenticator, out string DeviceID, out string DeviceCertificate, out string DeviceConfirmAuthenticator) {
            // 1. Check
            //   A. The <HostID>, <HostCertificate>, <IterationsRequired>, and <HostConfirmAuthenticator> elements MUST be syntactically validated.
            //   B. The <HostCertificate> (_HostCertificate) MAY be validated as per any vendor-defined rules.
            //   C. The <IterationsRequired> (N) MAY additionally be checked per vendor-defined rules.
            if (_TrustState != 1) {
                /* TODO: send soap error message, invalid */
                m_logger.LogInfo("Exchanging - TrustState Invalid");
            }

            // 2. Save Parameters
            //   _HostID, _HostCertificate, and _HostConfirmAuthenticator
            _HostID = HostID;
            _HostCertificate = Utility.Utility.ConvertBase64StringToCert(HostCertificate); // Just because
            _HostCertificateString = HostCertificate;
            _HostConfirmAuthenticator = HostConfirmAuthenticator;
            _Iterations = IterationsRequired;
            _Iter = 1;

            // 3. Generate and locally save _DeviceConfirmNonce.
            _DeviceConfirmNonce = GenerateNonce();

            // 4. Set TrustState from 1 (Exchanging) to 2 (Committing).
            _TrustState = 2;

            // 5. Start one-minute timeout timer
            // todo: time out check

            // 6. Send values back to host
            //    DeviceID - the UUID of the enclosing UPnP device, as specified in A_ARG_Type_EndpointID 
            //    DeviceCertificate - as specified in A_ARG_TYPE_Certificate 
            //    DeviceConfirmAuthenticator - an HMAC as specified in section 3.1.1, calculated as:
            //                                 Base64 (HMAC(_DeviceConfirmNonce, UTF-8 (N + OTP + _DeviceID + _DeviceCertificate) ).
            DeviceID = _DeviceID;
            DeviceCertificate = _DeviceCertificateString;
            DeviceConfirmAuthenticator = GenerateDeviceMsgAuthCode(_DeviceConfirmNonce, _Iterations, _OneTimePassword);

            //System.Threading.Thread.Sleep(5000);
            //m_logger.LogInfo("TrustAgreementService_Exchange(HostID : \"" + HostID.ToString() + "\",HostCertificate : \"" + HostCertificate.ToString() + "\",IterationsRequired : " + IterationsRequired.ToString() + ",HostConfirmAuthenticator : \"" + HostConfirmAuthenticator.ToString() + "\")");
            m_logger.LogInfo("Exchange Complete");
        }

        public void TrustAgreementService_Commit(string HostID, byte Iteration, string HostValidateAuthenticator, out string DeviceValidateAuthenticator) {

            m_logger.LogInfo($"Iteration: {Convert.ToInt32(_Iter)}");
            // 1. Check
            //    A. The <HostID>, <Iteration>, and <HostValidateAuthenticator> (_HostValidateAuthenticatorIter) elements MUST be syntactically validated.
            //    B. The <HostID> MUST match the value of the _HostID obtained in the Exchange action.
            if (_TrustState != 2) {
                /* TODO: send soap error message, invald */
                m_logger.LogInfo("Committing - TrustState Invalid");
            }
            if (HostID != _HostID) {
                /* TODO: send soap error message, invald */
                m_logger.LogInfo("Invalid HostID");
            }
            if (Iteration != _Iter) {
                /* TODO: send soap error message, invalid */
                m_logger.LogInfo("Invalid Iteration");
            }

            // 2. Save the HostValidateAuthenticator for Validation step
            _HostValidateAuthenticator = HostValidateAuthenticator;

            // 3. Set TrustState from 2 (Committing) to 3 (Validating).
            _TrustState = 3;

            // 4. Generate _DeviceValidateNonceIter
            _DeviceValidateNonce = GenerateNonce();

            // 5. Send values back to host
            //    DeviceValidateAuthenticator - an HMAC as specified in section 3.1.1, calculated as:
            //                                  Base64( HMAC( _DeviceValidateNonceIter, UTF-8( Iter + OTPIter + _DeviceID + _DeviceCertificate ) ).
            DeviceValidateAuthenticator = GenerateDeviceMsgAuthCode(_DeviceValidateNonce, _Iter, GetOTPIter());

            //System.Threading.Thread.Sleep(5000);
            //m_logger.LogInfo("TrustAgreementService_Commit(HostID : \"" + HostID.ToString() + "\",Iteration : \"" + Iteration.ToString() + "\",HostValidateAuthenticator : " + HostValidateAuthenticator.ToString() + ")");
            m_logger.LogInfo("Commit Complete");
        }

        public void TrustAgreementService_Validate(string HostID, byte Iteration, string HostValidateNonce, out string DeviceValidateNonce) {
            // 1. Check
            //    A. The <HostID>, <Iteration>, and <HostValidateNonce> (_HostValidateNonceIter) elements MUST be syntactically validated.
            //    B. The <HostID> MUST match the value of the _HostID obtained in the Exchange action.
            //    C. The <Iteration> number MUST be equal to the device's current iteration number, Iter.
            //    D. The value of HMAC(_HostValidateNonceIter, UTF-8(Iter + OTPIter + _HostID + _HostCertificate) ) calculated as specified in section 3.1.1, MUST match the _HostValidateAuthenticatorIter obtained in the Commit action.
            if (_TrustState != 3) {
                /* TODO: send soap error message, invald */
                m_logger.LogInfo("Validating - TrustState Invalid");
            }
            if (HostID != _HostID) {
                /* TODO: send soap error message, invald */
                m_logger.LogInfo("Validating - HostID Invalid");
            }
            if (Iteration != _Iter) {
                /* TODO: send soap error message, invalid */
                m_logger.LogInfo("Validating - Iteration Invalid");
            }

            string generatedHostValidateAuthenticator = GenerateHostMsgAuthCode(Convert.FromBase64String(HostValidateNonce), _Iter, GetOTPIter());

            if (_HostValidateAuthenticator != generatedHostValidateAuthenticator) {
                /* TODO: send soap error message, invalid */
                m_logger.LogInfo("Failed to validate host trust agreement, '" + _HostValidateAuthenticator + "' does not match '" + generatedHostValidateAuthenticator + "'");
                // Note, if we can't validate this how is host going to validate device?

            }

            // 3. Set TrustState from 3 (Validating) to 4 (Confirming), if this is the last iteration, or to 2 (Committing) if this is not the last iteration.
            if (_Iter == _Iterations) {
                m_logger.LogInfo("Last Iteration");
                /* Last iteration */
                _TrustState = 4;
            } else {
                _TrustState = 2;
            }

            // 2. Increment Iteration
            _Iter = Convert.ToByte(Convert.ToInt32(_Iter) + 1);

            // 4. Send values back to host
            //    DeviceValidateNonce - a Base64 encoded string of _DeviceValidateNonceIter, which is the 20-octet random number acquired in TrustAS_Commit.
            DeviceValidateNonce = Convert.ToBase64String(_DeviceValidateNonce);

            //System.Threading.Thread.Sleep(5000);
            //m_logger.LogInfo("TrustAgreementService_Validate(HostID : \"" + HostID.ToString() + "\",Iteration : \"" + Iteration.ToString() + "\",HostValidateNonce : \"" + HostValidateNonce.ToString() + "\",\"DeviceValidateNonce : \"" + DeviceValidateNonce + "\")");
            m_logger.LogInfo("Validate Complete");
        }

        public void TrustAgreementService_Confirm(string HostID, byte IterationsRequired, string HostConfirmNonce, out string DeviceConfirmNonce) {
            // 1. Check
            //    A. The <HostID>, <HostConfirmNonce>, and <IterationsRequired> (N) MUST be syntactically validated.
            //    B. The <HostID> MUST match the value of the _HostID obtained in the Exchange action.
            //    C. The value of HMAC(_HostConfirmNonce, UTF-8 (N + OTP + _HostID + _HostCertificate) ), as specified in section 3.1.1, MUST match the _HostConfirmAuthenticator acquired in the Exchange action.
            // TODO: 

            // 2. Store the following values in a tamper-proof persistent store.
            //    _HostID and _HostCertificate 
            // TODO:

            _TrustState = 0;

            // 3. Send values back to host
            //    <DeviceConfirmNonce>, a Base64 encoded string of _DeviceConfirmNonce, which is the 20 octet random number acquired in section TrustAS_Exchange.
            DeviceConfirmNonce = Convert.ToBase64String(_DeviceConfirmNonce);

            //m_logger.LogInfo("TrustAgreementService_Confirm(" + HostID.ToString() + IterationsRequired.ToString() + HostConfirmNonce.ToString() + ")");
            m_logger.LogInfo("Extender successfully exchanged certificates!");
        }

        private byte[] GenerateNonce() {
            //Generate the nonce 20-octet (160 bits)
            byte[] nonce = new byte[20];
            Random randNum = new Random();
            randNum.NextBytes(nonce);
            return nonce;
        }

        private string GenerateDeviceMsgAuthCode(byte[] key_Nonce, byte N_or_Iter, string OTP_or_OTPIter) {
            byte[] text_Utf8Concat = Encoding.UTF8.GetBytes(N_or_Iter + OTP_or_OTPIter + _DeviceID + _DeviceCertificateString);
            string reverseText = Encoding.UTF8.GetString(text_Utf8Concat);

            HMACSHA1 sha1Hashing = new HMACSHA1(key_Nonce, _UseManagedSHA1);
            byte[] hashedBytes = sha1Hashing.ComputeHash(text_Utf8Concat);

            return Convert.ToBase64String(hashedBytes);
        }

        private string GenerateHostMsgAuthCode(byte[] key_Nonce, byte N_or_Iter, string OTP_or_OTPIter) {
            byte[] text_Utf8Concat = Encoding.UTF8.GetBytes(N_or_Iter + OTP_or_OTPIter + _HostID + _HostCertificateString);
            string reverseText = Encoding.UTF8.GetString(text_Utf8Concat);

            HMACSHA1 sha1Hashing = new HMACSHA1(key_Nonce, _UseManagedSHA1);
            byte[] hashedBytes = sha1Hashing.ComputeHash(text_Utf8Concat);

            return Convert.ToBase64String(hashedBytes);
        }

        private string GetOTPIter() {
            int iterationNum = Convert.ToInt32(_Iter);
            //generate the OTP iteration string.  basically a partial of the whole string that is (string / total_iterations) in length and iteration into it.
            int lastNIter = _OneTimePassword.Length % _Iterations;
            int size = (iterationNum > (_Iterations - lastNIter)) ? Convert.ToInt32(_OneTimePassword.Length / _Iterations) + 1 : Convert.ToInt32(_OneTimePassword.Length / _Iterations);
            int start = (Convert.ToInt32(_OneTimePassword.Length / _Iterations)) * (iterationNum - 1);
            //m_logger.LogInfo("OTP_Iter for Iteration " + iterationNum + " is " + _OneTimePassword.Substring(start, size));
            return _OneTimePassword.Substring(start, size);
        }

        #endregion #############################################################
    }
}