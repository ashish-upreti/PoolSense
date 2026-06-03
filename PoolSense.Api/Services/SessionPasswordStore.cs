using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoolSense.Api.Contracts;

namespace PoolSense.Api.Services;

public sealed class SessionPasswordStore : ISessionPasswordStore
{
    private sealed class PersistedEntry
    {
        public string Jti { get; init; } = string.Empty;

        public string ProtectedPassword { get; init; } = string.Empty;

        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class StoredEntry
    {
        public string ProtectedPassword { get; init; } = string.Empty;

        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private static readonly JsonSerializerOptions PersistSerializerOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _decryptionFailures = new(StringComparer.Ordinal);
    private readonly string _storeFilePath;
    private readonly object _syncRoot = new();
    private readonly ILogger<SessionPasswordStore> _logger;

    public SessionPasswordStore(IHostEnvironment hostEnvironment, ILogger<SessionPasswordStore> logger)
    {
        _logger = logger;
        var authStateDirectory = Path.Combine(hostEnvironment.ContentRootPath, "artifacts", "auth-state");
        Directory.CreateDirectory(authStateDirectory);
        _storeFilePath = Path.Combine(authStateDirectory, "session-password-store.json");
        LoadFromDisk();
    }

    public void Store(string jti, string password, DateTimeOffset expiresAtUtc)
    {
        _entries[jti] = new StoredEntry
        {
            ProtectedPassword = ProtectPassword(password),
            ExpiresAtUtc = expiresAtUtc
        };

        PersistToDisk();
    }

    public bool TryGet(string jti, out SessionPasswordEntry? entry)
    {
        entry = null;
        if (!_entries.TryGetValue(jti, out var stored))
        {
            return false;
        }

        if (stored.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(jti, out _);
            PersistToDisk();
            return false;
        }

        if (!TryUnprotectPassword(stored.ProtectedPassword, out var password, jti))
        {
            _entries.TryRemove(jti, out _);
            _decryptionFailures[jti] = DateTimeOffset.UtcNow.ToString("O");
            PersistToDisk();
            return false;
        }

        entry = new SessionPasswordEntry
        {
            Password = password,
            ExpiresAtUtc = stored.ExpiresAtUtc
        };

        return true;
    }

    public bool HasDecryptionFailure(string jti)
    {
        return _decryptionFailures.ContainsKey(jti);
    }

    public void Remove(string jti)
    {
        _entries.TryRemove(jti, out _);
        PersistToDisk();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_storeFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_storeFilePath);
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json) ?? [];
            var nowUtc = DateTimeOffset.UtcNow;

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Jti)
                    || string.IsNullOrWhiteSpace(entry.ProtectedPassword)
                    || entry.ExpiresAtUtc <= nowUtc)
                {
                    continue;
                }

                _entries[entry.Jti] = new StoredEntry
                {
                    ProtectedPassword = entry.ProtectedPassword,
                    ExpiresAtUtc = entry.ExpiresAtUtc
                };
            }

            PersistToDisk();
        }
        catch
        {
        }
    }

    private void PersistToDisk()
    {
        lock (_syncRoot)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var persistedEntries = _entries
                .Where(pair => pair.Value.ExpiresAtUtc > nowUtc)
                .Select(pair => new PersistedEntry
                {
                    Jti = pair.Key,
                    ProtectedPassword = pair.Value.ProtectedPassword,
                    ExpiresAtUtc = pair.Value.ExpiresAtUtc
                })
                .OrderBy(entry => entry.ExpiresAtUtc)
                .ToArray();

            var json = JsonSerializer.Serialize(persistedEntries, PersistSerializerOptions);

            var tempFilePath = _storeFilePath + ".tmp";
            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _storeFilePath, true);
        }
    }

    private static string ProtectPassword(string password)
    {
        if (OperatingSystem.IsWindows())
        {
            var bytes = Encoding.UTF8.GetBytes(password);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(protectedBytes);
        }

        return password;
    }

    private bool TryUnprotectPassword(string protectedPassword, out string password, string jti)
    {
        password = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            password = protectedPassword;
            return true;
        }

        try
        {
            var bytes = Convert.FromBase64String(protectedPassword);
            var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
            password = Encoding.UTF8.GetString(unprotected);
            return true;
        }
        catch (CryptographicException)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return false;
                }

                var bytes = Convert.FromBase64String(protectedPassword);
                var unprotected = UnprotectWithCurrentUser(bytes);
                password = Encoding.UTF8.GetString(unprotected);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt session password for JTI {Jti}.", jti);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt session password for JTI {Jti}.", jti);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWithCurrentUser(byte[] protectedBytes)
    {
        return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
    }
}