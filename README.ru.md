[English](README.md) | **Русский**

[![Boosty](https://img.shields.io/badge/Boosty-redonkym-F15F2C?logo=boosty&logoColor=white)](https://boosty.to/redonkym)

# Unity Text Translator

Программа для удобного перевода игр на движке Unity.

![Unity Text Translator — переводчик JSON](docs/img/json-translator.png)

![Unity Text Translator — настройки](docs/img/settings.png)

## Что умеет

- Модуль Unity .assets: экспорт объектов из `.assets` в JSON и сборка обратно в новый `.assets` после правок JSON.
- Можно локализовать файлы с расширением `.bundle`.
- Замена в ассете на шрифт с кириллицей (для игр на IL2CPP).
- Можно доставать строки из JSON: рекурсивно находит текстовые поля в дереве Unity JSON и складывает их в таблицу.
- Память переводов (TM): переиспользует уже переведённые совпадения.
- Автоматический перевод через api-ключ. Сам переведёт пустые строки, а то, что является кодом, пропустит. Бэкенды: LibreTranslate, OpenRouter, локальные OpenAI-совместимые (например, LM Studio).
- Вставка ответа ИИ из буфера, поиск по таблице, темы, автосохранение.

Сейчас в приложении есть баги, но они будут исправляться.

## Как пользоваться

1. **Получите текст игры в виде JSON.** Если дампы JSON уже есть — пропустите. Иначе на вкладке **Unity .assets** или **Bundles** экспортируйте `.assets`/`.bundle` игры в папку с JSON.
2. **Откройте папку.** На вкладке **JSON Files** нажмите **Folder** и выберите эту папку с JSON. Таблица заполнится строками: Файл / Путь / Оригинал / Перевод.
3. **Переведите.** Впишите перевод в колонку **Translation**, или:
   - **AI translation** — заполнить пустые ячейки через API из настроек (LibreTranslate / OpenRouter / локальный OpenAI-совместимый сервер);
   - **Copy / Paste** — скопировать строки таблицей, вставить в любую ИИ-модель, ответ вставить обратно (сопоставляется по содержимому, порядок строк не важен);
   - память переводов сама подставит уже переведённые совпадения.
4. **Save changes** — запишет перевод обратно в файлы JSON (галку **Create .bak backups** лучше держать включённой).
5. **Верните в игру.** На вкладке **Unity .assets** / **Bundles** соберите переведённый JSON обратно в `.assets`/`.bundle`.
6. **Кириллица не отображается? (IL2CPP / TextMeshPro)** Откройте вкладку **Fonts** и пройдите мастер: анализ `.assets` → атлас → патч → применить.

Язык оригинала/перевода, тему и API-ключ задайте на вкладке **Settings**.

## Сборка

Открыть `UnityTextTranslator.slnx` в Visual Studio и собрать в Release. Зависимости пакуются в один `.exe` через Fody/Costura. Проект на .NET Framework 4.8, пакеты NuGet тянутся сами.

Юнит-тесты: `dotnet test` — проект `UnityTextTranslator.Tests` на net8.0, Visual Studio не нужна.

## Сторонние компоненты

- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) и [AddressablesTools](https://github.com/nesrak1/AddressablesTools) (nesrak1, MIT) — чтение и запись файлов Unity, через NuGet.
- Формат JSON совместим с [UABEA / UABEAvalonia](https://github.com/nesrak1/UABEA) (nesrak1, MIT); `classdata.tpk` качается из этого проекта в рантайме. Кода UABEA здесь нет.
- [Mono.Cecil](https://github.com/jbevain/cecil), [Newtonsoft.Json](https://www.newtonsoft.com/json), [Fody/Costura](https://github.com/Fody/Costura) — через NuGet.
- [msdf-atlas-gen](https://github.com/Chlumsky/msdf-atlas-gen) (Viktor Chlumský, MIT) — генерация SDF-атласа для замены шрифта.

## Лицензия

[MIT](LICENSE) © 2026 redonkym.
