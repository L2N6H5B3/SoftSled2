using System;
using System.Security.Cryptography;
using System.Text;

namespace SoftSled
{
    public class RSAEncoder
    {
        // Use a member variable to hold the RSA key for encoding and decoding.
        public RSA rsaKey;

        static void Main2(string message)
        {
            RSAEncoder rsaEncoder = new RSAEncoder();
            rsaEncoder.InitializeKey(RSA.Create());

            Console.WriteLine("Encoding the following message:");
            Console.WriteLine(message);
            byte[] encodedMessage = rsaEncoder.EncodeMessage(message);
            Console.WriteLine("Resulting message encoded:");
            Console.WriteLine(Encoding.ASCII.GetString(encodedMessage));

            string decodedMessage = rsaEncoder.DecodeMessage(encodedMessage);
            Console.WriteLine("Resulting message decoded:");
            Console.WriteLine(decodedMessage);

            // Construct a formatter to demonstrate how to set each property.
            rsaEncoder.ConstructFormatter();

            // Construct a deformatter to demonstrate how to set each property.
            rsaEncoder.ConstructDeformatter();

            Console.WriteLine("This sample completed successfully; " +
                "press Enter to exit.");
            Console.ReadLine();
        }

        // Initialize an rsaKey member variable with the specified RSA key.
        public void InitializeKey(RSA key)
        {
            rsaKey = key;
        }

        // Use the RSAPKCS1KeyExchangeDeformatter class to decode the 
        // specified message.
        public byte[] EncodeMessage(string message)
        {
            byte[] encodedMessage = null;

            try
            {
                // Construct a formatter with the specified RSA key.
                RSAPKCS1KeyExchangeFormatter keyEncryptor =
                    new RSAPKCS1KeyExchangeFormatter(rsaKey);

                // Convert the message to bytes to create the encrypted data.
                byte[] byteMessage = Encoding.ASCII.GetBytes(message);
                encodedMessage = keyEncryptor.CreateKeyExchange(byteMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unexpected exception caught:" + ex.ToString());
            }

            return encodedMessage;
        }

        // Use the RSAPKCS1KeyExchangeDeformatter class to decode the
        // specified message.
        public string DecodeMessage(byte[] encodedMessage)
        {
            string decodedMessage = null;

            try
            {
                // Construct a deformatter with the specified RSA key.
                RSAPKCS1KeyExchangeDeformatter keyDecryptor =
                    new RSAPKCS1KeyExchangeDeformatter(rsaKey);

                // Decrypt the encoded message.
                byte[] decodedBytes =
                    keyDecryptor.DecryptKeyExchange(encodedMessage);

                // Retrieve a string representation of the decoded message.
                decodedMessage = Encoding.ASCII.GetString(decodedBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unexpected exception caught:" + ex.ToString());
            }

            return decodedMessage;
        }

        // Create an RSAPKCS1KeyExchangeFormatter object with a new RSA key.
        // Display its properties to the console.
        public void ConstructFormatter()
        {
            // Construct an empty Optimal Asymmetric Encryption Padding (OAEP)
            // key exchange.
            RSAPKCS1KeyExchangeFormatter rsaFormatter =
                new RSAPKCS1KeyExchangeFormatter();

            // Create an RSAKey and set it into the specified 
            // RSAPKCS1KeyExchangeFormatter.
            RSA key = RSA.Create();
            rsaFormatter.SetKey(key);

            // Create a random number using RNGCryptoServiceProvider.
            RNGCryptoServiceProvider ring = new RNGCryptoServiceProvider();
            rsaFormatter.Rng = ring;

            Console.WriteLine();
            Console.WriteLine("**" + rsaFormatter.ToString() + "**");
            Console.Write("The following random number was generated using the ");
            Console.WriteLine("class:");
            Console.WriteLine(rsaFormatter.Rng);

            string xmlParameters = rsaFormatter.Parameters;

            Console.WriteLine();
            Console.WriteLine("The RSA formatter has the following parameters:");
            Console.WriteLine(rsaFormatter.Parameters);
        }

        // Create an RSAPKCS1KeyExchangeDeformatter object with a new RSA key.
        // Display its properties to the console.
        public void ConstructDeformatter()
        {
            // Construct an empty OAEP key exchange.
            RSAPKCS1KeyExchangeDeformatter rsaDeformatter =
                new RSAPKCS1KeyExchangeDeformatter();

            // Create an RSAKey and set it into the specified 
            // RSAPKCS1KeyExchangeFormatter.
            RSA key = RSA.Create();
            rsaDeformatter.SetKey(key);

            RNGCryptoServiceProvider ring = new RNGCryptoServiceProvider();
            rsaDeformatter.RNG = ring;

            Console.WriteLine();
            Console.WriteLine("**" + rsaDeformatter.ToString() + "**");

            string xmlParameters = rsaDeformatter.Parameters;

            Console.WriteLine();
            Console.WriteLine("The RSA deformatter has the following ");
            Console.WriteLine("parameters:" + xmlParameters);
        }
    }
}