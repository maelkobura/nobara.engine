// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Event;

/// <summary>
/// The registration of a handler on a bus. Dispose it to remove the handler.
/// </summary>
/// <remarks>
/// The bus revokes every handle of a module when its context stops. A handler
/// that is revoked while a dispatch runs is not called again, even by that
/// dispatch.
/// </remarks>
public sealed class EventHandle : IDisposable
{
    private readonly IRegistration _registration;

    internal EventHandle(IRegistration registration)
    {
        _registration = registration;
    }

    /// <summary>Gets whether the handler was removed.</summary>
    public bool IsRevoked => _registration.Revoked;

    /// <summary>Removes the handler. Calling it again does nothing. It can be called from any thread.</summary>
    public void Dispose() => _registration.Revoke();
}
