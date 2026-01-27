using System;

namespace TerraVoxel.Voxel.Save
{
    public static class Lz4Codec
    {
        const int MinMatch = 4;
        const int HashLog = 16;
        const int HashSize = 1 << HashLog;
        const int HashMask = HashSize - 1;

        public static int MaxCompressedLength(int inputLength)
        {
            if (inputLength < 0) throw new ArgumentOutOfRangeException(nameof(inputLength));
            return inputLength + (inputLength / 255) + 16;
        }

        public static byte[] Compress(byte[] input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            int inputLength = input.Length;
            if (inputLength == 0) return Array.Empty<byte>();

            var hash = new int[HashSize];
            for (int i = 0; i < hash.Length; i++) hash[i] = -1;

            byte[] output = new byte[MaxCompressedLength(inputLength)];
            int ip = 0;
            int op = 0;
            int anchor = 0;
            int matchLimit = inputLength - MinMatch;

            if (inputLength < MinMatch)
            {
                op = WriteLastLiterals(input, output, op, 0, inputLength);
                Array.Resize(ref output, op);
                return output;
            }

            while (ip <= matchLimit)
            {
                int h = Hash(input, ip);
                int refIndex = hash[h];
                hash[h] = ip;

                if (refIndex >= 0 && (ip - refIndex) <= 0xFFFF && Read32(input, refIndex) == Read32(input, ip))
                {
                    int matchLen = MinMatch;
                    int matchMax = inputLength;
                    while (ip + matchLen < matchMax && input[refIndex + matchLen] == input[ip + matchLen])
                        matchLen++;

                    int litLen = ip - anchor;
                    int tokenPos = op++;
                    int token = 0;

                    if (litLen >= 15)
                    {
                        token |= 15 << 4;
                        op = WriteLength(output, op, litLen - 15);
                    }
                    else
                    {
                        token |= litLen << 4;
                    }

                    Buffer.BlockCopy(input, anchor, output, op, litLen);
                    op += litLen;

                    int offset = ip - refIndex;
                    output[op++] = (byte)offset;
                    output[op++] = (byte)(offset >> 8);

                    int matchLenMinus = matchLen - MinMatch;
                    if (matchLenMinus >= 15)
                    {
                        token |= 15;
                        op = WriteLength(output, op, matchLenMinus - 15);
                    }
                    else
                    {
                        token |= matchLenMinus;
                    }

                    output[tokenPos] = (byte)token;
                    ip += matchLen;
                    anchor = ip;
                    continue;
                }

                ip++;
            }

            int remaining = inputLength - anchor;
            if (remaining > 0)
                op = WriteLastLiterals(input, output, op, anchor, remaining);
            Array.Resize(ref output, op);
            return output;
        }

        public static byte[] Decompress(byte[] input, int expectedSize)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
            if (expectedSize == 0) return Array.Empty<byte>();

            byte[] output = new byte[expectedSize];
            int ip = 0;
            int op = 0;

            while (ip < input.Length)
            {
                int token = input[ip++];

                int litLen = token >> 4;
                if (litLen == 15)
                    litLen += ReadLength(input, ref ip);

                if (ip + litLen > input.Length) throw new InvalidOperationException("LZ4 literal out of range.");
                if (op + litLen > output.Length) throw new InvalidOperationException("LZ4 literal overflow.");

                Buffer.BlockCopy(input, ip, output, op, litLen);
                ip += litLen;
                op += litLen;

                if (ip >= input.Length) break;

                int offset = input[ip] | (input[ip + 1] << 8);
                ip += 2;
                if (offset <= 0 || offset > op) throw new InvalidOperationException("LZ4 offset out of range.");

                int matchLen = token & 0x0F;
                if (matchLen == 15)
                    matchLen += ReadLength(input, ref ip);
                matchLen += MinMatch;

                if (op + matchLen > output.Length) throw new InvalidOperationException("LZ4 match overflow.");

                int refIndex = op - offset;
                for (int i = 0; i < matchLen; i++)
                    output[op++] = output[refIndex + i];
            }

            if (op != output.Length)
                throw new InvalidOperationException("LZ4 decompressed size mismatch.");

            return output;
        }

        static int WriteLastLiterals(byte[] input, byte[] output, int op, int anchor, int literalLength)
        {
            int tokenPos = op++;
            int token = 0;
            if (literalLength >= 15)
            {
                token |= 15 << 4;
                op = WriteLength(output, op, literalLength - 15);
            }
            else
            {
                token |= literalLength << 4;
            }

            Buffer.BlockCopy(input, anchor, output, op, literalLength);
            op += literalLength;
            output[tokenPos] = (byte)token;
            return op;
        }

        static int WriteLength(byte[] output, int op, int length)
        {
            while (length >= 255)
            {
                output[op++] = 255;
                length -= 255;
            }
            output[op++] = (byte)length;
            return op;
        }

        static int ReadLength(byte[] input, ref int ip)
        {
            int length = 0;
            byte s;
            do
            {
                if (ip >= input.Length) throw new InvalidOperationException("LZ4 length out of range.");
                s = input[ip++];
                length += s;
            } while (s == 255);
            return length;
        }

        static int Hash(byte[] input, int index)
        {
            uint value = Read32(input, index);
            return (int)((value * 2654435761u) >> (32 - HashLog)) & HashMask;
        }

        static uint Read32(byte[] input, int index)
        {
            return (uint)(input[index]
                | (input[index + 1] << 8)
                | (input[index + 2] << 16)
                | (input[index + 3] << 24));
        }
    }
}

