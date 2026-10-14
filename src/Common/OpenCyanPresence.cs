using System.Text.Json;

namespace SynthesizerVFormatSupport.Common;

/// OpenCyan 歌声插件是否装在这台机器上（目录级检查）。
///
/// 判据口径与宿主一致：`PathManager.ExtensionsFolder` = `%APPDATA%\TuneLab\Extensions`，
/// 宿主把**一级子目录里带 manifest.json 的都当一个包**扫进来（deploy.ps1 的注释还专门提醒
/// 备份目录必须放这棵树之外，否则会被当重复包加载）。所以这里不能靠目录名判：实测本机
/// 装出来的目录名既有 `OpenCyanForTuneLab`、也有带空格的显示名 `VOCALOID Format Support`。
///
/// 认「manifest 里有 type=voice 且 engine=OpenCyan 的条目」——`engine` 是不可变的注册 id、
/// 会写进工程文件，比程序集名与目录名都稳。程序集名只作 manifest 读不动时的兜底。
public static class OpenCyanPresence
{
    const string EngineId = "OpenCyan";

    /// 宿主 `TuneLabFolder`（可用 TUNELAB_DATA_DIR 覆盖，见 PathManager），未覆盖时是 %APPDATA%\TuneLab。
    public static string DefaultExtensionsRoot()
    {
        var dataRoot = Environment.GetEnvironmentVariable("TUNELAB_DATA_DIR");
        if (string.IsNullOrEmpty(dataRoot))
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TuneLab");
        return Path.Combine(dataRoot, "Extensions");
    }

    /// 返回 (是否装了, 命中的包目录)。root 传 null 用宿主默认目录；探针可传别的目录模拟未安装。
    public static (bool Installed, string? Path) Detect(string? root = null)
    {
        var baseDir = root ?? DefaultExtensionsRoot();
        if (!Directory.Exists(baseDir)) return (false, null);

        foreach (var package in Directory.EnumerateDirectories(baseDir))
        {
            var manifest = Path.Combine(package, "manifest.json");
            if (File.Exists(manifest) && ManifestDeclaresOpenCyan(manifest)) return (true, package);

            // manifest 读不动时的兜底：程序集名是 manifest 里 assembly 字段的默认取值来源。
            if (File.Exists(Path.Combine(package, "OpenCyanForTuneLab.dll"))) return (true, package);
        }
        return (false, null);
    }

    static bool ManifestDeclaresOpenCyan(string manifestPath)
    {
        JsonDocument document;
        try
        {
            // 只读探活：manifest 损坏/编码异常不该让整个导入失败，吞掉即可。
            using var stream = File.OpenRead(manifestPath);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (IsOpenCyanVoice(root)) return true;
            if (root.TryGetProperty("extensions", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray())
                    if (IsOpenCyanVoice(entry)) return true;
        }
        return false;
    }

    static bool IsOpenCyanVoice(JsonElement entry)
        => entry.ValueKind == JsonValueKind.Object
        && GetString(entry, "type") == "voice"
        && GetString(entry, "engine") == EngineId;

    static string? GetString(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
