using System.Text.Json;
using System.Text.Json.Serialization;
using TuneLab.SDK;

namespace SynthesizerVFormatSupport.Common;

/// 导入/导出的降级与拒绝清单。
///
/// 宿主给格式插件的反馈通道只有两条：抛 `InvalidDataException`（整体失败）与插件作用域日志。
/// 所以「能搬的先搬、搬不动的记账」是本格式包的默认策略：逐条降级不中断导入，
/// 结束时汇总成一份按类别 × 轨道的清单写进日志——逐音符打日志会把日志刷爆且看不出全貌。
public sealed class ImportReport
{
    /// 键 = (类别, 归属)，值 = 受影响的项目数与一条样例说明。
    readonly Dictionary<(string Category, string Owner), (int Count, string Sample)> _items = new();

    readonly List<string> _unknownTags = new();

    public int WarningCount => _items.Count;

    public void Degrade(string category, string owner, int count, string sample)
    {
        if (count <= 0) return;
        var key = (category, owner);
        _items[key] = _items.TryGetValue(key, out var existing)
            ? (existing.Count + count, existing.Sample)
            : (count, sample);
    }

    /// 未知 tag 单独记账：它意味着新版本 SynthV 写了本包不认识的字段，导出时一定会丢，
    /// 必须与「已知的有损映射」分开展示。
    public void UnknownTag(string path, IEnumerable<string> names)
    {
        foreach (var name in names)
            _unknownTags.Add($"{path}.{name}");
    }

    public IReadOnlyList<string> UnknownTags => _unknownTags;

    public void Log()
    {
        var logger = TuneLabContext.Global.GetLogger();
        if (_items.Count == 0 && _unknownTags.Count == 0)
        {
            logger.Info("SVP 导入：全部字段都有对应通道，无降级。");
            return;
        }

        logger.Warning($"SVP 导入降级清单（{_items.Count} 类）：");
        foreach (var ((category, owner), (count, sample)) in _items.OrderBy(k => k.Key.Category).ThenBy(k => k.Key.Owner))
            logger.Warning($"  [{category}] {owner}：{count} 处 —— {sample}");
        if (_unknownTags.Count > 0)
            logger.Warning($"  未知 tag {Math.Min(_unknownTags.Count, 40)} 项（导出会丢失）：{string.Join(", ", _unknownTags.Take(20))}");
    }

    public string Summary()
    {
        if (_items.Count == 0 && _unknownTags.Count == 0) return "无降级";
        return $"{_items.Count} 类降级 / {Math.Min(_unknownTags.Count, 40)} 项未知 tag";
    }
}

static class SvpJson
{
    /// 反序列化：未知 tag 进 ExtensionData（不静默丢），大小写严格按 JsonPropertyName。
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// 序列化：null（= 文件里没有这个 tag）一律不写；空曲线 / 空字典照写（原版也这么写）。
    public static readonly JsonSerializerOptions Write = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
