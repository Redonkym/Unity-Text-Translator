using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace UnityTextTranslator
{
    /// <summary>
    /// Встроенная копия <see href="https://github.com/Chlumsky/msdf-atlas-gen">msdf-atlas-gen</see> (MIT):
    /// MSDF/SDF атлас из TTF/OTF — тот же класс утилит, что часто используют для SDF-шрифтов.
    /// </summary>
    internal static class MsdfAtlasGenInterop
    {
        internal const string CompanionExeName = "msdf-atlas-gen.exe";
        internal const string DefaultAtlasSdfPngFileName = "atlas512_sdf.png";
        internal const string DefaultAtlasSdfJsonFileName = "atlas512_sdf.json";
        internal const string DefaultCharsetFileName = "charset.txt";

        private const string EmbeddedCompanionLogicalName = "msdf-atlas-gen.exe";
        private const int AtlasWidthPx = 512;
        private const int AtlasHeightPx = 512;

        /// <summary>Запуск генерации PNG + JSON рядом с <paramref name="workDir"/>.</summary>
        internal static int Run(
            string fontPath,
            string workDir,
            string imageFileName,
            string jsonFileName,
            int glyphSizePx,
            string charsetFilePath,
            string charsetInlineFallback,
            ICollection<string> logSink,
            int atlasDimensionPx = 0,
            int pxRange = 0)
        {
            var exe = ResolveCompanionExePath();
            if (string.IsNullOrEmpty(exe))
            {
                logSink?.Add(
                    "[msdf-atlas-gen] Нет встроенной утилиты: пересоберите проект (нужен скачанный Tools\\msdf-atlas-gen\\msdf-atlas-gen.exe и сеть при первой сборке).");
                return -1;
            }

            Directory.CreateDirectory(workDir ?? ".");
            var imageOut = Path.Combine(workDir, imageFileName ?? DefaultAtlasSdfPngFileName);
            var jsonOut = Path.Combine(workDir, jsonFileName ?? DefaultAtlasSdfJsonFileName);

            var args = new StringBuilder();
            args.Append("-font ").Append(QuoteArg(fontPath));
            args.Append(" -type sdf");
            args.Append(" -format png");
            var dim = atlasDimensionPx > 0 ? atlasDimensionPx : AtlasWidthPx;
            args.Append(" -dimensions ").Append(dim).Append(' ').Append(dim);
            if (pxRange > 0)
                args.Append(" -pxrange ").Append(pxRange);
            args.Append(" -size ").Append(glyphSizePx > 0 ? glyphSizePx : 36);
            args.Append(" -imageout ").Append(QuoteArg(imageOut));
            args.Append(" -json ").Append(QuoteArg(jsonOut));
            if (!string.IsNullOrWhiteSpace(charsetFilePath) && File.Exists(charsetFilePath))
            {
                args.Append(" -charset ").Append(QuoteArg(charsetFilePath));
            }
            else if (!string.IsNullOrWhiteSpace(charsetInlineFallback))
            {
                args.Append(" -chars ").Append(QuoteArg(charsetInlineFallback.Trim()));
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            logSink?.Add("[msdf cmd] " + psi.FileName + " " + psi.Arguments);

            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    logSink?.Add("[msdf-atlas-gen] Не удалось запустить процесс.");
                    return -1;
                }

                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
                proc.WaitForExit();
                var stdout = stdoutTask.GetAwaiter().GetResult();
                var stderr = stderrTask.GetAwaiter().GetResult();

                LogMsdfStream(logSink, "stdout", stdout);
                LogMsdfStream(logSink, "stderr", stderr);
                logSink?.Add("[msdf exit] " + proc.ExitCode);

                if (File.Exists(imageOut))
                    LogGeneratedPngOutput(imageOut, logSink);

                return proc.ExitCode;
            }
        }

        /// <summary>
        /// Пишет charset.txt для <c>-charset</c>: по одному decimal codepoint на строку (формат msdf-atlas-gen).
        /// </summary>
        internal static void WriteCharsetFileFromRanges(string charsetFilePath, string charsetRanges)
        {
            if (string.IsNullOrWhiteSpace(charsetFilePath))
                throw new ArgumentException("charsetFilePath is required.", nameof(charsetFilePath));

            var body = ExpandCharsetRangesToFileBody(charsetRanges);
            File.WriteAllText(charsetFilePath, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string ExpandCharsetRangesToFileBody(string charsetRanges)
        {
            if (string.IsNullOrWhiteSpace(charsetRanges))
                return string.Empty;

            var lines = new List<string>();
            foreach (var partRaw in charsetRanges.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = partRaw.Trim();
                if (part.Length == 0)
                    continue;

                var dash = part.IndexOf('-');
                if (dash > 0 && dash < part.Length - 1)
                {
                    var fromText = part.Substring(0, dash).Trim();
                    var toText = part.Substring(dash + 1).Trim();
                    if (!int.TryParse(fromText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
                        || !int.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
                    {
                        throw new InvalidOperationException("Invalid charset range: " + part);
                    }

                    if (from > to)
                    {
                        var swap = from;
                        from = to;
                        to = swap;
                    }

                    for (var cp = from; cp <= to; cp++)
                        lines.Add(cp.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cp))
                        throw new InvalidOperationException("Invalid charset codepoint: " + part);
                    lines.Add(cp.ToString(CultureInfo.InvariantCulture));
                }
            }

            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        private static void LogMsdfStream(ICollection<string> logSink, string streamName, string text)
        {
            if (logSink == null)
                return;

            var body = text ?? string.Empty;
            if (body.Length == 0)
            {
                logSink.Add("[msdf " + streamName + "] <empty>");
                return;
            }

            logSink.Add("[msdf " + streamName + "] " + body.TrimEnd('\r', '\n'));
        }

        private static void LogGeneratedPngOutput(string atlasPngPath, ICollection<string> logSink)
        {
            if (logSink == null || string.IsNullOrWhiteSpace(atlasPngPath))
                return;

            var pngInfo = new FileInfo(atlasPngPath);
            logSink.Add("[msdf output] PNG size: " + pngInfo.Length + " bytes, path: " + atlasPngPath);

            using (var fs = File.OpenRead(atlasPngPath))
            {
                var header = new byte[24];
                var read = fs.Read(header, 0, header.Length);
                if (read < 24)
                {
                    logSink.Add("[msdf output] PNG dimensions: header too short (" + read + " bytes)");
                    return;
                }

                var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                logSink.Add("[msdf output] PNG dimensions: " + width + "x" + height);
            }
        }

        private static string QuoteArg(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "\"\"";
            if (path.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return "\"" + path + "\"";
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }

        private static string ResolveCompanionExePath()
        {
            try
            {
                var cand = Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), CompanionExeName);
                if (File.Exists(cand))
                    return cand;
            }
            catch
            {
                /* ignore */
            }

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
            catch
            {
                /* ignore */
            }

            try
            {
                return MaterializeEmbeddedCompanionExe();
            }
            catch
            {
                /* ignore */
            }

            return null;
        }

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
            catch
            {
                /* replace */
            }

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
                catch
                {
                    /* ignore */
                }
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
