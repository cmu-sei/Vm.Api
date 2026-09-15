// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Threading.Tasks;
using VimClient;

namespace Player.Vm.Api.Domain.Vsphere.Models;

/// <summary>
/// The subset of the vSphere SOAP API this service actually calls - two dozen operations out of the
/// several hundred the WSDL defines. It exists so <see cref="VsphereConnection.Client"/> can be
/// substituted in tests: <see cref="VimPortTypeClient"/> is a generated WCF client whose methods are
/// non-virtual, so there is otherwise no seam between VsphereService and a live vCenter.
///
/// The generated <c>VimPortType</c> interface is not usable for this directly. For operations whose
/// response is a message contract - RetrieveProperties, ReadNextEvents, ListProcessesInGuest - it
/// declares only the wrapped form (<c>RetrievePropertiesAsync(RetrievePropertiesRequest)</c>), while
/// the friendly parameter-per-argument overload that every caller here uses is generated on the
/// client class instead. Declaring the friendly signatures ourselves keeps call sites unchanged and
/// keeps request wrappers out of the tests.
///
/// Adding a call to a new vSphere operation means adding it here too. Signatures must match
/// <see cref="VimPortTypeClient"/> exactly, or <see cref="VimPortClient"/> stops compiling - which is
/// the intended feedback.
/// </summary>
public interface IVimClient
{
    // Session and lookup
    Task<ServiceContent> RetrieveServiceContentAsync(ManagedObjectReference _this);
    Task<UserSession> LoginAsync(ManagedObjectReference _this, string userName, string password, string locale);
    Task<ManagedObjectReference> FindByUuidAsync(ManagedObjectReference _this, ManagedObjectReference datacenter, string uuid, bool vmSearch, bool instanceUuid);
    Task<RetrievePropertiesResponse> RetrievePropertiesAsync(ManagedObjectReference _this, PropertyFilterSpec[] specSet);

    // Power
    Task<ManagedObjectReference> PowerOnVM_TaskAsync(ManagedObjectReference _this, ManagedObjectReference host);
    Task<ManagedObjectReference> PowerOffVM_TaskAsync(ManagedObjectReference _this);
    Task RebootGuestAsync(ManagedObjectReference _this);
    Task ShutdownGuestAsync(ManagedObjectReference _this);

    // Snapshots
    Task<ManagedObjectReference> CreateSnapshot_TaskAsync(ManagedObjectReference _this, string name, string description, bool memory, bool quiesce);
    Task<ManagedObjectReference> RemoveSnapshot_TaskAsync(ManagedObjectReference _this, bool removeChildren, bool consolidate);
    Task<ManagedObjectReference> RevertToSnapshot_TaskAsync(ManagedObjectReference _this, ManagedObjectReference host, bool suppressPowerOn);
    Task<ManagedObjectReference> RevertToCurrentSnapshot_TaskAsync(ManagedObjectReference _this, ManagedObjectReference host, bool suppressPowerOn);

    // Guest operations
    Task<long> StartProgramInGuestAsync(ManagedObjectReference _this, ManagedObjectReference vm, GuestAuthentication auth, GuestProgramSpec spec);
    Task<ListProcessesInGuestResponse> ListProcessesInGuestAsync(ManagedObjectReference _this, ManagedObjectReference vm, GuestAuthentication auth, long[] pids);
    Task<FileTransferInformation> InitiateFileTransferFromGuestAsync(ManagedObjectReference _this, ManagedObjectReference vm, GuestAuthentication auth, string guestFilePath);
    Task<string> InitiateFileTransferToGuestAsync(ManagedObjectReference _this, ManagedObjectReference vm, GuestAuthentication auth, string guestFilePath, GuestFileAttributes fileAttributes, long fileSize, bool overwrite);

    // Reconfiguration and console
    Task<ManagedObjectReference> ReconfigVM_TaskAsync(ManagedObjectReference _this, VirtualMachineConfigSpec spec);
    Task SetScreenResolutionAsync(ManagedObjectReference _this, int width, int height);
    Task<VirtualMachineTicket> AcquireTicketAsync(ManagedObjectReference _this, string ticketType);

    // Datastore browsing and events
    Task MakeDirectoryAsync(ManagedObjectReference _this, string name, ManagedObjectReference datacenter, bool createParentDirectories);
    Task<ManagedObjectReference> SearchDatastoreSubFolders_TaskAsync(ManagedObjectReference _this, string datastorePath, HostDatastoreBrowserSearchSpec searchSpec);
    Task<ManagedObjectReference> CreateCollectorForEventsAsync(ManagedObjectReference _this, EventFilterSpec filter);
    Task<ReadNextEventsResponse> ReadNextEventsAsync(ManagedObjectReference _this, int maxCount);
    Task DestroyCollectorAsync(ManagedObjectReference _this);
}

/// <summary>
/// The real <see cref="IVimClient"/>: a <see cref="VimPortTypeClient"/> that also advertises the
/// interface. There is deliberately no body - the generated client already declares every
/// <see cref="IVimClient"/> member with a matching public signature, so inheritance satisfies the
/// interface and there is no forwarding layer that could drift from what it wraps.
///
/// Connection lifecycle (CommunicationState, CloseAsync, Dispose) is not on the interface and is
/// reached through this concrete type, which is why <see cref="VsphereConnection"/> keeps a
/// separately-typed reference to the same object.
/// </summary>
public class VimPortClient : VimPortTypeClient, IVimClient
{
    public VimPortClient(EndpointConfiguration endpointConfiguration, string remoteAddress)
        : base(endpointConfiguration, remoteAddress)
    {
    }
}
