using System.IO;
using System.Text.Json;
using MemOpz.Models;

namespace MemOpz.Services;

/// <summary>设置的加载与保存（%AppData%\MemOpz\settings.json）。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MemOpz");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }

        // 以注册表实际状态为准
        Current.AutoStartEnabled = AutoStartService.IsEnabled();
        return Current;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // 设置保存失败不影响主功能
        }
    }
}
