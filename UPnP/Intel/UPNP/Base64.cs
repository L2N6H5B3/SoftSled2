namespace Intel.UPNP
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    public class Base64
    {
        public static string Base64ToString(string b64)
        {
            byte[] bytes = Decode(b64);
            UTF8Encoding encoding = new UTF8Encoding();
            return encoding.GetString(bytes);
        }

        public static byte[] Decode(string Text)
        {
            FromBase64Transform transform = new FromBase64Transform();
            byte[] bytes = new UTF8Encoding().GetBytes(Text);
            byte[] outputBuffer = new byte[bytes.Length * 3];
            byte[] destinationArray = new byte[transform.TransformBlock(bytes, 0, bytes.Length, outputBuffer, 0)];
            Array.Copy(outputBuffer, 0, destinationArray, 0, destinationArray.Length);
            return destinationArray;
        }

        public static string Encode(byte[] buffer)
        {
            return Encode(buffer, 0, buffer.Length);
        }

        public static string Encode(byte[] buffer, int offset, int length)
        {
            byte[] buffer2;
            length += offset;
            ToBase64Transform transform = new ToBase64Transform();
            MemoryStream stream = new MemoryStream();
            int inputOffset = offset;
            int inputCount = 3;
            if (length < 3)
            {
                inputCount = length;
            }
            do
            {
                buffer2 = transform.TransformFinalBlock(buffer, inputOffset, inputCount);
                inputOffset += inputCount;
                if ((length - inputOffset) < inputCount)
                {
                    inputCount = length - inputOffset;
                }
                stream.Write(buffer2, 0, buffer2.Length);
            }
            while (inputOffset < length);
            buffer2 = stream.ToArray();
            stream.Close();
            UTF8Encoding encoding = new UTF8Encoding();
            return encoding.GetString(buffer2);
        }

        public static string StringToBase64(string TheString)
        {
            UTF8Encoding encoding = new UTF8Encoding();
            return Encode(encoding.GetBytes(TheString));
        }
    }
}

