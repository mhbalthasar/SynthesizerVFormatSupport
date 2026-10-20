using System.Text;
using SynthesizerVFormatSupport.Common;
using SynthesizerVFormatSupport.Model;
using SynthesizerVFormatSupport.Mapping;
using SynthesizerVFormatSupport.Reader;
using TuneLab.SDK;
using SynthesizerVFormatSupport.Writer;

// SVP 语料探针：批量读真实 .svp，打印规模与降级清单，并做「规范化 → 再读 → 再规范化」的
// 逐字节稳定性检查。
//
// 为什么只比规范化后的字节而不比原文件字节：原文件由 SynthV 存盘，数字格式（0 vs 0.0）、
// 键序都可能有出入；比原始字节会把「格式差异」误报成「数据丢失」。两次规范化结果一致
// 才说明本包的读写器对同一份数据是幂等的。
//
// 用法：SvpCorpusCheck <文件或目录> [...]

Console.OutputEncoding = Encoding.UTF8;

var files = CollectInputs(args);
if (files.Count == 0)
{
    Console.WriteLine("没有输入。用法：SvpCorpusCheck <文件或目录> [...]");
    return 2;
}

var (installed, installedPath) = SynthesizerVFormatSupport.Common.OpenCyanPresence.Detect(
    Environment.GetEnvironmentVariable("SVPCHECK_EXT_ROOT"));
Console.WriteLine($"OpenCyan 检测：{(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SVPCHECK_EXT_ROOT")) ? "宿主默认扩展目录" : "自定义根目录")} → installed={installed} {installedPath}");
Console.WriteLine();

Console.WriteLine();

int failed = 0;
foreach (var path in files)
{
    Console.WriteLine($"===== {Path.GetFileName(path)}  ({new FileInfo(path).Length / 1024} KB)");
    try
    {
        CheckOne(path, installed);
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  [FAIL] {ex.Message}");
        if (ex.InnerException is not null)
            Console.WriteLine($"         ← {ex.InnerException.Message}");
    }
}

Console.WriteLine(failed == 0 ? $"全部通过：{files.Count} 份" : $"失败 {failed} / {files.Count} 份");
return failed == 0 ? 0 : 1;

static void CheckOne(string path, bool installed)
{
    var report = new ImportReport();
    SvpProject project;
    using (var stream = File.OpenRead(path))
        project = SvpReader.Read(stream, report);

    int noteCount = project.Tracks.Sum(t => t.MainGroup.Notes.Count)
        + project.Library.Sum(g => g.Notes.Count);
    int curveCount = project.Tracks.Sum(t => t.MainGroup.Parameters.Count(p => p.Value.Points.Count > 0));
    int curvePoints = project.Tracks.Sum(t => t.MainGroup.Parameters.Values.Sum(c => c.Points.Count) / 2);
    int vocalModeCurves = project.Tracks.Sum(t => t.MainGroup.VocalModes.Count(m => m.Value.Points.Count > 0))
        + project.Library.Sum(g => g.VocalModes.Count(m => m.Value.Points.Count > 0));
    int takeTracks = project.Tracks.Count(t => !t.MainRef.PitchTakes.IsNeutralPlaceholder
                                            || !t.MainRef.TimbreTakes.IsNeutralPlaceholder);
    int nonNeutralTakes = project.Tracks.Sum(t => t.MainGroup.Notes.Count(n =>
        !n.PitchTakes.IsNeutralPlaceholder || !n.TimbreTakes.IsNeutralPlaceholder));
    int instrumental = project.Tracks.Count(t => t.MainRef.IsInstrumental);
    int offsetTracks = project.Tracks.Count(t => t.MainRef.BlickOffset != 0);
    var backends = project.Tracks.Select(t => t.MainRef.Database.BackendType)
        .Where(b => b.Length > 0).Distinct().OrderBy(b => b);
    var banks = project.Tracks.Select(t => $"{t.MainRef.Database.Name}#{t.MainRef.Database.Version}")
        .Where(b => b.Length > 1).Distinct().OrderBy(b => b);
    var unknownNames = report.UnknownTags
        .Select(t => t.Split('.').Last())
        .GroupBy(n => n)
        .OrderByDescending(g => g.Count())
        .Select(g => $"{g.Key}×{g.Count()}");

    Console.WriteLine($"  version={project.Version} tracks={project.Tracks.Count} libraryGroups={project.Library.Count} " +
        $"notes={noteCount} 带点曲线={curveCount}({curvePoints} 点) vocalMode曲线={vocalModeCurves} " +
        $"伴奏轨={instrumental} 非零offset轨={offsetTracks}");
    Console.WriteLine($"  backends=[{string.Join(", ", backends)}] banks=[{string.Join(", ", banks.Take(6))}]");
    Console.WriteLine($"  非占位 take：轨级 {takeTracks} 处、音符级 {nonNeutralTakes} 处");
    Console.WriteLine($"  {report.Summary()}");
    if (unknownNames.Any())
        Console.WriteLine($"  未知 tag：{string.Join(" ", unknownNames.Take(24))}");

    // —— 规范化幂等：project → 字节 → project → 字节，两次字节必须一致 ——
    var first = Canonical(project);
    var reparsed = Reparse(first);
    var second = Canonical(reparsed);
    if (!first.SequenceEqual(second))
    {
        int offset = 0;
        while (offset < Math.Min(first.Length, second.Length) && first[offset] == second[offset]) offset++;
        throw new InvalidOperationException(
            $"往返不稳定：首次规范化 {first.Length} B、二次 {second.Length} B，首个差异在偏移 {offset}");
    }

    ImportAndSummarize(project, path, installed);

    // —— 拍轴换算无损性：所有音符时值换算成 tick 再换回来必须相等 ——
    long ticksRoundTripFail = project.Tracks
        .SelectMany(t => t.MainGroup.Notes)
        .Concat(project.Library.SelectMany(g => g.Notes))
        .SelectMany(n => new[] { n.Onset, n.Duration, n.End })
        .Count(blick => TempoMap.TickToBlick(TempoMap.BlickToTick(blick)) != blick);
    if (ticksRoundTripFail > 0)
        Console.WriteLine($"  [note] {ticksRoundTripFail} 个 BLICK 值在 blick⇄tick 往返后有偏移（非 1,470,000 的整数倍）");
}

static void ImportAndSummarize(SvpProject project, string path, bool openCyan)
{
    var report = new ImportReport();
    var imported = new SvpImporter(project, report, openCyan).Import();

    int parts = imported.Tracks.Sum(t => t.Parts.Count);
    int notes = imported.Tracks.Sum(t => t.Parts.OfType<MidiPartInfo>().Sum(p => p.Notes.Count));
    int vibratos = imported.Tracks.Sum(t => t.Parts.OfType<MidiPartInfo>().Sum(p => p.Vibratos.Count));
    int curvePoints = imported.Tracks.Sum(t => t.Parts.OfType<MidiPartInfo>()
        .Sum(p => p.Automations.Values.Sum(a => a.Points.Count)));
    int pitchPoints = imported.Tracks.Sum(t => t.Parts.OfType<MidiPartInfo>()
        .Sum(p => p.Pitch.Segments.Sum(s => s.Count)));
    var trackKeys = imported.Tracks.SelectMany(t => t.Parts.OfType<MidiPartInfo>())
        .SelectMany(p => p.Automations.Keys).Distinct().OrderBy(k => k);
    int missingVoice = imported.Tracks.SelectMany(t => t.Parts.OfType<MidiPartInfo>())
        .Count(p => string.IsNullOrEmpty(p.SoundSource.Id));

    Console.WriteLine($"  导入({(openCyan ? "OpenCyan" : "无插件")}) → tracks={imported.Tracks.Count} parts={parts} notes={notes} " +
        $"vibratos={vibratos} 曲线点={curvePoints} 音高线点={pitchPoints} 声库键缺失={missingVoice}");
    var pitchDetail = imported.Tracks.SelectMany(t => t.Parts.OfType<MidiPartInfo>())
        .Where(p => p.Pitch.Segments.Count > 0)
        .Select(p => $"{p.Name}:{p.Pitch.Segments.Count}段/{p.Pitch.Segments.Sum(s => s.Count)}点");
    if (pitchDetail.Any())
        Console.WriteLine($"  音高分段=[{string.Join(", ", pitchDetail)}]");
    // 回归哨兵：段内连奏边界必须有「左极限 + 右极限」两个采样点，否则宿主 Hermite 会把
    // 整颗音符插成滑向下一颗音高的滑音（45:3 那次跑调就是这条）。
    int ramps = imported.Tracks.SelectMany(t => t.Parts.OfType<MidiPartInfo>()).Sum(CountRampBoundaries);
    if (ramps > 0)
        Console.WriteLine($"  [warn] 连奏边界缺采样点 {ramps} 处（音高会在音符内部滑音）");
    Console.WriteLine($"  轨键=[{string.Join(", ", trackKeys)}]");
    Console.WriteLine($"  tempos={imported.Tempos.Count} meters={imported.TimeSignatures.Count} | {report.Summary()}");
    // —— 导出回环：ProjectInfo → SVP → 字节 → 再读，规模必须与导入前同量级 ——
    var exportReport = new ImportReport();
    var exported = new SvpExporter(new ExportOptions(), exportReport).Export(imported);
    using var memory = new MemoryStream();
    SynthesizerVFormatSupport.Writer.SvpWriter.Write(memory, exported);
    var bytes = memory.ToArray();
    var reimport = new SvpImporter(ReparseCanonical(bytes), new ImportReport()).Import();
    int exportedNotes = reimport.Tracks.Sum(t => t.Parts.OfType<MidiPartInfo>().Sum(p => p.Notes.Count));
    int reimportPitch = reimport.Tracks.Sum(t => t.Parts.OfType<MidiPartInfo>()
        .Sum(p => p.Pitch.Segments.Sum(s => s.Count)));
    Console.WriteLine($"  导出 → {bytes.Length / 1024} KB、tracks={exported.Tracks.Count}、notes={exported.Tracks.Sum(t => t.MainGroup.Notes.Count)}"
        + $" | 再导入 notes={exportedNotes} vs {notes}（差 = 伴奏轨与合并 part 的宿主侧再展开）"
        + $" 音高线点={reimportPitch} | {exportReport.Summary()}");

    var dump = Environment.GetEnvironmentVariable("SVPDUMP");
    if (!string.IsNullOrEmpty(dump))
    {
        Directory.CreateDirectory(dump);
        var outPath = Path.Combine(dump, Path.GetFileNameWithoutExtension(path) + ".exported.svp");
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"  已落盘 {outPath}");
    }

    var top = imported.Tracks.SelectMany(t => t.Parts.OfType<MidiPartInfo>()).OrderByDescending(p => p.Notes.Count).FirstOrDefault();
    if (top is not null)
    {
        var first = top.Notes.OrderBy(n => n.Pos).First();
        Console.WriteLine($"  最大 part「{top.Name}」notes={top.Notes.Count} 首音符 pos={first.Pos:F1}t dur={first.Dur:F1}t pitch={first.Pitch} lyric={first.Lyric}");
    }
}

static int CountRampBoundaries(MidiPartInfo part)
{
    var notes = part.Notes.OrderBy(n => n.Pos).ToList();
    if (notes.Count < 2 || part.Pitch.Segments.Count == 0) return 0;

    int count = 0;
    for (int i = 0; i + 1 < notes.Count; i++)
    {
        var a = notes[i];
        var b = notes[i + 1];
        if (Math.Abs(a.Pos + a.Dur - b.Pos) > 0.5 || a.Pitch == b.Pitch) continue;

        double t = b.Pos;
        foreach (var segment in part.Pitch.Segments)
        {
            if (segment.Count == 0 || t <= segment[0].X || t >= segment[^1].X) continue;
            bool hasRight = segment.Any(p => Math.Abs(p.X - t) <= 1e-3);
            bool hasLeft = segment.Any(p => p.X < t && t - p.X <= 1e-3);
            if (!hasRight || !hasLeft) count++;
            break;
        }
    }
    return count;
}

static byte[] Canonical(SvpProject project){
    using var stream = new MemoryStream();
    SvpWriter.Write(stream, project);
    return stream.ToArray();
}

static SvpProject ReparseCanonical(byte[] bytes) => Reparse(bytes);

static SvpProject Reparse(byte[] bytes)
{
    using var stream = new MemoryStream(bytes);
    return SvpReader.Read(stream, new ImportReport());
}

static List<string> CollectInputs(string[] args)
{
    var files = new List<string>();
    foreach (var arg in args)
    {
        if (Directory.Exists(arg))
            files.AddRange(Directory.EnumerateFiles(arg, "*.svp", SearchOption.AllDirectories));
        else if (File.Exists(arg))
            files.Add(arg);
        else
            Console.WriteLine($"[skip] 路径不存在：{arg}");
    }
    files.Sort(StringComparer.OrdinalIgnoreCase);
    return files;
}
