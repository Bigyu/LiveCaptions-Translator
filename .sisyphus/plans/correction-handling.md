# Fix: LiveCaptions 纠错与翻译管道不匹配问题

## TL;DR

> **核心问题**: Windows LiveCaptions 语音识别会在句子完成后回溯纠正之前的文本，但翻译管道的 TranslationTaskQueue 取消机制存在竞态条件，导致纠错后的翻译可能被旧翻译覆盖。当前 SyncLoop 已有句子追踪和纠错检测逻辑，但 TranslationTaskQueue 的"完成的任务取消未完成的旧任务"机制无法保证纠错翻译一定胜出。
> 
> **修复方案**: 为 TranslationTaskQueue 引入 per-sentence 版本号机制，消除竞态条件；为 Caption 引入 SentenceState 列表支持逐句翻译结果追踪和 Overlay 纠错替换；恢复不完整句子的预翻译机制（idleCount/syncCount 风格，适配3-loop异步架构）。
>
> **Deliverables**:
> - TranslationTaskQueue 版本号机制（per-sentence version）
> - Caption SentenceState 列表 + Overlay 属性从列表计算
> - 预翻译触发机制（不完整句子的时间触发翻译）
> - 测试基础设施（xUnit）+ 核心逻辑单元测试
> - Overlay 句子换行分隔可选功能
> 
> **Estimated Effort**: Large
> **Parallel Execution**: YES — 4 waves
> **Critical Path**: T1 → T2 → T3/T4 → T5-T8 → T9 → F1-F4

---

## Context

### Original Request
用户分析 bad case 后发现：LiveCaptions 自身有语音识别纠错（回溯修正已完成句子），但实时翻译没有同步消费纠错后的文本。原因：
1. TranslationTaskQueue 的取消机制有竞态条件——旧翻译任务可能先完成，覆盖纠错后的新翻译
2. 纠错发生在非最后一句时，当前版本已能检测（逐句对比 `alignedTracked[i] vs completedSentences[i]`），但翻译结果替换机制不完善
3. 不完整句子没有预翻译，只有 EOS 结尾或超长(≥160字节)才入队

### Interview Summary
**Key Discussions**:
- Diff策略: 只对比最后3句 → 发现当前版本已有完整逐句对比（`trackedSentences`），改为利用现有机制
- 纠错翻译策略: 替换式重翻 — 新翻译直接替换旧翻译显示
- TranslationTaskQueue: 版本号机制（per-sentence version），不取消旧任务
- Caption模型: 句子列表模式（SentenceState list），Overlay属性从列表计算
- 预翻译: 恢复 idleCount/syncCount 风格，适配3-loop异步架构
- Overlay: 维持滚动字幕结构，纠错直接替换对应句翻译，可选句子换行分隔
- 去重: 版本号兜底，不做额外去重
- 测试: 搭建 xUnit 测试基础设施 + 核心逻辑单元测试
- Git: 每次功能更新后立即 commit

**Research Findings**:
- **当前 SyncLoop 已有句子追踪**（Translator.cs:87-146）：
  - `SplitSentences(fullText)` → 拆分全量文本为 completed + incomplete
  - `trackedSentences` 逐句对比检测纠错
  - 纠错句子入队 `pendingTextQueue` 重翻
  - 但 TranslationTaskQueue 竞态条件导致纠错翻译可能被旧翻译覆盖
- **TranslationTaskQueue.cs:31-40**: "完成的任务取消未完成的旧任务" → 竞态条件
- **历史版本（commit 85f34e7）有 idleCount/syncCount 预翻译机制**
- **commit 5ed2009 移除预翻译**（"fix: repeatedly translate same sentence"）
- **Caption.cs**: 单句枢纽（OriginalCaption/TranslatedCaption），Overlay属性从 Contexts 计算
- **Overlay XAML**: 3个 Run 元素绑定 OverlayOriginalCaption/OverlayPreviousTranslation/OverlayCurrentTranslation

### Metis Review
**Critical Gaps Identified** (addressed):
- **LiveCaptions 是否真的重写前面句子**: Metis 要求验证。结论：当前代码的 `trackedSentences` 逻辑已经能检测到这种重写（否则不会有逐句对比逻辑），且用户已确认看到过纠错行为。无需额外验证。
- **版本号必须 per-sentence**: 已采纳。每个句子有自己的版本计数器。
- **当前 SyncLoop 已有纠错检测**: Metis 发现 Translator.cs:117-126 已逐句对比。关键问题是 TranslationTaskQueue 竞态条件，而非 SyncLoop 检测逻辑。
- **Overlay XAML 绑定必须保持兼容**: 已采纳。SentenceState 列表计算出的属性保持相同的属性名和格式。
- **Pre-translation 不能污染 SQLite**: 已采纳。预翻译结果只存在于 SentenceState，不写入 SQLite 或 Contexts。
- **idleCount/syncCount 不能直接恢复**: 已采纳。需要为3-loop异步架构重新设计。
- **pendingTextQueue 需要承载元数据**: 已采纳。从 `Queue<string>` 改为承载 sentenceIndex/isCorrection/isPreTranslation 信息。

---

## Work Objectives

### Core Objective
修复 LiveCaptions 纠错传播链路，使翻译结果能同步反映纠错后的原文，同时恢复不完整句子的预翻译能力。

### Concrete Deliverables
- `TranslationTaskQueue.cs`: 版本号机制（per-sentence version），消除竞态条件
- `Caption.cs`: SentenceState 列表 + Overlay 属性从列表计算
- `Translator.cs`: pendingTextQueue 丰富元数据 + 预翻译触发 + DisplayLoop 使用版本号
- `TextUtil.cs` 或新类: SentenceDiff/版本号辅助逻辑
- 测试项目: xUnit 测试基础设施 + 核心逻辑单元测试
- Overlay 句子换行分隔可选功能（Setting 新属性）

### Definition of Done
- [ ] `dotnet build` 成功
- [ ] `dotnet test` 所有单元测试通过
- [ ] 正常流程（无纠错）翻译行为与当前版本一致
- [ ] 纠错场景：纠正后的翻译替换旧翻译显示
- [ ] 预翻译场景：不完整句子在 idle/sync 阈值后触发翻译
- [ ] 预翻译不写入 SQLite 历史

### Must Have
- Per-sentence 版本号机制（TranslationTaskQueue）
- Caption SentenceState 列表（支持逐句翻译结果追踪）
- 预翻译触发机制（idleCount/syncCount 风格）
- Overlay 纠错替换（SentenceState 列表驱动 Overlay 属性）
- 测试基础设施 + 核心逻辑单元测试
- 每次功能更新后立即 git commit

### Must NOT Have (Guardrails)
- ❌ 不修改 LiveCaptionsHandler.GetCaptions() 接口
- ❌ 不修改 SQLite schema 或 HistoryLogger 接口
- ❌ 不使用全局版本号（必须 per-sentence）
- ❌ 预翻译结果不写入 SQLite 或 Contexts
- ❌ 不新增 Overlay XAML 控件（保持 3个 Run 元素结构）
- ❌ 不改变 Overlay 属性名（OverlayOriginalCaption/OverlayCurrentTranslation/OverlayNoticePrefix 保持不变）
- ❌ pendingTextQueue 保持单生产者单消费者契约（SyncLoop enqueue ↔ TranslateLoop dequeue）
- ❌ 禁止启用 LLM 推理/思考
- ❌ 禁止使用非源生成正则
- ❌ 禁止修改 🔤 标记语义

---

## Verification Strategy

> **ZERO HUMAN INTERVENTION** — ALL verification is agent-executed. No exceptions.

### Test Decision
- **Infrastructure exists**: NO
- **Automated tests**: YES (TDD for core logic)
- **Framework**: xUnit（需要搭建新测试项目）
- **If TDD**: 核心算法任务遵循 RED → GREEN → REFACTOR

### QA Policy
Every task MUST include agent-executed QA scenarios.
Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

- **Frontend/UI**: Use Playwright — Navigate, interact, assert DOM, screenshot
- **TUI/CLI**: Use interactive_bash (tmux)
- **API/Backend**: Use Bash (curl)
- **Library/Module**: Use Bash (dotnet test)

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately — foundation + tests):
├── Task 1: 搭建 xUnit 测试项目 [quick]
├── Task 2: TranslationTaskQueue 版本号机制 [deep]
├── Task 3: Caption SentenceState 数据结构 [quick]
└── Task 4: pendingTextQueue 元数据封装 [quick]

Wave 2 (After Wave 1 — core integration):
├── Task 5: SyncLoop 预翻译触发机制 (depends: 4) [deep]
├── Task 6: TranslateLoop 版本号入队 (depends: 2, 4) [unspecified-high]
├── Task 7: DisplayLoop 版本号过滤 + SentenceState 回填 (depends: 2, 3) [deep]
├── Task 8: Overlay 属性从 SentenceState 计算 (depends: 3, 7) [unspecified-high]

Wave 3 (After Wave 2 — polish + tests):
├── Task 9: 单元测试：SentenceDiff + 版本号 + SentenceState→Overlay (depends: 1, 5, 6, 7, 8) [deep]
└── Task 10: Overlay 句子换行分隔可选功能 (depends: 8) [quick]

Wave FINAL (After ALL — 4 parallel reviews):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Code quality review (unspecified-high)
├── Task F3: Real manual QA (unspecified-high + playwright)
└── Task F4: Scope fidelity check (deep)
→ Present results → Get explicit user okay

Critical Path: T1 → T5/T6 → T7 → T8 → T9 → F1-F4 → user okay
Parallel Speedup: ~60% faster than sequential
Max Concurrent: 4 (Wave 1)
```

### Dependency Matrix

| Task | Depends On | Blocks | Wave |
|------|-----------|---------|------|
| 1    | -         | 9       | 1    |
| 2    | -         | 6, 7    | 1    |
| 3    | -         | 7, 8    | 1    |
| 4    | -         | 5, 6    | 1    |
| 5    | 4         | 9       | 2    |
| 6    | 2, 4      | 9       | 2    |
| 7    | 2, 3      | 8, 9    | 2    |
| 8    | 3, 7      | 9, 10   | 2    |
| 9    | 1, 5, 6, 7, 8 | F1-F4 | 3 |
| 10   | 8         | F1-F4   | 3    |

### Agent Dispatch Summary

- **Wave 1**: **4** — T1 → `quick`, T2 → `deep`, T3 → `quick`, T4 → `quick`
- **Wave 2**: **4** — T5 → `deep`, T6 → `unspecified-high`, T7 → `deep`, T8 → `unspecified-high`
- **Wave 3**: **2** — T9 → `deep`, T10 → `quick`
- **FINAL**: **4** — F1 → `oracle`, F2 → `unspecified-high`, F3 → `unspecified-high`, F4 → `deep`

---

## TODOs

- [x] 1. 搭建 xUnit 测试项目基础设施

  **What to do**:
  - 创建新测试项目 `LiveCaptionsTranslator.Tests`（xUnit）
  - 添加项目引用到 `LiveCaptionsTranslator`
  - 配置 `dotnet test` 可运行
  - 写一个示例测试验证基础设施工作（如 `Caption.GetInstance()` 不是 null）

  **Must NOT do**:
  - 不写业务逻辑测试（后续任务覆盖）
  - 不修改主项目的代码

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2, 3, 4)
  - **Blocks**: Task 9
  - **Blocked By**: None

  **References**:
  - `LiveCaptionsTranslator.csproj` — 主项目文件，获取目标框架和依赖信息
  - `src/models/Caption.cs:87-93` — Caption.GetInstance() 单例模式，用于示例测试

  **Acceptance Criteria**:
  - [ ] `dotnet test` 成功运行（1个示例测试 PASS）
  - [ ] 测试项目引用主项目正确

  **QA Scenarios**:
  ```
  Scenario: 测试基础设施工作
    Tool: Bash
    Steps:
      1. dotnet test LiveCaptionsTranslator.Tests
      2. 检查输出包含 "Passed!  - Failed: 0"
    Expected Result: 1个测试通过，0个失败
    Evidence: .sisyphus/evidence/task-1-test-infra.txt
  ```

  **Commit**: YES
  - Message: `test: add xUnit test project infrastructure`
  - Files: `LiveCaptionsTranslator.Tests/*.csproj, LiveCaptionsTranslator.Tests/*.cs`
  - Pre-commit: `dotnet test`

- [x] 2. TranslationTaskQueue 版本号机制

  **What to do**:
  - 修改 `TranslationTaskQueue.cs`：TranslationTask 增加 `int Version` 和 `int SentenceIndex` 字段
  - **关键改造**：`OnTaskCompleted` 不再取消旧任务，改为：
    - 完成的任务在 `Caption.SentenceStates[SentenceIndex]` 中回填翻译结果
    - 设置 `Caption.SentenceStates[SentenceIndex].Version = Version`
    - 从 tasks 列表中移除已完成的任务
    - **不取消任何其他任务**
  - `Enqueue` 方法增加 `sentenceIndex` 和 `version` 参数
  - 移除现有的"取消所有 index < 当前"逻辑（第36-37行）
  - `Output` 属性改为从 `Caption.SentenceStates` 的最高版本计算，而非直接存储

  **Must NOT do**:
  - 不使用全局版本号（必须 per-sentence）
  - 不修改 TranslateLoop 的调用逻辑（Task 6 负责）
  - 不修改 DisplayLoop 的读取逻辑（Task 7 负责）

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 核心架构改造，涉及竞态条件消除，需要深入理解现有取消机制
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 3, 4)
  - **Blocks**: Tasks 6, 7
  - **Blocked By**: None

  **References**:
  - `src/models/TranslationTaskQueue.cs:17-29` — 当前 Enqueue 方法和 ContinueWith 逻辑
  - `src/models/TranslationTaskQueue.cs:31-40` — 当前 OnTaskCompleted 取消机制（**需要改造的核心**）
  - `src/models/TranslationTaskQueue.cs:53-66` — TranslationTask 类定义（需要加 Version/SentenceIndex）
  - `src/models/Caption.cs` — Caption 模型，SentenceStates 将在此定义（Task 3）
  - **注意**: Task 3 定义 SentenceState 结构，Task 2 需要知道 SentenceState 有 Version 和 TranslatedText 字段。两者可以并行开发，只需约定接口。

  **Acceptance Criteria**:
  - [ ] TranslationTask 有 Version + SentenceIndex 字段
  - [ ] OnTaskCompleted 不取消任何旧任务
  - [ ] OnTaskCompleted 回填翻译结果到 Caption.SentenceStates
  - [ ] Output 从 Caption.SentenceStates 计算（最高版本 per-sentence）
  - [ ] `dotnet build` 成功

  **QA Scenarios**:
  ```
  Scenario: 版本号机制正确排序
    Tool: Bash (dotnet test)
    Steps:
      1. 创建两个翻译任务：sentenceIndex=0, version=1 和 sentenceIndex=0, version=2
      2. 让 version=1 先完成
      3. 验证 Output 返回 version=2 的结果（不返回 version=1）
    Expected Result: 最高版本号的翻译结果被选中
    Evidence: .sisyphus/evidence/task-2-version-order.txt

  Scenario: 不同句子的版本号互不干扰
    Tool: Bash (dotnet test)
    Steps:
      1. sentence0 version=1 完成，sentence2 version=1 完成
      2. sentence2 version=2 入队
      3. 验证 Output 中 sentence0 显示 version=1，sentence2 显示 version=2
    Expected Result: 每个句子独立追踪版本号
    Evidence: .sisyphus/evidence/task-2-per-sentence-version.txt

  Scenario: 旧任务完成不取消新任务（消除竞态）
    Tool: Bash (dotnet test)
    Steps:
      1. sentence0 version=1（旧翻译）和 sentence0 version=2（纠错翻译）同时在队列
      2. version=1 先完成
      3. version=2 的 CTS 未被取消
      4. version=2 后完成，Output 显示 version=2 的结果
    Expected Result: 纠错翻译最终胜出，旧翻译短暂显示后被覆盖
    Evidence: .sisyphus/evidence/task-2-no-race.txt
  ```

  **Commit**: YES
  - Message: `refactor(task-queue): replace cancellation mechanism with per-sentence version numbers`
  - Files: `src/models/TranslationTaskQueue.cs`
  - Pre-commit: `dotnet build`

- [x] 3. Caption SentenceState 数据结构

  **What to do**:
  - 在 `Caption.cs` 中定义 `SentenceState` 结构体/类：
    ```csharp
    public class SentenceState
    {
        public string OriginalText;       // 原文
        public string TranslatedText;      // 翻译结果（完成后回填）
        public int Version;               // 翻译版本号（per-sentence，单调递增）
        public bool IsComplete;           // EOS结尾=true（正式翻译），false=预翻译
        public bool IsTranslationPending; // 翻译任务是否在执行中
    }
    ```
  - 在 Caption 中添加 `List<SentenceState> SentenceStates` 属性（替代 `OriginalCaption`/`TranslatedCaption` 的内部追踪功能）
  - `OriginalCaption` 和 `TranslatedCaption` 保留为 auto-property，但改为从 SentenceStates 计算：
    - `OriginalCaption = SentenceStates.LastOrDefault()?.OriginalText ?? ""`
    - `TranslatedCaption` = SentenceStates 中最高版本的最后一句翻译
  - SentenceStates 的线程安全：使用 `lock` 保护读写（SyncLoop写、TranslateLoop/DisplayLoop读）
  - SentenceStates 的 PropertyChanged 通知：修改时触发 `OnPropertyChanged("SentenceStates")`

  **Must NOT do**:
  - 不删除 `OriginalCaption`/`TranslatedCaption` 属性（其他模块依赖）
  - 不删除 `Contexts` 队列（SQLite 历史依赖）
  - 不修改 Overlay 属性名（Task 8 负责计算逻辑）
  - 不修改 Overlay XAML 绑定结构

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 数据结构定义，逻辑简单
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2, 4)
  - **Blocks**: Tasks 7, 8
  - **Blocked By**: None

  **References**:
  - `src/models/Caption.cs:22-23` — 当前 OriginalCaption/TranslatedCaption auto-property
  - `src/models/Caption.cs:25` — Contexts 队列（保留）
  - `src/models/Caption.cs:52-78` — Overlay 属性定义（保留属性名，Task 8 改计算逻辑）
  - `src/models/Caption.cs:80-81` — OverlayPreviousTranslation（从 SentenceStates 重新计算）
  - `src/models/Caption.cs:95-125` — GetPreviousText 方法（需要适配 SentenceStates 来源）

  **Acceptance Criteria**:
  - [ ] SentenceState 类定义完成（含所有5个字段）
  - [ ] Caption.SentenceStates List 属性存在
  - [ ] SentenceStates 有 lock 保护
  - [ ] `dotnet build` 成功（可能需要临时调整引用）

  **QA Scenarios**:
  ```
  Scenario: SentenceState 数据结构正确
    Tool: Bash (dotnet build)
    Steps:
      1. dotnet build
      2. 检查编译成功
    Expected Result: Build succeeded, 0 errors
    Evidence: .sisyphus/evidence/task-3-build.txt
  ```

  **Commit**: YES
  - Message: `feat(caption): add SentenceState data structure for per-sentence tracking`
  - Files: `src/models/Caption.cs`
  - Pre-commit: `dotnet build`

- [x] 4. pendingTextQueue 元数据封装

  **What to do**:
  - 创建 `TranslationRequest` 类替代裸字符串：
    ```csharp
    public class TranslationRequest
    {
        public string OriginalText;         // 原文
        public int SentenceIndex;           // 在 SentenceStates 中的索引
        public bool IsCorrection;           // 是否纠错重翻
        public bool IsPreTranslation;       // 是否预翻译（不完整句子）
        public int ExpectedVersion;         // 预期版本号（入队时分配）
    }
    ```
  - 修改 `pendingTextQueue` 从 `Queue<string>` 改为 `Queue<TranslationRequest>`
  - 修改 SyncLoop 中 `pendingTextQueue.Enqueue()` 调用，传入 TranslationRequest 而非裸字符串
  - 修改 TranslateLoop 中 `pendingTextQueue.Dequeue()` 和后续 `translationTaskQueue.Enqueue()` 调用
  - TranslateLoop 将 TranslationRequest 的 SentenceIndex/ExpectedVersion 传递给 TranslationTaskQueue.Enqueue

  **Must NOT do**:
  - 不破坏单生产者单消费者契约（只有 SyncLoop enqueue、TranslateLoop dequeue）
  - 不改变 pendingTextQueue 的基本轮询逻辑
  - 不在此任务中实现预翻译触发逻辑（Task 5 负责）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 类型替换和参数传递调整，逻辑简单
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2, 3)
  - **Blocks**: Tasks 5, 6
  - **Blocked By**: None

  **References**:
  - `src/Translator.cs:19` — `pendingTextQueue` 定义（Queue<string> → Queue<TranslationRequest>）
  - `src/Translator.cs:117-126` — 逐句对比后 enqueue（需要传 SentenceIndex + IsCorrection）
  - `src/Translator.cs:128-135` — 新增句子 enqueue（需要传 SentenceIndex + IsCorrection=false）
  - `src/Translator.cs:139-146` — 超长不完整句子 enqueue（需要传 IsPreTranslation=true）
  - `src/Translator.cs:206-237` — TranslateLoop dequeue 和 translationTaskQueue.Enqueue

  **Acceptance Criteria**:
  - [ ] TranslationRequest 类定义完成
  - [ ] pendingTextQueue 类型改为 Queue<TranslationRequest>
  - [ ] SyncLoop 所有 Enqueue 调用传入 TranslationRequest
  - [ ] TranslateLoop Dequeue 和传递逻辑正确
  - [ ] `dotnet build` 成功

  **QA Scenarios**:
  ```
  Scenario: 元数据封装后编译通过
    Tool: Bash (dotnet build)
    Steps:
      1. dotnet build
      2. 检查编译成功
    Expected Result: Build succeeded, 0 errors
    Evidence: .sisyphus/evidence/task-4-metadata.txt
  ```

  **Commit**: YES
  - Message: `refactor(translator): replace bare-string pendingTextQueue with TranslationRequest metadata`
  - Files: `src/Translator.cs, 新 TranslationRequest 类文件`
  - Pre-commit: `dotnet build`

- [ ] 5. SyncLoop 预翻译触发机制

  **What to do**:
  - 在 SyncLoop 中引入 `idleCount` 和 `syncCount`（适配3-loop异步架构）
  - 逻辑：
    ```
    每轮 SyncLoop:
      if (incompleteSentence 与上次不同) → idleCount = 0, syncCount++
      else → idleCount++
      
      if (syncCount >= Setting.MaxSyncInterval || idleCount >= Setting.MaxIdleInterval)
        → 入队预翻译（TranslationRequest.IsPreTranslation=true）
        → reset syncCount, idleCount = 0
    ```
  - 在 Setting 中添加 `MaxSyncInterval` 和 `MaxIdleInterval` 属性（默认值参考历史版本：MaxSyncInterval=约10次变化，MaxIdleInterval=约8次静止）
  - **关键**: 预翻译只入队 pendingTextQueue，不触发 SQLite/Contexts 写入
  - 预翻译的 SentenceIndex 来自当前 incompleteSentence 在 SentenceStates 中的位置

  **Must NOT do**:
  - 不直接恢复旧版同步架构的 idleCount/syncCount（必须适配3-loop）
  - 不让预翻译结果写入 SQLite
  - 不改变现有的 EOS 完整句子入队逻辑
  - 不改变现有超长(≥160字节) fallback 入队逻辑

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 需要理解3-loop异步架构与旧版单loop的区别，正确适配阈值逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 9
  - **Blocked By**: Task 4（pendingTextQueue TranslationRequest 封装）

  **References**:
  - `src/Translator.cs:87-146` — 当前 SyncLoop（SplitSentences + trackedSentences + 逐句对比）
  - `src/Translator.cs:139-146` — 超长不完整句子的现有 fallback 入队逻辑（保留）
  - commit `85f34e7:src/Translator.cs` — 旧版 idleCount/syncCount 逻辑（参考但不可直接恢复）
  - `src/models/Setting.cs` — Setting 类，需要添加 MaxSyncInterval/MaxIdleInterval 属性
  - **注意**: Setting 属性 setter 必须调用 OnPropertyChanged（触发自动保存）

  **Acceptance Criteria**:
  - [ ] Setting 有 MaxSyncInterval 和 MaxIdleInterval 属性
  - [ ] SyncLoop 有 idleCount 和 syncCount 变量
  - [ ] 预翻译入队时 IsPreTranslation=true
  - [ ] 完整句子(EOS)入队不受预翻译逻辑影响
  - [ ] `dotnet build` 成功

  **QA Scenarios**:
  ```
  Scenario: 不完整句子达到阈值触发预翻译
    Tool: Bash (dotnet test)
    Steps:
      1. 模拟 SyncLoop 连续看到同一 incompleteSentence 8 次（idleCount=8）
      2. 验证 pendingTextQueue 收到 IsPreTranslation=true 的 TranslationRequest
    Expected Result: 预翻译请求正确入队
    Evidence: .sisyphus/evidence/task-5-pretranslation-trigger.txt

  Scenario: 句子完成(EOS)时立即入队正式翻译，不受预翻译影响
    Tool: Bash (dotnet test)
    Steps:
      1. incompleteSentence 变为完整句子（EOS结尾）
      2. 验证入队 IsPreTranslation=false 的正式翻译请求
    Expected Result: 正式翻译请求正确入队
    Evidence: .sisyphus/evidence/task-5-formal-translation.txt
  ```

  **Commit**: YES
  - Message: `feat(sync-loop): add idle/sync threshold pre-translation trigger for incomplete sentences`
  - Files: `src/Translator.cs, src/models/Setting.cs`
  - Pre-commit: `dotnet build`

- [ ] 6. TranslateLoop 版本号入队

  **What to do**:
  - 修改 TranslateLoop 中 `translationTaskQueue.Enqueue()` 调用，传入 SentenceIndex + ExpectedVersion
  - 从 `pendingTextQueue.Dequeue()` 获取 TranslationRequest
  - 将 TranslationRequest 的 SentenceIndex 和 ExpectedVersion 传递给 `translationTaskQueue.Enqueue(worker, originalText, sentenceIndex, version)`
  - 对于预翻译（IsPreTranslation=true），翻译完成后的处理：
    - 回填到 SentenceStates[SentenceIndex].TranslatedText
    - 标记 SentenceStates[SentenceIndex].IsComplete = false（预翻译）
    - 不写入 SQLite 或 Contexts
  - 对于正式翻译（IsPreTranslation=false），翻译完成后：
    - 回填到 SentenceStates[SentenceIndex].TranslatedText
    - 标记 SentenceStates[SentenceIndex].IsComplete = true
    - 按现有逻辑写入 SQLite/Contexts（IsOverwrite 判断）

  **Must NOT do**:
  - 不修改 TranslationTaskQueue 的内部逻辑（Task 2 已完成）
  - 不修改 SyncLoop 的入队逻辑（Task 4/5 已完成）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要协调 TranslationRequest 元数据、版本号和预翻译/正式翻译的不同处理路径
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Task 5 in Wave 2, but can start after Tasks 2+4)
  - **Parallel Group**: Wave 2 (with Tasks 5, 7, 8)
  - **Blocks**: Task 9
  - **Blocked By**: Tasks 2, 4

  **References**:
  - `src/Translator.cs:206-237` — 当前 TranslateLoop（需要适配 TranslationRequest）
  - `src/Translator.cs:221` — Dequeue 和 translationTaskQueue.Enqueue 调用点
  - `src/models/TranslationTaskQueue.cs:17` — Enqueue 方法签名（Task 2 已改为接受 sentenceIndex/version）
  - `src/Translator.cs:261-280` — IsOverwrite 和 LogOnly 逻辑（预翻译不走此路径）

  **Acceptance Criteria**:
  - [ ] TranslateLoop 正确传递 SentenceIndex/Version 给 TranslationTaskQueue
  - [ ] 预翻译结果回填到 SentenceStates，不写入 SQLite
  - [ ] 正式翻译结果回填到 SentenceStates + 写入 SQLite/Contexts
  - [ ] `dotnet build` 成功

  **QA Scenarios**:
  ```
  Scenario: 预翻译不入 SQLite
    Tool: Bash (dotnet test)
    Steps:
      1. TranslateLoop 处理 IsPreTranslation=true 的 TranslationRequest
      2. 翻译完成后检查 SQLiteHistoryLogger 无新记录
    Expected Result: SQLite 最后记录不变
    Evidence: .sisyphus/evidence/task-6-no-sqlite-pretranslation.txt

  Scenario: 正式翻译入 SQLite
    Tool: Bash (dotnet test)
    Steps:
      1. TranslateLoop 处理 IsPreTranslation=false 的 TranslationRequest
      2. 翻译完成后检查 SQLiteHistoryLogger 有新记录
    Expected Result: SQLite 有新记录
    Evidence: .sisyphus/evidence/task-6-formal-sqlite.txt
  ```

  **Commit**: YES
  - Message: `refactor(translate-loop): pass sentence index and version to TranslationTaskQueue, handle pre-translation differently`
  - Files: `src/Translator.cs`
  - Pre-commit: `dotnet build`

- [ ] 7. DisplayLoop 版本号过滤 + SentenceState 回填

  **What to do**:
  - 修改 DisplayLoop：从 `translationTaskQueue.Output` 获取翻译结果时，使用版本号判断是否更新显示
  - DisplayLoop 不再直接从 `translationTaskQueue.Output` 读取单一字符串，而是：
    - 从 `Caption.SentenceStates` 中获取每句的最高版本翻译结果
    - 拼接为 `DisplayTranslatedCaption` 和 `OverlayCurrentTranslation`
  - 纠错替换逻辑：当 SentenceStates[i] 的版本号增加且翻译结果更新时，DisplayLoop 自动反映变化
  - 预翻译 vs 正式翻译的显示：预翻译正常显示，但当正式翻译（更高版本）完成时自动替换
  - 保持现有的 `[ERROR]`/`[WARNING]` 检测和 NoticePrefix 分离逻辑
  - 保持现有的 isChoke（720ms停留）逻辑

  **Must NOT do**:
  - 不修改 Overlay 属性名（保持 OverlayCurrentTranslation/OverlayNoticePrefix）
  - 不修改 Overlay XAML 绑定结构
  - 不改变 Caption.Contexts 的消费方式（Log Cards 和 context-aware 翻译仍然使用 Contexts）

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: DisplayLoop 是翻译结果展示的核心路径，需要理解版本号过滤、预翻译替换、Overlay拼接的完整逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 5, 6, 8 in Wave 2, but needs Tasks 2+3)
  - **Parallel Group**: Wave 2 (with Tasks 5, 6, 8)
  - **Blocks**: Tasks 8, 9
  - **Blocked By**: Tasks 2, 3

  **References**:
  - `src/Translator.cs:239-280` — 当前 DisplayLoop（需要从 SentenceStates 计算）
  - `src/Translator.cs:243` — `translationTaskQueue.Output` 读取（改为从 SentenceStates 计算）
  - `src/Translator.cs:248-260` — TranslatedCaption 和 Overlay 属性设置逻辑
  - `src/models/Caption.cs:70-78` — OverlayCurrentTranslation 属性
  - `src/models/Caption.cs:61-69` — OverlayNoticePrefix 属性
  - `src/models/Caption.cs:80-81` — OverlayPreviousTranslation（从 SentenceStates 重新计算）

  **Acceptance Criteria**:
  - [ ] DisplayLoop 从 SentenceStates 读取翻译结果
  - [ ] 纠错后的翻译正确替换旧翻译显示
  - [ ] 预翻译正常显示，正式翻译完成后自动替换
  - [ ] [ERROR]/[WARNING] 检测逻辑保持
  - [ ] isChoke 逻辑保持
  - [ ] `dotnet build` 成功

  **QA Scenarios**:
  ```
  Scenario: 纠错翻译替换旧翻译
    Tool: Bash (dotnet test)
    Steps:
      1. SentenceStates[0] 有 version=1 翻译 "我想去商店"
      2. SentenceStates[0] 更新为 version=2 翻译 "我想去故事"
      3. DisplayLoop 计算 DisplayTranslatedCaption = "我想去故事"
    Expected Result: 显示纠错后的翻译
    Evidence: .sisyphus/evidence/task-7-correction-replace.txt

  Scenario: 预翻译→正式翻译替换
    Tool: Bash (dotnet test)
    Steps:
      1. SentenceStates[last] IsComplete=false, TranslatedText="我想去"（预翻译）
      2. 句子完成 EOS，SentenceStates[last] IsComplete=true, Version=2, TranslatedText="我想去商店"
      3. DisplayLoop 显示 "我想去商店"
    Expected Result: 正式翻译替换预翻译
    Evidence: .sisyphus/evidence/task-7-pre-to-formal.txt
  ```

  **Commit**: YES
  - Message: `refactor(display-loop): use SentenceState version numbers for translation result filtering and display`
  - Files: `src/Translator.cs`
  - Pre-commit: `dotnet build`

- [ ] 8. Overlay 属性从 SentenceState 计算

  **What to do**:
  - 修改 Caption 中 Overlay 属性的计算逻辑：
    - `OverlayOriginalCaption`：从 SentenceStates 取最近 N 句（Setting.DisplaySentences）的 OriginalText 拼接
    - `OverlayCurrentTranslation`：取最后一句 SentenceState 的 TranslatedText（最高版本），分离 NoticePrefix
    - `OverlayPreviousTranslation`：取最近 N-1 句的 TranslatedText 拼接（排除最后一句）
    - `OverlayNoticePrefix`：最后一句翻译的延迟前缀 `[XXXX ms]`
  - Overlay 属性值由 SyncLoop 和 DisplayLoop 共同驱动：
    - SyncLoop 更新 SentenceStates 时计算 OverlayOriginalCaption
    - DisplayLoop 更新翻译结果时计算 OverlayCurrentTranslation/OverlayPreviousTranslation
  - 保持 `GetPreviousText` 方法的格式（EOS补全、空格、CJ字符判断）
  - 纠错替换：当 SentenceStates[i] 的翻译更新时，Overlay 自动反映（无需手动替换）

  **Must NOT do**:
  - 不修改 Overlay XAML 绑定结构（保持 3个 Run 元素）
  - 不修改 Overlay 属性名
  - 不删除 Caption.Contexts（SQLite 和 log cards 仍然依赖）
  - 不新增 XAML 控件

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Overlay 显示是用户体验的核心，需要精确适配 SentenceState 计算到现有的3个 Run 元素绑定
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (in Wave 2 with Tasks 5, 6, 7)
  - **Parallel Group**: Wave 2
  - **Blocks**: Tasks 9, 10
  - **Blocked By**: Tasks 3, 7

  **References**:
  - `src/models/Caption.cs:52-78` — 当前 Overlay 属性定义（保持属性名，改计算来源）
  - `src/models/Caption.cs:80-81` — OverlayPreviousTranslation（改为从 SentenceStates 计算）
  - `src/models/Caption.cs:95-125` — GetPreviousText 方法（格式逻辑保持，来源从 Contexts 改为 SentenceStates）
  - `src/Translator.cs:148-169` — 当前 OverlayOriginalCaption 计算（从 fullText 直接计算，改为从 SentenceStates）
  - Overlay XAML 文件（需要 lsp_find_references 查找绑定位置）

  **Acceptance Criteria**:
  - [ ] OverlayOriginalCaption 从 SentenceStates 拼接
  - [ ] OverlayCurrentTranslation 从最后一句 SentenceState 计算
  - [ ] OverlayPreviousTranslation 从前 N-1 句 SentenceState 计算
  - [ ] 纠错时 Overlay 显示自动替换对应句翻译
  - [ ] XAML 绑定结构不变
  - [ ] `dotnet build` 成功

  **QA Scenarios**:
  ```
  Scenario: Overlay 纠错替换显示
    Tool: Bash (dotnet test)
    Steps:
      1. SentenceStates = ["Hello."→"你好", "How are you?"→"你好吗？", "I am fine."→"我很好"]
      2. SentenceStates[1] 纠错 → "How are you doing?"→"你好吗？在做什么？"（version=2）
      3. OverlayCurrentTranslation 显示最后一句翻译
      4. OverlayPreviousTranslation 包含纠错后的第1-2句翻译
    Expected Result: Overlay 显示纠错后的完整翻译列表
    Evidence: .sisyphus/evidence/task-8-overlay-correction.txt
  ```

  **Commit**: YES
  - Message: `feat(overlay): compute Overlay properties from SentenceState list for correction-aware display`
  - Files: `src/models/Caption.cs, src/Translator.cs`
  - Pre-commit: `dotnet build`

- [ ] 9. 单元测试：SentenceDiff + 版本号 + SentenceState→Overlay

  **What to do**:
  - 在 `LiveCaptionsTranslator.Tests` 项目中写以下单元测试：
    - **SplitSentences 测试组**：
      - 正常拆分："Hello. How are you." → completed=["Hello.", "How are you."], incomplete=""
      - 不完整句："Hello. How are you" → completed=["Hello."], incomplete="How are you"
      - 短句合并逻辑（<10字节时合并）
    - **纠错检测测试组**：
      - 纠错场景：tracked=["Hello.", "How are you."] vs current=["Hello.", "How are you doing."] → 检测 index=1 变化
      - 新增场景：tracked=["Hello."] vs current=["Hello.", "I am fine."] → 检测新增 sentence
      - 重置场景：tracked 大多数句子不匹配 → 触发 ClearContexts
    - **版本号排序测试组**：
      - 同句子 version=1 vs version=2 → version=2 胜出
      - 不同句子独立版本号 →互不干扰
      - 预翻译 vs 正式翻译版本号 → 正式翻译替换预翻译
    - **SentenceState→Overlay 属性计算测试组**：
      - OverlayOriginalCaption 从 SentenceStates 拼接
      - OverlayPreviousTranslation 从 SentenceStates 拼接（排除最后一句）
      - 纠错后 Overlay 属性更新

  **Must NOT do**:
  - 不写现有 TranslationAPI/Setting/SQLite 的测试（超出范围）
  - 不写 UI 自动化测试（手动 QA 在 Final Wave 覆盖）

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 单元测试需要精确理解核心逻辑的边界情况，覆盖纠错/版本号/预翻译等复杂场景
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 3 (with Task 10)
  - **Blocks**: F1-F4
  - **Blocked By**: Tasks 1, 5, 6, 7, 8

  **References**:
  - `LiveCaptionsTranslator.Tests/*.csproj` — Task 1 搭建的测试项目
  - `src/Translator.cs:186-204` — SplitSentences 方法（测试目标）
  - `src/Translator.cs:87-146` — SyncLoop 逐句对比逻辑（测试目标）
  - `src/models/TranslationTaskQueue.cs` — 版本号机制（测试目标）
  - `src/models/Caption.cs` — SentenceState 和 Overlay 计算（测试目标）

  **Acceptance Criteria**:
  - [ ] `dotnet test` 所有测试通过
  - [ ] 至少15个单元测试方法（4个测试组 × 3-4个方法每组）

  **QA Scenarios**:
  ```
  Scenario: 所有单元测试通过
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test LiveCaptionsTranslator.Tests --verbosity normal
      2. 检查输出包含 "Passed!  - Failed: 0"
    Expected Result: ≥15个测试通过，0个失败
    Evidence: .sisyphus/evidence/task-9-unit-tests.txt

  Scenario: 纠错检测测试正确识别句子变化
    Tool: Bash (dotnet test)
    Steps:
      1. dotnet test --filter "CorrectionDetection"
      2. 检查纠错测试组全部通过
    Expected Result: 3-4个纠错检测测试通过
    Evidence: .sisyphus/evidence/task-9-correction-tests.txt
  ```

  **Commit**: YES
  - Message: `test: add unit tests for SentenceDiff, version number, and SentenceState→Overlay computation`
  - Files: `LiveCaptionsTranslator.Tests/*.cs`
  - Pre-commit: `dotnet test`

- [ ] 10. Overlay 句子换行分隔可选功能

  **What to do**:
  - 在 Setting 中添加 `OverlaySentenceSeparator` bool 属性（默认 false）
  - 当 `OverlaySentenceSeparator=true` 时，OverlayOriginalCaption 的句子之间插入 `\n` 分隔
  - Caption 中 OverlayOriginalCaption 的计算逻辑：
    - 默认(false)：句子之间空格分隔（现有行为）
    - 开启(true)：句子之间 `\n` 分隔
  - 在 Settings 页面添加对应 UI 控件（CheckBox 或 ToggleSwitch）

  **Must NOT do**:
  - 不新增 Overlay XAML 控件（只修改 OverlayOriginalCaption 的拼接格式）
  - 不修改 Overlay 句子间的翻译分隔逻辑（只修改原文分隔）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单一属性添加 + UI 控件
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Task 9 in Wave 3)
  - **Parallel Group**: Wave 3
  - **Blocks**: F1-F4
  - **Blocked By**: Task 8

  **References**:
  - `src/models/Setting.cs` — Setting 类（新属性 setter 必须调用 OnPropertyChanged）
  - `src/models/Caption.cs` — OverlayOriginalCaption 属性（修改拼接逻辑）
  - `src/pages/SettingsPage.xaml` + `.xaml.cs` — Settings UI 页面（添加控件）

  **Acceptance Criteria**:
  - [ ] Setting.OverlaySentenceSeparator 属性存在
  - [ ] OverlayOriginalCaption 拼接逻辑根据此属性变化
  - [ ] Settings 页面有对应 UI 控件
  - [ ] `dotnet build` 成功

  **QA Scenarios**:
  ```
  Scenario: 句子换行分隔开关
    Tool: Bash (dotnet test)
    Steps:
      1. Setting.OverlaySentenceSeparator = false → OverlayOriginalCaption = "Hello. How are you."
      2. Setting.OverlaySentenceSeparator = true → OverlayOriginalCaption = "Hello.\nHow are you."
    Expected Result: 属性切换正确影响拼接格式
    Evidence: .sisyphus/evidence/task-10-separator.txt
  ```

  **Commit**: YES
  - Message: `feat(overlay): add optional sentence line-break separator setting`
  - Files: `src/models/Setting.cs, src/models/Caption.cs, src/pages/SettingsPage.xaml, src/pages/SettingsPage.xaml.cs`
  - Pre-commit: `dotnet build`

---

## Final Verification Wave (MANDATORY — after ALL implementation tasks)

- [ ] F1. **Plan Compliance Audit** — `oracle`
  Read the plan end-to-end. For each "Must Have": verify implementation exists. For each "Must NOT Have": search codebase for forbidden patterns. Compare deliverables against plan.
  Output: `Must Have [N/N] | Must NOT Have [N/N] | Tasks [N/N] | VERDICT: APPROVE/REJECT`

- [ ] F2. **Code Quality Review** — `unspecified-high`
  Run `dotnet build` + `dotnet format --verify-no-changes` + `dotnet test`. Review all changed files for: `as any`/commented-out code, unused imports. Check AI slop: excessive comments, over-abstraction, generic names.
  Output: `Build [PASS/FAIL] | Format [PASS/FAIL] | Tests [N pass/N fail] | VERDICT`

- [ ] F3. **Real Manual QA** — `unspecified-high` (+ `playwright` skill)
  Start app on Win11. Test: (1) Normal translation flow unchanged, (2) Correction scenario — verify corrected text gets new translation that replaces old, (3) Pre-translation — incomplete sentence shows translation before EOS, (4) Overlay shows corrected translations. Save screenshots to `.sisyphus/evidence/final-qa/`.
  Output: `Scenarios [N/N pass] | VERDICT`

- [ ] F4. **Scope Fidelity Check** — `deep`
  For each task: read "What to do", read actual diff. Verify 1:1 mapping. Check "Must NOT do" compliance. Detect cross-task contamination.
  Output: `Tasks [N/N compliant] | VERDICT`

---

## Commit Strategy

- **Wave 1 完成后**: `refactor(task-queue): introduce per-sentence version number mechanism`
- **Wave 2 完成后**: `feat(caption): add SentenceState list and overlay property computation from sentence list`
- **Wave 3 完成后**: `feat(sync-loop): add pre-translation trigger for incomplete sentences`
- **T9 完成后**: `test: add xUnit test infrastructure and core logic unit tests`
- **T10 完成后**: `feat(overlay): add optional sentence line-break separator`

---

## Success Criteria

### Verification Commands
```bash
dotnet build   # Expected: Build succeeded
dotnet test    # Expected: All tests pass
dotnet format ./LiveCaptionsTranslator.csproj --verify-no-changes  # Expected: No changes needed
```

### Final Checklist
- [ ] All "Must Have" present
- [ ] All "Must NOT Have" absent
- [ ] All tests pass
- [ ] Normal translation flow unchanged
- [ ] Correction translations correctly replace old translations
- [ ] Pre-translations show for incomplete sentences
- [ ] Pre-translations not logged to SQLite