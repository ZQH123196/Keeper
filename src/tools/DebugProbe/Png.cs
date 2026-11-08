using System;
using System.IO;
using System.IO.Compression;

namespace DebugProbe;

/// <summary>
/// 读一张 PNG。量具本来只写不读（<see cref="Frame.SavePng"/> 那一族）；
/// 离线核对新判据时要把以前存下来的帧读回来，所以这里补上读的那一半。
///
/// <para><b>只支持 8 位、非隔行</b>。这台机器离线构建，引不起新包，
/// 而手上的帧（量具自己写的、外面别的工具写的）全是这一种。
/// 别的位深、隔行那一族当场拒收，答出它读到的是什么，不猜。</para>
/// </summary>
internal static class Png
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>读回来的图。像素是 BGBA，和 <see cref="Frame"/> 内部那一份同一种摆法。</summary>
    internal readonly record struct Image(int Width, int Height, byte[] Bgra);

    internal static Image Load(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 8 || !Same(file, Signature)) throw new InvalidOperationException($"{path}：不是 PNG（头八个字节不对）");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        int at = 8;
        while (at + 8 <= file.Length)
        {
            int length = ReadBigEndian(file, at);
            string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            int data = at + 8;
            if (length < 0 || data + length + 4 > file.Length) throw new InvalidOperationException($"{path}：{type} 这一块的长度越界");

            switch (type)
            {
                case "IHDR":
                    width = ReadBigEndian(file, data);
                    height = ReadBigEndian(file, data + 4);
                    bitDepth = file[data + 8];
                    colorType = file[data + 9];
                    if (file[data + 10] != 0) throw new InvalidOperationException($"{path}：压缩方式不是 0，读不了");
                    if (file[data + 11] != 0) throw new InvalidOperationException($"{path}：滤波方式不是 0，读不了");
                    interlace = file[data + 12];
                    break;
                case "PLTE":
                    palette = Slice(file, data, length);
                    break;
                case "tRNS":
                    transparency = Slice(file, data, length);
                    break;
                case "IDAT":
                    idat.Write(file, data, length);
                    break;
            }

            at = data + length + 4;
            if (type == "IEND") break;
        }

        if (width <= 0 || height <= 0) throw new InvalidOperationException($"{path}：宽高读不出来");
        if (bitDepth != 8) throw new InvalidOperationException($"{path}：位深是 {bitDepth}，这一份只读 8 位");
        if (interlace != 0) throw new InvalidOperationException($"{path}：隔行的，这一份读不了");
        if (colorType is not (0 or 2 or 3 or 4 or 6)) throw new InvalidOperationException($"{path}：颜色类型是 {colorType}，不认识");

        int channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        if (colorType == 3 && palette == null) throw new InvalidOperationException($"{path}：调色板那一块不见了");

        byte[] flat;
        using (var packed = new MemoryStream(idat.ToArray()))
        using (var inflate = new ZLibStream(packed, CompressionMode.Decompress))
        using (var outStream = new MemoryStream())
        {
            inflate.CopyTo(outStream);
            flat = outStream.ToArray();
        }

        int stride = width * channels;
        if (flat.Length < height * (stride + 1)) throw new InvalidOperationException($"{path}：解开之后只有 {flat.Length} 字节，{width}×{height}×{channels} 要 {height * (stride + 1)}");

        var rows = new byte[height][];
        int source = 0;
        var previous = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            byte filter = flat[source++];
            var row = new byte[stride];
            Array.Copy(flat, source, row, 0, stride);
            source += stride;
            Unfilter(filter, row, previous, channels);
            rows[y] = row;
            previous = row;
        }

        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            byte[] row = rows[y];
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a;
                int at2 = x * channels;
                switch (colorType)
                {
                    case 0:
                        r = g = b = row[at2];
                        a = 255;
                        break;
                    case 2:
                        r = row[at2];
                        g = row[at2 + 1];
                        b = row[at2 + 2];
                        a = 255;
                        break;
                    case 3:
                    {
                        int index = row[at2];
                        int into = index * 3;
                        if (into + 2 >= palette!.Length) throw new InvalidOperationException($"{path}：调色板下标 {index} 越界");
                        r = palette[into];
                        g = palette[into + 1];
                        b = palette[into + 2];
                        a = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
                        break;
                    }
                    case 4:
                        r = g = b = row[at2];
                        a = row[at2 + 1];
                        break;
                    default:
                        r = row[at2];
                        g = row[at2 + 1];
                        b = row[at2 + 2];
                        a = row[at2 + 3];
                        break;
                }

                int to = (y * width + x) * 4;
                bgra[to] = b;
                bgra[to + 1] = g;
                bgra[to + 2] = r;
                bgra[to + 3] = a;
            }
        }

        return new Image(width, height, bgra);
    }

    // ---- PNG 那几种行滤波 ------------------------------------------------------

    private static void Unfilter(byte filter, byte[] row, byte[] previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int i = bytesPerPixel; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - bytesPerPixel]);
                return;
            case 2:
                for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + previous[i]);
                return;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }

                return;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    int up = previous[i];
                    int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    row[i] = (byte)(row[i] + Paeth(left, up, upLeft));
                }

                return;
            default:
                throw new InvalidOperationException($"不认识的行滤波 {filter}");
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int p = left + up - upLeft;
        int dl = Math.Abs(p - left);
        int du = Math.Abs(p - up);
        int dul = Math.Abs(p - upLeft);
        if (dl <= du && dl <= dul) return left;
        return du <= dul ? up : upLeft;
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length < b.Length) return false;
        for (int i = 0; i < b.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    private static byte[] Slice(byte[] source, int at, int length)
    {
        var copy = new byte[length];
        Array.Copy(source, at, copy, 0, length);
        return copy;
    }

    private static int ReadBigEndian(byte[] source, int at) =>
        (source[at] << 24) | (source[at + 1] << 16) | (source[at + 2] << 8) | source[at + 3];
}
