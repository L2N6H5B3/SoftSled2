using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SoftSled.Components {
    class DataUtilities {

        public static byte[] GetByteSubArray(byte[] byteArray, int startPosition, int byteCount) {

            byte[] result = new byte[byteCount];

            try {
                for (int i = startPosition; i < startPosition + byteCount; i++) {

                    result[i - startPosition] = byteArray[i];

                }
            } catch (IndexOutOfRangeException) {
                //System.Diagnostics.Debug.WriteLine($"IndexOutOfRangeException: StartPosition {startPosition} ByteCount {byteCount}");
                // DEBUG PURPOSES ONLY
                string incomingByteArray = "";
                foreach (byte b in byteArray) {
                    incomingByteArray += b.ToString("X2") + " ";
                }
                // DEBUG PURPOSES ONLY
                //System.Diagnostics.Debug.WriteLine(incomingByteArray);
            }
            return result;
        }

        public static string GetByteArrayString(byte[] byteArray, int startPosition, int length) {

            byte[] result = GetByteSubArray(byteArray, startPosition, length);

            return Encoding.UTF8.GetString(result);
        }

        public static int Get4ByteInt(byte[] byteArray, int startPosition) {

            byte[] result = GetByteSubArray(byteArray, startPosition, 4);

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(result);
            }

            return BitConverter.ToInt32(result, 0);
        }

        public static long Get8ByteInt(byte[] byteArray, int startPosition) {

            byte[] result = GetByteSubArray(byteArray, startPosition, 8);

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(result);
            }

            return BitConverter.ToInt64(result, 0);
        }

        public static int Get2ByteInt(byte[] byteArray, int startPosition) {

            byte[] result = GetByteSubArray(byteArray, startPosition, 2);

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(result);
            }

            return BitConverter.ToInt16(result, 0);
        }

        public static Guid GuidFromArray(byte[] byteArray, int startPosition) {

            int byteCount = 16;

            byte[] data1 = new byte[4];
            byte[] data2 = new byte[2];
            byte[] data3 = new byte[2];
            byte[] data4 = new byte[8];

            for (int i = startPosition; i < startPosition + byteCount; i++) {
                if (i - startPosition < 4) {
                    data1[i - startPosition] = byteArray[i];
                } else if (i - startPosition < 6) {
                    data2[i - startPosition - 4] = byteArray[i];
                } else if (i - startPosition < 8) {
                    data3[i - startPosition - 6] = byteArray[i];
                } else {
                    data4[i - startPosition - 8] = byteArray[i];
                }
            }

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(data1);
                Array.Reverse(data2);
                Array.Reverse(data3);
            }

            // Create Base Byte Array
            byte[] baseArray = new byte[0];
            // Formulate Array
            IEnumerable<byte> result = data1
                // Add Data 2
                .Concat(data2)
                // Add Data 3
                .Concat(data3)
                // Add Data 4
                .Concat(data4);

            // Return the created GUID
            return new Guid(result.ToArray());
        }

        public static byte[] GuidToArray(Guid guid) {

            byte[] byteArray = Encoding.Unicode.GetBytes(guid.ToString());

            byte[] data1 = new byte[4];
            byte[] data2 = new byte[2];
            byte[] data3 = new byte[2];
            byte[] data4 = new byte[8];

            for (int i = 0; i < 16; i++) {
                if (i < 4) {
                    data1[i] = byteArray[i];
                } else if (i < 6) {
                    data2[i - 4] = byteArray[i];
                } else if (i < 8) {
                    data3[i - 6] = byteArray[i];
                } else {
                    data4[i - 8] = byteArray[i];
                }
            }

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(data1);
                Array.Reverse(data2);
                Array.Reverse(data3);
            }

            // Create Base Byte Array
            byte[] baseArray = new byte[0];
            // Formulate Array
            IEnumerable<byte> result = data1
                // Add Data 2
                .Concat(data2)
                // Add Data 3
                .Concat(data3)
                // Add Data 4
                .Concat(data4);

            // Return the created Byte Array
            return result.ToArray();
        }
    }
}
