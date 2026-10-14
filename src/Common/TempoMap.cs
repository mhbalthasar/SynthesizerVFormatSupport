using SynthesizerVFormatSupport.Model;

namespace SynthesizerVFormatSupport.Common;

// BLICK ⇄ tick ⇄ 秒 的换算表。
//
// 三条轴各自的性质不同，混用就会错位：
//   · BLICK / tick 是**拍基**坐标（四分音符定长），与速度无关，换算比恒为 1,470,000；
//   · 秒是**速度相关**的，SVP 的音符级时间参数（tF0Offset / tF0Left / tF0VbrStart…）全是秒，
//     所以只有在 tempo 表上按位置取当前 BPM 才能落到拍轴上。
//
// BPM 取值口径与引擎 `TempoMap::bpm_at` 一致：取「位置 ≤ 查询点」的最后一个变速点，
// 变速点之前全部落在第一个标记上。
public sealed class TempoMap
{
    readonly long[] _positions;
    readonly double[] _bpms;

    public TempoMap(IReadOnlyList<SvpTempoMark> marks)
    {
        var ordered = (marks.Count == 0
            ? new List<SvpTempoMark> { new SvpTempoMark { Position = 0, Bpm = 120.0 } }
            : marks.OrderBy(m => m.Position).ToList());
        _positions = ordered.Select(m => m.Position).ToArray();
        _bpms = ordered.Select(m => m.Bpm > 0 ? m.Bpm : 120.0).ToArray();
        TempoMarks = ordered;
    }

    public IReadOnlyList<SvpTempoMark> TempoMarks { get; }

    public double BpmAt(long blick)
    {
        int index = 0;
        for (int i = 0; i < _positions.Length; i++)
        {
            if (_positions[i] > blick) break;
            index = i;
        }
        return _bpms[index];
    }

    /// 该 BPM 下 1 秒占多少 BLICK（= 每四分音符 BLICK × 每分钟四分音符数 ÷ 60）。
    public static double BlicksPerSecond(double bpm) => OpenCyanParams.BlickPerQuarter * bpm / 60.0;

    /// 从 <paramref name="atBlick"/> 起按当前 BPM 线性折算秒 → BLICK。
    /// 引擎的 `seconds_to_event_blick` 就是这么做的（音符内的秒参数不做跨变速点积分）。
    public long SecondsToBlick(double seconds, long atBlick)
        => (long)Math.Round(seconds * BlicksPerSecond(BpmAt(atBlick)));

    /// 跨变速点积分求两位置间的秒数（对应引擎 `TempoMap::seconds_between`）。
    public double SecondsBetween(long fromBlick, long toBlick)
    {
        if (toBlick <= fromBlick) return 0.0;
        double seconds = 0.0;
        long cursor = fromBlick;
        while (cursor < toBlick)
        {
            double bpm = BpmAt(cursor);
            long nextMark = _positions.FirstOrDefault(p => p > cursor);
            long stop = nextMark == 0 || nextMark > toBlick ? toBlick : nextMark;
            seconds += (stop - cursor) / BlicksPerSecond(bpm);
            cursor = stop;
        }
        return seconds;
    }

    // —— 拍轴换算：BLICK ⇄ tick ——
    public static double BlickToTick(double blick) => blick / OpenCyanParams.BlickPerTick;
    public static long TickToBlick(double tick) => (long)Math.Round(tick * OpenCyanParams.BlickPerTick);
}
