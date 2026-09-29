// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models;

/// <summary>
/// One login to a vCenter: the client, the service content it was read through and the session. Replaced
/// as a whole, never modified, so a reader can never pair one session's client with another's service
/// content, and anything built on a session can tell it has been replaced by comparing references.
/// </summary>
/// <param name="Client">The vSphere SOAP operations. See <see cref="IVimClient"/>.</param>
/// <param name="ClientBase">
/// The same object as <paramref name="Client"/>, concretely typed because connection lifecycle -
/// CommunicationState, CloseAsync, Abort - lives on ClientBase and not on the interface. Null for a
/// substituted client.
/// </param>
/// <param name="Sic">The service content read through <paramref name="Client"/>.</param>
/// <param name="Session">The login, or null where only the client and service content matter.</param>
internal sealed record VsphereSession(IVimClient Client, VimPortClient ClientBase, ServiceContent Sic, UserSession Session)
{
    public VsphereSession(IVimClient client, ServiceContent sic, UserSession session = null)
        : this(client, client as VimPortClient, sic, session)
    {
    }
}
