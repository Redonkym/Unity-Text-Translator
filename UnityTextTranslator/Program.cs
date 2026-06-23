using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    internal static class Program
    {
        internal const string EmbeddedMonoCecilRocksResourceName = "Mono.Cecil.Rocks.dll";

        private static Form1 _mainForm;

        /// <summary>Главная точка входа.</summary>
        [STAThread]
        static void Main()
        {
            ApplyEmbeddedRuntimeConfiguration();

            ConfigureHttpsConnectivityDefaults();

            EnsureMonoCecilSatellitesLoaded();

            InstallGlobalExceptionHandlers();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _mainForm = new Form1();
            Application.Run(_mainForm);
        }

        /// <summary>
        /// Исключения из async void-обработчиков (выбор папки, импорт/экспорт, пакетный перевод) всплывают как
        /// <see cref="Application.ThreadException"/> и раньше роняли приложение в обход автосейва; теперь логируем,
        /// аварийно сохраняем и НЕ закрываемся. Фатальные ловит <see cref="AppDomain.UnhandledException"/>.
        /// </summary>
        private static void InstallGlobalExceptionHandlers()
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            }
            catch
            {
                // режим уже зафиксирован / нестандартная среда — обработчики ниже всё равно ставим
            }

            Application.ThreadException += (_, e) => HandleGlobalException(e.Exception, fatal: false);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                HandleGlobalException(e.ExceptionObject as Exception, fatal: e.IsTerminating);
        }

        private static void HandleGlobalException(Exception ex, bool fatal)
        {
            int saved = -2; // -2 = аварийное сохранение не вызывалось
            try { saved = _mainForm?.TryEmergencySaveDirtyTranslations() ?? -2; }
            catch { /* обработчик не должен падать сам */ }

            try { WriteCrashLog(ex, fatal, saved); }
            catch { /* лог — best-effort */ }

            try
            {
                string savedNote;
                if (saved > 0)
                    savedNote = $"\r\n\r\nНесохранённые переводы аварийно записаны в JSON: {saved} строк.";
                else if (saved == -1)
                    savedNote = "\r\n\r\nАварийное сохранение не удалось — проверьте резервные копии (*.bak) и crash.log.";
                else
                    savedNote = "";

                MessageBox.Show(
                    (fatal
                        ? "Произошла критическая ошибка, приложение будет закрыто."
                        : "Произошла ошибка, но приложение продолжит работу.") +
                    "\r\n\r\n" + (ex?.Message ?? "неизвестная ошибка") +
                    savedNote +
                    "\r\n\r\nЛог: " + CrashLogPath(),
                    "Unity Text Translator",
                    MessageBoxButtons.OK,
                    fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
            }
            catch
            {
                // нет UI-контекста (фоновый поток при завершении) — молча, лог уже записан
            }
        }

        private static string CrashLogPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnityTextTranslator");
            return Path.Combine(dir, "crash.log");
        }

        private static void WriteCrashLog(Exception ex, bool fatal, int saved)
        {
            var path = CrashLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var text =
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {(fatal ? "FATAL" : "THREAD")} (emergencySave={saved}) ====\r\n" +
                (ex?.ToString() ?? "(no exception object)") + "\r\n\r\n";
            File.AppendAllText(path, text);
        }

        /// <summary>То же, что App.config (runtime/AppContext) — чтобы Release обходился без .exe.config рядом.</summary>
        private static void ApplyEmbeddedRuntimeConfiguration()
        {
            try
            {
                AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
                AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
            }
            catch
            {
                // игнор на очень старых / нестандартных CLR
            }
        }

        /// <summary>HTTPS к OpenRouter/LibreTranslate: TLS 1.2 и системный прокси (корпоративная сеть).</summary>
        private static void ConfigureHttpsConnectivityDefaults()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
                // игнор на экзотических конфигурациях
            }
        }

        /// <summary>
        /// Mono.Cecil тянет Mono.Cecil.Rocks при разборе типов; одной dll рядом с exe бывает мало (shadow copy VS, пустой
        /// Location). Дублируем: встроенный ресурс → Assembly.Load(bytes), файл рядом, <see cref="AppDomain.AssemblyResolve"/>.
        /// </summary>
        private static void EnsureMonoCecilSatellitesLoaded()
        {
            string exeDir = null;
            try
            {
                exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                // exeDir остаётся null — остаётся встроенный ресурс и AssemblyResolve без диска.
            }

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    var requested = new AssemblyName(args.Name);
                    if (!string.Equals(requested.Name, "Mono.Cecil.Rocks", StringComparison.OrdinalIgnoreCase))
                        return null;

                    return TryLoadMonoCecilRocksAssembly(exeDir);
                }
                catch
                {
                    return null;
                }
            };

            // Не подгружаем Rocks при старте: Assembly.Load из встроенного ресурса заметно тормозит холодный запуск.
            // При первом обращении к типам из Rocks сработает AssemblyResolve (см. выше).
        }

        /// <summary>Возвращает уже загруженную Rocks или загружает из manifest resource / с диска.</summary>
        internal static Assembly TryLoadMonoCecilRocksAssembly(string exeDirectoryOrNull)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(asm.GetName().Name, "Mono.Cecil.Rocks", StringComparison.OrdinalIgnoreCase))
                        return asm;
                }
                catch
                {
                    // игнор одной сборки с нестандартным именем
                }
            }

            try
            {
                var self = Assembly.GetExecutingAssembly();
                using (var stream = self.GetManifestResourceStream(EmbeddedMonoCecilRocksResourceName))
                {
                    if (stream != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            return Assembly.Load(ms.ToArray());
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(exeDirectoryOrNull))
                {
                    var path = Path.Combine(exeDirectoryOrNull, "Mono.Cecil.Rocks.dll");
                    if (File.Exists(path))
                        return Assembly.LoadFrom(path);
                }
            }
            catch
            {
                // AssemblyResolve или повторный вызов позже
            }

            return null;
        }
    }
}
