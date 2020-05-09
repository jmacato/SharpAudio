using System.ComponentModel;
using SharpAudio.Codec.FFMPEG;
using System;
using System.IO;
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
        private bool _hasSpectrumData;
        private byte[] _latestSample;
        private object _latesSampleLock = new object();

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// The audio format of this stream
        /// </summary>
        public AudioFormat Format => _decoder.Format;

        /// <summary>
        /// The metadata of the decoded data;
        /// </summary>
        // public AudioMetadata Metadata => _decoder.Metadata;

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

            // Prime the buffer chain with empty data.
            _chain.QueueData(Source, _silence, Format);

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        public event EventHandler<double[,]> FFTDataReady;

        private int fftLength = 512;

        double max = 0.0000000000000001;

        protected const double MinDbValue = -90;
        protected const double MaxDbValue = 0;
        protected const double DbScale = (MaxDbValue - MinDbValue);

        private double[,] FFT2Double(Complex[,] fftResults, int ch, int fftLength)
        {
            // Only return the N/2 bins since that's the nyquist limit.
            var n = fftLength / 2;
            var processedFFT = new double[ch, n];

            for (int c = 0; c < ch; c++)
                for (int i = 0; i < n; i++)
                {
                    var complex = fftResults[c, i];

                    var magnitude = complex.Magnitude;
                    if (magnitude == 0)
                    {
                        continue;
                    }

                    // decibel
                    var result = (((20 * Math.Log10(magnitude)) - MinDbValue) / DbScale) * 1;

                    // normalised decibel
                    //var result = (((10 * Math.Log10((complex.Real * complex.Real) + (complex.Imaginary * complex.Imaginary))) - MinDbValue) / DbScale) * 1;

                    // linear
                    //var result = (magnitude * 9) * 1;

                    // sqrt                
                    //var result = ((Math.Sqrt(magnitude)) * 2) * 1;

                    processedFFT[c, i] = Math.Max(0, result);
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

            var samplesDouble = new double[totalCh, fftLength];

            var channelCounters = new int[totalCh];
            var complexSamples = new Complex[totalCh, fftLength];

            var shortDivisor = (double)short.MaxValue;
            var binaryExp = (int)Math.Log(fftLength, 2.0);

            var cachedWindowVal = new double[fftLength];

            for (int i = 0; i < fftLength; i++)
            {
                cachedWindowVal[i] = FastFourierTransform.HammingWindow(i, fftLength);
            }

            do
            {
                await Task.Delay(SampleWait);

                if (_state == SoundStreamState.Paused || FFTDataReady is null) continue;

                bool gotData = false;

                lock (_latesSampleLock)
                {
                    if (_hasSpectrumData)
                    {
                        _hasSpectrumData = false;
                        tempBuf = _latestSample;
                        gotData = true;
                    }
                }

                if (!gotData)
                {
                    continue;
                }

                var rawSamplesShort = tempBuf.AsMemory().AsShorts().Slice(0, fftLength * totalCh);

                // Channel de-interleaving
                for (int i = 0; i < rawSamplesShort.Length; i++)
                {
                    samplesDouble[curChByteRaw, channelCounters[curChByteRaw]] = rawSamplesShort.Span[i] / shortDivisor;
                    channelCounters[curChByteRaw]++;
                    curChByteRaw++;
                    curChByteRaw %= totalCh;
                }

                Array.Clear(channelCounters, 0, channelCounters.Length);

                for (int curCh = 0; curCh < totalCh; curCh++)
                {
                    for (int i = 0; i < fftLength; i++)
                    {
                        var windowed_sample = samplesDouble[curCh, i] * cachedWindowVal[i];
                        complexSamples[curCh, i] = new Complex(windowed_sample, 0);
                    }

                    FastFourierTransform.ProcessFFT(true, binaryExp, complexSamples, curCh);
                }

                FFTDataReady?.Invoke(this, FFT2Double(complexSamples, totalCh, fftLength));

                Array.Clear(samplesDouble, 0, samplesDouble.Length);

            } while (_state != SoundStreamState.Stopped);
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

                            lock (_latesSampleLock)
                            {
                                _latestSample = _data;
                                _hasSpectrumData = true;
                            }

                            if (_data is null)
                                _data = _silence;

                            _chain.QueueData(Source, _data, Format);
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
            Stop();
            FFTDataReady = null;
            _decoder?.Dispose();
            _buffer?.Dispose();
            Source.Dispose();
        }
    }
}
