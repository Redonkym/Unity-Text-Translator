using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace UnityTextTranslator
{
    /// <summary>Патч <c>TMP_FontAsset</c> в .assets: подмена атласа <c>Texture2D</c> из PNG (Alpha8) и пересборка <c>m_GlyphTable</c>/<c>m_CharacterTable</c> по JSON msdf-atlas-gen.</summary>
    internal static class TmpFontAssetMsdfAtlasPatcher
    {
        private const int UnityTextureFormatAlpha8 = 1;
        private const int Atlas512PixelCount = 512 * 512;
        private const int GlyphTableEntrySize = 52;

        /// <summary>Найденный TMP_FontAsset: PathID, число глифов, атлас-текстура и её размер.</summary>
        internal sealed class TmpFontInfo
        {
            internal long FontPathId;
            internal int GlyphCount;
            internal long AtlasTexturePathId;
            internal int AtlasWidth;
            internal int AtlasHeight;
        }

        /// <summary>«Анализ»: структурно (без MonoScript, годится для IL2CPP) находит TMP_FontAsset по сигнатуре m_GlyphTable; читает атлас (m_AtlasTextures[0]) и размер.</summary>
        internal static List<TmpFontInfo> AnalyzeTmpFonts(string classDataPath, string assetsPath, ICollection<string> log)
        {
            if (string.IsNullOrWhiteSpace(classDataPath) || !File.Exists(classDataPath))
                throw new FileNotFoundException("classdata.tpk", classDataPath);
            if (string.IsNullOrWhiteSpace(assetsPath) || !File.Exists(assetsPath))
                throw new FileNotFoundException(assetsPath);

            var result = new List<TmpFontInfo>();
            var manager = new AssetsManager();
            manager.LoadClassPackage(classDataPath);
            var inst = manager.LoadAssetsFile(assetsPath, true);
            manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);

            const int glyphStride = GlyphTableEntrySize;
            const int charStride = 16;

            foreach (var info in inst.file.AssetInfos)
            {
                if (info.TypeId != (int)AssetClassID.MonoBehaviour)
                    continue;
                var size = info.ByteSize;
                if (size < 0x200 || size > 5_000_000) // TMP-шрифт: десятки КБ+
                    continue;

                byte[] raw;
                try
                {
                    lock (inst.LockReader)
                    {
                        var r = inst.file.Reader;
                        r.Position = ResolveAbsoluteInPlaceOffset(inst.file, info, null, "Analyze read");
                        raw = r.ReadBytes(checked((int)size));
                    }
                }
                catch { continue; }

                if (!TryLocateGlyphTable7296(raw, null, out var entriesStart, out var runLen))
                    continue;

                var glyphCountOff = entriesStart - 4;
                var glyphCount = glyphCountOff >= 0 ? ReadInt32LittleEndian(raw, glyphCountOff) : runLen;
                if (glyphCount < 1 || glyphCount > 20000)
                    glyphCount = runLen;

                var charCountOff = entriesStart + glyphCount * glyphStride;
                if (charCountOff + 12 > raw.Length)
                    continue;
                var charCount = ReadInt32LittleEndian(raw, charCountOff);
                if (charCount < 0 || charCount > 50000)
                    continue;
                var charTableEnd = charCountOff + 4 + charCount * charStride;
                if (charTableEnd + 12 > raw.Length)
                    continue;

                // m_AtlasTextures[0]: count@+0, PPtr m_FileID@+4, m_PathID@+8.
                var atlasCount = ReadInt32LittleEndian(raw, charTableEnd);
                if (atlasCount < 1 || atlasCount > 64)
                    continue;
                var atlasPathId = ReadInt32LittleEndian(raw, charTableEnd + 8);

                var fi = new TmpFontInfo
                {
                    FontPathId = info.PathId,
                    GlyphCount = glyphCount,
                    AtlasTexturePathId = atlasPathId,
                };

                var texInfo = inst.file.AssetInfos.FirstOrDefault(x => x.PathId == atlasPathId);
                if (texInfo != null && texInfo.TypeId == (int)AssetClassID.Texture2D)
                {
                    try
                    {
                        var texBase = manager.GetBaseField(inst, texInfo);
                        fi.AtlasWidth = ReadIntFieldOrDefault(texBase, "m_Width");
                        fi.AtlasHeight = ReadIntFieldOrDefault(texBase, "m_Height");
                    }
                    catch { /* размер не критичен */ }
                }

                log?.Add("[Analyze] TMP_FontAsset PathID=" + fi.FontPathId
                    + " глифов=" + fi.GlyphCount
                    + " atlas PathID=" + fi.AtlasTexturePathId
                    + " size=" + fi.AtlasWidth + "×" + fi.AtlasHeight);
                result.Add(fi);
            }

            log?.Add("[Analyze] Найдено TMP_FontAsset: " + result.Count + ".");
            return result;
        }


        /// <summary>
        /// In-place патч только <see cref="AssetClassID.MonoBehaviour"/> TMP_FontAsset:
        /// размеры атласа/charset и, при наличии JSON, raw m_CharacterTable.
        /// Texture2D и Material не трогаются.
        /// </summary>
        /// <param name="il2CppTmpFontPathId">
        /// Если задан (и не 0), в сырых байтах этого MonoBehaviour правятся m_AtlasWidth/Height,
        /// дубликаты в creationSettings и 50-байтовая charset-строка (IL2CPP без полей GetBaseField).
        /// </param>
        /// <param name="il2CppCreationSettingsCharsetAscii">
        /// ASCII-строка для поля charset (≤50 символов); null — см. <see cref="TmpFontAssetIl2CppRawMetadataPatcher.DefaultCyrillicCharsetPattern"/>.
        /// </param>
        /// <param name="atlasJsonForCharacterTable">
        /// Если задан существующий файл и указан <paramref name="il2CppTmpFontPathId"/>, после патча метаданных
        /// пересобирается <c>m_CharacterTable</c> из JSON (смещения 0xB94 / 0xB9C / хвост с 0xEDC).
        /// </param>
        internal static void ReplaceTexture2DAtlasFromPngSameFile(
            string classDataPath,
            string assetsPath,
            long texturePathId,
            string atlasPngPath,
            string outputPath,
            ICollection<string> log,
            long? il2CppTmpFontPathId = null,
            string il2CppCreationSettingsCharsetAscii = null,
            string atlasJsonForCharacterTable = null,
            bool skipCharTable = false,
            bool skipTexturePatch = false,
            bool skipGlyphPatch = false,
            bool metadataAtlasSizeOnly = false,
            bool growCyrillicTables = false,
            bool markerCyrillicGlyphs = false)
        {
            if (string.IsNullOrWhiteSpace(classDataPath) || !File.Exists(classDataPath))
                throw new FileNotFoundException("classdata.tpk", classDataPath);
            if (string.IsNullOrWhiteSpace(assetsPath) || !File.Exists(assetsPath))
                throw new FileNotFoundException(assetsPath);
            if (!skipTexturePatch && (string.IsNullOrWhiteSpace(atlasPngPath) || !File.Exists(atlasPngPath)))
                throw new FileNotFoundException(atlasPngPath);
            if (!il2CppTmpFontPathId.HasValue || il2CppTmpFontPathId.Value == 0)
                throw new InvalidOperationException("Нужен PathID TMP_FontAsset для raw in-place патча.");

            log?.Add("[Patch test] skipTexturePatch=" + skipTexturePatch
                + " skipGlyphPatch=" + skipGlyphPatch
                + " metadataAtlasSizeOnly=" + metadataAtlasSizeOnly);

            var originalFileBytes = File.ReadAllBytes(assetsPath);
            log?.Add("[Header orig]  " + BitConverter.ToString(originalFileBytes, 0, Math.Min(32, originalFileBytes.Length)));

            var atlasW = 512;
            var atlasH = 512;
            (byte[] Alpha8, int Width, int Height) alpha8 = default;
            if (!skipTexturePatch)
            {
                alpha8 = LoadPngAsAlpha8TopDownFlipped(atlasPngPath, log);
                atlasW = alpha8.Width;
                atlasH = alpha8.Height;
            }
            else if (!string.IsNullOrWhiteSpace(atlasPngPath) && File.Exists(atlasPngPath))
            {
                alpha8 = LoadPngAsAlpha8TopDownFlipped(atlasPngPath, log);
                atlasW = alpha8.Width;
                atlasH = alpha8.Height;
            }

            var manager = new AssetsManager();
            manager.LoadClassPackage(classDataPath);
            var inst = manager.LoadAssetsFile(assetsPath, true);
            manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
            if (!skipTexturePatch)
                LogOriginalTexture2DInfo(manager, inst, texturePathId, log);

            byte[] rawTex = null;
            AssetFileInfo texInfo = null;
            // В режиме роста кириллицы текстуру (хардкод 404) НЕ патчим этим путём: атлас шрифта — это
            // m_AtlasTextures[0] (напр. 406, 1024, стримится), его патчим inline ниже своим 1024-атласом.
            if (texturePathId != 0 && !skipTexturePatch && !growCyrillicTables)
            {
                rawTex = BuildPatchedTexture2DRawInPlaceFromPng(manager, inst, texturePathId, alpha8, log, out texInfo);
            }
            else if (skipTexturePatch)
            {
                log?.Add("[Patch test] Texture2D не патчилась (skipTexturePatch=true).");
            }
            else if (growCyrillicTables)
            {
                log?.Add("[Grow] Хардкод-текстура " + texturePathId + " пропущена — патчим атлас шрифта (m_AtlasTextures) inline.");
            }

            byte[] rawMb = null;
            AssetFileInfo mbInfo = null;
            var grew = false;
            long growAtlasPathId = 0;
            if (il2CppTmpFontPathId.HasValue && il2CppTmpFontPathId.Value != 0)
            {
                var tmpId = il2CppTmpFontPathId.Value;
                mbInfo = inst.file.AssetInfos.FirstOrDefault(x => x.PathId == tmpId)
                         ?? inst.file.GetAssetInfo(tmpId);
                if (mbInfo == null)
                    throw new InvalidOperationException("IL2CPP raw: MonoBehaviour PathID=" + tmpId + " не найден в этом .assets.");

                lock (inst.LockReader)
                {
                    var reader = inst.file.Reader;
                    reader.Position = ResolveAbsoluteInPlaceOffset(inst.file, mbInfo, log, "TMP_FontAsset read");
                    rawMb = reader.ReadBytes(checked((int)mbInfo.ByteSize));
                }

                if (tmpId == 7296 && rawMb != null)
                {
                    try
                    {
                        log?.Add("[TMP7296] raw size=" + rawMb.Length);
                        if (rawMb.Length >= 0x104 + 4)
                        {
                            var glyphCount = BitConverter.ToInt32(rawMb, 0x100);
                            log?.Add("[TMP7296] glyphCount@0x100=" + glyphCount);
                            for (int stride = 48; stride <= 56; stride += 4)
                            {
                                if (0x104 + stride + 4 > rawMb.Length)
                                    break;
                                var idx0 = BitConverter.ToInt32(rawMb, 0x104);
                                var idx1 = BitConverter.ToInt32(rawMb, 0x104 + stride);
                                log?.Add("[TMP7296] stride=" + stride + ": idx[0]=" + idx0 + " idx[1]=" + idx1);
                            }
                        }

                        if (rawMb.Length >= 0x940 + 4)
                        {
                            var cnt = BitConverter.ToInt32(rawMb, 0x93C);
                            log?.Add("[7296] count@0x93C=" + cnt);
                            var idx0 = BitConverter.ToInt32(rawMb, 0x940);
                            log?.Add("[7296] firstIdx@0x940=" + idx0);
                            for (int stride = 44; stride <= 56; stride += 4)
                            {
                                if (0x940 + stride + 4 > rawMb.Length)
                                    break;
                                var i1 = BitConverter.ToInt32(rawMb, 0x940 + stride);
                                log?.Add("[7296] stride=" + stride + ": idx[1]@0x"
                                    + (0x940 + stride).ToString("X", CultureInfo.InvariantCulture) + "=" + i1);
                            }
                        }

                        // Эвристика: поиск возможного count GlyphTable в диапазоне 0x00..0x200
                        var max = Math.Min(0x200, rawMb.Length - 8);
                        for (int off = 0; off < max; off += 4)
                        {
                            var cnt = BitConverter.ToInt32(rawMb, off);
                            if (cnt >= 50 && cnt <= 2000)
                            {
                                var nextInt = BitConverter.ToInt32(rawMb, off + 4);
                                log?.Add("[FindGlyph] offset=0x" + off.ToString("X", CultureInfo.InvariantCulture)
                                         + " count=" + cnt
                                         + " nextInt=" + nextInt);
                            }
                        }

                        // Поиск конкретного значения 527 (0x20F) в диапазоне 0x1000..0x7000
                        var rangeStart = Math.Min(0x1000, rawMb.Length);
                        var rangeEnd = Math.Min(0x7000, rawMb.Length - 4);
                        for (int off = rangeStart; off <= rangeEnd; off += 4)
                        {
                            var v = BitConverter.ToInt32(rawMb, off);
                            if (v == 527)
                                log?.Add("[Find527] offset=0x" + off.ToString("X", CultureInfo.InvariantCulture) + " value=527");
                        }

                        // Поиск паттерна: count(int32), затем firstGlyphIndex(int32) маленький (0..49)
                        // (сканируем весь raw, но лог ограничим первыми 50 совпадениями)
                        var hits = 0;
                        for (int off = 0; off < rawMb.Length - 8; off += 4)
                        {
                            var cnt = BitConverter.ToInt32(rawMb, off);
                            if (cnt > 50 && cnt < 3000)
                            {
                                var firstIdx = BitConverter.ToInt32(rawMb, off + 4);
                                if (firstIdx >= 0 && firstIdx < 50)
                                {
                                    log?.Add("[FindGlyphTable] offset=0x" + off.ToString("X", CultureInfo.InvariantCulture)
                                             + " count=" + cnt + " firstIdx=" + firstIdx);
                                    hits++;
                                    if (hits >= 50)
                                    {
                                        log?.Add("[FindGlyphTable] ... truncated (50 hits).");
                                        break;
                                    }
                                }
                            }
                        }

                        // Вердикт stride: правильный stride даёт idx[1] в диапазоне [0, glyphCount)
                        if (rawMb.Length >= 0x940 + 4)
                        {
                            var glyphCnt = BitConverter.ToInt32(rawMb, 0x93C);
                            var idx0v = BitConverter.ToInt32(rawMb, 0x940);
                            log?.Add("[7296] GlyphTable stride verdict (count=" + glyphCnt + ", idx[0]=" + idx0v + "):");
                            for (int stride = 44; stride <= 56; stride += 4)
                            {
                                if (0x940 + stride + 4 > rawMb.Length) break;
                                var idx1v = BitConverter.ToInt32(rawMb, 0x940 + stride);
                                var ok = glyphCnt > 0 && idx1v >= 0 && idx1v < glyphCnt && idx1v != idx0v;
                                log?.Add("[7296] stride=" + stride + " idx[1]=" + idx1v
                                    + (ok ? " ← разумный" : " (вне 0.." + (glyphCnt - 1) + ")"));
                            }
                        }

                        // CharacterTable эвристический поиск для 7296
                        if (TmpFontAssetIl2CppRawMetadataPatcher.TryFindCharacterTable(
                                rawMb, out var ct7296CountOff, out var ct7296EntryOff, out var ct7296Count))
                        {
                            log?.Add("[7296 CT] CharacterTable найдена: count=" + ct7296Count
                                + " @0x" + ct7296CountOff.ToString("X", CultureInfo.InvariantCulture)
                                + " entries@0x" + ct7296EntryOff.ToString("X", CultureInfo.InvariantCulture));
                            for (var i = 0; i < Math.Min(5, ct7296Count); i++)
                            {
                                var eoff = ct7296EntryOff + i * 16;
                                if (eoff + 16 > rawMb.Length) break;
                                var u = BitConverter.ToUInt32(rawMb, eoff);
                                var gi = BitConverter.ToUInt32(rawMb, eoff + 4);
                                var sc = BitConverter.ToSingle(rawMb, eoff + 8);
                                log?.Add("[7296 CT] entry[" + i + "] U+"
                                    + u.ToString("X4", CultureInfo.InvariantCulture)
                                    + " gi=" + gi
                                    + " sc=" + sc.ToString("G4", CultureInfo.InvariantCulture));
                            }
                        }
                        else
                        {
                            log?.Add("[7296 CT] CharacterTable НЕ найдена эвристически.");
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Add("[TMP7296] diagnostics failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                if (!metadataAtlasSizeOnly)
                {
                    LogOriginalObjectHashes(rawMb, originalFileBytes, assetsPath, tmpId, log);
                    LogOriginalCharacterTableEntries(rawMb, log);
                }

                // ВАЖНО (лог 2025-05-30): для 7296 offset'ы Apply (m_AtlasWidth 0x15cc, charset 0x1efc и т.д.)
                // 7295-калибровки попадают ВНУТРЬ m_GlyphTable (250 записей, 0xF8..0x33C0) и разрушают глифы —
                // это и был источник крашей. Атлас уже 512×512, поэтому метаданные не трогаем.
                if (tmpId == 7296)
                {
                    log?.Add("[TMP atlas PNG] PathID=7296: метаданные Apply ПРОПУЩЕНЫ "
                        + "(0x15cc/0x1efc лежат внутри glyph table и портят её). Атлас 512×512 уже соответствует.");
                }
                else
                {
                    TmpFontAssetIl2CppRawMetadataPatcher.Apply(
                        rawMb,
                        atlasW,
                        atlasH,
                        il2CppCreationSettingsCharsetAscii,
                        log,
                        atlasSizeOnly: metadataAtlasSizeOnly);
                    log?.Add(
                        "[TMP atlas PNG] IL2CPP raw PathID=" + tmpId + ": m_AtlasWidth/Height 0x15cc/0x15d0, creationSettings 0x1eec/0x1ef0"
                        + (metadataAtlasSizeOnly ? " (charset пропущен)." : ", charset 0x1efc×50")
                        + " → " + atlasW + "×" + atlasH + ".");
                }

                // 7296: glyph table локализуется самолокацией внутри BuildPatched… (count@0xF4, entries@0xF8,
                // stride 52, m_Index = позиция+3). Маркер 0x58 ниже — только сигнал «это 7296».
                var glyphTableCountOffset = tmpId == 7296 ? 0x58 : 0x100;

                if (skipGlyphPatch)
                {
                    log?.Add("[Patch test] GlyphRect не патчился (skipGlyphPatch=true).");
                }
                else if (growCyrillicTables && tmpId == 7296
                         && !string.IsNullOrWhiteSpace(atlasJsonForCharacterTable) && File.Exists(atlasJsonForCharacterTable))
                {
                    // Рост: добавляем кириллические записи в m_GlyphTable + m_CharacterTable (объект растёт → полная перезапись).
                    var before = rawMb.Length;
                    rawMb = BuildGrownTablesWithCyrillic(rawMb, atlasJsonForCharacterTable, log, out growAtlasPathId, markerCyrillicGlyphs);
                    grew = rawMb.Length != before;
                }
                else if (skipCharTable)
                {
                    if (!string.IsNullOrWhiteSpace(atlasJsonForCharacterTable) && File.Exists(atlasJsonForCharacterTable))
                    {
                        rawMb = BuildPatchedGlyphTableFromExistingCharacterTable(
                            rawMb,
                            atlasJsonForCharacterTable,
                            glyphTableCountOffset,
                            log);
                    }
                    else if (!string.IsNullOrWhiteSpace(atlasJsonForCharacterTable))
                    {
                        log?.Add("[TMP GlyphTable] JSON не найден — CharacterTable и GlyphTable не менялись: " + atlasJsonForCharacterTable);
                    }
                    else
                    {
                        log?.Add("[TMP GlyphTable] JSON атласа не передан — CharacterTable не менялась, GlyphRect не обновлялись.");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(atlasJsonForCharacterTable) && File.Exists(atlasJsonForCharacterTable))
                {
                    rawMb = BuildPatchedCharacterTableFromRawAndJson(rawMb, atlasJsonForCharacterTable, log);
                }
                else if (!string.IsNullOrWhiteSpace(atlasJsonForCharacterTable))
                {
                    log?.Add("[TMP CharTable] JSON не найден по пути — таблица символов не менялась: " + atlasJsonForCharacterTable);
                }
                else
                {
                    log?.Add("[TMP CharTable] JSON атласа не передан — m_CharacterTable не пересобиралась.");
                }

                if (!grew)
                {
                    var tmpSize = checked((int)mbInfo.ByteSize);
                    if (rawMb == null || rawMb.Length != tmpSize)
                    {
                        throw new InvalidOperationException(
                            "TMP_FontAsset in-place: размер новых байт не совпал с исходным ByteSize. old="
                            + tmpSize + ", new=" + (rawMb == null ? 0 : rawMb.Length) + ".");
                    }
                }
            }

            if (grew)
            {
                // Объект вырос — нельзя in-place. Полная перезапись через AssetsFileWriter (пересчёт offset/size).
                if (texInfo != null && rawTex != null)
                    texInfo.SetNewData(rawTex);
                if (mbInfo != null && rawMb != null)
                    mbInfo.SetNewData(rawMb);

                // Патчим АТЛАС ШРИФТА (m_AtlasTextures[0], напр. 406, 1024) inline нашим атласом — TMP рендерит
                // именно эту текстуру (через материал). Размер atlas должен совпадать с PNG (1024×1024).
                if (growAtlasPathId != 0 && alpha8.Alpha8 != null)
                {
                    var atlasInfo = inst.file.AssetInfos.FirstOrDefault(x => x.PathId == growAtlasPathId)
                                    ?? inst.file.GetAssetInfo(growAtlasPathId);
                    if (atlasInfo == null)
                        throw new InvalidOperationException("Рост: атлас-текстура PathID=" + growAtlasPathId + " не найдена.");

                    var atlasBase = manager.GetBaseField(inst, atlasInfo);
                    var prevW = ReadIntFieldOrDefault(atlasBase, "m_Width");
                    var prevH = ReadIntFieldOrDefault(atlasBase, "m_Height");
                    if (alpha8.Width != prevW || alpha8.Height != prevH)
                        log?.Add("[Grow] ВНИМАНИЕ: PNG " + alpha8.Width + "×" + alpha8.Height
                            + " ≠ атлас 406 " + prevW + "×" + prevH + " — нужен атлас того же размера!");
                    PatchTexture2DWithAlpha8(atlasBase, alpha8, log);
                    atlasInfo.SetNewData(atlasBase.WriteToByteArray());
                    log?.Add("[Grow] Атлас-текстура PathID=" + growAtlasPathId + " пропатчена inline "
                        + alpha8.Width + "×" + alpha8.Height + " (был " + prevW + "×" + prevH + ").");

                    // Подгоняем _GradientScale материала(ов) под distanceRange нашего атласа — чтобы края SDF
                    // совпали (иначе тонкий/двойной ореол). distanceRange берём из JSON атласа.
                    var distRange = ReadAtlasDistanceRange(atlasJsonForCharacterTable, 6f);
                    PatchMaterialsGradientScaleForAtlas(manager, inst, growAtlasPathId, distRange, log);
                }

                WriteAssetsFileToPath(inst.file, outputPath);
                log?.Add("[Grow] Полная перезапись .assets (объект TMP вырос): " + outputPath);

                var grownBytes = File.ReadAllBytes(outputPath);
                log?.Add("[Header patch] " + BitConverter.ToString(grownBytes, 0, Math.Min(32, grownBytes.Length)));
                log?.Add(
                    "[TMP atlas PNG] Готово (рост): " + outputPath + "; TMP_FontAsset PathID=" + il2CppTmpFontPathId.Value
                    + "; atlas=" + atlasW + "×" + atlasH + ".");
                return;
            }

            var inPlacePatches = new List<(AssetFileInfo Info, byte[] Data, string Label)>();
            if (texInfo != null && rawTex != null)
                inPlacePatches.Add((texInfo, rawTex, "Texture2D"));
            if (mbInfo != null && rawMb != null)
                inPlacePatches.Add((mbInfo, rawMb, "TMP_FontAsset"));

            WritePatchedObjectsInPlace(
                assetsPath,
                outputPath,
                inst.file,
                log,
                inPlacePatches.ToArray());

            LogBinaryDiffFirst10(assetsPath, outputPath, log);

            var patchedFileBytes = File.ReadAllBytes(outputPath);
            log?.Add("[Header patch] " + BitConverter.ToString(patchedFileBytes, 0, Math.Min(32, patchedFileBytes.Length)));

            log?.Add(
                "[TMP atlas PNG] Готово: " + outputPath + "; TMP_FontAsset PathID=" + il2CppTmpFontPathId.Value + "; atlas="
                + atlasW + "×" + atlasH + ".");
        }


        /// <summary>Пересборка <c>m_CharacterTable</c> в буфере MonoBehaviour: count@0xB94, записи@0xB9C, хвост с 0xEDC.</summary>
        private static byte[] BuildPatchedCharacterTableFromRawAndJson(byte[] raw, string atlasJsonPath, ICollection<string> log)
        {
            try
            {
                const int tableCountOffset = 0xB94;
                const int tableEntriesOffset = 0xB9C;
                const int tableTailOffset = 0xEDC;
                const int entrySize = 16;
                const int glyphTableBaseOffset = 0x104;
                const int glyphEntrySize = GlyphTableEntrySize;
                const int glyphRectOffsetInEntry = 36;

                if (raw == null || raw.Length < tableTailOffset)
                    throw new InvalidOperationException("Сырой объект слишком короткий для фиксированных смещений m_CharacterTable.");

                var oldSize = raw.Length;
                var fixedTableBytes = tableTailOffset - tableEntriesOffset;
                var maxEntriesThatFit = fixedTableBytes / entrySize;

                var atlasJsonText = File.ReadAllText(atlasJsonPath);
                log?.Add("[JSON preview] " + atlasJsonText.Substring(0, Math.Min(300, atlasJsonText.Length)));
                var json = JObject.Parse(atlasJsonText);
                var glyphs = ResolveGlyphsArray(json);
                if (glyphs == null || glyphs.Count == 0)
                    throw new InvalidOperationException("В JSON нет glyphs/variants[0].glyphs.");

                var map = BuildUnicodeGlyphIndexMap(glyphs);
                if (map.Count == 0)
                    throw new InvalidOperationException("В JSON не найдено пар unicode+glyphIndex.");
                var rectMap = BuildUnicodeAtlasRectMap(glyphs);

                var cyrillic = map.Where(k => k.Key >= 0x0400 && k.Key <= 0x04FF)
                    .OrderBy(k => k.Key)
                    .ToList();
                var basic = map.Where(k => k.Key == 0x20 || k.Key == 0x2E || k.Key == 0x2C)
                    .OrderBy(k => k.Key)
                    .ToList();
                var prioritized = cyrillic
                    .Concat(basic.Where(b => !cyrillic.Any(c => c.Key == b.Key)))
                    .ToList();
                var orderedAll = prioritized
                    .OrderBy(k => k.Key)
                    .ToList();
                var dropped = Math.Max(0, orderedAll.Count - maxEntriesThatFit);
                var ordered = orderedAll.Take(maxEntriesThatFit).ToList();
                var newCount = ordered.Count;

                log?.Add("[CharTable] Записанные unicode: " + string.Join(", ",
                    ordered.Take(52).Select(k => "U+" + k.Key.ToString("X4", CultureInfo.InvariantCulture) + "(" + (char)k.Key + ")")));

                var patched = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, patched, 0, raw.Length);

                // In-place: объект не растёт, хвост после 0xEDC не сдвигается.
                Array.Clear(patched, tableEntriesOffset, fixedTableBytes);
                WriteInt32LittleEndian(patched, tableCountOffset, newCount);

                for (var i = 0; i < ordered.Count; i++)
                {
                    var off = tableEntriesOffset + i * entrySize;
                    WriteInt32LittleEndian(patched, off, ordered[i].Key);
                    WriteInt32LittleEndian(patched, off + 4, ordered[i].Value);
                    WriteFloat32LittleEndian(patched, off + 8, 1.0f);
                    WriteInt32LittleEndian(patched, off + 12, 0);
                }

                var glyphPatched = 0;
                foreach (var item in ordered)
                {
                    if (!rectMap.TryGetValue(item.Key, out var rect))
                        continue;

                    var glyphIndex = item.Value;
                    var glyphRectOffset = glyphTableBaseOffset + glyphIndex * glyphEntrySize + glyphRectOffsetInEntry;
                    if (glyphRectOffset < 0 || glyphRectOffset + 16 > patched.Length)
                        continue;

                    WriteInt32LittleEndian(patched, glyphRectOffset + 0, rect.X);
                    WriteInt32LittleEndian(patched, glyphRectOffset + 4, rect.Y);
                    WriteInt32LittleEndian(patched, glyphRectOffset + 8, rect.W);
                    WriteInt32LittleEndian(patched, glyphRectOffset + 12, rect.H);
                    glyphPatched++;
                }

                if (dropped > 0)
                {
                    log?.Add(
                        "[TMP CharTable] Внимание: таблица фиксированного размера, помещается только "
                        + maxEntriesThatFit + " записей; отброшено " + dropped + ".");
                }
                log?.Add("[TMP GlyphTable] Обновлено UV/rect записей: " + glyphPatched + ".");

                log?.Add(
                    "[TMP CharTable] Записано " + newCount + " символов, offset 0xb94, размер файла изменился с "
                    + oldSize + " до " + patched.Length + " байт.");

                return patched;
            }
            catch (Exception ex)
            {
                log?.Add("[JSON ERROR] " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                throw;
            }
        }

        private static byte[] BuildPatchedGlyphTableFromExistingCharacterTable(
            byte[] raw,
            string atlasJsonPath,
            int glyphTableCountOffset,
            ICollection<string> log)
        {
            try
            {
                // Структура 7296 (лог 2025-05-30): m_GlyphTable — count@0xF4=250, entries@0xF8, stride 52,
                // GlyphRect@+24. m_Index идёт 3,4,5,… (НЕ с нуля), поэтому glyphIndex ≠ позиция —
                // строим карту m_Index→offset. CharacterTable идёт сразу за glyph table.
                // Метаданные Apply для 7296 НЕ применяются (их 7295-offsets рушат glyph table).
                // Для 7295 (glyphTableCountOffset=0x100) — прежние проверенные offset'ы.
                const int maxExistingEntries = 52;
                const int glyphEntrySize = GlyphTableEntrySize; // 52

                if (raw == null)
                    throw new InvalidOperationException("Сырой объект пуст.");

                int tableCountOffset;
                int tableEntriesOffset;
                int entrySize;
                int glyphRectOffsetInEntry;
                int glyphTableBaseOffset;
                int effectiveGlyphCountOffset;
                int charUnicodeFieldOffset;     // смещение m_Unicode внутри записи CharacterTable
                int charGlyphIndexFieldOffset;  // смещение m_GlyphIndex внутри записи CharacterTable
                Dictionary<int, int> glyphIndexToEntryOffset = null; // только для 7296
                if (glyphTableCountOffset == 0x58)
                {
                    // PathID 7296: локализуем начало GlyphTable; glyphCount берём из поля размера (entriesStart-4).
                    LogRawRegionDump(raw, 0x00, 0x100, "[7296 GThdr]", log);
                    if (!TryLocateGlyphTable7296(raw, log, out var entriesStart, out var runLen))
                    {
                        // Не бросаем: даём прогону завершиться и собрать диагностику. Патч пропускаем.
                        log?.Add("[7296 GT] GlyphTable не локализована — GlyphRect не патчился. "
                            + "Проверьте [7296 GThdr]/[GlyphArea]: возможно, патчится уже изменённый/другой файл.");
                        return raw;
                    }

                    // Размер вектора лежит непосредственно перед entries (для этого ассета — 0xF4=250).
                    var glyphCount = entriesStart >= 4 ? ReadInt32LittleEndian(raw, entriesStart - 4) : runLen;
                    if (glyphCount < 1 || glyphCount > 5000 || entriesStart + glyphCount * glyphEntrySize + 4 > raw.Length)
                    {
                        log?.Add("[7296 GT] count поле @0x" + Math.Max(0, entriesStart - 4).ToString("X", CultureInfo.InvariantCulture)
                            + "=" + glyphCount + " неправдоподобен — использую длину ряда run=" + runLen + ".");
                        glyphCount = runLen;
                    }

                    glyphRectOffsetInEntry = 24;
                    glyphTableBaseOffset = entriesStart;
                    effectiveGlyphCountOffset = entriesStart - 4;

                    // Карта m_Index → offset записи (m_Index не равен позиции: начинается с 3).
                    glyphIndexToEntryOffset = new Dictionary<int, int>(glyphCount);
                    for (var p = 0; p < glyphCount; p++)
                    {
                        var entryOff = entriesStart + p * glyphEntrySize;
                        if (entryOff + glyphEntrySize > raw.Length)
                            break;
                        glyphIndexToEntryOffset[ReadInt32LittleEndian(raw, entryOff)] = entryOff;
                    }

                    tableCountOffset = entriesStart + glyphCount * glyphEntrySize;
                    if (tableCountOffset + 4 > raw.Length)
                        throw new InvalidOperationException("CharacterTable count выходит за raw (off=0x"
                            + tableCountOffset.ToString("X", CultureInfo.InvariantCulture) + ").");

                    var ctCount = ReadInt32LittleEndian(raw, tableCountOffset);
                    tableEntriesOffset = tableCountOffset + 4;
                    // Запись CharacterTable 7296 = 16 байт: m_ElementType(+0), m_Unicode(+4), m_GlyphIndex(+8), m_Scale(+12).
                    entrySize = 16;
                    charUnicodeFieldOffset = 4;
                    charGlyphIndexFieldOffset = 8;
                    log?.Add("[TMP CharTable] 7296: glyphCount=" + glyphCount + " (run=" + runLen + ")"
                        + ", GlyphTable entries@0x" + entriesStart.ToString("X", CultureInfo.InvariantCulture)
                        + " m_Index map=" + glyphIndexToEntryOffset.Count
                        + ", CharacterTable count@0x" + tableCountOffset.ToString("X", CultureInfo.InvariantCulture)
                        + "=" + ctCount
                        + " entries@0x" + tableEntriesOffset.ToString("X", CultureInfo.InvariantCulture)
                        + " entrySize=16 (elementType,unicode,glyphIndex,scale)");
                    LogCharacterTablePreview(raw, tableEntriesOffset, ctCount, entrySize, charUnicodeFieldOffset, charGlyphIndexFieldOffset, log);
                }
                else
                {
                    glyphRectOffsetInEntry = 4;
                    glyphTableBaseOffset = glyphTableCountOffset + 4;
                    effectiveGlyphCountOffset = glyphTableCountOffset;
                    tableCountOffset = 0xB94;
                    tableEntriesOffset = 0xB9C;
                    entrySize = 16;
                    charUnicodeFieldOffset = 0;
                    charGlyphIndexFieldOffset = 4;
                }

                if (raw.Length < tableEntriesOffset + entrySize)
                    throw new InvalidOperationException("Сырой объект слишком короткий для чтения существующего m_CharacterTable.");

                var atlasJsonText = File.ReadAllText(atlasJsonPath);
                log?.Add("[JSON preview] " + atlasJsonText.Substring(0, Math.Min(300, atlasJsonText.Length)));
                var json = JObject.Parse(atlasJsonText);
                var glyphs = ResolveGlyphsArray(json);
                if (glyphs == null || glyphs.Count == 0)
                    throw new InvalidOperationException("В JSON нет glyphs/variants[0].glyphs.");

                var rectMap = BuildUnicodeAtlasRectMap(glyphs);
                var orderedCyrillic = rectMap
                    .Where(k => k.Key >= 0x0400 && k.Key <= 0x04FF)
                    .OrderBy(k => k.Key)
                    .ToList();
                if (orderedCyrillic.Count == 0)
                    throw new InvalidOperationException("В JSON не найдено кириллических glyphs с atlasBounds.");

                var existingCount = ReadInt32LittleEndian(raw, tableCountOffset);

                LogGlyphTableStructureDiagnostics(raw, log, "before patch", effectiveGlyphCountOffset, glyphEntrySize);

                var patched = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, patched, 0, raw.Length);

                var glyphPatched = 0;

                if (glyphIndexToEntryOffset != null)
                {
                    // 7296: точное сопоставление по m_Unicode. Для каждой записи CharacterTable, чей символ
                    // есть в новом атласе (JSON rectMap), обновляем GlyphRect её глифа (через карту m_Index→offset).
                    // m_CharacterTable не меняется (in-place), glyphIndex берётся из неё.
                    var matched = 0;
                    var cyrillicMatched = 0;
                    var logged = 0;
                    var entriesToScan = Math.Min(existingCount, (raw.Length - tableEntriesOffset) / entrySize);
                    for (var e = 0; e < entriesToScan; e++)
                    {
                        var off = tableEntriesOffset + e * entrySize;
                        var unicode = ReadInt32LittleEndian(raw, off + charUnicodeFieldOffset);
                        var glyphIndex = ReadInt32LittleEndian(raw, off + charGlyphIndexFieldOffset);
                        if (!rectMap.TryGetValue(unicode, out var rect))
                            continue;

                        matched++;
                        var isCyr = unicode >= 0x0400 && unicode <= 0x04FF;
                        if (isCyr)
                            cyrillicMatched++;

                        if (!glyphIndexToEntryOffset.TryGetValue(glyphIndex, out var glyphEntryOffset))
                        {
                            if (logged < 8)
                                log?.Add("[TMP GlyphTable] Пропуск U+" + unicode.ToString("X4", CultureInfo.InvariantCulture)
                                    + " glyphIndex=" + glyphIndex + " — нет в карте m_Index.");
                            continue;
                        }

                        var glyphRectOffset = glyphEntryOffset + glyphRectOffsetInEntry;
                        if (glyphRectOffset < 0 || glyphRectOffset + 16 > patched.Length)
                            continue;

                        WriteInt32LittleEndian(patched, glyphRectOffset + 0, rect.X);
                        WriteInt32LittleEndian(patched, glyphRectOffset + 4, rect.Y);
                        WriteInt32LittleEndian(patched, glyphRectOffset + 8, rect.W);
                        WriteInt32LittleEndian(patched, glyphRectOffset + 12, rect.H);
                        glyphPatched++;

                        if (isCyr && logged < 8)
                        {
                            logged++;
                            log?.Add("[TMP GlyphTable] U+" + unicode.ToString("X4", CultureInfo.InvariantCulture)
                                + " -> glyphIndex=" + glyphIndex
                                + " rect=" + rect.X + "," + rect.Y + "," + rect.W + "," + rect.H
                                + " @0x" + glyphRectOffset.ToString("X", CultureInfo.InvariantCulture));
                        }
                    }

                    LogGlyphTableStructureDiagnostics(patched, log, "after patch", effectiveGlyphCountOffset, glyphEntrySize);
                    log?.Add("[TMP CharTable] 7296: CharacterTable не менялась. Совпало по unicode с атласом: "
                        + matched + " (кириллических " + cyrillicMatched + "), обновлено GlyphRect: " + glyphPatched + ".");
                    if (cyrillicMatched == 0)
                        log?.Add("[TMP CharTable] ВНИМАНИЕ: в CharacterTable шрифта НЕТ кириллических unicode (0x0400-0x04FF) — "
                            + "кириллица не отобразится без добавления записей (рост таблицы in-place запрещён).");

                    return patched;
                }

                // --- 7295 (legacy): переиспользование первых 52 слотов CharacterTable ---
                var slotsToUse = Math.Min(Math.Min(existingCount, maxExistingEntries), orderedCyrillic.Count);
                int? firstSlotGlyphIndex = null;
                for (var i = 0; i < slotsToUse; i++)
                {
                    var charEntryOffset = tableEntriesOffset + i * entrySize;
                    if (charEntryOffset < 0 || charEntryOffset + entrySize > raw.Length)
                        break;

                    var existingUnicode = ReadInt32LittleEndian(raw, charEntryOffset + charUnicodeFieldOffset);
                    var glyphIndex = ReadInt32LittleEndian(raw, charEntryOffset + charGlyphIndexFieldOffset);
                    var target = orderedCyrillic[i];
                    var glyphRectOffset = glyphTableBaseOffset + glyphIndex * glyphEntrySize + glyphRectOffsetInEntry;
                    if (glyphRectOffset < 0 || glyphRectOffset + 16 > patched.Length)
                    {
                        log?.Add("[TMP GlyphTable] Пропуск slot=" + i + ", glyphIndex=" + glyphIndex + " — выход за границы raw.");
                        continue;
                    }

                    WriteInt32LittleEndian(patched, glyphRectOffset + 0, target.Value.X);
                    WriteInt32LittleEndian(patched, glyphRectOffset + 4, target.Value.Y);
                    WriteInt32LittleEndian(patched, glyphRectOffset + 8, target.Value.W);
                    WriteInt32LittleEndian(patched, glyphRectOffset + 12, target.Value.H);
                    glyphPatched++;

                    if (firstSlotGlyphIndex == null)
                        firstSlotGlyphIndex = glyphIndex;

                    log?.Add(
                        "[TMP GlyphTable] slot=" + i
                        + " char=U+" + existingUnicode.ToString("X4", CultureInfo.InvariantCulture)
                        + " -> glyphIndex=" + glyphIndex
                        + " uses U+" + target.Key.ToString("X4", CultureInfo.InvariantCulture));
                }

                LogGlyphTableStructureDiagnostics(patched, log, "after patch", effectiveGlyphCountOffset, glyphEntrySize);

                log?.Add("[TMP CharTable] Оставлена без изменений. Использовано существующих записей: " + slotsToUse + ".");
                log?.Add("[TMP GlyphTable] Обновлено GlyphRect записей: " + glyphPatched + ".");

                return patched;
            }
            catch (Exception ex)
            {
                log?.Add("[GLYPH JSON ERROR] " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                throw;
            }
        }

        /// <summary>
        /// Рост таблиц 7296: добавляет кириллические символы (0x0400-0x04FF) из JSON-атласа,
        /// которых нет в m_CharacterTable шрифта, как новые записи m_GlyphTable + m_CharacterTable.
        /// Объект РАСТЁТ → результат пишется не in-place. Раскладки:
        ///   Glyph(52): m_Index(4) | width,height,bearX,bearY,advance(5×f=20) | rect x,y,w,h(4×i=16) | scale(4) | atlasIndex(4) | classDef(4)
        ///   Char(16):  m_ElementType(4)=1 | m_Unicode(4) | m_GlyphIndex(4) | m_Scale(4)=1.0
        /// </summary>
        private static byte[] BuildGrownTablesWithCyrillic(byte[] raw, string atlasJsonPath, ICollection<string> log, out long atlasTexturePathId, bool markerCyrillic = false)
        {
            atlasTexturePathId = 0;
            const int glyphStride = GlyphTableEntrySize; // 52
            const int charStride = 16;

            if (!TryLocateGlyphTable7296(raw, log, out var glyphEntriesOff, out _))
                throw new InvalidOperationException("Рост: GlyphTable 7296 не локализована.");

            var glyphCountOff = glyphEntriesOff - 4;
            if (glyphCountOff < 0)
                throw new InvalidOperationException("Рост: некорректный glyphCountOff.");
            var glyphCount = ReadInt32LittleEndian(raw, glyphCountOff);
            if (glyphCount < 1 || glyphCount > 20000)
                throw new InvalidOperationException("Рост: неправдоподобный glyphCount=" + glyphCount + ".");

            var glyphTableEnd = glyphEntriesOff + glyphCount * glyphStride;
            var charCountOff = glyphTableEnd;
            var charEntriesOff = charCountOff + 4;
            if (charEntriesOff > raw.Length)
                throw new InvalidOperationException("Рост: CharacterTable count вне raw.");
            var charCount = ReadInt32LittleEndian(raw, charCountOff);
            if (charCount < 0 || charCount > 50000)
                throw new InvalidOperationException("Рост: неправдоподобный charCount=" + charCount + ".");
            var charTableEnd = charEntriesOff + charCount * charStride;
            if (charTableEnd > raw.Length)
                throw new InvalidOperationException("Рост: CharacterTable выходит за raw.");

            // m_AtlasTextures[0].PathID лежит сразу за таблицами: count@+0, PPtr m_FileID@+4, m_PathID@+8.
            if (charTableEnd + 12 <= raw.Length)
                atlasTexturePathId = ReadInt32LittleEndian(raw, charTableEnd + 8);
            log?.Add("[Grow] Атлас-текстура шрифта: PathID=" + atlasTexturePathId + " (m_AtlasTextures[0]).");

            // Существующие unicode (charTable @+4) и максимальный m_Index глифов.
            var existingUnicodes = new HashSet<int>();
            var maxIndex = -1;
            var maxRectExtent = 0;
            for (var p = 0; p < glyphCount; p++)
            {
                var o = glyphEntriesOff + p * glyphStride;
                maxIndex = Math.Max(maxIndex, ReadInt32LittleEndian(raw, o));
                var rxw = ReadInt32LittleEndian(raw, o + 24) + ReadInt32LittleEndian(raw, o + 32);
                var ryh = ReadInt32LittleEndian(raw, o + 28) + ReadInt32LittleEndian(raw, o + 36);
                maxRectExtent = Math.Max(maxRectExtent, Math.Max(rxw, ryh));
            }
            for (var c = 0; c < charCount; c++)
                existingUnicodes.Add(ReadInt32LittleEndian(raw, charEntriesOff + c * charStride + 4));

            // Диагностика размера ИСХОДНОГО атласа: если max rect-координата > 512, исходный атлас крупнее,
            // и m_AtlasWidth шрифта ≠ 512 → UV нашего 512-атласа считается неверно (вероятная причина каши).
            log?.Add("[Grow] Макс. rect-координата исходных глифов=" + maxRectExtent
                + (maxRectExtent > 512 ? " (>512 → исходный атлас крупнее, m_AtlasWidth≠512!)" : " (≤512)"));
            LogRawRegionDump(raw, charTableEnd, Math.Min(charTableEnd + 0x40, raw.Length), "[Grow suffix]", log);
            // Ищем во ВСЁМ хвосте: m_AtlasWidth/Height (1024/512), а также ссылки на атлас-текстуру (406/404).
            for (var off = charTableEnd; off + 4 <= raw.Length; off += 4)
            {
                var v = ReadInt32LittleEndian(raw, off);
                if (v == 512 || v == 1024 || v == 2048 || v == 4096)
                    log?.Add("[Grow] val=" + v + " @0x" + off.ToString("X", CultureInfo.InvariantCulture)
                        + " (rel+0x" + (off - charTableEnd).ToString("X", CultureInfo.InvariantCulture) + ")");
                else if (v == 406 || v == 404)
                    log?.Add("[Grow] PathID-ref=" + v + " @0x" + off.ToString("X", CultureInfo.InvariantCulture)
                        + " (rel+0x" + (off - charTableEnd).ToString("X", CultureInfo.InvariantCulture) + ")");
            }

            var json = JObject.Parse(File.ReadAllText(atlasJsonPath));
            var glyphsTok = ResolveGlyphsArray(json);
            if (glyphsTok == null || glyphsTok.Count == 0)
                throw new InvalidOperationException("Рост: в JSON нет glyphs.");

            // Метрики масштабируются на m_PointSize шрифта (НЕ на размер атласа): SDF чёткий при любом
            // размере, важно совпадение метрик с pointSize существующих глифов. Калибруем по общим латинским:
            // pointSize = median(metricHeight / planeBoundsHeight).
            var planeHByUnicode = new Dictionary<int, double>();
            foreach (var t in glyphsTok)
            {
                if (!(t is JObject g) || !TryReadUnicode(g, out var u)) continue;
                if (!(g["planeBounds"] is JObject pbj)) continue;
                var h = Num(pbj, "top") - Num(pbj, "bottom");
                if (h > 0.0001) planeHByUnicode[u] = h;
            }
            var idxToOff = new Dictionary<int, int>(glyphCount);
            for (var p = 0; p < glyphCount; p++)
            {
                var o = glyphEntriesOff + p * glyphStride;
                idxToOff[ReadInt32LittleEndian(raw, o)] = o;
            }
            var ratios = new List<double>();
            for (var c = 0; c < charCount; c++)
            {
                var co = charEntriesOff + c * charStride;
                var u = ReadInt32LittleEndian(raw, co + 4);
                var gi = ReadInt32LittleEndian(raw, co + 8);
                if (!planeHByUnicode.TryGetValue(u, out var planeH) || !idxToOff.TryGetValue(gi, out var go2)) continue;
                var metricH = BitConverter.ToSingle(raw, go2 + 8);
                if (metricH > 1f) ratios.Add(metricH / planeH);
            }
            double pxPerEm;
            if (ratios.Count > 0)
            {
                ratios.Sort();
                pxPerEm = ratios[ratios.Count / 2];
            }
            else
            {
                var atlasTok0 = json["atlas"] as JObject ?? new JObject();
                pxPerEm = atlasTok0["size"]?.Value<double>() ?? 36.0;
            }
            log?.Add("[Grow] Калиброванный pointSize=" + pxPerEm.ToString("F2", CultureInfo.InvariantCulture)
                + " (по " + ratios.Count + " общим латинским глифам).");

            // Кириллические глифы из JSON с atlasBounds, которых нет в шрифте (dedup по unicode).
            var seen = new HashSet<int>();
            var toAdd = new List<JObject>();
            foreach (var t in glyphsTok)
            {
                if (!(t is JObject g) || !TryReadUnicode(g, out var u))
                    continue;
                if (u < 0x0400 || u > 0x04FF || existingUnicodes.Contains(u) || !seen.Add(u))
                    continue;
                if (!(g["atlasBounds"] is JObject))
                    continue;
                toAdd.Add(g);
            }
            toAdd.Sort((a, b) =>
            {
                TryReadUnicode(a, out var ua);
                TryReadUnicode(b, out var ub);
                return ua.CompareTo(ub);
            });

            var n = toAdd.Count;
            log?.Add("[Grow] Кириллических к добавлению: " + n
                + " (glyphCount " + glyphCount + "→" + (glyphCount + n)
                + ", charCount " + charCount + "→" + (charCount + n) + ").");
            if (n == 0)
                return raw;

            var atlasHpx = (json["atlas"] as JObject)?["height"]?.Value<int>() ?? 512;

            // rect по unicode: x=left, y=bottom, w=right-left, h=top-bottom (yOrigin=bottom, текстура флипнута при загрузке).
            var rectByUnicode = new Dictionary<int, GlyphRectPatch>();
            foreach (var t in glyphsTok)
            {
                if (!(t is JObject g) || !TryReadUnicode(g, out var u))
                    continue;
                if (!(g["atlasBounds"] is JObject ab))
                    continue;
                double L = Num(ab, "left"), R = Num(ab, "right"), T = Num(ab, "top"), B = Num(ab, "bottom");
                // Инсет на 1px ВНУТРЬ ячейки (ceil/floor + ещё 1px): срезает перекрытие SDF-спреда соседа
                // на краю. Спред толстый (pxrange 6), потеря 1px контур не портит.
                var x = (int)Math.Ceiling(L) + 1;
                var yb = (int)Math.Ceiling(B) + 1;
                rectByUnicode[u] = new GlyphRectPatch(
                    x,
                    yb,
                    Math.Max(1, (int)Math.Floor(R) - 1 - x),
                    Math.Max(1, (int)Math.Floor(T) - 1 - yb));
            }

            // Ремап существующих глифов (латиница/цифры) на НОВЫЙ атлас — текстуру 404 заменили.
            var existingGlyphs = new byte[glyphCount * glyphStride];
            Buffer.BlockCopy(raw, glyphEntriesOff, existingGlyphs, 0, existingGlyphs.Length);
            var remapped = 0;
            for (var c = 0; c < charCount; c++)
            {
                var co = charEntriesOff + c * charStride;
                var u = ReadInt32LittleEndian(raw, co + 4);
                var gi = ReadInt32LittleEndian(raw, co + 8);
                if (!rectByUnicode.TryGetValue(u, out var rc) || !idxToOff.TryGetValue(gi, out var ao))
                    continue;
                var lo = ao - glyphEntriesOff + 24;
                if (lo < 0 || lo + 16 > existingGlyphs.Length)
                    continue;
                WriteInt32LittleEndian(existingGlyphs, lo + 0, rc.X);
                WriteInt32LittleEndian(existingGlyphs, lo + 4, rc.Y);
                WriteInt32LittleEndian(existingGlyphs, lo + 8, rc.W);
                WriteInt32LittleEndian(existingGlyphs, lo + 12, rc.H);
                remapped++;
            }
            log?.Add("[Grow] Ремап существующих глифов на новый атлас (флип Y): " + remapped + ".");

            var newGlyphBytes = new byte[n * glyphStride];
            var newCharBytes = new byte[n * charStride];

            // Диагностика (markerCyrillic): rect ЛАТИНСКОЙ 'A' (U+0041) подменяем на кириллическую 'Ж' (U+0416).
            // В игре 'A' покажет 'Ж' ⇒ шрифт 7296 рендерит латиницу (значит атлас/ремап/масштаб верны, баг в кириллице).
            // 'A' останется 'A' ⇒ латиница идёт из ДРУГОГО шрифта, и «латиница ок» нас вводило в заблуждение.
            var markerOn = markerCyrillic
                || string.Equals(Environment.GetEnvironmentVariable("TMP_CYR_MARKER"), "1", StringComparison.Ordinal);
            if (markerOn && rectByUnicode.TryGetValue(0x0416, out var zheRect))
            {
                for (var c = 0; c < charCount; c++)
                {
                    var co0 = charEntriesOff + c * charStride;
                    if (ReadInt32LittleEndian(raw, co0 + 4) != 0x0041)
                        continue;
                    var gi0 = ReadInt32LittleEndian(raw, co0 + 8);
                    if (idxToOff.TryGetValue(gi0, out var ao0))
                    {
                        var lo0 = ao0 - glyphEntriesOff + 24;
                        if (lo0 >= 0 && lo0 + 16 <= existingGlyphs.Length)
                        {
                            WriteInt32LittleEndian(existingGlyphs, lo0 + 0, zheRect.X);
                            WriteInt32LittleEndian(existingGlyphs, lo0 + 4, zheRect.Y);
                            WriteInt32LittleEndian(existingGlyphs, lo0 + 8, zheRect.W);
                            WriteInt32LittleEndian(existingGlyphs, lo0 + 12, zheRect.H);
                            log?.Add("[Grow] МАРКЕР: латинская 'A' rect → 'Ж' " + zheRect.X + "," + zheRect.Y + "," + zheRect.W + "," + zheRect.H);
                        }
                    }
                    break;
                }
            }

            for (var i = 0; i < n; i++)
            {
                var g = toAdd[i];
                TryReadUnicode(g, out var unicode);
                var newIndex = maxIndex + 1 + i;

                var pb = g["planeBounds"] as JObject ?? new JObject();
                var ab = g["atlasBounds"] as JObject ?? new JObject();
                double pl = Num(pb, "left"), pr = Num(pb, "right"), pt = Num(pb, "top"), pbm = Num(pb, "bottom");
                double al = Num(ab, "left"), ar = Num(ab, "right"), at = Num(ab, "top"), abm = Num(ab, "bottom");
                var advance = g["advance"]?.Value<double>() ?? 0;

                var go = i * glyphStride;
                WriteInt32LittleEndian(newGlyphBytes, go + 0, newIndex);
                WriteFloat32LittleEndian(newGlyphBytes, go + 4, (float)((pr - pl) * pxPerEm));   // width
                WriteFloat32LittleEndian(newGlyphBytes, go + 8, (float)((pt - pbm) * pxPerEm));  // height
                WriteFloat32LittleEndian(newGlyphBytes, go + 12, (float)(pl * pxPerEm));         // bearingX
                WriteFloat32LittleEndian(newGlyphBytes, go + 16, (float)(pt * pxPerEm));         // bearingY
                WriteFloat32LittleEndian(newGlyphBytes, go + 20, (float)(advance * pxPerEm));    // advance
                // rect: свой (та же карта, что и для ремапа латиницы).
                var rcN = rectByUnicode.TryGetValue(unicode, out var rcv)
                    ? rcv
                    : new GlyphRectPatch((int)Math.Round(al), (int)Math.Round(abm), (int)Math.Round(ar - al), (int)Math.Round(at - abm));
                WriteInt32LittleEndian(newGlyphBytes, go + 24, rcN.X);
                WriteInt32LittleEndian(newGlyphBytes, go + 28, rcN.Y);
                WriteInt32LittleEndian(newGlyphBytes, go + 32, rcN.W);
                WriteInt32LittleEndian(newGlyphBytes, go + 36, rcN.H);
                WriteFloat32LittleEndian(newGlyphBytes, go + 40, 1.0f); // scale
                WriteInt32LittleEndian(newGlyphBytes, go + 44, 0);      // atlasIndex
                WriteInt32LittleEndian(newGlyphBytes, go + 48, 0);      // classDefinitionType

                var co = i * charStride;
                WriteInt32LittleEndian(newCharBytes, co + 0, 1);        // m_ElementType = Character
                WriteInt32LittleEndian(newCharBytes, co + 4, unicode);
                WriteInt32LittleEndian(newCharBytes, co + 8, newIndex);
                WriteFloat32LittleEndian(newCharBytes, co + 12, 1.0f);  // m_Scale

                if (i < 4)
                    log?.Add("[Grow] cyr[" + i + "] U+" + unicode.ToString("X4", CultureInfo.InvariantCulture)
                        + " m_Index=" + newIndex
                        + " W=" + ((pr - pl) * pxPerEm).ToString("F1", CultureInfo.InvariantCulture)
                        + " H=" + ((pt - pbm) * pxPerEm).ToString("F1", CultureInfo.InvariantCulture)
                        + " bearY=" + (pt * pxPerEm).ToString("F1", CultureInfo.InvariantCulture)
                        + " adv=" + (advance * pxPerEm).ToString("F1", CultureInfo.InvariantCulture)
                        + " rect=" + rcN.X + "," + rcN.Y + "," + rcN.W + "," + rcN.H);
            }

            byte[] result;
            using (var ms = new MemoryStream(raw.Length + newGlyphBytes.Length + newCharBytes.Length))
            {
                ms.Write(raw, 0, glyphCountOff);                              // префикс до glyphCount
                WriteInt32ToStream(ms, glyphCount + n);                       // новый glyphCount
                ms.Write(existingGlyphs, 0, existingGlyphs.Length);          // существующие глифы (rect ремапнуты)
                ms.Write(newGlyphBytes, 0, newGlyphBytes.Length);            // новые глифы
                WriteInt32ToStream(ms, charCount + n);                        // новый charCount
                ms.Write(raw, charEntriesOff, charCount * charStride);        // существующие символы
                ms.Write(newCharBytes, 0, newCharBytes.Length);              // новые символы
                ms.Write(raw, charTableEnd, raw.Length - charTableEnd);       // хвост (atlas textures, creation settings, kerning…)
                result = ms.ToArray();
            }

            // Редирект/размер не трогаем: патчим саму атлас-текстуру (atlasTexturePathId) нашим 1024-атласом,
            // координаты глифов уже в 1024-пространстве (из 1024-JSON), m_AtlasWidth остаётся 1024.

            log?.Add("[Grow] Добавлено " + n + " символов (U+0400…). glyphCount@0x"
                + glyphCountOff.ToString("X", CultureInfo.InvariantCulture) + "=" + (glyphCount + n)
                + ", charCount@0x" + charCountOff.ToString("X", CultureInfo.InvariantCulture) + "=" + (charCount + n)
                + ". Размер объекта " + raw.Length + " → " + result.Length + " (+" + (result.Length - raw.Length) + ").");
            log?.Add("[Grow] Первый добавленный: U+"
                + (toAdd.Count > 0 && TryReadUnicode(toAdd[0], out var u0) ? u0.ToString("X4", CultureInfo.InvariantCulture) : "----")
                + " m_Index=" + (maxIndex + 1) + ".");
            return result;
        }

        /// <summary>Меняет int32 в суффиксе по относительному смещению с проверкой ожидаемого значения.</summary>
        private static void PatchSuffixInt32(byte[] buf, int suffixStart, int rel, int expected, int newVal, string name, ICollection<string> log)
        {
            var o = suffixStart + rel;
            if (o < 0 || o + 4 > buf.Length)
            {
                log?.Add("[Grow] " + name + " @rel+0x" + rel.ToString("X", CultureInfo.InvariantCulture) + ": вне диапазона — пропуск.");
                return;
            }
            var cur = ReadInt32LittleEndian(buf, o);
            if (cur == expected)
            {
                WriteInt32LittleEndian(buf, o, newVal);
                log?.Add("[Grow] " + name + " @0x" + o.ToString("X", CultureInfo.InvariantCulture) + ": " + expected + "→" + newVal + ".");
            }
            else
            {
                log?.Add("[Grow] " + name + " @0x" + o.ToString("X", CultureInfo.InvariantCulture)
                    + ": ожидал " + expected + ", фактически " + cur + " — пропуск (структура сместилась?).");
            }
        }

        private static void WriteInt32ToStream(System.IO.Stream s, int value)
        {
            s.WriteByte((byte)(value & 0xFF));
            s.WriteByte((byte)((value >> 8) & 0xFF));
            s.WriteByte((byte)((value >> 16) & 0xFF));
            s.WriteByte((byte)((value >> 24) & 0xFF));
        }

        /// <summary>Сигнатура glyph-записи 7296: m_Scale@+40 == 1.0f и m_AtlasIndex@+44 == 0.</summary>
        private static bool IsGlyphEntrySignature(byte[] raw, int off)
        {
            if (off < 0 || off + 48 > raw.Length)
                return false;
            // 1.0f == 0x3F800000
            return ReadInt32LittleEndian(raw, off + 40) == 0x3F800000
                && ReadInt32LittleEndian(raw, off + 44) == 0;
        }

        private static bool TryLocateGlyphTable7296(byte[] raw, ICollection<string> log, out int entriesStart, out int glyphCount)
        {
            entriesStart = 0;
            glyphCount = 0;
            const int stride = GlyphTableEntrySize; // 52

            // Ищем самый длинный ряд записей со stride 52, где у каждой scale==1.0f, atlasIndex==0.
            // Не зависит от значений m_Index (они не обязаны быть 0,1,2,…).
            var bestStart = -1;
            var bestLen = 0;
            var candidates = 0;
            var limit = Math.Min(raw.Length - 48, 0x8000);
            var a = 0;
            while (a <= limit)
            {
                if (!IsGlyphEntrySignature(raw, a)
                    || !IsGlyphEntrySignature(raw, a + stride)
                    || !IsGlyphEntrySignature(raw, a + 2 * stride))
                {
                    a += 4;
                    continue;
                }

                var start = a;
                while (start - stride >= 0 && IsGlyphEntrySignature(raw, start - stride))
                    start -= stride;

                var len = 0;
                while (IsGlyphEntrySignature(raw, start + len * stride))
                    len++;

                candidates++;
                if (candidates <= 8)
                {
                    log?.Add("[7296 GT] кандидат start@0x" + start.ToString("X", CultureInfo.InvariantCulture)
                        + " len=" + len
                        + " idx[0..2]=" + ReadInt32LittleEndian(raw, start)
                        + "," + ReadInt32LittleEndian(raw, start + stride)
                        + "," + ReadInt32LittleEndian(raw, start + 2 * stride));
                }

                if (len > bestLen)
                {
                    bestLen = len;
                    bestStart = start;
                }

                a = start + len * stride; // перескок за конец найденного ряда
                if (a <= start)
                    a = start + 4;
            }

            if (bestStart < 0 || bestLen <= 0)
            {
                log?.Add("[7296 GT] ряд glyph-записей (scale=1.0, atlasIndex=0, stride 52) не найден.");
                return false;
            }

            entriesStart = bestStart;
            glyphCount = bestLen;

            var idxSample = new List<string>();
            for (var i = 0; i < Math.Min(8, bestLen); i++)
                idxSample.Add(ReadInt32LittleEndian(raw, bestStart + i * stride).ToString(CultureInfo.InvariantCulture));

            var countFieldOff = bestStart - 4;
            var countField = countFieldOff >= 0 ? ReadInt32LittleEndian(raw, countFieldOff) : -1;
            log?.Add("[7296 GT] выбран entriesStart@0x" + bestStart.ToString("X", CultureInfo.InvariantCulture)
                + " glyphCount=" + glyphCount
                + " m_Index[0..]=" + string.Join(",", idxSample)
                + " (поле перед таблицей @0x" + Math.Max(0, countFieldOff).ToString("X", CultureInfo.InvariantCulture)
                + "=" + countField + (countField == glyphCount ? " == count ✓" : " ≠ count") + ")");

            return true;
        }

        /// <summary>Шестнадцатеричный дамп региона [start,end) построчно по 16 байт — диагностика заголовка таблицы.</summary>
        private static void LogRawRegionDump(byte[] raw, int start, int end, string tag, ICollection<string> log)
        {
            if (log == null || raw == null)
                return;

            var from = Math.Max(0, start);
            var to = Math.Min(end, raw.Length);
            for (var off = from; off < to; off += 16)
            {
                var count = Math.Min(16, to - off);
                log.Add(tag + " 0x" + off.ToString("X4", CultureInfo.InvariantCulture) + ": "
                    + BitConverter.ToString(raw, off, count));
            }
        }

        /// <summary>Размер записи m_CharacterTable (12/16 байт) по первым записям: валидный unicode + монотонность; по умолчанию 16.</summary>
        private static int DetectCharacterEntrySize(byte[] raw, int entriesOffset, int count, ICollection<string> log)
        {
            var best = 16;
            var bestScore = -1;
            foreach (var size in new[] { 12, 16 })
            {
                var sample = Math.Min(Math.Max(count, 0), 32);
                if (sample <= 0)
                    break;
                if (entriesOffset < 0 || entriesOffset + sample * size > raw.Length)
                    continue;

                var valid = 0;
                var monotonic = 0;
                var prev = -1;
                for (var i = 0; i < sample; i++)
                {
                    var off = entriesOffset + i * size;
                    var unicode = ReadInt32LittleEndian(raw, off);
                    var glyphIndex = ReadInt32LittleEndian(raw, off + 4);
                    if (unicode < 0 || unicode > 0x10FFFF || glyphIndex < 0 || glyphIndex > 200000)
                        continue;
                    valid++;
                    if (unicode >= prev)
                        monotonic++;
                    prev = unicode;
                }

                var score = valid * 10 + monotonic;
                log?.Add("[TMP CharTable] entrySize probe " + size + ": valid=" + valid + "/" + sample + " monotonic=" + monotonic);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = size;
                }
            }
            return best;
        }

        /// <summary>Лог первых записей m_CharacterTable (unicode/glyphIndex) по заданным смещениям полей.</summary>
        private static void LogCharacterTablePreview(
            byte[] raw, int entriesOffset, int count, int entrySize,
            int unicodeFieldOffset, int glyphIndexFieldOffset, ICollection<string> log)
        {
            if (log == null || raw == null)
                return;

            var preview = Math.Min(Math.Max(count, 0), 12);
            var items = new List<string>();
            for (var i = 0; i < preview; i++)
            {
                var off = entriesOffset + i * entrySize;
                if (off + entrySize > raw.Length)
                    break;
                var u = (uint)ReadInt32LittleEndian(raw, off + unicodeFieldOffset);
                var gi = ReadInt32LittleEndian(raw, off + glyphIndexFieldOffset);
                items.Add("U+" + u.ToString("X4", CultureInfo.InvariantCulture) + " gi=" + gi);
            }
            log.Add("[TMP CharTable] preview: " + string.Join(" | ", items));
        }

        private static byte[] BuildPatchedTexture2DRawInPlaceFromPng(
            AssetsManager manager,
            AssetsFileInstance inst,
            long texturePathId,
            (byte[] Alpha8, int Width, int Height) alpha8,
            ICollection<string> log,
            out AssetFileInfo texInfo)
        {
            texInfo = inst.file.AssetInfos.FirstOrDefault(x => x.PathId == texturePathId)
                      ?? inst.file.GetAssetInfo(texturePathId);
            if (texInfo == null)
                throw new InvalidOperationException("Texture2D PathID=" + texturePathId + " не найдена среди AssetInfos.");

            if (alpha8.Alpha8 == null || alpha8.Alpha8.Length != Atlas512PixelCount)
            {
                throw new InvalidOperationException(
                    "Texture2D in-place: ожидалось " + Atlas512PixelCount + " байт Alpha8 (512×512), получено "
                    + (alpha8.Alpha8 == null ? 0 : alpha8.Alpha8.Length) + ".");
            }

            if (alpha8.Width != 512 || alpha8.Height != 512)
            {
                log?.Add("[Tex404] Предупреждение: PNG " + alpha8.Width + "×" + alpha8.Height + " — патч image data рассчитан на 512×512.");
            }

            byte[] texRaw;
            lock (inst.LockReader)
            {
                var reader = inst.file.Reader;
                reader.Position = ResolveAbsoluteInPlaceOffset(inst.file, texInfo, log, "Texture2D read");
                texRaw = reader.ReadBytes(checked((int)texInfo.ByteSize));
            }

            if (texRaw == null || texRaw.Length < 8)
                throw new InvalidOperationException("Texture2D PathID=" + texturePathId + ": сырой объект слишком короткий.");

            log?.Add(
                "[Tex raw] size=" + texRaw.Length
                + ", last 8 bytes: " + BitConverter.ToString(texRaw, texRaw.Length - 8));

            var imageDataOffset = texRaw.Length - Atlas512PixelCount;
            log?.Add(
                "[Tex raw] imageData starts at offset " + imageDataOffset
                + " (0x" + imageDataOffset.ToString("X", CultureInfo.InvariantCulture) + ")");

            if (imageDataOffset < 0)
            {
                throw new InvalidOperationException(
                    "Texture2D PathID=" + texturePathId + ": raw ByteSize " + texRaw.Length
                    + " меньше image data " + Atlas512PixelCount + ".");
            }

            TryLogImageDataOffsetCrossCheck(manager, inst, texInfo, texturePathId, texRaw, imageDataOffset, log);

            var patched = new byte[texRaw.Length];
            Buffer.BlockCopy(texRaw, 0, patched, 0, texRaw.Length);
            Buffer.BlockCopy(alpha8.Alpha8, 0, patched, imageDataOffset, Atlas512PixelCount);

            log?.Add(
                "[InPlace] Texture2D PathID=" + texturePathId + ": image data only, "
                + Atlas512PixelCount + " bytes Alpha8 (SDF red) @ 0x"
                + imageDataOffset.ToString("X", CultureInfo.InvariantCulture) + ", header не менялся.");

            return patched;
        }

        private static void TryLogImageDataOffsetCrossCheck(
            AssetsManager manager,
            AssetsFileInstance inst,
            AssetFileInfo texInfo,
            long texturePathId,
            byte[] texRaw,
            int imageDataOffset,
            ICollection<string> log)
        {
            if (manager == null || inst == null || log == null)
                return;

            try
            {
                var texBase = manager.GetBaseField(inst, texInfo);
                var imgSize = FindImageDataField(texBase)?.AsByteArray?.Length ?? 0;
                log?.Add("[Tex404] GetBaseField image data=" + imgSize + " bytes, raw ByteSize=" + texRaw.Length + ".");
                if (imgSize != Atlas512PixelCount && imgSize > 0)
                {
                    log?.Add(
                        "[Tex raw] Внимание: image data в GetBaseField=" + imgSize
                        + ", патч пишет " + Atlas512PixelCount + " байт с конца raw.");
                }
            }
            catch (Exception ex)
            {
                log?.Add("[Tex404] cross-check skipped: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LogOriginalTexture2DInfo(
            AssetsManager manager,
            AssetsFileInstance inst,
            long texturePathId,
            ICollection<string> log)
        {
            if (manager == null || inst == null || texturePathId == 0)
                return;

            try
            {
                var texInfo = inst.file.AssetInfos.FirstOrDefault(x => x.PathId == texturePathId)
                    ?? inst.file.GetAssetInfo(texturePathId);
                if (texInfo == null)
                {
                    log?.Add("[Tex404] Texture2D PathID=" + texturePathId + " не найдена среди AssetInfos.");
                    return;
                }

                var texBase = manager.GetBaseField(inst, texInfo);
                if (texBase == null || texBase.IsDummy)
                {
                    log?.Add("[Tex404] Texture2D PathID=" + texturePathId + " не удалось разобрать через GetBaseField.");
                    return;
                }

                var w = ReadIntFieldOrDefault(texBase, "m_Width");
                var h = ReadIntFieldOrDefault(texBase, "m_Height");
                var fmt = ReadIntFieldOrDefault(texBase, "m_TextureFormat");
                var mips = ReadIntFieldOrDefault(texBase, "m_MipCount");
                var imgSize = FindImageDataField(texBase)?.AsByteArray?.Length ?? 0;

                log?.Add("[Tex404] " + w + "x" + h
                    + " format=" + fmt
                    + " mips=" + mips
                    + " imageData=" + imgSize + " bytes rawSize=" + texInfo.ByteSize);
            }
            catch (Exception ex)
            {
                log?.Add("[Tex404] inspect failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Dictionary<int, int> BuildUnicodeGlyphIndexMap(JArray glyphs)
        {
            var map = new Dictionary<int, int>();
            for (var i = 0; i < glyphs.Count; i++)
            {
                if (!(glyphs[i] is JObject g))
                    continue;

                int unicode;
                if (!TryReadUnicode(g, out unicode))
                    continue;

                map[unicode] = i;
            }
            return map;
        }

        private static Dictionary<int, GlyphRectPatch> BuildUnicodeAtlasRectMap(JArray glyphs)
        {
            var map = new Dictionary<int, GlyphRectPatch>();
            for (var i = 0; i < glyphs.Count; i++)
            {
                if (!(glyphs[i] is JObject g))
                    continue;

                if (!TryReadUnicode(g, out var unicode))
                    continue;

                var ab = g["atlasBounds"] as JObject;
                if (ab == null)
                    continue;

                var left = Num(ab, "left");
                var bottom = Num(ab, "bottom");
                var right = Num(ab, "right");
                var top = Num(ab, "top");

                map[unicode] = new GlyphRectPatch(
                    (int)Math.Round(left),
                    (int)Math.Round(bottom),
                    (int)Math.Round(right - left),
                    (int)Math.Round(top - bottom));
            }
            return map;
        }

        private static bool TryReadUnicode(JObject glyph, out int unicode)
        {
            unicode = -1;
            var u = glyph["unicode"];
            if (u != null && u.Type == JTokenType.Integer)
            {
                unicode = u.Value<int>();
                return unicode >= 0;
            }

            var unicodeHex = glyph["unicodeHex"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(unicodeHex))
            {
                var s = unicodeHex.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(2);
                int hex;
                if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                {
                    unicode = hex;
                    return unicode >= 0;
                }
            }

            return false;
        }

        private struct GlyphRectPatch
        {
            internal readonly int X;
            internal readonly int Y;
            internal readonly int W;
            internal readonly int H;

            internal GlyphRectPatch(int x, int y, int w, int h)
            {
                X = x;
                Y = y;
                W = w;
                H = h;
            }
        }

        private static int ReadInt32LittleEndian(byte[] bytes, int offset)
        {
            return bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
        }

        private static void WritePatchedObjectsInPlace(
            string sourceAssetsPath,
            string outputPath,
            AssetsFile file,
            ICollection<string> log,
            params (AssetFileInfo Info, byte[] Data, string Label)[] patches)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));
            if (string.IsNullOrWhiteSpace(sourceAssetsPath) || !File.Exists(sourceAssetsPath))
                throw new FileNotFoundException(sourceAssetsPath);

            var bytes = File.ReadAllBytes(sourceAssetsPath);
            log?.Add("[InPlace] Прочитано байт: " + bytes.Length);
            log?.Add("[Header orig]  " + BitConverter.ToString(bytes, 0, Math.Min(32, bytes.Length)));
            foreach (var patch in patches)
            {
                if (patch.Info == null || patch.Data == null)
                    continue;

                var offset = checked((int)ResolveAbsoluteInPlaceOffset(file, patch.Info, log, patch.Label + " write"));
                var size = checked((int)patch.Info.ByteSize);
                if (patch.Data.Length != size)
                {
                    throw new InvalidOperationException(
                        patch.Label + " in-place: размер патча не равен ByteSize. old=" + size + ", new=" + patch.Data.Length + ".");
                }
                if (offset < 0 || size < 0 || offset + size > bytes.Length)
                {
                    throw new InvalidOperationException(
                        patch.Label + " in-place: диапазон выходит за пределы файла. offset=" + offset + ", size=" + size + ", file=" + bytes.Length + ".");
                }

                Buffer.BlockCopy(patch.Data, 0, bytes, offset, size);
                log?.Add(
                    "[InPlace] " + patch.Label + " PathID=" + patch.Info.PathId + " offset=0x"
                    + offset.ToString("X", CultureInfo.InvariantCulture) + " size=" + size + " bytes.");
            }

            log?.Add("[Header patch] " + BitConverter.ToString(bytes, 0, Math.Min(32, bytes.Length)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            File.WriteAllBytes(outputPath, bytes);
            var written = new FileInfo(outputPath).Length;
            log?.Add("[InPlace] Записано байт: " + written + ", совпадает: " + (written == bytes.Length));
        }

        private static void LogBinaryDiffFirst10(string originalPath, string patchedPath, ICollection<string> log)
        {
            if (log == null)
                return;

            var orig = File.ReadAllBytes(originalPath);
            var patched = File.ReadAllBytes(patchedPath);

            if (orig.Length != patched.Length)
            {
                log.Add("[Diff] file sizes: orig=" + orig.Length + " patched=" + patched.Length);
            }

            var diffCount = 0;
            for (var i = 0; i < orig.Length && diffCount < 10; i++)
            {
                if (i >= patched.Length || orig[i] != patched[i])
                {
                    var patchedByte = i < patched.Length ? patched[i] : (byte)0;
                    log.Add(
                        "[Diff] offset=0x" + i.ToString("X", CultureInfo.InvariantCulture)
                        + " (" + i + "): orig=" + orig[i].ToString("X2", CultureInfo.InvariantCulture)
                        + " patched=" + patchedByte.ToString("X2", CultureInfo.InvariantCulture));
                    diffCount++;
                }
            }

            var totalDiff = orig.Length == patched.Length
                ? orig.Zip(patched, (a, b) => a != b).Count(x => x)
                : CountDifferentBytes(orig, patched);
            log.Add("[Diff] total different bytes: " + totalDiff);
        }

        private static int CountDifferentBytes(byte[] orig, byte[] patched)
        {
            var minLen = Math.Min(orig.Length, patched.Length);
            var total = 0;
            for (var i = 0; i < minLen; i++)
            {
                if (orig[i] != patched[i])
                    total++;
            }

            total += Math.Abs(orig.Length - patched.Length);
            return total;
        }

        private static long ResolveAbsoluteInPlaceOffset(AssetsFile file, AssetFileInfo info, ICollection<string> log, string label)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            var dataOffset = file.Header.DataOffset;
            var relativeOffset = info.ByteOffset;
            var absoluteOffset = dataOffset + relativeOffset;

            try
            {
                var absByMethod = info.GetAbsoluteByteOffset(file);
                log?.Add(
                    "[InPlace] " + label
                    + " dataOffset=0x" + dataOffset.ToString("X", CultureInfo.InvariantCulture)
                    + " relativeOffset=0x" + relativeOffset.ToString("X", CultureInfo.InvariantCulture)
                    + " absolute=0x" + absoluteOffset.ToString("X", CultureInfo.InvariantCulture)
                    + " getAbsolute=0x" + absByMethod.ToString("X", CultureInfo.InvariantCulture));
            }
            catch
            {
                log?.Add(
                    "[InPlace] " + label
                    + " dataOffset=0x" + dataOffset.ToString("X", CultureInfo.InvariantCulture)
                    + " relativeOffset=0x" + relativeOffset.ToString("X", CultureInfo.InvariantCulture)
                    + " absolute=0x" + absoluteOffset.ToString("X", CultureInfo.InvariantCulture));
            }

            return absoluteOffset;
        }

        private static void LogOriginalCharacterTableEntries(byte[] rawMb, ICollection<string> log)
        {
            if (rawMb == null || log == null)
                return;

            LogGlyphTableSearchRegion(rawMb, log);

            const int tableEntriesOffset = 0xB9C;
            const int entrySize = 16;
            const int maxPreview = 52;
            if (rawMb.Length < tableEntriesOffset + entrySize)
                return;

            var entries = new List<string>();
            var maxCount = Math.Min(maxPreview, (rawMb.Length - tableEntriesOffset) / entrySize);
            for (var i = 0; i < maxCount; i++)
            {
                var off = tableEntriesOffset + i * entrySize;
                var u = BitConverter.ToUInt32(rawMb, off);
                var gi = BitConverter.ToUInt32(rawMb, off + 4);
                var sc = BitConverter.ToSingle(rawMb, off + 8);
                var fl = BitConverter.ToUInt32(rawMb, off + 12);
                entries.Add(
                    "U+" + u.ToString("X4", CultureInfo.InvariantCulture)
                    + " gi=" + gi.ToString(CultureInfo.InvariantCulture)
                    + " sc=" + sc.ToString(CultureInfo.InvariantCulture)
                    + " fl=" + fl.ToString(CultureInfo.InvariantCulture));
            }

            log.Add("[OrigTable] " + string.Join(" | ", entries));
        }

        private static void LogOriginalObjectHashes(byte[] rawMb, byte[] fileBytes, string assetsPath, long pathId, ICollection<string> log)
        {
            if (rawMb == null || fileBytes == null || log == null)
                return;

            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(rawMb);
                var hashHex = BitConverter.ToString(hash).Replace("-", string.Empty);
                log.Add("[ObjHash] MD5 of PathID=" + pathId + " raw bytes: " + hashHex);

                var hits = 0;
                for (var i = 0; i <= fileBytes.Length - hash.Length; i++)
                {
                    var match = true;
                    for (var j = 0; j < hash.Length; j++)
                    {
                        if (fileBytes[i + j] != hash[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        log.Add("[ObjHash] MD5 found in file at offset 0x" + i.ToString("X", CultureInfo.InvariantCulture));
                        hits++;
                    }
                }

                if (hits == 0)
                    log.Add("[ObjHash] MD5 not found in file.");
            }

            var objectCrc = Crc32Utility.Compute(rawMb);
            log.Add("[ObjHash] CRC32: 0x" + objectCrc.ToString("X8", CultureInfo.InvariantCulture));

            var fileCrc = Crc32Utility.Compute(fileBytes);
            log.Add("[FileHash] resources.assets CRC32: 0x" + fileCrc.ToString("X8", CultureInfo.InvariantCulture));

            var ggmPath = TryResolveSiblingGlobalGameManagersAssetsPath(assetsPath);
            if (string.IsNullOrWhiteSpace(ggmPath) || !File.Exists(ggmPath))
            {
                log.Add("[ObjHash] globalgamemanagers.assets not found рядом с assetsPath.");
                return;
            }

            var ggm = File.ReadAllBytes(ggmPath);
            log.Add("[ObjHash] globalgamemanagers path: " + ggmPath);
            LogCrcPatternHits("[ObjHash]", objectCrc, ggm, log);
            LogCrcPatternHits("[FileHash]", fileCrc, ggm, log);
        }

        private static void LogCrcPatternHits(string tag, uint crc, byte[] haystack, ICollection<string> log)
        {
            if (haystack == null || log == null)
                return;

            var crcBytes = BitConverter.GetBytes(crc);
            var hits = 0;
            for (var i = 0; i <= haystack.Length - crcBytes.Length; i++)
            {
                if (haystack[i] == crcBytes[0]
                    && haystack[i + 1] == crcBytes[1]
                    && haystack[i + 2] == crcBytes[2]
                    && haystack[i + 3] == crcBytes[3])
                {
                    log.Add(tag + " CRC32 found in globalgamemanagers at 0x" + i.ToString("X", CultureInfo.InvariantCulture));
                    hits++;
                }
            }

            if (hits == 0)
                log.Add(tag + " CRC32 not found in globalgamemanagers.");
        }

        private static string TryResolveSiblingGlobalGameManagersAssetsPath(string assetsPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(assetsPath));
                if (string.IsNullOrWhiteSpace(dir))
                    return null;

                var candAssets = Path.Combine(dir, "globalgamemanagers.assets");
                if (File.Exists(candAssets))
                    return candAssets;

                var candNoExt = Path.Combine(dir, "globalgamemanagers");
                if (File.Exists(candNoExt))
                    return candNoExt;
            }
            catch
            {
                /* ignore */
            }

            return null;
        }

        private static void LogGlyphTableSearchRegion(byte[] rawMb, ICollection<string> log)
        {
            if (rawMb == null || log == null)
                return;

            const int start = 0x100;
            const int endExclusive = 0xB94;
            if (rawMb.Length <= start)
                return;

            var actualEnd = Math.Min(endExclusive, rawMb.Length);
            log.Add("[GlyphArea] dump 0x100..0xB94");
            for (var off = start; off < actualEnd; off += 16)
            {
                var count = Math.Min(16, actualEnd - off);
                log.Add("[GlyphArea] 0x" + off.ToString("X4", CultureInfo.InvariantCulture) + ": "
                    + BitConverter.ToString(rawMb, off, count));
            }
        }

        private static void LogGlyphTableStructureDiagnostics(
            byte[] rawMb,
            ICollection<string> log,
            string label,
            int glyphTableCountOffset = 0x100,
            int glyphEntrySize = GlyphTableEntrySize)
        {
            if (rawMb == null || log == null)
                return;

            var glyphTableBaseOffset = glyphTableCountOffset + 4;
            var dumpEntrySize = glyphEntrySize;

            if (rawMb.Length >= glyphTableCountOffset + 4)
            {
                var count = ReadInt32LittleEndian(rawMb, glyphTableCountOffset);
                log.Add(
                    "[GlyphVerify] " + label + " count@0x" + glyphTableCountOffset.ToString("X", CultureInfo.InvariantCulture) + "="
                    + count + " (0x" + count.ToString("X", CultureInfo.InvariantCulture) + ")");
            }

            for (var stride = 40; stride <= 56; stride += 4)
            {
                if (glyphTableBaseOffset + stride + 4 > rawMb.Length)
                    continue;

                var idx0 = ReadInt32LittleEndian(rawMb, glyphTableBaseOffset);
                var idx1 = ReadInt32LittleEndian(rawMb, glyphTableBaseOffset + stride);
                log.Add("[GlyphVerify] " + label + " stride=" + stride + ": idx[0]=" + idx0 + " idx[1]=" + idx1);
            }

            for (var e = 0; e < 3; e++)
            {
                var entryOff = glyphTableBaseOffset + e * dumpEntrySize;
                if (entryOff + 16 > rawMb.Length)
                    break;

                var dumpLen = Math.Min(dumpEntrySize, rawMb.Length - entryOff);
                log.Add(
                    "[GlyphVerify] " + label + " entry[" + e + "]@0x"
                    + entryOff.ToString("X", CultureInfo.InvariantCulture) + ": "
                    + BitConverter.ToString(rawMb, entryOff, dumpLen));
            }
        }

        private static void VerifyGlyphRectWrite(
            byte[] patched,
            int slotIndex,
            int glyphIndex,
            GlyphRectPatch expected,
            int glyphTableBaseOffset,
            int glyphEntrySize,
            int glyphRectOffsetInEntry,
            ICollection<string> log)
        {
            if (patched == null || log == null)
                return;

            var glyphOffset = glyphTableBaseOffset + glyphIndex * glyphEntrySize;
            var glyphRectOffset = glyphOffset + glyphRectOffsetInEntry;

            log.Add(
                "[GlyphVerify] slot " + slotIndex + " glyphIndex=" + glyphIndex
                + " -> rawOffset=0x" + glyphOffset.ToString("X", CultureInfo.InvariantCulture));
            log.Add(
                "[GlyphVerify] GlyphRect@0x" + glyphRectOffset.ToString("X", CultureInfo.InvariantCulture)
                + " bytes: "
                + (glyphRectOffset + 16 <= patched.Length
                    ? BitConverter.ToString(patched, glyphRectOffset, 16)
                    : "<out of range>"));

            if (glyphRectOffset + 16 > patched.Length)
            {
                log.Add("[GlyphVerify] slot " + slotIndex + " read-back failed: GlyphRect out of range.");
                return;
            }

            var x = ReadInt32LittleEndian(patched, glyphRectOffset + 0);
            var y = ReadInt32LittleEndian(patched, glyphRectOffset + 4);
            var w = ReadInt32LittleEndian(patched, glyphRectOffset + 8);
            var h = ReadInt32LittleEndian(patched, glyphRectOffset + 12);

            var match = x == expected.X && y == expected.Y && w == expected.W && h == expected.H;
            log.Add(
                "[GlyphVerify] slot " + slotIndex + " read-back rect="
                + x + "," + y + "," + w + "," + h
                + " expected=" + expected.X + "," + expected.Y + "," + expected.W + "," + expected.H
                + " match=" + match);
        }

        /// <summary>Читает atlas.distanceRange из JSON (толщина SDF-спреда в пикселях).</summary>
        private static float ReadAtlasDistanceRange(string atlasJsonPath, float fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(atlasJsonPath) || !File.Exists(atlasJsonPath))
                    return fallback;
                var json = JObject.Parse(File.ReadAllText(atlasJsonPath));
                var dr = (json["atlas"] as JObject)?["distanceRange"];
                if (dr != null)
                {
                    var v = dr.Value<float>();
                    if (v > 0)
                        return v;
                }
            }
            catch
            {
                /* ignore */
            }
            return fallback;
        }

        /// <summary>Ставит <c>_GradientScale = gradientScale</c> в материалах, чей <c>_MainTex</c> = атлас <paramref name="atlasPathId"/> — чтобы толщина края SDF совпала с атласом.</summary>
        private static void PatchMaterialsGradientScaleForAtlas(
            AssetsManager manager, AssetsFileInstance inst, long atlasPathId, float gradientScale, ICollection<string> log)
        {
            var patched = 0;
            foreach (var info in inst.file.AssetInfos)
            {
                if (info.TypeId != (int)AssetClassID.Material)
                    continue;

                AssetTypeValueField matBase;
                try { matBase = manager.GetBaseField(inst, info); }
                catch { continue; }
                if (matBase == null || matBase.IsDummy)
                    continue;

                var saved = FieldICase(matBase, "m_SavedProperties");
                if (saved == null || saved.IsDummy)
                    continue;

                var texArr = GetListArrayField(saved, "m_TexEnvs");
                if (texArr == null)
                    continue;

                var usesAtlas = false;
                foreach (var te in texArr.Children)
                {
                    if (!string.Equals(FieldICase(te, "first")?.AsString, "_MainTex", StringComparison.Ordinal))
                        continue;
                    var second = FieldICase(te, "second");
                    var texPtr = second == null ? null : FieldICase(second, "m_Texture");
                    if (texPtr != null && TryReadPathIdLong(texPtr, out var pid) && pid == atlasPathId)
                        usesAtlas = true;
                    break;
                }
                if (!usesAtlas)
                    continue;

                var floatArr = GetListArrayField(saved, "m_Floats");
                if (floatArr == null)
                    continue;
                foreach (var fl in floatArr.Children)
                {
                    if (!string.Equals(FieldICase(fl, "first")?.AsString, "_GradientScale", StringComparison.Ordinal))
                        continue;
                    var val = FieldICase(fl, "second");
                    if (val != null && !val.IsDummy)
                    {
                        try { val.AsFloat = gradientScale; }
                        catch { break; }
                        info.SetNewData(matBase.WriteToByteArray());
                        patched++;
                        log?.Add("[Grow] Материал PathID=" + info.PathId + ": _GradientScale → "
                            + gradientScale.ToString("F2", CultureInfo.InvariantCulture) + ".");
                    }
                    break;
                }
            }
            log?.Add("[Grow] Материалов под атлас " + atlasPathId + " (_GradientScale): пропатчено " + patched + ".");
        }

        private static void WriteAssetsFileToPath(AssetsFile file, string outputPath)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            using (var ms = new MemoryStream())
            {
                using (var writer = new AssetsFileWriter(ms) { BigEndian = false })
                    file.Write(writer);
                File.WriteAllBytes(outputPath, ms.ToArray());
            }
        }

        private static void WriteInt32LittleEndian(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value & 0xFF);
            bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
            bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
            bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteFloat32LittleEndian(byte[] bytes, int offset, float value)
        {
            var fb = BitConverter.GetBytes(value);
            Buffer.BlockCopy(fb, 0, bytes, offset, 4);
        }

        private static JArray ResolveGlyphsArray(JObject root)
        {
            var g = root["glyphs"] as JArray;
            if (g != null && g.Count > 0)
                return g;
            var vars = root["variants"] as JArray;
            if (vars != null && vars.Count > 0 && vars[0] is JObject v0)
            {
                var g2 = v0["glyphs"] as JArray;
                if (g2 != null && g2.Count > 0)
                    return g2;
            }
            return g;
        }

        private static (byte[] Alpha8, int Width, int Height) LoadPngAsAlpha8TopDownFlipped(string path, ICollection<string> log)
        {
            using (var bmp = new Bitmap(path))
            {
                var w = bmp.Width;
                var h = bmp.Height;
                var bytes = new byte[w * h];
                var bmpData = bmp.LockBits(
                    new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    var stride = bmpData.Stride;
                    var rowBuf = new byte[Math.Abs(stride)];
                    var scan0 = bmpData.Scan0;
                    for (int row = 0; row < h; row++)
                    {
                        int srcRow = h - 1 - row;
                        Marshal.Copy(new IntPtr(scan0.ToInt64() + srcRow * stride), rowBuf, 0, Math.Min(rowBuf.Length, stride));
                        int dstRow = row * w;
                        for (int x = 0; x < w; x++)
                        {
                            int s = x * 4;
                            bytes[dstRow + x] = rowBuf[s + 2];
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                log?.Add($"[TMP patch] PNG → Alpha8 from red (SDF): {w}×{h}, {bytes.Length} байт.");
                return (bytes, w, h);
            }
        }

        private static void PatchTexture2DWithAlpha8(
            AssetTypeValueField texBase,
            (byte[] Alpha8, int Width, int Height) alpha8,
            ICollection<string> log)
        {
            var imgField = FindImageDataField(texBase);
            if (imgField == null)
                throw new InvalidOperationException("Texture2D: не найдено поле image data.");

            try
            {
                imgField.TemplateField.ValueType = AssetValueType.ByteArray;
            }
            catch
            {
                /* часть версий без доступа к шаблону */
            }

            // Важно: сначала принудительно сбрасываем streamData, затем пишем inline image data.
            TryClearStreamData(texBase);

            TrySetIntField(texBase, "m_Width", alpha8.Width, log);
            TrySetIntField(texBase, "m_Height", alpha8.Height, log);
            TrySetIntField(texBase, "m_TextureFormat", UnityTextureFormatAlpha8, log);
            TrySetIntField(texBase, "m_MipCount", 1, log);
            TrySetIntField(texBase, "m_CompleteImageSize", alpha8.Alpha8.Length, log);

            imgField.Value.ValueType = AssetValueType.ByteArray;
            imgField.AsByteArray = alpha8.Alpha8;

            TryLogStreamData(texBase, log);
        }

        private static AssetTypeValueField FindImageDataField(AssetTypeValueField texRoot)
        {
            var direct = FieldICase(texRoot, "image data");
            if (direct != null && !direct.IsDummy)
                return direct;
            foreach (var ch in texRoot.Children)
            {
                var n = ch.FieldName;
                if (!string.IsNullOrEmpty(n)
                    && n.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0
                    && n.IndexOf("data", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ch;
            }
            return null;
        }

        private static void TryClearStreamData(AssetTypeValueField texRoot)
        {
            var sd = FieldICase(texRoot, "m_StreamData");
            if (sd == null || sd.IsDummy)
                return;
            try
            {
                var off = FieldICase(sd, "offset");
                var sz = FieldICase(sd, "size");
                var p = FieldICase(sd, "path");
                if (off != null && !off.IsDummy)
                {
                    try { off.AsULong = 0; }
                    catch
                    {
                        try { off.AsLong = 0; }
                        catch
                        {
                            try { off.AsInt = 0; }
                            catch { }
                        }
                    }
                }
                if (sz != null && !sz.IsDummy)
                {
                    try { sz.AsUInt = 0; }
                    catch
                    {
                        try { sz.AsInt = 0; }
                        catch
                        {
                            try { sz.AsLong = 0; }
                            catch { }
                        }
                    }
                }
                if (p != null && !p.IsDummy)
                    p.AsString = "";
            }
            catch
            {
                /* ignore */
            }
        }

        private static void TryLogStreamData(AssetTypeValueField texRoot, ICollection<string> log)
        {
            if (log == null)
                return;

            try
            {
                var streamData = FieldICase(texRoot, "m_StreamData");
                if (streamData == null || streamData.IsDummy)
                {
                    log.Add("[StreamData] m_StreamData missing");
                    return;
                }

                var off = FieldICase(streamData, "offset");
                var sz = FieldICase(streamData, "size");
                var p = FieldICase(streamData, "path");

                ulong offset = 0;
                uint size = 0;
                string path = "";

                if (off != null && !off.IsDummy)
                {
                    try { offset = off.AsULong; }
                    catch
                    {
                        try { offset = (ulong)Math.Max(0L, off.AsLong); }
                        catch { }
                    }
                }

                if (sz != null && !sz.IsDummy)
                {
                    try { size = sz.AsUInt; }
                    catch
                    {
                        try { size = (uint)Math.Max(0, sz.AsInt); }
                        catch { }
                    }
                }

                if (p != null && !p.IsDummy)
                {
                    try { path = p.AsString ?? ""; }
                    catch { }
                }

                log.Add("[StreamData] offset=" + offset + " size=" + size + " path='" + path + "'");
            }
            catch (Exception ex)
            {
                log.Add("[StreamData] log failed: " + ex.Message);
            }
        }

        /// <summary>TMP_FontAsset хранит в <c>m_AtlasTextures</c> массив <see cref="PPtr"/> на <c>Texture2D</c> в том же .assets (m_FileID=0).</summary>
        private static (int fileId, long pathId) ResolveAtlasTexturePPtrFromTmpFont(AssetTypeValueField tmpField, ICollection<string> log)
        {
            try
            {
                var atlasTextures = FieldICase(tmpField, "m_AtlasTextures");
                if (atlasTextures != null && !atlasTextures.IsDummy)
                {
                    log?.Add("[TMP patch] m_AtlasTextures: дочерних полей=" + atlasTextures.Children.Count + ".");
                    var pptr = TryGetFirstPPtrFromAtlasTexturesField(atlasTextures, log);
                    if (pptr != null)
                    {
                        var r = ReadPPtrFileAndPath(pptr);
                        if (r.pathId != 0)
                            return r;
                        var unwrapped = UnwrapNestedPPtr(pptr, maxDepth: 4);
                        if (unwrapped != null)
                        {
                            r = ReadPPtrFileAndPath(unwrapped);
                            if (r.pathId != 0)
                            {
                                log?.Add("[TMP patch] PPtr после развёртки вложенных полей.");
                                return r;
                            }
                        }
                    }
                }
                else
                    log?.Add("[TMP patch] Поле m_AtlasTextures отсутствует или пустое.");

                var single = FieldICase(tmpField, "m_AtlasTexture");
                if (single != null && !single.IsDummy)
                {
                    log?.Add("[TMP patch] Пробуем одиночное поле m_AtlasTexture.");
                    var r = ReadPPtrFileAndPath(single);
                    if (r.pathId != 0)
                        return r;
                    var u = UnwrapNestedPPtr(single, maxDepth: 4);
                    if (u != null)
                        return ReadPPtrFileAndPath(u);
                }
            }
            catch (Exception ex)
            {
                log?.Add("[TMP patch] Ошибка чтения ссылки на атлас: " + ex.Message);
            }

            return (0, 0);
        }

        /// <summary>Как в дампе: <c>m_AtlasTextures.Array[0]</c> — элемент типа PPtr.</summary>
        private static AssetTypeValueField TryGetFirstPPtrFromAtlasTexturesField(
            AssetTypeValueField atlasTexturesRoot,
            ICollection<string> log)
        {
            AssetTypeValueField arr = null;
            try
            {
                arr = atlasTexturesRoot["Array"];
            }
            catch
            {
                arr = null;
            }
            if (arr == null || arr.IsDummy)
                arr = FieldICase(atlasTexturesRoot, "Array");

            if (arr != null && !arr.IsDummy && arr.Children.Count > 0)
            {
                log?.Add("[TMP patch] Берём m_AtlasTextures.Array[0], детей в Array=" + arr.Children.Count + ".");
                return arr.Children[0];
            }

            foreach (var ch in atlasTexturesRoot.Children)
            {
                if (string.Equals(ch.FieldName, "Array", StringComparison.OrdinalIgnoreCase) && ch.Children.Count > 0)
                {
                    log?.Add("[TMP patch] Берём дочерний Array[0] внутри m_AtlasTextures.");
                    return ch.Children[0];
                }
            }

            if (atlasTexturesRoot.Children.Count > 0)
            {
                foreach (var ch in atlasTexturesRoot.Children)
                {
                    if (HasPPtrShape(ch))
                    {
                        log?.Add("[TMP patch] Первый дочерний узел m_AtlasTextures с m_FileID/m_PathID: «" + ch.FieldName + "».");
                        return ch;
                    }
                }
            }

            log?.Add("[TMP patch] Не найден ни Array с элементами, ни PPtr среди детей m_AtlasTextures.");
            return null;
        }

        private static bool HasPPtrShape(AssetTypeValueField node)
        {
            if (node == null || node.IsDummy)
                return false;
            return (FieldICase(node, "m_PathID") ?? FieldICase(node, "m_PathId")) != null
                   || (FieldICase(node, "m_FileID") ?? FieldICase(node, "m_FileId")) != null;
        }

        private static AssetTypeValueField UnwrapNestedPPtr(AssetTypeValueField node, int maxDepth)
        {
            if (node == null || maxDepth < 0)
                return null;
            if (TryReadPathIdLong(node, out var pid) && pid != 0)
                return node;
            foreach (var ch in node.Children)
            {
                var u = UnwrapNestedPPtr(ch, maxDepth - 1);
                if (u != null)
                    return u;
            }
            return null;
        }

        private static bool TryReadPathIdLong(AssetTypeValueField node, out long pathId)
        {
            pathId = 0;
            var pid = FieldICase(node, "m_PathID") ?? FieldICase(node, "m_PathId");
            if (pid == null || pid.IsDummy)
                return false;
            try
            {
                pathId = pid.AsLong;
                return true;
            }
            catch
            {
                try
                {
                    pathId = pid.AsInt;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static (int fileId, long pathId) ReadPPtrFileAndPath(AssetTypeValueField pptr)
        {
            if (pptr == null || pptr.IsDummy)
                return (0, 0);
            int fileId = 0;
            var fid = FieldICase(pptr, "m_FileID") ?? FieldICase(pptr, "m_FileId");
            if (fid != null && !fid.IsDummy)
            {
                try { fileId = fid.AsInt; }
                catch { }
            }
            TryReadPathIdLong(pptr, out var pathId);
            return (fileId, pathId);
        }

        private static AssetTypeValueField GetListArrayField(AssetTypeValueField parent, string listName)
        {
            var list = FieldICase(parent, listName);
            if (list == null || list.IsDummy)
                return null;
            var arr = FieldICase(list, "Array");
            if (arr != null && !arr.IsDummy)
                return arr;
            return list;
        }

        private static void RebuildGlyphAndCharacterTables(
            AssetsManager manager,
            AssetsFileInstance inst,
            AssetTypeValueField tmpBase,
            JArray glyphsTok,
            int atlasW,
            int atlasH,
            float pxPerEm,
            ICollection<string> log)
        {
            var ordered = new List<JObject>();
            foreach (var t in glyphsTok)
            {
                if (t is JObject jo)
                    ordered.Add(jo);
            }
            ordered.Sort((a, b) =>
            {
                var ua = GlyphUnicode(a);
                var ub = GlyphUnicode(b);
                return ua.CompareTo(ub);
            });

            var glyphList = GetListArrayField(tmpBase, "m_GlyphTable");
            var charList = GetListArrayField(tmpBase, "m_CharacterTable");
            if (glyphList == null || charList == null)
                throw new InvalidOperationException("Нет m_GlyphTable/m_CharacterTable (Array).");

            glyphList.Children.Clear();
            charList.Children.Clear();

            uint idx = 0;
            foreach (var gtok in ordered)
            {
                var unicode = GlyphUnicode(gtok);
                var advanceEm = gtok["advance"]?.Value<double>() ?? 0;
                var pb = gtok["planeBounds"] as JObject ?? new JObject();
                var ab = gtok["atlasBounds"] as JObject ?? new JObject();

                double pl = Num(pb, "left"), pr = Num(pb, "right"), pbB = Num(pb, "bottom"), pt = Num(pb, "top");
                double al = Num(ab, "left"), ar = Num(ab, "right"), abB = Num(ab, "bottom"), at = Num(ab, "top");

                float advPx = (float)(advanceEm * pxPerEm);
                float wPx = (float)((pr - pl) * pxPerEm);
                float hPx = (float)((pt - pbB) * pxPerEm);
                float bearX = (float)(pl * pxPerEm);
                float bearY = (float)(pt * pxPerEm);

                int gw = Math.Max(1, (int)Math.Round(ar - al));
                int gh = Math.Max(1, (int)Math.Round(at - abB));
                int gx = (int)Math.Floor(al);
                int gy = atlasH - (int)Math.Ceiling(at);

                var gItem = ValueBuilder.DefaultValueFieldFromArrayTemplate(glyphList);
                SetGlyphFields(gItem, idx, wPx, hPx, bearX, bearY, advPx, gx, gy, gw, gh);
                glyphList.Children.Add(gItem);

                var cItem = ValueBuilder.DefaultValueFieldFromArrayTemplate(charList);
                SetCharacterFields(cItem, unicode, idx);
                charList.Children.Add(cItem);

                idx++;
            }

            log?.Add($"[TMP patch] Таблицы: {glyphList.Children.Count} глифов / символов.");
        }

        private static uint GlyphUnicode(JObject gtok)
        {
            var u = gtok["unicode"];
            if (u != null)
            {
                try { return u.Value<uint>(); }
                catch
                {
                    try { return (uint)u.Value<long>(); }
                    catch { }
                }
            }
            var ix = gtok["index"];
            if (ix != null)
            {
                try { return (uint)ix.Value<int>(); }
                catch { }
            }
            return 0;
        }

        private static double Num(JObject o, string name)
        {
            var t = o?[name];
            if (t == null)
                return 0;
            try { return t.Value<double>(); }
            catch
            {
                try { return double.Parse(t.ToString(), CultureInfo.InvariantCulture); }
                catch { return 0; }
            }
        }

        private static void SetGlyphFields(
            AssetTypeValueField gItem,
            uint index,
            float wPx,
            float hPx,
            float bearX,
            float bearY,
            float advPx,
            int gx,
            int gy,
            int gw,
            int gh)
        {
            TrySetUInt(gItem, "m_Index", index);

            var metrics = FieldICase(gItem, "m_Metrics");
            if (metrics != null && !metrics.IsDummy)
            {
                TrySetFloat(metrics, "m_Width", wPx);
                TrySetFloat(metrics, "m_Height", hPx);
                TrySetFloat(metrics, "m_HorizontalBearingX", bearX);
                TrySetFloat(metrics, "m_HorizontalBearingY", bearY);
                TrySetFloat(metrics, "m_HorizontalAdvance", advPx);
            }

            var rect = FieldICase(gItem, "m_GlyphRect");
            if (rect != null && !rect.IsDummy)
            {
                TrySetIntField(rect, "m_X", gx, null);
                TrySetIntField(rect, "m_Y", gy, null);
                TrySetIntField(rect, "m_Width", gw, null);
                TrySetIntField(rect, "m_Height", gh, null);
            }

            TrySetFloat(gItem, "m_Scale", 1f);
            TrySetIntField(gItem, "m_AtlasIndex", 0, null);

            var cdt = FieldICase(gItem, "m_ClassDefinitionType");
            if (cdt != null && !cdt.IsDummy)
            {
                try { cdt.AsInt = 1; }
                catch { }
            }
        }

        private static void SetCharacterFields(AssetTypeValueField cItem, uint unicode, uint glyphIndex)
        {
            var et = FieldICase(cItem, "m_ElementType");
            if (et != null && !et.IsDummy)
            {
                try { et.AsInt = 1; }
                catch { }
            }

            TrySetUInt(cItem, "m_Unicode", unicode);
            TrySetUInt(cItem, "m_GlyphIndex", glyphIndex);
            TrySetFloat(cItem, "m_Scale", 1f);

            var off = FieldICase(cItem, "m_Offset");
            if (off != null && !off.IsDummy)
            {
                TrySetFloat(off, "x", 0f);
                TrySetFloat(off, "y", 0f);
                TrySetFloat(off, "m_X", 0f);
                TrySetFloat(off, "m_Y", 0f);
            }
        }

        private static void TrySetFacePointSize(AssetTypeValueField tmpBase, float pointSize, ICollection<string> log)
        {
            var face = FieldICase(tmpBase, "m_FaceInfo");
            if (face == null || face.IsDummy)
                return;
            if (TrySetFloat(face, "m_PointSize", pointSize))
                log?.Add($"[TMP patch] m_FaceInfo.m_PointSize = {pointSize.ToString(CultureInfo.InvariantCulture)}");
        }

        private static void TrySetIntField(AssetTypeValueField parent, string name, int value, ICollection<string> log)
        {
            var f = FieldICase(parent, name);
            if (f == null || f.IsDummy)
                return;
            try
            {
                f.AsInt = value;
            }
            catch
            {
                try { f.AsLong = value; }
                catch { }
            }
        }

        private static int ReadIntFieldOrDefault(AssetTypeValueField parent, string name, int fallback = 0)
        {
            var f = FieldICase(parent, name);
            if (f == null || f.IsDummy)
                return fallback;
            try
            {
                return f.AsInt;
            }
            catch
            {
                try
                {
                    return checked((int)f.AsLong);
                }
                catch
                {
                    return fallback;
                }
            }
        }

        private static bool TrySetFloat(AssetTypeValueField parent, string name, float value)
        {
            var f = FieldICase(parent, name);
            if (f == null || f.IsDummy)
                return false;
            try
            {
                f.AsFloat = value;
                return true;
            }
            catch
            {
                try
                {
                    f.AsInt = (int)Math.Round(value);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void TrySetUInt(AssetTypeValueField parent, string name, uint value)
        {
            var f = FieldICase(parent, name);
            if (f == null || f.IsDummy)
                return;
            try
            {
                f.AsLong = value;
            }
            catch
            {
                try { f.AsInt = (int)value; }
                catch { }
            }
        }

        private static AssetTypeValueField FieldICase(AssetTypeValueField parent, string name)
        {
            if (parent == null || name == null)
                return null;
            try
            {
                var direct = parent[name];
                if (direct != null && !direct.IsDummy)
                    return direct;
            }
            catch
            {
                /* bracket может кидать */
            }

            foreach (var ch in parent.Children)
            {
                if (!string.IsNullOrEmpty(ch?.FieldName)
                    && string.Equals(ch.FieldName, name, StringComparison.OrdinalIgnoreCase))
                    return ch;
            }
            return null;
        }

    }
}
