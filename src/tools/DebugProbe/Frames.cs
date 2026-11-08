using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DebugProbe;

/// <summary>
/// 抓屏。判「日历那一块上有没有日历」的那把尺子在 <see cref="PanelGrid"/>。
///
/// <para>出处：<c>.scratch/debug-tool/SPEC.md</c> 第三节。</para>
/// </summary>
internal static class Screen
{
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint CapTureBlt = 0x40000000;
    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint rop);

    /// <summary>
    /// 抓屏幕上的一块。进程起来已经设过 DPI 感知，所以这里的坐标就是屏幕像素。
    /// </summary>
    internal static Frame Grab(RectI region)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<BitmapInfo>(),
            Width = region.Width,
            Height = -region.Height,
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
        };

        IntPtr bits = IntPtr.Zero;
        IntPtr bitmap = CreateDIBSection(screen, ref info, DibRgbColors, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
            throw new InvalidOperationException($"抓屏：建不出那一块位图，错误码 {Marshal.GetLastWin32Error()}");
        }

        IntPtr previous = SelectObject(memory, bitmap);
        // CAPTUREBLT：把分层窗那一层也带上。不带它，WS_EX_LAYERED 的窗抓下来是空的。
        BitBlt(memory, 0, 0, region.Width, region.Height, screen, region.Left, region.Top, SrcCopy | CapTureBlt);

        int stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        Marshal.Copy(bits, pixels, 0, pixels.Length);

        SelectObject(memory, previous);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);

        return new Frame(region, pixels);
    }
}

/// <summary>
/// 抓下来的那一帧。
/// </summary>
internal sealed class Frame
{
    internal Frame(RectI where, byte[] pixels)
    {
        Where = where;
        Pixels = pixels;
        Width = where.Width;
        Height = where.Height;
    }

    internal RectI Where { get; }

    internal byte[] Pixels { get; }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>那一帧每一格归谁。抓帧的那一发必须带着它 —— 没有它，这一帧不算数。</summary>
    internal string OwnerText { get; set; } = "（没记归谁）";

    /// <summary>
    /// 把一张存下来的图读成一帧。<b>只有离线核对用</b> ——
    /// 屏上那一问永远走 <see cref="Screen.Grab"/>，不拿磁盘上的旧帧当屏上的读数。
    /// 读回来之后判据那一条路和屏上完全一样（<see cref="PanelGrid.Measure"/>）。
    /// </summary>
    internal static Frame FromPng(string path)
    {
        Png.Image image = Png.Load(path);
        return new Frame(new RectI(0, 0, image.Width, image.Height), image.Bgra);
    }

    /// <summary>
    /// 从这一帧裁一块出来。<paramref name="region"/> 用这一帧自己的坐标，
    /// 越界那一部分当没有 —— <see cref="Intersect"/> 那一处说了算。
    ///
    /// <para><b>裁出来的那一块从 (0,0) 起算。</b>像素数组已经搬到了开头，
    /// 位置还留着原来那一帧的坐标的话，下一处按坐标去取像素就取错地方了。</para>
    /// </summary>
    internal Frame Slice(RectI region)
    {
        RectI clipped = Intersect(region, new RectI(0, 0, Width, Height));
        var pixels = new byte[clipped.Width * clipped.Height * 4];
        for (int y = 0; y < clipped.Height; y++)
        {
            for (int x = 0; x < clipped.Width; x++)
            {
                int from = ((clipped.Top + y) * Width + clipped.Left + x) * 4;
                int to = (y * clipped.Width + x) * 4;
                Array.Copy(Pixels, from, pixels, to, 4);
            }
        }

        return new Frame(new RectI(0, 0, clipped.Width, clipped.Height), pixels);
    }

    internal static RectI Intersect(RectI a, RectI b) => new(
        Math.Max(a.Left, b.Left),
        Math.Max(a.Top, b.Top),
        Math.Min(a.Right, b.Right),
        Math.Min(a.Bottom, b.Bottom));

    /// <summary>落一张 PNG 给人对着看。零依赖 —— 这一台机器离线构建，不能引新包。</summary>
    internal void SavePng(string path)
    {
        var raw = new byte[Height * (Width * 3 + 1)];
        int at = 0;
        for (int y = 0; y < Height; y++)
        {
            raw[at++] = 0; // 每行的滤波字节，0 = 不滤波
            for (int x = 0; x < Width; x++)
            {
                int from = (y * Width + x) * 4;
                raw[at++] = Pixels[from + 2];
                raw[at++] = Pixels[from + 1];
                raw[at++] = Pixels[from];
            }
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, Width);
        WriteBigEndian(header, 4, Height);
        header[8] = 8;  // 位深
        header[9] = 2;  // 真彩色
        Chunk(png, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, png.ToArray());
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
        stream.Write(data);

        uint crc = Crc32(System.Text.Encoding.ASCII.GetBytes(type), data);
        var tail = new byte[4];
        WriteBigEndian(tail, 0, unchecked((int)crc));
        stream.Write(tail);
    }

    private static void WriteBigEndian(byte[] buffer, int at, int value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (byte b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFF;
    }
}