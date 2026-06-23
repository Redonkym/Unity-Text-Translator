using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UnityTextTranslator
{
    /// <summary>
    /// Обёртка над <see href="https://github.com/Perfare/Il2CppDumper">Il2CppDumper</see>: из <c>GameAssembly.dll</c>+<c>global-metadata.dat</c>
    /// генерит <c>DummyDll</c> (заглушки типов) как «Managed» → Mono.Cecil восстанавливает поля MonoBehaviour у IL2CPP-игр с вырезанным type tree.
    /// </summary>
    internal static class Il2CppDumperInterop
    {
        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("UnityTextTranslator");
            return h;
        }

        /// <summary>Куда кладём сам Il2CppDumper (рядом с classdata.tpk, в %AppData%).</summary>
        internal static string ToolsDir => Path.Combine(ClassPackageDownloader.AppDataAppFolder, "il2cppdumper");
        internal static string DumperExe => Path.Combine(ToolsDir, "Il2CppDumper.exe");

        /// <summary>Детерминированная папка вывода dump'а под конкретную игру (стабильна между запусками).</summary>
        internal static string DumpDirFor(string dataFolder)
        {
            string name;
            try { name = new DirectoryInfo(dataFolder.TrimEnd(Path.DirectorySeparatorChar)).Name; }
            catch { name = "game"; }
            var hash = StableHash((dataFolder ?? "").ToLowerInvariant());
            return Path.Combine(ToolsDir, "dump", UabeaJsonPaths.SafeFileNamePart(name) + "_" + hash);
        }

        internal static string DummyDllFor(string dataFolder) => Path.Combine(DumpDirFor(dataFolder), "DummyDll");

        /// <summary>GameAssembly.dll обычно в корне игры — родителе <c>*_Data</c>.</summary>
        internal static string FindGameAssembly(string dataFolder)
        {
            try
            {
                var root = Directory.GetParent(dataFolder.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
                if (!string.IsNullOrEmpty(root))
                {
                    var ga = Path.Combine(root, "GameAssembly.dll");
                    if (File.Exists(ga)) return ga;
                }
            }
            catch { }
            return null;
        }

        internal static string FindMetadata(string dataFolder)
        {
            var m = Path.Combine(dataFolder, "il2cpp_data", "Metadata", "global-metadata.dat");
            return File.Exists(m) ? m : null;
        }

        /// <summary>Скачивает (один раз) Il2CppDumper (.NET Framework build «-win») в <see cref="ToolsDir"/>.</summary>
        internal static async Task<bool> EnsureDumperAsync(Action<string> log)
        {
            if (File.Exists(DumperExe))
                return true;

            Directory.CreateDirectory(ToolsDir);
            log?.Invoke("Скачиваю Il2CppDumper…");

            string url = null, assetName = null;
            try
            {
                var apiJson = await Http.GetStringAsync("https://api.github.com/repos/Perfare/Il2CppDumper/releases/latest").ConfigureAwait(false);
                var rel = JObject.Parse(apiJson);
                foreach (var a in rel["assets"] ?? new JArray())
                {
                    var n = (string)a["name"];
                    if (string.IsNullOrEmpty(n)) continue;
                    // «-win» без net6/net7 — сборка под .NET Framework, запускается без .NET-рантайма.
                    if (n.StartsWith("Il2CppDumper-win-", StringComparison.OrdinalIgnoreCase) &&
                        n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        url = (string)a["browser_download_url"];
                        assetName = n;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Не удалось получить релиз Il2CppDumper: " + ex.Message);
                return false;
            }

            if (string.IsNullOrEmpty(url))
            {
                log?.Invoke("В релизе Il2CppDumper не найден ассет «Il2CppDumper-win-*.zip».");
                return false;
            }

            try
            {
                var zip = Path.Combine(ToolsDir, "_dumper.zip");
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                File.WriteAllBytes(zip, bytes);
                ExtractZipFlat(zip, ToolsDir);
                try { File.Delete(zip); } catch { }
            }
            catch (Exception ex)
            {
                log?.Invoke("Скачивание/распаковка Il2CppDumper не удалась: " + ex.Message);
                return false;
            }

            PatchConfigNoPrompt();
            var ok = File.Exists(DumperExe);
            log?.Invoke(ok ? ("Il2CppDumper готов: " + assetName) : "Il2CppDumper.exe не найден после распаковки.");
            return ok;
        }

        /// <summary>Распаковка zip с перезаписью (некоторые архивы кладут файлы в корень — берём все записи).</summary>
        private static void ExtractZipFlat(string zipPath, string destDir)
        {
            using (var z = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in z.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue; // каталог
                    var outPath = Path.Combine(destDir, e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? destDir);
                    e.ExtractToFile(outPath, overwrite: true);
                }
            }
        }

        private static void PatchConfigNoPrompt()
        {
            try
            {
                var cfg = Path.Combine(ToolsDir, "config.json");
                if (!File.Exists(cfg)) return;
                var t = File.ReadAllText(cfg);
                t = Regex.Replace(t, "\"RequireAnyKey\"\\s*:\\s*true", "\"RequireAnyKey\": false");
                File.WriteAllText(cfg, t);
            }
            catch { }
        }

        /// <summary>Запускает Il2CppDumper и возвращает путь к созданной папке DummyDll.</summary>
        internal static string GenerateDummyDll(string gameAssembly, string metadata, string outDir, ICollection<string> log)
        {
            if (!File.Exists(DumperExe))
                throw new FileNotFoundException("Il2CppDumper.exe не найден.", DumperExe);
            if (string.IsNullOrWhiteSpace(gameAssembly) || !File.Exists(gameAssembly))
                throw new FileNotFoundException("GameAssembly.dll не найден.", gameAssembly);
            if (string.IsNullOrWhiteSpace(metadata) || !File.Exists(metadata))
                throw new FileNotFoundException("global-metadata.dat не найден.", metadata);

            Directory.CreateDirectory(outDir);
            PatchConfigNoPrompt();

            var psi = new ProcessStartInfo
            {
                FileName = DumperExe,
                Arguments = "\"" + gameAssembly + "\" \"" + metadata + "\" \"" + outDir + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ToolsDir
            };

            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEnd();
                var se = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (!string.IsNullOrWhiteSpace(so))
                    foreach (var line in so.Replace("\r", "").Split('\n'))
                        if (line.Trim().Length > 0) log?.Add("[Il2CppDumper] " + line.Trim());
                if (!string.IsNullOrWhiteSpace(se))
                    log?.Add("[Il2CppDumper err] " + se.Trim());

                if (p.ExitCode != 0)
                    throw new InvalidOperationException("Il2CppDumper завершился с кодом " + p.ExitCode + ".");
            }

            var dummy = Path.Combine(outDir, "DummyDll");
            if (!Directory.Exists(dummy) || !Directory.GetFiles(dummy, "*.dll").Any())
                throw new InvalidOperationException("Папка DummyDll не создана (нет .dll). Проверьте версию игры/метаданных.");

            return dummy;
        }

        private static string StableHash(string s)
        {
            // FNV-1a 32-bit — детерминированный между запусками (в отличие от string.GetHashCode на некоторых рантаймах).
            unchecked
            {
                uint h = 2166136261;
                foreach (var ch in s ?? "")
                {
                    h ^= ch;
                    h *= 16777619;
                }
                return h.ToString("x8");
            }
        }
    }
}
