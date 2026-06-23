using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    /// <summary>IL2CPP: генерация DummyDll (Il2CppDumper) как «Managed» — чтобы у игр с вырезанным type tree читались поля MonoBehaviour (диалоги/UI).</summary>
    public partial class Form1
    {
        /// <summary>Если для текущей игры уже сгенерирована DummyDll — подключить её к разбору MonoBehaviour.</summary>
        private void TryAutoAttachDummyDll()
        {
            try
            {
                var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder);
                if (string.IsNullOrWhiteSpace(resolved))
                    return;
                var dummy = Il2CppDumperInterop.DummyDllFor(resolved);
                if (Directory.Exists(dummy) && Directory.GetFiles(dummy, "*.dll").Length > 0)
                    UnityAssetsGameFolderHelper.ManagedFolderOverride = dummy;
            }
            catch { }
        }

        private async void BtnIl2CppDummy_Click(object sender, EventArgs e)
        {
            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
            {
                Log(L("Pick the game *_Data folder first (Browse Unity Data).",
                      "Сначала выберите папку игры *_Data («Выбрать Unity Data»)."), true);
                return;
            }

            // GameAssembly.dll и global-metadata.dat — авто, иначе спрашиваем.
            var gameAssembly = Il2CppDumperInterop.FindGameAssembly(resolved);
            if (gameAssembly == null)
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = L("Pick GameAssembly.dll", "Выберите GameAssembly.dll");
                    ofd.Filter = "GameAssembly.dll|GameAssembly.dll|*.dll|*.dll";
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    gameAssembly = ofd.FileName;
                }
            }

            var metadata = Il2CppDumperInterop.FindMetadata(resolved);
            if (metadata == null)
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = L("Pick global-metadata.dat", "Выберите global-metadata.dat");
                    ofd.Filter = "global-metadata.dat|global-metadata.dat|*.dat|*.dat";
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    metadata = ofd.FileName;
                }
            }

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);
            SetAssetsModuleBusy(true);
            if (pb != null) { pb.Visible = true; pb.Style = ProgressBarStyle.Marquee; }
            try
            {
                Log(L("Preparing Il2CppDumper…", "Подготовка Il2CppDumper…"));
                var ok = await Il2CppDumperInterop.EnsureDumperAsync(m => Log(m)).ConfigureAwait(true);
                if (!ok)
                {
                    Log(L("Il2CppDumper unavailable (download failed?).", "Il2CppDumper недоступен (сбой загрузки?)."), true);
                    return;
                }

                var outDir = Il2CppDumperInterop.DumpDirFor(resolved);
                var lines = new List<string>();
                string dummy = null;
                var gaC = gameAssembly;
                var mdC = metadata;
                await Task.Run(() => dummy = Il2CppDumperInterop.GenerateDummyDll(gaC, mdC, outDir, lines)).ConfigureAwait(true);
                foreach (var l in lines) Log(l);

                UnityAssetsGameFolderHelper.ManagedFolderOverride = dummy;
                Log(L("Dummy DLLs ready — now press «Export from .assets to JSON»; MonoBehaviour text will be extracted.",
                      "Dummy-DLL готовы — теперь жми «Экспорт из .assets в JSON»: текст MonoBehaviour будет извлечён.") + " " + dummy);
            }
            catch (Exception ex)
            {
                Log(L("Dummy DLL generation failed: ", "Генерация dummy-DLL не удалась: ") + ex.Message, true);
            }
            finally
            {
                if (pb != null) { pb.Style = ProgressBarStyle.Continuous; pb.Visible = false; }
                SetAssetsModuleBusy(false);
                UpdateStatus();
            }
        }
    }
}
