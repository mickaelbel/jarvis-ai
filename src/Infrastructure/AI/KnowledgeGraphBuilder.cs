using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface IKnowledgeGraphBuilder
{
    void AddEntity(string name, string type, Dictionary<string, string>? properties = null);
    void AddRelation(string fromEntity, string toEntity, string relationType, Dictionary<string, string>? properties = null);
    IReadOnlyList<KnowledgeEntity> GetEntities(string? type = null);
    IReadOnlyList<KnowledgeRelation> GetRelations(string? fromEntity = null, string? toEntity = null);
    IReadOnlyList<KnowledgeEntity> SearchEntities(string query);
    KnowledgeGraphStats GetStats();
    string ExportGraph();
    void ImportGraph(string json);
}

public sealed class KnowledgeGraphBuilder : IKnowledgeGraphBuilder
{
    private readonly ILogger<KnowledgeGraphBuilder> _logger;
    private readonly string _storagePath;
    private readonly List<KnowledgeEntity> _entities = new();
    private readonly List<KnowledgeRelation> _relations = new();

    public KnowledgeGraphBuilder(ILogger<KnowledgeGraphBuilder> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "knowledge_graph.json");
        Load();
    }

    public void AddEntity(string name, string type, Dictionary<string, string>? properties = null)
    {
        if (_entities.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && e.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            return;

        var entity = new KnowledgeEntity
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Type = type,
            Properties = properties ?? new(),
            CreatedAt = DateTime.UtcNow
        };

        _entities.Add(entity);
        Save();
        _logger.LogDebug("[KGraph] Entity added: {Name} ({Type})", name, type);
    }

    public void AddRelation(string fromEntity, string toEntity, string relationType, Dictionary<string, string>? properties = null)
    {
        var from = _entities.FirstOrDefault(e => e.Name.Equals(fromEntity, StringComparison.OrdinalIgnoreCase));
        var to = _entities.FirstOrDefault(e => e.Name.Equals(toEntity, StringComparison.OrdinalIgnoreCase));

        if (from is null || to is null) return;

        var relation = new KnowledgeRelation
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            FromEntityId = from.Id,
            ToEntityId = to.Id,
            RelationType = relationType,
            Properties = properties ?? new(),
            CreatedAt = DateTime.UtcNow
        };

        _relations.Add(relation);
        Save();
    }

    public IReadOnlyList<KnowledgeEntity> GetEntities(string? type = null)
    {
        if (type is null) return _entities;
        return _entities.Where(e => e.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public IReadOnlyList<KnowledgeRelation> GetRelations(string? fromEntity = null, string? toEntity = null)
    {
        var query = _relations.AsEnumerable();

        if (fromEntity is not null)
        {
            var from = _entities.FirstOrDefault(e => e.Name.Equals(fromEntity, StringComparison.OrdinalIgnoreCase));
            if (from is not null)
                query = query.Where(r => r.FromEntityId == from.Id);
        }

        if (toEntity is not null)
        {
            var to = _entities.FirstOrDefault(e => e.Name.Equals(toEntity, StringComparison.OrdinalIgnoreCase));
            if (to is not null)
                query = query.Where(r => r.ToEntityId == to.Id);
        }

        return query.ToList();
    }

    public IReadOnlyList<KnowledgeEntity> SearchEntities(string query)
    {
        return _entities
            .Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       e.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       e.Properties.Values.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public KnowledgeGraphStats GetStats()
    {
        var typeGroups = _entities.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        var relationGroups = _relations.GroupBy(r => r.RelationType).ToDictionary(g => g.Key, g => g.Count());

        return new KnowledgeGraphStats
        {
            TotalEntities = _entities.Count,
            TotalRelations = _relations.Count,
            EntityTypes = typeGroups,
            RelationTypes = relationGroups,
            AverageRelationsPerEntity = _entities.Count > 0 ? (double)_relations.Count / _entities.Count : 0
        };
    }

    public string ExportGraph()
    {
        var data = new { entities = _entities, relations = _relations };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    public void ImportGraph(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("entities", out var entitiesEl))
            {
                var entitiesJson = entitiesEl.GetRawText();
                var imported = JsonSerializer.Deserialize<List<KnowledgeEntity>>(entitiesJson);
                if (imported is not null)
                {
                    foreach (var entity in imported)
                    {
                        if (!_entities.Any(e => e.Name == entity.Name && e.Type == entity.Type))
                            _entities.Add(entity);
                    }
                }
            }

            if (root.TryGetProperty("relations", out var relationsEl))
            {
                var relationsJson = relationsEl.GetRawText();
                var imported = JsonSerializer.Deserialize<List<KnowledgeRelation>>(relationsJson);
                if (imported is not null) _relations.AddRange(imported);
            }

            Save();
            _logger.LogInformation("[KGraph] Graph imported");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[KGraph] Import error");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("entities", out var entitiesEl))
                {
                    var entitiesJson = entitiesEl.GetRawText();
                    var loaded = JsonSerializer.Deserialize<List<KnowledgeEntity>>(entitiesJson);
                    if (loaded is not null) _entities.AddRange(loaded);
                }

                if (root.TryGetProperty("relations", out var relationsEl))
                {
                    var relationsJson = relationsEl.GetRawText();
                    var loaded = JsonSerializer.Deserialize<List<KnowledgeRelation>>(relationsJson);
                    if (loaded is not null) _relations.AddRange(loaded);
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { entities = _entities, relations = _relations };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class KnowledgeEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed class KnowledgeRelation
{
    public string Id { get; set; } = "";
    public string FromEntityId { get; set; } = "";
    public string ToEntityId { get; set; } = "";
    public string RelationType { get; set; } = "";
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed class KnowledgeGraphStats
{
    public int TotalEntities { get; set; }
    public int TotalRelations { get; set; }
    public Dictionary<string, int> EntityTypes { get; set; } = new();
    public Dictionary<string, int> RelationTypes { get; set; } = new();
    public double AverageRelationsPerEntity { get; set; }
}
