using System.Linq;
using System.Runtime.InteropServices;
using System;
using FFmpeg.AutoGen;
using System.IO;
using System.Threading;

namespace SharpAudio.Codec.Mp3
{
    public unsafe class FfmpegDecoder : Decoder
    {

        private const int fsStreamSize = 8192;
        private byte[] ffmpegFSBuf = new byte[fsStreamSize];
        private Stream targetStream;
        private avio_alloc_context_read_packet avioRead;
        private avio_alloc_context_seek avioSeek;
        private int stream_index;
        private bool _isFinished;
        private FfmpegPointers ff = new FfmpegPointers();
        private int Read(void* opaque, byte* targetBuffer, int targetBufferLength)
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

        private long Seek(void* opaque, long offset, int whence)
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

            codec->request_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            codec->request_channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(2);

            SetAudioFormat(codec, ff.format_context);

            ff.swr_context = ffmpeg.swr_alloc();
            ffmpeg.av_opt_set_int(ff.swr_context, "in_channel_layout", (int)codec->channel_layout, 0);
            ffmpeg.av_opt_set_int(ff.swr_context, "in_channel_count", codec->channels, 0);
            ffmpeg.av_opt_set_int(ff.swr_context, "in_sample_rate", codec->sample_rate, 0);
            ffmpeg.av_opt_set_int(ff.swr_context, "out_channel_layout", ffmpeg.av_get_default_channel_layout(2), 0);
            ffmpeg.av_opt_set_int(ff.swr_context, "out_channel_count", 2, 0);
            ffmpeg.av_opt_set_int(ff.swr_context, "out_sample_rate", 48000, 0);
            ffmpeg.av_opt_set_sample_fmt(ff.swr_context, "in_sample_fmt", codec->sample_fmt, 0);
            ffmpeg.av_opt_set_sample_fmt(ff.swr_context, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);

            ffmpeg.swr_init(ff.swr_context);

            if (ffmpeg.swr_is_initialized(ff.swr_context) == 0)
            {
                throw new FormatException($"FFMPEG: Resampler has not been properly initialized");
            }

            ff.av_packet = ffmpeg.av_packet_alloc();
            ff.av_frame = ffmpeg.av_frame_alloc();

        }

        private void SetAudioFormat(AVCodecContext* codec, AVFormatContext* fmt)
        {
            _audioFormat.SampleRate = 48000;
            _audioFormat.Channels = 2;
            _audioFormat.BitsPerSample = 16;
            _numSamples = (int)((fmt->duration / (float)ffmpeg.AV_TIME_BASE) * codec->sample_rate * codec->channels);
        }

        public override bool IsFinished => _isFinished;

        public int DecodeNext(AVCodecContext* avctx, AVFrame* frame, ref int got_frame_ptr, AVPacket* avpkt)
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
        public int Decode(AVCodecContext* avctx, AVFrame* frame, ref int got_frame_ptr, AVPacket* avpkt)
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

        public int DecodeFlush(AVCodecContext* avctx, AVPacket* avpkt)
        {
            avpkt->data = null;
            avpkt->size = 0;
            return ffmpeg.avcodec_send_packet(avctx, avpkt);
        }


        public override long GetSamples(int samples, ref byte[] data)
        {
            var memStream = new MemoryStream();
            byte* convertedData = null;

            int samplePos = 0;
            int frameFinished = 0;

            do
            {
                if (ffmpeg.av_read_frame(ff.format_context, ff.av_packet) >= 0)
                {
                    if (ff.av_packet->stream_index == stream_index)
                    {
                        int len = Decode(ff.av_stream->codec, ff.av_frame, ref frameFinished, ff.av_packet);
                        byte[] xdata = null;
                        ProcessAudioFrame(ref xdata);
                        memStream.Write(xdata, 0, xdata.Count());
                        samplePos += xdata.Count();

                    }
                }
                else
                {
                    _isFinished = true;
                }
            } while (  !_isFinished);

            _numSamples = samplePos;
            data = memStream.GetBuffer();

            return 0;
        }

        private void ProcessAudioFrame(ref byte[] data)
        {
            try
            {
                byte[] buffer = new byte[ff.av_frame->sample_rate * 2];

                fixed (byte** buffers = new byte*[8])
                {
                    fixed (byte* bufferPtr = &buffer[0])
                    {
                        // Convert
                        buffers[0] = bufferPtr;
                        int samplesCount = ffmpeg.swr_convert(ff.swr_context, buffers, ff.av_stream->codec->sample_rate, (byte**)&ff.av_frame->data, ff.av_frame->nb_samples);
                        var bufferSize = ffmpeg.av_samples_get_buffer_size(null, ff.av_stream->codec->channels, samplesCount, ff.av_stream->codec->sample_fmt, 1);

                        // Send Frame
                        if (samplesCount > 0)
                        {
                            data = new byte[bufferSize];
                            Buffer.BlockCopy(buffer, 0, data, 0, bufferSize);
                        }
                    }
                }
            }
            catch (Exception)
            {

            }
        }
    }
}
