using System;
using System.Globalization;
using System.Linq;

namespace Keeper;

/// <summary>生活模式那几份文件的<b>名字和编号</b>这一套，只住在这里 —— 谁要发号、要算文件名、
/// 要把一个文件名读回成号，都问这一处（设计文档 `docs/design/life-mode.md`「生活模式是一整套配置的快照」那一节「文件名由索引
/// 分配递增号，且永不复用」那条）。
///
/// <b>为什么名字是号而不是生活模式名</b>：生活模式名是任何合法 utf-8（中文、emoji、标点都行），
/// 而路径有长度上限、还有 Windows 自己不许的那些字。拿名字当文件名就是「算出来大概不撞」，
/// 撞了就是「档案里有、桌面上没有」那一对假象；号是程序自己发的一串数字，结构上不撞。
/// 这一族里 <b>不手抄文件名</b>，也<b>不数</b>有几个生活模式（设计文档 `docs/design/life-mode.md`「菜单里不数项」那条）。
///
/// <b>号为什么永不复用</b>：发号走的是一份记着的「下一个该发几」<b>加上</b>磁盘上已经存在的
/// 那些的最大号（<see cref="LifeModeIndex.RaiseTo"/>），只往大里推。删掉一条之后它那个号就作废了，
/// 下一个新生活模式拿更大的 —— 少一条和「那个文件又被另一个生活模式占回去」之间没有任何关系。</summary>
public static class LifeModeNumbers
{
    /// 第一个号。1 而不是 0：<c>0000</c> 那一眼像是「还没有」。</summary>
    public const int First = 1;

    /// 生活模式那一份和设置那一份是同一种格式，所以是同一个后缀（设计文档 `docs/design/life-mode.md`「一个生活模式 = 一份完整的配置文档」那一节）。而它们住在不同的目录里，撞不了。
    public const string Suffix = ".jsonc";

    /// 号 → 文件名。四位是给人看的（目录里一眼能数出「这是第几个」），
    /// 位数不够了自己长，不会绕回去。
    public static string FileNameOf(int number) => $"{number:D4}{Suffix}";

    /// 文件名 → 号。<b>认不出的返回 false</b>，不是 0：0 不是任何一个号，撞上 0 会把一个
    /// 无关的文件（<c>list.jsonc</c>、<c>readme.txt</c>）当成生活模式那一份。
    public static bool TryNumberIn(string fileName, out int number)
    {
        number = 0;
        if (!fileName.EndsWith(Suffix, StringComparison.Ordinal)) return false;
        var stem = fileName[..^Suffix.Length];
        // 全是数字，且不是空串。前导零没关系（0007 和 7 是同一个号），
        // 但「7x」「-1」「」都不行。
        if (stem.Length == 0 || !stem.All(char.IsAsciiDigit)) return false;
        return int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
