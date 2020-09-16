namespace Intel.UPNP
{
    using Intel.Utilities;
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading;

    public sealed class SafeTimer
    {
        public bool AutoReset;
        private WeakEvent ElapsedWeakEvent;
        private RegisteredWaitHandle handle;
        public int Interval;
        private ManualResetEvent mre;
        private object RegLock;
        private bool StartFlag;
        private int timeout;
        private bool WaitFlag;
        private WaitOrTimerCallback WOTcb;

        public event TimeElapsedHandler OnElapsed
        {
            add
            {
                this.ElapsedWeakEvent.Register(value);
            }
            remove
            {
                this.ElapsedWeakEvent.UnRegister(value);
            }
        }

        public SafeTimer()
        {
            this.ElapsedWeakEvent = new WeakEvent();
            this.Interval = 0;
            this.AutoReset = false;
            this.mre = new ManualResetEvent(false);
            this.RegLock = new object();
            this.WaitFlag = false;
            this.timeout = 0;
            this.WOTcb = new WaitOrTimerCallback(this.HandleTimer);
            InstanceTracker.Add(this);
        }

        public SafeTimer(int Milliseconds, bool Auto) : this()
        {
            this.Interval = Milliseconds;
            this.AutoReset = Auto;
            InstanceTracker.Add(this);
        }

        public void dispose()
        {
            if (this.handle != null)
            {
                this.handle.Unregister(null);
            }
        }

        private void HandleTimer(object State, bool TimedOut)
        {
            if (TimedOut)
            {
                lock (this.RegLock)
                {
                    if (this.handle != null)
                    {
                        this.handle.Unregister(null);
                        this.handle = null;
                    }
                    this.WaitFlag = true;
                    this.StartFlag = false;
                    this.timeout = this.Interval;
                }
                this.ElapsedWeakEvent.Fire();
                if (this.AutoReset)
                {
                    lock (this.RegLock)
                    {
                        this.mre.Reset();
                        this.handle = ThreadPool.RegisterWaitForSingleObject(this.mre, this.WOTcb, null, this.Interval, true);
                    }
                }
                else
                {
                    lock (this.RegLock)
                    {
                        if (this.WaitFlag && this.StartFlag)
                        {
                            this.Interval = this.timeout;
                            this.mre.Reset();
                            if (this.handle != null)
                            {
                                this.handle.Unregister(null);
                            }
                            this.handle = ThreadPool.RegisterWaitForSingleObject(this.mre, this.WOTcb, null, this.Interval, true);
                        }
                        this.WaitFlag = false;
                        this.StartFlag = false;
                    }
                }
            }
        }

        public void Start()
        {
            lock (this.RegLock)
            {
                if (!this.WaitFlag)
                {
                    this.mre.Reset();
                    if (this.handle != null)
                    {
                        this.handle.Unregister(null);
                    }
                    this.handle = ThreadPool.RegisterWaitForSingleObject(this.mre, this.WOTcb, null, this.Interval, true);
                }
                else
                {
                    this.StartFlag = true;
                    if (this.Interval < this.timeout)
                    {
                        this.timeout = this.Interval;
                    }
                }
            }
        }

        public void Stop()
        {
            lock (this.RegLock)
            {
                if (this.handle != null)
                {
                    bool flag = this.handle.Unregister(null);
                }
                this.handle = null;
            }
        }

        public delegate void TimeElapsedHandler();
    }
}

