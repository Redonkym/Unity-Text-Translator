using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace UnityTextTranslator
{
    internal static class TranslationMemory
    {
        public static string MemoryFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnityTextTranslator",
                "memory.json");

        private sealed class MemoryDocument
        {
            [JsonProperty("pairs")]
            public Dictionary<string, string> Pairs { get; set; }
        }

        public static Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(MemoryFilePath))
                    return new Dictionary<string, string>(StringComparer.Ordinal);

                var json = File.ReadAllText(MemoryFilePath);

                var doc = JsonConvert.DeserializeObject<MemoryDocument>(json);
                if (doc?.Pairs != null && doc.Pairs.Count > 0)
                    return new Dictionary<string, string>(doc.Pairs, StringComparer.Ordinal);

                var flat = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (flat != null && flat.Count > 0)
                    return new Dictionary<string, string>(flat, StringComparer.Ordinal);

                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        /// <summary>Строка без переводимого текста: только цифры/пробелы/символы (числа, проценты, «100 / 100», «$25000»).</summary>
        public static bool LooksLikeNonTranslatableToken(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0)
                return false;
            foreach (var ch in s)
                if (!(char.IsDigit(ch) || char.IsWhiteSpace(ch) || ".,/%$+-:×х".IndexOf(ch) >= 0))
                    return false;
            return true;
        }

        /// <summary>
        /// Очевидно битая пара (артефакт сдвига строк): одна сторона — чистое число/символы, другая — текст.
        /// Напр. «HV»→«250», «250»→«Причёска», «Hunger»→«100». «250»→«250» и «Голод»→«Hunger» НЕ считаются битыми.
        /// </summary>
        public static bool IsLikelyShiftCorruptedPair(string original, string translated)
        {
            return LooksLikeNonTranslatableToken(original) != LooksLikeNonTranslatableToken(translated);
        }

        public static void SaveMerge(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var dict = Load();
            foreach (var kv in pairs)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                if (string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                // Не сохраняем заведомо сдвинутый мусор (число↔текст) — иначе он копится в памяти и
                // потом подставляется при «обновить». Корень (сдвиг) уже починен, это страховка.
                if (IsLikelyShiftCorruptedPair(kv.Key, kv.Value))
                    continue;
                dict[kv.Key] = kv.Value;
            }
            Save(dict);
        }

        /// <summary>Удаляет из memory.json очевидно битые пары (число↔текст). Возвращает сколько удалено. Делает .bak.</summary>
        public static int PurgeShiftCorruptedPairs()
        {
            var dict = Load();
            if (dict.Count == 0)
                return 0;

            var bad = new List<string>();
            foreach (var kv in dict)
                if (IsLikelyShiftCorruptedPair(kv.Key, kv.Value))
                    bad.Add(kv.Key);

            if (bad.Count == 0)
                return 0;

            try
            {
                if (File.Exists(MemoryFilePath))
                    File.Copy(MemoryFilePath, MemoryFilePath + ".bak", overwrite: true);
            }
            catch { /* бэкап необязателен */ }

            foreach (var k in bad)
                dict.Remove(k);
            Save(dict);
            return bad.Count;
        }

        /// <summary>Полностью очищает базу: удаляет memory.json. Возвращает число записей, что были в базе.</summary>
        public static int Clear()
        {
            int had = 0;
            try { had = Load().Count; }
            catch { /* всё равно пробуем удалить */ }

            try
            {
                if (File.Exists(MemoryFilePath))
                    File.Delete(MemoryFilePath);
            }
            catch
            {
                // не удалось удалить (занят/права) — перезаписываем пустой базой
                Save(new Dictionary<string, string>(StringComparer.Ordinal));
            }

            return had;
        }

        public static void Save(Dictionary<string, string> pairs)
        {
            var dir = Path.GetDirectoryName(MemoryFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var doc = new MemoryDocument { Pairs = pairs ?? new Dictionary<string, string>() };
            File.WriteAllText(MemoryFilePath, JsonConvert.SerializeObject(doc, Formatting.Indented));
        }
    }
}
