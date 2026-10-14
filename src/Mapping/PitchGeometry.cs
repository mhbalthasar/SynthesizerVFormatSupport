using SynthesizerVFormatSupport.Common;
using SynthesizerVFormatSupport.Model;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace SynthesizerVFormatSupport.Mapping;

/// SVP 的音高侧参数 → 宿主的两条音高通道 + 颤音对象。
///
/// 宿主给插件的是**两条分离**的通道（VoiceSynthesisSnapshotFactory.cs:84-90）：
///   · `Pitch`         = 纯用户绘制曲线（NaN = 该处没有值）
///   · `PitchDeviation` = 颤音偏移（由 part.Vibratos 冻结而成）
/// 而 OpenCyan 在 predict 模式下把两者相加后当成 SVP 的 `pitchDelta` 下发
/// （OpenCyanVoiceSession.cs:1009-1045）。⇒ SVP 的音高类参数都有确定的落点，不需要改插件：
///
///   SVP `parameters.pitchDelta`（音分）  → Pitch 曲线：Y = 覆盖该处的标称 MIDI + delta/100
///   SVP `notes[].detune`（音分）          → Pitch 曲线：同上，再叠加 detune/100
///   SVP `voice.transpose*`（音分/半音）    → Pitch 曲线：恒等叠加（引擎的轨道级移调同样作用在
///                                            DDS F0 之后，与 pitchDelta 同层，故可合并）
///   SVP 音符级手动颤音（dF0Vbr/fF0Vbr/…） → part.Vibratos（宿主自己按 sin 叠加，不重跑 DDS）
///   SVP `parameters.vibratoEnv` × dF0VbrMod → 宿主内置 VibratoEnvelope 轨（两边都是乘性包络）
///   SVP 音符级手动滑音（tF0Left/dF0Left…） → 见 BakeManualTransitions
public static class PitchGeometry
{
    /// <param name="includeOpenCyanChannels">false = 没装 OpenCyan：只落音高线（pitchDelta /
    /// detune / 轨道移调），颤音对象与 VibratoEnvelope 轨都跳过——它们靠宿主的 PitchDeviation
    /// 通道进引擎，没有该引擎时写进去就是无人消费的孤儿数据。</param>
    public static void Apply(SvpTrack track, IReadOnlyList<SvpNote> notes, TempoMap tempo,
                             ImportReport report, string owner, MidiPartInfo part,
                             bool includeOpenCyanChannels = true)
    {
        double globalTransposeCents = (track.MainRef.Voice.TransposeCents ?? 0)
                                    + (track.MainRef.Voice.TransposeSemitones ?? 0) * 100.0;

        var curve = track.MainGroup.Parameters.GetValueOrDefault("pitchDelta") ?? new SvpAutomation();
        BuildPitchCurve(track, notes, tempo, curve, globalTransposeCents, part, report, owner);
        if (!includeOpenCyanChannels) return;
        ApplyVibratoEnvelope(track, part, report, owner);
        ImportManualVibratos(track, notes, tempo, owner, part, report);
        BakeManualTransitions(track, notes, tempo, part, report, owner);
    }

    // ── Pitch 曲线 ──

    static void BuildPitchCurve(SvpTrack track, IReadOnlyList<SvpNote> notes, TempoMap tempo,
                                SvpAutomation curve, double globalTransposeCents,
                                MidiPartInfo part, ImportReport report, string owner)
    {
        bool hasDetune = notes.Any(n => n.Detune != 0);
        if (curve.Points.Count == 0 && !hasDetune && Math.Abs(globalTransposeCents) < 1e-9)
            return;

        // 采样位置 = 曲线原有锚点 ∪ 每颗音符的起止（标称音高在音符边界跳变，必须显式给点，
        // 否则宿主会在两颗音符之间插出一条不存在的斜线）。
        var positions = new SortedSet<long>();
        foreach (var point in curve.Points) positions.Add(point.Blick);
        if (curve.Points.Count > 0)
            for (int i = 1; i < curve.Points.Count; i++)
            {
                // 线性模式的锚点之间补中点：宿主是 Hermite，直线会被插成鼓包。
                if (curve.Mode != SvpInterpolationMode.Linear) break;
                long a = curve.Points[i - 1].Blick, b = curve.Points[i].Blick;
                positions.Add(a + (b - a) / 2);
            }
        foreach (var note in notes)
        {
            positions.Add(note.Onset);
            positions.Add(note.End);
        }

        long spanStart = curve.Points.Count > 0 ? curve.Points[0].Blick : long.MinValue;
        long spanEnd = curve.Points.Count > 0 ? curve.Points[^1].Blick : long.MaxValue;

        var points = new List<Point>();
        foreach (long blick in positions)
        {
            // 没有 delta 曲线时，只在「有 detune 的音符内」和「全曲移调」这两件事上给值：
            // 曲线处处覆盖会把引擎自己的音高（含滑音、语调）整条抹平，那是不可逆的破坏。
            if (curve.Points.Count == 0)
            {
                var note = FindNote(notes, blick);
                bool insideDetuned = note is not null && note.Detune != 0 && blick >= note.Onset && blick < note.End;
                if (!insideDetuned && Math.Abs(globalTransposeCents) < 1e-9) continue;
                if (Math.Abs(globalTransposeCents) < 1e-9 && blick >= note!.End) continue;
            }
            else if (blick < spanStart || blick > spanEnd)
            {
                continue;
            }

            double deltaCents = curve.Evaluate(blick, 0.0);
            double nominal = NominalPitchAt(notes, blick);
            if (nominal <= 0) continue;

            double detune = DetuneAt(notes, blick);
            double value = nominal + (deltaCents + detune + globalTransposeCents) / 100.0;
            points.Add(new Point(
                TempoMap.BlickToTick(blick + track.MainRef.BlickOffset),
                Math.Clamp(value, 0.0, OpenCyanParams.MaxMidiNote)));
        }

        if (points.Count == 0) return;
        if (points.Count == 1)
            points.Add(new Point(points[0].X + 1, points[0].Y));

        part.Pitch.Segments.Add(points);
        if (hasDetune || Math.Abs(globalTransposeCents) > 1e-9)
            report.Degrade("音分精度", owner, 1,
                "detune / 轨道移调已并入绘制的音高线；宿主按 MIDI 浮点存储，音分级精度低于原字段");
    }

    /// 标称音高：取覆盖该位置的音符；落在音符间隙时取**最近**的一颗（引擎在间隙里也用邻近音符的
    /// 标称值来定义 pitchDelta，间隙长度不计入滑音区）。
    static double NominalPitchAt(IReadOnlyList<SvpNote> notes, long blick)
    {
        if (notes.Count == 0) return 0;
        int index = SearchLowerBound(notes, blick);
        if (index < notes.Count && notes[index].Onset <= blick && blick < notes[index].End)
            return notes[index].Pitch;
        int previous = index - 1;
        if (previous < 0) return index < notes.Count ? notes[index].Pitch : 0;
        if (index >= notes.Count) return notes[previous].Pitch;
        return blick - notes[previous].End <= notes[index].Onset - blick
            ? notes[previous].Pitch
            : notes[index].Pitch;
    }

    static double DetuneAt(IReadOnlyList<SvpNote> notes, long blick)
    {
        var note = FindNote(notes, blick);
        return note is not null && blick >= note.Onset && blick < note.End ? note.Detune : 0;
    }

    static SvpNote? FindNote(IReadOnlyList<SvpNote> notes, long blick)
    {
        int index = SearchLowerBound(notes, blick);
        if (index < notes.Count && notes[index].Onset <= blick && blick < notes[index].End)
            return notes[index];
        return null;
    }

    /// 返回第一颗 onset > blick 的下标（notes 已按 onset 升序，含重叠时不保证唯一）。
    static int SearchLowerBound(IReadOnlyList<SvpNote> notes, long blick)
    {
        int low = 0, high = notes.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (notes[mid].Onset <= blick) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    // ── 颤音包络：vibratoEnv × dF0VbrMod → 宿主 VibratoEnvelope 轨 ──

    static void ApplyVibratoEnvelope(SvpTrack track, MidiPartInfo part, ImportReport report, string owner)
    {
        var curve = track.MainGroup.Parameters.GetValueOrDefault("vibratoEnv");
        double modulation = track.MainRef.Voice.DF0VbrMod ?? 1.0;
        if (curve is null || curve.Points.Count == 0)
        {
            if (Math.Abs(modulation - 1.0) < 1e-12) return;
            part.Automations[OpenCyanParams.KeyVibratoEnvelope] = new AutomationInfo
            {
                DefaultValue = Math.Clamp(modulation, OpenCyanParams.VibratoEnvelopeMin, OpenCyanParams.VibratoEnvelopeMax),
            };
            return;
        }

        var automation = new AutomationInfo { DefaultValue = modulation };
        int clamped = 0;
        foreach (var point in curve.Points)
        {
            double value = modulation * point.Value;
            if (value is < OpenCyanParams.VibratoEnvelopeMin or > OpenCyanParams.VibratoEnvelopeMax)
            {
                value = Math.Clamp(value, OpenCyanParams.VibratoEnvelopeMin, OpenCyanParams.VibratoEnvelopeMax);
                clamped++;
            }
            automation.Points.Add(new Point(TempoMap.BlickToTick(point.Blick + track.MainRef.BlickOffset), value));
        }
        if (clamped > 0)
            report.Degrade("量程钳位", owner, clamped,
                $"vibratoEnv × dF0VbrMod 有 {clamped} 点超出宿主 VibratoEnvelope 量程 [0, 2]，已钳位");
        part.Automations[OpenCyanParams.KeyVibratoEnvelope] = automation;
    }

    // ── 手动颤音 → part.Vibratos ──

    /// SVP 的手动颤音公式（manual_pitch.rs:614-620，13338 `0x30F150` 的直译）：
    ///
    ///   `sin(2π·f·t + φ) · min(1, t/left, (end−t)/right) · depth · 0.5`
    ///
    /// 宿主 VibratoMath 是 `env · Amplitude · cubicFade · sin(2π·f·t − Phase·π)`，
    /// 于是逐项对位：峰值 = depth×0.5 半音、频率原值、相位取负后 ÷π（宿主在角频率里做减法），
    /// 淡入淡出走 Attack/Release（宿主用三次缓入、原版用线性 min()，形状略有差别 ⇒ 记降级）。
    static void ImportManualVibratos(SvpTrack track, IReadOnlyList<SvpNote> notes, TempoMap tempo,
                                     string owner, MidiPartInfo part, ImportReport report)
    {
        var voice = track.MainRef.Voice;
        int imported = 0;
        foreach (var note in notes)
        {
            if (note.InstantMode) continue;   // 自动音高的颤音由引擎自己生成（predict）

            double depth = note.Attributes.DF0Vbr ?? voice.DF0Vbr ?? DefaultVibratoDepthSemitones;
            double frequency = note.Attributes.FF0Vbr ?? voice.FF0Vbr ?? DefaultVibratoFrequencyHz;
            if (depth == 0 || frequency == 0) continue;

            double startSeconds = note.Attributes.TF0VbrStart ?? voice.TF0VbrStart ?? DefaultVibratoStartSeconds;
            double attack = note.Attributes.TF0VbrLeft ?? voice.TF0VbrLeft ?? DefaultVibratoFadeSeconds;
            double release = note.Attributes.TF0VbrRight ?? voice.TF0VbrRight ?? DefaultVibratoFadeSeconds;
            double phaseRadians = note.Attributes.PF0Vbr ?? 0;

            long startBlick = note.Onset + tempo.SecondsToBlick(Math.Max(startSeconds, 0), note.Onset);
            long endBlick = note.End;
            if (startBlick >= endBlick) continue;   // 颤音起点落在音符之外 = 原版也不发声


            imported++;
            part.Vibratos.Add(new VibratoInfo
            {
                Pos = TempoMap.BlickToTick(startBlick + track.MainRef.BlickOffset),
                Dur = Math.Max(TempoMap.BlickToTick(endBlick - startBlick), 1),
                Frequency = frequency,
                Amplitude = depth * 0.5,
                // 宿主 sin 里是 `− Phase·π`、原版是 `+ φ` ⇒ 相位取负换算，否则颤音起步方向相反。
                Phase = -phaseRadians / Math.PI,
                Attack = attack,
                Release = release,
            });
        }

        if (imported > 0)
            report.Degrade("颤音淡变", owner, imported,
                "淡入淡出改走宿主 cubic 缓入（原版是线性 min()），包络形状有轻微差别");
    }

    // 原版手动颤音参数的内置默认（manual_pitch.rs:80-85）。
    const double DefaultVibratoStartSeconds = 0.25;
    const double DefaultVibratoFadeSeconds = 0.20;
    const double DefaultVibratoDepthSemitones = 1.0;
    const double DefaultVibratoFrequencyHz = 5.5;

    /// 手动滑音**只记账、不烘焙**（用户确认的取舍）。
    ///
    /// 不是偷懒：引擎算的是差值 `cents = (effective − system) × blend × 100`
    /// （manual_pitch.rs:199），`system` 来自 `notes[].systemAttributes` 里 DDS 自动音高的基线
    /// 快照，缺字段就直接报错（`:932-948`）。语料里 14 份有 7 份根本没存这套基线
    /// （systemAttributes 只剩 evenSyllableDuration），「用户改了什么」在文件里没有答案 ⇒
    /// 离线逐位还原在原理上不可能。与其拿面板默认值凑一条看起来对的曲线（听感对不上时
    /// 没有任何可观测信号），不如把字段原样记进清单，交给 predict 让引擎自己生成。
    static void BakeManualTransitions(SvpTrack track, IReadOnlyList<SvpNote> notes, TempoMap tempo,
                                       MidiPartInfo part, ImportReport report, string owner)
    {
        int count = notes.Count(n => !n.InstantMode && HasTransition(n));
        if (count == 0) return;
        bool hasBaseline = notes.Any(n => n.SystemAttributes.TF0Offset is not null);
        report.Degrade("手动滑音", owner, count,
            "tF0Offset / tF0Left / tF0Right / dF0Left / dF0Right 未烘焙进音高线，音高交由引擎 predict。"
            + (hasBaseline
                ? "（本工程带 systemAttributes 基线，将来若完整移植 PitchModel 可逐位还原）"
                : "（本工程连 systemAttributes 基线都没存，任何还原方式都只能是近似）"));
    }

    static bool HasTransition(SvpNote note)
    {
        var a = note.Attributes;
        return a.TF0Offset is { } o && o != 0
            || a.TF0Left is not null || a.TF0Right is not null
            || a.DF0Left is not null || a.DF0Right is not null;
    }

}
