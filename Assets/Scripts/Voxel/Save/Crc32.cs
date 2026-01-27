using System;

namespace TerraVoxel.Voxel.Save
{
    public static class Crc32
    {
        static readonly uint[] Table = CreateTable();

        public static uint Compute(byte[] data)
        {
            if (data == null) return 0;
            return Compute(data, 0, data.Length);
        }

        public static uint Compute(byte[] data, int offset, int length)
        {
            if (data == null || length <= 0) return 0;
            if (offset < 0 || length < 0 || offset + length > data.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
            }
            return ~crc;
        }

        static uint[] CreateTable()
        {
            var table = new uint[256];
            const uint poly = 0xEDB88320u;
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((c & 1) != 0)
                        c = poly ^ (c >> 1);
                    else
                        c >>= 1;
                }
                table[i] = c;
            }
            return table;
        }
    }
}
