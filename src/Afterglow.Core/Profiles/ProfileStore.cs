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
                // ProgramData hardening: when elevated, a profile whose owner is
                // neither an administrator/SYSTEM nor the current user may have
                // been planted by a standard user — skip it rather than trust it
                // for a GPU-write (no-op unelevated / --data-dir). Checked on the
                // handle the content is read from.
                if (Security.DataRootProtection.TryReadAllText(file) is not { } json)
                {
                    errors.Add((file, "Skipped: file owner is not trusted while running elevated."));
                    continue;
                }

                var profile = JsonSerializer.Deserialize<TuningProfile>(json, JsonOptions);
                if (profile is null)
                {
                    errors.Add((file, "Empty profile file."));
                }
                // The store is vendor-agnostic, so its corruption guard uses
                // the widest legitimate floors (Intel clamps reach 100 MHz;
                // low-power Arc parts report power minimums below 50 W). The
                // apply engine re-validates against the actual device.
                else if (profile.Validate(lockFloorMHz: 100, powerFloorW: 15) is string error)
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
            // Elevated: refuse a profile whose owner is untrusted (see LoadAll).
            return Security.DataRootProtection.TryReadAllText(path) is { } json
                ? JsonSerializer.Deserialize<TuningProfile>(json, JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Validates then atomically writes the profile. Throws on validation failure.</summary>
    public void Save(TuningProfile profile)
    {
        // Widest legitimate floors — see the load-side comment above.
        if (profile.Validate(lockFloorMHz: 100, powerFloorW: 15) is string error)
        {
            throw new InvalidOperationException($"Refusing to save invalid profile: {error}");
        }

        // Retry directory creation here — the constructor's attempt is
        // best-effort, so a store that couldn't make its folder then may be
        // able to now. A genuine failure surfaces as the IOException /
        // UnauthorizedAccessException that every Save caller already handles.
        Directory.CreateDirectory(_directory);

        // Temp + rename: the result carries the folder's permissions, and a
        // refused (untrusted-owner) target is moved aside rather than replaced
        // in a way that would inherit its grants.
        Security.DataRootProtection.WriteAllTextAtomic(
            PathFor(profile.Name), JsonSerializer.Serialize(profile, JsonOptions));
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
