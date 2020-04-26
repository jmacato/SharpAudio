using System;
using System.IO;
using System.Threading;

namespace SharpAudio.Codec.Mp3
{
    class SlidingStream : Stream
    {
        public override bool CanRead
        {
            get { throw new NotImplementedException(); }
        }

        public override bool CanSeek
        {
            get { throw new NotImplementedException(); }
        }

        public override bool CanWrite
        {
            get { throw new NotImplementedException(); }
        }

        public override void Flush()
        {
            _length = 0;
            blocks = new ConcurrentQueue<byte[]>();
            currentBlock = null;
        }

        public override long Length
        {
            get { return _length; }
        }
        volatile int _length;

        public override long Position
        {
            get
            {
                return 0;
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        ConcurrentQueue<byte[]> blocks;
        byte[] currentBlock;

        public SlidingStream()
        {
            blocks = new ConcurrentQueue<byte[]>();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_length < count)
                count = _length;

            if (blocks.Count == 0 && (currentBlock == null || currentBlock.Length == 0))
                return 0;

            int readCount = 0;
            if (currentBlock == null || currentBlock.Length == 0)
                if (!blocks.TryDequeue(out currentBlock))
                    throw new InvalidOperationException("Failed to dequeue from SlidingStream");

            while (readCount < count)
            {
                if (readCount + currentBlock.Length < count)
                {
                    Buffer.BlockCopy(currentBlock, 0, buffer, offset + readCount, currentBlock.Length);
                    readCount += currentBlock.Length;
                    if (!blocks.TryDequeue(out currentBlock))
                        throw new InvalidOperationException("Failed to dequeue from SlidingStream with half-read buffer");
                }
                else
                {
                    Buffer.BlockCopy(currentBlock, 0, buffer, offset + readCount, count - readCount);
                    //resize the queued buffer to store only the unread data
                    Buffer.BlockCopy(currentBlock, count - readCount, currentBlock, 0, currentBlock.Length - (count - readCount));
                    Array.Resize(ref currentBlock, currentBlock.Length - (count - readCount));
                    readCount = count;
                    break;
                }
            }

            Interlocked.Add(ref _length, -readCount);

            return readCount;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            byte[] bufferCopy = new byte[count];
            Buffer.BlockCopy(buffer, offset, bufferCopy, 0, count);
            blocks.Enqueue(bufferCopy);
            Interlocked.Add(ref _length, count);
        }
    }
}
