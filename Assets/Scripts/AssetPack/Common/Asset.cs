using System;

// ReSharper disable InconsistentNaming

namespace AssetPack.Common
{
    [Serializable]
    public struct Asset
    {
        public string name;
        public string type;
        public string filename;

        public Asset(string name, string type, string filename)
        {
            this.name = name;
            this.type = type;
            this.filename = filename;
        }
    }
}