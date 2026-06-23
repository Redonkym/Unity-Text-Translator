using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityTextTranslator
{
    /// <summary>Варианты раскладки файлов при экспорте/импорте UABEA-совместимого JSON.</summary>
    internal enum UabeaJsonFileLayout
    {
        /// <summary>Как UABEAvalonia: ИмяMonoScript-контейнер-PathID.json (например Button-level0-2291).</summary>
        UabeaMonoScriptNameFlat = 0,

        /// <summary>Папка по имени контейнера: resources/35932.json.</summary>
        SubfolderPathIdOnly = 1,

        /// <summary>Плоско: resources-35932.json</summary>
        FlatDashPathId = 2,

        /// <summary>Плоско с type id: resources-t114-35932.json</summary>
        FlatTypeDashPathId = 3,
    }

    internal static class UabeaJsonPaths
    {
        /// <summary>Экспорт <c>$"{assetsBase}-{info.PathId}"</c> при отрицательном PathID даёт два тиря подряд перед цифрами — здесь восстанавливаем знак.</summary>
        private static readonly Regex DoubleHyphenNegativePathIdSuffix =
            new Regex(@"\-\-(\d+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex PathIdSuffix = new Regex(@"(?:^|[-_ ])(\d+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        /// <summary>Плоский вид с type id: <c>имя-t{typeId}-{pathId}.json</c> — последняя группа может быть со знаком «-» (как после экспорта с отрицательным PathID).</summary>
        private static readonly Regex TypedPathIdSuffix = new Regex(@"-t(\d+)-(-?\d+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static string SafeFileNamePart(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "asset";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.Trim())
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            var s = sb.ToString();
            return string.IsNullOrEmpty(s) ? "asset" : s;
        }

        internal static string GetExportJsonFullPath(
            UabeaJsonFileLayout layout,
            string outputFolder,
            string assetsBase,
            AssetFileInfo info,
            AssetsFileInstance fileInst,
            string monoScriptShortName = null)
        {
            assetsBase = (assetsBase ?? string.Empty).TrimEnd('-', '_', ' ');
            var typeId = info.GetTypeId(fileInst.file);
            switch (layout)
            {
                case UabeaJsonFileLayout.UabeaMonoScriptNameFlat:
                    if (!string.IsNullOrWhiteSpace(monoScriptShortName))
                    {
                        var mono = SafeFileNamePart(monoScriptShortName).TrimEnd('-', '_', ' ');
                        return Path.Combine(outputFolder,
                            $"{mono}-{assetsBase}-{info.PathId}.json");
                    }
                    return Path.Combine(outputFolder, $"{assetsBase}-{info.PathId}.json");

                case UabeaJsonFileLayout.SubfolderPathIdOnly:
                    var dir = Path.Combine(outputFolder, assetsBase);
                    return Path.Combine(dir, $"{info.PathId}.json");

                case UabeaJsonFileLayout.FlatTypeDashPathId:
                    return Path.Combine(outputFolder, $"{assetsBase}-t{typeId}-{info.PathId}.json");

                default:
                    return Path.Combine(outputFolder, $"{assetsBase}-{info.PathId}.json");
            }
        }

        /// <summary>PathID из имени файла (последняя группа цифр после разделителя или целиком имя «123»).</summary>
        internal static bool TryParsePathIdFromFilePath(string filePath, out long pathId)
        {
            pathId = 0;
            var name = Path.GetFileNameWithoutExtension(filePath);

            var typed = TypedPathIdSuffix.Match(name);
            if (typed.Success &&
                long.TryParse(typed.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out pathId))
                return true;

            var dblMinus = DoubleHyphenNegativePathIdSuffix.Match(name);
            if (dblMinus.Success)
                return long.TryParse("-" + dblMinus.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out pathId);

            var match = PathIdSuffix.Match(name);
            if (!match.Success)
                return false;

            return long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pathId);
        }

        /// <summary>Ищет JSON для .assets рекурсивно: «basename-*.json» на любой глубине или файл под папкой с именем контейнера.</summary>
        internal static List<(string Path, long PathId)> DiscoverImportJsonFiles(string jsonFolder, string assetsPath)
        {
            var baseSafe = SafeFileNamePart(Path.GetFileNameWithoutExtension(assetsPath)).TrimEnd('-', '_', ' ');
            return DiscoverImportJsonFilesForContainer(jsonFolder, baseSafe);
        }

        /// <summary>Сопоставление JSON с контейнером по безопасному имени (имя .assets или имя CAB внутри .bundle).</summary>
        internal static List<(string Path, long PathId)> DiscoverImportJsonFilesForContainer(string jsonFolder, string containerBaseSafe, AssetsFileInstance restrictPathIdsToThisFile = null)
        {
            var baseSafe = SafeFileNamePart(containerBaseSafe).TrimEnd('-', '_', ' ');
            var byPathId = new Dictionary<long, string>();

            HashSet<long> pathIdsInContainer = null;
            if (restrictPathIdsToThisFile?.file?.AssetInfos != null)
            {
                pathIdsInContainer = new HashSet<long>();
                foreach (var info in restrictPathIdsToThisFile.file.AssetInfos)
                {
                    if (info.Stripped != 0)
                        continue;
                    pathIdsInContainer.Add(info.PathId);
                }
            }

            void TryAdd(string path)
            {
                if (!TryParsePathIdFromFilePath(path, out var pid))
                    return;
                if (pathIdsInContainer != null)
                {
                    if (!pathIdsInContainer.Contains(pid) && pathIdsInContainer.Contains(-pid))
                        pid = -pid;
                    if (!pathIdsInContainer.Contains(pid))
                        return;
                }

                if (!byPathId.TryGetValue(pid, out var oldPath))
                {
                    byPathId[pid] = path;
                    return;
                }

                // Если для одного PathID несколько JSON (частая ситуация при повторах экспорта),
                // берём самый свежий файл, чтобы правки пользователя не терялись.
                DateTime oldWrite;
                DateTime newWrite;
                try { oldWrite = File.GetLastWriteTimeUtc(oldPath); } catch { oldWrite = DateTime.MinValue; }
                try { newWrite = File.GetLastWriteTimeUtc(path); } catch { newWrite = DateTime.MinValue; }
                if (newWrite > oldWrite)
                    byPathId[pid] = path;
            }

            if (string.IsNullOrWhiteSpace(jsonFolder) || !Directory.Exists(jsonFolder))
                return new List<(string Path, long PathId)>();

            jsonFolder = Path.GetFullPath(jsonFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var path in Directory.GetFiles(jsonFolder, "*.json", SearchOption.AllDirectories))
            {
                if (!JsonFileBelongsToContainer(jsonFolder, path, baseSafe))
                    continue;

                TryAdd(path);
            }

            // JSON с другого CAB (например экспорт english, целевой russian): PathID есть в этом .assets.
            if (byPathId.Count == 0 && restrictPathIdsToThisFile != null && restrictPathIdsToThisFile.file != null)
                return DiscoverImportJsonForPathIdsInFolder(jsonFolder, restrictPathIdsToThisFile.file);

            // Дампы вида «2664.json» — только если префикс и PathID не дали файлов (порядок важен: иначе мешают кросс-CAB).
            if (byPathId.Count == 0)
            {
                foreach (var path in Directory.GetFiles(jsonFolder, "*.json", SearchOption.AllDirectories))
                {
                    var fn = Path.GetFileNameWithoutExtension(path);
                    if (fn.Length == 0 || fn.Any(ch => !char.IsDigit(ch)))
                        continue;

                    TryAdd(path);
                }
            }

            var ordered = new List<(string Path, long PathId)>(byPathId.Count);
            foreach (var kv in byPathId.OrderBy(k => k.Key))
                ordered.Add((kv.Value, kv.Key));

            return ordered;
        }

        /// <summary>Сопоставляет файлы «*-PathID.json» из папки с PathID, реально присутствующими в указанном .assets, без проверки префикса CAB.</summary>
        internal static List<(string Path, long PathId)> DiscoverImportJsonForPathIdsInFolder(string jsonFolder, AssetsFile assets)
        {
            var ordered = new List<(string Path, long PathId)>();
            if (string.IsNullOrWhiteSpace(jsonFolder) || !Directory.Exists(jsonFolder) || assets?.AssetInfos == null)
                return ordered;

            var allowed = new HashSet<long>();
            foreach (var info in assets.AssetInfos)
            {
                if (info.Stripped != 0)
                    continue;
                allowed.Add(info.PathId);
            }

            if (allowed.Count == 0)
                return ordered;

            jsonFolder = Path.GetFullPath(jsonFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var byPathId = new Dictionary<long, string>();

            foreach (var path in Directory.GetFiles(jsonFolder, "*.json", SearchOption.AllDirectories))
            {
                if (!TryParsePathIdFromFilePath(path, out var pid))
                    continue;
                if (!allowed.Contains(pid) && allowed.Contains(-pid))
                    pid = -pid;
                if (!allowed.Contains(pid))
                    continue;
                if (!byPathId.TryGetValue(pid, out var oldPath))
                {
                    byPathId[pid] = path;
                    continue;
                }

                DateTime oldWrite;
                DateTime newWrite;
                try { oldWrite = File.GetLastWriteTimeUtc(oldPath); } catch { oldWrite = DateTime.MinValue; }
                try { newWrite = File.GetLastWriteTimeUtc(path); } catch { newWrite = DateTime.MinValue; }
                if (newWrite > oldWrite)
                    byPathId[pid] = path;
            }

            foreach (var kv in byPathId.OrderBy(k => k.Key))
                ordered.Add((kv.Value, kv.Key));

            return ordered;
        }

        /// <summary>Совпадение с дампами UABEA: «container-PathID», «ScriptName-container-PathID» или подпапка container.</summary>
        internal static bool JsonFileBelongsToContainer(string jsonRoot, string jsonFilePath, string containerBaseSafe)
        {
            var fn = Path.GetFileNameWithoutExtension(jsonFilePath);

            if (fn.StartsWith(containerBaseSafe + "-", StringComparison.OrdinalIgnoreCase))
                return true;

            var escaped = Regex.Escape(containerBaseSafe);
            if (Regex.IsMatch(fn, $"-{escaped}--?\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            // UABEAvalonia: «Тип-CAB-{hash}--PathID.json» (перед hash идёт литерал CAB, не только дефис).
            if (fn.IndexOf("-CAB-" + containerBaseSafe, StringComparison.OrdinalIgnoreCase) >= 0
                || fn.IndexOf("_CAB_" + containerBaseSafe, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return IsUnderNamedAssetsFolder(jsonRoot, jsonFilePath, containerBaseSafe);
        }

        /// <summary>На пути от файла к корню JSON-папки есть каталог с именем контейнера (...\sharedassets18\2664.json).</summary>
        private static bool IsUnderNamedAssetsFolder(string jsonRoot, string jsonFilePath, string baseSafe)
        {
            try
            {
                jsonRoot = Path.GetFullPath(jsonRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dir = Path.GetDirectoryName(Path.GetFullPath(jsonFilePath));

                while (!string.IsNullOrEmpty(dir))
                {
                    if (dir.Equals(jsonRoot, StringComparison.OrdinalIgnoreCase))
                        break;

                    var seg = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (seg.Equals(baseSafe, StringComparison.OrdinalIgnoreCase))
                        return true;

                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }

            return false;
        }
    }
}
