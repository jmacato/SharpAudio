using SharpAudio.Codec.Mp3;
using SharpAudio.Codec.Vorbis;
using SharpAudio.Codec.Wave;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SharpAudio.Codec
{
    public sealed class SoundStream : IDisposable
    {
        private Decoder _decoder;
        private BufferChain _chain;
        private AudioBuffer _buffer;
        private byte[] _data;
        private Stopwatch _timer;
        private readonly TimeSpan SampleQuantum = TimeSpan.FromSeconds(0.25);
        private readonly TimeSpan SampleWait = TimeSpan.FromSeconds(0.05);

        private static byte[] MakeFourCC(string magic)
        {
            return new[] {  (byte)magic[0],
                            (byte)magic[1],
                            (byte)magic[2],
                            (byte)magic[3]};
        }

        /// <summary>
        /// The audio format of this stream
        /// </summary>
        public AudioFormat Format => _decoder.Format;

        /// <summary>
        /// The underlying source
        /// </summary>
        public AudioSource Source { get; }

        /// <summary>
        /// Wether or not the audio is finished
        /// </summary>
        public bool IsPlaying => Source.IsPlaying();

        /// <summary>
        /// Wether or not the audio is streamed
        /// </summary>
        public bool IsStreamed { get; }

        /// <summary>
        /// The volume of the source
        /// </summary>
        public float Volume
        {
            get => Source.Volume;
            set => Source.Volume = value;
        }

        /// <summary>
        /// Duration when provided by the decoder. Otherwise 0
        /// </summary>
        public TimeSpan Duration => _decoder.Duration;

        /// <summary>
        /// Current position inside the stream
        /// </summary>
        public TimeSpan Position => _timer.Elapsed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundStream"/> class.
        /// </summary>
        /// <param name="stream">The sound stream.</param>
        /// <param name="engine">The audio engine</param>
        public SoundStream(Stream stream, AudioEngine engine)
        {
            if (stream == null)
                throw new ArgumentNullException("Stream cannot be null!");
 

            _decoder = new FfmpegDecoder(stream);

            Source = engine.CreateSource();

            _chain = new BufferChain(engine);

            _decoder.GetSamples(SampleQuantum, ref _data);
            _chain.QueueData(Source, _data, _decoder.Format);

            _decoder.GetSamples(SampleQuantum, ref _data);
            _chain.QueueData(Source, _data, _decoder.Format);

            _decoder.GetSamples(SampleQuantum, ref _data);
            _chain.QueueData(Source, _data, _decoder.Format);

            _timer = new Stopwatch();
        }

        /// <summary>
        /// Start playing the soundstream 
        /// </summary>
        public void Play()
        {
            Source.Play();
            _timer.Start();

            // if (IsStreamed)
            // {
            var t = Task.Run(() =>
            {
                while (Source.IsPlaying())
                {
                    if (Source.BuffersQueued < 3 && !_decoder.IsFinished)
                    {
                        _decoder.GetSamples(SampleQuantum, ref _data);
                        _chain.QueueData(Source, _data, Format);
                    }

                    Thread.Sleep(SampleWait);
                }
                _timer.Stop();
            });
            // }
        }

        /// <summary>
        /// Stop the soundstream
        /// </summary>
        public void Stop()
        {
            Source.Stop();
            _timer.Stop();
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            Source.Dispose();
        }
    }
}
