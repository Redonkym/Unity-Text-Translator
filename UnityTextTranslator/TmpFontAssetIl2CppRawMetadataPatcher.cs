using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityTextTranslator
{
    /// <summary>
    /// Фиксированные смещения в сыром теле <c>TMP_FontAsset</c> (MonoBehaviour) для IL2CPP, где type tree пустой (карта по <c>tmp_*_raw.bin</c>).
    /// PPtr на Texture2D/Material не меняются; таблицы глифов не трогаются.
    /// </summary>
    internal static class TmpFontAssetIl2CppRawMetadataPatcher
    {
        /// <summary>Последний байт поля charset (не включительно): 0x1efc + 50.</summary>
        internal const int MinRequiredLength = 0x1efc + 50;

        private const int AtlasWidthOffset = 0x15cc;
        private const int AtlasHeightOffset = 0x15d0;
        private const int CreationAtlasWidthOffset = 0x1eec;
        private const int CreationAtlasHeightOffset = 0x1ef0;
        private const int CharsetOffset = 0x1efc;
        private const int CharsetByteLength = 50;
        private const int HashOffset = 0x40;

        /// <summary>Диапазон символов для кириллицы в creationSettings (ASCII, ровно 50 байт с дополнением нулями).</summary>
        internal const string DefaultCyrillicCharsetPattern = "32 - 126, 1024 - 1279";

        /// <param name="charsetAscii">Строка в ASCII; не длиннее 50 символов, остаток поля обнуляется.</param>
        /// <param name="atlasSizeOnly">Только m_AtlasWidth/Height и creationSettings (без charset).</param>
        internal static void Apply(byte[] raw, int atlasWidth, int atlasHeight, string charsetAscii = null, ICollection<string> log = null, bool atlasSizeOnly = false)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));
            if (raw.Length < MinRequiredLength)
            {
                throw new InvalidOperationException(
                    "Сырой MonoBehaviour слишком короткий: " + raw.Length + " байт, нужно ≥ " + MinRequiredLength + ".");
            }

            if (raw.Length >= HashOffset + 8)
            {
                var hashBefore = BitConverter.ToUInt64(raw, HashOffset);
                log?.Add("[Hash] before patch: 0x" + hashBefore.ToString("X16", CultureInfo.InvariantCulture));
            }

            WriteInt32LittleEndian(raw, AtlasWidthOffset, atlasWidth);
            WriteInt32LittleEndian(raw, AtlasHeightOffset, atlasHeight);
            WriteInt32LittleEndian(raw, CreationAtlasWidthOffset, atlasWidth);
            WriteInt32LittleEndian(raw, CreationAtlasHeightOffset, atlasHeight);

            if (atlasSizeOnly)
            {
                log?.Add("[TMP raw] atlas size only: " + atlasWidth + "×" + atlasHeight + " (charset не менялся).");
            }
            else
            {
                WriteFixedAsciiZeroPadded(raw, CharsetOffset, CharsetByteLength, charsetAscii ?? DefaultCyrillicCharsetPattern);
            }

            if (raw.Length >= HashOffset + 8)
            {
                var hashAfter = BitConverter.ToUInt64(raw, HashOffset);
                log?.Add("[Hash] after patch: 0x" + hashAfter.ToString("X16", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Эвристически ищет <c>m_CharacterTable</c> (count + N*16 байт) и добавляет/обновляет кириллицу 0x0400-0x04FF.
        /// Запись: unicode(int32), glyphIndex(int32), scale(float), padding(int32).
        /// </summary>
        /// <param name="raw">Сырой MonoBehaviour (PathID TMP_FontAsset).</param>
        /// <param name="glyphIndexByUnicode">Карта unicode -> glyphIndex (обычно из atlas JSON).</param>
        /// <param name="log">Диагностический лог.</param>
        /// <returns>Новый массив raw (может быть длиннее, если таблица выросла).</returns>
        internal static byte[] PatchCharacterTableCyrillic(
            byte[] raw,
            IDictionary<int, int> glyphIndexByUnicode,
            ICollection<string> log = null)
        {
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));
            if (glyphIndexByUnicode == null)
                throw new ArgumentNullException(nameof(glyphIndexByUnicode));

            if (!TryFindCharacterTable(raw, out var countOffset, out var entryOffset, out var entryCount))
                throw new InvalidOperationException("Не удалось найти m_CharacterTable в сырых байтах TMP_FontAsset.");

            var entries = new Dictionary<int, CharacterEntry>(entryCount + 512);
            for (var i = 0; i < entryCount; i++)
            {
                var off = entryOffset + (i * 16);
                var unicode = BitConverter.ToInt32(raw, off);
                var glyphIndex = BitConverter.ToInt32(raw, off + 4);
                var scale = BitConverter.ToSingle(raw, off + 8);
                var padding = BitConverter.ToInt32(raw, off + 12);
                entries[unicode] = new CharacterEntry(unicode, glyphIndex, scale, padding);
            }

            var changed = 0;
            for (var cp = 0x0400; cp <= 0x04FF; cp++)
            {
                if (!glyphIndexByUnicode.TryGetValue(cp, out var glyphIndex) || glyphIndex < 0)
                    continue;

                entries[cp] = new CharacterEntry(cp, glyphIndex, 1.0f, 0);
                changed++;
            }

            if (changed == 0)
            {
                log?.Add("[TMP raw] m_CharacterTable: нет кириллических глифов в карте JSON (0x0400-0x04FF).");
                return raw;
            }

            var ordered = new List<CharacterEntry>(entries.Values);
            ordered.Sort((a, b) => a.Unicode.CompareTo(b.Unicode));

            var oldBytesLen = entryCount * 16;
            var newBytesLen = ordered.Count * 16;
            var delta = newBytesLen - oldBytesLen;

            var outRaw = new byte[raw.Length + delta];
            var oldEntriesEnd = entryOffset + oldBytesLen;
            var newEntriesEnd = entryOffset + newBytesLen;

            Buffer.BlockCopy(raw, 0, outRaw, 0, countOffset);
            WriteInt32LittleEndian(outRaw, countOffset, ordered.Count);

            for (var i = 0; i < ordered.Count; i++)
            {
                var off = entryOffset + (i * 16);
                WriteInt32LittleEndian(outRaw, off, ordered[i].Unicode);
                WriteInt32LittleEndian(outRaw, off + 4, ordered[i].GlyphIndex);
                var scaleBytes = BitConverter.GetBytes(ordered[i].Scale);
                Buffer.BlockCopy(scaleBytes, 0, outRaw, off + 8, 4);
                WriteInt32LittleEndian(outRaw, off + 12, ordered[i].Padding);
            }

            Buffer.BlockCopy(raw, oldEntriesEnd, outRaw, newEntriesEnd, raw.Length - oldEntriesEnd);

            log?.Add(
                "[TMP raw] m_CharacterTable offset=0x"
                + countOffset.ToString("X", CultureInfo.InvariantCulture)
                + ", oldCount=" + entryCount
                + ", newCount=" + ordered.Count
                + ", added/updated Cyrillic=" + changed + ".");

            return outRaw;
        }

        internal static bool TryFindCharacterTable(byte[] raw, out int countOffset, out int entryOffset, out int entryCount)
        {
            countOffset = 0;
            entryOffset = 0;
            entryCount = 0;

            var bestScore = -1;
            for (var off = 0; off <= raw.Length - 4; off += 4)
            {
                var count = BitConverter.ToInt32(raw, off);
                if (count < 32 || count > 4096)
                    continue;

                var dataOff = off + 4;
                var byteLen = count * 16;
                if (dataOff < 0 || byteLen < 0 || dataOff + byteLen > raw.Length)
                    continue;

                var sample = Math.Min(count, 64);
                var valid = 0;
                var monotonic = 0;
                var prevUnicode = -1;
                for (var i = 0; i < sample; i++)
                {
                    var eoff = dataOff + (i * 16);
                    var unicode = BitConverter.ToInt32(raw, eoff);
                    var glyph = BitConverter.ToInt32(raw, eoff + 4);
                    var scale = BitConverter.ToSingle(raw, eoff + 8);
                    var pad = BitConverter.ToInt32(raw, eoff + 12);

                    if (unicode < 0 || unicode > 0x10FFFF || glyph < 0 || glyph > 200000 || pad != 0)
                        continue;
                    if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0 || scale > 8)
                        continue;

                    valid++;
                    if (prevUnicode <= unicode)
                        monotonic++;
                    prevUnicode = unicode;
                }

                if (valid < sample * 8 / 10)
                    continue;
                if (monotonic < sample * 7 / 10)
                    continue;

                var score = (valid * 10) + count;
                if (score > bestScore)
                {
                    bestScore = score;
                    countOffset = off;
                    entryOffset = dataOff;
                    entryCount = count;
                }
            }

            return bestScore >= 0;
        }

        private struct CharacterEntry
        {
            internal readonly int Unicode;
            internal readonly int GlyphIndex;
            internal readonly float Scale;
            internal readonly int Padding;

            internal CharacterEntry(int unicode, int glyphIndex, float scale, int padding)
            {
                Unicode = unicode;
                GlyphIndex = glyphIndex;
                Scale = scale;
                Padding = padding;
            }
        }

        private static void WriteInt32LittleEndian(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value & 0xff);
            buf[offset + 1] = (byte)((value >> 8) & 0xff);
            buf[offset + 2] = (byte)((value >> 16) & 0xff);
            buf[offset + 3] = (byte)((value >> 24) & 0xff);
        }

        private static void WriteFixedAsciiZeroPadded(byte[] raw, int offset, int fixedLen, string text)
        {
            Array.Clear(raw, offset, fixedLen);
            if (string.IsNullOrEmpty(text))
                return;

            var bytes = Encoding.ASCII.GetBytes(text);
            var n = Math.Min(bytes.Length, fixedLen);
            Buffer.BlockCopy(bytes, 0, raw, offset, n);
        }
    }
}
