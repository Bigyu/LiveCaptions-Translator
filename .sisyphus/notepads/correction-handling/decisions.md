# Decisions — correction-handling

## 2026-04-15 Architecture Decisions
- Version number mechanism: per-sentence (NOT global), each SentenceState has its own version counter
- TranslationTaskQueue: don't cancel old tasks, let them complete naturally, DisplayLoop picks highest version
- Caption model: SentenceState list mode (not single-string)
- Pre-translation: restore idleCount/syncCount style, adapted for 3-loop async architecture
- Pre-translation results: ephemeral only in SentenceState, NOT in SQLite or Contexts
- Overlay: maintain current scrolling subtitle structure, corrections replace corresponding sentence translation
- Dedup: version number mechanism handles it, no extra dedup needed
- Overlay sentence separator: optional boolean Setting property, toggles \n between sentences