using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.Helpers;
using SysBot.Pokemon.WinForms.Properties;
using SysBot.Pokemon.Z3;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms
{
    public sealed partial class Main : Form
    {
        private readonly List<PokeBotState> Bots = [];

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal ProgramConfig Config { get; set; } = null!;

        private IPokeBotRunner RunningEnvironment { get; set; } = null!;

        public readonly ISwitchConnectionAsync? SwitchConnection;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public static bool IsUpdating { get; set; } = false;
        private System.Windows.Forms.Timer? _autoSaveTimer;
        private bool _isFormLoading = true;

        private SearchManager _searchManager = null!;

        internal bool hasUpdate = false;
        internal double pulsePhase = 0;
        private Color lastIndicatorColor = Color.Empty;
        private DateTime lastIndicatorUpdate = DateTime.MinValue;
        private const int PULSE_UPDATE_INTERVAL_MS = 50;
        private bool _isReallyClosing = false;
        private LinearGradientBrush? _logoBrush;
        private Image? _currentModeImage = null;

        private readonly Color CuztomBackground = Color.FromArgb(27, 40, 56);
        private readonly Color CuztomDarkBackground = Color.FromArgb(22, 32, 45);
        private readonly Color CuztomAccent = Color.FromArgb(102, 192, 244);
        private readonly Color CuztomText = Color.FromArgb(239, 239, 239);
        private readonly Color CuztomSubText = Color.FromArgb(139, 179, 217);

        public Main()
        {
            InitializeComponent();
            Load += async (sender, e) => await InitializeAsync();

            TC_Main = new TabControl { Visible = false };
            Tab_Bots = new TabPage();
            Tab_Hub = new TabPage();
            Tab_Logs = new TabPage();
            TC_Main.TabPages.AddRange([Tab_Bots, Tab_Hub, Tab_Logs]);
            TC_Main.SendToBack();

            _searchManager = new SearchManager(RTB_Logs, searchStatusLabel);
            ConfigureSearchEventHandlers();
        }

        private void ConfigureSearchEventHandlers()
        {
            btnCaseSensitive.CheckedChanged += (s, e) => _searchManager.ToggleCaseSensitive();
            btnRegex.CheckedChanged += (s, e) => _searchManager.ToggleRegex();
            btnWholeWord.CheckedChanged += (s, e) => _searchManager.ToggleWholeWord();
        }

        private async Task InitializeAsync()
        {
            if (IsUpdating)
                return;
            string discordName = string.Empty;

            PokeTradeBotSWSH.SeedChecker = new Z3SeedSearchHandler<PK8>();
            UpdateChecker updateChecker = new();

            try
            {
                var (updateAvailable, _, _) = await UpdateChecker.CheckForUpdatesAsync();
                hasUpdate = updateAvailable;
            }
            catch { }

            if (File.Exists(Program.ConfigPath))
            {
                var lines = File.ReadAllText(Program.ConfigPath);
                Config = JsonSerializer.Deserialize(lines, ProgramConfigContext.Default.ProgramConfig) ?? new ProgramConfig();
                LogConfig.MaxArchiveFiles = Config.Hub.MaxArchiveFiles;
                LogConfig.LoggingEnabled = Config.Hub.LoggingEnabled;
                comboBox1.SelectedValue = (int)Config.Mode;

                RunningEnvironment = GetRunner(Config);
                foreach (var bot in Config.Bots)
                {
                    bot.Initialize();
                    AddBot(bot);
                }
            }
            else
            {
                Config = new ProgramConfig();
                RunningEnvironment = GetRunner(Config);
                Config.Hub.Folder.CreateDefaults(Program.WorkingDirectory);
            }

            RTB_Logs.MaxLength = 32767;
            LoadControls();
            Text = $"{(string.IsNullOrEmpty(Config.Hub.BotName) ? "GenPKM.com" : Config.Hub.BotName)} {PokeBot.Version} ({Config.Mode})";
            trayIcon.Text = Text;
            _ = Task.Run(BotMonitor);
            InitUtil.InitializeStubs(Config.Mode);
            _isFormLoading = false;
            UpdateBackgroundImage(Config.Mode);
            UpdateStatusIndicatorColor();
            LogUtil.LogInfo($"Bot initialization complete", "System");
            _ = Task.Run(() =>
            {
                try
                {
                    this.InitWebServer();
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to initialize web server: {ex.Message}", "System");
                }
            });
        }

        #region Enhanced Search Implementation

        private void LogSearchBox_TextChanged(object sender, EventArgs e)
        {
            _searchManager.UpdateSearch(logSearchBox.Text);
        }

        private void LogSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    if (e.Shift)
                        _searchManager.FindPrevious();
                    else
                        _searchManager.FindNext();
                    break;

                case Keys.Escape:
                    e.SuppressKeyPress = true;
                    _searchManager.ClearSearch();
                    logSearchBox.Clear();
                    break;

                case Keys.F when e.Control:
                    e.SuppressKeyPress = true;
                    logSearchBox.Focus();
                    logSearchBox.SelectAll();
                    break;
            }
        }

        private void RTB_Logs_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F when e.Control:
                    e.SuppressKeyPress = true;
                    logSearchBox.Focus();
                    logSearchBox.SelectAll();
                    break;

                case Keys.F3:
                    e.SuppressKeyPress = true;
                    if (e.Shift)
                        _searchManager.FindPrevious();
                    else
                        _searchManager.FindNext();
                    break;
            }
        }

        #endregion

        private static IPokeBotRunner GetRunner(ProgramConfig cfg) => cfg.Mode switch
        {
            ProgramMode.SWSH => new PokeBotRunnerImpl<PK8>(cfg.Hub, new BotFactory8SWSH(), cfg),
            ProgramMode.BDSP => new PokeBotRunnerImpl<PB8>(cfg.Hub, new BotFactory8BS(), cfg),
            ProgramMode.LA => new PokeBotRunnerImpl<PA8>(cfg.Hub, new BotFactory8LA(), cfg),
            ProgramMode.SV => new PokeBotRunnerImpl<PK9>(cfg.Hub, new BotFactory9SV(), cfg),
            ProgramMode.LGPE => new PokeBotRunnerImpl<PB7>(cfg.Hub, new BotFactory7LGPE(), cfg),
            _ => throw new IndexOutOfRangeException("Unsupported mode."),
        };

        private async Task BotMonitor()
        {
            while (!Disposing)
            {
                try
                {
                    foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                        c.ReadState();

                    if (trayIcon != null && trayIcon.Visible && Config != null)
                    {
                        var runningBots = FLP_Bots.Controls.OfType<BotController>().Count(c => c.GetBot()?.IsRunning ?? false);
                        var totalBots = FLP_Bots.Controls.OfType<BotController>().Count();
                        string botTitle = string.IsNullOrWhiteSpace(Config.Hub.BotName) ? "PokéBot" : Config.Hub.BotName;
                        trayIcon.Text = totalBots == 0
                            ? $"{botTitle} - No bots configured"
                            : $"{botTitle} - {runningBots}/{totalBots} bots running";
                    }

                    UpdateControlButtonStates();
                }
                catch
                {
                }
                await Task.Delay(2_000).ConfigureAwait(false);
            }
        }

        private void UpdateControlButtonStates()
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => UpdateControlButtonStates());
                return;
            }

            var runningBots = FLP_Bots.Controls.OfType<BotController>().Count(c => c.GetBot()?.IsRunning ?? false);
            var totalBots = FLP_Bots.Controls.OfType<BotController>().Count();
            var anyRunning = runningBots > 0;

            if (btnStart?.Tag is EnhancedButtonAnimationState startState)
            {
                startState.IsActive = !anyRunning && totalBots > 0;
            }

            if (btnStop?.Tag is EnhancedButtonAnimationState stopState)
            {
                stopState.IsActive = anyRunning;
            }

            if (btnReboot?.Tag is EnhancedButtonAnimationState rebootState)
            {
                rebootState.IsActive = anyRunning;
            }
        }

        private void LoadControls()
        {
            PG_Hub.SelectedObject = RunningEnvironment.Config;
            _autoSaveTimer = new System.Windows.Forms.Timer
            {
                Interval = 10_000,
                Enabled = true
            };
            _autoSaveTimer.Tick += (s, e) => SaveCurrentConfig();
            var routines = ((PokeRoutineType[])Enum.GetValues(typeof(PokeRoutineType))).Where(z => RunningEnvironment.SupportsRoutine(z));
            var list = routines.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Routine.DisplayMember = nameof(ComboItem.Text);
            CB_Routine.ValueMember = nameof(ComboItem.Value);
            CB_Routine.DataSource = list;
            CB_Routine.SelectedValue = (int)PokeRoutineType.FlexTrade;

            var protocols = (SwitchProtocol[])Enum.GetValues(typeof(SwitchProtocol));
            var listP = protocols.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Protocol.DisplayMember = nameof(ComboItem.Text);
            CB_Protocol.ValueMember = nameof(ComboItem.Value);
            CB_Protocol.DataSource = listP;
            CB_Protocol.SelectedIndex = (int)SwitchProtocol.WiFi;

            var gameModes = Enum.GetValues<ProgramMode>()
                .Cast<ProgramMode>()
                .Where(m => m != ProgramMode.None)
                .Select(mode => new { Text = mode.ToString(), Value = (int)mode })
                .ToList();
            comboBox1.DisplayMember = "Text";
            comboBox1.ValueMember = "Value";
            comboBox1.DataSource = gameModes;
            comboBox1.SelectedValue = (int)Config.Mode;

            LogUtil.Forwarders.Add(new TextBoxForwarder(RTB_Logs));
        }

        private ProgramConfig GetCurrentConfiguration()
        {
            if (Config == null)
            {
                throw new InvalidOperationException("Config has not been initialized because a valid license was not entered.");
            }
            Config.Bots = [.. Bots];
            return Config;
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsUpdating) return;

            if (!_isReallyClosing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            this.StopWebServer();

            try
            {
                string? exePath = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string? dirPath = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(dirPath))
                    {
                        string portInfoPath = Path.Combine(dirPath, $"MergeBot_{Environment.ProcessId}.port");
                        if (File.Exists(portInfoPath))
                            File.Delete(portInfoPath);
                    }
                }
            }
            catch { }

            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer.Dispose();
            }

            if (animationTimer != null)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }

            if (_logoBrush != null)
            {
                _logoBrush.Dispose();
                _logoBrush = null;
            }

            SaveCurrentConfig();
            var bots = RunningEnvironment;
            if (!bots.IsRunning)
                return;

            async Task WaitUntilNotRunning()
            {
                while (bots.IsRunning)
                    await Task.Delay(10).ConfigureAwait(false);
            }

            _isReallyClosing = true;
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            bots.StopAll();
            Task.WhenAny(WaitUntilNotRunning(), Task.Delay(5_000)).ConfigureAwait(true).GetAwaiter().GetResult();
        }

        private void SaveCurrentConfig()
        {
            var cfg = GetCurrentConfiguration();
            var lines = JsonSerializer.Serialize(cfg, ProgramConfigContext.Default.ProgramConfig);
            File.WriteAllText(Program.ConfigPath, lines);
        }

        [JsonSerializable(typeof(ProgramConfig))]
        [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
        public sealed partial class ProgramConfigContext : JsonSerializerContext
        { }

        private void B_Start_Click(object sender, EventArgs e)
        {
            SaveCurrentConfig();

            LogUtil.LogInfo("Starting all bots...", "Form");
            RunningEnvironment.InitializeStart();
            SendAll(BotControlCommand.Start);

            SetButtonActiveState(btnStart, true);
            SetButtonActiveState(btnStop, false);
            SetButtonActiveState(btnReboot, false);

            foreach (Button navBtn in navButtonsPanel.Controls.OfType<Button>())
            {
                if (navBtn.Tag is NavButtonState state)
                {
                    state.IsSelected = false;
                    navBtn.Invalidate();
                }
            }

            if (btnNavLogs.Tag is NavButtonState logsState)
            {
                logsState.IsSelected = true;
                btnNavLogs.Invalidate();
            }

            TransitionPanels(2);
            titleLabel.Text = "System Logs";

            if (Bots.Count == 0)
                WinFormsUtil.Alert("No bots configured, but all supporting services have been started.");
        }

        private void B_RebootStop_Click(object sender, EventArgs e)
        {
            B_Stop_Click(sender, e);
            SetButtonActiveState(btnReboot, true);

            Task.Run(async () =>
            {
                await Task.Delay(3_500).ConfigureAwait(false);
                SaveCurrentConfig();
                LogUtil.LogInfo("Restarting all the consoles...", "Form");
                RunningEnvironment.InitializeStart();
                SendAll(BotControlCommand.RebootAndStop);
                await Task.Delay(5_000).ConfigureAwait(false);
                SendAll(BotControlCommand.Start);

                BeginInvoke((MethodInvoker)(() =>
                {
                    SetButtonActiveState(btnReboot, false);
                    SetButtonActiveState(btnStop, true);

                    foreach (Button navBtn in navButtonsPanel.Controls.OfType<Button>())
                    {
                        if (navBtn.Tag is NavButtonState state)
                        {
                            state.IsSelected = false;
                            navBtn.Invalidate();
                        }
                    }

                    if (btnNavLogs.Tag is NavButtonState logsState)
                    {
                        logsState.IsSelected = true;
                        btnNavLogs.Invalidate();
                    }

                    TransitionPanels(2);
                    titleLabel.Text = "System Logs";
                }));

                if (Bots.Count == 0)
                    WinFormsUtil.Alert("No bots configured, but all supporting services have been issued the reboot command.");
            });
        }

        private async void Updater_Click(object sender, EventArgs e)
        {
            var (updateAvailable, updateRequired, newVersion) = await UpdateChecker.CheckForUpdatesAsync();
            hasUpdate = updateAvailable;

            if (!updateAvailable)
            {
                var result = MessageBox.Show(
                    "You are on the latest version. Would you like to re-download the current version?",
                    "Update Check",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    UpdateForm updateForm = new(updateRequired, newVersion, updateAvailable: false);
                    updateForm.ShowDialog();
                }
            }
            else
            {
                UpdateForm updateForm = new(updateRequired, newVersion, updateAvailable: true);
                updateForm.ShowDialog();
            }
        }

        private void SetButtonActiveState(Button button, bool isActive)
        {
            if (button?.Tag is EnhancedButtonAnimationState state)
            {
                state.IsActive = isActive;
                button.Invalidate();
            }
        }

        private void SendAll(BotControlCommand cmd)
        {
            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                c.SendCommand(cmd, false);

            LogUtil.LogText($"All bots have been issued a command to {cmd}.");
        }

        private void BtnExit_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to exit PokéBot?",
                "Exit Confirmation",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                _isReallyClosing = true;
                Close();
            }
        }

        private void UpdateAddButtonPosition()
        {
            if (B_New != null && CB_Routine != null)
            {
                B_New.Location = new Point(CB_Routine.Right + 10, 16);
            }
        }

        private void AddBotPanel_Layout(object sender, EventArgs e)
        {
            UpdateAddButtonPosition();
        }

        private void CB_Routine_SizeChanged(object sender, EventArgs e)
        {
            UpdateAddButtonPosition();
        }

        private void CB_Routine_LocationChanged(object sender, EventArgs e)
        {
            UpdateAddButtonPosition();
        }

        private void FLP_Bots_Scroll(object sender, ScrollEventArgs e)
        {
            FLP_Bots.Invalidate();
        }

        private void FLP_Bots_ControlAdded(object sender, ControlEventArgs e)
        {
            FLP_Bots.Invalidate();
        }

        private void FLP_Bots_ControlRemoved(object sender, ControlEventArgs e)
        {
            FLP_Bots.Invalidate();
        }

        private void BtnClearLogs_Click(object sender, EventArgs e)
        {
            RTB_Logs.Clear();
            _searchManager.ClearSearch();
        }

        private void B_Stop_Click(object sender, EventArgs e)
        {
            var env = RunningEnvironment;
            if (!env.IsRunning && (ModifierKeys & Keys.Alt) == 0)
            {
                WinFormsUtil.Alert("Nothing is currently running.");
                return;
            }

            var cmd = BotControlCommand.Stop;

            if ((ModifierKeys & Keys.Control) != 0 || (ModifierKeys & Keys.Shift) != 0)
            {
                if (env.IsRunning)
                {
                    WinFormsUtil.Alert("Commanding all bots to Idle.", "Press Stop (without a modifier key) to hard-stop and unlock control, or press Stop with the modifier key again to resume.");
                    cmd = BotControlCommand.Idle;
                    SetButtonActiveState(btnStop, true);
                }
                else
                {
                    WinFormsUtil.Alert("Commanding all bots to resume their original task.", "Press Stop (without a modifier key) to hard-stop and unlock control.");
                    cmd = BotControlCommand.Resume;
                    SetButtonActiveState(btnStop, false);
                }
            }
            else
            {
                env.StopAll();
                SetButtonActiveState(btnStart, false);
                SetButtonActiveState(btnStop, false);
                SetButtonActiveState(btnReboot, false);
            }
            SendAll(cmd);
        }

        private void B_New_Click(object sender, EventArgs e)
        {
            var cfg = CreateNewBotConfig();
            if (!AddBot(cfg))
            {
                WinFormsUtil.Alert("Unable to add bot; ensure details are valid and not duplicate with an already existing bot.");
                return;
            }
            System.Media.SystemSounds.Asterisk.Play();
        }

        private bool AddBot(PokeBotState cfg)
        {
            if (!cfg.IsValid())
                return false;

            if (Bots.Any(z => z.Connection.Equals(cfg.Connection)))
                return false;

            PokeRoutineExecutorBase newBot;
            try
            {
                Console.WriteLine($"Current Mode ({Config.Mode}) does not support this type of bot ({cfg.CurrentRoutineType}).");
                newBot = RunningEnvironment.CreateBotFromConfig(cfg);
            }
            catch
            {
                return false;
            }

            try
            {
                RunningEnvironment.Add(newBot);
            }
            catch (ArgumentException ex)
            {
                WinFormsUtil.Error(ex.Message);
                return false;
            }

            AddBotControl(cfg);
            Bots.Add(cfg);
            return true;
        }

        private void AddBotControl(PokeBotState cfg)
        {
            int scrollBarWidth = SystemInformation.VerticalScrollBarWidth;
            int availableWidth = FLP_Bots.ClientSize.Width;

            if (FLP_Bots.VerticalScroll.Visible)
            {
                availableWidth -= scrollBarWidth;
            }

            int botWidth = Math.Max(400, availableWidth - 10);

            var row = new BotController { Width = botWidth };
            row.Initialize(RunningEnvironment, cfg);
            FLP_Bots.Controls.Add(row);
            FLP_Bots.SetFlowBreak(row, true);

            row.Click += (s, e) =>
            {
                var details = cfg.Connection;
                TB_IP.Text = details.IP;
                NUD_Port.Value = details.Port;
                CB_Protocol.SelectedIndex = (int)details.Protocol;
                CB_Routine.SelectedValue = (int)cfg.InitialRoutine;
            };

            row.Remove += (s, e) =>
            {
                Bots.Remove(row.State);
                RunningEnvironment.Remove(row.State, !RunningEnvironment.Config.SkipConsoleBotCreation);
                FLP_Bots.Controls.Remove(row);
            };
        }

        private PokeBotState CreateNewBotConfig()
        {
            var ip = TB_IP.Text;
            var port = (int)NUD_Port.Value;
            var cfg = BotConfigUtil.GetConfig<SwitchConnectionConfig>(ip, port);
            cfg.Protocol = (SwitchProtocol)WinFormsUtil.GetIndex(CB_Protocol);

            var pk = new PokeBotState { Connection = cfg };
            var type = (PokeRoutineType)WinFormsUtil.GetIndex(CB_Routine);
            pk.Initialize(type);
            return pk;
        }

        private void FLP_Bots_Resize(object sender, EventArgs e)
        {
            int scrollBarWidth = SystemInformation.VerticalScrollBarWidth;
            int availableWidth = FLP_Bots.ClientSize.Width;

            if (FLP_Bots.VerticalScroll.Visible)
            {
                availableWidth -= scrollBarWidth;
            }

            int botWidth = Math.Max(400, availableWidth - 10);

            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
            {
                c.Width = botWidth;
            }
        }

        private void CB_Protocol_SelectedIndexChanged(object sender, EventArgs e)
        {
            TB_IP.Visible = CB_Protocol.SelectedIndex == 0;
        }

        private void ComboBox1_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_isFormLoading) return;
            if (comboBox1.SelectedValue is int selectedValue)
            {
                ProgramMode newMode = (ProgramMode)selectedValue;
                Config.Mode = newMode;
                SaveCurrentConfig();
                UpdateRunnerAndUI();
                UpdateBackgroundImage(newMode);
            }
        }

        private void UpdateRunnerAndUI()
        {
            RunningEnvironment = GetRunner(Config);
            Text = $"{(string.IsNullOrEmpty(Config.Hub.BotName) ? "GenPKM.com" : Config.Hub.BotName)} {PokeBot.Version} ({Config.Mode})";
        }

        private void UpdateStatusIndicatorPulse()
        {
            var now = DateTime.Now;
            if ((now - lastIndicatorUpdate).TotalMilliseconds < PULSE_UPDATE_INTERVAL_MS)
                return;

            lastIndicatorUpdate = now;

            pulsePhase += 0.1;
            if (pulsePhase > Math.PI * 2)
                pulsePhase -= Math.PI * 2;

            UpdateStatusIndicatorColor();
        }

        private void UpdateStatusIndicatorColor()
        {
            if (statusIndicator == null) return;

            Color newColor;

            if (hasUpdate)
            {
                double pulse = (Math.Sin(pulsePhase) + 1) / 2;
                int minAlpha = 150;
                int maxAlpha = 255;
                int alpha = (int)(minAlpha + (maxAlpha - minAlpha) * pulse);
                newColor = Color.FromArgb(alpha, 102, 192, 244);
            }
            else
            {
                newColor = Color.FromArgb(100, 100, 100);
            }

            if (newColor != lastIndicatorColor)
            {
                lastIndicatorColor = newColor;
                statusIndicator.BackColor = newColor;
                statusIndicator.Invalidate();

                if (hasUpdate && btnUpdate != null)
                {
                    btnUpdate.Invalidate(new Rectangle(
                        statusIndicator.Left - 10,
                        statusIndicator.Top - 10,
                        statusIndicator.Width + 20,
                        statusIndicator.Height + 20
                    ));
                }
            }
        }

        private void UpdateBackgroundImage(ProgramMode mode)
        {
            try
            {
                _currentModeImage = mode switch
                {
                    ProgramMode.SV => Resources.sv_mode_image,
                    ProgramMode.SWSH => Resources.swsh_mode_image,
                    ProgramMode.BDSP => Resources.bdsp_mode_image,
                    ProgramMode.LA => Resources.pla_mode_image,
                    ProgramMode.LGPE => Resources.lgpe_mode_image,
                    _ => null,
                };
                FLP_Bots.Invalidate();
            }
            catch
            {
                _currentModeImage = null;
            }
        }

        #region Tray Icon Methods

        private void TrayIcon_DoubleClick(object sender, EventArgs e)
        {
            ShowFromTray();
        }

        private void TrayMenuShow_Click(object sender, EventArgs e)
        {
            ShowFromTray();
        }

        private void TrayMenuExit_Click(object sender, EventArgs e)
        {
            _isReallyClosing = true;
            Close();
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            trayIcon.Visible = false;
            BringToFront();
            Activate();

            int headerHeight = headerPanel.Height + 10;

            if (hubPanel.Padding.Top <= 40)
                hubPanel.Padding = new Padding(40, headerHeight, 40, 40);

            if (logsPanel.Padding.Top <= 40)
                logsPanel.Padding = new Padding(40, headerHeight, 40, 40);

            hubPanel.PerformLayout();
            PG_Hub.Refresh();

            logsPanel.PerformLayout();
            RTB_Logs.Refresh();
        }

        private void MinimizeToTray()
        {
            Hide();
            ShowInTaskbar = false;
            trayIcon.Visible = true;

            var runningBots = FLP_Bots.Controls.OfType<BotController>().Count(c => c.GetBot()?.IsRunning ?? false);
            var totalBots = FLP_Bots.Controls.OfType<BotController>().Count();

            string message = totalBots == 0
                ? "No bots configured"
                : $"{runningBots} of {totalBots} bots running";

            trayIcon.ShowBalloonTip(2000, "PokéBot Minimized", message, ToolTipIcon.Info);
        }

        private void Main_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized && !_isReallyClosing)
            {
                MinimizeToTray();
            }
        }

        #endregion
    }

    public sealed class SearchManager(RichTextBox textBox, Label statusLabel)
    {
        private readonly RichTextBox _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
        private readonly Label _statusLabel = statusLabel ?? throw new ArgumentNullException(nameof(statusLabel));
        private readonly List<SearchMatch> _matches = [];
        private int _currentIndex = -1;
        private string _lastSearchText = string.Empty;
        private bool _caseSensitive = false;
        private bool _useRegex = false;
        private bool _wholeWord = false;

        private readonly Color HighlightColor = Color.FromArgb(102, 192, 244);
        private readonly Color CurrentHighlightColor = Color.FromArgb(57, 255, 221);

        public void UpdateSearch(string searchText)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                ClearSearch();
                return;
            }

            if (searchText == _lastSearchText)
                return;

            _lastSearchText = searchText;
            PerformSearch(searchText);
        }

        public void FindNext()
        {
            if (_matches.Count == 0)
                return;

            _currentIndex = (_currentIndex + 1) % _matches.Count;
            HighlightCurrentMatch();
        }

        public void FindPrevious()
        {
            if (_matches.Count == 0)
                return;

            _currentIndex = _currentIndex == 0 ? _matches.Count - 1 : _currentIndex - 1;
            HighlightCurrentMatch();
        }

        public void ClearSearch()
        {
            ClearHighlights();
            _matches.Clear();
            _currentIndex = -1;
            _lastSearchText = string.Empty;
            _statusLabel.Text = string.Empty;
        }

        public void ToggleCaseSensitive()
        {
            _caseSensitive = !_caseSensitive;
            if (!string.IsNullOrEmpty(_lastSearchText))
                PerformSearch(_lastSearchText);
        }

        public void ToggleRegex()
        {
            _useRegex = !_useRegex;
            if (!string.IsNullOrEmpty(_lastSearchText))
                PerformSearch(_lastSearchText);
        }

        public void ToggleWholeWord()
        {
            _wholeWord = !_wholeWord;
            if (!string.IsNullOrEmpty(_lastSearchText))
                PerformSearch(_lastSearchText);
        }

        private void PerformSearch(string searchText)
        {
            ClearHighlights();
            _matches.Clear();
            _currentIndex = -1;

            if (string.IsNullOrEmpty(searchText))
            {
                _statusLabel.Text = string.Empty;
                return;
            }

            try
            {
                var text = _textBox.Text;
                var matches = _useRegex ? FindRegexMatches(text, searchText) : FindTextMatches(text, searchText);

                _matches.AddRange(matches);

                if (_matches.Count > 0)
                {
                    HighlightAllMatches();
                    _currentIndex = 0;
                    HighlightCurrentMatch();
                    _statusLabel.Text = $"1 of {_matches.Count}";
                }
                else
                {
                    _statusLabel.Text = "No matches found";
                }
            }
            catch (ArgumentException)
            {
                _statusLabel.Text = "Invalid regex pattern";
            }
        }

        private IEnumerable<SearchMatch> FindTextMatches(string text, string searchText)
        {
            var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var searchPattern = _wholeWord ? $@"\b{Regex.Escape(searchText)}\b" : searchText;

            if (_wholeWord)
            {
                var regex = new Regex(searchPattern, _caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return regex.Matches(text).Cast<Match>()
                    .Select(m => new SearchMatch(m.Index, m.Length));
            }

            var matches = new List<SearchMatch>();
            int index = 0;
            while ((index = text.IndexOf(searchText, index, comparison)) != -1)
            {
                matches.Add(new SearchMatch(index, searchText.Length));
                index += searchText.Length;
            }
            return matches;
        }

        private IEnumerable<SearchMatch> FindRegexMatches(string text, string pattern)
        {
            var options = _caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(pattern, options);
            return regex.Matches(text).Cast<Match>()
                .Select(m => new SearchMatch(m.Index, m.Length));
        }

        private void HighlightAllMatches()
        {
            foreach (var match in _matches)
            {
                _textBox.Select(match.Start, match.Length);
                _textBox.SelectionBackColor = HighlightColor;
            }
        }

        private void HighlightCurrentMatch()
        {
            if (_currentIndex < 0 || _currentIndex >= _matches.Count)
                return;

            ClearCurrentHighlight();

            var currentMatch = _matches[_currentIndex];
            _textBox.Select(currentMatch.Start, currentMatch.Length);
            _textBox.SelectionBackColor = CurrentHighlightColor;
            _textBox.ScrollToCaret();

            _statusLabel.Text = $"{_currentIndex + 1} of {_matches.Count}";
        }

        private void ClearCurrentHighlight()
        {
            if (_currentIndex >= 0 && _currentIndex < _matches.Count)
            {
                var match = _matches[_currentIndex];
                _textBox.Select(match.Start, match.Length);
                _textBox.SelectionBackColor = HighlightColor;
            }
        }

        private void ClearHighlights()
        {
            _textBox.SelectAll();
            _textBox.SelectionBackColor = _textBox.BackColor;
            _textBox.DeselectAll();
        }
    }

    public readonly record struct SearchMatch(int Start, int Length);
}
