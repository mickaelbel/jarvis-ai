using System.Text;

namespace JarvisAI.Application.Memory;

public sealed class MemoryContext
{
    public IReadOnlyList<MemoryEntry> SessionMemories { get; init; } = Array.Empty<MemoryEntry>();
    public IReadOnlyList<MemoryEntry> ShortTermMemories { get; init; } = Array.Empty<MemoryEntry>();
    public IReadOnlyList<MemoryEntry> LongTermMemories { get; init; } = Array.Empty<MemoryEntry>();
    public IReadOnlyList<MemoryEntry> UserMemories { get; init; } = Array.Empty<MemoryEntry>();
    public IReadOnlyList<MemoryEntry> ProjectMemories { get; init; } = Array.Empty<MemoryEntry>();

    public int TotalCount =>
        SessionMemories.Count + ShortTermMemories.Count + LongTermMemories.Count +
        UserMemories.Count + ProjectMemories.Count;

    public string Render()
    {
        var sb = new StringBuilder();

        AppendSection(sb, "SESSION (conversation actuelle)", SessionMemories);
        AppendSection(sb, "SHORT-TERM (récent)", ShortTermMemories);
        AppendSection(sb, "LONG-TERM (connaissances)", LongTermMemories);
        AppendSection(sb, "USER (profil utilisateur)", UserMemories);
        AppendSection(sb, "PROJECT (contexte projet)", ProjectMemories);

        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<MemoryEntry> entries)
    {
        if (entries.Count == 0)
            return;

        sb.Append('[').Append(title).AppendLine("]");
        foreach (var entry in entries)
        {
            sb.Append("- ").Append(entry.Content).AppendLine();
        }
        sb.AppendLine();
    }
}
