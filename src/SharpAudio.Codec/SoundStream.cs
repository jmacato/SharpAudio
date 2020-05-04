using System.ComponentModel;
using SharpAudio.Codec.FFMPEG;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpAudio.SpectrumAnalysis;
using System.Numerics;

namespace SharpAudio.Codec
{
    public sealed class SoundStream : IDisposable, INotifyPropertyChanged
    {
        private Decoder _decoder;
        private BufferChain _chain;
        private byte[] _silence;
        private AudioBuffer _buffer;
        private byte[] _data;
        private SoundStreamState _CurrentState;
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
        public bool IsPlaying => currentState == SoundStreamState.Playing;

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


        public CircularBuffer SamplesCopyBuf { get; }


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

            SamplesCopyBuf = new CircularBuffer((int)(_decoder.Format.Channels * _decoder.Format.SampleRate));

            // Prime the buffer chain with empty data.
            _chain.QueueData(Source, _silence, Format);

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        public Action<Complex[]> FFTDataReady;

        private async Task SpectrumLoop()
        {
            // Assuming 16 bit PCM, Little-endian, Variable Channels.
            int fftLength = 2048;
            int totalCh = _decoder.Format.Channels;
            int specSamples = fftLength * totalCh * sizeof(short);

            var tempBuf = new byte[specSamples];
            var smplInt = new byte[totalCh, fftLength * sizeof(short)];

            var samplesShort = new short[totalCh, fftLength];

            var summedSamples = new short[fftLength];

            var counters = new int[totalCh];
            var complexSamples = new Complex[fftLength];

            var m = (int)Math.Log(fftLength, 2.0);

            int curChByteRaw = 0;


            while (true)
            {
                await Task.Delay(1);

                if (currentState == SoundStreamState.Playing & SamplesCopyBuf.Length < specSamples) continue;
                if (FFTDataReady is null) return;

                SamplesCopyBuf.Read(tempBuf, 0, specSamples);

                for (int i = 0; i < specSamples; i++)
                {
                    smplInt[curChByteRaw, counters[curChByteRaw]] = tempBuf[i];
                    counters[curChByteRaw]++;

                    curChByteRaw++;
                    curChByteRaw %= totalCh;
                }

                Array.Clear(counters, 0, counters.Length);

                for (int ch = 0; ch < totalCh; ch++)
                {
                    for (int b = 0; b < fftLength; b += sizeof(short))
                    {
                        samplesShort[ch, counters[ch]] = (short)(smplInt[ch, b] + (smplInt[ch, b + 1] << 8));
                        counters[ch]++;
                    }
                }

                Array.Clear(counters, 0, counters.Length);

                for (int ch = 0; ch < totalCh; ch++)
                {
                    for (int b = 0; b < fftLength; b++)
                    {
                        summedSamples[b] += samplesShort[ch, counters[ch]];
                        counters[ch]++;
                    }
                }

                Array.Clear(counters, 0, counters.Length);

                for (int i = 0; i < summedSamples.Length; i++)
                {
                    var windowed_sample = FastFourierTransform.HammingWindow(summedSamples[i], fftLength);
                    complexSamples[i] = new Complex(windowed_sample, 0);
                }

                Array.Clear(summedSamples, 0, summedSamples.Length);

                FastFourierTransform.FFT(true, m, complexSamples);
                FFTDataReady.Invoke(complexSamples);

            }
        }

        /// <summary>
        /// Start playing the soundstream 
        /// </summary>
        public void PlayPause()
        {
            Console.WriteLine(currentState.ToString());

            switch (currentState)
            {
                case SoundStreamState.Idle:
                    currentState = SoundStreamState.PreparePlay;
                    break;
                case SoundStreamState.PreparePlay:
                case SoundStreamState.Playing:
                    currentState = SoundStreamState.Paused;
                    break;
                case SoundStreamState.Paused:
                    currentState = SoundStreamState.Playing;
                    break;
            }

        }

        volatile SoundStreamState currentState;

        private async Task MainLoop()
        {
            do
            {
                switch (currentState)
                {
                    case SoundStreamState.PreparePlay:
                        Source.Play();
                        currentState = SoundStreamState.Playing;
                        Task.Factory.StartNew(SpectrumLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
                        break;

                    case SoundStreamState.Playing:

                        if (Source.BuffersQueued < 3)
                        {
                            _decoder.GetSamples(SampleQuantum, ref _data);

                            if (_data is null)
                                _data = _silence;

                            _chain.QueueData(Source, _data, Format);

                            SamplesCopyBuf.Write(_data, 0, _data.Length);
                        }

                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));

                        if (!Source.IsPlaying() || _decoder.IsFinished)
                            currentState = SoundStreamState.Stopping;

                        break;

                    case SoundStreamState.Paused:
                        if (Source.BuffersQueued < 3)
                        {
                            _data = _silence;
                            _chain.QueueData(Source, _data, Format);
                        }
                        break;
                }

                await Task.Delay(SampleWait);

            } while (currentState != SoundStreamState.Stopping);

            Source.Stop();

            currentState = SoundStreamState.Stopped;
        }

        /// <summary>
        /// Stop the soundstream
        /// </summary>
        public void Stop()
        {
            currentState = SoundStreamState.Stopping;
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            Source.Dispose();
        }
    }
}
