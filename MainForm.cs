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
    private readonly Button _refreshButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly CheckBox _sameDeviceModeCheck = new();
    private readonly Label _routeStatusLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _latencyLabel = new();

    public MainForm()
    {
        Text = "实时 8D";
        MinimumSize = new Size(680, 500);
        Size = new Size(720, 540);
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
            Padding = new Padding(24, 20, 24, 20),
            BackColor = UiPalette.App
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildRoutingPanel(), 0, 1);
        root.Controls.Add(BuildStatusPanel(), 0, 2);
        ResumeLayout();
    }

    private Control BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(4, 0, 4, 0),
            BackColor = Color.Transparent
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(grid);

        var logo = new AppLogoView
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 16, 0)
        };
        grid.Controls.Add(logo, 0, 0);

        var titleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 6)
        };
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        grid.Controls.Add(titleStack, 1, 0);

        titleStack.Controls.Add(new Label
        {
            Text = "实时 8D 耳机处理器",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            ForeColor = UiPalette.Text,
            TextAlign = ContentAlignment.BottomLeft,
            AutoSize = false
        }, 0, 0);
        titleStack.Controls.Add(new Label
        {
            Text = "水平环绕轨道驱动全向空间感 · WASAPI 实时捕获",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 9F),
            ForeColor = UiPalette.Muted,
            TextAlign = ContentAlignment.TopLeft,
            AutoSize = false
        }, 0, 1);

        return panel;
    }

    private Control BuildRoutingPanel()
    {
        var card = NewCard("音频路由");
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(grid);

        StyleCombo(_captureCombo);
        StyleCombo(_outputCombo);
        _captureCombo.SelectedIndexChanged += (_, _) => UpdateRouteState();
        _outputCombo.SelectedIndexChanged += (_, _) => UpdateRouteState();

        StyleButton(_refreshButton, "刷新", UiPalette.Button, UiPalette.Text);
        _refreshButton.Click += (_, _) => LoadDevices();

        _sameDeviceModeCheck.Text = "同设备模式";
        _sameDeviceModeCheck.Checked = true;
        _sameDeviceModeCheck.Dock = DockStyle.Fill;
        _sameDeviceModeCheck.ForeColor = UiPalette.Text;
        _sameDeviceModeCheck.BackColor = Color.Transparent;
        _sameDeviceModeCheck.Font = new Font(Font.FontFamily, 9F);
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

        return card;
    }

    private Control BuildStatusPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiPalette.CardBg,
            Padding = new Padding(20, 0, 20, 0)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.Controls.Add(grid);

        _statusLabel.Text = "就绪";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = UiPalette.Muted;
        _statusLabel.Font = new Font(Font.FontFamily, 9F);
        _latencyLabel.Text = "--";
        _latencyLabel.Dock = DockStyle.Fill;
        _latencyLabel.TextAlign = ContentAlignment.MiddleRight;
        _latencyLabel.ForeColor = UiPalette.Muted;
        _latencyLabel.Font = new Font(Font.FontFamily, 9F);

        StyleButton(_startButton, "开始处理", UiPalette.Accent, Color.FromArgb(8, 14, 16));
        _startButton.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
        _startButton.Click += (_, _) => StartEngine();

        StyleButton(_stopButton, "停止", UiPalette.Stop, Color.White);
        _stopButton.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopEngine();

        grid.Controls.Add(_statusLabel, 0, 0);
        grid.Controls.Add(_latencyLabel, 1, 0);
        grid.Controls.Add(_startButton, 2, 0);
        grid.Controls.Add(_stopButton, 3, 0);
        return panel;
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
                _statusLabel.Text = $"已找到 {devices.Length} 个播放设备";
            }
            else
            {
                _statusLabel.Text = "没有找到可用设备";
            }
        }
        catch (Exception ex)
        {
            DeviceDiagnostics.WriteDeviceReport(ex);
            _statusLabel.Text = $"扫描失败：{ex.Message}";
        }

        UpdateRouteState();
    }

    private static SpatialSettings BuildSettings()
    {
        return new SpatialSettings(
            Enabled: true,
            InputGain: 0.84f,
            OutputGain: 0.80f,
            RotationHz: 0.12f,
            Depth: 0.90f,
            CircleStrength: 3.00f,
            HrtfStrength: 1.00f,
            ReverbWet: 0.28f,
            LimiterThreshold: 0.88f);
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
            ? "请选择可用设备。"
            : sameDeviceMode && !sameDeviceSupported
                ? "同设备模式需要 Windows 10 Build 20348+"
                : sameDeviceMode
                    ? "同设备模式保留原声；独立路由更纯净"
                    : sameDevice
                        ? "请避免捕获与输出使用同一设备。"
                        : "路由就绪。";
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
            Padding = new Padding(20, 42, 20, 16),
            Margin = new Padding(0, 0, 0, 16),
            FillColor = UiPalette.CardBg,
            BorderColor = UiPalette.CardBorder,
            Title = title
        };
        return card;
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
        combo.BackColor = UiPalette.InputBg;
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
        button.Margin = new Padding(6, 4, 0, 4);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}

internal static class UiPalette
{
    // Pure black canvas
    public static readonly Color App = Color.FromArgb(0, 0, 0);

    // Card surfaces — subtle lift from pure black
    public static readonly Color CardBg = Color.FromArgb(14, 14, 14);
    public static readonly Color CardBorder = Color.FromArgb(30, 30, 30);

    // Input elements
    public static readonly Color InputBg = Color.FromArgb(18, 18, 18);

    // Button surface
    public static readonly Color Button = Color.FromArgb(22, 22, 22);

    // Text hierarchy
    public static readonly Color Text = Color.FromArgb(230, 230, 230);
    public static readonly Color Muted = Color.FromArgb(100, 100, 100);

    // Accent — warm white with slight cool tint for contrast
    public static readonly Color Accent = Color.FromArgb(212, 225, 230);
    public static readonly Color Accent2 = Color.FromArgb(140, 170, 200);

    // Status
    public static readonly Color Stop = Color.FromArgb(220, 70, 70);
    public static readonly Color Warning = Color.FromArgb(220, 180, 60);
    public static readonly Color Success = Color.FromArgb(90, 180, 130);
}

internal sealed class CardPanel : Panel
{
    public string Title { get; set; } = string.Empty;
    public Color FillColor { get; set; } = UiPalette.CardBg;
    public Color BorderColor { get; set; } = UiPalette.CardBorder;

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
        using var path = RoundedRect(rect, 12);
        using var fill = new SolidBrush(FillColor);
        using var border = new Pen(BorderColor, 1f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        if (!string.IsNullOrWhiteSpace(Title))
        {
            using var titleBrush = new SolidBrush(UiPalette.Muted);
            using var font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            e.Graphics.DrawString(Title, font, titleBrush, new PointF(18, 14));
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

internal sealed class AppLogoView : Control
{
    public AppLogoView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        MinimumSize = new Size(52, 52);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var size = Math.Min(Width, Height) - 4;
        var x = (Width - size) / 2f;
        var y = (Height - size) / 2f;
        var rect = new RectangleF(x, y, size, size);

        using var bg = new LinearGradientBrush(rect, Color.FromArgb(18, 18, 18), Color.FromArgb(8, 8, 8), 45f);
        using var border = new Pen(Color.FromArgb(60, 60, 60), 1f);
        using var path = RoundedRect(Rectangle.Round(rect), 10);
        e.Graphics.FillPath(bg, path);
        e.Graphics.DrawPath(border, path);

        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f + 2f;
        var orbit = new RectangleF(cx - size * 0.18f, cy - size * 0.32f, size * 0.36f, size * 0.64f);
        using var orbitPen = new Pen(Color.FromArgb(140, 140, 140), 1.4f);
        using var headPen = new Pen(Color.FromArgb(200, 200, 200), 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var dot = new SolidBrush(Color.FromArgb(220, 220, 220));

        e.Graphics.DrawEllipse(orbitPen, orbit);
        e.Graphics.DrawArc(headPen, cx - size * 0.22f, cy - size * 0.22f, size * 0.44f, size * 0.38f, 200, 140);
        e.Graphics.FillEllipse(dot, cx - 3f, orbit.Top - 1f, 6f, 6f);
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
