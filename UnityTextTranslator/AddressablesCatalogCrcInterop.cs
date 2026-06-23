using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace UnityTextTranslator
{
    /// <summary>
    /// Addressables хранит ожидаемый CRC для каждого .bundle; после перепаковки байты меняются — игра может молча грузить «старый» контент или не грузить bundle.
    /// Обнуляем CRC записей провайдера AssetBundle как в официальном примере AddressablesTools («patchcrc»). См. https://github.com/nesrak1/AddressablesTools
    /// </summary>
    internal static class AddressablesCatalogCrcInterop
    {
        internal const string CompanionExeName = "AddressablesCatalogCrcZero.exe";

        /// <summary>Имя вложенного ресурса (LogicalName в csproj) — одна сборка exe для пользователя.</summary>
        private const string EmbeddedCompanionLogicalName = "AddressablesCatalogCrcZero.exe";

        /// <summary>Обнуляет CRC в catalog.json|.bin рядом с указанным bundle (Addressables).</summary>
        /// <param name="patchedBundleFullPath">Реальный путь к .bundle в каталоге игры (НЕ временный в %TEMP% — каталог не найдётся).</param>
        internal static void TryPatchCatalogsNearBundle(string patchedBundleFullPath, ICollection<string> messages)
        {
            var exe = ResolveCompanionExePath();
            if (string.IsNullOrEmpty(exe))
            {
                messages?.Add(
                    "[Addressables] Внутри UnityTextTranslator.exe нет встроенной утилиты каталога (пересоберите проект с .NET SDK 8+). Вручную: AddressablesTools, patchcrc для catalog.");
                return;
            }

            patchedBundleFullPath = Path.GetFullPath(patchedBundleFullPath ?? "");
            if (!File.Exists(patchedBundleFullPath))
            {
                messages?.Add("[Addressables] Пропуск патча каталога: выходной bundle не найден.");
                return;
            }

            try
            {
                messages?.Add("[Addressables] якорь «" + patchedBundleFullPath + "».");
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--patch-near-bundle \"" + patchedBundleFullPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        messages?.Add("[Addressables] Не удалось запустить процесс патча каталога.");
                        return;
                    }

                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    foreach (var line in SplitLines(stdout))
                        messages?.Add("[CatalogCRC] " + line);
                    foreach (var line in SplitLines(stderr))
                        messages?.Add("[CatalogCRC stderr] " + line);

                    if (proc.ExitCode != 0)
                    {
                        messages?.Add(
                            "[Addressables] Утилита каталога завершилась с кодом " + proc.ExitCode +
                            ": при необходимости откройте catalog вручную AddressablesTools. Проверьте *.hash рядом с каталогом и кеш в %LocalLow%.");
                        return;
                    }

                    messages?.Add("[Addressables] CRC bundle→0 в каталоге. Резерв: *.utt-prev-catalogbak");
                }
            }
            catch (Exception ex)
            {
                messages?.Add("[Addressables] «" + CompanionExeName + "»: " + ex.Message);
            }
        }

        private static IEnumerable<string> SplitLines(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                yield break;

            using (var r = new StringReader(s.Trim()))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        yield return line.TrimEnd();
                }
            }
        }

        private static string ResolveCompanionExePath()
        {
            try
            {
                var cand = Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), CompanionExeName);
                if (File.Exists(cand))
                    return cand;
            }
            catch { }

            try
            {
                var asm = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrWhiteSpace(asm))
                {
                    var dir = Path.GetDirectoryName(asm);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        var cand = Path.Combine(dir, CompanionExeName);
                        if (File.Exists(cand))
                            return cand;
                    }
                }
            }
            catch { }

            try
            {
                return MaterializeEmbeddedCompanionExe();
            }
            catch { }

            return null;
        }

        /// <summary>Снимает копию из ресурса в LocalAppData (обновляет при смене размера встроенного файла).</summary>
        private static string MaterializeEmbeddedCompanionExe()
        {
            var assembly = Assembly.GetExecutingAssembly();
            byte[] payload;
            using (var stream = assembly.GetManifestResourceStream(EmbeddedCompanionLogicalName))
            {
                if (stream == null)
                {
                    foreach (var name in assembly.GetManifestResourceNames())
                    {
                        if (name.EndsWith(CompanionExeName, StringComparison.OrdinalIgnoreCase))
                        {
                            using (var alt = assembly.GetManifestResourceStream(name))
                            {
                                if (alt == null) return null;
                                payload = ReadAllBytes(alt);
                                goto write;
                            }
                        }
                    }

                    return null;
                }

                payload = ReadAllBytes(stream);
            }

        write:
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityTextTranslator");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, CompanionExeName);

            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    if (fi.Length == payload.LongLength)
                        return path;
                }
            }
            catch { }

            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, payload);
            var bak = path + ".prev";
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, bak);
                else
                    File.Move(tmp, path);
            }
            catch
            {
                File.Copy(tmp, path, overwrite: true);
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch { }
            }

            return path;
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }
    }
}
