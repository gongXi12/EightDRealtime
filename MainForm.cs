using EightDRealtime.Audio;
using EightDRealtime.Audio.Dsp;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EightDRealtime;

public sealed class MainForm : Form
{
    private readonly Wasapi8DAudioEngine _engine = new();
    private readonly ComboBox _captureCombo = new();
    private readonly ComboBox _outputCombo = new();
    private readonly ComboBox _presetCombo = new();
    private readonly Button _refreshButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly CheckBox _enabledCheck = new();
    private readonly CheckBox _sameDeviceModeCheck = new();
    private readonly Label _routeStatusLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _latencyLabel = new();
    private readonly Label _modePillLabel = new();
    private readonly Dictionary<string, TrackBar> _sliders = new();
    private readonly Dictionary<string, Label> _valueLabels = new();
    private bool _loadingControls;

    public MainForm()
    {
        Text = "实时 8D";
        MinimumSize = new Size(980, 720);
        Size = new Size(1080, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = UiPalette.App;
        ForeColor = UiPalette.Text;
        DoubleBuffered = true;
        var windowIcon = TryLoadWindowIcon();
        if (windowIcon is not null)
        {
            Icon = windowIcon;
        }

        _engine.StatusChanged += (_, status) => OnUi(() => _statusLabel.Text = status);
        _engine.LatencyChanged += (_, latency) => OnUi(() => _latencyLabel.Text = latency <= 0 ? "延迟：--" : $"延迟：{latency:F0} ms");
        _engine.Stopped += (_, _) => OnUi(ResetTransportControls);

        BuildInterface();
        LoadDevices();
        ApplyPreset(SpatialPreset.Default);
        UpdateRouteState();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryUseDarkTitleBar();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _engine.Dispose();
        base.OnFormClosing(e);
    }

    private void BuildInterface()
    {
        SuspendLayout();
        Controls.Clear();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18),
            BackColor = UiPalette.App
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiPalette.App,
            Padding = new Padding(0, 8, 0, 10)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 348));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(content, 0, 1);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiPalette.App,
            Padding = new Padding(0, 0, 12, 0)
        };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 248));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(left, 0, 0);

        left.Controls.Add(BuildRoutingPanel(), 0, 0);
        left.Controls.Add(BuildPresetPanel(), 0, 1);
        left.Controls.Add(BuildOrbitPanel(), 0, 2);
        content.Controls.Add(BuildControlsPanel(), 1, 0);

        root.Controls.Add(BuildStatusPanel(), 0, 2);
        ResumeLayout();
    }

    private Control BuildHeader()
    {
        var panel = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 14),
            FillColor = UiPalette.Header,
            BorderColor = UiPalette.Border
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        panel.Controls.Add(grid);

        var logo = new AppLogoView
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 0)
        };
        grid.Controls.Add(logo, 0, 0);

        var titleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        grid.Controls.Add(titleStack, 1, 0);

        titleStack.Controls.Add(new Label
        {
            Text = "实时 8D 耳机处理器",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 20F, FontStyle.Bold),
            ForeColor = UiPalette.Text,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        titleStack.Controls.Add(new Label
        {
            Text = "上下立体环绕 · WASAPI 实时捕获 · 中文桌面版",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 9.5F),
            ForeColor = UiPalette.Muted,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);

        _modePillLabel.Text = "标准 8D 环绕";
        _modePillLabel.Dock = DockStyle.Right;
        _modePillLabel.AutoSize = false;
        _modePillLabel.Width = 178;
        _modePillLabel.Height = 34;
        _modePillLabel.Margin = new Padding(0, 22, 0, 20);
        _modePillLabel.TextAlign = ContentAlignment.MiddleCenter;
        _modePillLabel.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
        _modePillLabel.ForeColor = UiPalette.Accent;
        _modePillLabel.BackColor = UiPalette.Pill;
        grid.Controls.Add(_modePillLabel, 2, 0);

        return panel;
    }

    private Control BuildRoutingPanel()
    {
        var card = NewCard("音频路由");
        var grid = NewCardGrid(2, 5);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(grid);

        StyleCombo(_captureCombo);
        StyleCombo(_outputCombo);
        _captureCombo.SelectedIndexChanged += (_, _) => UpdateRouteState();
        _outputCombo.SelectedIndexChanged += (_, _) => UpdateRouteState();

        StyleButton(_refreshButton, "刷新", UiPalette.Surface2, UiPalette.Text);
        _refreshButton.Click += (_, _) => LoadDevices();

        _sameDeviceModeCheck.Text = "同设备模式";
        _sameDeviceModeCheck.Checked = true;
        _sameDeviceModeCheck.Dock = DockStyle.Fill;
        _sameDeviceModeCheck.ForeColor = UiPalette.Text;
        _sameDeviceModeCheck.BackColor = Color.Transparent;
        _sameDeviceModeCheck.CheckedChanged += (_, _) => UpdateRouteState();

        grid.Controls.Add(FormLabel("捕获源"), 0, 0);
        grid.Controls.Add(_captureCombo, 1, 0);
        grid.Controls.Add(FormLabel("输出"), 0, 1);
        grid.Controls.Add(_outputCombo, 1, 1);
        grid.Controls.Add(_sameDeviceModeCheck, 1, 2);

        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Color.Transparent
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        _routeStatusLabel.Dock = DockStyle.Fill;
        _routeStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _routeStatusLabel.Font = new Font(Font.FontFamily, 8.5F);
        actionRow.Controls.Add(_routeStatusLabel, 0, 0);
        actionRow.Controls.Add(_refreshButton, 1, 0);
        grid.Controls.Add(actionRow, 0, 3);
        grid.SetColumnSpan(actionRow, 2);

        grid.Controls.Add(new Label
        {
            Text = "提示：同设备模式优先保证有声；纯净单音源需要虚拟声卡或独立路由。",
            Dock = DockStyle.Fill,
            ForeColor = UiPalette.Muted,
            Font = new Font(Font.FontFamily, 8.5F),
            TextAlign = ContentAlignment.TopLeft
        }, 0, 4);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 4), 2);

        return card;
    }

    private Control BuildPresetPanel()
    {
        var card = NewCard("音效预设");
        var grid = NewCardGrid(1, 2);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(grid);

        StyleCombo(_presetCombo);
        _presetCombo.DataSource = SpatialPreset.All.ToArray();
        _presetCombo.DisplayMember = nameof(SpatialPreset.DisplayName);
        _presetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingControls && _presetCombo.SelectedItem is SpatialPreset preset)
            {
                ApplyPreset(preset);
            }
        };
        grid.Controls.Add(_presetCombo, 0, 0);

        _enabledCheck.Text = "启用 8D 处理";
        _enabledCheck.Checked = true;
        _enabledCheck.Dock = DockStyle.Fill;
        _enabledCheck.ForeColor = UiPalette.Text;
        _enabledCheck.BackColor = Color.Transparent;
        _enabledCheck.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
        _enabledCheck.CheckedChanged += (_, _) => PushSettings();
        grid.Controls.Add(_enabledCheck, 0, 1);

        return card;
    }

    private Control BuildOrbitPanel()
    {
        var card = NewCard("环绕轨迹");
        var grid = NewCardGrid(1, 2);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        card.Controls.Add(grid);

        grid.Controls.Add(new OrbitPreview { Dock = DockStyle.Fill }, 0, 0);
        grid.Controls.Add(new Label
        {
            Text = "声音路径：左 / 上 / 右 / 下\n上下感来自音色高度变化，左右感来自声像和短延迟。",
            Dock = DockStyle.Fill,
            ForeColor = UiPalette.Muted,
            Font = new Font(Font.FontFamily, 8.5F),
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 1);

        return card;
    }

    private Control BuildControlsPanel()
    {
        var card = NewCard("实时控制");
        var grid = NewCardGrid(3, 10);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        for (var row = 0; row < 10; row++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 10f));
        }
        card.Controls.Add(grid);

        AddSlider(grid, 0, "input", "输入增益", 0, 150, 84);
        AddSlider(grid, 1, "output", "输出增益", 0, 150, 80);
        AddSlider(grid, 2, "speed", "旋转速度", 1, 250, 105);
        AddSlider(grid, 3, "depth", "环绕深度", 0, 100, 92);
        AddSlider(grid, 4, "circle", "环绕范围", 0, 100, 92);
        AddSlider(grid, 5, "height", "上下幅度", 0, 100, 78);
        AddSlider(grid, 6, "heightSpeed", "上下速度", 25, 250, 100);
        AddSlider(grid, 7, "hrtf", "空间化强度", 0, 100, 90);
        AddSlider(grid, 8, "reverb", "房间混响", 0, 65, 18);
        AddSlider(grid, 9, "limit", "限幅上限", 50, 100, 90);

        return card;
    }

    private Control BuildStatusPanel()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            FillColor = UiPalette.Surface,
            BorderColor = UiPalette.Border,
            Padding = new Padding(16, 9, 16, 9)
        };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        card.Controls.Add(grid);

        _statusLabel.Text = "就绪";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = UiPalette.Text;
        _latencyLabel.Text = "延迟：--";
        _latencyLabel.Dock = DockStyle.Fill;
        _latencyLabel.TextAlign = ContentAlignment.MiddleRight;
        _latencyLabel.ForeColor = UiPalette.Muted;

        StyleButton(_startButton, "开始", UiPalette.Accent, Color.FromArgb(4, 24, 26));
        _startButton.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
        _startButton.Click += (_, _) => StartEngine();

        StyleButton(_stopButton, "停止", UiPalette.Stop, Color.White);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopEngine();

        grid.Controls.Add(_statusLabel, 0, 0);
        grid.Controls.Add(_latencyLabel, 1, 0);
        grid.Controls.Add(_startButton, 2, 0);
        grid.Controls.Add(_stopButton, 3, 0);
        return card;
    }

    private void AddSlider(TableLayoutPanel grid, int row, string key, string title, int min, int max, int value)
    {
        var label = FormLabel(title);
        var slider = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = Math.Max(1, (max - min) / 5),
            Value = Math.Clamp(value, min, max),
            Dock = DockStyle.Fill,
            BackColor = UiPalette.Surface,
            ForeColor = UiPalette.Accent,
            Margin = new Padding(4, 5, 4, 4)
        };
        var valueLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = UiPalette.Text,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold)
        };

        slider.ValueChanged += (_, _) =>
        {
            UpdateValueLabels();
            PushSettings();
        };

        _sliders[key] = slider;
        _valueLabels[key] = valueLabel;
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(slider, 1, row);
        grid.Controls.Add(valueLabel, 2, row);
    }

    private void LoadDevices()
    {
        try
        {
            var devices = CoreAudioDeviceEnumerator.GetActiveRenderDevices().ToArray();
            _captureCombo.DataSource = devices.ToArray();
            _outputCombo.DataSource = devices.ToArray();

            if (devices.Length > 0)
            {
                _captureCombo.SelectedIndex = 0;
                _outputCombo.SelectedIndex = devices.Length > 1 ? 1 : 0;
                _statusLabel.Text = $"已找到 {devices.Length} 个播放设备。";
            }
            else
            {
                _statusLabel.Text = "没有找到可用的播放设备。";
            }
        }
        catch (Exception ex)
        {
            DeviceDiagnostics.WriteDeviceReport(ex);
            _statusLabel.Text = $"设备扫描失败：{ex.Message}";
        }

        UpdateRouteState();
    }

    private void ApplyPreset(SpatialPreset preset)
    {
        _loadingControls = true;
        _modePillLabel.Text = preset.DisplayName;
        SetSlider("speed", (int)MathF.Round(preset.RotationHz * 1000f));
        SetSlider("depth", Percent(preset.Depth));
        SetSlider("circle", Percent(preset.CircleStrength));
        SetSlider("height", Percent(preset.HeightDepth));
        SetSlider("heightSpeed", Percent(preset.HeightRate));
        SetSlider("hrtf", Percent(preset.HrtfStrength));
        SetSlider("reverb", Percent(preset.ReverbWet));
        SetSlider("limit", Percent(preset.LimiterThreshold));
        _loadingControls = false;
        UpdateValueLabels();
        PushSettings();
    }

    private void SetSlider(string key, int value)
    {
        var slider = _sliders[key];
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
    }

    private void StartEngine()
    {
        if (_captureCombo.SelectedItem is not AudioDevice capture || _outputCombo.SelectedItem is not AudioDevice output)
        {
            return;
        }

        try
        {
            _engine.Start(capture, output, BuildSettings(), GetCaptureMode(), false);
            _startButton.Enabled = false;
            _stopButton.Enabled = true;
            _refreshButton.Enabled = false;
            _sameDeviceModeCheck.Enabled = false;
            _captureCombo.Enabled = false;
            _outputCombo.Enabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法启动 8D 音频", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateRouteState();
        }
    }

    private void StopEngine()
    {
        _engine.Stop();
        ResetTransportControls();
    }

    private void ResetTransportControls()
    {
        _stopButton.Enabled = false;
        _refreshButton.Enabled = true;
        _sameDeviceModeCheck.Enabled = true;
        _outputCombo.Enabled = true;
        UpdateRouteState();
    }

    private void UpdateRouteState()
    {
        var capture = _captureCombo.SelectedItem as AudioDevice;
        var output = _outputCombo.SelectedItem as AudioDevice;
        var sameDeviceMode = _sameDeviceModeCheck.Checked;
        var missingDevice = output is null || (!sameDeviceMode && capture is null);
        var sameDevice = capture is not null
            && output is not null
            && string.Equals(capture.Id, output.Id, StringComparison.OrdinalIgnoreCase);
        var sameDeviceSupported = Environment.OSVersion.Version.Build >= 20348;
        var ready = !missingDevice
            && !_engine.IsRunning
            && (sameDeviceMode ? sameDeviceSupported : !sameDevice);

        _startButton.Enabled = ready;
        _captureCombo.Enabled = !sameDeviceMode && !_engine.IsRunning;
        _routeStatusLabel.Text = missingDevice
            ? "请选择可用的播放设备。"
            : sameDeviceMode && !sameDeviceSupported
                ? "同设备模式需要 Windows 10 Build 20348 或更新版本。"
                : sameDeviceMode
                    ? "路由可用：同设备模式会保留原声；纯净单音源需要独立路由。"
                    : sameDevice
                        ? "普通模式下请避免捕获和输出使用同一设备。"
                        : "路由可用。";
        _routeStatusLabel.ForeColor = missingDevice || (!sameDeviceMode && sameDevice) || (sameDeviceMode && !sameDeviceSupported)
            ? UiPalette.Warning
            : UiPalette.Success;
    }

    private CaptureMode GetCaptureMode()
    {
        return _sameDeviceModeCheck.Checked
            ? CaptureMode.ProcessExclusionLoopback
            : CaptureMode.EndpointLoopback;
    }

    private SpatialSettings BuildSettings()
    {
        return new SpatialSettings(
            Enabled: _enabledCheck.Checked,
            InputGain: _sliders["input"].Value / 100f,
            OutputGain: _sliders["output"].Value / 100f,
            RotationHz: _sliders["speed"].Value / 1000f,
            Depth: _sliders["depth"].Value / 100f,
            CircleStrength: _sliders["circle"].Value / 100f,
            HeightDepth: _sliders["height"].Value / 100f,
            HeightRate: _sliders["heightSpeed"].Value / 100f,
            HrtfStrength: _sliders["hrtf"].Value / 100f,
            ReverbWet: _sliders["reverb"].Value / 100f,
            LimiterThreshold: _sliders["limit"].Value / 100f);
    }

    private void PushSettings()
    {
        if (_loadingControls)
        {
            return;
        }

        _engine.UpdateSettings(BuildSettings());
    }

    private void UpdateValueLabels()
    {
        if (_valueLabels.Count == 0)
        {
            return;
        }

        _valueLabels["input"].Text = $"{_sliders["input"].Value}%";
        _valueLabels["output"].Text = $"{_sliders["output"].Value}%";
        var speed = _sliders["speed"].Value / 1000f;
        _valueLabels["speed"].Text = speed <= 0 ? "静止" : $"{speed:F3} Hz";
        _valueLabels["depth"].Text = $"{_sliders["depth"].Value}%";
        _valueLabels["circle"].Text = $"{_sliders["circle"].Value}%";
        _valueLabels["height"].Text = $"{_sliders["height"].Value}%";
        _valueLabels["heightSpeed"].Text = $"{_sliders["heightSpeed"].Value}%";
        _valueLabels["hrtf"].Text = $"{_sliders["hrtf"].Value}%";
        _valueLabels["reverb"].Text = $"{_sliders["reverb"].Value}%";
        _valueLabels["limit"].Text = $"{_sliders["limit"].Value}%";
    }

    private void OnUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static CardPanel NewCard(string title)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 38, 16, 14),
            Margin = new Padding(0, 0, 0, 12),
            FillColor = UiPalette.Surface,
            BorderColor = UiPalette.Border,
            Title = title
        };
        return card;
    }

    private static TableLayoutPanel NewCardGrid(int columns, int rows)
    {
        return new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            RowCount = rows,
            BackColor = Color.Transparent
        };
    }

    private Label FormLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiPalette.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9F)
        };
    }

    private void StyleCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = UiPalette.Surface2;
        combo.ForeColor = UiPalette.Text;
        combo.Font = new Font(Font.FontFamily, 9F);
        combo.Margin = new Padding(0, 4, 0, 4);
    }

    private static void StyleButton(Button button, string text, Color backColor, Color foreColor)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.Margin = new Padding(8, 4, 0, 4);
        button.Cursor = Cursors.Hand;
    }

    private static Icon? TryLoadWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : null;
    }

    private void TryUseDarkTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return;
        }

        var enabled = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
    }

    private static int Percent(float value) => (int)MathF.Round(value * 100f);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}

internal static class UiPalette
{
    public static readonly Color App = Color.FromArgb(13, 17, 23);
    public static readonly Color Header = Color.FromArgb(18, 26, 34);
    public static readonly Color Surface = Color.FromArgb(22, 29, 38);
    public static readonly Color Surface2 = Color.FromArgb(31, 40, 52);
    public static readonly Color Border = Color.FromArgb(46, 60, 76);
    public static readonly Color Text = Color.FromArgb(232, 240, 247);
    public static readonly Color Muted = Color.FromArgb(147, 160, 174);
    public static readonly Color Accent = Color.FromArgb(74, 229, 208);
    public static readonly Color Accent2 = Color.FromArgb(96, 165, 250);
    public static readonly Color Pill = Color.FromArgb(18, 58, 61);
    public static readonly Color Stop = Color.FromArgb(204, 82, 92);
    public static readonly Color Warning = Color.FromArgb(241, 176, 74);
    public static readonly Color Success = Color.FromArgb(104, 211, 145);
}

internal sealed class CardPanel : Panel
{
    public string Title { get; set; } = string.Empty;
    public Color FillColor { get; set; } = UiPalette.Surface;
    public Color BorderColor { get; set; } = UiPalette.Border;

    public CardPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, 10);
        using var fill = new SolidBrush(FillColor);
        using var border = new Pen(BorderColor);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        if (!string.IsNullOrWhiteSpace(Title))
        {
            using var titleBrush = new SolidBrush(UiPalette.Text);
            using var font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            e.Graphics.DrawString(Title, font, titleBrush, new PointF(16, 13));
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class OrbitPreview : Control
{
    public OrbitPreview()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        MinimumSize = new Size(180, 180);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = Width / 2f;
        var cy = Height / 2f + 3;
        var radiusX = Math.Min(Width, Height) * 0.27f;
        var radiusY = Math.Min(Width, Height) * 0.36f;
        using var orbitPen = new Pen(Color.FromArgb(160, UiPalette.Accent), 3f);
        using var faintPen = new Pen(Color.FromArgb(55, UiPalette.Accent2), 1.5f);
        using var centerBrush = new SolidBrush(Color.FromArgb(36, 47, 61));
        using var glowBrush = new SolidBrush(Color.FromArgb(180, UiPalette.Accent));
        using var textBrush = new SolidBrush(UiPalette.Muted);
        using var labelFont = new Font(Font.FontFamily, 8F, FontStyle.Bold);

        e.Graphics.FillEllipse(centerBrush, cx - 38, cy - 38, 76, 76);
        e.Graphics.DrawEllipse(faintPen, cx - radiusX, cy - radiusY, radiusX * 2, radiusY * 2);
        e.Graphics.DrawArc(orbitPen, cx - radiusX, cy - radiusY, radiusX * 2, radiusY * 2, 200, 245);
        e.Graphics.FillEllipse(glowBrush, cx - 7, cy - radiusY - 7, 14, 14);
        e.Graphics.FillEllipse(new SolidBrush(Color.FromArgb(220, UiPalette.Accent2)), cx + radiusX - 5, cy - 5, 10, 10);

        DrawCentered(e.Graphics, "上", labelFont, textBrush, cx, cy - radiusY - 24);
        DrawCentered(e.Graphics, "下", labelFont, textBrush, cx, cy + radiusY + 14);
        DrawCentered(e.Graphics, "左", labelFont, textBrush, cx - radiusX - 20, cy - 7);
        DrawCentered(e.Graphics, "右", labelFont, textBrush, cx + radiusX + 20, cy - 7);
    }

    private static void DrawCentered(Graphics graphics, string text, Font font, Brush brush, float x, float y)
    {
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, brush, x - size.Width / 2f, y);
    }
}

internal sealed class AppLogoView : Control
{
    public AppLogoView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        MinimumSize = new Size(58, 58);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var size = Math.Min(Width, Height) - 4;
        var x = (Width - size) / 2f;
        var y = (Height - size) / 2f;
        var rect = new RectangleF(x, y, size, size);

        using var bg = new LinearGradientBrush(rect, Color.FromArgb(30, 45, 58), Color.FromArgb(10, 25, 29), 45f);
        using var border = new Pen(Color.FromArgb(95, UiPalette.Accent), 1.6f);
        using var path = RoundedRect(Rectangle.Round(rect), 12);
        e.Graphics.FillPath(bg, path);
        e.Graphics.DrawPath(border, path);

        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f + 2f;
        var orbit = new RectangleF(cx - size * 0.20f, cy - size * 0.34f, size * 0.40f, size * 0.68f);
        using var orbitPen = new Pen(UiPalette.Accent, 2.4f);
        using var sidePen = new Pen(Color.FromArgb(210, UiPalette.Accent2), 2.0f);
        using var headPen = new Pen(Color.FromArgb(235, 244, 248), 3.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var dot = new SolidBrush(Color.FromArgb(248, UiPalette.Accent));

        e.Graphics.DrawEllipse(orbitPen, orbit);
        e.Graphics.DrawArc(headPen, cx - size * 0.25f, cy - size * 0.24f, size * 0.50f, size * 0.42f, 200, 140);
        e.Graphics.DrawLine(sidePen, cx - size * 0.27f, cy - size * 0.02f, cx - size * 0.27f, cy + size * 0.16f);
        e.Graphics.DrawLine(sidePen, cx + size * 0.27f, cy - size * 0.02f, cx + size * 0.27f, cy + size * 0.16f);
        e.Graphics.FillEllipse(dot, cx - 4f, orbit.Top - 2f, 8f, 8f);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
