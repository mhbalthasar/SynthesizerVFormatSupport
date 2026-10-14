using SynthesizerVFormatSupport.Common;
using SynthesizerVFormatSupport.Model;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace SynthesizerVFormatSupport.Mapping;

/// `.svp` 工程 → TuneLab `ProjectInfo`。
///
/// 结构映射的一条决定性事实：引擎 `expand_track_notes`（compiler.rs:684）把
/// `mainGroup.notes` 与 `groups[]` 引用的 library group 展开成**同一条音符序列**，
/// 且引用必须与 `mainRef` 同声库同声线（`validate_inherited_group_reference`）。
/// 所以一条 SVP 歌声轨 = 宿主的一个 part，而不是多个 part——拆成多 part 会让同一条
/// 参数曲线被逐段重复应用。
///
/// part 锚点恒取 0（全局时间线原点）：SVP 的曲线与音符共用一条全局拍轴，锚点为 0 时
/// 「拍轴坐标」与「part 相对坐标」重合，也让曲线能画到首个音符之前（SynthV 里是常态）。
public sealed class SvpImporter
{
    readonly SvpProject _project;
    readonly ImportReport _report;
    readonly TempoMap _tempo;

    /// OpenCyan 未安装时只搬「结构 + 音符 + 音高线」：音色轨、颤音对象、声线混合、
    /// note 私有属性（音素锁定 / 语言覆盖 / rap）落的都是 OpenCyan 声明的键，没有该插件
    /// 就没有宿主轨可挂，写进去只会得到一堆指向缺失引擎的孤儿数据。
    readonly bool _openCyan;

    public SvpImporter(SvpProject project, ImportReport report, bool openCyanAvailable = true)
    {
        _project = project;
        _report = report;
        _tempo = new TempoMap(project.Time.Tempo);
        _openCyan = openCyanAvailable;
        if (!_openCyan)
            _report.Degrade("导入范围", "工程", 1,
                "本次仅导入拍速 / 拍号 / 轨道 / 音符 / 音高线：音色轨、颤音、音色混合、"
                + "音素与语言覆盖没有可用的参数通道，已跳过。");
    }

    public ProjectInfo Import()
    {
        var project = new ProjectInfo();

        foreach (var mark in _tempo.TempoMarks)
            project.Tempos.Add(new TempoInfo { Pos = TempoMap.BlickToTick(mark.Position), Bpm = mark.Bpm });
        if (project.Tempos.Count == 0)
            project.Tempos.Add(new TempoInfo { Pos = 0, Bpm = 120 });

        foreach (var meter in _project.Time.Meter)
            project.TimeSignatures.Add(new TimeSignatureInfo
            {
                BarIndex = (int)meter.Index,
                Numerator = meter.Numerator,
                Denominator = meter.Denominator,
            });
        if (project.TimeSignatures.Count == 0)
            project.TimeSignatures.Add(new TimeSignatureInfo { BarIndex = 0, Numerator = 4, Denominator = 4 });

        for (int index = 0; index < _project.Tracks.Count; index++)
            project.Tracks.Add(ImportTrack(_project.Tracks[index], index));

        return project;
    }

    TrackInfo ImportTrack(SvpTrack track, int index)
    {
        string owner = TrackOwner(track, index);
        var info = new TrackInfo
        {
            Name = owner,
            Gain = track.Mixer.GainDecibel,
            Pan = Math.Clamp(track.Mixer.Pan, -1, 1),
            Mute = track.Mixer.Mute,
            Solo = track.Mixer.Solo,
            Color = ConvertColor(track.DispColor),
        };

        if (!track.RenderEnabled || !track.Mixer.Display)
            _report.Degrade("导出设置", owner, 1,
                $"renderEnabled={track.RenderEnabled} display={track.Mixer.Display}：逐轨导出开关属宿主私有配置，不随工程交换，需在导出面板重设");

        if (track.MainRef.IsInstrumental || track.MainRef.Audio is not null)
        {
            _report.Degrade("伴奏轨", owner, 1, track.MainRef.Audio is null
                ? "纯 MIDI 伴奏轨没有可导入的音频内容"
                : $"伴奏音频 {track.MainRef.Audio.Filename}（{track.MainRef.Audio.Duration:F2}s）需手工导入——"
                  + "格式插件只拿到 Stream、拿不到 .svp 所在目录，写相对路径必然指错");
            return info;
        }

        info.Parts.Add(ImportPart(track, owner));
        return info;
    }

    MidiPartInfo ImportPart(SvpTrack track, string owner)
    {
        var reference = track.MainRef;
        var database = reference.Database;
        var part = new MidiPartInfo
        {
            Name = owner,
            Pos = 0,
            StartOffset = 0,
            SoundSource = new SoundSourceInfo
            {
                Kind = SourceKind.Voice,
                // 没装 OpenCyan 就不打引擎身份：写了 Type=OpenCyan 的 part 在宿主里是
                // 「指向缺失引擎的声源」，既渲染不出也不好在别处重选。
                Type = _openCyan ? OpenCyanParams.EngineType : string.Empty,
                // 身份键与插件写 SVP 时用的是同一组三元组（OpenCyanBank.DatabaseName/Version/Language
                // 正是回填 database.* 的那一组），所以反向推导是恒等式而非猜测。
                Id = _openCyan ? OpenCyanParams.VoicebankKey(database.Name, database.Version, database.Language) : string.Empty,
            },
        };

        var properties = ImportVoiceIdentity(track, owner);
        var notes = ExpandNotes(track, owner);
        foreach (var note in notes)
            part.Notes.Add(ImportNote(note, track, owner));

        if (_openCyan)
            ImportTimbreCurves(track, owner, part, properties);
        PitchGeometry.Apply(track, notes, _tempo, _report, owner, part, includeOpenCyanChannels: _openCyan);

        if (properties.Count > 0)
            part.Properties = new PropertyObject(properties);

        double contentEnd = part.Notes.Count > 0 ? part.Notes.Max(n => n.Pos + n.Dur) : 0;
        double curveEnd = part.Automations.Values
            .SelectMany(a => a.Points)
            .Concat(part.Pitch.Segments.SelectMany(s => s))
            .Select(p => p.X)
            .DefaultIfEmpty(0)
            .Max();
        part.EndOffset = Math.Max(1, Math.Max(contentEnd, curveEnd));
        return part;
    }

    // ── 声库 / 声线身份 ──

    Map<string, PropertyValue> ImportVoiceIdentity(SvpTrack track, string owner)
    {
        var properties = new Map<string, PropertyValue>();
        var database = track.MainRef.Database;
        var voice = track.MainRef.Voice;

        // 声库三元组原样留一份：SoundSource.Id 是哈希、不可逆推，导出回填 database.* 只能靠这条
        // 私有属性（宿主整表存取 part.Properties，未声明的键不裁）。没装 OpenCyan 时也照写——
        // 它是溯源信息、不是 OpenCyan 的参数通道。
        properties[SvpExporter.SidecarIdentityKey] = PropertyValue.Create(
            SvpExporter.MakeIdentity(database.Name, database.Version, database.Language));

        if (!_openCyan) return properties;

        if (database.BackendType != "SVR2AI")
            _report.Degrade("声库后端", owner, 1,
                $"backendType=\"{database.BackendType}\"：本包面向的歌声引擎只挂 AI 四模型链路，Standard 声库工程需改选对应的 AI 声库才能渲染");

        if (OpenCyanParams.IsClfLanguage(database.Language))
            properties[OpenCyanParams.KeyPartLanguage] = PropertyValue.Create(database.Language);
        else if (!string.IsNullOrEmpty(database.Language))
            _report.Degrade("语言", owner, 1,
                $"声库默认语言 {database.Language} 不在 CLF 五语种内，part 语言留空、由宿主回退声库默认");

        if (track.MainRef.BlickOffset != 0)
            _report.Degrade("坐标", owner, 1,
                $"mainRef.blickOffset={track.MainRef.BlickOffset}：位置已按绝对时间展开，但引擎侧「非零 offset 下的参数曲线坐标变换」尚未闭合，"
                + "这份工程交给歌声引擎渲染仍会被拒");

        // Vocal Mode：静态权重（voice.vocalModeParams，界面百分数）在宿主侧只有一个「声线」单选，
        // 取权重最大的一条；其余静态权重与逐声线曲线走 properties[xsy_mix] 的库内混合通道。
        if (voice.VocalModeParams.Count > 0)
        {
            var dominant = voice.VocalModeParams.OrderByDescending(kvp => kvp.Value).First();
            if (dominant.Value > 0)
            {
                properties[OpenCyanParams.KeyPartVocalMode] = PropertyValue.Create(dominant.Key);
                var others = voice.VocalModeParams
                    .Where(kvp => kvp.Key != dominant.Key && kvp.Value > 0)
                    .OrderByDescending(kvp => kvp.Value)
                    .Select(kvp => $"{kvp.Key}={kvp.Value:F0}%")
                    .ToList();
                if (others.Count > 0)
                    _report.Degrade("声线权重", owner, others.Count,
                        $"只保留最大的 {dominant.Key}={dominant.Value:F0}%，其余静态权重无对应通道（{string.Join(", ", others)}）");
            }
        }

        if (!string.IsNullOrEmpty(voice.VocalModePreset) && voice.VocalModePreset != "Customized")
            _report.Degrade("声线预设", owner, 1,
                $"vocalModePreset={voice.VocalModePreset}：命名预设要读声库才能展开成权重，无法导入");

        if (voice.RenderMode is { Length: > 0 })
            _report.Degrade("渲染模式", owner, 1, $"voice.renderMode=\"{voice.RenderMode}\" 引擎侧尚未接入");

        return properties;
    }

    // ── 音符 ──

    /// 展开 mainGroup 与 groups[] 引用成一条音符序列，口径与引擎 `expand_track_notes` 一致：
    /// 绝对位置 = 音符 onset + 引用 blickOffset；引用的 pitchOffset 直接加在展开副本的 MIDI 音高上
    /// （引擎实测它就是 DDS context 里那一个半音的平移，不是后处理项）。
    List<SvpNote> ExpandNotes(SvpTrack track, string owner)
    {
        var expanded = new List<SvpNote>(track.MainGroup.Notes);

        if (track.Groups.Count > 0)
        {
            var library = new Dictionary<string, SvpNoteGroup>(StringComparer.Ordinal);
            foreach (var group in _project.Library)
                library.TryAdd(group.Uuid, group);

            foreach (var reference in track.Groups)
            {
                if (!library.TryGetValue(reference.GroupId, out var group))
                    continue;   // reader 阶段已报「引用的 library group 不存在」
                if (reference.BlickAbsoluteBegin is >= 0 || reference.BlickAbsoluteEnd is >= 0)
                    _report.Degrade("引用裁剪", owner, 1,
                        $"blickAbsoluteBegin={reference.BlickAbsoluteBegin} / End={reference.BlickAbsoluteEnd}：区间裁剪未实现，整段引用原样展开");
                long shift = reference.BlickOffset - track.MainRef.BlickOffset;
                foreach (var note in group.Notes)
                    expanded.Add(Shift(note, shift, reference.PitchOffset));
            }
        }

        expanded.Sort((a, b) => a.Onset.CompareTo(b.Onset));
        int overlaps = 0;
        for (int i = 1; i < expanded.Count; i++)
            if (expanded[i].Onset < expanded[i - 1].End) overlaps++;
        if (overlaps > 0)
            _report.Degrade("重叠音符", owner, overlaps,
                "引擎会归一化重叠（完全被包裹者丢弃、相邻平分），导入按原样保留，宿主显示可能与引擎渲染不一致");
        return expanded;
    }

    static SvpNote Shift(SvpNote note, long blickDelta, int pitchDelta)
    {
        if (blickDelta == 0 && pitchDelta == 0) return note;
        return new SvpNote
        {
            MusicalType = note.MusicalType,
            Onset = note.Onset + blickDelta,
            Duration = note.Duration,
            Lyrics = note.Lyrics,
            Phonemes = note.Phonemes,
            Accent = note.Accent,
            Pitch = Math.Clamp(note.Pitch + pitchDelta, 0, 127),
            Detune = note.Detune,
            InstantMode = note.InstantMode,
            Attributes = note.Attributes,
            SystemAttributes = note.SystemAttributes,
            PitchTakes = note.PitchTakes,
            TimbreTakes = note.TimbreTakes,
        };
    }

    NoteInfo ImportNote(SvpNote note, SvpTrack track, string owner)
    {
        double onsetTick = TempoMap.BlickToTick(note.Onset + track.MainRef.BlickOffset);
        double durationTick = Math.Max(TempoMap.BlickToTick(note.Duration), 1.0);

        var info = new NoteInfo
        {
            Pos = onsetTick,
            Dur = durationTick,
            Pitch = Math.Clamp(note.Pitch, 0, 127),
            Lyric = note.Lyrics,
        };
        if (note.Pitch is < 0 or > 127)
            _report.Degrade("音域", owner, 1, $"音符 MIDI {note.Pitch} 超出 0..127，已钳位");

        // 没装 OpenCyan：音素锁定 / 语言覆盖 / rap 与表情组等都落在 OpenCyan 声明的 note 键上，
        // 无处可挂 ⇒ 音符只保留位置、时值、音高、唱词。
        if (!_openCyan) return info;

        var attributes = note.Attributes;
        var props = new Map<string, PropertyValue>();

        // 显式音素走 note 属性 phoneme_str（PhonemeLock 的判别顺序 1），**不**给歌词加 `.` 标记：
        // 标记会让歌词本身变成内容，导出回 SVP 时唱词就成了 ".an"。
        if (!string.IsNullOrWhiteSpace(note.Phonemes))
            props[OpenCyanParams.KeyNotePhonemeStr] = PropertyValue.Create(note.Phonemes.Trim());

        var language = attributes.LanguageOverride;
        if (!string.IsNullOrEmpty(language))
        {
            if (!OpenCyanParams.IsClfLanguage(language))
                _report.Degrade("语言", owner, 1, $"音符 languageOverride={language} 不在 CLF 五语种内，已忽略");
            else if (string.IsNullOrEmpty(attributes.PhonesetOverride))
                _report.Degrade("语言", owner, 1,
                    $"音符只给了 languageOverride={language} 而缺 phonesetOverride：语言与音素集必须成对下发");
            else if (OpenCyanParams.PhonesetFor(language) != attributes.PhonesetOverride)
                _report.Degrade("语言", owner, 1,
                    $"({language}, {attributes.PhonesetOverride}) 不是 CLF 的合法配对（应为 {OpenCyanParams.PhonesetFor(language)}），已忽略");
            else if (language != track.MainRef.Database.Language)
                props[OpenCyanParams.KeyNoteLanguage] = PropertyValue.Create(language);
        }

        if (note.MusicalType == "rap")
        {
            props[OpenCyanParams.KeyNoteMusicalType] = PropertyValue.Create("rap");
            if (int.TryParse(note.Accent, out int accent) && accent is >= 1 and <= 5)
                props[OpenCyanParams.KeyNoteAccent] = PropertyValue.Create(accent.ToString());
            else if (!string.IsNullOrEmpty(note.Accent))
                _report.Degrade("声调", owner, 1, $"rap 音符 accent=\"{note.Accent}\" 不在 1..5，已丢弃");
            if (attributes.RTone is { } rTone && rTone != 0)
                props[OpenCyanParams.KeyNoteRTone] = PropertyValue.Create(Math.Clamp(rTone, -1, 1));
            if (attributes.RIntonation is { } rInt && rInt != 0)
                props[OpenCyanParams.KeyNoteRIntonation] = PropertyValue.Create(Math.Clamp(rInt, -1, 1));
        }
        else if (!string.IsNullOrEmpty(note.Accent))
        {
            // 引擎对「singing 带 accent」硬报错（parse_rap_accent），这道闸必须在导入侧就关掉。
            _report.Degrade("声调", owner, 1, $"singing 音符带 accent=\"{note.Accent}\"，已丢弃");
        }

        if (attributes.ExprGroup is { Length: > 0 })
            _report.Degrade("表情组", owner, 1, "exprGroup（原版按音高命名的表情组）没有对应的参数通道");
        if (attributes.Alt is { Count: > 0 })
            _report.Degrade("ALT 组", owner, 1,
                "alt[] 要与 CLF 最终音素逐项等长，而真实文件里它与 dur[] 长度互不相同（语料实测 648/3457 处不自洽），无法可靠搬运");
        if (attributes.Dur is { Count: > 0 } || attributes.Strength is { Count: > 0 })
            _report.Degrade("音素时长/强度", owner, 1,
                "dur[]/strength[] 是相对倍率，绝对秒数要由 Duration 模型现场推理才能定；离线换算会错，故不搬运");
        if (attributes.DF0Jitter is { } jitter && jitter != 0)
            _report.Degrade("音高抖动", owner, 1, $"dF0Jitter={jitter}：引擎接受该字段但不消费（实测改它不跑 DDS）");
        if (!note.PitchTakes.IsNeutralPlaceholder || !note.TimbreTakes.IsNeutralPlaceholder)
            _report.Degrade("多 take", owner, 1,
                $"activeTakeId={note.PitchTakes.ActiveTakeId}/{note.TimbreTakes.ActiveTakeId}：非默认 take 的模型输入语义引擎尚未实现，只导入主 take");

        if (props.Count > 0)
            info.Properties = new PropertyObject(props);
        return info;
    }

    // ── 音色参数曲线 ──

    void ImportTimbreCurves(SvpTrack track, string owner, MidiPartInfo part, Map<string, PropertyValue> properties)
    {
        var group = track.MainGroup;
        var voice = track.MainRef.Voice;
        long offset = track.MainRef.BlickOffset;

        foreach (var (name, curve) in group.Parameters)
        {
            // pitchDelta 与 vibratoEnv 归音高几何处理（PitchGeometry）。
            if (curve.Points.Count == 0 || name is "pitchDelta" or "vibratoEnv") continue;

            var spec = OpenCyanParams.TrackFor(name);
            if (spec is null)
            {
                _report.Degrade("参数曲线", owner, 1, $"{name}：当前声源没有声明这条轨，曲线被丢弃");
                continue;
            }

            double global = HasGlobalField(spec.EngineName) ? GlobalOf(voice, spec.EngineName) : OpenCyanParams.NeutralOf(spec.EngineName);
            double neutral = OpenCyanParams.NeutralOf(spec.EngineName);
            var automation = new AutomationInfo
            {
                DefaultValue = spec.ToHost(global + neutral).Value,
            };
            automation.Points.AddRange(ConvertCurvePoints(spec, curve, global, offset, owner));
            part.Automations[spec.KeyId] = automation;
        }

        // 只有轨道全局值、没有曲线：仍要落成一条基线，否则全局值无处表达（宿主默认值 = 中性）。
        foreach (var spec in OpenCyanParams.TimbreTracks)
        {
            if (part.Automations.ContainsKey(spec.KeyId) || !HasGlobalField(spec.EngineName)) continue;
            double global = GlobalOf(voice, spec.EngineName);
            double neutral = OpenCyanParams.NeutralOf(spec.EngineName);
            if (Math.Abs(global - neutral) < 1e-12) continue;
            part.Automations[spec.KeyId] = new AutomationInfo { DefaultValue = spec.ToHost(global).Value };
        }

        ImportVocalModeCurves(track, owner, part, properties);
    }

    /// 有轨道级全局字段的参数名（其余轨只有曲线通道，全局值恒等于其中性值）。
    /// 判据必须按「有没有这个字段」列，不能对全部轨取 ?? 0：voicing / growlRate 的中性值
    /// 分别是 1.0 与 0.5，把「无字段」当 0 会让下面那段「只给全局值就落一条基线」的逻辑
    /// 凭空造出一条 −1 的 VOI 轨，把声门闭合直接压死。
    static bool HasGlobalField(string engineName) => engineName switch
    {
        "loudness" or "tension" or "breathiness" or "gender" or "toneShift" => true,
        _ => false,
    };

    static double GlobalOf(SvpVoice voice, string engineName) => engineName switch
    {
        "loudness" => voice.ParamLoudness ?? 0,
        "tension" => voice.ParamTension ?? 0,
        "breathiness" => voice.ParamBreathiness ?? 0,
        "gender" => voice.ParamGender ?? 0,
        "toneShift" => voice.ParamToneShift ?? 0,
        // SVP 的 Voice 没有 voicing / growl / distortion / aspiration 的全局字段，只有曲线。
        _ => 0,
    };

    List<Point> ConvertCurvePoints(OpenCyanParams.TimbreTrack spec, SvpAutomation curve,
                                   double global, long offset, string owner)
    {
        var points = new List<Point>();
        int clamped = 0;
        double neutral = OpenCyanParams.NeutralOf(spec.EngineName);

        for (int i = 0; i < curve.Points.Count; i++)
        {
            var point = curve.Points[i];
            var converted = spec.ToHost(global + point.Value);
            if (converted.Clamped) clamped++;
            points.Add(new Point(TempoMap.BlickToTick(point.Blick + offset), converted.Value));

            // 线性段补中点：宿主是 Hermite，两锚点之间会鼓包；补一个共线点把形状钉回直线。
            if (curve.Mode == SvpInterpolationMode.Linear && i + 1 < curve.Points.Count)
            {
                long middle = point.Blick + (curve.Points[i + 1].Blick - point.Blick) / 2;
                var mid = spec.ToHost(global + curve.Evaluate(middle, neutral));
                if (mid.Clamped) clamped++;
                points.Add(new Point(TempoMap.BlickToTick(middle + offset), mid.Value));
            }
        }

        if (clamped > 0)
            _report.Degrade("量程钳位", owner, clamped,
                $"{spec.KeyId} 有 {clamped} 个点超出宿主量程 [{spec.Min}, {spec.Max}]，已钳位（引擎不钳，超量程会改变原语义）");
        return points;
    }

    void ImportVocalModeCurves(SvpTrack track, string owner, MidiPartInfo part, Map<string, PropertyValue> properties)
    {
        var database = track.MainRef.Database;
        var entries = new List<PropertyValue>();
        foreach (var (style, curve) in track.MainGroup.VocalModes)
        {
            if (curve.Points.Count == 0) continue;

            // 库内混合：XSY 条目的声库字段留空 = part 当前声库，音色名 = 声线名；
            // 混合比落在内容寻址轨 XSY:{salt} 上（salt 口径同插件 XsyTracks.HashIdentity）。
            entries.Add(PropertyValue.Create(new PropertyObject(new Map<string, PropertyValue>
            {
                [OpenCyanParams.KeyXsyVoice] = PropertyValue.Create(string.Empty),
                [OpenCyanParams.KeyXsyStyle] = PropertyValue.Create(style),
            })));

            var automation = new AutomationInfo { DefaultValue = 0 };
            int clamped = 0;
            foreach (var point in curve.Points)
            {
                // SVP 的 Vocal Mode 曲线是界面百分数（进引擎 ÷100），宿主轨值域 0..1。
                double value = point.Value / 100.0;
                if (value is < 0 or > 1)
                {
                    value = Math.Clamp(value, 0, 1);
                    clamped++;
                }
                automation.Points.Add(new Point(TempoMap.BlickToTick(point.Blick + track.MainRef.BlickOffset), value));
            }
            if (clamped > 0)
                _report.Degrade("量程钳位", owner, clamped, $"vocalModes.{style} 有百分比超出 0..100，已钳位");

            part.Automations[OpenCyanParams.XsyTrackKey(
                OpenCyanParams.XsySalt(
                    OpenCyanParams.VoicebankKey(database.Name, database.Version, database.Language),
                    database.Version, style))] = automation;
        }

        // 直接写进 part 属性袋：这里是整表替换的 PropertyObject 还没成形，
        // 早一步换成 PropertyObject 会把已写入的 Language / vocal_mode 键抹掉。
        if (entries.Count > 0)
            properties[OpenCyanParams.KeyXsyMix] = PropertyValue.Create(new PropertyArray(entries));
    }

    static string ConvertColor(string dispColor)
    {
        // SynthV 存 AARRGGBB（语料里恒 8 位、alpha 为 ff）；宿主用 #RRGGBB。
        if (dispColor.Length == 8) return "#" + dispColor[2..];
        if (dispColor.Length == 6) return "#" + dispColor;
        return string.Empty;
    }

    static string TrackOwner(SvpTrack track, int index) =>
        string.IsNullOrWhiteSpace(track.Name) ? $"音轨 {index + 1}" : track.Name;
}
