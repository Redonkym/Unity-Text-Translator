using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityTextTranslator
{
    internal enum TranslationTxtFormat
    {
        PipeColumns,
        TabDelimited,
        CsvComma,
        /// <summary>Только значение «Оригинал», одна фраза на строку; импорт перевода построчно по тому же порядку строк, что при экспорте этого формата из приложения.</summary>
        OriginalOnlyLines,
    }

    internal static class TranslationTxtExchange
    {
        /// <summary>Служебная строка: ниже неё — только построчные данные; выше — текст для ИИ и пользователя.</summary>
        internal const string OriginalOnlyDataBeginMarker = "---UTT-DATA-BEGIN---";

        /// <summary>Фильтр «Сохранить»: можно выбрать тип в диалоге или по расширению файла.</summary>
        internal static string CombinedSaveFilter()
        {
            // В тексте описания нельзя использовать символ '|' — WinForms режет Filter только им.
            return
                "Все поддерживаемые (*.txt;*.tsv;*.csv)|*.txt;*.tsv;*.csv|" +
                "TXT с разделителем pipe, как в приложении (*.txt)|*.txt|" +
                "TXT: только «Оригинал», построчно — без файла и пути (*.txt)|*.txt|" +
                "TSV: табуляция (*.txt;*.tsv)|*.txt;*.tsv|" +
                "CSV: запятая, Excel (*.csv)|*.csv|" +
                "Все файлы (*.*)|*.*";
        }

        /// <summary>Фильтр «Открыть» для импорта.</summary>
        internal static string CombinedOpenFilter() => CombinedSaveFilter();

        internal static string DefaultExtensionForFormat(TranslationTxtFormat format)
        {
            switch (format)
            {
                case TranslationTxtFormat.TabDelimited:
                    return "tsv";
                case TranslationTxtFormat.CsvComma:
                    return "csv";
                case TranslationTxtFormat.OriginalOnlyLines:
                    return "txt";
                default:
                    return "txt";
            }
        }

        /// <summary>1-based индекс фильтра для <see cref="FileDialog.FilterIndex"/>.</summary>
        internal static int SaveFilterIndexFromFormat(TranslationTxtFormat format)
        {
            switch (format)
            {
                case TranslationTxtFormat.TabDelimited:
                    return 4;
                case TranslationTxtFormat.CsvComma:
                    return 5;
                case TranslationTxtFormat.OriginalOnlyLines:
                    return 3;
                default:
                    return 2;
            }
        }

        internal static TranslationTxtFormat ResolveFormatAfterDialog(string filePath, int filterIndex)
        {
            switch (filterIndex)
            {
                case 2:
                    return TranslationTxtFormat.PipeColumns;
                case 3:
                    return TranslationTxtFormat.OriginalOnlyLines;
                case 4:
                    return TranslationTxtFormat.TabDelimited;
                case 5:
                    return TranslationTxtFormat.CsvComma;
                case 6:
                    return InferFormatFromExtension(filePath);
                default:
                    return InferFormatFromExtension(filePath);
            }
        }

        internal static TranslationTxtFormat InferFormatFromExtension(string filePath)
        {
            var ext = Path.GetExtension(filePath ?? "");
            if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
                return TranslationTxtFormat.CsvComma;
            if (string.Equals(ext, ".tsv", StringComparison.OrdinalIgnoreCase))
                return TranslationTxtFormat.TabDelimited;
            return TranslationTxtFormat.PipeColumns;
        }

        internal static string FileFilter(TranslationTxtFormat format)
        {
            switch (format)
            {
                case TranslationTxtFormat.TabDelimited:
                    return "TSV (*.txt;*.tsv)|*.txt;*.tsv|Все файлы (*.*)|*.*";
                case TranslationTxtFormat.CsvComma:
                    return "CSV (*.csv)|*.csv|Текст (*.txt)|*.txt|Все файлы (*.*)|*.*";
                case TranslationTxtFormat.OriginalOnlyLines:
                    return "TXT: только оригинал (*.txt)|*.txt|Все файлы (*.*)|*.*";
                default:
                    return "Text (*.txt)|*.txt|Все файлы (*.*)|*.*";
            }
        }

        internal static string SuggestedExportFileName(TranslationTxtFormat format)
        {
            switch (format)
            {
                case TranslationTxtFormat.TabDelimited:
                    return "translations.tsv";
                case TranslationTxtFormat.CsvComma:
                    return "translations.csv";
                case TranslationTxtFormat.OriginalOnlyLines:
                    return "english-lines.txt";
                default:
                    return "translations.txt";
            }
        }

        internal static void WritePreamble(StreamWriter writer, TranslationTxtFormat format)
        {
            switch (format)
            {
                case TranslationTxtFormat.OriginalOnlyLines:
                    // Заголовок для ИИ пишет WriteOriginalOnlyAiPreamble при экспорте из Form1.
                    return;
                case TranslationTxtFormat.TabDelimited:
                    writer.WriteLine("# Формат: Файл<TAB>Путь<TAB>Оригинал<TAB>Перевод");
                    break;
                case TranslationTxtFormat.CsvComma:
                    writer.WriteLine("# CSV: Файл,Путь,Оригинал,Перевод");
                    break;
                default:
                    writer.WriteLine("# Формат: Файл | Путь | Оригинал | Перевод");
                    break;
            }

            writer.WriteLine();
        }

        /// <summary>Инструкции для модели и пользователя + маркер начала данных (построчный «Оригинал»).</summary>
        internal static void WriteOriginalOnlyAiPreamble(StreamWriter writer, string sourceLanguageDisplay, string targetLanguageDisplay)
        {
            if (writer == null)
                return;

            var src = string.IsNullOrWhiteSpace(sourceLanguageDisplay) ? "(source)" : sourceLanguageDisplay.Trim();
            var tgt = string.IsNullOrWhiteSpace(targetLanguageDisplay) ? "(target)" : targetLanguageDisplay.Trim();

            writer.WriteLine("# =============================================================================");
            writer.WriteLine("# Unity Text Translator — построчное задание для перевода");
            writer.WriteLine("# (формат без столбцов «Файл» и «Путь»)");
            writer.WriteLine("# =============================================================================");
            writer.WriteLine("#");
            writer.WriteLine("# Язык текста в блоке данных ниже (источник): " + src);
            writer.WriteLine("# Нужно перевести на язык: " + tgt);
            writer.WriteLine("#");
            writer.WriteLine("# ЗАДАЧА:");
            writer.WriteLine("# После строки-маркера «" + OriginalOnlyDataBeginMarker + "» идут строки из игры:");
            writer.WriteLine("# ровно одна фраза / один элемент — одна строка файла.");
            writer.WriteLine("# Переведите каждую строку на язык «" + tgt + "», сохраняя порядок один-к-одному");
            writer.WriteLine("# (1-я строка блока данных → 1-й перевод, 2-я → 2-й, …).");
            writer.WriteLine("# Не склеивайте и не разбивайте строки, не нумеруйте, не добавляйте и не удаляйте строки:");
            writer.WriteLine("# число строк данных должно остаться тем же.");
            writer.WriteLine("# Пустая строка в данных означает пустой перевод (оставьте строку пустой).");
            writer.WriteLine("# Сохраняйте плейсхолдеры ({0}, %d и т.п.), теги Unity/TextMeshPro (<color>, <size>, …).");
            writer.WriteLine("# Конструкции вида <?shake> … ?> и любые <? … ?> — буквальный игровой код, не HTML:");
            writer.WriteLine("# не склеивайте строки файла в одну из‑за угловых скобок; не переписывайте разметку;");
            writer.WriteLine("# внутри одной строки данных сохраняйте переносы и пробелы как в источнике.");
            writer.WriteLine("#");
            writer.WriteLine("# КАК ОТДАТЬ РЕЗУЛЬТАТ В ПРИЛОЖЕНИЕ:");
            writer.WriteLine("# Предпочтительно: верните весь файл целиком, заменив ТОЛЬКО строки под маркером");
            writer.WriteLine("# «" + OriginalOnlyDataBeginMarker + "» на переводы; текст и маркер выше не меняйте.");
            writer.WriteLine("# Допустимо: только блок переведённых строк подряд того же размера (если маркера в ответе нет —");
            writer.WriteLine("# приложение воспринимает файл целиком как данные, как в старых версиях).");
            writer.WriteLine("#");
            writer.WriteLine("# ---");
            writer.WriteLine("# [English for model:] Below the marker line \"" + OriginalOnlyDataBeginMarker + "\" are in-game source strings (" + src + "), exactly one string per line. Translate each line into " + tgt + " preserving the same line order and count: do not merge, split, reorder, insert, or omit lines. Treat <?…?> (e.g. <?shake>) as literal markup/code, not HTML—never collapse multiple file lines into one because of angle brackets; keep inner line breaks/spacing. Keep placeholders and TMP-like tags. Preferred: return the full file with only the post-marker lines replaced by translations.");
            writer.WriteLine("# =============================================================================");
            writer.WriteLine(OriginalOnlyDataBeginMarker);
        }

        /// <summary>Строки для позиционного импорта: есть маркер данных — берём всё ниже него, иначе весь файл (кроме BOM).</summary>
        internal static List<string> ExtractOriginalOnlyPayloadLines(string[] rawLines)
        {
            var all = NormalizeUtf8LinesStripBom(rawLines);
            for (var i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Trim(), OriginalOnlyDataBeginMarker, StringComparison.Ordinal))
                    return all.Skip(i + 1).ToList();
            }

            return all;
        }

        /// <summary>Снимает BOM только с первой строки; порядок строк сохраняется.</summary>
        internal static List<string> NormalizeUtf8LinesStripBom(string[] rawLines)
        {
            var list = new List<string>();
            if (rawLines == null || rawLines.Length == 0)
                return list;

            for (var i = 0; i < rawLines.Length; i++)
            {
                var s = rawLines[i] ?? "";
                if (i == 0 && s.Length > 0 && s[0] == '\uFEFF')
                    s = s.Substring(1);
                list.Add(s);
            }

            return list;
        }

        internal static void WriteRow(StreamWriter writer, TranslationTxtFormat format, string fileName, string displayPath, string original, string translated)
        {
            switch (format)
            {
                case TranslationTxtFormat.TabDelimited:
                    writer.WriteLine(string.Join("\t",
                        EscapeTabs(fileName),
                        EscapeTabs(displayPath),
                        EscapeTabs(original),
                        EscapeTabs(translated)));
                    break;

                case TranslationTxtFormat.CsvComma:
                    writer.WriteLine(string.Join(",",
                        CsvEscape(fileName),
                        CsvEscape(displayPath),
                        CsvEscape(original),
                        CsvEscape(translated)));
                    break;

                case TranslationTxtFormat.OriginalOnlyLines:
                    writer.WriteLine(FlattenOriginalForPlainExport(original));
                    break;

                case TranslationTxtFormat.PipeColumns:
                    writer.WriteLine(string.Join(" | ",
                        EscapePipeCell(fileName),
                        EscapePipeCell(displayPath),
                        EscapePipeCell(original),
                        EscapePipeCell(translated)));
                    break;

                default:
                    goto case TranslationTxtFormat.PipeColumns;
            }
        }

        internal static bool TryParseRow(string line, TranslationTxtFormat format, out string fileName, out string displayPath, out string original, out string translated)
        {
            fileName = displayPath = original = translated = null;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            switch (format)
            {
                case TranslationTxtFormat.TabDelimited:
                    {
                        var parts = line.Split(new[] { '\t' }, 4, StringSplitOptions.None);
                        if (parts.Length < 4)
                            return false;
                        fileName = UnescapeTabs(parts[0].Trim());
                        displayPath = UnescapeTabs(parts[1].Trim());
                        original = UnescapeTabs(parts[2].Trim());
                        translated = UnescapeTabs(parts[3].Trim());
                        return true;
                    }

                case TranslationTxtFormat.CsvComma:
                    {
                        var parts = ParseCsvLine(line);
                        if (parts.Count < 4)
                            return false;
                        fileName = parts[0].Trim();
                        displayPath = parts[1].Trim();
                        original = parts[2].Trim();
                        translated = parts.Count > 4 ? string.Join(",", parts.Skip(3)).Trim() : parts[3].Trim();
                        return true;
                    }

                case TranslationTxtFormat.OriginalOnlyLines:
                    return false;

                case TranslationTxtFormat.PipeColumns:
                    {
                        var parts = line.Split(new[] { '|' }, 4, StringSplitOptions.None);
                        if (parts.Length < 4)
                            return false;
                        fileName = UnescapePipe(parts[0].Trim());
                        displayPath = UnescapePipe(parts[1].Trim());
                        original = UnescapePipe(parts[2].Trim());
                        translated = UnescapePipe(parts[3].Trim());
                        return true;
                    }

                default:
                    goto case TranslationTxtFormat.PipeColumns;
            }
        }

        /// <summary>Одна строка файла — одна запись; переносы внутри текста сворачиваются в пробелы.</summary>
        private static string FlattenOriginalForPlainExport(string original)
        {
            var s = original ?? "";
            if (s.Length == 0)
                return "";
            var normalized = s.Replace("\r\n", "\n").Replace('\r', '\n');
            var parts = normalized.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts).Trim();
        }

        private static string EscapeTabs(string s) => (s ?? "").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

        private static string UnescapeTabs(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s ?? "";
            return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static string EscapePipeCell(string s)
        {
            return (s ?? "")
                .Replace("\t", "\\t")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("|", "\\|");
        }

        private static string UnescapePipe(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s ?? "";
            return s
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\|", "|");
        }

        /// <summary>Физические строки CSV (учёт кавычек и переносов внутри поля), для импорта после <see cref="File.ReadAllText"/>.</summary>
        internal static IEnumerable<string> EnumerateCsvPhysicalRecordLines(string csvText)
        {
            if (string.IsNullOrEmpty(csvText))
                yield break;

            var normalized = csvText.Replace("\r\n", "\n").Replace('\r', '\n');
            var sb = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < normalized.Length && normalized[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    sb.Append(c);
                    continue;
                }

                if (c == '\n' && !inQuotes)
                {
                    yield return sb.ToString();
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
                yield return sb.ToString();
        }

        private static string CsvEscape(string field)
        {
            var s = field ?? "";
            if (s.IndexOfAny(new[] { '"', ',', '\r', '\n' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == ',')
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            result.Add(sb.ToString());
            return result;
        }
    }
}
