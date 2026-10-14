using System.Text.Json;
using SynthesizerVFormatSupport.Common;
using SynthesizerVFormatSupport.Model;

namespace SynthesizerVFormatSupport.Reader;

/// `.svp` → 强类型模型。
///
/// 读法照 `SvpDocument::from_bytes`：允许**一个**尾部 NUL，正文内出现 NUL 直接拒；
/// 解析失败抛 InvalidDataException 并带上 JSON 路径（引擎报「某字段解析失败」时同样
/// 不告诉你期望形态，所以这里把路径打全）。
public static class SvpReader
{
    public const int SupportedVersion = 153;

    public static SvpProject Read(Stream stream, ImportReport report)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
            throw new InvalidDataException("SVP: 文件为空。");

        int payloadLength = bytes.Length;
        if (bytes[payloadLength - 1] == 0) payloadLength--;
        var payload = bytes.AsSpan(0, payloadLength);
        if (payload.IndexOf((byte)0) >= 0)
            throw new InvalidDataException($"SVP: 正文在偏移 {payload.IndexOf((byte)0)} 处出现 NUL，文件已损坏。");

        SvpProject? project;
        try
        {
            project = JsonSerializer.Deserialize<SvpProject>(payload, SvpJson.Read);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"SVP: JSON 解析失败 —— {ex.Message}", ex);
        }
        if (project is null)
            throw new InvalidDataException("SVP: 顶层不是 JSON 对象。");

        if (project.Version != SupportedVersion)
            report.Degrade("版本", "工程", 1, $"version={project.Version}，本包按 {SupportedVersion} 的字段形态解析，其余版本可能缺字段或多字段");

        Validate(project, report);
        CollectUnknownTags(project, report);
        return project;
    }

    static void Validate(SvpProject project, ImportReport report)
    {
        if (project.Tracks.Count == 0)
            throw new InvalidDataException("SVP: tracks 为空，没有可导入的内容。");

        foreach (var (name, automation) in EnumerateCurves(project))
        {
            string? error = automation.Validate(name);
            if (error is not null)
                throw new InvalidDataException($"SVP: {error}");
        }

        // library 的 uuid 是引用解析的键；重复会让「这段音符到底指哪一组」没有确定答案。
        var duplicates = project.Library
            .GroupBy(g => g.Uuid)
            .Where(g => g.Count() > 1 && g.Key.Length > 0)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            report.Degrade("引用", "library", duplicates.Count,
                $"library 里有重复 uuid（{string.Join(", ", duplicates.Take(3))}），这些 group 的引用只能取第一个");

        var known = project.Library.Select(g => g.Uuid).ToHashSet();
        for (int i = 0; i < project.Tracks.Count; i++)
        {
            var track = project.Tracks[i];
            var missing = track.Groups.Where(r => !known.Contains(r.GroupId)).Select(r => r.GroupId).ToList();
            if (missing.Count > 0)
                report.Degrade("引用", OwnerName(track, i), missing.Count,
                    $"groups[] 引用的 library group 不存在（{string.Join(", ", missing.Take(3))}），这些引用被跳过");
        }
    }

    /// 所有会被求值的曲线：参数轨 + Vocal Mode + systemPitchDelta。
    static IEnumerable<(string Name, SvpAutomation Curve)> EnumerateCurves(SvpProject project)
    {
        foreach (var (label, group) in EnumerateGroups(project))
        {
            foreach (var (name, curve) in group.Parameters)
                yield return ($"{label}.parameters.{name}", curve);
            foreach (var (name, curve) in group.VocalModes)
                yield return ($"{label}.vocalModes.{name}", curve);
        }
        for (int i = 0; i < project.Tracks.Count; i++)
        {
            yield return ($"tracks[{i}].mainRef.systemPitchDelta", project.Tracks[i].MainRef.SystemPitchDelta);
            for (int j = 0; j < project.Tracks[i].Groups.Count; j++)
                yield return ($"tracks[{i}].groups[{j}].systemPitchDelta", project.Tracks[i].Groups[j].SystemPitchDelta);
        }
    }

    static IEnumerable<(string Label, SvpNoteGroup Group)> EnumerateGroups(SvpProject project)
    {
        for (int i = 0; i < project.Library.Count; i++)
            yield return ($"library[{i}]:{project.Library[i].Name}", project.Library[i]);
        for (int i = 0; i < project.Tracks.Count; i++)
            yield return ($"tracks[{i}]:{OwnerName(project.Tracks[i], i)}.mainGroup", project.Tracks[i].MainGroup);
    }

    static string OwnerName(SvpTrack track, int index) =>
        string.IsNullOrWhiteSpace(track.Name) ? $"音轨 {index}" : track.Name;

    static void CollectUnknownTags(SvpProject project, ImportReport report)
    {
        void Sink(string path, IEnumerable<string> names)
        {
            if (names is IList<string> list && list.Count == 0) return;
            report.UnknownTag(path, names);
        }

        Sink("$", project.ExtensionData?.Keys ?? Array.Empty<string>());
        Sink("$.time", project.Time.ExtensionData?.Keys ?? Array.Empty<string>());
        Sink("$.renderConfig", project.RenderConfig?.ExtensionData?.Keys ?? Array.Empty<string>());
        for (int i = 0; i < project.Tracks.Count; i++)
        {
            var track = project.Tracks[i];
            Sink($"$.tracks[{i}]", track.ExtensionData?.Keys ?? Array.Empty<string>());
            Sink($"$.tracks[{i}].mixer", track.Mixer.ExtensionData?.Keys ?? Array.Empty<string>());
            ExtraReference($"$.tracks[{i}].mainRef", track.MainRef, Sink);
            for (int j = 0; j < track.Groups.Count; j++)
                ExtraReference($"$.tracks[{i}].groups[{j}]", track.Groups[j], Sink);
            ExtraGroup($"$.tracks[{i}].mainGroup", track.MainGroup, Sink);
        }
        for (int i = 0; i < project.Library.Count; i++)
            ExtraGroup($"$.library[{i}]", project.Library[i], Sink);
    }

    static void ExtraGroup(string path, SvpNoteGroup group, Action<string, IEnumerable<string>> sink)
    {
        sink($"{path}", group.ExtensionData?.Keys ?? Array.Empty<string>());
        foreach (var note in group.Notes)
        {
            sink($"{path}.notes[]", note.ExtensionData?.Keys ?? Array.Empty<string>());
            sink($"{path}.notes[].attributes", note.Attributes.ExtensionData?.Keys ?? Array.Empty<string>());
            sink($"{path}.notes[].systemAttributes", note.SystemAttributes.ExtensionData?.Keys ?? Array.Empty<string>());
            sink($"{path}.notes[].pitchTakes", note.PitchTakes.ExtensionData?.Keys ?? Array.Empty<string>());
            sink($"{path}.notes[].timbreTakes", note.TimbreTakes.ExtensionData?.Keys ?? Array.Empty<string>());
        }
    }

    static void ExtraReference(string path, SvpGroupReference reference, Action<string, IEnumerable<string>> sink)
    {
        sink(path, reference.ExtensionData?.Keys ?? Array.Empty<string>());
        sink($"{path}.database", reference.Database.ExtensionData?.Keys ?? Array.Empty<string>());
        sink($"{path}.voice", reference.Voice.ExtensionData?.Keys ?? Array.Empty<string>());
        if (reference.Audio is not null)
            sink($"{path}.audio", reference.Audio.ExtensionData?.Keys ?? Array.Empty<string>());
    }
}
