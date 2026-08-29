using JarvisAI.Application.Agents;
using JarvisAI.Application.Agents.Supervision;
using JarvisAI.Application.AI;
using JarvisAI.Application.Commands;
using JarvisAI.Application.Context;
using JarvisAI.Application.Cron;
using JarvisAI.Application.Debug;
using JarvisAI.Application.Delegation;
using JarvisAI.Application.Memory;
using JarvisAI.Application.MoA;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Planning.Strategies;
using JarvisAI.Application.Security;
using JarvisAI.Application.Services;
using JarvisAI.Application.Skills;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
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
        services.AddSingleton<IDictationService>(new DictationService());
        services.AddSingleton<IReminderService, ReminderService>();
        services.AddSingleton<OnboardingService>();

        services.AddSingleton(new ModelRouterOptions());
        services.AddSingleton(sp => ModelOverrideStore.Load());
        services.AddSingleton<IModelRouter>(sp => new ModelRouter(
            sp.GetRequiredService<ModelRouterOptions>(),
            sp.GetRequiredService<ILogger<ModelRouter>>(),
            sp.GetRequiredService<ModelOverrideStore>()));
        services.AddSingleton<RoutingFeedbackStore>();
        services.AddSingleton<SettingsProfileService>();

        // Hermes-inspired features
        services.AddSingleton<DelegationService>();
        services.AddSingleton<ITool, DelegateTaskTool>();
        services.AddSingleton<ITool, GetSubagentResultTool>();
        services.AddSingleton<CronScheduler>();
        services.AddSingleton<ITool, CronTool>();
        services.AddSingleton<ThreatPatternScanner>();
        services.AddSingleton<MixtureOfAgentsService>();
        services.AddSingleton<SkillManager>();
        services.AddSingleton<ITool, SkillTool>();

        services.AddSingleton<RetryPolicyOptions>();
        services.AddSingleton<IRetryPolicy>(sp => new RetryPolicy(
            sp.GetRequiredService<RetryPolicyOptions>(),
            sp.GetRequiredService<ILogger<RetryPolicy>>()));

        services.AddSingleton<AutomaticMemoryOptions>();
        services.AddSingleton<IAutomaticMemoryService>(sp => new AutomaticMemoryService(
            sp.GetRequiredService<IMemoryService>(),
            sp.GetRequiredService<AutomaticMemoryOptions>(),
            sp.GetRequiredService<ILogger<AutomaticMemoryService>>(),
            sp.GetService<IMemorySettingsStore>()));

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
        services.AddSingleton<IPlanningStrategy, CodingPlanningStrategy>();
        services.AddSingleton<IPlanningStrategySelector, PlanningStrategySelector>();

        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IMultiAgentOrchestrator, MultiAgentOrchestrator>();
        services.AddSingleton<IDebugToolFeed, InMemoryDebugToolFeed>();
        services.AddSingleton<JarvisAI.Application.Search.IEntityResolver, JarvisAI.Application.Search.OfficialEntityResolver>();

        // ── Agent Supervision (unifiée) ────────────────────────────────────────
        services.AddSingleton<IAgentVerifier, AgentVerifier>();
        services.AddSingleton<TaskExecutor>();
        services.AddSingleton<ISpecializedAgentSelector, SpecializedAgentSelector>();
        services.AddSingleton<IAgentSupervisor, AgentSupervisor>();

        return services;
    }
}
