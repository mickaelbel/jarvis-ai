using JarvisAI.Application.Agents;

namespace JarvisAI.Application.Commands;

public interface ICommand
{
    string Name { get; }
    string Description { get; }
    string? ToolName { get; }
    IReadOnlyList<string> Aliases { get; }
    bool CanHandle(string input);
}
