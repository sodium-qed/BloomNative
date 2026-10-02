using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace BloomNative.Windows;

public sealed class Settings
{
    public double Progress { get; set; } = 1;
    public bool Breathe { get; set; } = true;
    public bool ReplayOnWake { get; set; } = true;
    public string Language { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BloomNative");
    public static string DefaultPath => Path.Combine(DataDirectory, "settings.json");
    public void Normalize()
    {
        Progress = BloomMath.ClampProgress(Progress);
        Language = Language == "zh" ? "zh" : "en";
    }
    public static Settings Load(string? path = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path ?? DefaultPath)) ?? new Settings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Settings(); }
    }
    public void Save(string? path = null)
    {
        Normalize();
        string target = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, target, true);
    }
}
