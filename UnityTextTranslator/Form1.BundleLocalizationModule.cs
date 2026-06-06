using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    public partial class Form1
    {
        private string bundleLocGameDataFolder = "";
        private string bundleLocBundlePath = "";
        private string bundleLocJsonFolder = "";
        private string bundleLocOutputBundlePath = "";
        private string bundleLocLocalesBundlePath = "";
        private string bundleLocLocalesCode = "ru";
        private string bundleLocLocalesOutputPath = "";
        private bool bundleLocMonoBehaviourOnlySaved;
        private bool bundleLocOverwriteSourceAfterBuildSaved = true;
        private TextBox bundleLocGameDataTextBox;
        private Button bundleLocPickGameDataButton;
        private TextBox bundleLocBundlePathTextBox;
        private Button bundleLocPickBundleButton;
        private TextBox bundleLocJsonFolderTextBox;
        private Button bundleLocPickJsonFolderButton;
        private TextBox bundleLocOutputBundleTextBox;
        private Button bundleLocPickOutputBundleButton;
        private TextBox bundleLocLocalesBundleTextBox;
        private Button bundleLocPickLocalesBundleButton;
        private TextBox bundleLocLocalesCodeTextBox;
        private TextBox bundleLocLocalesOutputTextBox;
        private Button bundleLocPickLocalesOutputButton;
        private CheckBox bundleLocMonoBehaviourOnlyCheck;
        private CheckBox bundleLocOverwriteSourceAfterBuildCheck;
        private Button bundleLocExportButton;
        private Button bundleLocPackButton;
        private Button bundleLocPatchLocalesButton;
        private RichTextBox bundleLocLogBox;

        private void LoadBundleLocalizationModule()
        {
            ShowChromeHeader();
            ClearAssetsModuleRefs();
            ClearBundleLocModuleRefs();
            DetachModuleEvents();
            ClearContentPanel();

            // Без заголовка-страницы — экономим место (как в разделе JSON).
            if (headerPanel != null && !headerPanel.IsDisposed)
                headerPanel.Height = 6;
            if (headerLabel != null && !headerLabel.IsDisposed)
            {
                headerLabel.Text = "";
                headerLabel.Visible = false;
            }

            if (string.IsNullOrWhiteSpace(bundleLocJsonFolder) || !Directory.Exists(bundleLocJsonFolder))
            {
                bundleLocJsonFolder = !string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder)
                    ? currentFolder
                    : "";
            }

            var mainTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 8, 12, 8),
                ColumnCount = 3,
                AutoScroll = true,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232f));
            mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112f));
            mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int AddFieldRow(string labelEn, string labelRu, TextBox tb, Button browse, Action browseAction)
            {
                var r = mainTable.RowCount++;
                mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));

                var lbl = new Label
                {
                    Text = L(labelEn, labelRu),
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Margin = new Padding(0, 2, 8, 2)
                };
                browse.Margin = new Padding(0, 4, 8, 4);
                browse.Dock = DockStyle.Fill;
                browse.Click += (_, __) => browseAction();
                tb.Margin = new Padding(0, 4, 0, 4);
                tb.Dock = DockStyle.Fill;

                mainTable.Controls.Add(lbl, 0, r);
                mainTable.Controls.Add(browse, 1, r);
                mainTable.Controls.Add(tb, 2, r);
                return r;
            }

            int AddFullWidthRow(Control c, float rowHeight)
            {
                var r = mainTable.RowCount++;
                mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
                c.Dock = DockStyle.Fill;
                mainTable.Controls.Add(c, 0, r);
                mainTable.SetColumnSpan(c, 3);
                return r;
            }

            // Строки, которые прячутся под «Дополнительно» (индекс строки + её высота).
            var advancedRows = new List<(int idx, float h)>();
            void CollapseAdvanced(bool show)
            {
                mainTable.SuspendLayout();
                foreach (var (idx, h) in advancedRows)
                {
                    mainTable.RowStyles[idx].Height = show ? h : 0f;
                    for (int c = 0; c < mainTable.ColumnCount; c++)
                    {
                        var ctl = mainTable.GetControlFromPosition(c, idx);
                        if (ctl != null) ctl.Visible = show;
                    }
                }
                mainTable.ResumeLayout();
            }

            void AddLabeledWideTextRow(string labelEn, string labelRu, TextBox tb, float rowHeight = 42f)
            {
                var r = mainTable.RowCount++;
                mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
                var lbl = new Label
                {
                    Text = L(labelEn, labelRu),
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Margin = new Padding(0, 2, 8, 2)
                };
                tb.Margin = new Padding(0, 4, 0, 4);
                tb.Dock = DockStyle.Fill;
                mainTable.Controls.Add(lbl, 0, r);
                mainTable.Controls.Add(tb, 1, r);
                mainTable.SetColumnSpan(tb, 2);
            }

            bundleLocPickBundleButton = CreateModernButton(L("Browse…", "Выбрать…"), ButtonStyleKind.Secondary);
            bundleLocBundlePathTextBox = new TextBox { Font = new Font("Segoe UI", 10f) };
            bundleLocBundlePathTextBox.Text = bundleLocBundlePath;
            AddFieldRow(
                "Main .bundle (Russian — Replace target):",
                "Основной .bundle (русский — что заменять на диске):",
                bundleLocBundlePathTextBox,
                bundleLocPickBundleButton,
                () =>
                {
                    using (var ofd = new OpenFileDialog())
                    {
                        ofd.Filter = L("Asset bundles|*.bundle;*.unity3d|All files|*.*", "Бандлы|*.bundle;*.unity3d|Все файлы|*.*");
                        if (File.Exists(bundleLocBundlePath))
                            ofd.FileName = bundleLocBundlePath;
                        if (ofd.ShowDialog(this) == DialogResult.OK)
                        {
                            bundleLocBundlePath = ofd.FileName;
                            bundleLocBundlePathTextBox.Text = bundleLocBundlePath;

                            var inferred =
                                UnityAssetsGameFolderHelper.TryInferGameDataAncestorFromBundlePath(
                                    bundleLocBundlePath);
                            var currentGd = (bundleLocGameDataFolder ?? "").Trim();

                            bool mismatch = false;
                            try
                            {
                                var resolvedGd =
                                    string.IsNullOrWhiteSpace(currentGd)
                                        ? null
                                        : UnityAssetsGameFolderHelper.ResolveGameDataFolder(currentGd);

                                var nResolved =
                                    UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(resolvedGd);
                                var nInferred =
                                    UnityAssetsGameFolderHelper.NormalizeGameDataFolderPathOrNull(inferred);

                                mismatch = nResolved != null && nInferred != null &&
                                           !nResolved.Equals(nInferred, StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                /* ignore */
                            }

                            if ((!string.IsNullOrWhiteSpace(inferred)) &&
                                string.IsNullOrWhiteSpace(currentGd))
                            {
                                bundleLocGameDataFolder = inferred;
                                if (bundleLocGameDataTextBox != null && !bundleLocGameDataTextBox.IsDisposed)
                                    bundleLocGameDataTextBox.Text = inferred;

                                BundleLocAppendLog(L(
                                        $"Filled Game Data folder from bundle path ({Path.GetFileName(inferred)}).",
                                        "Папка данных игры автоматически задана по пути bundle: «" +
                                        Path.GetFileName(inferred) +
                                        "» — нужен именно этот сборник для Managed/локализации."));
                            }
                            else if (mismatch)
                            {
                                BundleLocAppendLog(L(
                                        "Game Data folder does not contain this bundle — use the game's Name_Data folder that StreamingAssets sits under.",
                                        "Внимание: укажите ту же игру что и этот bundle. Bundle лежит внутри «" +
                                        inferred + "», а сейчас выбрана другая *_Data («" + currentGd +
                                        "») — экспорт строк часто будет пустой."));
                            }

                            if (string.IsNullOrWhiteSpace(bundleLocOutputBundlePath))
                            {
                                bundleLocOutputBundlePath = Path.Combine(
                                    Path.GetDirectoryName(bundleLocBundlePath) ?? "",
                                    Path.GetFileNameWithoutExtension(bundleLocBundlePath) + ".patched.bundle");
                                if (bundleLocOutputBundleTextBox != null && !bundleLocOutputBundleTextBox.IsDisposed)
                                    bundleLocOutputBundleTextBox.Text = bundleLocOutputBundlePath;
                            }

                            SaveSettings();
                        }
                    }
                });

            bundleLocPickJsonFolderButton = CreateModernButton(L("Browse…", "Выбрать…"), ButtonStyleKind.Secondary);
            bundleLocJsonFolderTextBox = new TextBox { Font = new Font("Segoe UI", 10f) };
            bundleLocJsonFolderTextBox.Text = bundleLocJsonFolder;
            AddFieldRow(
                "Working JSON folder:",
                "Рабочая папка JSON:",
                bundleLocJsonFolderTextBox,
                bundleLocPickJsonFolderButton,
                () =>
                {
                    using (var fbd = new FolderBrowserDialog())
                    {
                        fbd.Description = L("Folder with exported JSON (same layout as UABEA)", "Папка с экспортированным JSON (как в UABEA)");
                        if (Directory.Exists(bundleLocJsonFolder))
                            fbd.SelectedPath = bundleLocJsonFolder;
                        if (fbd.ShowDialog(this) == DialogResult.OK)
                        {
                            bundleLocJsonFolder = fbd.SelectedPath;
                            bundleLocJsonFolderTextBox.Text = bundleLocJsonFolder;
                            SaveSettings();
                        }
                    }
                });

            // ——— Раскрывашка «Дополнительно»: редко нужные пути по умолчанию скрыты ———
            var advToggle = new LinkLabel
            {
                Text = L("▸ Advanced", "▸ Дополнительно (эти пути обычно заполняются сами)"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                LinkBehavior = LinkBehavior.NeverUnderline,
                LinkColor = DashboardAccentPrimary(),
                ActiveLinkColor = DashboardAccentPrimary(),
                Margin = new Padding(0, 8, 8, 4)
            };
            AddFullWidthRow(advToggle, 34f);

            bool advancedShown = false;
            advToggle.LinkClicked += (_, __) =>
            {
                advancedShown = !advancedShown;
                advToggle.Text = advancedShown
                    ? L("▾ Advanced", "▾ Дополнительно (эти пути обычно заполняются сами)")
                    : L("▸ Advanced", "▸ Дополнительно (эти пути обычно заполняются сами)");
                CollapseAdvanced(advancedShown);
            };

            int advStart = mainTable.RowCount;

            bundleLocPickGameDataButton = CreateModernButton(L("Browse…", "Выбрать…"), ButtonStyleKind.Secondary);
            bundleLocGameDataTextBox = new TextBox { Font = new Font("Segoe UI", 10f), ReadOnly = false };
            bundleLocGameDataTextBox.Text = bundleLocGameDataFolder;
            AddFieldRow(
                "Game Data folder (*_Data):",
                "Папка данных игры (*_Data):",
                bundleLocGameDataTextBox,
                bundleLocPickGameDataButton,
                () =>
                {
                    using (var fbd = new FolderBrowserDialog())
                    {
                        fbd.Description = L("Select the game's Name_Data folder (Managed, StreamingAssets…)", "Выберите папку Name_Data игры (Managed, StreamingAssets…)");
                        if (Directory.Exists(bundleLocGameDataFolder))
                            fbd.SelectedPath = bundleLocGameDataFolder;
                        if (fbd.ShowDialog(this) == DialogResult.OK)
                        {
                            bundleLocGameDataFolder = fbd.SelectedPath;
                            bundleLocGameDataTextBox.Text = bundleLocGameDataFolder;
                            SaveSettings();
                        }
                    }
                });

            bundleLocPickOutputBundleButton = CreateModernButton(L("Browse…", "Выбрать…"), ButtonStyleKind.Secondary);
            bundleLocOutputBundleTextBox = new TextBox { Font = new Font("Segoe UI", 10f) };
            bundleLocOutputBundleTextBox.Text = bundleLocOutputBundlePath;
            AddFieldRow(
                "Output .bundle (pack):",
                "Выходной .bundle (сборка):",
                bundleLocOutputBundleTextBox,
                bundleLocPickOutputBundleButton,
                () =>
                {
                    using (var sfd = new SaveFileDialog())
                    {
                        sfd.Filter = L("Asset bundles|*.bundle|All files|*.*", "Бандлы|*.bundle|Все файлы|*.*");
                        sfd.DefaultExt = "bundle";
                        if (!string.IsNullOrWhiteSpace(bundleLocOutputBundlePath))
                            sfd.FileName = bundleLocOutputBundlePath;
                        if (sfd.ShowDialog(this) == DialogResult.OK)
                        {
                            bundleLocOutputBundlePath = sfd.FileName;
                            bundleLocOutputBundleTextBox.Text = bundleLocOutputBundlePath;
                            SaveSettings();
                        }
                    }
                });

            void PersistBundleLocTypedPaths(object _, EventArgs __)
            {
                SyncBundleLocFieldsFromUi();
                SaveSettings();
            }

            bundleLocGameDataTextBox.Leave += PersistBundleLocTypedPaths;
            bundleLocBundlePathTextBox.Leave += PersistBundleLocTypedPaths;
            bundleLocJsonFolderTextBox.Leave += PersistBundleLocTypedPaths;
            bundleLocOutputBundleTextBox.Leave += PersistBundleLocTypedPaths;

            bundleLocPickLocalesBundleButton = CreateModernButton(L("Browse…", "Обзор…"), ButtonStyleKind.Secondary);
            bundleLocLocalesBundleTextBox = new TextBox { Font = new Font("Segoe UI", 10f) };
            bundleLocLocalesBundleTextBox.Text = bundleLocLocalesBundlePath;
            AddFieldRow(
                "Locales .bundle (localization-locales…):",
                "Бандл списка языков (localization-locales…):",
                bundleLocLocalesBundleTextBox,
                bundleLocPickLocalesBundleButton,
                () =>
                {
                    using (var ofd = new OpenFileDialog())
                    {
                        ofd.Filter = L("Asset bundles|*.bundle;*.unity3d|All files|*.*", "Бандлы|*.bundle;*.unity3d|Все файлы|*.*");
                        if (File.Exists(bundleLocLocalesBundlePath))
                            ofd.FileName = bundleLocLocalesBundlePath;
                        if (ofd.ShowDialog(this) == DialogResult.OK)
                        {
                            bundleLocLocalesBundlePath = ofd.FileName;
                            bundleLocLocalesBundleTextBox.Text = bundleLocLocalesBundlePath;
                            if (string.IsNullOrWhiteSpace(bundleLocLocalesOutputPath))
                            {
                                bundleLocLocalesOutputPath = Path.Combine(
                                    Path.GetDirectoryName(bundleLocLocalesBundlePath) ?? "",
                                    Path.GetFileNameWithoutExtension(bundleLocLocalesBundlePath) + ".locales-patched.bundle");
                                if (bundleLocLocalesOutputTextBox != null && !bundleLocLocalesOutputTextBox.IsDisposed)
                                    bundleLocLocalesOutputTextBox.Text = bundleLocLocalesOutputPath;
                            }
                            SaveSettings();
                        }
                    }
                });

            {
                var r = mainTable.RowCount++;
                mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
                var lbl = new Label
                {
                    Text = L("Locale code to enable:", "Код локали (включить в меню):"),
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Margin = new Padding(0, 2, 8, 2)
                };
                bundleLocLocalesCodeTextBox = new TextBox
                {
                    Font = new Font("Segoe UI", 10f),
                    Text = bundleLocLocalesCode,
                    Margin = new Padding(0, 4, 0, 4),
                    Dock = DockStyle.Fill
                };
                var z = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
                mainTable.Controls.Add(lbl, 0, r);
                mainTable.Controls.Add(z, 1, r);
                mainTable.Controls.Add(bundleLocLocalesCodeTextBox, 2, r);
            }

            bundleLocPickLocalesOutputButton = CreateModernButton(L("Browse…", "Обзор…"), ButtonStyleKind.Secondary);
            bundleLocLocalesOutputTextBox = new TextBox { Font = new Font("Segoe UI", 10f) };
            bundleLocLocalesOutputTextBox.Text = bundleLocLocalesOutputPath;
            AddFieldRow(
                "Output locales .bundle:",
                "Выходной locales .bundle:",
                bundleLocLocalesOutputTextBox,
                bundleLocPickLocalesOutputButton,
                () =>
                {
                    using (var sfd = new SaveFileDialog())
                    {
                        sfd.Filter = L("Asset bundles|*.bundle|All files|*.*", "Бандлы|*.bundle|Все файлы|*.*");
                        sfd.DefaultExt = "bundle";
                        if (!string.IsNullOrWhiteSpace(bundleLocLocalesOutputPath))
                            sfd.FileName = bundleLocLocalesOutputPath;
                        if (sfd.ShowDialog(this) == DialogResult.OK)
                        {
                            bundleLocLocalesOutputPath = sfd.FileName;
                            bundleLocLocalesOutputTextBox.Text = bundleLocLocalesOutputPath;
                            SaveSettings();
                        }
                    }
                });

            bundleLocLocalesBundleTextBox.Leave += PersistBundleLocTypedPaths;
            bundleLocLocalesCodeTextBox.Leave += PersistBundleLocTypedPaths;
            bundleLocLocalesOutputTextBox.Leave += PersistBundleLocTypedPaths;

            bundleLocMonoBehaviourOnlyCheck = new CheckBox
            {
                Text = " " + L("MonoBehaviour + TextAsset only (smaller export)", "Только MonoBehaviour и TextAsset (меньше файлов; в bundle — ещё CSV/таблицы в TextAsset)"),
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(4, 6, 8, 4),
                Font = new Font("Segoe UI", 10f),
                ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(31, 35, 40),
                Checked = bundleLocMonoBehaviourOnlySaved
            };

            bundleLocMonoBehaviourOnlyCheck.CheckedChanged += (_, __) =>
            {
                bundleLocMonoBehaviourOnlySaved = bundleLocMonoBehaviourOnlyCheck.Checked;
                SaveSettings();
            };

            AddFullWidthRow(bundleLocMonoBehaviourOnlyCheck, 40f);

            bundleLocOverwriteSourceAfterBuildCheck = new CheckBox
            {
                Text = " " + L(
                    "After Pack / locales patch: copy result over source .bundle (backup *.utt-orig-backup)",
                    "После сборки / патча locales: копировать результат поверх исходного .bundle (бэкап *.utt-orig-backup)"),
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(4, 6, 8, 4),
                Font = new Font("Segoe UI", 10f),
                ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(31, 35, 40),
                Checked = bundleLocOverwriteSourceAfterBuildSaved
            };
            bundleLocOverwriteSourceAfterBuildCheck.CheckedChanged += (_, __) =>
            {
                bundleLocOverwriteSourceAfterBuildSaved = bundleLocOverwriteSourceAfterBuildCheck.Checked;
                SaveSettings();
            };
            AddFullWidthRow(bundleLocOverwriteSourceAfterBuildCheck, 40f);

            // Все строки от «Дополнительно» до сюда — скрываемые; сворачиваем по умолчанию.
            for (int i = advStart; i < mainTable.RowCount; i++)
                advancedRows.Add((i, mainTable.RowStyles[i].Height));
            CollapseAdvanced(false);

            var btnFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
            // Ширина по тексту: иначе длинные подписи («Экспорт bundle → JSON») обрезаются «…».
            void FitButtonToText(Button b)
            {
                int tw = TextRenderer.MeasureText(b.Text, b.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                b.Width = Math.Max(b.Width, tw + 36);
            }

            bundleLocExportButton = CreateModernButton(L("Export bundle → JSON", "Экспорт bundle → JSON"), ButtonStyleKind.Primary);
            bundleLocExportButton.Margin = new Padding(0, 0, 12, 0);
            bundleLocPackButton = CreateModernButton(L("Pack JSON → bundle", "Сборка JSON → bundle"), ButtonStyleKind.Primary);
            bundleLocPackButton.Margin = new Padding(0, 0, 12, 0);
            bundleLocPatchLocalesButton = CreateModernButton(L("Patch locales bundle", "Патч списка языков"), ButtonStyleKind.Primary);
            bundleLocPatchLocalesButton.Margin = new Padding(0, 0, 12, 0);
            FitButtonToText(bundleLocExportButton);
            FitButtonToText(bundleLocPackButton);
            FitButtonToText(bundleLocPatchLocalesButton);
            btnFlow.Controls.Add(bundleLocExportButton);
            btnFlow.Controls.Add(bundleLocPackButton);
            btnFlow.Controls.Add(bundleLocPatchLocalesButton);
            AddFullWidthRow(btnFlow, 54f);

            bundleLocExportButton.Click += async (_, __) => await RunBundleExportAsync();
            bundleLocPackButton.Click += async (_, __) => await RunBundlePackAsync();
            bundleLocPatchLocalesButton.Click += async (_, __) => await RunBundlePatchLocalesAsync();

            bundleLocLogBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f),
                BackColor = isDarkTheme ? Color.FromArgb(14, 14, 16) : Color.FromArgb(246, 248, 250),
                ForeColor = isDarkTheme ? Color.FromArgb(196, 181, 253) : Color.FromArgb(26, 127, 55)
            };

            // Разделитель форма/журнал — как в JSON-разделе: лог можно тянуть, он «прикреплён» снизу.
            var bundleGridLogSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 8,
                FixedPanel = FixedPanel.None,
                Panel1MinSize = 160,
                Panel2MinSize = 80,
                BackColor = Color.Transparent
            };
            bundleGridLogSplit.Panel1.Controls.Add(mainTable);
            bundleGridLogSplit.Panel2.Controls.Add(bundleLocLogBox);
            bundleGridLogSplit.HandleCreated += (_, __) =>
            {
                BeginInvoke(new Action(() =>
                {
                    if (bundleGridLogSplit.Height < 200)
                        return;
                    int panel2 = Math.Min(220, Math.Max(bundleGridLogSplit.Panel2MinSize + 40, bundleGridLogSplit.Height / 4));
                    int dist = bundleGridLogSplit.Height - panel2 - bundleGridLogSplit.SplitterWidth;
                    if (dist >= bundleGridLogSplit.Panel1MinSize)
                        bundleGridLogSplit.SplitterDistance = dist;
                }));
            };

            moduleHostPanel.Controls.Add(bundleGridLogSplit);

            ApplyTheme();
            SyncBundleLocFieldsFromUi();
            BundleLocAppendLog(L("Bundle localization module.", "Раздел локализации через Asset Bundle."));
            UpdateStatus();
        }

        private void SyncBundleLocFieldsFromUi()
        {
            if (bundleLocGameDataTextBox != null && !bundleLocGameDataTextBox.IsDisposed)
                bundleLocGameDataFolder = bundleLocGameDataTextBox.Text.Trim();
            if (bundleLocBundlePathTextBox != null && !bundleLocBundlePathTextBox.IsDisposed)
                bundleLocBundlePath = bundleLocBundlePathTextBox.Text.Trim();
            if (bundleLocJsonFolderTextBox != null && !bundleLocJsonFolderTextBox.IsDisposed)
                bundleLocJsonFolder = bundleLocJsonFolderTextBox.Text.Trim();
            if (bundleLocOutputBundleTextBox != null && !bundleLocOutputBundleTextBox.IsDisposed)
                bundleLocOutputBundlePath = bundleLocOutputBundleTextBox.Text.Trim();
            if (bundleLocLocalesBundleTextBox != null && !bundleLocLocalesBundleTextBox.IsDisposed)
                bundleLocLocalesBundlePath = bundleLocLocalesBundleTextBox.Text.Trim();
            if (bundleLocLocalesCodeTextBox != null && !bundleLocLocalesCodeTextBox.IsDisposed)
                bundleLocLocalesCode = bundleLocLocalesCodeTextBox.Text.Trim();
            if (bundleLocLocalesOutputTextBox != null && !bundleLocLocalesOutputTextBox.IsDisposed)
                bundleLocLocalesOutputPath = bundleLocLocalesOutputTextBox.Text.Trim();
            if (bundleLocOverwriteSourceAfterBuildCheck != null && !bundleLocOverwriteSourceAfterBuildCheck.IsDisposed)
                bundleLocOverwriteSourceAfterBuildSaved = bundleLocOverwriteSourceAfterBuildCheck.Checked;
        }

        private void BundleLocMaybeOverwriteSourceAfterBuild(string originalBundlePath, string patchedBundlePath, string whatEn, string whatRu)
        {
            if (bundleLocOverwriteSourceAfterBuildCheck == null || bundleLocOverwriteSourceAfterBuildCheck.IsDisposed ||
                !bundleLocOverwriteSourceAfterBuildCheck.Checked)
                return;
            if (string.IsNullOrWhiteSpace(originalBundlePath) || !File.Exists(originalBundlePath))
                return;
            if (string.IsNullOrWhiteSpace(patchedBundlePath) || !File.Exists(patchedBundlePath))
                return;
            if (string.Equals(
                    Path.GetFullPath(originalBundlePath),
                    Path.GetFullPath(patchedBundlePath),
                    StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var backupPath = originalBundlePath + ".utt-orig-backup";
                File.Copy(originalBundlePath, backupPath, overwrite: true);
                File.Copy(patchedBundlePath, originalBundlePath, overwrite: true);
                BundleLocAppendLog(L(
                    $"{whatEn}: replaced original. Backup → «{Path.GetFileName(backupPath)}».",
                    $"{whatRu}: исходный файл заменён. Бэкап → «{Path.GetFileName(backupPath)}»."));

                var catalogMsgs = new List<string>();
                AddressablesCatalogCrcInterop.TryPatchCatalogsNearBundle(originalBundlePath, catalogMsgs);
                foreach (var line in catalogMsgs)
                    BundleLocAppendLog(line);
            }
            catch (Exception ex)
            {
                BundleLocAppendLog(L("Overwrite source failed: ", "Не удалось заменить исходный bundle: ") + ex.Message);
                MessageBox.Show(ex.Message, L("Bundles", "Бандлы"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BundleLocAppendLog(string line)
        {
            if (bundleLocLogBox == null || bundleLocLogBox.IsDisposed)
                return;

            void Append()
            {
                bundleLocLogBox.SelectionStart = bundleLocLogBox.TextLength;
                bundleLocLogBox.SelectionLength = 0;
                bundleLocLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
                bundleLocLogBox.ScrollToCaret();
            }

            if (bundleLocLogBox.InvokeRequired)
                bundleLocLogBox.Invoke(new Action(Append));
            else
                Append();
        }

        private async Task RunBundleExportAsync()
        {
            SyncBundleLocFieldsFromUi();
            if (string.IsNullOrWhiteSpace(bundleLocBundlePath) || !File.Exists(bundleLocBundlePath))
            {
                MessageBox.Show(
                    L("Select an existing .bundle file.", "Выберите существующий файл .bundle."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(bundleLocJsonFolder))
            {
                MessageBox.Show(
                    L("Select the folder where JSON should be written.", "Выберите папку для записи JSON."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SetBundleLocExportPackEnabled(false);
            UseWaitCursor = true;
            try
            {
                await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => BundleLocAppendLog(msg)).ConfigureAwait(true);

                try
                {
                    var bp = Path.GetFullPath(bundleLocBundlePath ?? "");
                    var op = Path.GetFullPath((bundleLocOutputBundlePath ?? "").Trim());
                    if (!string.IsNullOrWhiteSpace(bundleLocOutputBundlePath) && File.Exists(bundleLocOutputBundlePath) &&
                        !bp.Equals(op, StringComparison.OrdinalIgnoreCase))
                        BundleLocAppendLog(L(
                            "Export reads «Source bundle» only — it differs from «Output bundle». Point Source at the file Pack wrote, or exported JSON will still match the old bundle.",
                            "Экспорт читает только поле «исходный bundle». Оно отличается от «выход сборки» — укажите в «исходном» тот же файл, куда писала сборка, иначе JSON останется со старым содержимым."));
                }
                catch
                {
                    /* ignore */
                }

                var gameRoot = bundleLocGameDataFolder;
                var bundlePath = bundleLocBundlePath;
                var outFolder = bundleLocJsonFolder;
                var monoOnly = bundleLocMonoBehaviourOnlyCheck != null && bundleLocMonoBehaviourOnlyCheck.Checked;

                var result = await Task.Run(() =>
                    LocalizationBundleJsonInterop.ExportBundleToJson(
                        bundlePath,
                        outFolder,
                        monoOnly,
                        gameRoot,
                        UabeaJsonFileLayout.UabeaMonoScriptNameFlat)).ConfigureAwait(true);

                BundleLocAppendLog(L(
                    $"Done. CAB blocks: {result.AssetFilesScanned}, exported JSON objects: {result.Exported}, failed: {result.Failed}.",
                    $"Готово. CAB в bundle: {result.AssetFilesScanned}, экспортировано JSON: {result.Exported}, ошибок: {result.Failed}."));

                foreach (var msg in result.Messages)
                    BundleLocAppendLog(msg);

                Log(L($"Bundle export → «{outFolder}»", $"Экспорт bundle → «{outFolder}»"));
                SaveSettings();
            }
            catch (Exception ex)
            {
                BundleLocAppendLog(ex.ToString());
                MessageBox.Show(ex.Message, L("Export failed", "Ошибка экспорта"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                SetBundleLocExportPackEnabled(true);
            }
        }

        private async Task RunBundlePackAsync()
        {
            SyncBundleLocFieldsFromUi();
            if (string.IsNullOrWhiteSpace(bundleLocBundlePath) || !File.Exists(bundleLocBundlePath))
            {
                MessageBox.Show(
                    L("Select the source .bundle to patch.", "Выберите исходный .bundle для правки."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(bundleLocJsonFolder) || !Directory.Exists(bundleLocJsonFolder))
            {
                MessageBox.Show(
                    L("Select the folder that contains the edited JSON.", "Выберите папку с отредактированным JSON."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(bundleLocOutputBundlePath))
            {
                MessageBox.Show(
                    L("Select output path for the new .bundle.", "Укажите путь для сохранения нового .bundle."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SetBundleLocExportPackEnabled(false);
            UseWaitCursor = true;
            try
            {
                await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => BundleLocAppendLog(msg)).ConfigureAwait(true);

                var gameRoot = bundleLocGameDataFolder;
                var bundlePath = bundleLocBundlePath;
                var jsonFolder = bundleLocJsonFolder;
                var outBundle = bundleLocOutputBundlePath;

                var result = await Task.Run(() =>
                    LocalizationBundleJsonInterop.ImportJsonIntoBundle(
                        bundlePath, jsonFolder, outBundle, gameRoot,
                        null)).ConfigureAwait(true);

                BundleLocAppendLog(L(
                    $"Pack OK → «{outBundle}». Imported: {result.Imported}, skipped: {result.Skipped}, failed entries: {result.Failed}.",
                    $"Сборка OK → «{outBundle}». Импортировано: {result.Imported}, пропусков: {result.Skipped}, сбоев записей: {result.Failed}."));

                foreach (var msg in result.Messages)
                    BundleLocAppendLog(msg);

                Log(L($"Bundle pack → «{outBundle}»", $"Сборка bundle → «{outBundle}»"));
                SaveSettings();
                BundleLocMaybeOverwriteSourceAfterBuild(bundleLocBundlePath, outBundle, "Pack", "Сборка");
            }
            catch (Exception ex)
            {
                BundleLocAppendLog(ex.ToString());
                MessageBox.Show(ex.Message, L("Pack failed", "Ошибка сборки"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                SetBundleLocExportPackEnabled(true);
            }
        }

        private async Task RunBundlePatchLocalesAsync()
        {
            SyncBundleLocFieldsFromUi();
            if (string.IsNullOrWhiteSpace(bundleLocLocalesBundlePath) || !File.Exists(bundleLocLocalesBundlePath))
            {
                MessageBox.Show(
                    L("Select localization-locales .bundle.", "Выберите .bundle со списком языков (localization-locales…)."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(bundleLocLocalesCode))
            {
                MessageBox.Show(
                    L("Enter locale code (e.g. ru).", "Укажите код локали (например ru)."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(bundleLocLocalesOutputPath))
            {
                MessageBox.Show(
                    L("Select output path for the patched locales .bundle.", "Укажите путь для сохранения пропатченного locales .bundle."),
                    L("Bundles", "Бандлы"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SetBundleLocExportPackEnabled(false);
            UseWaitCursor = true;
            try
            {
                await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => BundleLocAppendLog(msg)).ConfigureAwait(true);

                var gameRoot = bundleLocGameDataFolder;
                var locBundle = bundleLocLocalesBundlePath;
                var code = bundleLocLocalesCode;
                var outBundle = bundleLocLocalesOutputPath;

                var result = await Task.Run(() =>
                    LocalizationBundleJsonInterop.PatchLocalesBundleEnableLanguage(locBundle, code, outBundle, gameRoot)).ConfigureAwait(true);

                BundleLocAppendLog(result.Imported == 0 && result.LocaleMatchCount > 0
                    ? L(
                        $"Locales: nothing to change (locale OK). Matched: {result.LocaleMatchCount}. Output file not updated — use Pack on russian string-tables bundle.",
                        $"Locales: менять нечего (локаль в порядке). Найдено: {result.LocaleMatchCount}. Файл не обновлялся — делайте сборку в localization-string-tables-russian(ru)….bundle.")
                    : L(
                        $"Locales patch → «{outBundle}». Locale assets matched: {result.LocaleMatchCount}, CAB data writes: {result.Imported}.",
                        $"Патч locales → «{outBundle}». Найдено Locale по коду: {result.LocaleMatchCount}, записано CAB: {result.Imported}."));

                foreach (var msg in result.Messages)
                    BundleLocAppendLog(msg);

                if (result.Imported > 0)
                    Log(L($"Locales bundle patch → «{outBundle}»", $"Патч списка языков → «{outBundle}»"));
                SaveSettings();
                if (result.Imported > 0)
                    BundleLocMaybeOverwriteSourceAfterBuild(bundleLocLocalesBundlePath, outBundle, "Locales patch", "Патч locales");
            }
            catch (Exception ex)
            {
                BundleLocAppendLog(ex.ToString());
                MessageBox.Show(ex.Message, L("Locales patch failed", "Ошибка патча locales"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                SetBundleLocExportPackEnabled(true);
            }
        }

        /// <summary>Включает/выключает кнопки экспорта и сборки; проверяет null — после await модуль мог быть закрыт.</summary>
        private void SetBundleLocExportPackEnabled(bool enabled)
        {
            if (bundleLocExportButton != null && !bundleLocExportButton.IsDisposed)
                bundleLocExportButton.Enabled = enabled;
            if (bundleLocPackButton != null && !bundleLocPackButton.IsDisposed)
                bundleLocPackButton.Enabled = enabled;
            if (bundleLocPatchLocalesButton != null && !bundleLocPatchLocalesButton.IsDisposed)
                bundleLocPatchLocalesButton.Enabled = enabled;
        }

        private void ClearBundleLocModuleRefs()
        {
            bundleLocGameDataTextBox = null;
            bundleLocPickGameDataButton = null;
            bundleLocBundlePathTextBox = null;
            bundleLocPickBundleButton = null;
            bundleLocJsonFolderTextBox = null;
            bundleLocPickJsonFolderButton = null;
            bundleLocOutputBundleTextBox = null;
            bundleLocPickOutputBundleButton = null;
            bundleLocLocalesBundleTextBox = null;
            bundleLocPickLocalesBundleButton = null;
            bundleLocLocalesCodeTextBox = null;
            bundleLocLocalesOutputTextBox = null;
            bundleLocPickLocalesOutputButton = null;
            bundleLocMonoBehaviourOnlyCheck = null;
            bundleLocOverwriteSourceAfterBuildCheck = null;
            bundleLocExportButton = null;
            bundleLocPackButton = null;
            bundleLocPatchLocalesButton = null;
            bundleLocLogBox = null;
        }
    }
}
