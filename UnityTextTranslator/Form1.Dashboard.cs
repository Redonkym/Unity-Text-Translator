using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    public partial class Form1
    {
        private void ShowChromeHeader()
        {
            if (headerPanel != null)
                headerPanel.Height = 56;
            if (headerLabel != null)
                headerLabel.Visible = true;
        }

        private void HideChromeHeaderForDashboard()
        {
            if (headerLabel != null)
                headerLabel.Visible = false;
            if (headerPanel != null)
                headerPanel.Height = 6;
        }

        private string DashboardChromeCacheKey() =>
            $"{currentThemeName}|{appUiLanguage}";

        private void BumpDashboardContentStamp()
        {
            unchecked { _dashboardContentStamp++; }
        }

        private void DisposeCachedDashboardRoot()
        {
            if (cachedDashboardRoot != null && !cachedDashboardRoot.IsDisposed)
                cachedDashboardRoot.Dispose();
            cachedDashboardRoot = null;
            _dashboardCacheBuiltAtStamp = -1;
            _cachedDashboardChromeKey = "";
        }

        /// <summary>Возвращает true, если переиспользовали закешированную «Главную» без пересборки UI.</summary>
        private bool TryAttachCachedDashboard()
        {
            if (cachedDashboardRoot == null || cachedDashboardRoot.IsDisposed)
                return false;
            if (_dashboardCacheBuiltAtStamp != _dashboardContentStamp)
                return false;
            if (!string.Equals(_cachedDashboardChromeKey, DashboardChromeCacheKey(), StringComparison.Ordinal))
                return false;

            moduleHostPanel.SuspendLayout();
            try
            {
                moduleHostPanel.Controls.Add(cachedDashboardRoot);
                cachedDashboardRoot.Dock = DockStyle.Fill;
            }
            finally
            {
                moduleHostPanel.ResumeLayout(true);
            }

            ApplyTheme();
            UpdateStatus();
            return true;
        }

        private void BuildDashboardUi()
        {
            DisposeCachedDashboardRoot();

            ApplyTheme();

            moduleHostPanel.SuspendLayout();
            try
            {
                bool dark = isDarkTheme;
            Color dashBg = _themePageBg;
            Color cardBg = currentThemeName == "Translator Purple"
                ? Color.FromArgb(30, 28, 40)
                : isDarkTheme
                    ? Color.FromArgb(30, 41, 59)
                    : Color.White;
            Color cardBorder = _themeGridColor;
            Color titleFg = _themeHeaderText;
            Color mutedFg = _themeSubtitleText;
            Color tableHeaderBg = _themeGridHeaderBg;
            Color tableDivider = _themeGridColor;
            Color accentPurple = DashboardAccentPrimary();
            Color accentGreen = Color.FromArgb(56, 212, 154);
            Color accentBlue = Color.FromArgb(122, 162, 247);
            Color accentOrange = Color.FromArgb(247, 202, 106);
            Color btnSurface = cardBg;

            var root = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = false,
                BackColor = dashBg,
                Padding = new Padding(20, 10, 20, 18)
            };

            // Первая строка — фиксированная высота шапки; иначе % от окна даёт «пустоту» над заголовком «Главная».
            var dashGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = dashBg,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            dashGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            dashGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 68f));   // шапка
            dashGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // действия | (недавние + автор)

            // ----- Заголовок Dashboard + Open Project (строго верх, строка 0)
            var headerBar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = dashBg,
                Margin = new Padding(0)
            };
            headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320f));

            var headerLeft = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = dashBg
            };
            headerLeft.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38f));
            headerLeft.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            headerLeft.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var gridPic = new PictureBox
            {
                Size = new Size(28, 28),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Image = DashBoardGlyph.Grid(28, accentPurple),
                Margin = new Padding(0, 4, 0, 0),
                BackColor = Color.Transparent
            };
            headerLeft.Controls.Add(gridPic, 0, 0);

            var lblDashTitle = new Label
            {
                Text = L("Home", "Главная"),
                Font = new Font("Segoe UI", 22f, FontStyle.Bold),
                ForeColor = titleFg,
                AutoSize = true,
                Margin = new Padding(6, 6, 0, 0),
                BackColor = Color.Transparent,
                UseCompatibleTextRendering = false
            };
            headerLeft.Controls.Add(lblDashTitle, 1, 0);

            var openHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = dashBg,
                Margin = new Padding(0),
                Padding = new Padding(0, 12, 0, 12),
                MinimumSize = new Size(280, 44)
            };

            var ctxOpen = new ContextMenuStrip();
            ctxOpen.Items.Add(L("Browse for folder…", "Выбрать папку…"), null, (_, __) => BtnSelectFolder_Click(openHost, EventArgs.Empty));
            foreach (var rf in recentJsonFolders.ToList())
            {
                if (string.IsNullOrWhiteSpace(rf))
                    continue;
                var copy = rf.Trim();
                ctxOpen.Items.Add(copy, null, (_, __) => OpenRecentJsonFolderFromDashboard(copy));
            }

            Color dashBtnHover = DashBlendRgb(btnSurface, titleFg, dark ? 0.08f : 0.06f);
            Color dashBtnPress = DashBlendRgb(btnSurface, titleFg, dark ? 0.14f : 0.11f);

            var strip = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = dashBg,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38f));
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));

            var btnDropOpen = new DashboardOutlineButton
            {
                Text = "▾",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                MinimumSize = new Size(38, 44),
                MaximumSize = new Size(38, 44),
                Font = new Font("Segoe UI", 11f),
                ForeColor = titleFg,
                BackColor = btnSurface,
                SurfaceColor = btnSurface,
                HoverSurfaceColor = dashBtnHover,
                PressSurfaceColor = dashBtnPress,
                OutlineColor = cardBorder,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnDropOpen.Click += (_, __) => ctxOpen.Show(btnDropOpen, new Point(0, btnDropOpen.Height));

            var btnOpenMain = new DashboardOutlineButton
            {
                Text = L("Choose project folder", "Выбрать папку проекта"),
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                MinimumSize = new Size(100, 44),
                MaximumSize = new Size(2000, 44),
                Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                ForeColor = titleFg,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(12, 0, 12, 0),
                BackColor = btnSurface,
                SurfaceColor = btnSurface,
                HoverSurfaceColor = dashBtnHover,
                PressSurfaceColor = dashBtnPress,
                OutlineColor = cardBorder,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnOpenMain.Click += (_, __) => BtnSelectFolder_Click(btnOpenMain, EventArgs.Empty);

            strip.Controls.Add(btnOpenMain, 0, 0);
            strip.Controls.Add(btnDropOpen, 1, 0);
            openHost.Controls.Add(strip);

            headerBar.Controls.Add(headerLeft, 0, 0);
            headerBar.Controls.Add(openHost, 1, 0);

            dashGrid.Controls.Add(headerBar, 0, 0);

            // ----- Строка 1: быстрые действия | недавние папки
            var mainRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = dashBg,
                Margin = new Padding(0)
            };
            mainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
            mainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));

            var actionsCard = BuildActionsGrid(cardBg, cardBorder, titleFg, mutedFg, accentPurple, accentBlue, accentGreen, accentOrange, dark);
            actionsCard.Margin = new Padding(0, 0, 12, 0);
            mainRow.Controls.Add(actionsCard, 0, 0);

            // Правая колонка: «Недавние папки» (растягивается) + «Автор» снизу — чтобы низ не пустовал.
            var rightCol = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = dashBg,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            rightCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rightCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rightCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var foldersCard = DashBoardCardShell(cardBg, cardBorder);
            foldersCard.Dock = DockStyle.Top;
            foldersCard.AutoSize = true;
            foldersCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            foldersCard.Margin = new Padding(0, 0, 0, 12);
            foldersCard.Controls.Add(DcStack(
                DashBoardCardTitleRow(
                    DashBoardGlyph.Clock(20, accentPurple), L("Recent Folders", "Недавние папки"), titleFg, mutedFg, accentPurple),
                BuildRecentFoldersBody(mutedFg, accentPurple, titleFg, tableHeaderBg, tableDivider, dark)));
            rightCol.Controls.Add(foldersCard, 0, 0);

            var infoCard = DashBoardCardShell(cardBg, cardBorder);
            infoCard.Margin = new Padding(0);
            infoCard.Dock = DockStyle.Top;
            infoCard.AutoSize = true;
            infoCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            infoCard.Controls.Add(DcStack(
                DashBoardCardTitleRow(
                    DashBoardGlyph.Info(20, accentPurple), L("Author", "Автор"), titleFg, mutedFg, accentPurple),
                BuildAuthorStrip(titleFg, mutedFg, accentPurple)));
            rightCol.Controls.Add(infoCard, 0, 1);

            mainRow.Controls.Add(rightCol, 1, 0);

            // Центральная стопка: основной ряд (действия | недавние+автор) + HTML-панели снизу (кнопка «+» добавляет).
            var centerStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = dashBg,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            centerStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            centerStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            centerStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            centerStack.Controls.Add(mainRow, 0, 0);
            centerStack.Controls.Add(
                BuildCustomPanelsSection(cardBg, cardBorder, titleFg, mutedFg, accentPurple, dark), 0, 1);

            var mainHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = dashBg,
                Padding = new Padding(0, 0, 0, 8)
            };
            mainHost.Controls.Add(centerStack);
            dashGrid.Controls.Add(mainHost, 0, 1);

            root.Controls.Add(dashGrid);
            moduleHostPanel.Controls.Add(root);

            cachedDashboardRoot = root;
            _dashboardCacheBuiltAtStamp = _dashboardContentStamp;
            _cachedDashboardChromeKey = DashboardChromeCacheKey();

            UpdateStatus();
            }
            finally
            {
                moduleHostPanel.ResumeLayout(true);
            }
        }

        private Color DashboardAccentPrimary()
        {
            switch (currentThemeName)
            {
                case "Translator Purple":
                    return Color.FromArgb(124, 77, 255);
                case "Dracula":
                    return Color.FromArgb(189, 147, 249);
                case "Nord":
                    return Color.FromArgb(136, 192, 208);
                case "GitHub Dark":
                    return Color.FromArgb(88, 166, 255);
                case "Visual Studio Dark":
                    return Color.FromArgb(0, 122, 204);
                case "Solarized Light":
                    return Color.FromArgb(38, 139, 210);
                default:
                    return isDarkTheme ? Color.FromArgb(59, 130, 246) : Color.FromArgb(37, 99, 235);
            }
        }

        private static int GuessActiveLanguages(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return 0;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var path in Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var idx = name.LastIndexOf('_');
                    if (idx > 0 && idx < name.Length - 1)
                    {
                        var tail = name.Substring(idx + 1);
                        if (tail.Length >= 2 && tail.Length <= 12 && Regex.IsMatch(tail, @"^[\w\-]+$"))
                            set.Add(tail);
                    }
                }
            }
            catch
            {
                return 2;
            }

            return set.Count > 0 ? set.Count : 2;
        }

        private Panel DashBoardCardShell(Color cardBg, Color borderHint)
        {
            const int radius = 10;
            var p = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18, 12, 18, 16),
                BackColor = cardBg,
                Margin = new Padding(0)
            };
            ApplyDashboardRoundedClip(p, radius);
            p.Paint += (_, e) =>
            {
                try
                {
                    using (var pen = new Pen(borderHint, 1f))
                    using (var path = CreateRoundedRectPath(
                               new Rectangle(0, 0, Math.Max(1, p.Width - 1), Math.Max(1, p.Height - 1)), radius))
                    {
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        e.Graphics.DrawPath(pen, path);
                    }
                }
                catch { }
            };

            return p;
        }

        private Panel DashBoardCardTitleRow(Image icon, string title, Color titleFg, Color mutedFg, Color accentPurple)
        {
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false,
                Padding = new Padding(0, 0, 0, 6),
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };

            var pic = new PictureBox
            {
                Size = new Size(24, 24),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Image = icon,
                Margin = new Padding(0, 2, 10, 0),
                BackColor = Color.Transparent
            };

            var lbl = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = titleFg,
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 0),
                BackColor = Color.Transparent,
                UseCompatibleTextRendering = false
            };

            row.Controls.Add(pic);
            row.Controls.Add(lbl);
            return row;
        }

        /// <summary>Компактная KPI-карточка: иконка, крупное число, подпись. Полосы фиксированной высоты — подпись не обрезается.</summary>
        private Panel BuildKpiCard(string number, string caption, Image glyph, Color accent, Color cardBg, Color border, Color titleFg, Color mutedFg)
        {
            var card = DashBoardCardShell(cardBg, border);
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0);
            card.Padding = new Padding(18, 14, 16, 12);

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));

            // Иконка в тонированном скруглённом бейдже цвета акцента.
            Color badgeBg = isDarkTheme
                ? DashBlendRgb(accent, cardBg, 0.72f)
                : DashBlendRgb(accent, Color.White, 0.84f);
            var badge = new Panel
            {
                Size = new Size(30, 30),
                BackColor = badgeBg,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 1, 0, 0)
            };
            ApplyDashboardRoundedClip(badge, 8);
            var pic = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.CenterImage,
                Image = glyph,
                BackColor = Color.Transparent
            };
            badge.Controls.Add(pic);

            var num = new Label
            {
                Dock = DockStyle.Fill,
                Text = number,
                Font = new Font("Segoe UI", 22f, FontStyle.Bold),
                ForeColor = titleFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.BottomLeft,
                UseCompatibleTextRendering = false,
                Margin = new Padding(0)
            };
            var cap = new Label
            {
                Dock = DockStyle.Fill,
                Text = caption,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = mutedFg,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0)
            };

            body.Controls.Add(badge, 0, 0);
            body.Controls.Add(num, 0, 1);
            body.Controls.Add(cap, 0, 2);
            card.Controls.Add(body);
            return card;
        }

        /// <summary>Детерминированно стопкой: заголовок сверху (34px), тело снизу (по содержимому). Снимает неоднозначность Dock=Top+Top.</summary>
        private TableLayoutPanel DcStack(Control titleRow, Control body)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            titleRow.Dock = DockStyle.Top;
            body.Dock = DockStyle.Top;
            t.Controls.Add(titleRow, 0, 0);
            t.Controls.Add(body, 0, 1);
            return t;
        }

        /// <summary>Карточка «Быстрые действия»: сетка 2×3 кликабельных плиток с навигацией по разделам.</summary>
        private Panel BuildActionsGrid(
            Color cardBg, Color cardBorder, Color titleFg, Color mutedFg,
            Color accentPurple, Color accentBlue, Color accentGreen, Color accentOrange, bool dark)
        {
            var card = DashBoardCardShell(cardBg, cardBorder);
            card.Dock = DockStyle.Top;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0);
            var actionsTitle = DashBoardCardTitleRow(
                DashBoardGlyph.Bolt(20, accentPurple), L("Quick Actions", "Быстрые действия"), titleFg, mutedFg, accentPurple);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 3,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));

            int idx = 0;
            void AddTile(string title, string sub, Color accent, Image icon, Action onClick)
            {
                int col = idx % 2;
                int rowI = idx / 2;
                bool lastCol = col == 1;
                idx++;

                Color tileBg = dark
                    ? Color.FromArgb(Math.Min(255, cardBg.R + 12), Math.Min(255, cardBg.G + 14), Math.Min(255, cardBg.B + 20))
                    : DashBlendRgb(accent, Color.White, 0.90f);
                // Единая нейтральная рамка для всех плиток (раньше тонировалась акцентом — оранжевые «Шрифты»/«Экспорт» выбивались).
                Color edge = cardBorder;

                var tile = new Panel
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 0, lastCol ? 0 : 8, 8),
                    BackColor = tileBg,
                    Cursor = Cursors.Hand
                };
                ApplyDashboardRoundedClip(tile, 12);
                tile.Paint += (_, e) =>
                {
                    try
                    {
                        using (var pen = new Pen(edge, 1f))
                        using (var path = CreateRoundedRectPath(
                                   new Rectangle(0, 0, Math.Max(1, tile.Width - 1), Math.Max(1, tile.Height - 1)), 12))
                        {
                            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                            e.Graphics.DrawPath(pen, path);
                        }
                    }
                    catch { }
                };

                var pic = new PictureBox
                {
                    Size = new Size(26, 26),
                    SizeMode = PictureBoxSizeMode.CenterImage,
                    Image = icon,
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand
                };
                var ttl = new Label
                {
                    Text = title,
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                    ForeColor = titleFg,
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand,
                    UseCompatibleTextRendering = false
                };
                var subl = new Label
                {
                    Text = sub,
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = new Font("Segoe UI", 8.75f),
                    ForeColor = mutedFg,
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand
                };

                void LayoutTile()
                {
                    int w = tile.ClientSize.Width;
                    int hgt = tile.ClientSize.Height;
                    pic.Location = new Point(14, Math.Max(8, (hgt - pic.Height) / 2));
                    int lx = 14 + pic.Width + 10;
                    int lw = Math.Max(20, w - lx - 12);
                    ttl.SetBounds(lx, hgt / 2 - 20, lw, 20);
                    subl.SetBounds(lx, hgt / 2 + 1, lw, 18);
                }

                tile.Resize += (_, __) => LayoutTile();
                tile.Controls.Add(pic);
                tile.Controls.Add(ttl);
                tile.Controls.Add(subl);

                EventHandler click = (_, __) => onClick?.Invoke();
                tile.Click += click;
                pic.Click += click;
                ttl.Click += click;
                subl.Click += click;

                // Подсветка при наведении (учитываем уход курсора на дочерние контролы).
                Color hoverBg = dark
                    ? DashBlendRgb(tileBg, Color.White, 0.07f)
                    : DashBlendRgb(tileBg, accent, 0.12f);
                void RefreshHover()
                {
                    try
                    {
                        bool inside = tile.ClientRectangle.Contains(tile.PointToClient(Cursor.Position));
                        tile.BackColor = inside ? hoverBg : tileBg;
                    }
                    catch { }
                }
                EventHandler hov = (_, __) => RefreshHover();
                tile.MouseEnter += hov;
                tile.MouseLeave += hov;
                pic.MouseEnter += hov;
                ttl.MouseEnter += hov;
                subl.MouseEnter += hov;

                grid.Controls.Add(tile, col, rowI);
                LayoutTile();
            }

            AddTile(
                L("Add JSON files", "Добавить JSON-файлы"),
                L("Pick a localization folder", "Выбрать папку локализации"),
                accentPurple, DashBoardGlyph.Plus(22, accentPurple),
                () =>
                {
                    LoadJsonTranslatorModule();
                    BeginInvoke(new Action(() => BtnSelectFolder_Click(this, EventArgs.Empty)));
                });

            AddTile(
                L("AI translation", "Перевод с ИИ"),
                L("Fill empty rows via API", "Заполнить пустые строки через API"),
                accentPurple, DashBoardGlyph.TranslateBadge(22, accentPurple),
                () =>
                {
                    LoadJsonTranslatorModule();
                    BeginInvoke(new Action(() => MenuTranslateEmptyViaLocalApi_Click(this, EventArgs.Empty)));
                });

            AddTile(
                L("Localization bundles", "Локализация: бандлы"),
                L("Export .bundle ↔ JSON", "Экспорт .bundle ↔ JSON"),
                accentGreen, DashBoardGlyph.Bolt(22, accentGreen),
                LoadBundleLocalizationModule);

            AddTile(
                L("Unity .assets", "Unity .assets"),
                L("Export / import asset strings", "Экспорт/импорт строк ассетов"),
                accentBlue, DashBoardGlyph.Clipboard(22, accentBlue),
                LoadAssetsModule);

            AddTile(
                L("Fonts", "Шрифты"),
                L("TMP / Cyrillic font tools", "Шрифты TMP / кириллица"),
                accentOrange, DashBoardGlyph.Globe(22, accentOrange),
                LoadFontToolsModule);

            AddTile(
                L("Export translations", "Экспорт переводов"),
                L("Save as TXT / TSV / CSV", "Сохранить как TXT / TSV / CSV"),
                accentOrange, DashBoardGlyph.Download(22, accentOrange),
                () =>
                {
                    LoadJsonTranslatorModule();
                    BeginInvoke(new Action(() =>
                    {
                        if (btnExportTxt != null)
                            BtnExportTxt_Click(btnExportTxt, EventArgs.Empty);
                    }));
                });

            card.Controls.Add(DcStack(actionsTitle, grid));
            return card;
        }

        /// <summary>Компактная дата для «Недавних папок» — одна строка, без обрезки «18…».</summary>
        private string FormatRecentStamp(DateTime t)
        {
            if (t == default(DateTime))
                return "—";

            DateTime today = DateTime.Now.Date;
            if (t.Date == today)
                return L("today", "сегодня") + " " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1))
                return L("yesterday", "вчера") + " " + t.ToString("HH:mm");
            return t.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        }

        private Control BuildRecentFoldersBody(
            Color mutedFg,
            Color accentPurple,
            Color titleFg,
            Color headerBg,
            Color divider,
            bool dark)
        {
            var host = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 2, 0, 0)
            };

            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));

            AddTableHeaderCell(tbl, 0, L("Folder", "Папка"), headerBg, mutedFg, dark);
            AddTableHeaderCell(tbl, 1, L("Last updated", "Изменено"), headerBg, mutedFg, dark);

            int row = 1;
            bool any = false;
            foreach (var folderPath in recentJsonFolders.ToList())
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                    continue;
                any = true;
                var fp = folderPath.Trim();
                DateTime stamp = SafeDirectoryWriteTime(fp);

                var cellFolder = CreateClickableTableCell(
                    fp,
                    fp,
                    mutedFg,
                    divider,
                    dark,
                    () => OpenRecentJsonFolderFromDashboard(fp));

                var cellTime = CreateClickableTableCell(
                    FormatRecentStamp(stamp),
                    fp,
                    mutedFg,
                    divider,
                    dark,
                    () => OpenRecentJsonFolderFromDashboard(fp));

                tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
                tbl.RowCount++;
                tbl.Controls.Add(cellFolder, 0, row);
                tbl.Controls.Add(cellTime, 1, row);
                row++;
            }

            if (!any)
            {
                tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
                tbl.RowCount++;
                var empty = new Label
                {
                    Text = L(
                        "No recent folders yet. Use Open Project or JSON Files to choose a folder.",
                        "Недавних папок нет. Используйте «Открыть проект» или раздел JSON Files."),
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 9.75f),
                    ForeColor = mutedFg,
                    Padding = new Padding(10, 12, 10, 8),
                    BackColor = Color.Transparent
                };
                tbl.SetColumnSpan(empty, 2);
                tbl.Controls.Add(empty, 0, 1);
            }

            host.Controls.Add(tbl);
            return host;
        }

        private Panel BuildAuthorStrip(Color titleFg, Color mutedFg, Color accent)
        {
            const string boostyUrl = "https://boosty.to/redonkym";

            var outer = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent
            };

            // Тело — ОДНА LinkLabel (а не вложенный контейнер): в узком ряду метка всегда рисует текст и не наезжает на заголовок.
            string prefix =
                L("Author - Redonkym", "Автор - Redonkym") + "\n" +
                L("Support development on Boosty: ", "Поддержать разработку на Boosty: ");
            const string linkText = "boosty.to/redonkym";

            var link = new LinkLabel
            {
                Dock = DockStyle.Top,
                Height = 52,
                AutoSize = false,
                Text = prefix + linkText,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = mutedFg,
                LinkColor = accent,
                ActiveLinkColor = accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Color.Transparent,
                Padding = new Padding(2, 0, 2, 2),
                LinkArea = new LinkArea(prefix.Length, linkText.Length),
                Cursor = Cursors.Hand
            };
            link.LinkClicked += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = boostyUrl,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    Clipboard.SetText(boostyUrl);
                    MessageBox.Show(this,
                        L("Link copied to clipboard.", "Ссылка скопирована в буфер обмена."),
                        "Boosty", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            outer.Controls.Add(link);
            return outer;
        }

        private static string TextEllipsis(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;
            return text.Substring(0, Math.Max(3, maxChars - 3)) + "…";
        }

        private static DateTime SafeDirectoryWriteTime(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    return Directory.GetLastWriteTime(path);
            }
            catch { }

            return DateTime.MinValue;
        }

        private static void AddTableHeaderCell(TableLayoutPanel tbl, int col, string text, Color bg, Color fg, bool dark)
        {
            var lbl = new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = fg,
                BackColor = bg,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 10, 8, 10)
            };
            lbl.Margin = new Padding(0, 0, 0, 1);

            var wrap = new Panel { Dock = DockStyle.Fill, Height = 38, BackColor = bg };
            wrap.Controls.Add(lbl);
            if (tbl.RowCount == 0)
            {
                tbl.RowCount = 1;
                tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
            }

            tbl.Controls.Add(wrap, col, 0);
        }

        private Panel CreateClickableTableCell(
            string text,
            string tagPath,
            Color fg,
            Color divider,
            bool dark,
            Action onClick)
        {
            var p = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 40,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent,
                Padding = new Padding(12, 8, 8, 8),
                Margin = new Padding(0, 0, 0, 1),
                Tag = tagPath
            };
            p.Paint += (_, e) =>
            {
                try
                {
                    using (var pen = new Pen(divider, 1f))
                        e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
                }
                catch { }
            };

            var lbl = new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.25f),
                ForeColor = fg,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            void InvokeClick(object _, EventArgs __) => onClick?.Invoke();

            p.Click += InvokeClick;
            lbl.Click += InvokeClick;

            p.Controls.Add(lbl);

            void HoverIn(object _, EventArgs __)
            {
                try
                {
                    p.BackColor = dark ? Color.FromArgb(51, 65, 85) : Color.FromArgb(243, 244, 246);
                    lbl.BackColor = p.BackColor;
                }
                catch { }
            }

            void HoverOut(object _, EventArgs __)
            {
                try
                {
                    p.BackColor = Color.Transparent;
                    lbl.BackColor = Color.Transparent;
                }
                catch { }
            }

            p.MouseEnter += HoverIn;
            p.MouseLeave += HoverOut;
            lbl.MouseEnter += HoverIn;
            lbl.MouseLeave += HoverOut;

            return p;
        }

        private static Color DashBlendRgb(Color a, Color b, float t)
        {
            float u = 1f - t;
            return Color.FromArgb(255,
                (int)Math.Round(a.R * u + b.R * t),
                (int)Math.Round(a.G * u + b.G * t),
                (int)Math.Round(a.B * u + b.B * t));
        }

        /// <summary>Полная перерисовка без стандартной обводки WinForms (убирает «белую» кайму при Flat-кнопках).</summary>
        private sealed class DashboardOutlineButton : Button
        {
            private bool _hover;

            public Color SurfaceColor { get; set; }
            public Color HoverSurfaceColor { get; set; }
            public Color PressSurfaceColor { get; set; }
            public Color OutlineColor { get; set; }

            public DashboardOutlineButton()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.DoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.Selectable,
                    true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                Cursor = Cursors.Hand;
                UseVisualStyleBackColor = false;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hover = true;
                base.OnMouseEnter(e);
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hover = false;
                base.OnMouseLeave(e);
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs mevent)
            {
                base.OnMouseDown(mevent);
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs mevent)
            {
                base.OnMouseUp(mevent);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;

                bool pressed = (MouseButtons & MouseButtons.Left) != 0 &&
                               ClientRectangle.Contains(PointToClient(MousePosition));

                Color fill;
                if (!Enabled)
                    fill = SystemColors.Control;
                else if (pressed && _hover)
                    fill = PressSurfaceColor;
                else if (_hover)
                    fill = HoverSurfaceColor;
                else
                    fill = SurfaceColor;

                using (var brush = new SolidBrush(fill))
                    g.FillRectangle(brush, ClientRectangle);

                var flags =
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoClipping |
                    TextFormatFlags.NoPrefix;

                Rectangle textRect = DeflateClientRectangleByPadding(this);
                if (textRect.Width > 0 && textRect.Height > 0)
                    TextRenderer.DrawText(
                        g,
                        Text,
                        Font,
                        textRect,
                        Enabled ? ForeColor : SystemColors.GrayText,
                        flags);

                var outline = OutlineColor;
                if (!outline.IsEmpty && Width > 1 && Height > 1)
                {
                    using (var pen = new Pen(outline, 1f))
                        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                }
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                Invalidate();
            }
        }

        private static class DashBoardGlyph
        {
            public static Bitmap Grid(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(c))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float u = size / 5f;
                    float pad = size * 0.18f;
                    g.FillRectangle(b, pad, pad, u, u);
                    g.FillRectangle(b, pad + u * 1.35f, pad, u, u);
                    g.FillRectangle(b, pad, pad + u * 1.35f, u, u);
                    g.FillRectangle(b, pad + u * 1.35f, pad + u * 1.35f, u, u);
                }

                return bmp;
            }

            public static Bitmap Clipboard(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float pad = size * 0.22f;
                    g.DrawRectangle(pen, pad, pad + size * 0.12f, size - pad * 2f, size - pad * 2f - size * 0.12f);
                    g.DrawRectangle(pen, pad + size * 0.18f, pad, size - pad * 2f - size * 0.36f, size * 0.22f);
                }

                return bmp;
            }

            public static Bitmap Clock(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float cx = size / 2f;
                    float cy = size / 2f;
                    float r = size * 0.36f;
                    g.DrawEllipse(pen, cx - r, cy - r, r * 2f, r * 2f);
                    g.DrawLine(pen, cx, cy, cx, cy - r * 0.65f);
                    g.DrawLine(pen, cx, cy, cx + r * 0.55f, cy + r * 0.15f);
                }

                return bmp;
            }

            public static Bitmap Bolt(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(c))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float ox = size * 0.34f;
                    float oy = size * 0.08f;
                    var pts = new[]
                    {
                        new PointF(ox + size * 0.08f, oy),
                        new PointF(ox + size * 0.42f, oy + size * 0.36f),
                        new PointF(ox + size * 0.22f, oy + size * 0.36f),
                        new PointF(ox + size * 0.52f, oy + size * 0.94f),
                        new PointF(ox + size * 0.18f, oy + size * 0.46f),
                        new PointF(ox + size * 0.34f, oy + size * 0.46f)
                    };
                    g.FillPolygon(b, pts);
                }

                return bmp;
            }

            public static Bitmap Info(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2f))
                using (var b = new SolidBrush(c))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float cx = size / 2f;
                    float cy = size / 2f;
                    float r = size * 0.38f;
                    g.DrawEllipse(pen, cx - r, cy - r, r * 2f, r * 2f);
                    g.FillEllipse(b, cx - size * 0.06f, cy - size * 0.26f, size * 0.12f, size * 0.12f);
                    g.FillRectangle(b, cx - size * 0.055f, cy - size * 0.08f, size * 0.11f, size * 0.30f);
                }

                return bmp;
            }

            public static Bitmap FolderGlyph(int size, Color c)
            {
                int s = Math.Max(8, size);
                var bmp = new Bitmap(s, s);
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(c))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float ox = s * 0.12f;
                    float oy = s * 0.26f;
                    float fw = s * 0.76f;
                    float fh = s * 0.52f;
                    float tab = s * 0.18f;
                    PointF[] body =
                    {
                        new PointF(ox, oy + tab),
                        new PointF(ox + fw * 0.26f, oy),
                        new PointF(ox + fw * 0.74f, oy),
                        new PointF(ox + fw, oy + tab),
                        new PointF(ox + fw, oy + fh),
                        new PointF(ox, oy + fh)
                    };
                    g.FillPolygon(b, body);
                    g.FillRectangle(b, ox + fw * 0.07f, oy + tab + fh * 0.22f, fw * 0.86f, fh * 0.42f);
                }

                return bmp;
            }

            public static Bitmap DocumentLarge(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2.4f))
                using (var path = CreateRoundedRectPathStatic(new Rectangle(4, 3, size - 8, size - 7), size / 9))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.DrawPath(pen, path);
                    g.DrawLine(pen, 10, size * 0.42f, size - 11, size * 0.42f);
                    g.DrawLine(pen, 10, size * 0.56f, size - 15, size * 0.56f);
                    g.DrawLine(pen, 10, size * 0.68f, size - 11, size * 0.68f);
                }

                return bmp;
            }

            public static Bitmap TranslateBadge(int size, Color green)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(green))
                using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (var fb = new SolidBrush(Color.White))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.FillEllipse(b, 3, 3, size - 6, size - 6);
                    g.DrawString("A", f, fb, size * 0.18f, size * 0.22f);
                    using (var fp = new Font("Segoe UI", 9f, FontStyle.Bold))
                        g.DrawString("文", fp, fb, size * 0.46f, size * 0.26f);
                }

                return bmp;
            }

            public static Bitmap DocMini(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 1.8f))
                using (var path = CreateRoundedRectPathStatic(new Rectangle(3, 2, size - 6, size - 5), size / 8))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.DrawPath(pen, path);
                }

                return bmp;
            }

            public static Bitmap Plus(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2.4f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float cx = size / 2f;
                    g.DrawLine(pen, cx - size * 0.28f, cx, cx + size * 0.28f, cx);
                    g.DrawLine(pen, cx, cx - size * 0.28f, cx, cx + size * 0.28f);
                }

                return bmp;
            }

            public static Bitmap Globe(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float cx = size / 2f;
                    float cy = size / 2f;
                    float r = size * 0.36f;
                    g.DrawEllipse(pen, cx - r, cy - r, r * 2f, r * 2f);
                    g.DrawEllipse(pen, cx - r * 0.45f, cy - r, r * 0.9f, r * 2f);
                    g.DrawLine(pen, cx - r, cy, cx + r, cy);
                    g.DrawArc(pen, cx - r * 0.9f, cy - r * 0.55f, r * 1.8f, r * 1.1f, 15, 150);
                    g.DrawArc(pen, cx - r * 0.9f, cy - r * 0.05f, r * 1.8f, r * 1.1f, 195, 150);
                }

                return bmp;
            }

            public static Bitmap Check(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2.4f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.Clear(Color.Transparent);
                    float ox = size * 0.18f;
                    float oy = size * 0.52f;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(ox, oy),
                        new PointF(ox + size * 0.18f, oy + size * 0.22f),
                        new PointF(ox + size * 0.56f, oy - size * 0.30f)
                    });
                }

                return bmp;
            }

            public static Bitmap Download(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2.2f))
                using (var b = new SolidBrush(Color.FromArgb(160, c)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float cx = size / 2f;
                    g.DrawLine(pen, cx, size * 0.22f, cx, size * 0.62f);
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - size * 0.22f, size * 0.46f),
                        new PointF(cx, size * 0.68f),
                        new PointF(cx + size * 0.22f, size * 0.46f)
                    });
                    g.FillRectangle(b, cx - size * 0.26f, size * 0.72f, size * 0.52f, size * 0.14f);
                }

                return bmp;
            }

            public static Bitmap Floppy(int size, Color c)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(c, 2f))
                using (var b = new SolidBrush(c))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    var body = new RectangleF(4, 5, size - 8, size - 9);
                    g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
                    g.FillRectangle(b, size * 0.34f, 5, size * 0.32f, size * 0.22f);
                    // стоковая Brushes.White — прежний inline new SolidBrush(Color.White) тёк GDI-хендлом на каждую иконку
                    g.FillRectangle(Brushes.White, 7, size * 0.52f, size - 14, size * 0.26f);
                }

                return bmp;
            }

            private static GraphicsPath CreateRoundedRectPathStatic(Rectangle rect, int radius)
            {
                var path = new GraphicsPath();
                if (rect.Width <= 0 || rect.Height <= 0)
                    return path;

                int r = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
                int d = r * 2;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
