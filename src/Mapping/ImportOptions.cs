using SynthesizerVFormatSupport.Common;
using TuneLab.Foundation;

namespace SynthesizerVFormatSupport.Mapping;

/// 导入侧的用户配置（设置面板落盘后回喂）。
///
/// 只有音高线的两个判定阈值需要用户调：`pitchDelta` 是**相对引擎自动音高**的偏差，
/// 哪些锚点算「用户编辑过」、多长的零偏差空档要把曲线切成两段，都没有唯一的正确答案，
/// 取决于工程里的编辑密度。默认值（1 音分 / 1 个四分音符）对语料里的真实工程都成立。
public sealed class ImportOptions
{
    public const string KeyPitchEditEpsilonCents = "import_pitch_edit_epsilon_cents";
    public const string KeyZeroGapSplitQuarters = "import_zero_gap_split_quarters";

    public const double DefaultPitchEditEpsilonCents = 1.0;
    public const double DefaultZeroGapSplitQuarters = 1.0;

    /// 偏差绝对值不超过该值（音分）的 `pitchDelta` 锚点视为「没有编辑」，不产出音高线。
    /// 0 = 只有精确的 0 才算没编辑。
    public double PitchEditEpsilonCents { get; set; } = DefaultPitchEditEpsilonCents;

    /// 两段编辑之间的零偏差空档达到该长度（四分音符）就切成两段独立折线；
    /// 段间留空（NaN），交回引擎自行 predict。0 = 见到零空档就断。
    public double ZeroGapSplitQuarters { get; set; } = DefaultZeroGapSplitQuarters;

    // 控件量程即合法域：手改过的设置文件也不该把扫描窗口撑成天文数字。
    public const double MinPitchEditEpsilonCents = 0.0;
    public const double MaxPitchEditEpsilonCents = 100.0;
    public const double MinZeroGapSplitQuarters = 0.0;
    public const double MaxZeroGapSplitQuarters = 64.0;

    public static ImportOptions FromSettings(PropertyObject settings) => new()
    {
        PitchEditEpsilonCents = Clamp(
            settings.GetDouble(KeyPitchEditEpsilonCents, DefaultPitchEditEpsilonCents),
            DefaultPitchEditEpsilonCents, MinPitchEditEpsilonCents, MaxPitchEditEpsilonCents),
        ZeroGapSplitQuarters = Clamp(
            settings.GetDouble(KeyZeroGapSplitQuarters, DefaultZeroGapSplitQuarters),
            DefaultZeroGapSplitQuarters, MinZeroGapSplitQuarters, MaxZeroGapSplitQuarters),
    };

    /// 零偏差空档的断段阈值（BLICK）。钳过 [0, 64] 后不会溢出。
    public long ZeroGapSplitBlick => (long)Math.Round(ZeroGapSplitQuarters * OpenCyanParams.BlickPerQuarter);

    static double Clamp(double value, double fallback, double min, double max)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
