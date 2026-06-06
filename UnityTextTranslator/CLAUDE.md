# Claude Code — папка проекта C# (cwd часто здесь)

## Документация (читать с этой папки как корня)

- [AGENTS.md](AGENTS.md)
- [docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md](docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md)

## Код задачи

- `TmpFontAssetMsdfAtlasPatcher.cs` — in-place патч атласа / GlyphRect / диагностика `[7296]`
- `TmpFontAssetIl2CppRawMetadataPatcher.cs` — raw offset atlas/charset
- `Form1.cs` — кнопка патча, PathID default 7296, A/B test modes

## Если cwd — родительский репозиторий (рядом `.slnx`)

Тогда: `UnityTextTranslator/AGENTS.md`, `UnityTextTranslator/docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md` (см. `../CLAUDE.md` в корне репо).
