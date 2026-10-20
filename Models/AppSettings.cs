namespace MemOpz.Models;

/// <summary>应用设置，持久化到 %AppData%\MemOpz\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>RAM 占用超过阈值时自动清理。</summary>
    public bool AutoCleanEnabled { get; set; }

    /// <summary>自动清理阈值（百分比，70-95）。</summary>
    public int AutoCleanThreshold { get; set; } = 90;

    /// <summary>开机自启动（写入 HKCU Run 项）。</summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>内存检测间隔（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>界面语言：zh-CN / en-US；为空则跟随系统。</summary>
    public string? Language { get; set; }
}
