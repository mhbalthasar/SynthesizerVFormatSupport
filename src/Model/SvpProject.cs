using System.Text.Json;
using System.Text.Json.Serialization;

namespace SynthesizerVFormatSupport.Model;

// SVP（Synthesizer V Studio 工程）的强类型模型，字段形态逐条对齐
// `libOpenCyan/src/svp/src/model/*.rs`（那是当前唯一被验证过能吃真实工程的解析器）。
//
// 三条容易踩死的形态约定：
//   · `version` 是整数，写成字符串会报 expected i64；
//   · `points` 是扁平交替数组（见 SvpAutomation），不是对象数组；
//   · 文件结尾是 JSON 字节 + **单个**尾部 NUL。
//
// 可空字段 = 「文件里没有这个 tag」，与「显式写了 0」必须区分：引擎对后者的处理不是取默认值
// （例如 rTone 显式 0 会把音高往中性拽，而缺字段是该段完全不参与）。
public sealed class SvpProject
{
    [JsonPropertyName("version")]
    public long Version { get; set; } = 153;

    [JsonPropertyName("time")]
    public SvpTimeAxis Time { get; set; } = new();

    [JsonPropertyName("library")]
    public List<SvpNoteGroup> Library { get; set; } = new();

    [JsonPropertyName("tracks")]
    public List<SvpTrack> Tracks { get; set; } = new();

    [JsonPropertyName("renderConfig")]
    public SvpRenderConfig? RenderConfig { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpTimeAxis
{
    [JsonPropertyName("meter")]
    public List<SvpMeterMark> Meter { get; set; } = new();

    [JsonPropertyName("tempo")]
    public List<SvpTempoMark> Tempo { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpTempoMark
{
    [JsonPropertyName("position")]
    public long Position { get; set; }

    [JsonPropertyName("bpm")]
    public double Bpm { get; set; } = 120.0;

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpMeterMark
{
    [JsonPropertyName("index")]
    public long Index { get; set; }

    [JsonPropertyName("numerator")]
    public int Numerator { get; set; } = 4;

    [JsonPropertyName("denominator")]
    public int Denominator { get; set; } = 4;

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpRenderConfig
{
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("numChannels")]
    public int NumChannels { get; set; } = 1;

    [JsonPropertyName("aspirationFormat")]
    public string AspirationFormat { get; set; } = "asIsolatedFiles";

    [JsonPropertyName("bitDepth")]
    public int BitDepth { get; set; } = 16;

    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; set; } = 44100;

    [JsonPropertyName("exportMixDown")]
    public bool ExportMixDown { get; set; }

    [JsonPropertyName("exportPitch")]
    public bool ExportPitch { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpTrack
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("dispColor")]
    public string DispColor { get; set; } = "";

    [JsonPropertyName("dispOrder")]
    public long DispOrder { get; set; }

    [JsonPropertyName("renderEnabled")]
    public bool RenderEnabled { get; set; } = true;

    [JsonPropertyName("mixer")]
    public SvpMixer Mixer { get; set; } = new();

    /// 本轨的音符与曲线都在这里（时间坐标 = 本组本地 BLICK，绝对位置再加 mainRef.blickOffset）。
    [JsonPropertyName("mainGroup")]
    public SvpNoteGroup MainGroup { get; set; } = new();

    [JsonPropertyName("mainRef")]
    public SvpGroupReference MainRef { get; set; } = new();

    /// 指向 <see cref="SvpProject.Library"/> 里某个 group 的引用；引擎展开时按 blickOffset / pitchOffset 平移。
    [JsonPropertyName("groups")]
    public List<SvpGroupReference> Groups { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpMixer
{
    [JsonPropertyName("gainDecibel")]
    public double GainDecibel { get; set; }

    [JsonPropertyName("pan")]
    public double Pan { get; set; }

    [JsonPropertyName("mute")]
    public bool Mute { get; set; }

    [JsonPropertyName("solo")]
    public bool Solo { get; set; }

    [JsonPropertyName("display")]
    public bool Display { get; set; } = true;

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpNoteGroup
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    /// 键 = 参数轨名（pitchDelta / loudness / tension / breathiness / voicing / gender / toneShift / vibratoEnv，
    /// 以及 OpenCyan 扩展的 growl / growlRate / distortion / aspiration）。用字典而非固定字段：
    /// 未知轨名要能原样读出来并在报告里点名，而不是静默丢掉。
    [JsonPropertyName("parameters")]
    public Dictionary<string, SvpAutomation> Parameters { get; set; } = new();

    /// Vocal Mode 名 → 逐帧强度曲线。旧版 SVP 没有这个对象，缺失与空等价。
    [JsonPropertyName("vocalModes")]
    public Dictionary<string, SvpAutomation> VocalModes { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<SvpNote> Notes { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpGroupReference
{
    // 原始 tag 是 `groupID`（两个字母都大写），不能靠 camelCase 策略推导。
    [JsonPropertyName("groupID")]
    public string GroupId { get; set; } = "";

    /// -1 等特殊值原样保留。
    [JsonPropertyName("blickAbsoluteBegin")]
    public long? BlickAbsoluteBegin { get; set; }

    [JsonPropertyName("blickAbsoluteEnd")]
    public long? BlickAbsoluteEnd { get; set; }

    [JsonPropertyName("blickOffset")]
    public long BlickOffset { get; set; }

    [JsonPropertyName("pitchOffset")]
    public int PitchOffset { get; set; }

    [JsonPropertyName("isInstrumental")]
    public bool IsInstrumental { get; set; }

    /// 模型派生的缓存曲线，不是用户画的；不参与导入。
    [JsonPropertyName("systemPitchDelta")]
    public SvpAutomation SystemPitchDelta { get; set; } = new();

    [JsonPropertyName("database")]
    public SvpDatabase Database { get; set; } = new();

    [JsonPropertyName("dictionary")]
    public string Dictionary { get; set; } = "";

    [JsonPropertyName("voice")]
    public SvpVoice Voice { get; set; } = new();

    [JsonPropertyName("pitchTakes")]
    public SvpTakeSet PitchTakes { get; set; } = SvpTakeSet.NeutralPlaceholder();

    [JsonPropertyName("timbreTakes")]
    public SvpTakeSet TimbreTakes { get; set; } = SvpTakeSet.NeutralPlaceholder();

    /// 伴奏文件引用；只在 isInstrumental 的轨道上出现。
    [JsonPropertyName("audio")]
    public SvpAudioReference? Audio { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpAudioReference
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpDatabase
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("phoneset")]
    public string Phoneset { get; set; } = "";

    [JsonPropertyName("languageOverride")]
    public string LanguageOverride { get; set; } = "";

    [JsonPropertyName("phonesetOverride")]
    public string PhonesetOverride { get; set; } = "";

    /// "SVR2AI" = AI 声库；"SVR2Standard" = v1 Standard 声库。OpenCyan 只吃 AI。
    [JsonPropertyName("backendType")]
    public string BackendType { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "100";

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpVoice
{
    [JsonPropertyName("tF0Left")]
    public double? TF0Left { get; set; }

    [JsonPropertyName("tF0Right")]
    public double? TF0Right { get; set; }

    [JsonPropertyName("dF0Left")]
    public double? DF0Left { get; set; }

    [JsonPropertyName("dF0Right")]
    public double? DF0Right { get; set; }

    [JsonPropertyName("tF0VbrStart")]
    public double? TF0VbrStart { get; set; }

    [JsonPropertyName("tF0VbrLeft")]
    public double? TF0VbrLeft { get; set; }

    [JsonPropertyName("tF0VbrRight")]
    public double? TF0VbrRight { get; set; }

    /// 颤音深度，单位 = 半音。
    [JsonPropertyName("dF0Vbr")]
    public double? DF0Vbr { get; set; }

    /// 颤音深度的轨道级倍率（与 vibratoEnv 曲线相乘后进 DDS）；缺失 = 1.0。
    [JsonPropertyName("dF0VbrMod")]
    public double? DF0VbrMod { get; set; }

    /// 颤音频率，单位 = Hz。
    [JsonPropertyName("fF0Vbr")]
    public double? FF0Vbr { get; set; }

    [JsonPropertyName("paramBreathiness")]
    public double? ParamBreathiness { get; set; }

    [JsonPropertyName("paramGender")]
    public double? ParamGender { get; set; }

    [JsonPropertyName("paramLoudness")]
    public double? ParamLoudness { get; set; }

    [JsonPropertyName("paramTension")]
    public double? ParamTension { get; set; }

    /// 单位 = 音分（不是半音）。
    [JsonPropertyName("paramToneShift")]
    public double? ParamToneShift { get; set; }

    [JsonPropertyName("renderMode")]
    public string? RenderMode { get; set; }

    [JsonPropertyName("transposeCents")]
    public double? TransposeCents { get; set; }

    [JsonPropertyName("transposeSemitones")]
    public double? TransposeSemitones { get; set; }

    /// 原版把布尔覆盖存成字符串 "true"/"false"；缺失 = 默认开启，显式 "false" 才是关闭。
    [JsonPropertyName("improviseAttackRelease")]
    public string? ImproviseAttackRelease { get; set; }

    [JsonPropertyName("vocalModeInherited")]
    public bool VocalModeInherited { get; set; } = true;

    [JsonPropertyName("vocalModePreset")]
    public string VocalModePreset { get; set; } = "";

    /// Vocal Mode 名 → 权重，**界面百分数**（进引擎前 ÷100）。
    [JsonPropertyName("vocalModeParams")]
    public Dictionary<string, double> VocalModeParams { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpNote
{
    /// "singing" / "rap"，小写字符串——不是数字。
    [JsonPropertyName("musicalType")]
    public string MusicalType { get; set; } = "singing";

    [JsonPropertyName("onset")]
    public long Onset { get; set; }

    [JsonPropertyName("duration")]
    public long Duration { get; set; }

    [JsonPropertyName("lyrics")]
    public string Lyrics { get; set; } = "";

    /// 空格分隔的显式音素；空串 = 交给 G2P 自动发音。
    [JsonPropertyName("phonemes")]
    public string Phonemes { get; set; } = "";

    [JsonPropertyName("accent")]
    public string Accent { get; set; } = "";

    [JsonPropertyName("pitch")]
    public int Pitch { get; set; }

    /// 音分。
    [JsonPropertyName("detune")]
    public int Detune { get; set; }

    /// true = 自动音高（引擎生成，基线在 systemAttributes）；false = 手动（用户参数生效）。
    [JsonPropertyName("instantMode")]
    public bool InstantMode { get; set; } = true;

    [JsonPropertyName("attributes")]
    public SvpNoteAttributes Attributes { get; set; } = new();

    [JsonPropertyName("systemAttributes")]
    public SvpNoteAttributes SystemAttributes { get; set; } = new();

    [JsonPropertyName("pitchTakes")]
    public SvpTakeSet PitchTakes { get; set; } = SvpTakeSet.NeutralPlaceholder();

    [JsonPropertyName("timbreTakes")]
    public SvpTakeSet TimbreTakes { get; set; } = SvpTakeSet.NeutralPlaceholder();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public long End => Onset + Duration;
}

/// 音符属性。`attributes` 与 `systemAttributes` 共用同一套字段名：前者是用户显式值，
/// 后者是引擎自动音高派生结果的缓存基线——两者必须分开保存，同名字段不能互相覆盖。
public sealed class SvpNoteAttributes
{
    [JsonPropertyName("evenSyllableDuration")]
    public bool? EvenSyllableDuration { get; set; }

    /// 逐音素的 ALT 组索引（0/1/2）。
    [JsonPropertyName("alt")]
    public List<int>? Alt { get; set; }

    /// 逐音素时长倍率（相对值，区间总长不变）。
    [JsonPropertyName("dur")]
    public List<double>? Dur { get; set; }

    /// OpenCyan 扩展：逐音素绝对占用秒数。
    [JsonPropertyName("phonemeSeconds")]
    public List<double>? PhonemeSeconds { get; set; }

    [JsonPropertyName("strength")]
    public List<double>? Strength { get; set; }

    [JsonPropertyName("tNoteOffset")]
    public double? TNoteOffset { get; set; }

    [JsonPropertyName("rTone")]
    public double? RTone { get; set; }

    [JsonPropertyName("rIntonation")]
    public double? RIntonation { get; set; }

    [JsonPropertyName("languageOverride")]
    public string? LanguageOverride { get; set; }

    [JsonPropertyName("phonesetOverride")]
    public string? PhonesetOverride { get; set; }

    /// 原版的「表情组」名（形如 "A4"/"D#4"）；OpenCyan 侧没有对应通道。
    [JsonPropertyName("exprGroup")]
    public string? ExprGroup { get; set; }

    /// 音符音高锚点的时间偏移，单位 = 秒。
    [JsonPropertyName("tF0Offset")]
    public double? TF0Offset { get; set; }

    /// 滑音区宽度，单位 = 秒。
    [JsonPropertyName("tF0Left")]
    public double? TF0Left { get; set; }

    [JsonPropertyName("tF0Right")]
    public double? TF0Right { get; set; }

    /// 滑音深度，单位 = 半音。
    [JsonPropertyName("dF0Left")]
    public double? DF0Left { get; set; }

    [JsonPropertyName("dF0Right")]
    public double? DF0Right { get; set; }

    /// 颤音起点相对音符 onset 的延迟，单位 = 秒。
    [JsonPropertyName("tF0VbrStart")]
    public double? TF0VbrStart { get; set; }

    /// 颤音淡入 / 淡出时长，单位 = 秒。
    [JsonPropertyName("tF0VbrLeft")]
    public double? TF0VbrLeft { get; set; }

    [JsonPropertyName("tF0VbrRight")]
    public double? TF0VbrRight { get; set; }

    /// 颤音深度 = 半音；频率 = Hz；相位 = 弧度。
    [JsonPropertyName("dF0Vbr")]
    public double? DF0Vbr { get; set; }

    [JsonPropertyName("fF0Vbr")]
    public double? FF0Vbr { get; set; }

    [JsonPropertyName("pF0Vbr")]
    public double? PF0Vbr { get; set; }

    [JsonPropertyName("dF0Jitter")]
    public double? DF0Jitter { get; set; }

    /// 引擎侧未建模的 tag（例如真实文件里出现的音符级 dF0VbrMod）原样留下，供报告点名。
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SvpTakeSet
{
    [JsonPropertyName("activeTakeId")]
    public long ActiveTakeId { get; set; }

    [JsonPropertyName("takes")]
    public List<SvpTake> Takes { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// 原版「没生成额外 take」的占位编码：单条 id=0、activeTakeId=0、liked=false。
    public static SvpTakeSet NeutralPlaceholder() => new()
    {
        ActiveTakeId = 0,
        Takes = new List<SvpTake> { new SvpTake { Id = 0, Expr = 0.0, Liked = false } },
    };

    /// 引擎只认这条占位（version 129 的 expr 是 1、153 是 0，其余一律「语义尚未实现」）。
    [JsonIgnore]
    public bool IsNeutralPlaceholder =>
        ActiveTakeId == 0
        && Takes.Count == 1
        && Takes[0].Id == 0
        && (Takes[0].Expr == 0.0 || Takes[0].Expr == 1.0)
        && !Takes[0].Liked;
}

public sealed class SvpTake
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("expr")]
    public double Expr { get; set; }

    [JsonPropertyName("liked")]
    public bool Liked { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
