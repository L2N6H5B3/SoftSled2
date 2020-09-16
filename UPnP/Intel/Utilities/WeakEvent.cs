namespace Intel.Utilities
{
    using System;
    using System.Collections;
    using System.Reflection;

    public sealed class WeakEvent
    {
        private ArrayList EventList = new ArrayList();
        private object EventLock = new object();

        public WeakEvent()
        {
            InstanceTracker.Add(this);
        }

        public void Fire()
        {
            this.Fire(new object[0]);
        }

        public void Fire(object sender)
        {
            this.Fire(new object[] { sender });
        }

        public void Fire(object[] args)
        {
            object[] objArray = (object[]) this.EventList.ToArray(typeof(object));
            foreach (object[] objArray2 in objArray)
            {
                WeakReference reference = (WeakReference) objArray2[1];
                object target = reference.Target;
                bool flag = (bool) objArray2[2];
                MethodInfo info = (MethodInfo) objArray2[0];
                if (reference.IsAlive || flag)
                {
                    try
                    {
                        info.Invoke(target, args);
                    }
                    catch (Exception exception)
                    {
                        EventLogger.Log(exception);
                    }
                }
                else
                {
                    lock (this.EventLock)
                    {
                        this.EventList.Remove(objArray2);
                    }
                }
            }
        }

        public void Fire(object sender, object arg1)
        {
            this.Fire(new object[] { sender, arg1 });
        }

        public void Fire(object sender, object arg1, object arg2)
        {
            this.Fire(new object[] { sender, arg1, arg2 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13, object arg14)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13, object arg14, object arg15)
        {
            this.Fire(new object[] { sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15 });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13, object arg14, object arg15, object arg16)
        {
            this.Fire(new object[] { 
                sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15, 
                arg16
             });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13, object arg14, object arg15, object arg16, object arg17)
        {
            this.Fire(new object[] { 
                sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15, 
                arg16, arg17
             });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13, object arg14, object arg15, object arg16, object arg17, object arg18)
        {
            this.Fire(new object[] { 
                sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15, 
                arg16, arg17, arg18
             });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13, object arg14, object arg15, object arg16, object arg17, object arg18, object arg19)
        {
            this.Fire(new object[] { 
                sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15, 
                arg16, arg17, arg18, arg19
             });
        }

        public void Fire(object sender, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13, object arg14, object arg15, object arg16, object arg17, object arg18, object arg19, object arg20)
        {
            this.Fire(new object[] { 
                sender, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15, 
                arg16, arg17, arg18, arg19, arg20
             });
        }

        public void Register(Delegate handler)
        {
            lock (this.EventLock)
            {
                this.EventList.Add(new object[] { handler.Method, new WeakReference(handler.Target), handler.Target == null });
            }
        }

        public void Register(object applicant, string methodName)
        {
            lock (this.EventLock)
            {
                this.EventList.Add(new object[] { applicant.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance), new WeakReference(applicant), false });
            }
        }

        public void UnRegister(Delegate handler)
        {
            this.UnRegister(handler.Target, handler.Method.Name);
        }

        public void UnRegister(object applicant, string methodName)
        {
            lock (this.EventLock)
            {
                object[] objArray = (object[]) this.EventList.ToArray(typeof(object));
                foreach (object[] objArray2 in objArray)
                {
                    WeakReference reference = (WeakReference) objArray2[1];
                    object target = null;
                    try
                    {
                        target = reference.Target;
                    }
                    catch
                    {
                        target = null;
                    }
                    MethodInfo info = (MethodInfo) objArray2[0];
                    if ((target != null) && reference.IsAlive)
                    {
                        if ((target != applicant) || !(info.Name == methodName))
                        {
                            goto Label_0099;
                        }
                        this.EventList.Remove(objArray2);
                        break;
                    }
                    this.EventList.Remove(objArray2);
                Label_0099:;
                }
            }
        }

        public void UnRegisterAll()
        {
            lock (this.EventLock)
            {
                this.EventList.Clear();
            }
        }
    }
}

