using System;
using System.Threading.Tasks;
using SharpAudio.SpectrumAnalysis;

namespace SharpAudio.Codec
{
    public sealed class SoundSink : IDisposable
    {
        private byte[] _silenceData;
        private AudioEngine _audioEngine;
        private SpectrumProcessor _spectrumProcessor;
        private BufferChain _chain;
        private AudioSource _audioSource;
        private CircularBuffer _circBuffer;
        private byte[] _tempBuf;
        private static readonly TimeSpan SampleQuantum = TimeSpan.FromSeconds(0.05);

        public SoundSink(AudioEngine audioEngine, SpectrumProcessor spectrumProcessor)
        {
            _format = new AudioFormat()
            {
                SampleRate = 44_100,
                Channels = 2,
                BitsPerSample = 16
            };

            _silenceData = new byte[(int)(_format.Channels * _format.SampleRate * sizeof(ushort) * SampleQuantum.TotalSeconds)];
            _audioEngine = audioEngine;
            _spectrumProcessor = spectrumProcessor;
            _chain = new BufferChain(_audioEngine);
            _circBuffer = new CircularBuffer(_silenceData.Length);
            _tempBuf = new byte[_silenceData.Length];

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        private async Task MainLoop()
        {
            _audioSource = _audioEngine.CreateSource();
            _chain.QueueData(Source, _silenceData, _format);
            _audioSource.Play();

            while (!_isDisposed)
            {
                await Task.Delay(1);

                if (Source.BuffersQueued >= 3)
                    continue;

                var cL = _circBuffer.Length;
                var tL = _tempBuf.Length;

                if (cL >= tL)
                {
                    _circBuffer.Read(_tempBuf, 0, _tempBuf.Length);
                    _chain.QueueData(Source, _tempBuf, _format);
                    _spectrumProcessor.Send(_tempBuf);
                }
                else if (cL < tL & cL > 0)
                {
                    var remainingSamples = new byte[cL];
                    _circBuffer.Read(remainingSamples, 0, remainingSamples.Length);

                    Buffer.BlockCopy(remainingSamples, 0, _tempBuf, 0, remainingSamples.Length);
                    _chain.QueueData(Source, _tempBuf, _format);
                    _spectrumProcessor.Send(_tempBuf);
                }
                else
                {
                    _chain.QueueData(Source, _silenceData, _format);
                }
            }
        }

        public AudioEngine Engine => _audioEngine;
        public AudioSource Source => _audioSource;

        public bool NeedsNewSample => _circBuffer.Length < _silenceData.Length;

        private volatile bool _isDisposed;
        private AudioFormat _format;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Source.Stop();
                Source.Dispose();
            }
        }

        public void Send(byte[] data)
        {
            _circBuffer.Write(data, 0, data.Length);
        }

        internal void ClearBuffers()
        {
            _circBuffer.Clear();
        }
    }
}
