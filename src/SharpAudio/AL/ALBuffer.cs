using System.Runtime.InteropServices;
using SharpAudio.ALBinding;

namespace SharpAudio.AL
{
    internal sealed class ALBuffer : AudioBuffer
    {
        public ALBuffer()
        {
            var buffers = new uint[1];
            AlNative.alGenBuffers(1, buffers);
            ALEngine.checkAlError();
            Buffer = buffers[0];
        }

        public uint Buffer { get; }

        public override unsafe void BufferData<T>(T[] buffer, AudioFormat format)
        {
            var fmt = format.Channels == 2 ? AlNative.AL_FORMAT_STEREO8 : AlNative.AL_FORMAT_MONO8;
            var sizeInBytes = sizeof(T) * buffer.Length;

            if (format.BitsPerSample == 16) fmt++;

            var handle = GCHandle.Alloc(buffer);
            var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);

            AlNative.alBufferData(Buffer, fmt, ptr, sizeInBytes, format.SampleRate);
            ALEngine.checkAlError();

            handle.Free();
            _format = format;
        }

        public override void Dispose()
        {
            AlNative.alDeleteBuffers(1, new[] {Buffer});
        }
    }
}