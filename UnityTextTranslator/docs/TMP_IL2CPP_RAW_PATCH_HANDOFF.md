# Handoff: IL2CPP raw-патч TMP_FontAsset + Texture2D (кириллица / MSDF 512×512)

## Где этот файл (пути)

| cwd (открытая в IDE папка) | AGENTS.md | Этот handoff |
|----------------------------|-----------|--------------|
| Git-корень (есть `UnityTextTranslator.slnx`) | `UnityTextTranslator/AGENTS.md` | `UnityTextTranslator/docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md` |
| Папка проекта (есть `UnityTextTranslator.csproj`) | `AGENTS.md` | `docs/TMP_IL2CPP_RAW_PATCH_HANDOFF.md` |

**Ошибка:** из папки проекта искать `UnityTextTranslator/AGENTS.md` — получится несуществующий вложенный каталог.

**Для Claude Code:** прочитай также `CLAUDE.md` в корне репо или в папке проекта.

**Для Claude Code / любого AI:** прочитай этот файл целиком перед правками. Контекст прошлой сессии: [transcript 33d3e9ed](file:///C:/Users/redo/.cursor/projects/c-Users-redo-source-repos-UnityTextTranslator/agent-transcripts/33d3e9ed-98ac-43c5-8b3e-18f0a94981f8/33d3e9ed-98ac-43c5-8b3e-18f0a94981f8.jsonl) (поиск по `7296`, `0x93C`, `GlyphVerify`, `msdf`).

## ТЕКУЩЕЕ СОСТОЯНИЕ (готово) — мастер из 4 кнопок

Замена шрифта реализована как **пошаговый мастер** (UI: `Form1.FontWizard.cs`, кнопки слева направо):
1. **Анализ .assets** — `TmpFontAssetMsdfAtlasPatcher.AnalyzeTmpFonts` находит TMP_FontAsset + атлас-текстуру + размер.
2. **Создать атлас (TTF)** — `MsdfAtlasGenInterop.Run` под размер игры (`-pxrange 6`, `-size≈dim/21`).
3. **Патч (рост кириллицы)** — `ReplaceTexture2DAtlasFromPngSameFile(growCyrillicTables:true)` → `BuildGrownTablesWithCyrillic`.
4. **Применить** — бэкап `.bak` + замена .assets.

Отладочный UI (тест-режимы Test1/2/3, маркер, старые кнопки patch/atlas/export) и мёртвые методы патчера (`PatchSameFile`, `ExportTexture2DAlpha8ToPng`, `PatchCharacterTableRawFromAtlasJsonSameFile`) **удалены** (сессия 2025-05-30). Остались внутри `ReplaceTexture2DAtlasFromPngSameFile` неиспользуемые A/B-ветки + `[7296]`-диагностика + вызов `TmpFontAssetIl2CppRawMetadataPatcher.Apply` (не на grow-пути) — можно вычистить отдельным проходом, переписав метод в «только рост».

Открытый вопрос на будущее: `StreamingAssets/aa/catalog.json` (Addressables) — если часть текста/шрифтов в бандлах, нужен их патч + обновление CRC (`AddressablesCatalogCrcInterop`). Для проверенного контента хватило resources.assets.

---

## ✅ РЕШЕНО (итог 2025-05-30) — кириллица в игре читается

После долгого реверса задача закрыта. Рабочий пайплайн и **главные открытия**:

### Структура TMP_FontAsset 7296 (IL2CPP raw)
| Элемент | Где |
|---|---|
| m_GlyphTable | count@`0xF4`, entries@`0xF8`, stride **52**, GlyphRect@+24, m_Index **не равен** позиции (начинается с 3) |
| Glyph entry (52б) | m_Index(4) · metrics 5×float(20) · GlyphRect 4×int(16) · scale(4) · atlasIndex(4) · classDef(4) |
| m_CharacterTable | count@(`0xF8`+glyphCount·52), запись **16б**: `m_ElementType(=1) · m_Unicode · m_GlyphIndex · m_Scale` |
| m_AtlasTextures[0].PathID | сразу за CharacterTable: `charTableEnd+8` |

Локация через `TryLocateGlyphTable7296` (сигнатура записи: scale@+40==1.0, atlasIndex@+44==0); count берётся из поля размера вектора, **не** из эвристики.

### 🔑 ГЛАВНАЯ ПРИЧИНА всех мучений
Атлас шрифта 7296 — это **Texture2D PathID 406** (1024×1024, пиксели в **`.resS` стриме**), а вовсе не 404 (512). TMP рендерит её **через материал шрифта** (`_MainTex`), поэтому:
- патч текстуры 404 и редирект `m_AtlasTextures→404` **не влияли на рендер**;
- координаты глифов должны быть в **1024-пространстве** (m_AtlasWidth=1024).

### Рабочий пайплайн (режим «Полный + РОСТ кириллицы»)
1. Генерим атлас **1024×1024** (кнопка «Создать атлас с кириллицей», msdf-atlas-gen `-dimensions 1024 1024 -size 40`, charset ASCII+кириллица) из шрифта игры (Arial/Liberation Sans).
2. `BuildGrownTablesWithCyrillic`: добавляет кириллические записи в m_GlyphTable+m_CharacterTable (объект растёт → полная перезапись), метрики = planeBounds × калиброванный pointSize (≈median(существующая metricH / planeH)), GlyphRect через **ceil/floor внутрь ячейки** (иначе видны части соседних глифов).
3. Авто-определяет атлас-текстуру (406) из `m_AtlasTextures[0]` и **патчит её inline** (`PatchTexture2DWithAlpha8`: image data inline 1024², чистит m_StreamData) — всё в одном `resources.tmp_raw.assets`, без `.resS`.
4. Никакого редиректа/смены m_AtlasWidth.

### Тупики (на будущее — НЕ повторять)
- Текстура 404 (512) — **не та**; атлас шрифта 406 (1024, .resS).
- Редирект `m_AtlasTextures[0]` 406→404 — **не работает** (рендер идёт через материал→406).
- Y-флип rect (`atlasH-top`) — **не нужен**, верно `y=bottom`.
- Метрики масштабировать на `atlas.size` (36) — неверно, нужно на pointSize шрифта.
- «Латиница ок» вводило в заблуждение — её рисует **другой шрифт**, не 7296 (проверено маркером 'A'→'Ж').

---

## Цель (исходная)

Встроить в игру (Unity IL2CPP) кириллицу в **TextMesh Pro** без изменения размера `resources.assets`:

1. Заменить **SDF-атлас** в `Texture2D` (PathID **404**) на 512×512 Alpha8 из PNG (красный канал = SDF).
2. В **TMP_FontAsset** (сейчас целевой PathID **7296**, раньше отлаживали **7295**) обновить метаданные атласа, charset и **GlyphRect** в `m_GlyphTable` по JSON от `msdf-atlas-gen`.
3. **Не ломать лобби:** патч только in-place (`ByteSize` объекта не меняется). Полная пересборка `m_CharacterTable` в UI отключена (`skipCharTable = true`).

Игра падала при агрессивных патчах; отладка идёт через **A/B режимы** (только текстура / только GlyphRect / только размер атласа).

## Ключевые PathID (текущая игра)

| Объект | PathID | Примечание |
|--------|--------|------------|
| `Texture2D` (атлас) | **404** | image data в **конце** raw-объекта, 262144 байт (512×512 Alpha8) |
| `TMP_FontAsset` | **7296** (default в UI) | структура raw **отличается** от 7295 |
| `TMP_FontAsset` (старый дамп) | 7295 | glyph table @ `0x100`, stride **52** — эталон для сравнения |

## Главные файлы

| Файл | Роль |
|------|------|
| `TmpFontAssetMsdfAtlasPatcher.cs` | Пайплайн in-place: текстура, TMP raw, GlyphRect, diff/verify, диагностика **7296** |
| `TmpFontAssetIl2CppRawMetadataPatcher.cs` | Фиксированные offset: `m_AtlasWidth/Height`, creationSettings, charset (50 байт ASCII) |
| `MsdfAtlasGenInterop.cs` | Запуск `msdf-atlas-gen`, `charset.txt`, логи `[msdf ...]` |
| `Form1.cs` | Кнопка raw-патча, диалог PathID (default **7296**), `Il2CppRawPatchTestMode` |

Сборка: **.NET Framework 4.8**, x64, WinForms. См. также `AGENTS.md` в корне проекта приложения.

## Пайплайн (`ReplaceTexture2DAtlasFromPngSameFile`)

1. PNG → SDF bytes (Alpha8 из R-канала), 512×512.
2. **Texture2D 404:** читать raw, писать только последние 262144 байт (заголовок не трогать).
3. **TMP raw:** `TmpFontAssetIl2CppRawMetadataPatcher.Apply` (опционально `atlasSizeOnly` в тесте 3).
4. **GlyphRect:** `BuildPatchedGlyphTableFromExistingCharacterTable` — **не меняет** размер/состав CharacterTable; читает существующие записи, для кириллицы из JSON обновляет только прямоугольники в glyph table.
5. `WritePatchedObjectsInPlace` → выходной файл (часто `.tmp_raw.assets`), лог `[Diff]` первых отличий.

### Режимы теста (UI, `Form1`)

| Режим | skipTexture | skipGlyph | metadataAtlasSizeOnly |
|-------|-------------|-----------|------------------------|
| Full | — | — | — |
| Test 1 texture only | | skipGlyph | |
| Test 2 GlyphRect only | skipTexture | | |
| Test 3 atlas size only | skipTexture | skipGlyph | **true** |

Всегда: `skipCharTable = true` (полный rebuild CharacterTable не вызывается из кнопки).

## Смещения: что подтверждено / что в работе

### Метаданные TMP (общие для 7295/7296 в текущем коде)

Проверены на дампе IL2CPP, **могут быть неверны для 7296** — сверять по логу и крашам:

| Поле | Offset |
|------|--------|
| `m_AtlasWidth` | `0x15CC` |
| `m_AtlasHeight` | `0x15D0` |
| creationSettings width/height | `0x1EEC` / `0x1EF0` |
| charset (50 байт) | `0x1EFC` |
| hash (только лог, **не обнуляется**) | `0x40` |

### Glyph table — PathID **7295** (эталон)

| | Offset |
|---|--------|
| count | `0x100` |
| entries | `0x104` |
| stride записи | **52** (`GlyphTableEntrySize`) |
| GlyphRect в записи | `+4` от начала записи (x,y,w,h int32) |

### Glyph table — PathID **7296** ✅ **ПОДТВЕРЖДЕНО ДАМПОМ ЗАГОЛОВКА 2025-05-30**

| | Offset / значение |
|---|--------|
| count | **`0xF4`** = **250** (размер вектора) |
| entries (позиция 0) | **`0xF8`** |
| stride | **52** (m_Index(4) + 5×float metrics(20) + GlyphRect(16) + scale(4) + atlasIndex(4) + classDef(4)) |
| GlyphRect внутри записи | **+24** |
| **m_Index** | **начинается с 3**, идёт 3,4,5,… → `m_Index = позиция + 3`, поэтому `glyphIndex ≠ позиция` |
| сигнатура записи | `scale@+40 == 1.0f`, `atlasIndex@+44 == 0` |
| конец glyph table | `0xF8 + 250*52 = 0x33C0` |

**Как подтвердили:** дамп `[7296 GThdr]` показал MonoBehaviour-заголовок: m_Script PathID 2691, m_Name «LiberationSans SDF», m_Version «1.1.0», GUID, FaceInfo «Liberation Sans / Regular», и на `0xF4` = `FA 00 00 00` (250), на `0xF8` первая запись m_Index=3.

**`0x58`/`0x5C` и `0x93C`/`0x940` — обе прошлые гипотезы ОШИБОЧНЫ.** `m_Index` 4,5,6,7,8 в дампе шли подряд случайно (это середина таблицы). В коде `0x58` оставлен только как маркер «это 7296»; реальное начало находит `TryLocateGlyphTable7296` по сигнатуре `scale=1.0,atlasIndex=0`, а count берётся из поля `entriesStart-4`.

### 🔥 Причина крашей: метаданные Apply рушат glyph table

Offset'ы `TmpFontAssetIl2CppRawMetadataPatcher.Apply` (m_AtlasWidth `0x15cc`, m_AtlasHeight `0x15d0`, creationSettings `0x1eec/0x1ef0`, charset `0x1efc`×50) — это **7295-калибровка**. Для 7296 они попадают **внутрь glyph table** (0x15cc → запись ~102, 0x1efc → запись ~147-148) и затирают глифы. Поэтому игра падала, и поэтому signature-run раньше обрывался на 147 (charset на `0x1efc` затирал `scale@+40`).

**Фикс:** для 7296 `Apply` теперь **не вызывается** (атлас уже 512×512, метаданные править не нужно). Реальные m_AtlasWidth/charset 7296 лежат **после** всех таблиц — точные offset'ы пока не нужны.

При патче 7296 в лог пишется (после сборки 2025-05-30):

- `[7296] GlyphTable stride verdict (count=N, idx[0]=M):` — затем по строке на каждый stride
- `[7296] stride=N idx[1]=X ← разумный` (если `0 ≤ X < count` и `X ≠ idx[0]`) **или** `(вне 0..N-1)`
- Старые зонды: `[TMP7296]`, `[FindGlyph]`, `[FindGlyphTable]`, `[Find527]`

**Критерий подтверждения stride:** строка `← разумный` для единственного (или очевидно правильного) stride.  
**Критерий подтверждения count@0x93C:** значение ~ сотни глифов (ожидаем ~527); если 0 или >3000 — offset неверен, смотреть `[FindGlyphTable]`.

### Character table — PathID **7296** ✅ **ПОДТВЕРЖДЕНО ЛОГОМ 2025-05-30**

CharacterTable идёт **сразу за GlyphTable**. Для этого ассета: count@`0x33C0`=250, entries@`0x33C4`.

```
CT count offset = 0xF8 + glyphCount * 52      (= 0x33C0 при glyphCount=250)
CT entries      = CT count offset + 4
```

**Запись CharacterTable 7296 = 16 байт** (TMP_Character с сериализованным m_ElementType):

| Поле | Offset | entry[0] |
|------|--------|----------|
| `m_ElementType` | +0 | 1 (Character) |
| `m_Unicode` | +4 | 32 (`U+0020` пробел) |
| `m_GlyphIndex` | +8 | 3 |
| `m_Scale` | +12 | 1.0 |

⚠️ **Это НЕ `[unicode][glyphIndex][scale][pad]`** (как у 7295) — поля сдвинуты на +4 из-за m_ElementType.

**Стратегия патча 7296 (после 2025-05-30):** `BuildPatchedGlyphTableFromExistingCharacterTable` теперь **сопоставляет по unicode** — для каждой записи CharacterTable, чей `m_Unicode` есть в новом атласе (JSON), обновляет GlyphRect её глифа через карту `m_Index→offset`. CharacterTable in-place не меняется.

**Открытый вопрос (решит лог `[TMP CharTable] 7296: … (кириллических N)`):** есть ли в CharacterTable шрифта кириллические unicode (0x0400-0x04FF)?
- **N > 0** → подход рабочий, кириллица отобразится.
- **N == 0** → шрифт собран без кириллицы; нужен либо homoglyph-ремап (переназначить латиницу), либо рост таблицы (не in-place). Решение за пользователем.

| | Offset |
|---|--------|
| count (7295) | `0xB94` |
| entries (7295) | `0xB9C`, запись 16 байт `[unicode][glyphIndex][scale][pad]` |
| count (7296) | `0xF8 + glyphCount*52` (= `0x33C0`) |
| entries (7296) | count + 4, запись 16 байт `[elementType][unicode][glyphIndex][scale]` |

## msdf-atlas-gen

Типичные аргументы: `-type sdf`, `-format png`, `-dimensions 512 512`, `-size 36`, `-imageout atlas512_sdf.png`, `-json atlas512_sdf.json`, `-charset charset.txt`.

- `charset.txt`: **один codepoint на строку** (диапазоны в файле ломали генерацию).
- JSON: glyphs с `atlasBounds` / unicode — используется в `BuildUnicodeAtlasRectMap`.

## Диагностика в логе (искать после прогона)

| Префикс | Смысл |
|---------|--------|
| `[Tex404]` | исходная текстура |
| `[Tex raw]` | offset image data |
| `[Hash]` | до/после патча |
| `[GlyphVerify]` | stride probe, read-back GlyphRect |
| `[Diff]` | отличия orig vs patched |
| `[7296]` / `[TMP7296]` | разведка структуры 7296 |
| `[7296] GlyphTable stride verdict` | **новое**: вердикт — какой stride разумный |
| `[7296 CT]` | **новое**: результат эвристического поиска CharacterTable для 7296 |
| `[TMP GlyphTable] 7296 CharacterTable` | **новое**: CT offset при патче GlyphRect |
| `[msdf cmd/stdout/exit]` | генератор атласа |

## Рост таблиц для кириллицы (РЕАЛИЗОВАНО 2025-05-30)

Лог подтвердил: CharacterTable шрифта = 250 ASCII/Latin символов, **кириллицы 0**. Поэтому добавлен режим роста:

- UI: `Form1` → кнопка raw-патча → режим **«Полный + РОСТ кириллицы (перезапись, не in-place)»** (`Il2CppRawPatchTestMode.FullGrowCyrillic`). Спрашивает PNG атласа и JSON.
- `ReplaceTexture2DAtlasFromPngSameFile(..., growCyrillicTables: true)` → `BuildGrownTablesWithCyrillic`:
  - находит glyph table (`0xF8`/250) и char table (`0x33C0`/250);
  - для каждого кириллического `U+0400…04FF` из JSON (с `atlasBounds`), которого нет в шрифте, добавляет запись Glyph(52) + Char(16);
  - объект РАСТЁТ → запись через `WriteAssetsFileToPath` (полный `AssetsFile.Write`, не in-place).
- Для 7296 `Apply` метаданных **отключён** (offset'ы рушили glyph table).

**Риски (проверить в игре):** m_AtlasPopulationMode (если Dynamic — TMP может игнорировать ручные глифы), масштаб метрик (`pxPerEm` = `atlas.size`=36; если кириллица не того размера — калибровать), m_UsedGlyphRects/m_FreeGlyphRects не обновляются.

## Следующие шаги (приоритет)

1. **Собрать**, запустить режим **«Полный + РОСТ кириллицы»**, PathID 7296, выбрать PNG+JSON нового атласа.
2. В логе проверить `[Grow] Добавлено N символов … Размер объекта X → Y`.
3. Применить `.tmp_raw.assets` (заменить `resources.assets`), запустить игру: **не падает ли лобби**, **рисуется ли кириллица** и **правильного ли размера**.
4. Если размер кириллицы не тот — калибровать `pxPerEm`/метрики в `BuildGrownTablesWithCyrillic`.
5. Если падает — проверить m_AtlasPopulationMode (Static нужен) и rect-конвенцию (y=bottom vs flip).

## Чего не делать без явной просьбы

- Hash @ `0x40` не обнулять (убрано намеренно).
- `Apply` (0x15cc/0x1efc) для 7296 НЕ включать — рушит glyph table.
- Не менять SDK-style csproj / PackageReference.

## Быстрый старт для агента

```text
1. Открыть TmpFontAssetMsdfAtlasPatcher.cs — ReplaceTexture2DAtlasFromPngSameFile, BuildPatchedGlyphTableFromExistingCharacterTable.
2. Прочитать блок if (tmpId == 7296) ~строка 252 — диагностика.
3. Прочитать Form1.cs BtnReplaceTexture2DAtlasFromPng_Click ~5306 — UI и флаги тестов.
4. Спросить у пользователя последний лог [7296] или воспроизвести патч сами на их resources.assets.
```

## Статус на момент handoff (2025-05-30)

- Структура 7296 **полностью декодирована и подтверждена логом**: GlyphTable `0xF8`/250/stride52/rect+24/m_Index=поз+3; CharacterTable `0x33C4`/250/16б `[elementType][unicode][glyphIndex][scale]`.
- Причина крашей найдена и устранена: `Apply` (7295-offsets) писал внутрь glyph table — **отключён для 7296**.
- In-place GlyphRect-патч латиницы: работает (`match=True`), сопоставление по unicode.
- Кириллицы в шрифте нет (K=0) → реализован **рост таблиц** + полная перезапись. **Ожидает проверки в игре** (краш/рендер/размер).
