using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace JarvisAI.Infrastructure.Automation;

public interface IFileEncryptorService
{
    Task<EncryptionResult> EncryptFileAsync(string inputPath, string password, string? outputPath = null, CancellationToken ct = default);
    Task<EncryptionResult> DecryptFileAsync(string inputPath, string password, string? outputPath = null, CancellationToken ct = default);
    Task<EncryptionResult> EncryptFolderAsync(string folderPath, string password, CancellationToken ct = default);
    Task<EncryptionResult> DecryptFolderAsync(string folderPath, string password, CancellationToken ct = default);
}

public sealed class FileEncryptorService : IFileEncryptorService
{
    private readonly ILogger<FileEncryptorService> _logger;

    public FileEncryptorService(ILogger<FileEncryptorService> logger)
    {
        _logger = logger;
    }

    public async Task<EncryptionResult> EncryptFileAsync(string inputPath, string password, string? outputPath = null, CancellationToken ct = default)
    {
        var result = new EncryptionResult { InputPath = inputPath };

        if (!File.Exists(inputPath))
        {
            result.ErrorMessage = $"File not found: {inputPath}";
            return result;
        }

        var output = outputPath ?? inputPath + ".encrypted";
        result.OutputPath = output;

        try
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = DeriveKey(password, salt);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();

            await using var inputStream = File.OpenRead(inputPath);
            await using var outputStream = File.Create(output);
            await outputStream.WriteAsync(salt, ct);
            await outputStream.WriteAsync(aes.IV, ct);

            using var cryptoStream = new CryptoStream(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write);
            await inputStream.CopyToAsync(cryptoStream, ct);

            result.Success = true;
            result.OutputSizeBytes = new FileInfo(output).Length;
            _logger.LogInformation("[Encrypt] Encrypted: {Input} → {Output}", inputPath, output);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Encrypt] Failed: {Input}", inputPath);
        }

        return result;
    }

    public async Task<EncryptionResult> DecryptFileAsync(string inputPath, string password, string? outputPath = null, CancellationToken ct = default)
    {
        var result = new EncryptionResult { InputPath = inputPath };

        if (!File.Exists(inputPath))
        {
            result.ErrorMessage = $"File not found: {inputPath}";
            return result;
        }

        var output = outputPath ?? Path.ChangeExtension(inputPath, null);
        result.OutputPath = output;

        try
        {
            await using var inputStream = File.OpenRead(inputPath);
            var salt = new byte[16];
            var iv = new byte[16];
            await inputStream.ReadAsync(salt, ct);
            await inputStream.ReadAsync(iv, ct);

            var key = DeriveKey(password, salt);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;

            using var cryptoStream = new CryptoStream(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read);
            await using var outputStream = File.Create(output);
            await cryptoStream.CopyToAsync(outputStream, ct);

            result.Success = true;
            result.OutputSizeBytes = new FileInfo(output).Length;
            _logger.LogInformation("[Encrypt] Decrypted: {Input} → {Output}", inputPath, output);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = "Decryption failed (wrong password or corrupted file)";
            _logger.LogWarning(ex, "[Encrypt] Decryption failed: {Input}", inputPath);
        }

        return result;
    }

    public async Task<EncryptionResult> EncryptFolderAsync(string folderPath, string password, CancellationToken ct = default)
    {
        var result = new EncryptionResult { InputPath = folderPath };

        if (!Directory.Exists(folderPath))
        {
            result.ErrorMessage = $"Folder not found: {folderPath}";
            return result;
        }

        foreach (var file in Directory.GetFiles(folderPath))
        {
            if (ct.IsCancellationRequested) break;
            var fileResult = await EncryptFileAsync(file, password, ct: ct);
            if (fileResult.Success) result.FilesProcessed++;
        }

        result.Success = true;
        return result;
    }

    public async Task<EncryptionResult> DecryptFolderAsync(string folderPath, string password, CancellationToken ct = default)
    {
        var result = new EncryptionResult { InputPath = folderPath };

        if (!Directory.Exists(folderPath))
        {
            result.ErrorMessage = $"Folder not found: {folderPath}";
            return result;
        }

        foreach (var file in Directory.GetFiles(folderPath, "*.encrypted"))
        {
            if (ct.IsCancellationRequested) break;
            var fileResult = await DecryptFileAsync(file, password, ct: ct);
            if (fileResult.Success) result.FilesProcessed++;
        }

        result.Success = true;
        return result;
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}

public sealed class EncryptionResult
{
    public bool Success { get; set; }
    public string InputPath { get; set; } = "";
    public string? OutputPath { get; set; }
    public long OutputSizeBytes { get; set; }
    public int FilesProcessed { get; set; }
    public string? ErrorMessage { get; set; }
}
