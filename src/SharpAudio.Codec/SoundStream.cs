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
        public bool IsPlaying => currentState == SoundState.Playing;

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

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        /// <summary>
        /// Start playing the soundstream 
        /// </summary>
        public void PlayPause()
        {
            Console.WriteLine(currentState.ToString());

            switch (currentState)
            {
                case SoundState.Idle:
                    currentState = SoundState.PreparePlay;
                    break;
                case SoundState.PreparePlay:
                case SoundState.Playing:
                    currentState = SoundState.Paused;
                    break;
                case SoundState.Paused:
                    currentState = SoundState.Playing;
                    break;
            }

        }

        volatile SoundState currentState;

        enum SoundState
        {
            Idle,
            PreparePlay,
            Playing,
            Paused,
            Stop
        }

        private async Task MainLoop()
        {

            do
            {

                switch (currentState)
                {
                    case SoundState.PreparePlay:
                        Source.Play();
                        currentState = SoundState.Playing;
                        break;
                    case SoundState.Playing:

                        if (Source.BuffersQueued < 3)
                        {
                            _decoder.GetSamples(SampleQuantum, ref _data);

                            if (_data is null)
                                _data = _silence;

                            _chain.QueueData(Source, _data, Format);
                        }

                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
                        break;

                    case SoundState.Paused:
                        if (Source.BuffersQueued < 3)
                        {
                            _data = _silence;
                            _chain.QueueData(Source, _data, Format);
                        }
                        break;
                }

                await Task.Delay(SampleWait);

                if (!Source.IsPlaying() && _decoder.IsFinished )
                    currentState = SoundState.Stop;

            } while (currentState != SoundState.Stop);

            Source.Stop();

            currentState = SoundState.Idle;
        }

        /// <summary>
        /// Stop the soundstream
        /// </summary>
        public void Stop()
        {
            currentState = SoundState.Stop;
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            Source.Dispose();
        }
    }
}
