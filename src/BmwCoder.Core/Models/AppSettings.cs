using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmwCoder.Core.Models;

public sealed class AppSettings
{
    public string Host { get; set; } = "169.254.16.17";
    public int Port { get; set; } = 6801;
    public string LastVin { get; set; } = string.Empty;
    public string PsdzDataPath { get; set; } = string.Empty;
    public string PsdzSeries { get; set; } = "F020";
    public string IstaPath { get; set; } = string.Empty;
    public string EsysPath { get; set; } = string.Empty;
    public string JavaPath { get; set; } = string.Empty;
    [JsonExtensionData]
    public System.Collections.Generic.Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public static AppSettings LoadFrom(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
            ?? throw new FormatException("Settings file must contain an object, not null.");
        settings.Validate();
        return settings;
    }

    public void SaveTo(string path)
    {
        Validate();
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535)
            throw new FormatException("Settings require a host and port between 1 and 65535.");
        if (PsdzDataPath == null || EsysPath == null || IstaPath == null || JavaPath == null || string.IsNullOrWhiteSpace(PsdzSeries))
            throw new FormatException("Settings paths cannot be null; use empty strings for unconfigured tools, and specify a series.");
    }

    public static string GetSettingsPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string folder = Path.Combine(appData, "BmwCoder");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Fallback to default
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = GetSettingsPath();
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore settings save errors on shutdown
        }
    }
}
