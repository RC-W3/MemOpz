using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MemOpz.Models;
using MemOpz.Native;
using MemOpz.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace MemOpz;

public partial class MainWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private static readonly TimeSpan AutoCleanCooldown = TimeSpan.FromSeconds(60);

    private readonly MemoryService _memory = new();
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;

    private bool _cleaning;
    private bool _suppressEvents;
    private DateTime _lastAutoClean = DateTime.MinValue;

    // 最近一次清理结果（语言切换时可重绘）
    private CleanResult? _lastResult;
    private bool _lastAuto;
    private string _currentStageKey = "StagePrep";

    public MainWindow()
    {
        InitializeComponent();

        Icon = CreateIconBitmap();

        _settings = _settingsService.Load();
        InitializeControls();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollIntervalSeconds, 1, 60))
        };
        _timer.Tick += OnTick;
        _timer.Start();
        RefreshMemoryStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        ApplySystemBackdrop(helper.Handle);

        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE)
        {
            // 系统个性化颜色/深浅色变化广播
            string? change = Marshal.PtrToStringUni(lParam);
            if (string.Equals(change, "ImmersiveColorSet", StringComparison.Ordinal))
            {
                var detected = ThemeManager.Detect();
                if (detected != ThemeManager.Current)
                {
                    ThemeManager.Apply(detected);
                    ApplySystemBackdrop(hwnd);
                }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>按当前系统主题应用 Mica 云母背景 + 标题栏深浅色 + 圆角；不支持时回退纯色。</summary>
    private void ApplySystemBackdrop(IntPtr handle)
    {
        bool dark = ThemeManager.Current == AppTheme.Dark;

        int darkMode = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(handle,
            NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        bool micaOk = false;
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            int backdrop = NativeMethods.DWMSBT_MAINWINDOW;
            micaOk = NativeMethods.DwmSetWindowAttribute(handle,
                NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }

        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);

        RootBorder.Background = micaOk
            ? Brushes.Transparent
            : Application.Current.Resources["WindowFallbackBrush"] as Brush
              ?? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
    }

    /// <summary>用 WPF 自绘窗口图标（无需 ico 文件）。</summary>
    private static BitmapSource CreateIconBitmap()
    {
        var gold = new SolidColorBrush(Color.FromRgb(0xD9, 0xB4, 0x4A));
        var dark = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
        var grid = new Grid { Width = 32, Height = 32 };

        var body = new Border
        {
            Width = 26,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = gold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        };
        var label = new TextBlock
        {
            Text = "M",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = dark,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        };

        grid.Children.Add(body);
        grid.Children.Add(label);
        grid.Measure(new Size(32, 32));
        grid.Arrange(new Rect(0, 0, 32, 32));

        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        bitmap.Freeze();
        return bitmap;
    }

    private void InitializeControls()
    {
        _suppressEvents = true;
        AutoCleanToggle.IsChecked = _settings.AutoCleanEnabled;
        AutoStartToggle.IsChecked = _settings.AutoStartEnabled;
        ThresholdSlider.Value = _settings.AutoCleanThreshold;
        ThresholdValueText.Text = $"{_settings.AutoCleanThreshold}%";
        ThresholdPanel.Visibility = _settings.AutoCleanEnabled ? Visibility.Visible : Visibility.Collapsed;

        SelectIntervalRadio(_settings.PollIntervalSeconds);
        LangZh.IsChecked = Localizer.Current == Localizer.ZhCN;
        LangEn.IsChecked = Localizer.Current == Localizer.EnUS;
        _suppressEvents = false;

        AdminBadge.Visibility = MemoryService.IsAdministrator() ? Visibility.Visible : Visibility.Collapsed;
        ElevationLink.Visibility = MemoryService.IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SelectIntervalRadio(int seconds)
    {
        RadioButton? target = seconds switch
        {
            1 => Interval1,
            5 => Interval5,
            10 => Interval10,
            _ => Interval2
        };
        target.IsChecked = true;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        RefreshMemoryStatus();

        if (!_cleaning && _settings.AutoCleanEnabled)
        {
            try
            {
                var status = _memory.GetStatus();
                if (status.LoadPercent >= _settings.AutoCleanThreshold &&
                    DateTime.Now - _lastAutoClean >= AutoCleanCooldown)
                {
                    _ = CleanAsync(auto: true);
                }
            }
            catch
            {
                // 监控失败不打扰用户
            }
        }
    }

    private void RefreshMemoryStatus()
    {
        try
        {
            var status = _memory.GetStatus();
            ulong used = status.TotalBytes - status.AvailableBytes;

            PercentText.Text = status.LoadPercent.ToString();
            UsedText.Text = Localizer.T("UsedFormat", ToGb(used), ToGb(status.TotalBytes));
            AvailableText.Text = Localizer.T("AvailableFormat", ToGb(status.AvailableBytes));
            CacheText.Text = $"{ToGb(status.SystemCacheBytes)} GB";
            RamBar.Value = status.LoadPercent;
        }
        catch
        {
            PercentText.Text = "--";
        }
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
        => await CleanAsync(auto: false);

    private async Task CleanAsync(bool auto)
    {
        if (_cleaning)
        {
            return;
        }

        _cleaning = true;
        CleanButton.IsEnabled = false;
        CleanButtonText.Visibility = Visibility.Collapsed;
        CleanBusyPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        _currentStageKey = "StagePrep";
        ProgressText.Text = Localizer.T(_currentStageKey);

        CleanResult result;
        try
        {
            var progress = new Progress<string>(key =>
            {
                _currentStageKey = key;
                ProgressText.Text = Localizer.T(key);
            });
            result = await Task.Run(() => _memory.Clean(progress));
        }
        catch (Exception ex)
        {
            ResetCleanButton();
            MessageBox.Show(this, Localizer.T("CleanFailedFormat", ex.Message), "MemOpz",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResetCleanButton();
        RefreshMemoryStatus();

        _lastResult = result;
        _lastAuto = auto;
        RenderResult();

        if (auto)
        {
            _lastAutoClean = DateTime.Now;
        }
    }

    private void RenderResult()
    {
        if (_lastResult is null)
        {
            return;
        }

        ResultModeText.Text = Localizer.T(_lastAuto ? "FreedAuto" : "FreedManual");
        FreedText.Text = " " + FormatSize(_lastResult.FreedBytes);
        ResultDetailText.Text = _lastResult.FreedBytes >= 1
            ? Localizer.T("ResultDetailFormat",
                ToGb(_lastResult.AvailableBeforeBytes),
                ToGb(_lastResult.AvailableAfterBytes),
                DateTime.Now.ToString("HH:mm:ss"))
            : Localizer.T("ResultCleanFormat", DateTime.Now.ToString("HH:mm:ss"));
        ResultPanel.Visibility = Visibility.Visible;
    }

    private void ResetCleanButton()
    {
        _cleaning = false;
        CleanButton.IsEnabled = true;
        CleanButtonText.Visibility = Visibility.Visible;
        CleanBusyPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void AutoCleanToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _settings.AutoCleanEnabled = AutoCleanToggle.IsChecked == true;
        ThresholdPanel.Visibility = _settings.AutoCleanEnabled ? Visibility.Visible : Visibility.Collapsed;
        _settingsService.Save();
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int value = (int)Math.Round(e.NewValue / 5.0) * 5;
        value = Math.Clamp(value, 70, 95);

        if (ThresholdValueText is not null)
        {
            ThresholdValueText.Text = $"{value}%";
        }
        if (_settings is not null && _settings.AutoCleanThreshold != value)
        {
            _settings.AutoCleanThreshold = value;
            _settingsService.Save();
        }
    }

    private void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        try
        {
            bool enabled = AutoStartToggle.IsChecked == true;
            AutoStartService.Set(enabled);
            _settings.AutoStartEnabled = AutoStartService.IsEnabled();
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _suppressEvents = true;
            AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
            _suppressEvents = false;
            MessageBox.Show(this, Localizer.T("AutoStartFailedFormat", ex.Message), "MemOpz",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Interval_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _settings is null || sender is not RadioButton rb)
        {
            return;
        }

        if (int.TryParse(rb.Tag as string, out int seconds) &&
            _settings.PollIntervalSeconds != seconds)
        {
            _settings.PollIntervalSeconds = seconds;
            _timer.Interval = TimeSpan.FromSeconds(seconds);
            _settingsService.Save();
        }
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _settings is null || sender is not RadioButton rb)
        {
            return;
        }

        string code = rb.Tag as string ?? Localizer.ZhCN;
        if (string.Equals(code, Localizer.Current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Localizer.Apply(code);
        _settings.Language = Localizer.Current;
        _settingsService.Save();

        // 重绘代码生成的动态文本
        RefreshMemoryStatus();
        RenderResult();
        if (_cleaning)
        {
            ProgressText.Text = Localizer.T(_currentStageKey);
        }
    }

    private void ElevationLink_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            // 用户拒绝了 UAC，保持当前实例
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>第二个实例启动时，将本窗口前置。</summary>
    public void ActivateWindow()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private static string ToGb(ulong bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0");

    /// <summary>容量格式化：≥1GB 显示 GB，否则显示 MB。</summary>
    private static string FormatSize(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
        {
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
        }
        if (bytes >= 1024UL * 1024)
        {
            return (bytes / (1024.0 * 1024)).ToString("0") + " MB";
        }
        return (bytes / 1024.0).ToString("0") + " KB";
    }
}
