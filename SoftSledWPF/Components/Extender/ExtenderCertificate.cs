using System;
using System.IO;
using System.Text;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

using BcX509Cert = Org.BouncyCastle.X509.X509Certificate;
using BcBigInteger = Org.BouncyCastle.Math.BigInteger;

namespace SoftSled.Components.ExtenderCertificate {
    /// <summary>
    /// Builds Media Center Extender device certificates signed by a SoftSled CA.
    ///
    /// The produced cert structurally matches the original Linksys / Cisco DMA device
    /// certs that real WMC extenders presented: SHA-1/RSA-2048, Client Authentication +
    /// the MCE device OID (1.3.6.1.4.1.311.10.5.12) in the EKU, a UUID-scheme URI in
    /// the SAN, and the Microsoft Certificate Template Name extension carrying
    /// "IPSECIntermediateOffline" as a BMPString.
    ///
    /// CRL Distribution Points and Authority Information Access extensions are
    /// intentionally omitted - the patched Mcx2Prov uses CERT_CHAIN_POLICY_BASE with
    /// IGNORE_END_REV_UNKNOWN, so a leaf with unknown revocation status is accepted.
    /// </summary>
    public static class ExtenderCertGenerator {
        /// <summary>
        /// Generates a new extender certificate signed by the given CA cert + private key.
        /// </summary>
        public static GeneratedExtenderCert Generate(BcX509Cert caCert, AsymmetricKeyParameter caPrivateKey, string subjectCommonName = "SoftSled Extender", Guid? uuid = null, TimeSpan? validity = null, string pfxPassword = "") {
            if (caCert == null) throw new ArgumentNullException(nameof(caCert));
            if (caPrivateKey == null) throw new ArgumentNullException(nameof(caPrivateKey));
            if (!caPrivateKey.IsPrivate)
                throw new ArgumentException("CA key must be a private key.", nameof(caPrivateKey));

            var random = new SecureRandom();
            var deviceUuid = uuid ?? Guid.NewGuid();
            var lifetime = validity ?? TimeSpan.FromDays(3650);

            // -- 1. Fresh RSA-2048 keypair for the extender --
            var keyGen = new RsaKeyPairGenerator();
            keyGen.Init(new KeyGenerationParameters(random, 2048));
            var extenderKeyPair = keyGen.GenerateKeyPair();

            // -- 2. Build the certificate --
            var certGen = new X509V3CertificateGenerator();

            // Positive random 20-byte serial (159 bits guarantees the sign bit is clear)
            var serial = BigIntegers.CreateRandomBigInteger(159, random);
            certGen.SetSerialNumber(serial);

            certGen.SetIssuerDN(caCert.SubjectDN);
            certGen.SetSubjectDN(new X509Name("CN=" + subjectCommonName));

            var notBefore = DateTime.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.Add(lifetime);
            certGen.SetNotBefore(notBefore);
            certGen.SetNotAfter(notAfter);

            certGen.SetPublicKey(extenderKeyPair.Public);

            // Key Usage (not critical, matching the original Linksys DMA cert)
            certGen.AddExtension(X509Extensions.KeyUsage, false,
                new KeyUsage(
                    KeyUsage.DigitalSignature |
                    KeyUsage.NonRepudiation |
                    KeyUsage.KeyEncipherment |
                    KeyUsage.DataEncipherment));

            // Extended Key Usage: clientAuth + szOID_MCEX_DEVICE
            certGen.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                new ExtendedKeyUsage(new[]
                {
                    KeyPurposeID.IdKPClientAuth,
                    new DerObjectIdentifier("1.3.6.1.4.1.311.10.5.12")
                }));

            // Subject Alternative Name: URI:uuid:<UUID>
            var uriValue = "uuid:" + deviceUuid.ToString().ToUpperInvariant();
            certGen.AddExtension(X509Extensions.SubjectAlternativeName, false,
                new GeneralNames(new GeneralName(GeneralName.UniformResourceIdentifier, uriValue)));

            // Subject Key Identifier (SHA-1 hash of the new public key)
            certGen.AddExtension(X509Extensions.SubjectKeyIdentifier, false,
                new SubjectKeyIdentifierStructure(extenderKeyPair.Public));

            // Authority Key Identifier referencing the CA - prefer the CA's
            // existing SKI extension value so the KeyID byte-matches whatever the
            // CA published, falling back to computing it from the CA's public key.
            AuthorityKeyIdentifier aki;
            var caSkiExt = caCert.GetExtensionValue(X509Extensions.SubjectKeyIdentifier);
            if (caSkiExt != null) {
                var skiOctets = Asn1OctetString.GetInstance(caSkiExt).GetOctets();
                var caSki = SubjectKeyIdentifier.GetInstance(Asn1Object.FromByteArray(skiOctets));
                aki = new AuthorityKeyIdentifier(caSki.GetKeyIdentifier());
            } else {
                aki = new AuthorityKeyIdentifierStructure(caCert.GetPublicKey());
            }
            certGen.AddExtension(X509Extensions.AuthorityKeyIdentifier, false, aki);

            // Microsoft Certificate Template Name (BMPString "IPSECIntermediateOffline")
            certGen.AddExtension(
                new DerObjectIdentifier("1.3.6.1.4.1.311.20.2"),
                false,
                new DerBmpString("IPSECIntermediateOffline"));

            // -- 3. Sign with CA private key, SHA-1 (matching the original device certs) --
            var signatureFactory = new Asn1SignatureFactory("SHA1WithRSA", caPrivateKey, random);
            var cert = certGen.Generate(signatureFactory);

            // -- 4. Assemble outputs --
            var certDer = cert.GetEncoded();
            var pfx = BuildPfx(cert, extenderKeyPair.Private, caCert, pfxPassword, random);
            var keyXml = BuildRsaXml((RsaPrivateCrtKeyParameters)extenderKeyPair.Private);

            return new GeneratedExtenderCert(cert, deviceUuid, keyXml, certDer, pfx);
        }

        /// <summary>
        /// Loads a CA cert + private key from PEM files. Both may be in the same file.
        /// </summary>
        public static CaMaterial LoadCaFromPem(string certPath, string keyPath) {
            BcX509Cert cert = null;
            AsymmetricKeyParameter privateKey = null;

            foreach (var path in new[] { certPath, keyPath }) {
                using (var sr = new StreamReader(path)) {
                    var pemReader = new PemReader(sr);
                    object obj;
                    while ((obj = pemReader.ReadObject()) != null) {
                        switch (obj) {
                            case BcX509Cert c:
                                cert = c;
                                break;
                            case AsymmetricCipherKeyPair kp:
                                privateKey = kp.Private;
                                break;
                            case AsymmetricKeyParameter ak when ak.IsPrivate:
                                privateKey = ak;
                                break;
                        }
                    }
                }
            }

            if (cert == null)
                throw new InvalidOperationException("Could not find a CA certificate in " + certPath);
            if (privateKey == null)
                throw new InvalidOperationException("Could not find a private key in " + keyPath);

            return new CaMaterial(cert, privateKey);
        }

        // ---- internals ----

        static byte[] BuildPfx(
            BcX509Cert leafCert,
            AsymmetricKeyParameter leafKey,
            BcX509Cert caCert,
            string password,
            SecureRandom random) {
            var store = new Pkcs12StoreBuilder().Build();
            var leafEntry = new X509CertificateEntry(leafCert);
            var caEntry = new X509CertificateEntry(caCert);
            store.SetKeyEntry("SoftSled Extender",
                new AsymmetricKeyEntry(leafKey),
                new[] { leafEntry, caEntry });

            using (var ms = new MemoryStream()) {
                store.Save(ms, (password ?? string.Empty).ToCharArray(), random);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Serializes a BouncyCastle RSA private key to the .NET RSAKeyValue XML format
        /// that <see cref="System.Security.Cryptography.RSA.ToXmlString"/> produces.
        /// </summary>
        static string BuildRsaXml(RsaPrivateCrtKeyParameters p) {
            int modulusBytes = (p.Modulus.BitLength + 7) / 8;       // 256 for 2048-bit
            int halfBytes = (modulusBytes + 1) / 2;              // 128 for 2048-bit

            var sb = new StringBuilder();
            sb.Append("<RSAKeyValue>\n");
            sb.Append("  <Modulus>").Append(Pad(p.Modulus, modulusBytes)).Append("</Modulus>\n");
            sb.Append("  <Exponent>").Append(Raw(p.PublicExponent)).Append("</Exponent>\n");
            sb.Append("  <P>").Append(Pad(p.P, halfBytes)).Append("</P>\n");
            sb.Append("  <Q>").Append(Pad(p.Q, halfBytes)).Append("</Q>\n");
            sb.Append("  <DP>").Append(Pad(p.DP, halfBytes)).Append("</DP>\n");
            sb.Append("  <DQ>").Append(Pad(p.DQ, halfBytes)).Append("</DQ>\n");
            sb.Append("  <InverseQ>").Append(Pad(p.QInv, halfBytes)).Append("</InverseQ>\n");
            sb.Append("  <D>").Append(Pad(p.Exponent, modulusBytes)).Append("</D>\n");
            sb.Append("</RSAKeyValue>");
            return sb.ToString();
        }

        static string Raw(BcBigInteger n) {
            return Convert.ToBase64String(n.ToByteArrayUnsigned());
        }

        static string Pad(BcBigInteger n, int length) {
            var raw = n.ToByteArrayUnsigned();
            if (raw.Length == length) return Convert.ToBase64String(raw);
            if (raw.Length > length)
                throw new InvalidOperationException("BigInteger overflows the expected RSA component length.");
            var padded = new byte[length];
            Buffer.BlockCopy(raw, 0, padded, length - raw.Length, raw.Length);
            return Convert.ToBase64String(padded);
        }
    }

    /// <summary>
    /// CA cert + private key pair, the input to <see cref="ExtenderCertGenerator.Generate"/>.
    /// </summary>
    public sealed class CaMaterial {
        public BcX509Cert Certificate { get; }
        public AsymmetricKeyParameter PrivateKey { get; }

        public CaMaterial(BcX509Cert cert, AsymmetricKeyParameter privateKey) {
            Certificate = cert;
            PrivateKey = privateKey;
        }
    }

    /// <summary>
    /// Result of a successful extender cert generation.
    /// </summary>
    public sealed class GeneratedExtenderCert {
        public BcX509Cert Certificate { get; }
        public Guid Uuid { get; }
        public string PrivateKeyXml { get; }
        public byte[] CertificateDer { get; }
        public byte[] PfxBytes { get; }

        public string Thumbprint {
            get {
                var sha1 = new Org.BouncyCastle.Crypto.Digests.Sha1Digest();
                sha1.BlockUpdate(CertificateDer, 0, CertificateDer.Length);
                var hash = new byte[sha1.GetDigestSize()];
                sha1.DoFinal(hash, 0);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.AppendFormat("{0:X2}", b);
                return sb.ToString();
            }
        }

        public GeneratedExtenderCert(
            BcX509Cert cert, Guid uuid, string xml, byte[] der, byte[] pfx) {
            Certificate = cert;
            Uuid = uuid;
            PrivateKeyXml = xml;
            CertificateDer = der;
            PfxBytes = pfx;
        }

        /// <summary>Writes the four standard artifacts (.cer / .pem / .pfx / _PrivateKey.xml).</summary>
        public void SaveTo(string outputDir, string baseName) {
            Directory.CreateDirectory(outputDir);
            var basePath = Path.Combine(outputDir, baseName);

            File.WriteAllBytes(basePath + ".cer", CertificateDer);
            File.WriteAllText(basePath + ".pem",
                "-----BEGIN CERTIFICATE-----\n" +
                Convert.ToBase64String(CertificateDer, Base64FormattingOptions.InsertLineBreaks) +
                "\n-----END CERTIFICATE-----\n");
            File.WriteAllBytes(basePath + ".pfx", PfxBytes);
            File.WriteAllText(basePath + "_PrivateKey.xml", PrivateKeyXml);
        }
    }
}
