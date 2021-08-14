using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SoftSled.VChan {
    class DSLR {

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

        public static byte[] GenericResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get Byte Arrays
            byte[] ChildCount = new byte[] { 0, 0 };
            byte[] PayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] PropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add PayloadSize
                .Concat(PropertyPayloadSize)
                // Add ChildCount
                .Concat(ChildCount)
                // Add Payload Result
                .Concat(PayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        private static byte[] GetInverse4ByteArrayFromInt(int intValue) {
            byte[] intBytes = BitConverter.GetBytes(intValue);
            if (BitConverter.IsLittleEndian) {
                Array.Reverse(intBytes);
            }
            return intBytes;
        }

        private static byte[] GetInverse8ByteArrayFromLong(long longValue) {
            byte[] longBytes = BitConverter.GetBytes(longValue);
            if (BitConverter.IsLittleEndian) {
                Array.Reverse(longBytes);
            }
            return longBytes;
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

        public static byte[] OpenMediaResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get OpenMedia Byte Arrays
            byte[] OpenMediaChildCount = new byte[] { 0, 0 };
            byte[] OpenMediaPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] OpenMediaPropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add OpenMedia PayloadSize
                .Concat(OpenMediaPropertyPayloadSize)
                // Add OpenMedia ChildCount
                .Concat(OpenMediaChildCount)
                // Add OpenMedia Payload Result
                .Concat(OpenMediaPayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] CloseMediaResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get CloseMedia Byte Arrays
            byte[] CloseMediaChildCount = new byte[] { 0, 0 };
            byte[] CloseMediaPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] CloseMediaPropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add CloseMedia PayloadSize
                .Concat(CloseMediaPropertyPayloadSize)
                // Add CloseMedia ChildCount
                .Concat(CloseMediaChildCount)
                // Add CloseMedia Payload Result
                .Concat(CloseMediaPayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] StartResponse(byte[] dispatchRequestHandle, int grantedPlayRateInt) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get Start Byte Arrays
            byte[] StartChildCount = new byte[] { 0, 0 };
            byte[] StartPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] StartPayloadGrantedPlayRate = GetInverse4ByteArrayFromInt(grantedPlayRateInt);
            byte[] StartPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                StartPayloadS_OK.Length +
                StartPayloadGrantedPlayRate.Length
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
                // Add Start PayloadSize
                .Concat(StartPropertyPayloadSize)
                // Add Start ChildCount
                .Concat(StartChildCount)
                // Add Start Payload Result
                .Concat(StartPayloadS_OK)
                // Add Start Payload GrantedPlayRate
                .Concat(StartPayloadGrantedPlayRate);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] PauseResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get Pause Byte Arrays
            byte[] PauseChildCount = new byte[] { 0, 0 };
            byte[] PausePayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] PausePropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add Pause PayloadSize
                .Concat(PausePropertyPayloadSize)
                // Add Pause ChildCount
                .Concat(PauseChildCount)
                // Add Pause Payload Result
                .Concat(PausePayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] StopResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get Stop Byte Arrays
            byte[] StopChildCount = new byte[] { 0, 0 };
            byte[] StopPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] StopPropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add Stop PayloadSize
                .Concat(StopPropertyPayloadSize)
                // Add Stop ChildCount
                .Concat(StopChildCount)
                // Add Stop Payload Result
                .Concat(StopPayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] GetDurationResponse(byte[] dispatchRequestHandle, long durationLong) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get GetDuration Byte Arrays
            byte[] GetDurationChildCount = new byte[] { 0, 0 };
            byte[] GetDurationPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] GetDurationPayloadDuration = GetInverse8ByteArrayFromLong(durationLong);
            byte[] GetDurationPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                GetDurationPayloadS_OK.Length +
                GetDurationPayloadDuration.Length
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
                // Add GetDuration PayloadSize
                .Concat(GetDurationPropertyPayloadSize)
                // Add GetDuration ChildCount
                .Concat(GetDurationChildCount)
                // Add GetDuration Payload Result
                .Concat(GetDurationPayloadS_OK)
                // Add GetDuration Payload Duration
                .Concat(GetDurationPayloadDuration);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] GetPositionResponse(byte[] dispatchRequestHandle, long positionLong) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get GetPosition Byte Arrays
            byte[] GetPositionChildCount = new byte[] { 0, 0 };
            byte[] GetPositionPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] GetPositionPayloadPosition = GetInverse8ByteArrayFromLong(positionLong);
            byte[] GetPositionPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                GetPositionPayloadS_OK.Length +
                GetPositionPayloadPosition.Length
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
                // Add GetPosition PayloadSize
                .Concat(GetPositionPropertyPayloadSize)
                // Add GetPosition ChildCount
                .Concat(GetPositionChildCount)
                // Add GetPosition Payload Result
                .Concat(GetPositionPayloadS_OK)
                // Add GetPosition Payload Position
                .Concat(GetPositionPayloadPosition);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] GetPositionResponse(int avCtrlIter, long positionLong) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                4
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get GetPosition Byte Arrays
            byte[] GetPositionChildCount = new byte[] { 0, 0 };
            byte[] GetPositionPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] GetPositionPayloadPosition = GetInverse8ByteArrayFromLong(positionLong);
            byte[] GetPositionPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                GetPositionPayloadS_OK.Length +
                GetPositionPayloadPosition.Length
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
                .Concat(GetInverse4ByteArrayFromInt(avCtrlIter))
                // Add GetPosition PayloadSize
                .Concat(GetPositionPropertyPayloadSize)
                // Add GetPosition ChildCount
                .Concat(GetPositionChildCount)
                // Add GetPosition Payload Result
                .Concat(GetPositionPayloadS_OK)
                // Add GetPosition Payload Position
                .Concat(GetPositionPayloadPosition);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] RegisterMediaEventCallbackResponse(byte[] dispatchRequestHandle, int cookieInt) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get RegisterMediaEventCallback Byte Arrays
            byte[] RegisterMediaEventCallbackChildCount = new byte[] { 0, 0 };
            byte[] RegisterMediaEventCallbackPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] RegisterMediaEventCallbackPayloadCookie = GetInverse4ByteArrayFromInt(cookieInt);
            byte[] RegisterMediaEventCallbackPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                RegisterMediaEventCallbackPayloadS_OK.Length +
                RegisterMediaEventCallbackPayloadCookie.Length
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
                // Add RegisterMediaEventCallback PayloadSize
                .Concat(RegisterMediaEventCallbackPropertyPayloadSize)
                // Add RegisterMediaEventCallback ChildCount
                .Concat(RegisterMediaEventCallbackChildCount)
                // Add RegisterMediaEventCallback Payload Result
                .Concat(RegisterMediaEventCallbackPayloadS_OK)
                // Add RegisterMediaEventCallback Payload Cookie
                .Concat(RegisterMediaEventCallbackPayloadCookie);

            // Return the created byte array
            return response.ToArray();
        }

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
            byte[] GetStringPropertyPayloadPropertyValue = Encoding.ASCII.GetBytes(propertyValueString);
            byte[] GetStringPropertyPayloadLength = GetInverse4ByteArrayFromInt(
                GetStringPropertyPayloadPropertyValue.Length
            );
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

        public static byte[] GetDWORDPropertyResponse(byte[] dispatchRequestHandle, int propertyValueInt) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get GetDWORDProperty Byte Arrays
            byte[] GetDWORDPropertyChildCount = new byte[] { 0, 0 };
            byte[] GetDWORDPropertyPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] GetDWORDPropertyPayloadPropertyValue = GetInverse4ByteArrayFromInt(propertyValueInt);
            byte[] GetDWORDPropertyPayloadLength = GetInverse4ByteArrayFromInt(
                GetDWORDPropertyPayloadPropertyValue.Length
            );
            byte[] GetDWORDPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                GetDWORDPropertyPayloadS_OK.Length +
                GetDWORDPropertyPayloadLength.Length +
                GetDWORDPropertyPayloadPropertyValue.Length
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
                // Add GetDWORDProperty PayloadSize
                .Concat(GetDWORDPropertyPayloadSize)
                // Add GetDWORDProperty ChildCount
                .Concat(GetDWORDPropertyChildCount)
                // Add GetDWORDProperty Payload Result
                .Concat(GetDWORDPropertyPayloadS_OK)
                // Add GetDWORDProperty Payload Length
                .Concat(GetDWORDPropertyPayloadLength)
                // Add GetDWORDProperty Payload PropertyValue
                .Concat(GetDWORDPropertyPayloadPropertyValue);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] SetDWORDPropertyResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get SetDWORDProperty Byte Arrays
            byte[] SetDWORDPropertyChildCount = new byte[] { 0, 0 };
            byte[] SetDWORDPropertyPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] SetDWORDPropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add SetDWORDProperty PayloadSize
                .Concat(SetDWORDPropertyPayloadSize)
                // Add SetDWORDProperty ChildCount
                .Concat(SetDWORDPropertyChildCount)
                // Add SetDWORDProperty Payload Result
                .Concat(SetDWORDPropertyPayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        #endregion ############################################################


        #region DRMRI Functions ###############################################

        public static byte[] RegisterTransmitterServiceResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get CreateService Byte Arrays
            byte[] RegisterTransmitterServicePayloadSize = new byte[] { 0, 0, 0, 4 };
            byte[] RegisterTransmitterServiceChildCount = new byte[] { 0, 0 };
            byte[] RegisterTransmitterServicePayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] RegisterTransmitterServicePayloadDSLR_E_FAIL = new byte[] { 136, 23, 64, 5 };

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
                // Add RegisterTransmitterService PayloadSize
                .Concat(RegisterTransmitterServicePayloadSize)
                // Add RegisterTransmitterService ChildCount
                .Concat(RegisterTransmitterServiceChildCount)
                // Add RegisterTransmitterService Payload
                .Concat(RegisterTransmitterServicePayloadDSLR_E_FAIL);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] UnregisterTransmitterServiceResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get CreateService Byte Arrays
            byte[] UnregisterTransmitterServicePayloadSize = new byte[] { 0, 0, 0, 4 };
            byte[] UnregisterTransmitterServiceChildCount = new byte[] { 0, 0 };
            byte[] UnregisterTransmitterServicePayloadS_OK = new byte[] { 0, 0, 0, 0 };

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
                // Add UnregisterTransmitterService PayloadSize
                .Concat(UnregisterTransmitterServicePayloadSize)
                // Add UnregisterTransmitterService ChildCount
                .Concat(UnregisterTransmitterServiceChildCount)
                // Add UnregisterTransmitterService Payload S_OK
                .Concat(UnregisterTransmitterServicePayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] InitiateRegistrationResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get CreateService Byte Arrays
            byte[] RegisterTransmitterServicePayloadSize = new byte[] { 0, 0, 0, 4 };
            byte[] RegisterTransmitterServiceChildCount = new byte[] { 0, 0 };
            byte[] RegisterTransmitterServicePayloadS_OK = new byte[] { 0, 0, 0, 0 };

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
                // Add RegisterTransmitterService PayloadSize
                .Concat(RegisterTransmitterServicePayloadSize)
                // Add RegisterTransmitterService ChildCount
                .Concat(RegisterTransmitterServiceChildCount)
                // Add RegisterTransmitterService Payload S_OK
                .Concat(RegisterTransmitterServicePayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        #endregion ############################################################


        #region DSMN Functions ################################################

        public static byte[] ShellDisconnectResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get ShellDisconnect Byte Arrays
            byte[] ShellDisconnectChildCount = new byte[] { 0, 0 };
            byte[] ShellDisconnectPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] ShellDisconnectPropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add ShellDisconnect PayloadSize
                .Concat(ShellDisconnectPropertyPayloadSize)
                // Add ShellDisconnect ChildCount
                .Concat(ShellDisconnectChildCount)
                // Add ShellDisconnect Payload Result
                .Concat(ShellDisconnectPayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] ShellIsActiveResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get ShellIsActive Byte Arrays
            byte[] ShellIsActiveChildCount = new byte[] { 0, 0 };
            byte[] ShellIsActivePayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] ShellIsActivePropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add ShellIsActive PayloadSize
                .Concat(ShellIsActivePropertyPayloadSize)
                // Add ShellIsActive ChildCount
                .Concat(ShellIsActiveChildCount)
                // Add ShellIsActive Payload Result
                .Concat(ShellIsActivePayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] HeartbeatResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get Heartbeat Byte Arrays
            byte[] HeartbeatChildCount = new byte[] { 0, 0 };
            byte[] HeartbeatPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] HeartbeatPropertyPayloadSize = new byte[] { 0, 0, 0, 4 };

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
                // Add Heartbeat PayloadSize
                .Concat(HeartbeatPropertyPayloadSize)
                // Add Heartbeat ChildCount
                .Concat(HeartbeatChildCount)
                // Add Heartbeat Payload Result
                .Concat(HeartbeatPayloadS_OK);

            // Return the created byte array
            return response.ToArray();
        }

        public static byte[] GetQWaveSinkInfoResponse(byte[] dispatchRequestHandle) {

            // Get Dispatch Byte Arrays
            byte[] dispatchPayloadSize = GetInverse4ByteArrayFromInt(
                4 +
                dispatchRequestHandle.Length
            );
            byte[] dispatchChildCount = new byte[] { 0, 1 };
            byte[] dispatchCallingConvention = new byte[] { 0, 0, 0, 2 };

            // Get GetQWaveSinkInfo Byte Arrays
            byte[] GetQWaveSinkInfoChildCount = new byte[] { 0, 0 };
            byte[] GetQWaveSinkInfoPayloadS_OK = new byte[] { 0, 0, 0, 0 };
            byte[] GetQWaveSinkInfoPayloadIsSinkRunning = new byte[] { 0, 0, 0, 0 };
            byte[] GetQWaveSinkInfoPayloadPortNumber = GetInverse4ByteArrayFromInt(2177);
            byte[] GetQWaveSinkInfoPropertyPayloadSize = GetInverse4ByteArrayFromInt(
                GetQWaveSinkInfoPayloadS_OK.Length +
                GetQWaveSinkInfoPayloadIsSinkRunning.Length +
                GetQWaveSinkInfoPayloadPortNumber.Length
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
                // Add GetQWaveSinkInfo PayloadSize
                .Concat(GetQWaveSinkInfoPropertyPayloadSize)
                // Add GetQWaveSinkInfo ChildCount
                .Concat(GetQWaveSinkInfoChildCount)
                // Add GetQWaveSinkInfo Payload Result
                .Concat(GetQWaveSinkInfoPayloadS_OK)
                // Add GetQWaveSinkInfo Payload IsSinkRunning
                .Concat(GetQWaveSinkInfoPayloadIsSinkRunning)
                // Add GetQWaveSinkInfo Payload PortNumber
                .Concat(GetQWaveSinkInfoPayloadPortNumber);

            // Return the created byte array
            return response.ToArray();
        }

        #endregion ############################################################
    }
}
