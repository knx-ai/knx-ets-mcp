# IPC Protocol: MCP Server <-> ETS6 AddIn

Binding contract between the platform-independent MCP server and the
in-process ETS6 AddIn. Both sides MUST adhere to this exactly. Changes
only by mutual agreement.

## Transport

- Windows **Named Pipe**, name `knx-ets-bridge-<random>` (per ETS session), access
  restricted to the current Windows user SID. On startup the AddIn writes pipe name +
  token to a file readable only by the user:
  `%LOCALAPPDATA%\knx-ets-bridge\session.json`; the MCP server reads it.
- For local development/testing (macOS, without ETS) the MCP server abstracts the
  transport behind an interface and uses a mock/TCP loopback. Named Pipe is the
  production path.
- Message framing: **one JSON message per line** (newline-delimited JSON, UTF-8).
- Every message carries `token` (from session.json); the AddIn discards messages with
  an invalid token.
- **Optional TCP endpoint (cross-machine, opt-in):** Can be enabled in the app
  preferences (`TcpEnabled`, `TcpPort`, `TcpAllowLan`). Same line-JSON frames and same
  `token` as the pipe. The token is displayed in the AddIn panel and must be configured
  manually on the MCP server (the remote machine cannot read `session.json`). The MCP
  server selects it via `KNX_BRIDGE_TRANSPORT=tcp` + `KNX_BRIDGE_HOST` /
  `KNX_BRIDGE_PORT` / `KNX_BRIDGE_TOKEN`.
  **Security:** no TLS -- token/data travel in cleartext over the network. Use only on
  a trusted LAN. Default off; when `TcpAllowLan=false` only loopback.

## Request

```json
{
  "id": "uuid",
  "token": "session-token",
  "method": "ga.create",
  "params": { },
  "idempotencyKey": "uuid",
  "expectedProjectRevision": "opaque-hash-or-null"
}
```

- `idempotencyKey`: required for mutating methods. **A freshly generated UUID per
  operation** (NOT a hash of method+params). The client reuses the same key only when
  resending THE SAME operation after a transport error. Two separate, content-identical
  tool calls receive different keys (otherwise e.g. link -> unlink -> link would be
  falsely deduplicated). The AddIn caches the result per `(projectId, key)` with a
  short TTL, checks the cache BEFORE the `expectedProjectRevision` check, and rebuilds
  the response with the current `id`/`revision`.
- `expectedProjectRevision`: optional. If set and the current project revision differs,
  the AddIn rejects with `error.code = "revision_mismatch"` (guard against user-vs-LLM
  races) -- but only AFTER the idempotency replay check. **Caveat:**
  `expectedProjectRevision` is a process-local counter and does NOT detect manual ETS
  edits, undo, or redo; it is a best-effort guard, not full race detection.
  The MCP server does not track the revision automatically; the field is an
  optional parameter on all mutating/programming tools and is forwarded unchanged.

## Response

```json
{ "id": "uuid", "ok": true, "result": { }, "projectRevision": "hash" }
```

```json
{ "id": "uuid", "ok": false, "error": { "code": "string", "message": "string" } }
```

Error codes (initial set): `revision_mismatch`, `not_found`, `invalid_params`,
`inactive_object`, `not_supported`, `secure_constraint`, `bus_unavailable`, `busy`,
`approval_required`, `not_implemented`, `internal`.

## Methods (v0)

### Label field conventions

A device or object `name` is often the product or app-program name (set by the
manufacturer). The human-assigned label that ETS displays prominently is usually
`description` (e.g. a device labelled "Couch" or a GA labelled "Light kitchen").
For communication objects, the functional label visible in ETS is `functionText`
(e.g. "Raffstore links"), while `text` is a read-only display string from the app
program. `comment` is a free-text field on most entities.

**Read-only fields (no setter):** com object `text`, channel `text`, catalog item
`description` (VisibleDescription).

Read (no idempotencyKey required):

- `bridge.info` -> `{ addinVersion, sdkVersion, projectName, projectId, projectRevision, knxIpOnly }`
  - Returns the running AddIn build version, the ETS SDK version actually loaded at
    runtime, the currently open project's name/ID/revision, and `knxIpOnly` (legacy
    field, always `true`). No bus access needed. Use this to confirm which AddIn build
    and SDK are active.
- `project.info` -> `{ projectId, name, groupAddressStyle, revision }`
- `devices.list` -> `[{ ref, address, name, description, comment, product, orderNumber, line }]`
  - De-duplicated by device Puid. `product` falls back to `Device.Product.Text` when the
    catalog item name is empty. `name` = user-assigned device name; `product` =
    catalog/product name; `orderNumber` = product order number.
  - `description`: user-assigned label in ETS (the text ETS displays prominently);
    `comment`: free-text comment field.
- `ga.list` -> `[{ ref, address, name, description, comment, dpt }]`
  - `comment`: free-text comment field (description was already returned).
- `comobjects.list` `{ deviceRef }` -> `[{ ref, number, name, description, functionText, text, dpt, flags, links:[gaRef], channel?, block? }]`
  (`block` is the authoritative ETS group-object-tree path, e.g. "Operation / Display >
  Push button functions > PB9/10: Push buttons 9/10", from walking `IGroupObjectTreeElement.
  ParentTreeElement`; `channel` is the heuristic ChannelInstance label. Both omitted when
  not applicable.)
- `application.dynamic` `{ deviceRef }` -> `{ xml }` -- the application program's dynamic UI
  tree (`ParameterBlock`/`Channel`/`ParameterRefRef`) as XML, from
  `ApplicationProgram.DynamicAsString`. Authoritative source for mapping a parameter to its
  UI block/channel (the SDK object model does not expose a parameter's block otherwise).
  - `description`: settable user label. `functionText`: settable functional label (e.g.
    "Raffstore links"). `text`: read-only display text from the app program (no setter).
- `topology.list` -> `[{ areaRef, address, name, description, comment, lines: [{ lineRef, address, name, description, comment, segments: [{ segmentRef, name, description, comment }] }] }]`
  - Returns the `lineRef`/`segmentRef` needed for `device.addFromCatalog`.
  - `description`, `comment` on areas, lines, and segments.
- `catalog.manufacturers` -> `[{ manufacturerRef, name }]`
  - Manufacturers available in the project/catalog. **Merges two sources:**
    `Root.Manufacturers` (project-local, products actually used in the project) and
    `Root.GlobalProductStoreManufacturers` (downloaded global product store),
    de-duplicated by manufacturer ID. This fixes the case where the global store is
    empty on a real project (only project-local manufacturers would be returned).
- `catalog.search` `{ query, manufacturerRef? }` -> `[{ catalogItemRef, manufacturer, name, orderNumber, description }]`
  - Searches products across **both** the project-local catalog (`Root.Manufacturers`)
    and the global product store (`Root.GlobalProductStoreManufacturers`).
    `query` is mandatory. De-duplicated by manufacturer + order number. Returns the
    `catalogItemRef` for `device.addFromCatalog`.
- `catalog.search_online` `{ query, manufacturerRef? }` -> `[{ unifiedCatalogItemRef, manufacturer, name, orderNumber, description }]`
  - Searches the **ETS online catalog** (`UnifiedManufacturers`, curated by KNX).
    Results are not yet locally usable -- fetch them first via `catalog.internalize`.
- `params.list` `{ deviceRef }` -> `[{ parameterRef, name, value, isDefault, isActive,
  text?, unit?, access?, options?: [{ value, text }], min?, max?, block? }]`. The optional
  fields carry product-data semantics (label, unit, access level, enum choices, numeric
  range) and are omitted when the product leaves them unset. `block` is the parameter's UI
  path from the application-program dynamic tree (e.g. "Operation / Display > Push button
  functions > PB9/10: Push buttons 9/10"), parsed once per app program and cached; it is the
  authoritative disambiguator for identically-named parameters -- select by `block` + `name`
  + `isActive` (a unique triple) to target the correct instance. `isActive` is the
  post-visibility result: `false` = deactivated by a controlling parameter (a set has no
  effect); the raw condition is not exposed, so detect dependencies by setting a parameter
  and re-reading.
  - Returns the `parameterRef` (and current/default value) for `param.set`.
- `building.list` -> `[{ ref, name, description, comment, type, children:[...], devices:[deviceRef], functions:[{ ref, name, description, comment, groupAddresses:[gaRef] }] }]`
  - Recursive tree of the building structure. `type` is one of: Building,
    BuildingPart, Floor, Room, DistributionBoard, Corridor, Stairway.
  - `description`, `comment` on building parts and building functions.
- `catalog.browseProducts` `{ manufacturerRef, offset?, limit? }` -> `[{ catalogItemRef, manufacturer, name, orderNumber, description }]`
  - Paginated browsing of all products of a manufacturer. `offset` (default 0),
    `limit` (default 100). Products are merged from project-local and global product
    store sources, de-duplicated by manufacturer + order number.
- `catalog.productInfo` `{ catalogItemRef }` -> `{ catalogItemRef, manufacturer, name, orderNumber, description, mediumTypes }`
  - Detail info for a catalog product (from `catalog.browseProducts` or `catalog.search`).
- `device.compare` `{ deviceRef }` -> `{ compared:[{property, equal, expected, actual}], partial:true, note }`
  - Deliberately LIMITED compare. `note` explains that the comparison is partial because
    full `CompareProperty`/`CompareMemory` requires low-level byte arrays. Compares
    project-side properties only.
- `device.channels` `{ deviceRef }` -> `[{ channelRef, name, text, modules:[{ moduleRef, name }] }]`
  - Lists ChannelInstances and nested ModuleInstances for the device.
  - `text`: read-only display text from the app program (no setter).
- `trades.list` -> `[{ tradeRef, name, description, comment, devices:[deviceRef] }]`
  - All trades in the project with their assigned devices.
  - `description`, `comment`: user-assigned label and free-text comment.
- `certificates.list` -> `[{ ref, serialNumber, deviceRef }]`
  - KNX Secure device certificates in the project. **Security: FDSK and password
    material is never exposed in responses.**
- `tags.list` -> `[{ tagRef, label, color }]`
  - Project tags (user-defined labels).
- `todos.list` -> `[{ todoRef, description, objectPath?, status }]`
  - Project to-do items.
- `projectHistory.list` -> `[{ historyRef, text, timestamp }]`
  - Audit trail / project history entries.

Mutating (idempotencyKey required, run on ETS UI thread under UndoManager marker):

- `ga.create` `{ name, address, dptMain?, dptSub? }` -> `{ ref, address }`
  - `address` is a group address string in the project style: `"main/middle/sub"`
    (3-level) or `"main/sub"` (2-level); a plain integer (raw 16-bit value) is also
    accepted. The AddIn parses according to `project.groupAddressStyle`.
  - Individual/physical address parsing (`device.setAddress`, `bus.ping`, etc.) tolerates
    commas and surrounding whitespace (e.g. `"1,1,5"` is accepted as `"1.1.5"`).
- `device.addFromCatalog` `{ lineRef, catalogItemRef, address }` -> `{ ref, address }`
- `link.create` `{ comObjectRef, gaRef }` -> `{ comObjectRef, gaRef, dpt?, gaDpt?,
  dptWarning? }`. The link always proceeds (ETS permits mismatched DPTs); `dptWarning` is a
  soft advisory, present only when the com-object and group-address DPTs have different
  main numbers. Filtering note: several MCP tools filter server-side to keep reads focused
  (the underlying protocol methods still return the full list): `knx_list_parameters`
  (`active_only` -- DEFAULTS TRUE, drops inactive; `name_contains`), `knx_list_comobjects`
  (`name_contains`, matches text/functionText/channel), `knx_list_devices` (`name_contains`,
  matches name/description/comment/product/orderNumber/address -- so a human label like
  "Couch" in the description is findable), and `knx_list_group_addresses` (`name_contains`,
  matches name/description/comment/address -- e.g. a label in the GA description).
  - Must check `IsActive` beforehand; inactive object -> `error.inactive_object`.
  - If the SDK's own link operation fails internally (observed when adding a SECOND group
    address to a modular device's com-object: the SDK raises a missing-dictionary-key
    error), the link is reported as `error.internal` with the link state unverified -- NOT
    `not_found` (the refs are valid). The project is left unchanged. Workaround: add that GA
    manually in ETS; use `atomic: false` for such batches.
- `link.delete` `{ comObjectRef, gaRef }` -> `{ ok }`
  - **Idempotent:** if the link does not (or no longer) exist, still returns
    `{ ok: true }`, not `not_found`. A `not_found` occurs only when `comObjectRef`/
    `gaRef` themselves are unknown.
  - The removal is verified: after unlinking, the com-object's live connectors are re-read,
    and if the link is still present an `error.internal` is returned instead of a false
    `{ ok: true }` (the unlink is rolled back).
- `param.set` `{ deviceRef, parameterRef, value }` -> `{ ok }`
- `ga.delete` `{ groupAddressRef }` -> `{ ok }`
  - Removes the GA and all its links. Not reversible except via Undo.
- `ga.rename` `{ groupAddressRef, name }` -> `{ ref, address, name, description?, dpt? }`
  - Sets `GroupAddress.Name`.
- `ga.setDescription` `{ groupAddressRef, description }` -> `{ ref, address, name, description, dpt? }`
  - Sets `GroupAddress.Description`.
- `ga.setDatapointType` `{ groupAddressRef, dptMain, dptSub? }` -> `{ ref, address, name, dpt? }`
  - Sets `GroupAddress.DatapointTypeObject` via `FindDatapointSubtype`.
- `groupRange.create` `{ name, address, parentGroupRangeRef? }` -> `{ ref, name, address, description, comment }`
  - Creates a group range level (main/middle group). `parentGroupRangeRef` determines
    nesting; without it a top-level group is created.
- `groupRange.delete` `{ groupRangeRef }` -> `{ ok }`
  - Deletes a group range including all contained GAs/sub-ranges.
- `device.delete` `{ deviceRef }` -> `{ ok }`
  - Removes the device from the topology (and all its links).
- `device.rename` `{ deviceRef, name }` -> `{ ref, address, name, product, line }`
  - Sets `Device.Name`.
- `device.setDescription` `{ deviceRef, description }` -> `{ ref, address, name, description, product, line }`
  - Sets `Device.Description` (the user-assigned label ETS displays).
- `device.setComment` `{ deviceRef, comment }` -> `{ ref, address, name, comment, product, line }`
  - Sets `Device.Comment` (free-text comment field).
- `device.setAddress` `{ deviceRef, address }` -> `{ ref, address, name, product, line }`
  - Sets the physical address in the project model. `address` as
    `"area.line.device"` (area/line must match the device's current line).
    No bus programming.
- `device.move` `{ deviceRef, lineRef }` -> **always** `error.not_supported`
  - SDK 6.3.0 provides no `Device.Move`. Workaround: delete the device and re-add it
    on the target line.
- `device.unassign` `{ deviceRef }` -> `{ ok }`
  - Detaches a device from its line and moves it to the unassigned-devices collection,
    without deleting it. SDK: `RefUnassignedDeviceCollection.Move`.
- `area.create` `{ name, address? }` -> `{ areaRef, address, name, lines:[] }`
  - Creates a new area in the topology. `address` [0..15]; omitted means
    auto-allocated.
- `area.delete` `{ areaRef }` -> `{ ok }`
  - Deletes the area including all lines/devices.
- `line.create` `{ areaRef, name, address? }` -> `{ lineRef, address, name, segments:[] }`
  - Creates a new line under the specified area. `address` [0..15].
- `line.delete` `{ lineRef }` -> `{ ok }`
  - Deletes the line including all devices.
- `comObject.setFlags` `{ comObjectRef, communicationFlag?, readFlag?, writeFlag?, transmitFlag?, updateFlag?, readOnInitFlag?, priority? }`
  -> `{ ref, communicationFlag, readFlag, writeFlag, transmitFlag, updateFlag, readOnInitFlag, priority }`
  - Sets individual communication flags on a ComObjectInstanceRef. Only fields passed
    are written; the rest remains unchanged. `priority`: `"Low"`, `"High"`, or
    `"Alert"`. IsActive guard: inactive objects -> `error.inactive_object`.
- `comObject.setDescription` `{ comObjectRef, description }` -> `{ ref, number, name, description }`
  - Sets `ComObjectInstanceRef.Description` (user-assigned label).
- `comObject.setFunctionText` `{ comObjectRef, functionText }` -> `{ ref, number, name, functionText }`
  - Sets `ComObjectInstanceRef.FunctionText` (functional label, e.g. "Raffstore links").
- `building.create` `{ name, type, parentBuildingPartRef?, spaceUsage? }` -> `{ ref, name, type, children, devices, functions }`
  - Creates a building part. `type`: Building, BuildingPart, Floor, Room,
    DistributionBoard, Corridor, Stairway. `parentBuildingPartRef` determines the
    parent level (without: top-level). `spaceUsage` from KNX master data.
- `building.delete` `{ buildingPartRef }` -> `{ ok }`
  - Deletes the building part including all children and associated functions.
- `building.rename` `{ buildingPartRef, name }` -> `{ ref, name, type, children, devices, functions }`
  - Sets `BuildingPart.Name`.
- `building.assignDevice` `{ buildingPartRef, deviceRef }` -> `{ ok }`
  - Assigns a device to a building part (`BuildingPart.Link(Device)`).
- `building.unassignDevice` `{ buildingPartRef, deviceRef }` -> `{ ok }`
  - Removes the assignment (`BuildingPart.Unlink(Device)`).
- `buildingFunction.create` `{ buildingPartRef, name }` -> `{ ref, name, groupAddresses:[] }`
  - Creates a building function under a building part.
- `buildingFunction.delete` `{ buildingFunctionRef }` -> `{ ok }`
  - Deletes a building function.
- `buildingFunction.linkGroupAddress` `{ buildingFunctionRef, groupAddressRefs }` -> `{ ok }`
  - Links group addresses to a building function.
- `buildingFunction.unlinkGroupAddress` `{ buildingFunctionRef, groupAddressRefs }` -> `{ ok }`
  - Removes group address links from a building function.
- `project.save` -> **always** `error.not_supported`
  - ETS saves projects automatically; no explicit save possible or needed.
- `project.export` `{ path, includeCatalog? }` -> `{ ok }`
  - Exports the project as `.knxproj` to `path` on the ETS host.
    `includeCatalog` (default false) includes catalog data.
- `project.backup` `{ path }` -> **always** `error.not_supported`
  - `Root.BackupDatabase` is a compat stub with no effect in ETS 6.
- `project.undo` -> `{ ok }`
  - Undoes the last operation (`UndoManager.Undo()`).
- `project.redo` -> `{ ok }`
  - Restores a previously undone operation (`UndoManager.Redo()`).
- `project.exportSemantic` -> **always** `error.not_supported`
  - `Root.ExportSemanticDataAsync` is asynchronous and would deadlock on the UI thread.
    Not supported in v1.
- `project.navigateTo` `{ ref }` -> `{ ok }`
  - Selects the object in the ETS UI. Accepts device, GA, groupRange, line, area,
    buildingPart, buildingFunction, trade, and segment refs.
- `param.setDefault` `{ deviceRef, parameterRef }` -> `{ ok }`
  - Resets a parameter to its product default (`Value = null`). Note:
    `param.setActive` is NOT supported -- `ParameterInstanceRef.IsActive` is a read-only
    computed value; change the controlling parameter instead.

Move operations:

- `groupRange.moveGroupAddress` `{ targetGroupRangeRef, groupAddressRef }` -> `{ ok }`
  - Moves a GA into a different group range.
- `groupRange.moveGroupRange` `{ targetGroupRangeRef, sourceGroupRangeRef }` -> `{ ok }`
  - Moves a group range (and its children) under another parent range.
- `line.move` `{ targetAreaRef, lineRef }` -> `{ ok }`
  - Moves a line to a different area.
- `buildingPart.move` `{ targetBuildingPartRef, sourceBuildingPartRef }` -> `{ ok }`
  - Moves a building part (and children) under a different parent.

Segments:

- `segment.create` `{ lineRef, name, mediumType }` -> `{ segmentRef, name }`
  - Creates a segment on a line. `mediumType`: `"TP"`, `"PL"`, `"RF"`, `"IP"`, `"IoT"`.
- `segment.delete` `{ segmentRef }` -> `{ ok }`

Additional addresses:

- `device.addAdditionalAddress` `{ deviceRef, address }` -> `{ ok }`
  - Adds an additional individual address to the device.
- `device.removeAdditionalAddress` `{ deviceRef, address }` -> `{ ok }`
  - Removes an additional address (parks it to 0xFFFF).
- `line.addAdditionalGroupAddress` `{ lineRef, groupAddressRefs }` -> `{ ok }`
  - Adds additional/sending GAs to a line. Note: additional GAs are per-Line in the SDK,
    not per-ComObject.
- `line.removeAdditionalGroupAddress` `{ lineRef, groupAddressRefs }` -> `{ ok }`
  - Removes additional/sending GAs from a line.

Trades:

- `trade.create` `{ name }` -> `{ tradeRef, name }`
- `trade.delete` `{ tradeRef }` -> `{ ok }`
- `trade.assignDevice` `{ tradeRef, deviceRef }` -> `{ ok }`
- `trade.unassignDevice` `{ tradeRef, deviceRef }` -> `{ ok }`

Bus interface (coupler filter table):

- `busInterface.link` `{ deviceRef, groupAddressRefs }` -> `{ ok }`
  - Links GAs to the device's bus interface (coupler filter table).
- `busInterface.unlink` `{ deviceRef, groupAddressRefs }` -> `{ ok }`
  - Removes GAs from the device's bus interface.

KNX Secure:

- `certificate.add` `{ value }` -> `{ ref, serialNumber, deviceRef }`
  - Adds a KNX Secure device certificate. KNX Secure secret material (FDSK, password)
    is never included in the response.
- `certificate.delete` `{ certificateRef }` -> `{ ok }`

Tags:

- `tag.create` `{ label, color? }` -> `{ tagRef, label, color }`
- `tag.delete` `{ tagRef }` -> `{ ok }`

To-do items:

- `todo.create` `{ description, objectPath?, status? }` -> `{ todoRef, description }`
- `todo.delete` `{ todoRef }` -> `{ ok }`

Project history (audit trail):

- `projectHistory.add` `{ text }` -> `{ historyRef, text }`
- `projectHistory.delete` `{ historyRef }` -> `{ ok }`

## Batch

`batch.apply` runs many project mutations in one request under a single UndoManager
marker, so parametrising and linking across many devices is one round-trip and one undo
step instead of dozens. Requires `idempotencyKey` (the batch is one mutating operation).

- `batch.apply` `{ operations: [ { method, params }, ... ], atomic?, validateOnly? }` ->
  `{ applied, atomic, rolledBack, rollbackNote?, total, ok, failed, skipped, results: [ { index, method, status, result?, error? } ] }`
  - `validateOnly` (default `false`): read-only pre-flight. Nothing is mutated; returns
    `{ validated: true, atomic, total, valid, invalid, results: [ { index, method, valid,
    issues: [string] } ] }`. Checks: required params present; `link.create` -> com-object
    exists + active, GA exists, DPT main-number match; `ga.create` -> address not already in
    use. Best-effort/conservative (a check that cannot run yields no false positive).
  - `operations`: executed in order. Only fast, undo-reversible PROJECT mutations are
    allowed (allowlist: `ga.*`, `groupRange.*`, `device.*` create/label/address/unassign,
    `link.*`, `param.set`/`param.setDefault`, `area.*`, `line.*`, `comObject.*`,
    `building*`, `segment.*`, `trade.*`, `busInterface.*`, `certificate.*`, `tag.*`,
    `todo.*`, `projectHistory.*`). NOT allowed: `device.program`, `firmware.update`,
    `bus.*`, `group.write`, `device.reset`, `device.unload`, `catalog.import`/
    `internalize`, `project.undo`/`redo`, `device.move`, or a nested `batch.apply`.
    Max 500 operations.
  - `atomic` (default `true`): all-or-nothing. The first failing step rolls back the whole
    batch (outer marker discarded); remaining steps are reported `status: "skipped"` and
    `applied: false`. With `atomic: false` each step is independent, failures do not stop
    later steps, `applied: true`.
  - `rolledBack` is `true` only when a failed atomic batch was rolled back cleanly. Rollback
    is best-effort: if an SDK operation fails mid-execution its undo can itself throw and halt
    the rollback, leaving earlier steps applied. In that case `rolledBack` is `false` and
    `rollbackNote` explains that the project may hold partial changes -- re-read the affected
    objects to confirm. For devices where a step is prone to internal SDK failures (e.g.
    linking a second group address to a modular device's com-object), prefer `atomic: false`.
  - A step is never reported `ok` unless it actually ran successfully; per-step errors
    are surfaced under `error: { code, message }`, never swallowed.
  - Sub-operations run inline on the ETS UI thread under the single batch marker.

Truncation reporting: `ga.create`/`ga.rename` and `device.rename` read the stored name
back and set `truncated: true` in the result when ETS shortened it to its length limit
(field omitted when not truncated).

Building-function empty-list caveat: a building function read (`building.list`) whose
`groupAddresses` is empty carries a `note`. In ETS 6.3.0 an empty function address list
may be genuine OR a stale SDK cache after a GA was unlinked/deleted (in this app or in
ETS); the SDK refreshes it only after the owning app restarts. Do not treat empty as
authoritative.

## Catalog Update

Modifies the local product store/catalog (NOT the project, therefore no UndoManager
marker), requires `idempotencyKey`. A full re-sync of the online catalog from the KNX
server is NOT possible via the SDK -- ETS handles that internally.

- `catalog.import` `{ path }` -> `{ imported: [{ catalogItemRef, manufacturer, name, orderNumber }] }`
  - Imports a `.knxprod` file (`path` on the ETS host) into the catalog via
    `Root.ImportProductData`. Only **signed** `.knxprod` files are accepted. Afterwards
    the product is available via `catalog.search`/`device.addFromCatalog`.
- `catalog.internalize` `{ unifiedCatalogItemRef }` -> `{ catalogItemRef }`
  - Fetches an online catalog product (`unifiedCatalogItemRef` from
    `catalog.search_online`) locally into the project via
    `Root.InternalizeProductData`. Returns the local `catalogItemRef`.

## Long-Running Operations

Long-running operations run asynchronously via the job model.

- `device.program` `{ deviceRef, options? }` -> `{ jobId }`
  - **idempotencyKey required.** Programming twice with the same key/params does not
    start a second job but returns the existing `jobId`.
  - **No approval token.** Application/parameter/GA-table download is reversible
    (simply re-program the corrected state), destroys nothing, and therefore runs
    automatically.
  - `options` (optional, string): `"partial"` (**default** -- writes only the changes,
    no individual-address step / no programming-button press), `"all"` (full incl.
    individual address), `"application"`, `"network"`, `"networkBySerial"`. Maps to ETS
    `LoadDeviceOptions`.
- `device.unload` `{ deviceRef, fullUnload? }` -> `{ jobId }`
  - **idempotencyKey required.** Unloads the application program from the device.
  - `fullUnload` (default false): true = full unload including individual address,
    false = application program only.
  - Progress via `job.status`, same events as `device.program`.
- `firmware.update` `{ deviceRef, firmware }` -> `{ jobId }`
  - Flashes real device firmware (`Root.StartFirmwareUpdate`) -- potentially
    irreversible/bricking. **idempotencyKey required.**
  - **Gated exclusively via app preferences:** runs only when "Unattended firmware
    update" is enabled in the configuration dialog (a standing, deliberately set
    operator approval). Otherwise `approval_required`. No per-request token.
- `job.status` `{ jobId }` -> `{ state, percent, error?, result? }` (state: running|done|failed|canceled)
  - No idempotencyKey (addressed by `jobId`, purely read).
  - `result` is null while the job is running; for completed scan/monitor jobs it
    contains the job-specific result (e.g. `{addresses:[...]}` or
    `{telegrams:[...]}`).
- `job.cancel` `{ jobId }` -> `{ ok }`
  - No idempotencyKey (addressed by `jobId`, naturally idempotent).

## Phase D: Bus / Online Operations

Bus jobs retain exclusive bus ownership. An active group monitor is the exception
for `group.read` and `group.write`: those operations borrow the monitor's existing
connection and are serialized with its cleanup. They use the monitor's selected
`lineRef`, rather than opening another default connection. Other bus operations
(including programming, scans and ping) remain excluded during the monitor and
return `error.bus_unavailable`. A second monitor is also excluded.

Read (no idempotencyKey):

- `bus.ping` `{ address }` -> `{ alive }` (bool)
  - Pings a KNX individual address. `address` in format `"area.line.device"`.
  - SDK: `NetworkManagement.Ping(UInt16)`. Direct return.
- `bus.scanLine` `{ lineRef }` -> `{ jobId }`
  - Scans a topology line for responding devices. Long-running -> job model.
  - Job result in `job.status.result`: `{ addresses: ["1.1.1", "1.1.5", ...] }`
  - SDK: `NetworkManagement.IndividualAddressScanRangeAsync`.
- `device.readInfo` `{ deviceRef }` -> `{ maskVersion, maskVersionId }`
  - Reads the device descriptor from the physical device on the bus.
  - Secured devices without key: `maskVersionId = "(secured)"`.
  - SDK: `DeviceManagement.GetDeviceDescriptor()`.
- `device.compare` `{ deviceRef }` -> `{ compared:[{property, equal, expected, actual}], partial:true, note }`
  - Deliberately LIMITED compare. `note` explains that the comparison is partial because
    full `CompareProperty`/`CompareMemory` requires low-level byte arrays. Project-side
    property comparison only; `device.readInfo` as a lightweight bus-side alternative.
- `bus.reconstructLine` `{ lineRef }` -> `{ jobId }`
  - Scans a line and reads each responding device's identity (mask version, serial
    number, manufacturer ID) for **project recovery**. Long-running -> job model.
  - Job result in `job.status.result`:
    `{ devices: [{ address, maskVersion, serialNumber?, manufacturerId?, error? }], scannedRange }`
  - Each device entry contains the data that could be read; `error` is set per device
    if reading its properties failed (e.g. secured device without key).
  - Requires a live KNXnet/IP interface configured in ETS; otherwise `bus_unavailable`.
  - SDK: combines `NetworkManagement.IndividualAddressScanRangeAsync` with
    `DeviceManagement.GetDeviceDescriptor` / `ReadProperty` per found address. Uses the
    correct manufacturer PID (`PID_MANUFACTURER_ID`) for device identification.
- `device.readGroupObjects` `{ address, async? }` **(DUAL-MODE)**
  - Reads the association table / group-object table from a physical device on the bus
    to discover which group addresses each communication object is linked to. Intended
    for **project recovery** (reverse-engineering an existing installation).
  - `address`: individual address in `"area.line.device"` format.
  - **Default (async=false):** returns the result directly:
    `{ address, comObjects: [{ number, groupAddresses:["x/y/z"], flags:{c,r,w,t,u} }], partial: true, note }`
  - **async=true:** returns `{ jobId }`, polled via `job.status` (same result shape when
    done).
  - `flags` is a structured object: `c` (communication), `r` (read), `w` (write),
    `t` (transmit), `u` (update) -- each boolean.
  - `partial` is always `true`: this is a generic readout of what the device reports,
    not a full ETS-level reconstruction. KNX Secure objects may be unreadable.
  - `note` explains the limitations.
  - Requires a live KNXnet/IP interface; otherwise `bus_unavailable`.
  - SDK: reads the association/address/group-object tables via
    `LocateObject(OT_Associationtable)` + `PID_TABLE` / `DeviceManagement.ReadProperty`.
- `group.read` `{ gaRef }` -> `{ value }` (hex string or null on timeout)
   - Reads a group value from the bus. Timeout ~2.3 s (KNX standard).
   - During a monitor, uses that monitor's connection/line and its responses are
     available in the capture. Otherwise uses ETS's default group connection.
  - SDK: `KnxCommunicationSync.GroupCommunicationReadSync`.
- `group.monitor` `{ lineRef?, durationMs? }` -> `{ jobId }`
  - Records group telegrams for a limited duration. Long-running -> job model.
  - `lineRef` (default `"default"`), `durationMs` (1000-300000, default 10000).
  - Job result: `{ telegrams: [{ service, sourceAddress, groupAddress, value, timestamp }] }`
  - **Note (6.3.0):** `GroupMessageReceived` does NOT fire for KNX Secure telegrams.
   - SDK: `KnxCommunicationSync.GroupMessageReceived` event.
   - Capture is subscribed before the job ID is returned. Cleanup waits for any
     borrowed synchronous read/write before unsubscribing and disposing the connection.

Mutating (idempotencyKey required):

- `bus.setIndividualAddress` `{ deviceRef, currentAddress }` -> `{ jobId }`
  - **idempotencyKey required.** Programs the device's project individual address onto
    the bus via `StartOverwritingIndividualAddress`. `currentAddress` is the device's
    current physical address on the bus (needed to reach it). Long-running -> job model.
    Requires live KNXnet/IP.
- `device.reset` `{ deviceRef }` -> `{ ok }`
  - **idempotencyKey required.** Resets the physical device on the bus
    (`Root.ResetDevice`). Requires live KNXnet/IP.
- `group.write` `{ gaRef, value, less7Bits? }` -> `{ acknowledged }` (bool)
  - Writes a group value to the bus. `value` as hex string (e.g. `"01"`).
  - `less7Bits` (default false): true if the value is < 7 bits (fits in the APCI byte).
   - SDK: synchronous `KnxCommunicationSync.GroupCommunicationWrite`.
   - `acknowledged=true` means the SDK send returned without an exception, not that
     an actuator or downstream gateway applied the value. Use device feedback.

## References (Refs)

`ref` is a project-stable string identifier assigned by the AddIn (e.g. `Puid`
or `UniqueId` from the SDK). The MCP server treats refs as opaque.

Ref prefixes:
- `dev:` - Device
- `ga:` - GroupAddress
- `co:` - ComObjectInstanceRef
- `bp:` - BuildingPart
- `bf:` - BuildingFunction
- `mfr:` - Manufacturer
- `cat:` - CatalogItem
- `tr:` - Trade
- `seg:` - Segment
- `cert:` - Certificate
- `tag:{Id}` - Tag (ID-based, not positional)
- `todo:{Id}` - ToDoItem (ID-based, not positional)
- `ph:{Id}` - ProjectHistory (ID-based, not positional)
- `ch:` - ChannelInstance

## Project Recovery (reverse-engineering from the bus)

Real verification requires a live KNXnet/IP bus + hardware.

The MCP bridge supports pulling raw device data from a live KNX bus so an LLM can
compose or reconstruct a project from that data. The MCP only **pulls** raw data; the
LLM **composes** the project model (or `.knxproj` XML) from the collected information.

### Available recovery tools

| Tool                      | What it returns                                                    |
| ------------------------- | ------------------------------------------------------------------ |
| `bus.scanLine`            | List of responding individual addresses on a line (inventory)      |
| `bus.reconstructLine`     | Per-device identity: mask version, serial number, manufacturer ID  |
| `device.readGroupObjects` | Association table: which GAs each communication object is linked to|
| `device.readInfo`         | Mask version / device descriptor for a single device               |
| `group.monitor`           | Live group telegrams (observe traffic to infer function)           |

### Workflow

1. **Inventory:** `bus.scanLine` or `bus.reconstructLine` to discover devices on each
   line. `reconstructLine` additionally reads each device's identity (serial, mask,
   manufacturer).
2. **GA links:** `device.readGroupObjects` per device to read the association table
   (which group addresses each communication object is linked to, plus flags).
3. **Traffic observation:** `group.monitor` to watch live telegrams and infer which GAs
   are actively used and what they carry.
4. **Product matching:** the manufacturer ID and mask version from step 1 can be matched
   against the ETS product catalog (`catalog.manufacturers`, `catalog.search`,
   `catalog.browseProducts`) to identify the product and retrieve its application
   description. This is needed to interpret parameter memory.
5. **Composition:** the LLM assembles the collected data into a project structure
   (topology, group addresses, links, building view).

### Inherent limits (KNX facts)

Device memory does **not** contain:

- Device or GA **names** (these are project metadata, never downloaded to the device).
- **Building/room structure** or documentation.
- **Group range names** or hierarchy.
- **Parameter names** or human-readable descriptions (the device stores raw parameter
  values; interpreting them requires the manufacturer's application description from the
  catalog).

What device memory **does** contain:

- Individual address and association table (GA <-> communication object links).
- Communication object flags (C/R/W/T/U).
- Raw parameter values (interpretation requires the product's app description).
- Mask version and (if readable) serial number / manufacturer ID.

A full app-aware SDK reconstruction (using `Knx.Ets.Sdk` reconstruction/plugin APIs to
rebuild the project model from bus data inside ETS) remains a larger future spike. The
current approach deliberately keeps the MCP as a data puller, with the LLM performing
the composition logic.

## SDK constraints (see CLAUDE.md)

- DPT must not be read via `ComObject.DatapointType` (returns empty in ETS 6.3.0).
- `DetermineLinkSecurityImpacts` is 6.4-only; not used.
