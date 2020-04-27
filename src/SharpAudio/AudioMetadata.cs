using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAudio
{
    public struct AudioMetadata
    {
        public string Title;
        public List<string> Artists;
        public string Album;
        public Dictionary<string, string> ExtraData;
        public List<string> Genre;
        public string Year;
    }
}
