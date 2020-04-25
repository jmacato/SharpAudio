using System.Linq;
using System.Runtime.InteropServices;
using System;
using FFmpeg.AutoGen;
using System.IO;
using System.Threading;

namespace SharpAudio.Codec.Mp3
{
    public class FfmpegDecoder : Decoder
    {
        private const int fsStreamSize = 8192;
        private byte[] ffmpegFSBuf = new byte[fsStreamSize];
        private Stream targetStream;
        private avio_alloc_context_read_packet avioRead;
        private avio_alloc_context_seek avioSeek;
        private int stream_index;
        private bool _isFinished;
        private FfmpegPointers ff = new FfmpegPointers();
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
            try
            {
                return whence == ffmpeg.AVSEEK_SIZE ?
                    targetStream.Length : targetStream.Seek(offset, SeekOrigin.Begin);
            }
            catch
            {
                return ffmpeg.AVERROR_EOF;
            }
        }

        public FfmpegDecoder(Stream src)
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

            AVCodecContext* codec = ff.av_stream->codec;
            if (ffmpeg.avcodec_open2(codec, ffmpeg.avcodec_find_decoder(codec->codec_id), null) < 0)
            {
                throw new FormatException("FFMPEG: Failed to open decoder for stream #{stream_index} in IO stream.");
            }

            // Fixes SWR @ 0x2192200] Input channel count and layout are unset error.
            if (codec->channel_layout == 0)
            {
                codec->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(codec->channels);
            }

            codec->request_channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(codec->channels);
            codec->request_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

            SetAudioFormat();

            ff.swr_context = ffmpeg.swr_alloc_set_opts(null,
                                                      (long)codec->channel_layout,
                                                      AVSampleFormat.AV_SAMPLE_FMT_S16,
                                                      48000,
                                                      (long)codec->channel_layout,
                                                      codec->sample_fmt,
                                                      codec->sample_rate,
                                                      0,
                                                      null);

            ffmpeg.swr_init(ff.swr_context);

            if (ffmpeg.swr_is_initialized(ff.swr_context) == 0)
            {
                throw new FormatException($"FFMPEG: Resampler has not been properly initialized");
            }

            ff.av_packet = ffmpeg.av_packet_alloc();
            ff.av_src_frame = ffmpeg.av_frame_alloc();

            this.overspill_time = TimeSpan.FromSeconds(2);

            overspill = new byte[(int)(overspill_time.TotalSeconds * _audioFormat.SampleRate * _audioFormat.Channels)];


        }

        private unsafe void SetAudioFormat()
        {
            _audioFormat.SampleRate = 48000;
            _audioFormat.Channels = 2;
            _audioFormat.BitsPerSample = 16;
            _numSamples = (int)((ff.format_context->duration / (float)ffmpeg.AV_TIME_BASE) * 48000 * 2);
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

        private TimeSpan overspill_time;
        byte[] overspill;
        int overspill_count = 0;
        int overspill_index = 0;

        public override long GetSamples(int samples, ref byte[] data)
        {
            var memStream = new MemoryStream();
            int frameFinished = 0;

            if (overspill_count > 0)
            {
                if (overspill_count > samples)
                {
                    memStream.Write(overspill, overspill_index, samples);
                    overspill_count -= samples;
                    overspill_index += samples;
                    data = new byte[samples];
                    Buffer.BlockCopy(memStream.GetBuffer(), 0, data, 0, samples);
                    return samples;
                }
                else
                {
                    memStream.Write(overspill, 0, overspill_count);
                    overspill_count = 0;
                    overspill_index = 0;
                }
            }

            unsafe
            {
                do
                {
                    if (ffmpeg.av_read_frame(ff.format_context, ff.av_packet) >= 0)
                    {
                        if (ff.av_packet->stream_index == stream_index)
                        {
                            int len = Decode(ff.av_stream->codec, ff.av_src_frame, ref frameFinished, ff.av_packet);
                            if (frameFinished > 0)
                            {
                                ProcessAudioFrame(out var xdata);
                                memStream.Write(xdata, 0, xdata.Length);
                            }
                        }
                    }
                    else
                    {
                        _isFinished = true;
                        return 0;
                    }

                    if (memStream.Length > samples)
                    {
                        var curBuf = memStream.GetBuffer();
                        overspill_count = (int)memStream.Length - samples;
                        Buffer.BlockCopy(curBuf, samples, overspill, 0, overspill_count);
                        data = new byte[samples];
                        Buffer.BlockCopy(curBuf, 0, data, 0, samples);
                        return samples;
                    }

                } while (!_isFinished);
            }

            data = memStream.GetBuffer();

            return data.Length;
        }

        private unsafe void ProcessAudioFrame(out byte[] data)
        {
            data = null;

            try
            {
                ff.av_dst_frame = ffmpeg.av_frame_alloc();
                ff.av_dst_frame->sample_rate = 48000;
                ff.av_dst_frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
                ff.av_dst_frame->channels = 2;
                ff.av_dst_frame->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(ff.av_dst_frame->channels);

                ffmpeg.swr_convert_frame(ff.swr_context, ff.av_dst_frame, ff.av_src_frame);

                int bufferSize = ffmpeg.av_samples_get_buffer_size(null,
                                    ff.av_dst_frame->channels,
                                    ff.av_dst_frame->nb_samples,
                                    (AVSampleFormat)ff.av_dst_frame->format,
                                    1);

                if (bufferSize > 0)
                {
                    data = new byte[bufferSize];
                    fixed (byte* h = &data[0])
                        Buffer.MemoryCopy(ff.av_dst_frame->data[0], h, bufferSize, bufferSize);
                }

                fixed (AVFrame** x = &ff.av_dst_frame)
                    ffmpeg.av_frame_free(x);
            }
            catch (Exception)
            {

            }
        }
    }
}
