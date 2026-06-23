using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace UnityTextTranslator
{
    // Drag-and-drop папки/файлов + «Ре-синк после патча игры» (подтянуть переводы прошлой версии, подсветить новые строки).
    partial class Form1
    {
        // Строки, помеченные как «новые после патча» (нет перевода и нет совпадения в памяти).
        private readonly HashSet<TranslationItem> _resyncNewItems = new HashSet<TranslationItem>();

        // ---------- Drag-and-drop ----------

        private void SetupDragAndDrop()
        {
            AllowDrop = true;
            DragEnter += Form1_DragEnter;
            DragDrop += Form1_DragDrop;
        }

        private static string[] GetDroppedPaths(DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                return Array.Empty<string>();
            return e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        }

        private static bool IsBundlePath(string p)
        {
            var ext = Path.GetExtension(p);
            return string.Equals(ext, ".bundle", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".unity3d", StringComparison.OrdinalIgnoreCase);
        }

        private void Form1_DragEnter(object sender, DragEventArgs e)
        {
            var paths = GetDroppedPaths(e);
            bool ok = paths.Any(p =>
                Directory.Exists(p) ||
                string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase) ||
                IsBundlePath(p));
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void Form1_DragDrop(object sender, DragEventArgs e)
        {
            var paths = GetDroppedPaths(e);
            if (paths.Length == 0)
                return;

            // 1) Папка → загрузить как рабочую папку JSON.
            var folder = paths.FirstOrDefault(Directory.Exists);
            if (folder == null)
            {
                // 2) Брошен .json → берём его папку.
                var jsonFile = paths.FirstOrDefault(p =>
                    string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase));
                if (jsonFile != null)
                    folder = Path.GetDirectoryName(jsonFile);
            }

            if (folder != null)
            {
                var target = folder;
                BeginInvoke(new Action(async () => await LoadJsonFolderFromPath(target)));
                return;
            }

            // 3) Брошен .bundle → раздел «Бандлы» с подставленным путём.
            var bundle = paths.FirstOrDefault(IsBundlePath);
            if (bundle != null)
            {
                bundleLocBundlePath = bundle;
                BeginInvoke(new Action(() =>
                {
                    ActivateNavByTag("Bundles");
                    LoadBundleLocalizationModule();
                    Log(L($"Bundle from drag-and-drop: {Path.GetFileName(bundle)}",
                          $"Бандл перетаскиванием: {Path.GetFileName(bundle)}"));
                }));
            }
        }

        private async System.Threading.Tasks.Task LoadJsonFolderFromPath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            if (!IsJsonTranslatorSurfaceHosted)
            {
                ActivateNavByTag("Page");
                LoadJsonTranslatorModule();
            }

            currentFolder = folder;
            RememberRecentFolder(folder);
            lastJsonExtractFolder = "";
            _resyncNewItems.Clear();
            Log(L("Folder via drag-and-drop: ", "Папка перетаскиванием: ") + folder);

            translationItems.Clear();
            if (dgv != null && !dgv.IsDisposed)
                dgv.Rows.Clear();
            UpdateStatus();
            await ExtractTextsAsync();
        }

        // ---------- Ре-синк после патча игры ----------

        private void MenuResyncAfterPatch_Click(object sender, EventArgs e)
        {
            if (!RequireJsonTranslatorSurface(L("re-sync", "ре-синк")))
                return;
            if (translationItems.Count == 0)
            {
                MessageBox.Show(this,
                    L("Load the new export (after the patch) first: choose its JSON folder.",
                      "Сначала загрузите новый экспорт (после патча): выберите его папку JSON."),
                    L("Re-sync after game patch", "Ре-синк после патча игры"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SyncGridToItems();

            var ask = MessageBox.Show(this,
                L("Pull translations from the previous version's folder to enrich the memory?\r\n\r\n" +
                  "Yes — pick the old folder (uses its *.bak originals).\r\n" +
                  "No — use the current translation memory only.\r\n" +
                  "Cancel — abort.",
                  "Подтянуть переводы из папки прошлой версии, чтобы пополнить память?\r\n\r\n" +
                  "Да — выбрать старую папку (берёт оригиналы из *.bak).\r\n" +
                  "Нет — использовать только текущую память переводов.\r\n" +
                  "Отмена — прервать."),
                L("Re-sync after game patch", "Ре-синк после патча игры"),
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (ask == DialogResult.Cancel)
                return;

            int harvested = 0;
            if (ask == DialogResult.Yes)
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = L("Select the previous version's JSON folder (translated, with *.bak)",
                                        "Выберите папку JSON прошлой версии (переведённую, с *.bak)");
                    if (fbd.ShowDialog(this) == DialogResult.OK)
                    {
                        harvested = HarvestPairsFromOldVersionFolder(fbd.SelectedPath);
                        Log(L($"Re-sync: harvested {harvested} pairs from previous version into memory.",
                              $"Ре-синк: собрано пар из прошлой версии в память: {harvested}."));
                        if (harvested == 0)
                            Log(L("No *.bak originals found — nothing to harvest (memory unchanged).",
                                  "Оригиналы *.bak не найдены — пополнять нечем (память не изменилась)."), true);
                    }
                }
            }

            int filled = ApplyTranslationMemoryFromStore();

            // Помечаем оставшиеся пустыми как «новые после патча».
            _resyncNewItems.Clear();
            foreach (var it in translationItems)
                if (string.IsNullOrWhiteSpace(it.Translated))
                    _resyncNewItems.Add(it);

            ApplyTableSearch();
            UpdateRowHighlights();
            UpdateProgressStats();
            UpdateStatus();

            int total = translationItems.Count;
            int newCount = _resyncNewItems.Count;
            int already = total - filled - newCount;

            Log(L($"Re-sync done: carried over {filled}, new (untranslated) {newCount}, already translated {already}.",
                  $"Ре-синк завершён: перенесено {filled}, новых (без перевода) {newCount}, уже было переведено {already}."));

            MessageBox.Show(this,
                L($"Re-sync after patch:\r\n\r\n• Carried over from memory: {filled}\r\n• New strings to translate: {newCount} (highlighted blue)\r\n• Already translated: {already}\r\n• Harvested pairs: {harvested}",
                  $"Ре-синк после патча:\r\n\r\n• Перенесено из памяти: {filled}\r\n• Новых строк к переводу: {newCount} (подсвечены синим)\r\n• Уже переведено: {already}\r\n• Собрано пар: {harvested}"),
                L("Re-sync after game patch", "Ре-синк после патча игры"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Пары «оригинал→перевод» из папки прошлой версии: для каждого *.json берёт сосед *.json.bak (оригинал), мапит по пути в JSON, кладёт в память. Возвращает число пар.</summary>
        private int HarvestPairsFromOldVersionFolder(string folder)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
            }
            catch
            {
                return 0;
            }

            foreach (var liveFile in files)
            {
                var bakFile = liveFile + ".bak";
                if (!File.Exists(bakFile))
                    continue;
                try
                {
                    var liveMap = MapDisplayPathToValue(liveFile); // перевод (значение перезаписано при сохранении)
                    var bakMap = MapDisplayPathToValue(bakFile);   // оригинал (до перевода)
                    foreach (var kv in bakMap)
                    {
                        var original = kv.Value;
                        if (string.IsNullOrWhiteSpace(original))
                            continue;
                        if (!liveMap.TryGetValue(kv.Key, out var translated))
                            continue;
                        if (string.IsNullOrWhiteSpace(translated))
                            continue;
                        if (string.Equals(original, translated, StringComparison.Ordinal))
                            continue; // не переводили
                        pairs[original] = translated;
                    }
                }
                catch { } // битый файл — пропускаем
            }

            if (pairs.Count > 0)
                TranslationMemory.SaveMerge(pairs);
            return pairs.Count;
        }

        /// <summary>Карта «путь в JSON → строковое значение» одного файла (та же логика, что при извлечении в таблицу).</summary>
        private Dictionary<string, string> MapDisplayPathToValue(string file)
        {
            var list = new List<TranslationItem>();
            var root = JToken.Parse(File.ReadAllText(file));
            ExtractStrings(root, new List<string>(), Path.GetFileName(file), list);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var it in list)
                map[it.DisplayPath] = it.Original;
            return map;
        }
    }
}
