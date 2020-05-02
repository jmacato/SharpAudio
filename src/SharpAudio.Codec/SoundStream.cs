using System.ComponentModel;
using SharpAudio.Codec.FFMPEG;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SharpAudio.Codec
{
    public sealed class SoundStream : IDisposable, INotifyPropertyChanged
    {
        private Decoder _decoder;
        private BufferChain _chain;
        private byte[] _silence;
        private AudioBuffer _buffer;
        private byte[] _data;
        private Stopwatch _timer;
        private readonly TimeSpan SampleQuantum = TimeSpan.FromSeconds(0.1);
        private readonly TimeSpan SampleWait = TimeSpan.FromMilliseconds(1);

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// The audio format of this stream
        /// </summary>
        public AudioFormat Format => _decoder.Format;

        /// <summary>
        /// The metadata of the decoded data;
        /// </summary>
        public AudioMetadata Metadata => _decoder.Metadata;

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
        public TimeSpan Position => _decoder.Position;

        public void TrySeek(TimeSpan seek) => _decoder.TrySeek(seek);

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundStream"/> class.
        /// </summary>
        /// <param name="stream">The sound stream.</param>
        /// <param name="engine">The audio engine</param>
        public SoundStream(Stream stream, AudioEngine engine)
        {
            if (stream == null)
                throw new ArgumentNullException("Stream cannot be null!");

            IsStreamed = !stream.CanSeek;

            Source = engine.CreateSource();

            _decoder = new FFmpegDecoder(stream);

            _chain = new BufferChain(engine);

            _silence = new byte[(int)(_decoder.Format.Channels * _decoder.Format.SampleRate * SampleQuantum.TotalSeconds)];

            // Prime the buffer chain with empty data.
            _chain.QueueData(Source, _silence, Format);

            _timer = new Stopwatch();
        }

        /// <summary>
        /// Start playing the soundstream 
        /// </summary>
        public void Play()
        {
            Source.Play();
            _timer.Start();

            Task.Factory.StartNew(async () =>
            {
                while (Source.IsPlaying())
                {
                    if (Source.BuffersQueued < 3 && !_decoder.IsFinished)
                    {
                        _decoder.GetSamples(SampleQuantum, ref _data);
                        
                         if (_data is null)
                            _data = _silence;

                        _chain.QueueData(Source, _data, Format);
                    }

                    await Task.Delay(SampleWait);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
                }
                _timer.Stop();
            }, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
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
