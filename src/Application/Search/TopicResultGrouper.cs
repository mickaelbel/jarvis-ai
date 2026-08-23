namespace JarvisAI.Application.Search;

public sealed class SearchResultGroup
{
    public int Id { get; init; }
    public string? Topic { get; init; }
    public IReadOnlyList<SearchResult> Members { get; init; } = Array.Empty<SearchResult>();
}

public sealed class TopicResultGrouper
{
    private const int MinOverlap = 2;

    public IReadOnlyList<SearchResultGroup> Group(IEnumerable<SearchResult> results)
    {
        var list = results.ToList();
        var groups = new List<SearchResultGroup>();
        var groupIndex = 1;

        foreach (var result in list)
        {
            var terms = TopicExtractor.Extract(result.Title);
            var matched = groups
                .Select((g, i) => new { Group = g, Index = i })
                .FirstOrDefault(x => OverlapCount(terms, TopicExtractor.Extract(x.Group.Topic ?? string.Empty)) >= MinOverlap);

            if (matched is not null)
            {
                var members = matched.Group.Members.ToList();
                members.Add(result);
                var topic = PickTopic(matched.Group.Topic, result.Title);
                groups[matched.Index] = new SearchResultGroup { Id = matched.Group.Id, Topic = topic, Members = members };
                result.GroupId = matched.Group.Id;
            }
            else
            {
                result.GroupId = groupIndex;
                groups.Add(new SearchResultGroup
                {
                    Id = groupIndex,
                    Topic = result.Title,
                    Members = new List<SearchResult> { result }
                });
                groupIndex++;
            }
        }

        return groups;
    }

    private static int OverlapCount(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
        => a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();

    private static string? PickTopic(string? existing, string candidate)
    {
        var existingTerms = TopicExtractor.Extract(existing ?? string.Empty);
        var candidateTerms = TopicExtractor.Extract(candidate);
        if (existingTerms.Count == 0)
            return candidateTerms.Count > 0 ? candidateTerms[0] : candidate;
        return existingTerms.Count >= candidateTerms.Count ? existing : candidate;
    }
}
