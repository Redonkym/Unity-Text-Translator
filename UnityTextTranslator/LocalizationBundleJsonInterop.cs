using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnityTextTranslator
{
    /// <summary>Экспорт/сборка Unity Asset Bundle (.bundle) ↔ JSON в стиле UABEA; типично Addressables (localization-string-tables-*_assets_all.bundle).</summary>
    internal static class LocalizationBundleJsonInterop
    {
        /// <summary>Без кешей: после <c>SetNewData</c> кешированный <c>GetBaseField</c> вернул бы прежнее дерево — импорт счёл бы «тексты совпадают» и не патчил.</summary>
        private static AssetsManager CreateAssetsManagerForBundleMutation()
        {
            return new AssetsManager
            {
                UseQuickLookup = false,
                UseTemplateFieldCache = false,
                UseMonoTemplateFieldCache = false,
                UseRefTypeManagerCache = false
            };
        }

        /// <summary>Код локали из имени Addressables-бандла (<c>…russian(ru)…</c>/<c>…english_en…</c>) — для <c>m_LocaleId.m_Code</c> при импорте en→ru.</summary>
        internal static string TryInferStringTableLocaleCodeFromOutputBundlePath(string outputBundlePath)
        {
            if (string.IsNullOrWhiteSpace(outputBundlePath)) return null;
            var name = Path.GetFileNameWithoutExtension(outputBundlePath);
            if (string.IsNullOrEmpty(name)) return null;

            // localization-string-tables-russian(ru)_assets_all
            var mParen = Regex.Match(name, @"string-tables[-_].+?\(([a-z]{2,8})\)", RegexOptions.IgnoreCase);
            if (mParen.Success)
            {
                var code = mParen.Groups[1].Value.ToLowerInvariant();
                if (code.Length >= 2) return code;
            }

            // localization-string-tables-english_en_assets_all
            var mUnder = Regex.Match(
                name,
                @"string-tables[-_]([a-z0-9]+)_([a-z]{2})(?:_|\.|$)",
                RegexOptions.IgnoreCase);
            if (mUnder.Success)
            {
                var code = mUnder.Groups[2].Value.ToLowerInvariant();
                if (code.Length >= 2) return code;
            }

            return null;
        }

        /// <summary>Если исходный и запрошенный путь совпадают — пишем во временный файл, затем <see cref="CommitBundleToRequestedPath"/>.</summary>
        private static string GetBundleDiskWritePath(string sourcePath, string requestedOutput, ICollection<string> messages, string logPrefix)
        {
            var inF = Path.GetFullPath(sourcePath ?? "");
            var outF = Path.GetFullPath(requestedOutput ?? "");
            if (!inF.Equals(outF, StringComparison.OrdinalIgnoreCase))
                return outF;
            var temp = Path.Combine(Path.GetTempPath(), "utt-w-" + Guid.NewGuid().ToString("N") + ".bundle");
            messages?.Add(logPrefix + " Исходный и выходной путь совпадают — запись во временный файл, затем замена.");
            return temp;
        }

        private static void CommitBundleToRequestedPath(
            string requestedOutput,
            string diskWritePath,
            ICollection<string> messages = null,
            string logPrefix = "[Bundle]")
        {
            var want = Path.GetFullPath(requestedOutput ?? "");
            var wrote = Path.GetFullPath(diskWritePath ?? "");
            if (want.Equals(wrote, StringComparison.OrdinalIgnoreCase))
            {
                if (messages != null && File.Exists(want))
                    messages.Add(logPrefix + " Запись сразу в целевой файл (без подмены из temp). Размер: " +
                                   new FileInfo(want).Length + " байт.");
                return;
            }

            long srcLen = 0;
            try
            {
                if (!File.Exists(wrote))
                    throw new FileNotFoundException("Временный bundle после Pack не найден.", wrote);
                srcLen = new FileInfo(wrote).Length;
                File.Copy(wrote, want, overwrite: true);
                var dstLen = new FileInfo(want).Length;
                if (srcLen != dstLen)
                    messages?.Add(logPrefix + " ОШИБКА: после копирования размер не совпадает (источник " + srcLen +
                                  ", назначение " + dstLen + "). Файл игры мог не обновиться.");
                else if (messages != null)
                    messages.Add(logPrefix + " Файл заменён из временной копии. Размер: " + dstLen + " байт.");
            }
            catch (Exception ex)
            {
                messages?.Add(logPrefix + " Не удалось подменить bundle на месте: " + ex.Message);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(wrote))
                        File.Delete(wrote);
                }
                catch { }
            }
        }

        /// <summary>
        /// Якорь для поиска <c>*catalog*</c> рядом с <c>StreamingAssets\aa\…</c> — обязательно реальный путь к bundle (не temp из
        /// <see cref="GetBundleDiskWritePath"/>), иначе патч каталога ищет «aa» в %TEMP% и падает с кодом 2.
        /// </summary>
        private static string AddressablesCatalogAnchorPath(string preferredPath, string alternatePath)
        {
            var a = Path.GetFullPath(preferredPath ?? "");
            if (!string.IsNullOrEmpty(a) && File.Exists(a))
                return a;
            var b = Path.GetFullPath(alternatePath ?? "");
            if (!string.IsNullOrEmpty(b) && File.Exists(b))
                return b;
            return string.IsNullOrEmpty(a) ? b : a;
        }

        /// <summary>Иначе UnityFS остаётся открытым после LoadBundleFile и <see cref="CommitBundleToRequestedPath"/> не может File.Copy на тот же путь (IOException: занят).</summary>
        private static void ReleaseBundleManagerHandles(AssetsManager manager)
        {
            if (manager == null)
                return;
            try { manager.UnloadAllBundleFiles(); } catch { }
            try { manager.UnloadAllAssetsFiles(true); } catch { }
        }

        /// <summary>Из пути к stringtables с <c>english(en)</c> получает путь к тому же имени с <c>russian(ru)</c>.</summary>
        public static string TryDeriveRussianStringTablesBundlePathFromEnglish(string englishBundlePath)
        {
            if (string.IsNullOrWhiteSpace(englishBundlePath))
                return null;
            var full = Path.GetFullPath(englishBundlePath);
            const string enToken = "english(en)";
            var idx = full.IndexOf(enToken, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;
            return full.Substring(0, idx) + "russian(ru)" + full.Substring(idx + enToken.Length);
        }

        /// <summary>
        /// Клонирует пропатченный english UnityFS в russian(ru): <c>m_LocaleId.m_Code</c> «en»→«ru», <c>m_Name</c> суффикс <c>_en</c>/<c>_english</c>→<c>_ru</c>.
        /// Порядок: LoadBundleFile → LoadAssetsFileFromBundle → правки+SetNewData по ассету → CAB в слот → Write.
        /// </summary>
        public static UabeaImportResult CloneEnglishBundleAsRussian(
            string patchedEnglishBundlePath,
            string russianBundlePath,
            string gameDataRootForPreload)
        {
            if (string.IsNullOrWhiteSpace(patchedEnglishBundlePath) || !File.Exists(patchedEnglishBundlePath))
                throw new FileNotFoundException("Пропатченный english .bundle не найден.", patchedEnglishBundlePath);
            if (string.IsNullOrWhiteSpace(russianBundlePath))
                throw new ArgumentException("Укажите путь для russian .bundle.");

            var aggregate = new UabeaImportResult();
            const bool unpackForClone = true;
            var manager = CreateAssetsManagerForBundleMutation();

            var inferredFromBundle =
                UnityAssetsGameFolderHelper.TryInferGameDataAncestorFromBundlePath(patchedEnglishBundlePath);
            var inferredNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(inferredFromBundle);

            var resolvedRoot = UnityAssetsGameFolderHelper.ResolveGameDataFolder(gameDataRootForPreload ?? "");
            var resolvedExists = !string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot);
            var resolvedNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(resolvedRoot);

            if (!resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm))
            {
                resolvedRoot = inferredFromBundle;
                resolvedExists = true;
                aggregate.Messages.Add(
                    "[Clone ru] Папка данных не была доступна — использован «" + resolvedRoot + "» над путём к bundle.");
                resolvedNorm = inferredNorm;
            }
            else if (resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm) &&
                     !string.IsNullOrWhiteSpace(resolvedNorm) &&
                     !resolvedNorm.Equals(inferredNorm, StringComparison.OrdinalIgnoreCase))
            {
                aggregate.Messages.Add(
                    "[Clone ru] Bundle из «" + inferredNorm + "», указана другая *_Data («" + resolvedNorm +
                    "») — MonoBehaviour может читаться неполно.");
            }

            if (!string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot))
            {
                var msgN = aggregate.Messages.Count;
                if (!UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, resolvedRoot, out _, aggregate.Messages) &&
                    aggregate.Messages.Count == msgN)
                    aggregate.Messages.Add(UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(resolvedRoot));

                UnityAssetsGameFolderHelper.PreloadAllAssetsFromDataFolder(manager, resolvedRoot);
            }

            BundleFileInstance bunInst = null;
            try
            {
                bunInst = manager.LoadBundleFile(patchedEnglishBundlePath, unpackForClone);
                if (bunInst?.file == null)
                    throw new InvalidOperationException("Не удалось прочитать Asset Bundle (пустой внутренний файл).");

                if (aggregate.Messages.Count < 260)
                    aggregate.Messages.Add(
                        "[Clone ru] шаг 1: LoadBundleFile(«" + Path.GetFileName(patchedEnglishBundlePath) +
                        "», unpack=true) — контейнер загружен.");

                var blockDir = bunInst.file.BlockAndDirInfo;
                if (blockDir?.DirectoryInfos == null || blockDir.DirectoryInfos.Count == 0)
                    throw new InvalidOperationException("В bundle нет записей каталога.");

                var dirs = blockDir.DirectoryInfos;
                var totalWritten = 0;

                for (var i = 0; i < dirs.Count; i++)
                {
                    if (!bunInst.file.IsAssetsFile(i))
                        continue;

                    if (aggregate.Messages.Count < 260)
                        aggregate.Messages.Add(
                            "[Clone ru] CAB[" + i + "] шаг 2: LoadAssetsFileFromBundle(index=" + i + ")…");

                    var afileInst = manager.LoadAssetsFileFromBundle(bunInst, i, true);
                    if (afileInst == null)
                        continue;

                    if (aggregate.Messages.Count < 260)
                        aggregate.Messages.Add("[Clone ru] CAB[" + i + "] шаг 2: экземпляр CAB в памяти — OK.");

                    UabeaJsonAssetImporter.TryLoadClassPackage(manager, afileInst);
                    var patchedIds = new List<long>();
                    var cabTag = "[Clone ru] CAB[" + i + "]";
                    var n = UabeaJsonAssetImporter.ApplyEnToRuLocaleCloneToAllAssetsInFile(
                        manager, afileInst, aggregate, patchedIds, cabTag);
                    totalWritten += n;
                    if (n == 0)
                        continue;

                    if (aggregate.Messages.Count < 260)
                    {
                        aggregate.Messages.Add(
                            "[Clone ru] CAB[" + i + "] шаг 4a: после всех SetNewData по этому CAB — контрольное чтение "
                            + Math.Min(6, patchedIds.Count) + " PathID перед сериализацией CAB.");
                        var shown = 0;
                        foreach (var pid in patchedIds)
                        {
                            if (shown >= 6 || aggregate.Messages.Count >= 258)
                                break;
                            AssetFileInfo match = null;
                            foreach (AssetFileInfo inf in afileInst.file.AssetInfos)
                            {
                                if (inf.Stripped != 0)
                                    continue;
                                if (inf.PathId == pid)
                                {
                                    match = inf;
                                    break;
                                }
                            }

                            if (match == null)
                                continue;
                            AssetTypeValueField vtree = null;
                            try
                            {
                                vtree = UabeaJsonAssetImporter.TryGetBaseFieldReliable(manager, afileInst, match);
                            }
                            catch { }

                            var code = UabeaJsonAssetImporter.TryReadLocaleIdCodeFromBaseField(vtree);
                            aggregate.Messages.Add(
                                "[Clone ru] CAB[" + i + "] шаг 4a PathID " + pid +
                                ": m_LocaleId.m_Code = «" + (code ?? "—") + "»");
                            shown++;
                        }
                    }

                    if (aggregate.Messages.Count < 260)
                        aggregate.Messages.Add(
                            "[Clone ru] CAB[" + i +
                            "] шаг 4: SerializeBundledCabToBytes — AssetsFile.Write по экземпляру CAB после всех SetNewData.");

                    var cabBytes = SerializeBundledCabToBytes(afileInst);

                    if (aggregate.Messages.Count < 260)
                        aggregate.Messages.Add(
                            "[Clone ru] CAB[" + i + "] шаг 4: сериализовано " + cabBytes.Length + " байт CAB.");

                    if (aggregate.Messages.Count < 260)
                        aggregate.Messages.Add(
                            "[Clone ru] CAB[" + i + "] шаг 5: BlockAndDirInfo.DirectoryInfos[" + i + "].SetNewData(byte[])…");
                    CommitBundledCabIntoBundleSlot(bunInst, i, afileInst, aggregate.Messages, "[Clone ru]");

                    if (aggregate.Messages.Count < 260)
                        aggregate.Messages.Add("[Clone ru] CAB[" + i + "] шаг 5: блок каталога обновлён.");
                }

                aggregate.Imported = totalWritten;

                var outDir = Path.GetDirectoryName(Path.GetFullPath(russianBundlePath));
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);

                var diskOut = GetBundleDiskWritePath(patchedEnglishBundlePath, russianBundlePath, aggregate.Messages, "[Clone ru]");

                if (totalWritten == 0)
                {
                    aggregate.Messages.Add(
                        "[Clone ru] Ни один ассет не потребовал правки en→ru; копирование пропатченного файла без пересборки UnityFS.");
                    try
                    {
                        File.Copy(patchedEnglishBundlePath, diskOut, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Не удалось скопировать bundle: " + ex.Message, ex);
                    }

                    AddressablesCatalogCrcInterop.TryPatchCatalogsNearBundle(
                        AddressablesCatalogAnchorPath(russianBundlePath, patchedEnglishBundlePath), aggregate.Messages);
                    ReleaseBundleManagerHandles(manager);
                    CommitBundleToRequestedPath(russianBundlePath, diskOut, aggregate.Messages, "[Clone ru]");
                    return aggregate;
                }

                if (aggregate.Messages.Count < 260)
                    aggregate.Messages.Add(
                        "[Clone ru] шаг 6: bunInst.file.Write → «" + Path.GetFileName(diskOut) + "»…");
                WriteBundleCloneRussianPreferLz4Pack(bunInst, diskOut, aggregate.Messages);
                AddressablesCatalogCrcInterop.TryPatchCatalogsNearBundle(
                    AddressablesCatalogAnchorPath(russianBundlePath, patchedEnglishBundlePath), aggregate.Messages);
                ReleaseBundleManagerHandles(manager);
                CommitBundleToRequestedPath(russianBundlePath, diskOut, aggregate.Messages, "[Clone ru]");
            }
            finally
            {
                try { manager.UnloadAllBundleFiles(); } catch { }
                try { manager.UnloadAllAssetsFiles(true); } catch { }
            }

            return aggregate;
        }

        public static UabeaExportResult ExportBundleToJson(
            string bundlePath,
            string outputFolder,
            bool monoBehaviourOnly,
            string gameDataRootForPreload,
            UabeaJsonFileLayout layout)
        {
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
                throw new FileNotFoundException("Файл .bundle не найден.", bundlePath);
            if (string.IsNullOrWhiteSpace(outputFolder))
                throw new ArgumentException("Укажите папку для JSON.");

            Directory.CreateDirectory(outputFolder);

            var result = new UabeaExportResult();
            var classDataPath = ClassPackageDownloader.ClassDataPath;
            if (!File.Exists(classDataPath))
            {
                result.Messages.Add(
                    "classdata.tpk не найден (ожидается «" + classDataPath +
                    "»). Без него разбор многих типов слабее; при сети программа пытается скачать файл при экспорте bundle.");
            }
            // Сжатые UnityFS при unpackIfPacked=false часто не читаются как полноценный assets — без JSON или с пустыми деревьями.
            const bool unpackForExport = true;
            var manager = new AssetsManager
            {
                UseQuickLookup = true,
                UseTemplateFieldCache = true,
                UseMonoTemplateFieldCache = true,
                UseRefTypeManagerCache = true
            };

            var inferredFromBundle =
                UnityAssetsGameFolderHelper.TryInferGameDataAncestorFromBundlePath(bundlePath);
            var inferredNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(inferredFromBundle);

            var resolvedRoot = UnityAssetsGameFolderHelper.ResolveGameDataFolder(gameDataRootForPreload ?? "");
            var resolvedExists = !string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot);
            var resolvedNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(resolvedRoot);

            if (!resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm))
            {
                resolvedRoot = inferredFromBundle;
                resolvedExists = true;
                result.Messages.Add(
                    "[Bundle] Указанная папка данных недоступна — подставлен каталог того же сборника что и bundle: «" +
                    resolvedRoot +
                    "» (там нужен Managed для разбора строк).");
                resolvedNorm = inferredNorm;
            }
            else if (resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm) &&
                     !string.IsNullOrWhiteSpace(resolvedNorm) &&
                     !resolvedNorm.Equals(inferredNorm, StringComparison.OrdinalIgnoreCase))
            {
                result.Messages.Add(
                    "[Bundle] ОШИБКА КОНФИГУРАЦИИ: файл bundle лежит внутри «" + inferredNorm +
                    "», а вы указали другую игру («" + resolvedNorm +
                    "»). Для Addressables нужны сборки именно из Name_Data этого bundle — иначе JSON часто пустой " +
                    "или только служебные строки.");
            }

            if (!string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot))
            {
                var msgN = result.Messages.Count;
                if (UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, resolvedRoot, out var managedFolder, result.Messages))
                    result.ManagedAssembliesFolder = managedFolder;
                else if (result.Messages.Count == msgN)
                    result.Messages.Add(UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(resolvedRoot));

                var satelliteDiag = UnityAssetsGameFolderHelper.GetMonoCecilSatelliteAssemblyDiagnosticOrNull();
                if (!string.IsNullOrEmpty(satelliteDiag))
                    result.Messages.Add(satelliteDiag);

                UnityAssetsGameFolderHelper.PreloadAllAssetsFromDataFolder(manager, resolvedRoot);
            }

            BundleFileInstance bunInst = null;
            try
            {
                bunInst = manager.LoadBundleFile(bundlePath, unpackForExport);
                if (bunInst?.file == null)
                    throw new InvalidOperationException("Не удалось прочитать Asset Bundle (пустой внутренний файл).");

                var exportBlockDir = bunInst.file.BlockAndDirInfo;
                if (exportBlockDir?.DirectoryInfos == null || exportBlockDir.DirectoryInfos.Count == 0)
                    throw new InvalidOperationException("В bundle нет записей каталога (пустой или неподдерживаемый формат).");

                var dirs = exportBlockDir.DirectoryInfos;

                for (var i = 0; i < dirs.Count; i++)
                {
                    if (!bunInst.file.IsAssetsFile(i))
                        continue;

                    var afileInst = manager.LoadAssetsFileFromBundle(bunInst, i, true);
                    if (afileInst == null)
                        continue;

                    // class database по UnityVersion ЭТОГО CAB: раньше DB грузилась только из первого — при пустом/иноверсионном первом блоке поля не разбирались
                    UabeaJsonAssetImporter.TryLoadClassPackage(manager, afileInst);
                    if (manager.ClassDatabase != null)
                        result.ClassDatabaseLoaded = true;

                    result.AssetFilesScanned++;
                    var innerName = bunInst.file.GetFileName(i);
                    var assetsBase = UabeaJsonPaths.SafeFileNamePart(Path.GetFileNameWithoutExtension(innerName ?? "cab"))
                        .TrimEnd('-', '_', ' ');

                    UabeaJsonAssetExporter.ExportAssetsFileToJsonFolder(
                        manager, afileInst, outputFolder, monoBehaviourOnly, assetsBase, layout, result,
                        cancellationToken: default,
                        includeTextAssetsWhenMonoFiltered: true);

                    AppendBundledCabBriefSummary(result.Messages, afileInst, innerName, assetsBase);
                }

                if (result.AssetFilesScanned == 0)
                    throw new InvalidOperationException("В bundle не найдено ни одного вложенного assets-файла (CAB).");

                if (result.Exported == 0)
                {
                    result.Messages.Add(
                        "Экспорт bundle: ни одного JSON не записано при разборе CAB. В этом режиме из bundle дополнительно " +
                        "берутся TextAsset (CSV/сырые строки). Если пусто — часто IL2CPP без Managed: дерево MonoBehaviour недоступно " +
                        "(nesrak1/UABEA «MonoBehaviour template info failed», dummy DLL после Il2CppDumper в папку Managed). " +
                        "Полностью снимите ограничение «только MonoBehaviour+TextAsset», чтобы пробовать все типы, либо AssetStudio/AssetRipper.");
                }
            }
            finally
            {
                try { manager.UnloadAllBundleFiles(); } catch { }
                try { manager.UnloadAllAssetsFiles(true); } catch { }
            }

            return result;
        }

        /// <summary>
        /// Как <see cref="ImportJsonIntoBundle"/>, но без JSON — только правка <c>Locale</c> в CAB. Если ни один CAB не изменён,
        /// перепаковки нет (у AssetsTools <c>Pack</c> падает, если BlockAndDirInfo не трогали): «локаль уже разблокирована» → выходим без записи.
        /// </summary>
        public static UabeaImportResult PatchLocalesBundleEnableLanguage(
            string localesBundlePath,
            string localeCode,
            string outputBundlePath,
            string gameDataRootForPreload)
        {
            if (string.IsNullOrWhiteSpace(localesBundlePath) || !File.Exists(localesBundlePath))
                throw new FileNotFoundException("Файл localization-locales .bundle не найден.", localesBundlePath);
            if (string.IsNullOrWhiteSpace(localeCode))
                throw new ArgumentException("Укажите код локали (например ru).");
            if (string.IsNullOrWhiteSpace(outputBundlePath))
                throw new ArgumentException("Укажите путь для сохранения bundle.");

            var aggregate = new UabeaImportResult();
            const bool unpackForImport = true;
            var manager = CreateAssetsManagerForBundleMutation();

            var inferredFromBundle =
                UnityAssetsGameFolderHelper.TryInferGameDataAncestorFromBundlePath(localesBundlePath);
            var inferredNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(inferredFromBundle);

            var resolvedRoot = UnityAssetsGameFolderHelper.ResolveGameDataFolder(gameDataRootForPreload ?? "");
            var resolvedExists = !string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot);
            var resolvedNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(resolvedRoot);

            if (!resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm))
            {
                resolvedRoot = inferredFromBundle;
                resolvedExists = true;
                aggregate.Messages.Add(
                    "[Locales] Папка данных не была доступна — использован «" + resolvedRoot + "» над путём к bundle.");
                resolvedNorm = inferredNorm;
            }
            else if (resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm) &&
                     !string.IsNullOrWhiteSpace(resolvedNorm) &&
                     !resolvedNorm.Equals(inferredNorm, StringComparison.OrdinalIgnoreCase))
            {
                aggregate.Messages.Add(
                    "[Locales] Bundle из «" + inferredNorm + "», указана другая *_Data («" + resolvedNorm +
                    "») — MonoBehaviour-поля могут читаться неполно.");
            }

            if (!string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot))
            {
                var msgN = aggregate.Messages.Count;
                if (!UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, resolvedRoot, out _, aggregate.Messages) &&
                    aggregate.Messages.Count == msgN)
                    aggregate.Messages.Add(UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(resolvedRoot));

                UnityAssetsGameFolderHelper.PreloadAllAssetsFromDataFolder(manager, resolvedRoot);
            }

            var code = localeCode.Trim();
            BundleFileInstance bunInst = null;
            try
            {
                bunInst = manager.LoadBundleFile(localesBundlePath, unpackForImport);
                if (bunInst?.file == null)
                    throw new InvalidOperationException("Не удалось прочитать Asset Bundle.");

                var blockDir = bunInst.file.BlockAndDirInfo;
                if (blockDir?.DirectoryInfos == null || blockDir.DirectoryInfos.Count == 0)
                    throw new InvalidOperationException("В bundle нет записей каталога.");

                var dirs = blockDir.DirectoryInfos;
                var totalMatched = 0;
                var totalWritten = 0;

                for (var i = 0; i < dirs.Count; i++)
                {
                    if (!bunInst.file.IsAssetsFile(i))
                        continue;

                    var afileInst = manager.LoadAssetsFileFromBundle(bunInst, i, true);
                    if (afileInst == null)
                        continue;

                    UabeaJsonAssetImporter.TryLoadClassPackage(manager, afileInst);
                    UabeaJsonAssetImporter.TryPatchLocalizationLocaleAssetsInFile(
                        manager, afileInst, code, aggregate, out var matched, out var written);
                    totalMatched += matched;
                    totalWritten += written;

                    if (written > 0)
                        CommitBundledCabIntoBundleSlot(bunInst, i, afileInst, aggregate.Messages, "[Locales]");
                }

                aggregate.Imported = totalWritten;
                aggregate.LocaleMatchCount = totalMatched;
                if (totalMatched == 0)
                    throw new InvalidOperationException(
                        "В «" + Path.GetFileName(localesBundlePath) + "» не найден ни один ассет Locale с кодом «" + code +
                        "» (ожидаются поля m_Identifier.m_Code или m_Code). Экспортируйте bundle в JSON и проверьте имена полей.");

                // Ни один CAB не получил SetNewData → AssetBundleFile.Pack в AssetsTools.NET может упасть (индекс вне массива).
                // Локаль уже в норме — не перезаписываем бандл; главное — собрать localization-string-tables-russian(ru)….
                if (totalWritten == 0)
                {
                    aggregate.Messages.Add(
                        "[Locales] Локаль «" + code +
                        "» найдена, сериализуемые поля не менялись (язык уже не заблокирован). Файл не пересобирался — сосредоточьтесь на Pack в localization-string-tables-russian(ru)_….bundle.");
                    return aggregate;
                }

                var outDir = Path.GetDirectoryName(Path.GetFullPath(outputBundlePath));
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);

                var diskOut = GetBundleDiskWritePath(localesBundlePath, outputBundlePath, aggregate.Messages, "[Locales]");
                WriteBundleWithCompressionMatchingOriginal(bunInst, diskOut, aggregate.Messages);
                AddressablesCatalogCrcInterop.TryPatchCatalogsNearBundle(
                    AddressablesCatalogAnchorPath(outputBundlePath, localesBundlePath), aggregate.Messages);
                ReleaseBundleManagerHandles(manager);
                CommitBundleToRequestedPath(outputBundlePath, diskOut, aggregate.Messages, "[Locales]");
            }
            finally
            {
                try { manager.UnloadAllBundleFiles(); } catch { }
                try { manager.UnloadAllAssetsFiles(true); } catch { }
            }

            return aggregate;
        }

        /// <summary>Сборка JSON обратно в UnityFS: LoadBundleFile → по каждому CAB LoadAssetsFileFromBundle → ImportJson… → CAB в слот → запись на диск.</summary>
        /// <param name="bundlePath">Основной bundle (цель замены/путей); должен существовать.</param>
        /// <param name="loadCabsFromBundlePath">Если задан — читать UnityFS/CAB отсюда (english), а <paramref name="bundlePath"/> остаётся целевым (russian для Replace).</param>
        public static UabeaImportResult ImportJsonIntoBundle(
            string bundlePath,
            string jsonFolder,
            string outputBundlePath,
            string gameDataRootForPreload,
            string loadCabsFromBundlePath = null)
        {
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
                throw new FileNotFoundException("Основной .bundle не найден (цель замены / путей).", bundlePath);
            if (string.IsNullOrWhiteSpace(jsonFolder) || !Directory.Exists(jsonFolder))
                throw new DirectoryNotFoundException("Папка с JSON не найдена.");
            if (string.IsNullOrWhiteSpace(outputBundlePath))
                throw new ArgumentException("Укажите путь для сохранения bundle.");

            var diskReadPath = bundlePath;
            if (!string.IsNullOrWhiteSpace(loadCabsFromBundlePath))
            {
                var alt = loadCabsFromBundlePath.Trim();
                if (!File.Exists(alt))
                    throw new FileNotFoundException(
                        "Файл в поле «грузить CAB из…» не найден. Укажите english string-tables или очистите поле.", alt);
                if (!string.Equals(Path.GetFullPath(alt), Path.GetFullPath(bundlePath), StringComparison.OrdinalIgnoreCase))
                    diskReadPath = alt;
            }

            var aggregate = new UabeaImportResult();
            if (!string.Equals(Path.GetFullPath(diskReadPath), Path.GetFullPath(bundlePath), StringComparison.OrdinalIgnoreCase))
                aggregate.Messages.Add(
                    "[Bundle импорт] UnityFS и имена внутренних CAB (префиксы JSON): «" + Path.GetFileName(diskReadPath) +
                    "»; основной файл «" + Path.GetFileName(bundlePath) + "» — для путей и замены на диске.");
            var stringTableLocaleOverride = TryInferStringTableLocaleCodeFromOutputBundlePath(outputBundlePath);
            if (!string.IsNullOrEmpty(stringTableLocaleOverride))
                aggregate.Messages.Add(
                    "[Bundle импорт] StringTable m_LocaleId.m_Code = «" + stringTableLocaleOverride + "» (из имени файла «" +
                    Path.GetFileName(outputBundlePath) + "»).");

            try
            {
                aggregate.Messages.Add("[Bundle импорт] JSON «" + Path.GetFullPath(jsonFolder.Trim()) + "».");
            }
            catch { }

            const bool unpackForImport = true;
            var manager = CreateAssetsManagerForBundleMutation();

            var inferredFromBundle =
                UnityAssetsGameFolderHelper.TryInferGameDataAncestorFromBundlePath(diskReadPath);
            var inferredNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(inferredFromBundle);

            var resolvedRoot = UnityAssetsGameFolderHelper.ResolveGameDataFolder(gameDataRootForPreload ?? "");
            var resolvedExists = !string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot);
            var resolvedNorm =
                UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(resolvedRoot);

            if (!resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm))
            {
                resolvedRoot = inferredFromBundle;
                resolvedExists = true;
                aggregate.Messages.Add(
                    "[Bundle импорт] Папка данных не была доступна — использован «" + resolvedRoot + "» над путём к bundle.");
                resolvedNorm = inferredNorm;
            }
            else if (resolvedExists && !string.IsNullOrWhiteSpace(inferredNorm) &&
                     !string.IsNullOrWhiteSpace(resolvedNorm) &&
                     !resolvedNorm.Equals(inferredNorm, StringComparison.OrdinalIgnoreCase))
            {
                aggregate.Messages.Add(
                    "[Bundle импорт] Конфигурация: bundle из «" + inferredNorm +
                    "», указана другая *_Data («" + resolvedNorm + "») — возможны ошибки импорта.");
            }

            if (!string.IsNullOrWhiteSpace(resolvedRoot) && Directory.Exists(resolvedRoot))
            {
                var msgN = aggregate.Messages.Count;
                if (!UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, resolvedRoot, out _, aggregate.Messages) &&
                    aggregate.Messages.Count == msgN)
                    aggregate.Messages.Add(UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(resolvedRoot));

                UnityAssetsGameFolderHelper.PreloadAllAssetsFromDataFolder(manager, resolvedRoot);
            }

            BundleFileInstance bunInst = null;
            try
            {
                bunInst = manager.LoadBundleFile(diskReadPath, unpackForImport);
                if (bunInst?.file == null)
                    throw new InvalidOperationException("Не удалось прочитать Asset Bundle (пустой внутренний файл).");

                var blockDir = bunInst.file.BlockAndDirInfo;
                if (blockDir?.DirectoryInfos == null || blockDir.DirectoryInfos.Count == 0)
                    throw new InvalidOperationException("В bundle нет записей каталога.");

                var dirs = blockDir.DirectoryInfos;

                var anyImported = false;

                for (var i = 0; i < dirs.Count; i++)
                {
                    if (!bunInst.file.IsAssetsFile(i))
                        continue;

                    var afileInst = manager.LoadAssetsFileFromBundle(bunInst, i, true);
                    if (afileInst == null)
                        continue;

                    var innerName = bunInst.file.GetFileName(i);
                    var assetsBase = UabeaJsonPaths.SafeFileNamePart(Path.GetFileNameWithoutExtension(innerName ?? "cab"))
                        .TrimEnd('-', '_', ' ');
                    aggregate.Messages.Add(
                        $"[Bundle импорт] CAB[{i}] «{innerName ?? "(null)"}» → префикс «{assetsBase}».");

                    var one = UabeaJsonAssetImporter.ImportJsonIntoAssetsFileInstanceFromFolder(
                        manager, afileInst, jsonFolder, assetsBase, stringTableLocaleOverride);

                    aggregate.JsonFound += one.JsonFound;
                    aggregate.Imported += one.Imported;
                    aggregate.Failed += one.Failed;
                    aggregate.Skipped += one.Skipped;

                    foreach (var m in one.Messages)
                        aggregate.Messages.Add($"[{assetsBase}] {m}");

                    if (one.Imported > 0)
                    {
                        CommitBundledCabIntoBundleSlot(bunInst, i, afileInst, aggregate.Messages, "[Bundle импорт]");
                        anyImported = true;
                    }

                    aggregate.Messages.Add(
                        $"CAB[{i}] base='{assetsBase}', inner='{innerName}', jsonFound={one.JsonFound}, imported={one.Imported}, failed={one.Failed}, skipped={one.Skipped}");
                }

                if (!anyImported)
                    throw new InvalidOperationException(
                        "Ни один JSON не был импортирован ни в один CAB bundle. Проверьте папку JSON и префиксы имён или укажите поле «грузить CAB из» (english), если JSON экспортирован с другого .bundle. " +
                        "См. префиксы CAB в логе выше.");

                var fullPrimary = Path.GetFullPath(bundlePath ?? "");
                var fullOut = Path.GetFullPath(outputBundlePath ?? "");
                if (!fullPrimary.Equals(fullOut, StringComparison.OrdinalIgnoreCase))
                    aggregate.Messages.Add(
                        "[Bundle импорт] Исходный .bundle (первый параметр импорта) и выход записи разные. При проверке через «Экспорт JSON» в интерфейсе читается поле «исходный bundle», а не выход — для совпадения укажите там файл, куда только что писала сборка.");

                var outDir = Path.GetDirectoryName(Path.GetFullPath(outputBundlePath));
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);

                var diskOut = GetBundleDiskWritePath(bundlePath, outputBundlePath, aggregate.Messages, "[Bundle импорт]");
                WriteBundleWithCompressionMatchingOriginal(bunInst, diskOut, aggregate.Messages);
                AddressablesCatalogCrcInterop.TryPatchCatalogsNearBundle(
                    AddressablesCatalogAnchorPath(outputBundlePath, bundlePath), aggregate.Messages);
                ReleaseBundleManagerHandles(manager);
                CommitBundleToRequestedPath(outputBundlePath, diskOut, aggregate.Messages, "[Bundle импорт]");
            }
            finally
            {
                try { manager.UnloadAllBundleFiles(); } catch { }
                try { manager.UnloadAllAssetsFiles(true); } catch { }
            }

            return aggregate;
        }

        /// <summary>Снимает байты CAB после правок — тот же путь, что запись контейнера на диск, но в память.</summary>
        private static byte[] SerializeBundledCabToBytes(AssetsFileInstance afileInst)
        {
            if (afileInst?.file == null)
                throw new ArgumentNullException(nameof(afileInst));

            using (var ms = new MemoryStream())
            {
                using (var writer = new AssetsFileWriter(ms) { BigEndian = false })
                    afileInst.file.Write(writer);
                return ms.ToArray();
            }
        }

        /// <summary>CAB в слот UnityFS только как полный дамп <see cref="AssetsFile.Write"/> — один предсказуемый путь для всех сборок.</summary>
        private static void CommitBundledCabIntoBundleSlot(
            BundleFileInstance bunInst,
            int dirIndex,
            AssetsFileInstance afileInst,
            ICollection<string> logSink,
            string logPrefix)
        {
            if (bunInst?.file?.BlockAndDirInfo?.DirectoryInfos == null || afileInst?.file == null)
                return;
            if (dirIndex < 0 || dirIndex >= bunInst.file.BlockAndDirInfo.DirectoryInfos.Count)
                return;

            try
            {
                afileInst.file.GenerateQuickLookup();
            }
            catch { }

            try
            {
                // Часть UnityFS теряет правки при Pack, если в каталог положили только byte[] сериализации;
                // передача живого AssetsFile совпадает с рекомендациями AssetsTools для замены CAB в bundle.
                bunInst.file.BlockAndDirInfo.DirectoryInfos[dirIndex].SetNewData(afileInst.file);
            }
            catch (Exception exAf)
            {
                try
                {
                    var cabBytes = SerializeBundledCabToBytes(afileInst);
                    bunInst.file.BlockAndDirInfo.DirectoryInfos[dirIndex].SetNewData(cabBytes);
                }
                catch (Exception exBytes)
                {
                    if (logSink != null && logSink.Count < 200)
                        logSink.Add(
                            logPrefix + " CAB в слот: SetNewData(AssetsFile) — " + exAf.Message +
                            "; byte[] — " + exBytes.Message);
                    throw new InvalidOperationException(
                        "Не удалось записать CAB в слот Asset Bundle (ни AssetsFile, ни байты сериализации).",
                        exBytes);
                }
            }
        }

        /// <summary>
        /// После LoadBundleFile(unpack=true) <see cref="AssetBundleFile.Pack"/> в AssetsTools часто кидает NRE — пишем только
        /// <see cref="AssetBundleFile.Write"/>: подменённые через <see cref="CommitBundledCabIntoBundleSlot"/> блоки уходят без повторной LZ4.
        /// </summary>
        private static void WriteBundleWithCompressionMatchingOriginal(
            BundleFileInstance bunInst,
            string outputBundlePath,
            ICollection<string> messages)
        {
            if (bunInst?.file == null)
                throw new ArgumentNullException(nameof(bunInst));

            var raw = bunInst.originalCompression;
            TryDeleteFileQuiet(outputBundlePath);
            using (var writer = new AssetsFileWriter(outputBundlePath) { BigEndian = false })
                bunInst.file.Write(writer);

            if (raw == AssetBundleCompressionType.None)
                messages?.Add("[Bundle] Write (bundle был без сжатия).");
            else
                messages?.Add(
                    "[Bundle] Write без Pack (исходное сжатие в файле было " + raw +
                    "): несжатый UnityFS; для Addressables держите CRC=0 в каталоге.");
        }

        /// <summary>Тот же вывод, что <see cref="WriteBundleWithCompressionMatchingOriginal"/> — без Pack.</summary>
        private static void WriteBundleCloneRussianPreferLz4Pack(
            BundleFileInstance bunInst,
            string outputBundlePath,
            ICollection<string> messages)
        {
            if (bunInst?.file == null)
                throw new ArgumentNullException(nameof(bunInst));
            WriteBundleWithCompressionMatchingOriginal(bunInst, outputBundlePath, messages);
        }

        /// <summary>Однострочник в лог: что реально есть в CAB, если «игровых» строк не видно (часто typeId без имён).</summary>
        private static void AppendBundledCabBriefSummary(ICollection<string> messages,
            AssetsFileInstance afileInst, string bundleInnerCabNameHint, string assetsBase)
        {
            if (messages == null || messages.Count > 620)
                return;

            try
            {
                var counts = new Dictionary<int, int>();
                var total = 0;
                foreach (AssetFileInfo info in afileInst.file.AssetInfos)
                {
                    if (info.Stripped != 0)
                        continue;
                    total++;
                    var tid = info.GetTypeId(afileInst.file);
                    counts[tid] = counts.TryGetValue(tid, out var n) ? n + 1 : 1;
                }

                var top = counts.Count == 0
                    ? "(объектов без Stripped нет)"
                    : string.Join(
                        "; ",
                        counts.OrderByDescending(kv => kv.Value).Take(14).Select(kv => kv.Key + "×" + kv.Value));

                var cabTail = bundleInnerCabNameHint;
                var cabDisp = string.IsNullOrEmpty(cabTail)
                    ? "«" + assetsBase + "»"
                    : "«" + assetsBase + "» / CAB «" + Path.GetFileName(cabTail) + "»";
                messages.Add(
                    "Состав " + cabDisp + ": объектов " + total + ", typeId: " + top +
                    ". (114=MonoBehaviour — StringTable: m_TableData.Array[].m_Id + текст в m_Localized или вложенном поле; 49=TextAsset.) " +
                    "Это содержимое того .bundle, что выбран при экспорте (поле «исходный bundle»); если оно не совпадает с файлом, куда писала сборка — экспорт покажет не ту версию.");
            }
            catch { }
        }

        private static void TryDeleteFileQuiet(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

    }
}
