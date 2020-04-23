using FFmpeg.AutoGen;

namespace SharpAudio.Codec.Mp3
{
    public unsafe struct FfmpegPointers
    {
        public AVFormatContext* format_context;
        public AVIOContext* ioContext;
        public AVStream* av_stream;
        public SwrContext* swr_context;
        public AVPacket* av_packet;
        public AVFrame* av_frame;
    }
}
