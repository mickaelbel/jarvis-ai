using JarvisAI.Application.Agents;
using JarvisAI.Application.AI;
using JarvisAI.Application.Commands;
using JarvisAI.Application.Context;
using JarvisAI.Application.Debug;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Planning.Strategies;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Abstractions;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<CommandRouter>();
        services.AddSingleton<ICommandRouter>(sp => sp.GetRequiredService<CommandRouter>());
        services.AddSingleton<AgentManager>();
        services.AddSingleton<IAgent, Agent>();
        services.AddSingleton<ICommandHandler, ToolCommandHandler>();
        services.AddSingleton<AIService>();
        services.AddSingleton<ConversationCondenser>();

        services.AddSingleton(new ModelRouterOptions());
        services.AddSingleton(sp => ModelOverrideStore.Load());
        services.AddSingleton<IModelRouter>(sp => new ModelRouter(
            sp.GetRequiredService<ModelRouterOptions>(),
            sp.GetRequiredService<ILogger<ModelRouter>>(),
            sp.GetRequiredService<ModelOverrideStore>()));

        services.AddSingleton<RetryPolicyOptions>();
        services.AddSingleton<IRetryPolicy>(sp => new RetryPolicy(
            sp.GetRequiredService<RetryPolicyOptions>(),
            sp.GetRequiredService<ILogger<RetryPolicy>>()));

        services.AddSingleton<AutomaticMemoryOptions>();
        services.AddSingleton<IAutomaticMemoryService>(sp => new AutomaticMemoryService(
            sp.GetRequiredService<IMemoryService>(),
            sp.GetRequiredService<AutomaticMemoryOptions>(),
            sp.GetRequiredService<ILogger<AutomaticMemoryService>>()));

        services.AddSingleton<ReasoningLoopOptions>();
        services.AddSingleton<IRunHistory, InMemoryRunHistory>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<IToolSelectionService, ToolSelectionService>();
        services.AddSingleton<IResponseDedupGuard, ResponseDedupGuard>();
        services.AddSingleton<IToolUsageTracker, InMemoryToolUsageTracker>();
        services.AddSingleton<IResponseCache, InMemoryResponseCache>();
        services.AddSingleton<IParallelToolExecutor, ParallelToolExecutor>();
        services.AddSingleton<IReasoningLoop, ReasoningLoop>();

        services.AddSingleton<IPlanningStrategy, SimplePlanningStrategy>();
        services.AddSingleton<IPlanningStrategy, ComplexPlanningStrategy>();
        services.AddSingleton<IPlanningStrategy, ResearchPlanningStrategy>();
        services.AddSingleton<IPlanningStrategy, ComputerUsePlanningStrategy>();
        services.AddSingleton<IPlanningStrategy, AutomationPlanningStrategy>();
        services.AddSingleton<IPlanningStrategySelector, PlanningStrategySelector>();

        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IDebugToolFeed, InMemoryDebugToolFeed>();
        services.AddSingleton<JarvisAI.Application.Search.IEntityResolver, JarvisAI.Application.Search.OfficialEntityResolver>();

        return services;
    }
}
