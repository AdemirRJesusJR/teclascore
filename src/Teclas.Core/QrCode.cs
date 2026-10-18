using System.Text;

namespace Teclas.Core;

/// <summary>
/// Minimal QR Code encoder for the phone remote address: byte mode, error correction level L, versions 1 to 5 (up to
/// 106 bytes, one Reed–Solomon block, no version information). The mask with the lowest penalty is chosen, as the
/// standard asks. Modules are [row, column], true = dark, without the quiet zone.
/// </summary>
public static class QrCode
{
    private static readonly int[] DataCodewords = [0, 19, 34, 55, 80, 108];
    private static readonly int[] EccCodewords = [0, 7, 10, 15, 20, 26];

    public static bool[,] Encode(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
        int version = Enumerable.Range(1, 5).FirstOrDefault(v => 4 + 8 + bytes.Length * 8 <= DataCodewords[v] * 8);
        if (version == 0) throw new ArgumentException("Texto longo demais para o código QR.", nameof(text));
        int size = 17 + 4 * version;
        var codewords = DataWithEcc(bytes, version);
        var modules = new bool[size, size];
        var function = new bool[size, size];
        DrawFunctionPatterns(modules, function, version);
        DrawCodewords(modules, function, codewords);
        int bestMask = 0; long bestPenalty = long.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, function, mask); DrawFormatBits(modules, function, mask);
            long penalty = Penalty(modules);
            if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
            ApplyMask(modules, function, mask); // XOR again undoes it
        }
        ApplyMask(modules, function, bestMask); DrawFormatBits(modules, function, bestMask);
        return modules;
    }

    private static byte[] DataWithEcc(byte[] bytes, int version)
    {
        int capacity = DataCodewords[version] * 8;
        var bits = new List<bool>();
        void Append(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4); Append(bytes.Length, 8);
        foreach (byte b in bytes) Append(b, 8);
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var data = new byte[DataCodewords[version]];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));
        var ecc = Remainder(data, Divisor(EccCodewords[version]));
        return [.. data, .. ecc];
    }

    private static byte[] Divisor(int degree)
    {
        var result = new byte[degree]; result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] Remainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1); result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    private static int Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
        return z;
    }

    private static void Set(bool[,] modules, bool[,] function, int x, int y, bool dark) { modules[y, x] = dark; function[y, x] = true; }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] function, int version)
    {
        int size = modules.GetLength(0);
        for (int i = 0; i < size; i++) { Set(modules, function, 6, i, i % 2 == 0); Set(modules, function, i, 6, i % 2 == 0); }
        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy, distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (x >= 0 && x < size && y >= 0 && y < size) Set(modules, function, x, y, distance != 2 && distance != 4);
                }
        if (version >= 2)
        {
            int c = size - 7;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++) Set(modules, function, c + dx, c + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }
        DrawFormatBits(modules, function, 0);
    }

    private static void DrawFormatBits(bool[,] modules, bool[,] function, int mask)
    {
        int size = modules.GetLength(0);
        int data = 1 << 3 | mask, rem = data; // level L
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++) Set(modules, function, 8, i, Bit(i));
        Set(modules, function, 8, 7, Bit(6)); Set(modules, function, 8, 8, Bit(7)); Set(modules, function, 7, 8, Bit(8));
        for (int i = 9; i < 15; i++) Set(modules, function, 14 - i, 8, Bit(i));
        for (int i = 0; i < 8; i++) Set(modules, function, size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) Set(modules, function, 8, size - 15 + i, Bit(i));
        Set(modules, function, 8, size - 8, true);
    }

    private static void DrawCodewords(bool[,] modules, bool[,] function, byte[] data)
    {
        int size = modules.GetLength(0), i = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j; bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (!function[y, x] && i < data.Length * 8) { modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0; i++; }
                }
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] function, int mask)
    {
        int size = modules.GetLength(0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (function[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0, 5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                };
                modules[y, x] ^= invert;
            }
    }

    /// <summary>Penalty rules of the standard: runs, 2×2 blocks, finder-like patterns and dark/light balance.</summary>
    private static long Penalty(bool[,] m)
    {
        int size = m.GetLength(0); long result = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b <= size; b++)
                {
                    bool same = b < size && (pass == 0 ? m[a, b] == m[a, b - 1] : m[b, a] == m[b - 1, a]);
                    if (same) { run++; continue; }
                    if (run >= 5) result += 3 + run - 5;
                    run = 1;
                }
                for (int b = 0; b + 10 < size + 4; b++)
                {
                    // 1:1:3:1:1 dark pattern with four light modules on one side (outside the symbol counts as light).
                    bool At(int i) => i >= 0 && i < size && (pass == 0 ? m[a, i] : m[i, a]);
                    bool core = At(b) && !At(b + 1) && At(b + 2) && At(b + 3) && At(b + 4) && !At(b + 5) && At(b + 6);
                    if (core && ((!At(b - 1) && !At(b - 2) && !At(b - 3) && !At(b - 4)) || (!At(b + 7) && !At(b + 8) && !At(b + 9) && !At(b + 10)))) result += 40;
                }
            }
        for (int y = 0; y + 1 < size; y++)
            for (int x = 0; x + 1 < size; x++)
                if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) result += 3;
        int dark = 0; foreach (bool module in m) if (module) dark++;
        int total = size * size;
        result += Math.Max(0, (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1) * 10;
        return result;
    }
}
