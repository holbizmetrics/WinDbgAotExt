using System.Runtime.CompilerServices;

// Exposes internal members (e.g. CommandHost.Argv) to the unit-test project so the
// pure-logic command-line parser can be tested without a debugger. The DbgEng COM
// layer + the [UnmanagedCallersOnly] exports remain integration-test territory.
[assembly: InternalsVisibleTo("WinDbgAotExt.Tests")]
