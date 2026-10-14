using SynthesizerVFormatSupport.Common;
using SynthesizerVFormatSupport.Model;
using SynthesizerVFormatSupport.Writer;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace SynthesizerVFormatSupport.Mapping;

/// TuneLab `ProjectInfo` → `.svp`。
///
/// 与导入不对称、必须写明白的三条取舍：
/// 1. **恒写 `mainRef.blickOffset = 0`**（位置压成工程绝对坐标）。非零 offset 在引擎侧是「曲线坐标
///    变换尚未通过 trace 确认」的硬拒项，导出不该制造它。
/// 2. **轨道全局值一律走曲线**。引擎算 `voice.paramX + curve(t)`，两者可互换；导出无法从宿主反推
///    「哪一半是全局值」，故统一写曲线、`voice.paramX` 留空。基线 ≠ 轨默认值时在 part 区间两端补
///    等值点：SVP 曲线在锚点之外是钳到端点，没有「基线」这一层概念，不补就丢基线。
/// 3. **音符一律 `instantMode = true`**。宿主工程不存引擎自动音高的基线快照（systemAttributes），
///    手动差值语义无从还原，交回引擎重新 predict。
///
/// 声库身份：`SoundSource.Id` 是 sha256 截断哈希、不可逆推名字。导入时把原始三元组写进 part 私有属性
/// <see cref="SidecarIdentityKey"/>（`MidiPart.GetInfo/SetInfo` 整表往返 Properties，未声明的键照样存取），
/// 导出读回；读不到退回设置里的默认三元组并记降级。
public sealed class SvpExporter
{
    public const string SidecarIdentityKey = "svp_identity";

    readonly ExportOptions _options;
    readonly ImportReport _report;
    TempoMap _tempo = new(Array.Empty<SvpTempoMark>());

    public SvpExporter(ExportOptions options, ImportReport report)
    {
        _options = options;
        _report = report;
    }

    public SvpProject Export(ProjectInfo info)
    {
        var project = new SvpProject { Version = 153 };

        foreach (var tempo in info.Tempos.OrderBy(t => t.Pos))
            project.Time.Tempo.Add(new SvpTempoMark { Position = TempoMap.TickToBlick(tempo.Pos), Bpm = tempo.Bpm });
        if (project.Time.Tempo.Count == 0)
            project.Time.Tempo.Add(new SvpTempoMark { Position = 0, Bpm = 120 });
        _tempo = new TempoMap(project.Time.Tempo);

        foreach (var meter in info.TimeSignatures.OrderBy(t => t.BarIndex))
            project.Time.Meter.Add(new SvpMeterMark
            {
                Index = meter.BarIndex,
                Numerator = meter.Numerator,
                Denominator = meter.Denominator,
            });
        if (project.Time.Meter.Count == 0)
            project.Time.Meter.Add(new SvpMeterMark { Index = 0, Numerator = 4, Denominator = 4 });

        for (int index = 0; index < info.Tracks.Count; index++)
        {
            var track = ExportTrack(info.Tracks[index], index);
            if (track is not null) project.Tracks.Add(track);
        }
        if (project.Tracks.Count == 0)
            throw new InvalidDataException("SVP 导出：工程里没有任何可导出的轨道。");

        project.RenderConfig = _options.BuildRenderConfig();
        return project;
    }

    SvpTrack? ExportTrack(TrackInfo track, int index)
    {
        string owner = string.IsNullOrWhiteSpace(track.Name) ? $"音轨 {index + 1}" : track.Name;
        var vocalParts = track.Parts.OfType<MidiPartInfo>().ToList();
        var audioPart = track.Parts.OfType<AudioPartInfo>().FirstOrDefault();
        if (vocalParts.Count == 0 && audioPart is null) return null;

        if (vocalParts.Count == 0) return ExportInstrumentalTrack(track, index, owner, audioPart!);

        if (audioPart is not null)
            _report.Degrade("伴奏混排", owner, 1, "同轨既有歌声 part 又有音频 part：SVP 一轨只能其一，音频 part 未导出");
        if (vocalParts.Count > 1)
            _report.Degrade("合并 part", owner, vocalParts.Count - 1,
                $"SVP 一轨只有一个 mainGroup，{vocalParts.Count} 个 part 已合并成一条音符序列");

        var (name, version, language) = ResolveIdentity(vocalParts[0], owner);
        string groupId = Guid.NewGuid().ToString();
        var result = new SvpTrack
        {
            Name = owner,
            DispColor = ConvertColor(track.Color),
            DispOrder = index,
            Mixer = new SvpMixer
            {
                GainDecibel = track.Gain,
                Pan = track.Pan,
                Mute = track.Mute,
                Solo = track.Solo,
                Display = true,
            },
            MainGroup = new SvpNoteGroup { Name = owner, Uuid = groupId },
            MainRef = new SvpGroupReference
            {
                GroupId = groupId,
                BlickAbsoluteBegin = 0,
                BlickAbsoluteEnd = -1,
                Database = new SvpDatabase
                {
                    Name = name,
                    Language = language,
                    // 语言与音素集必须成对下发，CLF 之外的组合一律 UnsupportedEnvironment。
                    Phoneset = OpenCyanParams.PhonesetFor(language) ?? "xsampa",
                    BackendType = "SVR2AI",
                    Version = version,
                },
                Voice = new SvpVoice { VocalModeInherited = true, VocalModePreset = string.Empty },
            },
        };

        foreach (var part in vocalParts)
            foreach (var note in part.Notes)
                result.MainGroup.Notes.Add(new SvpNote
                {
                    Onset = TempoMap.TickToBlick(part.Pos + note.Pos),
                    Duration = Math.Max(TempoMap.TickToBlick(note.Dur), 1),
                    Pitch = note.Pitch,
                    Lyrics = note.Lyric,
                    Attributes = new SvpNoteAttributes { EvenSyllableDuration = true },
                    SystemAttributes = new SvpNoteAttributes { EvenSyllableDuration = true },
                });

        result.MainGroup.Notes.Sort((a, b) => a.Onset.CompareTo(b.Onset));
        ExportPartsContent(vocalParts, result, owner);
        WriteTransitionBaseline(result);
        return result;
    }

    SvpTrack ExportInstrumentalTrack(TrackInfo track, int index, string owner, AudioPartInfo audio)
    {
        _report.Degrade("伴奏轨", owner, 1,
            $"写成 isInstrumental + audio.filename={Path.GetFileName(audio.Path)}；时长由拍速换算得出"
            + "（原版存音频真实秒长），SynthV 需能在工程目录旁找到该文件");
        double seconds = _tempo.SecondsBetween(0, TempoMap.TickToBlick(
            audio.Pos + audio.StartOffset + Math.Max(audio.EndOffset, 0)));
        string groupId = Guid.NewGuid().ToString();
        return new SvpTrack
        {
            Name = owner,
            DispColor = ConvertColor(track.Color),
            DispOrder = index,
            Mixer = new SvpMixer { GainDecibel = track.Gain, Pan = track.Pan, Mute = track.Mute, Solo = track.Solo },
            MainGroup = new SvpNoteGroup { Name = owner, Uuid = groupId },
            MainRef = new SvpGroupReference
            {
                GroupId = groupId,
                IsInstrumental = true,
                Database = new SvpDatabase { Version = "-2" },
                Voice = new SvpVoice(),
                Audio = new SvpAudioReference { Filename = Path.GetFileName(audio.Path), Duration = Math.Round(seconds, 3) },
            },
        };
    }

    // ── part 内容 → mainGroup（曲线 / 音高线 / 颤音 / 属性）──

    void ExportPartsContent(List<MidiPartInfo> parts, SvpTrack track, string owner)
    {
        var parameters = new Dictionary<string, SvpAutomation>(StringComparer.Ordinal);
        var vocalModes = new Dictionary<string, SvpAutomation>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            long anchor = TempoMap.TickToBlick(part.Pos);
            double lengthTicks = Math.Max(part.EndOffset,
                part.Notes.Count > 0 ? part.Notes.Max(n => n.Pos + n.Dur) : 1);
            long partEnd = anchor + TempoMap.TickToBlick(lengthTicks);

            foreach (var entry in part.Automations)
            {
                string key = entry.Key;
                var automation = entry.Value;
                if (key == OpenCyanParams.KeyVibratoEnvelope)
                {
                    parameters["vibratoEnv"] = BuildCurve(automation, anchor, partEnd,
                        host => host, OpenCyanParams.VibratoEnvelopeDefault);
                    continue;
                }

                var spec = OpenCyanParams.TimbreTracks.FirstOrDefault(
                    t => t.KeyId == key || t.EngineName == key);
                if (spec is not null)
                {
                    parameters[spec.EngineName] = BuildCurve(automation, anchor, partEnd,
                        host => spec.ToEngine(host), spec.Default);
                    continue;
                }

                if (key.StartsWith(OpenCyanParams.XsyTrackIdPrefix, StringComparison.Ordinal))
                {
                    var style = MatchXsyStyle(part, key);
                    if (style is null)
                    {
                        // 跨库混合（speakerMix）要读目标声库的 32 维向量，格式包没有声库访问能力。
                        _report.Degrade("混合轨", owner, 1, $"{key}：不是可解析的库内声线混合轨，未导出");
                        continue;
                    }
                    vocalModes[style] = BuildCurve(automation, anchor, partEnd,
                        host => host * 100.0, 0);   // 宿主 0..1 → SVP 界面百分数
                    continue;
                }

                _report.Degrade("未知轨", owner, 1, $"{key}：不是当前声源声明的轨，曲线未导出");
            }

            ExportPitchCurve(part, track, parameters, owner);
            ExportVibratos(part, track, owner);
            ExportNoteProperties(part, track, owner);
            ExportPartProperties(part, track, owner);
        }

        // 12 条参数轨一律写全（缺的为空曲线）：原版存盘就是满表，缺字段虽能解析，
        // 但两次导出对不齐、回归 diff 全是噪声。
        track.MainGroup.Parameters = SvpWriter.FullParameterSet(parameters);
        foreach (var (name, curve) in vocalModes)
            track.MainGroup.VocalModes[name] = curve;
    }

    static SvpAutomation BuildCurve(AutomationInfo automation, long anchor, long partEnd,
                                    Func<double, double> convert, double hostDefault)
    {
        var curve = new SvpAutomation
        {
            Mode = SvpInterpolationMode.Linear,
            Points = automation.Points
                .Select(p => new SvpCurvePoint { Blick = anchor + TempoMap.TickToBlick(p.X), Value = convert(p.Y) })
                .ToList(),
        };

        if (Math.Abs(automation.DefaultValue - hostDefault) > 1e-12)
        {
            double baseline = convert(automation.DefaultValue);
            if (curve.Points.Count == 0 || curve.Points[0].Blick > anchor)
                curve.Points.Insert(0, new SvpCurvePoint { Blick = anchor, Value = baseline });
            if (curve.Points.Count > 0 && curve.Points[^1].Blick < partEnd)
                curve.Points.Add(new SvpCurvePoint { Blick = partEnd, Value = baseline });
        }
        return curve;
    }

    string? MatchXsyStyle(MidiPartInfo part, string trackKey)
    {
        if (!part.Properties.Map.TryGetValue(OpenCyanParams.KeyXsyMix, out var value)) return null;
        if (!value.ToArray(out var array)) return null;
        var identity = SplitIdentity(part);
        string bankId = OpenCyanParams.VoicebankKey(identity.Name, identity.Version, identity.Language);
        for (int i = 0; i < array.Count; i++)
        {
            if (!array[i].ToObject(out var element)) continue;
            // 目标声库字段非空 = 跨库混合，落的是 speakerMix，格式包解析不了。
            if (element.GetString(OpenCyanParams.KeyXsyVoice, string.Empty).Length > 0) continue;
            string style = element.GetString(OpenCyanParams.KeyXsyStyle, string.Empty);
            // 轨键是内容寻址的 salt，与导入时同一条公式 ⇒ 这里重算即可命中。
            if (OpenCyanParams.XsyTrackKey(OpenCyanParams.XsySalt(bankId, identity.Version, style)) == trackKey)
                return style;
        }
        return null;
    }

    // ── 音高线 → pitchDelta（音分偏差）──

    void ExportPitchCurve(MidiPartInfo part, SvpTrack track, Dictionary<string, SvpAutomation> parameters, string owner)
    {
        if (part.Pitch.Segments.Count == 0) return;
        long anchor = TempoMap.TickToBlick(part.Pos);

        // 多条折线可能落在同一位置；SVP 只接受一条严格递增的点列，故按位置合并、后写覆盖。
        var merged = new SortedDictionary<long, double>();
        int outside = 0;
        foreach (var segment in part.Pitch.Segments)
            foreach (var point in segment)
            {
                long blick = anchor + TempoMap.TickToBlick(point.X);
                double nominal = NominalAt(track.MainGroup.Notes, blick);
                if (nominal <= 0)
                {
                    outside++;
                    continue;
                }
                merged[blick] = (point.Y - nominal) * 100.0;
            }

        if (outside > 0)
            _report.Degrade("音高线", owner, outside, "音高线的点落在所有音符之外，无法折成相对偏差，已丢弃");
        if (merged.Count == 0) return;

        var curve = new SvpAutomation { Mode = SvpInterpolationMode.Linear };
        foreach (var (blick, cents) in merged)
            curve.Points.Add(new SvpCurvePoint { Blick = blick, Value = cents });
        parameters["pitchDelta"] = curve;
    }

    static double NominalAt(List<SvpNote> notes, long blick)
    {
        if (notes.Count == 0) return 0;
        int index = LowerBound(notes, blick);
        if (index < notes.Count && notes[index].Onset <= blick && blick < notes[index].End) return notes[index].Pitch;
        int previous = index - 1;
        if (previous < 0) return index < notes.Count ? notes[index].Pitch : 0;
        if (index >= notes.Count) return notes[previous].Pitch;
        // 间隙取最近的一颗：与导入侧同一条规则，否则往返一次就把间隙里的值挪了位。
        return blick - notes[previous].End <= notes[index].Onset - blick ? notes[previous].Pitch : notes[index].Pitch;
    }

    static int LowerBound(List<SvpNote> notes, long blick)
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

    static int FindNoteIndex(List<SvpNote> notes, long blick)
    {
        int index = LowerBound(notes, blick);
        if (index > 0 && notes[index - 1].Onset <= blick && blick < notes[index - 1].End) return index - 1;
        if (index < notes.Count && notes[index].Onset <= blick && blick < notes[index].End) return index;
        return -1;
    }

    // ── 颤音对象 → 音符级手动颤音参数 ──

    void ExportVibratos(MidiPartInfo part, SvpTrack track, string owner)
    {
        if (part.Vibratos.Count == 0) return;
        long anchor = TempoMap.TickToBlick(part.Pos);
        var notes = track.MainGroup.Notes;
        int orphans = 0;

        foreach (var vibrato in part.Vibratos)
        {
            long blick = anchor + TempoMap.TickToBlick(vibrato.Pos);
            int index = FindNoteIndex(notes, blick);
            if (index < 0)
            {
                // SVP 的颤音参数只能挂在音符上；悬在音符之间的对象无处可落。
                orphans++;
                continue;
            }
            var note = notes[index];
            note.InstantMode = false;
            var attributes = note.Attributes;
            attributes.DF0Vbr = vibrato.Amplitude * 2.0;      // 引擎峰值 = depth × 0.5
            attributes.FF0Vbr = vibrato.Frequency;
            attributes.PF0Vbr = -vibrato.Phase * Math.PI;     // 宿主 sin 里是 −Phase·π
            attributes.TF0VbrStart = _tempo.SecondsBetween(note.Onset, blick);
            attributes.TF0VbrLeft = vibrato.Attack;
            attributes.TF0VbrRight = vibrato.Release;
        }

        if (orphans > 0)
            _report.Degrade("孤儿颤音", owner, orphans, "颤音对象不在任何音符区间内，SVP 无对应位置，未导出");
    }

    /// 给「本轨存在手动音符」的工程补一份 systemAttributes 基线。
    ///
    /// 不是可选的装饰：引擎的手动滑音算的是差值 `effective − system`（manual_pitch.rs:199），
    /// `system` 从**同一对里的自动邻音**的 systemAttributes 读（`controls_for_event(use_manual=false)`），
    /// 缺字段直接报 `systemAttributes.tF0Offset 缺失` —— 实跑本包导出的 base珠玉 就是这么挂的。
    /// 我们没有任何滑音改动要表达，所以让基线 = 面板默认 = 手动侧的生效值，差值恒为 0，
    /// 颤音那条（不做差值、直接叠加）照常生效。
    static void WriteTransitionBaseline(SvpTrack track)
    {
        if (track.MainGroup.Notes.All(n => n.InstantMode)) return;
        foreach (var note in track.MainGroup.Notes)
        {
            var system = note.SystemAttributes;
            system.TF0Offset ??= 0.0;
            system.TF0Left ??= DefaultTransitionWidthSeconds;
            system.TF0Right ??= DefaultTransitionWidthSeconds;
            system.DF0Left ??= DefaultTransitionDepthSemitones;
            system.DF0Right ??= DefaultTransitionDepthSemitones;
        }
    }

    // 原版手动模式的内置默认（manual_pitch.rs:70-73）：轨道与音符都没有显式覆盖时取这两个值。
    const double DefaultTransitionWidthSeconds = 0.07;
    const double DefaultTransitionDepthSemitones = 0.15;

    // ── note / part 属性回填 ──

    void ExportNoteProperties(MidiPartInfo part, SvpTrack track, string owner)
    {
        var notes = track.MainGroup.Notes;
        if (part.Notes.Count == 0 || notes.Count == 0) return;

        // Zip 按序配对：多 part 合并后本轨的音符序列已按 onset 排好，逐属性只能按位置对齐。
        foreach (var (source2, target) in part.Notes.OrderBy(n => n.Pos).Zip(notes))
        {
            var source = source2.Properties;

            var phonemes = source.GetString(OpenCyanParams.KeyNotePhonemeStr, string.Empty).Trim();
            var lyric = (source2.Lyric ?? string.Empty).TrimStart();
            if (phonemes.Length == 0 && lyric.Length > 1 && lyric[0] == OpenCyanParams.PhonemeLockMarker)
            {
                // `.` 只是判别入口、不是唱词：剥掉后分别回填 phonemes 与歌词。
                phonemes = lyric[1..].Trim();
                target.Lyrics = lyric[1..];
            }
            if (phonemes.Length > 0) target.Phonemes = phonemes;

            string language = source.GetString(OpenCyanParams.KeyNoteLanguage, string.Empty);
            if (OpenCyanParams.IsClfLanguage(language) && language != track.MainRef.Database.Language)
            {
                target.Attributes.LanguageOverride = language;
                target.Attributes.PhonesetOverride = OpenCyanParams.PhonesetFor(language);
            }

            if (source.GetString(OpenCyanParams.KeyNoteMusicalType, "singing") == "rap")
            {
                target.MusicalType = "rap";
                string accent = source.GetString(OpenCyanParams.KeyNoteAccent, string.Empty);
                if (accent.Length > 0) target.Accent = accent;
                double rTone = source.GetDouble(OpenCyanParams.KeyNoteRTone, 0);
                if (rTone != 0) target.Attributes.RTone = rTone;
                double rIntonation = source.GetDouble(OpenCyanParams.KeyNoteRIntonation, 0);
                if (rIntonation != 0) target.Attributes.RIntonation = rIntonation;
            }
        }
    }

    void ExportPartProperties(MidiPartInfo part, SvpTrack track, string owner)
    {
        string vocalMode = part.Properties.GetString(OpenCyanParams.KeyPartVocalMode, string.Empty);
        if (vocalMode.Length > 0)
        {
            // 「整个 part 用这条声线」= 该声线满强度。原版在用户直接拖过 Vocal Mode 滑条后
            // 也写 Customized：preset 只是界面状态，真数据在 vocalModeParams。
            track.MainRef.Voice.VocalModeInherited = false;
            track.MainRef.Voice.VocalModePreset = "Customized";
            track.MainRef.Voice.VocalModeParams[vocalMode] = 100.0;
        }

        string language = part.Properties.GetString(OpenCyanParams.KeyPartLanguage, string.Empty);
        if (OpenCyanParams.IsClfLanguage(language)) track.MainRef.Database.Language = language;
    }

    // ── 声库身份 ──

    /// part 私有属性值：`name␟version␟language`。分隔符用 U+001F（与声库身份键同一字符），
    /// 库名里可能有的 `|`、`-`、空格都不能当分隔符。
    public static string MakeIdentity(string name, string version, string language)
        => string.Join('\u001f', name, version, language);

    static (string Name, string Version, string Language) SplitIdentity(MidiPartInfo part)
    {
        if (part.Properties.Map.TryGetValue(SidecarIdentityKey, out var value) && value.ToString(out var text))
        {
            var segments = text.Split('\u001f');
            if (segments.Length == 3) return (segments[0], segments[1], segments[2]);
        }
        return (string.Empty, string.Empty, string.Empty);
    }

    (string Name, string Version, string Language) ResolveIdentity(MidiPartInfo part, string owner)
    {
        var triple = SplitIdentity(part);
        if (triple.Name.Length > 0) return triple;

        _report.Degrade("声库身份", owner, 1,
            $"工程里没有 {SidecarIdentityKey}（不是本包导入的工程），导出用设置里的默认三元组 "
            + $"{_options.BankName}|{_options.BankVersion}|{_options.BankLanguage}；"
            + "SynthV 打开时按该名找声库，对不上会提示缺库");
        return (_options.BankName, _options.BankVersion, _options.BankLanguage);
    }

    static string ConvertColor(string color)
    {
        // 宿主 #RRGGBB → SynthV AARRGGBB。
        var hex = color.StartsWith('#') ? color[1..] : color;
        if (hex.Length == 6) return "ff" + hex.ToLowerInvariant();
        if (hex.Length == 8) return hex.ToLowerInvariant();
        return "ff7db235";
    }
}
