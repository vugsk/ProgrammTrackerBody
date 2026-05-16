using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ProgrammTrackerBody.Domain;

namespace ProgrammTrackerBody.Persistence;

public sealed class TrackerConfigEntry
{
    public string DisplayName { get; set; } = string.Empty;
    public BodyPart BodyPart { get; set; } = BodyPart.None;
    public MountingOrientation Mounting { get; set; } = MountingOrientation.Front;
    public float[] YawOffset { get; set; } = QuaternionUtil.IdentityArray();
    public float[] FullOffset { get; set; } = QuaternionUtil.IdentityArray();
    public float[] MountingOffset { get; set; } = QuaternionUtil.IdentityArray();
}

public sealed class AppConfig
{
    public string Language { get; set; } = "ru";
    public int LastPort { get; set; } = 6969;
    public Dictionary<string, TrackerConfigEntry> Trackers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class QuaternionUtil
{
    public static float[] IdentityArray() => new[] { 0f, 0f, 0f, 1f };

    public static System.Numerics.Quaternion FromArray(float[]? a)
    {
        if (a is null || a.Length != 4)
        {
            return System.Numerics.Quaternion.Identity;
        }

        return new System.Numerics.Quaternion(a[0], a[1], a[2], a[3]);
    }

    public static float[] ToArray(System.Numerics.Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ConfigStore()
        : this(GetDefaultPath())
    {
    }

    public ConfigStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public async Task<AppConfig> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppConfig();
            }

            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions);
            return loaded ?? new AppConfig();
        }
        catch (Exception)
        {
            // Corrupted config — return defaults rather than crashing the UI.
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions);
        }

        // Atomic replace so a crash mid-write can't leave a half-written config.
        File.Move(tempPath, _path, overwrite: true);
    }

    private static string GetDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "ProgrammTrackerBody", "config.json");
    }
}
