namespace Intel.UPNP
{
    using System;

    public class UPnPModeratedStateVariable : UPnPStateVariable
    {
        public IAccumulator Accumulator;
        private Intel.UPNP.LifeTimeMonitor.LifeTimeHandler LifeTimeHandler;
        protected int PendingEvents;
        protected object PendingObject;
        protected double Seconds;
        protected LifeTimeMonitor t;

        public UPnPModeratedStateVariable(string VarName, object VarValue) : base(VarName, VarValue)
        {
            this.Accumulator = new DefaultAccumulator();
            this.PendingObject = null;
            this.Seconds = 0.0;
            this.PendingEvents = 0;
            this.t = new LifeTimeMonitor();
            this.InitMonitor();
        }

        public UPnPModeratedStateVariable(string VarName, object VarValue, string[] AllowedValues) : base(VarName, VarValue, AllowedValues)
        {
            this.Accumulator = new DefaultAccumulator();
            this.PendingObject = null;
            this.Seconds = 0.0;
            this.PendingEvents = 0;
            this.t = new LifeTimeMonitor();
            this.InitMonitor();
        }

        public UPnPModeratedStateVariable(string VarName, Type VarType, bool SendEvents) : base(VarName, VarType, SendEvents)
        {
            this.Accumulator = new DefaultAccumulator();
            this.PendingObject = null;
            this.Seconds = 0.0;
            this.PendingEvents = 0;
            this.t = new LifeTimeMonitor();
            this.InitMonitor();
        }

        protected void InitMonitor()
        {
            this.LifeTimeHandler = new Intel.UPNP.LifeTimeMonitor.LifeTimeHandler(this.LifeTimeSink);
            this.t.OnExpired += this.LifeTimeHandler;
        }

        protected void LifeTimeSink(LifeTimeMonitor sender, object Obj)
        {
            lock (this)
            {
                if (this.PendingEvents > 1)
                {
                    base.Value = this.PendingObject;
                }
                this.PendingObject = this.Accumulator.Reset();
                this.PendingEvents = 0;
            }
        }

        public double ModerationPeriod
        {
            get
            {
                return this.Seconds;
            }
            set
            {
                lock (this)
                {
                    this.Seconds = value;
                }
            }
        }

        public override object Value
        {
            get
            {
                return base.Value;
            }
            set
            {
                if (this.Seconds == 0.0)
                {
                    base.Value = value;
                }
                else
                {
                    lock (this)
                    {
                        if (this.PendingEvents == 0)
                        {
                            this.PendingEvents++;
                            base.Value = value;
                            this.PendingObject = this.Accumulator.Reset();
                            this.t.Add(this, this.Seconds);
                        }
                        else
                        {
                            this.PendingEvents++;
                            this.PendingObject = this.Accumulator.Merge(this.PendingObject, value);
                        }
                    }
                }
            }
        }

        public class DefaultAccumulator : UPnPModeratedStateVariable.IAccumulator
        {
            public object Merge(object current, object newobject)
            {
                return newobject;
            }

            public object Reset()
            {
                return null;
            }
        }

        public interface IAccumulator
        {
            object Merge(object current, object newobject);
            object Reset();
        }
    }
}

