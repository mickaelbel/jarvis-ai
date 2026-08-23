using JarvisAI.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace JarvisAI.Plugins;

public static class DependencyInjection
{
    public static IServiceCollection AddPlugins(this IServiceCollection services)
    {
        return services;
    }
}
