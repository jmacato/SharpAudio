using System.ComponentModel;
using System;
using System.Numerics;
using System.Threading.Tasks;
using SharpAudio.Codec;

namespace SharpAudio.SpectrumAnalysis
{
    public class SpectrumProcessor : IDisposable
    {
        public event EventHandler<double[,]> FFTDataReady;

        protected const double MinDbValue = -90;
        protected const double MaxDbValue = 0;
        protected const double DbScale = MaxDbValue - MinDbValue;
        private readonly int fftLength = 512;
        private readonly int binaryExp;
        private readonly int totalCh = 2;
        private readonly TimeSpan SampleWait = TimeSpan.FromMilliseconds(20);
        private bool _hasSpectrumData;
        private byte[] _latestSample;
        private object _latesSampleLock = new object();
        private volatile bool isDisposed = false;

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

        public SpectrumProcessor()
        {
            binaryExp = (int)Math.Log(fftLength, 2.0);
            _ = Task.Factory.StartNew(SpectrumLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        public void Send(byte[] data)
        {
            lock (_latesSampleLock)
            {
                _latestSample = data;
                _hasSpectrumData = true;
            }
        }

        private async Task SpectrumLoop()
        {
            // Assuming 16 bit PCM, Little-endian, 2 Channels.
            var specSamples = fftLength * totalCh * sizeof(short);
            var curChByteRaw = 0;
            var tempBuf = new byte[specSamples];
            var samplesDouble = new double[totalCh, fftLength];
            var channelCounters = new int[totalCh];
            var complexSamples = new Complex[totalCh, fftLength];
            var shortDivisor = (double)short.MaxValue;
            var cachedWindowVal = new double[fftLength];

            for (int i = 0; i < fftLength; i++)
            {
                cachedWindowVal[i] = FastFourierTransform.HammingWindow(i, fftLength);
            }

            do
            {
                await Task.Delay(SampleWait);

                if (FFTDataReady is null) continue;

                bool gotData = false;

                lock (_latesSampleLock)
                {
                    if (_hasSpectrumData)
                    {
                        _hasSpectrumData = false;

                        if (_latestSample.Length < tempBuf.Length)
                        {
                            Array.Clear(tempBuf, 0, tempBuf.Length);
                            Buffer.BlockCopy(_latestSample, 0, tempBuf, 0, _latestSample.Length);
                        }
                        else
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

                // Process FFT for each channel.
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

            } while (!isDisposed);
        }

        public void Dispose()
        {
            FFTDataReady = null;
            isDisposed = true;
        }
    }
}
