using System.Text.Json;
using JarvisAI.Application.Biometry;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Biometry;

/// <summary>
/// JSON-based face database stored in %LOCALAPPDATA%/JarvisAI/faces/
/// </summary>
public sealed class FaceDatabase
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly object _lock = new();

    public FaceDatabase()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "faces");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "faces.json");
    }

    public string PhotosDir => _dir;

    public IReadOnlyList<FaceEnrollment> LoadAll()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    var json = File.ReadAllText(_dbPath);
                    var list = JsonSerializer.Deserialize<List<FaceEnrollment>>(json) ?? new();
                    return list;
                }
            }
            catch { }
            return new List<FaceEnrollment>();
        }
    }

    public void Save(FaceEnrollment enrollment)
    {
        lock (_lock)
        {
            var list = LoadAll().ToList();
            var idx = list.FindIndex(f => f.Id == enrollment.Id);
            if (idx >= 0) list[idx] = enrollment;
            else list.Add(enrollment);
            Persist(list);
        }
    }

    public bool Delete(string idOrName)
    {
        lock (_lock)
        {
            var list = LoadAll().ToList();
            var removed = list.RemoveAll(f =>
                f.Id == idOrName ||
                f.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Persist(list);
            return removed > 0;
        }
    }

    private void Persist(List<FaceEnrollment> list)
    {
        try
        {
            // Store only metadata, not embeddings (too large for JSON)
            var slim = list.Select(f => new
            {
                f.Id, f.Name, f.Notes, f.EnrolledAt, f.PhotoPath,
                EmbeddingLength = f.Embedding.Length
            }).ToList();

            var json = JsonSerializer.Serialize(slim, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dbPath, json);

            // Store embeddings separately as binary files
            foreach (var f in list)
            {
                if (f.Embedding.Length > 0)
                {
                    var embPath = Path.Combine(_dir, $"{f.Id}.emb");
                    var bytes = new byte[f.Embedding.Length * sizeof(float)];
                    Buffer.BlockCopy(f.Embedding, 0, bytes, 0, bytes.Length);
                    File.WriteAllBytes(embPath, bytes);
                }
            }
        }
        catch { }
    }

    private List<FaceEnrollment> LoadAllWithEmbeddings()
    {
        var list = LoadAll().ToList();
        foreach (var f in list)
        {
            var embPath = Path.Combine(_dir, $"{f.Id}.emb");
            if (File.Exists(embPath))
            {
                var bytes = File.ReadAllBytes(embPath);
                var floats = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
                f.Embedding = floats;
            }
        }
        return list;
    }
}
