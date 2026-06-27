using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    public partial class Form1
    {
        /// <summary>VS/графы: «port»/«node» под «input» в ветке «RefIds» — связи узлов, не локализуемый текст.</summary>
        private static bool ShouldSkipJsonPropertyForTranslation(IReadOnlyList<string> pathToParentObject, string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName) || pathToParentObject == null || pathToParentObject.Count == 0)
                return false;

            if (!string.Equals(propertyName, "port", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(propertyName, "node", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(pathToParentObject[pathToParentObject.Count - 1], "input", StringComparison.OrdinalIgnoreCase))
                return false;

            for (var i = 0; i < pathToParentObject.Count; i++)
            {
                if (string.Equals(pathToParentObject[i], "RefIds", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Код локали Unity (<c>m_LocaleId.m_Code</c>): <c>en</c>, <c>ru</c> — не переводится.</summary>
        private static bool ShouldSkipUnityLocaleCodeField(IReadOnlyList<string> pathToParentObject, string propertyName)
        {
            if (!string.Equals(propertyName, "m_Code", StringComparison.OrdinalIgnoreCase) ||
                pathToParentObject == null || pathToParentObject.Count == 0)
                return false;
            return string.Equals(pathToParentObject[pathToParentObject.Count - 1], "m_LocaleId", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Файлы <c>ClothInstance-*.json</c> (дамп UABEA MonoBehaviour одежды): совпадают с ключами в <c>ClothesDatabase</c> и в сейве.</summary>
        private static bool IsClothInstanceUnityExportJson(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            return Path.GetFileName(fileName).StartsWith("ClothInstance-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Поля идентичности каталога одежды — не подписи (перевод ломает сопоставление с сейвом). <c>m_Name</c> уже в <see cref="SkipKeys"/>, тут — дублирующий ключ.</summary>
        private static bool ShouldSkipClothCatalogKeyFields(string fileName, string propertyName)
        {
            if (!IsClothInstanceUnityExportJson(fileName) || string.IsNullOrEmpty(propertyName))
                return false;
            return string.Equals(propertyName, "clothName", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Дампы UABEA «TypeName-resources-{pathId}.json»: при удалении JSON «без строк» не трогаем, если остались только служебные поля.</summary>
        private static bool IsUabeaResourcesAssetDumpJson(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            var fn = Path.GetFileName(fileName);
            return Regex.IsMatch(fn, @"-resources-\d+\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>Последний сегмент пути к строке (ключ свойства или индекс массива).</summary>
        private static string GetTranslationJsonLeafKey(TranslationItem item)
        {
            if (item?.PathKeys != null && item.PathKeys.Count > 0)
                return item.PathKeys[item.PathKeys.Count - 1]?.Trim() ?? "";

            var dp = item?.DisplayPath ?? "";
            var ix = dp.LastIndexOf('›');
            if (ix < 0)
                return dp.Trim();
            return dp.Substring(ix + 1).Trim();
        }

        /// <summary>Строка похожа на внутренний идентификатор Unity/ассета, а не на игроковский текст.</summary>
        private static bool LooksLikeTechnicalUnityIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var s = value.Trim();
            if (s.IndexOf(' ') >= 0 || s.IndexOf('\t') >= 0 || s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0)
                return false;

            var guidCandidate = s;
            if (guidCandidate.Length >= 2 && guidCandidate[0] == '{' && guidCandidate[guidCandidate.Length - 1] == '}')
                guidCandidate = guidCandidate.Substring(1, guidCandidate.Length - 2);
            if (Guid.TryParse(guidCandidate, out _))
                return true;

            if (s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return true;

            if (s.Length < 8)
                return false;

            if (!Regex.IsMatch(s, "^[A-Za-z][A-Za-z0-9_]*$"))
                return false;

            var upper = s.Count(char.IsUpper);
            return upper >= 2;
        }

        /// <summary>Значение похоже на id сцены/ассета (snake_case/CamelCase/путь/GUID без пробелов), не текст — перевод ломает поиск по id.</summary>
        private static bool LooksLikeSceneOrAssetId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var s = value.Trim();
            if (s.Any(char.IsWhiteSpace))
                return false; // текст с пробелами — реплика, не id
            if (LooksLikeTechnicalUnityIdentifier(s))
                return true;
            // snake_case (sr_meltdown1) либо число с точкой (9.TalkToLisa)
            if (s.Length >= 4 && Regex.IsMatch(s, "^[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+$"))
                return true;
            if (Regex.IsMatch(s, @"^[0-9]+\.[A-Za-z][A-Za-z0-9]*$"))
                return true;
            return false;
        }

        private static bool PathKeysSuggestVsGraphWire(IReadOnlyList<string> keys)
        {
            if (keys == null || keys.Count < 4)
                return false;

            var last = keys[keys.Count - 1];
            if (!string.Equals(last, "port", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(last, "node", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(keys[keys.Count - 2], "input", StringComparison.OrdinalIgnoreCase))
                return false;

            for (var i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], "RefIds", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool DisplayPathSuggestVsGraphWire(string displayPath)
        {
            if (string.IsNullOrWhiteSpace(displayPath))
                return false;
            if (displayPath.IndexOf("RefIds", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (displayPath.IndexOf("input", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            return Regex.IsMatch(displayPath.Trim(), @"\b(port|node)\s*$", RegexOptions.IgnoreCase);
        }

        /// <summary>Не звать LLM: скопировать оригинал в перевод (технич. поля Unity / графов).</summary>
        private static bool ShouldLeaveOriginalUntranslatedForLocalAi(TranslationItem it)
        {
            if (it == null)
                return false;

            var leaf = GetTranslationJsonLeafKey(it);
            var original = it.Original?.Trim() ?? "";

            if (string.Equals(leaf, "<Guid>k__BackingField", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(leaf, "bodyPartTag", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "bodyPartLayer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "currentTag", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "objectTag", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(leaf, "m_InputAxisName", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(leaf, "m_Code", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(original, @"^[a-z]{2}(?:-[A-Za-z]{2,4})?$", RegexOptions.IgnoreCase))
                return true;

            if (string.Equals(leaf, "m_Container", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "m_Dependencies", StringComparison.OrdinalIgnoreCase))
                return true;

            if (original.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                original.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(leaf, "m_Name", StringComparison.OrdinalIgnoreCase) &&
                LooksLikeTechnicalUnityIdentifier(original))
                return true;

            if (IsClothInstanceUnityExportJson(it.FileName) &&
                (string.Equals(leaf, "m_Name", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(leaf, "clothName", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (it.PathKeys != null && PathKeysSuggestVsGraphWire(it.PathKeys))
                return true;

            return DisplayPathSuggestVsGraphWire(it.DisplayPath ?? "");
        }

        private void ExtractStrings(JToken token, List<string> currentPath, string fileName, List<TranslationItem> list)
        {
            if (token == null) return;

            if (token.Type == JTokenType.String)
            {
                string value = token.Value<string>();
                var normalized = value?.Trim();
                if (string.IsNullOrWhiteSpace(normalized) || normalized == "\"\"" || normalized == "''")
                    return;
                if (LooksLikeSceneOrAssetId(normalized))
                    return; // id сцены/ассета — не текст
                list.Add(new TranslationItem
                {
                    FileName = fileName,
                    DisplayPath = string.Join(" › ", currentPath),
                    PathKeys = new List<string>(currentPath),
                    Original = value,
                    Translated = ""
                });
            }
            else if (token.Type == JTokenType.Object)
            {
                foreach (JProperty prop in token.Children<JProperty>())
                {
                    if (SkipKeys.Contains(prop.Name.Trim())) continue;
                    // технические поддеревья Unity (m_ExcludedPropertiesInInspector, scenesForLoad/Unload, eyeExpNames, TMP style-defs):
                    // их значения — имена полей/идентификаторы, а не игровой текст
                    if (MetadataPurgeTechnicalPathSegments.Contains(prop.Name.Trim())) continue;
                    if (ShouldSkipJsonPropertyForTranslation(currentPath, prop.Name.Trim())) continue;
                    if (ShouldSkipUnityLocaleCodeField(currentPath, prop.Name.Trim())) continue;
                    if (ShouldSkipClothCatalogKeyFields(fileName, prop.Name.Trim())) continue;
                    var newPath = new List<string>(currentPath) { prop.Name };
                    ExtractStrings(prop.Value, newPath, fileName, list);
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                // token.Count — O(1); прежний token.Children().Count() в условии цикла пересчитывал
                // весь массив на КАЖДОЙ итерации → O(N²) на большом массиве (таблицы локализации).
                var arr = (JArray)token;
                int n = arr.Count;
                for (int i = 0; i < n; i++)
                {
                    var newPath = new List<string>(currentPath) { $"[{i}]" };
                    ExtractStrings(arr[i], newPath, fileName, list);
                }
            }
        }

        private bool UpdateJsonValue(JToken root, List<string> pathKeys, string newValue)
        {
            try
            {
                JToken current = root;
                for (int i = 0; i < pathKeys.Count - 1; i++)
                {
                    string key = pathKeys[i];
                    if (current is JObject obj)
                    {
                        if (obj[key] == null) return false;
                        current = obj[key];
                    }
                    else if (current is JArray arr && key.StartsWith("[") && key.EndsWith("]"))
                    {
                        int idx = int.Parse(key.Substring(1, key.Length - 2));
                        if (idx < 0 || idx >= arr.Count) return false;
                        current = arr[idx];
                    }
                    else return false;
                }

                string lastKey = pathKeys.Last();
                if (current is JObject lastObj)
                {
                    if (lastObj[lastKey] == null) return false;
                    lastObj[lastKey] = newValue;
                    return true;
                }
                else if (current is JArray lastArr && lastKey.StartsWith("[") && lastKey.EndsWith("]"))
                {
                    int idx = int.Parse(lastKey.Substring(1, lastKey.Length - 2));
                    if (idx < 0 || idx >= lastArr.Count) return false;
                    lastArr[idx] = newValue;
                    return true;
                }
            }
            catch { return false; }
            return false;
        }
    }
}
