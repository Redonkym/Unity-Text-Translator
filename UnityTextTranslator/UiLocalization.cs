using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnityTextTranslator
{
    /// <summary>
    /// Переводы интерфейса для ДОП. языков (кроме en/ru — те в <c>L(en, ru)</c>). Хранятся отдельно от кода: встроенный
    /// <c>ui-languages.json</c> (EmbeddedResource) + внешний оверрайд <c>%AppData%\…\ui-languages.json</c> (правится без пересборки).
    /// Ключ — английская строка из <c>L(...)</c>; нет перевода → null → <c>L()</c> откатывается на English (новые элементы просто английские).
    /// </summary>
    internal static class UiLocalization
    {
        public const string FileName = "ui-languages.json";

        /// <summary>Доп. языки интерфейса: код → отображаемое имя для пикера. en/ru добавляются отдельно в UI.</summary>
        public static readonly (string Code, string Display)[] ExtraLanguages =
        {
            ("es", "Español"),
            ("fr", "Français"),
            ("de", "Deutsch"),
            ("pt", "Português"),
            ("it", "Italiano"),
        };

        // code(lower) -> (englishKey -> translation). Ленивая потокобезопасная загрузка.
        private static Dictionary<string, Dictionary<string, string>> _map;
        private static readonly object _gate = new object();

        public static bool IsExtraLanguage(string code) =>
            !string.IsNullOrEmpty(code) &&
            ExtraLanguages.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));

        /// <summary>Путь к внешнему (правимому) файлу переводов интерфейса.</summary>
        public static string ExternalFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnityTextTranslator",
            FileName);

        /// <summary>Перевод <paramref name="english"/> на язык <paramref name="langCode"/>; null если неизвестно (L покажет English).</summary>
        public static string Translate(string langCode, string english)
        {
            if (string.IsNullOrEmpty(langCode) || string.IsNullOrEmpty(english))
                return null;

            EnsureLoaded();
            if (_map.TryGetValue(langCode, out var dict) &&
                dict.TryGetValue(english, out var tr) &&
                !string.IsNullOrEmpty(tr))
                return tr;
            return null;
        }

        public static void EnsureLoaded()
        {
            if (_map != null)
                return;
            lock (_gate)
            {
                if (_map != null)
                    return;
                var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                MergeFrom(map, ReadEmbedded());   // встроенные значения
                MergeFrom(map, ReadExternal());   // внешний оверрайд поверх
                _map = map;
            }
        }

        /// <summary>Сбрасывает кеш (после правки внешнего файла можно перечитать без перезапуска).</summary>
        public static void Reload()
        {
            lock (_gate)
                _map = null;
            EnsureLoaded();
        }

        private static void MergeFrom(Dictionary<string, Dictionary<string, string>> map, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;
            try
            {
                var root = JObject.Parse(json);
                foreach (var langProp in root.Properties())
                {
                    if (!(langProp.Value is JObject entries))
                        continue;
                    if (!map.TryGetValue(langProp.Name, out var dict))
                    {
                        dict = new Dictionary<string, string>(StringComparer.Ordinal);
                        map[langProp.Name] = dict;
                    }
                    foreach (var e in entries.Properties())
                    {
                        if (string.IsNullOrEmpty(e.Name) || e.Value == null || e.Value.Type != JTokenType.String)
                            continue;
                        var val = e.Value.Value<string>();
                        if (!string.IsNullOrEmpty(val))
                            dict[e.Name] = val; // внешний файл (читается вторым) перекрывает встроенный
                    }
                }
            }
            catch
            {
                // битый JSON — игнорируем, остаёмся на English-фолбэке
            }
        }

        private static string ReadEmbedded()
        {
            try
            {
                var asm = typeof(UiLocalization).Assembly;
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(FileName, StringComparison.OrdinalIgnoreCase));
                if (name == null)
                    return null;
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s == null)
                        return null;
                    using (var r = new StreamReader(s))
                        return r.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ReadExternal()
        {
            try
            {
                var p = ExternalFilePath;
                return File.Exists(p) ? File.ReadAllText(p) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
