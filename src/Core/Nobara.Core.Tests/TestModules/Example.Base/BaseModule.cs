// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Nobara.Core;

[assembly: ModuleEntry(typeof(Example.Base.BaseModule))]

namespace Example.Base;

/// <summary>A service that other sample modules call.</summary>
public interface IGreeter
{
    /// <summary>Greets someone, or throws when the switch Example.Base.Fail is set.</summary>
    string Greet(string name);
}

internal sealed class Greeter : IGreeter
{
    public string Greet(string name)
    {
        if (AppContext.TryGetSwitch("Example.Base.Fail", out bool fail) && fail)
        {
            throw new InvalidOperationException("The greeter failed.");
        }

        return "Hello, " + name;
    }
}

/// <summary>Publishes the greeter.</summary>
public sealed class BaseModule : IModule
{
    /// <inheritdoc/>
    public void Register(ModuleContext context) => context.Publish<IGreeter>(new Greeter());
}
