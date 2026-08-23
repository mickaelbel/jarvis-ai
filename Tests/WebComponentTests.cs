using JarvisAI.Application.Abstractions;
using JarvisAI.Application.AI;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Events;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class MemoryConfirmationStoreTests
{
    [Fact]
    public async Task Store_request_returns_unique_id()
    {
        var store = new MemoryConfirmationStore();
        var request = new ConfirmationRequest("tool", "desc", "High", Guid.NewGuid());

        var id = await store.StoreRequestAsync(request);

        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task GetRequest_returns_stored_request()
    {
        var store = new MemoryConfirmationStore();
        var request = new ConfirmationRequest("tool", "desc", "High", Guid.NewGuid());

        var id = await store.StoreRequestAsync(request);
        var retrieved = await store.GetRequestAsync(id);

        Assert.NotNull(retrieved);
        Assert.Equal("tool", retrieved.ToolName);
        Assert.Equal("desc", retrieved.Description);
    }

    [Fact]
    public async Task GetRequest_returns_null_for_unknown_id()
    {
        var store = new MemoryConfirmationStore();

        var result = await store.GetRequestAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPending_returns_unresolved_requests()
    {
        var store = new MemoryConfirmationStore();
        var r1 = new ConfirmationRequest("a", "d1", "High", Guid.NewGuid());
        var r2 = new ConfirmationRequest("b", "d2", "Low", Guid.NewGuid());

        var id1 = await store.StoreRequestAsync(r1);
        await store.StoreRequestAsync(r2);

        var pending = await store.GetPendingAsync();
        Assert.Equal(2, pending.Count);

        await store.ResolveRequestAsync(id1, ConfirmationResult.Accepted(ConfirmationMethod.Text, TimeSpan.Zero));

        pending = await store.GetPendingAsync();
        Assert.Single(pending);
        Assert.Equal("b", pending[0].Request.ToolName);
    }

    [Fact]
    public async Task ResolveRequest_sets_is_resolved()
    {
        var store = new MemoryConfirmationStore();
        var id = await store.StoreRequestAsync(new ConfirmationRequest("t", "d", "Low", Guid.NewGuid()));

        var result = ConfirmationResult.Accepted(ConfirmationMethod.Voice, TimeSpan.FromSeconds(1));
        await store.ResolveRequestAsync(id, result);

        var pending = await store.GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task WaitForResolution_returns_result_when_resolved()
    {
        var store = new MemoryConfirmationStore();
        var id = await store.StoreRequestAsync(new ConfirmationRequest("t", "d", "High", Guid.NewGuid()));

        var expected = ConfirmationResult.Accepted(ConfirmationMethod.Voice, TimeSpan.Zero, "yes");

        var resolveTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            await store.ResolveRequestAsync(id, expected);
        });

        var actual = await store.WaitForResolutionAsync(id, TimeSpan.FromSeconds(5));

        Assert.True(actual.Confirmed);
        Assert.Equal("yes", actual.ResponseText);
        await resolveTask;
    }

    [Fact]
    public async Task WaitForResolution_returns_denied_on_timeout()
    {
        var store = new MemoryConfirmationStore();
        var id = await store.StoreRequestAsync(new ConfirmationRequest("t", "d", "High", Guid.NewGuid()));

        var result = await store.WaitForResolutionAsync(id, TimeSpan.FromMilliseconds(100));

        Assert.False(result.Confirmed);
        Assert.Equal("Timed out", result.ResponseText);
    }

    [Fact]
    public async Task WaitForResolution_returns_denied_for_unknown_request()
    {
        var store = new MemoryConfirmationStore();

        var result = await store.WaitForResolutionAsync(Guid.NewGuid(), TimeSpan.FromSeconds(1));

        Assert.False(result.Confirmed);
        Assert.Equal("Request not found", result.ResponseText);
    }
}

public class WebConfirmationServiceTests
{
    private static (WebConfirmationService service, MemoryConfirmationStore store) Create()
    {
        var store = new MemoryConfirmationStore();
        var service = new WebConfirmationService(store, NullLogger<WebConfirmationService>.Instance);
        return (service, store);
    }

    [Fact]
    public async Task RequestConfirmation_stores_and_returns_result()
    {
        var (service, store) = Create();
        var request = new ConfirmationRequest("tool", "desc", "High", Guid.NewGuid());

        ConfirmationRequest? notifiedRequest = null;
        service.OnConfirmationRequested += (req) =>
        {
            notifiedRequest = req;
            return Task.CompletedTask;
        };

        var resolveTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            var pending = await store.GetPendingAsync();
            Assert.Single(pending);
            await store.ResolveRequestAsync(pending[0].RequestId, ConfirmationResult.Accepted(ConfirmationMethod.Text, TimeSpan.Zero));
        });

        var result = await service.RequestConfirmationAsync(request);

        Assert.True(result.Confirmed);
        Assert.NotNull(notifiedRequest);
        Assert.Equal("tool", notifiedRequest.ToolName);
        await resolveTask;
    }

    [Fact]
    public async Task OnConfirmationRequested_fires_with_correct_data()
    {
        var (service, store) = Create();
        var request = new ConfirmationRequest("dangerous", "Delete all", "Critical", Guid.NewGuid());

        var notified = false;
        string? notifiedToolName = null;
        service.OnConfirmationRequested += (req) =>
        {
            notified = true;
            notifiedToolName = req.ToolName;
            return Task.CompletedTask;
        };

        var resolveTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            var pending = await store.GetPendingAsync();
            await store.ResolveRequestAsync(pending[0].RequestId, ConfirmationResult.Denied(ConfirmationMethod.Voice, TimeSpan.Zero));
        });

        await service.RequestConfirmationAsync(request);

        Assert.True(notified);
        Assert.Equal("dangerous", notifiedToolName);
        await resolveTask;
    }
}

public class PluginMetadataTests
{
    [Fact]
    public void PluginMetadata_has_default_state_discovered()
    {
        var meta = new PluginMetadata
        {
            Id = "test",
            Name = "Test Plugin"
        };

        Assert.Equal(PluginState.Discovered, meta.State);
    }

    [Fact]
    public void PluginMetadata_state_can_be_set()
    {
        var meta = new PluginMetadata
        {
            Id = "test",
            State = PluginState.Running
        };

        Assert.Equal(PluginState.Running, meta.State);
    }

    [Fact]
    public void PluginMetadata_has_all_expected_properties()
    {
        var meta = new PluginMetadata
        {
            Id = "id",
            Name = "Name",
            Description = "Desc",
            Author = "Author",
            Version = "2.0.0",
            AssemblyPath = "/path.dll",
            TypeName = "MyType",
            Dependencies = new() { "dep1" },
            Permissions = new() { "admin" }
        };

        Assert.Equal("id", meta.Id);
        Assert.Equal("Name", meta.Name);
        Assert.Equal("Desc", meta.Description);
        Assert.Equal("Author", meta.Author);
        Assert.Equal("2.0.0", meta.Version);
        Assert.Equal("/path.dll", meta.AssemblyPath);
        Assert.Equal("MyType", meta.TypeName);
        Assert.Single(meta.Dependencies);
        Assert.Single(meta.Permissions);
    }

    [Fact]
    public void PluginState_enum_has_all_states()
    {
        Assert.Equal(0, (int)PluginState.Discovered);
        Assert.Equal(1, (int)PluginState.Loaded);
        Assert.Equal(2, (int)PluginState.Initialized);
        Assert.Equal(3, (int)PluginState.Running);
        Assert.Equal(4, (int)PluginState.Stopped);
        Assert.Equal(5, (int)PluginState.Error);
    }
}

public class PluginRegistryStateSyncTests
{
    private static PluginRegistry CreateRegistry()
    {
        return new PluginRegistry(NullLogger<PluginRegistry>.Instance);
    }

    [Fact]
    public void Register_sets_metadata_state_to_loaded()
    {
        var registry = CreateRegistry();
        var meta = new PluginMetadata { Id = "test", Name = "Test" };
        var plugin = new DummyPlugin();

        registry.Register(meta, plugin);

        Assert.Equal(PluginState.Loaded, meta.State);
    }

    [Fact]
    public void UpdateState_syncs_to_metadata()
    {
        var registry = CreateRegistry();
        var meta = new PluginMetadata { Id = "test", Name = "Test" };
        registry.Register(meta, new DummyPlugin());

        registry.UpdateState("test", PluginState.Running);

        Assert.Equal(PluginState.Running, meta.State);
        var entry = registry.Get("test");
        Assert.Equal(PluginState.Running, entry!.State);
    }

    [Fact]
    public void GetAll_returns_metadata_with_current_state()
    {
        var registry = CreateRegistry();
        var meta1 = new PluginMetadata { Id = "a", Name = "A" };
        var meta2 = new PluginMetadata { Id = "b", Name = "B" };
        registry.Register(meta1, new DummyPlugin());
        registry.Register(meta2, new DummyPlugin());
        registry.UpdateState("a", PluginState.Running);

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
        var a = all.First(e => e.Metadata.Id == "a");
        var b = all.First(e => e.Metadata.Id == "b");
        Assert.Equal(PluginState.Running, a.Metadata.State);
        Assert.Equal(PluginState.Loaded, b.Metadata.State);
    }
}

public class ConfirmationRequestTests
{
    [Fact]
    public void ConfirmationRequest_properties_are_set()
    {
        var corrId = Guid.NewGuid();
        var request = new ConfirmationRequest("tool", "description", "High", corrId);

        Assert.Equal("tool", request.ToolName);
        Assert.Equal("description", request.Description);
        Assert.Equal("High", request.RiskLevel);
        Assert.Equal(corrId, request.CorrelationId);
        Assert.Empty(request.Parameters);
    }

    [Fact]
    public void ConfirmationRequest_with_parameters()
    {
        var parameters = new Dictionary<string, string> { { "key", "value" } };
        var request = new ConfirmationRequest("t", "d", "Low", Guid.NewGuid(), parameters);

        Assert.Single(request.Parameters);
        Assert.Equal("value", request.Parameters["key"]);
    }
}

public class ConfirmationResultTests
{
    [Fact]
    public void Accepted_creates_confirmed_result()
    {
        var result = ConfirmationResult.Accepted(ConfirmationMethod.Voice, TimeSpan.FromSeconds(2), "yes");

        Assert.True(result.Confirmed);
        Assert.Equal(ConfirmationMethod.Voice, result.Method);
        Assert.Equal(TimeSpan.FromSeconds(2), result.ResponseTime);
        Assert.Equal("yes", result.ResponseText);
    }

    [Fact]
    public void Denied_creates_denied_result()
    {
        var result = ConfirmationResult.Denied(ConfirmationMethod.Text, TimeSpan.FromSeconds(1), "no");

        Assert.False(result.Confirmed);
        Assert.Equal(ConfirmationMethod.Text, result.Method);
        Assert.Equal("no", result.ResponseText);
    }

    [Fact]
    public void AutoConfirmed_creates_auto_confirmed_result()
    {
        var result = ConfirmationResult.AutoConfirmed();

        Assert.True(result.Confirmed);
        Assert.Equal(ConfirmationMethod.Automatic, result.Method);
        Assert.Equal(TimeSpan.Zero, result.ResponseTime);
    }
}

public class PluginMetadataDiscoveryTests
{
    [Fact]
    public void PluginMetadata_has_default_discovery_time()
    {
        var before = DateTime.UtcNow;
        var meta = new PluginMetadata { Id = "test", Name = "Test" };
        var after = DateTime.UtcNow;

        Assert.True(meta.DiscoveredAt >= before.AddSeconds(-1));
        Assert.True(meta.DiscoveredAt <= after.AddSeconds(1));
    }

    [Fact]
    public void PluginMetadata_dependencies_and_permissions_default_to_empty()
    {
        var meta = new PluginMetadata { Id = "test" };

        Assert.NotNull(meta.Dependencies);
        Assert.Empty(meta.Dependencies);
        Assert.NotNull(meta.Permissions);
        Assert.Empty(meta.Permissions);
    }
}

internal sealed class DummyPlugin : IPlugin
{
    public PluginMetadata Metadata { get; } = new() { Id = "dummy", Name = "Dummy" };
    public PluginState State { get; } = PluginState.Loaded;
    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose() { }
}
