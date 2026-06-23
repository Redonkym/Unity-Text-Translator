using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityTextTranslator
{
    /// <summary>Экспорт объектов из Unity .assets в JSON в стиле UABEA/UABEANext (совместимо с <see cref="UabeaJsonAssetImporter"/>).</summary>
    internal static class UabeaJsonAssetExporter
    {
        public static UabeaExportResult ExportToFolder(string assetsPath, string outputFolder, bool monoBehaviourOnly, string gameDataRootForPreload = null, UabeaJsonFileLayout fileLayout = UabeaJsonFileLayout.UabeaMonoScriptNameFlat, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(assetsPath) || !File.Exists(assetsPath))
                throw new FileNotFoundException("Файл контейнера Unity не найден.", assetsPath);

            if (string.IsNullOrWhiteSpace(outputFolder))
                throw new ArgumentException("Укажите папку для экспорта.");

            Directory.CreateDirectory(outputFolder);

            var result = new UabeaExportResult();
            var manager = new AssetsManager
            {
                UseQuickLookup = true,
                UseTemplateFieldCache = true,
                UseMonoTemplateFieldCache = true,
                UseRefTypeManagerCache = true
            };

            // Резолвим каталог *_Data и предзагружаем всё дерево .assets (как контекст в UABEA Next).
            var resolvedRoot = !string.IsNullOrWhiteSpace(gameDataRootForPreload)
                ? UnityAssetsGameFolderHelper.ResolveGameDataFolder(gameDataRootForPreload)
                : UnityAssetsGameFolderHelper.ResolveGameDataFolder(Path.GetDirectoryName(assetsPath));

            if (!string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot))
            {
                var msgN = result.Messages.Count;
                if (UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, resolvedRoot, out var managedFolder, result.Messages))
                {
                    result.ManagedAssembliesFolder = managedFolder;
                }
                else if (result.Messages.Count == msgN)
                    result.Messages.Add(UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(resolvedRoot));

                var satelliteDiag = UnityAssetsGameFolderHelper.GetMonoCecilSatelliteAssemblyDiagnosticOrNull();
                if (!string.IsNullOrEmpty(satelliteDiag))
                    result.Messages.Add(satelliteDiag);

                UnityAssetsGameFolderHelper.PreloadAllAssetsFromDataFolder(manager, resolvedRoot);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fileInst = UnityAssetsGameFolderHelper.GetOrLoadPrimaryAssetsFile(manager, assetsPath);
            try
            {
                UabeaJsonAssetImporter.TryLoadClassPackage(manager, fileInst);

                var assetsBase = UabeaJsonPaths.SafeFileNamePart(Path.GetFileNameWithoutExtension(assetsPath))
                    .TrimEnd('-', '_', ' ');
                // При monoBehaviourOnly без TextAsset пропадает типичный игровой текст в .assets (CSV/JSON в TextAsset).
                ExportAssetInfosFromFile(manager, fileInst, outputFolder, monoBehaviourOnly, assetsBase, fileLayout, result, cancellationToken, includeTextAssetsWhenMonoFiltered: true);
                result.AssetFilesScanned = 1;
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }

            return result;
        }

        /// <summary>
        /// Предзагрузка <c>Name_Data</c> и экспорт каждого <c>.assets</c> в общую папку JSON; раскладку имён задаёт <paramref name="fileLayout"/>.
        /// При <paramref name="skipGlobalGameManagersAssets"/> globalgamemanagers*.assets пропускаются (обычно не нужны для UI).
        /// </summary>
        public static UabeaExportResult ExportEntireGameDataFolder(string gameDataFolder, string outputFolder, bool monoBehaviourOnly, UabeaJsonFileLayout fileLayout = UabeaJsonFileLayout.UabeaMonoScriptNameFlat, bool skipGlobalGameManagersAssets = true, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputFolder))
                throw new ArgumentException("Укажите папку для экспорта.");

            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(gameDataFolder);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                throw new DirectoryNotFoundException("Каталог данных игры не найден.");

            if (UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved).Count == 0)
                throw new InvalidOperationException("В каталоге не найдено ни одного файла .assets.");

            Directory.CreateDirectory(outputFolder);

            var result = new UabeaExportResult();
            var manager = new AssetsManager
            {
                UseQuickLookup = true,
                UseTemplateFieldCache = true,
                UseMonoTemplateFieldCache = true,
                UseRefTypeManagerCache = true
            };

            try
            {
                var msgN = result.Messages.Count;
                if (UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, resolved, out var managedFolder, result.Messages))
                {
                    result.ManagedAssembliesFolder = managedFolder;
                }
                else if (result.Messages.Count == msgN)
                    result.Messages.Add(UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(resolved));

                var satelliteDiag = UnityAssetsGameFolderHelper.GetMonoCecilSatelliteAssemblyDiagnosticOrNull();
                if (!string.IsNullOrEmpty(satelliteDiag))
                    result.Messages.Add(satelliteDiag);

                UnityAssetsGameFolderHelper.PreloadAllAssetsFromDataFolder(manager, resolved);

                cancellationToken.ThrowIfCancellationRequested();

                AssetsFileInstance first = null;
                foreach (AssetsFileInstance f in manager.Files)
                {
                    first = f;
                    break;
                }

                if (first == null)
                    throw new InvalidOperationException("Не удалось загрузить ни одного ресурса .assets.");

                var unityVersion = first.file.Metadata.UnityVersion ?? string.Empty;
                result.UnityVersion = unityVersion;

                UabeaJsonAssetImporter.TryLoadClassPackage(manager, first);
                result.ClassDatabaseLoaded = manager.ClassDatabase != null;
                if (!result.ClassDatabaseLoaded)
                {
                    result.Messages.Add(
                        "Class database не загружена: classdata.tpk либо отсутствует, либо в нём нет данных для версии Unity " +
                        unityVersion +
                        ". MonoBehaviour будут экспортированы только с базовыми полями.");
                }

                var snapshot = new List<AssetsFileInstance>();
                foreach (AssetsFileInstance f in manager.Files)
                    snapshot.Add(f);

                snapshot.Sort((a, b) => string.Compare(
                    UnityAssetsGameFolderHelper.GetAssetsFileInstancePath(a) ?? "",
                    UnityAssetsGameFolderHelper.GetAssetsFileInstancePath(b) ?? "",
                    StringComparison.OrdinalIgnoreCase));

                foreach (var fileInst in snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = UnityAssetsGameFolderHelper.GetAssetsFileInstancePath(fileInst);
                    if (string.IsNullOrEmpty(path) || !UnityAssetsGameFolderHelper.IsLikelyAssetsFile(path))
                        continue;

                    var containerBase = Path.GetFileNameWithoutExtension(path);
                    if (skipGlobalGameManagersAssets &&
                        containerBase.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.AssetFilesScanned++;
                    var assetsBase = UabeaJsonPaths.SafeFileNamePart(containerBase).TrimEnd('-', '_', ' ');
                    // При monoBehaviourOnly без TextAsset пропадает типичный игровой текст в .assets (CSV/JSON в TextAsset).
                    ExportAssetInfosFromFile(manager, fileInst, outputFolder, monoBehaviourOnly, assetsBase, fileLayout, result, cancellationToken, includeTextAssetsWhenMonoFiltered: true);
                }
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }

            return result;
        }

        internal static void ExportAssetsFileToJsonFolder(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            string outputFolder,
            bool monoBehaviourOnly,
            string assetsBase,
            UabeaJsonFileLayout fileLayout,
            UabeaExportResult result,
            CancellationToken cancellationToken = default,
            bool includeTextAssetsWhenMonoFiltered = false)
        {
            ExportAssetInfosFromFile(manager, fileInst, outputFolder, monoBehaviourOnly, assetsBase, fileLayout, result, cancellationToken, includeTextAssetsWhenMonoFiltered);
        }

        private static void ExportAssetInfosFromFile(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            string outputFolder,
            bool monoBehaviourOnly,
            string assetsBase,
            UabeaJsonFileLayout fileLayout,
            UabeaExportResult result,
            CancellationToken cancellationToken = default,
            bool includeTextAssetsWhenMonoFiltered = false)
        {
            AssetReadFlags baseFlags = AssetReadFlags.None;
            if ((int)fileInst.file.Metadata.TargetPlatform < 0)
                baseFlags |= AssetReadFlags.PreferEditor;

            var tag = $"[{assetsBase}] ";

            foreach (AssetFileInfo info in fileInst.file.AssetInfos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (info.Stripped != 0)
                    continue;

                var typeId = info.GetTypeId(fileInst.file);
                var isMonoBehaviourType = typeId == (int)AssetClassID.MonoBehaviour;
                var isTextAssetType = typeId == (int)AssetClassID.TextAsset;

                if (monoBehaviourOnly &&
                    !isMonoBehaviourType &&
                    !(includeTextAssetsWhenMonoFiltered && isTextAssetType))
                    continue;

                result.TotalCandidates++;

                // ForceFromCldb только если type tree недоступен: при TypeTreeEnabled он подавляет встроенное дерево и
                // оставляет лишь базовые поля MonoBehaviour — потому из Addressables/Localization бандлов не видно строк.
                var perInfoFlags = baseFlags;
                var fileHasTypeTree = fileInst.file.Metadata.TypeTreeEnabled;
                if (isMonoBehaviourType && manager.ClassDatabase != null && !fileHasTypeTree)
                    perInfoFlags |= AssetReadFlags.ForceFromCldb;

                AssetTypeValueField baseField;
                bool augmentedManually = false;
                try
                {
                    baseField = TryGetBaseField(manager, fileInst, info, perInfoFlags);

                    if (isMonoBehaviourType
                        && baseField != null
                        && manager.MonoTempGenerator != null
                        && !HasMonoBehaviourScriptFields(baseField))
                    {
                        if (TryAugmentMonoBehaviourBaseField(manager, fileInst, info, baseField, result, out var augmented))
                        {
                            baseField = augmented;
                            augmentedManually = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var raw = TryReadRawAssetBytes(fileInst, info, maxBytes: 512 * 1024);
                    if (raw.Length > 0)
                    {
                        try
                        {
                            var fallback = BuildFallbackRawJson(info, fileInst, raw, ex.Message);
                            var outPath = UabeaJsonPaths.GetExportJsonFullPath(fileLayout, outputFolder, assetsBase, info, fileInst);
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? outputFolder);
                            File.WriteAllText(outPath, fallback.ToString(Formatting.Indented), new UTF8Encoding(false));
                            result.Exported++;
                            if (result.Messages.Count < 24)
                                result.Messages.Add($"{tag}PathID {info.PathId}: JSON со сырыми байтами (разбор не удался: {ex.Message})");
                        }
                        catch (Exception ioEx)
                        {
                            result.Failed++;
                            if (result.Messages.Count < 24)
                                result.Messages.Add($"{tag}PathID {info.PathId}: чтение ({ex.Message}); запись fallback ({ioEx.Message})");
                        }
                    }
                    else
                    {
                        result.Failed++;
                        if (result.Messages.Count < 24)
                            result.Messages.Add($"{tag}PathID {info.PathId}: чтение ({ex.Message})");
                    }

                    continue;
                }

                if (baseField == null)
                {
                    result.Failed++;
                    if (result.Messages.Count < 24)
                        result.Messages.Add($"{tag}PathID {info.PathId}: пустое дерево полей.");
                    continue;
                }

                try
                {
                    var isMonoBehaviour = isMonoBehaviourType;
                    string monoScriptShortName = TryResolveMonoExportScriptName(fileLayout, manager, fileInst, info, baseField, perInfoFlags);

                    if (isMonoBehaviour)
                    {
                        if (HasMonoBehaviourScriptFields(baseField))
                            result.MonoBehavioursWithScriptFields++;
                        else
                            result.MonoBehavioursBaseOnly++;

                        if (augmentedManually)
                            result.MonoBehavioursAugmentedManually++;

                        if (!string.IsNullOrWhiteSpace(monoScriptShortName))
                        {
                            if (!result.MonoBehaviourClassCounts.TryGetValue(monoScriptShortName, out var count))
                                count = 0;
                            result.MonoBehaviourClassCounts[monoScriptShortName] = count + 1;
                        }
                    }

                    var json = RecurseJsonDump(baseField, false);
                    var outPath = UabeaJsonPaths.GetExportJsonFullPath(fileLayout, outputFolder, assetsBase, info, fileInst, monoScriptShortName);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? outputFolder);
                    File.WriteAllText(outPath, json.ToString(Formatting.Indented), new UTF8Encoding(false));
                    result.Exported++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    if (result.Messages.Count < 24)
                        result.Messages.Add($"{tag}PathID {info.PathId}: JSON ({ex.Message})");
                }
            }
        }

        /// <summary>MonoBehaviour только с базовыми полями → собираем шаблон через <see cref="AssetsManager.MonoTempGenerator"/> и перечитываем значение.</summary>
        private static bool TryAugmentMonoBehaviourBaseField(
            AssetsManager manager,
            AssetsFileInstance fileInst,
            AssetFileInfo info,
            AssetTypeValueField currentBase,
            UabeaExportResult result,
            out AssetTypeValueField augmented)
        {
            augmented = currentBase;

            void Diag(string stage, string detail)
            {
                if (result == null) return;
                if (result.MonoAugmentDiagnostics.Count >= 12) return;
                result.MonoAugmentDiagnostics.Add($"[PathID {info.PathId}] {stage}: {detail}");
            }

            try
            {
                var scriptField = currentBase?["m_Script"];
                if (scriptField == null || scriptField.IsDummy)
                {
                    Diag("m_Script", "поле отсутствует или dummy");
                    return false;
                }

                AssetExternal scriptExt;
                try
                {
                    scriptExt = manager.GetExtAsset(fileInst, scriptField, false, AssetReadFlags.SkipMonoBehaviourFields);
                }
                catch (Exception ex)
                {
                    Diag("GetExtAsset(m_Script)", ex.GetType().Name + ": " + ex.Message);
                    return false;
                }

                if (scriptExt.baseField == null)
                {
                    Diag("GetExtAsset(m_Script)", "baseField == null (не загружен файл MonoScript)");
                    return false;
                }

                var assemblyName = scriptExt.baseField["m_AssemblyName"]?.AsString;
                var nameSpace = scriptExt.baseField["m_Namespace"]?.AsString;
                var className = scriptExt.baseField["m_ClassName"]?.AsString;

                if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(className))
                {
                    Diag("MonoScript", $"asm='{assemblyName}', ns='{nameSpace}', class='{className}' — пустые значения");
                    return false;
                }

                var assemblyDll = assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? assemblyName
                    : assemblyName + ".dll";
                var dllPath = Path.Combine(result?.ManagedAssembliesFolder ?? "", assemblyDll);
                var dllExists = !string.IsNullOrEmpty(result?.ManagedAssembliesFolder) && File.Exists(dllPath);

                if (!dllExists)
                {
                    Diag("Managed DLL", $"не найден '{assemblyDll}' в папке Managed для класса {nameSpace}.{className}");
                    return false;
                }

                if (assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    assemblyName = assemblyName.Substring(0, assemblyName.Length - 4);

                var unityVersion = new UnityVersion(fileInst.file.Metadata.UnityVersion);

                AssetTypeTemplateField mbBaseTemplate;
                try
                {
                    mbBaseTemplate = manager.GetTemplateBaseField(fileInst, info, AssetReadFlags.SkipMonoBehaviourFields);
                }
                catch (Exception ex)
                {
                    Diag("GetTemplateBaseField(SkipMono)", ex.GetType().Name + ": " + ex.Message);
                    mbBaseTemplate = null;
                }

                if (mbBaseTemplate == null)
                {
                    Diag("MonoBaseTemplate", "null после GetTemplateBaseField");
                    return false;
                }

                AssetTypeTemplateField generated = null;
                try
                {
                    generated = manager.MonoTempGenerator.GetTemplateField(
                        mbBaseTemplate, assemblyName, nameSpace ?? string.Empty, className, unityVersion);
                }
                catch (Exception ex)
                {
                    Diag("MonoTempGenerator.GetTemplateField", $"{ex.GetType().Name} для {nameSpace}.{className} ({assemblyDll}): {ex.Message}");
                    return false;
                }

                if (generated == null)
                {
                    Diag("MonoTempGenerator.GetTemplateField", $"null для {nameSpace}.{className} ({assemblyDll})");
                    return false;
                }

                AssetTypeValueField rebuilt;
                try
                {
                    using (var ms = new MemoryStream((int)info.ByteSize))
                    {
                        var src = fileInst.file.Reader;
                        lock (fileInst.LockReader)
                        {
                            src.Position = info.GetAbsoluteByteOffset(fileInst.file);
                            var buf = src.ReadBytes((int)info.ByteSize);
                            ms.Write(buf, 0, buf.Length);
                        }
                        ms.Position = 0;
                        rebuilt = generated.MakeValue(new AssetsFileReader(ms));
                    }
                }
                catch (Exception ex)
                {
                    Diag("MakeValue", ex.GetType().Name + ": " + ex.Message);
                    return false;
                }

                if (rebuilt == null || !HasMonoBehaviourScriptFields(rebuilt))
                {
                    // Часть типов в IL без сериализуемых полей или без нужной информации — не засоряем лог по PathID.
                    if (result != null)
                        result.MonoAugmentRebuildStillBare++;
                    return false;
                }

                augmented = rebuilt;
                return true;
            }
            catch (Exception ex)
            {
                Diag("общее", ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string TryResolveMonoExportScriptName(
            UabeaJsonFileLayout layout,
            AssetsManager manager,
            AssetsFileInstance fileInst,
            AssetFileInfo info,
            AssetTypeValueField monoBehaviourRoot,
            AssetReadFlags baseFlags)
        {
            if (layout != UabeaJsonFileLayout.UabeaMonoScriptNameFlat)
                return null;

            if (info.GetTypeId(fileInst.file) != (int)AssetClassID.MonoBehaviour)
                return null;

            var first = MonoBehaviourScriptResolver.TryGetMonoScriptShortClassName(manager, fileInst, monoBehaviourRoot, baseFlags);
            if (!string.IsNullOrWhiteSpace(first))
                return first;

            foreach (var extra in new[]
                     {
                         AssetReadFlags.ForceFromCldb,
                         AssetReadFlags.SkipMonoBehaviourFields,
                         AssetReadFlags.ForceFromCldb | AssetReadFlags.SkipMonoBehaviourFields
                     })
            {
                var n = MonoBehaviourScriptResolver.TryGetMonoScriptShortClassName(manager, fileInst, monoBehaviourRoot, baseFlags | extra);
                if (!string.IsNullOrWhiteSpace(n))
                    return n;
            }

            return null;
        }

        /// <summary>
        /// Читает baseField ТОЧНО как экспорт (те же флаги + augmentation через Mono.Cecil). Критично для импортёра:
        /// набор/порядок строк в дампе должен совпасть с экспортированным JSON, чтобы точечная замена нашла поля.
        /// </summary>
        internal static AssetTypeValueField ReadBaseFieldLikeExport(
            AssetsManager manager, AssetsFileInstance fileInst, AssetFileInfo info, string managedAssembliesFolder)
        {
            AssetReadFlags baseFlags = AssetReadFlags.None;
            if ((int)fileInst.file.Metadata.TargetPlatform < 0)
                baseFlags |= AssetReadFlags.PreferEditor;

            var isMonoBehaviour = info.GetTypeId(fileInst.file) == (int)AssetClassID.MonoBehaviour;
            var perInfoFlags = baseFlags;
            if (isMonoBehaviour && manager.ClassDatabase != null && !fileInst.file.Metadata.TypeTreeEnabled)
                perInfoFlags |= AssetReadFlags.ForceFromCldb;

            var baseField = TryGetBaseField(manager, fileInst, info, perInfoFlags);
            if (isMonoBehaviour
                && baseField != null
                && manager.MonoTempGenerator != null
                && !HasMonoBehaviourScriptFields(baseField))
            {
                var tmp = new UabeaExportResult { ManagedAssembliesFolder = managedAssembliesFolder };
                if (TryAugmentMonoBehaviourBaseField(manager, fileInst, info, baseField, tmp, out var augmented))
                    baseField = augmented;
            }

            return baseField;
        }

        /// <summary>Дамп текущего состояния объекта в JSON тем же кодом, что экспорт (с augmentation) — импортёр сравнивает «менялся ли текст» и делает splice.</summary>
        internal static JToken TryDumpCurrentAssetJson(AssetsManager manager, AssetsFileInstance fileInst, AssetFileInfo info, string managedAssembliesFolder)
        {
            try
            {
                var bf = ReadBaseFieldLikeExport(manager, fileInst, info, managedAssembliesFolder);
                if (bf == null)
                    return null;
                return RecurseJsonDump(bf, false);
            }
            catch
            {
                return null;
            }
        }

        private static AssetTypeValueField TryGetBaseField(AssetsManager manager, AssetsFileInstance fileInst, AssetFileInfo info, AssetReadFlags baseFlags)
        {
            Exception last = null;

            foreach (var flags in EnumerateReadFlagAttempts(baseFlags))
            {
                try
                {
                    return manager.GetBaseField(fileInst, info, flags);
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                try
                {
                    return manager.GetBaseField(fileInst, info.PathId, flags);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new InvalidOperationException("GetBaseField не вернул результат.");
        }

        private static IEnumerable<AssetReadFlags> EnumerateReadFlagAttempts(AssetReadFlags baseFlags)
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

        private static byte[] TryReadRawAssetBytes(AssetsFileInstance fileInst, AssetFileInfo info, int maxBytes)
        {
            try
            {
                var reader = fileInst.file.Reader;
                long pos = info.GetAbsoluteByteOffset(fileInst.file);
                int len = (int)Math.Min((ulong)info.ByteSize, (ulong)Math.Max(0, maxBytes));
                if (len <= 0)
                    return Array.Empty<byte>();

                reader.BaseStream.Position = pos;
                return reader.ReadBytes(len);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static JObject BuildFallbackRawJson(AssetFileInfo info, AssetsFileInstance fileInst, byte[] raw, string parseError)
        {
            var truncated = info.ByteSize > (uint)raw.Length;
            return new JObject
            {
                ["_unityTextTranslator_rawFallback"] = true,
                ["pathId"] = info.PathId,
                ["typeId"] = info.GetTypeId(fileInst.file),
                ["byteSize"] = info.ByteSize,
                ["truncated"] = truncated,
                ["parseError"] = parseError ?? "",
                ["rawBytesBase64"] = Convert.ToBase64String(raw)
            };
        }

        /// <summary>Логика как в UABEANext <c>AssetExport.RecurseJsonDump</c>.</summary>
        private static JToken RecurseJsonDump(AssetTypeValueField field, bool uabeFlavor)
        {
            var template = field.TemplateField;
            var isArray = template.IsArray;

            if (isArray)
            {
                var jArray = new JArray();
                if (template.ValueType != AssetValueType.ByteArray)
                {
                    for (int i = 0; i < field.Children.Count; i++)
                        jArray.Add(RecurseJsonDump(field.Children[i], uabeFlavor));
                }
                else
                {
                    var byteArrayData = field.AsByteArray;
                    for (int i = 0; i < byteArrayData.Length; i++)
                        jArray.Add(byteArrayData[i]);
                }

                return jArray;
            }

                if (field.Value != null)
                {
                    var valueType = field.Value.ValueType;

                    if (field.Value.ValueType != AssetValueType.ManagedReferencesRegistry)
                    {
                        object value;
                        switch (valueType)
                        {
                            case AssetValueType.Bool:
                                value = field.AsBool;
                                break;
                            case AssetValueType.Int8:
                            case AssetValueType.Int16:
                            case AssetValueType.Int32:
                                value = field.AsInt;
                                break;
                            case AssetValueType.Int64:
                                value = field.AsLong;
                                break;
                            case AssetValueType.UInt8:
                            case AssetValueType.UInt16:
                            case AssetValueType.UInt32:
                                value = field.AsUInt;
                                break;
                            case AssetValueType.UInt64:
                                value = field.AsULong;
                                break;
                            case AssetValueType.String:
                                value = field.AsString;
                                break;
                            case AssetValueType.Float:
                                value = field.AsFloat;
                                break;
                            case AssetValueType.Double:
                                value = field.AsDouble;
                                break;
                            default:
                                value = "invalid value";
                                break;
                        }

                        return JToken.FromObject(value);
                    }

                    return JsonDumpManagedReferencesRegistry(field, uabeFlavor);
                }

            var jObject = new JObject();
            foreach (AssetTypeValueField child in field)
                jObject.Add(child.FieldName, RecurseJsonDump(child, uabeFlavor));

            return jObject;
        }

        private static JObject JsonDumpManagedReferencesRegistry(AssetTypeValueField field, bool uabeFlavor = false)
        {
            var registry = field.Value.AsManagedReferencesRegistry;

            if (registry.version >= 1 && registry.version <= 2)
            {
                var jArrayRefs = new JArray();
                foreach (var refObj in registry.references)
                {
                    var typeRef = refObj.type;

                    var jObjManagedType = new JObject
                    {
                        { "class", typeRef.ClassName },
                        { "ns", typeRef.Namespace },
                        { "asm", typeRef.AsmName }
                    };

                    var jObjData = new JObject();
                    foreach (var child in refObj.data)
                        jObjData.Add(child.FieldName, RecurseJsonDump(child, uabeFlavor));

                    JObject jObjRefObject;
                    if (registry.version == 1)
                    {
                        jObjRefObject = new JObject
                        {
                            { "type", jObjManagedType },
                            { "data", jObjData }
                        };
                    }
                    else
                    {
                        jObjRefObject = new JObject
                        {
                            { "rid", refObj.rid },
                            { "type", jObjManagedType },
                            { "data", jObjData }
                        };
                    }

                    jArrayRefs.Add(jObjRefObject);
                }

                return new JObject
                {
                    { "version", registry.version },
                    { "RefIds", jArrayRefs }
                };
            }

            throw new NotSupportedException($"ManagedReferencesRegistry версии {registry.version} не поддерживается.");
        }

        /// <summary>MonoBehaviour «полноценный», если есть поля сверх базовых <c>m_GameObject/m_Enabled/m_Script/m_Name</c>.</summary>
        private static bool HasMonoBehaviourScriptFields(AssetTypeValueField root)
        {
            if (root == null)
                return false;

            try
            {
                int extra = 0;
                foreach (AssetTypeValueField child in root)
                {
                    var n = child.FieldName;
                    if (string.IsNullOrEmpty(n))
                        continue;
                    if (n == "m_GameObject" || n == "m_Enabled" || n == "m_Script" || n == "m_Name")
                        continue;
                    extra++;
                    if (extra > 0)
                        return true;
                }
            }
            catch
            {
                // если перечисление падает — считаем, что поля скрипта недоступны
            }

            return false;
        }
    }

    internal class UabeaExportResult
    {
        public string ManagedAssembliesFolder { get; set; }
        public string UnityVersion { get; set; }
        public bool ClassDatabaseLoaded { get; set; }
        public int AssetFilesScanned { get; set; }
        public int TotalCandidates { get; set; }
        public int Exported { get; set; }
        public int Failed { get; set; }
        public int MonoBehavioursWithScriptFields { get; set; }
        public int MonoBehavioursBaseOnly { get; set; }
        public int MonoBehavioursAugmentedManually { get; set; }
        public Dictionary<string, int> MonoBehaviourClassCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<string> MonoAugmentDiagnostics { get; } = new List<string>();
        /// <summary>Счётчик случаев, когда Cecil построил шаблон, но полей скрипта так и не появилось (типично для части компонентов).</summary>
        public int MonoAugmentRebuildStillBare { get; set; }
        public List<string> Messages { get; } = new List<string>();
    }
}
