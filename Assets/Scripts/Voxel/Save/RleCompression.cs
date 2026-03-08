/*
using System;

namespace TerraVoxel.Voxel.Save
{
    /// <summary>
    /// Run-length encoding for byte arrays. Each run is (value: byte, count: byte) with count 1..255.
    /// Runs longer than 255 are split into multiple pairs. Used optionally in ChunkSaveBinary for snapshot body.
    /// </summary>
    public static class RleCompression
    {
        const int MaxRun = 255;

        /// <summary>Compresses input with RLE. Returns empty array for empty input. Throws ArgumentNullException if input is null.</summary>
        public static byte[] Compress(byte[] input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Length == 0) return Array.Empty<byte>();

            var list = new System.Collections.Generic.List<byte>(input.Length / 2 + 32);
            int i = 0;
            while (i < input.Length)
            {
                byte value = input[i];
                int count = 0;
                while (i < input.Length && input[i] == value && count < MaxRun)
                {
                    count++;
                    i++;
                }
                list.Add(value);
                list.Add((byte)count);
            }

            return list.ToArray();
        }

        /// <summary>Decompresses RLE stream to exactly uncompressedLength bytes. Throws on invalid or truncated data.</summary>
        public static byte[] Decompress(byte[] compressed, int uncompressedLength)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (uncompressedLength < 0) throw new ArgumentOutOfRangeException(nameof(uncompressedLength));

            var output = new byte[uncompressedLength];
            int op = 0;
            int ip = 0;
            while (op < uncompressedLength && ip + 2 <= compressed.Length)
            {
                byte value = compressed[ip];
                int count = compressed[ip + 1];
                ip += 2;
                if (count == 0) throw new InvalidOperationException("[RleCompression] Invalid run count 0.");
                if (op + count > uncompressedLength) throw new InvalidOperationException("[RleCompression] Decompress overrun.");
                for (int k = 0; k < count; k++)
                    output[op++] = value;
            }

            if (op != uncompressedLength)
                throw new InvalidOperationException($"[RleCompression] Decompress length mismatch: got {op}, expected {uncompressedLength}.");
            if (ip < compressed.Length)
                throw new InvalidOperationException("[RleCompression] Trailing data after decompress.");
            return output;
        }
    }
}
*/