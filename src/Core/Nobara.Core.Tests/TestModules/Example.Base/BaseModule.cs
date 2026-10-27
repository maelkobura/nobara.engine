// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using System.IO;
using Example.Base.Schemas;
using Nobara.Core;
using Nobara.Core.Resources;

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

/// <summary>A gem, as the module decodes it.</summary>
/// <param name="Data">The loaded resource.</param>
/// <param name="Hardness">The hardness, overrides applied.</param>
public sealed record GemType(ResourceData Data, int Hardness);

/// <summary>Publishes the greeter and decodes the gems.</summary>
public sealed class BaseModule : IModule
{
    private FreeProperty? _weight;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        context.Publish<IGreeter>(new Greeter());
        context.Resources.RegisterDecoder("gem", data => new GemType(data, Gem.Parser.ParseFrom(data.Content.Span).Hardness));
        _weight = context.Resources.DeclareProperty("Weight", PropertyType.Float);
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        context.Logger.Info("greeting " + context.Settings.GetString("base.greeting"));
        foreach (GemType gem in context.Resources.GetAll<GemType>("gem"))
        {
            _weight!.TryGetFloat(gem.Data, out double weight);
            string picture = Gem.Parser.ParseFrom(gem.Data.Content.Span).Picture;
            using var reader = new StreamReader(context.Resources.OpenFile(gem.Data, picture));
            context.Logger.Info($"{gem.Data.ObjectId} hardness {gem.Hardness} weight {weight.ToString(CultureInfo.InvariantCulture)} picture '{reader.ReadToEnd()}'");
        }

        context.Resources.TryGetTag("#gem:example.precious", out ResourceTag? tag);
        context.Logger.Info("precious " + string.Join(",", tag?.Members ?? []));
    }
}
