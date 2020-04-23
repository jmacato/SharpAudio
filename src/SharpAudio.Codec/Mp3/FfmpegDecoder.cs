using System.Runtime.InteropServices;
using System;
using FFmpeg.AutoGen;
using System.IO;

namespace SharpAudio.Codec.Mp3
{

    public unsafe class FfmpegDecoder : Decoder
    {
        private AVFormatContext global_formatContext;
        private AVIOContext global_ioContext;
        private AVStream global_av_stream;
        private AVPacket global_av_packet;

        private const int fsStreamSize = 8192;
        private byte[] ffmpegFSBuf = new byte[fsStreamSize];
        private Stream targetStream;
        private avio_alloc_context_read_packet avioRead;
        private avio_alloc_context_seek avioSeek;
        private int stream_index;

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

            fixed (AVFormatContext* a = &global_formatContext)
            fixed (AVIOContext* b = &global_ioContext)
            fixed (AVStream* c = &global_av_stream)
            fixed (AVPacket* d = &global_av_packet)
                Ffmpeg_Initialize(a, b, c, d);
        }

        private unsafe void Ffmpeg_Initialize(AVFormatContext* formatContext,
                                              AVIOContext* ioContext,
                                              AVStream* av_stream,
                                              AVPacket* av_packet)
        {
            var inputBuffer = (byte*)ffmpeg.av_malloc((ulong)fsStreamSize);

            avioRead = Read;
            avioSeek = Seek;

            ioContext = ffmpeg.avio_alloc_context(inputBuffer, fsStreamSize, 0, null, avioRead, null, avioSeek);

            if ((int)ioContext == 0)
            {
                throw new FormatException("FFMPEG: Unable to allocate IO stream context.");
            }

            formatContext = ffmpeg.avformat_alloc_context();
            formatContext->pb = ioContext;
            formatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            if (ffmpeg.avformat_open_input(&formatContext, "", null, null) != 0)
            {
                throw new FormatException("FFMPEG: Could not open media stream.");
            }

            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
            {
                throw new FormatException("FFMPEG: Could not retrieve stream info from IO stream");
            }

            // Find the index of the first audio stream
            this.stream_index = -1;
            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                if (formatContext->streams[i]->codec->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    stream_index = i;
                    break;
                }
            }
            if (stream_index == -1)
            {
                throw new FormatException("FFMPEG: Could not retrieve audio stream from IO stream.");
            }

            av_stream = formatContext->streams[stream_index];

            AVCodecContext* codec = av_stream->codec;
            if (ffmpeg.avcodec_open2(codec, ffmpeg.avcodec_find_decoder(codec->codec_id), null) < 0)
            {
                throw new FormatException("FFMPEG: Failed to open decoder for stream #{stream_index} in IO stream.");
            }

            SwrContext* swr = ffmpeg.swr_alloc();

            SetAudioFormat(codec);

            ffmpeg.av_opt_set_int(swr, "in_channel_count", codec->channels, 0);
            ffmpeg.av_opt_set_int(swr, "out_channel_count", 2, 0);
            ffmpeg.av_opt_set_int(swr, "in_channel_layout", (long)codec->channel_layout, 0);
            ffmpeg.av_opt_set_int(swr, "out_channel_layout", (long)codec->channel_layout, 0);
            ffmpeg.av_opt_set_int(swr, "in_sample_rate", codec->sample_rate, 0);
            ffmpeg.av_opt_set_int(swr, "out_sample_rate", codec->sample_rate, 0);
            ffmpeg.av_opt_set_sample_fmt(swr, "in_sample_fmt", codec->sample_fmt, 0);
            ffmpeg.av_opt_set_sample_fmt(swr, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);
            ffmpeg.swr_init(swr);

            if (ffmpeg.swr_is_initialized(swr) == 0)
            {
                throw new FormatException($"FFMPEG: Resampler has not been properly initialized");
            }

            // prepare to read data
            ffmpeg.av_init_packet(av_packet);
        }

        private void SetAudioFormat(AVCodecContext* codec)
        {
            _audioFormat.SampleRate = codec->sample_rate;
            _audioFormat.Channels = codec->channels;
            _audioFormat.BitsPerSample = codec->bits_per_coded_sample;
        }

        public override bool IsFinished => throw new NotImplementedException();

        public override long GetSamples(int samples, ref byte[] data)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            if ((int)frame == 0)
            {
                throw new FormatException($"FFMPEG: Resampler has not been properly initialized");
            }

            return 0;
        }
    }
}
