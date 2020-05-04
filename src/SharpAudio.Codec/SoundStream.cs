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
        public bool IsPlaying => _state == SoundStreamState.Playing;

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
        volatile SoundStreamState _state;

        public SoundStreamState State => _state;

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

            SamplesCopyBuf = new CircularBuffer((int)(_decoder.Format.Channels * _decoder.Format.SampleRate * SampleQuantum.TotalSeconds * sizeof(short)));

            // Prime the buffer chain with empty data.
            _chain.QueueData(Source, _silence, Format);
            SamplesCopyBuf.Write(_silence, 0, _silence.Length);

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        public event EventHandler<double[]> FFTDataReady;

        private int fftLength = 4096;
        private int bins = 4096;
        private int binsPerPoint = 1;
        private double minDB = -25;

        private double GetYPosLog(Complex complex)
        {
            return complex.Magnitude;
            //if (intensityDB < minDB) intensityDB = minDB;
            //return intensityDB / minDB;
        }

        private double[] ProcessFFT(Complex[] fftResults, double[] windowMultipliers)
        {
            var processedFFT = new double[bins / binsPerPoint];

            for (int n = 0; n < fftResults.Length / binsPerPoint; n += binsPerPoint)
            {
                // averaging out bins
                double yPos = 0;
                for (int b = 0; b < binsPerPoint; b++)
                {
                    yPos += fftResults[n + b].Magnitude;
                }

                processedFFT[n / binsPerPoint] = (yPos / binsPerPoint) * 10;
            }

            return processedFFT;
        }

        private async Task SpectrumLoop()
        {
            // Assuming 16 bit PCM, Little-endian, Variable Channels.
            int totalCh = _decoder.Format.Channels;
            int specSamples = fftLength * totalCh * sizeof(short);
            int curChByteRaw = 0;

            var tempBuf = new byte[specSamples];
            var rawSamplesShort = new short[totalCh * fftLength];
            var samplesShort = new short[totalCh, fftLength];

            var summedSamples = new double[fftLength / totalCh];
            var summedSamplesDouble = new double[fftLength / totalCh];
            var counters = new int[totalCh];
            var complexSamples = new Complex[fftLength];
            var shortDivisor = (double)short.MaxValue;
            var binaryExp = (int)Math.Log(fftLength, 2.0);

            var cachedWindowVal = new double[summedSamples.Length];

            for (int i = 0; i < summedSamples.Length; i++)
            {
                cachedWindowVal[i] = FastFourierTransform.HammingWindow(i, summedSamples.Length);
            }

            while (_state == SoundStreamState.Playing)
            {
                await Task.Delay(SampleWait);

                if (SamplesCopyBuf.Length < specSamples) continue;
                if (FFTDataReady is null) continue;

                Array.Clear(tempBuf, 0, tempBuf.Length);
                Array.Clear(rawSamplesShort, 0, rawSamplesShort.Length);
                Array.Clear(summedSamplesDouble, 0, summedSamplesDouble.Length);
                Array.Clear(summedSamples, 0, summedSamples.Length);

                SamplesCopyBuf.Read(tempBuf, 0, specSamples);
                Buffer.BlockCopy(tempBuf, 0, rawSamplesShort, 0, rawSamplesShort.Length);

                // Channel de-interleaving
                for (int i = 0; i < rawSamplesShort.Length; i++)
                {
                    samplesShort[curChByteRaw, counters[curChByteRaw]] = rawSamplesShort[i];
                    counters[curChByteRaw]++;
                    curChByteRaw++;
                    curChByteRaw %= totalCh;
                }

                Array.Clear(counters, 0, counters.Length);

                // Mixing down
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int b = 0; b < summedSamples.Length; b++)
                    {
                        summedSamplesDouble[b] += samplesShort[ch, counters[ch]];
                        counters[ch]++;
                    }
                }

                double max = 0;

                // Hard clipping stage
                for (int b = 0; b < summedSamples.Length; b++)
                {
                    summedSamples[b] += Math.Clamp(summedSamplesDouble[b] / shortDivisor, -1, 1);

                    if (summedSamples[b] > max)
                    {
                        max = summedSamples[b];
                    }
                }

                Array.Clear(counters, 0, counters.Length);

                for (int i = 0; i < summedSamples.Length; i++)
                {
                    var windowed_sample = summedSamples[i] * cachedWindowVal[i];
                    complexSamples[i] = new Complex(windowed_sample, 0);
                }

                FastFourierTransform.FFT(true, binaryExp, complexSamples);
                FFTDataReady?.Invoke(this, ProcessFFT(complexSamples, cachedWindowVal));
            }
        }

        /// <summary>
        /// Start playing the soundstream 
        /// </summary>
        public void PlayPause()
        {
            switch (_state)
            {
                case SoundStreamState.Idle:
                    _state = SoundStreamState.PreparePlay;
                    break;
                case SoundStreamState.PreparePlay:
                case SoundStreamState.Playing:
                    _state = SoundStreamState.Paused;
                    break;
                case SoundStreamState.Paused:
                    _state = SoundStreamState.Playing;
                    break;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }


        private async Task MainLoop()
        {
            do
            {
                switch (_state)
                {
                    case SoundStreamState.PreparePlay:
                        Source.Play();
                        _state = SoundStreamState.Playing;
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
                        {
                            _state = SoundStreamState.Stopping;
                        }

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

            } while (_state != SoundStreamState.Stopping);

            Source.Stop();

            _state = SoundStreamState.Stopped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }

        /// <summary>
        /// Stop the soundstream
        /// </summary>
        public void Stop()
        {
            _state = SoundStreamState.Stopping;
        }

        public void Dispose()
        {
            FFTDataReady = null;
            _buffer?.Dispose();
            Source.Dispose();
        }
    }
}
