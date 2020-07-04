using SharpAudio.Codec;
using System;
using System.IO;
using System.Threading;
using CommandLine;
using System.Collections.Generic;
using SharpAudio.Codec.FFMPEG;
using System.Linq;

namespace SharpAudio.Sample
{
    class Program
    {
        public class Options
        {
            [Option('i', "input", Required = true, HelpText = "Specify the file(s) that should be played")]
            public IEnumerable<string> InputFiles { get; set; }

            [Option('v', "volume", Required = false, HelpText = "Set the output volume (0-100).", Default = 100)]
            public int Volume { get; set; }
        }

        static void Main(string[] args)
        {
            CommandLine.Parser.Default.ParseArguments<Options>(args)
              .WithParsed<Options>(opts => RunOptionsAndReturnExitCode(opts));
        }

        private static void RunOptionsAndReturnExitCode(Options opts)
        {
            var engine = AudioEngine.CreateDefault();

            if (engine == null)
            {
                Console.WriteLine("Failed to create an audio backend!");
            }

            var sink= new SoundSink(engine, null);

            foreach (var file in opts.InputFiles)
            {
                var soundStream = new SoundStream(File.OpenRead(file), sink);

                soundStream.Volume = opts.Volume / 100.0f;

                soundStream.PlayPause();

                while (soundStream.IsPlaying)
                {
 
                    Console.Write($"Playing  {soundStream.Position}/{(soundStream.Duration.TotalSeconds < 0 ? "\u221E" : soundStream.Duration.ToString())}\r");

                    Thread.Sleep(10);
                }

                Console.Write("\n");
            }
        }
    }
}
