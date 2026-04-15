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
