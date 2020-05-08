using System.ComponentModel;
using System;

namespace SharpAudio.Codec
{
    public abstract class Decoder : IDisposable
    {
        protected AudioFormat _audioFormat;
        // protected AudioMetadata _audioMetaData;
        protected int _numSamples = 0;
        protected int _readSize;

        /// <summary>
        /// The format of the decoded data
        /// </summary>
        public AudioFormat Format => _audioFormat;

        /// <summary>
        /// The metadata of the decoded data;
        /// </summary>
        // public AudioMetadata Metadata => _audioMetaData;

        /// <summary>
        /// Specifies the length of the decoded data. If not available returns 0
        /// </summary>
        public virtual TimeSpan Duration => TimeSpan.FromSeconds((float)_numSamples / (_audioFormat.SampleRate * _audioFormat.Channels));

        /// <summary>
        /// Wether or not the decoder reached the end of data
        /// </summary>
        public abstract bool IsFinished { get; }
        public abstract TimeSpan Position { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public abstract void Dispose();

        /// <summary>
        /// Reads the specified amount of samples
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public abstract long GetSamples(int samples, ref byte[] data);

        /// <summary>
        /// Reads the specified amount of samples
        /// </summary>
        /// <param name="span"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public long GetSamples(TimeSpan span, ref byte[] data)
        {
            int numSamples = (int)(span.TotalSeconds * Format.SampleRate * Format.Channels);
            return GetSamples(numSamples, ref data);
        }

        /// <summary>
        /// Read all samples from this stream
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public long GetSamples(ref byte[] data)
        {
            return GetSamples(_numSamples, ref data);
        }

        public abstract void TrySeek(TimeSpan time);
    }
}
