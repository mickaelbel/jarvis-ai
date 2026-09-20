using System.Text.Json;

namespace JarvisAI.Infrastructure.AutoImprovement;

/// <summary>
/// A single improvement proposal with its code change and status.
/// </summary>
public sealed class ImprovementProposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Description { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string OldCode { get; set; } = "";
    public string NewCode { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending, applied, rejected, rolled_back
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Stores improvement proposals with rollback capability.
/// </summary>
public sealed class ImprovementStore
{
    private readonly string _dir;
    private readonly string _proposalsPath;
    private readonly object _lock = new();

    public ImprovementStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "improvements");
        Directory.CreateDirectory(_dir);
        _proposalsPath = Path.Combine(_dir, "proposals.json");
    }

    public void SaveProposal(ImprovementProposal proposal)
    {
        lock (_lock)
        {
            var list = LoadAll();
            var existing = list.FindIndex(p => p.Id == proposal.Id);
            if (existing >= 0) list[existing] = proposal;
            else list.Add(proposal);
            Persist(list);
        }
    }

    public ImprovementProposal? GetProposal(string id)
    {
        lock (_lock)
        {
            return LoadAll().FirstOrDefault(p => p.Id == id);
        }
    }

    public IReadOnlyList<ImprovementProposal> GetPendingProposals()
    {
        lock (_lock)
        {
            return LoadAll()
                .Where(p => p.Status == "pending")
                .OrderByDescending(p => p.Timestamp)
                .ToList();
        }
    }

    public IReadOnlyList<ImprovementProposal> GetAll(int limit = 50)
    {
        lock (_lock)
        {
            return LoadAll()
                .OrderByDescending(p => p.Timestamp)
                .Take(limit)
                .ToList();
        }
    }

    public bool ApplyProposal(string id)
    {
        lock (_lock)
        {
            var list = LoadAll();
            var proposal = list.FirstOrDefault(p => p.Id == id);
            if (proposal is null || proposal.Status != "pending") return false;

            try
            {
                // Backup the current file before modifying
                if (File.Exists(proposal.FilePath))
                {
                    var backupPath = proposal.FilePath + ".bak";
                    File.Copy(proposal.FilePath, backupPath, overwrite: true);
                }

                // Apply the change
                if (!string.IsNullOrEmpty(proposal.OldCode) && File.Exists(proposal.FilePath))
                {
                    var content = File.ReadAllText(proposal.FilePath);
                    if (content.Contains(proposal.OldCode))
                    {
                        content = content.Replace(proposal.OldCode, proposal.NewCode);
                        File.WriteAllText(proposal.FilePath, content);
                    }
                    else
                    {
                        proposal.Status = "rejected";
                        proposal.ErrorMessage = "Old code not found in file (file may have changed)";
                        Persist(list);
                        return false;
                    }
                }

                proposal.Status = "applied";
                Persist(list);
                return true;
            }
            catch (Exception ex)
            {
                proposal.Status = "rejected";
                proposal.ErrorMessage = ex.Message;
                Persist(list);
                return false;
            }
        }
    }

    public bool RejectProposal(string id)
    {
        lock (_lock)
        {
            var list = LoadAll();
            var proposal = list.FirstOrDefault(p => p.Id == id);
            if (proposal is null || proposal.Status != "pending") return false;

            proposal.Status = "rejected";
            Persist(list);
            return true;
        }
    }

    public bool RollbackProposal(string id)
    {
        lock (_lock)
        {
            var list = LoadAll();
            var proposal = list.FirstOrDefault(p => p.Id == id);
            if (proposal is null || proposal.Status != "applied") return false;

            try
            {
                var backupPath = proposal.FilePath + ".bak";
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, proposal.FilePath, overwrite: true);
                    proposal.Status = "rolled_back";
                    Persist(list);
                    return true;
                }

                proposal.ErrorMessage = "Backup file not found";
                Persist(list);
                return false;
            }
            catch (Exception ex)
            {
                proposal.ErrorMessage = ex.Message;
                Persist(list);
                return false;
            }
        }
    }

    private List<ImprovementProposal> LoadAll()
    {
        try
        {
            if (File.Exists(_proposalsPath))
            {
                var json = File.ReadAllText(_proposalsPath);
                return JsonSerializer.Deserialize<List<ImprovementProposal>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void Persist(List<ImprovementProposal> list)
    {
        try
        {
            // Keep only last 100 proposals
            if (list.Count > 100)
                list = list.Skip(list.Count - 100).ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_proposalsPath, json);
        }
        catch { }
    }
}
