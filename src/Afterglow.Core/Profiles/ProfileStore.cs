using System.Text.Json;
using System.Text.Json.Serialization;

namespace Afterglow.Core.Profiles;

/// <summary>
/// JSON profile persistence under <see cref="AppPaths.ProfilesDir"/>. One file per
/// profile, atomic writes, tolerant loading (a corrupt file is skipped and reported,
/// never crashes the app).
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public ProfileStore(string? directory = null)
    {
        _directory = directory ?? AppPaths.ProfilesDir;

        // Best-effort: a store whose directory can't be created (ProgramData
        // locked down, a read-only path, an AV block) must still construct, so
        // a host that only reads — or that never touches profiles at all — is
        // not killed at startup. Save/LoadAll retry and report instead (F-36).
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Files that failed to parse during the last <see cref="LoadAll"/>.</summary>
    public IReadOnlyList<(string File, string Error)> LastLoadErrors { get; private set; } = [];

    public IReadOnlyList<TuningProfile> LoadAll()
    {
        var profiles = new List<TuningProfile>();
        var errors = new List<(string, string)>();

        // Enumeration itself can throw (directory missing, unreadable, or not a
        // directory at all) — that used to escape LoadAll and, from the MCP
        // server's list_profiles tool, take the whole stdio process down (F-36).
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_directory, "*.json").ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadErrors = [(_directory, ex.Message)];
            return [];
        }

        foreach (string file in files)
        {
            try
            {
                var profile = JsonSerializer.Deserialize<TuningProfile>(File.ReadAllText(file), JsonOptions);
                if (profile is null)
                {
                    errors.Add((file, "Empty profile file."));
                }
                else if (profile.Validate() is string error)
                {
                    errors.Add((file, error));
                }
                else
                {
                    profiles.Add(profile);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add((file, ex.Message));
            }
        }

        LastLoadErrors = errors;
        return profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public TuningProfile? Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TuningProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Validates then atomically writes the profile. Throws on validation failure.</summary>
    public void Save(TuningProfile profile)
    {
        if (profile.Validate() is string error)
        {
            throw new InvalidOperationException($"Refusing to save invalid profile: {error}");
        }

        // Retry directory creation here — the constructor's attempt is
        // best-effort, so a store that couldn't make its folder then may be
        // able to now. A genuine failure surfaces as the IOException /
        // UnauthorizedAccessException that every Save caller already handles.
        Directory.CreateDirectory(_directory);

        string path = PathFor(profile.Name);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, JsonOptions));
        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    public bool Delete(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public string PathFor(string profileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. profileName.Select(c => invalid.Contains(c) ? '_' : c)]);
        if (safe.Length == 0)
        {
            safe = "profile";
        }

        return Path.Combine(_directory, safe + ".json");
    }
}
