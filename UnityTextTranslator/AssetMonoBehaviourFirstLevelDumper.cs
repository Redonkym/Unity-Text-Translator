using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityTextTranslator
{
    /// <summary>
    /// Рекурсивный дамп полей MonoBehaviour (напр. TMP_FontAsset) с фильтром по пути имени; перед GetBaseField грузит classdata.tpk+type db.
    /// Для MonoBehaviour ещё логирует сырые байты и эвристический поиск PPtr (fileID=0, pathID) без Mono.Cecil.
    /// </summary>
    internal static class AssetMonoBehaviourFirstLevelDumper
    {
        private const int MaxDumpDepth = 3;
        private const int MaxPPtrScanLines = 256;

        internal static void DumpTopLevelFieldsToMessages(
            AssetsManager manager,
            AssetsFileInstance afileInst,
            long pathId,
            bool requireTmpFontAsset,
            ICollection<string> messages)
        {
            if (manager == null || afileInst?.file == null || messages == null)
                return;

            UabeaJsonAssetImporter.TryLoadClassPackage(manager, afileInst);

            var info = afileInst.file.AssetInfos.FirstOrDefault(x => x.PathId == pathId);
            if (info == null)
            {
                messages.Add("[TMP fields] PathID=" + pathId + " не найден в этом .assets.");
                return;
            }

            var unityVer = afileInst.file.Metadata?.UnityVersion ?? "?";
            messages.Add("[TMP fields] classdata: после TryLoadClassPackage UnityVersion(meta)=" + unityVer + ".");

            if (info.GetTypeId(afileInst.file) == (int)AssetClassID.MonoBehaviour)
                AppendMonoBehaviourRawPPtrScan(afileInst, info, pathId, messages);

            AssetTypeValueField baseField;
            try
            {
                baseField = manager.GetBaseField(afileInst, info);
            }
            catch (Exception ex)
            {
                messages.Add("[TMP fields] GetBaseField: " + ex.Message);
                return;
            }

            if (baseField == null)
            {
                messages.Add("[TMP fields] baseField == null.");
                return;
            }

            var scriptClass = MonoBehaviourScriptResolver.TryGetMonoScriptShortClassName(
                manager, afileInst, baseField, AssetReadFlags.None);
            messages.Add("[TMP fields] PathID=" + pathId + " MonoScript class=" + (scriptClass ?? "?") + ".");

            if (requireTmpFontAsset
                && !string.Equals(scriptClass, "TMP_FontAsset", StringComparison.Ordinal))
            {
                messages.Add(
                    "[TMP fields] Ожидался TMP_FontAsset — дамп всё равно выводится (проверьте PathID).");
            }

            messages.Add(
                "[TMP fields] Фильтр пути (без учёта регистра): atlas, texture, glyph, character, face, width, height; глубина ≤ "
                + MaxDumpDepth + ".");

            DumpFields(baseField, "", messages, 0);
        }

        /// <summary>Читает тело объекта как в UABEA и ищет в сыром потоке пары int fileID + int64 pathID с шагом 4 байта (эвристика PPtr на Texture2D и др.).</summary>
        private static void AppendMonoBehaviourRawPPtrScan(
            AssetsFileInstance afileInst,
            AssetFileInfo info,
            long pathId,
            ICollection<string> messages)
        {
            if (afileInst?.file?.Reader == null || info == null)
                return;

            byte[] rawBytes;
            try
            {
                var len = checked((int)info.ByteSize);
                if (len <= 0)
                {
                    messages.Add("[TMP raw] PathID=" + pathId + " ByteSize=0 — сырой дамп пропущен.");
                    return;
                }

                lock (afileInst.LockReader)
                {
                    var reader = afileInst.file.Reader;
                    reader.Position = info.GetAbsoluteByteOffset(afileInst.file);
                    rawBytes = reader.ReadBytes(len);
                }

                if (rawBytes == null || rawBytes.Length != len)
                {
                    messages.Add("[TMP raw] PathID=" + pathId + " не удалось прочитать " + len + " байт.");
                    return;
                }
            }
            catch (Exception ex)
            {
                messages.Add("[TMP raw] PathID=" + pathId + " чтение: " + ex.Message);
                return;
            }

            messages.Add("[TMP raw] PathID=" + pathId + " size=" + rawBytes.Length + " bytes (MonoBehaviour, без разбора полей).");
            var preview = Math.Min(32, rawBytes.Length);
            messages.Add("[TMP raw] first " + preview + " bytes: " + BitConverter.ToString(rawBytes, 0, preview));

            TryWriteMonoBehaviourRawCaptureToDisk(pathId, rawBytes, afileInst, messages);

            var printed = 0;
            for (var i = 0; i <= rawBytes.Length - 12 && printed < MaxPPtrScanLines; i += 4)
            {
                var fileId = BitConverter.ToInt32(rawBytes, i);
                var pathIdCandidate = BitConverter.ToInt64(rawBytes, i + 4);
                if (fileId == 0 && pathIdCandidate > 0 && pathIdCandidate < 100000)
                {
                    messages.Add(
                        "[TMP raw] possible PPtr at offset " + i + ": fileID=" + fileId + " pathID=" + pathIdCandidate);
                    printed++;
                }
            }

            if (printed >= MaxPPtrScanLines)
                messages.Add("[TMP raw] … показано не более " + MaxPPtrScanLines + " совпадений PPtr.");

            // Типичные соседние ссылки в одном .assets (атлас после глифов / ранний PPtr) — проверка class ID.
            AppendResolvedPathIdTypes(afileInst.file, messages, 404, 165);
        }

        /// <summary>Сохраняет тело MonoBehaviour в <c>tmp_{pathId}_raw.bin</c> + hex-дамп <c>.hex.txt</c> рядом с .assets (или в каталоге процесса).</summary>
        private static void TryWriteMonoBehaviourRawCaptureToDisk(
            long pathId,
            byte[] rawBytes,
            AssetsFileInstance afileInst,
            ICollection<string> messages)
        {
            if (rawBytes == null || rawBytes.Length == 0 || messages == null)
                return;

            try
            {
                var assetsPath = UnityAssetsGameFolderHelper.GetAssetsFileInstancePath(afileInst);
                var dir = !string.IsNullOrEmpty(assetsPath)
                    ? Path.GetDirectoryName(Path.GetFullPath(assetsPath))
                    : null;
                if (string.IsNullOrEmpty(dir))
                    dir = Environment.CurrentDirectory;

                var baseName = "tmp_" + pathId + "_raw";
                var binPath = Path.Combine(dir, baseName + ".bin");
                File.WriteAllBytes(binPath, rawBytes);
                messages.Add("[TMP raw] Сохранено для анализа: " + binPath);

                var hexPath = Path.Combine(dir, baseName + ".hex.txt");
                var sb = new StringBuilder(rawBytes.Length * 3);
                for (var i = 0; i < rawBytes.Length; i += 16)
                {
                    var n = Math.Min(16, rawBytes.Length - i);
                    sb.Append(i.ToString("X8", CultureInfo.InvariantCulture));
                    sb.Append("  ");
                    for (var j = 0; j < n; j++)
                    {
                        if (j > 0)
                            sb.Append(' ');
                        sb.Append(rawBytes[i + j].ToString("X2", CultureInfo.InvariantCulture));
                    }

                    sb.AppendLine();
                }

                File.WriteAllText(hexPath, sb.ToString(), Encoding.UTF8);
                messages.Add("[TMP raw] Hex-дамп (построчно): " + hexPath);
            }
            catch (Exception ex)
            {
                messages.Add("[TMP raw] Не удалось сохранить сырой дамп: " + ex.Message);
            }
        }

        private static void AppendResolvedPathIdTypes(AssetsFile file, ICollection<string> messages, params long[] pathIds)
        {
            if (file?.AssetInfos == null || pathIds == null || pathIds.Length == 0)
                return;

            foreach (var pid in pathIds)
            {
                var target = file.AssetInfos.FirstOrDefault(x => x.PathId == pid);
                var typeName = target == null ? "not found" : AssetHelper.GetTypeName(file, target);
                messages.Add("PathID=" + pid + " type=" + typeName);
            }
        }

        private static void DumpFields(AssetTypeValueField field, string prefix, ICollection<string> messages, int depth)
        {
            if (field == null || depth > MaxDumpDepth)
                return;

            foreach (AssetTypeValueField child in field)
            {
                var segment = string.IsNullOrEmpty(child.FieldName) ? "?" : child.FieldName;
                var name = prefix + segment;

                if (MatchesFieldPathFilter(name))
                {
                    string valueStr;
                    if (child.IsDummy)
                        valueStr = "DUMMY";
                    else if (child.Children != null && child.Children.Count > 0)
                        valueStr = "(" + child.Children.Count + " nested)";
                    else
                        valueStr = FormatLeafValue(child);
                    messages.Add("[TMP fields] " + name + " = " + valueStr);
                }

                DumpFields(child, name + ".", messages, depth + 1);
            }
        }

        private static bool MatchesFieldPathFilter(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return false;
            var l = fullPath.ToLowerInvariant();
            return l.Contains("atlas")
                || l.Contains("texture")
                || l.Contains("glyph")
                || l.Contains("character")
                || l.Contains("face")
                || l.Contains("width")
                || l.Contains("height");
        }

        private static string FormatLeafValue(AssetTypeValueField child)
        {
            if (child == null)
                return "?";

            try
            {
                var s = child.AsString;
                if (s != null)
                {
                    if (s.Length > 120)
                        return s.Substring(0, 117) + "...";
                    return s;
                }
            }
            catch { }

            try
            {
                return child.AsInt.ToString(CultureInfo.InvariantCulture);
            }
            catch { }

            try
            {
                return child.AsLong.ToString(CultureInfo.InvariantCulture);
            }
            catch { }

            try
            {
                return child.AsFloat.ToString(CultureInfo.InvariantCulture);
            }
            catch { }

            try
            {
                if (child.AsByteArray != null && child.AsByteArray.Length > 0)
                    return "byte[" + child.AsByteArray.Length + "]";
            }
            catch { }

            try
            {
                return "type=" + child.Value?.ValueType.ToString();
            }
            catch
            {
                return "(?)";
            }
        }
    }
}
