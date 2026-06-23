using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    /// <summary>Пошаговый мастер замены шрифта на кириллицу: 1) Анализ .assets → 2) атлас из TTF → 3) патч (рост кириллицы) → 4) применить. PathID/размер атласа — из анализа.</summary>
    public partial class Form1
    {
        // Состояние мастера (между шагами).
        private string wizAssetsPath;
        private long wizFontPathId;
        private int wizAtlasW;
        private int wizAtlasH;
        private string wizAtlasPng;
        private string wizAtlasJson;
        private string wizOutputPath;

        private Button wizBtnAnalyze;
        private Button wizBtnAtlas;
        private Button wizBtnPatch;
        private Button wizBtnApply;

        /// <summary>Создаёт 4 кнопки мастера в указанном ряду (слева направо).</summary>
        private void BuildFontWizardButtons(FlowLayoutPanel row)
        {
            if (row == null)
                return;

            wizBtnAnalyze = CreateModernButton(L("1. Analyze .assets", "1. Анализ .assets"), ButtonStyleKind.Secondary);
            wizBtnAnalyze.Width = 230;
            wizBtnAnalyze.Margin = new Padding(0, 4, 8, 0);
            wizBtnAnalyze.Click += BtnWizAnalyze_Click;
            row.Controls.Add(wizBtnAnalyze);

            wizBtnAtlas = CreateModernButton(L("2. Create atlas (TTF)", "2. Создать атлас (TTF)"), ButtonStyleKind.Secondary);
            wizBtnAtlas.Width = 230;
            wizBtnAtlas.Margin = new Padding(0, 4, 8, 0);
            wizBtnAtlas.Click += BtnWizAtlas_Click;
            row.Controls.Add(wizBtnAtlas);

            wizBtnPatch = CreateModernButton(L("3. Patch (grow Cyrillic)", "3. Патч (рост кириллицы)"), ButtonStyleKind.Secondary);
            wizBtnPatch.Width = 230;
            wizBtnPatch.Margin = new Padding(0, 4, 8, 0);
            wizBtnPatch.Click += BtnWizPatch_Click;
            row.Controls.Add(wizBtnPatch);

            wizBtnApply = CreateModernButton(L("4. Apply to game", "4. Применить в игру"), ButtonStyleKind.Secondary);
            wizBtnApply.Width = 230;
            wizBtnApply.Margin = new Padding(0, 4, 0, 0);
            wizBtnApply.Click += BtnWizApply_Click;
            row.Controls.Add(wizBtnApply);
        }

        // ---- Шаг 1: Анализ ----
        private async void BtnWizAnalyze_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = L("Pick game .assets (e.g. resources.assets)", "Выберите .assets игры (напр. resources.assets)");
                ofd.Filter = L("Unity assets (*.assets)|*.assets|All files|*.*", "Unity assets (*.assets)|*.assets|Все файлы|*.*");
                if (ofd.ShowDialog(this) != DialogResult.OK)
                    return;
                wizAssetsPath = ofd.FileName;
            }

            var classDataPath = await EnsureClassDataAsync().ConfigureAwait(true);
            if (classDataPath == null)
                return;

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);
            SetAssetsModuleBusy(true);
            if (pb != null) { pb.Visible = true; pb.Style = ProgressBarStyle.Marquee; }
            try
            {
                var lines = new List<string>();
                List<TmpFontAssetMsdfAtlasPatcher.TmpFontInfo> fonts = null;
                await Task.Run(() =>
                    fonts = TmpFontAssetMsdfAtlasPatcher.AnalyzeTmpFonts(classDataPath, wizAssetsPath, lines)).ConfigureAwait(true);
                foreach (var line in lines)
                    Log(line);

                if (fonts == null || fonts.Count == 0)
                {
                    Log(L("No TMP_FontAsset found.", "TMP_FontAsset не найдены."), true);
                    return;
                }

                var chosen = fonts.Count == 1 ? fonts[0] : PickFont(fonts);
                if (chosen == null)
                    return;

                wizFontPathId = chosen.FontPathId;
                wizAtlasW = chosen.AtlasWidth > 0 ? chosen.AtlasWidth : 1024;
                wizAtlasH = chosen.AtlasHeight > 0 ? chosen.AtlasHeight : 1024;
                Log(L("Selected font PathID=", "Выбран шрифт PathID=") + wizFontPathId
                    + L(", atlas ", ", атлас ") + wizAtlasW + "×" + wizAtlasH
                    + L(". Next: step 2 (atlas).", ". Дальше: шаг 2 (атлас)."));
            }
            catch (Exception ex)
            {
                Log(L("Analyze failed: ", "Анализ не удался: ") + ex.Message, true);
            }
            finally
            {
                if (pb != null) { pb.Style = ProgressBarStyle.Continuous; pb.Visible = false; }
                SetAssetsModuleBusy(false);
                UpdateStatus();
            }
        }

        private TmpFontAssetMsdfAtlasPatcher.TmpFontInfo PickFont(List<TmpFontAssetMsdfAtlasPatcher.TmpFontInfo> fonts)
        {
            using (var dlg = new Form())
            {
                dlg.Text = L("Pick TMP_FontAsset", "Выберите TMP_FontAsset");
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(520, 120);

                var combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(12, 16),
                    Size = new Size(496, 23)
                };
                foreach (var f in fonts)
                    combo.Items.Add("PathID " + f.FontPathId + " — глифов " + f.GlyphCount
                        + ", атлас " + f.AtlasTexturePathId + " (" + f.AtlasWidth + "×" + f.AtlasHeight + ")");
                combo.SelectedIndex = 0;

                var ok = new Button { Text = L("OK", "OK"), DialogResult = DialogResult.OK, Location = new Point(352, 70), Size = new Size(75, 28) };
                var cancel = new Button { Text = L("Cancel", "Отмена"), DialogResult = DialogResult.Cancel, Location = new Point(433, 70), Size = new Size(75, 28) };
                dlg.Controls.Add(combo);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return null;
                return fonts[combo.SelectedIndex];
            }
        }

        // ---- Шаг 2: Атлас ----
        private async void BtnWizAtlas_Click(object sender, EventArgs e)
        {
            if (wizFontPathId == 0)
            {
                Log(L("Run step 1 (Analyze) first.", "Сначала шаг 1 (Анализ)."), true);
                return;
            }

            string ttfPath;
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = L("TTF/OTF font with Cyrillic (e.g. C:\\Windows\\Fonts\\arial.ttf)",
                    "TTF/OTF шрифт с кириллицей (напр. C:\\Windows\\Fonts\\arial.ttf)");
                ofd.Filter = L("Fonts (*.ttf;*.otf)|*.ttf;*.otf|All files|*.*", "Шрифты (*.ttf;*.otf)|*.ttf;*.otf|Все файлы|*.*");
                if (ofd.ShowDialog(this) != DialogResult.OK)
                    return;
                ttfPath = ofd.FileName;
            }

            var outDir = Path.Combine(Path.GetDirectoryName(wizAssetsPath) ?? ".", "cyr_atlas");
            var dim = wizAtlasW > 0 ? wizAtlasW : 1024;
            // -size подбираем под размер атласа (≈ dim/21), чтобы влезли ASCII+кириллица с зазором.
            var glyphSize = Math.Max(24, dim / 21);

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);
            SetAssetsModuleBusy(true);
            if (pb != null) { pb.Visible = true; pb.Style = ProgressBarStyle.Marquee; }
            try
            {
                var lines = new List<string>();
                var exit = await Task.Run(() =>
                {
                    Directory.CreateDirectory(outDir);
                    var charsetPath = Path.Combine(outDir, MsdfAtlasGenInterop.DefaultCharsetFileName);
                    MsdfAtlasGenInterop.WriteCharsetFileFromRanges(charsetPath, "32-126, 1024-1279");
                    return MsdfAtlasGenInterop.Run(
                        ttfPath, outDir,
                        MsdfAtlasGenInterop.DefaultAtlasSdfPngFileName,
                        MsdfAtlasGenInterop.DefaultAtlasSdfJsonFileName,
                        glyphSize, charsetPath, null, lines,
                        atlasDimensionPx: dim, pxRange: 6);
                }).ConfigureAwait(true);
                foreach (var line in lines)
                    Log(line);

                var png = Path.Combine(outDir, MsdfAtlasGenInterop.DefaultAtlasSdfPngFileName);
                var json = Path.Combine(outDir, MsdfAtlasGenInterop.DefaultAtlasSdfJsonFileName);
                if (exit == 0 && File.Exists(png) && File.Exists(json))
                {
                    wizAtlasPng = png;
                    wizAtlasJson = json;
                    Log(L("Atlas created. Next: step 3 (patch).", "Атлас создан. Дальше: шаг 3 (патч).") + " " + png);
                }
                else
                {
                    Log(L("Atlas generation failed, exit=", "Генерация атласа не удалась, exit=") + exit, true);
                }
            }
            catch (Exception ex)
            {
                Log(L("Atlas error: ", "Ошибка атласа: ") + ex.Message, true);
            }
            finally
            {
                if (pb != null) { pb.Style = ProgressBarStyle.Continuous; pb.Visible = false; }
                SetAssetsModuleBusy(false);
                UpdateStatus();
            }
        }

        // ---- Шаг 3: Патч ----
        private async void BtnWizPatch_Click(object sender, EventArgs e)
        {
            if (wizFontPathId == 0 || string.IsNullOrWhiteSpace(wizAssetsPath))
            {
                Log(L("Run step 1 (Analyze) first.", "Сначала шаг 1 (Анализ)."), true);
                return;
            }
            if (string.IsNullOrWhiteSpace(wizAtlasPng) || string.IsNullOrWhiteSpace(wizAtlasJson)
                || !File.Exists(wizAtlasPng) || !File.Exists(wizAtlasJson))
            {
                Log(L("Run step 2 (atlas) first.", "Сначала шаг 2 (атлас)."), true);
                return;
            }

            var classDataPath = await EnsureClassDataAsync().ConfigureAwait(true);
            if (classDataPath == null)
                return;

            wizOutputPath = Path.Combine(
                Path.GetDirectoryName(wizAssetsPath) ?? "",
                Path.GetFileNameWithoutExtension(wizAssetsPath) + ".cyr.assets");

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);
            SetAssetsModuleBusy(true);
            if (pb != null) { pb.Visible = true; pb.Style = ProgressBarStyle.Marquee; }
            try
            {
                var lines = new List<string>();
                await Task.Run(() =>
                    TmpFontAssetMsdfAtlasPatcher.ReplaceTexture2DAtlasFromPngSameFile(
                        classDataPath,
                        wizAssetsPath,
                        0,                       // texturePathId не нужен в режиме роста (атлас авто из шрифта)
                        wizAtlasPng,
                        wizOutputPath,
                        lines,
                        il2CppTmpFontPathId: wizFontPathId,
                        atlasJsonForCharacterTable: wizAtlasJson,
                        skipCharTable: true,
                        skipTexturePatch: false,
                        skipGlyphPatch: false,
                        metadataAtlasSizeOnly: false,
                        growCyrillicTables: true,
                        markerCyrillicGlyphs: false)).ConfigureAwait(true);
                foreach (var line in lines)
                    Log(line);

                if (File.Exists(wizOutputPath))
                    Log(L("Patched file ready. Next: step 4 (apply).", "Пропатченный файл готов. Дальше: шаг 4 (применить).") + " " + wizOutputPath);
                else
                    Log(L("Patch produced no output.", "Патч не создал выходной файл."), true);
            }
            catch (Exception ex)
            {
                Log(L("Patch failed: ", "Патч не удался: ") + ex.Message, true);
            }
            finally
            {
                if (pb != null) { pb.Style = ProgressBarStyle.Continuous; pb.Visible = false; }
                SetAssetsModuleBusy(false);
                UpdateStatus();
            }
        }

        // ---- Шаг 4: Применить ----
        private void BtnWizApply_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(wizOutputPath) || !File.Exists(wizOutputPath))
            {
                Log(L("Run step 3 (patch) first.", "Сначала шаг 3 (патч)."), true);
                return;
            }
            if (string.IsNullOrWhiteSpace(wizAssetsPath) || !File.Exists(wizAssetsPath))
            {
                Log(L("Original .assets not found.", "Оригинальный .assets не найден."), true);
                return;
            }

            var confirm = MessageBox.Show(this,
                L("Replace original:\n" + wizAssetsPath + "\nwith patched file? A .bak backup will be created.",
                  "Заменить оригинал:\n" + wizAssetsPath + "\nпропатченным файлом? Будет создана резервная копия .bak."),
                L("Apply to game", "Применить в игру"),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK)
                return;

            try
            {
                var bak = wizAssetsPath + ".bak";
                if (!File.Exists(bak))
                    File.Copy(wizAssetsPath, bak, false);
                File.Copy(wizOutputPath, wizAssetsPath, true);
                Log(L("Applied. Backup: ", "Применено. Резервная копия: ") + bak);
            }
            catch (Exception ex)
            {
                Log(L("Apply failed: ", "Применение не удалось: ") + ex.Message, true);
            }
        }

        private async Task<string> EnsureClassDataAsync()
        {
            await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => Log(msg)).ConfigureAwait(true);
            var classDataPath = ClassPackageDownloader.ClassDataPath;
            if (!File.Exists(classDataPath))
            {
                Log(L("classdata.tpk not found.", "classdata.tpk не найден."), true);
                return null;
            }
            return classDataPath;
        }
    }
}
