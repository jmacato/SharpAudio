using System.Runtime.InteropServices;
using System;
using FFmpeg.AutoGen;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace SharpAudio.Codec.FFMPEG
{
    public class FFmpegDecoder : Decoder
    {

        static FFmpegDecoder()
        {
            var curPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

            string runtimeId = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (RuntimeInformation.OSArchitecture == Architecture.X64)
                {
                    runtimeId = "win7-x64";
                }
                else if (RuntimeInformation.OSArchitecture == Architecture.X86)
                {
                    runtimeId = "win7-x86";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (RuntimeInformation.OSArchitecture == Architecture.X64)
                {
                    runtimeId = "linux-x64";
                }
                else if (RuntimeInformation.OSArchitecture == Architecture.X86)
                {
                    runtimeId = "linux-x86";
                }
                else if (RuntimeInformation.OSArchitecture == Architecture.Arm)
                {
                    runtimeId = "linux-arm";
                }
                else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                {
                    runtimeId = "linux-arm64";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (RuntimeInformation.OSArchitecture == Architecture.X64)
                {
                    runtimeId = "osx-x64";
                }
                else if (RuntimeInformation.OSArchitecture == Architecture.X86)
                {
                    runtimeId = "osx-x86";
                }
            }

            ffmpeg.RootPath = Path.Combine(curPath, $"runtimes/{runtimeId}/native/");

        }

        private const int fsStreamSize = 8192;
        private byte[] ffmpegFSBuf = new byte[fsStreamSize];
        private Stream targetStream;
        private int sampleMult;
        private avio_alloc_context_read_packet avioRead;
        private avio_alloc_context_seek avioSeek;
        private int stream_index;
        private bool _isFinished;
        private FFmpegPointers ff = new FFmpegPointers();
        private byte[] tempSampleBuf;
        private CircularBuffer _slidestream;

        private TimeSpan seekTimeTarget;
        private volatile bool doSeek = false;
        private TimeSpan curPos;
        private bool doSeek2;
        private bool _isDecoderFinished;
        private readonly AVSampleFormat _DESIRED_SAMPLE_FORMAT = AVSampleFormat.AV_SAMPLE_FMT_S16;
        private readonly int _DESIRED_SAMPLE_RATE = 44_100;
        private readonly int _DESIRED_CHANNEL_COUNT = 2;

        private unsafe int Read(void* opaque, byte* targetBuffer, int targetBufferLength)
        {
            try
            {
                var readCount = targetStream.Read(ffmpegFSBuf, 0, ffmpegFSBuf.Length);
                if (readCount > 0)
                    Marshal.Copy(ffmpegFSBuf, 0, (IntPtr)targetBuffer, readCount);

                return readCount;
            }
            catch (Exception)
            {
                return ffmpeg.AVERROR_EOF;
            }
        }

        private unsafe long Seek(void* opaque, long offset, int whence)
        {
            SeekOrigin origin;

            switch (whence)
            {
                case ffmpeg.AVSEEK_SIZE:
                    return targetStream.Length;
                case 0:
                case 1:
                case 2:
                    origin = (SeekOrigin)whence;
                    break;
                default:
                    return ffmpeg.AVERROR_EOF;

            }

            targetStream.Seek(offset, origin);
            return targetStream.Position;
        }

        public FFmpegDecoder(Stream src)
        {
            targetStream = src;
            sampleMult = _DESIRED_SAMPLE_RATE * _DESIRED_CHANNEL_COUNT * sizeof(ushort);

            Ffmpeg_Initialize();
        }


        private unsafe void Ffmpeg_Initialize()
        {
            var inputBuffer = (byte*)ffmpeg.av_malloc((ulong)fsStreamSize);

            avioRead = Read;
            avioSeek = Seek;

            ff.ioContext = ffmpeg.avio_alloc_context(inputBuffer, fsStreamSize, 0, null, avioRead, null, avioSeek);

            if ((int)ff.ioContext == 0)
            {
                throw new FormatException("FFMPEG: Unable to allocate IO stream context.");
            }

            ff.format_context = ffmpeg.avformat_alloc_context();
            ff.format_context->pb = ff.ioContext;
            ff.format_context->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO | ffmpeg.AVFMT_FLAG_GENPTS | ffmpeg.AVFMT_FLAG_DISCARD_CORRUPT;

            fixed (AVFormatContext** fmt2 = &ff.format_context)
                if (ffmpeg.avformat_open_input(fmt2, "", null, null) != 0)
                {
                    throw new FormatException("FFMPEG: Could not open media stream.");
                }

            if (ffmpeg.avformat_find_stream_info(ff.format_context, null) < 0)
            {
                throw new FormatException("FFMPEG: Could not retrieve stream info from IO stream");
            }

            // Find the index of the first audio stream
            this.stream_index = -1;
            for (int i = 0; i < ff.format_context->nb_streams; i++)
            {
                if (ff.format_context->streams[i]->codec->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    stream_index = i;
                    break;
                }
            }

            if (stream_index == -1)
            {
                throw new FormatException("FFMPEG: Could not retrieve audio stream from IO stream.");
            }

            ff.av_stream = ff.format_context->streams[stream_index];
            ff.av_codec = ff.av_stream->codec;

            if (ffmpeg.avcodec_open2(ff.av_codec, ffmpeg.avcodec_find_decoder(ff.av_codec->codec_id), null) < 0)
            {
                throw new FormatException("FFMPEG: Failed to open decoder for stream #{stream_index} in IO stream.");
            }

            // Fixes SWR @ 0x2192200] Input channel count and layout are unset error.
            if (ff.av_codec->channel_layout == 0)
            {
                ff.av_codec->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(ff.av_codec->channels);
            }

            // ff.av_codec->request_channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(ff.av_codec->channels);
            // ff.av_codec->request_sample_fmt = _DESIRED_SAMPLE_FORMAT;

            SetAudioFormat();

            ff.swr_context = ffmpeg.swr_alloc_set_opts(null,
                                                      ffmpeg.av_get_default_channel_layout(_DESIRED_CHANNEL_COUNT),
                                                      _DESIRED_SAMPLE_FORMAT,
                                                      _DESIRED_SAMPLE_RATE,
                                                      (long)ff.av_codec->channel_layout,
                                                      ff.av_codec->sample_fmt,
                                                      ff.av_codec->sample_rate,
                                                      0,
                                                      null);

            ffmpeg.swr_init(ff.swr_context);

            if (ffmpeg.swr_is_initialized(ff.swr_context) == 0)
            {
                throw new FormatException($"FFMPEG: Resampler has not been properly initialized");
            }

            ff.av_packet = ffmpeg.av_packet_alloc();
            ff.av_src_frame = ffmpeg.av_frame_alloc();

            tempSampleBuf = new byte[(int)(_audioFormat.SampleRate * _audioFormat.Channels * 5)];
            _slidestream = new CircularBuffer(tempSampleBuf.Length);

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        private unsafe void SetAudioFormat()
        {
            _audioFormat.SampleRate = _DESIRED_SAMPLE_RATE;
            _audioFormat.Channels = _DESIRED_CHANNEL_COUNT;
            _audioFormat.BitsPerSample = 16;
            _numSamples = (int)(ff.format_context->duration / (float)ffmpeg.AV_TIME_BASE * _DESIRED_SAMPLE_RATE * _DESIRED_CHANNEL_COUNT);
        }

        public override bool IsFinished => _isFinished;

        public override TimeSpan Position => curPos;

        public override TimeSpan Duration => base.Duration;

        public async Task MainLoop()
        {
            Console.WriteLine("Preloaded");
            int frameFinished = 0;
            int count = 0;

            do
            {
                await Task.Delay(1);

                if (_isDecoderFinished) return;

                if (_slidestream.Length > sampleMult * 2)
                {
                    continue;
                }

                unsafe
                {
                    if (doSeek)
                    {
                        long seek = (long)(seekTimeTarget.TotalSeconds / ffmpeg.av_q2d(ff.av_stream->time_base));
                        ffmpeg.av_seek_frame(ff.format_context, stream_index, seek, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        ffmpeg.avcodec_flush_buffers(ff.av_stream->codec);
                        ff.av_packet = ffmpeg.av_packet_alloc();
                        doSeek = false;
                        seekTimeTarget = TimeSpan.Zero;
                        _slidestream.Clear();
                        doSeek2 = true;
                    }

                    if (ffmpeg.av_read_frame(ff.format_context, ff.av_packet) >= 0)
                    {
                        if (ff.av_packet->stream_index == stream_index)
                        {

#pragma warning disable 
                            int res = ffmpeg.avcodec_decode_audio4(ff.av_stream->codec, ff.av_src_frame, &frameFinished, ff.av_packet);
#pragma warning restore

                            if (res == 0)
                                continue;

                            if (ff.av_src_frame->pts == ffmpeg.AV_NOPTS_VALUE)
                            {
                                continue;
                                //curPos += TimeSpan.FromSeconds(ff.av_src_frame->nb_samples * ff.av_stream->time_base.num / (double)ff.av_stream->time_base.den);
                            }
                            else if (doSeek2)
                            {
                                double pts = ff.av_src_frame->pts;
                                pts *= ff.av_stream->time_base.num / (double)ff.av_stream->time_base.den;
                                curPos = TimeSpan.FromSeconds(pts);
                                doSeek2 = false;
                            }

                            if (frameFinished > 0)
                            {
                                ProcessAudioFrame(ref tempSampleBuf, ref count);
                            }
                        }
                    }
                    else
                    {
                        // Hack just to make sure it always return the full length.
                        // if (curPos != Duration)
                        //     curPos = Duration;

                        _isDecoderFinished = true;
                    }
                }

                _slidestream.Write(tempSampleBuf, 0, count);


            } while (!_isDecoderFinished);

        }

        public override long GetSamples(int samples, ref byte[] data)
        {
            if (_slidestream.Length == 0 && _isDecoderFinished)
            {
                _isFinished = true;
                return -2;
            }

            if (_slidestream.Length >= samples)
            {
                data = new byte[samples];
                var res = _slidestream.Read(data, 0, samples);
                // Console.WriteLine(_slidestream.Length / (double)(sampleMult));
                var x = res / (double)(sampleMult);
                // Console.WriteLine(x);
                curPos += TimeSpan.FromSeconds(x);

                return res;
            }

            if (_slidestream.Length < samples)
            {
                data = new byte[samples];
                var res = _slidestream.Read(data, 0, samples);
                data = data[0..res];
                return res;
            }

            return -2;
        }

        private unsafe void ProcessAudioFrame(ref byte[] data, ref int count)
        {
            ff.av_dst_frame = ffmpeg.av_frame_alloc();
            ff.av_dst_frame->sample_rate = _DESIRED_SAMPLE_RATE;
            ff.av_dst_frame->format = (int)_DESIRED_SAMPLE_FORMAT;
            ff.av_dst_frame->channels = _DESIRED_CHANNEL_COUNT;
            ff.av_dst_frame->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(ff.av_dst_frame->channels);

            ffmpeg.swr_convert_frame(ff.swr_context, ff.av_dst_frame, ff.av_src_frame);

            int bufferSize = ffmpeg.av_samples_get_buffer_size(null,
                                ff.av_dst_frame->channels,
                                ff.av_dst_frame->nb_samples,
                                (AVSampleFormat)ff.av_dst_frame->format,
                                1);

            if (bufferSize <= 0)
            {
                throw new Exception($"ffmpeg returned an invalid buffer size {bufferSize}");
            }

            count = bufferSize;

            fixed (byte* h = &data[0])
                Buffer.MemoryCopy(ff.av_dst_frame->data[0], h, bufferSize, bufferSize);

            fixed (AVFrame** x = &ff.av_dst_frame)
                ffmpeg.av_frame_free(x);
        }

        public override void TrySeek(TimeSpan time)
        {
            if (!doSeek & targetStream.CanSeek)
            {
                doSeek = true;
                seekTimeTarget = time;
            }
        }

        public override void Dispose()
        {
            targetStream?.Dispose();
        }

        public override void Preload()
        {

        }
    }
}
