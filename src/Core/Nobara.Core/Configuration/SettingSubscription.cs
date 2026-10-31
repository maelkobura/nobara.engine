// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Configuration;

/// <summary>
/// A handler subscribed to the changes of configuration keys with
/// <see cref="ModuleSettings.Subscribe"/>. Disposing it removes the handler:
/// changes that are not delivered yet are dropped.
/// </summary>
public sealed class SettingSubscription : IDisposable
{
    private readonly SettingStore _store;
    private readonly Func<SettingInfo, bool> _accepts;
    private readonly Action<SendOrPostCallback, object?> _post;
    private readonly Action<string> _handler;
    private volatile bool _revoked;

    internal SettingSubscription(SettingStore store, ModuleRuntime module, Func<SettingInfo, bool> accepts, Action<SendOrPostCallback, object?> post, Action<string> handler)
    {
        _store = store;
        Module = module;
        _accepts = accepts;
        _post = post;
        _handler = handler;
    }

    /// <summary>Gets whether the handler still receives changes.</summary>
    public bool IsActive => !_revoked && !Module.Disabled;

    internal ModuleRuntime Module { get; }

    /// <summary>Removes the handler.</summary>
    public void Dispose()
    {
        if (!_revoked)
        {
            _revoked = true;
            _store.RemoveListener(this);
        }
    }

    /// <summary>Queues the change of a key for the loop thread of the subscriber, when the subscriber can read the key.</summary>
    internal void Notify(SettingInfo info)
    {
        if (IsActive && _accepts(info))
        {
            _post(static state => ((Change)state!).Deliver(), new Change(this, info.Key));
        }
    }

    private sealed class Change(SettingSubscription subscription, string key)
    {
        public void Deliver()
        {
            if (subscription.IsActive)
            {
                subscription._handler(key);
            }
        }
    }
}
