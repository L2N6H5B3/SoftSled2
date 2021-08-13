using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SoftSled.VChan {
    class AVCTRL {

        #region Shared Functions ##############################################

        public static byte[] Encapsulate(byte[] byteArray) {

            byte[] segment1 = GetByteArrayFromInt(byteArray.Length);
            byte[] segment2 = new byte[] { 13, 0, 0, 0 };

            // Create Base Byte Array
            byte[] baseArray = new byte[0];
            // Formulate Encapsulated Array
            IEnumerable<byte> encapsulated = baseArray
                // Add Segment 1
                .Concat(segment1)
                // Add Segment 2
                .Concat(segment2)
                // Add Byte Array 
                .Concat(byteArray);

            // Return the created byte array
            return encapsulated.ToArray();
        }

        private static byte[] GetInverse4ByteArrayFromInt(int intValue) {
            byte[] intBytes = BitConverter.GetBytes(intValue);
            if (BitConverter.IsLittleEndian) {
                Array.Reverse(intBytes);
            }
            return intBytes;
        }

        private static byte[] GetByteArrayFromInt(int intValue) {
            return BitConverter.GetBytes(intValue);
        }

        #endregion ############################################################


        #region DSLR Functions ################################################

        public static byte[] CreateServiceResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get CreateService Byte Arrays
            byte[] createServicePayloadSize = new byte[] { 0, 0, 0, 4 };
            byte[] createServiceChildCount = new byte[] { 0, 0 };
            byte[] createServicePayloadS_OK = new byte[] { 0, 0, 0, 0 };

            // Create Base Byte Array
            byte[] baseArray = new byte[0];
            // Formulate full response
            IEnumerable<byte> response = baseArray
                // Add Dispatch PayloadSize
                .Concat(dispatchPayloadSize)
                // Add Dispatch ChildCount
                .Concat(dispatchChildCount)
                // Add Dispatch CallingConvention 
                .Concat(dispatchCallingConvention)
                // Add Dispatch RequestHandle
                .Concat(dispatchRequestHandle)
                // Add CreateService PayloadSize
                .Concat(createServicePayloadSize)
                // Add CreateService ChildCount
                .Concat(createServiceChildCount)
                // Add CreateService Payload S_OK
                .Concat(createServicePayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] DispenserErrorResponse(byte[] requestHandle, byte[] serviceHandle, byte[] dispatchfunctionHandle) {

            //UNFINISHED


            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                requestHandle.Length +
                serviceHandle.Length +
                //functionHandle.Length
                4
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Testing 
            byte[] functionHandle = new byte[] { 0, 0, 0, 1 };

            // Get CreateService Byte Arrays
            byte[] createServiceChildCount = new byte[] { 0, 0 };
            byte[] createServicePayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] createServicePayloadSize = new byte[] { 0, 0, 0, 4 };

            // Create Base Byte Array
            byte[] baseArray = new byte[0];
            // Formulate full response
            IEnumerable<byte> response = baseArray
                // Add Dispatch PayloadSize
                .Concat(dispatchPayloadSize)
                // Add Dispatch ChildCount
                .Concat(dispatchChildCount)
                // Add Dispatch CallingConvention 
                .Concat(dispatchCallingConvention)
                // Add Dispatch RequestHandle
                .Concat(requestHandle)
                // Add Dispatch ServiceHandle
                .Concat(serviceHandle)
                // Add Dispatch FunctionHandle
                .Concat(functionHandle)
                // Add CreateService PayloadSize
                .Concat(createServicePayloadSize)
                // Add CreateService ChildCount
                .Concat(createServiceChildCount)
                // Add CreateService Payload Result
                .Concat(createServicePayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        #endregion ############################################################


        #region DMCT Functions ################################################


        #endregion ############################################################


        #region DSPA Functions ################################################

        public static byte[] GetStringPropertyResponse(byte[] dispatchRequestHandle, string propertyValueString) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get GetStringProperty Byte Arrays
            
            byte[] GetStringPropertyChildCount = new byte[] { 0, 0 };
            byte[] GetStringPropertyPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] GetStringPropertyPayloadLength = new byte[] { 0, 0, 0, 0 };
            byte[] GetStringPropertyPayloadPropertyValue = Encoding.ASCII.GetBytes(propertyValueString);
            byte[] GetStringPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                GetStringPropertyPayloadS_OK.Length +
                GetStringPropertyPayloadLength.Length +
                GetStringPropertyPayloadPropertyValue.Length
            );

            // Create Base Byte Array
            byte[] baseArray = new byte[0];
            // Formulate full response
            IEnumerable<byte> response = baseArray
                // Add Dispatch PayloadSize
                .Concat(dispatchPayloadSize)
                // Add Dispatch ChildCount
                .Concat(dispatchChildCount)
                // Add Dispatch CallingConvention 
                .Concat(dispatchCallingConvention)
                // Add Dispatch RequestHandle
                .Concat(dispatchRequestHandle)
                // Add GetStringProperty PayloadSize
                .Concat(GetStringPropertyPayloadSize)
                // Add GetStringProperty ChildCount
                .Concat(GetStringPropertyChildCount)
                // Add GetStringProperty Payload Result
                .Concat(GetStringPropertyPayloadS_OK)
                // Add GetStringProperty Payload Length
                .Concat(GetStringPropertyPayloadLength)
                // Add GetStringProperty Payload PropertyValue
                .Concat(GetStringPropertyPayloadPropertyValue);

            // Return the created byte array
            return response.ToArray();
        }

        #endregion ############################################################


        #region DRMRI Functions ###############################################


        #endregion ############################################################


        #region DSMN Functions ################################################


        #endregion ############################################################
    }
}
