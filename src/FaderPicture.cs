using System;
using System.Collections.Generic;
using Godot;

namespace Keeper;

/// <summary>遮罩背后那张图。<c>image.enabled</c> / <c>image.file</c> / <c>image.position</c>
/// 三个键和设置窗那三行从 2026-09-25 就活着，缺的是<b>画它的那一层</b>：
/// <c>TextureRect</c> 之前只出现在 <c>Calendar.cs</c>（画壁纸那一层用），遮罩里一处都没有。
///
/// 形状在设计文档 `docs/design/fader-layout.md`：「The <c>image</c> picture is not a step's
/// <c>content</c> — it sits behind every step of every flow, as it always did」，所以它不跟着
/// 换档藏藏露露，只跟着整窗那条 alpha 斜坡一起深浅（它是 <c>_content</c> 的子节点，
/// modulate 乘在它身上）。
///
/// 五种摆法，就是设计文档 `docs/design/fader-layout.md` 那一族的五个取值：<c>fill</c>=cover（盖满，多的裁掉）、
/// <c>fit</c>=contain（装下，留边）、<c>stretch</c>=两轴各拉满、<c>tile</c>=原尺寸平铺、
/// <c>center</c>=原尺寸居中不铺。摆位的基准是「图的中心对屏的中心」——
/// 那一条对 <c>tile</c> 也成立，所以平铺的格子是从屏幕正中往外排的，不是从左上角。
///
/// 没用 <c>TextureRect</c> 是因为它只到得了前三种：它的 stretch mode 没有平铺这一档，
/// 而一个控件画五种摆法比「三种走控件、两种走自绘」好读。摆位本身是<b>纯算术</b>
/// （<see cref="Place"/>），所以那五种在 <c>--check</c> 里各算一次；真像素留给拿眼睛看那一发。</summary>
public partial class FaderPicture : Control
{
    /// 声明表里 <c>image.position</c> 那五个取值，一个不多一个不少。
    public static readonly string[] Positions = ["fill", "fit", "stretch", "tile", "center"];

    /// 这一版放得了的图档。<c>gif</c> 不在里面 —— 那是引擎的能力问题，不是口味：
    /// 2026-09-28 拿两份**已知好的**两帧 gif 量过 ——
    /// <c>Godot.Image.LoadFromFile()</c> 对它们回 <c>null</c> + <c>Error 15</c>
    /// （<c>ERR_FILE_UNRECOGNIZED</c>，「没认领这个格式」，和 <c>16</c>「认得但这份坏了」是两件事），
    /// 同一跑里 png / jpg / bmp 各读到 8x8、webp 写得出来也读得回来。实测结果记在
    /// <c>docs/research/gif-decoder-spike.md</c> 那五条实测结果。处置照 m4a 那条先例：不引第三方
    /// 解码器（三条库路的代价都量在那一行里），但界面别说谎 —— 这一档不在可选列表里，
    /// 而且挑进来要说清是哪一档不行。
    public static readonly string[] Formats = ["jpg", "jpeg", "png", "bmp", "webp"];

    private Texture2D? _tex;
    private Image? _image;
    private Vector2I _size;
    private string _position = "center";
    private Color _flat;

    /// <summary>读那张图并摆起来。<b>必须在举起窗口之前调用</b>：要求是「淡入不许先露纯色底、
    /// 过一会儿才补上图」—— 引擎这边读文件是同步的，所以这一条白送，只要调用方在 <c>Show()</c>
    /// 之前问它。读不到就什么都不画（纯色底照旧），并给一句原因。
    ///
    /// <paramref name="flat"/> 是文档里那个 <c>backgroundColor</c>，而这一层<b>自己把它画上</b>：
    /// 有图的地方是图，没图的那一圈（<c>fit</c> 留的边）是纯色底。为什么要同一层画 ——
    /// 纯色底和图分两层的话，两层各吃一次整窗那条 alpha 斜坡，合成出来的不透明度成了
    /// <c>1-(1-a)²</c> 而不是 <c>a</c>，图上还透出 19% 的底。2026-09-26 拿判据一反解出来的：
    /// 压在黑底上读回 (24,72,84)，两层模型算出来正是 (24,72,84)，单层模型该是 (24,48,72)。</summary>
    public bool Set(string path, string position, Color flat, Action<string>? log = null)
    {
        _flat = flat;
        _position = Array.IndexOf(Positions, position) >= 0 ? position : "center";
        // 引擎这条是静态工厂：读不进来还回 null，不是还回一个错误码。
        var image = Image.LoadFromFile(path);
        if (image == null || image.GetWidth() < 1 || image.GetHeight() < 1)
        {
            log?.Invoke(WhyNot(path));
            Clear();
            return false;
        }
        _size = new Vector2I(image.GetWidth(), image.GetHeight());
        _image = image;
        _tex = ImageTexture.CreateFromImage(image);
        Visible = true;
        QueueRedraw();
        log?.Invoke($"图：{path} {_size.X}x{_size.Y} 摆法={_position}");
        return true;
    }

    /// <summary>「读不进来」那一句分两种说法：这一档引擎放不了，还是那一个文件读不出来。
    /// 分不开的话人只能去猜是不是路径写错了；这一版按 <c>Sound.cs</c> 那句「认不出的格式，这一版会放 …」
    /// 的同一种写法说清。
    /// gif 就是落在前一句里的那一种（实测结果见 <see cref="Formats"/> 上面那段注释）。</summary>
    public static string WhyNot(string path)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext.Length == 0 || Array.IndexOf(Formats, ext) < 0
            ? $"图：{(path.Length == 0 ? "(空)" : path)} 这一档（{(ext.Length == 0 ? "没有扩展名" : "." + ext)}）" +
              $"这一版放不了，遮罩还是纯色底 —— 这一版会放 {string.Join(" / ", Formats)}"
            : $"图：{path} 读不进来（文件不在、是空的或坏了），遮罩还是纯色底";
    }

    /// 不画了。<b>不叫 Hide</b>：<c>CanvasItem.Hide()</c> 在那儿摆着，同名会把继承的那一个遮掉
    /// （CS0108），而这里要的是「把图扔掉」，不是「把控件藏起来」。
    public void Clear()
    {
        _tex = null;
        _image?.Dispose();
        _image = null;
        Visible = false;
    }

    /// <summary>这一层在<b>窗口内</b>那一点上画的是什么色；没图、或者那一点在图外
    /// （<c>fit</c> 留的那两条边）就还回 <c>null</c>。
    ///
    /// 测量程序那一发要的正是这个：判据得知道「字底下压着的是什么」，不然一张图会被数成一片字
    /// —— 2026-09-26 那一发开了 <c>image</c> 之后读到 613‰ 的「字」，那个数不是布局，是图本身。
    /// 摆位用同一个 <see cref="Place"/>，所以这一发顺带就是那五种摆法的逐像素实测结果。</summary>
    /// <param name="surface">按哪一块面积摆这张图。<b>默认是控件自己的 Size</b>；遮罩那边在
    /// 构造里问「字底下是什么」的时候容器还没排版，Size 还是 (0,0) —— 那一发不传就会一路读到
    /// null（2026-09-27 亮图那一发帧上字色没换档，就是这个）。</param>
    public Color? PixelAt(Vector2 at, Vector2? surface = null)
    {
        var size = surface ?? Size;
        if (_image == null || size.X < 1 || size.Y < 1) return null;
        var (dst, tile) = Place(size, _size, _position);
        if (dst.Size.X <= 0f || dst.Size.Y <= 0f) return null;
        float x, y;
        if (tile)
        {
            // 平铺：格线从 dst 的原点往外重复，所以那一点落在第几格里是取模的事。
            x = Mod(at.X - dst.Position.X, _size.X);
            y = Mod(at.Y - dst.Position.Y, _size.Y);
        }
        else
        {
            if (!dst.HasPoint(at)) return null;
            x = (at.X - dst.Position.X) / dst.Size.X * _size.X;
            y = (at.Y - dst.Position.Y) / dst.Size.Y * _size.Y;
        }
        // 取整那一档：一个目标像素覆盖好几个源像素时，读的是它左上角那一个。近似，
        // 但对「这一处该是什么色」的判据够用（差一个像素的采样点不会把字读成图）。
        var px = Math.Clamp((int)System.Math.Floor(x), 0, _size.X - 1);
        var py = Math.Clamp((int)System.Math.Floor(y), 0, _size.Y - 1);
        var c = _image.GetPixel(px, py);
        return new Color(c.R, c.G, c.B);
    }

    /// <summary>纯色底 + 图，<b>一个像素只吃一笔</b>。
    ///
    /// 为什么不是「先铺一层纯色底、再把图盖上去」：两笔各乘一次整窗那条 alpha 斜坡，
    /// 合成出来的不透明度是 <c>1-(1-a)²</c> 而不是 <c>a</c>，图上还透出 19% 的底 ——
    /// 2026-09-26 拿判据一反解出来的那一发（黑底上读回 (24,72,84)，两层模型算出来正是这个数，
    /// 单层该是 (24,48,72)）。同一层里的两笔也一样：画布是按每一笔往表面上合成的。
    /// 所以纯色底只补<b>图盖不到的那几条边</b>（<c>fit</c> 和 <c>center</c> 留的那两圈），
    /// 图那一块单独一笔。</summary>
    public override void _Draw()
    {
        var whole = new Rect2(Vector2.Zero, Size);
        if (_tex == null)
        {
            DrawRect(whole, _flat);   // 没图：整窗就是纯色底，一笔
            return;
        }
        var (dst, tile) = Place(Size, _size, _position);
        if (dst.Size.X <= 0f || dst.Size.Y <= 0f) { DrawRect(whole, _flat); return; }
        if (tile)
        {
            DrawTextureRect(_tex, dst, true);   // 平铺盖满整窗，没有边要补
            return;
        }
        var covered = whole.Intersection(dst);
        if (covered.Size.X <= 0f || covered.Size.Y <= 0f) DrawRect(whole, _flat);
        else
        {
            if (covered.Position.Y > 0f)
                DrawRect(new Rect2(0, 0, Size.X, covered.Position.Y), _flat);
            if (covered.End.Y < Size.Y)
                DrawRect(new Rect2(0, covered.End.Y, Size.X, Size.Y - covered.End.Y), _flat);
            if (covered.Position.X > 0f)
                DrawRect(new Rect2(0, covered.Position.Y, covered.Position.X, covered.Size.Y), _flat);
            if (covered.End.X < Size.X)
                DrawRect(new Rect2(covered.End.X, covered.Position.Y, Size.X - covered.End.X,
                    covered.Size.Y), _flat);
        }
        DrawTextureRect(_tex, dst, false);
    }

    /// <summary>这块屏上该把这张图画在哪一块、要不要平铺。<b>纯的</b>：没有引擎、没有磁盘，
    /// 所以五种摆法各能在自检里算一次。两个参数都是 <c>Vector2</c>：控件的尺寸本来就是浮点的
    /// （<c>Control.Size</c> 是 Vector2，只有 <c>Window.Size</c> 才是 Vector2I），而 Vector2I
    /// 隐式转得过来，所以调用方两边都不用写一次转换。</summary>
    public static (Rect2 Dst, bool Tile) Place(Vector2 screen, Vector2 image, string position)
    {
        var (w, h) = (screen.X, screen.Y);
        var (iw, ih) = (image.X, image.Y);
        if (iw <= 0f || ih <= 0f || w <= 0f || h <= 0f) return (default, false);

        switch (position)
        {
            case "stretch":
                return (new Rect2(0, 0, w, h), false);
            case "tile":
            {
                // tile 的摆位基准：先按「图的中心对屏的中心」摆第一块，
                // 再从那一块往两边铺。往左上多铺一格是为了把 (0,0) 那一角也盖住 —— 裁切靠
                // clip_contents，伸出窗外的那一块不画出来。
                var ox = Mod((w - iw) / 2f, iw);
                var oy = Mod((h - ih) / 2f, ih);
                return (new Rect2(ox - iw, oy - ih, w + iw * 2f, h + ih * 2f), true);
            }
            case "fill":
            case "fit":
            {
                // cover 取大的那个比例（于是短的那一轴溢出去、被裁掉），contain 取小的（留边）。
                var scale = position == "fill" ? Math.Max(w / iw, h / ih) : Math.Min(w / iw, h / ih);
                return (Centered(w, h, iw * scale, ih * scale), false);
            }
            default:   // center；设置里那个值不认识时也走这一档
                return (Centered(w, h, iw, ih), false);
        }
    }

    private static Rect2 Centered(float w, float h, float bw, float bh) =>
        new((w - bw) / 2f, (h - bh) / 2f, bw, bh);

    /// 数学取模（结果不带符号），平铺原点要的就是它 —— 差出整整一格也是同一个格子。
    private static float Mod(float a, float m) => ((a % m) + m) % m;

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Picture = new(
        () => "遮罩的图通过：fill/fit/stretch/tile/center 各摆一次、平铺的格线从屏幕正中往外排、" +
              "大图 cover 盖得住、空图什么都不画、认不出的摆法退到居中",
        "遮罩的图", SelfTest);

    /// 自检里那一发：五种摆法各算一次，查的是「铺满没有、裁了多少、平铺的格子排在哪」。
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        void Expect(string what, bool ok)
        {
            if (!ok) problems.Add($"遮罩的图「{what}」不对");
        }
        void ExpectSize(string what, Rect2 got, float wide, float high)
        {
            if (!Near(got.Size.X, wide) || !Near(got.Size.Y, high))
                problems.Add($"遮罩的图「{what}」算出来是 {got.Size}，该是 {wide}x{high}");
        }

        var screen = new Vector2I(1000, 1000);
        var pic = new Vector2I(500, 250);

        var stretch = Place(screen, pic, "stretch");
        Expect("stretch 拉成整块屏", stretch.Dst.Position == Vector2.Zero && !stretch.Tile);
        ExpectSize("stretch 拉成整块屏", stretch.Dst, 1000, 1000);

        var center = Place(screen, pic, "center");
        Expect("center 原尺寸、左上角摆在对角线上", center.Dst.Position == new Vector2(250, 375) && !center.Tile);
        ExpectSize("center 原尺寸", center.Dst, 500, 250);

        var fit = Place(screen, pic, "fit");
        Expect("fit 上下留边", Near(fit.Dst.Position.X, 0) && Near(fit.Dst.Position.Y, 250));
        ExpectSize("fit 长边贴边、不裁", fit.Dst, 1000, 500);

        var fill = Place(screen, pic, "fill");
        Expect("fill 左右溢出", Near(fill.Dst.Position.X, -500) && Near(fill.Dst.Position.Y, 0));
        ExpectSize("fill 短边贴边", fill.Dst, 2000, 1000);

        var tile = Place(screen, pic, "tile");
        Expect("tile 是平铺", tile.Tile);
        ExpectSize("tile 往四边各多铺一格", tile.Dst, 2000, 1500);
        // 平铺原点：屏 1000 图 500 → (1000-500)/2=250，mod 500=250，再往左一格 = -250；
        // 高那一轴 (1000-250)/2=375，mod 250=125，再往上一格 = -125。
        // 于是格线在 -250/250/750 和 -125/125/375…，屏幕正中 500 那一处正好是某一格的中心 ——
        // 「图的中心对屏的中心」要的就是这个，而不是从左上角开始铺。
        Expect("tile 的格线从屏幕正中往外排", Near(tile.Dst.Position.X, -250) && Near(tile.Dst.Position.Y, -125));

        // 比屏还大的图：cover 缩到刚好盖住，contain 缩到刚好装下。
        var big = new Vector2I(5000, 3000);
        ExpectSize("大图 contain 之后不越界", Place(screen, big, "fit").Dst, 1000, 600);
        ExpectSize("大图 cover 之后盖得住", Place(screen, big, "fill").Dst, 1666.6667f, 1000);

        // 退化那一档：图还没读进来（0x0）不该画出个 1x1 的格子。
        Expect("空图什么都不画", Place(screen, Vector2I.Zero, "tile").Dst.Size == Vector2.Zero);
        Expect("认不出的摆法退到居中", Place(screen, pic, "斜着来").Dst == center.Dst);

        // 「读不进来」那一句要分两档：这一版的引擎不认 gif（2026-09-28 拿两份**已知好的**
        // 两帧文件量过：LoadFromFile 回 null + Error 15
        // = ERR_FILE_UNRECOGNIZED，而同一跑里 png / jpg / bmp 各读到 8x8、webp 写得回 —— 实测结果在
        // docs/research/gif-decoder-spike.md），混在「读不进来」里就是让人猜。
        Expect("gif 那一档说成「这一版放不了」",
            WhyNot("C:/x/a.gif").Contains("这一档（.gif）这一版放不了") &&
            WhyNot("C:/x/a.gif").Contains("jpg / jpeg"));
        Expect("认得的格式读不出来才说「读不进来」", WhyNot("C:/x/a.png").Contains("读不进来"));
        Expect("没有扩展名也归到「这一版放不了」那一档", WhyNot("C:/x/noext").Contains("没有扩展名"));
        Expect("可选列表里没有 gif（引擎放不了就不给人挑）", Array.IndexOf(Formats, "gif") < 0);
        return problems;
    }

    /// 比例是 float 除法算出来的，0.2f 乘 5000 不是正好 1000，所以这里按 1/100 像素比。
    private static bool Near(float got, float want) => Math.Abs(got - want) < 0.01f;
}
