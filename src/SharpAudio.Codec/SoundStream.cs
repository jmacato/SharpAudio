﻿using System;
using System.ComponentModel;
using System.IO;
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
        private readonly Decoder _decoder;
        private byte[] _silence;

        private readonly SoundSink _soundSink;

        private volatile SoundStreamState _state = SoundStreamState.PreparePlay;
        private volatile bool hasDecodedSamples = false;

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

            _soundSink = sink;

            _decoder = new FFmpegDecoder(stream);

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        /// <summary>
        ///     The audio format of this stream
        /// </summary>
        public AudioFormat Format => _decoder.Format;

        /// <summary>
        ///     Whether or not the audio is finished
        /// </summary>
        public bool IsPlaying => _state == SoundStreamState.Playing;

        /// <summary>
        ///     Whether or not the audio is streamed
        /// </summary>
        public bool IsStreamed { get; }

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
            _decoder?.Dispose();
            _buffer?.Dispose();
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

        private async Task MainLoop()
        {
            do
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
                                _state = SoundStreamState.Stopping;
                                continue;
                            }
                            
                            _soundSink.Send(_data);
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
                        }

                        if (_decoder.IsFinished)
                        {
                            _state = SoundStreamState.Stopping;
                            continue;
                        }

                        break;

                    case SoundStreamState.Stopping:
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
                        _state = SoundStreamState.Stop;
                        break;

                    case SoundStreamState.Paused:
                        break;
                }

                await Task.Delay(SampleWait);
            } while (_state != SoundStreamState.Stop);

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