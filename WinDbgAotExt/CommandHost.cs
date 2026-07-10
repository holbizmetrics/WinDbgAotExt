using System;
using System.Collections.Generic;
using System.Text;

namespace WinDbgAotExt;

public static unsafe class CommandHost
{
	public delegate int CommandHandler(IntPtr pDebugClient, IntPtr pDebugControl, IReadOnlyList<string> argv, string raw);

	private static readonly Dictionary<string, CommandHandler> _map = new(StringComparer.OrdinalIgnoreCase);

	public const uint EXT_VERSION_MAJOR = 1;
	public const uint EXT_VERSION_MINOR = 1;

	static CommandHost()
	{
		Register("hello", HelloHandler);
		Register("echo", EchoHandler);
		Register("version", VersionHandler);
		Register("clrtest", ClrTestHandler);   // Layer 2, step 3a: boot CoreCLR + Ping -> 4242
		Register("cs", CsHandler);             // Layer 2, step 3b: run live C# via Roslyn
		Register("wiltriage", WiltriageHandler); // triage the current break: benign vs fault + culprit
	}

	public static void Register(string name, CommandHandler handler) => _map[name] = handler;

	public static int Run(string name, IntPtr pDebugClient, byte* argsUtf8)
	{
		const int S_OK = 0, E_FAIL = unchecked((int)0x80004005);
		IntPtr pControl = IntPtr.Zero;

		try
		{
			string raw = Utf8ZToString(argsUtf8);
			var argv = Argv(raw);

			// NEW: guard null debug client (WinDbg can pass NULL in some contexts)
			if (pDebugClient != IntPtr.Zero)
			{
				if (DbgEng.QueryInterface(pDebugClient, DbgEng.IID_IDebugControl, out pControl) != S_OK)
					pControl = IntPtr.Zero;
			}

			if (_map.TryGetValue(name, out var handler))
				return handler(pDebugClient, pControl, argv, raw);

			if (pControl != IntPtr.Zero)
				DbgEng.DbgOutLine(pControl, $"Unknown command '{name}'");

			return E_FAIL;
		}
		catch
		{
			if (pControl != IntPtr.Zero) DbgEng.DbgOutLine(pControl, "Command error.");
			return E_FAIL;
		}
		finally
		{
			if (pControl != IntPtr.Zero) DbgEng.Release(pControl);
		}
	}

	// --- Handlers ---

	private static int HelloHandler(IntPtr _, IntPtr ctrl, IReadOnlyList<string> argv, string raw)
	{
		if (argv.Count == 0)
			DbgEng.DbgOutLine(ctrl, "Hello from C# Native AOT! (no args)");
		else
			DbgEng.DbgOutLine(ctrl, $"Hello from C# Native AOT! args=[{string.Join(", ", argv)}]");
		return 0;
	}

	private static int EchoHandler(IntPtr _, IntPtr ctrl, IReadOnlyList<string> __, string raw)
	{
		DbgEng.DbgOutLine(ctrl, raw ?? string.Empty);
		return 0;
	}

	private static int VersionHandler(IntPtr _, IntPtr ctrl, IReadOnlyList<string> __, string ___)
	{
		DbgEng.DbgOutLine(ctrl, $"{EXT_VERSION_MAJOR}.{EXT_VERSION_MINOR}");
		return 0;
	}

	// Layer 2, step 3a: prove the AOT extension can boot CoreCLR in the debugger's process.
	private static int ClrTestHandler(IntPtr _, IntPtr ctrl, IReadOnlyList<string> __, string ___)
	{
		var err = ClrHost.EnsureBooted();
		if (err != null) { DbgEng.DbgOutLine(ctrl, "CLR host boot FAILED: " + err); return unchecked((int)0x80004005); }
		int r = ClrHost.Ping();
		DbgEng.DbgOutLine(ctrl, $"CLR Ping returned: {r}  (expected 4242)");
		return 0;
	}

	// Layer 2, step 3b/2c: run a live C# expression via Roslyn in the hosted CoreCLR, handing the
	// script the debugger client (first handler arg) so it can reach the live target (Dbg.Exec, ...).
	private static int CsHandler(IntPtr client, IntPtr ctrl, IReadOnlyList<string> __, string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) { DbgEng.DbgOutLine(ctrl, "usage: !cs <C# expression>"); return 0; }
		DbgEng.DbgOutLine(ctrl, ClrHost.Eval(raw, client));
		return 0;
	}

	// !wiltriage : one-word triage of the current break. Reads the exception code from `.lastevent`
	// (0x80000003 = an int3, a deliberate break, never a hardware fault) and the culprit module from `k`,
	// then reports mechanism-not-meaning: a breakpoint is "deliberate, process alive" (possibly a tripped
	// assert/WIL check), and a first-chance AV is distinguished from a real 2nd-chance fault. Packages the
	// classifier proven at the !cs prompt so a recurring WIL/DebugBreak -- e.g. WSL's wsldevicehost inside a
	// DllHost COM surrogate -- is one command. The logic lives in the bridge's WilTriage.Classify (real,
	// unit-tested C#); this handler just feeds it the two command outputs. See README "Roadmap: native
	// fault-triage surface". v1 = classify; v2 (decode WHY the WIL check tripped) needs symbol-scoped
	// frame-local reads the extension does not have yet.
	private static int WiltriageHandler(IntPtr client, IntPtr ctrl, IReadOnlyList<string> __, string ___)
	{
		DbgEng.DbgOutLine(ctrl, ClrHost.Eval(WiltriageScript, client));
		return 0;
	}

	// Feeds `.lastevent` + the best available stack to the compiled, unit-tested classifier (WilTriage.Classify).
	// On a crash dump the current thread is the dump-writer, not the crash site, so prefer the stored exception
	// context: `.ecxr` sets it and the following `k` walks the real faulting stack. Detected by `.ecxr` printing
	// a register context ("rip="/"eip="); on a live target with no stored exception `.ecxr` fails harmlessly
	// ("Unable to get exception context") and `k` stays the current stack -- verified, no corruption.
	private const string WiltriageScript =
		"var lastEvent = debugger.Run(\".lastevent\");\n" +
		"var ecxr = debugger.Run(\".ecxr\");\n" +
		"var hasException = ecxr.Contains(\"rip=\") || ecxr.Contains(\"eip=\");\n" +
		"return WinDbgAotExt.Bridge.WilTriage.Classify(lastEvent, debugger.Run(\"k\"), hasException);\n";

	// --- Utils ---

	private static string Utf8ZToString(byte* p)
	{
		if (p == null) return string.Empty;
		byte* t = p; while (*t != 0) t++;
		int len = checked((int)(t - p));
		return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(p, len));
	}

	internal static List<string> Argv(string input)
	{
		var list = new List<string>();
		if (string.IsNullOrWhiteSpace(input)) return list;

		var sb = new StringBuilder();
		bool inQuotes = false;

		for (int i = 0; i < input.Length; i++)
		{
			char c = input[i];

			if (c == '\\' && i + 1 < input.Length && (input[i + 1] == '"' || input[i + 1] == '\\'))
			{ sb.Append(input[i + 1]); i++; continue; }

			if (c == '"') { inQuotes = !inQuotes; continue; }

			if (!inQuotes && char.IsWhiteSpace(c))
			{ if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }

			sb.Append(c);
		}

		if (sb.Length > 0) list.Add(sb.ToString());
		return list;
	}
}
