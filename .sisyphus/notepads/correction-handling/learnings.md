# Learnings — correction-handling

## 2026-04-15 Initial Analysis
- Current SyncLoop (Translator.cs:87-146) ALREADY has sentence tracking via `trackedSentences` + `SplitSentences`
- TranslationTaskQueue.cs:31-40 has "completed task cancels older tasks" → race condition
- Historical version (commit 85f34e7) had idleCount/syncCount pre-translation mechanism
- This was removed in commit 5ed2009 ("fix: repeatedly translate same sentence")
- Caption.cs is a single-string hub (OriginalCaption/TranslatedCaption), Overlay properties computed from Contexts
- Overlay XAML uses 3 Run elements bound to OverlayOriginalCaption/OverlayPreviousTranslation/OverlayCurrentTranslation
- Setting properties must call OnPropertyChanged (triggers auto-save)
- pendingTextQueue is Queue<string>, single-producer-single-consumer contract
## 2026-04-16 SentenceState Data Structure
- Added SentenceState class (5 fields: OriginalText, TranslatedText, Version, IsComplete, IsTranslationPending) in Caption.cs
- Added SentenceStates List<SentenceState> property with readonly _sentenceStatesLock for thread safety
- OriginalCaption/TranslatedCaption kept as auto-properties alongside SentenceStates (will be computed from SentenceStates later)
- Contexts queue and Overlay properties unchanged
- dotnet build cannot run on this Linux machine (WPF project, Windows-only) — verified syntax manually
- ImplicitUsings=enable in csproj, so System.Collections.Generic auto-imported, List<> works without explicit using

## 2026-04-16 xUnit Test Infrastructure
- Created LiveCaptionsTranslator.Tests project with xUnit
- Target framework: net8.0-windows (must match main project)
- EnableWindowsTargeting=true required for building WPF project on Linux
- WPF temporary project (_CompileTemporaryAssembly) auto-includes all .cs files in subdirectories
  → Must add <Compile Remove="LiveCaptionsTranslator.Tests\**\*.cs" /> to main csproj
- Caption namespace is LiveCaptionsTranslator.models (capital L)
- dotnet test CANNOT run on Linux for net8.0-windows (requires WindowsDesktop.App runtime)
- Build succeeds on Linux; tests will pass on Windows
- .NET 8 SDK installed to ~/.dotnet (not pre-installed)

## 2026-04-16 TranslationTaskQueue Version Number Mechanism
- Replaced cancellation-based mechanism with per-sentence version number mechanism
- TranslationTask now has SentenceIndex and Version fields
- OnTaskCompleted NO longer cancels other tasks — just removes completed task from list and writes back to Caption.SentenceStates
- Version check (>=) prevents stale writes from out-of-order completions
- Output property now computes from Caption.SentenceStates (last non-empty TranslatedText) instead of stored field
- Added backward-compatible 2-param Enqueue overload (sentenceIndex=0, version=0) so existing TranslateLoop call still compiles
- Log/AddContexts only called for complete translations (isComplete=true), not for partial/in-progress ones
- CTS still kept on TranslationTask for now (TranslateLoop still passes CancellationTokenSource) — will be cleaned up in Task 6

## 2026-04-16 TranslationRequest Metadata Class (Task 4)
- Created TranslationRequest class in src/models/TranslationRequest.cs with 5 fields: OriginalText, SentenceIndex, IsCorrection, IsPreTranslation, ExpectedVersion
- Changed pendingTextQueue from Queue<string> to Queue<TranslationRequest> in Translator.cs
- Added sentenceVersionCounters (Dictionary<int,int>) for per-sentence version tracking
- Added GetNextVersion(sentenceIndex) helper method that increments version counter per sentence
- SyncLoop correction block (compareCount loop): IsCorrection=true, SentenceIndex=i
- SyncLoop new sentence block (alignedTracked→completedSentences): IsCorrection=false, SentenceIndex=i
- SyncLoop long incomplete fallback: IsPreTranslation=true, SentenceIndex=completedSentences.Count
- TranslateLoop now passes originalSnapshot.OriginalText, SentenceIndex, ExpectedVersion to 4-param Enqueue
- LogOnly mode uses originalSnapshot.OriginalText instead of bare string
- Build succeeds with 0 errors (warnings are pre-existing nullability warnings)

## 2026-04-16 SyncLoop Pre-Translation Trigger (idleCount/syncCount)
- Added idleCount and syncCount as local variables in SyncLoop (declared before while loop, persist across iterations)
- Added previousIncompleteSentence as static field (like lastEnqueuedIncomplete)
- Logic: incompleteSentence changed → idleCount=0, syncCount++; unchanged → idleCount++; empty → reset both to 0
- Trigger: syncCount >= MaxSyncInterval OR idleCount >= MaxIdleInterval AND byteCount >= SHORT_THRESHOLD → enqueue pre-translation
- After trigger: reset idleCount=0, syncCount=0
- SHORT_THRESHOLD=10 bytes (minimum for meaningful translation), LONG_THRESHOLD=160 bytes (existing long-incomplete fallback)
- Pre-translation trigger and long-incomplete fallback are COMPLEMENTARY: pre-translation handles shorter sentences via thresholds, long-incomplete handles ≥160 bytes immediately
- EOS complete sentences unaffected — they enqueue immediately in the new-sentence loop
- Setting.MaxIdleInterval (default 50) and Setting.MaxSyncInterval (default 3) already existed, no new Setting properties needed

## 2026-04-16 DisplayLoop Version Filtering + SentenceState Backfill (Task 5)
- Changed SentenceStates from List<SentenceState> to Dictionary<int, SentenceState> for stable absolute indexing
- Added FirstActiveSentenceIndex/LastActiveSentenceIndex to Caption for Output to iterate efficiently
- Added sentenceBaseIndex static field in Translator — tracks absolute sentence index offset
- When sentences scroll off (scrollOff > 0), sentenceBaseIndex += scrollOff, keeping indices stable
- On LiveCaptions reset (similarity mismatch), sentenceBaseIndex reset to 0, SentenceStates cleared
- SyncLoop now creates/updates SentenceState entries for all completed sentences each iteration
- SyncLoop sets Caption.OriginalCaption from last SentenceState's OriginalText
- All TranslationRequest.SentenceIndex now uses absolute index (sentenceBaseIndex + relativeIndex)
- sentenceVersionCounters keys now use absolute indices too (via GetNextVersion(absIdx))
- TranslateLoop IsTranslationPending marking uses Dictionary.TryGetValue instead of List index
- TranslationTaskQueue.Output iterates from LastActiveSentenceIndex down to FirstActiveSentenceIndex
- TranslationTaskQueue.OnTaskCompleted uses Dictionary.TryGetValue for SentenceState lookup
- DisplayLoop code unchanged — it reads Output (computed from SentenceStates), which automatically reflects version-based filtering
- Correction replacement: when SentenceState gets higher-version translation, Output reflects it, DisplayLoop updates
- Pre-translation → formal translation: same mechanism — higher version auto-replaces in Output
- [ERROR]/[WARNING] detection preserved in DisplayLoop
- isChoke (720ms pause) logic preserved — comes from SentenceState.IsComplete via Output
- Build succeeds with 0 errors
