# Claude Code — UnityTextTranslator

**Git-корень** — папка, где лежат `UnityTextTranslator.slnx`, `packages/`, `Tools/`.  
**Проект C#** — подпапка `UnityTextTranslator/` (`.csproj`, `Form1.cs`, `AGENTS.md`).

## Что читать перед IL2CPP TMP-патчем (PathID 7296)

**Если cwd = корень репозитория** (рядом с `.slnx`):

| Документ | Путь |
|----------|------|
| AGENTS | `UnityTextTranslator/AGENTS.md` |
| Handoff | `UnityTextTranslator/docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md` |
| Патчер | `UnityTextTranslator/TmpFontAssetMsdfAtlasPatcher.cs` |

**Если cwd = папка проекта** (рядом с `UnityTextTranslator.csproj`):

| Документ | Путь |
|----------|------|
| AGENTS | `AGENTS.md` |
| Handoff | `docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md` |

## Частая ошибка (из-за неё файлы «не находятся»)

Из папки проекта **не** открывайте `UnityTextTranslator/AGENTS.md` — это ищет несуществующий `UnityTextTranslator/UnityTextTranslator/AGENTS.md`.

Промпт «прочитай AGENTS.md в UnityTextTranslator» при cwd=проект → читайте **`AGENTS.md`**, без префикса.
