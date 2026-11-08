using System;
using System.IO;
using System.Text;

namespace DebugProbe;

/// <summary>
/// 一边写控制台、一边写文件。
///
/// <para>量具不是每次都从一个控制台起来的 —— 桌面快捷方式、经 <c>explorer.exe</c> 起，
/// 那时那四行会刷进一个没人看得见的窗口。落一份在文件里，那一发才拿得到读数。</para>
/// </summary>
internal sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly string _path;

    internal TeeWriter(TextWriter console, string path)
    {
        _console = console;
        _path = path;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Write(char value)
    {
        _console.Write(value);
        TryAppend(value.ToString());
    }

    public override void Write(string? value)
    {
        _console.Write(value);
        if (value != null) TryAppend(value + Environment.NewLine);
    }

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        TryAppend((value ?? string.Empty) + Environment.NewLine);
    }

    /// <summary>落盘那一口自己不许再炸 —— 它坏了也不能把那一发的读数带走。</summary>
    private void TryAppend(string text)
    {
        try
        {
            File.AppendAllText(_path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"落盘读数失败（{_path}）：{ex.GetType().Name} {ex.Message}");
        }
    }
}