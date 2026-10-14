using System.Security.Cryptography;
using System.Text;

namespace SynthesizerVFormatSupport.Common;

// OpenCyan 参数体系的**手抄镜像**（不引用 OpenCyanForTuneLab / OpenCyan.Net 程序集，
// 与 VocaloidFormatSupport 的 DaisiumParams 同一做法：格式包只依赖 TuneLab.SDK）。
//
// 真相源：
//   · 轨键 / 量程 / 换算  → libOpenCyan\wrapper\OpenCyan.Net\EngineConstants.cs 的 TimbreTracks
//   · note/part 属性键    → OpenCyanForTuneLab\Engine\Declarations.cs + EngineConstants
//   · 声库身份键          → OpenCyanForTuneLab\Engine\VoicebankNaming.Key
// 上游改任何一条，这里必须同步；不同步的后果是**静默画错曲线**（量程/中性点对不上时
// 「画一条默认平线」也会改变音色），所以每条 spec 都自带中性值。
public static class OpenCyanParams
{
    public const string EngineType = "OpenCyan";

    // —— 时间基 ——
    // SVP：705,600,000 BLICK = 一个四分音符；TuneLab：480 tick = 一个四分音符。
    // ⇒ 1 tick = 1,470,000 BLICK（十六分音符 = 30 tick = 44,100,000 BLICK，整除 ⇒ 常规时值无损）。
    public const double BlickPerQuarter = 705_600_000.0;
    public const double TicksPerQuarter = 480.0;
    public const double BlickPerTick = BlickPerQuarter / TicksPerQuarter;

    // —— 音色参数轨：宿主键 ⇄ SVP 曲线名 ——
    // Map = 宿主轨值 → 引擎参数值；Inverse = 引擎参数值 → 宿主轨值（导入方向用 Inverse）。
    // 约束：Map(Default) 必须落在引擎中性值上（voicing 中性 1.0、growlRate 中性 0.5，其余为 0）。
    public sealed record TimbreTrack(string KeyId, string EngineName, double Min, double Max, double Default,
                                     double Neutral, Func<double, double> Map, Func<double, double> Inverse)
    {
        /// 引擎值 → 宿主值并钳进量程；第二个返回值标记是否发生了钳位（钳位要进警告清单）。
        public (double Value, bool Clamped) ToHost(double engineValue)
        {
            double raw = Inverse(engineValue);
            double clamped = Math.Clamp(raw, Min, Max);
            return (clamped, clamped != raw);
        }

        public double ToEngine(double hostValue) => Map(Math.Clamp(hostValue, Min, Max));
    }

    public static readonly TimbreTrack[] TimbreTracks =
    [
        // DYN 满偏 ±1 = ±12 dB（SVP 的 loudness 曲线单位就是 dB）。
        new("DYN", "loudness", -1, 1, 0, 0, v => v * 12.0, e => e / 12.0),
        // 键名就是字面 "tension"（不是 Daisium 的 formanttns / TNS）。
        new("tension", "tension", -1, 1, 0, 0, v => v, e => e),
        new("BRE", "breathiness", 0, 1, 0, 0, v => v, e => e),
        // 引擎中性 1.0 而非 0 ⇒ 换算带 +1 偏移；按宿主默认 0 画平线会把声门闭合压到底。
        new("VOI", "voicing", -1, 1, 0, 1.0, v => 1.0 + v, e => e - 1.0),
        new("GEN", "gender", -1, 1, 0, 0, v => v, e => e),
        new("GWL", "growl", 0, 1, 0, 0, v => v, e => e),
        new("GWR", "growlRate", 0, 1, 0.5, 0.5, v => v, e => e),
        // 宿主单位 = 半音，SVP toneShift 单位 = 音分 ⇒ ×100。
        new("STS", "toneShift", -12, 12, 0, 0, v => v * 100.0, e => e / 100.0),
        new("DIS", "distortion", 0, 1, 0, 0, v => v, e => e),
        new("ASP", "aspiration", -1, 1, 0, 0, v => v, e => e),
    ];

    // 弯音是宿主 DAW 通道，SVP 侧没有对应曲线（引擎不认识它们，折算在叠算层做）。
    public const string KeyPitchBend = "PIT";
    public const string KeyPitchBendSens = "PBS";

    /// 各 SVP 参数曲线的中性值（空曲线 / 无覆盖时引擎取的默认）。vibratoEnv 是乘性包络 ⇒ 中性 1.0。
    public static double NeutralOf(string engineName) => engineName switch
    {
        "voicing" => 1.0,
        "growlRate" => 0.5,
        "vibratoEnv" => 1.0,
        _ => 0.0,
    };

    /// SVP 参数名 → 音色轨。pitchDelta / vibratoEnv 不是音色轨，返回 null 由调用方单独处理。
    public static TimbreTrack? TrackFor(string engineName) =>
        TimbreTracks.FirstOrDefault(t => t.EngineName == engineName);

    // 宿主 NoteInfo.Pitch / Pitch 曲线的值域（0..127 MIDI）。
    public const double MaxMidiNote = 127.0;

    /// bend(半音) = floor(PBS) × PIT（PIT 已归一化到 ±1）。
    public static double PitchBendSemitones(double pit, double pbs) => Math.Floor(pbs) * pit;

    // —— 宿主内置的颤音包络轨（ConstantDefine.VibratoEnvelopeID）——
    // 量程 0..2、默认 1，与 SVP `vibratoEnv`（中性 1.0 的乘性包络）同形：
    // 宿主在 VibratoMath 里做 `envelope × Amplitude`，引擎做 `dF0VbrMod × vibratoEnv − 1` 进 DDS，
    // 两边都是「乘在颤音深度上的包络」，所以 vibratoEnv 可以整条平移进这条轨，
    // 只需把轨道级 dF0VbrMod 乘进去。
    public const string KeyVibratoEnvelope = "VibratoEnvelope";
    public const double VibratoEnvelopeMin = 0.0;
    public const double VibratoEnvelopeMax = 2.0;
    public const double VibratoEnvelopeDefault = 1.0;

    // —— XSY 音色混合（part 属性：变长列表 + 逐条自动化轨）——
    public const string KeyXsyMix = "xsy_mix";      // part 属性：变长列表
    public const string KeyXsyVoice = "voice";      // 元素字段：目标声库 id（空串 = 本声库 ⇒ 库内混合）
    public const string KeyXsyStyle = "style";      // 元素字段：目标音色名
    public const string XsyTrackIdPrefix = "XSY:";  // 逐条混合比自动化轨的键前缀

    public static string XsyTrackKey(string salt) => XsyTrackIdPrefix + salt;

    /// 库内混合条目的轨 salt：md5("S" + 声库 id + ␟ + 版本 + ␟ + 音色名) 前 8 字节（小写 hex）。
    /// 与插件 XsyTracks.HashIdentity 逐字同口径：通道标记、版本 trim、音色名不 trim 都得一致，
    /// 否则导入生成的轨键对不上声明面的轨键，**曲线会静默不生效**（轨存在但没人读）。
    public static string XsySalt(string bankId, string bankVersion, string style)
    {
        const char Separator = '\u001f';
        var identity = "S" + bankId + Separator + bankVersion.Trim() + Separator + style;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    // —— note 属性键（⚠️ 宿主工程序列化引用，改名即失配）——
    public const string KeyNoteLanguage = "language";          // 音符级语言覆盖
    public const string KeyNotePhonemeStr = "phoneme_str";     // 显式音素，跳过 G2P
    public const string KeyNoteMusicalType = "musical_type";   // "singing" / "rap"
    public const string KeyNoteAccent = "accent";              // "1".."5"，仅 rap + 普通话
    public const string KeyNoteRTone = "r_tone";               // ±1，0 = 不下发
    public const string KeyNoteRIntonation = "r_intonation";   // ±1，0 = 不下发

    // —— part 属性键 ——
    public const string KeyPartLanguage = "Language";          // part 级语言
    public const string KeyPartVocalMode = "vocal_mode";       // 单选声线（StyleBook 名）
    public const string KeyPartPitchMode = "PitchMode";        // predict / tunelab / capture
    public const string PitchModePredict = "predict";

    /// 音素锁定标记：歌词以 `.` 开头即「本音符音素已手工指定」。插件按这个前缀判，
    /// 引擎只看 SVP `notes[].phonemes` 非空。导入时给了显式音素就必须补这个标记，
    /// 否则属性里的音素会被宿主侧忽略。
    public const char PhonemeLockMarker = '.';

    /// 歌词延续标记：SVP 用 "-" 表示「延续前一个发音」，OpenCyan 侧同样认 "-"。
    public const string ContinuationLyric = "-";

    // —— CLF 支持的语言 ⇄ 音素集配对（只给一个 = UnsupportedEnvironment）——
    public static readonly (string Language, string Phoneset)[] ClfLanguages =
    [
        ("japanese", "romaji"),
        ("english", "arpabet"),
        ("mandarin", "xsampa"),
        ("cantonese", "xsampa"),
        ("spanish", "xsampa"),
    ];

    public static bool IsClfLanguage(string? language) =>
        ClfLanguages.Any(c => c.Language == language);

    public static string? PhonesetFor(string? language) =>
        ClfLanguages.FirstOrDefault(c => c.Language == language).Phoneset;

    /// 声库身份键：sha256("opencyan␟name␟version␟language") 前 16 个十六进制位（小写）。
    /// 与 OpenCyanForTuneLab\Engine\VoicebankNaming.Key 逐字同口径——分隔符 U+001F、三段 Trim、
    /// 语言进键（同名同版本不同语言是两个独立可选声库）。任一细节不同就会算出另一个键，
    /// 工程打开时显示「找不到声库」。
    public static string VoicebankKey(string? name, string? version, string? language)
    {
        const char Separator = '\u001f';
        var material = string.Join(Separator, "opencyan", (name ?? "").Trim(), (version ?? "").Trim(), (language ?? "").Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }
}
