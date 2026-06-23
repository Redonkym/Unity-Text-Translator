using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UnityTextTranslator
{
    // HTML-панели на «Главной»: пользователь кодит содержимое (HTML/CSS/JS), рендер во встроенном WebBrowser; хранятся в настройках.
    partial class Form1
    {
        /// <summary>Одна пользовательская HTML-панель дашборда.</summary>
        private class DashboardPanelData
        {
            public string Title { get; set; } = "";
            public string Html { get; set; } = "";
        }

        private readonly List<DashboardPanelData> dashboardPanels = new List<DashboardPanelData>();
        private readonly ToolTip _toolTip = new ToolTip();
        private FlowLayoutPanel customPanelsFlow;
        private Action _rebuildCustomPanels;
        private static bool _ieEmulationSet;

        private const int CustomPanelCardW = 380;
        private const int CustomPanelCardH = 240;

        /// <summary>Секция «Мои панели» под основным рядом: карточки HTML-панелей + плитка «+».</summary>
        private Control BuildCustomPanelsSection(
            Color cardBg, Color cardBorder, Color titleFg, Color mutedFg, Color accent, bool dark)
        {
            EnsureWebBrowserModernEmulation();

            var wrap = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0, 16, 0, 0)
            };

            var title = DashBoardCardTitleRow(
                DashBoardGlyph.Grid(20, accent), L("My panels", "Мои панели"), titleFg, mutedFg, accent);

            customPanelsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            _rebuildCustomPanels = () =>
                PopulateCustomPanelsFlow(cardBg, cardBorder, titleFg, mutedFg, accent, dark);
            _rebuildCustomPanels();

            wrap.Controls.Add(DcStack(title, customPanelsFlow));
            return wrap;
        }

        private void PopulateCustomPanelsFlow(
            Color cardBg, Color cardBorder, Color titleFg, Color mutedFg, Color accent, bool dark)
        {
            if (customPanelsFlow == null || customPanelsFlow.IsDisposed)
                return;

            customPanelsFlow.SuspendLayout();
            try
            {
                foreach (Control c in customPanelsFlow.Controls)
                    c.Dispose();
                customPanelsFlow.Controls.Clear();

                foreach (var panel in dashboardPanels.ToList())
                    customPanelsFlow.Controls.Add(
                        BuildCustomPanelCard(panel, cardBg, cardBorder, titleFg, mutedFg, accent, dark));

                customPanelsFlow.Controls.Add(
                    BuildAddPanelTile(cardBg, cardBorder, titleFg, mutedFg, accent, dark));
            }
            finally
            {
                customPanelsFlow.ResumeLayout(true);
            }
        }

        private Control BuildCustomPanelCard(
            DashboardPanelData panel, Color cardBg, Color cardBorder, Color titleFg, Color mutedFg, Color accent, bool dark)
        {
            var card = new Panel
            {
                Width = CustomPanelCardW,
                Height = CustomPanelCardH,
                BackColor = cardBg,
                Margin = new Padding(0, 0, 12, 12)
            };
            ApplyDashboardRoundedClip(card, 10);
            card.Paint += (_, e) =>
            {
                try
                {
                    using (var pen = new Pen(cardBorder, 1f))
                    using (var path = CreateRoundedRectPath(
                               new Rectangle(0, 0, Math.Max(1, card.Width - 1), Math.Max(1, card.Height - 1)), 10))
                    {
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        e.Graphics.DrawPath(pen, path);
                    }
                }
                catch { }
            };

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.Transparent,
                Padding = new Padding(12, 5, 6, 0)
            };

            var lbl = new Label
            {
                Dock = DockStyle.Fill,
                Text = string.IsNullOrWhiteSpace(panel.Title) ? L("Panel", "Панель") : panel.Title,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = titleFg,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
                UseCompatibleTextRendering = false
            };

            Label HeaderButton(string glyph, string tip)
            {
                var b = new Label
                {
                    Dock = DockStyle.Right,
                    Width = 26,
                    Text = glyph,
                    Font = new Font("Segoe UI", 10f),
                    ForeColor = mutedFg,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    BackColor = Color.Transparent
                };
                b.MouseEnter += (_, __) => b.ForeColor = accent;
                b.MouseLeave += (_, __) => b.ForeColor = mutedFg;
                _toolTip.SetToolTip(b, tip);
                return b;
            }

            var btnEdit = HeaderButton("✎", L("Edit panel", "Изменить панель"));
            var btnDel = HeaderButton("✕", L("Remove panel", "Удалить панель"));

            header.Controls.Add(lbl);
            header.Controls.Add(btnEdit);
            header.Controls.Add(btnDel);

            var web = new WebBrowser
            {
                Dock = DockStyle.Fill,
                IsWebBrowserContextMenuEnabled = false,
                WebBrowserShortcutsEnabled = false,
                AllowWebBrowserDrop = false,
                ScrollBarsEnabled = true
            };

            card.Controls.Add(web);
            card.Controls.Add(header);

            try { web.DocumentText = WrapPanelHtml(panel.Html, dark); }
            catch { }

            btnEdit.Click += (_, __) =>
            {
                if (ShowDashboardPanelEditor(panel, isNew: false))
                {
                    SaveSettings();
                    _rebuildCustomPanels?.Invoke();
                }
            };
            btnDel.Click += (_, __) =>
            {
                var ok = MessageBox.Show(this,
                    L("Remove this panel?", "Удалить эту панель?"),
                    L("My panels", "Мои панели"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ok != DialogResult.Yes)
                    return;
                dashboardPanels.Remove(panel);
                SaveSettings();
                _rebuildCustomPanels?.Invoke();
            };

            return card;
        }

        private Control BuildAddPanelTile(
            Color cardBg, Color cardBorder, Color titleFg, Color mutedFg, Color accent, bool dark)
        {
            var tileBg = ThemeMix(cardBg, dark ? Color.White : Color.Black, 0.03);
            var tile = new Panel
            {
                Width = CustomPanelCardW,
                Height = CustomPanelCardH,
                BackColor = tileBg,
                Margin = new Padding(0, 0, 12, 12),
                Cursor = Cursors.Hand
            };
            ApplyDashboardRoundedClip(tile, 10);
            tile.Paint += (_, e) =>
            {
                try
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var pen = new Pen(ThemeMix(cardBorder, accent, 0.5), 1.4f) { DashStyle = DashStyle.Dash })
                    using (var path = CreateRoundedRectPath(
                               new Rectangle(1, 1, Math.Max(1, tile.Width - 3), Math.Max(1, tile.Height - 3)), 10))
                        e.Graphics.DrawPath(pen, path);

                    string plus = "+";
                    using (var f = new Font("Segoe UI", 34f, FontStyle.Bold))
                    {
                        var sz = e.Graphics.MeasureString(plus, f);
                        using (var br = new SolidBrush(accent))
                            e.Graphics.DrawString(plus, f, br,
                                (tile.Width - sz.Width) / 2f, tile.Height / 2f - sz.Height + 6);
                    }
                    string cap = L("Add panel (HTML)", "Добавить панель (HTML)");
                    using (var f2 = new Font("Segoe UI", 9.75f, FontStyle.Bold))
                    {
                        var sz2 = e.Graphics.MeasureString(cap, f2);
                        using (var br2 = new SolidBrush(mutedFg))
                            e.Graphics.DrawString(cap, f2, br2,
                                (tile.Width - sz2.Width) / 2f, tile.Height / 2f + 12);
                    }
                }
                catch { }
            };

            void AddNew(object _, EventArgs __)
            {
                var d = new DashboardPanelData { Title = "", Html = DefaultPanelTemplate() };
                if (ShowDashboardPanelEditor(d, isNew: true))
                {
                    dashboardPanels.Add(d);
                    SaveSettings();
                    _rebuildCustomPanels?.Invoke();
                }
            }

            tile.Click += AddNew;
            return tile;
        }

        /// <summary>Диалог-редактор панели: заголовок + поле HTML-кода. Возвращает true, если сохранено.</summary>
        private bool ShowDashboardPanelEditor(DashboardPanelData panel, bool isNew)
        {
            Color formBg = _themePageBg;
            Color fieldBg = ThemeCardSurface();
            Color titleFg = _themeHeaderText;
            Color bodyFg = _themeGridRowFore;
            Color accent = DashboardAccentPrimary();

            using (var dlg = new Form())
            {
                dlg.Text = isNew
                    ? L("New panel", "Новая панель")
                    : L("Edit panel", "Изменить панель");
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowIcon = false;
                dlg.ClientSize = new Size(640, 520);
                dlg.MinimumSize = new Size(480, 360);
                dlg.BackColor = formBg;
                dlg.ForeColor = titleFg;
                ApplyThemedTitleBar(dlg);

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 4,
                    Padding = new Padding(14),
                    BackColor = formBg
                };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));

                var titleLbl = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = L("Title", "Заголовок"),
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    ForeColor = bodyFg,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var titleBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Text = panel.Title ?? "",
                    Font = new Font("Segoe UI", 10f),
                    BackColor = fieldBg,
                    ForeColor = titleFg,
                    BorderStyle = BorderStyle.FixedSingle
                };

                var codeBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    AcceptsTab = true,
                    AcceptsReturn = true,
                    WordWrap = false,
                    ScrollBars = ScrollBars.Both,
                    Font = new Font("Consolas", 10f),
                    BackColor = fieldBg,
                    ForeColor = titleFg,
                    BorderStyle = BorderStyle.FixedSingle,
                    Text = string.IsNullOrEmpty(panel.Html) ? DefaultPanelTemplate() : panel.Html
                };

                var footer = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    Padding = new Padding(0, 10, 0, 0),
                    BackColor = formBg
                };
                var btnOk = new Button
                {
                    Text = isNew ? L("Add", "Добавить") : L("Save", "Сохранить"),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.White,
                    BackColor = accent,
                    Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                    Size = new Size(120, 32),
                    DialogResult = DialogResult.OK,
                    Cursor = Cursors.Hand
                };
                btnOk.FlatAppearance.BorderSize = 0;
                var btnCancel = new Button
                {
                    Text = L("Cancel", "Отмена"),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = bodyFg,
                    BackColor = ThemeMix(fieldBg, isDarkTheme ? Color.White : Color.Black, 0.06),
                    Font = new Font("Segoe UI", 9.75f),
                    Size = new Size(110, 32),
                    Margin = new Padding(8, 3, 0, 3),
                    DialogResult = DialogResult.Cancel,
                    Cursor = Cursors.Hand
                };
                btnCancel.FlatAppearance.BorderSize = 0;
                footer.Controls.Add(btnOk);
                footer.Controls.Add(btnCancel);

                root.Controls.Add(titleLbl, 0, 0);
                root.Controls.Add(titleBox, 0, 1);
                root.Controls.Add(codeBox, 0, 2);
                root.Controls.Add(footer, 0, 3);

                dlg.Controls.Add(root);
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return false;

                panel.Title = (titleBox.Text ?? "").Trim();
                panel.Html = codeBox.Text ?? "";
                return true;
            }
        }

        private static string DefaultPanelTemplate()
        {
            return
                "<h3>Моя панель</h3>\r\n" +
                "<p>Здесь любой HTML / CSS / JS.</p>\r\n" +
                "<button onclick=\"document.getElementById('t').innerText = new Date().toLocaleTimeString()\">Время</button>\r\n" +
                "<p id=\"t\"></p>";
        }

        /// <summary>Оборачивает HTML панели базовым стилем под текущую тему (тело — это код пользователя).</summary>
        private static string WrapPanelHtml(string bodyHtml, bool dark)
        {
            string bg = dark ? "#1e1c28" : "#ffffff";
            string fg = dark ? "#e4e2ee" : "#1f2328";
            string link = dark ? "#9b8cff" : "#5a32ff";
            return
                "<!DOCTYPE html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">" +
                "<meta charset=\"utf-8\"><style>" +
                "html,body{margin:0;padding:10px;font-family:'Segoe UI',Arial,sans-serif;font-size:13px;" +
                "background:" + bg + ";color:" + fg + ";} a{color:" + link + ";} " +
                "h1,h2,h3{margin:.2em 0;} button{cursor:pointer;}" +
                "</style></head><body>" + (bodyHtml ?? "") + "</body></html>";
        }

        /// <summary>Включает движок IE11 для встроенного WebBrowser (иначе по умолчанию IE7).</summary>
        private static void EnsureWebBrowserModernEmulation()
        {
            if (_ieEmulationSet)
                return;
            _ieEmulationSet = true;
            try
            {
                string exe = Path.GetFileName(Application.ExecutablePath);
                if (string.IsNullOrEmpty(exe))
                    return;
                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    key?.SetValue(exe, 11001, RegistryValueKind.DWord);
                }
            }
            catch { }
        }
    }
}
