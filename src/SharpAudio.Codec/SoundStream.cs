using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpAudio.Codec.FFMPEG;

namespace SharpAudio.Codec
{
    public sealed class SoundStream : IDisposable, INotifyPropertyChanged
    {
        private readonly TimeSpan SampleQuantum = TimeSpan.FromSeconds(0.05);
        private readonly TimeSpan SampleWait = TimeSpan.FromMilliseconds(1);
        private AudioBuffer _buffer;
        private byte[] _data;
        private Decoder _decoder;
        private readonly SoundSink _soundSink;
        private volatile SoundStreamState _state = SoundStreamState.PreparePlay;

        /// <summary>
        ///     Initializes a new instance of the <see cref="SoundStream" /> class.
        /// </summary>
        /// <param name="stream">The sound stream.</param>
        /// <param name="engine">The audio engine</param>
        public SoundStream(Stream stream, SoundSink sink)
        {
            if (stream == null)
                throw new ArgumentNullException("Stream cannot be null!");

            IsStreamed = !stream.CanSeek;

            _targetStream = stream;

            _soundSink = sink;

            _decoder = new FFmpegDecoder(_targetStream);

            var streamThread = new Thread(MainLoop);

            streamThread.Start();
        }

        /// <summary>
        ///     Whether or not the audio is finished
        /// </summary>
        public bool IsPlaying => _state == SoundStreamState.Playing;

        /// <summary>
        ///     Whether or not the audio is streamed
        /// </summary>
        public bool IsStreamed { get; }

        private readonly Stream _targetStream;

        /// <summary>
        ///     The volume of the source
        /// </summary>
        public float Volume
        {
            get => _soundSink?.Source.Volume ?? 0;
            set => _soundSink.Source.Volume = value;
        }

        /// <summary>
        ///     Duration when provided by the decoder. Otherwise 0
        /// </summary>
        public TimeSpan Duration => _decoder.Duration;

        /// <summary>
        ///     Current position inside the stream
        /// </summary>
        public TimeSpan Position => _decoder.Position;

        public SoundStreamState State => _state;

        public void Dispose()
        {
            _state = SoundStreamState.Stop;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void TrySeek(TimeSpan seek)
        {
            _soundSink.ClearBuffers();
            _decoder.TrySeek(seek);
        }

        /// <summary>
        ///     Start playing the soundstream
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

        private void MainLoop()
        {
            while (_state != SoundStreamState.Stop & _state != SoundStreamState.TrackFinished)
            {
                switch (_state)
                {
                    case SoundStreamState.PreparePlay:
                        _state = SoundStreamState.Paused;
                        break;

                    case SoundStreamState.Playing:
                        if (_soundSink.NeedsNewSample)
                        {
                            var res = _decoder.GetSamples(SampleQuantum, ref _data);

                            if (res == 0)
                            {
                                continue;
                            }

                            if (res == -1)
                            {
                                _state = SoundStreamState.TrackFinished;
                                continue;
                            }

                            _soundSink.Send(_data);
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
                        }

                        if (_decoder.IsFinished)
                        {
                            _state = SoundStreamState.TrackFinished;
                            continue;
                        }

                        break;

                    case SoundStreamState.Paused:
                        break;
                }

                Thread.Sleep(SampleWait);

            }

            _targetStream?.Dispose();
            _decoder?.Dispose();
            _buffer?.Dispose();

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }

        /// <summary>
        ///     Stop the soundstream
        /// </summary>
        public void Stop()
        {
            _state = SoundStreamState.Stop;
        }
    }
}