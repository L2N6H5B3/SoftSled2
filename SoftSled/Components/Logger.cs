using System;
using System.Collections.Generic;
using System.Text;

namespace SoftSled.Components
{
    public abstract class Logger
    {
        protected abstract void OnLogDebug(string message);
        protected abstract void OnLogInfo(string message);
        protected abstract void OnLogError(string message);

        public bool IsLoggingDebug
        {
            get;
            set;
        }

        public void LogDebug(string message)
        {
 
            OnLogDebug(message);
        }

        public void LogInfo(string message)
        {

            OnLogInfo(message);
        }

        public void LogError(string message)
        {
            OnLogError(message);
        }

        protected string GetPrefix()
        {
            return DateTime.Now.ToShortDateString() + " " + DateTime.Now.ToShortTimeString() + " :";
        }


    }
}
