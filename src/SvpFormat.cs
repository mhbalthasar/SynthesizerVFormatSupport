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

    public ProjectInfo Deserialize(Stream stream)
    {
        var report = new ImportReport();
        var project = SvpReader.Read(stream, report);
        var (installed, path) = OpenCyanPresence.Detect();
        if (installed)
            TuneLabContext.Global.GetLogger().Info($"已检测到 OpenCyan 插件：{path}");
        var info = new SvpImporter(project, report, openCyanAvailable: installed).Import();
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
        return ObjectConfig.Create(props);
    }

    public void ApplySettings(PropertyObject settings) => _options = ExportOptions.FromSettings(settings);
}
