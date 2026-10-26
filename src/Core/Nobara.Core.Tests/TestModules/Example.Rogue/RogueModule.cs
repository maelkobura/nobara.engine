// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Runtime.CompilerServices;
using Nobara.Core;

[assembly: ModuleEntry(typeof(Example.Rogue.RogueModule))]

namespace Example.Rogue;

/// <summary>A module that conflicts with another and records any code it runs.</summary>
public sealed class RogueModule : IModule
{
    /// <inheritdoc/>
    public void Register(ModuleContext context) => AppContext.SetData("Example.Rogue.Ran", "Register");
}

internal static class Initializer
{
    [ModuleInitializer]
    internal static void Run() => AppContext.SetData("Example.Rogue.Ran", "module initializer");
}
