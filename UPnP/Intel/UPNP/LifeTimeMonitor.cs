namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Collections;
    using System.Runtime.CompilerServices;

    public sealed class LifeTimeMonitor
    {
        private SortedList MonitorList = new SortedList();
        private object MonitorLock = new object();
        private WeakEvent OnExpiredEvent = new WeakEvent();
        private SafeTimer SafeNotifyTimer = new SafeTimer();

        public event LifeTimeHandler OnExpired
        {
            add
            {
                this.OnExpiredEvent.Register(value);
            }
            remove
            {
                this.OnExpiredEvent.UnRegister(value);
            }
        }

        public LifeTimeMonitor()
        {
            this.SafeNotifyTimer.OnElapsed += new SafeTimer.TimeElapsedHandler(this.OnTimedEvent);
            this.SafeNotifyTimer.AutoReset = false;
            InstanceTracker.Add(this);
        }

        public void Add(object obj, double secondTimeout)
        {
            if (obj != null)
            {
                if (secondTimeout <= 0.0)
                {
                    secondTimeout = 0.01;
                }
                lock (this.MonitorLock)
                {
                    this.SafeNotifyTimer.Stop();
                    if (this.MonitorList.ContainsValue(obj))
                    {
                        this.MonitorList.RemoveAt(this.MonitorList.IndexOfValue(obj));
                    }
                    DateTime key = DateTime.Now.AddSeconds(secondTimeout);
                    while (this.MonitorList.ContainsKey(key))
                    {
                        key = key.AddMilliseconds(1.0);
                    }
                    this.MonitorList.Add(key, obj);
                }
                this.OnTimedEvent();
            }
        }

        public void Add(object obj, int secondTimeout)
        {
            this.Add(obj, (double) secondTimeout);
        }

        public void Clear()
        {
            lock (this.MonitorLock)
            {
                this.SafeNotifyTimer.Stop();
                this.MonitorList.Clear();
            }
        }

        ~LifeTimeMonitor()
        {
            this.SafeNotifyTimer.dispose();
            this.SafeNotifyTimer = null;
        }

        private void OnTimedEvent()
        {
            ArrayList list = new ArrayList();
            lock (this.MonitorLock)
            {
                goto Label_0034;
            Label_0015:
                list.Add(this.MonitorList.GetByIndex(0));
                this.MonitorList.RemoveAt(0);
            Label_0034:
                if (this.MonitorList.Count > 0)
                {
                    DateTime key = (DateTime) this.MonitorList.GetKey(0);
                    if (key.CompareTo(DateTime.Now.AddSeconds(0.05)) < 0)
                    {
                        goto Label_0015;
                    }
                }
            }
            foreach (object obj2 in list)
            {
                this.OnExpiredEvent.Fire(this, obj2);
            }
            lock (this.MonitorLock)
            {
                if (this.MonitorList.Count > 0)
                {
                    TimeSpan span = ((DateTime) this.MonitorList.GetKey(0)).Subtract(DateTime.Now);
                    if (span.TotalMilliseconds <= 0.0)
                    {
                        this.SafeNotifyTimer.Interval = 1;
                    }
                    else
                    {
                        this.SafeNotifyTimer.Interval = (int) span.TotalMilliseconds;
                    }
                    this.SafeNotifyTimer.Start();
                }
            }
        }

        public bool Remove(object obj)
        {
            if (obj == null)
            {
                return false;
            }
            bool flag = false;
            lock (this.MonitorLock)
            {
                if (!this.MonitorList.ContainsValue(obj))
                {
                    return flag;
                }
                this.SafeNotifyTimer.Stop();
                flag = true;
                this.MonitorList.RemoveAt(this.MonitorList.IndexOfValue(obj));
                if (this.MonitorList.Count > 0)
                {
                    TimeSpan span = ((DateTime) this.MonitorList.GetKey(0)).Subtract(DateTime.Now);
                    if (span.TotalMilliseconds <= 0.0)
                    {
                        this.SafeNotifyTimer.Interval = 1;
                    }
                    else
                    {
                        this.SafeNotifyTimer.Interval = (int) span.TotalMilliseconds;
                    }
                    this.SafeNotifyTimer.Start();
                }
            }
            return flag;
        }

        public delegate void LifeTimeHandler(LifeTimeMonitor sender, object obj);
    }
}

