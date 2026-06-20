using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    partial class Form1
    {
        private Panel navPanel, navLogoPanel, contentPanel, headerPanel, moduleHostPanel;
        private WelcomeLayeredDimPanel welcomeOverlayDimPanel;
        private Panel welcomeOverlayCardPanel;
        private bool welcomeOverlayBuilt;
        private FlowLayoutPanel navButtonsContainer;
        private Label headerLabel, appTitle;
        private readonly System.Collections.Generic.List<NavSidebarRow> navButtons = new System.Collections.Generic.List<NavSidebarRow>();
        private NavSidebarRow activeNavButton;
        internal DataGridView dgv;
        internal RichTextBox logBox;
        internal Button btnSelectFolder, btnApply, btnExportTxt, btnImportTxt, btnTranslateEmptyApi, btnDeleteJsonWithoutText, btnCopySelectedAi, btnPasteAi, btnClearLog;
        internal CheckBox chkBackup;
        internal Label progressStatsLabel;
        internal ProgressBar progressBar;
        internal ToolStripStatusLabel statusLabel;
        internal ToolStripButton btnCancelApiBatchTranslate;
        private StatusStrip statusStrip;
        private FlowLayoutPanel toolbarFlow;
        private MenuStrip mainMenuStrip;

        private Panel topChromeHost;
        private TableLayoutPanel captionChromePanel;
        private PictureBox captionIconPic;
        private FlowLayoutPanel captionButtonsFlow;
        private Button captionBtnMin;
        private Button captionBtnMax;
        private Button captionBtnClose;

        private Panel jsonWorkspaceCard;
        private Panel jsonSearchPanel;
        private TextBox jsonSearchBox;
        private Label lblJsonModuleTitle, lblActivityTitle;
        private Panel sidebarFooterPanel;
        private Label lblSidebarReady;

        private Panel _resizeGripBottomMid;
        private Panel _resizeGripRightMid;
        private Panel _resizeGripLeftMid;
        private Panel _resizeGripTl;
        private Panel _resizeGripTr;
        private Panel _resizeGripBl;
        private Panel _resizeGripBr;

        /// <summary>Толщина невидимой зоны захвата для изменения размера (borderless).</summary>
        private const int ResizeGripThickness = 10;

        public void InitializeLayout()
        {
            this.Text = "Unity Text Translator";
            this.MinimumSize = new Size(960, 620);
            this.MaximumSize = Size.Empty;
            this.ClientSize = new Size(1240, 780);
            // Обычный запуск — панель задач видна; разворот по кнопке не перекрывает панель задач.
            this.WindowState = FormWindowState.Normal;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Load += Form1_OnFirstLoadWorkingArea;

            if (savedWindowX.HasValue && savedWindowY.HasValue &&
                savedWindowWidth.HasValue && savedWindowHeight.HasValue)
            {
                var stored = new Rectangle(savedWindowX.Value, savedWindowY.Value, savedWindowWidth.Value, savedWindowHeight.Value);
                bool intersectsAnyScreen = false;
                foreach (var scr in Screen.AllScreens)
                {
                    if (scr.WorkingArea.IntersectsWith(stored))
                    {
                        intersectsAnyScreen = true;
                        break;
                    }
                }

                if (intersectsAnyScreen)
                {
                    this.StartPosition = FormStartPosition.Manual;
                    this.Bounds = stored;
                }
            }

            // Намеренно НЕ восстанавливаем максимизацию: окно открывается обычным (перемещаемым),
            // иначе приходится сначала кликать «восстановить», чтобы его подвинуть.

            this.KeyPreview = true;
            this.Font = new Font("Segoe UI", 9.75f);
            Bitmap sidebarIconBitmap;
            using (var loadedIcon = LoadFormIconSameAsExecutable())
            {
                this.Icon = (Icon)loadedIcon.Clone();
                sidebarIconBitmap = loadedIcon.ToBitmap();
            }

            BuildMainMenu();

            this.FormBorderStyle = FormBorderStyle.None;

            this.BackColor = IsDarkTheme(currentThemeName)
                ? Color.FromArgb(20, 19, 26)
                : Color.FromArgb(247, 249, 251);

            // Левая панель навигации (дашборд-макет)
            navPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 268,
                BackColor = Color.FromArgb(27, 25, 36),
                Padding = new Padding(0, 8, 0, 0)
            };

            navLogoPanel = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = navPanel.BackColor, Padding = new Padding(14, 12, 14, 12) };
            appTitle = new Label
            {
                Text = "Unity Text Translator",
                Font = PickSidebarTitleFont(17f),
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
                UseCompatibleTextRendering = false
            };
            navLogoPanel.Controls.Add(appTitle);

            // Тонкий разделитель под лого (цвет — чуть светлее фона сайдбара).
            navLogoPanel.Paint += (s, e) =>
            {
                var pnl = (Panel)s;
                var c = pnl.BackColor;
                using (var pen = new Pen(Color.FromArgb(
                    Math.Min(255, c.R + 24), Math.Min(255, c.G + 24), Math.Min(255, c.B + 30))))
                    e.Graphics.DrawLine(pen, 18, pnl.Height - 1, pnl.Width - 18, pnl.Height - 1);
            };

            navButtonsContainer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(14, 4, 14, 8),
                BackColor = navPanel.BackColor
            };

            sidebarFooterPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(18, 12, 18, 10),
                BackColor = navPanel.BackColor
            };
            lblSidebarReady = new Label
            {
                Text = "Ready",
                AutoSize = true,
                Location = new Point(34, 12),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(134, 239, 172)
            };
            sidebarFooterPanel.Controls.Add(lblSidebarReady);

            // Разделитель сверху + статус-точка цвета «готово».
            sidebarFooterPanel.Paint += (s, e) =>
            {
                var pnl = (Panel)s;
                var c = pnl.BackColor;
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(
                    Math.Min(255, c.R + 24), Math.Min(255, c.G + 24), Math.Min(255, c.B + 30))))
                    g.DrawLine(pen, 18, 0, pnl.Width - 18, 0);
                Color dot = isDarkTheme ? Color.FromArgb(74, 222, 128) : Color.FromArgb(34, 197, 94);
                using (var br = new SolidBrush(dot))
                    g.FillEllipse(br, 18, pnl.Height / 2 - 6, 9, 9);
            };

            navPanel.Controls.Add(navButtonsContainer);
            navPanel.Controls.Add(sidebarFooterPanel);
            navPanel.Controls.Add(navLogoPanel);

            // Правая область
            contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(28, 20, 28, 24),
                BackColor = Color.FromArgb(18, 18, 18)
            };

            headerPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.Transparent };
            headerLabel = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(0, 14),
                AutoSize = true,
                UseCompatibleTextRendering = false
            };
            headerPanel.Controls.Add(headerLabel);
            moduleHostPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };
            contentPanel.Controls.Add(moduleHostPanel);
            contentPanel.Controls.Add(headerPanel);

            BuildCaptionChrome(sidebarIconBitmap);

            topChromeHost = new Panel { Dock = DockStyle.Top, Height = 40 };
            captionChromePanel.Dock = DockStyle.Fill;
            topChromeHost.Controls.Add(captionChromePanel);

            // Dock Fill первым, затем Left и верхняя полоса — чтобы меню и сайдбар были поверх правильно.
            this.Controls.Add(contentPanel);
            this.Controls.Add(navPanel);
            this.Controls.Add(topChromeHost);
            this.MainMenuStrip = mainMenuStrip;

            BuildResizeEdgeGrips();

            this.Resize += (_, __) =>
            {
                UpdateCaptionMaxGlyph();
                ArrangeResizeGrips();
            };
            this.Layout += (_, __) => ArrangeResizeGrips();
        }

        private void BuildResizeEdgeGrips()
        {
            Color GripBg() => BackColor;

            void AddGrip(ref Panel slot, Cursor cursor, int ht)
            {
                slot = new Panel
                {
                    BackColor = GripBg(),
                    Cursor = cursor
                };
                slot.MouseDown += (_, e) => ResizeGrip_MouseDown(e, ht);
                Controls.Add(slot);
                slot.BringToFront();
            }

            AddGrip(ref _resizeGripBottomMid, Cursors.SizeNS, 15); // HTBOTTOM
            AddGrip(ref _resizeGripRightMid, Cursors.SizeWE, 11); // HTRIGHT
            AddGrip(ref _resizeGripLeftMid, Cursors.SizeWE, 10); // HTLEFT
            AddGrip(ref _resizeGripTl, Cursors.SizeNWSE, 13); // HTTOPLEFT
            AddGrip(ref _resizeGripTr, Cursors.SizeNESW, 14); // HTTOPRIGHT
            AddGrip(ref _resizeGripBl, Cursors.SizeNESW, 16); // HTBOTTOMLEFT
            AddGrip(ref _resizeGripBr, Cursors.SizeNWSE, 17); // HTBOTTOMRIGHT
        }

        private void ArrangeResizeGrips()
        {
            if (!IsHandleCreated)
                return;

            const int g = ResizeGripThickness;
            bool normal = WindowState == FormWindowState.Normal;
            void Show(Panel p, bool on)
            {
                if (p == null || p.IsDisposed)
                    return;
                p.Visible = on;
            }

            Show(_resizeGripBottomMid, normal);
            Show(_resizeGripRightMid, normal);
            Show(_resizeGripLeftMid, normal);
            Show(_resizeGripTl, normal);
            Show(_resizeGripTr, normal);
            Show(_resizeGripBl, normal);
            Show(_resizeGripBr, normal);

            if (!normal)
                return;

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w < g * 4 || h < g * 4)
                return;

            var bg = BackColor;
            _resizeGripBottomMid.BackColor = bg;
            _resizeGripRightMid.BackColor = bg;
            _resizeGripLeftMid.BackColor = bg;
            _resizeGripTl.BackColor = bg;
            _resizeGripTr.BackColor = bg;
            _resizeGripBl.BackColor = bg;
            _resizeGripBr.BackColor = bg;

            _resizeGripBottomMid.SetBounds(g, h - g, Math.Max(0, w - 2 * g), g);
            _resizeGripRightMid.SetBounds(w - g, g, g, Math.Max(0, h - 2 * g));
            _resizeGripLeftMid.SetBounds(0, g, g, Math.Max(0, h - 2 * g));
            _resizeGripTl.SetBounds(0, 0, g, g);
            _resizeGripTr.SetBounds(w - g, 0, g, g);
            _resizeGripBl.SetBounds(0, h - g, g, g);
            _resizeGripBr.SetBounds(w - g, h - g, g, g);

            _resizeGripBr.BringToFront();
            _resizeGripBl.BringToFront();
            _resizeGripTr.BringToFront();
            _resizeGripTl.BringToFront();
            _resizeGripRightMid.BringToFront();
            _resizeGripBottomMid.BringToFront();
            _resizeGripLeftMid.BringToFront();
        }

        private void ResizeGrip_MouseDown(MouseEventArgs e, int htHitTest)
        {
            if (e.Button != MouseButtons.Left || WindowState != FormWindowState.Normal)
                return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, htHitTest, 0);
        }

        /// <summary>При развороте окна не перекрываем панель задач (рабочая область экрана).</summary>
        private void Form1_OnFirstLoadWorkingArea(object sender, EventArgs e)
        {
            Load -= Form1_OnFirstLoadWorkingArea;
            try
            {
                MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
            }
            catch
            {
                try { MaximizedBounds = Screen.PrimaryScreen.WorkingArea; }
                catch { /* игнор */ }
            }
        }

        private void BuildCaptionChrome(Bitmap sidebarIconBitmap)
        {
            captionChromePanel = new TableLayoutPanel
            {
                Height = 40,
                MinimumSize = new Size(0, 38),
                MaximumSize = new Size(0, 40),
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(0),
                Margin = new Padding(0),
                BackColor = Color.FromArgb(24, 22, 32),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            captionChromePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42f));
            captionChromePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            captionChromePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            captionChromePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));

            captionButtonsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Padding = new Padding(0, 4, 8, 4),
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };

            Font CaptionGlyphFont()
            {
                try { return new Font("Segoe MDL2 Assets", 10f); }
                catch { return new Font("Segoe UI", 11f); }
            }

            var glyphFont = CaptionGlyphFont();

            captionBtnClose = CreateCaptionChromeButton(glyphFont, "\uE8BB");
            captionBtnMax = CreateCaptionChromeButton(glyphFont, "\uE922");
            captionBtnMin = CreateCaptionChromeButton(glyphFont, "\uE921");

            captionButtonsFlow.Controls.Add(captionBtnClose);
            captionButtonsFlow.Controls.Add(captionBtnMax);
            captionButtonsFlow.Controls.Add(captionBtnMin);

            captionIconPic = new PictureBox
            {
                Size = new Size(22, 22),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Image = new Bitmap(sidebarIconBitmap, new Size(22, 22)),
                Margin = new Padding(10, 8, 4, 4),
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            };

            mainMenuStrip.Dock = DockStyle.Fill;
            mainMenuStrip.Padding = new Padding(2, 2, 0, 2);
            mainMenuStrip.Margin = new Padding(0);
            mainMenuStrip.GripStyle = ToolStripGripStyle.Hidden;

            captionChromePanel.Controls.Add(captionIconPic, 0, 0);
            captionChromePanel.Controls.Add(mainMenuStrip, 1, 0);
            captionChromePanel.Controls.Add(captionButtonsFlow, 2, 0);

            captionChromePanel.MouseDown += CaptionChrome_StartDrag;
            captionChromePanel.DoubleClick += CaptionChrome_ToggleMaximize;
            captionIconPic.MouseDown += CaptionChrome_StartDrag;
            captionIconPic.DoubleClick += CaptionChrome_ToggleMaximize;
            mainMenuStrip.MouseDown += CaptionChrome_StartDrag;
            mainMenuStrip.DoubleClick += CaptionChrome_ToggleMaximize;

            captionBtnClose.Click += (_, __) => Close();
            captionBtnMin.Click += (_, __) => { WindowState = FormWindowState.Minimized; };
            captionBtnMax.Click += (_, __) => ToggleCaptionWindowState();

            void HoverBtnEnter(object s, EventArgs __)
            {
                if (s is Button b && !b.IsDisposed)
                    b.BackColor = Color.FromArgb(55, 55, 62);
            }

            void HoverBtnLeave(object s, EventArgs __)
            {
                if (s is Button b && !b.IsDisposed)
                    b.BackColor = Color.Transparent;
            }

            captionBtnMin.MouseEnter += HoverBtnEnter;
            captionBtnMin.MouseLeave += HoverBtnLeave;
            captionBtnMax.MouseEnter += HoverBtnEnter;
            captionBtnMax.MouseLeave += HoverBtnLeave;
            captionBtnClose.MouseEnter += (s, __) =>
            {
                if (captionBtnClose != null && !captionBtnClose.IsDisposed)
                    captionBtnClose.BackColor = Color.FromArgb(232, 17, 35);
            };
            captionBtnClose.MouseLeave += HoverBtnLeave;
        }

        private static Button CreateCaptionChromeButton(Font font, string text)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(46, 32),
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                Cursor = Cursors.Hand,
                Font = font,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0),
                TextAlign = ContentAlignment.MiddleCenter,
                UseCompatibleTextRendering = false
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void BuildMainMenu()
        {
            mainMenuStrip = new MenuStrip
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(4, 2, 0, 2),
                Stretch = false
            };

            ToolStripMenuItem Mi(string menuKey, string shortcutDisplay, EventHandler onClick)
            {
                var it = new ToolStripMenuItem(MainMenuText(menuKey), null, onClick) { Tag = menuKey };
                if (!string.IsNullOrEmpty(shortcutDisplay))
                    it.ShortcutKeyDisplayString = shortcutDisplay;
                return it;
            }

            ToolStripMenuItem Root(string menuKey)
            {
                return new ToolStripMenuItem(MainMenuText(menuKey)) { Tag = menuKey };
            }

            var fileMenu = Root("m_file");
            fileMenu.DropDownItems.Add(Mi("file_refresh", "F5", (s, e) => HotkeyRefreshExtract()));
            fileMenu.DropDownItems.Add(Mi("file_choose_folder", "Ctrl+O", (s, e) => BtnSelectFolder_Click(s, e)));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(Mi("file_save_json", "Ctrl+S", (s, e) => BtnApply_Click(s, e)));
            _autosaveMenuItem = new ToolStripMenuItem(MainMenuText("file_autosave"))
            {
                Tag = "file_autosave",
                CheckOnClick = true,
                Checked = autosaveEnabled
            };
            _autosaveMenuItem.CheckedChanged += (s, e) =>
            {
                autosaveEnabled = _autosaveMenuItem.Checked;
                SaveSettings();
                ApplyAutosaveSetting();
                Log(autosaveEnabled
                    ? L("Autosave enabled (every 2 min).", "Автосохранение включено (каждые 2 мин).")
                    : L("Autosave disabled.", "Автосохранение выключено."));
            };
            fileMenu.DropDownItems.Add(_autosaveMenuItem);
            fileMenu.DropDownItems.Add(Mi("file_export_assets_json", null, (s, e) => BtnExportFromAssets_Click(s, e)));
            fileMenu.DropDownItems.Add(Mi("file_import_assets", null, (s, e) => BtnImportAsset_Click(s, e)));
            fileMenu.DropDownItems.Add(Mi("file_export_txt", "Ctrl+E", (s, e) => BtnExportTxt_Click(s, e)));
            fileMenu.DropDownItems.Add(Mi("file_import_txt", "Ctrl+I", (s, e) => BtnImportTxt_Click(s, e)));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(Mi("file_tools", null, (s, e) =>
            {
                ActivateNavByTag("Bundles");
                LoadBundleLocalizationModule();
            }));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(Mi("file_exit", "Alt+F4", (s, e) => Close()));

            var editMenu = Root("m_edit");
            editMenu.DropDownItems.Add(Mi("edit_copy_ai", "Ctrl+Shift+C", (s, e) => BtnCopySelectedAi_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_paste_buffer", "Ctrl+Shift+V", (s, e) => BtnPasteAi_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_search_table", "Ctrl+F", (s, e) => ShowJsonTableSearchDialog()));
            editMenu.DropDownItems.Add(Mi("edit_find_next", "F3", (s, e) => FindNextTableSearchMatch()));
            editMenu.DropDownItems.Add(Mi("edit_refresh_table", "F5", (s, e) => HotkeyRefreshExtract()));
            editMenu.DropDownItems.Add(Mi("edit_next_untranslated", "Ctrl+]", (s, e) => NavigateToRelativeUntranslated(1)));
            editMenu.DropDownItems.Add(Mi("edit_prev_untranslated", "Ctrl+[", (s, e) => NavigateToRelativeUntranslated(-1)));
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add(Mi("edit_translate_api", "Ctrl+Shift+T", (s, e) => MenuTranslateEmptyViaLocalApi_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_apply_tm", null, (s, e) => BtnApplyTranslationMemory_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_resync_patch", null, (s, e) => MenuResyncAfterPatch_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_qa_check", null, (s, e) => MenuRunQaCheck_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_delete_meta_json", null, (s, e) => BtnDeleteMetadataOnlyJson_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_clear_working_folder", null, (s, e) => MenuClearWorkingJsonFolder_Click(s, e)));
            editMenu.DropDownItems.Add(Mi("edit_clear_log", "Ctrl+Shift+L", (s, e) => BtnClearLog_Click(s, e)));

            var viewMenu = Root("m_view");
            var lightThemesMenu = new ToolStripMenuItem(MainMenuText("view_light_themes")) { Tag = "view_light_themes" };
            var darkThemesMenu = new ToolStripMenuItem(MainMenuText("view_dark_themes")) { Tag = "view_dark_themes" };
            AddThemeMenuItem(lightThemesMenu, "GitHub Light");
            AddThemeMenuItem(lightThemesMenu, "Solarized Light");
            AddThemeMenuItem(darkThemesMenu, "Translator Purple");
            AddThemeMenuItem(darkThemesMenu, "GitHub Dark");
            AddThemeMenuItem(darkThemesMenu, "Visual Studio Dark");
            AddThemeMenuItem(darkThemesMenu, "Dracula");
            AddThemeMenuItem(darkThemesMenu, "Nord");
            viewMenu.DropDownItems.Add(lightThemesMenu);
            viewMenu.DropDownItems.Add(darkThemesMenu);

            var helpMenu = Root("m_help");
            helpMenu.DropDownItems.Add(Mi("help_guide", "F1", (s, e) => ShowUserGuideDialog()));
            helpMenu.DropDownItems.Add(Mi("help_about", null, (s, e) => ShowAboutDialog()));

            mainMenuStrip.Items.Add(fileMenu);
            mainMenuStrip.Items.Add(editMenu);
            mainMenuStrip.Items.Add(viewMenu);
            mainMenuStrip.Items.Add(helpMenu);

            ApplyAutosaveSetting();
        }

        private void AddThemeMenuItem(ToolStripMenuItem parentMenu, string themeName)
        {
            parentMenu.DropDownItems.Add(themeName, null, (s, e) =>
            {
                currentThemeName = themeName;
                isDarkTheme = IsDarkTheme(currentThemeName);
                ApplyTheme();
                SaveSettings();
                Log(L($"Theme changed: {currentThemeName}", $"Тема изменена: {currentThemeName}"));
            });
        }

        private void ShowAboutDialog()
        {
            using (var aboutForm = new Form())
            {
                aboutForm.Text = "О программе";
                aboutForm.StartPosition = FormStartPosition.CenterParent;
                aboutForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                aboutForm.MaximizeBox = false;
                aboutForm.MinimizeBox = false;
                aboutForm.ClientSize = new Size(360, 170);
                aboutForm.BackColor = Color.White;
                aboutForm.Font = new Font("Segoe UI", 10f);

                var titleLabel = new Label
                {
                    Text = "Unity Text Translator",
                    Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                    AutoSize = true,
                    Location = new Point(22, 20)
                };

                var descLabel = new Label
                {
                    Text = "Инструмент для перевода Unity JSON.",
                    AutoSize = true,
                    Location = new Point(24, 56)
                };

                var authorPrefixLabel = new Label
                {
                    Text = "Автор:",
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    AutoSize = true,
                    Location = new Point(24, 88)
                };

                var authorNameLabel = new Label
                {
                    Text = "Redonkym",
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    AutoSize = true,
                    Location = new Point(82, 88)
                };

                var boostyLink = new LinkLabel
                {
                    Text = "Boosty",
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    AutoSize = true,
                    LinkColor = Color.FromArgb(37, 99, 235),
                    ActiveLinkColor = Color.FromArgb(29, 78, 216),
                    Location = new Point(158, 88)
                };
                boostyLink.LinkClicked += (s, e) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "https://boosty.to/redonkym",
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        Clipboard.SetText("https://boosty.to/redonkym");
                        MessageBox.Show("Ссылка скопирована в буфер обмена.", "Boosty", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                };

                var closeButton = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Width = 80,
                    Height = 30,
                    Location = new Point(258, 128)
                };

                aboutForm.Controls.Add(titleLabel);
                aboutForm.Controls.Add(descLabel);
                aboutForm.Controls.Add(authorPrefixLabel);
                aboutForm.Controls.Add(authorNameLabel);
                aboutForm.Controls.Add(boostyLink);
                aboutForm.Controls.Add(closeButton);
                aboutForm.AcceptButton = closeButton;
                aboutForm.ShowDialog(this);
            }
        }

        private void ShowUserGuideDialog()
        {
            Color formBg = _themePageBg;
            Color cardBg = ThemeCardSurface();
            Color titleFg = _themeHeaderText;
            Color bodyFg = _themeGridRowFore;
            Color mutedFg = _themeSubtitleText;
            Color sectionAccent = DashboardAccentPrimary();

            var sections = ParseGuideSections(UiIsRussian ? UserGuideBodyRu() : UserGuideBodyEn());

            using (var guideForm = new Form())
            {
                guideForm.Text = L("User guide — Unity Text Translator", "Помощь — Unity Text Translator");
                guideForm.StartPosition = FormStartPosition.CenterParent;
                guideForm.MinimizeBox = false;
                guideForm.ShowIcon = false;
                guideForm.ClientSize = new Size(760, 620);
                guideForm.MinimumSize = new Size(560, 460);
                guideForm.BackColor = formBg;
                guideForm.ForeColor = titleFg;
                ApplyThemedTitleBar(guideForm);

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 3,
                    BackColor = formBg,
                };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));

                // ---- Header band with accent gradient ----
                var header = new Panel { Dock = DockStyle.Fill, BackColor = cardBg, Margin = new Padding(0) };
                header.Paint += (_, e) =>
                {
                    var r = header.ClientRectangle;
                    if (r.Width <= 0 || r.Height <= 0)
                        return;
                    using (var br = new LinearGradientBrush(
                               new Rectangle(0, 0, r.Width, 4),
                               sectionAccent,
                               ThemeMix(sectionAccent, Color.White, 0.35),
                               LinearGradientMode.Horizontal))
                        e.Graphics.FillRectangle(br, new Rectangle(0, 0, r.Width, 4));
                };
                var headerTitle = new Label
                {
                    AutoSize = true,
                    Text = L("User guide", "Помощь по приложению"),
                    Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                    ForeColor = titleFg,
                    BackColor = Color.Transparent,
                    Location = new Point(24, 22),
                };
                var headerSub = new Label
                {
                    AutoSize = true,
                    Text = L("Everything in Unity Text Translator, section by section.",
                             "Всё об Unity Text Translator — раздел за разделом."),
                    Font = new Font("Segoe UI", 9.75f),
                    ForeColor = mutedFg,
                    BackColor = Color.Transparent,
                    Location = new Point(25, 56),
                };
                header.Controls.Add(headerTitle);
                header.Controls.Add(headerSub);

                // ---- Scrollable section cards ----
                var scrollHost = new Panel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true,
                    BackColor = formBg,
                    Padding = new Padding(20, 16, 20, 8),
                };
                var stack = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = formBg,
                    Margin = new Padding(0),
                };

                var cardWidthBindings = new List<Action<int>>();
                foreach (var sec in sections)
                {
                    var card = new Panel
                    {
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        BackColor = cardBg,
                        Margin = new Padding(0, 0, 0, 12),
                        Padding = new Padding(0),
                    };

                    var accentStrip = new Panel
                    {
                        Width = 4,
                        Dock = DockStyle.Left,
                        BackColor = sectionAccent,
                    };

                    var pad = new Panel
                    {
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        Dock = DockStyle.Top,
                        BackColor = cardBg,
                        Padding = new Padding(18, 14, 18, 14),
                        Location = new Point(4, 0),
                    };

                    var inner = new FlowLayoutPanel
                    {
                        FlowDirection = FlowDirection.TopDown,
                        WrapContents = false,
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        BackColor = cardBg,
                        Margin = new Padding(0),
                    };

                    var secTitle = new Label
                    {
                        AutoSize = true,
                        Text = sec.Title,
                        Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                        ForeColor = titleFg,
                        BackColor = cardBg,
                        Margin = new Padding(0, 0, 0, 8),
                    };
                    var secBody = new Label
                    {
                        AutoSize = true,
                        Text = sec.Body,
                        Font = new Font("Segoe UI", 9.75f),
                        ForeColor = bodyFg,
                        BackColor = cardBg,
                        Margin = new Padding(0),
                    };

                    inner.Controls.Add(secTitle);
                    inner.Controls.Add(secBody);
                    pad.Controls.Add(inner);
                    card.Controls.Add(pad);
                    card.Controls.Add(accentStrip);
                    stack.Controls.Add(card);

                    // Bind width on resize so text wraps and no horizontal scroll appears.
                    cardWidthBindings.Add(w =>
                    {
                        card.MinimumSize = new Size(w, 0);
                        card.MaximumSize = new Size(w, 0);
                        int textW = w - accentStrip.Width - pad.Padding.Horizontal;
                        secTitle.MaximumSize = new Size(textW, 0);
                        secBody.MaximumSize = new Size(textW, 0);
                    });
                }

                scrollHost.Controls.Add(stack);

                void RelayoutGuideCards()
                {
                    int w = scrollHost.ClientSize.Width
                            - scrollHost.Padding.Horizontal
                            - SystemInformation.VerticalScrollBarWidth;
                    if (w < 200)
                        w = 200;
                    foreach (var bind in cardWidthBindings)
                        bind(w);
                }
                scrollHost.Resize += (_, __) => RelayoutGuideCards();

                // ---- Footer ----
                var footer = new Panel { Dock = DockStyle.Fill, BackColor = formBg, Margin = new Padding(0) };
                var btnClose = new Button
                {
                    Text = L("Close", "Закрыть"),
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                    ForeColor = Color.White,
                    BackColor = sectionAccent,
                    Size = new Size(120, 34),
                    Anchor = AnchorStyles.Right,
                    DialogResult = DialogResult.OK,
                };
                btnClose.FlatAppearance.BorderSize = 0;
                btnClose.Location = new Point(guideForm.ClientSize.Width - 120 - 20, 11);
                footer.Controls.Add(btnClose);

                layout.Controls.Add(header, 0, 0);
                layout.Controls.Add(scrollHost, 0, 1);
                layout.Controls.Add(footer, 0, 2);

                guideForm.Controls.Add(layout);
                guideForm.AcceptButton = btnClose;
                guideForm.Shown += (_, __) =>
                {
                    ApplyThemedScrollBars(guideForm);
                    RelayoutGuideCards();
                };
                guideForm.ShowDialog(this);
            }
        }

        /// <summary>Разбивает текст руководства на секциии: первая строка блока — заголовок, остальное — тело.</summary>
        private static List<(string Title, string Body)> ParseGuideSections(string raw)
        {
            var list = new List<(string, string)>();
            if (string.IsNullOrEmpty(raw))
                return list;

            foreach (var block in raw.Split(new[] { "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int nl = block.IndexOf("\r\n", StringComparison.Ordinal);
                if (nl < 0)
                {
                    list.Add((block.Trim(), ""));
                    continue;
                }
                string title = block.Substring(0, nl).Trim();
                string body = block.Substring(nl + 2).Trim();
                list.Add((title, body));
            }
            return list;
        }

        private void ShowWelcomeOverlay()
        {
            EnsureWelcomeOverlayBuilt();
            SetNavLockedForWelcome(true);
            welcomeOverlayDimPanel.Visible = true;
            welcomeOverlayCardPanel.Visible = true;
            welcomeOverlayDimPanel.BringToFront();
            welcomeOverlayCardPanel.BringToFront();
            PositionWelcomeOverlayCard();
        }

        private void SetNavLockedForWelcome(bool locked)
        {
            foreach (var row in navButtons)
            {
                if (row != null && !row.IsDisposed)
                    row.Enabled = !locked;
            }

            if (mainMenuStrip != null && !mainMenuStrip.IsDisposed)
                mainMenuStrip.Enabled = !locked;
        }

        private void DismissWelcomeOverlayAndPersist()
        {
            welcomeShown = true;
            SaveSettings();
            if (welcomeOverlayDimPanel != null && !welcomeOverlayDimPanel.IsDisposed)
                welcomeOverlayDimPanel.Visible = false;
            if (welcomeOverlayCardPanel != null && !welcomeOverlayCardPanel.IsDisposed)
                welcomeOverlayCardPanel.Visible = false;
            SetNavLockedForWelcome(false);
        }

        private void PositionWelcomeOverlayCard()
        {
            if (contentPanel == null || contentPanel.IsDisposed ||
                welcomeOverlayCardPanel == null || welcomeOverlayCardPanel.IsDisposed)
                return;

            const int pad = 12;
            Rectangle area = contentPanel.DisplayRectangle;
            welcomeOverlayCardPanel.Left = area.Left + Math.Max(pad, (area.Width - welcomeOverlayCardPanel.Width) / 2);
            welcomeOverlayCardPanel.Top = area.Top + Math.Max(pad, (area.Height - welcomeOverlayCardPanel.Height) / 2);
        }

        /// <summary>Шрифт заголовка сайдбара — реально установленный системный (без встроенного файла).</summary>
        private static Font PickSidebarTitleFont(float size)
        {
            foreach (var name in new[] { "Segoe UI Semibold", "Segoe UI" })
            {
                try
                {
                    var style = name.IndexOf("Semibold", StringComparison.OrdinalIgnoreCase) >= 0
                        ? FontStyle.Regular
                        : FontStyle.Bold;
                    var f = new Font(name, size, style);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                        return f;
                    f.Dispose();
                }
                catch { /* следующий вариант */ }
            }
            return new Font("Segoe UI", size, FontStyle.Bold);
        }

        /// <summary>Вертикальное центрирование текста на кнопке без лишнего смещения (GDI).</summary>
        private static void ApplyWelcomeActionButtonStyle(Button b, int heightPx, int horizontalPaddingPx, int minWidthPx)
        {
            b.UseCompatibleTextRendering = false;
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.AutoSize = false;
            b.Padding = Padding.Empty;
            b.Height = heightPx;
            int tw = TextRenderer.MeasureText(b.Text, b.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
            b.Width = Math.Max(minWidthPx, tw + horizontalPaddingPx * 2);
        }

        /// <summary>Поверхность карточки для текущей темы (приподнята над фоном страницы).</summary>
        private Color ThemeCardSurface() =>
            currentThemeName == "Translator Purple" ? Color.FromArgb(36, 33, 48)
            : isDarkTheme ? Color.FromArgb(30, 41, 59)
            : Color.White;

        /// <summary>Линейное смешивание двух цветов (t=0 → a, t=1 → b).</summary>
        private static Color ThemeMix(Color a, Color b, double t)
        {
            int Ch(int x, int y) => Math.Max(0, Math.Min(255, (int)Math.Round(x + (y - x) * t)));
            return Color.FromArgb(Ch(a.R, b.R), Ch(a.G, b.G), Ch(a.B, b.B));
        }

        private void EnsureWelcomeOverlayBuilt()
        {
            if (welcomeOverlayBuilt)
                return;
            welcomeOverlayBuilt = true;

            Color cardBg = ThemeCardSurface();
            Color bodyText = _themeGridRowFore;
            Color titleText = _themeHeaderText;
            Color accentPrimary = DashboardAccentPrimary();
            Color ghostBg = ThemeMix(cardBg, isDarkTheme ? Color.White : Color.Black, 0.08);
            Color ghostBorder = ThemeMix(cardBg, isDarkTheme ? Color.White : Color.Black, 0.20);

            welcomeOverlayDimPanel = new WelcomeLayeredDimPanel(168) { Visible = false };
            contentPanel.Controls.Add(welcomeOverlayDimPanel);

            var card = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = cardBg,
                Margin = new Padding(12),
                Visible = false,
                Anchor = AnchorStyles.None,
            };
            welcomeOverlayCardPanel = card;
            welcomeOverlayCardPanel.SizeChanged += (_, __) => PositionWelcomeOverlayCard();
            contentPanel.Resize += (_, __) => PositionWelcomeOverlayCard();
            ApplyWelcomeCardRoundedCorners(card, 10);

            var grid = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                BackColor = cardBg,
                Margin = new Padding(0),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var accentStrip = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = cardBg };
            accentStrip.Paint += (_, e) =>
            {
                var r = accentStrip.ClientRectangle;
                if (r.Width <= 0 || r.Height <= 0)
                    return;

                using (var br = new LinearGradientBrush(
                           r,
                           accentPrimary,
                           ThemeMix(accentPrimary, Color.White, 0.35),
                           LinearGradientMode.Vertical))
                    e.Graphics.FillRectangle(br, r);
            };

            var contentPad = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = cardBg,
                Padding = new Padding(22, 18, 22, 16),
            };

            const int welcomeInnerWidth = 440;
            Color tagText = _themeSubtitleText;
            Color eyebrowText = ThemeMix(accentPrimary, isDarkTheme ? Color.White : Color.Black, 0.20);

            var contentGrid = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = cardBg,
                Margin = new Padding(0),
            };

            var eyebrow = new Label
            {
                AutoSize = true,
                Text = L("GETTING STARTED", "С ЧЕГО НАЧАТЬ"),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = eyebrowText,
                BackColor = cardBg,
                Margin = new Padding(0, 2, 0, 4),
            };

            var title = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(welcomeInnerWidth, 0),
                Text = L("Welcome to UTT", "Добро пожаловать в UTT"),
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = titleText,
                BackColor = cardBg,
                Margin = new Padding(0, 0, 0, 4),
            };

            var tagline = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(welcomeInnerWidth, 0),
                Text = L("Localize Unity games — export strings to JSON, translate, and write them back.",
                         "Локализация игр Unity — экспорт строк в JSON, перевод и запись обратно."),
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = bodyText,
                BackColor = cardBg,
                Margin = new Padding(0, 0, 0, 18),
            };

            Panel MakeWelcomeFeatureRow(int n, string name, string desc)
            {
                var row = new TableLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 2,
                    RowCount = 1,
                    BackColor = cardBg,
                    Margin = new Padding(0, 0, 0, 12),
                };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36f));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                var chip = new Label
                {
                    Text = n.ToString(),
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = Color.White,
                    BackColor = accentPrimary,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = new Size(26, 26),
                    Margin = new Padding(0, 1, 10, 0),
                    UseCompatibleTextRendering = false,
                };
                ApplyWelcomeCardRoundedCorners(chip, 7);

                var text = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    BackColor = cardBg,
                    Margin = new Padding(0),
                };
                text.Controls.Add(new Label
                {
                    AutoSize = true,
                    Text = name,
                    Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                    ForeColor = titleText,
                    BackColor = cardBg,
                    Margin = new Padding(0, 4, 0, 0),
                });
                text.Controls.Add(new Label
                {
                    AutoSize = true,
                    Text = "  —  " + desc,
                    Font = new Font("Segoe UI", 10.5f),
                    ForeColor = bodyText,
                    BackColor = cardBg,
                    Margin = new Padding(0, 4, 0, 0),
                });

                row.Controls.Add(chip, 0, 0);
                row.Controls.Add(text, 1, 0);
                return row;
            }

            var features = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = cardBg,
                Margin = new Padding(0, 0, 0, 6),
            };
            features.Controls.Add(MakeWelcomeFeatureRow(1,
                L("JSON Files", "JSON-файлы"),
                L("edit dumps & translate in a table", "правка дампов и перевод в таблице")));
            features.Controls.Add(MakeWelcomeFeatureRow(2,
                L("Unity .assets", "Unity .assets"),
                L("export / import game strings", "экспорт и импорт строк игры")));
            features.Controls.Add(MakeWelcomeFeatureRow(3,
                L("Fonts", "Шрифты"),
                L("Cyrillic glyphs for TextMeshPro", "кириллица для TextMeshPro")));

            var hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(welcomeInnerWidth, 0),
                Text = L("Pick a folder in JSON Files, then open Settings for theme, languages and a translation API.\r\nFull reference: Help → User guide… or F1.",
                         "Откройте папку в разделе JSON-файлы, затем Настройки — тема, языки и API перевода.\r\nПодробности: Справка → Помощь по приложению… или F1."),
                Font = new Font("Segoe UI", 9.25f),
                ForeColor = tagText,
                BackColor = cardBg,
                Margin = new Padding(0, 6, 0, 18),
            };

            var footer = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = false,
                Size = new Size(welcomeInnerWidth, 46),
                Padding = new Padding(0, 2, 0, 0),
                BackColor = cardBg,
                Margin = new Padding(0),
            };

            var btnOk = new Button
            {
                Text = L("Get started", "Начать"),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = accentPrimary,
                Margin = new Padding(12, 0, 0, 0),
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (_, __) => DismissWelcomeOverlayAndPersist();
            ApplyWelcomeActionButtonStyle(btnOk, heightPx: 40, horizontalPaddingPx: 22, minWidthPx: 96);

            var btnGuide = new Button
            {
                Text = L("User guide…", "Помощь по приложению…"),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(226, 232, 240),
                BackColor = ghostBg,
                Margin = new Padding(0, 0, 0, 0),
            };
            btnGuide.FlatAppearance.BorderSize = 1;
            btnGuide.FlatAppearance.BorderColor = ghostBorder;
            btnGuide.Click += (_, __) => ShowUserGuideDialog();
            ApplyWelcomeActionButtonStyle(btnGuide, heightPx: 40, horizontalPaddingPx: 20, minWidthPx: 220);

            footer.Controls.Add(btnOk);
            footer.Controls.Add(btnGuide);

            contentGrid.Controls.Add(eyebrow);
            contentGrid.Controls.Add(title);
            contentGrid.Controls.Add(tagline);
            contentGrid.Controls.Add(features);
            contentGrid.Controls.Add(hint);
            contentGrid.Controls.Add(footer);

            contentPad.Controls.Add(contentGrid);
            grid.Controls.Add(accentStrip, 0, 0);
            grid.Controls.Add(contentPad, 1, 0);
            card.Controls.Add(grid);

            contentPanel.Controls.Add(card);
            welcomeOverlayDimPanel.BringToFront();
            card.BringToFront();
        }

        /// <summary>Скругление карточки приветствия (стиль «B»).</summary>
        private static void ApplyWelcomeCardRoundedCorners(Control control, int radius)
        {
            void Update(object sender, EventArgs e)
            {
                if (control.Width < radius * 2 || control.Height < radius * 2)
                    return;

                using (var path = new GraphicsPath())
                {
                    RectangleF rf = control.ClientRectangle;
                    float d = radius * 2f;
                    path.AddArc(rf.X, rf.Y, d, d, 180, 90);
                    path.AddArc(rf.Right - d, rf.Y, d, d, 270, 90);
                    path.AddArc(rf.Right - d, rf.Bottom - d, d, d, 0, 90);
                    path.AddArc(rf.X, rf.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();
                    control.Region = new Region(path);
                }
            }

            control.HandleCreated += Update;
            control.SizeChanged += Update;
        }

        private static string UserGuideBodyEn()
        {
            return
                "WHAT THIS APP DOES\r\n" +
                "Unity Text Translator extracts MonoBehaviour text fields from Unity .assets containers into JSON files, lets you edit translations in a table, then merges changes back into .assets. You can also exchange work as TXT tables.\r\n\r\n" +
                "LEFT SIDEBAR (SECTIONS)\r\n" +
                "• Home — dashboard: quick overview of the current folder and shortcuts.\r\n" +
                "• JSON Files — main workspace: pick a folder with JSON dumps, edit Original / Translation in the grid, save.\r\n" +
                "• Unity .assets — point at the game *_Data folder, export all MonoBehaviour strings to a JSON folder, then after editing use Import to write back into a chosen .assets file.\r\n" +
                "• Fonts — add Cyrillic (or other missing) glyphs to a game's TextMeshPro font so translated text isn't blank; a 4-step wizard that works on IL2CPP builds.\r\n" +
                "• Bundles — export Addressables / localization .bundle files to UABEA-style JSON and pack them back.\r\n" +
                "• Settings — theme, UI language, source/target languages for labels, translation API (LibreTranslate or AI backends), TXT export format, translation memory (TM).\r\n\r\n" +
                "TYPICAL FLOW (JSON FILES)\r\n" +
                "1) Get JSON either from «Unity .assets → Export JSON» or from your own pipeline.\r\n" +
                "2) Open JSON Files → Folder — choose the folder that contains the .json files.\r\n" +
                "3) Edit the Translation column (double-click cells).\r\n" +
                "4) Save — toolbar «Save changes» or File → Save changes to JSON (Ctrl+S).\r\n" +
                "5) Export TXT / Import TXT — round-trip with spreadsheets or translators; default column format is configured in Settings.\r\n" +
                "6) Translate empty rows — «AI translation» after enabling the API in Settings (LibreTranslate server URL or an AI provider with Base URL, optional API key, chat model).\r\n\r\n" +
                "UNITY .assets MODULE\r\n" +
                "• Browse Unity Data — select the game's *_Data directory (level0, sharedassets*, resources…).\r\n" +
                "• Export JSON — writes MonoBehaviour data from every relevant .assets into the folder you choose; that folder becomes the JSON Files working directory.\r\n" +
                "• Import into .assets — applies edited JSON into the container you select (from the list or file dialog).\r\n" +
                "• Optional: export only the highlighted container row to JSON.\r\n\r\n" +
                "FONTS MODULE (CYRILLIC IN TextMeshPro)\r\n" +
                "If translated text shows as blank boxes, the game's TMP font has no Cyrillic glyphs. A left-to-right wizard rebuilds it (works on IL2CPP):\r\n" +
                "1) Analyze .assets — pick the .assets that holds the TMP_FontAsset (e.g. resources.assets); detects the font PathID and atlas size.\r\n" +
                "2) Create atlas (TTF) — choose a TTF/OTF that contains Cyrillic (e.g. arial.ttf); generates a new MSDF atlas.\r\n" +
                "3) Patch (grow Cyrillic) — rebuilds the font's glyph/character tables with the added letters.\r\n" +
                "4) Apply to game — replaces the original .assets (a .bak backup is created first).\r\n\r\n" +
                "SETTINGS — TRANSLATION API\r\n" +
                "Choose provider (LibreTranslate, OpenRouter, OpenAI, Groq, Ollama, custom OpenAI-compatible URL, …). Base URL is suggested per provider but editable.\r\n" +
                "Chat model field — editable combo: open the dropdown and type to filter the list; Refresh loads models from the server when supported.\r\n" +
                "API key — masked by default; the eye button toggles visibility. Not required for Ollama / Custom in many setups.\r\n" +
                "Translation memory — optional JSON-backed TM used in JSON Files (see the TM card for paths and behaviour).\r\n\r\n" +
                "MENU & SHORTCUTS (REFERENCE)\r\n" +
                "File — Ctrl+O choose folder, Ctrl+S save JSON, Ctrl+E export TXT, Ctrl+I import TXT; entries for .assets export/import; Bundles (JSON↔.bundle); Exit.\r\n" +
                "Edit — copy/paste for external AI, Ctrl+F find in table, Esc clears search filter when not editing a cell, F3 next match, F5 reload table from JSON, jump between rows with empty translation, AI translation via API (Ctrl+Shift+T), TM apply, clear log (Ctrl+Shift+L).\r\n" +
                "View — light/dark themes.\r\n" +
                "Help — this guide (F1), About.\r\n\r\n" +
                "TIP\r\n" +
                "After changing UI language in Settings, menu labels update immediately; the guide text follows the same language.";
        }

        private static string UserGuideBodyRu()
        {
            return
                "НАЗНАЧЕНИЕ\r\n" +
                "Unity Text Translator вытаскивает текстовые поля MonoBehaviour из контейнеров Unity .assets в JSON, позволяет править переводы в таблице и записывает изменения обратно в .assets. Есть обмен через TXT-таблицы.\r\n\r\n" +
                "БОКОВАЯ ПАНЕЛЬ (РАЗДЕЛЫ)\r\n" +
                "• Главная — дашборд: обзор текущей папки и быстрые действия.\r\n" +
                "• JSON-файлы — основная работа: выберите папку с JSON-дампами, редактируйте столбцы оригинала и перевода в таблице, сохраняйте.\r\n" +
                "• Unity .assets — укажите папку *_Data игры, экспортируйте строки MonoBehaviour в JSON; после правок — импорт выбранного .assets.\r\n" +
                "• Шрифты — добавить кириллицу (или другие недостающие глифы) в шрифт TextMeshPro игры, чтобы перевод не отображался пустыми квадратами; мастер из 4 шагов, работает на сборках IL2CPP.\r\n" +
                "• Бандлы — экспорт .bundle (Addressables / локализация) в JSON в стиле UABEA и сборка обратно.\r\n" +
                "• Настройки — тема, язык интерфейса, языки подписей к колонкам, API перевода (LibreTranslate или ИИ), формат TXT, память переводов (TM).\r\n\r\n" +
                "ТИПИЧНЫЙ СЦЕНАРИЙ (JSON-ФАЙЛЫ)\r\n" +
                "1) Получите JSON через «Unity .assets → Экспорт JSON» или свой пайплайн.\r\n" +
                "2) Раздел JSON-файлы → «Папка» — укажите каталог с .json.\r\n" +
                "3) Правьте столбец перевода (двойной щелчок по ячейке).\r\n" +
                "4) Сохранение — «Сохранить изменения» или Файл → Сохранить изменения в JSON (Ctrl+S).\r\n" +
                "5) Экспорт / импорт TXT — обмен с таблицами; формат по умолчанию задаётся в Настройках.\r\n" +
                "6) «Перевод с ИИ» — после включения API в Настройках (LibreTranslate или ИИ: URL, ключ, модель чата).\r\n\r\n" +
                "РАЗДЕЛ UNITY .ASSETS\r\n" +
                "• Выбрать Unity Data — каталог *_Data (level0, sharedassets*, resources…).\r\n" +
                "• Экспорт JSON — записывает данные MonoBehaviour из нужных .assets в выбранную папку; она становится рабочей для JSON-файлов.\r\n" +
                "• Импорт в .assets — подставляет отредактированный JSON в выбранный контейнер (строка списка или диалог файла).\r\n" +
                "• Дополнительно — экспорт только выделенного контейнера в JSON.\r\n\r\n" +
                "РАЗДЕЛ ШРИФТЫ (КИРИЛЛИЦА В TextMeshPro)\r\n" +
                "Если перевод выводится пустыми квадратами — в шрифте TMP игры нет кириллических глифов. Мастер слева направо пересобирает шрифт (работает и на IL2CPP):\r\n" +
                "1) Анализ .assets — выберите .assets с TMP_FontAsset (напр. resources.assets); определяются PathID шрифта и размер атласа.\r\n" +
                "2) Создать атлас (TTF) — укажите TTF/OTF с кириллицей (напр. arial.ttf); генерируется новый MSDF-атлас.\r\n" +
                "3) Патч (рост кириллицы) — пересобирает таблицы глифов и символов шрифта с добавленными буквами.\r\n" +
                "4) Применить в игру — заменяет оригинальный .assets (сначала создаётся резервная копия .bak).\r\n\r\n" +
                "НАСТРОЙКИ — API ПЕРЕВОДА\r\n" +
                "Провайдер (LibreTranslate, OpenRouter, OpenAI, Groq, Ollama, свой URL и т.д.). Базовый URL подставляется автоматически, но его можно править.\r\n" +
                "Поле модели чата — редактируемый список: откройте выпадающий список и набирайте текст для фильтра; «Обновить список моделей» подгружает каталог с сервера, где поддерживается.\r\n" +
                "Ключ API — по умолчанию скрыт; кнопка «глаз» переключает отображение. Для Ollama и «Свой URL» ключ часто не нужен.\r\n" +
                "Память переводов — опциональный TM на JSON для раздела JSON-файлов (пути и логика на карточке TM).\r\n\r\n" +
                "МЕНЮ И СОЧЕТАНИЯ КЛАВИШ\r\n" +
                "Файл — Ctrl+O папка, Ctrl+S сохранить JSON, Ctrl+E экспорт TXT, Ctrl+I импорт TXT; пункты экспорта/импорта .assets; Бандлы (JSON↔.bundle); Выход.\r\n" +
                "Правка — копирование/вставка для внешнего ИИ, Ctrl+F поиск в таблице, Esc сбрасывает фильтр поиска (если не редактируете ячейку), F3 следующее совпадение, F5 обновить таблицу из JSON, переход по непереведённым строкам, перевод пустых через API (Ctrl+Shift+T), применение TM, очистка лога (Ctrl+Shift+L).\r\n" +
                "Вид — светлые/тёмные темы.\r\n" +
                "Справка — эта помощь (F1), О программе.\r\n\r\n" +
                "ПОДСКАЗКА\r\n" +
                "Язык этого текста совпадает с языком интерфейса в Настройках; подписи меню обновляются сразу после смены языка.";
        }

        internal void BuildJsonTranslatorUI()
        {
            moduleHostPanel.Controls.Clear();

            jsonWorkspaceCard = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                BackColor = Color.FromArgb(30, 28, 40)
            };

            lblJsonModuleTitle = new GdiSingleLineHeadingLabel
            {
                Dock = DockStyle.Top,
                Height = 36,
                AutoSize = false,
                OpticalShiftY = -4,
                Text = L("JSON translation", "Перевод JSON"),
                Font = new Font("Segoe UI", 17f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Padding = new Padding(4, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false
            };

            toolbarFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(4, 4, 4, 10),
                BackColor = Color.Transparent
            };

            btnSelectFolder = CreateModernButton(L("Folder", "Папка"), ButtonStyleKind.Secondary);
            btnApply = CreateModernButton(L("Save changes", "Сохранить изменения"), ButtonStyleKind.Primary);
            btnExportTxt = CreateModernButton(L("Export", "Экспорт"), ButtonStyleKind.Secondary);
            btnImportTxt = CreateModernButton(L("Import", "Импорт"), ButtonStyleKind.Secondary);
            btnTranslateEmptyApi = CreateModernButton(L("AI translation", "Перевод с ИИ"), ButtonStyleKind.Secondary);
            btnDeleteJsonWithoutText = CreateModernButton(L("Delete text-less entries", "Удалить без текста"), ButtonStyleKind.Danger);
            btnCopySelectedAi = CreateModernButton(L("Copy", "Копировать"), ButtonStyleKind.Secondary);
            btnPasteAi = CreateModernButton(L("Paste", "Вставить"), ButtonStyleKind.Secondary);

            chkBackup = new CheckBox
            {
                Text = " " + L("Create .bak backups", "Создавать .bak"),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                UseCompatibleTextRendering = false,
                Checked = createBackup,
                Margin = new Padding(18, 18, 0, 0)
            };

            progressStatsLabel = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(12, 20, 0, 0)
            };

            toolbarFlow.Controls.Add(btnSelectFolder);
            toolbarFlow.Controls.Add(btnApply);
            toolbarFlow.Controls.Add(btnExportTxt);
            toolbarFlow.Controls.Add(btnImportTxt);
            toolbarFlow.Controls.Add(btnTranslateEmptyApi);
            toolbarFlow.Controls.Add(btnDeleteJsonWithoutText);
            toolbarFlow.Controls.Add(btnCopySelectedAi);
            toolbarFlow.Controls.Add(btnPasteAi);
            toolbarFlow.Controls.Add(chkBackup);
            toolbarFlow.Controls.Add(progressStatsLabel);

            dgv = new SmoothDataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false,
                Font = new Font("Segoe UI", 10f),
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable,
                GridColor = Color.FromArgb(48, 48, 54),
                CellBorderStyle = DataGridViewCellBorderStyle.None
            };
            dgv.RowTemplate.MinimumHeight = 28;
            dgv.ColumnHeadersHeight = 42;

            // Drag-and-drop работает и над таблицей (дочерний контрол перехватывает drop у формы).
            dgv.AllowDrop = true;
            dgv.DragEnter += Form1_DragEnter;
            dgv.DragDrop += Form1_DragDrop;
            dgv.DefaultCellStyle.Padding = new Padding(8, 3, 8, 5);
            dgv.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 3, 8, 5);
            dgv.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            dgv.Columns.Add("File", L("File", "Файл"));
            dgv.Columns.Add("Path", L("Path in JSON", "Путь в JSON"));
            dgv.Columns.Add("Original", L("Original", "Оригинал"));
            dgv.Columns.Add("Translated", L("Translation", "Перевод"));
            dgv.Columns["File"].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            dgv.Columns["File"].Width = 200;
            dgv.Columns["File"].MinimumWidth = 140;
            dgv.Columns["Path"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            dgv.Columns["Original"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            dgv.Columns["Translated"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            dgv.Columns["Path"].FillWeight = 120;
            dgv.Columns["Original"].FillWeight = 150;
            dgv.Columns["Translated"].FillWeight = 150;
            dgv.Columns["Original"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            dgv.Columns["Translated"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;

            // Сортировка — программная (Programmatic), НЕ Automatic: встроенная сортировка DataGridView
            // переставила бы строки грида, не трогая список translationItems, и связь «строка↔элемент» (row.Tag)
            // осталась бы корректной, но порядок СПИСКА разошёлся бы с гридом (сохранение/экспорт идут по списку).
            // Поэтому сортируем сами (SortJsonTableByColumn): переставляем и список, и грид; PopulateJsonGridRowsFast
            // заново проставляет row.Tag. Клик по заголовку обрабатывает Dgv_ColumnHeaderMouseClick.
            foreach (DataGridViewColumn col in dgv.Columns)
                col.SortMode = DataGridViewColumnSortMode.Programmatic;

            var gridPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 4, 0, 8),
                BackColor = Color.Transparent
            };
            gridPanel.Controls.Add(dgv);

            // Встроенная строка поиска над таблицей (вместо модального окна). Скрыта по умолчанию.
            jsonSearchPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Visible = false,
                Padding = new Padding(8, 5, 6, 5),
                BackColor = Color.FromArgb(30, 30, 30)
            };
            jsonSearchBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            var searchIcon = new Label
            {
                Text = L("Find:", "Поиск:"),
                Dock = DockStyle.Left,
                AutoSize = false,
                Width = 58,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            var searchClose = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = 34,
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand
            };
            searchClose.FlatAppearance.BorderSize = 0;
            searchClose.Click += (_, __) => HideJsonTableSearchBar(true);

            jsonSearchPanel.Controls.Add(jsonSearchBox);
            jsonSearchPanel.Controls.Add(searchClose);
            jsonSearchPanel.Controls.Add(searchIcon);

            jsonSearchBox.TextChanged += (_, __) =>
            {
                currentSearchText = jsonSearchBox.Text.Trim();
                ApplyTableSearch();
                UpdateProgressStats();
                UpdateStatus();
            };
            jsonSearchBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    HideJsonTableSearchBar(true);
                    e.Handled = e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    FindNextTableSearchMatch();
                    e.Handled = e.SuppressKeyPress = true;
                }
            };

            gridPanel.Controls.Add(jsonSearchPanel);

            logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.None,
                Padding = new Padding(12, 10, 12, 10)
            };

            progressBar = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 6,
                Style = ProgressBarStyle.Continuous,
                Visible = false,
                ForeColor = Color.FromArgb(124, 77, 255)
            };

            statusStrip = new StatusStrip { Dock = DockStyle.Bottom, Padding = new Padding(8, 0, 8, 0), CanOverflow = false };
            btnCancelApiBatchTranslate = new ToolStripButton(L("Cancel API translation", "Отменить перевод API"))
            {
                Visible = false,
                Alignment = ToolStripItemAlignment.Right,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Font = new Font("Segoe UI", 9f),
                Margin = new Padding(8, 0, 0, 0),
                Overflow = ToolStripItemOverflow.Never
            };
            statusLabel = new ToolStripStatusLabel { Text = L("Ready", "Готов"), Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);
            statusStrip.Items.Add(btnCancelApiBatchTranslate);

            var logArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            logArea.Controls.Add(logBox);
            logArea.Controls.Add(progressBar);
            logArea.Controls.Add(statusStrip);

            var bottomPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };
            bottomPanel.Controls.Add(logArea);

            var jsonGridLogSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 8,
                FixedPanel = FixedPanel.None,
                Panel1MinSize = 120,
                Panel2MinSize = 96,
                BackColor = Color.Transparent
            };
            jsonGridLogSplit.Panel1.Controls.Add(gridPanel);
            jsonGridLogSplit.Panel2.Controls.Add(bottomPanel);
            jsonGridLogSplit.HandleCreated += (_, __) =>
            {
                BeginInvoke(new Action(() =>
                {
                    if (jsonGridLogSplit.Height < 160)
                        return;
                    int panel2 = Math.Min(280, Math.Max(jsonGridLogSplit.Panel2MinSize + 40, jsonGridLogSplit.Height / 4));
                    int dist = jsonGridLogSplit.Height - panel2 - jsonGridLogSplit.SplitterWidth;
                    if (dist >= jsonGridLogSplit.Panel1MinSize)
                        jsonGridLogSplit.SplitterDistance = dist;
                }));
            };

            jsonWorkspaceCard.Controls.Add(jsonGridLogSplit);
            jsonWorkspaceCard.Controls.Add(toolbarFlow);

            moduleHostPanel.Controls.Add(jsonWorkspaceCard);
        }

        private enum ButtonStyleKind
        {
            Primary,
            Secondary,
            Danger
        }

        private Button CreateModernButton(string text, ButtonStyleKind styleKind)
        {
            var color = GetButtonBackColor(styleKind);
            var foreColor = GetButtonForeColor(styleKind);
            var btn = new RoundedToolbarButton
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                BackColor = color,
                ForeColor = foreColor,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Size = new Size(138, 40),
                Margin = new Padding(4),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(8, 0, 8, 0),
                UseVisualStyleBackColor = false
            };
            btn.FlatAppearance.BorderColor = GetButtonBorderColor(styleKind);
            btn.FlatAppearance.MouseOverBackColor = GetButtonHoverColor(styleKind);
            btn.FlatAppearance.MouseDownBackColor = GetButtonDownColor(styleKind);
            btn.HoverBackColor = GetButtonHoverColor(styleKind);
            btn.PressedBackColor = GetButtonDownColor(styleKind);
            return btn;
        }

        /// <summary>Сильное скругление («таблетка»): радиус до ~половины высоты кнопки.</summary>
        private void ApplyToolbarButtonRounding(Button btn)
        {
            int r = ToolbarSurfaceCornerRadius(btn.Size);
            btn.Region = CreateRoundedRegion(btn.Size, r);
        }

        private static int ToolbarSurfaceCornerRadius(Size size, int preferred = 22)
        {
            int innerW = Math.Max(1, size.Width - 1);
            int innerH = Math.Max(1, size.Height - 1);
            int maxR = Math.Min(innerW, innerH) / 2;
            return Math.Max(8, Math.Min(preferred, maxR));
        }

        private Color GetButtonBackColor(ButtonStyleKind styleKind)
        {
            switch (styleKind)
            {
                case ButtonStyleKind.Primary:
                    return Color.FromArgb(37, 99, 235);
                case ButtonStyleKind.Danger:
                    return Color.FromArgb(127, 29, 29);
                default:
                    return Color.FromArgb(31, 41, 55);
            }
        }

        private Color GetButtonForeColor(ButtonStyleKind styleKind)
        {
            switch (styleKind)
            {
                case ButtonStyleKind.Primary:
                    return Color.White;
                case ButtonStyleKind.Danger:
                    return Color.White;
                default:
                    return Color.FromArgb(243, 244, 246);
            }
        }

        private Color GetButtonBorderColor(ButtonStyleKind styleKind)
        {
            switch (styleKind)
            {
                case ButtonStyleKind.Primary:
                    return Color.FromArgb(29, 78, 216);
                case ButtonStyleKind.Danger:
                    return Color.FromArgb(153, 27, 27);
                default:
                    return Color.FromArgb(31, 41, 55);
            }
        }

        private Color GetButtonHoverColor(ButtonStyleKind styleKind)
        {
            switch (styleKind)
            {
                case ButtonStyleKind.Primary:
                    return Color.FromArgb(29, 78, 216);
                case ButtonStyleKind.Danger:
                    return Color.FromArgb(153, 27, 27);
                default:
                    return Color.FromArgb(55, 65, 81);
            }
        }

        private Color GetButtonDownColor(ButtonStyleKind styleKind)
        {
            switch (styleKind)
            {
                case ButtonStyleKind.Primary:
                    return Color.FromArgb(30, 64, 175);
                case ButtonStyleKind.Danger:
                    return Color.FromArgb(91, 33, 33);
                default:
                    return Color.FromArgb(17, 24, 39);
            }
        }

        private sealed class GdiSingleLineHeadingLabel : Label
        {
            public int OpticalShiftY { get; set; }

            public GdiSingleLineHeadingLabel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                DoubleBuffered = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Color bg = BackColor.A == 0 ? (Parent?.BackColor ?? SystemColors.Control) : BackColor;
                using (var br = new SolidBrush(bg))
                    e.Graphics.FillRectangle(br, ClientRectangle);

                var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                var pad = Padding;
                var textBounds = new Rectangle(
                    pad.Left,
                    pad.Top + OpticalShiftY,
                    Math.Max(1, ClientSize.Width - pad.Horizontal),
                    Math.Max(1, ClientSize.Height - pad.Vertical));
                TextRenderer.DrawText(e.Graphics, Text ?? "", Font, textBounds, ForeColor, flags);
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                Invalidate();
            }

            protected override void OnForeColorChanged(EventArgs e)
            {
                base.OnForeColorChanged(e);
                Invalidate();
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                Invalidate();
            }

            protected override void OnBackColorChanged(EventArgs e)
            {
                base.OnBackColorChanged(e);
                Invalidate();
            }

            protected override void OnParentBackColorChanged(EventArgs e)
            {
                base.OnParentBackColorChanged(e);
                Invalidate();
            }
        }

        private sealed class NavSidebarRow : Panel
        {
            private static readonly Font NavFontRegular = new Font("Segoe UI", 10.5f, FontStyle.Regular);
            private static readonly Font NavFontBold = new Font("Segoe UI", 10.5f, FontStyle.Bold);

            /// <summary>Смещение строки подписи вверх относительно геометрического центра (оптика Segoe UI).</summary>
            private const int CaptionShiftUpPx = 1;

            /// <summary>Смещение иконки вверх (чуть меньше текста), чтобы блок читался одной линией.</summary>
            private const int IconShiftUpPx = 1;

            private readonly Form1 _host;
            private readonly Bitmap _icon;
            private bool _hover;

            public NavSidebarRow(Form1 host, Bitmap icon)
            {
                _host = host ?? throw new ArgumentNullException(nameof(host));
                _icon = icon ?? throw new ArgumentNullException(nameof(icon));

                DoubleBuffered = true;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Height = 50;
                Width = 236;
                Margin = new Padding(0, 0, 0, 4);
                Cursor = Cursors.Hand;
                TabStop = false;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; Invalidate(); };
            }

            internal void SyncAppearance()
            {
                Invalidate();
            }

            private bool IsActive => ReferenceEquals(_host.activeNavButton, this);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _icon?.Dispose();
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                bool active = IsActive;
                Color accent = _host.DashboardAccentPrimary();
                Color navBg = Parent != null ? Parent.BackColor : BackColor;

                // Фон строки = фон сайдбара (неактивные пункты сливаются — без рамок-«коробок»).
                using (var bgBr = new SolidBrush(navBg))
                    g.FillRectangle(bgBr, ClientRectangle);

                var pill = new Rectangle(6, 3, Math.Max(1, Width - 12), Math.Max(1, Height - 6));

                Color fg;
                if (active)
                {
                    // Скруглённая заливка акцентом текущей темы.
                    using (var path = Form1.CreateRoundedRectPath(pill, 10))
                    using (var br = new SolidBrush(accent))
                        g.FillPath(br, path);
                    fg = Color.White;
                }
                else if (_hover)
                {
                    Color hoverFill = Color.FromArgb(
                        Math.Min(255, navBg.R + 16),
                        Math.Min(255, navBg.G + 17),
                        Math.Min(255, navBg.B + 22));
                    using (var path = Form1.CreateRoundedRectPath(pill, 10))
                    using (var br = new SolidBrush(hoverFill))
                        g.FillPath(br, path);
                    fg = Color.FromArgb(236, 238, 244);
                }
                else
                {
                    fg = Color.FromArgb(170, 178, 192);
                }

                int iconX = pill.Left + 10;
                int iy = Math.Max(pill.Top, pill.Top + (pill.Height - _icon.Height) / 2 - IconShiftUpPx);
                g.DrawImage(_icon, iconX, iy);

                string caption = _host.NavCaption(Tag as string ?? "Dashboard");
                var font = active ? NavFontBold : NavFontRegular;
                int textLeft = iconX + _icon.Width + 6;
                int textWidth = Math.Max(1, pill.Right - textLeft - 10);

                var textRect = new Rectangle(textLeft, CaptionShiftUpPx, textWidth, Math.Max(1, Height - CaptionShiftUpPx * 2));
                var tf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, caption, font, textRect, fg, tf);
            }
        }

        internal void AddNavButton(string iconKey, System.Action onClick)
        {
            Bitmap bmp = CreateNavIcon(iconKey);
            var row = new NavSidebarRow(this, bmp)
            {
                Tag = iconKey
            };
            row.Click += (_, __) =>
            {
                SetActiveNavButton(row);
                onClick();
            };
            navButtonsContainer.Controls.Add(row);
            navButtons.Add(row);
            if (activeNavButton == null)
                SetActiveNavButton(row);
            else
                row.SyncAppearance();
        }

        private Bitmap CreateNavIcon(string iconKey)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            using (var iconPen = new Pen(Color.FromArgb(203, 213, 225), 2.2f))
            using (var iconBrush = new SolidBrush(Color.FromArgb(203, 213, 225)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                switch (iconKey)
                {
                    case "Dashboard":
                        g.FillRectangle(iconBrush, 8, 8, 7, 7);
                        g.FillRectangle(iconBrush, 18, 8, 7, 7);
                        g.FillRectangle(iconBrush, 8, 18, 7, 7);
                        g.FillRectangle(iconBrush, 18, 18, 7, 7);
                        break;
                    case "Lang":
                        g.DrawRectangle(iconPen, 8, 9, 16, 14);
                        using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                        using (var br = new SolidBrush(Color.FromArgb(203, 213, 225)))
                        {
                            g.DrawString("A", f, br, 10, 11);
                            g.DrawString("Я", f, br, 19, 11);
                        }
                        break;
                    case "TM":
                        g.FillRectangle(iconBrush, 8, 11, 16, 3);
                        g.FillRectangle(iconBrush, 8, 16, 16, 3);
                        g.FillRectangle(iconBrush, 8, 21, 11, 3);
                        break;
                    case "Home":
                        iconPen.LineJoin = LineJoin.Round;
                        g.DrawLines(iconPen, new[]
                        {
                            new PointF(16, 7),
                            new PointF(7, 15),
                            new PointF(7, 25),
                            new PointF(25, 25),
                            new PointF(25, 15),
                            new PointF(16, 7)
                        });
                        break;
                    case "Page":
                        using (var pagePath = CreateRoundedPath(new Rectangle(9, 6, 15, 20), 3))
                        {
                            g.DrawPath(iconPen, pagePath);
                            g.DrawLine(iconPen, 12, 13, 21, 13);
                            g.DrawLine(iconPen, 12, 18, 21, 18);
                        }
                        break;
                    case "Toolbox":
                        using (var bodyPath = CreateRoundedPath(new Rectangle(7, 13, 18, 11), 3))
                            g.DrawPath(iconPen, bodyPath);
                        g.DrawLine(iconPen, 12, 10, 20, 10);
                        g.DrawLine(iconPen, 12, 10, 12, 13);
                        g.DrawLine(iconPen, 20, 10, 20, 13);
                        break;
                    case "Fonts":
                        using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
                        using (var br = new SolidBrush(Color.FromArgb(203, 213, 225)))
                        {
                            g.DrawString("A", f, br, 9, 10);
                            g.DrawString("а", f, br, 18, 14);
                        }
                        break;
                    case "Bundles":
                        g.DrawRectangle(iconPen, 8, 10, 16, 14);
                        g.DrawLine(iconPen, 12, 14, 20, 14);
                        g.DrawLine(iconPen, 12, 18, 18, 18);
                        g.DrawLine(iconPen, 12, 22, 20, 22);
                        break;
                    case "Textures":
                        using (var imgPath = CreateRoundedPath(new Rectangle(7, 9, 18, 14), 3))
                            g.DrawPath(iconPen, imgPath);
                        g.FillEllipse(iconBrush, 11, 12, 3, 3); // «солнце»
                        iconPen.LineJoin = LineJoin.Round;
                        g.DrawLines(iconPen, new[]
                        {
                            new PointF(9, 22),
                            new PointF(14, 16),
                            new PointF(17, 19),
                            new PointF(20, 15),
                            new PointF(23, 22)
                        }); // «горы»
                        break;
                    case "Settings":
                        g.DrawEllipse(iconPen, 10, 10, 12, 12);
                        g.FillEllipse(iconBrush, 14, 14, 4, 4);
                        g.DrawLine(iconPen, 16, 6, 16, 10);
                        g.DrawLine(iconPen, 16, 22, 16, 26);
                        g.DrawLine(iconPen, 6, 16, 10, 16);
                        g.DrawLine(iconPen, 22, 16, 26, 16);
                        g.DrawLine(iconPen, 8.8f, 8.8f, 11.5f, 11.5f);
                        g.DrawLine(iconPen, 20.5f, 20.5f, 23.2f, 23.2f);
                        break;
                    default:
                        g.FillEllipse(iconBrush, 14, 14, 4, 4);
                        break;
                }
            }
            return bmp;
        }

        private void ActivateNavByTag(string iconKey)
        {
            if (string.IsNullOrEmpty(iconKey))
                return;
            foreach (var row in navButtons)
            {
                if (string.Equals(row.Tag as string, iconKey, StringComparison.Ordinal))
                {
                    SetActiveNavButton(row);
                    return;
                }
            }
        }

        private void SetActiveNavButton(NavSidebarRow selectedRow)
        {
            activeNavButton = selectedRow;
            UpdateNavButtonsAppearance();
        }

        private void UpdateNavButtonsAppearance()
        {
            foreach (var row in navButtons)
                row.SyncAppearance();
        }

        private void DrawPlaceholderIcon(Graphics g, Rectangle rect)
        {
            DrawPurpleBrandIcon(g, rect);
        }

        private void DrawPurpleBrandIcon(Graphics g, Rectangle rect)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var pad = Math.Max(2, rect.Width / 16);
            var box = new Rectangle(rect.X + pad, rect.Y + pad, rect.Width - pad * 2, rect.Height - pad * 2);
            var radius = Math.Max(8, box.Width / 6);

            using (var path = CreateRoundedPath(box, radius))
            using (var brush = new LinearGradientBrush(
                box,
                Color.FromArgb(240, 138, 108, 255),
                Color.FromArgb(255, 92, 58, 210),
                LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
                using (var pen = new Pen(Color.FromArgb(180, 210, 190, 255), Math.Max(1f, rect.Width / 48f)))
                    g.DrawPath(pen, path);
            }

            float fontPx = Math.Max(10f, rect.Width * 0.42f);
            using (var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var tb = new SolidBrush(Color.White))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("U", font, tb, box, sf);
            }

            float starSize = Math.Max(7f, rect.Width * 0.24f);
            float sx = box.Right - starSize * 0.75f;
            float sy = box.Top - starSize * 0.28f;
            DrawFivePointStar(g, sx, sy, starSize, Color.FromArgb(255, 244, 196, 58));

            using (var shine = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
                g.FillEllipse(shine, box.X + box.Width * 0.14f, box.Y + box.Height * 0.1f, box.Width * 0.38f, box.Height * 0.22f);
        }

        private static void DrawFivePointStar(Graphics g, float cx, float cy, float size, Color fill)
        {
            var pts = new PointF[10];
            double outer = size / 2d;
            double inner = outer * 0.42d;
            for (int i = 0; i < 5; i++)
            {
                double ao = (i * 72d - 90d) * Math.PI / 180d;
                double ai = ((i * 72d) + 36d - 90d) * Math.PI / 180d;
                pts[i * 2] = new PointF(cx + (float)(outer * Math.Cos(ao)), cy + (float)(outer * Math.Sin(ao)));
                pts[i * 2 + 1] = new PointF(cx + (float)(inner * Math.Cos(ai)), cy + (float)(inner * Math.Sin(ai)));
            }

            using (var b = new SolidBrush(fill))
                g.FillPolygon(b, pts);
            using (var p = new Pen(Color.FromArgb(220, 255, 255, 255), Math.Max(0.8f, size / 18f)))
                g.DrawPolygon(p, pts);
        }

        private static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
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

        private GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            return CreateRoundedRectPath(rect, radius);
        }

        private Region CreateRoundedRegion(Size size, int radius)
        {
            var bounds = new Rectangle(0, 0, Math.Max(1, size.Width - 1), Math.Max(1, size.Height - 1));
            return new Region(CreateRoundedRectPath(bounds, radius));
        }

        /// <summary>Скругляет контур карточки Dashboard (фон уже задаётся <see cref="Control.BackColor"/>).</summary>
        private void ApplyDashboardRoundedClip(Control panel, int radius = 12)
        {
            void Clip(object _, EventArgs __)
            {
                if (panel.IsDisposed || panel.Width <= 2 || panel.Height <= 2)
                    return;

                try
                {
                    panel.Region?.Dispose();
                    panel.Region = CreateRoundedRegion(panel.Size, radius);
                }
                catch
                {
                    // игнорируем редкие сбои Region при ранней инициализации
                }
            }

            panel.SizeChanged += Clip;
            panel.HandleCreated += (_, __) => Clip(panel, EventArgs.Empty);
            if (panel.IsHandleCreated)
                Clip(panel, EventArgs.Empty);
        }

        private static Color ResolveToolbarSurroundBackColor(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
            {
                if (p.BackColor.A == 255)
                    return p.BackColor;
            }

            return SystemColors.Control;
        }

        /// <summary>Клиентская область минус <see cref="Control.Padding"/> — чтобы текст совпадал с полями WinForms.</summary>
        private static Rectangle DeflateClientRectangleByPadding(Control c)
        {
            Rectangle r = c.ClientRectangle;
            Padding p = c.Padding;
            int innerW = Math.Max(1, r.Width - p.Horizontal);
            int innerH = Math.Max(1, r.Height - p.Vertical);
            return new Rectangle(r.Left + p.Left, r.Top + p.Top, innerW, innerH);
        }

        private sealed class RoundedToolbarButton : Button
        {
            public Color HoverBackColor { get; set; }
            public Color PressedBackColor { get; set; }

            private bool _hover;

            public RoundedToolbarButton()
            {
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.UserPaint |
                    ControlStyles.DoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.Selectable,
                    true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                Cursor = Cursors.Hand;
                TabStop = true;
                UseVisualStyleBackColor = false;
            }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                // Иначе Button заливает ClientRectangle цветом BackColor — видны «квадратные» уголки.
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

            protected override void OnPaint(PaintEventArgs pevent)
            {
                var g = pevent.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var clip = ClientRectangle;

                using (var surround = new SolidBrush(ResolveToolbarSurroundBackColor(this)))
                    g.FillRectangle(surround, clip);

                var rect = ClientRectangle;
                rect.Width--;
                rect.Height--;

                int corner = ToolbarSurfaceCornerRadius(Size);
                bool pressed = (MouseButtons & MouseButtons.Left) == MouseButtons.Left
                    && ClientRectangle.Contains(PointToClient(MousePosition));

                Color fill;
                if (!Enabled)
                    fill = ControlPaint.Light(BackColor, 0.25f);
                else if (pressed && _hover)
                    fill = PressedBackColor;
                else if (_hover)
                    fill = HoverBackColor;
                else
                    fill = BackColor;

                using (var path = CreateRoundedRectPath(rect, corner))
                {
                    using (var brush = new SolidBrush(fill))
                        g.FillPath(brush, path);

                    using (var edgePen = new Pen(ControlPaint.Dark(fill, 0.12f), 1f))
                    {
                        edgePen.Alignment = PenAlignment.Inset;
                        g.DrawPath(edgePen, path);
                    }
                }

                Rectangle textRect = DeflateClientRectangleByPadding(this);
                if (textRect.Width > 0 && textRect.Height > 0)
                {
                    // Segoe UI: VerticalCenter слегка ниже визуального центра «таблетки».
                    textRect.Offset(0, -1);
                    TextRenderer.DrawText(
                        g,
                        Text,
                        Font,
                        textRect,
                        ForeColor,
                        TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPadding |
                        TextFormatFlags.SingleLine |
                        TextFormatFlags.NoPrefix);
                }
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                Invalidate();
            }
        }

        private const uint LwaAlpha = 0x2;

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(System.IntPtr handle);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>Тёмный/светлый системный заголовок окна под текущую тему (Win10 2004+).</summary>
        private void ApplyThemedTitleBar(Form form)
        {
            if (form == null)
                return;

            void Apply()
            {
                try
                {
                    int dark = isDarkTheme ? 1 : 0;
                    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (или 19 на ранних 19041); пробуем оба.
                    if (DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)) != 0)
                        DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
                }
                catch { /* ignore */ }
            }

            if (form.IsHandleCreated)
                Apply();
            else
                form.HandleCreated += (_, __) => Apply();
        }

        /// <summary>
        /// Тематизирует нативные полосы прокрутки контрола и всех его детей под текущую тему
        /// (тёмные при тёмных темах). Применять после создания хэндла.
        /// </summary>
        private void ApplyThemedScrollBars(Control root)
        {
            if (root == null)
                return;

            string subApp = isDarkTheme ? "DarkMode_Explorer" : "Explorer";

            void Apply(Control c)
            {
                try
                {
                    if (c.IsHandleCreated)
                        SetWindowTheme(c.Handle, subApp, null);
                    else
                        c.HandleCreated += (s, e) => { try { SetWindowTheme(((Control)s).Handle, subApp, null); } catch { /* ignore */ } };
                }
                catch { /* ignore */ }

                foreach (Control child in c.Controls)
                    Apply(child);
            }

            Apply(root);
        }

        /// <summary>Полупрозрачное затемнение поверх области контента (видно интерфейс под оверлеем).</summary>
        private sealed class WelcomeLayeredDimPanel : Panel
        {
            private readonly byte _alpha;

            public WelcomeLayeredDimPanel(byte alpha)
            {
                _alpha = alpha;
                Dock = DockStyle.Fill;
                TabStop = false;
                BackColor = Color.Black;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x80000; // WS_EX_LAYERED
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                if (IsHandleCreated)
                    SetLayeredWindowAttributes(Handle, 0, _alpha, LwaAlpha);
            }
        }

        /// <summary>
        /// По умолчанию DataGridView при Ctrl+C копирует все выделенные ячейки (часто всю строку с TAB).
        /// Здесь: если ячейка не в режиме редактирования — в буфер только содержимое <see cref="DataGridView.CurrentCell"/> (можно F2 и выделить фрагмент — тогда сработает стандартное копирование из TextBox).
        /// </summary>
        private static bool TryClipboardCopyCurrentCellWhenNotEditing(DataGridView grid, Keys keyData)
        {
            if (keyData != (Keys.Control | Keys.C))
                return false;
            if (grid == null || grid.IsDisposed || grid.IsCurrentCellInEditMode)
                return false;
            var cell = grid.CurrentCell;
            if (cell == null || cell.RowIndex < 0 || cell.ColumnIndex < 0)
                return false;
            object fv = cell.FormattedValue;
            var text = fv as string ?? fv?.ToString() ?? "";
            try
            {
                if (text.Length > 0)
                    Clipboard.SetText(text);
                else
                    Clipboard.Clear();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private class ClipboardAwareDataGridView : DataGridView
        {
            public ClipboardAwareDataGridView()
            {
                DoubleBuffered = true;
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (TryClipboardCopyCurrentCellWhenNotEditing(this, keyData))
                    return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
        }

        private class SmoothDataGridView : ClipboardAwareDataGridView
        {
            public SmoothDataGridView()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
        }

        /// <summary>Системная иконка приложения из ресурсов того же .exe (совпадает с UnityTextTranslator.ico).</summary>
        private Icon LoadFormIconSameAsExecutable()
        {
            try
            {
                var path = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(path))
                {
                    using (var extracted = Icon.ExtractAssociatedIcon(path))
                    {
                        if (extracted != null)
                            return (Icon)extracted.Clone();
                    }
                }
            }
            catch
            {
            }

            return CreateWindowIcon();
        }

        private Icon CreateWindowIcon()
        {
            using (var bmp = new Bitmap(64, 64))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                DrawPlaceholderIcon(g, new Rectangle(0, 0, 64, 64));

                var hIcon = bmp.GetHicon();
                try
                {
                    using (var tempIcon = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tempIcon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
    }
}