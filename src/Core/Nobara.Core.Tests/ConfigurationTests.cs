// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Google.Protobuf;
using Nobara.Core.Configuration;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;
using Scope = Nobara.Formats.SettingDeclaration.Types.Scope;
using SettingKind = Nobara.Formats.SettingDeclaration.Types.Type;
using Operation = Nobara.Formats.SettingValue.Types.Operation;

namespace Nobara.Core.Tests;

/// <summary>Configuration keys, values of the distribution, the settings file of the player, and module configurations.</summary>
public sealed class ConfigurationTests : IDisposable
{
    private readonly TestDistribution _distribution = new();

    public void Dispose() => _distribution.Dispose();

    private string SettingsFile => Path.Combine(_distribution.Root, "config", "settings.xml");

    private static F.SettingDeclaration Key(string key, SettingKind type, string value, Scope scope = Scope.User, double? min = null, double? max = null, F.Side side = F.Side.Unspecified, params string[] values)
    {
        var declaration = new F.SettingDeclaration { Key = key, Type = type, DefaultValue = value, Scope = scope, Side = side };
        if (min is double low)
        {
            declaration.Min = low;
        }

        if (max is double high)
        {
            declaration.Max = high;
        }

        declaration.Values.AddRange(values);
        return declaration;
    }

    // A module with one key of each kind, and a distribution that adjusts three of them.
    private TestDistribution View(Action<TestModule>? behavior = null)
    {
        _distribution.Configure = d => d.Settings.AddRange(
        [
            new F.SettingValue { Key = "view.radius", Operation = Operation.Default, Value = "192" },
            new F.SettingValue { Key = "view.fog", Operation = Operation.Lock, Value = "false" },
            new F.SettingValue { Key = "view.cap", Operation = Operation.Set, Value = "20" },
        ]);
        return _distribution.Module(
            "example.view",
            m => m.Settings.AddRange(
            [
                Key("view.radius", SettingKind.Int, "256", min: 32, max: 1024),
                Key("view.fog", SettingKind.Bool, "true"),
                Key("view.quality", SettingKind.Enum, "low", values: ["low", "high"]),
                Key("view.scale", SettingKind.Float, "1.5", min: 0.5, max: 4),
                Key("view.title", SettingKind.String, "Example"),
                Key("view.cap", SettingKind.Int, "10", Scope.Distribution),
            ]),
            behavior);
    }

    private void WriteSettingsFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, content);
    }

    [Fact]
    public void Reads_the_declared_defaults_and_the_values_of_the_distribution()
    {
        ModuleSettings? settings = null;
        using Engine engine = View(m => m.OnInitialize = c => settings = c.Settings).Load();
        engine.MainContext.Start();

        Assert.Equal(192, settings!.GetInt("view.radius"));
        Assert.False(settings.GetBool("view.fog"));
        Assert.Equal("low", settings.GetEnum("view.quality"));
        Assert.Equal(1.5, settings.GetFloat("view.scale"));
        Assert.Equal("Example", settings.GetString("view.title"));
        Assert.Equal(20, settings.GetInt("view.cap"));

        SettingInfo fog = settings.GetInfo("view.fog");
        Assert.True(fog.IsLocked);
        Assert.Equal("false", fog.Default);
        Assert.Equal(SettingScope.Distribution, settings.GetInfo("view.cap").Scope);
        Assert.Equal(["view.cap", "view.fog", "view.quality", "view.radius", "view.scale", "view.title"], settings.Keys.Select(k => k.Key));
    }

    [Fact]
    public void Reads_the_values_of_the_player_and_ignores_those_it_cannot_take()
    {
        WriteSettingsFile("""
            <Settings>
              <Setting key="view.radius" value="512" />
              <Setting key="view.quality" value="HIGH" />
              <Setting key="view.scale" value="9" />
              <Setting key="view.fog" value="true" />
              <Setting key="view.cap" value="5" />
              <Setting key="view.unknown" value="1" />
              <Other />
            </Settings>
            """);
        ModuleSettings? settings = null;
        using Engine engine = View(m => m.OnInitialize = c => settings = c.Settings).Load();
        engine.MainContext.Start();

        Assert.Equal(512, settings!.GetInt("view.radius"));
        Assert.Equal("high", settings.GetEnum("view.quality"));
        Assert.Equal(1.5, settings.GetFloat("view.scale"));
        Assert.False(settings.GetBool("view.fog"));
        Assert.Equal(20, settings.GetInt("view.cap"));

        string[] warnings = _distribution.Log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();
        Assert.Contains(warnings, w => w.EndsWith("9 is above the maximum, 4 for 'view.scale'; the key keeps its default value, 1.5.", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.EndsWith("'view.cap' is set by the distribution, not by the player; the value is ignored.", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.EndsWith("no module declares the key 'view.unknown'; the value is ignored.", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("settings.xml(8,", StringComparison.Ordinal) && w.Contains("expected <Setting", StringComparison.Ordinal));
        Assert.Contains(_distribution.Log.Entries, e => e.Message.EndsWith("the distribution locks 'view.fog' to false; the value of the file is ignored.", StringComparison.Ordinal));
    }

    [Fact]
    public void Uses_the_defaults_when_the_settings_file_cannot_be_read()
    {
        WriteSettingsFile("<Settings><Setting");
        ModuleSettings? settings = null;
        using Engine engine = View(m => m.OnInitialize = c => settings = c.Settings).Load();
        engine.MainContext.Start();

        Assert.Equal(192, settings!.GetInt("view.radius"));
        Assert.Contains(_distribution.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("cannot be read, so every key has its default value", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_only_the_values_the_player_chose()
    {
        using (Engine engine = View(m => m.OnInitialize = c =>
        {
            c.Settings.Set("view.radius", 300);
            c.Settings.Set("view.quality", "high");
            c.Settings.Set("view.scale", 2.0);
            c.Settings.Reset("view.scale");
            c.Settings.Save();
        }).Load())
        {
            engine.MainContext.Start();
        }

        XElement root = XDocument.Load(SettingsFile).Root!;
        Assert.Equal(
            [("view.quality", "high"), ("view.radius", "300")],
            root.Elements("Setting").Select(e => (e.Attribute("key")!.Value, e.Attribute("value")!.Value)));

        // The next start reads them back.
        var next = new TestDistribution();
        try
        {
            File.Copy(SettingsFile, Path.Combine(Directory.CreateDirectory(Path.Combine(next.Root, "config")).FullName, "settings.xml"));
            ModuleSettings? settings = null;
            using Engine engine = next.Module("example.view", m => m.Settings.Add(Key("view.radius", SettingKind.Int, "256")), m => m.OnInitialize = c => settings = c.Settings).Load();
            engine.MainContext.Start();
            Assert.Equal(300, settings!.GetInt("view.radius"));
        }
        finally
        {
            next.Dispose();
        }
    }

    [Theory]
    [InlineData("view.fog", SettingFailure.ReadOnly)]
    [InlineData("view.cap", SettingFailure.ReadOnly)]
    [InlineData("view.radius", SettingFailure.InvalidValue)]
    [InlineData("view.title", SettingFailure.WrongType)]
    public void Refuses_a_value_the_player_cannot_give(string key, SettingFailure failure)
    {
        Exception? error = null;
        using Engine engine = View(m => m.OnInitialize = c => error = Record.Exception(() =>
        {
            switch (key)
            {
                case "view.fog":
                    c.Settings.Set(key, true);
                    break;
                case "view.cap":
                case "view.title":
                    c.Settings.Set(key, 5);
                    break;
                default:
                    c.Settings.Set(key, 2000);
                    break;
            }
        })).Load();
        engine.MainContext.Start();

        Assert.Equal(failure, Assert.IsType<SettingException>(error).Failure);
    }

    [Fact]
    public void Lets_a_module_read_its_own_keys_and_those_of_its_dependencies_only()
    {
        Exception? undeclared = null;
        Exception? unknown = null;
        Exception? wrongType = null;
        int fromDependency = 0;
        using Engine engine = View()
            .Module("example.map", m => m.Requires("example.view"), m => m.OnInitialize = c =>
            {
                fromDependency = c.Settings.GetInt("view.radius");
                unknown = Record.Exception(() => c.Settings.GetInt("view.depth"));
                wrongType = Record.Exception(() => c.Settings.GetString("view.radius"));
            })
            .Module("example.other", behavior: m => m.OnInitialize = c => undeclared = Record.Exception(() => c.Settings.GetInt("view.radius")))
            .Load();
        engine.MainContext.Start();

        Assert.Equal(192, fromDependency);
        Assert.Equal(SettingFailure.UnknownKey, Assert.IsType<SettingException>(unknown).Failure);
        Assert.Equal(SettingFailure.WrongType, Assert.IsType<SettingException>(wrongType).Failure);
        SettingException refused = Assert.IsType<SettingException>(undeclared);
        Assert.Equal(SettingFailure.UndeclaredDependency, refused.Failure);
        Assert.Equal("Module 'example.other' asked for 'view.radius' of module 'example.view', which is not one of its declared dependencies.", refused.Message);
    }

    [Fact]
    public void Hides_a_key_of_the_client_from_the_server()
    {
        Exception? error = null;
        using Engine engine = _distribution
            .Module("example.view", m => m.Settings.Add(Key("view.radius", SettingKind.Int, "256", side: F.Side.Client)), m => m.OnInitialize = c => error = Record.Exception(() => c.Settings.GetInt("view.radius")))
            .Load(Side.Server);
        engine.MainContext.Start();

        Assert.Equal(SettingFailure.WrongSide, Assert.IsType<SettingException>(error).Failure);
    }

    [Theory]
    [InlineData(Operation.Set, "view.radius", "100", "The distribution uses <Set> on 'view.radius', a user key of module 'example.view'; <Default> and <Lock> change user keys.")]
    [InlineData(Operation.Lock, "view.cap", "1", "The distribution uses <Lock> on 'view.cap', a distribution key of module 'example.view'; <Set> gives the value of distribution keys.")]
    [InlineData(Operation.Default, "view.radius", "2000", "The value the distribution gives to 'view.radius' of module 'example.view' is invalid: 2000 is above the maximum, 1024.")]
    public void Refuses_an_invalid_value_of_the_distribution(Operation operation, string key, string value, string message)
    {
        _distribution.Module("example.view", m => m.Settings.AddRange(
        [
            Key("view.radius", SettingKind.Int, "256", min: 32, max: 1024),
            Key("view.cap", SettingKind.Int, "10", Scope.Distribution),
        ]));
        _distribution.Configure = d => d.Settings.Add(new F.SettingValue { Key = key, Operation = operation, Value = value });

        var exception = Assert.Throws<ModuleCompositionException>(() => _distribution.Load());

        CompositionProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(CompositionProblemKind.InvalidConfiguration, problem.Kind);
        Assert.Equal(message, problem.Message);
        Assert.Equal(0, _distribution.Instances);
    }

    [Fact]
    public void Refuses_a_key_declared_by_two_modules()
    {
        var exception = Assert.Throws<ModuleCompositionException>(() => _distribution
            .Module("example.view", m => m.Settings.Add(Key("view.radius", SettingKind.Int, "256")))
            .Module("example.map", m => m.Settings.Add(Key("view.radius", SettingKind.Int, "128")))
            .Load());

        Assert.Equal("Module 'example.map' declares the configuration key 'view.radius', which module 'example.view' declares too.", Assert.Single(exception.Problems).Message);
    }

    [Fact]
    public void Gives_each_module_its_configuration_block()
    {
        var block = new F.GenericNode { Name = "ModuleConfig", Source = new F.SourcePosition { File = "engine.nobara", Line = 12, Column = 3 } };
        block.Children.Add(new F.GenericNode
        {
            Name = "Stage",
            Attributes = { new F.GenericAttribute { Name = "name", Value = new F.Value { Text = "example.combat" } } },
            Source = new F.SourcePosition { File = "engine.nobara", Line = 13, Column = 5 },
        });
        _distribution.Configure = d => d.ModuleConfigs.Add(new F.ModuleConfig { Module = "example.game", Config = block });
        GenericElement? game = null;
        GenericElement? other = null;
        bool otherRead = false;
        using Engine engine = _distribution
            .Module("example.game", behavior: m => m.OnInitialize = c => game = c.ModuleConfig)
            .Module("example.other", behavior: m => m.OnInitialize = c => (other, otherRead) = (c.ModuleConfig, true))
            .Load();
        engine.MainContext.Start();

        GenericElement stage = Assert.Single(game!.Children);
        Assert.Equal("example.combat", stage.Attribute("name")!.Value.Text);
        Assert.Equal("engine.nobara(13,5)", stage.Source.ToString());
        Assert.True(otherRead);
        Assert.Null(other);
    }

    [Fact]
    public void Reads_an_element_serialized_in_the_generic_representation()
    {
        var node = new F.GenericNode { Name = "Component", Source = new F.SourcePosition { File = "Resources/food.nof", Line = 4, Column = 6 } };
        node.Attributes.Add(new F.GenericAttribute { Name = "ref", Value = new F.Value { Text = "example.food.edible" } });
        node.Children.Add(new F.GenericNode { Name = "Food", Value = new F.Value { Text = "3", Integer = 3 } });

        GenericElement element = GenericElement.Parse(node.ToByteArray());

        Assert.Equal("example.food.edible", element.Attribute("ref")!.Value.Text);
        Assert.True(element.Child("Food")!.Value!.TryGetInteger(out long food));
        Assert.Equal(3, food);
        Assert.Equal("Resources/food.nof(4,6)", element.Source.ToString());
        Assert.Throws<FormatException>(() => GenericElement.Parse(new byte[] { 0xFF, 0xFF, 0xFF }));
    }
}
