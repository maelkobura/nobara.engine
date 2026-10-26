// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

[assembly: ModuleEntry(typeof(Example.Extra.ExtraModule))]

namespace Example.Extra;

/// <summary>A module that only logs its steps.</summary>
public sealed class ExtraModule : IModule
{
    /// <inheritdoc/>
    public void Start(ModuleContext context) => context.Logger.Info("extra started");

    /// <inheritdoc/>
    public void Stop(ModuleContext context) => context.Logger.Info("extra stopped");
}
