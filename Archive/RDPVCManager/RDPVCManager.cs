using RGiesecke.DllExport;
using SoftSled.Components;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Win32.WtsApi32;

namespace RDPVCManager {
    public static class RDPVCManager {
        static int OpenChannelHandle = 0;
        static IntPtr Channel;
        static ChannelEntryPoints EntryPoints;
        private static ChannelContext[] _channelContexts;
        //private static string _logFilePath = "RDPVCManager.log"; // Log file path
        private static string pipePrefix = "RDPVCManager_";
        private static string[] channelNames = { "McxSess", "devcaps", "avctrl", "MCECaps", "VCHD", "splash" };
        static ChannelInitEventDelegate channelInitEventDelegate = new ChannelInitEventDelegate(VirtualChannelInitEventProc);
        static ChannelOpenEventDelegate channelOpenEventDelegate = new ChannelOpenEventDelegate(VirtualChannelOpenEvent);

        [DllExport("VirtualChannelEntry", CallingConvention = CallingConvention.StdCall)] // Export the function
        public static bool VirtualChannelEntry(ref ChannelEntryPoints entry) {
            try {
                // Create ChannelDef Struct
                ChannelDef[] cd = new ChannelDef[channelNames.Length];
                // Iterate over each Virtual Channel Name
                for (int i = 0; i < channelNames.Length; i++) {
                    cd[i] = new ChannelDef();
                    cd[i].name = channelNames[i];
                    cd[i].options = ChannelOptions.EncryptSC;
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

        public static void VirtualChannelInitEventProc(IntPtr initHandle, ChannelEvents Event, byte[] data, int dataLength) {
            LogToFile(Event.ToString());
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
                        _channelContexts[i].PipeClient = new NamedPipeClient($"{pipePrefix}{_channelContexts[i].ChannelName}", _channelContexts[i].ChannelName);
                        _channelContexts[i].PipeClient.OnReceivedMessage += new EventHandler<DataReceived>(Client_OnReceivedMessage);
                        _channelContexts[i].PipeClient.Start();
                        _channelContexts[i].SentBytes = new byte[0];
                        _channelContexts[i].RecBytes = new byte[0];
                        _channelContexts[i].RecIndex = 0;

                        ChannelReturnCodes ret = EntryPoints.VirtualChannelOpen(initHandle, ref OpenChannelHandle, channelNames[i], channelOpenEventDelegate);

                        if (ret != ChannelReturnCodes.Ok)
                            LogToFile("ERROR: Open of RDP virtual channel failed.\n" + ret.ToString());
                        else {
                            LogToFile($"Init Channel '{_channelContexts[i].ChannelName}' with handle '{OpenChannelHandle}'");
                            _channelContexts[i].OpenHandle = OpenChannelHandle;
                        }

                    }
                    break;
                case ChannelEvents.V1Connected:
                    LogToFile("ERROR: Connecting to a non Windows 2000 Terminal Server.");
                    break;
                case ChannelEvents.DataReceived:
                    break;
                case ChannelEvents.WriteComplete:
                    break;
                case ChannelEvents.WriteCanceled:
                    break;
                case ChannelEvents.Disconnected:
                    //foreach (ChannelContext channelContext in _channelContexts) {
                    //    channelContext.PipeClient.Stop();
                    //}
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    break;
                case ChannelEvents.Terminated:
                    //foreach (ChannelContext channelContext in _channelContexts) {
                    //    channelContext.PipeClient.Stop();
                    //}
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

        public static void VirtualChannelOpenEvent(int openHandle, ChannelEvents Event, byte[] data, int dataLength, int totalLength, ChannelFlags dataFlags) {
            LogToFile($"Execute VirtualChannelOpenEvent with '{Event}'");

            switch (Event) {
                case ChannelEvents.DataReceived:
                    // Get the Channel Context
                    var channelContext = _channelContexts.First(xx => xx.OpenHandle == openHandle);

                    var sb = new StringBuilder();
                    foreach (var b in data) {
                        sb.Append(b + " ");
                    }
                    LogToFile($"Received Data on {channelContext.ChannelName} Channel {sb}");

                    bool completeData = false;

                    switch (dataFlags & ChannelFlags.Only) {
                        case ChannelFlags.Only:
                            LogToFile($"Only Data");
                            completeData = true;
                            channelContext.RecBytes = data;
                            break;
                        case ChannelFlags.First:
                            LogToFile($"First Data");
                            channelContext.RecBytes = new byte[totalLength];
                            Array.Copy(data, channelContext.RecBytes, dataLength);
                            channelContext.RecIndex = dataLength;
                            break;
                        case ChannelFlags.Middle:
                            LogToFile($"Middle Data");
                            Array.Copy(data, 0, channelContext.RecBytes, channelContext.RecIndex, dataLength);
                            channelContext.RecIndex += dataLength;
                            break;
                        case ChannelFlags.Last:
                            LogToFile($"Last Data");
                            completeData = true;
                            Array.Copy(data, 0, channelContext.RecBytes, channelContext.RecIndex, dataLength);
                            channelContext.RecIndex = 0;
                            break;
                    }

                    if (completeData == true) {
                        LogToFile($"Sending Data over Pipe '{pipePrefix}{channelContext.ChannelName}'");
                        channelContext.PipeClient.Write(channelContext.RecBytes);
                    }

                    break;
            }
        }

        private static void Client_OnReceivedMessage(object sender, DataReceived e) {
            // Get the Channel Context
            var channelContext = _channelContexts.First(xx => xx.ChannelName == e.channelName);
            // If there is a Context for this Channel
            if (channelContext != null) {

                channelContext.SentBytes = new byte[e.data.Length];
                Array.Copy(e.data, channelContext.SentBytes, e.data.Length);
                var sb = new StringBuilder();
                foreach (var b in channelContext.SentBytes) {
                    sb.Append(b + " ");
                }
                LogToFile($"Received Data to send on {channelContext.ChannelName} Channel {sb}");

                ChannelReturnCodes ret = EntryPoints.VirtualChannelWrite(channelContext.OpenHandle, channelContext.SentBytes, channelContext.SentBytes.Length, Encoding.Unicode.GetBytes(sb.ToString()));

                if (ret != ChannelReturnCodes.Ok)
                    LogToFile($"ERROR: Media Center Extender VirtualChannelWrite failed for {channelContext.ChannelName} with error code: {ret}");
                //Marshal.FreeHGlobal(pBuffer); // Free the buffer on error
                else {
                    LogToFile($"Sent Pipe Data for '{e.channelName}' with handle '{channelContext.OpenHandle}'");
                }
            } else {
                LogToFile($"ERROR: Context for {e.channelName} does not exist");
            }
        }

        // Helper function for logging to file
        private static void LogToFile(string message, bool isError = false) {
            //try {
            //    using (StreamWriter writer = new StreamWriter(_logFilePath, true)) {
            //        string logEntry = $"{DateTime.Now} - {(isError ? "Error" : "Info")}: {message}";
            //        writer.WriteLine(logEntry);
            //    }
            //} catch (Exception ex) {
            //    // Handle potential exceptions during logging (e.g., file access errors)
            //    // You might want to write to the console or use a fallback logging mechanism here
            //    Console.WriteLine($"Error writing to log file: {ex.Message}");
            //}
        }
    }

    public class ChannelContext {
        public string ChannelName { get; set; }
        public int OpenHandle { get; set; }
        public byte[] SentBytes { get; set; }
        public byte[] RecBytes { get; set; }
        public int RecIndex { get; set; }
        public SoftSled.Components.NamedPipeClient PipeClient { get; set; }
    }
}