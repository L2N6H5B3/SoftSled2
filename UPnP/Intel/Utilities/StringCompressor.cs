namespace Intel.Utilities
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;

    public class StringCompressor
    {
        public static int BestMatch(byte[] buffer, int Offset, int bufferLength, int WindowSize, int MinMatchSize, int MaxMatchSize, out int MatchOffset, out int MatchLength)
        {
            int num = ((Offset - WindowSize) < 0) ? 0 : (Offset - WindowSize);
            int srcLength = Offset - num;
            int num6 = 0;
            int num7 = MinMatchSize;
            MatchOffset = 0;
            int num8 = 0;
            for (int i = 0; i < MinMatchSize; i++)
            {
                int num3;
                int targetLength = MinMatchSize;
                if (FindMatch(buffer, num + num8, srcLength, buffer, Offset + num8, targetLength, out num3))
                {
                    int num4;
                    while (((Offset + MinMatchSize) <= bufferLength) && FindMatch(buffer, num + num8, srcLength, buffer, Offset + num8, targetLength, out num4))
                    {
                        num3 = num4;
                        targetLength++;
                    }
                    targetLength--;
                    if (targetLength > MaxMatchSize)
                    {
                        targetLength = MaxMatchSize;
                    }
                    if (num8 == 0)
                    {
                        MatchOffset = num3;
                    }
                }
                if (targetLength == MaxMatchSize)
                {
                    MatchLength = targetLength;
                    MatchOffset = num3;
                    return num8;
                }
                if (targetLength > num7)
                {
                    num6 = num8;
                    num7 = targetLength;
                    MatchOffset = num3;
                }
                num8++;
            }
            MatchLength = num7;
            return num6;
        }

        public static byte[] CompressString(string str)
        {
            byte[] bytes = new UTF8Encoding().GetBytes(str);
            return CompressString(bytes, 0, bytes.Length);
        }

        private static byte[] CompressString(byte[] inbuf, int inbufOffset, int inbufLength)
        {
            return CompressString(inbuf, inbufOffset, inbufLength, 0);
        }

        private static byte[] CompressString(byte[] inbuf, int inbufOffset, int inbufLength, int loop)
        {
            byte length;
            MemoryStream stream = new MemoryStream();
            MemoryStream stream2 = new MemoryStream();
            int count = 0;
            int srcOffset = 0;
            int srcLength = 0;
            int targetOffset = inbufOffset;
            int targetLength = 4;
            bool flag = false;
            while ((targetOffset + targetLength) <= inbuf.Length)
            {
                int num4;
                if ((count - 0x3ff) < 0)
                {
                    srcOffset = 0;
                }
                else
                {
                    srcOffset = count - 0x3ff;
                }
                srcLength = count - srcOffset;
                if (FindMatch(inbuf, srcOffset, srcLength, inbuf, targetOffset, targetLength, out num4))
                {
                    int num9 = BestMatch(inbuf, targetOffset, inbuf.Length, 0x3ff, 4, 0x3f, out num4, out targetLength);
                    for (int i = 0; i < num9; i++)
                    {
                        stream2.Write(inbuf, targetOffset, 1);
                        count++;
                        if (stream2.Length == 0xffL)
                        {
                            if (flag)
                            {
                                byte[] buffer = new byte[2];
                                stream.Write(buffer, 0, 2);
                            }
                            stream.Write(new byte[] { 0xff }, 0, 1);
                            stream.Write(stream2.ToArray(), 0, (int) stream2.Length);
                            stream2 = new MemoryStream();
                            flag = true;
                        }
                        srcOffset++;
                        targetOffset++;
                    }
                    if (stream2.Length > 0L)
                    {
                        if (flag)
                        {
                            byte[] buffer4 = new byte[2];
                            stream.Write(buffer4, 0, 2);
                        }
                        length = (byte) stream2.Length;
                        stream.Write(BitConverter.GetBytes((short) length), 0, 1);
                        stream.Write(stream2.ToArray(), 0, (int) stream2.Length);
                        stream2 = new MemoryStream();
                        flag = true;
                    }
                    num4 = srcOffset + num4;
                    num4 = count - num4;
                    uint num2 = (uint) num4;
                    num2 = num2 << 6;
                    num2 |= (uint) targetLength;
                    if (!flag)
                    {
                        byte[] buffer5 = new byte[1];
                        stream.Write(buffer5, 0, 1);
                    }
                    UTF8Encoding encoding = new UTF8Encoding();
                    string str = encoding.GetString(inbuf, count - num4, targetLength);
                    string str2 = encoding.GetString(inbuf, 0, count);
                    count += targetLength;
                    stream.Write(BitConverter.GetBytes(num2), 0, 1);
                    stream.Write(BitConverter.GetBytes(num2), 1, 1);
                    targetOffset += targetLength;
                    targetLength = 4;
                    flag = false;
                }
                else
                {
                    stream2.Write(inbuf, targetOffset, 1);
                    count++;
                    if (stream2.Length == 0xffL)
                    {
                        if (flag)
                        {
                            byte[] buffer6 = new byte[2];
                            stream.Write(buffer6, 0, 2);
                        }
                        stream.Write(new byte[] { 0xff }, 0, 1);
                        stream.Write(stream2.ToArray(), 0, (int) stream2.Length);
                        stream2 = new MemoryStream();
                        flag = true;
                    }
                    targetOffset++;
                }
            }
            if (stream2.Length > 0L)
            {
                if (flag)
                {
                    byte[] buffer8 = new byte[2];
                    stream.Write(buffer8, 0, 2);
                }
                length = (byte) stream2.Length;
                stream.Write(BitConverter.GetBytes((short) length), 0, 1);
                stream.Write(stream2.ToArray(), 0, (int) stream2.Length);
                stream2 = new MemoryStream();
                flag = true;
            }
            if ((inbuf.Length - targetOffset) != 0)
            {
                if (flag)
                {
                    byte[] buffer9 = new byte[2];
                    stream.Write(buffer9, 0, 2);
                }
                length = (byte) (inbuf.Length - targetOffset);
                stream.Write(BitConverter.GetBytes((short) length), 0, 1);
                stream.Write(inbuf, targetOffset, inbuf.Length - targetOffset);
                flag = true;
            }
            if (flag)
            {
                byte[] buffer10 = new byte[2];
                stream.Write(buffer10, 0, 2);
            }
            return stream.ToArray();
        }

        public static string DecompressString(byte[] buffer, int offset, int length)
        {
            UTF8Encoding encoding = new UTF8Encoding();
            MemoryStream stream = new MemoryStream();
            int index = 0;
            uint num4 = 0x3f;
            while (index < (length - offset))
            {
                byte count = buffer[index];
                if (count != 0)
                {
                    stream.Write(buffer, index + 1, count);
                    index += 1 + count;
                }
                else
                {
                    index++;
                }
                if (index < (length - offset))
                {
                    uint num3 = BitConverter.ToUInt16(buffer, index);
                    if (num3 == 0)
                    {
                        index += 2;
                    }
                    else
                    {
                        int num6 = (int) (num3 & num4);
                        int num5 = (int) (num3 >> 6);
                        stream.Write(stream.ToArray(), ((int) stream.Length) - num5, num6);
                        index += 2;
                    }
                }
            }
            return encoding.GetString(stream.ToArray());
        }

        private static bool FindMatch(byte[] srcBuffer, int srcOffset, int srcLength, byte[] TargetBuffer, int TargetOffset, int TargetLength, out int Offset)
        {
            int num = -1;
            Offset = -1;
            bool flag = false;
            bool flag2 = false;
            if ((TargetOffset + TargetLength) <= TargetBuffer.Length)
            {
                while (!flag2)
                {
                    do
                    {
                        num++;
                    }
                    while ((num < srcLength) && (srcBuffer[srcOffset + num] != TargetBuffer[TargetOffset]));
                    if ((num < srcLength) && ((srcLength - num) >= TargetLength))
                    {
                        flag = true;
                        for (int i = 0; i < TargetLength; i++)
                        {
                            if (srcBuffer[(srcOffset + num) + i] != TargetBuffer[TargetOffset + i])
                            {
                                flag = false;
                                break;
                            }
                        }
                        if (flag)
                        {
                            flag2 = true;
                            Offset = num;
                        }
                    }
                    else
                    {
                        flag2 = true;
                    }
                }
                return flag;
            }
            return false;
        }
    }
}

