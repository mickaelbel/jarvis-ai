using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Security;

public interface ISecureStorage
{
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<bool> RemoveAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken ct = default);
    Task<bool> ContainsKeyAsync(string key, CancellationToken ct = default);
}

public sealed class SecureStorage : ISecureStorage
{
    private readonly ILogger<SecureStorage> _logger;
    private readonly string _storagePath;
    private readonly byte[] _entropy;
    private Dictionary<string, string> _data = new();

    public SecureStorage(ILogger<SecureStorage> logger)
    {
        _logger = logger;
        _entropy = Encoding.UTF8.GetBytes("JarvisAI_SecureStorage_v1");
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "secure_storage.dat");
        Load();
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var encrypted = Protect(value);
        lock (_data)
        {
            _data[key] = encrypted;
        }
        await SaveAsync(ct);
        _logger.LogDebug("[SecureStorage] Set: {Key}", key);
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_data)
        {
            if (!_data.TryGetValue(key, out var encrypted))
                return Task.FromResult<string?>(null);

            try
            {
                return Task.FromResult<string?>(Unprotect(encrypted));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SecureStorage] Failed to decrypt: {Key}", key);
                return Task.FromResult<string?>(null);
            }
        }
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        bool removed;
        lock (_data)
        {
            removed = _data.Remove(key);
        }
        if (removed)
            await SaveAsync(ct);
        return removed;
    }

    public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken ct = default)
    {
        lock (_data)
        {
            return Task.FromResult((IReadOnlyList<string>)_data.Keys.ToList());
        }
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken ct = default)
    {
        lock (_data)
        {
            return Task.FromResult(_data.ContainsKey(key));
        }
    }

    private string Protect(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(plainBytes, _entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private string Unprotect(string encryptedBase64)
    {
        var encrypted = Convert.FromBase64String(encryptedBase64);
        var plainBytes = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch
        {
            _data = new();
        }
    }

    private async Task SaveAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(_storagePath);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json;
                lock (_data)
                {
                    json = JsonSerializer.Serialize(_data);
                }
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SecureStorage] Failed to save");
            }
        }, ct);
    }
}
