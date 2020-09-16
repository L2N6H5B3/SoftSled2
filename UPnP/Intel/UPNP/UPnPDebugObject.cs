namespace Intel.UPNP
{
    using System;
    using System.Reflection;

    public sealed class UPnPDebugObject
    {
        private object _Object;
        private Type _Type;

        public UPnPDebugObject(object Obj)
        {
            this._Object = null;
            this._Type = null;
            this._Object = Obj;
        }

        public UPnPDebugObject(Type tp)
        {
            this._Object = null;
            this._Type = null;
            this._Type = tp;
        }

        public object GetField(string FieldName)
        {
            return this._Object.GetType().GetField(FieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(this._Object);
        }

        public object GetProperty(string PropertyName, object[] indexes)
        {
            return this._Object.GetType().GetProperty(PropertyName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(this._Object, indexes);
        }

        public object GetStaticField(string FieldName)
        {
            return this._Type.GetField(FieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).GetValue(null);
        }

        public object InvokeNonStaticMethod(string MethodName, object[] Arg)
        {
            return this._Object.GetType().GetMethod(MethodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Invoke(this._Object, Arg);
        }

        public object InvokeStaticMethod(string MethodName, object[] Arg)
        {
            if (this._Object != null)
            {
                return this._Object.GetType().GetMethod(MethodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Invoke(null, Arg);
            }
            return this._Type.GetMethod(MethodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Invoke(null, Arg);
        }

        public void SetField(string FieldName, object Arg)
        {
            this._Object.GetType().GetField(FieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).SetValue(this._Object, Arg);
        }

        public void SetProperty(string PropertyName, object Val)
        {
            this._Object.GetType().GetProperty(PropertyName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).SetValue(this._Object, Val, null);
        }
    }
}

