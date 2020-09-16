namespace Intel.Utilities
{
    using System;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Windows.Forms;

    public sealed class EventLogger
    {
        public static bool Enabled = false;
        private static string g_logName;
        private static bool g_onExceptionShowMessage = false;
        private static string g_productVersion;
        private static string g_sourceName;
        private static EventLog log;
        public static bool ShowAll = false;

        public static  event EventHandler OnEvent;

        public static void Log(Exception exception)
        {
            Log(exception, "");
        }

        public static void Log(string information)
        {
            Log(new object(), EventLogEntryType.Information, information);
        }

        public static void Log(Exception exception, string additional)
        {
            string fullName = exception.GetType().FullName;
            string message = exception.Message;
            Exception innerException = exception;
            for (int i = 0; innerException.InnerException != null; i++)
            {
                innerException = innerException.InnerException;
                fullName = fullName + " : " + innerException.GetType().FullName;
                string str3 = innerException.Message;
                message = str3 + "\r\n\r\nInnerException #" + i.ToString() + ":\r\nMessage: " + innerException.Message + "\r\nSource: " + innerException.Source + "\r\nStackTrace: " + innerException.StackTrace;
            }
            string str4 = fullName;
            fullName = str4 + "\r\n\r\n Additional Info: " + additional + "\r\n" + message;
            if (Enabled)
            {
                if (log != null)
                {
                    try
                    {
                        log.WriteEntry(exception.Source + " threw exception: " + exception.ToString(), EventLogEntryType.Error);
                    }
                    catch (Exception)
                    {
                    }
                }
                if (OnEvent != null)
                {
                    OnEvent(EventLogEntryType.Error, exception.Source, exception.StackTrace, fullName);
                }
            }
            if (g_onExceptionShowMessage)
            {
                ExceptionForm form = new ExceptionForm(exception);
                if (form.ShowDialog() == DialogResult.OK)
                {
                    Debugger.Break();
                }
                form.Dispose();
            }
        }

        public static void Log(object sender, EventLogEntryType LogType, string information)
        {
            if (Enabled && ((ShowAll || (LogType == EventLogEntryType.Error)) || (LogType == EventLogEntryType.SuccessAudit)))
            {
                string fullName = sender.GetType().FullName;
                StringBuilder builder = new StringBuilder();
                if (LogType == EventLogEntryType.Error)
                {
                    StackTrace trace = new StackTrace();
                    for (int i = 0; i < trace.FrameCount; i++)
                    {
                        builder.Append(trace.GetFrame(i).GetMethod().DeclaringType.FullName + "." + trace.GetFrame(i).GetMethod().Name + "\r\n");
                    }
                }
                if (builder != null)
                {
                    if (log != null)
                    {
                        try
                        {
                            log.WriteEntry(fullName + ": " + information + "\r\n\r\nTRACE:\r\n" + builder.ToString(), LogType);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                else if (log != null)
                {
                    try
                    {
                        log.WriteEntry(fullName + ": " + information, LogType);
                    }
                    catch (Exception)
                    {
                    }
                }
                if (OnEvent != null)
                {
                    OnEvent(LogType, sender, builder.ToString(), information);
                }
            }
        }

        public static void SetLog(string sourceName, string logName, string productVersion)
        {
            g_logName = logName;
            g_sourceName = sourceName;
            g_productVersion = productVersion;
            if (!EventLog.SourceExists(sourceName))
            {
                EventLog.CreateEventSource(sourceName, logName);
            }
            log = new EventLog(logName);
            log.Source = sourceName;
        }

        public static void SetOnExceptionAction(bool showMessageBox)
        {
            g_onExceptionShowMessage = showMessageBox;
        }

        public static void StopLog()
        {
            log.Close();
            log = null;
            g_logName = null;
            g_sourceName = null;
            g_productVersion = null;
        }

        public delegate void EventHandler(EventLogEntryType LogType, object origin, string StackTrace, string LogMessage);
    }
}

