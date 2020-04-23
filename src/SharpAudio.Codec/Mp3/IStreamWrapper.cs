// using System.Runtime.InteropServices;
// using System;
// using System.IO;
 
// namespace SharpAudio.Codec.Mp3
// {

//     [ComImport]
//     [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
//     [Guid("0000000c-0000-0000-C000-000000000046")]
//     public interface IStream
//     {
//         [PreserveSig]
//         uint Read([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] [Out] byte[] pv, int cb, IntPtr pcbRead);

//         [PreserveSig]
//         uint Write([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pv, int cb, IntPtr pcbWritten);

//         [PreserveSig]
//         uint Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition);

//         [PreserveSig]
//         uint SetSize(long libNewSize);

//         uint CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten);

//         [PreserveSig]
//         uint Commit(int grfCommitFlags);

//         [PreserveSig]
//         uint Revert();

//         [PreserveSig]
//         uint LockRegion(long libOffset, long cb, int dwLockType);

//         [PreserveSig]
//         uint UnlockRegion(long libOffset, long cb, int dwLockType);

//         [PreserveSig]
//         uint Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag);

//         [PreserveSig]
//         uint Clone(out IStream ppstm);
//     }

//     /// <summary>
//     /// see http://msdn.microsoft.com/en-us/library/windows/desktop/ms752876(v=vs.85).aspx
//     /// </summary>
//     public class IStreamWrapper : Stream, IStream
//     {
//         private Stream _stream;

//         public IStreamWrapper(Stream stream)
//             : this(stream, true)
//         {
//         }

//         internal IStreamWrapper(Stream stream, bool sync)
//         {
//             if (stream == null)
//                 throw new ArgumentNullException("stream");

//             if (sync)
//             {
//                 stream = Stream.Synchronized(stream);
//             }
//             _stream = stream;
//         }

//         uint IStream.Clone(out IStream ppstm)
//         {
//             IStreamWrapper newstream = new IStreamWrapper(_stream, false);
//             ppstm = newstream; 
//             return HResult.S_OK;
//         }

//         uint IStream.Commit(int grfCommitFlags)
//         {
//             return HResult.E_NOTIMPL;
//         }

//         uint IStream.CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
//         {
//             return HResult.E_NOTIMPL;
//         }

//         uint IStream.LockRegion(long libOffset, long cb, int dwLockType)
//         {
//             return HResult.E_NOTIMPL;
//         }

//         uint IStream.Read(byte[] pv, int cb, IntPtr pcbRead)
//         {
//             if (!CanRead)
//                 throw new InvalidOperationException("Stream not readable");

//             int read = Read(pv, 0, cb);
//             if (pcbRead != IntPtr.Zero)
//                 Marshal.WriteInt64(pcbRead, read);
//             return HResult.S_OK;
//         }

//         uint IStream.Revert()
//         {
//             return HResult.E_NOTIMPL;
//         }

//         uint IStream.Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
//         {
//             SeekOrigin origin = (SeekOrigin)dwOrigin; //hope that the SeekOrigin enumeration won't change
//             long pos = Seek(dlibMove, origin);
//             if (plibNewPosition != IntPtr.Zero)
//                 Marshal.WriteInt64(plibNewPosition, pos);
//             return HResult.S_OK;
//         }

//         uint IStream.SetSize(long libNewSize)
//         {
//             return HResult.E_NOTIMPL;
//         }

//         uint IStream.Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
//         {
//             pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG();
//             pstatstg.cbSize = Length;
//             return HResult.S_OK;
//         }

//         uint IStream.UnlockRegion(long libOffset, long cb, int dwLockType)
//         {
//             return HResult.E_NOTIMPL;
//         }

//         uint IStream.Write(byte[] pv, int cb, IntPtr pcbWritten)
//         {
//             if (!CanWrite)
//                 throw new InvalidOperationException("Stream is not writeable.");

//             Write(pv, 0, cb);
//             if (pcbWritten != null)
//                 Marshal.WriteInt32(pcbWritten, cb);
//             return HResult.S_OK;
//         }

//         public override bool CanRead
//         {
//             get { return _stream.CanRead; }
//         }

//         public override bool CanSeek
//         {
//             get { return _stream.CanSeek; }
//         }

//         public override bool CanWrite
//         {
//             get { return _stream.CanWrite; }
//         }

//         public override void Flush()
//         {
//             _stream.Flush();
//         }

//         public override long Length
//         {
//             get { return _stream.Length; }
//         }

//         public override long Position
//         {
//             get
//             {
//                 return _stream.Position;
//             }
//             set
//             {
//                 _stream.Position = value;
//             }
//         }

//         public override int Read(byte[] buffer, int offset, int count)
//         {
//             return _stream.Read(buffer, offset, count);
//         }

//         public override long Seek(long offset, SeekOrigin origin)
//         {
//             return _stream.Seek(offset, origin);
//         }

//         public override void SetLength(long value)
//         {
//             _stream.SetLength(value);
//         }

//         public override void Write(byte[] buffer, int offset, int count)
//         {
//             _stream.Write(buffer, offset, count);
//         }

//         protected override void Dispose(bool disposing)
//         {
//             if (_stream != null)
//             {
//                 _stream.Dispose();
//                 _stream = null;
//             }
//         }
//     }
// }
