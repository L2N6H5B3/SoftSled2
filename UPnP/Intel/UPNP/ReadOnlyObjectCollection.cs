namespace Intel.UPNP
{
    using System;
    using System.Collections;

    public class ReadOnlyObjectCollection : ReadOnlyCollectionBase
    {
        public ReadOnlyObjectCollection(ICollection items)
        {
            if ((items != null) && (items.Count > 0))
            {
                foreach (object obj2 in items)
                {
                    base.InnerList.Add(obj2);
                }
            }
        }

        public object Item(int Index)
        {
            return base.InnerList[Index];
        }
    }
}

