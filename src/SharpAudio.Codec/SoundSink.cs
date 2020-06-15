using System;
using System.Threading;
using System.Threading.Tasks;
using SharpAudio.SpectrumAnalysis;

namespace SharpAudio.Codec
{
    public sealed class SoundSink : IDisposable
    {
        private static readonly TimeSpan SampleQuantum = TimeSpan.FromSeconds(0.05);
        private readonly BufferChain _chain;
        private readonly CircularBuffer _circBuffer;
        private readonly AudioFormat _format;
        private readonly byte[] _silenceData;
        private readonly SpectrumProcessor _spectrumProcessor;
        private readonly byte[] _tempBuf;
        private volatile bool _isDisposed;

        public SoundSink(AudioEngine audioEngine, SpectrumProcessor spectrumProcessor)
        {
            _format = new AudioFormat
            {
                SampleRate = 44_100,
                Channels = 2,
                BitsPerSample = 16
            };

            _silenceData =
                new byte[(int)(_format.Channels * _format.SampleRate * sizeof(ushort) * SampleQuantum.TotalSeconds)];
            Engine = audioEngine;
            _spectrumProcessor = spectrumProcessor;
            _chain = new BufferChain(Engine);
            _circBuffer = new CircularBuffer(_silenceData.Length);
            _tempBuf = new byte[_silenceData.Length];

            var sinkThread = new Thread(MainLoop);
            sinkThread.Start();
        }

        public AudioEngine Engine { get; }

        public AudioSource Source { get; private set; }

        public bool NeedsNewSample => _circBuffer.Length < _silenceData.Length;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Source.Stop();
                Source.Dispose();
            }
        }

        private void InitializeSource()
        {
            Source?.Dispose();
            Source = Engine.CreateSource();
            _chain.QueueData(Source, _silenceData, _format);
            Source.Play();
        }


        private void MainLoop()
        {
            InitializeSource();

            while (!_isDisposed)
            {
                Thread.Sleep(1);

                if (!Source.IsPlaying())
                {
                    Thread.Sleep(TimeSpan.FromSeconds(0.5));
                    InitializeSource();
                    continue;
                }

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
                else if ((cL < tL) & (cL > 0))
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