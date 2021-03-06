﻿using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Net.Libuv
{
    unsafe public abstract class UVStream : UVHandle
    {
        uv_stream_t* _stream;

        protected UVStream(UVLoop loop, HandleType type) : base(loop, type)
        {
            _stream = (uv_stream_t*)(Handle.ToInt64() + GetSize(HandleType.UV_HANDLE));
        }

        public void ReadStart()
        {
            EnsureNotDisposed();

            if (IsUnix)
            {
                UVException.ThrowIfError(UVInterop.uv_read_start(Handle, UVBuffer.AllocateUnixBuffer, ReadUnix));
            }
            else
            {
                UVException.ThrowIfError(UVInterop.uv_read_start(Handle, UVBuffer.AllocWindowsBuffer, ReadWindows));
            }
        }

        public event Action<Memory<byte>> ReadCompleted;
        public event Action EndOfStream;

        public unsafe void TryWrite(byte[] data)
        {
            Debug.Assert(data != null);
            EnsureNotDisposed();

            fixed (byte* pData = data)
            {
                IntPtr ptrData = (IntPtr)pData;
                if (IsUnix)
                {
                    var buffer = new UVBuffer.Unix(ptrData, (uint)data.Length);
                    UVException.ThrowIfError(UVInterop.uv_try_write(Handle, &buffer, 1));
                }
                else
                {
                    var buffer = new UVBuffer.Windows(ptrData, (uint)data.Length);
                    UVException.ThrowIfError(UVInterop.uv_try_write(Handle, &buffer, 1));
                }
            }
        }

        public unsafe void TryWrite(byte[] data, int length)
        {
            Debug.Assert(data != null);
            if (data.Length < length)
            {
                throw new ArgumentOutOfRangeException("length");
            }

            EnsureNotDisposed();

            fixed (byte* pData = data)
            {
                IntPtr ptrData = (IntPtr)pData;
                if (IsUnix)
                {
                    var buffer = new UVBuffer.Unix(ptrData, (uint)length);
                    UVException.ThrowIfError(UVInterop.uv_try_write(Handle, &buffer, 1));
                }
                else
                {
                    var buffer = new UVBuffer.Windows(ptrData, (uint)length);
                    UVException.ThrowIfError(UVInterop.uv_try_write(Handle, &buffer, 1));
                }
            }
        }

        public unsafe void TryWrite(Memory<byte> data)
        {
            // This can work with Span<byte> because it's synchronous but we need pinning support
            EnsureNotDisposed();

            void* pointer;
            if (!data.TryGetPointer(out pointer))
            {
                throw new InvalidOperationException("Pointer not available");
            }

            IntPtr ptrData = (IntPtr)pointer;
            var length = data.Length;

            if (IsUnix)
            {
                var buffer = new UVBuffer.Unix(ptrData, (uint)length);
                UVException.ThrowIfError(UVInterop.uv_try_write(Handle, &buffer, 1));
            }
            else
            {
                var buffer = new UVBuffer.Windows(ptrData, (uint)length);
                UVException.ThrowIfError(UVInterop.uv_try_write(Handle, &buffer, 1));
            }
        }

        static bool IsUnix
        {
            get
            {
                return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            }
        }

        void OnReadWindows(UVBuffer.Windows buffer, IntPtr bytesAvaliable)
        {
            // TODO: all branches need to release buffer, I think
            long bytesRead = bytesAvaliable.ToInt64();
            if (bytesRead == 0)
            {
                buffer.Dispose();
                return;
            }
            else if (bytesRead < 0)
            {
                var error = UVException.ErrorCodeToError((int)bytesRead);
                if (error == UVError.EOF)
                {
                    OnEndOfStream();
                    Dispose();
                    buffer.Dispose();
                }
                else if (error == UVError.ECONNRESET)
                {
                    Debug.Assert(buffer.Buffer == IntPtr.Zero && buffer.Length == 0);
                    // no need to dispose
                    // TODO: what should we do here?
                }
                else
                {
                    Dispose();
                    buffer.Dispose();
                    throw new UVException((int)bytesRead);
                }
            }
            else
            {
                var readSlice = new Memory<byte>((byte*)buffer.Buffer, (int)bytesRead);
                OnReadCompleted(readSlice);
                buffer.Dispose();
            }
        }

        void OnReadUnix(UVBuffer.Unix buffer, IntPtr bytesAvaliable)
        {
            long bytesRead = bytesAvaliable.ToInt64();
            if (bytesRead == 0)
            {
                return;
            }
            else if (bytesRead < 0)
            {
                var error = UVException.ErrorCodeToError((int)bytesRead);
                if (error == UVError.EOF)
                {
                    OnEndOfStream();
                    Dispose();
                }
                else
                {
                    Dispose();
                    throw new UVException((int)bytesRead);
                }
            }
            else
            {
                // This can be a Span<byte> but the samples pass it directly to TryWrite which
                // needs to unpack the data and turn it back into either an array or native memory
                var readSlice = new Memory<byte>((byte*)buffer.Buffer, (int)bytesRead);
                OnReadCompleted(readSlice);
                buffer.Dispose();
            }
        }

        void OnReadCompleted(Memory<byte> bytesRead)
        {
            if (ReadCompleted != null)
            {
                ReadCompleted(bytesRead);
            }
        }

        static UVInterop.read_callback_unix ReadUnix = OnReadUnix;
        static void OnReadUnix(IntPtr streamPointer, IntPtr size, ref UVBuffer.Unix buffer)
        {
            var stream = As<UVStream>(streamPointer);
            stream.OnReadUnix(buffer, size);
        }

        static UVInterop.read_callback_win ReadWindows = OnReadWindows;
        static void OnReadWindows(IntPtr streamPointer, IntPtr size, ref UVBuffer.Windows buffer)
        {
            var stream = As<UVStream>(streamPointer);
            stream.OnReadWindows(buffer, size);
        }

        protected virtual void OnEndOfStream()
        {
            if (EndOfStream != null)
            {
                EndOfStream();
            }
        }

        public override void Dispose(bool disposing)
        {
            EnsureNotDisposed();
            var request = new DisposeRequest(this);
        }
    }
}
