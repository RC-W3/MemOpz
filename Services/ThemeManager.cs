using System.Windows;
using Microsoft.Win32;

namespace MemOpz.Services;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// 主题：读取系统「应用深浅色」设置（HKCU\...\Themes\Personalize\AppsUseLightTheme），
/// 通过替换合并资源字典实时切换；颜色完全追随系统。
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>读取系统当前应用主题。</summary>
    public static AppTheme Detect()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch
        {
            // 注册表不可用时回退浅色（Windows 默认）
        }
        return AppTheme.Light;
    }

    /// <summary>应用主题（替换 Themes 合并字典）。</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        string file = theme == AppTheme.Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        var uri = new Uri($"pack://application:,,,/{file}", UriKind.Absolute);

        ResourceDictionary? existing = null;
        foreach (ResourceDictionary dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.Source is { } source && source.OriginalString.Contains("/Themes/"))
            {
                existing = dict;
                break;
            }
        }

        if (existing is not null)
        {
            Application.Current.Resources.MergedDictionaries.Remove(existing);
        }
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }
}
