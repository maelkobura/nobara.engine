// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using System.Xml.Linq;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Plugins;

[assembly: ResourcePlugin(typeof(Nobara.ResourceCompiler.Tests.XmlTypesPlugin))]

namespace Nobara.ResourceCompiler.Tests;

/// <summary>Loads this test assembly as a plugin assembly.</summary>
public sealed class PluginLoaderTests
{
    [Fact]
    public void Creates_the_plugins_that_an_assembly_declares()
    {
        var diagnostics = new DiagnosticBag();
        using PluginLoader loader = PluginLoader.Load([typeof(XmlTypesPlugin).Assembly.Location], [], diagnostics);

        Assert.False(diagnostics.HasErrors);
        ResourcePlugin plugin = Assert.Single(loader.Plugins);
        Assert.Equal(nameof(XmlTypesPlugin), plugin.GetType().Name);
        Assert.NotSame(typeof(XmlTypesPlugin), plugin.GetType());
    }

    // A self-contained game probes its own folder, which holds a copy of the
    // .NET libraries: the plugins must still see the types of the host.
    [Fact]
    public void Gives_the_plugins_the_libraries_of_the_host_when_a_probe_folder_holds_copies()
    {
        var diagnostics = new DiagnosticBag();
        using PluginLoader loader = PluginLoader.Load([typeof(XmlTypesPlugin).Assembly.Location], [RuntimeEnvironment.GetRuntimeDirectory()], diagnostics);

        Assert.False(diagnostics.HasErrors);
        ResourcePlugin plugin = Assert.Single(loader.Plugins);
        var element = (Type)plugin.GetType().GetProperty(nameof(XmlTypesPlugin.ElementType))!.GetValue(plugin)!;
        Assert.Same(typeof(XElement), element);
    }
}

/// <summary>A plugin that tells which <see cref="XElement"/> type it sees.</summary>
public sealed class XmlTypesPlugin : ResourcePlugin
{
    /// <inheritdoc/>
    public override IReadOnlyCollection<string> ResourceTypes => [];

    /// <summary>Gets the type <see cref="XElement"/> as the plugin assembly resolves it.</summary>
    public Type ElementType => typeof(XElement);
}
