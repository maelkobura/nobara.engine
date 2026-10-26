// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

public interface IClock
{
    int Ticks { get; }
}

public interface ISpawner
{
    void Spawn();
}

/// <summary>The service registry of a context.</summary>
public sealed class ServiceTests : IDisposable
{
    private readonly TestDistribution _distribution = new();

    public void Dispose() => _distribution.Dispose();

    private sealed class Clock : IClock
    {
        public int Ticks => 30;
    }

    [Fact]
    public void Resolves_a_service_of_a_declared_dependency_from_initialization_on()
    {
        IClock? resolved = null;
        _distribution
            .Module("example.time", behavior: m => m.OnRegister = c => c.Publish<IClock>(new Clock()))
            .Module("example.mobs", m => m.Requires("example.time"), m => m.OnInitialize = c => resolved = c.Resolve<IClock>());
        using Engine engine = _distribution.Load();

        engine.MainContext.Start();

        Assert.Equal(30, resolved?.Ticks);
    }

    [Fact]
    public void Lets_a_module_resolve_its_own_services()
    {
        bool found = false;
        _distribution.Module("example.time", behavior: m =>
        {
            m.OnRegister = c => c.Publish<IClock>(new Clock());
            m.OnStart = c => found = c.TryResolve(out IClock? _);
        });
        using Engine engine = _distribution.Load();

        engine.MainContext.Start();

        Assert.True(found);
    }

    [Fact]
    public void Refuses_to_resolve_a_service_during_registration()
    {
        _distribution
            .Module("example.time", behavior: m => m.OnRegister = c => c.Publish<IClock>(new Clock()))
            .Module("example.mobs", m => m.Requires("example.time"), m => m.OnRegister = c => c.Resolve<IClock>());
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal("example.mobs", exception.ModuleId);
        Assert.Equal(LifecycleStep.Registration, exception.Step);
        Assert.Contains("during registration", exception.InnerException!.Message);
    }

    [Fact]
    public void Refuses_to_publish_outside_registration()
    {
        _distribution.Module("example.time", behavior: m => m.OnInitialize = c => c.Publish<IClock>(new Clock()));
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(LifecycleStep.Initialization, exception.Step);
    }

    [Fact]
    public void Refuses_a_service_of_a_module_that_is_not_a_declared_dependency()
    {
        _distribution
            .Module("example.time", behavior: m => m.OnRegister = c => c.Publish<IClock>(new Clock()))
            .Module("example.mobs", behavior: m => m.OnInitialize = c => c.TryResolve(out IClock? _));
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        var refusal = Assert.IsType<ServiceResolutionException>(exception.InnerException);
        Assert.Equal(ServiceFailure.UndeclaredDependency, refusal.Failure);
        Assert.Contains("'example.mobs'", refusal.Message);
        Assert.Contains("'example.time'", refusal.Message);
    }

    [Fact]
    public void Reports_absent_the_service_of_an_inactive_optional_dependency()
    {
        bool? found = null;
        _distribution.Module("example.mobs", m => m.Uses("example.particles"), m => m.OnInitialize = c => found = c.TryResolve(out ISpawner? _));
        using Engine engine = _distribution.Load();

        engine.MainContext.Start();

        Assert.False(found);
    }

    [Fact]
    public void Throws_when_resolving_a_service_nobody_publishes()
    {
        _distribution
            .Module("example.time")
            .Module("example.mobs", m => m.Requires("example.time"), m => m.OnInitialize = c => c.Resolve<IClock>());
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal(ServiceFailure.NotPublished, Assert.IsType<ServiceResolutionException>(exception.InnerException).Failure);
    }

    [Fact]
    public void Refuses_a_service_published_twice()
    {
        _distribution
            .Module("example.time", behavior: m => m.OnRegister = c => c.Publish<IClock>(new Clock()))
            .Module("example.othertime", behavior: m => m.OnRegister = c => c.Publish<IClock>(new Clock()));
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal("example.othertime", exception.ModuleId);
        Assert.Contains("already publishes", exception.InnerException!.Message);
    }

    [Fact]
    public void Tells_whether_a_module_is_active()
    {
        bool time = false, particles = true;
        _distribution
            .Module("example.time", m => m.Level = F.Level.Platform)
            .Module("example.mobs", m => m.Uses("example.particles"), m => m.OnInitialize = c =>
            {
                time = c.IsModuleActive("example.time");
                particles = c.IsModuleActive("example.particles");
            });
        using Engine engine = _distribution.Load();

        engine.MainContext.Start();

        Assert.True(time);
        Assert.False(particles);
    }
}
