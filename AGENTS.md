# PROJECT KNOWLEDGE BASE

**Generated:** 2026-04-15
**Commit:** 74c437e
**Branch:** master

使用中文回复

## OVERVIEW

WPF (.NET 8, C#) Win11 桌面应用 — 通过 UI Automation 劫持 LiveCaptions 进程读取实时字幕，经翻译 API 翻译。仅限 Windows。

## STRUCTURE

```
src/
├── apis/       # 翻译引擎 + LLM请求工厂 + Win32 P/Invoke（混合关注点）
├── controls/   # 静态辅助类(SnackbarHost) + 自定义控件(StrokeDecorator)
├── models/     # 数据模型 + 业务逻辑(TaskQueue/Setting) + 多态配置
├── pages/      # WPF-UI Fluent 页面（4 组 .xaml + .xaml.cs）
├── utils/      # LiveCaptions集成 + 源生成正则 + SQLite历史 + Win32
├── windows/    # 4 个 Window（主窗口/悬浮/设置/欢迎）
├── App.xaml.cs # 入口 — 启动3个并发后台循环
└── Translator.cs # 静态协调器 — 全局状态枢纽
```

## WHERE TO LOOK

| 任务 | 位置 | 备注 |
|------|------|------|
| 添加翻译引擎 | `src/apis/TranslateAPI.cs` + `src/models/TranslateAPIConfig.cs` + `src/models/Setting.cs` | 4步注册，见子目录AGENTS.md |
| 修改字幕轮询逻辑 | `src/Translator.cs` SyncLoop | 25ms Thread.Sleep轮询 |
| 修改翻译流程 | `src/Translator.cs` TranslateLoop → `src/models/TranslationTaskQueue.cs` | 取消旧任务机制 |
| 修改UI显示 | `src/Translator.cs` DisplayLoop → `src/models/Caption.cs` | 单例绑定枢纽 |
| 操控LiveCaptions窗口 | `src/utils/LiveCaptionsHandler.cs` + `src/apis/WindowsAPI.cs` | UI Automation + P/Invoke |
| LLM请求格式回退 | `src/apis/LLMRequestDataFactory.cs` | 8种格式依次尝试 |
| 设置持久化 | `src/models/Setting.cs` | setting.json，OnPropertyChanged自动保存 |
| 历史记录 | `src/utils/HistoryLogger.cs`（类名SQLiteHistoryLogger） | translation_history.db |
| UI页面/窗口 | `src/pages/` + `src/windows/` | WPF-UI Fluent设计 |
| 版本号 | `Properties/AssemblyInfo.tt` | T4模板，日期驱动 `1.7.{yyMM/2}.{ddHH}` |

## CONVENTIONS

- **非 DI 架构** — 全部通过 `Translator` 静态属性访问全局状态，无 IoC 容器
- **`Caption` 单例** — `Caption.GetInstance()` 作为所有 UI 视图的数据绑定枢纽
- **`🔤` 标记** — LLM提示词和传统API上下文感知中界定源文本，翻译后必须移除
- **源生成正则** — 使用 `[GeneratedRegex]` 的 `RegexPatterns`，不手写 `new Regex()`
- **LLM推理禁用** — 所有LLM请求数据中 reasoning/thinking 强制disabled或minimal，减少延迟
- **双重 AssemblyInfo** — `src/AssemblyInfo.cs`（手动）+ `Properties/AssemblyInfo.cs`（T4自动生成）
- **工作目录存储** — setting.json/translation_history.db 在 `Directory.GetCurrentDirectory()`
- **命名空间不一致** — `StyleEnums.cs` 用大写 `Utils`，其余用小写 `utils`
- **类名与文件名不匹配** — `HistoryLogger.cs` → 类名 `SQLiteHistoryLogger`

## ANTI-PATTERNS（禁止）

- **禁止抛出翻译异常** — 必须返回 `[ERROR]`/`[WARNING]` 前缀字符串，唯一例外：`OperationCanceledException` 必须 rethrow
- **禁止遗漏新引擎注册** — 必须在4处同步注册：TRANSLATE_FUNCTIONS + TranslateAPIConfig + configs + configIndices
- **禁止遗漏 Setting 自动保存** — 新属性 setter 必须调用 OnPropertyChanged（触发自动保存）
- **禁止直接修改 pendingTextQueue** — 它是 SyncLoop↔TranslateLoop 的桥梁（单生产者单消费者）
- **禁止在 TranslationTaskQueue 之外管理翻译任务** — 新任务取消旧任务机制在 Queue 内维护
- **禁止启用 LLM 推理/思考** — 必须禁用以减少响应延迟
- **禁止使用非源生成正则** — 文本预处理用 RegexPatterns，不手写 `new Regex()`
- **禁止修改 🔤 标记语义** — 界定源文本，翻译后必须移除

## COMMANDS

```bash
dotnet restore
dotnet format ./LiveCaptionsTranslator.csproj --verify-no-changes --verbosity diagnostic   # lint
dotnet build
dotnet test --verbosity normal  # 无测试项目，此命令实质为空操作
```

CI 发布4变体：win-x64/win-arm64 × 自包含/框架依赖，单文件模式。

## NOTES

- 仓库**没有单元测试**
- **每次功能更新后必须立即 git commit**，不要积累
- 静态构造函数链式触发：App() → Translator.SyncLoop → static Translator() → Setting.Load() → TranslateAPI字典初始化 → SQLiteHistoryLogger静态构造
- App() 中 `Translator.Setting?.Save()` 无效 — 此时静态构造函数未执行，Setting为null
- `SupportedOSPlatformVersion=7.0` 但实际需 Win11 22H2+
- OverlayWindow 按需创建/销毁，非启动时创建