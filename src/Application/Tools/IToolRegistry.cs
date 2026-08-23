using JarvisAI.Application.Agents;

namespace JarvisAI.Application.Tools;

public interface IToolRegistry
{
    void Register(ITool tool);
    bool Unregister(string name);
    ITool? GetByName(string name);
    IReadOnlyList<ITool> GetByCategory(string category);
    IReadOnlyList<ITool> GetAll();
}
