# AGENTS.md

## 项目

WPF (.NET 8, C#) Windows 11 桌面应用，通过 UI Automation 劫持内置 LiveCaptions 进程，读取其实时语音识别输出，再经由翻译 API 进行翻译。仅限 Windows，无法在 Linux 上运行。

## 构建与验证

```bash
dotnet restore
dotnet format ./LiveCaptionsTranslator.csproj --verify-no-changes --verbosity diagnostic   # lint/格式检查
dotnet build
dotnet test --verbosity normal
```

CI 还会执行 `dotnet publish`（win-x64 和 win-arm64，含自包含和框架依赖两种模式）。仓库当前**没有单元测试**——`dotnet test` 大概率找不到可运行的测试。

## Git 工作流

**每次完成一个功能更新后，必须立即进行 git commit 保存。** 不要积累多个功能再一次性提交。

```bash
git add -A
git commit -m "功能描述（中文或英文均可，简明扼要）"
```

例如：新增翻译引擎后提交 `git commit -m "添加 LibreTranslate 翻译引擎支持"`。

## 架构

整个应用在 `src/` 下，单一项目，无多包/monorepo 结构。

**入口**：`src/App.xaml.cs` — 启动时运行三个并发后台循环：

1. **`Translator.SyncLoop()`** — 每 25ms 轮询 LiveCaptions UI 元素（`CaptionsTextBlock`，通过 UI Automation），预处理文本，提取最新句子，入队
2. **`Translator.TranslateLoop()`** — 从队列取出文本，分发到 `TranslationTaskQueue`
3. **`Translator.DisplayLoop()`** — 读取翻译结果，更新 UI（主窗口 + 悬浮窗）

**核心协调**：`src/Translator.cs` — 静态类，持有全局状态（`Window`、`Caption`、`Setting`）。非 DI 架构，全部通过静态属性访问。

**LiveCaptions 集成**：`src/utils/LiveCaptionsHandler.cs` — 启动 `LiveCaptions.exe`，通过 `AutomationElement` 查找窗口，隐藏任务栏图标，从 `CaptionsTextBlock` AutomationId 读取字幕文本。使用 Win32 P/Invoke（`src/apis/WindowsAPI.cs`）操控窗口。

**翻译引擎**：`src/apis/TranslateAPI.cs` — 包含 11 个 `Func<string, CancellationToken, Task<string>>` 的字典。添加新翻译引擎需要：
- 在 `TRANSLATE_FUNCTIONS` 字典中添加条目
- 在 `src/models/TranslateAPIConfig.cs` 中添加继承 `TranslateAPIConfig`（或 `BaseLLMConfig`）的配置类
- 在 `Setting` 构造函数的 `configs` 和 `configIndices` 字典中添加条目
- 若为 LLM 类引擎，加入 `LLM_BASED_APIS` 列表；若无需配置，加入 `NO_CONFIG_APIS`

**LLM 请求序列化**：`src/apis/LLMRequestDataFactory.cs` — OpenAI 兼容 API 有回退机制，遇到 400/422 响应时依次尝试多种请求格式（Aliyun、Anthropic、Ollama、OpenAI、XAI 等）。

**任务队列**：`src/models/TranslationTaskQueue.cs` — 新翻译完成时取消队列中所有更早的待处理任务，避免旧结果乱序显示。

**设置**：`src/models/Setting.cs` — 以 `setting.json` 保存在工作目录。使用 `ConfigDictConverter`（位于 `TranslateAPI.cs`）进行 API 配置的多态 JSON 序列化。每次 `OnPropertyChanged` 调用都会自动保存文件。

**历史记录**：SQLite（`Microsoft.Data.Sqlite`）— 数据库文件 `translation_history.db` 在工作目录。

## 关键约定

- `🔤` 标记用于在 LLM 提示词和上下文感知的传统 API 调用中界定源文本
- `RegexPatterns`（`src/utils/RegexPatterns.cs` 中的源生成正则）用于全文预处理
- 所有翻译错误以 `[ERROR]` 或 `[WARNING]` 前缀字符串呈现，不抛异常
- UI 使用 WPF-UI（Fluent 设计）库 — XAML 页面在 `src/pages/`，窗口在 `src/windows/`
- `Caption` 是单例（`Caption.GetInstance()`），作为所有 UI 视图的数据绑定枢纽

## 平台限制

- 需要 Windows 11 22H2+（LiveCaptions 功能）
- 需要 .NET 8 运行时
- 大量 P/Invoke user32.dll — 无跨平台兼容性
- `Interop.UIAutomationClient` 包是 LiveCaptions 集成的关键依赖