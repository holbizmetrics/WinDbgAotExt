# WinDbgAotExt

A **C# NativeAOT WinDbg extension** — and, more importantly, the crash-proof native bridge for a larger goal.

## The dream (why this exists)

A **live C# scripting engine inside WinDbg.** Host the C# runtime compiler (Roslyn) in the
extension so that, sitting at a WinDbg prompt against a live process or a dump, you **write C# on
the fly and it compiles and runs immediately against the target** — no edit → rebuild → reload
cycle. It is the C# answer to WinDbg's built-in JavaScript provider (`dx` / `.scriptload`), but
with the full weight of C# and the entire .NET library ecosystem behind it.

What that unlocks (**illustrative — this is the Layer 2 *goal*, NOT yet built.** Today the extension
has only the `hello` / `echo` / `version` demo commands; the snippets below are the vision, not
shipped code — see [Status](#status)):

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

- Exports: `DebugExtensionInitialize` (reports v1.1), `DebugExtensionUninitialize`,
  `DebugExtensionNotify`, plus demo commands `hello`, `echo`, `version`.
- Command dispatch through `CommandHost` with a UTF-8 arg parser; output via the `IDebugControl`
  vtable (`Output` is index **14**, not 8 — a real bug fixed in `0a4dcbc`, **confirmed live**).
- **Three independent test layers, all green:**
  - `WinDbgAotExt.Tests` — xUnit, **17/17**: the `Argv` parser (11) **plus the native Output path**
    (6). `DbgEngOutputTests` hand-builds a mock `IDebugClient`/`IDebugControl` with real native
    vtables, puts a capturing function at `Output` index 14, and asserts the exact bytes each
    command emits through `enter → QueryInterface → dispatch → Output → return`. No WinDbg needed.
  - `tools/load-harness.ps1` — native ABI proof *without WinDbg*, **14/14**: LoadLibrary the AOT
    DLL, resolve every export, call `DebugExtensionInitialize` (assert `S_OK` + version
    `0x00010001`), dispatch the commands on the null-client path.
  - **Live `.load` in cdb** — the definitive test against real dbgeng:
    ```
    cdb -c ".load WinDbgAotExt.dll; !hello world; !version; q" cmd.exe
      -> Hello from C# Native AOT! args=[world]
      -> 1.1
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
```

- Native AOT (no CoreCLR of its own) → `hostfxr_initialize_for_runtime_config` is the *first* init in
  the debugger process, which is why hosting works (a managed host fails `0x80008081`).
- `layer2/bridge` = the managed "brain" (Roslyn); `layer2/host` = the standalone AOT-hosts-CoreCLR
  spike; `WinDbgAotExt/ClrHost.cs` = the same, ported into the extension behind `!clrtest` / `!cs`.
- Deploy = the extension DLL + a `bridge/` subfolder (bridge DLL + Roslyn deps + runtimeconfig).
- **Remaining:** swap raw `CSharpScript` for the operator's `EvaluatorLib` (globals support; needs a
  net9↔net10 align), then expose the debuggee (`IDebugDataSpaces`, threads, heap) as script globals →
  **LINQ over the live process** (the illustrative snippets at the top of this file).

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
.load C:\path\to\WinDbgAotExt.dll
!hello world
!version
```

## Layout

| File | Role |
|------|------|
| `WinDbgAotExt/Exports.cs` | `[UnmanagedCallersOnly]` exports dbgeng calls at `.load` |
| `WinDbgAotExt/CommandHost.cs` | command registry + dispatch + UTF-8 `Argv` parser; catches everything so nothing escapes the boundary |
| `WinDbgAotExt/DbgEngInterop.cs` | minimal COM-vtable interop (`QueryInterface`/`Release`/`Output`) for AOT |
| `WinDbgAotExt.Tests/ArgvTests.cs` | xUnit parser coverage |
| `WinDbgAotExt.Tests/DbgEngOutputTests.cs` | mock `IDebugControl` — tests the Output vtable[14] path without WinDbg |
| `tools/load-harness.ps1` | native load-test without WinDbg |
