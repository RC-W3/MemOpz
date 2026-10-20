using System.Threading;
using MemOpz.Services;
using Application = System.Windows.Application;
using ExitEventArgs = System.Windows.ExitEventArgs;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace MemOpz;

public partial class App : Application
{
    private const string MutexName = "Local\\MemOpz_SingleInstance_v1";
    private const string ShowEventName = "Local\\MemOpz_ShowWindow_v1";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _listenerCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 窗口创建前先应用语言与系统主题，避免首帧闪烁
        var settingsService = new SettingsService();
        var settings = settingsService.Load();
        Localizer.Apply(settings.Language);
        ThemeManager.Apply(ThemeManager.Detect());

        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        if (!createdNew)
        {
            // 已有实例运行：通知其前置主窗口后退出
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 忽略
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventName);
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;
        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_showEvent.WaitOne(500))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (MainWindow is MainWindow window)
                        {
                            window.ActivateWindow();
                        }
                    });
                }
            }
        }, token);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _listenerCts?.Cancel();
        _showEvent?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
