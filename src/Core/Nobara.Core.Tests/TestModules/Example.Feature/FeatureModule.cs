// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Example.Base;
using Nobara.Core;

[assembly: ModuleEntry(typeof(Example.Feature.FeatureModule))]

namespace Example.Feature;

/// <summary>Calls the greeter of its dependency when it starts.</summary>
public sealed class FeatureModule : IModule
{
    private IGreeter? _greeter;

    /// <inheritdoc/>
    public void Initialize(ModuleContext context) => _greeter = context.Resolve<IGreeter>();

    /// <inheritdoc/>
    public void Start(ModuleContext context) => context.Logger.Info(_greeter!.Greet("feature"));
}
