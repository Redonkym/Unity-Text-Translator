using System;

namespace UnityTextTranslator
{
    internal static class Crc32Utility
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            uint crc = 0xFFFFFFFFu;
            for (var i = 0; i < data.Length; i++)
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (var j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
                table[i] = c;
            }
            return table;
        }
    }
}
