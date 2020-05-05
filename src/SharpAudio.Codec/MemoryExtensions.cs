using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace SharpAudio.Codec
{
    public static class MemoryExtensions
    {
        public static Memory<short> AsShorts(this Memory<byte> data) => Cast<byte, short>(data);

        public static Memory<TTo> Cast<TFrom, TTo>(Memory<TFrom> from)
                where TFrom : unmanaged
                where TTo : unmanaged
        {
            // avoid the extra allocation/indirection, at the cost of a gen-0 box
            if (typeof(TFrom) == typeof(TTo)) return (Memory<TTo>)(object)from;

            return new CastMemoryManager<TFrom, TTo>(from).Memory;
        }

        private sealed class CastMemoryManager<TFrom, TTo> : MemoryManager<TTo>
        where TFrom : unmanaged
        where TTo : unmanaged
        {
            private readonly Memory<TFrom> _from;

            public CastMemoryManager(Memory<TFrom> from) => _from = from;

            public override Span<TTo> GetSpan()
                => MemoryMarshal.Cast<TFrom, TTo>(_from.Span);

            protected override void Dispose(bool disposing) { }
            public override MemoryHandle Pin(int elementIndex = 0)
            {
                if (elementIndex == 0)
                {
                    return _from.Pin();
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
            public override void Unpin()
            {

            }
        }
    }
}
