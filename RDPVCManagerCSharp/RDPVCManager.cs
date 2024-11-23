using RGiesecke.DllExport;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Win32.WtsApi32;

namespace RDPVCManager {
    public static class VirtualChannelEntryPoints {

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int VirtualChannelEntry(
            IntPtr hInst,
            [MarshalAs(UnmanagedType.LPStr)] string pEntryPointsName, // Marshal as LPStr
            int channelCount,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr, SizeParamIndex = 2)] string[] pChannelNames,
            int versionRequested,
            out IntPtr ppVirtualChannelInit);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int VirtualChannelInit(ref IntPtr initHandle, ChannelDef[] channels, int channelCount, int versionRequested, [MarshalAs(UnmanagedType.FunctionPtr)] ChannelInitEventDelegate channelInitEventProc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int VirtualChannelOpen(
            IntPtr initHandle,
            ref uint openHandle,
            string pChannelName,
            ref IntPtr ppData);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int VirtualChannelClose(
            uint openHandle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int VirtualChannelWrite(
            uint openHandle,
            IntPtr pBuffer,
            uint length,
            IntPtr pOverlapped);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int VirtualChannelOpenEvent(
            uint openHandle,
            uint eventCode,
            IntPtr pEventData,
            uint dataLength,
            uint dataFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int VirtualChannelInitEvent(
            IntPtr initHandle,
            uint eventCode,
            IntPtr pEventData,
            uint dataLength);
    }

    public static class RDPVCManager {

        static IntPtr Channel;
        private static IntPtr _hInstance;
        static ChannelEntryPoints EntryPoints;
        private static ChannelContext[] _channelContexts;
        private static string _logFilePath = "RDPVCManager.log"; // Log file path
        private static string[] channelNames = { "McxSess", "devcaps", "avctrl" };
        static ChannelInitEventDelegate channelInitEventDelegate = new ChannelInitEventDelegate(VirtualChannelInitEventProc);
        static ChannelOpenEventDelegate channelOpenEventDelegate = new ChannelOpenEventDelegate(VirtualChannelOpenEvent);


        public static void VirtualChannelInitEventProc(IntPtr initHandle, ChannelEvents Event, byte[] data, int dataLength) {
            //try {
                switch (Event) {
                    case ChannelEvents.Initialized:
                        break;
                    case ChannelEvents.Connected:
                        // Get the Channel Count
                        int channelCount = channelNames.Length;
                        // Allocate an array to store channel contexts
                        _channelContexts = new ChannelContext[channelCount];

                        // Initialize each channel context
                        for (int i = 0; i < channelCount; i++) {
                            _channelContexts[i] = new ChannelContext();
                            _channelContexts[i].ChannelName = channelNames[i];
                            int OpenChannel = 0;

                            // Assign named pipe names for each channel
                            switch (_channelContexts[i].ChannelName) {
                                case "McxSess":
                                    _channelContexts[i].PipeName = "RDPVCManager_McxSess";
                                    break;
                                case "devcaps":
                                    _channelContexts[i].PipeName = "RDPVCManager_devcaps";
                                    break;
                                case "avctrl":
                                    _channelContexts[i].PipeName = "RDPVCManager_avctrl";
                                    break;
                            }

                            //// Create a thread to handle the named pipe for this channel
                            //Thread pipeThread = new Thread(() => PipeThreadProc(_channelContexts[i]));
                            //pipeThread.Start();

                            ChannelReturnCodes ret = EntryPoints.VirtualChannelOpen(initHandle, ref OpenChannel, channelNames[i], channelOpenEventDelegate);
                            _channelContexts[i].OpenHandleInt = OpenChannel;

                            if (ret != ChannelReturnCodes.Ok)
                                LogToFile("ERROR: Open of RDP virtual channel failed.\n" + ret.ToString());
                            else {
                                string servername = System.Text.Encoding.Unicode.GetString(data);
                                servername = servername.Substring(0, servername.IndexOf('\0'));
                                LogToFile($"Init Channel '{_channelContexts[i].ChannelName}' on '{servername}'");

                            }
                        }
                        break;
                    case ChannelEvents.V1Connected:
                        LogToFile("ERROR: Connecting to a non Windows 2000 Terminal Server.");
                        break;
                    case ChannelEvents.Disconnected:
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        break;
                    case ChannelEvents.Terminated:
                        GC.KeepAlive(channelInitEventDelegate);
                        GC.KeepAlive(channelOpenEventDelegate);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        break;
                }
            //} catch (Exception ex) {
            //    // Log the Exception
            //    LogToFile($"ERROR (VirtualChannelInitEventProc): '{ex.Message}' {ex.StackTrace}");
            //}
        }

        public static void VirtualChannelOpenEvent(int openHandle, ChannelEvents Event, byte[] data, int dataLength, uint totalLength, ChannelFlags dataFlags) {
            LogToFile($"Execute VirtualChannelOpenEvent");

            switch (Event) {
                case ChannelEvents.DataRecived:
                    LogToFile($"Received Data on Channel");

                    switch (dataFlags & ChannelFlags.Only) {
                        case ChannelFlags.Only:
                            //Unpacker unpack = new Unpacker(totalLength);
                            //unpack.DataRecived(data);
                            //unpack.Unpack();
                            //main.pbMain.Maximum = (int)totalLength;
                            //main.pbMain.Value = (int)dataLength;
                            break;
                        case ChannelFlags.First:
                            //unpacker = new Unpacker(totalLength);
                            //unpacker.DataRecived(data);
                            //main.pbMain.Maximum = (int)totalLength;
                            //main.pbMain.Value = 0;
                            //main.pbMain.Value += dataLength;
                            break;
                        case ChannelFlags.Middle:
                            //if (unpacker != null) {
                            //    unpacker.DataRecived(data);
                            //    main.pbMain.Value += dataLength;
                            //}
                            break;
                        case ChannelFlags.Last:
                            //if (unpacker != null) {
                            //    unpacker.DataRecived(data);
                            //    unpacker.Unpack();
                            //    unpacker = null;
                            //    main.pbMain.Value += dataLength;
                            //}
                            break;
                    }
                    break;
            }
        }

        //[DllExport("VirtualChannelEntry", CallingConvention = CallingConvention.StdCall)] // Export the function
        //public static int MyVirtualChannelEntry(
        //    IntPtr hInst,
        //    string pEntryPointsName,
        //    int channelCount,
        //    string[] pChannelNames,
        //    int versionRequested,
        //    ref IntPtr ppVirtualChannelInit) {

        //    // Create the log file if it doesn't exist
        //    if (!File.Exists(_logFilePath)) {
        //        File.Create(_logFilePath).Close();
        //    }

        //    // Log the entry point call
        //    LogToFile($"Media Center Extender VirtualChannelEntry called with EntryPointsName: {pEntryPointsName}, ChannelCount: {channelCount}");

        //    try {
        //        // Assign the VirtualChannelInit function pointer (with explicit delegate creation)
        //        ppVirtualChannelInit = Marshal.GetFunctionPointerForDelegate(new VirtualChannelEntryPoints.VirtualChannelInit(VirtualChannelInit));
        //    } catch (Exception ex) {
        //        // Log the Exception
        //        LogToFile($"ERROR: '{ex.Message}' {ex.StackTrace}");
        //    }
        //    return 0; // CHANNEL_RC_OK
        //}

        [DllExport("VirtualChannelEntry", CallingConvention = CallingConvention.StdCall)] // Export the function
        public static bool VirtualChannelEntry(ref ChannelEntryPoints entry) {
            try {
                // Create ChannelDef Struct
                ChannelDef[] cd = new ChannelDef[channelNames.Length];
                // Iterate over each Virtual Channel Name
                for (int i = 0; i < channelNames.Length; i++) {
                    cd[i] = new ChannelDef();
                    cd[i].name = channelNames[i];
                }

                EntryPoints = entry;
                ChannelReturnCodes ret = EntryPoints.VirtualChannelInit(ref Channel, cd, channelNames.Length, 1, channelInitEventDelegate);
                if (ret != ChannelReturnCodes.Ok) {
                    LogToFile("ERROR: RDP Virtual channel Init Failed.\n" + ret.ToString());
                    return false;
                }
            } catch (Exception ex) {
                // Log the Exception
                LogToFile($"ERROR (VirtualChannelEntry): '{ex.Message}' {ex.StackTrace}");
                return false;
            }
            return true;
        }

        //public static int VirtualChannelInit(
        //    IntPtr hInst,
        //    ref IntPtr ppInitHandle,
        //    IntPtr pChannelNames,
        //    int channelCount,
        //    int versionRequested,
        //    ref IntPtr ppVirtualChannelOpen,
        //    ref IntPtr ppVirtualChannelClose,
        //    ref IntPtr ppVirtualChannelWrite,
        //    ref IntPtr ppVirtualChannelOpenEvent,
        //    ref IntPtr ppVirtualChannelInitEvent) {
        //    _hInstance = hInst;

        //    // Create the log file if it doesn't exist
        //    if (!File.Exists(_logFilePath)) {
        //        File.Create(_logFilePath).Close();
        //    }

        //    // Static channel names list 
        //    string[] channelNames = new string[] { "McxSess", "devcaps", "avctrl" };
        //    channelCount = channelNames.Length; // Use the length of the static list

        //    // Allocate an array to store channel contexts
        //    _channelContexts = new ChannelContext[channelCount];

        //    // Initialize each channel context
        //    for (int i = 0; i < channelCount; i++) {
        //        _channelContexts[i] = new ChannelContext();
        //        _channelContexts[i].ChannelName = channelNames[i];

        //        // Assign named pipe names for each channel
        //        switch (_channelContexts[i].ChannelName) {
        //            case "McxSess":
        //                _channelContexts[i].PipeName = "RDPVCManager_McxSess";
        //                break;
        //            case "devcaps":
        //                _channelContexts[i].PipeName = "RDPVCManager_devcaps";
        //                break;
        //            case "avctrl":
        //                _channelContexts[i].PipeName = "RDPVCManager_avctrl";
        //                break;
        //        }

        //        // Create a thread to handle the named pipe for this channel
        //        Thread pipeThread = new Thread(() => PipeThreadProc(_channelContexts[i]));
        //        pipeThread.Start();
        //    }

        //    ppVirtualChannelOpen = Marshal.GetFunctionPointerForDelegate(new VirtualChannelEntryPoints.VirtualChannelOpen(_VirtualChannelOpen));
        //    ppVirtualChannelClose = Marshal.GetFunctionPointerForDelegate(new VirtualChannelEntryPoints.VirtualChannelClose(_VirtualChannelClose));
        //    ppVirtualChannelWrite = Marshal.GetFunctionPointerForDelegate(new VirtualChannelEntryPoints.VirtualChannelWrite(_VirtualChannelWrite));
        //    ppVirtualChannelOpenEvent = Marshal.GetFunctionPointerForDelegate(new VirtualChannelEntryPoints.VirtualChannelOpenEvent(_VirtualChannelOpenEvent));
        //    ppVirtualChannelInitEvent = Marshal.GetFunctionPointerForDelegate(new VirtualChannelEntryPoints.VirtualChannelInitEvent(_VirtualChannelInitEvent));

        //    return 0; // CHANNEL_RC_OK
        //}

        //private static int _VirtualChannelOpen(IntPtr initHandle, ref uint openHandle, string pChannelName, ref IntPtr ppData) {
        //    // ... your channel opening logic ...

        //    // Allocate memory for channel-specific data (example)
        //    ppData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ChannelData)));

        //    // ... initialize the data in ppData if needed ...

        //    // Log the channel opening
        //    LogToFile($"Media Center Extender Opening virtual channel: {pChannelName}");

        //    return 0; // CHANNEL_RC_OK
        //}

        //private static int _VirtualChannelClose(uint openHandle) {
        //    // Find the corresponding channel context
        //    ChannelContext channelContext = Array.Find(_channelContexts, c => c.OpenHandle == openHandle);
        //    if (channelContext == null) {
        //        LogToFile($"ERROR: Media Center Extender Channel context not found for openHandle {openHandle}");
        //        return -1; // CHANNEL_RC_BAD_CHANNEL
        //    }

        //    // Log the channel closing
        //    LogToFile($"Media Center Extender Closing virtual channel: {channelContext.ChannelName}");

        //    // Signal the PipeThreadProc to exit
        //    channelContext.PipeClosedEvent.Set();

        //    // Close the named pipe
        //    if (channelContext.PipeStream != null) {
        //        channelContext.PipeStream.Close();
        //        channelContext.PipeStream.Dispose();
        //        channelContext.PipeStream = null;
        //    }

        //    // Free the channel data
        //    if (channelContext.ChannelDataPtr != IntPtr.Zero) {
        //        Marshal.FreeHGlobal(channelContext.ChannelDataPtr);
        //        channelContext.ChannelDataPtr = IntPtr.Zero;
        //    }

        //    return 0; // CHANNEL_RC_OK
        //}

        //private static int _VirtualChannelWrite(uint openHandle, IntPtr pBuffer, uint length, IntPtr pOverlapped) {
        //    // Find the corresponding channel context
        //    ChannelContext channelContext = Array.Find(_channelContexts, c => c.OpenHandle == openHandle);
        //    if (channelContext == null) {
        //        LogToFile($"ERROR: Media Center Extender Channel context not found for openHandle {openHandle}");
        //        return -1; // CHANNEL_RC_BAD_CHANNEL
        //    }

        //    // Log the write operation
        //    LogToFile($"Media Center Extender Writing {length} bytes to virtual channel: {channelContext.ChannelName}");

        //    // ... your channel writing logic ...

        //    // Example: Store the buffer and user data for later use in VirtualChannelOpenEvent
        //    channelContext.PendingWrites.Add(new PendingWrite {
        //        Buffer = pBuffer,
        //        Length = length,
        //        UserData = pOverlapped // Assuming pOverlapped is used for user data
        //    });

        //    return 0; // CHANNEL_RC_OK
        //}

        //private static int _VirtualChannelInitEvent(IntPtr initHandle, uint eventCode, IntPtr pEventData, uint dataLength) {
        //    switch (eventCode) {
        //        case CHANNEL_EVENT.CONNECTED:
        //            // Start opening the virtual channels
        //            LogToFile("Media Center RDP session connected. Starting virtual channel initialization.");

        //            // Call VirtualChannelOpen for each channel
        //            foreach (var channelContext in _channelContexts) {
        //                uint openHandle = 0;
        //                IntPtr ppData = IntPtr.Zero;
        //                int result = _VirtualChannelOpen(initHandle, ref openHandle, channelContext.ChannelName, ref ppData);

        //                if (result == 0) // CHANNEL_RC_OK
        //                {
        //                    LogToFile($"Media Center Extender VirtualChannelOpen successful for {channelContext.ChannelName}");
        //                    channelContext.OpenHandle = openHandle;
        //                    channelContext.ChannelDataPtr = ppData; // Store ppData

        //                    // Create/open the named pipe for this channel
        //                    channelContext.PipeStream = new NamedPipeClientStream(".", channelContext.PipeName, PipeDirection.InOut);
        //                    channelContext.PipeStream.Connect();
        //                    LogToFile($"Media Center Extender Named pipe connected for {channelContext.ChannelName}");
        //                } else {
        //                    LogToFile($"ERROR: Media Center Extender VirtualChannelOpen failed for {channelContext.ChannelName} with error code: {result}");
        //                }
        //            }

        //            break;

        //            // ... handle other initialization events ...
        //    }
        //    return 0; // CHANNEL_RC_OK
        //}

        //private static int _VirtualChannelOpenEvent(uint openHandle, uint eventCode, IntPtr pEventData, uint dataLength, uint dataFlags) {
        //    // Find the corresponding channel context
        //    ChannelContext channelContext = Array.Find(_channelContexts, c => c.OpenHandle == openHandle);
        //    if (channelContext == null) {
        //        LogToFile($"ERROR: Media Center Extender Channel context not found for openHandle {openHandle}");
        //        return -1; // CHANNEL_RC_BAD_CHANNEL
        //    }

        //    switch (eventCode) {
        //        case CHANNEL_EVENT.WRITE_COMPLETE:
        //            // Handle write completion and free/reuse the buffer
        //            PendingWrite completedWrite = channelContext.PendingWrites.Find(w => w.UserData == pEventData);
        //            if (completedWrite != null) {
        //                Marshal.FreeHGlobal(completedWrite.Buffer); // Free the buffer
        //                channelContext.PendingWrites.Remove(completedWrite);
        //                LogToFile($"Media Center Extender Write operation completed for {channelContext.ChannelName} with user data: {completedWrite.UserData}");
        //            }
        //            break;

        //        default:
        //            // Handle RDP chunks and combine them
        //            if (dataLength > 0 && channelContext.PipeStream != null) {
        //                byte[] chunk = new byte[dataLength];
        //                Marshal.Copy(pEventData, chunk, 0, (int)dataLength);

        //                // Add the chunk to the buffer
        //                channelContext.ReceivedData.AddRange(chunk);

        //                // Check if the chunk is the last one using dataFlags
        //                if ((dataFlags & CHANNEL_FLAG.LAST) == CHANNEL_FLAG.LAST) {
        //                    // Combine the chunks into a single message
        //                    byte[] completeMessage = channelContext.ReceivedData.ToArray();
        //                    channelContext.ReceivedData.Clear(); // Clear the buffer

        //                    // Write the complete message to the named pipe
        //                    channelContext.PipeStream.Write(completeMessage, 0, completeMessage.Length);
        //                    LogToFile($"Media Center Extender Complete message written to named pipe for {channelContext.ChannelName}");
        //                }
        //            }
        //            break;
        //    }
        //    return 0; // CHANNEL_RC_OK
        //}

        private static void PipeThreadProc(ChannelContext channelContext) {
            try {
                using (NamedPipeServerStream pipeServer = new NamedPipeServerStream(
                    channelContext.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous)) {
                    // Wait for a client to connect
                    pipeServer.WaitForConnection();

                    // Handle pipe communication
                    while (true) {
                        try {
                            // Use a manual reset event for signaling instead of WaitForPipeDrain
                            WaitHandle[] waitHandles = new WaitHandle[] { channelContext.DataAvailableEvent, channelContext.PipeClosedEvent };
                            int signaledIndex = WaitHandle.WaitAny(waitHandles);

                            if (signaledIndex == 1) // PipeClosedEvent signaled
                            {
                                break; // Exit the loop
                            }
                            byte[] buffer = new byte[1024];
                            int bytesRead = pipeServer.Read(buffer, 0, buffer.Length);
                            if (bytesRead > 0) {
                                // Process data received from the C# application
                                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                LogToFile($"Media Center Extender Received from C# app on {channelContext.ChannelName}: {message}");

                                // Send the data to the virtual channel using VirtualChannelWrite
                                IntPtr pBuffer = Marshal.AllocHGlobal(bytesRead);
                                Marshal.Copy(buffer, 0, pBuffer, bytesRead);
                                //int result = _VirtualChannelWrite(channelContext.OpenHandle, pBuffer, (uint)bytesRead, IntPtr.Zero); // Use IntPtr.Zero for user data if not needed

                                //if (result != 0) {
                                //    LogToFile($"ERROR: Media Center Extender VirtualChannelWrite failed for {channelContext.ChannelName} with error code: {result}");
                                //    Marshal.FreeHGlobal(pBuffer); // Free the buffer on error
                                //}
                            }
                        } catch (Exception ex) {
                            LogToFile($"ERROR: Media Center Extender Error in pipe communication for {channelContext.ChannelName}: {ex.Message}");
                            break; // Exit the loop on error
                        }
                    }
                }
            } catch (Exception ex) {
                // Log the Exception
                LogToFile($"ERROR (PipeThreadProc): '{ex.Message}' {ex.StackTrace}");
            }
        }

        // Helper function for logging to file
        private static void LogToFile(string message, bool isError = false) {
            try {
                using (StreamWriter writer = new StreamWriter(_logFilePath, true)) {
                    string logEntry = $"{DateTime.Now} - {(isError ? "Error" : "Info")}: {message}";
                    writer.WriteLine(logEntry);
                }
            } catch (Exception ex) {
                // Handle potential exceptions during logging (e.g., file access errors)
                // You might want to write to the console or use a fallback logging mechanism here
                Console.WriteLine($"Error writing to log file: {ex.Message}");
            }
        }
    }

    public class ChannelContext {
        public string ChannelName { get; set; }
        public string PipeName { get; set; }
        public uint OpenHandle { get; set; }
        public int OpenHandleInt { get; set; }
        public IntPtr OpenChannel { get; set; }
        public IntPtr ChannelDataPtr { get; set; }
        public NamedPipeClientStream PipeStream { get; set; }
        public List<byte> ReceivedData { get; set; } = new List<byte>();
        public List<PendingWrite> PendingWrites { get; set; } = new List<PendingWrite>();
        public ManualResetEvent DataAvailableEvent { get; set; } = new ManualResetEvent(false);
        public ManualResetEvent PipeClosedEvent { get; set; } = new ManualResetEvent(false);
    }

    public class PendingWrite {
        public IntPtr Buffer { get; set; }
        public uint Length { get; set; }
        public IntPtr UserData { get; set; }
    }

    public struct ChannelData {
        public int SomeValue;
        // ... add other fields as needed ...
    }

    public static class CHANNEL_EVENT {
        public const uint CONNECTED = 0x0001;
        public const uint DISCONNECTED = 0x0002;
        public const uint TERMINATED = 0x0004;
        public const uint WRITE_COMPLETE = 0x0008;
        // ... other events if needed ...
    }

    public static class CHANNEL_FLAG {
        public const uint FIRST = 0x0001;
        public const uint LAST = 0x0002;
        public const uint MIDDLE = 0x0004;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CHANNEL_ENTRY_POINTS {
        public IntPtr VirtualChannelInit;
        public IntPtr VirtualChannelOpen;
        public IntPtr VirtualChannelClose;
        public IntPtr VirtualChannelWrite;
        public IntPtr VirtualChannelOpenEvent;
        public IntPtr VirtualChannelInitEvent;
    }
}