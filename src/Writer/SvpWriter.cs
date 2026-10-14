using System.Text.Json;
using SynthesizerVFormatSupport.Common;
using SynthesizerVFormatSupport.Model;

namespace SynthesizerVFormatSupport.Writer;

/// 强类型模型 → `.svp`。
///
/// 落盘形态照 `SvpDocument` 的写侧约定：紧凑 JSON + **单个**尾部 NUL。
/// 只序列化非 null 的可空字段（null = 「文件里没有这个 tag」），因为原版对
/// 「缺字段」与「显式 0」的处理不同（例如 rTone 给 0 会真的施加一个往中性拽的力）。
public static class SvpWriter
{
    public static void Write(Stream output, SvpProject project)
    {
        var options = new JsonSerializerOptions(SvpJson.Write);
        // 宿主给的流只能顺序写，不能 Seek / 关闭（流的寿命归宿主）。
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        JsonSerializer.Serialize(writer, project, options);
        writer.Flush();
        output.WriteByte(0);
    }

    /// 参数轨按固定顺序写：字典的枚举顺序不保证稳定，而原版存盘是固定顺序，
    /// 保持顺序可让两次导出的文件可比对（回归对拍全靠逐字节 diff）。
    public static Dictionary<string, SvpAutomation> OrderedParameters(
        IReadOnlyDictionary<string, SvpAutomation> source)
    {
        var order = new[]
        {
            "pitchDelta", "vibratoEnv", "loudness", "tension",
            "breathiness", "voicing", "gender", "toneShift",
            // OpenCyan 扩展轨：真实 SynthV 读不懂但会原样保留，写在标准轨之后。
            "growl", "growlRate", "distortion", "aspiration",
        };
        var ordered = new Dictionary<string, SvpAutomation>();
        foreach (var name in order)
            if (source.TryGetValue(name, out var curve))
                ordered[name] = curve;
        // 未知轨名原样追加，不静默丢。
        foreach (var (name, curve) in source)
            if (!ordered.ContainsKey(name))
                ordered[name] = curve;
        return ordered;
    }

    /// 参数轨缺字段会让原版按「没有这条曲线」处理，导出时统一补齐成空曲线。
    public static Dictionary<string, SvpAutomation> FullParameterSet(
        IReadOnlyDictionary<string, SvpAutomation>? partial)
    {
        var names = new[]
        {
            "pitchDelta", "vibratoEnv", "loudness", "tension",
            "breathiness", "voicing", "gender", "toneShift",
        };
        var result = new Dictionary<string, SvpAutomation>();
        foreach (var name in names)
            result[name] = partial is not null && partial.TryGetValue(name, out var curve)
                ? curve
                : new SvpAutomation();
        if (partial is not null)
            foreach (var (name, curve) in partial)
                if (!result.ContainsKey(name))
                    result[name] = curve;
        return result;
    }
}
