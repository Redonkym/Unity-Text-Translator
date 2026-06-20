using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnityTextTranslator
{
    /// <summary>
    /// Поиск, экспорт и замена <c>Texture2D</c> (фоны/картинки с запечённым текстом) в Unity-контейнерах.
    /// Декод любого формата (BCn/DXT/ETC/ASTC/RGBA…) — через <see cref="TextureFile"/> (AssetsTools.NET.Texture);
    /// обратно пишем всегда <b>RGBA32</b> inline (managed-кодек не нужен), обнуляя <c>m_StreamData</c>, и
    /// перезаписываем контейнер целиком (объект растёт) — как grow-путь шрифтового патчера.
    /// </summary>
    internal static class TextureReplacePatcher
    {
        /// <summary>Одна найденная текстура: где лежит, размер, формат, в стриме ли пиксели.</summary>
        internal sealed class TextureEntry
        {
            internal long PathId;
            internal string Name;
            internal int Width;
            internal int Height;
            internal int FormatId;
            internal string FormatName;
            /// <summary>Пиксели лежат в <c>.resS</c>-стриме (m_StreamData.path задан), а не inline.</summary>
            internal bool Streamed;
            internal long ByteSize;
            /// <summary>Путь к контейнеру (.assets или .bundle), которому принадлежит текстура.</summary>
            internal string ContainerPath;
            internal bool InBundle;
            /// <summary>Индекс CAB внутри bundle (для .assets = -1).</summary>
            internal int BundleCabIndex = -1;
            internal string BundleCabName;

            internal string DisplayLabel =>
                "PathID " + PathId + " — " + (string.IsNullOrEmpty(Name) ? "(без имени)" : Name)
                + "  " + Width + "×" + Height + "  " + FormatName
                + (Streamed ? "  [stream]" : "  [inline]")
                + (InBundle ? "  CAB#" + BundleCabIndex : "");
        }

        // ---------- Анализ ----------

        /// <summary>Перечисляет все Texture2D в одном .assets: имя, размер, формат, inline/stream.</summary>
        internal static List<TextureEntry> AnalyzeAssets(string classDataPath, string assetsPath, ICollection<string> log)
        {
            if (string.IsNullOrWhiteSpace(assetsPath) || !File.Exists(assetsPath))
                throw new FileNotFoundException("Контейнер .assets не найден.", assetsPath);

            var manager = CreateManager(classDataPath);
            var result = new List<TextureEntry>();
            try
            {
                var inst = manager.LoadAssetsFile(assetsPath, true);
                TryLoadClassDb(manager, inst);
                CollectTextures(manager, inst, assetsPath, inBundle: false, cabIndex: -1, cabName: null, result, log);
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }

            log?.Add("[Текстуры] В «" + Path.GetFileName(assetsPath) + "» найдено Texture2D: " + result.Count + ".");
            return result;
        }

        /// <summary>Перечисляет Texture2D во всех CAB внутри .bundle (Addressables).</summary>
        internal static List<TextureEntry> AnalyzeBundle(string classDataPath, string bundlePath, ICollection<string> log)
        {
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
                throw new FileNotFoundException("Файл .bundle не найден.", bundlePath);

            var manager = CreateManager(classDataPath);
            var result = new List<TextureEntry>();
            BundleFileInstance bun = null;
            try
            {
                bun = manager.LoadBundleFile(bundlePath, true);
                var dirs = bun.file?.BlockAndDirInfo?.DirectoryInfos;
                if (dirs == null || dirs.Count == 0)
                    throw new InvalidOperationException("В bundle нет записей каталога (пустой/неподдерживаемый формат).");

                for (var i = 0; i < dirs.Count; i++)
                {
                    if (!bun.file.IsAssetsFile(i))
                        continue;
                    var inst = manager.LoadAssetsFileFromBundle(bun, i, true);
                    if (inst == null)
                        continue;
                    TryLoadClassDb(manager, inst);
                    CollectTextures(manager, inst, bundlePath, inBundle: true, cabIndex: i, cabName: bun.file.GetFileName(i), result, log);
                }
            }
            finally
            {
                manager.UnloadAll(true);
            }

            log?.Add("[Текстуры] В bundle «" + Path.GetFileName(bundlePath) + "» найдено Texture2D: " + result.Count + ".");
            return result;
        }

        private static void CollectTextures(
            AssetsManager manager, AssetsFileInstance inst, string containerPath,
            bool inBundle, int cabIndex, string cabName, List<TextureEntry> result, ICollection<string> log)
        {
            foreach (var info in inst.file.AssetInfos)
            {
                if (info.GetTypeId(inst.file) != (int)AssetClassID.Texture2D)
                    continue;
                try
                {
                    var bf = manager.GetBaseField(inst, info);
                    if (bf == null)
                        continue;

                    var fmt = ReadIntField(bf, "m_TextureFormat");
                    var entry = new TextureEntry
                    {
                        PathId = info.PathId,
                        Name = ReadStringField(bf, "m_Name"),
                        Width = ReadIntField(bf, "m_Width"),
                        Height = ReadIntField(bf, "m_Height"),
                        FormatId = fmt,
                        FormatName = FormatName(fmt),
                        Streamed = HasStreamPath(bf),
                        ByteSize = (long)info.ByteSize,
                        ContainerPath = containerPath,
                        InBundle = inBundle,
                        BundleCabIndex = cabIndex,
                        BundleCabName = cabName
                    };
                    result.Add(entry);
                    log?.Add("[Текстуры] " + entry.DisplayLabel);
                }
                catch (Exception ex)
                {
                    log?.Add("[Текстуры] PathID " + info.PathId + ": не прочитан (" + ex.Message + ").");
                }
            }
        }

        // ---------- Экспорт в PNG ----------

        /// <summary>Декодирует текстуру из .assets и сохраняет PNG (для перерисовки во внешнем редакторе).</summary>
        internal static void ExportAssetsTextureToPng(string classDataPath, string assetsPath, long pathId, string outPng, ICollection<string> log)
        {
            var manager = CreateManager(classDataPath);
            try
            {
                var inst = manager.LoadAssetsFile(assetsPath, true);
                TryLoadClassDb(manager, inst);
                var info = FindTexture(inst, pathId);
                var bf = manager.GetBaseField(inst, info);
                var tf = TextureFile.ReadTextureFile(bf);
                var data = tf.FillPictureData(inst);
                DecodeToPng(tf, data, outPng, log);
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }
        }

        /// <summary>Декодирует текстуру из CAB внутри .bundle и сохраняет PNG.</summary>
        internal static void ExportBundleTextureToPng(string classDataPath, string bundlePath, int cabIndex, long pathId, string outPng, ICollection<string> log)
        {
            var manager = CreateManager(classDataPath);
            BundleFileInstance bun = null;
            try
            {
                bun = manager.LoadBundleFile(bundlePath, true);
                var inst = manager.LoadAssetsFileFromBundle(bun, cabIndex, true);
                if (inst == null)
                    throw new InvalidOperationException("CAB #" + cabIndex + " не загрузился из bundle.");
                TryLoadClassDb(manager, inst);
                var info = FindTexture(inst, pathId);
                var bf = manager.GetBaseField(inst, info);
                var tf = TextureFile.ReadTextureFile(bf);

                byte[] data;
                if (HasStreamPath(bf))
                {
                    tf.SetPictureDataFromBundle(bun); // пиксели в .resS внутри bundle
                    data = tf.pictureData;
                }
                else
                {
                    data = tf.pictureData;
                    if (data == null || data.Length == 0)
                        data = tf.FillPictureData(inst);
                }
                DecodeToPng(tf, data, outPng, log);
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }

        /// <summary>Выгружает ВСЕ Texture2D из .assets в папку (PNG с именем <c>имя-PathID.png</c>). Возвращает число успешных.
        /// При <paramref name="largeOnly"/> пропускает мелкие/служебные (см. <see cref="IsLikelyTextCandidate"/>) — быстрее и без мусора.</summary>
        internal static int ExportAllAssetsTexturesToFolder(string classDataPath, string assetsPath, string outFolder, ICollection<string> log, bool largeOnly = false)
        {
            var manager = CreateManager(classDataPath);
            int n = 0, fail = 0;
            try
            {
                var inst = manager.LoadAssetsFile(assetsPath, true);
                TryLoadClassDb(manager, inst);
                foreach (var info in inst.file.AssetInfos)
                {
                    if (info.GetTypeId(inst.file) != (int)AssetClassID.Texture2D)
                        continue;
                    try
                    {
                        var bf = manager.GetBaseField(inst, info);
                        var tf = TextureFile.ReadTextureFile(bf);
                        if (largeOnly && !IsLikelyTextCandidate(tf.m_Name, tf.m_Width, tf.m_Height))
                            continue;
                        Directory.CreateDirectory(outFolder);
                        DecodeToPng(tf, tf.FillPictureData(inst), Path.Combine(outFolder, MakePngName(tf.m_Name, info.PathId)), log);
                        n++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        log?.Add("[Текстуры] PathID " + info.PathId + ": пропуск (" + ex.Message + ").");
                    }
                }
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }
            if (n > 0 || fail > 0)
                log?.Add("[Текстуры] " + Path.GetFileName(assetsPath) + ": PNG " + n + (fail > 0 ? (", пропущено " + fail) : "") + ".");
            return n;
        }

        /// <summary>Выгружает ВСЕ Texture2D из всех CAB внутри .bundle в папку. Возвращает число успешных.
        /// При <paramref name="largeOnly"/> пропускает мелкие/служебные.</summary>
        internal static int ExportAllBundleTexturesToFolder(string classDataPath, string bundlePath, string outFolder, ICollection<string> log, bool largeOnly = false)
        {
            var manager = CreateManager(classDataPath);
            int n = 0, fail = 0;
            BundleFileInstance bun = null;
            try
            {
                bun = manager.LoadBundleFile(bundlePath, true);
                var dirs = bun.file?.BlockAndDirInfo?.DirectoryInfos;
                if (dirs == null || dirs.Count == 0)
                    throw new InvalidOperationException("В bundle нет записей каталога.");

                for (var i = 0; i < dirs.Count; i++)
                {
                    if (!bun.file.IsAssetsFile(i))
                        continue;
                    var inst = manager.LoadAssetsFileFromBundle(bun, i, true);
                    if (inst == null)
                        continue;
                    TryLoadClassDb(manager, inst);
                    foreach (var info in inst.file.AssetInfos)
                    {
                        if (info.GetTypeId(inst.file) != (int)AssetClassID.Texture2D)
                            continue;
                        try
                        {
                            var bf = manager.GetBaseField(inst, info);
                            var tf = TextureFile.ReadTextureFile(bf);
                            if (largeOnly && !IsLikelyTextCandidate(tf.m_Name, tf.m_Width, tf.m_Height))
                                continue;
                            byte[] data;
                            if (HasStreamPath(bf)) { tf.SetPictureDataFromBundle(bun); data = tf.pictureData; }
                            else { data = tf.pictureData; if (data == null || data.Length == 0) data = tf.FillPictureData(inst); }
                            Directory.CreateDirectory(outFolder);
                            DecodeToPng(tf, data, Path.Combine(outFolder, MakePngName(tf.m_Name, info.PathId)), log);
                            n++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            log?.Add("[Текстуры] CAB#" + i + " PathID " + info.PathId + ": пропуск (" + ex.Message + ").");
                        }
                    }
                }
            }
            finally
            {
                manager.UnloadAll(true);
            }
            if (n > 0 || fail > 0)
                log?.Add("[Текстуры] " + Path.GetFileName(bundlePath) + ": PNG " + n + (fail > 0 ? (", пропущено " + fail) : "") + ".");
            return n;
        }

        // Имена, по которым текстуру считаем служебной (карты материала, не фон с текстом).
        private static readonly string[] AuxNameHints =
        {
            "normal", "_n", "bump", "mask", "spec", "gloss", "metal", "rough",
            "ao", "occlusion", "noise", "gradient", "lut", "lightmap", "shadow",
            "depth", "emiss", "height", "_d", "detail"
        };

        /// <summary>
        /// Быстрая эвристика «вероятно фон/баннер/титул с текстом» (без чтения пикселей):
        /// крупная по площади/стороне и не похожа по имени на служебную карту материала.
        /// </summary>
        internal static bool IsLikelyTextCandidate(string name, int width, int height)
        {
            var area = (long)Math.Max(0, width) * Math.Max(0, height);
            var maxSide = Math.Max(width, height);
            if (area < 256L * 128L && maxSide < 512) // мелкие — иконки/служебное
                return false;

            var n = (name ?? "").ToLowerInvariant();
            if (n.Length > 0)
                foreach (var h in AuxNameHints)
                    if (n.Contains(h))
                        return false;

            return true;
        }

        /// <summary>Декодирует текстуру из .assets в PNG-байты (для превью в приложении, без файла на диске).</summary>
        internal static byte[] DecodeAssetsTextureToPngBytes(string classDataPath, string assetsPath, long pathId, ICollection<string> log)
        {
            var manager = CreateManager(classDataPath);
            try
            {
                var inst = manager.LoadAssetsFile(assetsPath, true);
                TryLoadClassDb(manager, inst);
                var info = FindTexture(inst, pathId);
                var bf = manager.GetBaseField(inst, info);
                var tf = TextureFile.ReadTextureFile(bf);
                return DecodeToPngBytes(tf, tf.FillPictureData(inst), log);
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }
        }

        /// <summary>Декодирует текстуру из CAB внутри .bundle в PNG-байты (для превью).</summary>
        internal static byte[] DecodeBundleTextureToPngBytes(string classDataPath, string bundlePath, int cabIndex, long pathId, ICollection<string> log)
        {
            var manager = CreateManager(classDataPath);
            try
            {
                var bun = manager.LoadBundleFile(bundlePath, true);
                var inst = manager.LoadAssetsFileFromBundle(bun, cabIndex, true);
                if (inst == null)
                    throw new InvalidOperationException("CAB #" + cabIndex + " не загрузился из bundle.");
                TryLoadClassDb(manager, inst);
                var info = FindTexture(inst, pathId);
                var bf = manager.GetBaseField(inst, info);
                var tf = TextureFile.ReadTextureFile(bf);
                byte[] data;
                if (HasStreamPath(bf)) { tf.SetPictureDataFromBundle(bun); data = tf.pictureData; }
                else { data = tf.pictureData; if (data == null || data.Length == 0) data = tf.FillPictureData(inst); }
                return DecodeToPngBytes(tf, data, log);
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }

        private static byte[] DecodeToPngBytes(TextureFile tf, byte[] encoded, ICollection<string> log)
        {
            if (encoded == null || encoded.Length == 0)
                throw new InvalidOperationException("Пиксели текстуры не получены (пустой буфер; возможно, .resS не найден).");
            using (var ms = new MemoryStream())
            {
                var ok = tf.DecodeTextureImage(encoded, ms, ImageExportType.Png, 100);
                if (!ok)
                    throw new InvalidOperationException(
                        "Формат " + FormatName(tf.m_TextureFormat) + " не поддержан декодером (" + tf.m_Width + "×" + tf.m_Height + ").");
                return ms.ToArray();
            }
        }

        private static string MakePngName(string name, long pathId)
        {
            var baseName = UabeaJsonPaths.SafeFileNamePart(
                (string.IsNullOrEmpty(name) ? "texture" : name) + "-" + pathId);
            return baseName + ".png";
        }

        private static void DecodeToPng(TextureFile tf, byte[] encoded, string outPng, ICollection<string> log)
        {
            if (encoded == null || encoded.Length == 0)
                throw new InvalidOperationException(
                    "Пиксели текстуры не получены (пустой буфер). Возможно, .resS-стрим не найден рядом с контейнером.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng)) ?? ".");
            var ok = tf.DecodeTextureImage(encoded, outPng, ImageExportType.Png, 100);
            if (!ok || !File.Exists(outPng))
                throw new InvalidOperationException(
                    "Не удалось декодировать формат " + FormatName(tf.m_TextureFormat) + " ("
                    + tf.m_Width + "×" + tf.m_Height + "). Этот формат текстуры не поддержан декодером.");

            log?.Add("[Текстуры] PNG сохранён: " + outPng + " (" + tf.m_Width + "×" + tf.m_Height
                + ", из " + FormatName(tf.m_TextureFormat) + ").");
        }

        // ---------- Замена из PNG (.assets) ----------

        /// <summary>
        /// Кодирует PNG в RGBA32 и заменяет эту текстуру в .assets inline (обнуляя m_StreamData), пишет в <paramref name="outputPath"/>.
        /// Объект растёт → полная перезапись файла.
        /// </summary>
        internal static void ReplaceAssetsTextureFromPng(
            string classDataPath, string assetsPath, long pathId, string pngPath, string outputPath, ICollection<string> log)
        {
            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
                throw new FileNotFoundException("PNG для импорта не найден.", pngPath);

            var manager = CreateManager(classDataPath);
            try
            {
                var inst = manager.LoadAssetsFile(assetsPath, true);
                TryLoadClassDb(manager, inst);
                var info = FindTexture(inst, pathId);
                var bf = manager.GetBaseField(inst, info);
                var tf = TextureFile.ReadTextureFile(bf);

                var prevW = tf.m_Width;
                var prevH = tf.m_Height;
                var prevFmt = FormatName(tf.m_TextureFormat);

                int w, h;
                var rgba = TextureFile.EncodeManagedImage(pngPath, TextureFormat.RGBA32, out w, out h);
                if (rgba == null || rgba.Length == 0)
                    throw new InvalidOperationException("Кодирование PNG → RGBA32 дало пустой результат.");

                tf.m_TextureFormat = (int)TextureFormat.RGBA32;
                tf.SetPictureData(rgba, w, h);
                // Явно фиксируем размеры (PNG мог быть иного размера, чем оригинал).
                tf.m_Width = w;
                tf.m_Height = h;
                tf.m_CompleteImageSize = rgba.Length;
                tf.m_MipCount = 1;
                tf.m_MipMap = false;
                // Пиксели теперь inline — обнуляем ссылку на .resS, иначе игра прочитает старые байты из стрима.
                tf.m_StreamData.path = "";
                tf.m_StreamData.offset = 0;
                tf.m_StreamData.size = 0;

                tf.WriteTo(bf);
                info.SetNewData(bf);

                WriteAssetsFileToPath(inst.file, outputPath);
                log?.Add("[Текстуры] Заменено PathID " + pathId + ": было " + prevW + "×" + prevH + " " + prevFmt
                    + " → стало " + w + "×" + h + " RGBA32 inline. Файл: " + outputPath);
            }
            finally
            {
                manager.UnloadAllAssetsFiles(true);
            }
        }

        // ---------- helpers ----------

        private static AssetsManager CreateManager(string classDataPath)
        {
            var manager = new AssetsManager();
            if (!string.IsNullOrWhiteSpace(classDataPath) && File.Exists(classDataPath))
                manager.LoadClassPackage(classDataPath);
            return manager;
        }

        private static void TryLoadClassDb(AssetsManager manager, AssetsFileInstance inst)
        {
            try
            {
                if (manager.ClassPackage != null)
                    manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
            }
            catch { /* type tree в файле может хватить и без базы */ }
        }

        private static AssetFileInfo FindTexture(AssetsFileInstance inst, long pathId)
        {
            var info = inst.file.AssetInfos.FirstOrDefault(x => x.PathId == pathId);
            if (info == null)
                throw new InvalidOperationException("Texture2D PathID " + pathId + " не найден в контейнере.");
            if (info.GetTypeId(inst.file) != (int)AssetClassID.Texture2D)
                throw new InvalidOperationException("Объект PathID " + pathId + " — не Texture2D.");
            return info;
        }

        private static void WriteAssetsFileToPath(AssetsFile file, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            using (var ms = new MemoryStream())
            {
                using (var writer = new AssetsFileWriter(ms) { BigEndian = false })
                    file.Write(writer);
                File.WriteAllBytes(outputPath, ms.ToArray());
            }
        }

        private static int ReadIntField(AssetTypeValueField bf, string name)
        {
            var f = bf[name];
            return (f == null || f.IsDummy) ? 0 : f.AsInt;
        }

        private static string ReadStringField(AssetTypeValueField bf, string name)
        {
            var f = bf[name];
            return (f == null || f.IsDummy) ? "" : (f.AsString ?? "");
        }

        private static bool HasStreamPath(AssetTypeValueField bf)
        {
            var sd = bf["m_StreamData"];
            if (sd == null || sd.IsDummy)
                return false;
            var p = sd["path"];
            return p != null && !p.IsDummy && !string.IsNullOrEmpty(p.AsString);
        }

        private static string FormatName(int fmt)
        {
            return Enum.IsDefined(typeof(TextureFormat), fmt)
                ? ((TextureFormat)fmt).ToString()
                : ("fmt" + fmt);
        }
    }
}
