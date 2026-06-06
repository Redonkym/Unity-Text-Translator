using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace UnityTextTranslator
{
    /// <summary>Скачивание classdata.tpk из открытого репозитория UABEA в каталог данных приложения в AppData.</summary>
    internal static class ClassPackageDownloader
    {
        private const string ClassDataTpkUrl =
            "https://github.com/nesrak1/UABEA/raw/refs/heads/master/ReleaseFiles/classdata.tpk";

        /// <summary>Папка приложения в Roaming (settings.json, memory.json и т.д.).</summary>
        public static string AppDataAppFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityTextTranslator");

        public static string ClassDataPath =>
            Path.Combine(AppDataAppFolder, "classdata.tpk");

        /// <summary>
        /// Гарантирует наличие classdata.tpk в %AppData%\UnityTextTranslator\ (скачивает при необходимости).
        /// </summary>
        public static async Task EnsureClassDataPresentAsync(Action<string> log)
        {
            try
            {
                var dest = ClassDataPath;
                if (File.Exists(dest) && new FileInfo(dest).Length > 8192)
                    return;

                var legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "classdata.tpk");
                if (File.Exists(legacy) && new FileInfo(legacy).Length > 8192)
                {
                    try
                    {
                        var appDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(appDir))
                            Directory.CreateDirectory(appDir);
                        File.Copy(legacy, dest, overwrite: true);
                        log?.Invoke("Скопирован classdata.tpk из папки с программой в AppData.");
                        return;
                    }
                    catch
                    {
                        // затем попробуем скачать
                    }
                }

                log?.Invoke("Файл classdata.tpk не найден в AppData (UnityTextTranslator). Скачиваю эталон с GitHub (репозиторий UABEA)…");

                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(4);
                    var bytes = await http.GetByteArrayAsync(ClassDataTpkUrl).ConfigureAwait(false);
                    if (bytes == null || bytes.Length < 8192)
                        throw new InvalidOperationException("Получен пустой или слишком короткий ответ.");

                    File.WriteAllBytes(dest, bytes);
                }

                log?.Invoke($"classdata.tpk сохранён ({new FileInfo(dest).Length / 1024} KiB).");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Не удалось скачать classdata.tpk: {ex.Message}. Положи файл вручную в папку «{AppDataAppFolder}» (из релиза UABEA).");
            }
        }
    }
}
