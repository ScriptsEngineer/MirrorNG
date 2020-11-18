using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Mirror
{
    /// <summary>
    /// a class that holds writers for the different types
    /// Note that c# creates a different static variable for each
    /// type
    /// This will be populated by the weaver
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public static class Writer<T>
    {
        public static Action<NetworkWriter, T> Write { internal get; set; }
    }

    /// <summary>
    /// Binary stream Writer. Supports simple types, buffers, arrays, structs, and nested types
    /// <para>Use <see cref="NetworkWriterPool.GetWriter">NetworkWriter.GetWriter</see> to reduce memory allocation</para>
    /// </summary>
    public class NetworkWriter
    {
        public const int MaxStringLength = 1024 * 32;

        // create writer immediately with it's own buffer so no one can mess with it and so that we can resize it.
        // note: BinaryWriter allocates too much, so we only use a MemoryStream
        // => 1500 bytes by default because on average, most packets will be <= MTU
        byte[] buffer = new byte[1500];

        // 'int' is the best type for .Position. 'short' is too small if we send >32kb which would result in negative .Position
        // -> converting long to int is fine until 2GB of data (MAX_INT), so we don't have to worry about overflows here
        int position;

        public int Length { get; private set; }

        public int Position
        {
            get => position;
            set
            {
                position = value;
                EnsureLength(value);
            }
        }

        /// <summary>
        /// Reset both the position and length of the stream
        /// </summary>
        /// <remarks>
        /// Leaves the capacity the same so that we can reuse this writer without extra allocations
        /// </remarks>
        public void Reset()
        {
            position = 0;
            Length = 0;
        }

        /// <summary>
        /// Sets length, moves position if it is greater than new length
        /// </summary>
        /// <param name="newLength"></param>
        /// <remarks>
        /// Zeros out any extra length created by setlength
        /// </remarks>
        public void SetLength(int newLength)
        {
            int oldLength = Length;

            // ensure length & capacity
            EnsureLength(newLength);

            // zero out new length
            if (oldLength < newLength)
            {
                Array.Clear(buffer, oldLength, newLength - oldLength);
            }

            Length = newLength;
            position = Mathf.Min(position, Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureLength(int value)
        {
            if (Length < value)
            {
                Length = value;
                EnsureCapacity(value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureCapacity(int value)
        {
            if (buffer.Length < value)
            {
                int capacity = Math.Max(value, buffer.Length * 2);
                Array.Resize(ref buffer, capacity);
            }
        }

        // MemoryStream has 3 values: Position, Length and Capacity.
        // Position is used to indicate where we are writing
        // Length is how much data we have written
        // capacity is how much memory we have allocated
        // ToArray returns all the data we have written,  regardless of the current position
        public byte[] ToArray()
        {
            byte[] data = new byte[Length];
            Array.ConstrainedCopy(buffer, 0, data, 0, Length);
            return data;
        }

        // Gets the serialized data in an ArraySegment<byte>
        // this is similar to ToArray(),  but it gets the data in O(1)
        // and without allocations.
        // Do not write anything else or modify the NetworkWriter
        // while you are using the ArraySegment
        public ArraySegment<byte> ToArraySegment()
        {
            return new ArraySegment<byte>(buffer, 0, Length);
        }

        //   WriteBlittable assumes same endianness for server & client.
        //   All Unity 2018+ platforms are little endian.
        //   => run NetworkWriterTests.BlittableOnThisPlatform() to verify!
        internal unsafe void WriteBlittable<T>(T value)
            where T : unmanaged
        {
            // check if blittable for safety
#if UNITY_EDITOR
            if (!UnsafeUtility.IsBlittable(typeof(T)))
            {
                Debug.LogError(typeof(T) + " is not blittable!");
                return;
            }
#endif
            // calculate size
            //   sizeof(T) gets the managed size at compile time.
            //   Marshal.SizeOf<T> gets the unmanaged size at runtime (slow).
            // => our 1mio writes benchmark is 6x slower with Marshal.SizeOf<T>
            // => for blittable types, sizeof(T) is even recommended:
            // https://docs.microsoft.com/en-us/dotnet/standard/native-interop/best-practices
            int size = sizeof(T);

            // ensure length
            EnsureLength(position + size);

            // write blittable
            fixed (byte* ptr = &buffer[Position])
            {
                // cast buffer to T* pointer, then assign value to the area
                *(T*)ptr = value;
            }
            Position += size;
        }


        // for byte arrays with consistent size, where the reader knows how many to read
        // (like a packet opcode that's always the same)
        public void WriteBytes(byte[] buffer, int offset, int count)
        {
            EnsureLength(position + count);
            Array.ConstrainedCopy(buffer, offset, this.buffer, position, count);
            position += count;
        }

        public void WriteUInt32(uint value)
        {
            EnsureLength(position + 4);
            buffer[position++] = (byte)value;
            buffer[position++] = (byte)(value >> 8);
            buffer[position++] = (byte)(value >> 16);
            buffer[position++] = (byte)(value >> 24);
        }

        public void WriteInt32(int value) => WriteUInt32((uint)value);

        public void WriteUInt64(ulong value)
        {
            EnsureLength(position + 8);
            buffer[position++] = (byte)value;
            buffer[position++] = (byte)(value >> 8);
            buffer[position++] = (byte)(value >> 16);
            buffer[position++] = (byte)(value >> 24);
            buffer[position++] = (byte)(value >> 32);
            buffer[position++] = (byte)(value >> 40);
            buffer[position++] = (byte)(value >> 48);
            buffer[position++] = (byte)(value >> 56);
        }

        public void WriteInt64(long value) => WriteUInt64((ulong)value);

        /// <summary>
        /// Writes any type that mirror supports
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        public void Write<T>(T value)
        {
            if (Writer<T>.Write == null)
                Debug.AssertFormat(
                    Writer<T>.Write != null,
                    @"No writer found for {0}. See https://mirrorng.github.io/MirrorNG/Articles/General/Troubleshooting.html for details",
                    typeof(T));

            Writer<T>.Write(this, value);
        }
    }


    // Mirror's Weaver automatically detects all NetworkWriter function types,
    // but they do all need to be extensions.
    public static class NetworkWriterExtensions
    {
        // cache encoding instead of creating it with BinaryWriter each time
        // 1000 readers before:  1MB GC, 30ms
        // 1000 readers after: 0.8MB GC, 18ms
        static readonly UTF8Encoding encoding = new UTF8Encoding(false, true);
        static readonly byte[] stringBuffer = new byte[NetworkWriter.MaxStringLength];

        public static void WriteByte(this NetworkWriter writer, byte value) => writer.WriteBlittable(value);
        public static void WriteSByte(this NetworkWriter writer, sbyte value) => writer.WriteBlittable(value);
        public static void WriteChar(this NetworkWriter writer, char value) => writer.WriteBlittable((short)value); // char isn't blittable
        public static void WriteBoolean(this NetworkWriter writer, bool value) => writer.WriteBlittable((byte)(value ? 1 : 0)); // bool isn't blittable
        public static void WriteUInt16(this NetworkWriter writer, ushort value) => writer.WriteBlittable(value);
        public static void WriteInt16(this NetworkWriter writer, short value) => writer.WriteUInt16((ushort)value);
        public static void WriteUInt32(this NetworkWriter writer, uint value) => writer.WriteBlittable(value);
        public static void WriteInt32(this NetworkWriter writer, int value) => writer.WriteBlittable(value);
        public static void WriteUInt64(this NetworkWriter writer, ulong value) => writer.WriteBlittable(value);
        public static void WriteInt64(this NetworkWriter writer, long value) => writer.WriteBlittable(value);
        public static void WriteSingle(this NetworkWriter writer, float value) => writer.WriteBlittable(value);
        public static void WriteDouble(this NetworkWriter writer, double value) => writer.WriteBlittable(value);
        public static void WriteDecimal(this NetworkWriter writer, decimal value) => writer.WriteBlittable(value);

        public static void WriteString(this NetworkWriter writer, string value)
        {
            // write 0 for null support, increment real size by 1
            // (note: original HLAPI would write "" for null strings, but if a
            //        string is null on the server then it should also be null
            //        on the client)
            if (value == null)
            {
                writer.WriteUInt16(0);
                return;
            }

            // write string with same method as NetworkReader
            // convert to byte[]
            int size = encoding.GetBytes(value, 0, value.Length, stringBuffer, 0);

            // check if within max size
            if (size >= NetworkWriter.MaxStringLength)
            {
                throw new DataMisalignedException("NetworkWriter.Write(string) too long: " + size + ". Limit: " + NetworkWriter.MaxStringLength);
            }

            // write size and bytes
            writer.WriteUInt16(checked((ushort)(size + 1)));
            writer.WriteBytes(stringBuffer, 0, size);
        }

        // for byte arrays with dynamic size, where the reader doesn't know how many will come
        // (like an inventory with different items etc.)
        public static void WriteBytesAndSize(this NetworkWriter writer, byte[] buffer, int offset, int count)
        {
            // null is supported because [SyncVar]s might be structs with null byte[] arrays
            // write 0 for null array, increment normal size by 1 to save bandwith
            // (using size=-1 for null would limit max size to 32kb instead of 64kb)
            if (buffer == null)
            {
                writer.WriteUInt32(0u);
                return;
            }
            writer.WriteUInt32(checked((uint)count) + 1u);
            writer.WriteBytes(buffer, offset, count);
        }

        // Weaver needs a write function with just one byte[] parameter
        // (we don't name it .Write(byte[]) because it's really a WriteBytesAndSize since we write size / null info too)
        public static void WriteBytesAndSize(this NetworkWriter writer, byte[] buffer)
        {
            // buffer might be null, so we can't use .Length in that case
            writer.WriteBytesAndSize(buffer, 0, buffer != null ? buffer.Length : 0);
        }

        public static void WriteBytesAndSizeSegment(this NetworkWriter writer, ArraySegment<byte> buffer)
        {
            writer.WriteBytesAndSize(buffer.Array, buffer.Offset, buffer.Count);
        }

        public static void WriteVector2(this NetworkWriter writer, Vector2 value) => writer.WriteBlittable(value);
        public static void WriteVector3(this NetworkWriter writer, Vector3 value) => writer.WriteBlittable(value);
        public static void WriteVector4(this NetworkWriter writer, Vector4 value) => writer.WriteBlittable(value);
        public static void WriteVector2Int(this NetworkWriter writer, Vector2Int value) => writer.WriteBlittable(value);
        public static void WriteVector3Int(this NetworkWriter writer, Vector3Int value) => writer.WriteBlittable(value);
        public static void WriteColor(this NetworkWriter writer, Color value) => writer.WriteBlittable(value);
        public static void WriteColor32(this NetworkWriter writer, Color32 value) => writer.WriteBlittable(value);
        public static void WriteQuaternion(this NetworkWriter writer, Quaternion value) => writer.WriteBlittable(value);
        public static void WriteRect(this NetworkWriter writer, Rect value) => writer.WriteBlittable(value);
        public static void WritePlane(this NetworkWriter writer, Plane value) => writer.WriteBlittable(value);
        public static void WriteRay(this NetworkWriter writer, Ray value) => writer.WriteBlittable(value);
        public static void WriteMatrix4X4(this NetworkWriter writer, Matrix4x4 value) => writer.WriteBlittable(value);

        public static void WriteGuid(this NetworkWriter writer, Guid value)
        {
            byte[] data = value.ToByteArray();
            writer.WriteBytes(data, 0, data.Length);
        }

        public static void WriteNetworkIdentity(this NetworkWriter writer, NetworkIdentity value)
        {
            if (value == null)
            {
                writer.WriteUInt32(0);
                return;
            }
            writer.WriteUInt32(value.NetId);
        }

        public static void WriteUri(this NetworkWriter writer, Uri uri)
        {
            writer.WriteString(uri.ToString());
        }

        public static void WriteList<T>(this NetworkWriter writer, List<T> list)
        {
            if (list is null)
            {
                writer.WriteInt32(-1);
                return;
            }
            writer.WriteInt32(list.Count);
            for (int i=0; i< list.Count; i++)
                writer.Write(list[i]);
        }

        public static void WriteArray<T>(this NetworkWriter writer, T[] array)
        {
            if (array is null)
            {
                writer.WriteInt32(-1);
                return;
            }
            writer.WriteInt32(array.Length);
            for (int i = 0; i < array.Length; i++)
                writer.Write(array[i]);
        }

        public static void WriteArraySegment<T>(this NetworkWriter writer, ArraySegment<T> segment)
        {
            int length = segment.Count;
            writer.WriteInt32(length);
            for (int i = 0; i< length; i++)
            {
                writer.Write(segment.Array[segment.Offset + i]);
            }
        }
    }
}
