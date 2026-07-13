# WinDbgAotExt

A **C# NativeAOT WinDbg extension** — and, more importantly, the crash-proof native bridge for a larger goal.

## The dream (why this exists)

A **live C# scripting engine inside WinDbg.** Host the C# runtime compiler (Roslyn) in the
extension so that, sitting at a WinDbg prompt against a live process or a dump, you **write C# on
the fly and it compiles and runs immediately against the target** — no edit → rebuild → reload
cycle. It is the C# answer to WinDbg's built-in JavaScript provider (`dx` / `.scriptload`), but
with the full weight of C# and the entire .NET library ecosystem behind it.

What that unlocks (**shipped now** — see [Status](#status)): running live C#, *calling / piping /
reformatting* commands, *reading target memory* (`debugger.ReadU64`), *querying loaded modules as typed
objects* (`debugger.Modules`), and *walking the managed heap as typed objects* (`debugger.Heap`) — the
leak-hunt below — all work today against a live target:

- **Query the debuggee like a database.** Expose threads, modules, the heap, handles as queryable
  sources and use **LINQ** as your debugger query language:
  ```csharp
  Debuggee.Heap.Objects
      .GroupBy(o => o.TypeName)
      .Select(g => new { Type = g.Key, Count = g.Count(), Bytes = g.Sum(o => o.Size) })
      .OrderByDescending(x => x.Bytes)          // leak hunt: biggest types first
  ```
- **Call and pipe existing commands.** Run any WinDbg command (`IDebugControl::Execute`), capture
  its output, then parse / LINQ / transform it in C# and **pipe** the result onward — wrapping the
  whole battle-tested command surface (`!analyze`, `k`, `lm`, `!heap`, …).
- **Reformat output to be understandable.** Turn a cryptic wall of dbgeng text into a sorted table,
  a filtered summary, a diff — raw output in, readable view out.
- **Automate triage.** Prototype an analysis in ten lines and run it interactively; script memory
  reads via `IDebugDataSpaces`; walk native structures declaratively.

For chasing something like a misbehaving native client, that is the "tremendous help."

## Why NativeAOT — the two-layer architecture

The dream cannot be "an AOT extension that compiles and runs C#," because **NativeAOT has no JIT**:
Roslyn compiles C# to IL, IL needs a JIT to execute, and AOT has no runtime code generation
(`Expression.Compile()` — the engine behind `IQueryable` / expression-tree LINQ — throws under AOT).
So the design is two layers:

- **Layer 1 — the AOT native bridge (this repo today).** A dependency-free DLL that loads cleanly
  into dbgeng and crosses the native ↔ managed boundary *safely*: exceptions are caught before they
  can escape an `[UnmanagedCallersOnly]` export (an escaping managed exception has no native unwind
  info and fail-fasts the debugger), vtable calls use exact signatures and indices, and no
  GC-movable pointer escapes to the caller. **This is the "safely enter and return" problem** that
  makes these extensions crash — solved and tested here.
- **Layer 2 — hosted CoreCLR + Roslyn (the north star).** The AOT bridge boots a full JIT-capable
  runtime in-process (via `nethost` / `hostfxr`), and *that* runtime hosts Roslyn and runs your live
  C# — LINQ, expression trees, `async`, reflection, the whole language. The AOT shim is the seatbelt;
  CoreCLR is the engine.

## Status

**Layer 1 — complete.** The extension loads into a real debugger, dispatches commands, prints
output through the live `IDebugControl::Output` path, and returns cleanly — no crash.

- Exports: `DebugExtensionInitialize` (reports the version declared in `CommandHost.EXT_VERSION_*`,
  today v1.3), `DebugExtensionUninitialize`, `DebugExtensionNotify`, plus commands `hello`, `echo`,
  `version`, `clrtest`, `cs`, `csreset` (clear the persistent `!cs` session), `csvars` (list the
  session's variables), `fields` (inspect one managed object's fields by address), and `wiltriage`
  (break triage — see the Roadmap section).
- Command dispatch through `CommandHost` with a UTF-8 arg parser; output via the `IDebugControl`
  vtable (`Output` is index **14**, not 8 — a real bug fixed in `0a4dcbc`, **confirmed live**).
- **Three independent test layers, all green:**
  - `WinDbgAotExt.Tests` — xUnit, **45/45**: the `Argv` parser (11), **the native Output path**
    (9, including the printf-format-string class — `Output` is a *varargs* method, so a `%s` in
    echoed text used to make the engine dereference a garbage pointer), the `WilTriage` classifier
    goldens (9), and the typed last-event decoder + typed-classify goldens (16).
    `DbgEngOutputTests` hand-builds a mock `IDebugClient`/`IDebugControl` with real native
    vtables, puts a capturing function at `Output` index 14, and asserts the exact bytes each
    command emits through `enter → QueryInterface → dispatch → Output → return`. No WinDbg needed.
  - `tools/load-harness.ps1` — native ABI proof *without WinDbg*, **18/18**: LoadLibrary the AOT
    DLL, resolve **all 10 exports** (including the CLR-backed `clrtest`/`cs`/`csreset`/`wiltriage`,
    so a trimming change that silently dropped the headline command cannot stay green), call
    `DebugExtensionInitialize` (assert `S_OK` + the version the source declares), dispatch the
    commands on the null-client path, negative-control a bogus export name.
  - **Live `.load` in cdb** — the definitive test against real dbgeng:
    ```
    cdb -c ".load WinDbgAotExt.dll; !hello world; !version; q" cmd.exe
      -> Hello from C# Native AOT! args=[world]
      -> 1.3
    ```
    loads, prints via the real Output vtable[14], and quits with no crash. (The Store WinDbg
    package ships a scriptable `cdb.exe` under `…\amd64\cdb.exe`.)

**Layer 2 — core working.** The extension boots CoreCLR in-process (via `hostfxr`) and runs live
C# through Roslyn. Load it and type C# at the debugger prompt:

```
cdb> .load WinDbgAotExt.dll
cdb> !clrtest
CLR Ping returned: 4242
cdb> !cs 1 + 2
3
cdb> !cs Enumerable.Range(1,10).Where(x => x % 2 == 0).Sum()
30
cdb> !cs debugger.Exec("? 5 + 5")                        # run a WinDbg command from C#
Evaluate expression: 10 = 0xa
cdb> !cs debugger.Run("lm").Split('\n').Where(l => l.Length > 0).Count()   # LINQ over its output
21
cdb> !cs debugger.ReadU32(0x7ffe0000).ToString("x8")    # read raw target memory (matches `dd`)
00000000
cdb> !cs debugger.Modules.OrderByDescending(m => m.Size).First().Name   # LINQ the loaded modules
SHELL32
cdb> !cs debugger.Heap.Objects.Count(o => o.TypeName == "Widget")       # walk the managed GC heap (ClrMD)
1000
cdb> !cs debugger.Heap.Objects.GroupBy(o => o.TypeName).OrderByDescending(g => g.Sum(o => (long)o.Size)).First().Key
System.Byte[]                                                           # the leak-hunt: biggest type by total bytes
```

**`!cs` is a SESSION, not a calculator.** State persists across invocations (Roslyn `ScriptState`
continuation), so a variable declared in one command is still there in the next — build up an
investigation step by step instead of re-typing one giant expression:

```
cdb> !cs var big = debugger.Heap.Objects.Where(o => o.Size > 85000).ToList()
(no value -- declaration stored in the !cs session)
cdb> !cs big.Count                       # 'big' survived
57
cdb> !cs big.GroupBy(o => o.TypeName).OrderByDescending(g => g.Count()).First().Key
System.Byte[]
cdb> !csreset                            # drop every declared variable
!cs session reset -- all script variables dropped.
```

Two things it handles for you: **the debugger eats semicolons** (WinDbg/cdb treat `;` as a *command
separator*, so a trailing `;` never reaches C# — a failed compile is retried with it restored), and
a failed submission **leaves the session intact** (a typo doesn't wipe your variables). `debugger` is
re-bound on every submission, so it never talks to a stale debug client. `!csvars` lists what you've
declared; `!csreset` clears it.

**`!fields <address>` — the inspector.** The census (`!cs debugger.Heap.Objects…`) finds *which*
object; `!fields` reads *what's inside* one, its instance fields by name / declared type / value.
Object-reference fields print the referent's address so you drill in with another `!fields`:

```
cdb> !cs debugger.Heap.Objects.First(o => o.TypeName == "Widget").Address.ToString("x")
19b3c445360
cdb> !fields 0x19b3c445360
  System.Int32 <Id>k__BackingField = 0
  System.String <Label>k__BackingField = "widget-0"
```

Accepts `0x`-prefixed, bare, or WinDbg backtick-grouped addresses. Managed objects only (it reads
the CLR heap via ClrMD); a native target says so. This is the ClrMD heap-object twin of the
frame-local struct read on the Roadmap (the `wil::FailureInfo` decode).

Scripts reach the live target through a `debugger` object (the debug client, handed in per command):
- `debugger.Exec("cmd")` — run a WinDbg command.
- `debugger.Run("cmd")` — run it **and return the output as a string**, so you can parse / LINQ /
  reformat it (the "call → pipe → reformat" pillar).
- `debugger.ReadBytes(addr, n)` / `ReadU64(addr)` / `ReadU32(addr)` — read raw target memory
  (`IDebugDataSpaces::ReadVirtual`), cross-checked to agree with the debugger's own `dd`.
- `debugger.Modules` — the loaded modules as **typed objects** (`Name` / `Start` / `End` / `Size`),
  so you LINQ the process's module state directly:
  `debugger.Modules.Where(m => m.Name.StartsWith("K")).OrderByDescending(m => m.Size)`.
  (Parsed from `lm` today; a future slice can back it with `IDebugSymbols`.)
- `debugger.Heap.Objects` — the debuggee's **managed GC heap** as typed objects
  (`TypeName` / `Address` / `Size`), so you LINQ the process *state* itself — `GroupBy(TypeName)` for a
  leak-hunt, filter by type, count instances. Backed by **ClrMD** (`Microsoft.Diagnostics.Runtime`),
  which attaches to the *same* dbgeng session via `DataTarget.CreateFromDbgEng` — no separate attach.
  Only meaningful against a **managed (.NET) debuggee** (`debugger.Heap.ClrPresent` is `false` on a
  native target — there is no CLR heap to walk). Verified live: 1000 allocated objects found as exactly
  1000. (Each access re-walks the heap; a caching/streaming refinement is a later slice.)

Every dbgeng call goes through vtable indices verified against `dbgeng.h` (`IDebugControl::Execute`=66,
`IDebugClient::Get/SetOutputCallbacks`=33/34, `IDebugOutputCallbacks::Output`=3,
`IDebugDataSpaces::ReadVirtual`=3), each anchored by the known-good `Output`=14 — never guessed (a
wrong index is this project's signature crash).

- Native AOT (no CoreCLR of its own) → `hostfxr_initialize_for_runtime_config` is the *first* init in
  the debugger process, which is why hosting works (a managed host fails `0x80008081`).
- `bridge` = the managed "brain" (Roslyn + the `Debugger` debuggee surface); `host` =
  the standalone AOT-hosts-CoreCLR spike; `WinDbgAotExt/ClrHost.cs` boots the runtime behind
  `!clrtest` / `!cs`.
- Deploy = the extension DLL + a `bridge/` subfolder (bridge DLL + Roslyn deps + ClrMD deps +
  runtimeconfig). The **bridge targets net10.0** (the AOT extension stays net9): ClrMD 4.0's net10 build
  and the net10 `System.Reflection.Metadata` / `System.Collections.Immutable` must unify, so the hosted
  CoreCLR the bridge boots is **.NET 10** — a machine running the extension needs the .NET 10 runtime
  installed. hostfxr boots whatever the bridge's runtimeconfig requests; the AOT shim is version-agnostic.
- **Remaining (all optional — the hard architecture is done):** `debugger.Modules` and
  `debugger.Heap` ship the typed-queryable-object model (modules via `lm`; the managed heap via ClrMD).
  Left: *threads* / *handles* as typed objects, and heap-object *field* access (read an object's members
  by name, not just its type/size) — the deeper ClrMD surface. Optionally swap raw `CSharpScript` for
  `EvaluatorLib` for globals ergonomics (both are net10 now, so the framework mismatch is gone).

## Roadmap: native fault-triage surface (`!wiltriage`)

Driving specimen (2026-07): a `wil::details::DebugBreak` fired from `wsldevicehost.dll` inside a
`DllHost.exe` COM surrogate (WSL's device host), on Windows 11 24H2 after the servicing bump
26100.8521 -> 26100.8655. It *looks* like a crash in WinDbg but isn't: `AeDebug\Auto=1`
auto-attaches the debugger to a routine WIL break, and there is **zero crash telemetry** (no WER
report, no Application-Error event) -- the process never dies. The break is noise amplified by the
postmortem-debugger registration, not a fault. This one specimen split cleanly into "ships today"
and "the gap", and drove both.

**Ships today -- the classification (`!wiltriage` v1).** The command reads the exception code from
`.lastevent` and the culprit module from `k`, and reports mechanism-not-meaning. The logic is
`WinDbgAotExt.Bridge.WilTriage.Classify` (real, unit-tested C#); `!wiltriage` just feeds it the two
command outputs. Live-proven in cdb; every path is covered by `WinDbgAotExt.Tests/WilTriageTests.cs`.
On the driving specimen it reports:

```
DELIBERATE int3 break in wsldevicehost -- no hardware fault, process is alive. Likely benign
(WIL/loader/manual break) but may be a tripped assert/WIL check -- 'g' to continue, or decode the
reason. [code=80000003 1st-chance, top=KERNELBASE!wil::details::DebugBreak+0x2]
```

Two lessons banked by *running and auditing* it, not by reasoning:
- **Key on the exception code, not a symbol allowlist.** An early cut matched only
  `wil::details::DebugBreak` / `DbgBreakPoint` and misfired on `ntdll!LdrpDoDebuggerBreak` (a benign
  loader break) -- calling it a fault. `0x80000003` is an int3, deliberate by definition, never a
  hardware fault.
- **The code is the mechanism, not the meaning.** A TRIAD/KG audit caught v1 overclaiming "BENIGN /
  no crash occurred" for *every* int3 -- but a `__debugbreak` assert, a WIL check, or a
  heap-corruption break is also `0x80000003` and is a real failure. So a breakpoint is reported
  "deliberate, process alive (possibly a tripped assert/WIL check)", a failure-marker frame
  (`RtlpBreakPointHeap`/`FailFast`/`_assert`) is flagged for investigation, and a first-chance AV is
  distinguished from a real 2nd-chance fault. The loader-break and specimen cases are pinned in
  `WilTriageTests.cs` so the classifier cannot regress.

**The gap -- decoding *why* the WIL check tripped (`!wiltriage` v2).** WIL stashes the real reason
(HRESULT, file, line, function, message) in a `wil::FailureInfo` struct; this break's exception
record carried `Parameter[0]=0`, so the reason is only recoverable by reading that struct out of
the failing frame's locals. The extension cannot do this yet. Four capabilities, none currently on
the Remaining list above, are required -- in priority order:

1. ~~`debugger.LastEvent`~~ **SHIPPED**: exception code + record as a typed object
   (`IDebugControl::GetLastEventInformation`, vtable slot 94, header-verified) plus
   `debugger.IsDumpTarget` (`GetDebuggeeType`, slot 34 -- dump targets honestly demote chance to
   `unknown`). `!wiltriage` now feeds on the typed object; `.lastevent` text parsing survives only
   as the fallback when the typed call fails. Live-proven both arms (live target: `1st chance`
   matching cdb's banner; minidump: `chance-unknown` + `via .ecxr`). Decoder offsets unit-tested
   in `LastEventInfoTests`.
2. `debugger.Stack` -- stack frames as typed objects (module / offset / **symbol** / disp), the
   native twin of `debugger.Modules`.
3. a symbol resolver -- address -> `module!symbol+disp` via `IDebugSymbols` (the slice the Modules
   note already anticipates).
4. frame-scoped local / typed-struct read -- pull `wil::FailureInfo` from a frame's locals; the
   native twin of the ClrMD heap-object *field* access above.

Honest blocker on v2 for this specimen specifically: `wsldevicehost` has no public PDBs, so even
with (1)-(4) the local read needs private symbols Microsoft does not ship. The capabilities are
still worth building -- they apply to any symbol-available native target -- but this exact break
stays classify-only until symbols exist. That the by-hand diagnosis and the extension hit the
*same* wall (symbol-scoped local reads) is the finding: the missing feature and the missing
diagnosis are one capability.

## Install & run in WinDbg

Nothing works until the extension is loaded -- so this is the load-bearing part. It runs in both
**WinDbgX** (the Store / Preview GUI) and **cdb** (the scriptable console) -- same dbgeng under both.

**Prerequisites:** the **.NET 10 runtime** (the hosted CoreCLR the bridge boots -- `dotnet --list-runtimes`
should list a `Microsoft.NETCore.App 10.0.x`). To *build* it you also need the .NET SDK + the VS C++
toolchain (for the AOT native link).

**What you load** is a deploy bundle = `WinDbgAotExt.dll` **plus a `bridge/` subfolder next to it**
(the bridge DLL + its Roslyn/ClrMD deps + runtimeconfig). `deploy/` is gitignored (a build
artifact), so a fresh clone has to build it:

```powershell
# 1. the AOT extension DLL (vswhere must be on PATH for the native link -- it lives in
#    "C:\Program Files (x86)\Microsoft Visual Studio\Installer")
dotnet publish WinDbgAotExt/WinDbgAotExt.csproj -c Release -r win-x64
# 2. the bridge (net10) + all its deps
dotnet build bridge/WinDbgAotExt.Bridge.csproj -c Release
# 3. assemble the bundle: put the published WinDbgAotExt.dll next to a `bridge/` folder holding the
#    bridge build output (WinDbgAotExt.Bridge.dll + deps + *.runtimeconfig.json).
#    The working bundle in this repo is deploy/ -- mirror that layout.
```

**Load and use** -- in the WinDbgX command window or cdb, at any break:

```
.load C:\path\to\WinDbgAotExt.dll     # needs the bridge/ subfolder alongside it
!wiltriage                            # triage the current break: deliberate int3 vs real fault + culprit
```

The first `!wiltriage` (or `!cs`) boots CoreCLR in-process -- a ~1-2 s pause, once per session. cdb
one-liner: `cdb -c ".load <path>\WinDbgAotExt.dll; !wiltriage; q" <target.exe>`.

## Build & test

```powershell
# Build the AOT extension DLL (needs the .NET SDK + the VS C++ toolchain for the native link step)
dotnet publish WinDbgAotExt/WinDbgAotExt.csproj -c Release -r win-x64
#   -> WinDbgAotExt/bin/Release/net9.0-windows/win-x64/publish/WinDbgAotExt.dll

# Unit tests (managed)
dotnet test WinDbgAotExt.Tests/WinDbgAotExt.Tests.csproj

# Native ABI harness (no WinDbg required)
powershell -ExecutionPolicy Bypass -File tools/load-harness.ps1
```

To load it in WinDbg once you have the DLL:

```
.load C:\path\to\WinDbgAotExt.dll   (needs the bridge/ subfolder alongside it — see Status)
!hello world
!version
!wiltriage                                        # triage the current break (benign vs fault + culprit)
!cs Enumerable.Range(1,10).Sum()                  # live C# + LINQ
!cs debugger.Run("lm").Split('\n').Length         # LINQ over a command's output
```

## Layout

| File | Role |
|------|------|
| `WinDbgAotExt/Exports.cs` | `[UnmanagedCallersOnly]` exports dbgeng calls at `.load` |
| `WinDbgAotExt/CommandHost.cs` | command registry + dispatch + UTF-8 `Argv` parser; catches everything so nothing escapes the boundary |
| `WinDbgAotExt/DbgEngInterop.cs` | minimal COM-vtable interop (`QueryInterface`/`Release`/`Output`) for AOT |
| `WinDbgAotExt/ClrHost.cs` | boots CoreCLR via `hostfxr` + calls the bridge (behind `!clrtest` / `!cs`) |
| `bridge/Bridge.cs` | managed Roslyn engine + the `Debugger` debuggee surface (`Exec` / `Run` / `ReadU64` / `Modules` / `Heap`) |
| `bridge/WilTriage.cs` | pure break-triage classifier behind `!wiltriage` (compiled into the bridge, linked into the tests) |
| `bridge/LastEventInfo.cs` | typed last-event POCO + pure `DEBUG_LAST_EVENT_INFO_EXCEPTION` buffer decoder (offsets unit-tested; feeds `WilTriage` typed path) |
| `host/Host.cs` | standalone AOT-hosts-CoreCLR spike (proves the seam without WinDbg) |
| `tools/heaptarget/` | a managed test debuggee (allocates 1000 known objects, parks) for exercising `debugger.Heap` |
| `tools/heapwalk.cdb` | cdb script: attach, `.load`, LINQ the heap — the `debugger.Heap` live test |
| `tools/wiltriage.cdb` | cdb script: `.load`, boot CoreCLR, `!wiltriage` — the break-triage live test |
| `WinDbgAotExt.Tests/ArgvTests.cs` | xUnit parser coverage |
| `WinDbgAotExt.Tests/DbgEngOutputTests.cs` | mock `IDebugControl` — tests the Output vtable[14] path without WinDbg |
| `WinDbgAotExt.Tests/WilTriageTests.cs` | golden tests for every `WilTriage.Classify` path (no debugger needed) |
| `tools/load-harness.ps1` | native load-test without WinDbg |
