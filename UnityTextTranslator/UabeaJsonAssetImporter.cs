using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UnityTextTranslator
{
    internal static class UabeaJsonAssetImporter
    {
        public static UabeaImportResult ImportFolder(string assetsPath, string jsonFolder, string outputPath, string gameDataRootForPreload = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(assetsPath) || !File.Exists(assetsPath))
                throw new FileNotFoundException("Файл .assets не найден.", assetsPath);

            if (string.IsNullOrWhiteSpace(jsonFolder) || !Directory.Exists(jsonFolder))
                throw new DirectoryNotFoundException("Папка с JSON не найдена.");

            var baseSafe = UabeaJsonPaths.SafeFileNamePart(Path.GetFileNameWithoutExtension(assetsPath)).TrimEnd('-', '_', ' ');
            var result = new UabeaImportResult();
            var manager = new AssetsManager
            {
                UseQuickLookup = false,
                UseTemplateFieldCache = false,
                UseMonoTemplateFieldCache = false,
                UseRefTypeManagerCache = false
            };

            var resolvedRoot = !string.IsNullOrWhiteSpace(gameDataRootForPreload)
                ? UnityAssetsGameFolderHelper.ResolveGameDataFolder(gameDataRootForPreload)
                : UnityAssetsGameFolderHelper.ResolveGameDataFolder(Path.GetDirectoryName(assetsPath));

            string managedAssembliesFolder = null;
            if (!string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot))
            {
                var msgN = result.Messages.Count;
                if (!UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, resolvedRoot, out managedAssembliesFolder, result.Messages) &&
                    result.Messages.Count == msgN)
                    result.Messages.Add(UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(resolvedRoot));

                UnityAssetsGameFolderHelper.PreloadAllAssetsFromDataFolder(manager, resolvedRoot);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fileInst = UnityAssetsGameFolderHelper.GetOrLoadPrimaryAssetsFile(manager, assetsPath);
            string absAssets;
            string absOut;
            try
            {
                absAssets = Path.GetFullPath(assetsPath);
                absOut = Path.GetFullPath(outputPath);
            }
            catch
            {
                absAssets = assetsPath;
                absOut = outputPath;
            }

            var writeInPlace =
                string.Equals(absAssets, absOut, StringComparison.OrdinalIgnoreCase);
            var stagedWritePath = writeInPlace ? UnitySerializedFileSidecars.GetStagedWritePathForInPlaceImport(absOut) : absOut;
            if (writeInPlace && result.Messages.Count < 200)
                result.Messages.Add(
                    "[Импорт] Тот же путь, что у открытого контейнера — запись во временный файл «" +
                    Path.GetFileName(stagedWritePath) + "» и подмена после выгрузки (иначе AssetsTools.NET портит пару с .resS).");

            try
            {
                TryLoadClassPackage(manager, fileInst);
                var jsonFiles = UabeaJsonPaths.DiscoverImportJsonFilesForContainer(jsonFolder, baseSafe, fileInst);
                if (jsonFiles.Count == 0)
                {
                    var allJson = Directory.GetFiles(jsonFolder, "*.json", SearchOption.AllDirectories);
                    var names = string.Join(", ", allJson.Take(12).Select(Path.GetFileName));
                    throw new InvalidOperationException(
                        "Не найдено JSON для «" + Path.GetFileName(assetsPath) + "». Ожидаются: «" + baseSafe +
                        "-PathID.json», «ЧтоУгодно-" + baseSafe + "-PathID.json» (как в UABEAvalonia), либо PathID.json под папкой «" + baseSafe + "» внутри рабочей папки JSON.\r\n" +
                        "Всего .json в папке: " + allJson.Length +
                        (allJson.Length > 0 ? ". Примеры: " + names + (allJson.Length > 12 ? " …" : "") : "") +
                        "\r\nЕсли включён только MonoBehaviour, для этого .assets могло не создаться ни одного файла — снимите галочку или экспортируйте нужный контейнер заново.");
                }

                result.JsonFound = jsonFiles.Count;
                var refMan = CreateRefTypeManager(manager, fileInst);

                var isLevelFile = UnityAssetsGameFolderHelper.LooksLikeStreamingSceneLevelContainer(absAssets);

                ApplyJsonPatchesToAssetsFile(manager, fileInst, jsonFiles, result, cancellationToken, refMan,
                    managedAssembliesFolder: managedAssembliesFolder);

                if (result.Imported == 0)
                {
                    if (result.Skipped > 0 && result.Failed == 0)
                        throw new InvalidOperationException(
                            "Изменений нет: ни в одном объекте этого контейнера текст не отличается от оригинала " +
                            $"({result.Skipped} объект(ов) пропущено как неизменённые). " +
                            "Этот файл (например level*) может вообще не содержать переводимого текста — " +
                            "переводимые строки часто лежат в sharedassets*/resources.assets или в бандлах. " +
                            "Файл не пересобирался, оригинал не тронут.");
                    throw new InvalidOperationException("Ни один JSON не был импортирован. Проверь, что JSON экспортирован из этого же .assets файла через UABEA.");
                }

                if (isLevelFile)
                {
                    string canonStem = null;
                    try
                    {
                        canonStem = Path.GetFileName(absAssets.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar));
                    }
                    catch { }

                    var writeLeaf = Path.GetFileName(stagedWritePath);
                    if (!string.IsNullOrEmpty(canonStem) && !string.IsNullOrEmpty(writeLeaf))
                    {
                        UnitySerializedFileSidecars.TryRetargetStreamingExternalsToCanonicalStem(
                            fileInst.file, writeLeaf, canonStem, result.Messages);
                    }
                }

                try
                {
                    if (File.Exists(stagedWritePath))
                        File.Delete(stagedWritePath);
                }
                catch { /* если файл занят — Write перезапишет или упадёт */ }

                using (var writer = new AssetsFileWriter(stagedWritePath))
                {
                    fileInst.file.Write(writer);
                }

                if (writeInPlace)
                {
                    // запись ПОВЕРХ оригинала: парные .resS/.resource НЕ копируем — импорт меняет только MonoBehaviour, а потоки
                    // уже соответствуют контейнеру по имени. Раньше копировался весь resS (для resources.assets ~1.9 ГБ на КАЖДЫЙ импорт) впустую.
                    result.CompanionResourceFilesCopied = 0;
                    if (result.Messages.Count < 200)
                        result.Messages.Add(
                            "[Sidecar] Запись поверх оригинала — парные .resS/.resource не трогаются (остаются исходные, имя совпадает). Копирование потоков пропущено для скорости.");
                }
                else
                {
                    result.CompanionResourceFilesCopied =
                        UnitySerializedFileSidecars.CopyCompanionsToOutput(assetsPath, stagedWritePath, fileInst.file,
                            result.Messages);
                }
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }

            if (writeInPlace && stagedWritePath != null &&
                !string.Equals(stagedWritePath, absOut, StringComparison.OrdinalIgnoreCase))
            {
                if (!UnitySerializedFileSidecars.TryCommitStagedContainerInPlace(stagedWritePath, absOut, result.Messages))
                    throw new InvalidOperationException(
                        "Импорт записал данные во временный файл, но подмена оригинала не удалась. " +
                        "Проверьте сообщения [Sidecar] в логе. Оригинал не трогался после выгрузки менеджера.");
            }

            return result;
        }

        /// <summary>Применяет найденные JSON к уже открытому контейнеру (<see cref="AssetsFileInstance"/>).</summary>
        internal static void ApplyJsonPatchesToAssetsFile(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            List<(string Path, long PathId)> jsonFiles,
            UabeaImportResult result,
            CancellationToken cancellationToken = default,
            RefTypeManager refMan = null,
            string stringTableLocaleCodeOverride = null,
            string managedAssembliesFolder = null)
        {
            if (jsonFiles == null || jsonFiles.Count == 0 || result == null)
                return;

            refMan = refMan ?? CreateRefTypeManager(manager, fileInst);

            foreach (var entry in jsonFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = fileInst.file.GetAssetInfo(entry.PathId);
                if (info == null)
                {
                    result.Skipped++;
                    result.Messages.Add(
                        $"PathID {entry.PathId}: объект не найден в asset (файл «{Path.GetFileName(entry.Path)}» не от этого CAB или число в конце имени не PathID).");
                    continue;
                }

                try
                {
                    var jsonText = File.ReadAllText(entry.Path);
                    jsonText = ApplyStringTableTargetLocaleJsonAdjustments(jsonText, stringTableLocaleCodeOverride);

                    // Экспортёр пишет такой файл, если дерево типа разобрать не удалось; обычный шаблонный импорт невозможен.
                    if (TryApplyRawFallbackJsonPatch(jsonText, info, Path.GetFileName(entry.Path), result))
                        continue;

                    var typeId = info.GetTypeId(fileInst.file);
                    var importFlags = AssetReadFlags.None;
                    if (typeId == (int)AssetClassID.MonoBehaviour
                        && manager.ClassDatabase != null
                        && !fileInst.file.Metadata.TypeTreeEnabled)
                        importFlags |= AssetReadFlags.ForceFromCldb;

                    // StringTable: 1) ImportJsonAsset в байты по шаблону (как UABEA) — переносит весь JSON в сериализацию;
                    // 2) патч по GetBaseField — доводит m_TableData, если шаблон не совпал с type tree.
                    // Сначала только «патч по старым байтам» оставлял IL2CPP-ассет без шаблонного шага — правки не попадали.
                    if (typeId == (int)AssetClassID.MonoBehaviour && JsonTextHasStringTableTableData(jsonText))
                    {
                        var importedBeforePath = result.Imported;
                        var label = Path.GetFileName(entry.Path);
                        var templateOk = TryImportStringTableViaFullTemplate(
                            manager, fileInst, info, jsonText, refMan, importFlags,
                            label, result,
                            suppressImportedIncrement: true);
                        var patchHandled = TryPatchStringTableDirectly(
                            manager, fileInst, info, jsonText, result, label,
                            stringTableLocaleCodeOverride);

                        if (templateOk || patchHandled)
                        {
                            if (result.Imported == importedBeforePath)
                                result.Imported++;
                            continue;
                        }
                    }

                    // если JSON совпадает с ассетом (текст не меняли) — НЕ перезаписываем: пересборка MonoBehaviour по
                    // IL2CPP-шаблону неточно сериализует не-строковые поля (float'ы камер/света → чёрный экран).
                    if (IsJsonUnchangedAgainstAsset(manager, fileInst, info, jsonText, managedAssembliesFolder))
                    {
                        result.Skipped++;
                        if (result.Messages.Count < 200)
                            result.Messages.Add(
                                $"{Path.GetFileName(entry.Path)}: без изменений текста — объект не перезаписан " +
                                "(пересборка нетронутых объектов могла бы повредить не-строковые поля).");
                        continue;
                    }

                    // ОСНОВНОЙ безопасный путь: точечная замена ТОЛЬКО байт строк в оригинальных байтах объекта.
                    // Пересборка по IL2CPP-шаблону (ImportJsonAsset) ВСЕГДА может испортить не-строковые поля
                    // (float'ы камер/света → чёрный экран), поэтому используем ТОЛЬКО splice.
                    var spliced = TryBuildStringSplicedRawBytes(manager, fileInst, info, jsonText, managedAssembliesFolder, result, Path.GetFileName(entry.Path));
                    if (spliced != null)
                    {
                        info.SetNewData(spliced);
                        result.Imported++;
                        continue;
                    }

                    // splice не удался: НЕ пересобираем по шаблону (испортит не-строковые поля) — ПРОПУСКАЕМ объект (Write
                    // скопирует байт-в-байт). Перевод не применится, но игра не сломается.
                    result.Skipped++;
                    if (result.Messages.Count < 200)
                        result.Messages.Add(
                            $"{Path.GetFileName(entry.Path)}: точечная замена строк невозможна — объект ПРОПУЩЕН " +
                            "(оставлен оригинал, чтобы не повредить не-строковые поля). Перевод этого объекта не применён.");
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Messages.Add($"{Path.GetFileName(entry.Path)}: {ex.Message}");
                }
            }
        }

        /// <summary>Импорт JSON для CAB/контейнера (класс-пакет по версии файла, без записи на диск); нет JSON → 0 импортов без ошибки.</summary>
        internal static UabeaImportResult ImportJsonIntoAssetsFileInstanceFromFolder(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            string jsonFolder,
            string containerBaseSafe,
            string stringTableLocaleCodeOverride = null)
        {
            TryLoadClassPackage(manager, fileInst);
            var jsonFiles = UabeaJsonPaths.DiscoverImportJsonFilesForContainer(jsonFolder, containerBaseSafe, fileInst);
            var result = new UabeaImportResult { JsonFound = jsonFiles.Count };
            if (jsonFiles.Count == 0)
                return result;

            ApplyJsonPatchesToAssetsFile(manager, fileInst, jsonFiles, result, default, CreateRefTypeManager(manager, fileInst), stringTableLocaleCodeOverride);
            return result;
        }

        /// <summary>Перебор флагов чтения (как экспортёр): иначе в CAB с TypeTree один GetBaseField часто даёт dummy/пустое дерево и патч не меняет файл.</summary>
        internal static AssetTypeValueField TryGetBaseFieldReliable(AssetsManager manager, AssetsFileInstance fileInst, AssetFileInfo info)
        {
            var baseFlags = AssetReadFlags.None;
            if ((int)fileInst.file.Metadata.TargetPlatform < 0)
                baseFlags |= AssetReadFlags.PreferEditor;

            var typeId = info.GetTypeId(fileInst.file);
            if (typeId == (int)AssetClassID.MonoBehaviour
                && manager.ClassDatabase != null
                && !fileInst.file.Metadata.TypeTreeEnabled)
                baseFlags |= AssetReadFlags.ForceFromCldb;

            foreach (var flags in EnumerateImportFlagAttempts(baseFlags))
            {
                try
                {
                    var b = manager.GetBaseField(fileInst, info, flags);
                    if (b != null)
                        return b;
                }
                catch { }

                try
                {
                    var b = manager.GetBaseField(fileInst, info.PathId, flags);
                    if (b != null)
                        return b;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Пишет правки в <see cref="AssetFileInfo"/> так, чтобы байты совпали с <see cref="AssetsFile.Write"/>: на части IL2CPP
        /// один <see cref="AssetFileInfo.SetNewData(AssetTypeValueField)"/> не обновляет буфер (повторный экспорт CAB показывает старый текст).
        /// </summary>
        private static void SetNewDataFromBaseFieldForWrite(AssetFileInfo info, AssetTypeValueField baseField)
        {
            if (info == null || baseField == null)
                return;
            try
            {
                var bytes = baseField.WriteToByteArray(false);
                if (bytes != null && bytes.Length > 0)
                {
                    info.SetNewData(bytes);
                    return;
                }
            }
            catch { }

            info.SetNewData(baseField);
        }

        /// <summary>Текущее значение <c>m_LocaleId.m_Code</c> (без учёта регистра имён полей через <see cref="FieldICase"/>).</summary>
        internal static string TryReadLocaleIdCodeFromBaseField(AssetTypeValueField baseField)
        {
            if (baseField == null)
                return null;
            var localeField = FieldICase(baseField, "m_LocaleId");
            if (localeField == null || localeField.IsDummy)
                return null;
            var codeField = FieldICase(localeField, "m_Code");
            if (codeField == null || codeField.IsDummy)
                return null;
            try
            {
                return codeField.AsString;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Ищет <c>Locale</c> (Unity Localization) по <c>m_Identifier.m_Code</c>/<c>m_Code</c> и снимает флаги «выключено/скрыто».</summary>
        internal static void TryPatchLocalizationLocaleAssetsInFile(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            string localeCode,
            UabeaImportResult result,
            out int assetsMatched,
            out int assetsWritten)
        {
            assetsMatched = 0;
            assetsWritten = 0;
            if (manager == null || fileInst?.file == null || string.IsNullOrWhiteSpace(localeCode))
                return;

            var want = localeCode.Trim();
            foreach (AssetFileInfo info in fileInst.file.AssetInfos)
            {
                if (info.Stripped != 0)
                    continue;

                AssetTypeValueField baseField;
                try
                {
                    baseField = TryGetBaseFieldReliable(manager, fileInst, info);
                }
                catch
                {
                    continue;
                }

                if (baseField == null)
                    continue;

                if (!AssetLooksLikeUnityLocaleWithCode(baseField, want))
                    continue;

                assetsMatched++;
                var label = $"PathID {info.PathId}";
                if (TryRelaxLocaleBlockingFlags(baseField, result, label))
                {
                    try
                    {
                        SetNewDataFromBaseFieldForWrite(info, baseField);
                        assetsWritten++;
                    }
                    catch (Exception ex)
                    {
                        result?.Messages.Add($"{label}: [Locale] SetNewData: {ex.Message}");
                    }
                }
                else if (result != null && result.Messages.Count < 120)
                    result.Messages.Add($"{label}: [Locale] код «{want}» найден, bool-полей блокировки не найдено.");
            }
        }

        private static bool AssetLooksLikeUnityLocaleWithCode(AssetTypeValueField root, string code)
        {
            if (root == null || string.IsNullOrWhiteSpace(code))
                return false;

            var id = FieldICase(root, "m_Identifier");
            if (id != null && !id.IsDummy)
            {
                var c = FieldICase(id, "m_Code");
                if (c != null && !c.IsDummy)
                {
                    try
                    {
                        if (string.Equals(c.AsString, code, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }

            var rootCode = FieldICase(root, "m_Code");
            if (rootCode != null && !rootCode.IsDummy)
            {
                try
                {
                    return string.Equals(rootCode.AsString, code, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryRelaxLocaleBlockingFlags(AssetTypeValueField root, UabeaImportResult result, string fileLabel)
        {
            var any = false;
            foreach (var name in new[]
                     {
                         "m_Disabled", "m_IsDisabled", "m_Hidden", "m_IsHidden", "m_Inactive", "m_IsInactive",
                         "m_Blocked", "m_Suppressed", "m_Excluded",
                     })
            {
                if (TrySetBoolFieldIfPresent(root, name, false, result, fileLabel, "[Locale] "))
                    any = true;
            }

            foreach (var name in new[]
                     {
                         "m_Enabled", "m_IsEnabled", "m_Active", "m_IsActive", "m_Visible", "m_ShowInSelector",
                         "m_Selectable", "m_Available",
                     })
            {
                if (TrySetBoolFieldIfPresent(root, name, true, result, fileLabel, "[Locale] "))
                    any = true;
            }

            foreach (var ch in root.Children)
            {
                if (ch == null || ch.IsDummy || string.IsNullOrEmpty(ch.FieldName))
                    continue;
                bool cur;
                try
                {
                    cur = ch.AsBool;
                }
                catch
                {
                    continue;
                }

                var l = ch.FieldName.ToLowerInvariant();
                bool? desired = null;
                if (l.Contains("disabl") || l.Contains("hidden") ||
                    l.Contains("inactive") || l.Contains("block") || l.Contains("suppress") ||
                    l.Contains("exclud"))
                    desired = false;
                else if (l.Contains("enabl") || l.Contains("active") || l.Contains("visible") ||
                         l.Contains("show") || l.Contains("avail") || l.Contains("select"))
                    desired = true;

                if (desired == null || cur == desired.Value)
                    continue;
                try
                {
                    ch.AsBool = desired.Value;
                    any = true;
                    if (result != null && result.Messages.Count < 120)
                        result.Messages.Add($"{fileLabel}: [Locale] {ch.FieldName}: {cur} → {desired.Value}.");
                }
                catch { }
            }

            var meta = FieldICase(root, "m_Metadata");
            if (meta != null && !meta.IsDummy)
            {
                foreach (var ch in meta.Children)
                {
                    if (ch == null || ch.IsDummy || string.IsNullOrEmpty(ch.FieldName))
                        continue;
                    bool cur;
                    try
                    {
                        cur = ch.AsBool;
                    }
                    catch
                    {
                        continue;
                    }

                    var l = ch.FieldName.ToLowerInvariant();
                    if (!(l.Contains("disabl") || l.Contains("hidden") || l.Contains("inactive") ||
                          l.Contains("enabl") || l.Contains("active")))
                        continue;
                    var want = l.Contains("disabl") || l.Contains("hidden") || l.Contains("inactive") ? false : true;
                    if (cur == want)
                        continue;
                    try
                    {
                        ch.AsBool = want;
                        any = true;
                        if (result != null && result.Messages.Count < 120)
                            result.Messages.Add($"{fileLabel}: [Locale] m_Metadata.{ch.FieldName}: {cur} → {want}.");
                    }
                    catch { }
                }
            }

            return any;
        }

        private static bool TrySetBoolFieldIfPresent(
            AssetTypeValueField parent,
            string fieldName,
            bool desired,
            UabeaImportResult result,
            string fileLabel,
            string logPrefix)
        {
            var f = FieldICase(parent, fieldName);
            if (f == null || f.IsDummy)
                return false;
            bool cur;
            try
            {
                cur = f.AsBool;
            }
            catch
            {
                return false;
            }

            if (cur == desired)
                return false;
            try
            {
                f.AsBool = desired;
                if (result != null && result.Messages.Count < 120)
                    result.Messages.Add($"{fileLabel}: {logPrefix}{fieldName}: {cur} → {desired}.");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<AssetReadFlags> EnumerateImportFlagAttempts(AssetReadFlags baseFlags)
        {
            var extras = new[]
            {
                AssetReadFlags.None,
                AssetReadFlags.SkipMonoBehaviourFields,
                AssetReadFlags.ForceFromCldb,
                AssetReadFlags.ForceFromCldb | AssetReadFlags.SkipMonoBehaviourFields,
                AssetReadFlags.PreferEditor,
                AssetReadFlags.PreferEditor | AssetReadFlags.SkipMonoBehaviourFields,
                AssetReadFlags.PreferEditor | AssetReadFlags.ForceFromCldb,
                AssetReadFlags.PreferEditor | AssetReadFlags.ForceFromCldb | AssetReadFlags.SkipMonoBehaviourFields,
            };

            var seen = new HashSet<uint>();
            foreach (var extra in extras)
            {
                var combined = baseFlags | extra;
                var key = (uint)combined;
                if (seen.Add(key))
                    yield return combined;
            }
        }

        private static bool JsonTextHasStringTableTableData(string jsonText)
        {
            if (string.IsNullOrEmpty(jsonText))
                return false;
            try
            {
                var root = JObject.Parse(jsonText);
                return JGetICase(root, "m_TableData") != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Полная пересборка <c>MonoBehaviour</c> из JSON при наличии <c>m_TableData</c> (Unity Localization StringTable).</summary>
        private static bool TryImportStringTableViaFullTemplate(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            AssetFileInfo info,
            string jsonText,
            RefTypeManager refMan,
            AssetReadFlags importFlags,
            string fileLabel,
            UabeaImportResult result,
            bool suppressImportedIncrement = false)
        {
            if (manager == null || fileInst?.file == null || info == null || string.IsNullOrEmpty(jsonText) || result == null)
                return false;
            if (info.GetTypeId(fileInst.file) != (int)AssetClassID.MonoBehaviour)
                return false;

            JObject root;
            try { root = JObject.Parse(jsonText); }
            catch { return false; }

            if (JGetICase(root, "m_TableData") == null)
                return false;

            try
            {
                var template = manager.GetTemplateBaseField(fileInst, info, importFlags);
                if (template == null)
                    return false;

                var data = ImportJsonAsset(jsonText, template, refMan);
                info.SetNewData(data);
                if (!suppressImportedIncrement)
                    result.Imported++;
                if (result.Messages.Count < 120)
                    result.Messages.Add(
                        suppressImportedIncrement
                            ? $"{fileLabel}: [StringTable] ImportJsonAsset → байты (далее патч строк)."
                            : $"{fileLabel}: [StringTable] импорт из JSON (ImportJsonAsset → байты).");
                return true;
            }
            catch (Exception ex)
            {
                if (result.Messages.Count < 120)
                    result.Messages.Add($"{fileLabel}: [StringTable] ImportJsonAsset не вышел ({ex.Message}) — пробуем патч строк.");
                return false;
            }
        }

        /// <summary>Надёжный импорт StringTable: через <c>GetBaseField</c> меняются только строки в <c>m_TableData</c> (<c>m_Localized</c>/вложенный <c>LocalizedString</c>), без пересборки MonoBehaviour.</summary>
        /// <returns><c>true</c>, если патч применён (дальнейший импорт PathID не нужен).</returns>
        private static bool TryPatchStringTableDirectly(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            AssetFileInfo info,
            string jsonText,
            UabeaImportResult result,
            string fileLabel,
            string stringTableLocaleCodeOverride = null)
        {
            JObject root;
            try { root = JObject.Parse(jsonText); }
            catch { return false; }

            var tableTok = JGetICase(root, "m_TableData");
            JArray entriesArr = null;
            if (tableTok is JArray directArr)
                entriesArr = directArr;
            else if (tableTok is JObject tableDataJson)
                entriesArr = JGetICase(tableDataJson, "Array") as JArray;

            if (entriesArr == null || entriesArr.Count == 0)
                return false;

            var jsonRows = new List<(long Id, string Text, int Index)>(entriesArr.Count);
            foreach (var tok in entriesArr)
            {
                var entry = tok as JObject;
                if (entry == null) continue;
                var idTok = JGetICase(entry, "m_Id") as JValue;
                if (idTok == null) continue;
                long id;
                try { id = idTok.Value<long>(); }
                catch { continue; }
                if (!TryReadLocalizedStringFromStringTableJsonEntry(entry, out var text) || text == null)
                    continue;
                jsonRows.Add((id, text, jsonRows.Count));
            }

            if (entriesArr.Count > jsonRows.Count && result != null && result.Messages.Count < 72)
                result.Messages.Add(
                    $"{fileLabel}: [StringTable] в таблице {entriesArr.Count} записей, из JSON извлечено текстов для патча: {jsonRows.Count}. " +
                    "Остальные строки без распознанного текста в m_Localized (или только ссылки без строки) — правка одного слова там могла не попасть в импорт.");

            if (jsonRows.Count == 0)
            {
                if (result != null && result.Messages.Count < 64)
                    result.Messages.Add(
                        $"{fileLabel}: [StringTable] в JSON есть m_TableData ({entriesArr.Count} записей), но не удалось прочитать ни одной строки " +
                        "(ожидаются m_Id и текст в m_Localized или вложенном объекте). Импорт по этому PathID пойдёт общим путём и может не затронуть локализацию.");
                return false;
            }

            var translations = new Dictionary<long, string>(jsonRows.Count);
            foreach (var row in jsonRows)
                translations[row.Id] = row.Text;

            AssetTypeValueField baseField = TryGetBaseFieldReliable(manager, fileInst, info);
            if (baseField == null)
            {
                if (result != null && result.Messages.Count < 64)
                    result.Messages.Add(
                        $"{fileLabel}: [StringTable] не удалось прочитать MonoBehaviour (GetBaseField ни с одним набором флагов) — проверьте classdata.tpk и версию Unity.");
                return false;
            }

            var liveTableData = FieldICase(baseField, "m_TableData");
            if (liveTableData == null || liveTableData.IsDummy)
                return false;

            var liveArray = FieldICase(liveTableData, "Array");
            if (liveArray == null || liveArray.IsDummy)
                return false;

            var entries = liveArray.Children;
            if (entries.Count == 0)
                return false;

            var liveIds = new HashSet<long>();
            foreach (var liveEntry in entries)
            {
                var idField = FieldICase(liveEntry, "m_Id");
                if (idField == null || idField.IsDummy)
                    continue;
                try
                {
                    liveIds.Add(idField.AsLong);
                }
                catch { }
            }

            var matchedIds = 0;
            var patched = 0;
            var equalRows = 0;
            var writeFailedRows = 0;
            foreach (var entry in entries)
            {
                var idField = FieldICase(entry, "m_Id");
                if (idField == null || idField.IsDummy)
                    continue;
                long id;
                try { id = idField.AsLong; }
                catch { continue; }
                if (!translations.TryGetValue(id, out var newValue))
                    continue;
                matchedIds++;

                switch (PatchLocalizedStringOnTableEntry(entry, newValue))
                {
                    case LocalizedRowPatchResult.Patched:
                        patched++;
                        break;
                    case LocalizedRowPatchResult.AlreadyEqual:
                        equalRows++;
                        break;
                    case LocalizedRowPatchResult.WriteFailed:
                        writeFailedRows++;
                        break;
                }
            }

            // Fallback для некоторых дампов: часть m_Id в JSON может не совпасть, хотя файл тот же CAB.
            // Если размер массива совпадает, добиваем изменения по индексу элементов.
            var patchedByIndex = 0;
            if (patched < translations.Count && entries.Count == entriesArr.Count)
            {
                foreach (var row in jsonRows)
                {
                    if (liveIds.Contains(row.Id))
                        continue;
                    if (row.Index < 0 || row.Index >= entries.Count)
                        continue;
                    switch (PatchLocalizedStringOnTableEntry(entries[row.Index], row.Text))
                    {
                        case LocalizedRowPatchResult.Patched:
                            patched++;
                            patchedByIndex++;
                            break;
                        case LocalizedRowPatchResult.AlreadyEqual:
                            equalRows++;
                            break;
                        case LocalizedRowPatchResult.WriteFailed:
                            writeFailedRows++;
                            break;
                    }
                }
            }

            if (patched == 0)
            {
                if (matchedIds > 0 && writeFailedRows > 0)
                {
                    if (result != null && result.Messages.Count < 64)
                        result.Messages.Add(
                            $"{fileLabel}: [StringTable] совпало строк по m_Id: {matchedIds}, из них записать текст не удалось: {writeFailedRows} " +
                            "(LocalizedString в asset не строка одного поля — см. полный импорт ниже). Равных JSON: " + equalRows + ".");
                    return false;
                }

                if (matchedIds > 0)
                {
                    if (result != null && result.Messages.Count < 64)
                        result.Messages.Add(
                            $"{fileLabel}: [StringTable] совпало строк: {matchedIds} — как в JSON; сериализация WriteToByteArray в слот ассета.");
                    try
                    {
                        var flushTree = TryGetBaseFieldReliable(manager, fileInst, info) ?? baseField;
                        SetNewDataFromBaseFieldForWrite(info, flushTree);
                    }
                    catch (Exception ex)
                    {
                        if (result != null && result.Messages.Count < 64)
                            result.Messages.Add($"{fileLabel}: [StringTable] сериализация после совпадения: {ex.Message}");
                    }

                    return true;
                }

                if (result != null && result.Messages.Count < 64)
                    result.Messages.Add(
                        $"{fileLabel}: [StringTable] из JSON прочитано {translations.Count} строк, но совпадений m_Id с asset не найдено " +
                        "(возможен другой PathID/CAB или устаревший JSON не из этого bundle).");
                return false;
            }

            TryPatchStringTableLocaleOnBaseField(baseField, stringTableLocaleCodeOverride, result, fileLabel);
            TryPatchStringTableAssetNameOnBaseField(baseField, stringTableLocaleCodeOverride, result, fileLabel);

            try
            {
                SetNewDataFromBaseFieldForWrite(info, baseField);
                result.Imported++;
                if (patchedByIndex > 0)
                    result.Messages.Add(
                        $"{fileLabel}: [StringTable] прямой патч {patched} строк из {translations.Count} (id-match + index-fallback={patchedByIndex}).");
                else
                    result.Messages.Add(
                        $"{fileLabel}: [StringTable] патч строк {patched}/{translations.Count}.");
                return true;
            }
            catch (Exception ex)
            {
                result.Messages.Add(
                    $"{fileLabel}: [StringTable] GetBaseField патч: SetNewData упал ({ex.Message}), пробуем стандартный JSON-импорт.");
                return false;
            }
        }

        private static string ApplyStringTableTargetLocaleJsonAdjustments(string jsonText, string localeCode)
        {
            if (string.IsNullOrWhiteSpace(localeCode) || string.IsNullOrEmpty(jsonText))
                return jsonText;
            try
            {
                var root = JObject.Parse(jsonText);
                var code = localeCode.Trim();
                var codeLower = code.ToLowerInvariant();
                var changed = false;

                if (JGetICase(root, "m_LocaleId") is JObject loc)
                {
                    JProperty codeProp = null;
                    foreach (var prop in loc.Properties())
                    {
                        if (!string.Equals(prop.Name, "m_Code", StringComparison.OrdinalIgnoreCase))
                            continue;
                        codeProp = prop;
                        break;
                    }
                    if (codeProp != null)
                    {
                        var cur = codeProp.Value?.ToString();
                        if (!string.Equals(cur, code, StringComparison.Ordinal))
                        {
                            codeProp.Value = code;
                            changed = true;
                        }
                    }
                    else
                    {
                        loc["m_Code"] = code;
                        changed = true;
                    }
                }

                if (JGetICase(root, "m_Name") is JValue jn && jn.Type == JTokenType.String)
                {
                    var n = jn.Value<string>();
                    var newN = RemapStringTableAssetNameSuffix(n, codeLower);
                    if (newN != null && !string.Equals(n, newN, StringComparison.Ordinal))
                    {
                        jn.Value = newN;
                        changed = true;
                    }
                }

                if (!changed)
                    return jsonText;
                return root.ToString(Formatting.None);
            }
            catch
            {
                return jsonText;
            }
        }

        /// <summary>Например <c>Dialogs_en</c> → <c>Dialogs_ru</c> при целевой локали <c>ru</c>.</summary>
        private static string RemapStringTableAssetNameSuffix(string name, string targetLocaleCodeLower)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(targetLocaleCodeLower))
                return null;
            var t = targetLocaleCodeLower.Trim().ToLowerInvariant();
            if (name.EndsWith("_en", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 3) + "_" + t;
            if (name.EndsWith("_english", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 8) + "_" + t;
            return null;
        }

        private static void TryPatchStringTableAssetNameOnBaseField(
            AssetTypeValueField baseField,
            string localeCode,
            UabeaImportResult result,
            string fileLabel)
        {
            if (baseField == null || string.IsNullOrWhiteSpace(localeCode))
                return;
            var nameField = FieldICase(baseField, "m_Name");
            if (nameField == null || nameField.IsDummy)
                return;
            string cur;
            try { cur = nameField.AsString; }
            catch { return; }
            var newN = RemapStringTableAssetNameSuffix(cur, localeCode);
            if (newN == null || string.Equals(cur, newN, StringComparison.Ordinal))
                return;
            try
            {
                nameField.AsString = newN;
                if (result != null && result.Messages.Count < 96)
                    result.Messages.Add($"{fileLabel}: [StringTable] m_Name: «{cur}» → «{newN}».");
            }
            catch
            {
                if (result != null && result.Messages.Count < 96)
                    result.Messages.Add($"{fileLabel}: [StringTable] не удалось записать m_Name = «{newN}».");
            }
        }

        private static void TryPatchStringTableLocaleOnBaseField(
            AssetTypeValueField baseField,
            string localeCode,
            UabeaImportResult result,
            string fileLabel)
        {
            if (baseField == null || string.IsNullOrWhiteSpace(localeCode))
                return;
            var localeField = FieldICase(baseField, "m_LocaleId");
            if (localeField == null || localeField.IsDummy)
                return;
            var codeField = FieldICase(localeField, "m_Code");
            if (codeField == null || codeField.IsDummy)
                return;
            string before = null;
            try { before = codeField.AsString; }
            catch { }
            var code = localeCode.Trim();
            if (string.Equals(before, code, StringComparison.Ordinal))
                return;
            try
            {
                codeField.AsString = code;
                if (result != null && result.Messages.Count < 96)
                    result.Messages.Add(
                        $"{fileLabel}: [StringTable] m_LocaleId.m_Code: «{before ?? "?"}» → «{code}».");
            }
            catch
            {
                if (result != null && result.Messages.Count < 96)
                    result.Messages.Add(
                        $"{fileLabel}: [StringTable] не удалось записать m_LocaleId.m_Code = «{code}».");
            }
        }

        private static bool TryForceLocaleCodeEnToRu(AssetTypeValueField baseField, UabeaImportResult result, string fileLabel)
        {
            if (baseField == null)
                return false;
            var localeField = FieldICase(baseField, "m_LocaleId");
            if (localeField == null || localeField.IsDummy)
                return false;
            var codeField = FieldICase(localeField, "m_Code");
            if (codeField == null || codeField.IsDummy)
                return false;
            string before;
            try { before = codeField.AsString; }
            catch { return false; }
            if (!string.Equals(before, "en", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                codeField.AsString = "ru";
                if (result != null && result.Messages.Count < 120)
                    result.Messages.Add($"{fileLabel}: [Clone ru] m_LocaleId.m_Code «en» → «ru».");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRemapAssetNameEnSuffixToRu(AssetTypeValueField baseField, UabeaImportResult result, string fileLabel)
        {
            if (baseField == null)
                return false;
            var nameField = FieldICase(baseField, "m_Name");
            if (nameField == null || nameField.IsDummy)
                return false;
            string cur;
            try { cur = nameField.AsString; }
            catch { return false; }
            var newN = RemapStringTableAssetNameSuffix(cur, "ru");
            if (newN == null || string.Equals(cur, newN, StringComparison.Ordinal))
                return false;
            try
            {
                nameField.AsString = newN;
                if (result != null && result.Messages.Count < 120)
                    result.Messages.Add($"{fileLabel}: [Clone ru] m_Name «{cur}» → «{newN}».");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Во всех ассетах CAB: <c>m_LocaleId.m_Code</c> «en»→«ru», <c>m_Name</c> суффикс <c>_en</c>→<c>_ru</c>.</summary>
        /// <param name="patchedPathIds">PathID ассетов, для которых вызван <see cref="AssetFileInfo.SetNewData"/>.</param>
        /// <param name="cabLogPrefix">Префикс строк лога (например <c>[Clone ru] CAB[0]</c>); при null — без пошаговой диагностики.</param>
        internal static int ApplyEnToRuLocaleCloneToAllAssetsInFile(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            UabeaImportResult result,
            ICollection<long> patchedPathIds = null,
            string cabLogPrefix = null)
        {
            if (manager == null || fileInst?.file == null || result == null)
                return 0;
            if (!string.IsNullOrEmpty(cabLogPrefix) && result.Messages.Count < 240)
                result.Messages.Add(
                    $"{cabLogPrefix} шаг 3: перебор AssetInfos — TryGetBaseFieldReliable → правка → SetNewData " +
                    "(сериализация CAB выполняется в вызывающем коде только после этого).");

            var written = 0;
            var verifyLeft = 40;
            foreach (AssetFileInfo info in fileInst.file.AssetInfos)
            {
                if (info.Stripped != 0)
                    continue;
                AssetTypeValueField baseField;
                try
                {
                    baseField = TryGetBaseFieldReliable(manager, fileInst, info);
                }
                catch
                {
                    continue;
                }

                if (baseField == null)
                    continue;
                var label = "PathID " + info.PathId;
                var changed = TryForceLocaleCodeEnToRu(baseField, result, label)
                    | TryRemapAssetNameEnSuffixToRu(baseField, result, label);
                if (!changed)
                    continue;
                try
                {
                    SetNewDataFromBaseFieldForWrite(info, baseField);
                    written++;
                    patchedPathIds?.Add(info.PathId);
                }
                catch (Exception ex)
                {
                    if (result.Messages.Count < 120)
                        result.Messages.Add($"{label}: [Clone ru] SetNewData: {ex.Message}");
                    continue;
                }

                if (string.IsNullOrEmpty(cabLogPrefix) || verifyLeft-- <= 0 || result.Messages.Count >= 240)
                    continue;
                AssetTypeValueField verifyTree = null;
                try
                {
                    verifyTree = TryGetBaseFieldReliable(manager, fileInst, info);
                }
                catch { }

                var codeAfter = TryReadLocaleIdCodeFromBaseField(verifyTree);
                result.Messages.Add(
                    $"{cabLogPrefix} шаг 3 (сразу после SetNewData) PathID {info.PathId}: повторное чтение " +
                    $"m_LocaleId.m_Code = «{codeAfter ?? "—"}» " +
                    $"(ожидается «ru», если меняли только имя — код может быть прежним).");
            }

            if (!string.IsNullOrEmpty(cabLogPrefix) && written > 0 && result.Messages.Count < 240)
                result.Messages.Add($"{cabLogPrefix} шаг 3: для этого CAB вызван SetNewData у {written} ассет(ов).");

            return written;
        }

        /// <summary>Достаёт переводимую строку из одной записи <c>m_TableData</c> в экспортированном JSON (разные версии Unity Localization).</summary>
        private static bool TryReadLocalizedStringFromStringTableJsonEntry(JObject entry, out string text)
        {
            text = null;
            if (entry == null)
                return false;

            var locTok = JGetICase(entry, "m_Localized");
            if (locTok is JValue jv && jv.Type == JTokenType.String)
            {
                text = jv.Value<string>();
                return true;
            }

            if (locTok is JObject locObj)
            {
                foreach (var name in new[] { "m_LocalizedString", "localizedString", "m_Value", "value" })
                {
                    var inner = JGetICase(locObj, name) as JValue;
                    if (inner != null && inner.Type == JTokenType.String)
                    {
                        text = inner.Value<string>();
                        return true;
                    }
                }

                if (TryReadDeepJsonLocalizedString(locObj, out text, 0))
                    return true;
            }

            foreach (var name in new[] { "m_LocalizedString", "m_Value" })
            {
                var v = JGetICase(entry, name) as JValue;
                if (v != null && v.Type == JTokenType.String)
                {
                    text = v.Value<string>();
                    return true;
                }
            }

            return false;
        }

        /// <summary>Вложенный LocalizedString в новых сборках Unity (не только один уровень под m_Localized).</summary>
        private static bool TryReadDeepJsonLocalizedString(JObject obj, out string text, int depth)
        {
            text = null;
            if (obj == null || depth > 8)
                return false;

            foreach (var name in new[] { "m_LocalizedString", "localizedString", "m_Value", "value", "m_String" })
            {
                var t = JGetICase(obj, name);
                if (t is JValue jvv && jvv.Type == JTokenType.String)
                {
                    text = jvv.Value<string>();
                    return true;
                }

                if (t is JObject nest && TryReadDeepJsonLocalizedString(nest, out text, depth + 1))
                    return true;
            }

            foreach (var prop in obj.Properties())
            {
                if (prop.Value is JObject child && TryReadDeepJsonLocalizedString(child, out text, depth + 1))
                    return true;
            }

            return false;
        }

        private enum LocalizedRowPatchResult
        {
            AlreadyEqual,
            Patched,
            WriteFailed
        }

        /// <summary>Сравнивает JSON-текст с тем, что реально читается из asset (в т.ч. вложенный LocalizedString), затем пишет или сообщает о провале записи.</summary>
        private static LocalizedRowPatchResult PatchLocalizedStringOnTableEntry(AssetTypeValueField entry, string newValue)
        {
            if (entry == null || newValue == null)
                return LocalizedRowPatchResult.WriteFailed;

            TryReadLocalizedStringFromAssetEntry(entry, out var cur);
            if (cur != null && string.Equals(cur, newValue, StringComparison.Ordinal))
                return LocalizedRowPatchResult.AlreadyEqual;

            return TryForceWriteLocalizedStringOnTableEntry(entry, newValue)
                ? LocalizedRowPatchResult.Patched
                : LocalizedRowPatchResult.WriteFailed;
        }

        /// <summary>То же, что чтение из JSON в <see cref="TryReadLocalizedStringFromStringTableJsonEntry"/>, но по живому дереву AssetsTools.</summary>
        private static bool TryReadLocalizedStringFromAssetEntry(AssetTypeValueField entry, out string text)
        {
            text = null;
            if (entry == null)
                return false;

            var locField = FieldICase(entry, "m_Localized");
            if (locField != null && !locField.IsDummy)
            {
                if (TryReadAssetStringLeaf(locField, out text))
                    return true;

                foreach (var subName in new[] { "m_LocalizedString", "localizedString", "m_Value", "value" })
                {
                    var sub = FieldICase(locField, subName);
                    if (sub != null && !sub.IsDummy && TryReadAssetStringLeaf(sub, out text))
                        return true;
                }

                if (TryReadLocalizedStringDeepInAssetTree(locField, out text, 0))
                    return true;
            }

            foreach (var subName in new[] { "m_LocalizedString", "m_Value" })
            {
                var sub = FieldICase(entry, subName);
                if (sub != null && !sub.IsDummy && TryReadAssetStringLeaf(sub, out text))
                    return true;
            }

            return false;
        }

        private static bool TryReadLocalizedStringDeepInAssetTree(AssetTypeValueField node, out string text, int depth)
        {
            text = null;
            if (node == null || node.IsDummy || depth > 10)
                return false;

            foreach (var subName in new[] { "m_LocalizedString", "localizedString", "m_Value", "value", "m_String" })
            {
                var sub = FieldICase(node, subName);
                if (sub != null && !sub.IsDummy && TryReadAssetStringLeaf(sub, out text))
                    return true;
            }

            foreach (var child in node.Children)
            {
                if (TryReadLocalizedStringDeepInAssetTree(child, out text, depth + 1))
                    return true;
            }

            return false;
        }

        private static bool IsAssetTemplateString(AssetTypeValueField field)
        {
            try
            {
                return field?.TemplateField != null && field.TemplateField.ValueType == AssetValueType.String;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadAssetStringLeaf(AssetTypeValueField field, out string text)
        {
            text = null;
            if (field == null || field.IsDummy || !IsAssetTemplateString(field))
                return false;
            try
            {
                text = field.AsString;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Пишет строку без предварительного сравнения (сравнение делает <see cref="PatchLocalizedStringOnTableEntry"/>).</summary>
        private static bool TryForceWriteLocalizedStringOnTableEntry(AssetTypeValueField entry, string newValue)
        {
            if (entry == null || newValue == null)
                return false;

            var locField = FieldICase(entry, "m_Localized");
            if (locField != null && !locField.IsDummy)
            {
                if (IsAssetTemplateString(locField))
                {
                    try
                    {
                        locField.AsString = newValue;
                        return true;
                    }
                    catch { }
                }

                foreach (var subName in new[] { "m_LocalizedString", "localizedString", "m_Value", "value" })
                {
                    var sub = FieldICase(locField, subName);
                    if (sub == null || sub.IsDummy || !IsAssetTemplateString(sub))
                        continue;
                    try
                    {
                        sub.AsString = newValue;
                        return true;
                    }
                    catch { }
                }

                if (TryForceWriteLocalizedStringDeepMatchingReadOrder(locField, newValue, 0))
                    return true;
            }

            foreach (var subName in new[] { "m_Value", "m_LocalizedString" })
            {
                var sub = FieldICase(entry, subName);
                if (sub == null || sub.IsDummy || !IsAssetTemplateString(sub))
                    continue;
                try
                {
                    sub.AsString = newValue;
                    return true;
                }
                catch { }
            }

            return false;
        }

        /// <summary>Тот же порядок обхода, что <see cref="TryReadLocalizedStringDeepInAssetTree"/>.</summary>
        private static bool TryForceWriteLocalizedStringDeepMatchingReadOrder(AssetTypeValueField node, string newValue, int depth)
        {
            if (node == null || node.IsDummy || depth > 10)
                return false;

            foreach (var subName in new[] { "m_LocalizedString", "localizedString", "m_Value", "value", "m_String" })
            {
                var sub = FieldICase(node, subName);
                if (sub == null || sub.IsDummy || !IsAssetTemplateString(sub))
                    continue;
                try
                {
                    sub.AsString = newValue;
                    return true;
                }
                catch { }
            }

            foreach (var child in node.Children)
            {
                if (TryForceWriteLocalizedStringDeepMatchingReadOrder(child, newValue, depth + 1))
                    return true;
            }

            return false;
        }

        /// <summary>Возвращает дочерний JToken по имени без учёта регистра.</summary>
        private static JToken JGetICase(JObject obj, string name)
        {
            if (obj == null || name == null) return null;
            foreach (var prop in obj.Properties())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            return null;
        }

        /// <summary>Возвращает дочернее поле AssetTypeValueField по имени без учёта регистра.</summary>
        private static AssetTypeValueField FieldICase(AssetTypeValueField parent, string name)
        {
            if (parent == null || name == null) return null;
            foreach (var child in parent.Children)
                if (string.Equals(child.FieldName, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            return null;
        }

        /// <returns><c>true</c>, если JSON — наш сырой fallback (обработали или засчитали ошибку).</returns>
        private static bool TryApplyRawFallbackJsonPatch(string jsonText, AssetFileInfo info, string fileLabel,
            UabeaImportResult result)
        {
            JObject root;
            try
            {
                root = JObject.Parse(jsonText);
            }
            catch
            {
                return false;
            }

            var marker = root["_unityTextTranslator_rawFallback"];
            if (marker == null || marker.Type != JTokenType.Boolean || !marker.Value<bool>())
                return false;

            if (root["truncated"] != null && root["truncated"].Type == JTokenType.Boolean &&
                root["truncated"].Value<bool>())
            {
                result.Failed++;
                if (result.Messages.Count < 48)
                    result.Messages.Add($"{fileLabel}: сырой JSON был обрезан при экспорте — безопасно импортировать нельзя (нужен успешный разбор этого PathID без fallback).");
                return true;
            }

            var b64 = root["rawBytesBase64"]?.Value<string>();
            if (string.IsNullOrEmpty(b64))
            {
                result.Failed++;
                if (result.Messages.Count < 48)
                    result.Messages.Add($"{fileLabel}: сырой JSON без rawBytesBase64.");
                return true;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(b64);
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Messages.Count < 48)
                    result.Messages.Add($"{fileLabel}: Base64 сырых байт не разобрался: " + ex.Message);
                return true;
            }

            var expectedFromJson = root["byteSize"]?.Value<long>() ?? -1;
            if (expectedFromJson >= 0 && bytes.Length != expectedFromJson)
            {
                result.Failed++;
                if (result.Messages.Count < 48)
                    result.Messages.Add(
                        $"{fileLabel}: длина сырых байт ({bytes.Length}) не совпадает с «byteSize» в JSON ({expectedFromJson}) — импорт пропущен.");
                return true;
            }

            try
            {
                info.SetNewData(bytes);
                result.Imported++;
                return true;
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Messages.Count < 48)
                    result.Messages.Add($"{fileLabel}: установка сырых байтов: " + ex.Message);
                return true;
            }
        }

        internal static void TryLoadClassPackage(AssetsManager manager, AssetsFileInstance fileInst)
        {
            var classDataPath = ClassPackageDownloader.ClassDataPath;
            if (File.Exists(classDataPath))
            {
                manager.LoadClassPackage(classDataPath);
                manager.LoadClassDatabaseFromPackage(fileInst.file.Metadata.UnityVersion);
            }
        }

        private static RefTypeManager CreateRefTypeManager(AssetsManager manager, AssetsFileInstance fileInst)
        {
            var refMan = new RefTypeManager();
            refMan.FromTypeTree(fileInst.file.Metadata);
            if (manager.MonoTempGenerator != null)
            {
                refMan.WithMonoTemplateGenerator(
                    fileInst.file.Metadata,
                    manager.MonoTempGenerator,
                    new Dictionary<AssetTypeReference, AssetTypeTemplateField>());
            }
            return refMan;
        }

        private static byte[] ImportJsonAsset(string jsonText, AssetTypeTemplateField templateField, RefTypeManager refMan)
        {
            using (var ms = new MemoryStream())
            using (var writer = new AssetsFileWriter(ms) { BigEndian = false })
            {
                var token = JToken.Parse(jsonText);
                RecurseJsonImport(writer, templateField, token, refMan);
                return ms.ToArray();
            }
        }

        private static void RecurseJsonImport(AssetsFileWriter writer, AssetTypeTemplateField templateField, JToken token, RefTypeManager refMan)
        {
            var align = templateField.IsAligned;

            if (templateField.Children.Count == 1 && templateField.Children[0].IsArray && token.Type == JTokenType.Array)
            {
                RecurseJsonImport(writer, templateField.Children[0], token, refMan);
                return;
            }

            if (!templateField.HasValue && !templateField.IsArray)
            {
                foreach (var childTemplateField in templateField.Children)
                {
                    var childToken = token[childTemplateField.Name];
                    if (childToken == null)
                        throw new Exception($"В JSON нет поля {childTemplateField.Name}.");

                    RecurseJsonImport(writer, childTemplateField, childToken, refMan);
                }

                if (align)
                    writer.Align();
            }
            else if (templateField.HasValue && templateField.ValueType == AssetValueType.ManagedReferencesRegistry)
            {
                JsonImportManagedReferencesRegistry(writer, templateField, token, refMan);
            }
            else
            {
                switch (templateField.ValueType)
                {
                    case AssetValueType.Bool:
                        writer.Write((bool)token);
                        break;
                    case AssetValueType.UInt8:
                        writer.Write((byte)token);
                        break;
                    case AssetValueType.Int8:
                        writer.Write((sbyte)token);
                        break;
                    case AssetValueType.UInt16:
                        writer.Write((ushort)token);
                        break;
                    case AssetValueType.Int16:
                        writer.Write((short)token);
                        break;
                    case AssetValueType.UInt32:
                        writer.Write((uint)token);
                        break;
                    case AssetValueType.Int32:
                        writer.Write((int)token);
                        break;
                    case AssetValueType.UInt64:
                        writer.Write((ulong)token);
                        break;
                    case AssetValueType.Int64:
                        writer.Write((long)token);
                        break;
                    case AssetValueType.Float:
                        writer.Write((float)token);
                        break;
                    case AssetValueType.Double:
                        writer.Write((double)token);
                        break;
                    case AssetValueType.String:
                        align = true;
                        writer.WriteCountStringInt32((string)token ?? "");
                        break;
                    case AssetValueType.ByteArray:
                        var byteArray = token as JArray ?? new JArray();
                        var bytes = byteArray.Select(x => (byte)x).ToArray();
                        writer.Write(bytes.Length);
                        writer.Write(bytes);
                        break;
                }

                if (templateField.IsArray && templateField.ValueType != AssetValueType.ByteArray)
                {
                    var tokenArray = token as JArray;
                    if (tokenArray == null)
                        throw new Exception($"Поле {templateField.Name} должно быть массивом.");

                    var childTemplateField = templateField.Children[1];
                    writer.Write(tokenArray.Count);
                    foreach (var childToken in tokenArray.Children())
                        RecurseJsonImport(writer, childTemplateField, childToken, refMan);
                }

                if (align)
                    writer.Align();
            }
        }

        /// <summary>Как в UABEANext <c>AssetImport.JsonImportManagedReferencesRegistry</c> (SerializeReference / RefTypes).</summary>
        private static void JsonImportManagedReferencesRegistry(
            AssetsFileWriter writer,
            AssetTypeTemplateField tempField,
            JToken token,
            RefTypeManager refMan)
        {
            if (refMan == null)
                throw new InvalidOperationException("ManagedReferencesRegistry: RefTypeManager не инициализирован.");

            var versionTok = token["version"];
            if (versionTok == null)
                throw new Exception("В JSON нет поля version (ManagedReferencesRegistry).");

            var version = (int)versionTok;
            if (version < 1 || version > 2)
                throw new Exception($"ManagedReferencesRegistry: версия {version} не поддерживается.");

            var refIdsArray = token["RefIds"] as JArray;
            if (refIdsArray == null)
                throw new Exception("В JSON нет массива RefIds (ManagedReferencesRegistry).");

            writer.Write(version);
            var childCount = refIdsArray.Count;

            if (version != 1)
                writer.Write(childCount);

            for (var i = 0; i < childCount; i++)
            {
                var refObjectToken = refIdsArray[i];
                long rid;
                if (version == 1)
                {
                    var ridTok = refObjectToken["rid"];
                    rid = ridTok != null ? (long)ridTok : i;
                    if (rid != i)
                        throw new Exception($"ManagedReferencesRegistry v1: rid должны идти подряд 0…n-1; ожидалось {i}, в JSON {rid}.");
                }
                else
                {
                    var ridTok = refObjectToken["rid"];
                    if (ridTok == null)
                        throw new Exception("ManagedReferencesRegistry v2: у элемента RefIds нет поля rid.");
                    rid = (long)ridTok;
                    writer.Write(rid);
                }

                var typeToken = refObjectToken["type"];
                if (typeToken == null || typeToken.Type != JTokenType.Object)
                    throw new Exception("ManagedReferencesRegistry: у элемента RefIds нет объекта type.");

                var typeRef = new AssetTypeReference
                {
                    ClassName = typeToken["class"]?.Value<string>() ?? string.Empty,
                    Namespace = typeToken["ns"]?.Value<string>() ?? string.Empty,
                    AsmName = typeToken["asm"]?.Value<string>() ?? string.Empty
                };

                var dataToken = refObjectToken["data"];
                if (dataToken == null && (typeRef.ClassName != string.Empty || typeRef.Namespace != string.Empty || typeRef.AsmName != string.Empty))
                    throw new Exception("ManagedReferencesRegistry: нет поля data у элемента RefIds.");

                typeRef.WriteAsset(writer);
                if (typeRef.ClassName == string.Empty && typeRef.Namespace == string.Empty && typeRef.AsmName == string.Empty)
                    continue;

                var objectTempField = refMan.GetTemplateField(typeRef);
                if (objectTempField == null)
                    throw new Exception(
                        "ManagedReferencesRegistry: не удалось получить шаблон типа «" + typeRef.ClassName + "» (" +
                        typeRef.Namespace + " в " + typeRef.AsmName + "). Проверьте Managed/*.dll и classdata.tpk.");

                RecurseJsonImport(writer, objectTempField, dataToken, refMan);
            }

            if (version == 1)
                AssetTypeReference.TERMINUS.WriteAsset(writer);
            else
                writer.Align();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Безопасная замена текста: точечный splice строк в оригинальных байтах объекта
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>true</c>, если строки JSON совпадают с состоянием объекта (текст не меняли). Сравниваются ТОЛЬКО строки —
        /// числовые поля игнорируются (различия сериализации не дают ложного «изменилось»). При сомнении → <c>false</c>.
        /// </summary>
        private static bool IsJsonUnchangedAgainstAsset(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            AssetFileInfo info,
            string jsonText,
            string managedAssembliesFolder)
        {
            try
            {
                var current = UabeaJsonAssetExporter.TryDumpCurrentAssetJson(manager, fileInst, info, managedAssembliesFolder);
                if (current == null)
                    return false;
                var onDisk = JToken.Parse(jsonText);

                var fromDisk = new List<string>();
                CollectNonEmptyStringLeaves(onDisk, fromDisk);
                var fromAsset = new List<string>();
                CollectNonEmptyStringLeaves(current, fromAsset);

                if (fromDisk.Count != fromAsset.Count)
                    return false;
                fromDisk.Sort(StringComparer.Ordinal);
                fromAsset.Sort(StringComparer.Ordinal);
                for (var i = 0; i < fromDisk.Count; i++)
                    if (!string.Equals(fromDisk[i], fromAsset[i], StringComparison.Ordinal))
                        return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Собирает все непустые строковые листья JSON-дерева (для сравнения «менялся ли текст»).</summary>
        private static void CollectNonEmptyStringLeaves(JToken token, List<string> into)
        {
            if (token == null)
                return;
            switch (token.Type)
            {
                case JTokenType.String:
                    var s = token.Value<string>();
                    if (!string.IsNullOrWhiteSpace(s))
                        into.Add(s);
                    break;
                case JTokenType.Object:
                    foreach (var prop in ((JObject)token).Properties())
                        CollectNonEmptyStringLeaves(prop.Value, into);
                    break;
                case JTokenType.Array:
                    foreach (var item in (JArray)token)
                        CollectNonEmptyStringLeaves(item, into);
                    break;
            }
        }

        /// <summary>
        /// Заменяет ТОЛЬКО изменённые строки прямо в оригинальных байтах (len-префикс + UTF8 + align 4); не-строковые поля
        /// остаются байт-в-байт → камеры/свет не ломаются. Старые значения читает экспортёр, позиции ищет в сырых байтах.
        /// <c>null</c>, если хоть одну строку не нашли надёжно (тогда вызывающий код пробует пересборку по шаблону).
        /// </summary>
        private static byte[] TryBuildStringSplicedRawBytes(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            AssetFileInfo info,
            string jsonText,
            string managedAssembliesFolder,
            UabeaImportResult result,
            string fileLabel)
        {
            try
            {
                // Старые строки (как в ассете) и новые (из отредактированного JSON), в одном порядке обхода.
                var current = UabeaJsonAssetExporter.TryDumpCurrentAssetJson(manager, fileInst, info, managedAssembliesFolder);
                if (current == null)
                    return null;
                var edited = JToken.Parse(jsonText);

                var oldStrings = new List<string>();
                CollectAllStringLeaves(current, oldStrings);
                var newStrings = new List<string>();
                CollectAllStringLeaves(edited, newStrings);

                // Структура должна совпадать (JSON = тот же экспорт, у которого поменяли только значения строк).
                if (oldStrings.Count != newStrings.Count)
                    return null;

                // Список реально изменённых пар (непустая старая строка, которую можно найти в байтах).
                var changes = new List<(string Old, string New)>();
                for (var i = 0; i < oldStrings.Count; i++)
                {
                    if (string.Equals(oldStrings[i], newStrings[i], StringComparison.Ordinal))
                        continue;
                    if (string.IsNullOrEmpty(oldStrings[i]))
                        return null; // пустую старую строку в байтах не найти однозначно
                    changes.Add((oldStrings[i], newStrings[i]));
                }

                if (changes.Count == 0)
                    return null; // нечего менять (сюда не дойдём — но на всякий случай)

                // Читаем оригинальные сырые байты объекта.
                var raw = ReadOriginalAssetBytes(fileInst, info);
                if (raw == null || raw.Length == 0)
                    return null;

                var bigEndian = fileInst.file.Reader.BigEndian;
                var working = raw;

                foreach (var ch in changes)
                {
                    var oldEnc = EncodeUnityString(ch.Old, bigEndian, withAlignPad: false, out var oldLen);
                    var idx = IndexOfBytes(working, oldEnc, 0);
                    if (idx < 0)
                        return null; // не нашли — небезопасно, fallback
                    // Уникальность: если таких вхождений несколько, не рискуем (fallback).
                    if (IndexOfBytes(working, oldEnc, idx + 1) >= 0)
                        return null;

                    var oldPad = (4 - (oldLen & 3)) & 3;
                    var newEnc = EncodeUnityString(ch.New, bigEndian, withAlignPad: true, out _);

                    var tailStart = idx + oldEnc.Length + oldPad;
                    if (tailStart > working.Length)
                        return null;

                    var spliced = new byte[idx + newEnc.Length + (working.Length - tailStart)];
                    Buffer.BlockCopy(working, 0, spliced, 0, idx);
                    Buffer.BlockCopy(newEnc, 0, spliced, idx, newEnc.Length);
                    Buffer.BlockCopy(working, tailStart, spliced, idx + newEnc.Length, working.Length - tailStart);
                    working = spliced;
                }

                if (result != null && result.Messages.Count < 200)
                    result.Messages.Add(
                        $"{fileLabel}: заменено строк точечно: {changes.Count} (не-строковые поля сохранены байт-в-байт).");

                return working;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Сырые байты объекта из исходного файла (оригинал, до SetNewData).</summary>
        private static byte[] ReadOriginalAssetBytes(AssetsFileInstance fileInst, AssetFileInfo info)
        {
            try
            {
                var reader = fileInst.file.Reader;
                reader.Position = fileInst.file.Header.DataOffset + info.ByteOffset;
                return reader.ReadBytes(checked((int)info.ByteSize));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Кодирует строку как Unity: int32-длина (с порядком байт) + UTF8; при <paramref name="withAlignPad"/> — нули до кратности 4.</summary>
        private static byte[] EncodeUnityString(string s, bool bigEndian, bool withAlignPad, out int utf8Len)
        {
            var utf8 = Encoding.UTF8.GetBytes(s ?? string.Empty);
            utf8Len = utf8.Length;
            var lenBytes = BitConverter.GetBytes(utf8.Length);
            if (bigEndian)
                Array.Reverse(lenBytes);

            var pad = withAlignPad ? ((4 - (utf8.Length & 3)) & 3) : 0;
            var outBytes = new byte[4 + utf8.Length + pad];
            Buffer.BlockCopy(lenBytes, 0, outBytes, 0, 4);
            Buffer.BlockCopy(utf8, 0, outBytes, 4, utf8.Length);
            return outBytes;
        }

        /// <summary>Индекс первого вхождения <paramref name="pattern"/> в <paramref name="data"/> начиная с <paramref name="start"/>; -1 если нет.</summary>
        private static int IndexOfBytes(byte[] data, byte[] pattern, int start)
        {
            if (data == null || pattern == null || pattern.Length == 0 || data.Length < pattern.Length)
                return -1;
            var last = data.Length - pattern.Length;
            for (var i = Math.Max(0, start); i <= last; i++)
            {
                var ok = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { ok = false; break; }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        /// <summary>Собирает ВСЕ строковые листья (включая пустые) в порядке обхода — для пар старое/новое.</summary>
        private static void CollectAllStringLeaves(JToken token, List<string> into)
        {
            if (token == null)
                return;
            switch (token.Type)
            {
                case JTokenType.String:
                    into.Add(token.Value<string>() ?? string.Empty);
                    break;
                case JTokenType.Object:
                    foreach (var prop in ((JObject)token).Properties())
                        CollectAllStringLeaves(prop.Value, into);
                    break;
                case JTokenType.Array:
                    foreach (var item in (JArray)token)
                        CollectAllStringLeaves(item, into);
                    break;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
    }

    internal class UabeaImportResult
    {
        public int JsonFound { get; set; }
        public int Imported { get; set; }
        /// <summary>Патч localization-locales: сколько ассетов Locale совпало с запрошенным кодом.</summary>
        public int LocaleMatchCount { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        /// <summary>Сколько файлов побочников (.resS / .resource) скопировано рядом с выходным контейнером.</summary>
        public int CompanionResourceFilesCopied { get; set; }
        public List<string> Messages { get; } = new List<string>();
    }
}
