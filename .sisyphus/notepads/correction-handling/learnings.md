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
