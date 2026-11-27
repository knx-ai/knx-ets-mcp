using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Knx.Ets.Common.Types.Enumerations;
using Knx.Ets.Common.Types.Strategies;
using Knx.Ets.Sdk;
using Knx.Ets.Sdk.AddIns.AddInViews;
using Knx.Ets.Sdk.MasterData;
using Knx.Ets.Sdk.PlugIn;
using Knx.Ets.Sdk.Product;
using Knx.Ets.Sdk.Project;
using Knx.Ets.Sdk.Network;
using Knx.Ets.Sdk.UndoRedo;
using Knx.Ets.Sdk.External;
using Knx.Ets.Sdk.UnifiedCatalog;

namespace Knx.EtsBridge.Addin
{
    /// <summary>
    /// ETS 6.3.0 implementation of <see cref="IEtsProjectGateway"/>.
    /// Every public method MUST be called on the ETS UI thread (via <see cref="EtsDispatcher"/>).
    /// Every mutation is wrapped in a Project.UndoManager marker.
    ///
    /// Ref encoding (project-stable, opaque to MCP server):
    ///   Device       -> "d{Puid}"
    ///   GroupAddress -> "ga:{DomObject.Id}"  (#8: stable, not address-derived)
    ///   ComObject    -> "d{devicePuid}:c{Number}"              (device-level)
    ///                   "d{devicePuid}:m{moduleIndex}:c{Number}" (module-level; index is the
    ///                   module instance's position -- its Puid is 0 for every instance)
    ///   Parameter    -> "d{devicePuid}:p:{UniqueId}"        (#6: prefixed, round-trips)
    ///   Area         -> "a{Address}"
    ///   Line         -> "a{areaAddr}:l{lineAddr}"
    ///   Segment      -> "a{areaAddr}:l{lineAddr}:s{segNumber}"
    ///   Manufacturer -> "m{KnxManufacturerId}"
    ///   CatalogItem  -> "ci:{Id}"
    ///   BuildingPart -> "bp:{DomObject.Id}"
    ///   BuildingFunc -> "bf:{DomObject.Id}"
    /// </summary>
    internal sealed class Ets6ProjectGateway : IEtsProjectGateway, IDisposable
    {
        private readonly IInitializationContext _ctx;
        // Captured during construction (on the UI thread) so background tasks
        // can marshal SDK object creation back to the dispatcher (#4).
        private readonly System.Windows.Threading.Dispatcher _uiDispatcher;
        private long _revisionCounter;

        // -- Job registry for device.program / firmware.update / scan / monitor / unload --
        // Maps jobId -> JobEntry. Thread-safe for concurrent event handler + read access.
        private readonly ConcurrentDictionary<string, JobEntry> _jobs
            = new ConcurrentDictionary<string, JobEntry>(StringComparer.Ordinal);
        private bool _eventSubscribed;
        private bool _disposed;

        // ---------------------------------------------------------------
        // #3/#7/#8: Owner-aware bus lease.
        // The _busOwner field is null when the bus is free, or holds the
        // owner token (jobId or GUID) of the lease holder. All access is
        // synchronized via lock(_busLock).
        // ---------------------------------------------------------------
        private readonly object _busLock = new object();
        private readonly MonitoredConnection<KnxCommunicationSync> _groupMonitorConnection;
        private string? _busOwner;

        /// <summary>
        /// #7: Acquire the bus for the given owner token.
        /// Throws BusUnavailableException if the bus is already held by another owner.
        /// </summary>
        private void AcquireBus(string owner)
        {
            lock (_busLock)
            {
                if (_busOwner != null && _busOwner != owner)
                {
                    // A LIVE, non-terminal job legitimately holds the lease for as long as
                    // the operation runs (download/scan/monitor/firmware) -- we NEVER reclaim
                    // from a running job, no matter how long it takes. Only reclaim a lease
                    // whose owner is NOT a live job: that is a leaked lease from a direct op
                    // that failed to release, or a job that already failed at startup and was
                    // removed from the registry. This self-heals leaks without aborting long ops.
                    bool holderIsLiveJob =
                        _jobs.TryGetValue(_busOwner, out var holderJob) && !holderJob.IsTerminal;
                    if (holderIsLiveJob)
                        throw new BusUnavailableException(
                            "Another bus operation is in progress. Only one bus operation can run "
                          + "at a time; it releases when that operation completes (or use job.cancel).");

                    System.Diagnostics.Trace.TraceWarning(
                        $"[EtsBridge] Reclaiming leaked bus lease from non-job owner '{_busOwner}'.");
                    _busOwner = null;
                }
                _busOwner = owner;
            }
        }

        /// <summary>
        /// #3/#4: Release the bus ONLY if the caller is the current owner.
        /// A non-owner release is a safe no-op (prevents cross-job release).
        /// </summary>
        private void ReleaseBus(string owner)
        {
            lock (_busLock)
            {
                if (_busOwner == owner)
                    _busOwner = null;
                // else: not our lease, no-op (#3: prevents firmware.update releasing download's lock)
            }
        }

        /// <summary>
        /// #8: Job operation type for correlating OnOnlineOperationsEvent to the correct job.
        /// </summary>
        private enum JobOpType
        {
            Download,
            FirmwareUpdate,
            Unload,
            Scan,
            Monitor,
            SetIndividualAddress
        }

        private sealed class JobEntry
        {
            public readonly string JobId;
            public readonly string DeviceRef;
            public readonly Device? Device;
            public readonly JobOpType OpType;
            public readonly string BusOwner; // the owner token for the bus lease
            public volatile string State;
            public volatile int Percent;
            public volatile string? Error;
            public volatile object? Result;
            public System.Threading.CancellationTokenSource? Cts;

            public JobEntry(string jobId, string deviceRef, Device? device, JobOpType opType, string busOwner)
            {
                JobId = jobId;
                DeviceRef = deviceRef;
                Device = device;
                OpType = opType;
                BusOwner = busOwner;
                State = "running";
                Percent = 0;
            }

            /// <summary>#8: True when the job has reached a terminal state and must not be mutated.</summary>
            public bool IsTerminal =>
                State == "done" || State == "failed" || State == "canceled";
        }

        // #8: Bounded retention for terminal jobs (keep last N).
        private const int MaxTerminalJobs = 200;

        public Ets6ProjectGateway(IInitializationContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
            _groupMonitorConnection = new MonitoredConnection<KnxCommunicationSync>(_busLock);
            // #4: Capture the UI dispatcher so background tasks can marshal
            // SDK object creation (CreateDeviceManagement) to the UI thread.
            _uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Unsubscribe the OnOnlineOperationsEvent handler.
            if (_eventSubscribed)
            {
                try { Project.OnOnlineOperationsEvent -= OnOnlineOperationEvent; }
                catch { /* best-effort during shutdown */ }
                _eventSubscribed = false;
            }

            // Dispose all CancellationTokenSources in the job registry.
            foreach (var kvp in _jobs)
            {
                kvp.Value.Cts?.Dispose();
            }
        }

        // -- Helpers: SDK object lookup by ref string --

        private Project Project => _ctx.Project;
        private Installation Inst => Project.DefaultInstallation;

        public string ProjectRevision => _revisionCounter.ToString();

        // #19: Expose ProjectId for idempotency cache scoping.
        // ETS5: Project.ProjectId (ushort); ETS6: Project.ProjectGuid (Guid).
#if ETS5
        public string ProjectId => Project.ProjectId.ToString();
#else
        public string ProjectId => Project.ProjectGuid.ToString();
#endif

        private void BumpRevision() => System.Threading.Interlocked.Increment(ref _revisionCounter);

        // When a batch.apply is running, its outer marker is held here so sub-operations
        // run directly under it instead of each opening its own child marker. Set on the UI
        // thread only (a batch executes in a single UI-thread round), so no locking is needed.
        private Marker? _activeBatchMarker;

        /// <summary>
        /// #14: Run a mutation inside an UndoManager marker with proper rollback.
        /// On exception before Commit, Discard() is called to roll back all operations.
        /// Verified: M:Knx.Ets.Sdk.UndoRedo.Marker.Discard (SDK XML line 29402)
        ///   "Discards this marker and rolls back all operations associated with this marker."
        /// Verified: M:Knx.Ets.Sdk.UndoRedo.Marker.Dispose (SDK XML line 29411)
        ///   Dispose does NOT roll back -- it only removes from parent's child marker list.
        /// Inside a batch (BeginMarker has set _activeBatchMarker) the body runs directly under
        /// that one outer marker: every sub-operation then belongs to a single marker that the
        /// batch commits or discards as a whole, rather than each committing its own child
        /// marker. The outer marker owns the revision bump.
        /// </summary>
        private T RunInMarker<T>(string name, Func<T> body)
        {
            if (_activeBatchMarker != null)
                return body();

            var marker = Project.UndoManager.OpenMarker(name);
            try
            {
                var result = body();
                marker.Commit();
                BumpRevision();
                return result;
            }
            catch
            {
                try { marker.Discard(); }
                catch { /* Discard best-effort; may throw if marker already closed */ }
                throw;
            }
            finally
            {
                marker.Dispose();
            }
        }

        private void RunInMarker(string name, Action body)
        {
            RunInMarker<object?>(name, () => { body(); return null; });
        }

        // -- Batch orchestration marker primitives (see IEtsProjectGateway) --
        // batch.apply wraps all its sub-operations in ONE outer marker. While it is open,
        // RunInMarker runs sub-operations directly under it (no child markers), so the whole
        // batch commits or discards as a unit. MUST run on the UI thread.
        //
        // Rollback is best-effort: the SDK undoes a discarded marker's operations in reverse,
        // and if one operation's undo throws (e.g. after an internal SDK failure left it
        // partially applied) the traversal can stop, leaving earlier operations applied.
        // DiscardMarker therefore reports whether Discard completed without error so the
        // caller can tell the user the rollback was clean vs. possibly partial.

        public object BeginMarker(string name)
        {
            var marker = Project.UndoManager.OpenMarker(name);
            _activeBatchMarker = marker;
            return marker;
        }

        public void CommitMarker(object markerHandle)
        {
            var marker = (Marker)markerHandle;
            try
            {
                marker.Commit();
                BumpRevision();
            }
            finally
            {
                if (ReferenceEquals(_activeBatchMarker, marker)) _activeBatchMarker = null;
                marker.Dispose();
            }
        }

        /// <summary>
        /// Discard (roll back) the batch marker. Returns true if Discard completed without
        /// throwing; false if it threw, which means the rollback may be incomplete.
        /// </summary>
        public bool DiscardMarker(object markerHandle)
        {
            var marker = (Marker)markerHandle;
            bool ok = true;
            try { marker.Discard(); }
            catch { ok = false; /* rollback may be partial; reported to the caller */ }
            finally
            {
                if (ReferenceEquals(_activeBatchMarker, marker)) _activeBatchMarker = null;
                marker.Dispose();
            }
            return ok;
        }

        private Device FindDevice(string deviceRef)
        {
            if (!deviceRef.StartsWith("d", StringComparison.Ordinal) ||
                !uint.TryParse(deviceRef.Substring(1), out var puid))
                throw new ArgumentException($"Invalid device ref: {deviceRef}");

            var device = Inst.AllDevices.Cast<Device>().FirstOrDefault(d => d.Puid == puid);
            if (device == null)
                throw new KeyNotFoundException($"Device {deviceRef} not found.");
            return device;
        }

        /// <summary>
        /// #8: GA ref is now "ga:{DomObject.Id}" -- stable across address changes.
        /// </summary>
        private GroupAddress FindGroupAddress(string gaRef)
        {
            if (!gaRef.StartsWith("ga:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid GA ref: {gaRef}");

            var id = gaRef.Substring(3);
            var ga = Inst.AllGroupAddresses.Cast<GroupAddress>()
                         .FirstOrDefault(g => g.Id == id);
            if (ga == null)
                throw new KeyNotFoundException($"GroupAddress {gaRef} not found.");
            return ga;
        }

        /// <summary>
        /// Build the canonical gaRef for a GroupAddress.
        /// </summary>
        private static string BuildGaRef(GroupAddress ga)
        {
            // #8: Use DomObject.Id (stable) instead of the mutable address.
            return $"ga:{ga.Id}";
        }

        /// <summary>
        /// Parse a composite comobject ref and resolve it to its device + CO.
        /// Device-level: "d{puid}:c{number}". Module-level: "d{puid}:m{index}:c{number}"
        /// where index is the module instance's position in the device's module list.
        /// Resolution is by the device-unique com-object Number: a module ref prefers the
        /// indicated module instance, then falls back to a device-wide scan by Number. The
        /// fallback keeps legacy "m{puid}" refs (which always carried puid 0) working and is
        /// robust to module-ordering changes. The active collection is preferred throughout
        /// (after a parameter change the SDK may expose the activated object only there).
        /// </summary>
        private (Device device, ComObjectInstanceRef co) FindComObject(string coRef)
        {
            var parts = coRef.Split(':');
            if (parts.Length != 2 && parts.Length != 3)
                throw new ArgumentException($"Invalid comobject ref: {coRef}");
            if (!parts[0].StartsWith("d", StringComparison.Ordinal)
                || !parts[parts.Length - 1].StartsWith("c", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid comobject ref: {coRef}");

            var device = FindDevice(parts[0]);
            if (!uint.TryParse(parts[parts.Length - 1].Substring(1), out var coNumber))
                throw new ArgumentException($"Invalid comobject number in ref: {coRef}");

            // Module-level: prefer the indicated module instance (by index), else fall back.
            if (parts.Length == 3)
            {
                if (!parts[1].StartsWith("m", StringComparison.Ordinal))
                    throw new ArgumentException($"Invalid comobject ref: {coRef}");
                if (!uint.TryParse(parts[1].Substring(1), out var moduleIndex))
                    throw new ArgumentException($"Invalid module index in ref: {coRef}");

                var modules = device.ModuleInstances.Cast<ModuleInstance>().ToList();
                if (moduleIndex < modules.Count)
                {
                    var hit = FindCoByNumber(modules[(int)moduleIndex], coNumber);
                    if (hit != null) return (device, hit);
                }
                // Out-of-range index, or number not in that module (legacy "m0" ref for a
                // later module): fall through to the device-wide scan below.
            }

            var co = FindCoByNumberAnywhere(device, coNumber);
            if (co == null)
                throw new KeyNotFoundException($"ComObject {coRef} not found.");
            return (device, co);
        }

        /// <summary>
        /// Build the canonical comobject ref for a CO.
        /// Module-level COs are identified by the module instance's 0-based position in the
        /// device's module list, not by its Puid: a module instance's Puid is 0 for every
        /// instance of the same module (its underlying model object carries no Puid), so the
        /// Puid cannot distinguish two channels of a modular device. The com-object Number is
        /// device-unique, so the (module index, number) pair is unambiguous.
        /// </summary>
        private static string BuildCoRef(long devicePuid, ComObjectInstanceRef co)
            => BuildCoRef(devicePuid, co, ResolveModuleIndex(co));

        /// <summary>
        /// Build a comobject ref with a known module index (-1 = device-level). Callers that
        /// already iterate module instances pass the loop index to avoid a lookup.
        /// </summary>
        private static string BuildCoRef(long devicePuid, ComObjectInstanceRef co, int moduleIndex)
            => moduleIndex >= 0
                ? $"d{devicePuid}:m{moduleIndex}:c{co.Number}"
                : $"d{devicePuid}:c{co.Number}";

        /// <summary>
        /// Resolve a module CO's 0-based module index. Returns -1 for a device-level CO.
        /// Matches the parent module instance by reference first; falls back to the module
        /// that contains the CO's (device-unique) Number if reference matching fails.
        /// </summary>
        private static int ResolveModuleIndex(ComObjectInstanceRef co)
        {
            var parentModule = co.ParentModuleInstance;
            if (parentModule == null) return -1;
            var device = parentModule.Parent;
            if (device == null) return -1;

            int i = 0;
            foreach (ModuleInstance m in device.ModuleInstances)
            {
                if (ReferenceEquals(m, parentModule)) return i;
                i++;
            }
            i = 0;
            foreach (ModuleInstance m in device.ModuleInstances)
            {
                if (ModuleContainsNumber(m, co.Number)) return i;
                i++;
            }
            return -1;
        }

        private static bool ModuleContainsNumber(ModuleInstance module, uint number)
            => module.ActiveComObjectInstanceRefs.Cast<ComObjectInstanceRef>().Any(c => c.Number == number)
            || module.ComObjectInstanceRefs.Cast<ComObjectInstanceRef>().Any(c => c.Number == number);

        private static ComObjectInstanceRef? FindCoByNumber(ModuleInstance module, uint number)
            => module.ActiveComObjectInstanceRefs.Cast<ComObjectInstanceRef>().FirstOrDefault(c => c.Number == number)
            ?? module.ComObjectInstanceRefs.Cast<ComObjectInstanceRef>().FirstOrDefault(c => c.Number == number);

        /// <summary>
        /// Find a com-object by its device-unique Number across the device-level collections
        /// and every module instance (active collection preferred, then the full collection).
        /// </summary>
        private static ComObjectInstanceRef? FindCoByNumberAnywhere(Device device, uint number)
        {
            var co = device.ActiveComObjectInstanceRefs.Cast<ComObjectInstanceRef>().FirstOrDefault(c => c.Number == number)
                  ?? device.ComObjectInstanceRefs.Cast<ComObjectInstanceRef>().FirstOrDefault(c => c.Number == number);
            if (co != null) return co;
            foreach (ModuleInstance m in device.ModuleInstances)
            {
                co = FindCoByNumber(m, number);
                if (co != null) return co;
            }
            return null;
        }

        /// <summary>
        /// #6: Parse "d{puid}:p:{uniqueId}" and resolve to ParameterInstanceRef.
        /// The paramRef is the full prefixed ref as emitted by ListParameters.
        /// </summary>
        private ParameterInstanceRef FindParameter(string deviceRef, string paramRef)
        {
            var device = FindDevice(deviceRef);

            // #6: paramRef format is "p:{uniqueId}" -- strip the prefix.
            string uniqueId;
            if (paramRef.StartsWith("p:", StringComparison.Ordinal))
                uniqueId = paramRef.Substring(2);
            else
                uniqueId = paramRef; // backwards compat: accept raw UniqueId

            // Search global params first, then module instances.
            var param = device.ParameterInstanceRefs.Cast<ParameterInstanceRef>()
                            .FirstOrDefault(p => p.UniqueId == uniqueId);
            if (param == null)
            {
                foreach (var module in device.ModuleInstances.Cast<ModuleInstance>())
                {
                    param = module.ParameterInstanceRefs.Cast<ParameterInstanceRef>()
                                .FirstOrDefault(p => p.UniqueId == uniqueId);
                    if (param != null) break;
                }
            }
            if (param == null)
                throw new KeyNotFoundException($"Parameter '{paramRef}' not found on device '{deviceRef}'.");
            return param;
        }

        // -- Helper: build CO flags string --

        private static string BuildFlags(ComObjectInstanceRef co)
        {
            var flags = new List<string>(6);
            if (co.CommunicationFlag) flags.Add("C");
            if (co.ReadFlag) flags.Add("R");
            if (co.WriteFlag) flags.Add("W");
            if (co.TransmitFlag) flags.Add("T");
            if (co.UpdateFlag) flags.Add("U");
            if (co.ReadOnInitFlag) flags.Add("I");
            return string.Join("", flags);
        }

        // -- Helper: format DPT (#9) --

        /// <summary>
        /// #9: Return "main.sub" (e.g. "1.001"), not just the subtype number.
        /// Verified: P:Knx.Ets.Sdk.MasterData.DatapointSubtype.Parent (exists in 6.3.0)
        /// Verified: P:Knx.Ets.Sdk.MasterData.DatapointSubtype.Number
        /// Verified: P:Knx.Ets.Sdk.MasterData.DatapointType.Number
        /// </summary>
        private static string? FormatDpt(object? dptObj)
        {
            if (dptObj is DatapointSubtype sub)
            {
                // Parent returns the owning DatapointType.
                var parent = sub.Parent as DatapointType;
                if (parent != null)
                    return $"{parent.Number}.{sub.Number:D3}";
                // Fallback if Parent is unexpectedly null.
                return sub.Number.ToString();
            }
            if (dptObj is DatapointType dt)
                return dt.Number.ToString();
            return null;
        }

        /// <summary>Build a full GroupAddressInfo DTO from a GroupAddress.
        /// Verified: P:Knx.Ets.Sdk.Project.GroupAddress.Comment (get/set, SDK XML line 14336)
        /// </summary>
#if ETS5
        private GroupAddressInfo BuildGaInfo(GroupAddress ga)
        {
            return new GroupAddressInfo
            {
                Ref = BuildGaRef(ga),
                Address = FormatGroupAddressEts5(ga),
                Name = ga.Name ?? "",
                Description = ga.Description,
                Comment = ga.Comment,
                Dpt = FormatDpt(ga.DatapointTypeObject)
            };
        }
#else
        private static GroupAddressInfo BuildGaInfo(GroupAddress ga)
        {
            return new GroupAddressInfo
            {
                Ref = BuildGaRef(ga),
                Address = ga.AddressString ?? ga.AddressValue.Address.ToString(),
                Name = ga.Name ?? "",
                Description = ga.Description,
                Comment = ga.Comment,
                Dpt = FormatDpt(ga.DatapointTypeObject)
            };
        }
#endif

        /// <summary>Build a DeviceInfo DTO from a Device.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.Description (get/set, SDK XML line 12683)
        /// Verified: P:Knx.Ets.Sdk.Project.Device.Comment (get/set, SDK XML line 12632)
        /// </summary>
        private static DeviceInfo BuildDeviceInfo(Device dev)
        {
            return new DeviceInfo
            {
                Ref = $"d{dev.Puid}",
                Address = dev.IndividualAddressString ?? "",
                Name = dev.Name ?? "",
                Description = dev.Description,
                Comment = dev.Comment,
                Product = ProductName(dev),
                OrderNumber = ProductOrderNumber(dev),
                Line = dev.Line?.Name ?? ""
            };
        }

        // Product name / order number with fallback: dev.CatalogItem can be null
        // or have an empty Name for some devices, but dev.Product still carries the
        // product text + order number. Verified: P:Knx.Ets.Sdk.Project.Device.Product
        // (Knx.Ets.Sdk.Product.Product), P:Product.Text, P:Product.OrderNumber,
        // P:Product.CatalogItem.Name, P:CatalogItem.Number.
        private static string ProductName(Device dev)
        {
            var n = dev.CatalogItem?.Name;
            return !string.IsNullOrEmpty(n) ? n! : (dev.Product?.Text ?? "");
        }

        private static string ProductOrderNumber(Device dev)
        {
            // Product.OrderNumber is the human order number (string); CatalogItem.Number
            // is a numeric index, not the order number, so use Product.OrderNumber.
            return dev.Product?.OrderNumber ?? "";
        }

        // ---------------------------------------------------------------
        // Topology/catalog lookup helpers for device.addFromCatalog
        // ---------------------------------------------------------------

        /// <summary>
        /// Resolve lineRef "a{areaAddr}:l{lineAddr}" to a Line object.
        /// Reuses the same topology traversal pattern as ListTopology.
        /// Verified: P:Knx.Ets.Sdk.Project.Installation.Areas (SDK XML line 15480)
        /// Verified: P:Knx.Ets.Sdk.Project.Area.Lines (SDK XML line 8950)
        /// Verified: P:Knx.Ets.Sdk.Project.Area.Address, P:Knx.Ets.Sdk.Project.Line.Address
        /// </summary>
        private Line FindLine(string lineRef)
        {
            var parts = lineRef.Split(':');
            if (parts.Length != 2
                || !parts[0].StartsWith("a", StringComparison.Ordinal)
                || !parts[1].StartsWith("l", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid line ref: {lineRef}");

            if (!ushort.TryParse(parts[0].Substring(1), out var areaAddr))
                throw new ArgumentException($"Invalid area address in line ref: {lineRef}");
            if (!ushort.TryParse(parts[1].Substring(1), out var lineAddr))
                throw new ArgumentException($"Invalid line address in line ref: {lineRef}");

            foreach (Area area in Inst.Areas)
            {
                if (area.Address != areaAddr) continue;
                foreach (Line line in area.Lines)
                {
                    if (line.Address == lineAddr)
                        return line;
                }
            }

            throw new KeyNotFoundException($"Line {lineRef} not found in topology.");
        }

        /// <summary>
        /// Resolve areaRef "a{addr}" to an Area object.
        /// </summary>
        private Area FindArea(string areaRef)
        {
            if (!areaRef.StartsWith("a", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid area ref: {areaRef}");

            if (!ushort.TryParse(areaRef.Substring(1), out var areaAddr))
                throw new ArgumentException($"Invalid area address in ref: {areaRef}");

            foreach (Area area in Inst.Areas)
            {
                if (area.Address == areaAddr)
                    return area;
            }

            throw new KeyNotFoundException($"Area {areaRef} not found in topology.");
        }

        /// <summary>
        /// Resolve groupRangeRef "gr:{DomObject.Id}" to a GroupRange object.
        /// Searches recursively through all group range levels.
        /// </summary>
        private GroupRange FindGroupRange(string grRef)
        {
            if (!grRef.StartsWith("gr:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid group range ref: {grRef}");

            var id = grRef.Substring(3);
            var found = FindGroupRangeById(Inst.GroupRanges, id);
            if (found == null)
                throw new KeyNotFoundException($"GroupRange {grRef} not found.");
            return found;
        }

        private static GroupRange? FindGroupRangeById(GroupRangeCollection ranges, string id)
        {
            foreach (GroupRange gr in ranges)
            {
                if (gr.Id == id)
                    return gr;
                if (gr.GroupRanges != null)
                {
                    var sub = FindGroupRangeById(gr.GroupRanges, id);
                    if (sub != null)
                        return sub;
                }
            }
            return null;
        }

        private static string BuildGroupRangeRef(GroupRange gr) => $"gr:{gr.Id}";

        /// <summary>
        /// Resolve catalogItemRef "ci:{id}" to a CatalogItem.
        /// First searches the project-local catalog (Root.Manufacturers). If not found,
        /// searches the global product store (Root.GlobalProductStoreManufacturers) by
        /// ExternalProduct.Id, auto-internalizes the hardware, then re-resolves from the
        /// project catalog.
        ///
        /// GlobalProductStoreManufacturers yields ExternalManufacturer (Knx.Ets.Sdk.External),
        /// NOT Manufacturer (Knx.Ets.Sdk.Product). The traversal path is:
        ///   ExternalManufacturer -> HardwareCollection -> ExternalHardware -> Products -> ExternalProduct
        ///
        /// Verified: P:Knx.Ets.Sdk.Root.GlobalProductStoreManufacturers (SDK XML line 28242)
        ///   "Returns a list of the Manufacturers having currently data in the global product store."
        ///   Element type: ExternalManufacturer (Knx.Ets.Sdk.External).
        /// Verified: P:Knx.Ets.Sdk.External.ExternalManufacturer.HardwareCollection (SDK XML line 20082)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalHardware.Products (SDK XML line 20181)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalProduct.Id (SDK XML line 20250)
        /// Verified: M:Knx.Ets.Sdk.Root.InternalizeProductData(System.String) (SDK XML line 28252)
        ///   "Imports the product data from the global product store into the current Project."
        ///   param hardwareId: "The Id of the corresponding hardware".
        /// Verified: P:Knx.Ets.Sdk.Product.Manufacturer.AllCatalogItems (SDK XML line 8209)
        /// Verified: P:Knx.Ets.Sdk.DomObject.Id (SDK XML line 19710, inherited by CatalogItem)
        ///
        /// </summary>
        private CatalogItem FindCatalogItem(string catalogItemRef)
        {
            if (!catalogItemRef.StartsWith("ci:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid catalog item ref: {catalogItemRef}");

            var id = catalogItemRef.Substring(3);

            // 1. Try project-local first (fast path, covers already-internalized items).
            foreach (Manufacturer mfr in Project.Root.Manufacturers)
            {
                foreach (CatalogItem item in mfr.AllCatalogItems)
                {
                    if (item.Id == id)
                        return item;
                }
            }

            // 2. Not in project. Search the global product store by ExternalProduct.Id.
            //    If found, auto-internalize the hardware, then re-resolve from the project.
            foreach (ExternalManufacturer emfr in Project.Root.GlobalProductStoreManufacturers)
            {
                foreach (ExternalHardware hw in emfr.HardwareCollection)
                {
                    foreach (ExternalProduct ep in hw.Products)
                    {
                        if (ep.Id == id)
                        {
                            // Internalize the hardware into the project.
                            Project.Root.InternalizeProductData(hw.Id);
                            BumpRevision();

                            // Re-search project-local by ID (may match after internalization).
                            foreach (Manufacturer mfr2 in Project.Root.Manufacturers)
                            {
                                foreach (CatalogItem ci in mfr2.AllCatalogItems)
                                {
                                    if (ci.Id == id)
                                        return ci;
                                }
                            }

                            // ID didn't match. Fall back to OrderNumber + manufacturer.
                            var targetOrderNumber = ep.OrderNumber;
                            if (!string.IsNullOrEmpty(targetOrderNumber))
                            {
                                foreach (Manufacturer mfr2 in Project.Root.Manufacturers)
                                {
                                    if (mfr2.KnxManufacturerId != emfr.KnxManufacturerId)
                                        continue;
                                    foreach (CatalogItem ci in mfr2.AllCatalogItems)
                                    {
                                        if (string.Equals(ci.Product?.OrderNumber, targetOrderNumber,
                                                StringComparison.OrdinalIgnoreCase))
                                            return ci;
                                    }
                                }
                            }

                            throw new InvalidOperationException(
                                $"Internalized hardware '{hw.Id}' from global product store but "
                              + $"could not resolve the resulting CatalogItem for '{catalogItemRef}'.");
                        }
                    }
                }
            }

            throw new KeyNotFoundException(
                $"CatalogItem {catalogItemRef} not found in project or global product store.");
        }

        /// <summary>
        /// Parse an individual address string "area.line.device" (e.g. "1.1.5") and
        /// validate that the area/line portion matches the target Line.
        /// Returns the device-level address byte (0-255) for DeviceCollection.Add.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.Address "Gets or sets the device address [0...255]" (SDK XML line 12527)
        /// Verified: P:Knx.Ets.Sdk.Project.Line.Area (SDK XML line 15798)
        /// </summary>
        private static ushort ParseIndividualAddress(string address, Line line)
        {
            address = (address ?? "").Trim().Replace(',', '.');
            var parts = address.Split('.');
            if (parts.Length != 3)
                throw new ArgumentException(
                    $"Invalid individual address format: '{address}'. Expected 'area.line.device' (e.g. '1.1.5').");

            if (!ushort.TryParse(parts[0], out var areaVal) || areaVal > 15)
                throw new ArgumentException($"Invalid area in individual address: {parts[0]}");
            if (!ushort.TryParse(parts[1], out var lineVal) || lineVal > 15)
                throw new ArgumentException($"Invalid line in individual address: {parts[1]}");
            if (!ushort.TryParse(parts[2], out var deviceVal) || deviceVal > 255)
                throw new ArgumentException($"Invalid device in individual address: {parts[2]}");

            if (line.Area.Address != areaVal)
                throw new ArgumentException(
                    $"Area {areaVal} in address does not match target line's area {line.Area.Address}.");
            if (line.Address != lineVal)
                throw new ArgumentException(
                    $"Line {lineVal} in address does not match target line address {line.Address}.");

            return deviceVal;
        }

        // ---------------------------------------------------------------
        // Read methods
        // ---------------------------------------------------------------

        public ProjectInfo GetProjectInfo()
        {
            return new ProjectInfo
            {
#if ETS5
                ProjectId = Project.ProjectId.ToString(),
#else
                ProjectId = Project.ProjectGuid.ToString(),
#endif
                Name = Project.Name,
                GroupAddressStyle = Project.GroupAddressStyle.ToString(),
                Revision = ProjectRevision
            };
        }

        public List<DeviceInfo> ListDevices()
        {
            var result = new List<DeviceInfo>();
            // #15: Dedup by Device.Puid. AllDevices may surface the same device
            // multiple times via different topology/building paths.
            var seenPuids = new HashSet<long>();
            foreach (Device dev in Inst.AllDevices)
            {
                if (seenPuids.Add(dev.Puid))
                    result.Add(BuildDeviceInfo(dev));
            }
            return result;
        }

        public List<GroupAddressInfo> ListGroupAddresses()
        {
            var result = new List<GroupAddressInfo>();
            foreach (GroupAddress ga in Inst.AllGroupAddresses)
            {
                result.Add(BuildGaInfo(ga));
            }
            return result;
        }

        public List<ComObjectInfo> ListComObjects(string deviceRef)
        {
            var device = FindDevice(deviceRef);
            var result = new List<ComObjectInfo>();
            var devicePuid = device.Puid;

            // Map each ComObject to its channel/function label so a flat list can be grouped.
            // Built defensively: any channel-traversal quirk just leaves objects ungrouped.
            var channelByCoRef = BuildChannelByCoRef(device, devicePuid);

            void AddCos(IEnumerable<ComObjectInstanceRef> cos, int moduleIndex)
            {
                foreach (var co in cos)
                {
                    var links = new List<string>();
                    foreach (Connector conn in co.Connectors)
                    {
                        if (conn.GroupAddress != null)
                            links.Add(BuildGaRef(conn.GroupAddress));
                    }

                    var coRef = BuildCoRef(devicePuid, co, moduleIndex);
                    channelByCoRef.TryGetValue(coRef, out var channelLabel);

                    result.Add(new ComObjectInfo
                    {
                        // Module index keeps refs unique across module instances.
                        Ref = coRef,
                        Number = co.Number,
                        Name = co.Name ?? "",
                        Channel = string.IsNullOrEmpty(channelLabel) ? null : channelLabel,
                        Block = BuildBlockPath(co),
                        // Verified: P:Knx.Ets.Sdk.Project.ComObjectInstanceRef.Description (get/set, SDK XML line 11788)
                        Description = co.Description,
                        // Verified: P:Knx.Ets.Sdk.Project.ComObjectInstanceRef.FunctionText (get/set, SDK XML line 11802)
                        FunctionText = co.FunctionText,
                        // Verified: P:Knx.Ets.Sdk.Project.ComObjectInstanceRef.Text (get/set, SDK XML line 11991)
                        Text = co.Text,
                        Dpt = FormatDpt(co.DatapointTypes?.Cast<object>().FirstOrDefault()),
                        Flags = BuildFlags(co),
                        Links = links
                    });
                }
            }

            AddCos(device.ActiveComObjectInstanceRefs.Cast<ComObjectInstanceRef>(), -1);
            int moduleIndex = 0;
            foreach (ModuleInstance mod in device.ModuleInstances)
            {
                AddCos(mod.ActiveComObjectInstanceRefs.Cast<ComObjectInstanceRef>(), moduleIndex);
                moduleIndex++;
            }

            return result;
        }

        /// <summary>
        /// Builds a map coRef -> channel label by walking the device's ChannelInstances
        /// (and each module's ChannelInstances) and their ActiveComObjectInstances. The
        /// label prefers the (user-editable) Name, then the display Text, then the
        /// application-program channel id. Fully defensive: any traversal quirk leaves the
        /// affected objects ungrouped rather than failing the ComObject listing.
        /// </summary>
        private static Dictionary<string, string> BuildChannelByCoRef(Device device, long devicePuid)
        {
            var map = new Dictionary<string, string>();

            void Index(System.Collections.IEnumerable channels, int moduleIndex)
            {
                if (channels == null) return;
                foreach (ChannelInstance ch in channels)
                {
                    string label;
                    try
                    {
                        label = !string.IsNullOrEmpty(ch.Name) ? ch.Name
                              : !string.IsNullOrEmpty(ch.Text) ? ch.Text
                              : (ch.ApplicationProgramChannelId ?? "");
                    }
                    catch { label = ""; }
                    if (string.IsNullOrEmpty(label)) continue;

                    try
                    {
                        foreach (ComObjectInstanceRef co in ch.ActiveComObjectInstances)
                            map[BuildCoRef(devicePuid, co, moduleIndex)] = label;
                    }
                    catch { /* skip this channel's objects */ }
                }
            }

            try { Index(device.ChannelInstances, -1); } catch { }
            try
            {
                int moduleIndex = 0;
                foreach (ModuleInstance mod in device.ModuleInstances)
                {
                    try { Index(mod.ChannelInstances, moduleIndex); } catch { }
                    moduleIndex++;
                }
            }
            catch { }

            return map;
        }

        /// <summary>
        /// Walks the group-object tree from a ComObject up to the root, collecting the
        /// Folder texts, to yield the authoritative ETS UI block path (e.g.
        /// "Operation / Display > Push button functions > PB9/10: Push buttons 9/10").
        /// Defensive: any traversal quirk yields null rather than failing the listing.
        /// </summary>
        private static string? BuildBlockPath(ComObjectInstanceRef co)
        {
            try
            {
                var parts = new List<string>();
                IGroupObjectTreeElement? cur = co.ParentTreeElement;
                int guard = 0;
                while (cur != null && guard++ < 32)
                {
                    if (cur is Folder f)
                    {
                        var txt = f.Text;
                        if (!string.IsNullOrEmpty(txt)) parts.Insert(0, txt.Trim());
                    }
                    cur = cur.ParentTreeElement;
                }
                return parts.Count > 0 ? string.Join(" > ", parts) : null;
            }
            catch { return null; }
        }

        public List<TopologyAreaInfo> ListTopology()
        {
            var areas = new List<TopologyAreaInfo>();
            foreach (Area area in Inst.Areas)
            {
                var areaAddr = area.Address;
                var areaInfo = new TopologyAreaInfo
                {
                    AreaRef = $"a{areaAddr}",
                    Address = areaAddr.ToString(),
                    Name = area.Name ?? "",
                    // Verified: P:Knx.Ets.Sdk.Project.Area.Description (get/set, SDK XML line 8941)
                    Description = area.Description,
                    // Verified: P:Knx.Ets.Sdk.Project.Area.Comment (get/set, SDK XML line 8922)
                    Comment = area.Comment
                };

                foreach (Line line in area.Lines)
                {
                    var lineAddr = line.Address;
                    var lineInfo = new TopologyLineInfo
                    {
                        LineRef = $"a{areaAddr}:l{lineAddr}",
                        Address = line.AddressString ?? $"{areaAddr}.{lineAddr}",
                        Name = line.Name ?? "",
                        // Verified: P:Knx.Ets.Sdk.Project.Line.Description (get/set, SDK XML line 15819)
                        Description = line.Description,
                        // Verified: P:Knx.Ets.Sdk.Project.Line.Comment (get/set, SDK XML line 15804)
                        Comment = line.Comment
                    };

#if !ETS5
                    // ETS5 has no Segment concept (no Line.Segments).
                    foreach (Segment seg in line.Segments)
                    {
                        lineInfo.Segments.Add(new SegmentInfo
                        {
                            SegmentRef = $"a{areaAddr}:l{lineAddr}:s{seg.Number}",
                            Name = seg.Name ?? "",
                            // Verified: P:Knx.Ets.Sdk.Project.Segment.Description (get/set, SDK XML line 17608)
                            Description = seg.Description,
                            // Verified: P:Knx.Ets.Sdk.Project.Segment.Comment (get/set, SDK XML line 17594)
                            Comment = seg.Comment
                        });
                    }
#endif

                    areaInfo.Lines.Add(lineInfo);
                }

                areas.Add(areaInfo);
            }
            return areas;
        }

        /// <summary>
        /// List manufacturers from BOTH the global product store AND the project-local
        /// catalog, de-duplicated by KnxManufacturerId.
        ///
        /// Finding (#4 fix): GlobalProductStoreManufacturers only contains manufacturers
        /// whose products have been DOWNLOADED to the global product store (via ETS online
        /// catalog download or .knxprod import). On a real project with 80 installed devices,
        /// GlobalProductStoreManufacturers can return 0 if no online catalog download was
        /// performed -- the devices were added from a .knxproj import or a previous ETS
        /// session that no longer has cached product data. Root.Manufacturers (project-local)
        /// always has the manufacturers of products actually used in the project.
        ///
        /// Solution: MERGE both sources. Project-local manufacturers (Root.Manufacturers)
        /// are always included. GlobalProductStoreManufacturers are added as a supplement
        /// for manufacturers whose products are installed but not (yet) used in the project.
        /// De-duplication by KnxManufacturerId ensures no duplicates.
        ///
        /// Verified: P:Knx.Ets.Sdk.Root.Manufacturers (SDK XML line 28214)
        ///   "Gets the manufacturer collection of the specified project."
        ///   Element type: Manufacturer (Knx.Ets.Sdk.Product).
        /// Verified: P:Knx.Ets.Sdk.Product.Manufacturer.KnxManufacturerId (SDK XML line 8216)
        /// Verified: P:Knx.Ets.Sdk.Product.Manufacturer.Name (SDK XML line 8203)
        /// Verified: P:Knx.Ets.Sdk.Root.GlobalProductStoreManufacturers (SDK XML line 28242)
        ///   Element type: ExternalManufacturer (Knx.Ets.Sdk.External).
        /// Verified: P:Knx.Ets.Sdk.External.ExternalManufacturer.Name (SDK XML line 20066)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalManufacturer.KnxManufacturerId (SDK XML line 20074)
        /// </summary>
        public List<ManufacturerInfo> ListManufacturers()
        {
            var seen = new HashSet<uint>();
            var result = new List<ManufacturerInfo>();

            // 1. Project-local manufacturers first (always populated for installed devices).
            foreach (Manufacturer mfr in Project.Root.Manufacturers)
            {
                var mfrId = mfr.KnxManufacturerId;
                if (seen.Add(mfrId))
                {
                    result.Add(new ManufacturerInfo
                    {
                        ManufacturerRef = $"m{mfrId}",
                        Name = mfr.Name ?? ""
                    });
                }
            }

            // 2. Global product store manufacturers (supplement: downloaded/imported products
            //    that may not yet be used in the project).
            foreach (ExternalManufacturer emfr in Project.Root.GlobalProductStoreManufacturers)
            {
                var mfrId = emfr.KnxManufacturerId;
                if (seen.Add(mfrId))
                {
                    result.Add(new ManufacturerInfo
                    {
                        ManufacturerRef = $"m{mfrId}",
                        Name = emfr.Name ?? ""
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Search BOTH the project-local catalog AND the global product store.
        /// Project-local items (Root.Manufacturers) are searched first, then the global
        /// product store as a supplement. De-duplicated by CatalogItem/ExternalProduct Id.
        ///
        /// This matches the fix for ListManufacturers (#4): GlobalProductStoreManufacturers
        /// can be empty on a real project if no online catalog download was performed.
        /// Root.Manufacturers always has the installed products.
        ///
        /// Verified: P:Knx.Ets.Sdk.Root.Manufacturers (SDK XML line 28214)
        /// Verified: P:Knx.Ets.Sdk.Product.Manufacturer.AllCatalogItems (SDK XML line 8209)
        /// Verified: P:Knx.Ets.Sdk.DomObject.Id (inherited by CatalogItem)
        /// Verified: P:Knx.Ets.Sdk.Root.GlobalProductStoreManufacturers (SDK XML line 28242)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalManufacturer.HardwareCollection (SDK XML line 20082)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalHardware.Products (SDK XML line 20181)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalProduct.OrderNumber (SDK XML line 20259)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalProduct.Text (SDK XML line 20264)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalProduct.Id (SDK XML line 20250)
        /// </summary>
        public List<CatalogItemInfo> SearchCatalog(string query, string? manufacturerRef)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("query is required for catalog.search.");

            uint? filterMfrId = null;
            if (!string.IsNullOrEmpty(manufacturerRef))
            {
                if (!manufacturerRef!.StartsWith("m", StringComparison.Ordinal) ||
                    !uint.TryParse(manufacturerRef.Substring(1), out var mId))
                    throw new ArgumentException($"Invalid manufacturer ref: {manufacturerRef}");
                filterMfrId = mId;
            }

            var queryLower = query.ToLowerInvariant();
            var result = new List<CatalogItemInfo>();
            // #14: Dedup by (KnxManufacturerId + normalized OrderNumber) instead of
            // mixing CatalogItem.Id and ExternalProduct.Id across different ID spaces.
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const int maxResults = 200;

            // 1. Search project-local catalog first (always has installed products).
            foreach (Manufacturer mfr in Project.Root.Manufacturers)
            {
                if (filterMfrId.HasValue && mfr.KnxManufacturerId != filterMfrId.Value)
                    continue;

                foreach (CatalogItem ci in mfr.AllCatalogItems)
                {
                    var itemName = ci.Name ?? "";
                    var orderNumber = ci.Product?.OrderNumber ?? "";

                    if (itemName.ToLowerInvariant().Contains(queryLower) ||
                        orderNumber.ToLowerInvariant().Contains(queryLower))
                    {
                        var dedupKey = $"{mfr.KnxManufacturerId}|{orderNumber.Trim().ToUpperInvariant()}";
                        if (seenKeys.Add(dedupKey))
                        {
                            result.Add(new CatalogItemInfo
                            {
                                CatalogItemRef = $"ci:{ci.Id}",
                                Manufacturer = mfr.Name ?? "",
                                Name = itemName,
                                OrderNumber = orderNumber,
                                // Verified: P:Knx.Ets.Sdk.Product.CatalogItem.VisibleDescription (get-only, SDK XML line 7937)
                                Description = ci.VisibleDescription
                            });

                            if (result.Count >= maxResults)
                                return result;
                        }
                    }
                }
            }

            // 2. Search global product store as supplement.
            // Note: ExternalProduct does not have VisibleDescription; Description left null.
            foreach (ExternalManufacturer emfr in Project.Root.GlobalProductStoreManufacturers)
            {
                if (filterMfrId.HasValue && emfr.KnxManufacturerId != filterMfrId.Value)
                    continue;

                foreach (ExternalHardware hw in emfr.HardwareCollection)
                {
                    foreach (ExternalProduct ep in hw.Products)
                    {
                        var itemName = ep.Text ?? "";
                        var orderNumber = ep.OrderNumber ?? "";

                        if (itemName.ToLowerInvariant().Contains(queryLower) ||
                            orderNumber.ToLowerInvariant().Contains(queryLower))
                        {
                            var dedupKey = $"{emfr.KnxManufacturerId}|{orderNumber.Trim().ToUpperInvariant()}";
                            if (seenKeys.Add(dedupKey))
                            {
                                result.Add(new CatalogItemInfo
                                {
                                    CatalogItemRef = $"ci:{ep.Id}",
                                    Manufacturer = emfr.Name ?? "",
                                    Name = itemName,
                                    OrderNumber = orderNumber
                                    // Description: ExternalProduct has no VisibleDescription -> null
                                });

                                if (result.Count >= maxResults)
                                    return result;
                            }
                        }
                    }
                }
            }
            return result;
        }

        public List<ParameterInfo> ListParameters(string deviceRef)
        {
            var device = FindDevice(deviceRef);
            var result = new List<ParameterInfo>();

            // Build the RefId -> UI block-path map once (from the app-program dynamic tree,
            // cached per application program). This is what makes a parameter's UI block
            // ("... > PB9/10: Push buttons 9/10") known, so identically-named parameters can
            // be told apart. Defensive: on any failure the map is empty and Block stays null.
            var ap = device.ParameterInstanceRefs.Cast<ParameterInstanceRef>()
                         .FirstOrDefault()?.ParameterRef?.Parent;
            var blockMap = ap != null ? GetParamBlockMap(ap) : new Dictionary<string, string>();

            void AddParams(IEnumerable<ParameterInstanceRef> parms)
            {
                foreach (var p in parms)
                {
                    var info = new ParameterInfo
                    {
                        // #6: Prefix with "p:" so it round-trips into param.set/FindParameter.
                        ParameterRef = $"p:{p.UniqueId ?? ""}",
                        Name = p.Name ?? "",
                        Value = p.Value?.ToString() ?? "",
                        IsDefault = p.IsDefault,
                        IsActive = p.IsActive
                    };
                    // Enrich with product-data semantics (label, unit, access, options,
                    // min/max) so the LLM can reason about a parameter instead of guessing.
                    // Fully defensive: any SDK quirk on a single field must not drop the
                    // whole parameter (some products/versions leave these unset).
                    EnrichParameter(info, p);
                    // Authoritative UI block path (disambiguates repeated parameter names).
                    var refId = ExtractRefId(p.UniqueId);
                    if (refId != null && blockMap.TryGetValue(refId, out var bp) && !string.IsNullOrEmpty(bp))
                        info.Block = bp;
                    result.Add(info);
                }
            }

            AddParams(device.ParameterInstanceRefs.Cast<ParameterInstanceRef>());
            foreach (ModuleInstance mod in device.ModuleInstances)
            {
                AddParams(mod.ParameterInstanceRefs.Cast<ParameterInstanceRef>());
            }

            return result;
        }

        // Cache of RefId -> UI-block-path per application program (keyed by fingerprint).
        // The dynamic XML can be ~18 MB; parsing it once per app and reusing keeps
        // params.list responsive.
        private static readonly Dictionary<string, Dictionary<string, string>> _blockMapCache
            = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>
        /// Returns RefId -> UI block path (e.g. "Operation / Display > Push button functions
        /// > PB9/10: Push buttons 9/10") by parsing the application program's dynamic tree.
        /// Cached per app program. Read-only, fully defensive (empty map on any failure).
        /// </summary>
        private Dictionary<string, string> GetParamBlockMap(ApplicationProgram ap)
        {
            string key;
            try { key = ap.Fingerprint ?? ap.Hash ?? ("app-" + ap.ApplicationNumber); }
            catch { key = "app"; }

            lock (_blockMapCache)
                if (_blockMapCache.TryGetValue(key, out var cached)) return cached;

            var map = new Dictionary<string, string>();
            try
            {
                object? dyn = ap.Dynamic;
                var root = dyn as System.Xml.XmlNode;
                if (root is System.Xml.XmlDocument doc) root = doc.DocumentElement;
                if (root != null) WalkDynamic(root, new List<string>(), map);
            }
            catch { /* leave map empty */ }

            lock (_blockMapCache) _blockMapCache[key] = map;
            return map;
        }

        /// <summary>Recursively walks the dynamic tree, mapping each ParameterRefRef@RefId to
        /// the path of enclosing ParameterBlock/Channel labels. choose/when/ParameterSeparator
        /// are transparent (they do not add a UI-tree level).</summary>
        private static void WalkDynamic(System.Xml.XmlNode node, List<string> stack,
                                        Dictionary<string, string> map)
        {
            foreach (System.Xml.XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != System.Xml.XmlNodeType.Element) continue;
                var name = child.LocalName;
                if (name == "ParameterRefRef")
                {
                    var refId = child.Attributes?["RefId"]?.Value;
                    if (!string.IsNullOrEmpty(refId) && !map.ContainsKey(refId))
                        map[refId] = string.Join(" > ", stack);
                }
                else if (name == "ParameterBlock" || name == "Channel")
                {
                    var label = CleanBlockLabel(child.Attributes?["Text"]?.Value)
                                ?? child.Attributes?["Name"]?.Value;
                    bool pushed = !string.IsNullOrEmpty(label);
                    if (pushed) stack.Add(label!);
                    WalkDynamic(child, stack, map);
                    if (pushed) stack.RemoveAt(stack.Count - 1);
                }
                else
                {
                    WalkDynamic(child, stack, map);
                }
            }
        }

        /// <summary>Cleans a block label: strips the "{{n:...}}" text-parameter placeholders
        /// and trims (e.g. "    PB9/10: {{0:Push buttons 9/10}}" -> "PB9/10: Push buttons 9/10").</summary>
        private static string? CleanBlockLabel(string? t)
        {
            if (string.IsNullOrEmpty(t)) return null;
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\{\{\d+:", "");
            t = t.Replace("}}", "").Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        /// <summary>Extracts the dynamic-tree RefId (e.g. "M-0083_A-008A-25-83B3_P-365_R-923")
        /// from a parameter UniqueId (e.g. "P-02D7-0_DI-90_M-0083_A-008A-25-83B3_P-365_R-923").</summary>
        private static string? ExtractRefId(string? uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return null;
            var i = uniqueId.IndexOf("_M-", System.StringComparison.Ordinal);
            return i >= 0 ? uniqueId.Substring(i + 1) : null;
        }

        /// <summary>
        /// Adds product-data semantics to a ParameterInfo: label (Text), unit (SuffixText),
        /// access level, enum options (value+label) or numeric min/max. This is what lets an
        /// LLM reason about a parameter ("Nachlaufzeit", choices "10s/30s/1min", 0..255)
        /// rather than seeing a bare value. Best-effort: every field is guarded so an SDK
        /// quirk (e.g. an empty DatapointType, a version without a given property, or a
        /// product that leaves a field unset) degrades to omitting that field, never to
        /// dropping the parameter. The condition/controlling-parameter graph is not exposed
        /// by the SDK; callers rely on IsActive (post-visibility) plus re-reading after a set.
        /// </summary>
        private static void EnrichParameter(ParameterInfo info, ParameterInstanceRef p)
        {
            try
            {
                var pref = p.ParameterRef;
                if (pref == null) return;

                try { var t = pref.Text; if (!string.IsNullOrEmpty(t)) info.Text = t; } catch { }
                try { var s = pref.SuffixText; if (!string.IsNullOrEmpty(s)) info.Unit = s; } catch { }
                try { var a = pref.Access.ToString(); if (!string.IsNullOrEmpty(a)) info.Access = a; } catch { }

                var pt = pref.Parameter?.ParameterType;
                if (pt == null) return;

                if (pt is ParameterTypeRestriction restr)
                {
                    try
                    {
                        var opts = new List<ParameterOption>();
                        foreach (TypeRestrictionEnumeration e in restr.Enumerations)
                        {
                            opts.Add(new ParameterOption
                            {
                                Value = e.Value.ToString(),
                                Text = e.Text ?? ""
                            });
                        }
                        if (opts.Count > 0) info.Options = opts;
                    }
                    catch { }
                }
                else if (pt is ParameterTypeNumber num)
                {
                    try { info.Min = num.MinInclusive.ToString(); info.Max = num.MaxInclusive.ToString(); } catch { }
                }
                else if (pt is ParameterTypeFloat fl)
                {
                    try { info.Min = fl.MinInclusive.ToString(); info.Max = fl.MaxInclusive.ToString(); } catch { }
                }
            }
            catch { /* semantics are best-effort; never fail the listing */ }
        }

        // ---------------------------------------------------------------
        // Mutating methods -- all wrapped in UndoManager markers
        // ---------------------------------------------------------------

        /// <summary>
        /// #3: Parse address string per project.GroupAddressStyle.
        /// Accepts: "main/middle/sub" (3-level), "main/sub" (2-level), or raw integer.
        /// Returns the created GA with the canonical address string in the result.
        /// </summary>
        public GroupAddressInfo CreateGroupAddress(string name, string address, uint? dptMain, uint? dptSub)
        {
            var rawAddress = ParseGroupAddress(address, Project.GroupAddressStyle);

            return RunInMarker("Bridge: Create GroupAddress", () =>
            {
                GroupAddress? ga = null;

                if (Project.GroupAddressStyle == GroupAddressStyle.ThreeLevel)
                {
                    var mainAddr = (ushort)((rawAddress >> 11) & 0x1F);
                    var middleAddr = (ushort)((rawAddress >> 8) & 0x07);
                    var subAddr = (ushort)(rawAddress & 0xFF);

                    var mainGroup = Inst.GroupRanges.Cast<GroupRange>()
                        .FirstOrDefault(r => r.Address == mainAddr);
                    if (mainGroup == null)
                    {
                        mainGroup = Inst.GroupRanges
                            .Add("", 1, AddressAllocations.StartWith, mainAddr)
                            .Cast<GroupRange>().First();
                    }

                    var middleGroup = mainGroup.GroupRanges.Cast<GroupRange>()
                        .FirstOrDefault(r => r.Address == middleAddr);
                    if (middleGroup == null)
                    {
                        middleGroup = mainGroup.GroupRanges
                            .Add("", 1, AddressAllocations.StartWith, middleAddr)
                            .Cast<GroupRange>().First();
                    }

                    ga = middleGroup.GroupAddresses
                        .Add(name, 1, AddressAllocations.StartWith, subAddr)
                        .Cast<GroupAddress>().First();
                }
                else if (Project.GroupAddressStyle == GroupAddressStyle.TwoLevel)
                {
                    var mainAddr = (ushort)((rawAddress >> 11) & 0x1F);
                    var subAddr = (ushort)(rawAddress & 0x7FF);

                    var mainGroup = Inst.GroupRanges.Cast<GroupRange>()
                        .FirstOrDefault(r => r.Address == mainAddr);
                    if (mainGroup == null)
                    {
                        mainGroup = Inst.GroupRanges
                            .Add("", 1, AddressAllocations.StartWith, mainAddr)
                            .Cast<GroupRange>().First();
                    }

                    ga = mainGroup.GroupAddresses
                        .Add(name, 1, AddressAllocations.StartWith, subAddr)
                        .Cast<GroupAddress>().First();
                }
                else // Free
                {
                    var range = Inst.GroupRanges.Cast<GroupRange>().FirstOrDefault();
                    if (range == null)
                    {
                        range = Inst.GroupRanges
                            .Add("Default", 1)
                            .Cast<GroupRange>().First();
                    }
                    ga = range.GroupAddresses
                        .Add(name, 1, AddressAllocations.StartWith, rawAddress)
                        .Cast<GroupAddress>().First();
                }

                // Optionally set DPT if specified.
                if (dptMain.HasValue && ga != null)
                {
                    // #9: FindDatapointSubtype no longer silently falls back.
                    var dptObj = FindDatapointSubtype(dptMain.Value, dptSub);
                    if (dptObj != null)
                    {
                        ga.DatapointTypeObject = dptObj;
                    }
                }

                var info = BuildGaInfo(ga!);
                info.Truncated = info.Name != name; // ETS shortened the name to its limit
                return info;
            });
        }

        /// <summary>
        /// #3: Parse a group address string per the project's GroupAddressStyle.
        /// Accepts "main/middle/sub" (3-level), "main/sub" (2-level), or a raw integer.
        /// Returns the raw 16-bit address value.
        /// </summary>
        private static ushort ParseGroupAddress(string address, GroupAddressStyle style)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("address is required for ga.create.");

            // Try raw integer first.
            if (ushort.TryParse(address, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawInt))
                return rawInt;

            var parts = address.Split('/');

            if (parts.Length == 3 && style == GroupAddressStyle.ThreeLevel)
            {
                // 3-level: main/middle/sub -> bits 15..11 / 10..8 / 7..0
                if (!ushort.TryParse(parts[0], out var main) || main > 31)
                    throw new ArgumentException($"Invalid 3-level main group: {parts[0]}");
                if (!ushort.TryParse(parts[1], out var middle) || middle > 7)
                    throw new ArgumentException($"Invalid 3-level middle group: {parts[1]}");
                if (!ushort.TryParse(parts[2], out var sub) || sub > 255)
                    throw new ArgumentException($"Invalid 3-level sub address: {parts[2]}");

                return (ushort)((main << 11) | (middle << 8) | sub);
            }

            if (parts.Length == 2 && style == GroupAddressStyle.TwoLevel)
            {
                // 2-level: main/sub -> bits 15..11 / 10..0
                if (!ushort.TryParse(parts[0], out var main) || main > 31)
                    throw new ArgumentException($"Invalid 2-level main group: {parts[0]}");
                if (!ushort.TryParse(parts[1], out var sub) || sub > 2047)
                    throw new ArgumentException($"Invalid 2-level sub address: {parts[1]}");

                return (ushort)((main << 11) | sub);
            }

            // Also accept 3-level strings when project is 2-level, or vice versa,
            // as a best-effort convenience (protocol.md says "parst gemaess project style").
            if (parts.Length == 3)
            {
                if (!ushort.TryParse(parts[0], out var main) || main > 31)
                    throw new ArgumentException($"Invalid main group: {parts[0]}");
                if (!ushort.TryParse(parts[1], out var middle) || middle > 7)
                    throw new ArgumentException($"Invalid middle group: {parts[1]}");
                if (!ushort.TryParse(parts[2], out var sub) || sub > 255)
                    throw new ArgumentException($"Invalid sub address: {parts[2]}");
                return (ushort)((main << 11) | (middle << 8) | sub);
            }

            if (parts.Length == 2)
            {
                if (!ushort.TryParse(parts[0], out var main) || main > 31)
                    throw new ArgumentException($"Invalid main group: {parts[0]}");
                if (!ushort.TryParse(parts[1], out var sub) || sub > 2047)
                    throw new ArgumentException($"Invalid sub address: {parts[1]}");
                return (ushort)((main << 11) | sub);
            }

            throw new ArgumentException(
                $"Cannot parse address '{address}' for style {style}. "
              + "Expected 'main/middle/sub' (3-level), 'main/sub' (2-level), or a raw integer.");
        }

        public LinkResult CreateLink(string comObjectRef, string gaRef)
        {
            var (_, co) = FindComObject(comObjectRef);
            if (!co.IsActive)
                throw new InactiveObjectException(comObjectRef);

            var ga = FindGroupAddress(gaRef);

            RunInMarker("Bridge: Link ComObject", () =>
            {
                try
                {
                    co.Link(ga);
                }
                catch (KeyNotFoundException ex)
                {
                    // The SDK can throw KeyNotFoundException ("The given key was not present
                    // in the dictionary.") from deep inside its own link operation -- observed
                    // when adding a second group address to a module com-object. That is an
                    // internal SDK failure, not an unknown ref; rethrow as a non-not_found
                    // error so it is not mistaken for a bad comObjectRef/gaRef. The marker is
                    // discarded, so the project is left unchanged.
                    throw new InvalidOperationException(
                        $"SDK link operation failed for {comObjectRef} -> {gaRef} "
                      + "(the SDK reported a missing dictionary key). The link was not applied.",
                        ex);
                }
            });

            // The link always proceeds; attach a soft DPT-compatibility advisory so a wrong
            // wiring (e.g. a % object linked to a switch GA) is caught. Fully defensive:
            // DPT inspection must never fail an otherwise-successful link.
            var result = new LinkResult { ComObjectRef = comObjectRef, GaRef = gaRef };
            try
            {
                result.Dpt = FormatDpt(co.DatapointTypes?.Cast<object>().FirstOrDefault());
                result.GaDpt = FormatDpt(ga.DatapointTypeObject);
                result.DptWarning = DptMismatchWarning(result.Dpt, result.GaDpt);
            }
            catch { /* advisory only */ }
            return result;
        }

        /// <summary>
        /// Returns a human-readable warning when a com-object DPT and a group-address DPT
        /// have different MAIN numbers (e.g. 1.x switch vs 5.x percent), else null. An empty
        /// DPT on either side yields no warning: a GA often has no DPT set, and some ETS
        /// versions return an empty com-object DPT -- neither is a mismatch we can assert.
        /// </summary>
        private static string? DptMismatchWarning(string? coDpt, string? gaDpt)
        {
            if (string.IsNullOrEmpty(coDpt) || string.IsNullOrEmpty(gaDpt))
                return null;
            string MainOf(string s)
            {
                var i = s.IndexOf('.');
                return i > 0 ? s.Substring(0, i) : s;
            }
            if (!string.Equals(MainOf(coDpt!), MainOf(gaDpt!), System.StringComparison.OrdinalIgnoreCase))
                return $"DPT mismatch: com-object is {coDpt} but group address is {gaDpt}. " +
                       "They will not exchange data correctly -- align the DPTs.";
            return null;
        }

        /// <summary>Returns the device's application-program dynamic structure as XML
        /// (ParameterBlock/Channel/ParameterRefRef tree). Authoritative source for
        /// parameter -> UI-block/channel mapping. Read-only.</summary>
        public string GetApplicationDynamic(string deviceRef)
        {
            var device = FindDevice(deviceRef);
            var pir = device.ParameterInstanceRefs.Cast<ParameterInstanceRef>().FirstOrDefault();
            var ap = pir?.ParameterRef?.Parent;
            if (ap == null)
                throw new KeyNotFoundException($"No application program found for device {deviceRef}.");
            // NOTE: DynamicAsString returns the XmlDocument's type name ("System.Xml.XmlDocument"),
            // not the XML -- a .NET gotcha (XmlDocument.ToString()). Use Dynamic (the XML node)
            // and serialize its OuterXml.
            object? dyn = ap.Dynamic;
            if (dyn == null) return "";
            if (dyn is System.Xml.XmlNode node) return node.OuterXml;
            var outerXmlProp = dyn.GetType().GetProperty("OuterXml");
            if (outerXmlProp != null)
                return outerXmlProp.GetValue(dyn)?.ToString() ?? "";
            return dyn.ToString() ?? "";
        }

        /// <summary>Read-only pre-check for a link (batch validate_only). Never mutates.</summary>
        public List<string> ValidateLink(string comObjectRef, string gaRef)
        {
            var issues = new List<string>();
            ComObjectInstanceRef? co = null;
            try { (_, co) = FindComObject(comObjectRef); }
            catch { issues.Add($"comObject {comObjectRef} not found"); }
            if (co != null && !co.IsActive)
                issues.Add($"comObject {comObjectRef} is inactive and cannot be linked");

            GroupAddress? ga = null;
            try { ga = FindGroupAddress(gaRef); }
            catch { issues.Add($"groupAddress {gaRef} not found"); }

            if (co != null && ga != null)
            {
                try
                {
                    var coDpt = FormatDpt(co.DatapointTypes?.Cast<object>().FirstOrDefault());
                    var gaDpt = FormatDpt(ga.DatapointTypeObject);
                    var w = DptMismatchWarning(coDpt, gaDpt);
                    if (w != null) issues.Add(w);
                }
                catch { /* DPT advisory only */ }
            }
            return issues;
        }

        /// <summary>Read-only GA address-collision pre-check (batch validate_only).
        /// Conservative: any parse/compare failure returns false (no false positives).</summary>
        public bool GroupAddressAddressInUse(string address)
        {
            try
            {
                var raw = ParseGroupAddress(address, Project.GroupAddressStyle);
                foreach (GroupAddress g in Inst.AllGroupAddresses)
                {
                    try { if (System.Convert.ToUInt32(g.Address) == raw) return true; }
                    catch { /* skip this GA */ }
                }
            }
            catch { /* cannot parse/compare -> do not assert a collision */ }
            return false;
        }

        /// <summary>
        /// #10: link.delete is idempotent per protocol.md.
        /// If the link does not exist, returns silently.
        /// Only throws not_found if comObjectRef or gaRef themselves are unknown.
        /// </summary>
        public void DeleteLink(string comObjectRef, string gaRef)
        {
            var (_, co) = FindComObject(comObjectRef);
            var ga = FindGroupAddress(gaRef);

            // Find the existing connector for this GA. Idempotent: an absent link is a no-op.
            var connector = co.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.GroupAddress != null && c.GroupAddress.Id == ga.Id);
            if (connector == null)
            {
                // Idempotent: link already absent -> ok, no error.
                return;
            }

            // Unlink via the connector's own GroupAddress instance (the exact wrapper the SDK
            // compares against), then confirm the link is actually gone before committing.
            // The SDK unlink can return without removing the connector; reading the live
            // connector collection back and failing surfaces that instead of a false success.
            // Removed IsActive guard: delete is idempotent and refusing to unlink an inactive
            // object's existing link is surprising.
            var linkedGa = connector.GroupAddress;
            RunInMarker("Bridge: Unlink ComObject", () =>
            {
                co.Unlink(linkedGa);
                if (co.Connectors.Cast<Connector>()
                        .Any(c => c.GroupAddress != null && c.GroupAddress.Id == ga.Id))
                    throw new InvalidOperationException(
                        $"Unlink of {comObjectRef} -> {gaRef} did not remove the link "
                      + "(the SDK reported success but the connector is still present).");
            });
        }

        public void SetParameter(string deviceRef, string parameterRef, string value)
        {
            var param = FindParameter(deviceRef, parameterRef);

            RunInMarker("Bridge: Set Parameter", () => { param.Value = value; });
        }

        /// <summary>
        /// Add a device from the catalog to a topology line.
        /// FindCatalogItem now searches GlobalProductStoreManufacturers as a fallback.
        /// If the product is in the global store but not yet in the project,
        /// FindCatalogItem auto-internalizes it (via Root.InternalizeProductData(hardwareId))
        /// before returning the project-local CatalogItem.
        /// Verified: M:Knx.Ets.Sdk.Project.DeviceCollection.Add(
        ///   Knx.Ets.Sdk.Project.Line, Knx.Ets.Sdk.Product.CatalogItem,
        ///   System.UInt16, Knx.Ets.Common.Types.Strategies.AddressAllocations,
        ///   System.UInt16)  (SDK XML line 13699)
        ///   count=1, AddressAllocations.StartWith, startAddressOrOffset=deviceAddr
        ///   Returns: "a collection of the added devices"
        /// Verified: P:Knx.Ets.Sdk.Project.Installation.AllDevices (SDK XML line 15434)
        /// Verified: F:Knx.Ets.Common.Types.Strategies.AddressAllocations.StartWith (Common.Types XML line 7172)
        /// </summary>
        public AddDeviceResult AddDeviceFromCatalog(string lineRef, string catalogItemRef, string address)
        {
            var line = FindLine(lineRef);
            var catalogItem = FindCatalogItem(catalogItemRef);
            var deviceAddr = ParseIndividualAddress(address, line);

            return RunInMarker("Bridge: Add device", () =>
            {
                var added = Inst.AllDevices.Add(line, catalogItem, 1,
                    AddressAllocations.StartWith, deviceAddr);
                var device = added.Cast<Device>().First();

                return new AddDeviceResult
                {
                    // Verified: P:Knx.Ets.Sdk.ProjectRelatedObjectWithPuid.Puid (SDK XML line 27077)
                    Ref = $"d{device.Puid}",
                    // Verified: P:Knx.Ets.Sdk.Project.Device.IndividualAddressString (SDK XML line 12791)
                    Address = device.IndividualAddressString ?? address
                };
            });
        }

        /// <summary>
        /// Start a device download via the ETS download engine.
        ///
        /// #7: Parse options and do all can-fail setup BEFORE acquiring the bus.
        /// #8: Register the job BEFORE calling StartDownload; remove on startup failure.
        /// #3: Bus lease is owner-aware (jobId); released only by matching terminal event.
        /// </summary>
        /// <param name="options">Download type string: "partial" (default -- changes only,
        /// no individual address), "all" (full incl. individual address), "application",
        /// "network", "networkBySerial". Maps to LoadDeviceOptions.</param>
        public string StartDeviceProgram(string deviceRef, string? options)
        {
            var device = FindDevice(deviceRef);

            // #7: Parse/validate options BEFORE acquiring the bus (can throw).
            // Verified: T:Knx.Ets.Sdk.PlugIn.LoadDeviceOptions (SDK XML line 26578)
            var loadOpts = ParseLoadDeviceOptions(options);

            // IsConnectionAvailable only does a general check; actual download
            // may still fail if no KNXnet/IP interface is configured or reachable.
            // Verified: M:Knx.Ets.Sdk.Root.IsConnectionAvailable(Device) -> bool (SDK XML line 28900)
            if (!Project.Root.IsConnectionAvailable(device))
                throw new BusUnavailableException(
                    $"No bus connection available for device {deviceRef}. "
                  + "Ensure a bus interface is selected in ETS.");

            var jobId = Guid.NewGuid().ToString("D");
            AcquireBus(jobId);

            // #8: Register job BEFORE starting the SDK operation.
            var entry = new JobEntry(jobId, deviceRef, device, JobOpType.Download, jobId);
            _jobs[jobId] = entry;

            try
            {
                EnsureEventSubscription();

                // Verified: M:Knx.Ets.Sdk.Root.StartDownload(Device, LoadDeviceOptions) -> bool
                //   (SDK XML line 28671)
                // StartDownload returns synchronously (bool = "can download start?"),
                // the actual download runs asynchronously. Progress/completion arrive via
                // Project.OnOnlineOperationsEvent.
                bool started = Project.Root.StartDownload(device, loadOpts);

                if (!started)
                {
                    throw new BusUnavailableException(
                        $"StartDownload returned false for device {deviceRef}. "
                      + "The bus connection may be unavailable or the device configuration may be incomplete.");
                }
            }
            catch
            {
                // #7/#8: Startup failure: remove job and release bus via try/finally.
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                throw;
            }

            PruneTerminalJobs();
            return jobId;
        }

        /// <summary>Map a user-facing options string to LoadDeviceOptions.</summary>
        private static LoadDeviceOptions ParseLoadDeviceOptions(string? options)
        {
            // Default: Partial -- writes only the changes (parameters / GA tables) to an
            // already-programmed device, WITHOUT the individual-address step (no physical
            // programming-button press). This is the safe everyday "apply my edits"
            // download. Pass "all" explicitly for a full download incl. individual address.
            if (string.IsNullOrEmpty(options))
                return LoadDeviceOptions.Partial;

            switch (options)
            {
                case "all": return LoadDeviceOptions.All;
                case "partial": return LoadDeviceOptions.Partial;
                case "application": return LoadDeviceOptions.LoadApplicationProgram;
                case "network": return LoadDeviceOptions.LoadNetworkConfiguration;
                case "networkBySerial":
                    return LoadDeviceOptions.LoadNetworkConfiguration
                         | LoadDeviceOptions.ProgramNetworkConfigurationBySerialNumber;
                default:
                    throw new ArgumentException(
                        $"Unknown download option: '{options}'. "
                      + "Valid: all, partial, application, network, networkBySerial.");
            }
        }

        /// <summary>
        /// Start an asynchronous firmware update via the ETS engine.
        /// Verified: M:Knx.Ets.Sdk.Root.StartFirmwareUpdate(Knx.Ets.Sdk.Project.Device,System.String)
        ///   -> bool (SDK XML line 28707)
        ///   "Starts a firmware update for the device specified. This is currently implemented
        ///    only for IoT devices."
        ///   Throws NotSupportedException if device is not an IoT device (SDK XML line 28722).
        ///   Progress via E:Project.OnOnlineOperationsEvent (SDK XML line 28718).
        ///
        /// #3: AcquireBus with owner BEFORE StartFirmwareUpdate (was missing).
        /// #7: Validate args before acquiring the bus.
        /// #8: Register job before starting SDK op; remove on startup failure.
        /// </summary>
#if ETS5
        public string UpdateFirmware(string deviceRef, string firmware)
        {
            throw new NotSupportedOperationException(
                "firmware.update is not supported on ETS5 (Root.StartFirmwareUpdate does not exist in ETS5 SDK).");
        }
#else
        public string UpdateFirmware(string deviceRef, string firmware)
        {
            var device = FindDevice(deviceRef);

            // #7: Validate args BEFORE acquiring the bus (can throw).
            if (string.IsNullOrWhiteSpace(firmware))
                throw new ArgumentException("firmware (update package path) is required.");

            // Verified: M:Knx.Ets.Sdk.Root.IsConnectionAvailable(Device) -> bool (SDK XML line 28900)
            if (!Project.Root.IsConnectionAvailable(device))
                throw new BusUnavailableException(
                    $"No bus connection available for device {deviceRef}. "
                  + "Ensure a bus interface is selected in ETS.");

            var jobId = Guid.NewGuid().ToString("D");
            // #3: AcquireBus BEFORE StartFirmwareUpdate (was missing in old code).
            AcquireBus(jobId);

            // #8: Register job BEFORE starting the SDK operation.
            var entry = new JobEntry(jobId, deviceRef, device, JobOpType.FirmwareUpdate, jobId);
            _jobs[jobId] = entry;

            try
            {
                // Subscribe to online operation events (once) for progress tracking.
                EnsureEventSubscription();

                bool started;
                try
                {
                    started = Project.Root.StartFirmwareUpdate(device, firmware);
                }
                catch (NotSupportedException)
                {
                    // SDK XML line 28722: "If device is not an IoT device."
                    throw new ArgumentException(
                        $"Firmware update is only supported for IoT devices in ETS 6.3.0. "
                      + $"Device {deviceRef} is not an IoT device.");
                }

                if (!started)
                    throw new BusUnavailableException(
                        $"StartFirmwareUpdate returned false for device {deviceRef}. "
                      + "The bus connection may be unavailable or the update package may be invalid.");
            }
            catch
            {
                // #7/#8: Startup failure: remove job and release bus.
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                throw;
            }

            PruneTerminalJobs();
            return jobId;
        }
#endif

        // ---------------------------------------------------------------
        // Online catalog methods (no UndoManager -- product store, not project)
        // ---------------------------------------------------------------

        /// <summary>
        /// Search the unified catalog (online + local stores) by query string.
        /// Verified: P:Knx.Ets.Sdk.Root.UnifiedManufacturers (SDK XML line 28229)
        ///   "The unified manufacturers contain all catalog data from the different stores
        ///    (e.g. online catalog, cached local product store and the project store)."
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedManufacturer.AllCatalogItems (SDK XML line 29701)
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedManufacturer.KnxManufacturerId (SDK XML line 29725)
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedManufacturer.Name (SDK XML line 29717)
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem.Name (SDK XML line 29529)
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem.OrderNumber (SDK XML line 29548)
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem.Id (SDK XML line 29513)
        ///   Note: Id is NOT guaranteed stable across sessions (SDK remark).
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem.ParentManufacturer (SDK XML line 29643)
        /// </summary>
        public List<UnifiedCatalogItemInfo> SearchOnlineCatalog(string query, string? manufacturerRef)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("query is required for catalog.search_online.");

            uint? filterMfrId = null;
            if (!string.IsNullOrEmpty(manufacturerRef))
            {
                if (!manufacturerRef!.StartsWith("m", StringComparison.Ordinal) ||
                    !uint.TryParse(manufacturerRef.Substring(1), out var mId))
                    throw new ArgumentException($"Invalid manufacturer ref: {manufacturerRef}");
                filterMfrId = mId;
            }

            var queryLower = query.ToLowerInvariant();
            var result = new List<UnifiedCatalogItemInfo>();
            const int maxResults = 200;

            foreach (UnifiedManufacturer mfr in Project.Root.UnifiedManufacturers)
            {
                if (filterMfrId.HasValue && mfr.KnxManufacturerId != filterMfrId.Value)
                    continue;

                foreach (UnifiedCatalogItem item in mfr.AllCatalogItems)
                {
                    var itemName = item.Name ?? "";
                    var orderNumber = item.OrderNumber ?? "";

                    if (itemName.ToLowerInvariant().Contains(queryLower) ||
                        orderNumber.ToLowerInvariant().Contains(queryLower))
                    {
                        result.Add(new UnifiedCatalogItemInfo
                        {
                            UnifiedCatalogItemRef = $"uci:{item.Id}",
                            Manufacturer = mfr.Name ?? "",
                            Name = itemName,
                            OrderNumber = orderNumber
                        });

                        if (result.Count >= maxResults)
                            return result;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Import a .knxprod file into the local product store.
        /// Verified: M:Knx.Ets.Sdk.Root.ImportProductData(System.String) -> bool (SDK XML line 28884)
        ///   "Imports a product data file" (knxprod or vd file).
        ///   Returns true if import was performed, false if it could not be performed.
        ///   "This method may show an UI requiring the user to select products and languages,
        ///    so it is not suitable for unintended use and must be called on the UI thread."
        /// No UndoManager: changes the product store, not the project.
        /// The SDK note about UI prompts means ImportProductData may show a selection dialog.
        /// Diff over GlobalProductStoreManufacturers: imported products land in the
        /// global product store, not the project subset. Element type is ExternalManufacturer.
        /// Verified: P:Knx.Ets.Sdk.Root.GlobalProductStoreManufacturers (SDK XML line 28242)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalManufacturer.HardwareCollection (SDK XML line 20082)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalHardware.Products (SDK XML line 20181)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalProduct.Id (SDK XML line 20250)
        /// </summary>
#if ETS5
        public CatalogImportResult ImportProductData(string path)
        {
            throw new NotSupportedOperationException(
                "catalog.import is not supported on ETS5 (Root.ImportProductData does not exist in ETS5 SDK).");
        }
#else
        public CatalogImportResult ImportProductData(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path is required for catalog.import.");

            if (!System.IO.File.Exists(path))
                throw new KeyNotFoundException($"File not found: {path}");

            // Snapshot global product store ExternalProduct IDs before import.
            var beforeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ExternalManufacturer emfr in Project.Root.GlobalProductStoreManufacturers)
            {
                foreach (ExternalHardware hw in emfr.HardwareCollection)
                {
                    foreach (ExternalProduct ep in hw.Products)
                    {
                        beforeIds.Add(ep.Id);
                    }
                }
            }

            bool imported = Project.Root.ImportProductData(path);
            if (!imported)
                throw new ArgumentException(
                    $"ImportProductData returned false for '{path}'. "
                  + "The file may be invalid, unsigned, or the import was canceled.");

            BumpRevision();

            // Best-effort: diff the global product store to find newly present items.
            var result = new CatalogImportResult();
            foreach (ExternalManufacturer emfr in Project.Root.GlobalProductStoreManufacturers)
            {
                foreach (ExternalHardware hw in emfr.HardwareCollection)
                {
                    foreach (ExternalProduct ep in hw.Products)
                    {
                        if (!beforeIds.Contains(ep.Id))
                        {
                            result.Imported.Add(new CatalogItemInfo
                            {
                                CatalogItemRef = $"ci:{ep.Id}",
                                Manufacturer = emfr.Name ?? "",
                                Name = ep.Text ?? "",
                                OrderNumber = ep.OrderNumber ?? ""
                            });
                        }
                    }
                }
            }

            return result;
        }
#endif

        /// <summary>
        /// Internalize a UnifiedCatalogItem into the project's local catalog.
        /// Verified: M:Knx.Ets.Sdk.Root.InternalizeProductData(Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem)
        ///   (SDK XML line 28277)
        ///   "Imports the product data from the Catalog into the Project."
        ///   Throws ValidationResultException if the object cannot be added.
        /// No UndoManager: changes the product store, not the project.
        ///
        /// Iterates Project.Root.Manufacturers (the PROJECT collection) because
        /// InternalizeProductData pulls the item INTO the project. NOT switched to
        /// GlobalProductStoreManufacturers -- that would be wrong here.
        ///
        /// Deterministic resolution: if the ID-diff finds nothing (item was already
        /// local), fall back to matching by OrderNumber + KnxManufacturerId.
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem.OrderNumber (SDK XML line 29548)
        ///   "Gets the order number; must be unique within all products of one manufacturer."
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem.ParentManufacturer (SDK XML line 29643)
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedManufacturer.KnxManufacturerId (SDK XML line 29721)
        /// Verified: P:Knx.Ets.Sdk.Product.Product.OrderNumber (SDK XML line 7141)
        /// Verified: P:Knx.Ets.Sdk.Product.Manufacturer.KnxManufacturerId (SDK XML line 8216)
        /// </summary>
        public InternalizeResult InternalizeProductData(string unifiedCatalogItemRef)
        {
            if (string.IsNullOrWhiteSpace(unifiedCatalogItemRef))
                throw new ArgumentException("unifiedCatalogItemRef is required for catalog.internalize.");

            var item = FindUnifiedCatalogItem(unifiedCatalogItemRef);

            // Snapshot local (project) catalog before internalization.
            var beforeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Manufacturer mfr in Project.Root.Manufacturers)
            {
                foreach (CatalogItem ci in mfr.AllCatalogItems)
                {
                    beforeIds.Add(ci.Id);
                }
            }

            Project.Root.InternalizeProductData(item);
            BumpRevision();

            // Primary: find the newly added local CatalogItem by ID-diff.
            foreach (Manufacturer mfr in Project.Root.Manufacturers)
            {
                foreach (CatalogItem ci in mfr.AllCatalogItems)
                {
                    if (!beforeIds.Contains(ci.Id))
                    {
                        return new InternalizeResult { CatalogItemRef = $"ci:{ci.Id}" };
                    }
                }
            }

            // Fallback: item was already local (no new ID appeared).
            // Deterministically resolve by matching OrderNumber + manufacturer.
            var targetOrderNumber = item.OrderNumber;
            var targetMfrId = item.ParentManufacturer?.KnxManufacturerId;

            if (!string.IsNullOrEmpty(targetOrderNumber) && targetMfrId.HasValue)
            {
                foreach (Manufacturer mfr in Project.Root.Manufacturers)
                {
                    if (mfr.KnxManufacturerId != targetMfrId.Value)
                        continue;

                    foreach (CatalogItem ci in mfr.AllCatalogItems)
                    {
                        if (string.Equals(ci.Product?.OrderNumber, targetOrderNumber,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return new InternalizeResult { CatalogItemRef = $"ci:{ci.Id}" };
                        }
                    }
                }
            }

            // If neither diff nor OrderNumber match found the item,
            // something unexpected happened. Fail loud rather than return empty ref.
            throw new InvalidOperationException(
                $"InternalizeProductData succeeded but the newly-local CatalogItem "
              + $"could not be resolved for '{unifiedCatalogItemRef}' "
              + $"(OrderNumber='{targetOrderNumber ?? ""}', MfrId={targetMfrId?.ToString() ?? "null"}).");
        }

        /// <summary>
        /// Resolve "uci:{Id}" to a UnifiedCatalogItem from Root.UnifiedManufacturers.
        /// Verified: P:Knx.Ets.Sdk.UnifiedCatalog.UnifiedCatalogItem.Id (SDK XML line 29513)
        /// </summary>
        private UnifiedCatalogItem FindUnifiedCatalogItem(string uciRef)
        {
            if (!uciRef.StartsWith("uci:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid unified catalog item ref: {uciRef}");

            var id = uciRef.Substring(4);
            foreach (UnifiedManufacturer mfr in Project.Root.UnifiedManufacturers)
            {
                foreach (UnifiedCatalogItem item in mfr.AllCatalogItems)
                {
                    if (item.Id == id)
                        return item;
                }
            }

            throw new KeyNotFoundException($"UnifiedCatalogItem {uciRef} not found in unified catalog.");
        }

        // ---------------------------------------------------------------
        // Job tracking (device.program / firmware.update)
        // ---------------------------------------------------------------

        public JobStatusInfo GetJobStatus(string jobId)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
                throw new KeyNotFoundException($"Job {jobId} not found.");

            return new JobStatusInfo
            {
                State = entry.State,
                Percent = entry.Percent,
                Error = entry.Error,
                Result = entry.Result
            };
        }

        /// <summary>
        /// Cancel a running job. Idempotent: canceling a terminal job is a no-op.
        /// Verified: M:Knx.Ets.Sdk.Root.CancelPendingOperations (SDK XML line 28721)
        ///   "Cancels all pending operations." (void, no parameters)
        /// CancelPendingOperations cancels ALL pending operations, not
        /// just this specific job/device. The SDK (6.3.0) does not expose a per-device
        /// cancel method. If multiple downloads run concurrently, all will be canceled.
        ///
        /// #4: CancelJob marks "canceling" and RETAINS the bus lease. The bus is
        /// released only when the matching terminal ETS event fires (OnOnlineOperationEvent)
        /// or when the background task (scan/monitor) actually completes.
        /// </summary>
        public void CancelJob(string jobId)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
                throw new KeyNotFoundException($"Job {jobId} not found.");

            // Already terminal? No-op (idempotent per protocol.md).
            if (entry.IsTerminal)
                return;

            // #4: Mark "canceling" -- do NOT release bus lease here.
            // The lease is released by the terminal event handler or background task completion.
            entry.State = "canceling";

            // Cancel background tasks (scan, monitor) -- they release the bus in their finally.
            entry.Cts?.Cancel();

            // Cancel ETS engine operations (program, unload, firmware).
            // The terminal OnOnlineOperationsEvent will release the bus lease.
            if (entry.Device != null)
                Project.Root.CancelPendingOperations();
        }

        // ---------------------------------------------------------------
        // Online operation event subscription for job progress
        // ---------------------------------------------------------------

        private void EnsureEventSubscription()
        {
            if (_eventSubscribed) return;
            // Verified: E:Knx.Ets.Sdk.Project.Project.OnOnlineOperationsEvent (SDK XML line 16542)
            // Verified: T:Knx.Ets.Sdk.Project.Project.OnlineOperationsEvents delegate (SDK XML line 16530)
            // The event fires on a background thread; JobEntry fields use volatile for visibility.
            Project.OnOnlineOperationsEvent += OnOnlineOperationEvent;
            _eventSubscribed = true;
        }

        /// <summary>
        /// Handles SDK online operation events for job tracking.
        /// Called on a background thread by ETS.
        /// sender = the Device on which the operation runs.
        /// e = OnlineOperationStateChangedEventArgs | OnlineOperationProgressChangedEventArgs
        ///     | OnlineOperationProgModeEventArgs
        ///
        /// #8: Correlate events to the ACTIVE (non-terminal) job by BOTH device AND op type.
        ///     Only Download/FirmwareUpdate/Unload jobs receive OnOnlineOperationsEvent;
        ///     Scan/Monitor jobs use background tasks. Never mutate a terminal job again.
        ///     Release only the job's OWN bus lease on terminal state.
        /// </summary>
        private void OnOnlineOperationEvent(object sender, EventArgs e)
        {
            var device = sender as Device;
            if (device == null) return;

            // #7: Extract OperationType from the event args base class to correlate
            // the event to the correct job. Without this, an unrelated ETS operation
            // on the same device could complete the wrong job.
            // Verified: T:Knx.Ets.Sdk.Project.OnlineOperationEventArgs (SDK XML line 16293)
            //   F:Device, F:OperationType, F:LoadUnloadType (SDK XML lines 16306-16318)
            // Verified: T:Knx.Ets.Common.Types.OnlineOperationType (Common.Types XML line 2217)
            //   Members: LoadDevice, UnloadDevice, FirmwareUpdate, DeviceInfo, ResetDevice, ...
            // Verified: T:Knx.Ets.Common.Types.OnlineOperationLoadUnloadType (Common.Types XML line 5763)
            //   Members: PhysicalAddressOverwrite, All, Application, Partial, ...
            var baseArgs = e as OnlineOperationEventArgs;

            foreach (var kvp in _jobs)
            {
                var entry = kvp.Value;

                // #8: Skip terminal jobs -- never re-mutate them.
                if (entry.IsTerminal) continue;

                // #8: Match by device object identity AND restrict to op types that
                // receive OnOnlineOperationsEvent (Download, FirmwareUpdate, Unload, SetIndividualAddress).
                if (entry.Device != device) continue;
                if (entry.OpType != JobOpType.Download
                    && entry.OpType != JobOpType.FirmwareUpdate
                    && entry.OpType != JobOpType.Unload
                    && entry.OpType != JobOpType.SetIndividualAddress)
                    continue;

                // #7: Correlate the event's OperationType/LoadUnloadType to the job's OpType.
                // An unrelated ETS operation on the same device must NOT match.
                if (baseArgs != null && !EventMatchesJob(baseArgs, entry))
                    continue;

                // Verified: T:Knx.Ets.Sdk.Project.OnlineOperationStateChangedEventArgs (SDK XML line 16323)
                //   F:State  -> Knx.Ets.Common.Types.OnlineOperationState enum (Common.Types XML line 2182)
                //   F:ErrorMessage -> string (only when State == Failed, SDK XML line 16340)
                if (e is OnlineOperationStateChangedEventArgs stateArgs)
                {
                    var newState = MapOnlineState(stateArgs.State);
                    entry.State = newState;
#if !ETS5
                    // ETS5 SDK: OnlineOperationStateChangedEventArgs has no ErrorMessage field.
                    if (stateArgs.ErrorMessage != null)
                        entry.Error = stateArgs.ErrorMessage;
#endif

                    // #3/#8: Release only THIS job's bus lease on terminal state.
                    if (newState == "done" || newState == "failed" || newState == "canceled")
                    {
                        ReleaseBus(entry.BusOwner);
                        entry.Cts?.Dispose();
                    }
                }
                // Verified: T:Knx.Ets.Sdk.Project.OnlineOperationProgressChangedEventArgs (SDK XML line 16343)
                //   F:ProgressInPercent -> uint, 0-100 (SDK XML line 16356)
                else if (e is OnlineOperationProgressChangedEventArgs progressArgs)
                {
                    entry.Percent = (int)progressArgs.ProgressInPercent;
                }

                // #8: Only one active job per device expected; stop after first match.
                break;
            }
        }

        /// <summary>
        /// #7: Check whether an OnlineOperationEventArgs matches the job's operation type.
        /// Correlates:
        ///   Download         -> LoadDevice (+any LoadUnloadType sub-variant)
        ///   Unload           -> UnloadDevice
        ///   SetIndividualAddress -> LoadDevice + PhysicalAddressOverwrite
        ///   FirmwareUpdate   -> FirmwareUpdate
        /// </summary>
        private static bool EventMatchesJob(OnlineOperationEventArgs args, JobEntry entry)
        {
            var evtOp = args.OperationType;
            var evtLu = args.LoadUnloadType;

            switch (entry.OpType)
            {
                case JobOpType.Download:
                    // Download is LoadDevice with any LoadUnloadType except PhysicalAddressOverwrite
                    // (that's setIndividualAddress).
                    return evtOp == Knx.Ets.Common.Types.OnlineOperationType.LoadDevice
                        && evtLu != Knx.Ets.Common.Types.OnlineOperationLoadUnloadType.PhysicalAddressOverwrite;

                case JobOpType.Unload:
                    return evtOp == Knx.Ets.Common.Types.OnlineOperationType.UnloadDevice;

                case JobOpType.SetIndividualAddress:
                    // StartOverwritingIndividualAddress -> LoadDevice + PhysicalAddressOverwrite.
                    return evtOp == Knx.Ets.Common.Types.OnlineOperationType.LoadDevice
                        && evtLu == Knx.Ets.Common.Types.OnlineOperationLoadUnloadType.PhysicalAddressOverwrite;

#if !ETS5
                // ETS5 SDK: OnlineOperationType has no FirmwareUpdate member.
                case JobOpType.FirmwareUpdate:
                    return evtOp == Knx.Ets.Common.Types.OnlineOperationType.FirmwareUpdate;
#endif

                default:
                    // Scan/Monitor don't use this event path.
                    return false;
            }
        }

        /// <summary>#8: Prune terminal jobs beyond bounded retention.</summary>
        private void PruneTerminalJobs()
        {
            var terminalJobs = new List<KeyValuePair<string, JobEntry>>();
            foreach (var kvp in _jobs)
            {
                if (kvp.Value.IsTerminal)
                    terminalJobs.Add(kvp);
            }

            if (terminalJobs.Count <= MaxTerminalJobs)
                return;

            // Remove oldest terminal jobs (by insertion order -- ConcurrentDictionary
            // enumerates in roughly insertion order).
            int toRemove = terminalJobs.Count - MaxTerminalJobs;
            for (int i = 0; i < toRemove; i++)
            {
                _jobs.TryRemove(terminalJobs[i].Key, out _);
            }
        }

        /// <summary>
        /// Map SDK OnlineOperationState enum to protocol state strings.
        /// Verified enum members (Common.Types XML line 2182):
        ///   Waiting, Running, Finished, Canceled, Failed, Canceling
        /// Protocol states (protocol.md): running, done, failed, canceled
        /// </summary>
        private static string MapOnlineState(Knx.Ets.Common.Types.OnlineOperationState state)
        {
            switch (state)
            {
                case Knx.Ets.Common.Types.OnlineOperationState.Waiting:
                case Knx.Ets.Common.Types.OnlineOperationState.Running:
                case Knx.Ets.Common.Types.OnlineOperationState.Canceling:
                    return "running";
                case Knx.Ets.Common.Types.OnlineOperationState.Finished:
                    return "done";
                case Knx.Ets.Common.Types.OnlineOperationState.Canceled:
                    return "canceled";
                case Knx.Ets.Common.Types.OnlineOperationState.Failed:
                    return "failed";
                default:
                    return "running";
            }
        }

        // -- Helpers --

        /// <summary>
        /// #9: Look up a DatapointSubtype (or DatapointType) from master data.
        /// Does NOT silently fall back to the main type when the requested subtype is absent.
        /// Throws KeyNotFoundException if the main type is not found.
        /// Returns null only when no subNumber is specified and the main type is not found
        /// (for optional DPT assignment).
        /// </summary>
        private DomObject? FindDatapointSubtype(uint mainNumber, uint? subNumber)
        {
            var dpt = _ctx.Project.Root.KnxMasterData.DatapointTypes
                          .Cast<DatapointType>()
                          .FirstOrDefault(t => t.Number == mainNumber);
            if (dpt == null)
                throw new KeyNotFoundException($"Datapoint type {mainNumber} not found in master data.");

            if (!subNumber.HasValue)
                return dpt;

            var sub = dpt.DatapointSubtypes
                         .Cast<DatapointSubtype>()
                         .FirstOrDefault(s => s.Number == subNumber.Value);
            if (sub == null)
            {
                // #9: Do NOT silently fall back to the main type.
                throw new KeyNotFoundException(
                    $"Datapoint subtype {mainNumber}.{subNumber.Value:D3} not found in master data.");
            }

            return sub;
        }

        // ---------------------------------------------------------------
        // Phase A: Group Address structural editing
        // ---------------------------------------------------------------

        public void DeleteGroupAddress(string gaRef)
        {
            var ga = FindGroupAddress(gaRef);

            RunInMarker("Bridge: Delete GroupAddress", () =>
            {
                // Navigate to the parent GroupRange's collection for safe deletion.
                // AllGroupAddresses may be a read-only flat view in some SDK versions.
                bool deleted = false;
                foreach (GroupRange gr in Inst.GroupRanges)
                {
                    if (TryDeleteGaFromRange(gr, ga))
                    {
                        deleted = true;
                        break;
                    }
                }
                if (!deleted)
                    throw new KeyNotFoundException($"Could not find parent collection for GroupAddress {gaRef}.");
            });
        }

        /// <summary>Recursively search group ranges for a GA and delete it from its parent collection.</summary>
        private static bool TryDeleteGaFromRange(GroupRange range, GroupAddress target)
        {
            foreach (GroupAddress ga in range.GroupAddresses)
            {
                if (ga.Id == target.Id)
                {
                    range.GroupAddresses.Delete(target);
                    return true;
                }
            }
            if (range.GroupRanges != null)
            {
                foreach (GroupRange sub in range.GroupRanges)
                {
                    if (TryDeleteGaFromRange(sub, target))
                        return true;
                }
            }
            return false;
        }

        public GroupAddressInfo RenameGroupAddress(string gaRef, string name)
        {
            var ga = FindGroupAddress(gaRef);

            return RunInMarker("Bridge: Rename GroupAddress", () =>
            {
                ga.Name = name;
                var info = BuildGaInfo(ga);
                info.Truncated = info.Name != name; // ETS shortened the name to its limit
                return info;
            });
        }

        public GroupAddressInfo SetGroupAddressDescription(string gaRef, string description)
        {
            var ga = FindGroupAddress(gaRef);

            return RunInMarker("Bridge: Set GA Description", () =>
            {
                ga.Description = description;
                return BuildGaInfo(ga);
            });
        }

        public GroupAddressInfo SetGroupAddressDpt(string gaRef, uint dptMain, uint? dptSub)
        {
            var ga = FindGroupAddress(gaRef);
            var dptObj = FindDatapointSubtype(dptMain, dptSub);

            return RunInMarker("Bridge: Set GA DPT", () =>
            {
                if (dptObj != null)
                    ga.DatapointTypeObject = dptObj;
                return BuildGaInfo(ga);
            });
        }

        // ---------------------------------------------------------------
        // Phase A: Group Range
        // ---------------------------------------------------------------

        public GroupRangeInfo CreateGroupRange(string name, ushort address, string? parentGroupRangeRef)
        {
            GroupRangeCollection targetCollection;

            if (!string.IsNullOrEmpty(parentGroupRangeRef))
            {
                var parent = FindGroupRange(parentGroupRangeRef!);
                if (parent.GroupRanges == null)
                    throw new ArgumentException(
                        $"GroupRange {parentGroupRangeRef} does not support child ranges.");
                targetCollection = parent.GroupRanges;
            }
            else
            {
                targetCollection = Inst.GroupRanges;
            }

            return RunInMarker("Bridge: Create GroupRange", () =>
            {
                var added = targetCollection
                    .Add(name, 1, AddressAllocations.StartWith, address)
                    .Cast<GroupRange>().First();

                return new GroupRangeInfo
                {
                    Ref = BuildGroupRangeRef(added),
                    Name = added.Name ?? name,
                    // Verified: P:Knx.Ets.Sdk.Project.GroupRange.Description (get/set, SDK XML line 15088)
                    Description = added.Description,
                    // Verified: P:Knx.Ets.Sdk.Project.GroupRange.Comment (get/set, SDK XML line 15074)
                    Comment = added.Comment,
                    Address = added.Address ?? 0
                };
            });
        }

        public void DeleteGroupRange(string groupRangeRef)
        {
            var gr = FindGroupRange(groupRangeRef);

            RunInMarker("Bridge: Delete GroupRange", () =>
            {
                // Find parent collection by searching recursively.
                bool deleted = TryDeleteGroupRangeFromCollection(Inst.GroupRanges, gr);
                if (!deleted)
                    throw new KeyNotFoundException(
                        $"Could not find parent collection for GroupRange {groupRangeRef}.");
            });
        }

        private static bool TryDeleteGroupRangeFromCollection(GroupRangeCollection collection, GroupRange target)
        {
            foreach (GroupRange gr in collection)
            {
                if (gr.Id == target.Id)
                {
                    collection.Delete(target);
                    return true;
                }
                if (gr.GroupRanges != null)
                {
                    if (TryDeleteGroupRangeFromCollection(gr.GroupRanges, target))
                        return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Phase A: Device structural editing
        // ---------------------------------------------------------------

        public void DeleteDevice(string deviceRef)
        {
            var device = FindDevice(deviceRef);

            RunInMarker("Bridge: Delete Device", () => Inst.AllDevices.Delete(device));
        }

        public DeviceInfo RenameDevice(string deviceRef, string name)
        {
            var device = FindDevice(deviceRef);

            return RunInMarker("Bridge: Rename Device", () =>
            {
                device.Name = name;
                var info = BuildDeviceInfo(device);
                info.Truncated = info.Name != name; // ETS shortened the name to its limit
                return info;
            });
        }

        /// <summary>
        /// Set the device-level address (0-255 range, within its current line).
        /// Accepts a full "area.line.device" address string; validates area/line match.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.Address "Gets or sets the device address [0...255]"
        /// Throws NotSupportedException if the device has no individual address.
        /// </summary>
        public DeviceInfo SetDeviceAddress(string deviceRef, string address)
        {
            var device = FindDevice(deviceRef);
            var line = device.Line;
            if (line == null)
                throw new NotSupportedOperationException(
                    $"Device {deviceRef} is not assigned to a line and has no individual address.");

            var deviceAddr = ParseIndividualAddress(address, line);

            return RunInMarker("Bridge: Set Device Address", () =>
            {
                device.Address = deviceAddr;
                return BuildDeviceInfo(device);
            });
        }

        public void UnassignDeviceFromLine(string deviceRef)
        {
            // Moves the device into the installation's unassigned-devices collection,
            // i.e. detaches it from its line WITHOUT deleting it (config/links kept).
            // Verified: P:Knx.Ets.Sdk.Project.Installation.UnassignedDevices +
            // M:Knx.Ets.Sdk.Project.RefUnassignedDeviceCollection.Move(Device).
            var device = FindDevice(deviceRef);
            RunInMarker("Bridge: Unassign Device", () => Inst.UnassignedDevices.Move(device));
        }

        // ---------------------------------------------------------------
        // Phase A: Topology
        // ---------------------------------------------------------------

        /// <summary>
        /// Create a new area in the topology.
        /// Verified: M:Knx.Ets.Sdk.Project.AreaCollection.Add (SDK XML)
        /// </summary>
        public TopologyAreaInfo CreateArea(string name, ushort? address)
        {
            return RunInMarker("Bridge: Create Area", () =>
            {
                Area added;
                if (address.HasValue)
                {
                    added = Inst.Areas
                        .Add(name, 1, AddressAllocations.StartWith, address.Value)
                        .Cast<Area>().First();
                }
                else
                {
                    added = Inst.Areas
                        .Add(name)
                        .Cast<Area>().First();
                }

                return new TopologyAreaInfo
                {
                    AreaRef = $"a{added.Address}",
                    Address = added.Address.ToString(),
                    Name = added.Name ?? name,
                    Description = added.Description,
                    Comment = added.Comment
                };
            });
        }

        public void DeleteArea(string areaRef)
        {
            var area = FindArea(areaRef);

            RunInMarker("Bridge: Delete Area", () => Inst.Areas.Delete(area));
        }

        /// <summary>
        /// Create a new line under the specified area.
        /// Verified: M:Knx.Ets.Sdk.Project.LineCollection.Add (SDK XML)
        /// </summary>
        public TopologyLineInfo CreateLine(string areaRef, string name, ushort? address)
        {
            var area = FindArea(areaRef);

            return RunInMarker("Bridge: Create Line", () =>
            {
                Line added;
                if (address.HasValue)
                {
                    added = area.Lines
                        .Add(name, 1, AddressAllocations.StartWith, address.Value)
                        .Cast<Line>().First();
                }
                else
                {
                    added = area.Lines
                        .Add(name)
                        .Cast<Line>().First();
                }

                return new TopologyLineInfo
                {
                    LineRef = $"a{area.Address}:l{added.Address}",
                    Address = added.AddressString ?? $"{area.Address}.{added.Address}",
                    Name = added.Name ?? name,
                    Description = added.Description,
                    Comment = added.Comment
                };
            });
        }

        public void DeleteLine(string lineRef)
        {
            var line = FindLine(lineRef);
            var area = line.Area;

            RunInMarker("Bridge: Delete Line", () => area.Lines.Delete(line));
        }

        // ---------------------------------------------------------------
        // Phase A: ComObject flags
        // ---------------------------------------------------------------

        /// <summary>
        /// Set individual flags on a ComObjectInstanceRef. Only provided (non-null)
        /// flags are written; others are left unchanged. Returns read-back state.
        /// Verified: All flag properties are bool? (nullable) and writable in 6.3.0.
        /// Verified: Priority is Priority? (nullable enum: Low, High, Alert).
        /// IsActive guard: setting flags on an inactive CO is refused.
        /// </summary>
        public ComObjectFlagsResult SetComObjectFlags(string comObjectRef,
            bool? communicationFlag, bool? readFlag, bool? writeFlag,
            bool? transmitFlag, bool? updateFlag, bool? readOnInitFlag,
            string? priority)
        {
            var (device, co) = FindComObject(comObjectRef);
            if (!co.IsActive)
                throw new InactiveObjectException(comObjectRef);

            Priority? parsedPriority = null;
            if (!string.IsNullOrEmpty(priority))
            {
                switch (priority!.ToLowerInvariant())
                {
                    case "low": parsedPriority = Priority.Low; break;
                    case "high": parsedPriority = Priority.High; break;
                    case "alert": parsedPriority = Priority.Alert; break;
                    default:
                        throw new ArgumentException(
                            $"Invalid priority: {priority}. Valid values: Low, High, Alert.");
                }
            }

            return RunInMarker("Bridge: Set CO Flags", () =>
            {
                if (communicationFlag.HasValue) co.CommunicationFlag = communicationFlag.Value;
                if (readFlag.HasValue) co.ReadFlag = readFlag.Value;
                if (writeFlag.HasValue) co.WriteFlag = writeFlag.Value;
                if (transmitFlag.HasValue) co.TransmitFlag = transmitFlag.Value;
                if (updateFlag.HasValue) co.UpdateFlag = updateFlag.Value;
                if (readOnInitFlag.HasValue) co.ReadOnInitFlag = readOnInitFlag.Value;
                if (parsedPriority.HasValue) co.Priority = parsedPriority.Value;

                // Read-back inside marker for atomicity (#14).
                return new ComObjectFlagsResult
                {
                    Ref = BuildCoRef(device.Puid, co),
                    CommunicationFlag = co.CommunicationFlag,
                    ReadFlag = co.ReadFlag,
                    WriteFlag = co.WriteFlag,
                    TransmitFlag = co.TransmitFlag,
                    UpdateFlag = co.UpdateFlag,
                    ReadOnInitFlag = co.ReadOnInitFlag,
                    Priority = co.Priority.ToString()
                };
            });
        }

        // ---------------------------------------------------------------
        // Phase A: Device label setters
        // ---------------------------------------------------------------

        /// <summary>
        /// Set Device.Description.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.Description "Gets or sets" (SDK XML line 12683)
        /// </summary>
        public DeviceInfo SetDeviceDescription(string deviceRef, string description)
        {
            var device = FindDevice(deviceRef);
            return RunInMarker("Bridge: Set Device Description", () =>
            {
                device.Description = description;
                return BuildDeviceInfo(device);
            });
        }

        /// <summary>
        /// Set Device.Comment.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.Comment "Gets or sets" (SDK XML line 12632)
        /// Value may be plain text or RTF.
        /// </summary>
        public DeviceInfo SetDeviceComment(string deviceRef, string comment)
        {
            var device = FindDevice(deviceRef);
            return RunInMarker("Bridge: Set Device Comment", () =>
            {
                device.Comment = comment;
                return BuildDeviceInfo(device);
            });
        }

        // ---------------------------------------------------------------
        // Phase A: ComObject label setters
        // ---------------------------------------------------------------

        /// <summary>Build a ComObjectInfo DTO from a resolved device + co.</summary>
        private static ComObjectInfo BuildComObjectInfo(long devicePuid, ComObjectInstanceRef co)
        {
            var links = new List<string>();
            foreach (Connector conn in co.Connectors)
            {
                if (conn.GroupAddress != null)
                    links.Add(BuildGaRef(conn.GroupAddress));
            }
            return new ComObjectInfo
            {
                Ref = BuildCoRef(devicePuid, co),
                Number = co.Number,
                Name = co.Name ?? "",
                Description = co.Description,
                FunctionText = co.FunctionText,
                Text = co.Text,
                Dpt = FormatDpt(co.DatapointTypes?.Cast<object>().FirstOrDefault()),
                Flags = BuildFlags(co),
                Links = links
            };
        }

        /// <summary>
        /// Set ComObjectInstanceRef.Description.
        /// Verified: P:Knx.Ets.Sdk.Project.ComObjectInstanceRef.Description "Gets or sets" (SDK XML line 11788)
        /// Not guarded by device download lock.
        /// </summary>
        public ComObjectInfo SetComObjectDescription(string comObjectRef, string description)
        {
            var (device, co) = FindComObject(comObjectRef);
            return RunInMarker("Bridge: Set CO Description", () =>
            {
                co.Description = description;
                return BuildComObjectInfo(device.Puid, co);
            });
        }

        /// <summary>
        /// Set ComObjectInstanceRef.FunctionText.
        /// Verified: P:Knx.Ets.Sdk.Project.ComObjectInstanceRef.FunctionText "Gets or sets" (SDK XML line 11802)
        /// Not guarded by device download lock.
        /// </summary>
        public ComObjectInfo SetComObjectFunctionText(string comObjectRef, string functionText)
        {
            var (device, co) = FindComObject(comObjectRef);
            return RunInMarker("Bridge: Set CO FunctionText", () =>
            {
                co.FunctionText = functionText;
                return BuildComObjectInfo(device.Puid, co);
            });
        }

        // ---------------------------------------------------------------
        // Phase B: Building structure helpers
        // ---------------------------------------------------------------

        /// <summary>
        /// Resolve "bp:{DomObject.Id}" to a BuildingPart.
        /// Recursively searches Inst.Buildings.
        /// </summary>
        private BuildingPart FindBuildingPart(string bpRef)
        {
            if (!bpRef.StartsWith("bp:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid building part ref: {bpRef}");

            var id = bpRef.Substring(3);
            var found = FindBuildingPartById(Inst.Buildings, id);
            if (found == null)
                throw new KeyNotFoundException($"BuildingPart {bpRef} not found.");
            return found;
        }

        private static BuildingPart? FindBuildingPartById(BuildingPartCollection parts, string id)
        {
            foreach (BuildingPart bp in parts)
            {
                if (bp.Id == id)
                    return bp;
                var sub = FindBuildingPartById(bp.BuildingParts, id);
                if (sub != null)
                    return sub;
            }
            return null;
        }

        private static string BuildBpRef(BuildingPart bp) => $"bp:{bp.Id}";

        /// <summary>
        /// Resolve "bf:{DomObject.Id}" to a BuildingFunction.
        /// Recursively searches all BuildingParts.
        /// </summary>
        private BuildingFunction FindBuildingFunction(string bfRef)
        {
            if (!bfRef.StartsWith("bf:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid building function ref: {bfRef}");

            var id = bfRef.Substring(3);
            var found = FindBuildingFunctionById(Inst.Buildings, id);
            if (found == null)
                throw new KeyNotFoundException($"BuildingFunction {bfRef} not found.");
            return found;
        }

        private static BuildingFunction? FindBuildingFunctionById(BuildingPartCollection parts, string id)
        {
            foreach (BuildingPart bp in parts)
            {
                foreach (BuildingFunction bf in bp.BuildingFunctions)
                {
                    if (bf.Id == id)
                        return bf;
                }
                var sub = FindBuildingFunctionById(bp.BuildingParts, id);
                if (sub != null)
                    return sub;
            }
            return null;
        }

        private static string BuildBfRef(BuildingFunction bf) => $"bf:{bf.Id}";

        /// <summary>Build a BuildingPartInfo tree recursively.
        /// Verified: P:Knx.Ets.Sdk.Project.BuildingPart.Description (get/set, SDK XML line 9558)
        /// Verified: P:Knx.Ets.Sdk.Project.BuildingPart.Comment (get/set, SDK XML line 9539)
        /// Verified: P:Knx.Ets.Sdk.Project.BuildingFunction.Description (get/set, SDK XML line 9285)
        /// Verified: P:Knx.Ets.Sdk.Project.BuildingFunction.Comment (get/set, SDK XML line 9268)
        /// </summary>
        private static BuildingPartInfo BuildBuildingPartInfo(BuildingPart bp)
        {
            var info = new BuildingPartInfo
            {
                Ref = BuildBpRef(bp),
                Name = bp.Name ?? "",
                Description = bp.Description,
                Comment = bp.Comment,
                Type = bp.Type.ToString()
            };

            foreach (Device dev in bp.Devices)
                info.Devices.Add($"d{dev.Puid}");

            foreach (BuildingFunction bf in bp.BuildingFunctions)
            {
                var bfInfo = new BuildingFunctionInfo
                {
                    Ref = BuildBfRef(bf),
                    Name = bf.Name ?? "",
                    Description = bf.Description,
                    Comment = bf.Comment
                };
                foreach (GroupAddressRef gaRef in bf.GroupAddressRefs)
                {
                    if (gaRef.GroupAddress != null)
                        bfInfo.GroupAddresses.Add(BuildGaRef(gaRef.GroupAddress));
                }
                if (bfInfo.GroupAddresses.Count == 0)
                {
                    // ETS 6.3.0 SDK caches object trees: after a GA is unlinked/deleted
                    // from a function the address list reads empty until the app restarts.
                    // Flag it so the LLM does not treat "empty" as authoritative.
                    bfInfo.Note = "Empty address list. In ETS 6.3.0 this may be genuinely " +
                        "empty OR a stale SDK cache after a GA was unlinked/deleted (in this " +
                        "app or in ETS); the list refreshes only after the owning app restarts. " +
                        "Do not treat empty as authoritative -- verify in ETS if it matters.";
                }
                info.Functions.Add(bfInfo);
            }

            foreach (BuildingPart child in bp.BuildingParts)
                info.Children.Add(BuildBuildingPartInfo(child));

            return info;
        }

        // ---------------------------------------------------------------
        // Phase B: Building structure read
        // ---------------------------------------------------------------

        public List<BuildingPartInfo> ListBuilding()
        {
            var result = new List<BuildingPartInfo>();
            foreach (BuildingPart bp in Inst.Buildings)
                result.Add(BuildBuildingPartInfo(bp));
            return result;
        }

        // ---------------------------------------------------------------
        // Phase B: Building structure mutations
        // ---------------------------------------------------------------

        /// <summary>
        /// Parses a BuildingPartType string into the SDK enum.
        /// Valid values: Building, BuildingPart, Floor, Room, DistributionBoard, Corridor, Stairway.
        /// </summary>
        private static BuildingPartType ParseBuildingPartType(string typeStr)
        {
            switch (typeStr)
            {
                case "Building": return BuildingPartType.Building;
                case "BuildingPart": return BuildingPartType.BuildingPart;
                case "Floor": return BuildingPartType.Floor;
                case "Room": return BuildingPartType.Room;
                case "DistributionBoard": return BuildingPartType.DistributionBoard;
                case "Corridor": return BuildingPartType.Corridor;
                case "Stairway": return BuildingPartType.Stairway;
                default:
                    throw new ArgumentException(
                        $"Invalid building part type: {typeStr}. "
                      + "Valid: Building, BuildingPart, Floor, Room, DistributionBoard, Corridor, Stairway.");
            }
        }

        public BuildingPartInfo CreateBuildingPart(string name, string type, string? parentBuildingPartRef, string? spaceUsage)
        {
            var bpType = ParseBuildingPartType(type);

            return RunInMarker("Bridge: Create BuildingPart", () =>
            {
                BuildingPartCollection targetCollection;
                if (!string.IsNullOrEmpty(parentBuildingPartRef))
                {
                    var parent = FindBuildingPart(parentBuildingPartRef!);
                    targetCollection = parent.BuildingParts;
                }
                else
                {
                    targetCollection = Inst.Buildings;
                }

                BuildingPart added;
                if (!string.IsNullOrEmpty(spaceUsage))
                {
                    // SpaceUsage is a MasterData type, lookup by text from KnxMasterData.SpaceUsages.
                    SpaceUsage? su = null;
                    foreach (SpaceUsage s in Project.Root.KnxMasterData.SpaceUsages)
                    {
                        if (string.Equals(s.Text, spaceUsage, StringComparison.OrdinalIgnoreCase))
                        {
                            su = s;
                            break;
                        }
                    }
                    if (su == null)
                        throw new KeyNotFoundException($"SpaceUsage '{spaceUsage}' not found in master data.");

                    added = targetCollection
                        .Add(name, 1, su, bpType)
                        .Cast<BuildingPart>().First();
                }
                else
                {
                    added = targetCollection
                        .Add(name, bpType)
                        .Cast<BuildingPart>().First();
                }

                return BuildBuildingPartInfo(added);
            });
        }

        public void DeleteBuildingPart(string buildingPartRef)
        {
            var bp = FindBuildingPart(buildingPartRef);

            RunInMarker("Bridge: Delete BuildingPart", () =>
            {
                // Delete from parent collection.
                var parentCollection = bp.ParentCollection as BuildingPartCollection;
                if (parentCollection != null)
                {
                    parentCollection.Delete(bp);
                }
                else
                {
                    throw new KeyNotFoundException(
                        $"Could not find parent collection for BuildingPart {buildingPartRef}.");
                }
            });
        }

        public BuildingPartInfo RenameBuildingPart(string buildingPartRef, string name)
        {
            var bp = FindBuildingPart(buildingPartRef);

            return RunInMarker("Bridge: Rename BuildingPart", () =>
            {
                bp.Name = name;
                return BuildBuildingPartInfo(bp);
            });
        }

        public void AssignDevice(string buildingPartRef, string deviceRef)
        {
            var bp = FindBuildingPart(buildingPartRef);
            var device = FindDevice(deviceRef);

            RunInMarker("Bridge: Assign Device to BuildingPart", () => bp.Link(device));
        }

        public void UnassignDevice(string buildingPartRef, string deviceRef)
        {
            var bp = FindBuildingPart(buildingPartRef);
            var device = FindDevice(deviceRef);

            RunInMarker("Bridge: Unassign Device from BuildingPart", () => bp.Unlink(device));
        }

        // ---------------------------------------------------------------
        // Phase B: Building functions
        // ---------------------------------------------------------------

        public BuildingFunctionInfo CreateBuildingFunction(string buildingPartRef, string name)
        {
            var bp = FindBuildingPart(buildingPartRef);

            return RunInMarker("Bridge: Create BuildingFunction", () =>
            {
                // BuildingFunctionCollection.Add(IEnumerable<string> names, ushort count)
                var added = bp.BuildingFunctions
                    .Add(new[] { name }, 1)
                    .Cast<BuildingFunction>().First();

                return new BuildingFunctionInfo
                {
                    Ref = BuildBfRef(added),
                    Name = added.Name ?? name,
                    Description = added.Description,
                    Comment = added.Comment
                };
            });
        }

        public void DeleteBuildingFunction(string buildingFunctionRef)
        {
            var bf = FindBuildingFunction(buildingFunctionRef);

            RunInMarker("Bridge: Delete BuildingFunction", () =>
            {
                var parentCollection = bf.ParentCollection as BuildingFunctionCollection;
                if (parentCollection != null)
                {
                    parentCollection.Delete(new[] { bf });
                }
                else
                {
                    throw new KeyNotFoundException(
                        $"Could not find parent collection for BuildingFunction {buildingFunctionRef}.");
                }
            });
        }

        public void LinkBuildingFunctionGA(string buildingFunctionRef, List<string> gaRefs)
        {
            var bf = FindBuildingFunction(buildingFunctionRef);
            var groupAddresses = new List<GroupAddress>();
            foreach (var gaRef in gaRefs)
                groupAddresses.Add(FindGroupAddress(gaRef));

            RunInMarker("Bridge: Link GAs to BuildingFunction", () => bf.Link(groupAddresses));
        }

        public void UnlinkBuildingFunctionGA(string buildingFunctionRef, List<string> gaRefs)
        {
            var bf = FindBuildingFunction(buildingFunctionRef);
            // BuildingFunction.Unlink takes IEnumerable<GroupAddressRef>, not GroupAddress.
            // We need to find the GroupAddressRef objects matching the requested GA refs.
            var gaIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gaRef in gaRefs)
            {
                var ga = FindGroupAddress(gaRef);
                gaIds.Add(ga.Id);
            }

            var refsToUnlink = new List<GroupAddressRef>();
            foreach (GroupAddressRef gar in bf.GroupAddressRefs)
            {
                if (gar.GroupAddress != null && gaIds.Contains(gar.GroupAddress.Id))
                    refsToUnlink.Add(gar);
            }

            RunInMarker("Bridge: Unlink GAs from BuildingFunction", () => bf.Unlink(refsToUnlink));
        }

        // ---------------------------------------------------------------
        // Phase C: Project management
        // ---------------------------------------------------------------

        /// <summary>
        /// Root.Save does NOT exist in the SDK. ETS auto-saves projects.
        /// Always throws NotSupportedOperationException per protocol.
        /// </summary>
        public void ProjectSave()
        {
            throw new NotSupportedOperationException(
                "project.save is not supported: ETS auto-saves projects. "
              + "There is no Root.Save method in the SDK.");
        }

        /// <summary>
        /// Export the current project to a .knxproj file.
        /// Verified: M:Knx.Ets.Sdk.Root.ExportProject(System.String,System.Boolean) -> bool
        /// </summary>
        public bool ProjectExport(string path, bool includeCatalog)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path is required for project.export.");

            var result = Project.Root.ExportProject(path, includeCatalog);
            return result;
        }

        /// <summary>
        /// Root.BackupDatabase is a no-op stub in the SDK (backwards compat only).
        /// Always throws NotSupportedOperationException per protocol.
        /// </summary>
        public void ProjectBackup(string path)
        {
            throw new NotSupportedOperationException(
                "project.backup is not supported: Root.BackupDatabase has no effect "
              + "in ETS 6 (backwards compatibility stub only).");
        }

        public void ProjectUndo()
        {
            Project.UndoManager.Undo();
            BumpRevision();
        }

        public void ProjectRedo()
        {
            Project.UndoManager.Redo();
            BumpRevision();
        }

        // ---------------------------------------------------------------
        // Phase E: Catalog browsing
        // ---------------------------------------------------------------

        /// <summary>
        /// Browse products for a specific manufacturer with paging.
        /// Searches the GLOBAL product store (all installed products).
        /// Traversal: ExternalManufacturer -> HardwareCollection -> ExternalHardware -> Products -> ExternalProduct
        /// Verified: P:Knx.Ets.Sdk.Root.GlobalProductStoreManufacturers (SDK XML line 28242)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalManufacturer.HardwareCollection (SDK XML line 20082)
        /// Verified: P:Knx.Ets.Sdk.External.ExternalHardware.Products (SDK XML line 20181)
        /// </summary>
        /// <summary>
        /// Browse products for a manufacturer. Merges project-local catalog (Root.Manufacturers)
        /// and global product store, same as SearchCatalog and ListManufacturers (#4 fix).
        ///
        /// Verified: P:Knx.Ets.Sdk.Root.Manufacturers (SDK XML line 28214)
        /// Verified: P:Knx.Ets.Sdk.Product.Manufacturer.AllCatalogItems (SDK XML line 8209)
        /// Verified: P:Knx.Ets.Sdk.Root.GlobalProductStoreManufacturers (SDK XML line 28242)
        /// </summary>
        public List<CatalogItemInfo> BrowseProducts(string manufacturerRef, int offset, int limit)
        {
            if (string.IsNullOrWhiteSpace(manufacturerRef))
                throw new ArgumentException("manufacturerRef is required for catalog.browseProducts.");

            if (!manufacturerRef.StartsWith("m", StringComparison.Ordinal) ||
                !uint.TryParse(manufacturerRef.Substring(1), out var mfrId))
                throw new ArgumentException($"Invalid manufacturer ref: {manufacturerRef}");

            // #14: Dedup by (KnxManufacturerId + normalized OrderNumber) instead of
            // mixing CatalogItem.Id and ExternalProduct.Id across different ID spaces.
            var allItems = new List<CatalogItemInfo>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool found = false;

            // 1. Project-local catalog (always has installed products).
            foreach (Manufacturer mfr in Project.Root.Manufacturers)
            {
                if (mfr.KnxManufacturerId != mfrId) continue;
                found = true;
                foreach (CatalogItem ci in mfr.AllCatalogItems)
                {
                    var orderNumber = ci.Product?.OrderNumber ?? "";
                    var dedupKey = $"{mfr.KnxManufacturerId}|{orderNumber.Trim().ToUpperInvariant()}";
                    if (seenKeys.Add(dedupKey))
                    {
                        allItems.Add(new CatalogItemInfo
                        {
                            CatalogItemRef = $"ci:{ci.Id}",
                            Manufacturer = mfr.Name ?? "",
                            Name = ci.Name ?? "",
                            OrderNumber = orderNumber,
                            Description = ci.VisibleDescription
                        });
                    }
                }
            }

            // 2. Global product store as supplement.
            foreach (ExternalManufacturer emfr in Project.Root.GlobalProductStoreManufacturers)
            {
                if (emfr.KnxManufacturerId != mfrId) continue;
                found = true;
                foreach (ExternalHardware hw in emfr.HardwareCollection)
                {
                    foreach (ExternalProduct ep in hw.Products)
                    {
                        var orderNumber = ep.OrderNumber ?? "";
                        var dedupKey = $"{emfr.KnxManufacturerId}|{orderNumber.Trim().ToUpperInvariant()}";
                        if (seenKeys.Add(dedupKey))
                        {
                            allItems.Add(new CatalogItemInfo
                            {
                                CatalogItemRef = $"ci:{ep.Id}",
                                Manufacturer = emfr.Name ?? "",
                                Name = ep.Text ?? "",
                                OrderNumber = orderNumber
                                // Description: ExternalProduct has no VisibleDescription -> null
                            });
                        }
                    }
                }
            }

            if (!found)
                throw new KeyNotFoundException($"Manufacturer {manufacturerRef} not found in project or global product store.");

            // Apply offset/limit pagination.
            var result = new List<CatalogItemInfo>();
            for (int i = offset; i < allItems.Count && result.Count < limit; i++)
                result.Add(allItems[i]);
            return result;
        }

        /// <summary>
        /// Get detailed product info for a specific catalog item.
        /// </summary>
        public CatalogProductDetailInfo ProductInfo(string catalogItemRef)
        {
            var item = FindCatalogItem(catalogItemRef);

            var mediumTypes = new List<string>();
            if (item.MediumTypes != null)
            {
                foreach (var mt in item.MediumTypes)
                    mediumTypes.Add(mt.ToString());
            }

            return new CatalogProductDetailInfo
            {
                CatalogItemRef = $"ci:{item.Id}",
                Manufacturer = item.ParentManufacturer?.Name ?? "",
                Name = item.Name ?? "",
                OrderNumber = item.Product?.OrderNumber ?? "",
                Description = item.VisibleDescription ?? "",
                MediumTypes = mediumTypes
            };
        }

        // ---------------------------------------------------------------
        // Phase D: Bus / Online operations
        // All bus ops serialize through AcquireBus()/ReleaseBus().
        // ---------------------------------------------------------------

        // -- Address helpers for bus operations --

        /// <summary>
        /// Parse "area.line.device" to KNX individual address UInt16.
        /// Encoding: (area &lt;&lt; 12) | (line &lt;&lt; 8) | device.
        /// </summary>
        private static ushort ParseIndividualAddressString(string address)
        {
            address = (address ?? "").Trim().Replace(',', '.');
            var parts = address.Split('.');
            if (parts.Length != 3)
                throw new ArgumentException(
                    $"Invalid individual address format: '{address}'. Expected 'area.line.device' (e.g. '1.1.5').");

            if (!ushort.TryParse(parts[0], out var area) || area > 15)
                throw new ArgumentException($"Invalid area in address: {parts[0]}");
            if (!ushort.TryParse(parts[1], out var line) || line > 15)
                throw new ArgumentException($"Invalid line in address: {parts[1]}");
            if (!ushort.TryParse(parts[2], out var device) || device > 255)
                throw new ArgumentException($"Invalid device in address: {parts[2]}");

            return (ushort)((area << 12) | (line << 8) | device);
        }

        /// <summary>Format UInt16 to "area.line.device".</summary>
        private static string FormatIndividualAddress(ushort addr)
        {
            int area = (addr >> 12) & 0x0F;
            int line = (addr >> 8) & 0x0F;
            int device = addr & 0xFF;
            return $"{area}.{line}.{device}";
        }

        /// <summary>Format UInt16 to 3-level group address "main/middle/sub".</summary>
        private static string FormatGroupAddress3Level(ushort addr)
        {
            int main = (addr >> 11) & 0x1F;
            int middle = (addr >> 8) & 0x07;
            int sub = addr & 0xFF;
            return $"{main}/{middle}/{sub}";
        }

#if ETS5
        /// <summary>
        /// ETS5: format a GroupAddress using GroupAddressValue.ToString with the
        /// project's GroupAddressStyle. ETS5 has no GroupAddress.AddressString;
        /// GroupAddressValue supports "G3" (ThreeLevel), "G2" (TwoLevel), "F" (Free).
        /// </summary>
        private string FormatGroupAddressEts5(GroupAddress ga)
        {
            var style = Project.GroupAddressStyle;
            string fmt;
            switch (style)
            {
                case GroupAddressStyle.ThreeLevel: fmt = "G3"; break;
                case GroupAddressStyle.TwoLevel:   fmt = "G2"; break;
                default:                           fmt = "F";  break;
            }
            return ga.AddressValue.ToString(fmt);
        }
#endif

        /// <summary>Convert byte[] to hex string (uppercase, no separators).</summary>
        private static string BytesToHex(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            var sb = new System.Text.StringBuilder(data.Length * 2);
            foreach (byte b in data)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        /// <summary>Parse hex string to byte array.</summary>
        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
            if (hex.Length % 2 != 0)
                throw new ArgumentException($"Invalid hex string (odd length): '{hex}'");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }


        /// <summary>
        /// Ping a KNX individual address via NetworkManagement.
        /// Verified: M:Knx.Ets.Sdk.Network.NetworkManagement.Ping(System.UInt16) -> Nullable{UInt16}
        ///   (SDK XML line 1625) - sync, returns address if alive, null if not.
        /// Verified: M:Knx.Ets.Sdk.Root.CreateNetworkManagement(Knx.Ets.Sdk.Project.Line)
        ///   -> NetworkManagement (SDK XML line 28645)
        ///   "Creates a new instance of the NetworkManagement interface."
        ///   Line can be null for default connection.
        /// </summary>
        public BusPingResult BusPing(string address)
        {
            var ia = ParseIndividualAddressString(address);
            var busOwner = Guid.NewGuid().ToString("D");

            AcquireBus(busOwner);
            try
            {
                // Line=null -> default connection.
                // #9: Create SDK objects on the UI thread (this method is dispatched on UI thread).
                using (var nm = Project.Root.CreateNetworkManagement(null))
                {
                    if (nm == null)
                        throw new BusUnavailableException(
                            "No network management available. Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                          + "current interface.");

                    // Verified: Ping returns ushort? -- non-null means alive.
                    var result = nm.Ping(ia);
                    return new BusPingResult { Alive = result.HasValue };
                }
            }
            finally
            {
                ReleaseBus(busOwner);
            }
        }

#if ETS5
        public string StartScanLine(string lineRef)
        {
            throw new NotSupportedOperationException(
                "bus.scanLine is not supported on ETS5 (NetworkManagement.IndividualAddressScanRangeAsync does not exist in ETS5 SDK).");
        }
#else
        /// <summary>
        /// Scan a topology line for responding devices. Long-running -> job model.
        /// Verified: M:Knx.Ets.Sdk.Network.NetworkManagement.IndividualAddressScanRangeAsync(
        ///   System.UInt16, System.UInt16, System.IProgress{System.Int32},
        ///   System.Threading.CancellationToken) -> Task{UInt16[]}
        ///   (SDK XML line 1667) - "Scans a range of individual addresses."
        /// #9: Create NetworkManagement ON the UI thread (this method runs on UI via dispatcher).
        ///     The returned Task is awaited off the dispatcher by the background task.
        /// #8: Register job BEFORE starting the SDK operation.
        /// </summary>
        public string StartScanLine(string lineRef)
        {
            var line = FindLine(lineRef);
            ushort areaAddr = (ushort)line.Area.Address;
            ushort lineAddr = (ushort)line.Address;
            ushort rangeStart = (ushort)((areaAddr << 12) | (lineAddr << 8) | 0);
            ushort rangeEnd = (ushort)((areaAddr << 12) | (lineAddr << 8) | 255);

            var jobId = Guid.NewGuid().ToString("D");
            AcquireBus(jobId);

            // #8: Register job BEFORE starting.
            var cts = new System.Threading.CancellationTokenSource();
            var entry = new JobEntry(jobId, lineRef, null, JobOpType.Scan, jobId) { Cts = cts };
            _jobs[jobId] = entry;

            // #9: Create SDK NetworkManagement ON the UI thread.
            NetworkManagement? nm;
            try
            {
                nm = Project.Root.CreateNetworkManagement(line);
                if (nm == null)
                {
                    throw new BusUnavailableException(
                        "No network management available. Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                          + "current interface.");
                }

            }
            catch
            {
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                cts.Dispose();
                throw;
            }

            // #9/#5: Initiate async SDK call on UI thread, await the Task off the dispatcher.
            // The call is guarded so a synchronous throw does not leak the bus.
            System.Threading.Tasks.Task<ushort[]> scanTask;
            try
            {
                scanTask = nm.IndividualAddressScanRangeAsync(
                    rangeStart, rangeEnd,
                    new Progress<int>(p => { entry.Percent = p; }),
                    cts.Token);
            }
            catch
            {
                nm.Dispose();
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                cts.Dispose();
                throw;
            }

            // Background continuation -- awaits the Task off the UI thread.
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var addresses = await scanTask;

                    var result = new ScanLineJobResult();
                    if (addresses != null)
                    {
                        foreach (var addr in addresses)
                            result.Addresses.Add(FormatIndividualAddress(addr));
                    }
                    entry.Result = result;
                    entry.State = "done";
                    entry.Percent = 100;
                }
                catch (OperationCanceledException)
                {
                    entry.State = "canceled";
                }
                catch (Exception ex)
                {
                    entry.Error = ex.Message;
                    entry.State = "failed";
                }
                finally
                {
                    nm.Dispose();
                    ReleaseBus(entry.BusOwner);
                    cts.Dispose();
                }
            });

            PruneTerminalJobs();
            return jobId;
        }
#endif

        /// <summary>
        /// Read device descriptor from a physical device on the bus.
        /// Verified: M:Knx.Ets.Sdk.Root.CreateDeviceManagement(
        ///   Knx.Ets.Sdk.Project.Device, System.Boolean) -> DeviceManagement
        ///   (SDK XML line 28363) - "Creates a new instance of the DeviceManagement interface."
        ///   connectionless=false for connection-oriented.
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.Connect() (SDK XML line 22672)
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.GetDeviceDescriptor()
        ///   -> DeviceDescriptor (SDK XML line 22704)
        ///   "Gets the device descriptor. Returns null if device is secured."
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.MaskVersion (SDK XML line 22466)
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.MaskVersionId (SDK XML line 22485)
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.Disconnect() (SDK XML line 22684)
        /// </summary>
        public DeviceReadInfoResult DeviceReadInfo(string deviceRef)
        {
            var device = FindDevice(deviceRef);
            // Fast pre-guard: if no KNXnet/IP connection is current, fail immediately
            // instead of calling CreateDeviceManagement, which would pop ETS's modal
            // "Select Connection" dialog and block the UI dispatcher. Same guard the
            // Root-level ops (device.program / device.reset) already use.
            if (!Project.Root.IsConnectionAvailable(device))
                throw new BusUnavailableException(
                    "No current KNXnet/IP connection. Select a KNXnet/IP interface in ETS "
                  + "(Bus > Interfaces), tick \"Use also for future connections\", then retry.");
            var busOwner = Guid.NewGuid().ToString("D");

            AcquireBus(busOwner);
            try
            {
                // connectionless=false for connection-oriented.
                // #9: Create SDK DeviceManagement on the UI thread (this runs on dispatcher).
                using (var dm = Project.Root.CreateDeviceManagement(device, false))
                {
                    if (dm == null)
                        throw new BusUnavailableException(
                            $"Cannot create device management for {deviceRef}. "
                          + "Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                          + "current interface.");

                    dm.Connect();
                    try
                    {
                        var desc = dm.GetDeviceDescriptor();
                        if (desc == null)
                        {
                            // Secured device -- descriptor not accessible without key.
                            return new DeviceReadInfoResult
                            {
                                MaskVersion = "",
                                MaskVersionId = "(secured)"
                            };
                        }

                        return new DeviceReadInfoResult
                        {
                            MaskVersion = $"0x{desc.MaskVersion:X4}",
                            MaskVersionId = desc.MaskVersionId ?? ""
                        };
                    }
                    finally
                    {
                        dm.Disconnect();
                    }
                }
            }
            finally
            {
                ReleaseBus(busOwner);
            }
        }

        /// <summary>
        /// Read a group address value from the bus (sync, 2.3 s default timeout).
        /// Verified: M:Knx.Ets.Sdk.Root.CreateSyncKnxGroupCommunication(Knx.Ets.Sdk.Project.Line)
        ///   -> KnxCommunicationSync (SDK XML line 28499)
        ///   "Provides a method for synchronous communication" / returns null if no connection.
        /// Verified: M:Knx.Ets.Sdk.Network.KnxCommunicationSync.GroupCommunicationReadSync(
        ///   System.Object, System.Int32) -> Byte[] (SDK XML line 23392)
        ///   The object address must contain a string or UInt16, not a project DOM object.
        /// </summary>
        public GroupReadResult GroupRead(string gaRef)
        {
            var ga = FindGroupAddress(gaRef);
            return WithGroupCommunication(comm =>
            {
                // routingCounter is unused; the SDK read timeout remains 2.3 seconds.
                var raw = comm.GroupCommunicationReadSync((ushort)ga.Address, 0);
                var value = raw as byte[];
                return new GroupReadResult
                {
                    Value = value != null && value.Length > 0 ? BytesToHex(value) : null
                };
            });
        }

        /// <summary>
        /// Write a group value to the bus.
        /// Verified: M:Knx.Ets.Sdk.Network.KnxCommunicationSync.GroupCommunicationWrite(
        ///   System.Object, System.Int32, System.Boolean, System.Object) -> void
        ///   (SDK XML line 1437) - sync write, no ACK status returned.
        /// Verified: P:Knx.Ets.Sdk.Project.GroupAddress.Address (SDK XML line 14307)
        ///   "Gets or sets the address value of the group address."
        /// #5: REMOVED WriteGroupValueAsync().GetAwaiter().GetResult() -- deadlocks on
        ///     the UI dispatcher. Using the synchronous GroupCommunicationWrite instead.
        ///     Trade-off: no ACK boolean; Acknowledged is always true on success (no
        ///     exception means the write was sent). Verified: GroupCommunicationWrite
        ///     throws OnlineManagementException on bus failure.
        /// </summary>
        public GroupWriteResult GroupWrite(string gaRef, string valueHex, bool less7Bits)
        {
            var ga = FindGroupAddress(gaRef);
            var valueBytes = HexToBytes(valueHex);
            return WithGroupCommunication(comm =>
            {
                comm.GroupCommunicationWrite((ushort)ga.Address, 0, less7Bits, (object)valueBytes);
                // This means the SDK send completed, not that an actuator applied the value.
                return new GroupWriteResult { Acknowledged = true };
            });
        }

        private TResult WithGroupCommunication<TResult>(Func<KnxCommunicationSync, TResult> action)
        {
            // Borrow only the active monitor's transport. Other bus jobs stay exclusive.
            return _groupMonitorConnection.Use(action, () =>
            {
                var busOwner = Guid.NewGuid().ToString("D");
                AcquireBus(busOwner);
                try
                {
                    using (var comm = Project.Root.CreateSyncKnxGroupCommunication(null))
                    {
                        if (comm == null)
                            throw new BusUnavailableException(
                                "No group communication available. Select a KNXnet/IP interface in ETS.");
                        return action(comm);
                    }
                }
                finally
                {
                    ReleaseBus(busOwner);
                }
            });
        }

#if ETS5
        public string StartGroupMonitor(string lineRef, int durationMs)
        {
            throw new NotSupportedOperationException(
                "group.monitor is not supported on ETS5 (KnxCommunicationSync.GroupMessageReceived and GroupMessageEventArgs do not exist in ETS5 SDK).");
        }
#else
        /// <summary>
        /// Monitor group telegrams on the bus for a bounded duration. Long-running -> job model.
        /// Verified: E:Knx.Ets.Sdk.Network.KnxCommunicationSync.GroupMessageReceived
        ///   (SDK XML line 1488) - fires on background thread for every group telegram.
        /// Verified: T:Knx.Ets.Sdk.Network.GroupMessageEventArgs (SDK XML line 97)
        ///   Properties: Service, SourceAddress, GroupAddress, HopCount, Priority, Less7Bits, Value.
        /// NOTE (6.3.0): GroupMessageReceived does NOT fire for KNX Secure telegrams.
        ///
        /// #9: Create KnxCommunicationSync ON the UI thread. Await the delay off-thread.
        /// #8: Register job before starting.
        /// </summary>
        public string StartGroupMonitor(string lineRef, int durationMs)
        {
            // Validate lineRef (may be "default" for null line).
            Line? line = null;
            if (!string.IsNullOrEmpty(lineRef) && lineRef != "default")
                line = FindLine(lineRef);

            if (durationMs < 1000 || durationMs > 300000)
                throw new ArgumentException(
                    "durationMs must be between 1000 and 300000 (1 s to 5 min).");

            var jobId = Guid.NewGuid().ToString("D");
            AcquireBus(jobId);

            // #8: Register job BEFORE starting.
            var cts = new System.Threading.CancellationTokenSource();
            var entry = new JobEntry(jobId, lineRef, null, JobOpType.Monitor, jobId) { Cts = cts };
            _jobs[jobId] = entry;

            var telegrams = new List<GroupMonitorTelegram>();
            bool accepting = true;
            EventHandler<GroupMessageEventArgs> handler = (sender, e) =>
            {
                var telegram = new GroupMonitorTelegram
                {
                    Service = e.Service.ToString(),
                    SourceAddress = FormatIndividualAddress(e.SourceAddress),
                    GroupAddress = FormatGroupAddress3Level(e.GroupAddress),
                    Value = e.Value is byte[] valBytes ? BytesToHex(valBytes) : null,
                    Timestamp = DateTime.UtcNow.ToString("o")
                };
                lock (telegrams)
                {
                    if (accepting) telegrams.Add(telegram);
                }
            };

            // Subscribe before returning the job, so callers cannot outrun capture setup.
            KnxCommunicationSync? comm;
            try
            {
                comm = Project.Root.CreateSyncKnxGroupCommunication(line);
                if (comm == null)
                {
                    throw new BusUnavailableException(
                        "No group communication available. Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                          + "current interface.");
                }
                try
                {
                    comm.GroupMessageReceived += handler;
                    _groupMonitorConnection.Attach(jobId, comm);
                }
                catch
                {
                    comm.GroupMessageReceived -= handler;
                    comm.Dispose();
                    throw;
                }
            }
            catch
            {
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                cts.Dispose();
                throw;
            }

            // #5/#9: Background continuation; delay is awaited off the UI thread.
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        // Wait for the specified duration or until canceled.
                        await System.Threading.Tasks.Task.Delay(durationMs, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal cancel path.
                    }
                    finally
                    {
                        _groupMonitorConnection.Close(jobId, connection =>
                        {
                            lock (telegrams) accepting = false;
                            connection.GroupMessageReceived -= handler;
                        });
                    }

                    List<GroupMonitorTelegram> snapshot;
                    lock (telegrams)
                    {
                        snapshot = new List<GroupMonitorTelegram>(telegrams);
                    }

                    entry.Result = new GroupMonitorJobResult { Telegrams = snapshot };
                    entry.State = cts.IsCancellationRequested ? "canceled" : "done";
                    entry.Percent = 100;
                }
                catch (Exception ex)
                {
                    entry.Error = ex.Message;
                    entry.State = "failed";
                }
                finally
                {
                    ReleaseBus(entry.BusOwner);
                    cts.Dispose();
                }
            });

            PruneTerminalJobs();
            return jobId;
        }
#endif

        /// <summary>
        /// Unload a device (remove application program or full unload including address).
        /// Verified: M:Knx.Ets.Sdk.Root.StartUnload(Knx.Ets.Sdk.Project.Device, System.Boolean)
        ///   -> bool (SDK XML line 28913)
        ///   "Starts unloading of the specified device."
        ///   removeAddress parameter: true = full unload (app + address), false = app only.
        ///   Progress via OnOnlineOperationsEvent (same as StartDownload).
        /// </summary>
        // ---------------------------------------------------------------
        // NEW METHOD: device.compare (limited, READ, bus)
        // ---------------------------------------------------------------

        /// <summary>
        /// Perform a deliberately LIMITED compare of a device against its physical counterpart
        /// using DeviceManagement.GetDeviceDescriptor() for basic device identity checks.
        ///
        /// This is READ-only (no UndoManager, no revision bump) but uses the bus lease.
        /// Returns a structured result with compared properties and a partial flag.
        ///
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.GetDeviceDescriptor()
        ///   -> DeviceDescriptor (SDK XML line 651)
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.MaskVersion (SDK XML line 244)
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.MaskVersionId (SDK XML line 250)
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.BigAssociationTableFormat (SDK XML line 270)
        /// Verified: P:Knx.Ets.Sdk.Product.ApplicationProgram.MaskVersion (SDK XML line 3655)
        ///   -- returns the project-side expected mask version string.
        /// </summary>
        public DeviceCompareResult DeviceCompare(string deviceRef)
        {
            var device = FindDevice(deviceRef);
            // Fast pre-guard: if no bus connection is current, fail immediately
            // instead of calling CreateDeviceManagement, which would pop ETS's modal
            // "Select Connection" dialog and block the UI dispatcher. Same guard the
            // Root-level ops (device.program / device.reset) already use.
            if (!Project.Root.IsConnectionAvailable(device))
                throw new BusUnavailableException(
                    "No current bus connection. Select a bus interface in ETS "
                  + "(Bus > Interfaces), tick \"Use also for future connections\", then retry.");
            var busOwner = Guid.NewGuid().ToString("D");

            AcquireBus(busOwner);
            try
            {
                // #9: Create DeviceManagement on the UI thread.
                using (var dm = Project.Root.CreateDeviceManagement(device, false))
                {
                    if (dm == null)
                        throw new BusUnavailableException(
                            $"Cannot create device management for {deviceRef}. "
                          + "Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                          + "current interface.");

                    dm.Connect();
                    try
                    {
                        var compared = new List<DeviceComparePropertyResult>();

                        var desc = dm.GetDeviceDescriptor();
                        if (desc == null)
                        {
                            // Secured device -- cannot read descriptor.
                            return new DeviceCompareResult
                            {
                                Compared = compared,
                                Partial = true,
                                Note = "Device is secured -- descriptor not accessible without key."
                            };
                        }

                        // Compare mask version, project vs physical device. The two sides use
                        // different notations for the SAME value (project "7.5" == device
                        // MaskVersionId "MV-0705" == 0x0705), so normalize both to (major,minor)
                        // before comparing -- otherwise identical devices reported as unequal (#c).
                        static (int, int)? PM(string? s)
                        {
                            if (string.IsNullOrWhiteSpace(s)) return null;
                            s = s!.Trim();
                            if (s.StartsWith("MV-", StringComparison.OrdinalIgnoreCase)) s = s.Substring(3);
                            else if (s.StartsWith("MV", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                            if (s.Contains("."))
                            {
                                var p = s.Split('.');
                                return (p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b))
                                    ? (a, b) : ((int, int)?)null;
                            }
                            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)
                                ? ((v >> 8) & 0xFF, v & 0xFF) : ((int, int)?)null;
                        }

                        // Verified: P:Knx.Ets.Sdk.Project.Device.Hardware2Program ->
                        //   ApplicationProgram.MaskVersion (project's expected mask).
                        string? expectedMask = null;
                        try { expectedMask = device.Hardware2Program?.ApplicationProgram?.MaskVersion?.ToString(); }
                        catch { /* best-effort */ }
                        string actualMaskId = desc.MaskVersionId ?? "";
                        var pmE = PM(expectedMask);
                        var pmA = PM(actualMaskId);
                        compared.Add(new DeviceComparePropertyResult
                        {
                            Property = "maskVersion",
                            Equal = pmE != null && pmA != null && pmE.Value == pmA.Value,
                            Expected = expectedMask,   // e.g. "7.5"
                            Actual = actualMaskId       // e.g. "MV-0705"
                        });

                        return new DeviceCompareResult
                        {
                            Compared = compared,
                            Partial = true,
                            Note = "Limited compare -- not a full ETS device compare. "
                                 + "Only basic device identity (mask version) is checked."
                        };
                    }
                    finally
                    {
                        dm.Disconnect();
                    }
                }
            }
            finally
            {
                ReleaseBus(busOwner);
            }
        }

        // ---------------------------------------------------------------
        // bridge.info (READ, no bus)
        // ---------------------------------------------------------------

        /// <summary>
        /// Health/version endpoint. Returns bridge info without requiring a bus connection.
        /// Verified: typeof(Knx.Ets.Sdk.Root).Assembly.GetName().Version -- standard .NET
        ///   reflection; returns the runtime-loaded SDK assembly version.
        /// Verified: typeof(EtsBridgeAddIn).Assembly.GetName().Version -- the AddIn's own
        ///   assembly version from the [AddIn] attribute / AssemblyVersion.
        /// Verified: P:Knx.Ets.Sdk.Project.Project.Name (SDK XML line 16836)
        /// Verified: P:Knx.Ets.Sdk.Project.Project.ProjectGuid (SDK XML line 16836)
        /// </summary>
        public BridgeInfo GetBridgeInfo()
        {
            var addinVersion = typeof(EtsBridgeAddIn).Assembly.GetName().Version?.ToString() ?? "unknown";
            // Runtime-loaded SDK = the host ETS's version.
            var sdkVersion = typeof(Knx.Ets.Sdk.Root).Assembly.GetName().Version?.ToString() ?? "unknown";
            // Compile-time SDK = the Knx.Ets.Sdk version this AddIn references. Differs from
            // sdkVersion when a 6.4-built AddIn runs on ETS 6.3 (bound via AssemblyResolve).
            var builtAgainstSdk = "unknown";
            foreach (var r in typeof(EtsBridgeAddIn).Assembly.GetReferencedAssemblies())
            {
                if (string.Equals(r.Name, "Knx.Ets.Sdk", StringComparison.Ordinal))
                {
                    builtAgainstSdk = r.Version?.ToString() ?? "unknown";
                    break;
                }
            }

            return new BridgeInfo
            {
                AddinVersion = addinVersion,
                SdkVersion = sdkVersion,
                BuiltAgainstSdk = builtAgainstSdk,
                ProjectName = Project.Name ?? "",
#if ETS5
                ProjectId = Project.ProjectId.ToString(),
#else
                ProjectId = Project.ProjectGuid.ToString(),
#endif
                ProjectRevision = ProjectRevision,
                KnxIpOnly = true // Kept for backward compatibility in bridge.info DTO
            };
        }

#if ETS5
        public string StartReconstructLine(string lineRef)
        {
            throw new NotSupportedOperationException(
                "bus.reconstructLine is not supported on ETS5 (NetworkManagement.IndividualAddressScanRangeAsync does not exist in ETS5 SDK).");
        }
#else
        // ---------------------------------------------------------------
        // bus.reconstructLine (READ, JOB)
        // Device inventory for recovery: scan + per-device identity read.
        // ---------------------------------------------------------------

        /// <summary>
        /// Scan a line for present devices, then for each found address best-effort
        /// read device identity via DeviceManagement (mask version, serial, manufacturer).
        ///
        /// Reuses the existing bus-lease + job model + KNXnet/IP-guard patterns from
        /// StartScanLine. The scan uses NetworkManagement.IndividualAddressScanRangeAsync
        /// (same path as StartScanLine), then iterates found addresses with
        /// DeviceManagement per-device reads.
        ///
        /// Verified: M:Knx.Ets.Sdk.Network.NetworkManagement.IndividualAddressScanRangeAsync(
        ///   System.UInt16, System.UInt16, System.IProgress{System.Int32},
        ///   System.Threading.CancellationToken) -> Task{UInt16[]} (SDK XML line 1667)
        /// Verified: M:Knx.Ets.Sdk.Root.CreateDeviceManagement(System.UInt16,System.UInt32,System.Boolean)
        ///   -> DeviceManagement (SDK XML line 28585)
        ///   "Creates a DeviceManagement object for the specified individual address."
        ///   accessKey=0 (no BCU key), connectionless=false.
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.Connect() (SDK XML line 22672)
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.GetDeviceDescriptor()
        ///   -> DeviceDescriptor (SDK XML line 651)
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.MaskVersion (SDK XML line 244)
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.ReadProperty(System.Byte,System.Byte,
        ///   Knx.Ets.Common.Types.Enumerations.PropType,System.UInt16,System.UInt16,System.UInt32)
        ///   -> byte[] (SDK XML line 792)
        ///   objectIndex=0 (Device Object), propertyId: PID_SERIAL_NUMBER (11), PID_MANUFACTURER_ID (12)
        ///   propType=PDT_GENERIC_06 (serial) / PDT_UNSIGNED_INT (mfr ID), startElement=1, count=1, size=0
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.Disconnect() (SDK XML line 22684)
        ///
        /// </summary>
        public string StartReconstructLine(string lineRef)
        {
            var line = FindLine(lineRef);
            ushort areaAddr = (ushort)line.Area.Address;
            ushort lineAddr = (ushort)line.Address;
            ushort rangeStart = (ushort)((areaAddr << 12) | (lineAddr << 8) | 0);
            ushort rangeEnd = (ushort)((areaAddr << 12) | (lineAddr << 8) | 255);

            var jobId = Guid.NewGuid().ToString("D");
            AcquireBus(jobId);

            var cts = new System.Threading.CancellationTokenSource();
            var entry = new JobEntry(jobId, lineRef, null, JobOpType.Scan, jobId) { Cts = cts };
            _jobs[jobId] = entry;

            // Create SDK NetworkManagement ON the UI thread.
            NetworkManagement? nm;
            try
            {
                nm = Project.Root.CreateNetworkManagement(line);
                if (nm == null)
                    throw new BusUnavailableException(
                        "No network management available. Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                          + "current interface.");

            }
            catch
            {
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                cts.Dispose();
                throw;
            }

            // Capture Root for use in dispatcher.Invoke inside the background task (#4).
            var root = Project.Root;

            // Initiate async scan on UI thread.
            System.Threading.Tasks.Task<ushort[]> scanTask;
            try
            {
                scanTask = nm.IndividualAddressScanRangeAsync(
                    rangeStart, rangeEnd,
                    new Progress<int>(p => { entry.Percent = Math.Min(p / 2, 50); }), // scan = first 50%
                    cts.Token);
            }
            catch
            {
                nm.Dispose();
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                cts.Dispose();
                throw;
            }

            // Background continuation -- scan, then per-device identity reads.
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var addresses = await scanTask;
                    nm.Dispose(); // Done with NetworkManagement.

                    var result = new ReconstructLineJobResult
                    {
                        ScannedRange = $"{FormatIndividualAddress(rangeStart)}-{FormatIndividualAddress(rangeEnd)}"
                    };

                    if (addresses == null || addresses.Length == 0)
                    {
                        entry.Result = result;
                        entry.State = "done";
                        entry.Percent = 100;
                        return;
                    }

                    int deviceIndex = 0;
                    foreach (var addr in addresses)
                    {
                        if (cts.Token.IsCancellationRequested) break;

                        var devInfo = new ReconstructLineDeviceInfo
                        {
                            Address = FormatIndividualAddress(addr)
                        };

                        // Best-effort per-device identity read.
                        try
                        {
                            // #4: Create DeviceManagement ON the UI thread (SDK requirement),
                            // then do bus I/O off the dispatcher.
                            // Verified: M:Knx.Ets.Sdk.Root.CreateDeviceManagement(System.UInt16,System.UInt32,System.Boolean)
                            //   (SDK XML line 28585)
                            var dm = _uiDispatcher.Invoke(() =>
                                root.CreateDeviceManagement(addr, 0, false));
                            using (dm)
                            {
                                if (dm != null)
                                {
                                    dm.Connect();
                                    try
                                    {
                                        // Read device descriptor for mask version.
                                        var desc = dm.GetDeviceDescriptor();
                                        if (desc != null)
                                        {
                                            devInfo.MaskVersion = $"0x{desc.MaskVersion:X4}";
                                        }

                                        // Best-effort: read serial number from Device Object (objectIndex=0).
                                        // Verified: P:Knx.Ets.Common.Types.InterfaceObjectPropertyIds.PID_SERIAL_NUMBER
                                        //   (Common.Types XML line 4678) -- KNX PID 11, 6 bytes.
                                        // Confirmed by hm-knx: TryReadStdProp(dm, 0, 11, 6)
                                        try
                                        {
                                            var serialBytes = dm.ReadProperty(
                                                0, // objectIndex: Device Object
                                                Knx.Ets.Common.Types.InterfaceObjectPropertyIds.PID_SERIAL_NUMBER,
                                                Knx.Ets.Common.Types.Enumerations.PropType.PDT_GENERIC_06,
                                                1, // startElement
                                                1, // count (1 element of 6 bytes)
                                                0  // size (unused compat param)
                                            );
                                            if (serialBytes != null && serialBytes.Length > 0)
                                                devInfo.SerialNumber = BytesToHex(serialBytes);
                                        }
                                        catch { /* PID_SERIAL_NUMBER not supported on all devices */ }

                                        // Best-effort: read manufacturer ID from Device Object (objectIndex=0).
                                        // Verified: P:Knx.Ets.Common.Types.InterfaceObjectPropertyIds.PID_MANUFACTURER_ID
                                        //   (Common.Types XML line 4684) -- KNX PID 12, 2 bytes.
                                        // BUG FIX (#3): was PID 4 (= PID_GROUP_OBJECT_REFERENCE), corrected to PID 12.
                                        // Confirmed by hm-knx: TryReadStdProp(dm, 0, 12, 2)
                                        try
                                        {
                                            var mfrBytes = dm.ReadProperty(
                                                0, // objectIndex: Device Object
                                                Knx.Ets.Common.Types.InterfaceObjectPropertyIds.PID_MANUFACTURER_ID,
                                                Knx.Ets.Common.Types.Enumerations.PropType.PDT_UNSIGNED_INT,
                                                1, // startElement
                                                1, // count (1 element of 2 bytes)
                                                0  // size (unused compat param)
                                            );
                                            if (mfrBytes != null && mfrBytes.Length >= 2)
                                            {
                                                devInfo.ManufacturerId = (uint)((mfrBytes[0] << 8) | mfrBytes[1]);
                                            }
                                        }
                                        catch { /* PID_MANUFACTURER_ID not supported on all devices */ }
                                    }
                                    finally
                                    {
                                        dm.Disconnect();
                                    }
                                }
                                else
                                {
                                    devInfo.Error = "CreateDeviceManagement returned null (no bus connection)";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            devInfo.Error = ex.Message;
                        }

                        result.Devices.Add(devInfo);
                        deviceIndex++;
                        entry.Percent = 50 + (int)(50.0 * deviceIndex / addresses.Length);
                    }

                    entry.Result = result;
                    entry.State = cts.IsCancellationRequested ? "canceled" : "done";
                    entry.Percent = 100;
                }
                catch (OperationCanceledException)
                {
                    nm.Dispose();
                    entry.State = "canceled";
                }
                catch (Exception ex)
                {
                    nm.Dispose();
                    entry.Error = ex.Message;
                    entry.State = "failed";
                }
                finally
                {
                    ReleaseBus(entry.BusOwner);
                    cts.Dispose();
                }
            });

            PruneTerminalJobs();
            return jobId;
        }
#endif

        // ---------------------------------------------------------------
        // device.readGroupObjects (READ)
        // Read group-object association tables from a physical device.
        // Two modes: sync (default) and async/job (opt-in via IPC).
        //
        // ReadGroupObjectsCore  -- shared read logic (Connect/Read/Disconnect)
        // ReadDeviceGroupObjects -- sync entry point (direct return)
        // StartReadDeviceGroupObjects -- async entry point (returns jobId)
        // ---------------------------------------------------------------

        /// <summary>
        /// Pure read logic for device group-object/association tables.
        /// Assumes <paramref name="dm"/> is already created but NOT connected.
        /// Connects, reads all three tables, disconnects, returns the result.
        /// Does NOT dispose <paramref name="dm"/> -- callers handle disposal.
        /// Does NOT touch any JobEntry fields.
        ///
        /// Uses LocateObject to resolve the RUNTIME object index for each
        /// interface object type, instead of assuming fixed indices.
        ///
        /// Reads three tables:
        ///   1. Association Table (OT_Associationtable): maps GA-index to CO-number.
        ///   2. Address Table (OT_Addresstable): maps GA-index to actual group address.
        ///   3. Group Object Table (OT_GroupObjectTable): CO descriptor bytes with flags.
        /// Combines them to produce real group addresses ("x/y/z") per CO and structured
        /// flags {c,r,w,t,u} where possible.
        ///
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.LocateObject(System.UInt16,System.Byte)
        ///   -> byte (SDK XML line 988) "Locates an interface object by object type and occurrence."
        /// Verified: P:Knx.Ets.Common.Types.InterfaceObjectTypes.OT_Associationtable (Common.Types XML line 5260)
        /// Verified: P:Knx.Ets.Common.Types.InterfaceObjectTypes.OT_Addresstable (Common.Types XML line 5254)
        /// Verified: P:Knx.Ets.Common.Types.InterfaceObjectTypes.OT_GroupObjectTable (Common.Types XML line 5302)
        /// Verified: P:Knx.Ets.Common.Types.InterfaceObjectPropertyIds.PID_TABLE (Common.Types XML line 5092)
        /// Verified: M:Knx.Ets.Sdk.Network.DeviceManagement.ReadProperty(System.Byte,System.Byte,
        ///   PropType,System.UInt16,System.UInt16,System.UInt32) (SDK XML line 792)
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.MaskVersion (SDK XML line 244)
        /// Verified: P:Knx.Ets.Sdk.Network.DeviceDescriptor.BigAssociationTableFormat (SDK XML line 270)
        /// </summary>
        private DeviceGroupObjectsResult ReadGroupObjectsCore(
            DeviceManagement dm, string address, IProgress<int>? progress = null)
        {
            var result = new DeviceGroupObjectsResult
            {
                Address = address,
                Partial = true // always partial for generic bus readout
            };

            dm.Connect();
            try
            {
                // Step 1: Read device descriptor for mask version.
                var desc = dm.GetDeviceDescriptor();
                if (desc == null)
                {
                    result.Note = "Device is secured -- descriptor not accessible without key.";
                    return result;
                }

                var maskVersion = desc.MaskVersion;
                bool bigAssocTable = desc.BigAssociationTableFormat;
                progress?.Report(10);

                var notes = new List<string>();
                notes.Add($"mask=0x{maskVersion:X4}, bigAssocTable={bigAssocTable}");

                // -- SDK constants for interface object types and property IDs --
                // Verified: these are static properties returning ushort/byte values.
                var otAssocTable = Knx.Ets.Common.Types.InterfaceObjectTypes.OT_Associationtable;
                var otAddrTable = Knx.Ets.Common.Types.InterfaceObjectTypes.OT_Addresstable;
                var otGroupObjTable = Knx.Ets.Common.Types.InterfaceObjectTypes.OT_GroupObjectTable;
                var pidTable = Knx.Ets.Common.Types.InterfaceObjectPropertyIds.PID_TABLE;

                // Step 2: LocateObject for Association Table.
                // Use LocateObject to find the RUNTIME object index, not a hardcoded value.
                byte assocObjIdx;
                try
                {
                    assocObjIdx = dm.LocateObject(otAssocTable, 0);
                    notes.Add($"assocTable: objIdx={assocObjIdx} (via LocateObject)");
                }
                catch (Exception ex)
                {
                    notes.Add($"assocTable: LocateObject failed ({ex.Message})");
                    result.Note = string.Join("; ", notes);
                    return result;
                }
                progress?.Report(20);

                // Step 3: Read Association Table entry count (startElement=0).
                // PID_TABLE on the Association Table object.
                byte[]? assocHeader;
                try
                {
                    assocHeader = dm.ReadProperty(
                        assocObjIdx, pidTable,
                        PropType.PDT_UNSIGNED_INT,
                        0, 1, 0);
                }
                catch (Exception ex)
                {
                    notes.Add($"assocTable: ReadProperty(count) failed ({ex.Message})");
                    result.Note = string.Join("; ", notes);
                    return result;
                }

                if (assocHeader == null || assocHeader.Length < 2)
                {
                    notes.Add("assocTable: no header data");
                    result.Note = string.Join("; ", notes);
                    return result;
                }

                int assocEntryCount = (assocHeader[0] << 8) | assocHeader[1];
                if (assocEntryCount <= 0 || assocEntryCount >= 4096)
                {
                    notes.Add($"assocTable: unexpected entry count {assocEntryCount}");
                    result.Note = string.Join("; ", notes);
                    return result;
                }

                // Step 4: Read all association table entries.
                int entrySize = bigAssocTable ? 4 : 2;
                var entryPropType = bigAssocTable
                    ? PropType.PDT_GENERIC_04
                    : PropType.PDT_GENERIC_02;

                byte[]? assocEntries;
                try
                {
                    assocEntries = dm.ReadProperty(
                        assocObjIdx, pidTable,
                        entryPropType,
                        1, (ushort)assocEntryCount, 0);
                }
                catch (Exception ex)
                {
                    notes.Add($"assocTable: ReadProperty(entries) failed ({ex.Message})");
                    result.Note = string.Join("; ", notes);
                    return result;
                }

                if (assocEntries == null || assocEntries.Length == 0)
                {
                    notes.Add("assocTable: empty entry data");
                    result.Note = string.Join("; ", notes);
                    return result;
                }
                progress?.Report(40);

                // Parse association table: (gaIndex, coNumber) pairs.
                var assocPairs = new List<(uint gaIndex, uint coNumber)>();
                {
                    int off = 0;
                    for (int i = 0; i < assocEntryCount && off + entrySize <= assocEntries.Length; i++)
                    {
                        uint gaIdx, coNum;
                        if (bigAssocTable)
                        {
                            gaIdx = (uint)((assocEntries[off] << 8) | assocEntries[off + 1]);
                            coNum = (uint)((assocEntries[off + 2] << 8) | assocEntries[off + 3]);
                        }
                        else
                        {
                            gaIdx = assocEntries[off];
                            coNum = assocEntries[off + 1];
                        }
                        off += entrySize;
                        assocPairs.Add((gaIdx, coNum));
                    }
                }
                notes.Add($"assocTable: {assocPairs.Count} entries (entrySize={entrySize})");

                // Step 5: Read Address Table to resolve GA indices -> real group addresses.
                // Resolves "ga-index:*" to actual "x/y/z" addresses.
                var gaIndexToAddress = new Dictionary<uint, string>();
                try
                {
                    byte addrObjIdx = dm.LocateObject(otAddrTable, 0);

                    // Read count (startElement=0).
                    var addrHeader = dm.ReadProperty(
                        addrObjIdx, pidTable,
                        PropType.PDT_UNSIGNED_INT,
                        0, 1, 0);

                    if (addrHeader != null && addrHeader.Length >= 2)
                    {
                        int addrEntryCount = (addrHeader[0] << 8) | addrHeader[1];
                        // Address table: entry 0 = device's own address, entries 1..N = group addresses.
                        // Each entry is 2 bytes (16-bit group address).
                        if (addrEntryCount > 0 && addrEntryCount < 4096)
                        {
                            var addrEntries = dm.ReadProperty(
                                addrObjIdx, pidTable,
                                PropType.PDT_GENERIC_02,
                                1, (ushort)addrEntryCount, 0);

                            if (addrEntries != null)
                            {
                                // Entry at index 0 in the address table is the device's own IA.
                                // Entries at indices 1..N are group addresses.
                                // The assoc table's gaIndex refers to 1-based positions in the
                                // address table (index 0 = own address is not a GA).
                                // We read starting from element 1 (startElement=1), so
                                // addrEntries[0..1] = table entry 1, etc.
                                // So array index i corresponds to table entry (i+1),
                                // which is gaIndex (i+1).
                                for (int i = 0; i < addrEntryCount && (i * 2 + 1) < addrEntries.Length; i++)
                                {
                                    uint rawGa = (uint)((addrEntries[i * 2] << 8) | addrEntries[i * 2 + 1]);
                                    // Format as 3-level: main/middle/sub
                                    uint main = (rawGa >> 11) & 0x1F;
                                    uint middle = (rawGa >> 8) & 0x07;
                                    uint sub = rawGa & 0xFF;
                                    gaIndexToAddress[(uint)(i + 1)] = $"{main}/{middle}/{sub}";
                                }
                                notes.Add($"addrTable: {gaIndexToAddress.Count} GAs resolved");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"addrTable: failed ({ex.Message})");
                }
                progress?.Report(60);

                // Step 6: Best-effort read Group Object Table for CO descriptor flags.
                // Each GO table entry contains configuration bytes including flags.
                // KNX System 7 format (3 bytes per entry): [pointer_hi, pointer_lo, config]
                // Config byte: bit7=transmit, bit6=valueReadOnInit, bit5=writeEnable,
                //              bit4=readEnable, bit3=communication, bit2=update priority,
                //              bit1..0=priority (unused here).
                // System B "small" format (2 bytes per entry): [config, type]
                // This is mask-dependent; we attempt the read and decode what we can.
                var coFlags = new Dictionary<uint, BusComObjectFlags>();
                try
                {
                    byte goObjIdx = dm.LocateObject(otGroupObjTable, 0);

                    // Read count (startElement=0).
                    var goHeader = dm.ReadProperty(
                        goObjIdx, pidTable,
                        PropType.PDT_UNSIGNED_INT,
                        0, 1, 0);

                    if (goHeader != null && goHeader.Length >= 2)
                    {
                        int goEntryCount = (goHeader[0] << 8) | goHeader[1];
                        if (goEntryCount > 0 && goEntryCount < 4096)
                        {
                            // Read entries as raw bytes. Try PDT_GENERIC_03 (3 bytes per entry,
                            // System 7 format). If that fails, try PDT_GENERIC_04 (4 bytes, cEMI).
                            byte[]? goEntries = null;
                            int goEntrySize = 3;
                            try
                            {
                                goEntries = dm.ReadProperty(
                                    goObjIdx, pidTable,
                                    PropType.PDT_GENERIC_03,
                                    1, (ushort)goEntryCount, 0);
                            }
                            catch
                            {
                                // Try 4-byte entries (cEMI server format).
                                try
                                {
                                    goEntries = dm.ReadProperty(
                                        goObjIdx, pidTable,
                                        PropType.PDT_GENERIC_04,
                                        1, (ushort)goEntryCount, 0);
                                    goEntrySize = 4;
                                }
                                catch { /* not readable with either format */ }
                            }

                            if (goEntries != null && goEntries.Length > 0)
                            {
                                // Decode flags from the last byte of each entry (config byte).
                                // Bit layout (KNX System 7, 3-byte entries):
                                //   byte[2] config: bit7=T, bit6=I(readOnInit), bit5=W, bit4=R, bit3=C
                                // The exact bit layout varies, but this is the most common one.
                                for (int i = 0; i < goEntryCount && (i + 1) * goEntrySize <= goEntries.Length; i++)
                                {
                                    byte config = goEntries[i * goEntrySize + goEntrySize - 1];
                                    // CO number is 0-based (GO table entry 0 = CO 0).
                                    coFlags[(uint)i] = new BusComObjectFlags
                                    {
                                        C = (config & 0x08) != 0,
                                        R = (config & 0x10) != 0,
                                        W = (config & 0x20) != 0,
                                        T = (config & 0x80) != 0,
                                        U = (config & 0x40) != 0  // readOnInit / update
                                    };
                                }
                                notes.Add($"goTable: {coFlags.Count} entries (entrySize={goEntrySize})");
                            }
                            else
                            {
                                notes.Add("goTable: empty entry data");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"goTable: failed ({ex.Message})");
                }
                progress?.Report(80);

                // Step 7: Combine association table, address table, and GO table.
                var coMap = new Dictionary<uint, BusComObjectEntry>();
                foreach (var (gaIdx, coNum) in assocPairs)
                {
                    if (!coMap.TryGetValue(coNum, out var coEntry))
                    {
                        coEntry = new BusComObjectEntry { Number = coNum };
                        // Attach flags if available from the GO table.
                        if (coFlags.TryGetValue(coNum, out var flags))
                            coEntry.Flags = flags;
                        coMap[coNum] = coEntry;
                    }
                    // Resolve GA-index to actual group address.
                    if (gaIndexToAddress.TryGetValue(gaIdx, out var gaAddr))
                        coEntry.GroupAddresses.Add(gaAddr);
                    else
                        coEntry.GroupAddresses.Add($"ga-index:{gaIdx}(unresolved)");
                }

                result.ComObjects = new List<BusComObjectEntry>(coMap.Values);
                result.Note = string.Join("; ", notes);
                return result;
            }
            finally
            {
                dm.Disconnect();
            }
        }

        /// <summary>
        /// Synchronous read of a physical device's group-object/association tables.
        /// Blocks the dispatcher for the duration of bus reads. For non-blocking mode,
        /// use StartReadDeviceGroupObjects (job-based, polled via job.status).
        /// </summary>
        public DeviceGroupObjectsResult ReadDeviceGroupObjects(string address)
        {
            var ia = ParseIndividualAddressString(address);
            var busOwner = Guid.NewGuid().ToString("D");

            AcquireBus(busOwner);
            try
            {
                // Create DeviceManagement on the UI thread (this runs on dispatcher).
                var dm = Project.Root.CreateDeviceManagement(ia, 0, false);
                if (dm == null)
                    throw new BusUnavailableException(
                        $"Cannot create device management for address {address}. "
                      + "Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                      + "current interface.");

                using (dm)
                {
                    return ReadGroupObjectsCore(dm, address);
                }
            }
            finally
            {
                ReleaseBus(busOwner);
            }
        }

        /// <summary>
        /// Asynchronous job-based read of a physical device's group-object/association
        /// tables via DeviceManagement property reads. Returns a jobId; result is a
        /// DeviceGroupObjectsResult available via job.status.
        ///
        /// Verified: M:Knx.Ets.Sdk.Root.CreateDeviceManagement(System.UInt16,System.UInt32,System.Boolean)
        ///   (SDK XML line 28585)
        ///
        /// </summary>
        public string StartReadDeviceGroupObjects(string address)
        {
            var ia = ParseIndividualAddressString(address);

            var jobId = Guid.NewGuid().ToString("D");
            AcquireBus(jobId);

            var cts = new System.Threading.CancellationTokenSource();
            var entry = new JobEntry(jobId, $"addr:{address}", null, JobOpType.Scan, jobId) { Cts = cts };
            _jobs[jobId] = entry;

            // Create DeviceManagement ON the UI thread, then do bus I/O off the dispatcher.
            DeviceManagement? dm;
            try
            {
                dm = Project.Root.CreateDeviceManagement(ia, 0, false);
                if (dm == null)
                    throw new BusUnavailableException(
                        $"Cannot create device management for address {address}. "
                      + "Select a KNXnet/IP interface in ETS (Bus > Interfaces) and set it as the "
                      + "current interface.");
            }
            catch
            {
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                cts.Dispose();
                throw;
            }

            // Background task for bus I/O -- does NOT run on the UI thread.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var progress = new Progress<int>(p => { entry.Percent = p; });
                    var taskResult = ReadGroupObjectsCore(dm, address, progress);
                    entry.Result = taskResult;
                    entry.State = "done";
                    entry.Percent = 100;
                }
                catch (OperationCanceledException)
                {
                    dm.Dispose();
                    entry.State = "canceled";
                }
                catch (Exception ex)
                {
                    dm.Dispose();
                    entry.Error = ex.Message;
                    entry.State = "failed";
                }
                finally
                {
                    ReleaseBus(entry.BusOwner);
                    cts.Dispose();
                }
            });

            PruneTerminalJobs();
            return jobId;
        }

        /// <summary>
        /// #7: Validate before acquiring bus. #8: Register job before SDK op.
        /// </summary>
        public string StartDeviceUnload(string deviceRef, bool fullUnload)
        {
            var device = FindDevice(deviceRef);

            if (!Project.Root.IsConnectionAvailable(device))
                throw new BusUnavailableException(
                    $"No bus connection available for device {deviceRef}. "
                  + "Ensure a bus interface is selected in ETS.");

            var jobId = Guid.NewGuid().ToString("D");
            AcquireBus(jobId);

            // #8: Register job BEFORE starting the SDK operation.
            var entry = new JobEntry(jobId, deviceRef, device, JobOpType.Unload, jobId);
            _jobs[jobId] = entry;

            try
            {
                EnsureEventSubscription();

                // Verified: StartUnload(Device, bool removeAddress) (SDK XML line 28913)
                bool started = Project.Root.StartUnload(device, fullUnload);

                if (!started)
                {
                    throw new BusUnavailableException(
                        $"StartUnload returned false for device {deviceRef}. "
                      + "The bus connection may be unavailable.");
                }
            }
            catch
            {
                // #7/#8: Startup failure: remove job and release bus.
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                throw;
            }

            PruneTerminalJobs();
            return jobId;
        }

        // ---------------------------------------------------------------
        // bus.setIndividualAddress (WRITE, JOB)
        // Program a device's individual address by overwriting its
        // current bus address, without pressing the programming button.
        // ---------------------------------------------------------------

        /// <summary>
        /// Program a device's project individual address onto the physical device by
        /// overwriting its known current bus address. Avoids pressing the programming button.
        ///
        /// Verified: M:Knx.Ets.Sdk.Root.StartOverwritingIndividualAddress(
        ///   Knx.Ets.Sdk.Project.Device, System.UInt16) -> bool (SDK XML line 28694)
        ///   "Starts assigning the individual address to a KNX device by overwriting a
        ///    known previous address. This function avoids having to physically press the
        ///    programming button at the device."
        ///   device: "The device for which a download shall be started."
        ///   currentAddress: "The current individual address of the device to be overwritten.
        ///     The new address will be the one currently entered in the individual address
        ///     field of the selected ETS device."
        ///   Returns true if the download can be performed.
        ///   Progress via E:Knx.Ets.Sdk.Project.Project.OnOnlineOperationsEvent.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.IndividualAddress (SDK XML line 12759)
        ///   "Gets the individual address of the device or 0/null if not assigned to a line."
        /// Verified: P:Knx.Ets.Sdk.Project.Device.HasIndividualAddress (SDK XML line 12742)
        ///   "Gets a value indicating whether the device has an individual address."
        /// Verified: M:Knx.Ets.Sdk.Root.IsConnectionAvailable(Device) -> bool (SDK XML line 28900)
        ///
        /// Bus write, NOT a project mutation -> no UndoManager, no revision bump.
        /// Same async/event pattern as StartDownload: returns immediately, progress via
        /// OnOnlineOperationsEvent, terminal event releases bus lease.
        ///
        /// </summary>
        /// <param name="deviceRef">Project device ref ("d{puid}").</param>
        /// <param name="currentAddress">Device's current bus address ("area.line.device",
        /// e.g. "1.1.5") to be overwritten. The device's project address becomes the new one.</param>
        public string StartSetIndividualAddress(string deviceRef, string currentAddress)
        {
            var device = FindDevice(deviceRef);

            // Validate args BEFORE acquiring bus (can throw).
            if (string.IsNullOrWhiteSpace(currentAddress))
                throw new ArgumentException(
                    "currentAddress is required: the device's current bus address to overwrite.");

            var currentAddrUInt16 = ParseIndividualAddressString(currentAddress);

            // Verify the device has a project individual address (the NEW address).
            if (!device.HasIndividualAddress)
                throw new ArgumentException(
                    $"Device {deviceRef} has no individual address configured in the project. "
                  + "Assign the device to a line and set an address first.");

            // Verified: M:Knx.Ets.Sdk.Root.IsConnectionAvailable(Device) -> bool (SDK XML line 28900)
            if (!Project.Root.IsConnectionAvailable(device))
                throw new BusUnavailableException(
                    $"No bus connection available for device {deviceRef}. "
                  + "Ensure a bus interface is selected in ETS.");

            var jobId = Guid.NewGuid().ToString("D");
            AcquireBus(jobId);

            // Register job BEFORE starting the SDK operation.
            var entry = new JobEntry(jobId, deviceRef, device, JobOpType.SetIndividualAddress, jobId);
            _jobs[jobId] = entry;

            try
            {
                EnsureEventSubscription();

                // StartOverwritingIndividualAddress returns synchronously (bool = "can download start?"),
                // the actual address write runs asynchronously. Progress/completion arrive via
                // Project.OnOnlineOperationsEvent (same as StartDownload).
                bool started = Project.Root.StartOverwritingIndividualAddress(device, currentAddrUInt16);

                if (!started)
                {
                    throw new BusUnavailableException(
                        $"StartOverwritingIndividualAddress returned false for device {deviceRef}. "
                      + "The bus connection may be unavailable or the device configuration may be incomplete.");
                }
            }
            catch
            {
                // Startup failure: remove job and release bus.
                _jobs.TryRemove(jobId, out _);
                ReleaseBus(jobId);
                throw;
            }

            PruneTerminalJobs();
            return jobId;
        }

        // ---------------------------------------------------------------
        // Phase F: Find helpers for new entity types
        // ---------------------------------------------------------------

#if !ETS5
        /// <summary>
        /// Resolve segmentRef "a{areaAddr}:l{lineAddr}:s{segNumber}" to a Segment.
        /// ETS6 only -- ETS5 has no Segment concept.
        /// </summary>
        private Segment FindSegment(string segmentRef)
        {
            var parts = segmentRef.Split(':');
            if (parts.Length != 3
                || !parts[0].StartsWith("a", StringComparison.Ordinal)
                || !parts[1].StartsWith("l", StringComparison.Ordinal)
                || !parts[2].StartsWith("s", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid segment ref: {segmentRef}");

            if (!ushort.TryParse(parts[0].Substring(1), out var areaAddr))
                throw new ArgumentException($"Invalid area address in segment ref: {segmentRef}");
            if (!ushort.TryParse(parts[1].Substring(1), out var lineAddr))
                throw new ArgumentException($"Invalid line address in segment ref: {segmentRef}");
            if (!uint.TryParse(parts[2].Substring(1), out var segNumber))
                throw new ArgumentException($"Invalid segment number in segment ref: {segmentRef}");

            foreach (Area area in Inst.Areas)
            {
                if (area.Address != areaAddr) continue;
                foreach (Line line in area.Lines)
                {
                    if (line.Address != lineAddr) continue;
                    foreach (Segment seg in line.Segments)
                    {
                        if (seg.Number == segNumber)
                            return seg;
                    }
                }
            }

            throw new KeyNotFoundException($"Segment {segmentRef} not found.");
        }
#endif

        /// <summary>
        /// Resolve tradeRef "t:{Id}" to a Trade object.
        /// Searches Inst.Trades recursively (trades can be nested).
        /// Verified: P:Knx.Ets.Sdk.Project.Installation.Trades (SDK XML line 15622)
        /// Verified: P:Knx.Ets.Sdk.Project.Trade.Trades (SDK XML line 18413) -- sub-trades
        /// </summary>
        private Trade FindTrade(string tradeRef)
        {
            if (!tradeRef.StartsWith("t:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid trade ref: {tradeRef}");

            var id = tradeRef.Substring(2);
            var found = FindTradeById(Inst.Trades, id);
            if (found == null)
                throw new KeyNotFoundException($"Trade {tradeRef} not found.");
            return found;
        }

        private static Trade? FindTradeById(TradeCollection trades, string id)
        {
            foreach (Trade t in trades)
            {
                if (t.Id == id)
                    return t;
                if (t.Trades != null)
                {
                    var sub = FindTradeById(t.Trades, id);
                    if (sub != null)
                        return sub;
                }
            }
            return null;
        }

        private static string BuildTradeRef(Trade t) => $"t:{t.Id}";

        /// <summary>
        /// Verified: P:Knx.Ets.Sdk.Project.Trade.Description (get/set, SDK XML line 18253)
        /// Verified: P:Knx.Ets.Sdk.Project.Trade.Comment (get/set, SDK XML line 18226)
        /// </summary>
        private static TradeInfo BuildTradeInfo(Trade t)
        {
            var info = new TradeInfo
            {
                Ref = BuildTradeRef(t),
                Name = t.Name ?? "",
                Description = t.Description,
                Comment = t.Comment,
                Number = t.Number
            };
            foreach (Device dev in t.Devices)
                info.Devices.Add($"d{dev.Puid}");
            return info;
        }

        /// <summary>
        /// Find the primary BusInterface for a device.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.BusInterface (SDK XML line 12511)
        ///   "Gets the bus interface object when the current individual address is a tunneling address."
        /// Verified: P:Knx.Ets.Sdk.Project.Device.BusInterfaces (SDK XML line 12519)
        ///   "Gets the bus interfaces of a device. Only used for devices that have one more tunneling server."
        /// </summary>
        /// <summary>
        /// Find the BusInterface for a device. #21: Rejects devices with multiple
        /// bus interfaces (ambiguous) instead of silently picking the first one.
        /// Verified: P:Knx.Ets.Sdk.Project.Device.BusInterface (SDK XML line 12511)
        /// Verified: P:Knx.Ets.Sdk.Project.Device.BusInterfaces (SDK XML line 12519)
        /// </summary>
        private BusInterface FindBusInterface(string deviceRef)
        {
            var device = FindDevice(deviceRef);

            // Primary bus interface (single tunneling address).
            var bi = device.BusInterface;
            if (bi != null)
                return bi;

            // Multi-tunnel: count interfaces; reject if ambiguous.
            if (device.BusInterfaces != null)
            {
                int count = 0;
                BusInterface? first = null;
                foreach (BusInterface bif in device.BusInterfaces)
                {
                    if (first == null) first = bif;
                    count++;
                    if (count > 1) break; // early exit, we only need to know >1
                }

                if (count > 1)
                {
                    // #21: Reject ambiguous multi-interface devices.
                    throw new ArgumentException(
                        $"Device {deviceRef} has {count}+ bus interfaces (multi-tunnel). "
                      + "Ambiguous: cannot determine which BusInterface to use. "
                      + "This device requires interface-level addressing (not yet supported).");
                }

                if (first != null)
                    return first;
            }

            throw new KeyNotFoundException(
                $"Device {deviceRef} has no BusInterface. "
              + "BusInterface is only available for devices with tunneling addresses.");
        }

        /// <summary>
        /// Resolve a MediumType by name from KnxMasterData.
        /// Verified: P:Knx.Ets.Sdk.MasterData.KnxMasterData.MediumTypes (SDK XML line 25315)
        /// Verified: P:Knx.Ets.Sdk.MasterData.MediumTypeCollection.Item(String) (SDK XML line 24825)
        ///   "Gets the MediumType by the specified name."
        /// Valid names: TP, PL, RF, IP, IoT (MediumType.Name).
        /// </summary>
        private MediumType FindMediumType(string mediumTypeName)
        {
            if (string.IsNullOrWhiteSpace(mediumTypeName))
                throw new ArgumentException("mediumTypeName is required.");

            try
            {
                return Project.Root.KnxMasterData.MediumTypes[mediumTypeName];
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new KeyNotFoundException(
                    $"MediumType '{mediumTypeName}' not found. Valid: TP, PL, RF, IP, IoT.");
            }
        }

        // ---------------------------------------------------------------
        // Phase F: Move / re-parent
        // ---------------------------------------------------------------

        /// <summary>
        /// Move a GroupAddress into a different GroupRange.
        /// Verified: M:Knx.Ets.Sdk.Project.GroupRange.Move(
        ///   Knx.Ets.Sdk.Project.GroupAddress,
        ///   Knx.Ets.Common.Types.Strategies.AddressAllocations,
        ///   System.UInt16) (SDK XML line 15188)
        ///   "Moves the specified source GroupAddress into this GroupRange."
        /// </summary>
        public void MoveGroupAddress(string targetGroupRangeRef, string groupAddressRef)
        {
            var targetGr = FindGroupRange(targetGroupRangeRef);
            var ga = FindGroupAddress(groupAddressRef);

            RunInMarker("Bridge: Move GroupAddress", () =>
            {
                targetGr.Move(ga, AddressAllocations.FirstFree, 0);
            });
        }

        /// <summary>
        /// Move a child GroupRange into a different parent GroupRange.
        /// Verified: M:Knx.Ets.Sdk.Project.GroupRange.Move(
        ///   Knx.Ets.Sdk.Project.GroupRange,
        ///   Knx.Ets.Common.Types.Strategies.AddressAllocations,
        ///   System.UInt16) (SDK XML line 15221)
        ///   "Moves the specified source GroupRange into this GroupRange."
        /// </summary>
        public void MoveGroupRange(string targetGroupRangeRef, string sourceGroupRangeRef)
        {
            var targetGr = FindGroupRange(targetGroupRangeRef);
            var sourceGr = FindGroupRange(sourceGroupRangeRef);

            if (targetGr.Id == sourceGr.Id)
                throw new ArgumentException("Cannot move a GroupRange into itself.");

            RunInMarker("Bridge: Move GroupRange", () =>
            {
                targetGr.Move(sourceGr, AddressAllocations.FirstFree, 0);
            });
        }

        /// <summary>
        /// Move a Line into a different Area.
        /// Verified: M:Knx.Ets.Sdk.Project.Area.Move(
        ///   Knx.Ets.Sdk.Project.Line,
        ///   Knx.Ets.Common.Types.Strategies.AddressAllocations,
        ///   System.UInt16) (SDK XML line 9010)
        ///   "Moves the specified source Line into this Area."
        /// </summary>
        public void MoveLine(string targetAreaRef, string lineRef)
        {
            var targetArea = FindArea(targetAreaRef);
            var line = FindLine(lineRef);

            RunInMarker("Bridge: Move Line", () =>
            {
                targetArea.Move(line, AddressAllocations.FirstFree, 0);
            });
        }

        /// <summary>
        /// Move a BuildingPart into a different parent BuildingPart.
        /// Verified: M:Knx.Ets.Sdk.Project.BuildingPart.Move(
        ///   Knx.Ets.Sdk.Project.BuildingPart) (SDK XML line 9776)
        ///   "Moves the specified source BuildingPart into this BuildingPart."
        /// </summary>
        public void MoveBuildingPart(string targetBuildingPartRef, string sourceBuildingPartRef)
        {
            var targetBp = FindBuildingPart(targetBuildingPartRef);
            var sourceBp = FindBuildingPart(sourceBuildingPartRef);

            if (targetBp.Id == sourceBp.Id)
                throw new ArgumentException("Cannot move a BuildingPart into itself.");

            RunInMarker("Bridge: Move BuildingPart", () =>
            {
                targetBp.Move(sourceBp);
            });
        }

        // ---------------------------------------------------------------
        // Phase F: Additional addresses
        // ---------------------------------------------------------------

        /// <summary>
        /// Add an additional individual address to a device.
        /// Verified: M:Knx.Ets.Sdk.Project.AdditionalDeviceAddressCollection.Add(System.UInt16)
        ///   (SDK XML line 8535)
        /// Verified: P:Knx.Ets.Sdk.Project.Device.AdditionalAddresses (inherited from DeviceNode)
        /// </summary>
        public AdditionalAddressResult AddAdditionalAddress(string deviceRef, ushort address)
        {
            var device = FindDevice(deviceRef);

            return RunInMarker("Bridge: Add Additional Address", () =>
            {
                device.AdditionalAddresses.Add(address);
                return new AdditionalAddressResult
                {
                    DeviceRef = deviceRef,
                    Address = address
                };
            });
        }

        /// <summary>
        /// Remove (park) an additional individual address from a device.
        /// There is no Delete on AdditionalDeviceAddressCollection in 6.3.0.
        /// Workaround: find the AdditionalDeviceAddressInfo with the matching Address
        /// and set it to 0xFFFF (parked/unused).
        /// Verified: P:Knx.Ets.Sdk.Project.Device.AdditionalAddressInfos (provides the info objects)
        /// Verified: P:Knx.Ets.Sdk.Project.AdditionalDeviceAddressInfo.Address (SDK XML line 8602)
        ///   "Gets or sets the address." -- settable.
        /// </summary>
        public void RemoveAdditionalAddress(string deviceRef, ushort address)
        {
            var device = FindDevice(deviceRef);

            RunInMarker("Bridge: Remove Additional Address", () =>
            {
                bool found = false;
                foreach (AdditionalDeviceAddressInfo addrInfo in device.AdditionalAddressInfos)
                {
                    if (addrInfo.Address == address)
                    {
                        // Park the address (no Delete method in 6.3.0).
                        addrInfo.Address = 0xFFFF;
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new KeyNotFoundException(
                        $"Additional address {address} not found on device {deviceRef}.");
            });
        }

        /// <summary>
        /// Add sending group addresses to a line's additional GA list (filter table).
        /// Verified: P:Knx.Ets.Sdk.Project.Line.AdditionalGroupAddresses (SDK XML line 15789)
        /// Verified: M:Knx.Ets.Sdk.Project.AdditionalGroupAddressCollection.Add(
        ///   System.Collections.Generic.IEnumerable{Knx.Ets.Sdk.Project.GroupAddress})
        ///   (SDK XML line 8704)
        /// </summary>
        public void AddLineAdditionalGroupAddresses(string lineRef, List<string> gaRefs)
        {
            var line = FindLine(lineRef);
            var groupAddresses = new List<GroupAddress>();
            foreach (var gaRef in gaRefs)
                groupAddresses.Add(FindGroupAddress(gaRef));

            RunInMarker("Bridge: Add Line Additional GAs", () =>
            {
                line.AdditionalGroupAddresses.Add(groupAddresses);
            });
        }

        /// <summary>
        /// Remove sending group addresses from a line's additional GA list.
        /// Verified: M:Knx.Ets.Sdk.Project.AdditionalGroupAddressCollection.Delete(
        ///   System.Collections.Generic.IEnumerable{Knx.Ets.Sdk.Project.GroupAddress})
        ///   (SDK XML line 8747)
        /// </summary>
        public void RemoveLineAdditionalGroupAddresses(string lineRef, List<string> gaRefs)
        {
            var line = FindLine(lineRef);
            var groupAddresses = new List<GroupAddress>();
            foreach (var gaRef in gaRefs)
                groupAddresses.Add(FindGroupAddress(gaRef));

            RunInMarker("Bridge: Remove Line Additional GAs", () =>
            {
                line.AdditionalGroupAddresses.Delete(groupAddresses);
            });
        }

        // ---------------------------------------------------------------
        // Phase F: Segments
        // ---------------------------------------------------------------

        /// <summary>
        /// Create a segment on a line.
        /// Verified: M:Knx.Ets.Sdk.Project.SegmentCollection.Add(
        ///   System.Collections.Generic.IEnumerable{System.String},
        ///   System.UInt16, System.UInt64,
        ///   Knx.Ets.Sdk.MasterData.MediumType) (SDK XML line 17820)
        ///   "Add segments to this collection."
        ///   names: names of segments; count: number to add; domainAddress: 0 for TP;
        ///   mediumType: TP, PL, RF, IP, IoT.
        /// Verified: P:Knx.Ets.Sdk.Project.Line.Segments (SDK XML line 15849)
        /// Verified: P:Knx.Ets.Sdk.Project.Segment.Number (SDK XML line 17580)
        /// Verified: P:Knx.Ets.Sdk.Project.Segment.Name (inherited from DomObject)
        /// </summary>
        public SegmentInfo CreateSegment(string lineRef, string name, string mediumTypeName)
        {
#if ETS5
            throw new NotSupportedOperationException(
                "segment.create is not supported on ETS5 (no Segment concept in the ETS5 SDK).");
#else
            var line = FindLine(lineRef);
            var mediumType = FindMediumType(mediumTypeName);

            return RunInMarker("Bridge: Create Segment", () =>
            {
                var added = line.Segments
                    .Add(new[] { name }, 1, 0, mediumType)
                    .Cast<Segment>().First();

                var areaAddr = line.Area.Address;
                var lineAddr = line.Address;

                return new SegmentInfo
                {
                    SegmentRef = $"a{areaAddr}:l{lineAddr}:s{added.Number}",
                    Name = added.Name ?? name,
                    Description = added.Description,
                    Comment = added.Comment
                };
            });
#endif
        }

        /// <summary>
        /// Delete a segment from its parent line.
        /// Note: main segments cannot be deleted (use line.delete instead).
        /// Verified: M:Knx.Ets.Sdk.Project.SegmentCollection.Delete(
        ///   System.Collections.Generic.IEnumerable{Knx.Ets.Sdk.Project.Segment})
        ///   (SDK XML line 17846)
        ///   "Deletes the specified collection of segments. Note: It is not possible
        ///    to delete main segments."
        /// </summary>
        public void DeleteSegment(string segmentRef)
        {
#if ETS5
            throw new NotSupportedOperationException(
                "segment.delete is not supported on ETS5 (no Segment concept in the ETS5 SDK).");
#else
            var segment = FindSegment(segmentRef);

            // Navigate to parent line's Segments collection.
            // The segment's parent is accessible via the ref encoding (line part).
            var parts = segmentRef.Split(':');
            var lineRef = parts[0] + ":" + parts[1];
            var line = FindLine(lineRef);

            RunInMarker("Bridge: Delete Segment", () =>
            {
                line.Segments.Delete(new[] { segment });
            });
#endif
        }

        // ---------------------------------------------------------------
        // Phase F: Trades
        // ---------------------------------------------------------------

        /// <summary>
        /// List all trades in the installation.
        /// Verified: P:Knx.Ets.Sdk.Project.Installation.Trades (SDK XML line 15622)
        /// </summary>
        public List<TradeInfo> ListTrades()
        {
            var result = new List<TradeInfo>();
            CollectTrades(Inst.Trades, result);
            return result;
        }

        private static void CollectTrades(TradeCollection trades, List<TradeInfo> result)
        {
            foreach (Trade t in trades)
            {
                result.Add(BuildTradeInfo(t));
                if (t.Trades != null)
                    CollectTrades(t.Trades, result);
            }
        }

        /// <summary>
        /// Create a new trade.
        /// Verified: M:Knx.Ets.Sdk.Project.TradeCollection.Add(System.String)
        ///   (SDK XML line 18490) -- "Add a trade."
        /// </summary>
        public TradeInfo CreateTrade(string name)
        {
            return RunInMarker("Bridge: Create Trade", () =>
            {
                var added = Inst.Trades.Add(name)
                    .Cast<Trade>().First();
                return BuildTradeInfo(added);
            });
        }

        /// <summary>
        /// Delete a trade.
        /// Verified: M:Knx.Ets.Sdk.Project.TradeCollection.Delete(
        ///   Knx.Ets.Sdk.Project.Trade) (SDK XML line 18517)
        ///   "Deletes the specified trade."
        /// </summary>
        /// <summary>
        /// Delete a trade from its parent collection (supports nested trades).
        /// Verified: P:Knx.Ets.Sdk.Project.Trade.ParentCollection (SDK XML line 18319)
        /// Verified: M:Knx.Ets.Sdk.Project.TradeCollection.Delete(Trade) (SDK XML line 18517)
        /// </summary>
        public void DeleteTrade(string tradeRef)
        {
            var trade = FindTrade(tradeRef);

            RunInMarker("Bridge: Delete Trade", () =>
            {
                // #10: Use ParentCollection so nested trades are deleted from their
                // actual parent TradeCollection, not always from Inst.Trades (top-level).
                var parentCollection = trade.ParentCollection as TradeCollection;
                if (parentCollection != null)
                {
                    parentCollection.Delete(trade);
                }
                else
                {
                    // Fallback: try top-level (should not happen if FindTrade succeeded).
                    Inst.Trades.Delete(trade);
                }
            });
        }

        /// <summary>
        /// Assign a device to a trade.
        /// Verified: M:Knx.Ets.Sdk.Project.Trade.Link(Knx.Ets.Sdk.Project.Device)
        ///   (SDK XML line 18333) -- "Links the specified device to this Trade."
        /// </summary>
        public void TradeAssignDevice(string tradeRef, string deviceRef)
        {
            var trade = FindTrade(tradeRef);
            var device = FindDevice(deviceRef);

            RunInMarker("Bridge: Assign Device to Trade", () =>
            {
                trade.Link(device);
            });
        }

        /// <summary>
        /// Unassign a device from a trade.
        /// Verified: M:Knx.Ets.Sdk.Project.Trade.Unlink(Knx.Ets.Sdk.Project.Device)
        ///   (SDK XML line 18370) -- "Unlinks the specified device from this Trade."
        /// </summary>
        public void TradeUnassignDevice(string tradeRef, string deviceRef)
        {
            var trade = FindTrade(tradeRef);
            var device = FindDevice(deviceRef);

            RunInMarker("Bridge: Unassign Device from Trade", () =>
            {
                trade.Unlink(device);
            });
        }

        // ---------------------------------------------------------------
        // Phase F: Bus interface (filter table)
        // ---------------------------------------------------------------

        /// <summary>
        /// Link group addresses to a device's bus interface (filter table).
        /// Verified: M:Knx.Ets.Sdk.Project.BusInterface.Link(
        ///   System.Collections.Generic.IEnumerable{Knx.Ets.Sdk.Project.GroupAddress})
        ///   (SDK XML line 10605)
        ///   "Links the specified collection of group addresses to this BusInterface."
        /// </summary>
        public void BusInterfaceLink(string deviceRef, List<string> gaRefs)
        {
            var bi = FindBusInterface(deviceRef);
            var groupAddresses = new List<GroupAddress>();
            foreach (var gaRef in gaRefs)
                groupAddresses.Add(FindGroupAddress(gaRef));

            RunInMarker("Bridge: Link GAs to BusInterface", () =>
            {
                bi.Link(groupAddresses);
            });
        }

        /// <summary>
        /// Unlink group addresses from a device's bus interface.
        /// Verified: M:Knx.Ets.Sdk.Project.BusInterface.Unlink(
        ///   System.Collections.Generic.IEnumerable{Knx.Ets.Sdk.Project.BusInterfaceConnector})
        ///   (SDK XML line 10643)
        ///   "Unlinks the specified collection of BusInterfaceConnector's from this BusInterface."
        ///   NOTE: Unlink takes BusInterfaceConnector, not GroupAddress. Must find connectors
        ///   matching the requested GA refs.
        /// Verified: P:Knx.Ets.Sdk.Project.BusInterface.Connectors (SDK XML line 10596)
        /// Verified: P:Knx.Ets.Sdk.Project.BusInterfaceConnector.GroupAddress (SDK XML line 10724)
        /// </summary>
        public void BusInterfaceUnlink(string deviceRef, List<string> gaRefs)
        {
            var bi = FindBusInterface(deviceRef);
            var gaIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gaRef in gaRefs)
            {
                var ga = FindGroupAddress(gaRef);
                gaIds.Add(ga.Id);
            }

            // Find BusInterfaceConnectors matching the requested GAs.
            var connectorsToUnlink = new List<BusInterfaceConnector>();
            foreach (BusInterfaceConnector conn in bi.Connectors)
            {
                if (conn.GroupAddress != null && gaIds.Contains(conn.GroupAddress.Id))
                    connectorsToUnlink.Add(conn);
            }

            if (connectorsToUnlink.Count == 0)
                return; // Idempotent: nothing to unlink.

            RunInMarker("Bridge: Unlink GAs from BusInterface", () =>
            {
                bi.Unlink(connectorsToUnlink);
            });
        }

        // ---------------------------------------------------------------
        // Phase F: Parameter reset to default
        // ---------------------------------------------------------------

        /// <summary>
        /// Reset a parameter to its default value.
        /// Verified: P:Knx.Ets.Sdk.Project.ParameterInstanceRef.Value (SDK XML line 16416)
        ///   "Setting the value back to null reference the default value of the
        ///    underlying ParameterRef becomes valid."
        /// </summary>
        public void ResetParameterToDefault(string deviceRef, string parameterRef)
        {
            var param = FindParameter(deviceRef, parameterRef);

            RunInMarker("Bridge: Reset Parameter to Default", () =>
            {
                param.Value = null;
            });
        }

        // ---------------------------------------------------------------
        // device.reset (WRITE, direct return)
        // Send a reset/restart command to a physical device on the bus.
        // ---------------------------------------------------------------

        /// <summary>
        /// Perform a bus reset on a physical KNX device (restart command).
        ///
        /// Verified: M:Knx.Ets.Sdk.Root.ResetDevice(Knx.Ets.Sdk.Project.Device) -> void
        ///   (SDK XML line 28726)
        ///   "Performs a reset operation on a device."
        ///   IHostContext.ResetDevice doc (SDK XML line 20338): "This is called from
        ///   Root.ResetDevice(Device)."
        /// Verified: M:Knx.Ets.Sdk.Root.IsConnectionAvailable(Device) -> bool (SDK XML line 28900)
        ///
        /// Synchronous, void return. Bus write, NOT a project mutation -> no UndoManager.
        /// No job model needed (quick bus command, not a long-running download).
        ///
        /// </summary>
        public void ResetDevice(string deviceRef)
        {
            var device = FindDevice(deviceRef);

            if (!Project.Root.IsConnectionAvailable(device))
                throw new BusUnavailableException(
                    $"No bus connection available for device {deviceRef}. "
                  + "Ensure a bus interface is selected in ETS.");

            var busOwner = Guid.NewGuid().ToString("D");
            AcquireBus(busOwner);
            try
            {
                // ResetDevice is void, synchronous -- sends a restart command to the device.
                Project.Root.ResetDevice(device);
            }
            finally
            {
                ReleaseBus(busOwner);
            }
        }

        // ===============================================================
        // Group 1: KNX Secure Certificates
        // ===============================================================

        /// <summary>
        /// Build the canonical ref for a Certificate.
        /// Certificate has no Id/Puid; use serial number hex as unique identifier.
        /// </summary>
        private static string BuildCertRef(Certificate cert)
        {
            var sn = cert.SerialNumber;
            if (sn != null && sn.Length > 0)
                return "cert:" + BitConverter.ToString(sn).Replace("-", "").ToLowerInvariant();
            // Fallback for certs without serial (should not happen in practice).
            return "cert:" + cert.GetHashCode().ToString("x8");
        }

        /// <summary>
        /// Find a Certificate by ref "cert:{serialHex}".
        /// </summary>
        private Certificate FindCertificate(string certRef)
        {
            if (!certRef.StartsWith("cert:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid certificate ref: {certRef}");
            // NOTE: Property is DeviceCertifcates (SDK typo, missing 'i').
            foreach (Certificate cert in Project.DeviceCertifcates)
            {
                if (BuildCertRef(cert) == certRef)
                    return cert;
            }
            throw new KeyNotFoundException($"Certificate {certRef} not found.");
        }

        /// <summary>
        /// Build a CertificateInfo DTO from a Certificate.
        /// FDSK and SerialNumber are byte[] in 6.3.0; convert to hex via existing BytesToHex.
        /// </summary>
        /// <summary>
        /// Build CertificateInfo DTO from a Certificate.
        /// #16 (SECURITY): FDSK and Password stripped -- only non-sensitive identity fields.
        /// </summary>
        private static CertificateInfo BuildCertInfo(Certificate cert)
        {
            var sn = cert.SerialNumber;
            return new CertificateInfo
            {
                Ref = BuildCertRef(cert),
                SerialNumber = sn != null && sn.Length > 0 ? BytesToHex(sn) : null,
                DeviceRef = cert.Device != null ? $"d{cert.Device.Puid}" : null
            };
        }

        /// <summary>
        /// List all device certificates in the project.
        /// Verified: P:Knx.Ets.Sdk.Project.Project.DeviceCertifcates (SDK XML line 16662)
        ///   NOTE: property name has SDK typo ("Certifcates" not "Certificates").
        /// Verified: P:Knx.Ets.Sdk.Project.Certificate.FDSK (SDK XML line 10767) -> byte[]
        /// Verified: P:Knx.Ets.Sdk.Project.Certificate.Password (SDK XML line 10776) -> string
        /// Verified: P:Knx.Ets.Sdk.Project.Certificate.SerialNumber (SDK XML line 10785) -> byte[]
        /// Verified: P:Knx.Ets.Sdk.Project.Certificate.Device (SDK XML line 10804) -> Device?
        /// </summary>
        public List<CertificateInfo> ListCertificates()
        {
            var result = new List<CertificateInfo>();
            foreach (Certificate cert in Project.DeviceCertifcates)
            {
                result.Add(BuildCertInfo(cert));
            }
            return result;
        }

        /// <summary>
        /// Add a device certificate from a raw certificate string (e.g. QR code scan).
        /// Verified: M:Knx.Ets.Sdk.Project.CertificateCollection.Add(System.String) (SDK XML line 10879)
        ///   "Adds a new Certificate with the specified raw certificate string."
        ///   Returns null if the device certificate already exists; if serial exists, overwrites.
        /// Remarks: TagCollection.Add says "not undoable", but CertificateCollection.Add
        ///   has the standard UnauthorizedAccessException doc, so we wrap in marker.
        /// </summary>
        public CertificateInfo AddCertificate(string rawKey)
        {
            // #12: Null-check INSIDE marker body so Discard() runs on failure,
            // preventing a no-op commit from polluting the undo stack.
            return RunInMarker("Bridge: Add Certificate", () =>
            {
                var cert = Project.DeviceCertifcates.Add(rawKey);

                if (cert == null)
                    throw new InvalidOperationException(
                        "Certificate already exists (Add returned null). "
                      + "If the serial number exists, the certificate was overwritten.");

                return BuildCertInfo(cert);
            });
        }

        /// <summary>
        /// Delete a certificate by ref.
        /// Verified: M:Knx.Ets.Sdk.Project.CertificateCollection.Delete(Certificate) (SDK XML line 10895)
        /// </summary>
        public void DeleteCertificate(string certificateRef)
        {
            var cert = FindCertificate(certificateRef);
            RunInMarker("Bridge: Delete Certificate", () =>
            {
                Project.DeviceCertifcates.Delete(cert);
            });
        }

        // ===============================================================
        // Group 2: UI Navigation
        // ===============================================================

        /// <summary>
        /// Navigate the ETS UI to the given object ref.
        /// Dispatches to the appropriate Root.NavigateTo* method.
        /// Not a project mutation -- no UndoManager marker needed.
        ///
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToDevice(Device) (SDK XML line 28791)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToGroupAddress(GroupAddress) (SDK XML line 28812)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToGroupRange(GroupRange) (SDK XML line 28805)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToLine(Line) (SDK XML line 28840)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToArea(Area) (SDK XML line 28847)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToBuildingPart(BuildingPart) (SDK XML line 28819)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToBuildingFunction(BuildingFunction) (SDK XML line 28826)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToTrade(Trade) (SDK XML line 28833)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToSegment(Segment) (SDK XML line 28861)
        /// Verified: M:Knx.Ets.Sdk.Root.NavigateToParameterDialog(Device) (SDK XML line 28854)
        /// </summary>
        public void NavigateTo(string objectRef)
        {
            // Dispatch by ref prefix.
            if (objectRef.StartsWith("d", StringComparison.Ordinal) &&
                !objectRef.StartsWith("dp:", StringComparison.Ordinal))
            {
                var device = FindDevice(objectRef);
                Project.Root.NavigateToDevice(device);
            }
            else if (objectRef.StartsWith("ga:", StringComparison.Ordinal))
            {
                var ga = FindGroupAddress(objectRef);
                Project.Root.NavigateToGroupAddress(ga);
            }
            else if (objectRef.StartsWith("gr:", StringComparison.Ordinal))
            {
                var gr = FindGroupRange(objectRef);
                Project.Root.NavigateToGroupRange(gr);
            }
            else if (objectRef.StartsWith("a", StringComparison.Ordinal) &&
                     objectRef.Contains(":l") && objectRef.Contains(":s"))
            {
#if ETS5
                // ETS5: no Segment type and no Root.NavigateToSegment.
                throw new NotSupportedOperationException(
                    "project.navigateTo for segment refs is not supported on ETS5 (no Segment type in ETS5 SDK).");
#else
                // "a{area}:l{line}:s{seg}" -> segment ref (check before line!)
                var seg = FindSegment(objectRef);
                Project.Root.NavigateToSegment(seg);
#endif
            }
            else if (objectRef.StartsWith("a", StringComparison.Ordinal) &&
                     objectRef.Contains(":l"))
            {
                // "a{area}:l{line}" -> line ref
                var line = FindLine(objectRef);
                Project.Root.NavigateToLine(line);
            }
            else if (objectRef.StartsWith("a", StringComparison.Ordinal) &&
                     !objectRef.Contains(":"))
            {
                // "a{area}" -> area ref (no colon)
                var area = FindArea(objectRef);
                Project.Root.NavigateToArea(area);
            }
            else if (objectRef.StartsWith("bp:", StringComparison.Ordinal))
            {
                var bp = FindBuildingPart(objectRef);
                Project.Root.NavigateToBuildingPart(bp);
            }
            else if (objectRef.StartsWith("bf:", StringComparison.Ordinal))
            {
                var bf = FindBuildingFunction(objectRef);
                Project.Root.NavigateToBuildingFunction(bf);
            }
            else if (objectRef.StartsWith("t:", StringComparison.Ordinal))
            {
                var trade = FindTrade(objectRef);
                Project.Root.NavigateToTrade(trade);
            }
            else if (objectRef.StartsWith("dp:", StringComparison.Ordinal))
            {
                // "dp:{deviceRef}" -> navigate to parameter dialog
                var innerRef = objectRef.Substring(3);
                var device = FindDevice(innerRef);
                Project.Root.NavigateToParameterDialog(device);
            }
            else
            {
                throw new ArgumentException(
                    $"Cannot navigate to ref '{objectRef}': unrecognized ref prefix. "
                  + "Supported: d{{puid}}, ga:{{id}}, gr:{{id}}, a{{addr}}, a{{addr}}:l{{addr}}, "
                  + "a{{addr}}:l{{addr}}:s{{num}}, bp:{{id}}, bf:{{id}}, t:{{id}}, dp:d{{puid}}.");
            }
        }

        // ===============================================================
        // Group 3: Organization (Tags, ToDoItems)
        // ===============================================================

#if !ETS5
        /// <summary>
        /// List all project tags.
        /// Verified: P:Knx.Ets.Sdk.Project.Project.Tags (SDK XML line 16621)
        /// Verified: P:Knx.Ets.Sdk.Project.Tag.Label (SDK XML line 17966)
        /// Verified: P:Knx.Ets.Sdk.Project.Tag.Color (SDK XML line 17977)
        ///   "Gets or sets a RGB hex string color"
        /// </summary>
        public List<TagInfo> ListTags()
        {
            var result = new List<TagInfo>();
            foreach (Tag tag in Project.Tags)
            {
                result.Add(new TagInfo
                {
                    // #13: Use DomObject.Id instead of positional index for stable refs.
                    Ref = $"tag:{tag.Id}",
                    Label = tag.Label ?? "",
                    Color = tag.Color ?? ""
                });
            }
            return result;
        }

        /// <summary>
        /// Create a new project tag.
        /// Verified: M:Knx.Ets.Sdk.Project.TagCollection.Add(System.String,System.Drawing.Color) (SDK XML line 18056)
        ///   "Adds a new Tag with the specified label and color code."
        ///   Remarks: "This operation is not undoable."
        /// Since Add is documented as "not undoable", we still wrap in RunInMarker
        /// for consistency and revision bumping (the marker commit is harmless for
        /// non-undo-participating ops).
        /// </summary>
        public TagInfo CreateTag(string label, string colorHex)
        {
            // Parse hex color string (e.g. "FF0000") to System.Drawing.Color.
            var color = ParseHexColor(colorHex);

            Tag? tag = null;
            RunInMarker("Bridge: Create Tag", () =>
            {
                tag = Project.Tags.Add(label, color);
            });

            if (tag == null)
                throw new InvalidOperationException("Tags.Add returned null unexpectedly.");

            // #13: Use DomObject.Id for stable ref.
            return new TagInfo
            {
                Ref = $"tag:{tag.Id}",
                Label = tag.Label ?? "",
                Color = tag.Color ?? ""
            };
        }

        /// <summary>
        /// Delete a project tag by ref "tag:{index}".
        /// Verified: M:Knx.Ets.Sdk.Project.TagCollection.Delete(Tag) (SDK XML line 18071)
        /// </summary>
        public void DeleteTag(string tagRef)
        {
            var tag = FindTag(tagRef);
            RunInMarker("Bridge: Delete Tag", () =>
            {
                Project.Tags.Delete(tag);
            });
        }

        /// <summary>
        /// #13: Find a Tag by DomObject.Id-based ref "tag:{Id}".
        /// Verified: P:Knx.Ets.Sdk.DomObject.Id (SDK XML line 19710)
        /// </summary>
        private Tag FindTag(string tagRef)
        {
            if (!tagRef.StartsWith("tag:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid tag ref: {tagRef}");

            var id = tagRef.Substring(4);
            foreach (Tag tag in Project.Tags)
            {
                if (tag.Id == id)
                    return tag;
            }
            throw new KeyNotFoundException($"Tag {tagRef} not found.");
        }
#else
        // ETS5 SDK 5.7 has no Tag concept (no Project.Tags).
        public List<TagInfo> ListTags() =>
            throw new NotSupportedOperationException(
                "tags are not supported on ETS5 (no Tag concept in the ETS5 SDK).");
        public TagInfo CreateTag(string label, string colorHex) =>
            throw new NotSupportedOperationException(
                "tag.create is not supported on ETS5 (no Tag concept in the ETS5 SDK).");
        public void DeleteTag(string tagRef) =>
            throw new NotSupportedOperationException(
                "tag.delete is not supported on ETS5 (no Tag concept in the ETS5 SDK).");
#endif

        /// <summary>
        /// Parse a hex color string ("RRGGBB" or "#RRGGBB") to System.Drawing.Color.
        /// </summary>
        private static System.Drawing.Color ParseHexColor(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return System.Drawing.Color.Gray;

            hex = hex.TrimStart('#');
            if (hex.Length != 6)
                throw new ArgumentException($"Color must be a 6-character hex string (RRGGBB), got '{hex}'.");

            int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
            int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
            int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            return System.Drawing.Color.FromArgb(r, g, b);
        }

        /// <summary>
        /// List all project to-do items.
        /// Verified: P:Knx.Ets.Sdk.Project.Project.ToDoItems (SDK XML line 16814)
        /// Verified: P:Knx.Ets.Sdk.Project.ToDoItem.Description (SDK XML line 18102)
        /// Verified: P:Knx.Ets.Sdk.Project.ToDoItem.ObjectPath (SDK XML line 18115)
        /// Verified: P:Knx.Ets.Sdk.Project.ToDoItem.CurrentStatus (SDK XML line 18088)
        ///   Enum: ToDoItemStatus.Open, ToDoItemStatus.Accomplished
        /// </summary>
        public List<ToDoItemInfo> ListToDoItems()
        {
            var result = new List<ToDoItemInfo>();
            foreach (ToDoItem item in Project.ToDoItems)
            {
                result.Add(new ToDoItemInfo
                {
                    // #13: Use DomObject.Id instead of positional index for stable refs.
                    Ref = $"todo:{item.Id}",
                    Description = item.Description ?? "",
                    ObjectPath = item.ObjectPath ?? "",
                    Status = item.CurrentStatus.ToString()
                });
            }
            return result;
        }

        /// <summary>
        /// Create a new to-do item.
        /// Verified: M:Knx.Ets.Sdk.Project.ToDoItemCollection.Add(String,String,ToDoItemStatus) (SDK XML line 18181)
        ///   Parameters: description, objectPath, status.
        ///   "The date is set to the current date and time (UTC)."
        /// </summary>
        public ToDoItemInfo CreateToDoItem(string description, string objectPath, string status)
        {
            var parsedStatus = string.Equals(status, "Accomplished", StringComparison.OrdinalIgnoreCase)
                ? ToDoItemStatus.Accomplished
                : ToDoItemStatus.Open;

            ToDoItem? item = null;
            RunInMarker("Bridge: Create ToDoItem", () =>
            {
                // SDK XML doc comment says returns ProjectHistory (copy-paste error),
                // but the actual return type is the created object cast-compatible with ToDoItem.
                item = (ToDoItem)(object)Project.ToDoItems.Add(description, objectPath, parsedStatus);
            });

            if (item == null)
                throw new InvalidOperationException("ToDoItems.Add returned null unexpectedly.");

            // #13: Use DomObject.Id for stable ref.
            return new ToDoItemInfo
            {
                Ref = $"todo:{item.Id}",
                Description = item.Description ?? "",
                ObjectPath = item.ObjectPath ?? "",
                Status = item.CurrentStatus.ToString()
            };
        }

        /// <summary>
        /// Delete a to-do item by ref "todo:{index}".
        /// Verified: M:Knx.Ets.Sdk.Project.ToDoItemCollection.Delete(ToDoItem) (SDK XML line 18198)
        /// </summary>
        public void DeleteToDoItem(string toDoItemRef)
        {
            var item = FindToDoItem(toDoItemRef);
            RunInMarker("Bridge: Delete ToDoItem", () =>
            {
                Project.ToDoItems.Delete(item);
            });
        }

        /// <summary>
        /// #13: Find a ToDoItem by DomObject.Id-based ref "todo:{Id}".
        /// Verified: P:Knx.Ets.Sdk.DomObject.Id (SDK XML line 19710)
        /// </summary>
        private ToDoItem FindToDoItem(string todoRef)
        {
            if (!todoRef.StartsWith("todo:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid to-do ref: {todoRef}");

            var id = todoRef.Substring(5);
            foreach (ToDoItem item in Project.ToDoItems)
            {
                if (item.Id == id)
                    return item;
            }
            throw new KeyNotFoundException($"ToDoItem {todoRef} not found.");
        }

        // ===============================================================
        // Group 5: Channels / Modules (READ)
        // ===============================================================

        /// <summary>
        /// List device channels (ChannelInstance), modules (ModuleInstance with
        /// ModuleArguments), and their active COM objects.
        ///
        /// Verified: P:Knx.Ets.Sdk.Project.Device.ChannelInstances (SDK XML line 12421)
        /// Verified: P:Knx.Ets.Sdk.Project.Device.ModuleInstances (SDK XML line 12381)
        /// Verified: P:Knx.Ets.Sdk.Project.ChannelInstance.Name (SDK XML line 10921)
        /// Verified: P:Knx.Ets.Sdk.Project.ChannelInstance.Description (SDK XML line 10931)
        /// Verified: P:Knx.Ets.Sdk.Project.ChannelInstance.IsActive (SDK XML line 10940)
        /// Verified: P:Knx.Ets.Sdk.Project.ChannelInstance.ApplicationProgramChannelId (SDK XML line 10911)
        /// Verified: P:Knx.Ets.Sdk.Project.ChannelInstance.ActiveComObjectInstances (SDK XML line 10967)
        /// Verified: P:Knx.Ets.Sdk.Project.ModuleInstance.ModuleArguments (SDK XML line 11415)
        /// Verified: P:Knx.Ets.Sdk.Project.ModuleInstance.ChannelInstances (inferred from SDK structure)
        /// Verified: P:Knx.Ets.Sdk.Project.ModuleArgument.RefId (SDK XML line 11266)
        /// Verified: P:Knx.Ets.Sdk.Project.ModuleArgument.Name (SDK XML line 11271)
        /// Verified: P:Knx.Ets.Sdk.Project.ModuleArgument.Value (SDK XML line 11302)
        /// Verified: P:Knx.Ets.Sdk.Project.ModuleArgument.Type (SDK XML line 11296)
        /// </summary>
        public DeviceChannelsResult ListDeviceChannels(string deviceRef)
        {
            var device = FindDevice(deviceRef);
            var devPuid = device.Puid;

            var channels = new List<ChannelInstanceInfo>();
            foreach (ChannelInstance ch in device.ChannelInstances)
            {
                channels.Add(BuildChannelInfo(ch, devPuid, -1));
            }

            var modules = new List<ModuleInstanceInfo>();
            int moduleIndex = 0;
            foreach (ModuleInstance mod in device.ModuleInstances)
            {
                var modChannels = new List<ChannelInstanceInfo>();
                foreach (ChannelInstance ch in mod.ChannelInstances)
                {
                    modChannels.Add(BuildChannelInfo(ch, devPuid, moduleIndex));
                }

                var args = new List<ModuleArgumentInfo>();
                foreach (ModuleArgument arg in mod.ModuleArguments)
                {
                    args.Add(new ModuleArgumentInfo
                    {
#if ETS5
                        // ETS5: ModuleArgument has AllocatorRefId instead of RefId,
                        // and no Type property.
                        RefId = arg.AllocatorRefId ?? "",
#else
                        RefId = arg.RefId ?? "",
#endif
                        Name = arg.Name ?? "",
                        Value = arg.Value?.ToString(),
#if ETS5
                        Type = "" // ETS5 SDK has no ModuleArgument.Type
#else
                        Type = arg.Type.ToString()
#endif
                    });
                }

                modules.Add(new ModuleInstanceInfo
                {
                    Ref = $"d{devPuid}:m{moduleIndex}",
#if ETS5
                    // ETS5: ModuleInstance has ModuleId (string) instead of ModuleDefId.
                    Name = mod.ModuleId ?? "",
#else
                    // ModuleInstance has no Name property in 6.3.0.
                    // Use ModuleDefId as the identifying string.
                    // Verified: P:Knx.Ets.Sdk.Project.ModuleInstance.ModuleDefId (SDK XML line 11379)
                    Name = mod.ModuleDefId ?? "",
#endif
                    Channels = modChannels,
                    Arguments = args
                });
                moduleIndex++;
            }

            return new DeviceChannelsResult
            {
                DeviceRef = $"d{devPuid}",
                Channels = channels,
                Modules = modules
            };
        }

        private ChannelInstanceInfo BuildChannelInfo(ChannelInstance ch, long devPuid, int moduleIndex)
        {
            var coRefs = new List<string>();
            foreach (ComObjectInstanceRef co in ch.ActiveComObjectInstances)
            {
                coRefs.Add(BuildCoRef(devPuid, co, moduleIndex));
            }

            return new ChannelInstanceInfo
            {
                Name = ch.Name ?? "",
                Description = ch.Description,
                // Verified: P:Knx.Ets.Sdk.Project.ChannelInstance.Text (get-only, SDK XML line 10991)
                Text = ch.Text,
                IsActive = ch.IsActive,
                ApplicationProgramChannelId = ch.ApplicationProgramChannelId,
                ActiveComObjectRefs = coRefs
            };
        }

        // ===============================================================
        // Group 6: Project History (Niche -- useful for LLM audit trail)
        // ===============================================================

        /// <summary>
        /// List project history entries.
        /// Verified: P:Knx.Ets.Sdk.Project.Project.ProjectHistories (SDK XML line 16722)
        /// Verified: P:Knx.Ets.Sdk.Project.ProjectHistory.Date (SDK XML line 16910)
        /// Verified: P:Knx.Ets.Sdk.Project.ProjectHistory.Text (SDK XML line 16951)
        /// Verified: P:Knx.Ets.Sdk.Project.ProjectHistory.Detail (SDK XML line 16924)
        /// Verified: P:Knx.Ets.Sdk.Project.ProjectHistory.User (SDK XML line 16965)
        /// </summary>
        public List<ProjectHistoryInfo> ListProjectHistory()
        {
            var result = new List<ProjectHistoryInfo>();
            foreach (ProjectHistory ph in Project.ProjectHistories)
            {
                result.Add(new ProjectHistoryInfo
                {
                    // #13: Use DomObject.Id instead of positional index for stable refs.
                    Ref = $"ph:{ph.Id}",
                    Date = ph.Date.ToString("o"),
                    Text = ph.Text ?? "",
                    Detail = ph.Detail,
                    User = ph.User
                });
            }
            return result;
        }

        /// <summary>
        /// Add a project history entry (LLM audit trail).
        /// Verified: M:Knx.Ets.Sdk.Project.ProjectHistoryCollection.Add(String) (SDK XML line 17020)
        ///   "Adds a new ProjectHistory entry. The date is set automatically."
        /// </summary>
        public ProjectHistoryInfo AddProjectHistory(string text)
        {
            ProjectHistory? ph = null;
            RunInMarker("Bridge: Add ProjectHistory", () =>
            {
                ph = Project.ProjectHistories.Add(text);
            });

            if (ph == null)
                throw new InvalidOperationException("ProjectHistories.Add returned null unexpectedly.");

            // #13: Use DomObject.Id for stable ref.
            return new ProjectHistoryInfo
            {
                Ref = $"ph:{ph.Id}",
                Date = ph.Date.ToString("o"),
                Text = ph.Text ?? "",
                Detail = ph.Detail,
                User = ph.User
            };
        }

        /// <summary>
        /// Delete a project history entry by ref "ph:{index}".
        /// Verified: M:Knx.Ets.Sdk.Project.ProjectHistoryCollection.Delete(ProjectHistory) (SDK XML line 17034)
        /// </summary>
        public void DeleteProjectHistory(string historyRef)
        {
            var ph = FindProjectHistory(historyRef);
            RunInMarker("Bridge: Delete ProjectHistory", () =>
            {
                Project.ProjectHistories.Delete(ph);
            });
        }

        /// <summary>
        /// #13: Find a ProjectHistory by DomObject.Id-based ref "ph:{Id}".
        /// Verified: P:Knx.Ets.Sdk.DomObject.Id (SDK XML line 19710)
        /// </summary>
        private ProjectHistory FindProjectHistory(string phRef)
        {
            if (!phRef.StartsWith("ph:", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid project history ref: {phRef}");

            var id = phRef.Substring(3);
            foreach (ProjectHistory ph in Project.ProjectHistories)
            {
                if (ph.Id == id)
                    return ph;
            }
            throw new KeyNotFoundException($"ProjectHistory {phRef} not found.");
        }
    }
}
