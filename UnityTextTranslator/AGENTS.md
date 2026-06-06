# UnityTextTranslator — заметки для AI-ассистента

Краткая справка о проекте, чтобы не приходилось каждый раз восстанавливать контекст из исходников.

## Замена шрифта на кириллицу (IL2CPP TMP) — РЕШЕНО ✅

Реализован **пошаговый мастер из 4 кнопок** (Анализ → Атлас → Патч → Применить). Полный контекст, offset'ы, причина и итог:

→ **[docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md](docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md)** (разделы «ТЕКУЩЕЕ СОСТОЯНИЕ» / «✅ РЕШЕНО»)

Кратко: UI `Form1.FontWizard.cs`; патчер `TmpFontAssetMsdfAtlasPatcher.cs` (`AnalyzeTmpFonts`, `ReplaceTexture2DAtlasFromPngSameFile` grow-путь, `BuildGrownTablesWithCyrillic`). Атлас шрифта = `m_AtlasTextures[0]` (не хардкод), патчится inline; координаты в пространстве m_AtlasWidth. **Старый .csproj не подхватывает новые .cs — добавляй `<Compile Include=...>` вручную.**

## Что это

Десктоп-утилита для перевода текстов в играх на Unity. Работает с `*.assets` и Asset Bundle через библиотеку **AssetsTools.NET** (формат экспорта/импорта совместим с UABEA/UABEANext) и через локальные/онлайн API перевода.

## Стек

- **Платформа**: .NET Framework 4.8, WinForms, x64.
- **Язык**: C# (старый стиль `.csproj` с `packages.config`, без PackageReference и без SDK-style проекта).
- **Сборка**: MSBuild + Fody/Costura (все DLL встраиваются в один `UnityTextTranslator.exe`).
- **Зависимости**: `AssetsTools.NET` 3.0.4, `AssetsTools.NET.MonoCecil`, `Mono.Cecil` 0.10.4, `Newtonsoft.Json` 13, `Costura.Fody` 6, `Fody` 6.8.
- **Структура репозитория**: git-корень (`.slnx`, `packages/`, `Tools/`) и подпапка проекта `UnityTextTranslator/` (этот `AGENTS.md`, `.csproj`). Для AI: `CLAUDE.md` в корне репо и в папке проекта — **пути к docs зависят от cwd** (с префиксом `UnityTextTranslator/` или без).

## Структура файлов

| Файл | Что внутри |
|---|---|
| `Form1.cs` | Главная форма, ~5650 строк. Состояние, обработчики событий, интеграция модулей. |
| `Form1.Layout.cs` | Создание контролов, навигация, темы (partial class). |
| `Form1.Dashboard.cs` | Стартовый дашборд (partial class). |
| `Form1.BundleLocalizationModule.cs` | Модуль «Asset Bundle ↔ JSON» (partial class). |
| `LocalTranslateApi.cs` | HTTP-клиенты для всех LLM/translate-провайдеров (~1500 строк). |
| `UabeaJsonAssetExporter.cs` / `UabeaJsonAssetImporter.cs` | Экспорт/импорт Unity-объектов в JSON в формате UABEA. |
| `UabeaJsonPaths.cs` | Маршруты JSON-полей с переводимым текстом. |
| `MonoBehaviourScriptResolver.cs` | Резолвинг типов через `Mono.Cecil` для MonoBehaviour. |
| `ClassPackageDownloader.cs` | Загрузка `classdata.tpk` (типы Unity) при необходимости. |
| `UnityAssetsGameFolderHelper.cs` | Эвристика поиска `*_Data` папки игры. |
| `TranslationItem.cs` | DTO одной строки перевода. |
| `TranslationMemory.cs` | Кэш «оригинал → перевод» в `%AppData%\UnityTextTranslator\memory.json`. |
| `TranslationTxtExchange.cs` | Импорт/экспорт переводов в TXT (pipe), TSV, CSV. |
| `LocalizationBundleJsonInterop.cs` | Конверсия между bundle-JSON и форматом приложения. |
| `NoWheelComboBox.cs` | Кастомный ComboBox без прокрутки колесом. |
| `Program.cs` | `Main`, TLS-настройка, lazy-load `Mono.Cecil.Rocks` через `AssemblyResolve`. |
| `Properties/*.Designer.cs` | Автогенерируемое — не трогать руками. |
| `tools/gen_app_icon.py` | Утилита генерации `.ico`. |
| `TmpFontAssetMsdfAtlasPatcher.cs` | IL2CPP in-place: MSDF атлас 512, GlyphRect, диагностика 7296. |
| `TmpFontAssetIl2CppRawMetadataPatcher.cs` | Raw offset'ы atlas/charset для TMP. |
| `MsdfAtlasGenInterop.cs` | Обёртка msdf-atlas-gen. |
| `docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md` | Handoff для AI по текущей задаче. |

## Поддерживаемые провайдеры перевода

В `LocalTranslateApi.cs` enum `TranslationAiBackend`. Базовые (всегда видны): `LibreTranslate`, `OpenRouter`, `OpenAI`, `Mistral`, `DeepSeek`, `Gemini`, `Ollama`, `CustomOpenAiCompatible`. Дополнительные (флаг `junkFeaturesEnabled` в `Form1`): `Groq`, `TogetherAI`, `Qwen`, `Cohere`, `Kimi`, `Nvidia`, `Cursor`, `CloudflareWorkersAi`, `Apify`. Все, кроме LibreTranslate и Cloudflare, ходят в OpenAI-совместимый `/chat/completions`.

## Конвенции кода

- **XML-doc и комментарии — на русском.** Сохраняй язык при правках.
- **UI двуязычный**: подписи задаются хелпером `L("English text", "Русский текст")`. При добавлении контролов всегда передавай оба варианта.
- **Имена** — `PascalCase` для типов и публичных членов, `camelCase` для приватных полей (без префикса `_`).
- **`Form1` — `partial class`.** Новый функциональный модуль — в отдельный `Form1.<Имя>.cs`, не дописывай в `Form1.cs`.
- **Переводимые строки кода** — через `L(...)`, не зашивай хардкод.
- **Никаких `async void`** кроме обработчиков событий WinForms.
- **HTTP** — переиспользуй `HttpClient` из `LocalTranslateApi`, не создавай новые в обработчиках кнопок.
- **Исключения** в обработчиках UI: лови и пиши через `LogLine` / `logBox`, не давай форме упасть.
- **Newtonsoft.Json**, не `System.Text.Json` (его нет в .NET Framework 4.8 без NuGet).

## Сборка и запуск

- Открыть `..\UnityTextTranslator.slnx` (или `.sln`) в Visual Studio 2019/2022.
- При первой сборке VS должна восстановить пакеты из `..\packages\` через старый NuGet (`packages.config`). Если нет — `Tools → NuGet → Restore`.
- Конфигурация по умолчанию: **x64 / Debug** или **x64 / Release**. `Any CPU` тоже работает (явно прокинуто `PlatformTarget=x64`).
- Релиз кладёт **один exe** в `bin\Release\` — Costura встраивает все DLL. Конфиг `App.config` дублируется в `Program.cs` через `AppContext.SetSwitch`, чтобы можно было запускать exe без `.exe.config` рядом.

## Что игнорировать (см. `.cursorignore`)

`bin/`, `obj/`, `build.log`, `*.cache`, `*.pdb`, `*.dll`, `*.exe`, `.vs/`, `Properties/*.Designer.cs`, бинарные ресурсы.

## Подсказки для частых задач

- **Добавить нового LLM-провайдера**: enum в `LocalTranslateApi.cs` → ключ в массиве `TranslationBackendKeysCore` или `TranslationBackendKeysJunk` в `Form1.cs` → ветка в свитче запроса в `LocalTranslateApi`.
- **Новое поле в JSON-экспорте Unity**: посмотри `UabeaJsonPaths.cs` (карта путей с переводимым текстом).
- **Новая колонка в таблице переводов**: `Form1.Layout.cs` (создание `dgv`) + `TranslationItem.cs`.
- **Новый формат TXT-обмена**: `TranslationTxtExchange.cs`, не дублируй логику в `Form1`.

## Что НЕ нужно делать

- Не переводить проект на SDK-style csproj и PackageReference без явной просьбы — сломает Costura/Fody на этой версии.
- Не добавлять `System.Text.Json`, `Span<T>`-API, `record`, `init`-сеттеры — целевой framework 4.8, доступен C# 7.3 без правки `LangVersion`.
- Не создавать новые `*.Designer.cs` через VS designer для существующих форм — UI собирается в коде в `Form1.Layout.cs`.
- Не запускать `dotnet build` — проект не SDK-style. Используй `msbuild` или Visual Studio.
