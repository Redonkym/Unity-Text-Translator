using AssetsTools.NET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityTextTranslator
{
    /// <summary>Пары SerializedFile ↔ .resS/.resource(.split*) на диске; после импорта JSON копируем ресурсы к имени сохранённого контейнера.</summary>
    internal static class UnitySerializedFileSidecars
    {
        private static readonly Regex SplitSuffixRegex =
            new Regex(@"\.split\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Externals стриминговой сцены ссылаются на записанное имя <c>levelN.translated.assets</c>; после переименования
        /// в <c>levelN</c>+<c>levelN.resS</c> смещения в заголовке не сходятся → <c>corrupted / Position out of bounds</c>.
        /// </summary>
        internal static int TryRetargetStreamingExternalsToCanonicalStem(
            AssetsFile assetsFile,
            string writtenFileLeafName,
            string canonicalStemNoExtension,
            ICollection<string> messages)
        {
            if (assetsFile?.Metadata?.Externals == null)
                return 0;
            if (string.IsNullOrWhiteSpace(writtenFileLeafName) || string.IsNullOrWhiteSpace(canonicalStemNoExtension))
                return 0;
            if (string.Equals(writtenFileLeafName, canonicalStemNoExtension, StringComparison.OrdinalIgnoreCase))
                return 0;

            var changed = 0;
            foreach (var ext in assetsFile.Metadata.Externals)
            {
                if (ext == null)
                    continue;
                if (RetargetExternalPathField(ext, writtenFileLeafName, canonicalStemNoExtension, isVirtual: false))
                    changed++;
                if (RetargetExternalPathField(ext, writtenFileLeafName, canonicalStemNoExtension, isVirtual: true))
                    changed++;
            }

            if (changed > 0 && messages != null && messages.Count < 220)
            {
                messages.Add(
                    "[Sidecar] В Externals путей к парному .resS заменено полей: " + changed +
                    ". Целевое имя как в билде: «" + canonicalStemNoExtension +
                    "» (переименуйте контейнер и .resS в эти имена).");
            }

            return changed;
        }

        private static bool RetargetExternalPathField(AssetsFileExternal ext, string writtenLeaf, string canonStem,
            bool isVirtual)
        {
            var field = isVirtual ? ext.VirtualAssetPathName : ext.PathName;
            if (string.IsNullOrEmpty(field))
                return false;
            var next = ReplaceStreamingExternalLeafInPath(field, writtenLeaf, canonStem);
            if (string.Equals(next, field, StringComparison.Ordinal))
                return false;
            if (isVirtual)
                ext.VirtualAssetPathName = next;
            else
                ext.PathName = next;
            return true;
        }

        /// <summary>Сначала <c>.resS</c>, затем базовое имя, без учёта регистра.</summary>
        private static string ReplaceStreamingExternalLeafInPath(string path, string writtenLeaf, string canonStem)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            var s = ReplaceOrdinalInsensitive(path, writtenLeaf + ".resS", canonStem + ".resS");
            s = ReplaceOrdinalInsensitive(s, writtenLeaf, canonStem);
            return s;
        }

        private static string ReplaceOrdinalInsensitive(string input, string from, string to)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(from))
                return input;
            var pos = 0;
            var result = input;
            while (true)
            {
                var idx = result.IndexOf(from, pos, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    break;
                result = result.Substring(0, idx) + to + result.Substring(idx + from.Length);
                pos = idx + to.Length;
            }

            return result;
        }

        internal static HashSet<string> CollectCompanionSourcesOnDisk(string sourceMainPath, AssetsFile serializedFileOrNull)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(sourceMainPath))
                return set;

            string srcMain;
            try
            {
                srcMain = Path.GetFullPath(sourceMainPath);
            }
            catch
            {
                return set;
            }

            var dir = Path.GetDirectoryName(srcMain);
            var stem = Path.GetFileName(srcMain);

            // Точные пары имени (никаких «stem*.resS»: в Windows это ловит level10.resS для level1.assets).
            AddIfExists(set, srcMain + ".resS");
            if (!string.IsNullOrEmpty(dir))
                AddIfExists(set, Path.Combine(dir, stem + ".resource"));

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                EnumerateGlobInto(dir, stem + ".split*", set, max: 96);
            }

            if (serializedFileOrNull?.Metadata?.Externals != null)
            {
                try
                {
                    foreach (AssetsFileExternal ext in serializedFileOrNull.Metadata.Externals)
                    {
                        foreach (var token in CandidateExternalTokens(ext))
                        {
                            var leaf = Path.GetFileName(token);
                            if (string.IsNullOrEmpty(leaf) || !LooksLikeStreamCompanion(leaf))
                                continue;
                            if (!IsCompanionNameForStem(leaf, stem))
                                continue;
                            foreach (var p in ResolveUnderDirectory(dir, token))
                                AddIfExists(set, p);
                        }
                    }
                }
                catch
                {
                    // старый/экзотический формат Externals — остаёмся только на поиск по диску
                }
            }

            return set;
        }

        private static void AddIfExists(HashSet<string> set, string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    set.Add(Path.GetFullPath(path));
            }
            catch { }
        }

        private static void EnumerateGlobInto(string directory, string pattern, HashSet<string> set, int max)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(pattern) || !Directory.Exists(directory))
                return;

            var n = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    AddIfExists(set, f);
                    if (++n >= max)
                        break;
                }
            }
            catch { }
        }

        private static IEnumerable<string> CandidateExternalTokens(AssetsFileExternal ext)
        {
            if (ext == null)
                yield break;

            yield return NormalizeExternalLeaf(ext.OriginalPathName);
            yield return NormalizeExternalLeaf(ext.PathName);
        }

        private static string NormalizeExternalLeaf(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var s = raw.Trim().Replace('\\', '/');
            if (s.StartsWith("archive:/", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("archive:", StringComparison.OrdinalIgnoreCase))
            {
                var li = s.LastIndexOf('/') >= 0 ? s.LastIndexOf('/') : -1;
                s = li >= 0 ? s.Substring(li + 1) : s;
            }

            var j = s.LastIndexOf('/') >= 0 ? s.LastIndexOf('/') : -1;
            if (j >= 0)
                s = s.Substring(j + 1);
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private static bool LooksLikeStreamCompanion(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                return true;
            return SplitSuffixRegex.IsMatch(name);
        }

        /// <summary>Отсекает чужие CAB/*.resS из соседней папки, не относящиеся к имени основного контейнера.</summary>
        private static bool IsCompanionNameForStem(string fileName, string stem)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(stem))
                return false;
            if (string.Equals(fileName, stem + ".resS", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(fileName, stem + ".resource", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
            {
                if (fileName.EndsWith(".resS", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (fileName.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                    return true;
                return SplitSuffixRegex.IsMatch(fileName);
            }

            return false;
        }

        private static IEnumerable<string> ResolveUnderDirectory(string containerDir, string leafOrPath)
        {
            if (string.IsNullOrEmpty(containerDir) || string.IsNullOrWhiteSpace(leafOrPath))
                yield break;

            yield return Path.Combine(containerDir, leafOrPath);
        }

        /// <summary>Временный контейнер рядом с целью: AssetsTools.NET при записи дочитывает байты из открытого исходника — поверх того же пути нельзя (corrupted в игре).</summary>
        internal static string GetStagedWritePathForInPlaceImport(string finalMainPath)
        {
            if (string.IsNullOrWhiteSpace(finalMainPath))
                return null;
            try
            {
                var dir = Path.GetDirectoryName(finalMainPath);
                var stem = Path.GetFileName(finalMainPath.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem))
                    return finalMainPath + "_utt_import_tmp";
                return Path.Combine(dir, stem + "_utt_import_tmp");
            }
            catch
            {
                return finalMainPath + "_utt_import_tmp";
            }
        }

        /// <summary>После записи во временный файл и <see cref="CopyCompanionsToOutput"/> заменяет оригинальный контейнер и .resS/.split*.</summary>
        internal static bool TryCommitStagedContainerInPlace(string stagedMainPath, string finalMainPath,
            ICollection<string> messages)
        {
            if (string.IsNullOrWhiteSpace(stagedMainPath) || string.IsNullOrWhiteSpace(finalMainPath))
                return false;

            string staged;
            string final;
            try
            {
                staged = Path.GetFullPath(stagedMainPath);
                final = Path.GetFullPath(finalMainPath);
            }
            catch (Exception ex)
            {
                messages?.Add("[Sidecar] Некорректные пути при подмене файла: " + ex.Message);
                return false;
            }

            if (string.Equals(staged, final, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!File.Exists(staged))
            {
                messages?.Add("[Sidecar] Временный файл «" + Path.GetFileName(staged) + "» не найден — подмена отменена.");
                return false;
            }

            if (!File.Exists(final))
            {
                messages?.Add("[Sidecar] Целевой файл «" + Path.GetFileName(final) + "» не найден — подмена отменена.");
                return false;
            }

            var dir = Path.GetDirectoryName(staged);
            var stagedStem = Path.GetFileName(staged);
            var finalStem = Path.GetFileName(final);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stagedStem) || string.IsNullOrEmpty(finalStem))
            {
                messages?.Add("[Sidecar] Не удалось разобрать имена для подмены staged/final.");
                return false;
            }

            if (!TryReplaceInPlace(staged, final, dir, messages))
            {
                messages?.Add("[Sidecar] Подмена основного файла не удалась. " +
                              "Закройте игру/антивирус и проверьте права на «" + final + "».");
                return false;
            }

            messages?.Add("[Sidecar] Подменён основной контейнер «" + finalStem + "» (безопасная запись через временный файл).");

            var n = 0;
            try
            {
                foreach (var src in Directory.EnumerateFiles(dir, stagedStem + ".*", SearchOption.TopDirectoryOnly))
                {
                    var fn = Path.GetFileName(src);
                    if (string.IsNullOrEmpty(fn) || fn.Length <= stagedStem.Length)
                        continue;
                    if (!fn.StartsWith(stagedStem + ".", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dest = Path.Combine(dir, finalStem + fn.Substring(stagedStem.Length));
                    if (!TryReplaceInPlace(src, dest, dir, messages))
                    {
                        messages?.Add("[Sidecar] Не удалось подменить «" + fn + "».");
                        return false;
                    }

                    n++;
                    messages?.Add("[Sidecar] Подменён «" + Path.GetFileName(dest) + "».");
                }
            }
            catch (Exception ex)
            {
                messages?.Add("[Sidecar] Ошибка перечисления побочников: " + ex.Message);
                return false;
            }

            if (n > 0)
                messages?.Add("[Sidecar] Подменено файлов ресурсов (.resS и т.д.): " + n + ".");

            return true;
        }

        /// <summary>
        /// Перемещает <paramref name="src"/> поверх <paramref name="dest"/>. Бэкап для File.Replace держим в папке цели:
        /// он требует бэкап на ТОМ ЖЕ томе (иначе «Unable to remove the file to be replaced», когда игра не на %TEMP%-диске).
        /// При сбое — delete+move.
        /// </summary>
        private static bool TryReplaceInPlace(string src, string dest, string dir, ICollection<string> messages)
        {
            if (!File.Exists(dest))
            {
                try
                {
                    File.Move(src, dest);
                    return true;
                }
                catch (Exception ex)
                {
                    messages?.Add("[Sidecar] Перемещение «" + Path.GetFileName(dest) + "» не удалось: " + ex.Message);
                    return false;
                }
            }

            var backup = Path.Combine(dir, finalBackupName(dest));
            try
            {
                File.Replace(src, dest, backup, ignoreMetadataErrors: true);
                TryDelete(backup);
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(backup);
                messages?.Add("[Sidecar] File.Replace для «" + Path.GetFileName(dest) + "» не удался (" +
                              ex.Message + "), пробую удалить+переместить.");
                try
                {
                    File.Delete(dest);
                    File.Move(src, dest);
                    return true;
                }
                catch (Exception ex2)
                {
                    messages?.Add("[Sidecar] Резервная подмена «" + Path.GetFileName(dest) + "» тоже не удалась: " + ex2.Message);
                    return false;
                }
            }
        }

        private static string finalBackupName(string dest)
        {
            return Path.GetFileName(dest) + ".utt_replace_" + Guid.NewGuid().ToString("N") + ".bak";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        public static int CopyCompanionsToOutput(string sourceSerializedContainerPath, string outputSerializedContainerPath,
            AssetsFile serializedFileFromImportOrNull,
            ICollection<string> messages)
        {
            if (string.IsNullOrWhiteSpace(sourceSerializedContainerPath) ||
                string.IsNullOrWhiteSpace(outputSerializedContainerPath))
                return 0;

            string srcMain;
            string dstMain;
            try
            {
                srcMain = Path.GetFullPath(sourceSerializedContainerPath);
                dstMain = Path.GetFullPath(outputSerializedContainerPath);
            }
            catch
            {
                return 0;
            }

            if (string.Equals(srcMain, dstMain, StringComparison.OrdinalIgnoreCase))
            {
                messages?.Add(
                    "[Sidecar] Сохранение по тому же пути, что и исходник — существующие потоки (.resS, .split*) " +
                    "не копируются (остаются рядом как есть). Если игра пишет corrupted/Position out of bounds, " +
                    "частая причина — перезапись основного файла инструментом при несовместимом с содержимым .resS " +
                    "(откатите оригинальную пару levelN + levelN.resS из бэкапа).");
                return 0;
            }

            var outDir = Path.GetDirectoryName(dstMain);
            if (string.IsNullOrEmpty(outDir))
                return 0;

            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                messages?.Add("[Sidecar] Каталог выхода недоступен («" + outDir + "»): " + ex.Message);
                return 0;
            }

            var stemSrc = Path.GetFileName(srcMain);
            var stemDst = Path.GetFileName(dstMain);
            if (!string.Equals(stemSrc, stemDst, StringComparison.OrdinalIgnoreCase))
            {
                var sceneLike = stemSrc.IndexOf('.') < 0 || stemDst.IndexOf('.') < 0;
                messages?.Add(sceneLike
                    ? "[Sidecar] Базовое имя выходного файла не совпадает с исходным. Для сцен вида level0/level1 " +
                      "Unity грузит пару «ИМЯ» + «ИМЯ.resS»: при подмене в билде оба файла должны иметь одно и то же " +
                      "базовое имя, что ожидает игра (иначе сообщение corrupted / Position out of bounds в Player.log)."
                    : "[Sidecar] Базовое имя выходного файла не совпадает с исходным — переименуйте и основной контейнер, " +
                      "и все скопированные .resS так, чтобы они соответствовали друг другу (как у оригинала).");
            }

            var sources = CollectCompanionSourcesOnDisk(srcMain, serializedFileFromImportOrNull);

            string preferredResS = null;
            try
            {
                preferredResS = Path.GetFullPath(srcMain + ".resS");
            }
            catch { }

            var planned = new List<(string src, string dst)>();
            foreach (var companionPath in sources)
            {
                var dstCompanion = MapDestinationCompanion(companionPath, stemSrc, stemDst, outDir);
                if (string.IsNullOrEmpty(dstCompanion))
                {
                    messages?.Add("[Sidecar] Пропуск «" + Path.GetFileName(companionPath) +
                                  "»: не удалось сопоставить с префиксом «" + stemSrc + "».");
                    continue;
                }

                try
                {
                    if (string.Equals(Path.GetFullPath(companionPath), Path.GetFullPath(dstCompanion),
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch
                {
                    continue;
                }

                planned.Add((companionPath, dstCompanion));
            }

            // Несколько исходников не должны перетирать один и тот же выход (после старых glob это было часто).
            var byDst = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (src, dst) in planned)
            {
                if (!byDst.TryGetValue(dst, out var list))
                {
                    list = new List<string>();
                    byDst[dst] = list;
                }

                list.Add(src);
            }

            var count = 0;
            foreach (var kv in byDst)
            {
                var dstCompanion = kv.Key;
                var candidates = kv.Value;
                string pick;
                if (candidates.Count == 1)
                {
                    pick = candidates[0];
                }
                else
                {
                    pick = null;
                    if (!string.IsNullOrEmpty(preferredResS))
                    {
                        foreach (var c in candidates)
                        {
                            try
                            {
                                if (string.Equals(Path.GetFullPath(c), preferredResS,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    pick = c;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    if (pick == null)
                        pick = candidates[0];
                    messages?.Add(
                        "[Sidecar] Несколько файлов дали одно имя «" + Path.GetFileName(dstCompanion) + "»; " +
                        "используется «" + Path.GetFileName(pick) + "». Проверьте вручную: " +
                        string.Join(", ", candidates.ConvertAll(Path.GetFileName)));
                }

                try
                {
                    File.Copy(pick, dstCompanion, overwrite: true);
                    count++;
                    messages?.Add("[Sidecar] Скопирован «" + Path.GetFileName(pick) + "» → «" +
                                  Path.GetFileName(dstCompanion) + "» (папка: «" + outDir + "»).");
                }
                catch (Exception ex)
                {
                    messages?.Add("[Sidecar] Ошибка копирования «" + Path.GetFileName(pick) + "»: " + ex.Message);
                }
            }

            if (count == 0)
            {
                messages?.Add(
                    "[Sidecar] Автоматически не найдено парных ресурсов. Они добавляются после «Импорт JSON», " +
                    "в каталог, куда вы сохраните файл сборки («" + outDir + "»), а не рядом с Player.log. " +
                    "Ожидалось что-то вроде «" + stemSrc + ".resS» в той же папке, что и «" + stemSrc +
                    "». Убедитесь, что вы собрали свежую версию Unity Text Translator после обновления.");
                MaybeWarnOtherResS(messages, Path.GetDirectoryName(srcMain));
            }

            return count;
        }

        private static string MapDestinationCompanion(string companionAbs, string stemSrc, string stemDst,
            string outDir)
        {
            var fn = Path.GetFileName(companionAbs);
            if (string.IsNullOrEmpty(fn) || string.IsNullOrEmpty(stemSrc) || stemSrc.Length > fn.Length)
                return null;

            if (fn.StartsWith(stemSrc + ".", StringComparison.OrdinalIgnoreCase) ||
                (SplitSuffixRegex.IsMatch(fn) && fn.StartsWith(stemSrc, StringComparison.OrdinalIgnoreCase) &&
                 fn.Length > stemSrc.Length && fn[stemSrc.Length] == '.'))
                return Path.Combine(outDir, stemDst + fn.Substring(stemSrc.Length));

            var idx = fn.IndexOf(stemSrc, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return Path.Combine(outDir, fn.Remove(idx, stemSrc.Length).Insert(idx, stemDst));

            return null;
        }

        private static void MaybeWarnOtherResS(ICollection<string> lines, string gameDataDirOrNull)
        {
            if (string.IsNullOrEmpty(gameDataDirOrNull) || !Directory.Exists(gameDataDirOrNull))
                return;

            try
            {
                var k = Directory.GetFiles(gameDataDirOrNull, "*.resS", SearchOption.TopDirectoryOnly);
                if (k.Length == 0)
                    return;
                lines.Add(
                    "[Sidecar] В папке есть .resS, но они не связаны именем с текущим контейнером. " +
                    "Найдите тот файл, который идёт в паре именно к этому контейнеру Unity, скопируйте в каталог сохранённого результата и переименуйте с тем же базовым именем, что и сохранённый файл.");
            }
            catch { }
        }
    }
}
