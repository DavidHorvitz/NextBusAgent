using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BusPulse.Configuration;

public sealed class AppConfigStore
{
    private const int MaxConfigBytes = 128 * 1024;
    private static readonly byte[] Entropy = "BusPulse.Config.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BusPulse",
        "settings.dpapi");

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
        {
            return new AppConfig();
        }

        var protectedBytes = await File.ReadAllBytesAsync(_configPath, cancellationToken);
        if (protectedBytes.Length is 0 or > MaxConfigBytes)
        {
            throw new InvalidDataException("The protected configuration has an invalid size.");
        }

        var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
            ?? throw new InvalidDataException("The configuration is empty.");
        MigrateLegacySettings(config);
        config.Validate();
        return config;
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        config.Validate();
        var json = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = _configPath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
        File.Move(temporaryPath, _configPath, overwrite: true);
    }

    private static void MigrateLegacySettings(AppConfig config)
    {
        if (config.SavedPlaces.Count == 0 && !string.IsNullOrWhiteSpace(config.LegacyStopCode))
        {
            var previousStop = new SavedPlace
            {
                Name = "מקום קודם",
                StopCode = config.LegacyStopCode
            };
            config.SavedPlaces.Add(previousStop);
            config.StartPlaceId = previousStop.Id;
        }

        config.LegacyStopCode = null;
        config.ApiEndpoint = null;
        config.ApiKey = null;
    }
}