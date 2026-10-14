using System.Text.Json;
using System.Text.Json.Serialization;
using SynthesizerVFormatSupport.Common;

namespace SynthesizerVFormatSupport.Model;

// SVP 的 Automation：`{"mode":"cubic","points":[blick0,value0,blick1,value1,…]}`。
//
// points 是**扁平交替数组**（不是对象数组）——写成对象数组会被解析成空曲线而不报错，
// 是最难查的一种错法。求值语义逐条对齐 `libOpenCyan/src/svp/src/automation.rs`：
// 空曲线取调用方给的中性值、越界钳到最近端点、cubic 在点数 < 3 时退化为线性。
public sealed class SvpAutomation
{
    [JsonPropertyName("mode")]
    public SvpInterpolationMode Mode { get; set; } = SvpInterpolationMode.Cubic;

    [JsonPropertyName("points")]
    [JsonConverter(typeof(SvpCurvePointListConverter))]
    public List<SvpCurvePoint> Points { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    // 计算属性一律标 JsonIgnore：STJ 会把派生属性当数据写出，再读回来就成了「未知 tag」，
    // 往返对拍立刻不幂等。
    [JsonIgnore]
    public bool IsEmpty => Points.Count == 0;

    /// 按原公式求值；<paramref name="neutral"/> 为空曲线时的取值（各参数轨不同，见 OpenCyanParams）。
    public double Evaluate(long blick, double neutral)
    {
        if (Points.Count == 0) return neutral;
        if (blick <= Points[0].Blick) return Points[0].Value;
        int last = Points.Count - 1;
        if (blick >= Points[last].Blick) return Points[last].Value;

        int right = 0;
        while (Points[right].Blick < blick) right++;
        int left = right - 1;
        var p1 = Points[left];
        var p2 = Points[right];
        if (blick == p1.Blick) return p1.Value;

        double h = p2.Blick - p1.Blick;
        double u = (blick - p1.Blick) / h;
        return Mode switch
        {
            SvpInterpolationMode.Linear => p1.Value + (p2.Value - p1.Value) * u,
            SvpInterpolationMode.Cosine => p1.Value + (p2.Value - p1.Value) * (1.0 - Math.Cos(Math.PI * u)) * 0.5,
            // 引擎：cubic 且点数 < 3 ⇒ 退化线性。
            _ when Points.Count < 3 => p1.Value + (p2.Value - p1.Value) * u,
            _ => EvaluateCubic(left, right, h, u),
        };
    }

    // 切线来自相邻点斜率 × h，并按「区间长（四分音符数）距 0.25 的远近」在段间斜率与
    // 端点斜率之间插值（alpha 随距离衰减）——这段非标准 Hermite 是原版存盘公式，改动会让
    // 烘焙出的音高曲线与引擎自算结果分叉，故逐式照抄。
    double EvaluateCubic(int left, int right, double h, double u)
    {
        var p1 = Points[left];
        var p2 = Points[right];
        var p0 = left > 0 ? Points[left - 1] : new SvpCurvePoint { Blick = p1.Blick - 1, Value = p1.Value };
        var p3 = right + 1 < Points.Count ? Points[right + 1] : new SvpCurvePoint { Blick = p2.Blick + 1, Value = p2.Value };

        double y0 = p0.Value, y1 = p1.Value, y2 = p2.Value, y3 = p3.Value;
        double slopeLeft = (y2 - y0) / (p2.Blick - p0.Blick);
        double slopeRight = (y3 - y1) / (p3.Blick - p1.Blick);
        double slopeSegment = (y2 - y1) / h;

        double distance = Math.Max(h / OpenCyanParams.BlickPerQuarter - 0.25, 0.0);
        double alpha = distance <= 4.0 ? 1.0 / (distance * 10.0 + 1.0) : 0.0;
        double m1 = slopeLeft * h * alpha + slopeSegment * (1.0 - alpha);
        double m2 = slopeRight * h * alpha + slopeSegment * (1.0 - alpha);

        double u2 = u * u, u3 = u2 * u;
        return (u3 - 2 * u2 + u) * m1 + (1 - 3 * u2 + 2 * u3) * y1 + (3 * u2 - 2 * u3) * y2 + (u3 - u2) * m2;
    }

    /// 校验控制点：时间必须严格递增、值必须有限。不排序、不去重（照引擎口径，重排会改变原语义）。
    public string? Validate(string path)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            if (!double.IsFinite(Points[i].Value))
                return $"{path}.points[{i * 2 + 1}] 不是有限数值";
            if (i > 0 && Points[i].Blick <= Points[i - 1].Blick)
                return $"{path}.points[{i}] 的 BLICK {Points[i].Blick} 不大于前一点 {Points[i - 1].Blick}";
        }
        return null;
    }
}

public readonly struct SvpCurvePoint
{
    [JsonPropertyName("blick")]
    public long Blick { get; init; }

    [JsonPropertyName("value")]
    public double Value { get; init; }
}

[JsonConverter(typeof(SvpInterpolationModeConverter))]
public enum SvpInterpolationMode
{
    Linear,
    Cosine,
    Cubic,
}

// mode 在文件里是小写字符串；未知值不能静默降级成线性（会让曲线整体形状悄悄变掉），直接拒。
sealed class SvpInterpolationModeConverter : JsonConverter<SvpInterpolationMode>
{
    public override SvpInterpolationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return text switch
        {
            "linear" => SvpInterpolationMode.Linear,
            "cosine" => SvpInterpolationMode.Cosine,
            "cubic" => SvpInterpolationMode.Cubic,
            _ => throw new JsonException($"未知的插值模式 {text ?? "<null>"}（应为 linear/cosine/cubic）"),
        };
    }

    public override void Write(Utf8JsonWriter writer, SvpInterpolationMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            SvpInterpolationMode.Linear => "linear",
            SvpInterpolationMode.Cosine => "cosine",
            _ => "cubic",
        });
    }
}

/// points 的扁平交替数组编解码：`[blick0, value0, blick1, value1, …]`。
public sealed class SvpCurvePointListConverter : JsonConverter<List<SvpCurvePoint>>
{
    public override List<SvpCurvePoint> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("points 必须是数组");

        var raw = new List<double>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"points 的第 {raw.Count} 项不是数字");
            raw.Add(reader.GetDouble());
        }

        if (raw.Count % 2 != 0)
            throw new JsonException($"points 长度 {raw.Count} 为奇数，缺少配对的时间或值");

        var points = new List<SvpCurvePoint>(raw.Count / 2);
        for (int i = 0; i < raw.Count; i += 2)
            points.Add(new SvpCurvePoint { Blick = (long)raw[i], Value = raw[i + 1] });
        return points;
    }

    public override void Write(Utf8JsonWriter writer, List<SvpCurvePoint> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var point in value)
        {
            writer.WriteNumberValue(point.Blick);
            writer.WriteNumberValue(point.Value);
        }
        writer.WriteEndArray();
    }
}
