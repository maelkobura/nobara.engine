// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core;

/// <summary>
/// Thrown when a context stops because a module with the
/// <see cref="ErrorPolicy.Stop"/> policy threw an exception. The emergency
/// hooks have run and the crash report is written.
/// </summary>
public sealed class EngineStopException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="moduleId">The module that threw.</param>
    /// <param name="step">The lifecycle step that was running.</param>
    /// <param name="side">The side of the context that stopped.</param>
    /// <param name="crashReport">The path of the crash report, or an empty string when it could not be written.</param>
    /// <param name="innerException">The exception the module threw.</param>
    public EngineStopException(string moduleId, LifecycleStep step, Side side, string crashReport, Exception innerException)
        : base($"Module '{moduleId}' threw during {Describe(step)} on the {Describe(side)} side: {innerException.Message}", innerException)
    {
        ModuleId = moduleId;
        Step = step;
        Side = side;
        CrashReport = crashReport;
    }

    /// <summary>Gets the module that threw.</summary>
    public string ModuleId { get; }

    /// <summary>Gets the lifecycle step that was running.</summary>
    public LifecycleStep Step { get; }

    /// <summary>Gets the side of the context that stopped.</summary>
    public Side Side { get; }

    /// <summary>Gets the path of the crash report, or an empty string when it could not be written.</summary>
    public string CrashReport { get; }

    internal static string Describe(LifecycleStep step) => step switch
    {
        LifecycleStep.Registration => "registration",
        LifecycleStep.Initialization => "initialization",
        LifecycleStep.Start => "start",
        LifecycleStep.Execution => "execution",
        LifecycleStep.Stop => "stop",
        LifecycleStep.Release => "release",
        LifecycleStep.Loading => "loading",
        _ => "discovery",
    };

    internal static string Describe(Side side) => side == Side.Server ? "server" : "client";
}

/// <summary>Why a service cannot be resolved.</summary>
public enum ServiceFailure
{
    /// <summary>No active module publishes the service.</summary>
    NotPublished,

    /// <summary>The module that publishes the service is not a declared dependency of the caller.</summary>
    UndeclaredDependency,
}

/// <summary>Thrown when a module asks for a service it cannot have.</summary>
public sealed class ServiceResolutionException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="serviceType">The service asked for.</param>
    /// <param name="failure">Why it cannot be resolved.</param>
    /// <param name="message">A message that names the modules involved.</param>
    public ServiceResolutionException(Type serviceType, ServiceFailure failure, string message)
        : base(message)
    {
        ServiceType = serviceType;
        Failure = failure;
    }

    /// <summary>Gets the service asked for.</summary>
    public Type ServiceType { get; }

    /// <summary>Gets why it cannot be resolved.</summary>
    public ServiceFailure Failure { get; }
}

/// <summary>A state of the application registered in code, to compare with the states declared in manifests.</summary>
/// <param name="StateId">The qualified identifier of the state: <c>state:example.menu</c>.</param>
/// <param name="ModuleId">The module whose code registered it.</param>
public sealed record RegisteredState(string StateId, string ModuleId);

/// <summary>Thrown when the states registered in code differ from the states declared in the manifests.</summary>
public sealed class StateMismatchException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="differences">One message per difference, naming the module and the state.</param>
    public StateMismatchException(IReadOnlyList<string> differences)
        : base("The states registered in code differ from the states declared in the module manifests:"
            + string.Concat(differences.Select(d => Environment.NewLine + "- " + d)))
    {
        Differences = differences;
    }

    /// <summary>Gets one message per difference.</summary>
    public IReadOnlyList<string> Differences { get; }
}

/// <summary>What the emergency hooks receive before a context stops on an error.</summary>
/// <param name="ModuleId">The module that threw.</param>
/// <param name="Step">The lifecycle step that was running.</param>
/// <param name="Side">The side of the context that stops.</param>
/// <param name="Exception">The exception the module threw.</param>
public sealed record EmergencyStop(string ModuleId, LifecycleStep Step, Side Side, Exception Exception);
