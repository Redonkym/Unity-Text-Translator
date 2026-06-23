using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    /// <summary>
    /// Модуль «Текстуры»: слева контейнеры (.assets+бандлы) и текстуры, справа превью, снизу журнал. Замена фонов/картинок с запечённым текстом.
    /// Ядро — <see cref="TextureReplacePatcher"/>.
    /// </summary>
    public partial class Form1
    {
        // Состояние выбора.
        private string texContainerPath;
        private bool texIsBundle;
        private int texSelCabIndex = -1;
        private long texSelPathId;
        private string texSelName;
        private string texOutputPath;
        private bool texShowBundles = true;
        private bool texHideSmall = true;
        private int texBusyDepth;
        private List<TextureReplacePatcher.TextureEntry> texLastList;

        // UI-ссылки модуля.
        private DataGridView texContainerGrid;
        private DataGridView texTextureGrid;
        private PictureBox texPreviewBox;
        private Label texPreviewInfo;
        private CheckBox texShowBundlesCheck;
        private CheckBox texHideSmallCheck;
        private ProgressBar texProgress;
        private RichTextBox texLogBox;
        private Button texBtnExport, texBtnExportAll, texBtnImport, texBtnApply, texBtnOcr;

        private void LoadTextureToolsModule()
        {
            ShowChromeHeader();
            ClearAssetsModuleRefs();
            ClearBundleLocModuleRefs();
            ClearTextureModuleRefs();
            DetachModuleEvents();
            ClearContentPanel();

            if (headerPanel != null && !headerPanel.IsDisposed)
                headerPanel.Height = 6;
            if (headerLabel != null && !headerLabel.IsDisposed)
            {
                headerLabel.Text = "";
                headerLabel.Visible = false;
            }

            try
            {
            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8) };

            // --- Верхняя панель: источник + действия ---
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 0, 0, 8),
                BackColor = Color.Transparent
            };

            var btnFolder = CreateModernButton(L("Choose game folder…", "Папка игры…"), ButtonStyleKind.Primary);
            btnFolder.Margin = new Padding(0, 0, 8, 0);
            btnFolder.Click += TexPickGameFolder_Click;
            toolbar.Controls.Add(btnFolder);

            var btnFile = CreateModernButton(L("Open file…", "Открыть файл…"), ButtonStyleKind.Secondary);
            btnFile.Margin = new Padding(0, 0, 8, 0);
            btnFile.Click += TexPickSingleFile_Click;
            toolbar.Controls.Add(btnFile);

            texShowBundlesCheck = new CheckBox
            {
                Text = L("Show bundles", "Показывать бандлы"),
                Checked = texShowBundles,
                AutoSize = true,
                Margin = new Padding(4, 8, 16, 0),
                ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                BackColor = Color.Transparent
            };
            texShowBundlesCheck.CheckedChanged += (_, __) =>
            {
                texShowBundles = texShowBundlesCheck.Checked;
                RefreshTextureContainers();
            };
            toolbar.Controls.Add(texShowBundlesCheck);

            texHideSmallCheck = new CheckBox
            {
                Text = L("Only large (likely text/bg)", "Только крупные (фоны/текст)"),
                Checked = texHideSmall,
                AutoSize = true,
                Margin = new Padding(4, 8, 16, 0),
                ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                BackColor = Color.Transparent
            };
            texHideSmallCheck.CheckedChanged += (_, __) =>
            {
                texHideSmall = texHideSmallCheck.Checked;
                RenderTextureGrid();
            };
            toolbar.Controls.Add(texHideSmallCheck);

            texBtnExport = CreateModernButton(L("Export PNG", "Экспорт PNG"), ButtonStyleKind.Secondary);
            texBtnExport.Margin = new Padding(0, 0, 8, 0);
            texBtnExport.Click += TexExport_Click;
            toolbar.Controls.Add(texBtnExport);

            texBtnExportAll = CreateModernButton(L("Export ALL…", "Экспорт ВСЕХ…"), ButtonStyleKind.Secondary);
            texBtnExportAll.Margin = new Padding(0, 0, 8, 0);
            texBtnExportAll.Click += TexExportAll_Click;
            toolbar.Controls.Add(texBtnExportAll);

            texBtnOcr = CreateModernButton(L("Find text (OCR)", "Найти текст (OCR)"), ButtonStyleKind.Secondary);
            texBtnOcr.Margin = new Padding(0, 0, 8, 0);
            texBtnOcr.Click += TexOcr_Click;
            toolbar.Controls.Add(texBtnOcr);

            texBtnImport = CreateModernButton(L("Import PNG…", "Импорт PNG…"), ButtonStyleKind.Secondary);
            texBtnImport.Margin = new Padding(0, 0, 8, 0);
            texBtnImport.Click += TexImport_Click;
            toolbar.Controls.Add(texBtnImport);

            texBtnApply = CreateModernButton(L("Apply to game", "Применить в игру"), ButtonStyleKind.Secondary);
            texBtnApply.Margin = new Padding(0, 0, 8, 0);
            texBtnApply.Click += TexApply_Click;
            toolbar.Controls.Add(texBtnApply);

            texProgress = new ProgressBar
            {
                Width = 160,
                Height = 18,
                Margin = new Padding(0, 4, 0, 0),
                Visible = false,
                Style = ProgressBarStyle.Marquee
            };
            toolbar.Controls.Add(texProgress);

            // --- Левая колонка: контейнеры (сверху) + текстуры (снизу) ---
            texContainerGrid = MakeTexGrid();
            texContainerGrid.Columns.Add("File", L("Container", "Контейнер"));
            texContainerGrid.Columns.Add("Kind", L("Type", "Тип"));
            texContainerGrid.Columns.Add("Size", L("Size", "Размер"));
            texContainerGrid.Columns["File"].FillWeight = 200;
            texContainerGrid.Columns["Kind"].FillWeight = 70;
            texContainerGrid.Columns["Size"].FillWeight = 70;
            texContainerGrid.SelectionChanged += TexContainerGrid_SelectionChanged;

            texTextureGrid = MakeTexGrid();
            texTextureGrid.Columns.Add("PathId", "PathID");
            texTextureGrid.Columns.Add("Name", L("Name", "Имя"));
            texTextureGrid.Columns.Add("Size", L("Size", "Размер"));
            texTextureGrid.Columns.Add("Format", L("Format", "Формат"));
            texTextureGrid.Columns.Add("Ocr", L("Text (OCR)", "Текст (OCR)"));
            texTextureGrid.Columns["PathId"].FillWeight = 60;
            texTextureGrid.Columns["Name"].FillWeight = 120;
            texTextureGrid.Columns["Size"].FillWeight = 70;
            texTextureGrid.Columns["Format"].FillWeight = 80;
            texTextureGrid.Columns["Ocr"].FillWeight = 160;
            texTextureGrid.SelectionChanged += TexTextureGrid_SelectionChanged;

            var leftTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent
            };
            leftTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            leftTable.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
            leftTable.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
            leftTable.Controls.Add(WrapWithLabel(texContainerGrid, L("Containers (.assets + bundles)", "Контейнеры (.assets + бандлы)")), 0, 0);
            leftTable.Controls.Add(WrapWithLabel(texTextureGrid, L("Textures in container", "Текстуры контейнера")), 0, 1);

            // --- Правая колонка: превью ---
            texPreviewBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = isDarkTheme ? Color.FromArgb(18, 18, 20) : Color.FromArgb(240, 242, 245)
            };
            texPreviewInfo = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9.25f, FontStyle.Bold),
                ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                Text = L("Pick a game folder, then a container, then a texture — preview shows here.",
                         "Выберите папку игры, контейнер и текстуру — превью появится здесь.")
            };
            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            rightPanel.Controls.Add(texPreviewBox);
            rightPanel.Controls.Add(texPreviewInfo);

            // --- Содержимое: слева списки, справа превью (TableLayout — без SplitContainer, чтобы исключить краш сайзинга) ---
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44f));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56f));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            content.Controls.Add(leftTable, 0, 0);
            content.Controls.Add(rightPanel, 1, 0);

            // --- Журнал снизу ---
            texLogBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f),
                DetectUrls = false,
                BackColor = isDarkTheme ? Color.FromArgb(14, 14, 16) : Color.FromArgb(246, 248, 250),
                ForeColor = isDarkTheme ? Color.FromArgb(196, 181, 253) : Color.FromArgb(26, 127, 55)
            };

            var rootTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent
            };
            rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rootTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
            rootTable.Controls.Add(content, 0, 0);
            rootTable.Controls.Add(texLogBox, 0, 1);

            root.Controls.Add(rootTable);
            root.Controls.Add(toolbar);
            moduleHostPanel.Controls.Add(root);

            ApplyTheme();
            TexLog(L("Textures module. Pick the game folder to list containers.",
                     "Модуль «Текстуры». Выберите папку игры — появится список контейнеров."));
            RefreshTextureContainers();
            UpdateStatus();
            }
            catch (Exception ex)
            {
                try { ClearContentPanel(); } catch { }
                var errLabel = new Label
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    TextAlign = ContentAlignment.TopLeft,
                    Font = new Font("Segoe UI", 9.5f),
                    ForeColor = isDarkTheme ? Color.FromArgb(248, 113, 113) : Color.FromArgb(185, 28, 28),
                    Text = L("Textures module failed to load: ", "Модуль текстур не загрузился: ") + ex.Message
                };
                if (moduleHostPanel != null && !moduleHostPanel.IsDisposed)
                    moduleHostPanel.Controls.Add(errLabel);
                Log("Textures module load failed: " + ex);
            }
        }

        private DataGridView MakeTexGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = isDarkTheme ? Color.FromArgb(24, 24, 26) : Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ColumnHeadersHeight = 28,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable
            };
        }

        private Label MakeSectionLabel(string text)
        {
            return new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = text,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = isDarkTheme ? Color.FromArgb(148, 163, 184) : Color.FromArgb(71, 85, 105),
                BackColor = Color.Transparent
            };
        }

        /// <summary>Контрол + заголовок-секция сверху, в обёртке-панели (для ячейки TableLayout).</summary>
        private Panel WrapWithLabel(Control inner, string label)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);                   // Fill — первым
            p.Controls.Add(MakeSectionLabel(label)); // Top — вторым
            return p;
        }

        // ---------- Список контейнеров ----------

        private void RefreshTextureContainers()
        {
            if (texContainerGrid == null || texContainerGrid.IsDisposed)
                return;

            texContainerGrid.Rows.Clear();

            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
            {
                if (texPreviewInfo != null && !texPreviewInfo.IsDisposed)
                    texPreviewInfo.Text = L("Pick a game *_Data folder first.", "Сначала выберите папку игры *_Data.");
                return;
            }

            foreach (var p in UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved))
                AddContainerRow(p, false);

            if (texShowBundles)
                foreach (var p in EnumerateBundlePaths(resolved))
                    AddContainerRow(p, true);

            TexLog(L("Containers listed: ", "Контейнеров в списке: ") + texContainerGrid.Rows.Count
                + (texShowBundles ? "" : L(" (bundles hidden)", " (бандлы скрыты)")) + ".");
        }

        private void AddContainerRow(string path, bool isBundle)
        {
            long len = 0;
            try { len = new FileInfo(path).Length; } catch { }
            var idx = texContainerGrid.Rows.Add(
                Path.GetFileName(path),
                isBundle ? L("Bundle", "Бандл") : L("Assets", "Ассеты"),
                FormatBytes(len));
            texContainerGrid.Rows[idx].Tag = path;
        }

        private static IEnumerable<string> EnumerateBundlePaths(string root)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in new[] { "*.bundle", "*.unity3d" })
            {
                string[] files;
                try { files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files)
                {
                    if (seen.Add(f))
                        yield return f;
                    if (seen.Count >= 5000)
                        yield break;
                }
            }
        }

        private void TexContainerGrid_SelectionChanged(object sender, EventArgs e)
        {
            if (texContainerGrid == null || texContainerGrid.IsDisposed || texContainerGrid.SelectedRows.Count == 0)
                return;
            var path = texContainerGrid.SelectedRows[0].Tag as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            texContainerPath = path;
            texIsBundle = LooksLikeBundlePath(path);
            texSelPathId = 0;
            texSelName = null;
            texOutputPath = null;
            if (texTextureGrid != null && !texTextureGrid.IsDisposed)
                texTextureGrid.Rows.Clear();
            AnalyzeContainerAsync();
        }

        // ---------- Анализ текстур контейнера ----------

        private async void AnalyzeContainerAsync()
        {
            var path = texContainerPath;
            var isBundle = texIsBundle;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            var classDataPath = await EnsureClassDataAsync().ConfigureAwait(true);
            if (classDataPath == null || path != texContainerPath)
                return;

            SetTextureBusy(true);
            try
            {
                var lines = new List<string>();
                List<TextureReplacePatcher.TextureEntry> list = null;
                await Task.Run(() =>
                    list = isBundle
                        ? TextureReplacePatcher.AnalyzeBundle(classDataPath, path, lines)
                        : TextureReplacePatcher.AnalyzeAssets(classDataPath, path, lines)).ConfigureAwait(true);

                if (path != texContainerPath) // выбор сменился, пока шёл анализ
                    return;

                foreach (var line in lines)
                    TexLog(line);
                FillTextureGrid(list);
            }
            catch (Exception ex)
            {
                TexLog(L("Analyze failed: ", "Анализ не удался: ") + ex.Message, true);
            }
            finally
            {
                SetTextureBusy(false);
            }
        }

        private void FillTextureGrid(List<TextureReplacePatcher.TextureEntry> list)
        {
            texLastList = list;
            RenderTextureGrid();
        }

        /// <summary>Перерисовывает грид текстур из <see cref="texLastList"/> с учётом фильтра «только крупные».</summary>
        private void RenderTextureGrid()
        {
            if (texTextureGrid == null || texTextureGrid.IsDisposed)
                return;
            texTextureGrid.Rows.Clear();
            if (texLastList == null)
                return;

            var shown = 0;
            foreach (var t in texLastList)
            {
                if (texHideSmall && !IsLikelyTextCandidate(t))
                    continue;
                var idx = texTextureGrid.Rows.Add(
                    t.PathId,
                    string.IsNullOrEmpty(t.Name) ? "—" : t.Name,
                    t.Width + "×" + t.Height,
                    t.FormatName + (t.Streamed ? " [stream]" : ""));
                texTextureGrid.Rows[idx].Tag = t;
                shown++;
            }

            TexLog(L("Textures shown: ", "Показано текстур: ") + shown + L(" of ", " из ") + texLastList.Count
                + (texHideSmall ? L(" (small/aux hidden)", " (мелкие/служебные скрыты)") : ""));

            // Авто-превью первой текстуры.
            if (texTextureGrid.Rows.Count > 0)
                texTextureGrid.Rows[0].Selected = true;
        }

        private static bool IsLikelyTextCandidate(TextureReplacePatcher.TextureEntry t)
        {
            return t != null && TextureReplacePatcher.IsLikelyTextCandidate(t.Name, t.Width, t.Height);
        }

        // ---------- Превью ----------

        private void TexTextureGrid_SelectionChanged(object sender, EventArgs e)
        {
            if (texTextureGrid == null || texTextureGrid.IsDisposed || texTextureGrid.SelectedRows.Count == 0)
                return;
            var entry = texTextureGrid.SelectedRows[0].Tag as TextureReplacePatcher.TextureEntry;
            if (entry == null)
                return;
            texSelPathId = entry.PathId;
            texSelCabIndex = entry.BundleCabIndex;
            texSelName = entry.Name;
            PreviewSelectedAsync(entry);
        }

        private async void PreviewSelectedAsync(TextureReplacePatcher.TextureEntry entry)
        {
            var capPath = texContainerPath;
            var capId = entry.PathId;
            var capCab = entry.BundleCabIndex;
            var isBundle = texIsBundle;
            var classDataPath = ClassPackageDownloader.ClassDataPath;

            if (texPreviewInfo != null && !texPreviewInfo.IsDisposed)
                texPreviewInfo.Text = L("Decoding…", "Декодирую…") + " " + entry.DisplayLabel;

            SetTextureBusy(true);
            try
            {
                byte[] png = null;
                var lines = new List<string>();
                await Task.Run(() =>
                    png = isBundle
                        ? TextureReplacePatcher.DecodeBundleTextureToPngBytes(classDataPath, capPath, capCab, capId, lines)
                        : TextureReplacePatcher.DecodeAssetsTextureToPngBytes(classDataPath, capPath, capId, lines)).ConfigureAwait(true);

                if (capPath != texContainerPath || capId != texSelPathId) // выбор сменился
                    return;
                if (texPreviewBox == null || texPreviewBox.IsDisposed)
                    return;

                var bmp = BitmapFromPngBytes(png);
                var old = texPreviewBox.Image;
                texPreviewBox.Image = bmp;
                old?.Dispose();
                if (texPreviewInfo != null && !texPreviewInfo.IsDisposed)
                    texPreviewInfo.Text = entry.DisplayLabel;
            }
            catch (Exception ex)
            {
                if (capPath == texContainerPath && capId == texSelPathId && texPreviewInfo != null && !texPreviewInfo.IsDisposed)
                    texPreviewInfo.Text = L("Preview failed: ", "Превью не удалось: ") + ex.Message;
            }
            finally
            {
                SetTextureBusy(false);
            }
        }

        private static Bitmap BitmapFromPngBytes(byte[] png)
        {
            using (var ms = new MemoryStream(png))
            using (var img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        // ---------- Действия ----------

        private void TexPickGameFolder_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = L("Choose the game folder or *_Data", "Выберите папку игры или *_Data");
                if (!string.IsNullOrWhiteSpace(lastUnityGameDataFolder) && Directory.Exists(lastUnityGameDataFolder))
                    fbd.SelectedPath = lastUnityGameDataFolder;
                if (fbd.ShowDialog(this) != DialogResult.OK)
                    return;

                var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(fbd.SelectedPath);
                if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                {
                    TexLog(L("Could not resolve the game data directory.", "Не удалось определить каталог данных игры."), true);
                    return;
                }
                lastUnityGameDataFolder = resolved;
                SaveSettings();
                RefreshTextureContainers();
            }
        }

        private void TexPickSingleFile_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = L("Open .assets or .bundle", "Открыть .assets или .bundle");
                ofd.Filter = L(
                    "Unity containers|*.assets;*.bundle;*.unity3d|All files|*.*",
                    "Контейнеры Unity|*.assets;*.bundle;*.unity3d|Все файлы|*.*");
                if (ofd.ShowDialog(this) != DialogResult.OK)
                    return;

                // Добавляем как строку вверх списка и выбираем — анализ запустится по SelectionChanged.
                texContainerGrid.Rows.Insert(0, Path.GetFileName(ofd.FileName),
                    LooksLikeBundlePath(ofd.FileName) ? L("Bundle", "Бандл") : L("Assets", "Ассеты"),
                    SafeFileLen(ofd.FileName));
                texContainerGrid.Rows[0].Tag = ofd.FileName;
                texContainerGrid.ClearSelection();
                texContainerGrid.Rows[0].Selected = true;
            }
        }

        private static string SafeFileLen(string path)
        {
            try { return FormatBytes(new FileInfo(path).Length); } catch { return ""; }
        }

        private async void TexExport_Click(object sender, EventArgs e)
        {
            if (!HaveSelectedTexture())
                return;

            string outPng;
            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = L("Save selected texture as PNG", "Сохранить выбранную текстуру в PNG");
                sfd.Filter = L("PNG image|*.png", "PNG-изображение|*.png");
                sfd.FileName = UabeaJsonPaths.SafeFileNamePart(
                    (string.IsNullOrEmpty(texSelName) ? "texture" : texSelName) + "-" + texSelPathId) + ".png";
                sfd.InitialDirectory = Path.GetDirectoryName(texContainerPath);
                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;
                outPng = sfd.FileName;
            }

            var classDataPath = await EnsureClassDataAsync().ConfigureAwait(true);
            if (classDataPath == null)
                return;

            await RunTexOp(async lines =>
            {
                await Task.Run(() =>
                {
                    if (texIsBundle)
                        TextureReplacePatcher.ExportBundleTextureToPng(classDataPath, texContainerPath, texSelCabIndex, texSelPathId, outPng, lines);
                    else
                        TextureReplacePatcher.ExportAssetsTextureToPng(classDataPath, texContainerPath, texSelPathId, outPng, lines);
                }).ConfigureAwait(true);
                if (File.Exists(outPng))
                    TexLog(L("Exported: ", "Экспортировано: ") + outPng);
            }, L("Export failed: ", "Экспорт не удался: ")).ConfigureAwait(true);
        }

        private async void TexExportAll_Click(object sender, EventArgs e)
        {
            // Все контейнеры из папки игры (учитываем чекбокс «Показывать бандлы»); если папка не задана — берём выбранный.
            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder);
            var containers = new List<(string Path, bool IsBundle)>();
            if (!string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved))
            {
                foreach (var p in UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved))
                    containers.Add((p, false));
                if (texShowBundles)
                    foreach (var p in EnumerateBundlePaths(resolved))
                        containers.Add((p, true));
            }
            if (containers.Count == 0 && !string.IsNullOrWhiteSpace(texContainerPath) && File.Exists(texContainerPath))
                containers.Add((texContainerPath, texIsBundle));
            if (containers.Count == 0)
            {
                TexLog(L("Pick a game folder (or a container) first.", "Сначала выберите папку игры (или контейнер)."), true);
                return;
            }

            string outRoot;
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = L("Folder for exported textures (a subfolder per container)",
                                    "Папка для экспорта текстур (по подпапке на каждый контейнер)");
                fbd.SelectedPath = !string.IsNullOrWhiteSpace(resolved) ? resolved : Path.GetDirectoryName(texContainerPath);
                if (fbd.ShowDialog(this) != DialogResult.OK)
                    return;
                outRoot = Path.Combine(fbd.SelectedPath, "UTT_textures_export");
            }

            // Полнота экспорта — явно (фильтр списка не должен молча урезать выгрузку).
            var ans = MessageBox.Show(this,
                L("Export ALL textures, including small/aux ones?\nYes = everything, No = only large (as in the list).\n\nBundles are included only if «Show bundles» is checked.",
                  "Экспортировать ВСЕ текстуры, включая мелкие/служебные?\nДа — все, Нет — только крупные (как в списке).\n\nБандлы попадут в экспорт только при включённой галочке «Показывать бандлы»."),
                L("Export ALL", "Экспорт ВСЕХ"),
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel)
                return;
            var largeOnly = ans == DialogResult.No;

            var classDataPath = await EnsureClassDataAsync().ConfigureAwait(true);
            if (classDataPath == null)
                return;

            SetTextureBusy(true);
            TexLog(L("Exporting from all containers: ", "Экспорт из всех контейнеров: ") + containers.Count
                + (largeOnly ? L(" (only large)", " (только крупные)") : "") + " → " + outRoot);

            var total = 0;
            var withTex = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var c in containers)
                {
                    var cap = c;
                    var sub = UniqueSubfolder(outRoot, Path.GetFileNameWithoutExtension(cap.Path), used);
                    var clines = new List<string>();
                    var n = 0;
                    try
                    {
                        await Task.Run(() =>
                            n = cap.IsBundle
                                ? TextureReplacePatcher.ExportAllBundleTexturesToFolder(classDataPath, cap.Path, sub, clines, largeOnly)
                                : TextureReplacePatcher.ExportAllAssetsTexturesToFolder(classDataPath, cap.Path, sub, clines, largeOnly)).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        clines.Add("[Текстуры] " + Path.GetFileName(cap.Path) + ": ошибка (" + ex.Message + ").");
                    }
                    foreach (var l in clines)
                        TexLog(l); // живой прогресс: по строке на контейнер
                    total += n;
                    if (n > 0) withTex++;
                }

                TexLog(L("Done. Containers with textures: ", "Готово. Контейнеров с текстурами: ") + withTex + "/" + containers.Count
                    + L(", PNG total: ", ", всего PNG: ") + total + ". " + outRoot);
                if (total > 0 && Directory.Exists(outRoot))
                    try { Process.Start("explorer.exe", "\"" + outRoot + "\""); } catch { }
            }
            catch (Exception ex)
            {
                TexLog(L("Export all failed: ", "Экспорт всех не удался: ") + ex.Message, true);
            }
            finally
            {
                SetTextureBusy(false);
            }
        }

        /// <summary>Уникальная подпапка под контейнер в корне экспорта (на коллизии имён добавляет _2, _3…).</summary>
        private static string UniqueSubfolder(string root, string baseName, HashSet<string> used)
        {
            var safe = UabeaJsonPaths.SafeFileNamePart(string.IsNullOrEmpty(baseName) ? "container" : baseName);
            var name = safe;
            var i = 2;
            while (used.Contains(name.ToLowerInvariant()))
                name = safe + "_" + i++;
            used.Add(name.ToLowerInvariant());
            return Path.Combine(root, name);
        }

        // ---- Найти текст на текстурах (встроенный Windows OCR) ----
        private async void TexOcr_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(texContainerPath) || !File.Exists(texContainerPath))
            {
                TexLog(L("Pick a container first.", "Сначала выберите контейнер."), true);
                return;
            }
            if (texTextureGrid == null || texTextureGrid.Rows.Count == 0)
            {
                TexLog(L("No textures in the list.", "В списке нет текстур."), true);
                return;
            }

            var classDataPath = await EnsureClassDataAsync().ConfigureAwait(true);
            if (classDataPath == null)
                return;

            var containerPath = texContainerPath;
            var isBundle = texIsBundle;
            var largeOnly = texHideSmall; // OCR'им ровно то, что показано в списке
            // Рабочие PNG — в рабочую папку (currentFolder), иначе рядом с контейнером; НЕ удаляем — можно открыть и посмотреть.
            var workBase = (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                ? currentFolder
                : (Path.GetDirectoryName(containerPath) ?? Path.GetTempPath());
            var workDir = Path.Combine(workBase, Path.GetFileNameWithoutExtension(containerPath) + "_ocr");

            SetTextureBusy(true);
            TexLog(L("OCR: decoding textures and recognizing text (Windows OCR)…",
                     "OCR: декодирую текстуры и распознаю текст (Windows OCR)…") + " → " + workDir);
            try
            {
                var lines = new List<string>();
                Dictionary<string, string> ocr = null;
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(workDir);
                    if (isBundle)
                        TextureReplacePatcher.ExportAllBundleTexturesToFolder(classDataPath, containerPath, workDir, lines, largeOnly);
                    else
                        TextureReplacePatcher.ExportAllAssetsTexturesToFolder(classDataPath, containerPath, workDir, lines, largeOnly);
                    ocr = OcrInterop.RunOcrOnFolder(workDir, "en", lines);
                }).ConfigureAwait(true);
                foreach (var l in lines) TexLog(l);

                // Карта PathID → распознанный текст (имена PNG вида «имя-PathID.png»).
                var byPath = new Dictionary<long, string>();
                if (ocr != null)
                    foreach (var kv in ocr)
                        if (UabeaJsonPaths.TryParsePathIdFromFilePath(kv.Key, out var pid) && !string.IsNullOrWhiteSpace(kv.Value))
                            byPath[pid] = kv.Value;

                var found = 0;
                if (texTextureGrid != null && !texTextureGrid.IsDisposed)
                {
                    foreach (DataGridViewRow row in texTextureGrid.Rows)
                    {
                        var en = row.Tag as TextureReplacePatcher.TextureEntry;
                        if (en == null) continue;
                        if (byPath.TryGetValue(en.PathId, out var txt))
                        {
                            row.Cells["Ocr"].Value = txt;
                            row.DefaultCellStyle.BackColor = isDarkTheme ? Color.FromArgb(30, 52, 34) : Color.FromArgb(223, 246, 226);
                            found++;
                        }
                    }
                }

                TexLog(L("OCR done. Textures with recognized text: ", "OCR готово. Текстур с распознанным текстом: ")
                    + found + " / " + texTextureGrid.Rows.Count
                    + L(" (highlighted). PNGs kept in: ", " (подсвечены). PNG сохранены в: ") + workDir);
                if (Directory.Exists(workDir))
                    try { Process.Start("explorer.exe", "\"" + workDir + "\""); } catch { }
            }
            catch (Exception ex)
            {
                TexLog(L("OCR failed: ", "OCR не удался: ") + ex.Message, true);
            }
            finally
            {
                SetTextureBusy(false);
            }
        }

        private async void TexImport_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(texContainerPath) || !File.Exists(texContainerPath))
            {
                TexLog(L("Pick a container first.", "Сначала выберите контейнер."), true);
                return;
            }
            if (texIsBundle)
            {
                TexLog(L("Texture write-back into .bundle is not implemented yet — this works for .assets.",
                         "Запись текстуры обратно в .bundle пока не реализована — работает для .assets."), true);
                return;
            }

            string pngPath;
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = L("Pick the edited PNG (filename must keep -PathID)", "Выберите отредактированный PNG (в имени должен остаться -PathID)");
                ofd.Filter = L("PNG image|*.png|All files|*.*", "PNG-изображение|*.png|Все файлы|*.*");
                ofd.InitialDirectory = Path.GetDirectoryName(texContainerPath);
                if (ofd.ShowDialog(this) != DialogResult.OK)
                    return;
                pngPath = ofd.FileName;
            }

            long targetPathId;
            if (UabeaJsonPaths.TryParsePathIdFromFilePath(pngPath, out var parsedId) && parsedId != 0)
                targetPathId = parsedId;
            else
                targetPathId = texSelPathId;

            if (targetPathId == 0)
            {
                TexLog(L("Pick a texture in the list, or import a PNG whose name ends with -PathID.",
                         "Выберите текстуру в списке или импортируйте PNG, имя которого оканчивается на -PathID."), true);
                return;
            }

            var classDataPath = await EnsureClassDataAsync().ConfigureAwait(true);
            if (classDataPath == null)
                return;

            texOutputPath = Path.Combine(
                Path.GetDirectoryName(texContainerPath) ?? "",
                Path.GetFileNameWithoutExtension(texContainerPath) + ".tex.assets");

            await RunTexOp(async lines =>
            {
                await Task.Run(() =>
                    TextureReplacePatcher.ReplaceAssetsTextureFromPng(
                        classDataPath, texContainerPath, targetPathId, pngPath, texOutputPath, lines)).ConfigureAwait(true);
                if (File.Exists(texOutputPath))
                    TexLog(L("Patched file ready — press «Apply to game».", "Пропатченный файл готов — нажмите «Применить в игру».") + " " + texOutputPath);
            }, L("Import failed: ", "Импорт не удался: ")).ConfigureAwait(true);
        }

        private void TexApply_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(texOutputPath) || !File.Exists(texOutputPath))
            {
                TexLog(L("Import a PNG first.", "Сначала импортируйте PNG."), true);
                return;
            }
            if (string.IsNullOrWhiteSpace(texContainerPath) || !File.Exists(texContainerPath))
            {
                TexLog(L("Original container not found.", "Оригинальный контейнер не найден."), true);
                return;
            }

            var confirm = MessageBox.Show(this,
                L("Replace original:\n" + texContainerPath + "\nwith patched file? A .bak backup will be created.",
                  "Заменить оригинал:\n" + texContainerPath + "\nпропатченным файлом? Будет создана резервная копия .bak."),
                L("Apply to game", "Применить в игру"),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK)
                return;

            try
            {
                var bak = texContainerPath + ".bak";
                if (!File.Exists(bak))
                    File.Copy(texContainerPath, bak, false);
                File.Copy(texOutputPath, texContainerPath, true);
                TexLog(L("Applied. Backup: ", "Применено. Резервная копия: ") + bak);
            }
            catch (Exception ex)
            {
                TexLog(L("Apply failed: ", "Применение не удалось: ") + ex.Message, true);
            }
        }

        // ---------- инфраструктура ----------

        private bool HaveSelectedTexture()
        {
            if (string.IsNullOrWhiteSpace(texContainerPath) || texSelPathId == 0)
            {
                TexLog(L("Pick a texture in the list first.", "Сначала выберите текстуру в списке."), true);
                return false;
            }
            return true;
        }

        /// <summary>Общая обёртка операции: занятость + сбор строк лога + единый catch.</summary>
        private async Task RunTexOp(Func<List<string>, Task> body, string errPrefix)
        {
            SetTextureBusy(true);
            var lines = new List<string>();
            try
            {
                await body(lines).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                TexLog(errPrefix + ex.Message, true);
            }
            finally
            {
                foreach (var line in lines)
                    TexLog(line);
                SetTextureBusy(false);
            }
        }

        private void SetTextureBusy(bool busy)
        {
            texBusyDepth += busy ? 1 : -1;
            if (texBusyDepth < 0) texBusyDepth = 0;
            var show = texBusyDepth > 0;
            if (texProgress != null && !texProgress.IsDisposed)
                texProgress.Visible = show;
            Cursor = show ? Cursors.AppStarting : Cursors.Default;
        }

        private void TexLog(string message, bool isError = false)
        {
            if (texLogBox == null || texLogBox.IsDisposed)
                return;
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            texLogBox.SelectionStart = texLogBox.TextLength;
            texLogBox.SelectionColor = isError
                ? Color.FromArgb(248, 113, 113)
                : (isDarkTheme ? Color.FromArgb(196, 181, 253) : Color.FromArgb(26, 127, 55));
            texLogBox.AppendText("[" + stamp + "] " + message + Environment.NewLine);
            texLogBox.SelectionStart = texLogBox.TextLength;
            texLogBox.ScrollToCaret();
        }

        private void ClearTextureModuleRefs()
        {
            if (texPreviewBox != null && !texPreviewBox.IsDisposed && texPreviewBox.Image != null)
            {
                var img = texPreviewBox.Image;
                texPreviewBox.Image = null;
                try { img.Dispose(); } catch { }
            }
            texContainerGrid = null;
            texTextureGrid = null;
            texPreviewBox = null;
            texPreviewInfo = null;
            texShowBundlesCheck = null;
            texHideSmallCheck = null;
            texProgress = null;
            texLogBox = null;
            texBtnExport = texBtnExportAll = texBtnImport = texBtnApply = texBtnOcr = null;
            texLastList = null;
            texBusyDepth = 0;
        }

        private static bool LooksLikeBundlePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var ext = Path.GetExtension(path);
            return string.Equals(ext, ".bundle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".unity3d", StringComparison.OrdinalIgnoreCase);
        }
    }
}
