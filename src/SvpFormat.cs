using SynthesizerVFormatSupport.Common;
using SynthesizerVFormatSupport.Mapping;
using SynthesizerVFormatSupport.Reader;
using SynthesizerVFormatSupport.Writer;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace SynthesizerVFormatSupport;

/// Synthesizer V 工程格式（`.svp`）的导入 + 导出入口，参数落到 OpenCyan 体系。
///
/// 失败口径照 VocaloidFormatSupport：只有「文件根本不是 SVP / 结构非法」才抛
/// `InvalidDataException` 让导入整体失败；「字段有但 OpenCyan 没通道」一律降级并汇总，
/// 因为一份 12 轨工程因为 242 处表情组就打不开，比丢掉表情组糟糕得多。
public sealed class SvpFormat : IImportFormat, IExportFormat, IExtensionSettings
{
    ExportOptions _options = new();
    ImportOptions _importOptions = new();

    public ProjectInfo Deserialize(Stream stream)
    {
        var report = new ImportReport();
        var project = SvpReader.Read(stream, report);
        // 检测结果不落日志：装了没有、检查过没检查过都是用户环境的隐私细节，
        // 而且它对本包的正确性没有任何信息量（缺失的后果已经写在下面那条「导入范围」里）。
        var (installed, _) = OpenCyanPresence.Detect();
        var info = new SvpImporter(project, report, openCyanAvailable: installed, importOptions: _importOptions)
            .Import();
        report.Log();
        return info;
    }

    public void Serialize(Stream output, ProjectInfo info)
    {
        var report = new ImportReport();
        var project = new SvpExporter(_options, report).Export(info);
        SvpWriter.Write(output, project);
        report.Log();
    }

    public ObjectConfig GetSettingsConfig(IExtensionSettingsContext context)
    {
        var current = ExportOptions.FromSettings(context.Settings);
        var import = ImportOptions.FromSettings(context.Settings);
        var props = new OrderedMap<PropertyKey, IControllerConfig>();
        props.Add((ExportOptions.KeyBankName, "导出默认声库名"),
            TextBoxConfig.Create(current.BankName));
        props.Add((ExportOptions.KeyBankVersion, "导出默认声库版本"),
            TextBoxConfig.Create(current.BankVersion));
        var items = OpenCyanParams.ClfLanguages
            .Select(c => (c.Language, Item: new ComboBoxItem(PropertyValue.Create(c.Language), c.Language)))
            .ToList();
        var combo = ComboBoxConfig.Create();
        foreach (var (_, item) in items) combo.Append(item);
        // 缺省选中当前已保存的值，读不到才落到第一项（宿主按 Value 匹配，文案只是显示）。
        var match = items.FirstOrDefault(i => i.Language == current.BankLanguage);
        var selected = string.IsNullOrEmpty(match.Language) ? items[0].Item : match.Item;
        props.Add((ExportOptions.KeyBankLanguage, "导出默认声库语言"), combo.WithDefault(selected));

        // 导入侧：音高线的两个判定阈值（默认 1 音分 / 1 个四分音符，语料里的真实工程都成立）。
        // 只影响「哪些 pitchDelta 段落转成绝对音高」，不影响音符与其它参数。
        props.Add((ImportOptions.KeyPitchEditEpsilonCents, "音高编辑阈值（音分）"),
            DraggableNumberBoxConfig.Create(import.PitchEditEpsilonCents)
                .WithRange(ImportOptions.MinPitchEditEpsilonCents, ImportOptions.MaxPitchEditEpsilonCents)
                .WithStep(0.1)
                .WithFormat(NumberFormat.Decimals(1)));
        props.Add((ImportOptions.KeyZeroGapSplitQuarters, "音高空档断段（四分音符）"),
            DraggableNumberBoxConfig.Create(import.ZeroGapSplitQuarters)
                .WithRange(ImportOptions.MinZeroGapSplitQuarters, ImportOptions.MaxZeroGapSplitQuarters)
                .WithStep(0.25)
                .WithFormat(NumberFormat.Decimals(2)));
        return ObjectConfig.Create(props);
    }

    public void ApplySettings(PropertyObject settings)
    {
        _options = ExportOptions.FromSettings(settings);
        _importOptions = ImportOptions.FromSettings(settings);
    }
}
