using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UnityTextTranslator
{
    /// <summary>
    /// Распознавание текста на картинках через <b>встроенный OCR Windows 10</b> (Windows.Media.Ocr) —
    /// без сторонних движков/языковых данных и без тяжёлых бинарников. Запускаем системный PowerShell с
    /// WinRT-скриптом (через stdin, чтобы не упираться в execution policy). Скрипт-помощник кладём в
    /// <c>%AppData%\UnityTextTranslator\ocr\ocr.ps1</c> для прозрачности; запуск — по содержимому.
    /// </summary>
    internal static class OcrInterop
    {
        internal static string OcrDir => Path.Combine(ClassPackageDownloader.AppDataAppFolder, "ocr");
        internal static string ScriptPath => Path.Combine(OcrDir, "ocr.ps1");

        // Скрипт читает пути из переменных окружения (без кавычек в аргументах). Пишет TSV: «имя.png<TAB>текст».
        private const string OcrScript = @"
$ErrorActionPreference='Stop'
$folder=$env:UTT_OCR_FOLDER; $out=$env:UTT_OCR_OUT; $lang=$env:UTT_OCR_LANG
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
$asTask=([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op,$ty){ $m=$asTask.MakeGenericMethod($ty); $tk=$m.Invoke($null,@($op)); $tk.Wait(); $tk.Result }
[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder,Windows.Foundation,ContentType=WindowsRuntime] | Out-Null
[Windows.Storage.StorageFile,Windows.Foundation,ContentType=WindowsRuntime] | Out-Null
[Windows.Globalization.Language,Windows.Foundation,ContentType=WindowsRuntime] | Out-Null
$engine=$null
if($lang){ try{ $engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language($lang))) }catch{} }
if($null -eq $engine){ $engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
if($null -eq $engine){ Write-Error 'no OCR engine/language'; exit 3 }
$sw=New-Object System.IO.StreamWriter($out,$false,(New-Object System.Text.UTF8Encoding($false)))
$n=0
foreach($file in (Get-ChildItem $folder -Filter *.png -File)){
  try{
    $sf=Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($file.FullName)) ([Windows.Storage.StorageFile])
    $st=Await ($sf.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $dec=Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($st)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $sb=Await ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $ocr=Await ($engine.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult])
    $st.Dispose()
    $txt=($ocr.Text -replace '\s+',' ').Trim()
    if($txt.Length -ge 2){ $sw.WriteLine($file.Name + ""`t"" + $txt); $n++ }
  }catch{}
}
$sw.Close()
Write-Output ('ocr done: ' + $n)
";

        /// <summary>Кладёт ocr.ps1 в AppData (для прозрачности; запуск идёт по содержимому через stdin).</summary>
        internal static void EnsureScript()
        {
            try
            {
                Directory.CreateDirectory(OcrDir);
                File.WriteAllText(ScriptPath, OcrScript, new UTF8Encoding(false));
            }
            catch { /* не критично — запускаем по содержимому */ }
        }

        /// <summary>
        /// OCR всех PNG в папке встроенным Windows OCR. Возвращает карту «имя_файла.png → распознанный текст».
        /// Пустой результат, если OCR/язык недоступны (на не-Windows10 или без языкового пакета).
        /// </summary>
        internal static Dictionary<string, string> RunOcrOnFolder(string imageFolder, string langTag, ICollection<string> log)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(imageFolder) || !Directory.Exists(imageFolder))
                return map;

            EnsureScript();
            var outTsv = Path.Combine(imageFolder, "_ocr_result.tsv");
            try { if (File.Exists(outTsv)) File.Delete(outTsv); } catch { /* ignore */ }

            var psExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psExe))
                psExe = "powershell.exe";

            // -EncodedCommand надёжнее, чем «-Command -» со stdin (тот в GUI-процессе не читался → 0 результатов)
            // и не упирается в execution policy (это команды, а не файл-скрипт).
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(OcrScript));

            var psi = new ProcessStartInfo
            {
                FileName = psExe,
                Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["UTT_OCR_FOLDER"] = imageFolder;
            psi.EnvironmentVariables["UTT_OCR_OUT"] = outTsv;
            psi.EnvironmentVariables["UTT_OCR_LANG"] = langTag ?? "";

            try
            {
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEnd();
                    var se = p.StandardError.ReadToEnd();
                    p.WaitForExit();

                    foreach (var l in SplitLines(so)) log?.Add("[OCR] " + l);
                    foreach (var l in SplitLines(se)) log?.Add("[OCR err] " + l);
                }
            }
            catch (Exception ex)
            {
                log?.Add("OCR (PowerShell/Windows.Media.Ocr) не запустился: " + ex.Message);
                return map;
            }

            try
            {
                if (File.Exists(outTsv))
                {
                    foreach (var line in File.ReadAllLines(outTsv, Encoding.UTF8))
                    {
                        var tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        var file = line.Substring(0, tab);
                        var text = line.Substring(tab + 1);
                        if (!string.IsNullOrEmpty(file))
                            map[file] = text;
                    }
                    try { File.Delete(outTsv); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                log?.Add("OCR результат не прочитан: " + ex.Message);
            }

            return map;
        }

        private static IEnumerable<string> SplitLines(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                yield break;
            using (var r = new StringReader(s.Trim()))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                    if (!string.IsNullOrWhiteSpace(line))
                        yield return line.TrimEnd();
            }
        }
    }
}
