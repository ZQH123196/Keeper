using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Keeper;

/// <summary>休息期间那段音乐。<c>sound.*</c> 六个键和设置窗那六行从 2026-09-25 就是活的，
/// 缺的从来不是设置，是<b>播放那一段</b>：全 <c>src</c> 里搜不到一处
/// <c>AudioStreamPlayer</c>，所以 <c>todoList</c> 那条休息流程落定之后是静音的。
///
/// 走法就是下面这四条，一条没新加：
/// <list type="bullet">
/// <item>要不要响 = 这一步 step 的 <c>sound</c> + <c>sound.enabled</c> + 文件名非空
/// （设计文档 `docs/design/routine.md` 的 step 表：「Whether the <c>sound</c> file plays through it」）。</item>
/// <item>进一个 step 不重头再来：已经响着就让它响着，只有「不要声 → 要声」才起，
/// 「要声 → 不要声」就立刻掐。</item>
/// <item><c>fadeIn</c> 开着就从 0 推到 <c>volume</c>，900 毫秒 —— 这是一根独立的定值，
/// 不是 <c>fadeInMs</c>；<c>fadeOut</c> 开着就跟着淡出那一段收掉（默认开着，注释里写了理由：
/// 闹钟跟遮罩一起淡出去才是它该有的样子）。</item>
/// <item><c>repeat</c> 是「文件放完了再放一遍」，也就是声明里那句「曲子比休息短的时候就靠它了」。
/// 用 <c>finished</c> 重播而不是各格式自带的 loop：mp3/ogg 的 loop 是个 bool、wav 的是
/// 帧区间，三条路三种写法；重播这一条对三种格式是同一句，代价只是循环点上那点空档。</item>
/// </list>
///
/// 斜坡按帧推，和遮罩的 alpha 同一个理由（设计文档 `docs/design/fader-window.md`：the ramp is driven by frames）。
/// 一次淡入只有一份声音，不是每块屏一份 —— 契约那一句也是单数的
/// 「the <c>sound</c> file starts」。</summary>
public sealed partial class Sound : Node
{
    /// <c>fadeIn</c> 那条斜坡走多久。定死 900 毫秒：它不是 <c>fadeInMs</c>
    /// （那是画面的事），拧 <c>fadeInMs</c> 不该把音乐也拉长。
    public const double RampInMs = 900;

    /// 这一版解得开的格式。m4a 不在里面：引擎的运行时里没有 AAC 的解码器，
    /// 所以那一个会读到「认不出的格式」那一行，而不是悄悄静音。
    /// 公开是给设置窗那个文件框用的：文件框的扩展名列表和这里能解的必须是同一份（不抄第二份）。
    public static readonly string[] Formats = [".mp3", ".ogg", ".wav"];

    private readonly Action<string> _log;
    private readonly AudioStreamPlayer _player = new() { Name = "Player" };

    private string _path = "";
    private AudioStream? _stream;
    private string? _problem;

    /// 此刻该不该有声 —— 是「这一步 step 要不要声」，不是「播放器在不在转」。
    private bool _wanted;
    private bool _repeat;

    // 斜坡：从 _rampFrom 走到 _rampTo，走 _rampTotal 毫秒。_rampTotal 归零表示不在斜坡上。
    private double _rampTotal;
    private double _rampLeftMs;
    private float _rampFrom;
    private float _rampTo;

    public Sound(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        AddChild(_player);
        _player.Finished += OnFinished;
    }

    /// <summary>这一步该不该放声音。四个条件缺一个都不放：step 没要、总开关关着、还没有文件，
    /// 或者 <c>sound.afterLanding</c> 开着而这一次淡入还没走完。
    ///
    /// 最后那一条是 2026-09-27 加的选择，为的是把契约和出厂行为对上：设计文档 `docs/design/fader-window.md`
    /// 「「落定」是哪一个时刻」那一节写的是「落定之后才开始放声音」，而 <c>steps[].sound</c> 的默认值是 true，
    /// 内置 todoList 那第一条（淡入那一步）因此也在要声 —— 他从淡入那一刻就听见了。现在默认按契约
    /// （落定之后才响），想要「哪一步说要声就从哪一步响」把它拧关即可。</summary>
    public static bool Plays(Step step, SoundOptions sound, bool landed) =>
        step.Sound && sound.Enabled && sound.File.Length > 0 && (!sound.AfterLanding || landed);

    /// <summary>这一次淡入的斜坡走完了没有。<c>Leave</c> 归零，所以下一次举起重新算。</summary>
    private bool _landed;

    private Step? _step;

    /// 淡举起和进下一个 step 都走这一发。已经在那个状态里就什么都不做。
    public void Enter(Step step, Settings settings)
    {
        _step = step;
        var want = Plays(step, settings.Sound, _landed);
        if (want == _wanted) return;
        _wanted = want;
        if (want) Start(settings.Sound);
        else StopNow("进了一步不要声音的 step");
    }

    /// <summary>淡入走完、遮罩落定那一刻。「落定之后才开始放声音」指的就是这里，
    /// 所以 <c>sound.afterLanding</c> 开着时，前面那几段即使自己说要声也要等这一发。</summary>
    public void Land(Settings settings)
    {
        if (_landed) return;
        _landed = true;
        if (_step != null) Enter(_step, settings);
    }

    /// 淡出那一段。跟着收掉是默认（<c>sound.fadeOut</c> 默认 true）。
    public void Leave(Settings settings)
    {
        _landed = false;
        _step = null;
        if (!_wanted) return;
        _wanted = false;
        var ms = settings.FadeOutMs;
        if (!settings.Sound.FadeOut || ms <= 0) { StopNow("fadeOut 关着"); return; }
        RampTo(0f, ms);
        _log($"声音：跟着淡出收 {ms} 毫秒");
    }

    public override void _Process(double delta)
    {
        if (_rampTotal <= 0) return;
        _rampLeftMs -= delta * 1000;
        var at = Math.Clamp(1 - _rampLeftMs / _rampTotal, 0.0, 1.0);
        _player.VolumeLinear = _rampFrom + (_rampTo - _rampFrom) * (float)at;
        if (at < 1.0) return;
        _rampTotal = 0;
        if (_rampTo <= 0f) StopNow("收完了");
    }

    private void Start(SoundOptions sound)
    {
        var stream = Load(sound.File);
        if (stream == null)
        {
            _log($"声音：{sound.File} 放不出来 —— {_problem}（这一段静音，遮罩照旧）");
            return;
        }
        _repeat = sound.Repeat;
        var target = sound.Volume / 100f;
        _player.Stream = stream;
        _player.VolumeLinear = sound.FadeIn ? 0f : target;
        _player.Play();
        if (sound.FadeIn) RampTo(target, RampInMs);
        // 「在转」这一口是这一发唯一的实测结果：音频设备在不在、文件解没解开，屏幕上看不出来，
        // 而这一支的规矩是「代码写了、自检也全绿」不算数 —— 拿眼睛看的换成问播放器自己。
        _log($"声音：起 {sound.File} 音量={sound.Volume}% 淡进={sound.FadeIn} 重放={sound.Repeat} " +
             $"播放器在转={_player.Playing}");
    }

    /// <c>finished</c> 在放完和被我 Stop 掉的时候都会来，所以这里必须先问「还要不要声」；
    /// 而它来了不等于该重播 —— 只有 <c>repeat</c> 开着才再来一遍。
    private void OnFinished()
    {
        if (!_wanted || !_repeat) return;
        _player.Play();
    }

    private void RampTo(float to, double ms)
    {
        _rampFrom = _player.VolumeLinear;
        _rampTo = to;
        _rampTotal = ms;
        _rampLeftMs = ms;
    }

    private void StopNow(string why)
    {
        _rampTotal = 0;
        if (!_player.Playing) return;
        _player.Stop();
        _log($"声音：掐掉（{why}）");
    }

    /// 读文件，按扩展名交给对应的解码器。三条路都是引擎的静态工厂（<c>LoadFromBuffer</c>
    /// 还回一个新对象，读不通还回 null），所以这里没有错误码可记。<b>同一路径只读一次</b>：
    /// 一次休息要举几回遮罩，而那份文件不会自己变；读不出来也记着，别每一发都去敲一遍磁盘
    /// 再静音一遍。
    private AudioStream? Load(string path)
    {
        if (path == _path) return _stream;
        _path = path;
        _stream = null;
        _problem = null;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(Formats, ext) < 0)
        {
            _problem = $"认不出的格式，这一版会放 {string.Join(" / ", Formats)}";
            return null;
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            _problem = e.Message;
            return null;
        }

        _stream = ext switch
        {
            ".mp3" => AudioStreamMP3.LoadFromBuffer(bytes),
            ".ogg" => AudioStreamOggVorbis.LoadFromBuffer(bytes),
            _ => AudioStreamWav.LoadFromBuffer(bytes, new Godot.Collections.Dictionary()),
        };
        if (_stream == null) _problem = "解不开";
        return _stream;
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf When = new(
        () => "声音判据过了：step 要声 + 开关开着 + 有文件 + 落定了才响（afterLanding 关着就不等落定），" +
              "出厂那一条是等落定，解不开的格式要说而不是静音",
        "声音", SelfTest);

    /// 自检里那一发：<c>Plays</c> 那张真值表（四个条件）。播放本身要音频设备，不在自检里跑。
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        void Expect(string what, bool got, bool want)
        {
            if (got != want) problems.Add($"声音「{what}」读到了 {got}，该是 {want}");
        }

        // afterLanding 两头各摆一份：这一档 2026-09-27 才存在，而它是那一条 defect 的答案
        // （内置 todoList 第一步的 sound 是默认的 true，于是他从淡入那一刻就听见了）。
        var ready = new SoundOptions { Enabled = true, File = "alarm.mp3", AfterLanding = true };
        var fromStep = new SoundOptions { Enabled = true, File = "alarm.mp3", AfterLanding = false };
        var muted = new SoundOptions { Enabled = false, File = "alarm.mp3", AfterLanding = true };
        var nofile = new SoundOptions { Enabled = true, File = "", AfterLanding = true };

        Expect("step 要声、开关开着、有文件、已落定", Plays(new Step { Sound = true }, ready, true), true);
        Expect("step 不要声", Plays(new Step { Sound = false }, ready, true), false);
        Expect("总开关关着", Plays(new Step { Sound = true }, muted, true), false);
        Expect("文件还没填", Plays(new Step { Sound = true }, nofile, true), false);
        Expect("afterLanding 开着、斜坡还没走完", Plays(new Step { Sound = true }, ready, false), false);
        Expect("afterLanding 关着、斜坡还没走完（哪一步说要声，哪一步就响那一档）",
            Plays(new Step { Sound = true }, fromStep, false), true);
        Expect("出厂那一条是「落定之后才响」", new Settings().Sound.AfterLanding, true);
        Expect("格式认不出的那一档在表里", Array.IndexOf(Formats, ".mp3") >= 0, true);
        Expect("m4a 这一版解不开，得说而不是静音", Array.IndexOf(Formats, ".m4a") >= 0, false);
        return problems;
    }
}
