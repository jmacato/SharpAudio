using System.Buffers;
using System.Threading;
using System.Linq;
using System.Runtime.InteropServices;
using System;
using FFmpeg.AutoGen;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SharpAudio.Codec.FFMPEG
{
    public class FFmpegDecoder : Decoder
    {
        private const int fsStreamSize = 8192;
        private byte[] ffmpegFSBuf = new byte[fsStreamSize];
        private Stream targetStream;
        private avio_alloc_context_read_packet avioRead;
        private avio_alloc_context_seek avioSeek;
        private int stream_index;
        private bool _isFinished;
        private FFmpegPointers ff = new FFmpegPointers();
        private byte[] tempSampleBuf;
        private CircularBuffer _slidestream;

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
            ff.format_context->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

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

            ff.av_codec->request_channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(ff.av_codec->channels);
            ff.av_codec->request_sample_fmt = _DESIRED_SAMPLE_FORMAT;

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

            this.tempSampleBuf = new byte[(int)(_audioFormat.SampleRate * _audioFormat.Channels * 2)];

            this._slidestream = new CircularBuffer(tempSampleBuf.Length);

            AVDictionaryEntry* tag = null;
            this._audioMetaData = new AudioMetadata();
            _audioMetaData.ExtraData = new Dictionary<string, string>();


            do
            {
                tag = ffmpeg.av_dict_get(ff.format_context->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX);

                if (tag == null)
                    break;

                var key = Marshal.PtrToStringAuto((IntPtr)tag->key);
                var val = Marshal.PtrToStringAuto((IntPtr)tag->value);

                switch (key.ToLowerInvariant().Trim())
                {
                    case "title":
                        _audioMetaData.Title = val;
                        break;
                    case "artist":
                    case "artists":
                    case "author":
                    case "composer":
                        if (_audioMetaData.Artists is null)
                            _audioMetaData.Artists = new List<string>();

                        _audioMetaData.Artists.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                        break;
                    case "album":
                        _audioMetaData.Album = val;
                        break;
                    case "genre":
                        if (_audioMetaData.Genre is null)
                            _audioMetaData.Genre = new List<string>();

                        _audioMetaData.Genre.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                        break;
                    case "year":
                        _audioMetaData.Year = val;
                        break;
                    default:
                        _audioMetaData.ExtraData.Add(key, val);
                        break;
                }

            } while (true);

            if (_audioMetaData.Artists != null)
                _audioMetaData.Artists = _audioMetaData.Artists.GroupBy(x => x).Select(y => y.First()).ToList();

        }

        private unsafe void SetAudioFormat()
        {
            _audioFormat.SampleRate = _DESIRED_SAMPLE_RATE;
            _audioFormat.Channels = _DESIRED_CHANNEL_COUNT;
            _audioFormat.BitsPerSample = 16;
            _numSamples = (int)((ff.format_context->duration / (float)ffmpeg.AV_TIME_BASE) * _DESIRED_SAMPLE_RATE * _DESIRED_CHANNEL_COUNT);
        }

        public override bool IsFinished => _isFinished;

        public unsafe int DecodeNext(AVCodecContext* avctx, AVFrame* frame, ref int got_frame_ptr, AVPacket* avpkt)
        {
            int ret = 0;
            got_frame_ptr = 0;
            if ((ret = ffmpeg.avcodec_receive_frame(avctx, frame)) == 0)
            {
                //0 on success, otherwise negative error code
                got_frame_ptr = 1;
            }
            else if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                //AVERROR(EAGAIN): input is not accepted in the current state - user must read output with avcodec_receive_packet()
                //(once all output is read, the packet should be resent, and the call will not fail with EAGAIN)
                ret = Decode(avctx, frame, ref got_frame_ptr, avpkt);
            }
            else if (ret == ffmpeg.AVERROR_EOF)
            {
                throw new FormatException("FFMPEG: Unexpected end of stream.");
            }
            else if (ret == ffmpeg.AVERROR(ffmpeg.EINVAL))
            {
                throw new FormatException("FFMPEG: Invalid data.");
            }
            else if (ret == ffmpeg.AVERROR(ffmpeg.ENOMEM))
            {
                throw new FormatException("FFMPEG: Out of memory.");
            }
            else
            {
                throw new FormatException($"FFMPEG: Unknown return code {ret}.");
            }
            return ret;
        }
        public unsafe int Decode(AVCodecContext* avctx, AVFrame* frame, ref int got_frame_ptr, AVPacket* avpkt)
        {
            int ret = 0;
            got_frame_ptr = 0;
            if ((ret = ffmpeg.avcodec_send_packet(avctx, avpkt)) == 0)
            {
                //0 on success, otherwise negative error code
                return DecodeNext(avctx, frame, ref got_frame_ptr, avpkt);
            }
            else if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                throw new FormatException("input is not accepted in the current state - user must read output with avcodec_receive_frame()(once all output is read, the packet should be resent, and the call will not fail with EAGAIN");
            }
            else if (ret == ffmpeg.AVERROR_EOF)
            {
                throw new FormatException("AVERROR_EOF: the decoder has been flushed, and no new packets can be sent to it (also returned if more than 1 flush packet is sent");
            }
            else if (ret == ffmpeg.AVERROR(ffmpeg.EINVAL))
            {
                throw new FormatException("codec not opened, it is an encoder, or requires flush");
            }
            else if (ret == ffmpeg.AVERROR(ffmpeg.ENOMEM))
            {
                throw new FormatException("Failed to add packet to internal queue, or similar other errors: legitimate decoding errors");
            }
            else
            {
                throw new FormatException($"FFMPEG: Unknown return code {ret}.");
            }
        }

        public override long GetSamples(int samples, ref byte[] data)
        {

            int frameFinished = 0;
            int count = 0;

            if (_slidestream.Length > samples)
            {
                data = new byte[samples];
                _slidestream.Read(data, 0, samples);
                return samples;
            }

            do
            {
                unsafe
                {

                    if (doSeek)
                    {
                        ffmpeg.av_seek_frame(ff.format_context, stream_index, (long)(targetSeek.TotalSeconds * ffmpeg.AV_TIME_BASE), ffmpeg.AVSEEK_FLAG_ANY);

                        doSeek = false;
                        targetSeek = TimeSpan.Zero;
                    }

                    if (ffmpeg.av_read_frame(ff.format_context, ff.av_packet) >= 0)
                    {
                        if (ff.av_packet->stream_index == stream_index)
                        {
                            int len = Decode(ff.av_stream->codec, ff.av_src_frame, ref frameFinished, ff.av_packet);
                            if (frameFinished > 0)
                            {
                                ProcessAudioFrame(ref tempSampleBuf, ref count);
                            }
                        }
                    }
                    else
                    {
                        _isFinished = true;
                        return 0;
                    }
                }

                _slidestream.Write(tempSampleBuf, 0, count);

                if (_slidestream.Length > samples)
                {
                    break;
                }

            } while (!_isFinished);


            data = new byte[samples];
            _slidestream.Read(data, 0, samples);
            return samples;
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

        TimeSpan targetSeek;
        volatile bool doSeek = false;

        public override void TrySeek(TimeSpan time)
        {
            if (!doSeek & targetStream.CanSeek)
            {
                doSeek = true;
                targetSeek = time;
            }
        }
    }
}
