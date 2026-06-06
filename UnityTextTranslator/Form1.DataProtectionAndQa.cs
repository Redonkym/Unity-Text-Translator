using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityTextTranslator
{
    // Защита данных (флаг несохранённого, предупреждение при выходе, автосохранение)
    // + QA-проверка строк (плейсхолдеры/теги/переносы/непереведённое).
    partial class Form1
    {
        private bool _jsonDirty;
        private bool autosaveEnabled;
        private Timer _autosaveTimer;
        private ToolStripMenuItem _autosaveMenuItem;

        // ---------- Защита данных ----------

        private void MarkJsonDirty() => _jsonDirty = true;
        private void ClearJsonDirty() => _jsonDirty = false;

        /// <summary>Синхронная запись переводов в JSON (ядро для «Сохранить», автосейва и выхода).</summary>
        private int WriteAllTranslationsToJson(Action<int, int> onProgress = null)
        {
            int updated = 0;
            if (translationItems.Count == 0 || string.IsNullOrEmpty(currentFolder))
                return 0;

            var files = translationItems.GroupBy(x => x.FileName).Select(g => g.Key).ToList();
            int total = files.Count, processed = 0;

            foreach (var file in files)
            {
                var fullPath = Path.Combine(currentFolder, file);
                if (File.Exists(fullPath))
                {
                    if (createBackup)
                    {
                        var bp = fullPath + ".bak";
                        if (!File.Exists(bp))
                            File.Copy(fullPath, bp);
                    }
                    try
                    {
                        var root = JToken.Parse(File.ReadAllText(fullPath));
                        foreach (var item in translationItems.Where(x => x.FileName == file))
                        {
                            if (string.IsNullOrWhiteSpace(item.Translated)) continue;
                            if (UpdateJsonValue(root, item.PathKeys, item.Translated)) updated++;
                        }
                        File.WriteAllText(fullPath, root.ToString(Formatting.Indented));
                    }
                    catch (Exception ex) { Log($"Ошибка записи {file}: {ex.Message}", true); }
                }
                processed++;
                onProgress?.Invoke(processed, total);
            }
            return updated;
        }

        /// <summary>
        /// Аварийное сохранение «грязных» переводов при НЕОБРАБОТАННОМ исключении (зовётся из глобального
        /// обработчика в <see cref="Program"/>). Best-effort и полностью в try/catch, чтобы не уронить
        /// обработчик повторно. Грид НЕ трогаем (UI может быть в нестабильном состоянии) — пишем прямо из
        /// <see cref="translationItems"/> (закоммиченные правки ячеек туда уже перенесены через CellEndEdit).
        /// Возвращает число записанных строк, 0 — сохранять нечего, -1 — сбой записи.
        /// </summary>
        internal int TryEmergencySaveDirtyTranslations()
        {
            try
            {
                if (!_jsonDirty || translationItems.Count == 0 || string.IsNullOrEmpty(currentFolder))
                    return 0;
                int n = WriteAllTranslationsToJson();
                ClearJsonDirty();
                return n;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Вызвать из OnFormClosing. true — закрытие отменено.</summary>
        private bool PromptSaveIfDirtyOnClose(FormClosingEventArgs e)
        {
            if (!_jsonDirty || translationItems.Count == 0 || string.IsNullOrEmpty(currentFolder))
                return false;

            var r = MessageBox.Show(this,
                L("You have unsaved translations. Save them to JSON before exit?",
                  "Есть несохранённые переводы. Сохранить их в JSON перед выходом?"),
                L("Unsaved changes", "Несохранённые изменения"),
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

            if (r == DialogResult.Cancel)
            {
                e.Cancel = true;
                return true;
            }
            if (r == DialogResult.Yes)
            {
                try
                {
                    SyncGridToItems();
                    int n = WriteAllTranslationsToJson();
                    ClearJsonDirty();
                    Log(L($"Saved {n} rows to JSON before exit.", $"Перед выходом сохранено строк в JSON: {n}."));
                }
                catch (Exception ex)
                {
                    var rr = MessageBox.Show(this,
                        L("Save failed: ", "Не удалось сохранить: ") + ex.Message + "\n\n" +
                        L("Exit anyway (changes will be lost)?", "Всё равно выйти (изменения потеряются)?"),
                        L("Unsaved changes", "Несохранённые изменения"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                    if (rr != DialogResult.Yes)
                    {
                        e.Cancel = true;
                        return true;
                    }
                }
            }
            return false;
        }

        private void ApplyAutosaveSetting()
        {
            if (_autosaveTimer == null)
            {
                _autosaveTimer = new Timer { Interval = 120_000 }; // 2 мин
                _autosaveTimer.Tick += AutosaveTimer_Tick;
            }
            _autosaveTimer.Enabled = autosaveEnabled;
            if (_autosaveMenuItem != null && _autosaveMenuItem.Checked != autosaveEnabled)
                _autosaveMenuItem.Checked = autosaveEnabled;
        }

        private void AutosaveTimer_Tick(object sender, EventArgs e)
        {
            if (!autosaveEnabled || !_jsonDirty)
                return;
            if (!IsJsonTranslatorSurfaceHosted || translationItems.Count == 0 || string.IsNullOrEmpty(currentFolder))
                return;
            try
            {
                SyncGridToItems();
                int n = WriteAllTranslationsToJson();
                ClearJsonDirty();
                Log(L($"Autosave: {n} rows written to JSON.", $"Автосохранение: записано строк в JSON: {n}."));
            }
            catch (Exception ex) { Log(L("Autosave failed: ", "Автосохранение не удалось: ") + ex.Message, true); }
        }

        // ---------- QA-проверка ----------

        private static readonly Regex QaPlaceholderRegex =
            new Regex(@"\{[^{}]*\}|%[0-9]*\$?[a-zA-Z]", RegexOptions.Compiled);
        private static readonly Regex QaTagNameRegex =
            new Regex(@"</?[a-zA-Z][a-zA-Z0-9]*", RegexOptions.Compiled);

        private sealed class QaIssue
        {
            /// <summary>Ссылка на проблемный элемент (НЕ индекс): переход к строке находит её через row.Tag,
            /// поэтому остаётся точным даже если таблицу пересортировали между генерацией отчёта и кликом.</summary>
            public TranslationItem Item;
            public string File;
            public string Kind;
            public string Original;
            public string Translated;
        }

        private static List<string> QaTokens(string s, Regex rx)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (Match m in rx.Matches(s))
                list.Add(m.Value.ToLowerInvariant());
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static bool QaTokensEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static int QaNewlineCount(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int c = s.Count(ch => ch == '\n');
            c += Regex.Matches(s, @"\\n").Count; // литеральные \n
            return c;
        }

        private static bool QaHasLetters(string s) => !string.IsNullOrEmpty(s) && s.Any(char.IsLetter);

        private List<QaIssue> CollectQaIssues()
        {
            var issues = new List<QaIssue>();
            foreach (var it in translationItems)
            {
                string orig = it.Original ?? "";
                string tr = it.Translated ?? "";
                if (!QaHasLetters(orig))
                    continue; // нечего переводить (числа/символы)

                void Add(string kind) => issues.Add(new QaIssue
                {
                    Item = it,
                    File = it.FileName,
                    Kind = kind,
                    Original = orig,
                    Translated = tr
                });

                if (string.IsNullOrWhiteSpace(tr))
                {
                    Add(L("Untranslated (empty)", "Не переведено (пусто)"));
                    continue;
                }
                if (string.Equals(orig.Trim(), tr.Trim(), StringComparison.Ordinal))
                    Add(L("Same as original", "Совпадает с оригиналом"));
                if (!QaTokensEqual(QaTokens(orig, QaPlaceholderRegex), QaTokens(tr, QaPlaceholderRegex)))
                    Add(L("Placeholder mismatch ({0}, %s)", "Рассинхрон плейсхолдеров ({0}, %s)"));
                if (!QaTokensEqual(QaTokens(orig, QaTagNameRegex), QaTokens(tr, QaTagNameRegex)))
                    Add(L("Tag mismatch (<b>, <color>)", "Рассинхрон тегов (<b>, <color>)"));
                if (QaNewlineCount(orig) != QaNewlineCount(tr))
                    Add(L("Newline count differs", "Разное число переносов"));
            }
            return issues;
        }

        private void MenuRunQaCheck_Click(object sender, EventArgs e)
        {
            if (!RequireJsonTranslatorSurface(L("QA check", "QA-проверка")))
                return;
            SyncGridToItems();
            var issues = CollectQaIssues();
            if (issues.Count == 0)
            {
                MessageBox.Show(this,
                    L("No issues found. ", "Проблем не найдено. ") +
                    L($"Checked {translationItems.Count} rows.", $"Проверено строк: {translationItems.Count}."),
                    L("QA check", "QA-проверка"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ShowQaReportDialog(issues);
        }

        private void ShowQaReportDialog(List<QaIssue> issues)
        {
            Color formBg = _themePageBg;
            Color fieldBg = ThemeCardSurface();
            Color titleFg = _themeHeaderText;
            Color bodyFg = _themeGridRowFore;
            Color accent = DashboardAccentPrimary();

            var dlg = new Form
            {
                Text = L("QA check — issues", "QA-проверка — проблемы"),
                StartPosition = FormStartPosition.CenterParent,
                ShowIcon = false,
                ClientSize = new Size(900, 560),
                MinimumSize = new Size(560, 360),
                BackColor = formBg,
                ForeColor = titleFg
            };
            ApplyThemedTitleBar(dlg);

            int rowsWithIssues = issues.Select(x => x.Item).Distinct().Count();
            var head = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Text = L($"Found {issues.Count} issues in {rowsWithIssues} rows. Double-click a row to jump to it.",
                         $"Найдено проблем: {issues.Count} в {rowsWithIssues} строках. Двойной клик — перейти к строке."),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = bodyFg,
                Padding = new Padding(4, 6, 0, 0),
                BackColor = formBg
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = fieldBg,
                BorderStyle = BorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
            };
            grid.Columns.Add("File", L("File", "Файл"));
            grid.Columns.Add("Kind", L("Issue", "Проблема"));
            grid.Columns.Add("Original", L("Original", "Оригинал"));
            grid.Columns.Add("Translated", L("Translation", "Перевод"));
            grid.Columns["File"].FillWeight = 80;
            grid.Columns["Kind"].FillWeight = 90;
            grid.Columns["Original"].FillWeight = 130;
            grid.Columns["Translated"].FillWeight = 130;
            grid.BackgroundColor = fieldBg;
            grid.DefaultCellStyle.BackColor = fieldBg;
            grid.DefaultCellStyle.ForeColor = bodyFg;
            grid.DefaultCellStyle.SelectionBackColor = accent;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.ColumnHeadersDefaultCellStyle.BackColor = _themeGridHeaderBg;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = titleFg;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = _themeGridColor;

            string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ⏎ ");
            foreach (var iss in issues)
                grid.Rows.Add(iss.File, iss.Kind, OneLine(iss.Original), OneLine(iss.Translated));

            grid.CellDoubleClick += (_, ev) =>
            {
                if (ev.RowIndex < 0 || ev.RowIndex >= issues.Count) return;
                // Строку грида находим по ссылке на элемент (Tag), а не по сохранённому индексу — устойчиво к пересортировке.
                NavigateToTranslationRow(RowIndexOfItem(issues[ev.RowIndex].Item));
            };

            dlg.Controls.Add(grid);
            dlg.Controls.Add(head);
            dlg.FormClosed += (_, __) => dlg.Dispose();
            dlg.Show(this);
        }

        /// <summary>Переход к строке грида по индексу СТРОКИ грида (снимает фильтр поиска). -1 — нет строки, выходим.</summary>
        private void NavigateToTranslationRow(int index)
        {
            if (!IsJsonTranslatorSurfaceHosted)
            {
                ActivateNavByTag("Page");
                LoadJsonTranslatorModule();
            }
            if (dgv == null || dgv.IsDisposed)
                return;

            if (!string.IsNullOrEmpty(currentSearchText))
            {
                currentSearchText = "";
                ApplyTableSearch();
            }

            if (index < 0 || index >= dgv.Rows.Count)
                return;
            try
            {
                dgv.ClearSelection();
                var row = dgv.Rows[index];
                row.Selected = true;
                if (dgv.Columns.Contains("Translated"))
                    dgv.CurrentCell = row.Cells["Translated"];
                dgv.FirstDisplayedScrollingRowIndex = Math.Max(0, index - 3);
                dgv.Focus();
            }
            catch { /* ignore */ }
        }
    }
}
