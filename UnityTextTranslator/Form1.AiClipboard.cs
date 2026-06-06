using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    public partial class Form1
    {
        private void BtnCopySelectedAi_Click(object sender, EventArgs e)
        {
            if (!RequireJsonTranslatorSurface("копирование в буфер"))
                return;

            SyncGridToItems();
            SyncJsonCopyModeFromSettingsUiIfAvailable();

            var indices = dgv.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(x => !x.IsNewRow && x.Index >= 0 && x.Index < translationItems.Count)
                .OrderBy(x => x.Index)
                .Select(x => x.Index)
                .Distinct()
                .ToList();

            if (indices.Count == 0)
            {
                indices = dgv.Rows
                    .Cast<DataGridViewRow>()
                    .Where(x => !x.IsNewRow && x.Visible && x.Index >= 0 && x.Index < translationItems.Count)
                    .OrderBy(x => x.Index)
                    .Select(x => x.Index)
                    .Distinct()
                    .ToList();
            }

            // indices — индексы СТРОК ГРИДА; элемент берём из строки через Tag (RowItemAt), а не translationItems[idx],
            // чтобы копировалась ровно та пара «Оригинал→Перевод», что видна в строке (без сдвига при пересортировке).
            CopyItemsForAi(
                indices.Select(RowItemAt).Where(it => it != null),
                dgv.SelectedRows.Count > 0 ? "выбранные строки" : "видимые строки");
        }

        private void CopyItemsForAi(IEnumerable<TranslationItem> items, string sourceName)
        {
            var rawList = items
                .Where(x => !string.IsNullOrWhiteSpace(x.Original))
                .ToList();

            if (rawList.Count == 0)
            {
                Log(L("Nothing to copy.", "Нет текста для копирования."), true);
                return;
            }

            var list = rawList.Where(ShouldIncludeByJsonCopyMode).ToList();

            if (list.Count == 0)
            {
                Log(L("Nothing to copy after filtering technical rows.", "После фильтра служебных строк копировать нечего."), true);
                return;
            }

            var lines = new List<string>
            {
                "=== Инструкция для языковой модели (Unity Text Translator) ===",
                "Ниже таблица строк из дампов Unity JSON (MonoBehaviour и др.), которые нужно перевести для локализации игры.",
                "",
                "Формат данных: TSV (колонки разделены символом TAB). Первая строка после этого блока — заголовок таблицы.",
                "Колонки:",
                "  1) Файл — только имя JSON-файла; не менять и не переводить.",
                "  2) Путь в JSON — цепочка ключей к полю в JSON (например m_Text › text); не менять.",
                "  3) Оригинал — исходная строка на языке источника.",
                "  4) Перевод — ЗАПОЛНИ перевод на целевой язык (пользователь ожидает качественный игровой текст). Колонка сейчас пустая после табуляции.",
                "",
                "Требования:",
                "— Сохраняй порядок строк и точное содержимое колонок 1–3.",
                "— Не добавляй и не удаляй строки данных.",
                "— Сохраняй плейсхолдеры ({0}, %d), HTML/XML-подобные теги Unity/TextMeshPro, переносы строк и экранирование как в оригинале.",
                "— Фрагменты вида <?shake> … ?> и любые <? … ?> — это буквальный игровой код/разметка (не HTML-документ для «исправления»): не склеивай несколько физических строк таблицы в одну из‑за угловых скобок, не переписывай разметку как обычный текст; внутри одной ячейки сохраняй переносы и пробелы как в «Оригинале».",
                "— Переводи смысл и стиль; допускается естественная адаптация на целевом языке без искажения контента.",
                "",
                "Чего не переводить (в колонке «Перевод» поставь ТОЧНУЮ копию «Оригинала» как есть):",
                "— Строки, где «Путь в JSON» заканчивается на m_Name или ведёт к чисто служебному имени объекта/ассета: если «Оригинал» выглядит как код или имя ресурса (слитное PascalCase/CamelCase без пробелов, например ConcentratedMutantProtein; внутренние кодовые имена предметов/баффов; совсем без пробелов и без знаков видимого человеческого предложения) — это не пользовательский текст.",
                "— Технические ветки и содержимое: … › input › port или … › input › node под RefIds; поля вида <Guid>k__BackingField; propertyName в графах; bodyPartTag, bodyPartLayer, currentTag, objectTag; строки GUID/UUID, квалифицированные имена типов Unity/System, чистые числа/URL, типичная сериализация Unity и машинные пути Assets/Packages.",
                "— То, что похоже на ключ словаря, ID локализации, хеш или enum-длинную строку без местоимений — не локализуй.",
                "Если по пути видно игровой UI или повествование (например m_Text, text, description, title для понятной игроку фразы) — переводи нормально.",
                "",
                "[English for model:] Treat <?…?> tokens (e.g. <?shake>) as literal in-game markup/code, NOT parsable HTML/XML—never merge multiple TSV data rows into one line because of angle brackets, and never “pretty-print” or reflow that markup into prose. Preserve line breaks and spacing inside a cell exactly as in Original. Do NOT translate rows that are Unity/developer identifiers — echo Original unchanged into Перевод when Path suggests internals (e.g. ending with m_Name with PascalCase asset/code names like ConcentratedMutantProtein), Visual Scripting graph wiring under RefIds with …/input/port or …/input/node, GUID backing fields, propertyName, bodyPart*, objectTag, GUIDs/serialized Unity noise or Assets paths. Translate real player-facing UI/dialog/description strings.",
                "",
                "Формат ответа: верни одну таблицу TSV с тем же заголовком и четырьмя колонками; в колонке «Перевод» должны быть готовые строки.",
                "Если во фразе есть перенос строки (#13/#10 или Enter), каждая такая строка данных всё равно должна занимать ровно ОДНУ физическую строку всего блока ответа: запиши переносы как два символа обратная косая + n (‹\\› + ‹n›), как в уже выданном вам примере, а не как настоящий перевод каретки между строками TSV.",
                "[English:] If Original or Перевод must contain hard line-breaks inside the cell, keep each TSV ROW as ONE physical screen line — encode internal breaks as a two-character ‹\\› + ‹n› (ASCII backslash+n), never as a real newline that would split one record across multiple pasted lines.",
                "=== Конец инструкции ===",
                "",
                "Файл\tПуть в JSON\tОригинал\tПеревод"
            };

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                lines.Add($"{ToTsvField(item.FileName)}\t{ToTsvField(item.DisplayPath)}\t{ToTsvField(item.Original)}\t");
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            var skippedByMode = Math.Max(0, rawList.Count - list.Count);
            if (skippedByMode > 0)
                Log(L($"Copied {sourceName}: {list.Count}. Skipped by rules: {skippedByMode}.", $"Скопированы {sourceName}: {list.Count}. Пропущено по правилам: {skippedByMode}."));
            else
                Log(L($"Copied {sourceName}: {list.Count}.", $"Скопированы {sourceName}: {list.Count}."));
        }

        private bool ShouldIncludeByJsonCopyMode(TranslationItem item)
        {
            if (item == null)
                return false;
            if (NormalizeJsonCopyModeIndex(jsonCopyModeSelectedIndex) == 1)
                return true; // режим "копировать всё"
            return !IsTechnicalOnlyJsonCopyRow(item) && !ShouldLeaveOriginalUntranslatedForLocalAi(item);
        }

        /// <summary>Содержимое «Оригинала» похоже на сериализацию Unity/GUID/чистые числа — для режима «копировать по правилам».</summary>
        private static bool JsonCopyRowLooksLikeNoiseContent(string original, string leaf)
        {
            var o = (original ?? "").Trim();
            if (o.Length == 0)
                return false;

            var lk = (leaf ?? "").Trim();
            if (MetadataPurgeGameplayStringKeys.Contains(lk) ||
                string.Equals(lk, "m_Localized", StringComparison.OrdinalIgnoreCase))
                return false;

            if (LooksLikeTechnicalUnitySerializedString(o))
                return true;

            if (LooksLikeQualifiedUnityEngineTypeName(o))
                return true;

            if (JsonCopyEmbeddedGuidPattern.IsMatch(o))
                return true;

            if (LooksLikeTechnicalUnityIdentifier(o))
                return true;

            if (Regex.IsMatch(o, @"^(https?|steam):\/\/", RegexOptions.IgnoreCase))
                return true;

            if (o.Length >= 4 && Regex.IsMatch(o, @"^-?[0-9]+(?:[\.,][0-9]+)?$"))
                return true;

            if (o.Length >= 12)
            {
                var letters = 0;
                foreach (var c in o)
                {
                    var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                    if (uc == UnicodeCategory.UppercaseLetter || uc == UnicodeCategory.LowercaseLetter ||
                        uc == UnicodeCategory.TitlecaseLetter || uc == UnicodeCategory.OtherLetter)
                        letters++;
                }

                if (letters * 3 < o.Length)
                    return true;
            }

            return false;
        }

        /// <summary>Служебные Unity-строки (имена объектов/ID/пути), которые обычно не нужны для перевода через кнопку «Копировать».</summary>
        private static bool IsTechnicalOnlyJsonCopyRow(TranslationItem item)
        {
            if (item == null)
                return false;

            var path = (item.DisplayPath ?? "").Trim();
            var original = (item.Original ?? "").Trim();
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(original))
                return false;

            var leaf = GetTranslationJsonLeafKey(item);

            if (string.Equals(leaf, "m_Name", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("m_Name", StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.IndexOf("m_ExcludedPropertiesInInspector", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (JsonCopyTechnicalLeafHints.Contains(leaf))
                return true;

            if (path.IndexOf("<Guid>k__BackingField", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var fileName = (item.FileName ?? "").Trim();
            if (fileName.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (original.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                original.IndexOf("Packages/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Имя узла/поля без пробелов (слишком похоже на кодовый идентификатор).
            if (string.Equals(leaf, "name", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(original, @"^[A-Za-z0-9_\-\.]{6,}$") &&
                original.IndexOf(' ') < 0)
                return true;

            if (JsonCopyRowLooksLikeNoiseContent(original, leaf))
                return true;

            return false;
        }

        private void BtnPasteAi_Click(object sender, EventArgs e)
        {
            if (!RequireJsonTranslatorSurface("вставка из буфера"))
                return;

            if (translationItems.Count == 0)
            {
                Log(L("No rows to paste translations into.", "Нет строк для вставки переводов."), true);
                return;
            }

            if (!Clipboard.ContainsText())
            {
                Log(L("Clipboard has no text with translations.", "В буфере обмена нет текста с переводами."), true);
                return;
            }

            SyncGridToItems();

            var clipboardText = NormalizeMarkdownTableToTsv(Clipboard.GetText());

            var indices = dgv.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(x => !x.IsNewRow && x.Index >= 0 && x.Index < translationItems.Count)
                .OrderBy(x => x.Index)
                .Select(x => x.Index)
                .Distinct()
                .ToList();

            if (indices.Count > 0)
            {
                var tsvOrdered = ExtractOrderedAiPasteTsvDataRows(clipboardText);
                if (tsvOrdered.Count > 0)
                {
                    var pasted = PasteOrderedTsvIntoSelectedIndices(tsvOrdered, indices);
                    if (pasted.AppliedCount > 0)
                    {
                        ApplyTableSearch();
                        UpdateRowHighlights();
                        UpdateStatus();
                        Log(
                            pasted.AppliedCount +
                            " переводов вставлено по совпадению «Файл/Путь/Оригинал» (порядок и количество строк в ответе ИИ не важны — сдвига больше не будет)." +
                            (pasted.SkippedMismatch > 0
                                ? " Без совпадения в ответе (оставлены как были): " + pasted.SkippedMismatch + "."
                                : ""));
                        BumpDashboardContentStamp();
                        return;
                    }

                    if (pasted.SkippedMismatch > 0)
                        Log(
                            "Ни одна строка TSV не совпала с выделением по «Файл/Путь/Оригинал» — пробуем сопоставить по префиксу и как нумерованный список.",
                            true);
                }

                // Ответ ИИ с колонками через ПРОБЕЛЫ (без табов) + шапка: структурный TSV не сработал.
                // Сопоставляем по префиксу «Файл Путь Оригинал» с известными строками — без сдвига и без шапки.
                var byPrefix = TryPasteAiByRowPrefix(clipboardText, indices);
                if (byPrefix.AppliedCount > 0)
                {
                    ApplyTableSearch();
                    UpdateRowHighlights();
                    UpdateStatus();
                    Log(byPrefix.AppliedCount +
                        " переводов вставлено по совпадению «Файл/Путь/Оригинал» (таблица с пробелами/без табов; шапка и лишние строки пропущены)." +
                        (byPrefix.SkippedMismatch > 0 ? " Без совпадения в ответе: " + byPrefix.SkippedMismatch + "." : ""));
                    BumpDashboardContentStamp();
                    return;
                }

                var translations = ParseAiTranslations(clipboardText);
                if (translations.Count == 0)
                {
                    Log(L("Could not parse translations. Need TSV with a «Translation» column or a list like 1. text", "Не удалось распознать переводы. Нужна TSV с колонкой «Перевод» или список 1. текст"), true);
                    return;
                }

                if (indices.Count == 1 && translations.Count > 1)
                {
                    var startIndex = indices[0];
                    indices = dgv.Rows
                        .Cast<DataGridViewRow>()
                        .Where(x => !x.IsNewRow && x.Visible && x.Index >= startIndex && x.Index < translationItems.Count)
                        .OrderBy(x => x.Index)
                        .Take(translations.Count)
                        .Select(x => x.Index)
                        .ToList();
                }

                var count = Math.Min(indices.Count, translations.Count);
                var undoFrame = new List<TranslationUndoCell>();
                for (var i = 0; i < count; i++)
                {
                    var rowIndex = indices[i];
                    var item = RowItemAt(rowIndex);
                    if (item == null)
                        continue;
                    undoFrame.Add(new TranslationUndoCell { Item = item, PreviousTranslated = item.Translated ?? "" });
                    item.Translated = translations[i];
                    dgv.Rows[rowIndex].Cells["Translated"].Value = translations[i];
                }

                if (undoFrame.Count > 0)
                    PushTranslationUndoFrame(undoFrame);

                ApplyTableSearch();
                UpdateRowHighlights();
                UpdateStatus();

                if (translations.Count != indices.Count)
                    Log(L($"Pasted {count} translations. Rows selected: {indices.Count}, response items: {translations.Count}.", $"Вставлено {count} переводов. Выбрано строк: {indices.Count}, элементов ответа: {translations.Count}."), true);
                else
                    Log(L($"Pasted translations from clipboard: {count}.", $"Вставлено переводов из буфера: {count}."));

                if (count > 0)
                    BumpDashboardContentStamp();

                return;
            }

            // Нет выделения: пробуем сопоставить по префиксу «Файл/Путь/Оригинал» над ВСЕМИ строками
            // (на случай ответа ИИ с пробелами вместо табов), затем — по ключу через PasteAiTableByMatch.
            var allRowIndices = Enumerable.Range(0, translationItems.Count).ToList();
            var byPrefixAll = TryPasteAiByRowPrefix(clipboardText, allRowIndices);
            if (byPrefixAll.AppliedCount > 0)
            {
                ApplyTableSearch();
                UpdateRowHighlights();
                UpdateStatus();
                Log(byPrefixAll.AppliedCount +
                    " переводов вставлено по совпадению «Файл/Путь/Оригинал» (по всей таблице; шапка и лишние строки пропущены).");
                BumpDashboardContentStamp();
                return;
            }

            var matchedCount = PasteAiTableByMatch(clipboardText);
            if (matchedCount > 0)
            {
                ApplyTableSearch();
                UpdateRowHighlights();
                UpdateStatus();
                Log(L($"Pasted translations from clipboard by key matches: {matchedCount}.", $"Вставлено переводов из буфера по совпадениям: {matchedCount}."));
                BumpDashboardContentStamp();
                return;
            }

            Log(L("Select table rows to place the model's response into — otherwise paste only matches exact keys across the whole table.", "Выберите строки таблицы, куда подставить ответ модели — иначе вставка только по точным совпадениям ключей всей таблицы."), true);
        }

        private sealed class AiPasteTsvRow
        {
            public string FileName;
            public string DisplayPath;
            public string Original;
            public string Translated;
        }

        private struct PasteTsvIntoSelectionOutcome
        {
            public int AppliedCount;
            public int SkippedMismatch;
        }

        /// <summary>
        /// ИИ часто отвечает Markdown-таблицей (колонки разделены «|»), а весь парсер вставки
        /// ждёт TSV (табы). Преобразуем такие строки в TSV: срезаем крайние «|», делим по «|»
        /// (кроме экранированных «\|»), пропускаем строку-разделитель «|---|---|». Строки, где
        /// «|» нет, остаются как есть — обычный TSV/нумерованный список не ломается.
        /// </summary>
        private static string NormalizeMarkdownTableToTsv(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('|') < 0)
                return text ?? "";

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var outLines = new List<string>();

            foreach (var rawLine in normalized.Split('\n'))
            {
                var line = rawLine;
                var trimmed = line.Trim();

                // Не Markdown-строка таблицы — оставляем без изменений.
                if (trimmed.IndexOf('|') < 0)
                {
                    outLines.Add(line);
                    continue;
                }

                // Срезаем по одному крайнему «|» (Markdown-обрамление), сохраняя внутренние.
                if (trimmed.StartsWith("|"))
                    trimmed = trimmed.Substring(1);
                if (trimmed.EndsWith("|") && !trimmed.EndsWith("\\|"))
                    trimmed = trimmed.Substring(0, trimmed.Length - 1);

                // Делим по неэкранированным «|».
                var cells = new List<string>();
                var sb = new StringBuilder();
                for (int i = 0; i < trimmed.Length; i++)
                {
                    var c = trimmed[i];
                    if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
                    {
                        sb.Append('|');
                        i++;
                        continue;
                    }
                    if (c == '|')
                    {
                        cells.Add(sb.ToString().Trim());
                        sb.Clear();
                        continue;
                    }
                    sb.Append(c);
                }
                cells.Add(sb.ToString().Trim());

                // Строка-разделитель Markdown («---», «:--:») — выкидываем целиком.
                bool isSeparator = cells.Count > 0 &&
                    cells.All(x => x.Length > 0 && x.All(ch => ch == '-' || ch == ':' || ch == ' '));
                if (isSeparator)
                    continue;

                outLines.Add(string.Join("\t", cells));
            }

            return string.Join("\n", outLines);
        }

        /// <summary>Склеивает физические строки буфера, пока для одной записи не получится 4 TSV-колонки (переводчик разбил строку из-за перевода каретки внутри последней колонки).</summary>
        private static IEnumerable<string> EnumerateLogicalAiPasteTsvRows(string text)
        {
            var normalized = text?.Replace("\r\n", "\n").Replace('\r', '\n') ?? "";
            string buf = null;
            foreach (var rawLine in normalized.Split('\n'))
            {
                var trimmedLead = rawLine.TrimStart();

                if (buf == null)
                {
                    if (string.IsNullOrWhiteSpace(rawLine) ||
                        trimmedLead.StartsWith("#", StringComparison.Ordinal) ||
                        trimmedLead.StartsWith("```", StringComparison.Ordinal))
                        continue;

                    if (rawLine.IndexOf('\t') < 0)
                        continue;

                    buf = rawLine;
                }
                else
                {
                    buf += "\n" + rawLine;
                }

                var parts = buf.Split(new[] { '\t' }, 4, StringSplitOptions.None);
                if (parts.Length == 4)
                {
                    yield return buf;
                    buf = null;
                }
            }

            if (buf != null && buf.IndexOf('\t') >= 0)
                yield return buf;
        }

        /// <summary>Упорядоченный разбор строк TSV данных (игнорирует заголовок и пустые «Перевод»).</summary>
        private static List<AiPasteTsvRow> ExtractOrderedAiPasteTsvDataRows(string text)
        {
            var rows = new List<AiPasteTsvRow>();
            foreach (var logicalLine in EnumerateLogicalAiPasteTsvRows(text))
            {
                if (string.IsNullOrWhiteSpace(logicalLine))
                    continue;

                var parts = logicalLine.Split(new[] { '\t' }, 4, StringSplitOptions.None);
                if (parts.Length < 4)
                    continue;

                var fileName = parts[0].Trim();
                var displayPath = parts[1].Trim();
                var original = parts[2].Trim();
                var translated = parts[3].Trim();

                if (fileName.Equals("Файл", StringComparison.OrdinalIgnoreCase) ||
                    translated.Equals("Перевод", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(translated))
                    continue;

                rows.Add(new AiPasteTsvRow
                {
                    FileName = fileName,
                    DisplayPath = displayPath,
                    Original = original,
                    Translated = translated
                });
            }

            return rows;
        }

        private bool PasteTsvRowMatchesGridItem(TranslationItem item, AiPasteTsvRow row)
        {
            if (item == null || row == null)
                return false;

            var k1 = BuildTranslationKey(item.FileName, item.DisplayPath, item.Original);
            var k2 = BuildTranslationKey(row.FileName, row.DisplayPath, row.Original);
            if (string.Equals(k1, k2, StringComparison.Ordinal))
                return true;

            var kp1 = $"{NormalizeKeyPart(item.DisplayPath)}\u001F{NormalizeKeyPart(item.Original)}";
            var kp2 = $"{NormalizeKeyPart(row.DisplayPath)}\u001F{NormalizeKeyPart(row.Original)}";
            return string.Equals(kp1, kp2, StringComparison.Ordinal);
        }

        private PasteTsvIntoSelectionOutcome PasteOrderedTsvIntoSelectedIndices(
            IReadOnlyList<AiPasteTsvRow> tsvRows,
            IReadOnlyList<int> selectedIndicesSorted)
        {
            // Сопоставляем переводы НЕ по позиции, а по ключу «Файл/Путь/Оригинал». Внешний ИИ часто
            // меняет порядок строк, склеивает многострочные ответы, теряет или добавляет строки — при
            // позиционной привязке это сдвигало весь хвост, и перевод уходил в ЧУЖУЮ строку (Select →
            // дисклеймер 18+ и т.п.). Ключевое сопоставление к таким искажениям ответа устойчиво:
            // пропавшая строка остаётся непереведённой, а не сдвигает остальные.
            var fullKeyToIdx = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var pathOrigToIdx = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < tsvRows.Count; i++)
            {
                var r = tsvRows[i];
                AddPasteIndex(fullKeyToIdx, BuildTranslationKey(r.FileName, r.DisplayPath, r.Original), i);
                AddPasteIndex(pathOrigToIdx, $"{NormalizeKeyPart(r.DisplayPath)}{NormalizeKeyPart(r.Original)}", i);
            }

            var used = new bool[tsvRows.Count];
            var undoFrame = new List<TranslationUndoCell>();
            var applied = 0;
            var skipped = 0;

            foreach (var rowIndex in selectedIndicesSorted)
            {
                var item = RowItemAt(rowIndex);
                if (item == null)
                    continue;

                var pick = TakeFirstUnusedPasteIndex(
                    fullKeyToIdx, BuildTranslationKey(item.FileName, item.DisplayPath, item.Original), used);
                if (pick < 0)
                    pick = TakeFirstUnusedPasteIndex(
                        pathOrigToIdx, $"{NormalizeKeyPart(item.DisplayPath)}{NormalizeKeyPart(item.Original)}", used);

                if (pick < 0)
                {
                    skipped++;
                    continue;
                }

                used[pick] = true;
                undoFrame.Add(new TranslationUndoCell { Item = item, PreviousTranslated = item.Translated ?? "" });
                var clean = CleanupAiTranslation(tsvRows[pick].Translated);
                item.Translated = clean;
                dgv.Rows[rowIndex].Cells["Translated"].Value = clean;
                applied++;
            }

            if (undoFrame.Count > 0)
                PushTranslationUndoFrame(undoFrame);

            return new PasteTsvIntoSelectionOutcome { AppliedCount = applied, SkippedMismatch = skipped };
        }

        /// <summary>
        /// Вставка ответа ИИ, когда колонки разделены ПРОБЕЛАМИ, а не табами (структурный TSV-парсер их не
        /// берёт, а делить по пробелу нельзя — пробелы есть и в «Оригинале», и в «Переводе»: «Game Over»,
        /// «Lust Madness»). Каждую строку ответа сопоставляем с ИЗВЕСТНОЙ строкой таблицы по префиксу
        /// «Файл Путь Оригинал»; остаток строки — перевод. Шапка и лишние строки ответа не находят совпадения
        /// и пропускаются. Привязка по содержимому, не по позиции — сдвига нет.
        /// </summary>
        private PasteTsvIntoSelectionOutcome TryPasteAiByRowPrefix(string clipboardText, IReadOnlyList<int> targetIndices)
        {
            var outcome = new PasteTsvIntoSelectionOutcome();
            if (string.IsNullOrEmpty(clipboardText) || targetIndices == null || targetIndices.Count == 0)
                return outcome;

            var lines = clipboardText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var used = new bool[lines.Length];
            var undoFrame = new List<TranslationUndoCell>();

            foreach (var rowIndex in targetIndices)
            {
                var item = RowItemAt(rowIndex);
                if (item == null)
                    continue;
                if (string.IsNullOrEmpty(item.Original))
                    continue;

                var lineIdx = -1;
                string translation = null;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (used[i])
                        continue;
                    var line = lines[i].Trim();
                    if (line.Length == 0)
                        continue;

                    var rest = ConsumeOrderedFieldsReturnRest(line, item.FileName, item.DisplayPath, item.Original)
                               ?? ConsumeOrderedFieldsReturnRest(line, item.DisplayPath, item.Original);
                    if (rest == null)
                        continue;

                    lineIdx = i;
                    translation = rest;
                    break;
                }

                if (lineIdx < 0)
                {
                    outcome.SkippedMismatch++;
                    continue;
                }

                used[lineIdx] = true;
                undoFrame.Add(new TranslationUndoCell { Item = item, PreviousTranslated = item.Translated ?? "" });
                var clean = CleanupAiTranslation(translation);
                item.Translated = clean;
                dgv.Rows[rowIndex].Cells["Translated"].Value = clean;
                outcome.AppliedCount++;
            }

            if (undoFrame.Count > 0)
                PushTranslationUndoFrame(undoFrame);

            return outcome;
        }

        /// <summary>
        /// Если строка начинается с перечисленных полей по порядку (между ними — любые пробелы/табы), возвращает
        /// остаток строки (перевод, может быть пустым); иначе null. Поля сравниваются как литералы, поэтому
        /// внутренние пробелы в них допустимы («Game Over»); после каждого поля требуется граница (пробел/конец).
        /// </summary>
        private static string ConsumeOrderedFieldsReturnRest(string line, params string[] fields)
        {
            if (line == null)
                return null;
            var pos = 0;
            foreach (var field in fields)
            {
                while (pos < line.Length && char.IsWhiteSpace(line[pos]))
                    pos++;
                if (string.IsNullOrEmpty(field))
                    continue;
                if (pos + field.Length > line.Length)
                    return null;
                if (string.Compare(line, pos, field, 0, field.Length, StringComparison.Ordinal) != 0)
                    return null;
                pos += field.Length;
                if (pos < line.Length && !char.IsWhiteSpace(line[pos]))
                    return null;
            }

            while (pos < line.Length && char.IsWhiteSpace(line[pos]))
                pos++;
            return line.Substring(pos);
        }

        private static void AddPasteIndex(Dictionary<string, List<int>> map, string key, int idx)
        {
            if (string.IsNullOrEmpty(key))
                return;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>();
                map[key] = list;
            }

            list.Add(idx);
        }

        /// <summary>Первый ещё не использованный TSV-ряд для данного ключа (дубликаты оригинала раздаются по порядку).</summary>
        private static int TakeFirstUnusedPasteIndex(Dictionary<string, List<int>> map, string key, bool[] used)
        {
            if (string.IsNullOrEmpty(key) || !map.TryGetValue(key, out var list))
                return -1;
            foreach (var i in list)
                if (!used[i])
                    return i;
            return -1;
        }

        private List<string> ParseAiTranslations(string text)
        {
            var result = new List<string>();
            var current = new List<string>();
            var fallbackLines = new List<string>();

            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("```"))
                    continue;

                var tabParts = rawLine.Split('\t');
                if (tabParts.Length >= 4)
                {
                    var firstColumn = tabParts[0].Trim();
                    var lastColumn = tabParts[tabParts.Length - 1].Trim();

                    if (firstColumn.Equals("Файл", StringComparison.OrdinalIgnoreCase) ||
                        lastColumn.Equals("Перевод", StringComparison.OrdinalIgnoreCase))
                        continue;

                    AddParsedTranslation(result, current);
                    current.Clear();

                    if (!string.IsNullOrWhiteSpace(lastColumn))
                        result.Add(CleanupAiTranslation(lastColumn));
                    continue;
                }

                var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*\d+[\.\)]\s*(.+)$");
                if (match.Success)
                {
                    AddParsedTranslation(result, current);
                    current.Clear();
                    current.Add(CleanupAiTranslation(match.Groups[1].Value));
                }
                else if (line.StartsWith("Перевод:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Translated:", StringComparison.OrdinalIgnoreCase))
                {
                    AddParsedTranslation(result, current);
                    current.Clear();
                    result.Add(CleanupAiTranslation(line));
                }
                else if (line.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Path:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Original:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Переведи строки", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Сохраняй ", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Стиль:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Если слово", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Короткие названия", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Сохрани ", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Верни ответ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else if (current.Count > 0)
                {
                    current.Add(CleanupAiTranslation(line));
                }
                else
                {
                    fallbackLines.Add(CleanupAiTranslation(line));
                }
            }

            AddParsedTranslation(result, current);
            if (result.Count == 0)
                result.AddRange(fallbackLines.Where(x => !string.IsNullOrWhiteSpace(x)));
            return result;
        }

        private int PasteAiTableByMatch(string text)
        {
            // Очередь хранит ССЫЛКИ на элементы (не индексы): перевод пишем прямо в элемент, а его строку грида
            // находим через Tag (RowIndexOfItem). Так совпадение по ключу и запись не зависят от выравнивания
            // порядка грида и списка.
            var rowsByKey = new Dictionary<string, Queue<TranslationItem>>();
            foreach (var item in translationItems)
            {
                var key = BuildTranslationKey(item.FileName, item.DisplayPath, item.Original);
                if (!rowsByKey.TryGetValue(key, out var queue))
                {
                    queue = new Queue<TranslationItem>();
                    rowsByKey[key] = queue;
                }
                queue.Enqueue(item);
            }

            var updated = 0;
            var undoFrame = new List<TranslationUndoCell>();
            foreach (var logicalLine in EnumerateLogicalAiPasteTsvRows(text))
            {
                if (string.IsNullOrWhiteSpace(logicalLine))
                    continue;

                var parts = logicalLine.Split(new[] { '\t' }, 4, StringSplitOptions.None);
                if (parts.Length < 4)
                    continue;

                var fileName = parts[0].Trim();
                var displayPath = parts[1].Trim();
                var original = parts[2].Trim();
                var translated = parts[3].Trim();

                if (fileName.Equals("Файл", StringComparison.OrdinalIgnoreCase) ||
                    translated.Equals("Перевод", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(translated))
                    continue;

                var key = BuildTranslationKey(fileName, displayPath, original);
                if (!rowsByKey.TryGetValue(key, out var queue) || queue.Count == 0)
                    continue;

                var item = queue.Dequeue();
                undoFrame.Add(new TranslationUndoCell { Item = item, PreviousTranslated = item.Translated ?? "" });
                item.Translated = CleanupAiTranslation(translated);
                int r = RowIndexOfItem(item);
                if (dgv != null && r >= 0)
                    dgv.Rows[r].Cells["Translated"].Value = item.Translated;
                updated++;
            }

            if (undoFrame.Count > 0)
                PushTranslationUndoFrame(undoFrame);

            return updated;
        }

        private string BuildTranslationKey(string fileName, string displayPath, string original)
        {
            return $"{NormalizeKeyPart(fileName)}\u001F{NormalizeKeyPart(displayPath)}\u001F{NormalizeKeyPart(original)}";
        }

        private string NormalizeKeyPart(string value)
        {
            return (value ?? "")
                .Trim()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\\r\\n", "\n")
                .Replace("\\n", "\n")
                .Replace("\\r", "\n");
        }

        private string ToTsvField(string value)
        {
            return (value ?? "")
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n")
                .Replace("\r", "\\n")
                .Replace("\t", " ");
        }

        private void AddParsedTranslation(List<string> result, List<string> lines)
        {
            var value = string.Join(Environment.NewLine, lines)
                .Trim();

            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }

        private string CleanupAiTranslation(string text)
        {
            var value = text.Trim();
            if (value.StartsWith("Перевод:", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("Перевод:".Length).Trim();
            if (value.StartsWith("Translated:", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("Translated:".Length).Trim();
            return value.Trim().Trim('"')
                .Replace("\\r\\n", Environment.NewLine)
                .Replace("\\n", Environment.NewLine)
                .Replace("\\r", Environment.NewLine);
        }
    }
}
