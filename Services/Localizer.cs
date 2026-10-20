using System.Globalization;
using System.Windows;

namespace MemOpz.Services;

/// <summary>
/// 中英双语：替换 Strings 合并资源字典；XAML 用 DynamicResource 自动刷新，
/// 代码中用 T(key) / T(key, args) 读取。
/// </summary>
public static class Localizer
{
    public const string ZhCN = "zh-CN";
    public const string EnUS = "en-US";

    public static string Current { get; private set; } = ZhCN;

    /// <summary>空值/未知值时按系统 UI 语言回退。</summary>
    public static string Normalize(string? code)
    {
        if (string.Equals(code, EnUS, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
        {
            return EnUS;
        }
        if (string.Equals(code, ZhCN, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "zh-hans", StringComparison.OrdinalIgnoreCase))
        {
            return ZhCN;
        }

        // 跟随系统
        string twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.Equals(twoLetter, "zh", StringComparison.OrdinalIgnoreCase) ? ZhCN : EnUS;
    }

    public static void Apply(string? code)
    {
        Current = Normalize(code);
        string file = $"Resources/Strings.{Current}.xaml";
        var uri = new Uri($"pack://application:,,,/{file}", UriKind.Absolute);

        ResourceDictionary? existing = null;
        foreach (ResourceDictionary dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.Source is { } source && source.OriginalString.Contains("/Strings."))
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

    /// <summary>取当前语言文本，缺失时返回 key 本身。</summary>
    public static string T(string key)
        => Application.Current.TryFindResource(key) as string ?? key;

    /// <summary>取当前语言文本并格式化。</summary>
    public static string T(string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(key), args);
        }
        catch (FormatException)
        {
            return T(key);
        }
    }
}
