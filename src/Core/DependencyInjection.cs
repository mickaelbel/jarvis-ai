using JarvisAI.Application.Abstractions;
using JarvisAI.Core.Engine;
using JarvisAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Core;

public static class DependencyInjection
{
    public static IHostBuilder ConfigureJarvis(this IHostBuilder hostBuilder)
    {
        hostBuilder.ConfigureServices((context, services) =>
        {
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            services.AddApplication();
            services.AddInfrastructure();
            services.AddSingleton<CoreEngine>();
        });

        return hostBuilder;
    }
}
