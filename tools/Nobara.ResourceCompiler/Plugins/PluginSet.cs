// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.ResourceCompiler.Plugins;

/// <summary>The plugins of a compilation, indexed by the resource types they handle.</summary>
internal sealed class PluginSet
{
    private readonly Dictionary<string, List<ResourcePlugin>> _byElement = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Element, uint From), SchemaConverter> _converters = [];

    public PluginSet(IEnumerable<ResourcePlugin> plugins, DiagnosticBag diagnostics)
    {
        foreach (ResourcePlugin plugin in plugins)
        {
            All.Add(plugin);
            foreach (string element in plugin.ResourceTypes)
            {
                if (!_byElement.TryGetValue(element, out List<ResourcePlugin>? list))
                {
                    _byElement[element] = list = [];
                }

                list.Add(plugin);
            }

            foreach (SchemaConverter converter in plugin.Converters)
            {
                if (!_converters.TryAdd((converter.Element, converter.FromVersion), converter))
                {
                    diagnostics.Error("NRC601", $"two plugins convert <{converter.Element}> from version {converter.FromVersion}", null);
                }
            }
        }
    }

    public List<ResourcePlugin> All { get; } = [];

    public IReadOnlyList<ResourcePlugin> For(string element) => _byElement.TryGetValue(element, out List<ResourcePlugin>? list) ? list : [];

    public SchemaConverter? Converter(string element, uint fromVersion) => _converters.GetValueOrDefault((element, fromVersion));
}
