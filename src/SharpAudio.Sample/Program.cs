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

            foreach (var file in opts.InputFiles)
            {
                var soundStream = new SoundStream(File.OpenRead(file), engine);

                soundStream.Volume = opts.Volume / 100.0f;

                soundStream.Play();

                while (soundStream.IsPlaying)
                {
                    var xx = string.Join(", ", soundStream.Metadata.Artists ?? new List<string>());

                    Console.Write($"Playing [{soundStream.Metadata.Title ?? Path.GetFileNameWithoutExtension(file)}] by [{(xx.Length > 0 ? xx : "Unknown")}] {soundStream.Position}/{(soundStream.Duration.TotalSeconds < 0 ? "\u221E" : soundStream.Duration.ToString())}\r");

                    Thread.Sleep(10);
                }

                Console.Write("\n");
            }
        }
    }
}
