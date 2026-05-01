namespace MaxMix.Tests.Composition;

using MaxMix.App.Composition;
using MaxMix.App.Services;
using MaxMix.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Composition-root tests for <see cref="ServiceRegistration"/>. Locks in the
/// DI bindings the spec mandates so a future refactor can't silently drop
/// the interface registration and leave the codebase coupled to the concrete
/// type again.
///
/// Spec coverage (sdd/start-with-windows/spec §IAutoStartService Abstraction):
///   - "DI resolves IAutoStartService" → resolving <see cref="IAutoStartService"/>
///     returns an <see cref="AutoStartService"/> instance.
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void Build_ResolvesIAutoStartService_AsAutoStartService()
    {
        // Arrange + Act: build the real composition root.
        using var services = ServiceRegistration.Build();
        var resolved = services.GetRequiredService<IAutoStartService>();

        // Assert: the contract returns the concrete adapter.
        Assert.NotNull(resolved);
        Assert.IsType<AutoStartService>(resolved);
    }

    [Fact]
    public void Build_IAutoStartService_IsRegisteredAsSingleton()
    {
        // Two resolutions of the same interface must hand back the SAME
        // instance — the registry adapter is stateless but the spec calls
        // for singleton lifetime ("AddSingleton<IAutoStartService, AutoStartService>()").
        using var services = ServiceRegistration.Build();

        var first  = services.GetRequiredService<IAutoStartService>();
        var second = services.GetRequiredService<IAutoStartService>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Build_DoesNotExposeConcreteAutoStartService_DirectlyToConsumers()
    {
        // The composition root MUST NOT register the concrete type as a
        // standalone service — consumers depend on IAutoStartService. This
        // test guards against accidental dual registration that would let
        // a consumer regress to depending on the concrete class.
        using var services = ServiceRegistration.Build();

        var concrete = services.GetService<AutoStartService>();

        Assert.Null(concrete);
    }
}
