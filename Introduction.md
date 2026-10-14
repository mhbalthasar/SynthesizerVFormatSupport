# Synthesizer V Format Support

Synthesizer V Studio 工程文件（`.svp`）的导入 / 导出，参数落到**配套 AI 歌声插件**的参数体系
（下文简称「目标插件」）。纯 C# 实现，只依赖 TuneLab.SDK，无原生组件。

## 时间基

`.svp` 用整数 BLICK 计时：`705,600,000 BLICK = 一个四分音符`；TuneLab 用 PPQ 480 的 tick。
⇒ `1 tick = 1,470,000 BLICK`，十六分音符 = 30 tick，常规时值换算无损。
秒只在 `.svp` 的音符级音高参数（`tF0Offset` / `tF0Left` / `tF0VbrStart` …）里出现，按该位置的
BPM 折算，跨变速点的位置由 tempo 表分段积分。

## 参数映射

| SVP | 落到宿主 |
| --- | --- |
| `parameters.loudness / tension / breathiness / voicing / gender / toneShift` | 力度、张力、气声、声门混合、性别、移调轨（含各自的量程与中性点换算） |
| `parameters.growl / growlRate / distortion / aspiration` | 嘶吼强度、嘶吼速率、失真、气声权重轨（目标插件的扩展轨，真实 SynthV 不写这些） |
| `parameters.pitchDelta`（音分） | 绘制的音高线：`Y = 覆盖该处的标称 MIDI + delta/100` |
| `parameters.vibratoEnv` × `voice.dF0VbrMod` | 宿主内置的颤音包络轨（同为乘性包络，0..2、默认 1） |
| 音符级手动颤音 `dF0Vbr / fF0Vbr / pF0Vbr / tF0VbrStart / Left / Right` | 宿主颤音对象（峰值 = depth×0.5、相位 = −φ/π、淡入淡出走 Attack/Release） |
| `mainGroup.vocalModes.<声线>` 曲线 | part 的音色混合条目（库内通道）+ 该条目自动生成的内容寻址混合比轨 |
| `voice.vocalModeParams`（界面百分数） | part 的「渲染声线」单选（取权重最大的一条） |
| `notes[].phonemes` / `languageOverride` + `phonesetOverride` / rap 的 `accent`、`rTone`、`rIntonation` | 音符属性：显式音素、语言覆盖、音乐类型、声调、说唱音高与音准 |
| `mainRef.database.{name,version,language}` | 声源身份（Type = 目标插件注册的引擎 id，Id = 由库名 + 版本 + 语言算出的声库身份键）；原始三元组另存 part 私有属性 `svp_identity` 供导出回填 |

## 导入时的歌词规则

- **`br`（休止）**：整颗移除。TuneLab 的自动切片会把长 `br` 切成独立片段，导入期就清掉。
  唯一的例外：贴在前一音符尾部的短休止——`br` 起点与前一音符末端的间隔 ≤32 tick 且自身时长
  ≤32 tick 时，保留为一颗静音音符，歌词 `.sil` 并以音素锁定指定音素 `sil`（音高沿用前一音符）。
  语料实测 624 颗 `br` 里只有 8 颗满足该条件，`br` 时长中位 240 tick。
- **`'`（呼吸音）**：暂不支持，导入时去掉歌词头部的连续单引号（语料 86 处，形如 `'yi`、`'爱`）。

## 没装目标插件时

导入前按宿主的扩展根目录（`%APPDATA%\TuneLab\Extensions`，可被 `TUNELAB_DATA_DIR` 改指沙盒）
逐个一级子目录读 `manifest.json`，找 `type=voice` 且 `engine` 等于目标插件引擎 id 的条目——
不靠目录名判（实测装出来的目录名既有引擎名式的、也有带空格的显示名）。

检测不到时**只导入拍速 / 拍号 / 轨道 / 音符 / 音高线**：音色轨、颤音对象、颤音包络、
音色混合与声线选择、音素锁定与语言覆盖、rap 属性全部跳过——这些落的是目标插件声明的
轨键与属性键，没有该插件就没人消费，写进去只是一堆指向缺失引擎的孤儿数据。
声源身份此时留空（不盖目标插件的引擎 id），日志里一条汇总说明装上后重新导入即可拿全。
part 私有属性 `svp_identity` 照常写：它是溯源信息，装了插件之后导出仍能回填正确的声库三元组。

## 已知有损项（导入时逐轨汇总进日志）

- **手动滑音参数**（`tF0Offset / tF0Left / tF0Right / dF0Left / dF0Right`）不搬运。引擎算的是
  `(手动值 − systemAttributes 基线)` 的差值，而多数真实工程根本没存这套基线，离线还原只能是近似；
  音高交由引擎自行预测。
- `attributes.exprGroup`、`alt[]`、`dur[]`、`strength[]`：目标插件无对应通道。且真实文件里
  `dur[]` 与 `strength[]` 在同一颗音符上长度互不相同（语料 3457 颗里有 648 颗不自洽），
  与引擎要求的「逐项等于最终音素数」对不上，不做猜测式对齐。
- 非默认 `pitchTakes` / `timbreTakes` 只取主 take。
- `SVR2Standard` 声库工程：目标插件只挂 AI 链路，导入照常，但需改选 AI 声库才能渲染。
- 伴奏轨（`isInstrumental` + `audio`）不导入音频——格式插件只拿到数据流，拿不到 `.svp`
  所在目录，写相对路径必然指错。
- 非零 `mainRef.blickOffset` 已按绝对位置展开，但引擎侧对该形态的参数曲线坐标变换尚未闭合。

## 导出

写回的 `.svp` 是 version 153 的紧凑 JSON + 单个尾部 NUL。三条固定取舍：位置一律压成工程绝对坐标
（`blickOffset = 0`）；轨道全局值写成曲线；音符 `instantMode = true`（宿主不存引擎自动音高的基线），
只在带手动颤音的轨上补 `systemAttributes` 基线使滑音差值恒为 0。
跨库音色混合需要读目标声库的 32 维音色向量，格式包没有声库访问能力，不导出。

不是本包导入的工程读不到 `svp_identity`，此时用设置里的「导出默认声库名 / 版本 / 语言」兜底。
