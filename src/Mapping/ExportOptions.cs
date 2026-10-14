using SynthesizerVFormatSupport.Model;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace SynthesizerVFormatSupport.Mapping;

/// 导出侧的用户配置（设置面板落盘后回喂）。
///
/// 只有「默认声库三元组」这一件事需要用户填：宿主工程里的声源身份是哈希、不可逆推名字，
/// 而 SynthV 打开 .svp 时按 `database.name` 找已安装声库。本包导入的工程会在 part 私有属性里
/// 带上原始三元组、导出直接读回，走不到这里；只有**不是本包导入**的工程才需要这份兜底值。
public sealed class ExportOptions
{
    public const string KeyBankName = "export_bank_name";
    public const string KeyBankVersion = "export_bank_version";
    public const string KeyBankLanguage = "export_bank_language";

    public string BankName { get; set; } = "SynthV";
    public string BankVersion { get; set; } = "100";
    public string BankLanguage { get; set; } = "mandarin";

    public static ExportOptions FromSettings(PropertyObject settings) => new()
    {
        BankName = settings.GetString(KeyBankName, "SynthV"),
        BankVersion = settings.GetString(KeyBankVersion, "100"),
        BankLanguage = settings.GetString(KeyBankLanguage, "mandarin"),
    };

    /// 导出设置：SynthV 侧的渲染配置与歌声数据无关，给一份能打开的默认值即可。
    /// 数字与语料一致（16 bit / 44.1 kHz / 单声道 / asIsolatedFiles）。
    public SvpRenderConfig BuildRenderConfig() => new()
    {
        Destination = "",
        Filename = "",
        NumChannels = 1,
        AspirationFormat = "asIsolatedFiles",
        BitDepth = 16,
        SampleRate = 44100,
        ExportMixDown = false,
        ExportPitch = false,
    };
}
