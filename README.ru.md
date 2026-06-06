<!-- Язык: Русский | [English](README.md) -->

# Unity Text Translator

Программа для удобного перевода игр на движке Unity.

## Что умеет

- Модуль Unity .assets: экспорт объектов из `.assets` в JSON и сборка обратно в новый `.assets` после правок JSON.
- Можно локализовать файлы с расширением `.bundle`.
- Замена в ассете на шрифт с кириллицей (для игр на IL2CPP).
- Можно доставать строки из JSON: рекурсивно находит текстовые поля в дереве Unity JSON и складывает их в таблицу.
- Память переводов (TM): переиспользует уже переведённые совпадения.
- Автоматический перевод через api-ключ. Сам переведёт пустые строки, а то, что является кодом, пропустит. Бэкенды: LibreTranslate, OpenRouter, локальные OpenAI-совместимые (например, LM Studio).
- Вставка ответа ИИ из буфера, поиск по таблице, темы, автосохранение.

Сейчас в приложении есть баги, но они будут исправляться.

## Сборка

Открыть `UnityTextTranslator.slnx` в Visual Studio и собрать в Release. Зависимости пакуются в один `.exe` через Fody/Costura. Проект на .NET Framework 4.8, пакеты NuGet тянутся сами.

## Сторонние компоненты

- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) и [AddressablesTools](https://github.com/nesrak1/AddressablesTools) (nesrak1, MIT) — чтение и запись файлов Unity, через NuGet.
- Формат JSON совместим с [UABEA / UABEAvalonia](https://github.com/nesrak1/UABEA) (nesrak1, MIT); `classdata.tpk` качается из этого проекта в рантайме. Кода UABEA здесь нет.
- [Mono.Cecil](https://github.com/jbevain/cecil), [Newtonsoft.Json](https://www.newtonsoft.com/json), [Fody/Costura](https://github.com/Fody/Costura) — через NuGet.
- [msdf-atlas-gen](https://github.com/Chlumsky/msdf-atlas-gen) (Viktor Chlumský, MIT) — генерация SDF-атласа для замены шрифта.

## Лицензия

[MIT](LICENSE) © 2026 redonkym.
