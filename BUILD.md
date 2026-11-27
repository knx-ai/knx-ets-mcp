# Building the AddIn yourself

How to build the ETS AddIn (`Knx.EtsBridge.Addin.dll`) from source, for ETS6 and/or
ETS5, and package a sideload zip. The build runs on macOS, Linux, or Windows — you do
**not** need Windows or Mono to compile.

For sideloading a finished build see the `install.bat` in a release zip; for the MCP
server see [`mcp-server/`](mcp-server/).

## Prerequisites

- **.NET SDK 8.0 or newer** (tested with 10.0). On macOS: `brew install dotnet`.
  `Microsoft.NETFramework.ReferenceAssemblies` (a NuGet package, restored automatically)
  lets the SDK cross-compile the net48 target on any OS.
- **The ETS SDK DLLs** — proprietary KNX material, **not** included in this repo. You must
  supply them yourself (see below). This is the only manual step.

## Provide the ETS SDK DLLs

The project references four SDK assemblies with `<Private>false</Private>` (they are
**not** shipped in the output — ETS provides them at runtime). You only need them to
compile against. Get them from the official KNX SDK downloads (KNX login required):

- ETS6 SDK v6.4.0: <https://support.knx.org/hc/en-us/articles/31860110892690-ETS6-SDK-v6-4-0>
- ETS5 SDK v5.7.2: <https://support.knx.org/hc/en-us/articles/360006115700-ETS5-SDK-v5-7-2>

Or copy them from an ETS installation directory. The four files:

| File |
|------|
| `Knx.Ets.Sdk.dll` |
| `Knx.Ets.Common.Types.dll` |
| `Knx.Ets.Sdk.AddIns.AddInViews.dll` |
| `Knx.Ets.Common.Resources.dll` |

Put them in a folder and point `EtsDllPath` at it on the command line:

```bash
# ETS6 (default target -- runs on ETS 6.3 and 6.4)
dotnet build addin -c Release -p:"EtsDllPath=/path/to/ets6-sdk/"

# ETS5
dotnet build addin -c Release -p:Ets5=true -p:"EtsDllPath=/path/to/ets5-sdk/"
```

On Windows, if ETS6 is installed, the default `EtsDllPath` is `C:\Program Files (x86)\ETS6\`,
so no override is needed there.

### Which SDK version to build against

The SDK is strong-named with a build-exact version. **Build against the SDK that matches
your target ETS** (or an older one): an app built against an older SDK runs on a newer
ETS, but not vice versa. The default ETS6 build targets the **6.4 SDK**; the resulting
DLL runs on both ETS 6.3 and 6.4. The ETS5 target builds against SDK 5.7. The AddIn
logs the loaded SDK version at runtime; you verify compatibility yourself.

## Build

SDK-independent monitor-connection regression tests can run without ETS DLLs:

```bash
dotnet test addin-tests -c Release
```

On Windows PowerShell, an optional offline check against the installed ETS SDK
demonstrates its group-address conversion contract without opening a bus connection:

```powershell
.\tools\check-group-address-contract.ps1
```

The check rejects a project DOM `GroupAddress` object and accepts its numeric
`UInt16` address or an address string. Group read/write calls must pass the
numeric address, not the DOM object, despite their first parameter being `object`.

From the repo root:

```bash
# ETS6 (default target, SDK 6.4)
dotnet build addin -c Release

# ETS5 (SDK 5.x)
dotnet build addin -c Release -p:Ets5=true
```

Output: a **single self-contained** `addin/bin/Release/net48/Knx.EtsBridge.Addin.dll`.
JSON uses **Newtonsoft.Json**, which is **embedded as a manifest resource** and loaded via
an `AppDomain.AssemblyResolve` handler -- so there are no loose dependency DLLs to ship
(ETS' AddIn loader does not probe the AddIn folder for dependencies). Both builds are
expected to be **0 warnings / 0 errors**.

### What `-p:Ets5=true` changes

- Swaps `EtsDllPath` to the ETS5 SDK folder and defines the `ETS5` compile constant.
- Excludes `Host/ConfigurationDialog.cs` (ETS6-only `IModalDialog`).
- `#if ETS5` guards handle the SDK deltas: project identity uses `Project.ProjectId`
  (ETS5) vs `Project.ProjectGuid` (ETS6); group-address strings are formatted from
  `AddressValue`; and features absent in the ETS5 SDK are stubbed as `not_supported`
  (`firmware.update`, `catalog.import`, `bus.scanLine`/`reconstructLine`, `group.monitor`,
  `segment.*`, `tag.*`, segment navigation).
- ETS5 has no configuration-dialog contract, so the ETS5 build configures the TCP
  endpoint / token directly in the AddIn panel (`GetAddInUI`).

## Package a sideload zip (ETS5 + ETS6)

A release zip has this layout — smart install scripts at the root plus one payload folder
per ETS version, each with its own separately-built DLL and version-matching manifest:

```
install.bat / install.ps1 / uninstall.bat
ets6/<AppId>/  Knx.EtsBridge.Addin.dll (6.4 build) + AddInManifest.xml + deps
ets6/<AppId>.signature            (ETS directory signature, beside the folder)
ets5/<AppId>/  Knx.EtsBridge.Addin.dll (5.7 build) + AddInManifest.xml + deps
ets5/<AppId>.signature            (ETS directory signature, beside the folder)
```

`install.ps1` holds the real logic (kept readable so a user can inspect it): it detects
each installed ETS version (via `%ProgramData%\KNX\ETS<n>`) and copies the matching
payload into `%ProgramData%\KNX\ETS<n>\Apps\AddIns\<AppId>\`, then prints a prominent
"next step: start the MCP server" banner. `install.bat` is a thin double-click launcher
for it, with a guard: if `install.ps1` is not next to it (i.e. it was run from inside the
zip, where the native Windows Explorer zip handler unpacks only the single clicked file to
a random `%temp%` folder), it prints a loud "extract the whole zip first" banner instead
of a cryptic PowerShell `-File` error. `uninstall.bat` is self-contained (inline
PowerShell). The installer is named **exactly `install.bat`** on purpose: WinZip's "Unzip
and Install" recognizes a program named `setup`/`install` and extracts the *whole* archive
to a temp folder before running it (WinZip KB 130526), so the payload is present — a
numbered/prefixed name would break that recognition. The MCP server exe ships as
`Start-MCP-Server.exe`.

To assemble it, build both targets and copy the single self-contained AddIn DLL —
never ship the proprietary `Knx.Ets.*` SDK DLLs:

```
Knx.EtsBridge.Addin.dll   (the only DLL; Newtonsoft.Json is embedded inside it)
```

plus the matching manifest (`addin/AddInManifest.xml` for ETS6,
`addin/AddInManifest.ets5.xml` renamed to `AddInManifest.xml` for ETS5) and `icon.png`.
Guard the result: `find <pkg> -iname 'Knx.Ets.*.dll'` must be empty.

### Sign the payload folders

Sign each `<AppId>` folder (writes `<AppId>.signature` next to it):

```bash
python3 -m venv .sign/venv && .sign/venv/bin/pip install pyuca
export ETS_CONVERTER_KEY="$(cat /path/to/converter-key.xml)"   # <RSAKeyValue> XML
.sign/venv/bin/python .sign/sign.py <pkg>/ets6/M0FFF-A0001
.sign/venv/bin/python .sign/sign.py <pkg>/ets5/M0FFF-A0001
.sign/venv/bin/python .sign/sign.py <pkg>/ets6/M0FFF-A0001 --check   # verify
```

`ETS_CONVERTER_KEY` is the ETS "converter" RSA key (a fixed 1024-bit key embedded in
ETS — tamper detection, not secrecy). In the release pipeline it is the
`ETS_CONVERTER_KEY` GitHub Actions secret; locally you can obtain it the same way you
obtained it for the repo secret. The signer needs `pyuca` (a pure-Python UCA
collation, used to reproduce .NET's `StringComparer.InvariantCulture` ordering) and the
full `<RSAKeyValue>` (with `D`) to sign.

## Notes

- **Signature:** each AddIn payload folder is signed with ETS's own *directory
  signature* so ETS treats the sideloaded folder as signed (the file-copied variant,
  as opposed to a signed `.etsapp`). `tools/sign_addin.py` reproduces the algorithm
  from `Knx.Ets.XmlSigning` (`DirectorySigner` + `AddInSigning`): SHA-1 per file,
  `relpath:base64(sha1)` entries sorted with .NET `StringComparer.InvariantCulture`
  (via `pyuca`), SHA-1 over the comma-joined string, RSA-PKCS#1 v1.5 (SHA-1) with the
  fixed ETS "converter" key, written to `<AppId>.signature` **next to** the folder.
  The release pipeline signs the payload in the `release` job (before zipping); the key
  is the `ETS_CONVERTER_KEY` repo secret (the `<RSAKeyValue>` XML). Without that secret
  the payload is built **unsigned** (ETS still runs it, but flags the folder as not
  signed). `install.ps1` copies the `.signature` into `...\AddIns\` beside the app
  folder; `uninstall.bat` removes it. For a *registered* distribution you would instead
  use a signed `.etsapp` with a KNX Association-registered manufacturer ID (the
  `M0FFF-A0001` AppId here is a placeholder).
- **Single self-contained DLL:** Newtonsoft.Json is embedded as a manifest resource and
  resolved from an `AppDomain.AssemblyResolve` handler, so the AddIn ships as one DLL with
  no loose dependencies. (System.Text.Json was removed because its dependency chain could
  not bind in-process on .NET Framework inside ETS.)
