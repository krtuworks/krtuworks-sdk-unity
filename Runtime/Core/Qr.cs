#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Krtuworks
{
    /// <summary>
    /// A QR code (ISO/IEC 18004) for a console screen's sign-in address: byte mode, error correction level M,
    /// versions 1 to 10 (up to 213 bytes). <c>Modules(text)[row][column]</c> is true for a dark module; the quiet
    /// zone (four light modules on every side) is the drawer's.
    /// </summary>
    public static class QrCode
    {
        // Error correction codewords per block and the number of blocks, level M, versions 1..10.
        static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
        const int MaxVersion = 10;
        // Format information's two bits for level M.
        const int LevelM = 0;

        static int RawDataModules(int version)
        {
            var result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                var align = version / 7 + 2;
                result -= (25 * align - 10) * align - 55;
                if (version >= 7) result -= 36;
            }
            return result;
        }

        static int TotalCodewords(int version) => RawDataModules(version) / 8;

        static int DataCodewords(int version) => TotalCodewords(version) - EcPerBlock[version] * Blocks[version];

        static int GfMultiply(int x, int y)
        {
            var z = 0;
            for (var i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((int)((uint)z >> 7) * 0x11d);
                z ^= ((y >> i) & 1) * x;
            }
            return z & 0xff;
        }

        static int[] RsDivisor(int degree)
        {
            var result = new int[degree];
            result[degree - 1] = 1;
            var root = 1;
            for (var i = 0; i < degree; i++)
            {
                for (var j = 0; j < degree; j++)
                {
                    result[j] = GfMultiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = GfMultiply(root, 0x02);
            }
            return result;
        }

        static int[] RsRemainder(List<int> data, int[] divisor)
        {
            var result = new int[divisor.Length];
            foreach (var value in data)
            {
                var factor = value ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (var i = 0; i < divisor.Length; i++) result[i] ^= GfMultiply(divisor[i], factor);
            }
            return result;
        }

        /// <summary>The data codewords: mode, count, bytes, terminator, then the 0xEC/0x11 padding to the version's capacity.</summary>
        static List<int> DataBytes(byte[] bytes, int version)
        {
            var bits = new List<int>();
            void Put(int value, int length)
            {
                for (var i = length - 1; i >= 0; i--) bits.Add((value >> i) & 1);
            }
            Put(4, 4);
            Put(bytes.Length, version <= 9 ? 8 : 16);
            foreach (var value in bytes) Put(value, 8);
            var capacity = DataCodewords(version) * 8;
            Put(0, Math.Min(4, capacity - bits.Count));
            Put(0, (8 - bits.Count % 8) % 8);
            var output = new List<int>();
            for (var i = 0; i < bits.Count; i += 8)
            {
                var word = 0;
                for (var k = 0; k < 8; k++) word = (word << 1) | bits[i + k];
                output.Add(word);
            }
            for (var pad = 0xec; output.Count < DataCodewords(version); pad ^= 0xec ^ 0x11) output.Add(pad);
            return output;
        }

        /// <summary>Splits the data into the version's blocks, adds each block's error correction and interleaves them.</summary>
        static List<int> Codewords(List<int> data, int version)
        {
            var blocks = Blocks[version];
            var ecLength = EcPerBlock[version];
            var total = TotalCodewords(version);
            var shortBlocks = blocks - total % blocks;
            var shortLength = total / blocks;
            var divisor = RsDivisor(ecLength);
            var parts = new List<List<int>>();
            var codes = new List<int[]>();
            var at = 0;
            for (var i = 0; i < blocks; i++)
            {
                var length = shortLength - ecLength + (i < shortBlocks ? 0 : 1);
                var part = data.GetRange(at, length);
                at += length;
                parts.Add(part);
                codes.Add(RsRemainder(part, divisor));
            }
            var output = new List<int>();
            for (var i = 0; i <= shortLength - ecLength; i++)
            {
                for (var b = 0; b < blocks; b++)
                {
                    if (i < parts[b].Count) output.Add(parts[b][i]);
                }
            }
            for (var i = 0; i < ecLength; i++)
            {
                for (var b = 0; b < blocks; b++) output.Add(codes[b][i]);
            }
            return output;
        }

        sealed class Matrix
        {
            internal readonly int Size;
            internal readonly bool[][] Dark;
            internal readonly bool[][] Fixed;

            internal Matrix(int size)
            {
                Size = size;
                Dark = new bool[size][];
                Fixed = new bool[size][];
                for (var i = 0; i < size; i++)
                {
                    Dark[i] = new bool[size];
                    Fixed[i] = new bool[size];
                }
            }

            internal void Set(int x, int y, bool dark)
            {
                Dark[y][x] = dark;
                Fixed[y][x] = true;
            }
        }

        static List<int> AlignmentPositions(int version, int size)
        {
            var positions = new List<int>();
            if (version == 1) return positions;
            var count = version / 7 + 2;
            var span = count * 2 - 2;
            var step = ((version * 4 + 4 + span - 1) / span) * 2;
            positions.Add(6);
            for (var position = size - 7; positions.Count < count; position -= step) positions.Insert(1, position);
            return positions;
        }

        static void DrawFormat(Matrix m, int mask)
        {
            var data = (LevelM << 3) | mask;
            var rem = data;
            for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            var bits = ((data << 10) | rem) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) == 1;
            for (var i = 0; i <= 5; i++) m.Set(8, i, Bit(i));
            m.Set(8, 7, Bit(6));
            m.Set(8, 8, Bit(7));
            m.Set(7, 8, Bit(8));
            for (var i = 9; i < 15; i++) m.Set(14 - i, 8, Bit(i));
            for (var i = 0; i < 8; i++) m.Set(m.Size - 1 - i, 8, Bit(i));
            for (var i = 8; i < 15; i++) m.Set(8, m.Size - 15 + i, Bit(i));
            m.Set(8, m.Size - 8, true);
        }

        static void DrawFunctionPatterns(Matrix m, int version)
        {
            var size = m.Size;
            for (var i = 0; i < size; i++)
            {
                m.Set(6, i, i % 2 == 0);
                m.Set(i, 6, i % 2 == 0);
            }
            foreach (var center in new[] { new[] { 3, 3 }, new[] { size - 4, 3 }, new[] { 3, size - 4 } })
            {
                for (var dy = -4; dy <= 4; dy++)
                {
                    for (var dx = -4; dx <= 4; dx++)
                    {
                        var x = center[0] + dx;
                        var y = center[1] + dy;
                        var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        if (x >= 0 && x < size && y >= 0 && y < size) m.Set(x, y, distance != 2 && distance != 4);
                    }
                }
            }
            var positions = AlignmentPositions(version, size);
            var last = positions.Count - 1;
            for (var i = 0; i < positions.Count; i++)
            {
                for (var j = 0; j < positions.Count; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)) continue;
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++) m.Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
            DrawFormat(m, 0);
            if (version >= 7)
            {
                var rem = version;
                for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1f25);
                var bits = (version << 12) | rem;
                for (var i = 0; i < 18; i++)
                {
                    var dark = ((bits >> i) & 1) == 1;
                    var a = size - 11 + i % 3;
                    var b = i / 3;
                    m.Set(a, b, dark);
                    m.Set(b, a, dark);
                }
            }
        }

        static void DrawData(Matrix m, List<int> data)
        {
            var i = 0;
            for (var right = m.Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (var vertical = 0; vertical < m.Size; vertical++)
                {
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? m.Size - 1 - vertical : vertical;
                        if (!m.Fixed[y][x] && i < data.Count * 8)
                        {
                            m.Dark[y][x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) == 1;
                            i++;
                        }
                    }
                }
            }
        }

        static bool Masked(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (x + y) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (x + y) % 3 == 0;
                case 4: return (x / 3 + y / 2) % 2 == 0;
                case 5: return x * y % 2 + x * y % 3 == 0;
                case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
                default: return ((x + y) % 2 + x * y % 3) % 2 == 0;
            }
        }

        static void ApplyMask(Matrix m, int mask)
        {
            for (var y = 0; y < m.Size; y++)
            {
                for (var x = 0; x < m.Size; x++)
                {
                    if (!m.Fixed[y][x] && Masked(mask, x, y)) m.Dark[y][x] = !m.Dark[y][x];
                }
            }
        }

        static readonly bool[][] FinderLike =
        {
            new[] { true, false, true, true, true, false, true, false, false, false, false },
            new[] { false, false, false, false, true, false, true, true, true, false, true },
        };

        /// <summary>The standard's four penalty rules; the mask with the lowest score is drawn.</summary>
        static int Penalty(bool[][] dark)
        {
            var size = dark.Length;
            bool At(int x, int y, bool transpose) => transpose ? dark[x][y] : dark[y][x];
            var score = 0;
            foreach (var transpose in new[] { false, true })
            {
                for (var y = 0; y < size; y++)
                {
                    var run = 1;
                    for (var x = 1; x <= size; x++)
                    {
                        if (x < size && At(x, y, transpose) == At(x - 1, y, transpose))
                        {
                            run++;
                            continue;
                        }
                        if (run >= 5) score += 3 + (run - 5);
                        run = 1;
                    }
                    for (var x = 0; x + 11 <= size; x++)
                    {
                        foreach (var pattern in FinderLike)
                        {
                            var same = true;
                            for (var k = 0; k < pattern.Length && same; k++) same = At(x + k, y, transpose) == pattern[k];
                            if (same) score += 40;
                        }
                    }
                }
            }
            for (var y = 0; y + 1 < size; y++)
            {
                for (var x = 0; x + 1 < size; x++)
                {
                    var c = dark[y][x];
                    if (c == dark[y][x + 1] && c == dark[y + 1][x] && c == dark[y + 1][x + 1]) score += 3;
                }
            }
            var darkCount = 0;
            foreach (var row in dark)
            {
                foreach (var cell in row)
                {
                    if (cell) darkCount++;
                }
            }
            var total = size * size;
            var off = Math.Abs(darkCount * 20 - total * 10);
            score += ((off + total - 1) / total - 1) * 10;
            return score;
        }

        /// <summary>The QR code for <paramref name="text"/> (UTF-8). <paramref name="mask"/> forces one of the eight masks; by default the standard's choice.</summary>
        public static bool[][] Modules(string text, int? mask = null, int? version = null)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var chosen = version ?? 1;
            while (chosen <= MaxVersion && (bytes.Length + (chosen <= 9 ? 2 : 3)) * 8 - 4 > DataCodewords(chosen) * 8) chosen++;
            if (chosen > MaxVersion) throw new ArgumentOutOfRangeException(nameof(text), $"{bytes.Length} bytes do not fit a version {MaxVersion} QR code");
            var data = Codewords(DataBytes(bytes, chosen), chosen);
            var size = chosen * 4 + 17;
            bool[][]? best = null;
            var bestScore = int.MaxValue;
            for (var candidate = 0; candidate < 8; candidate++)
            {
                if (mask.HasValue && candidate != mask.Value) continue;
                var m = new Matrix(size);
                DrawFunctionPatterns(m, chosen);
                DrawData(m, data);
                ApplyMask(m, candidate);
                DrawFormat(m, candidate);
                var score = mask.HasValue ? 0 : Penalty(m.Dark);
                if (score < bestScore)
                {
                    best = m.Dark;
                    bestScore = score;
                }
            }
            return best ?? throw new ArgumentOutOfRangeException(nameof(mask));
        }
    }
}
