// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Hashing;
using Example.Ticker.Schemas;
using Nobara.Core;
using Nobara.Core.Loop;
using Nobara.Core.Resources;

[assembly: ModuleEntry(typeof(Example.Ticker.TickerModule))]

namespace Example.Ticker;

/// <summary>
/// Counts ticks and stops the game after the number of ticks its tuning
/// gives, or as soon as its speed is 7. It reloads its tuning in place.
/// </summary>
public sealed class TickerModule : IModule
{
    private ModuleContext? _context;
    private int _speed;
    private int _stopAfter;
    private ulong _ticks;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _context = context;
        context.Resources.RegisterDecoder("tuning", data => Tuning.Parser.ParseFrom(data.Content.Span));
        context.Resources.RegisterReloadHandler("tuning", reload =>
        {
            foreach (ResourceData added in reload.Added)
            {
                reload.Refuse(added.ObjectId, "a new tuning needs a restart of the context");
            }

            Read(context);
        });
        context.Loop.Subscribe("Simulation", new TickHandler(this));
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        // The library comes from lib/.
        context.Logger.Info("library hash " + XxHash32.HashToUInt32("ticker"u8).ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
        Read(context);
    }

    private void Read(ModuleContext context)
    {
        Tuning tuning = context.Resources.Get<Tuning>("tuning:example.ticker.main");
        _speed = tuning.Speed;
        _stopAfter = tuning.StopAfterTicks;
        context.Logger.Info("speed " + _speed);
    }

    private void Tick()
    {
        _ticks++;
        if (_speed == 7 || _ticks >= (ulong)_stopAfter)
        {
            _context!.Logger.Info("stopping after " + _ticks + " ticks");
            _context.RequestStop();
        }
    }

    private sealed class TickHandler(TickerModule module) : IPhaseHandler
    {
        public void Run(in PhaseTime time) => module.Tick();
    }
}
