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

	// --- Utils ---

	private static string Utf8ZToString(byte* p)
	{
		if (p == null) return string.Empty;
		byte* t = p; while (*t != 0) t++;
		int len = checked((int)(t - p));
		return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(p, len));
	}

	private static List<string> Argv(string input)
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
