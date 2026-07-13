#Requires -Version 5.1
<#
.SYNOPSIS
  Native load-harness for the WinDbgAotExt NativeAOT extension - the "load-test without WinDbg".

  The xUnit suite only exercises the managed Argv parser. It does NOT verify the thing that
  actually makes this a working WinDbg extension: that the AOT-compiled DLL emits the right
  EXPORT symbols and that they can be CALLED natively without crashing. This harness closes
  that gap by doing exactly what dbgeng.dll does at `.load` time:

    1. LoadLibrary the AOT DLL
    2. Resolve every expected export (DebugExtensionInitialize/Uninitialize/Notify + commands)
    3. Call DebugExtensionInitialize -> assert S_OK (0) AND *version == 0x00010001 (v1.1)
    4. Call each command with a NULL debug client -> asserts the null-guard path returns S_OK
       (CommandHost skips QueryInterface on a null client; DbgOutLine no-ops on IntPtr.Zero)
    5. Negative control: a bogus export name must NOT resolve

  It is NOT a substitute for a real `.load` in WinDbg (no live IDebugControl, so command OUTPUT
  is not exercised) - but it proves the AOT export ABI end-to-end, which is the riskiest part.

.PARAMETER Dll
  Path to the published native DLL. Defaults to the Release AOT publish output.
#>
param(
  [string]$Dll = "$PSScriptRoot\..\WinDbgAotExt\bin\Release\net9.0-windows\win-x64\publish\WinDbgAotExt.dll"
)
$ErrorActionPreference = 'Stop'

$resolved = Resolve-Path -LiteralPath $Dll -ErrorAction SilentlyContinue
if(-not $resolved){ Write-Host "FAIL: native DLL not found at '$Dll' (build it: dotnet publish -c Release -r win-x64)" -ForegroundColor Red; exit 2 }
$Dll = $resolved.Path
Write-Host "Harness target: $Dll"
Write-Host ("DLL size: {0:N0} bytes  built: {1}" -f (Get-Item $Dll).Length, (Get-Item $Dll).LastWriteTime)
Write-Host ""

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Loader {
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)] public static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Ansi)]    public static extern IntPtr GetProcAddress(IntPtr moduleHandle, string name);
    [DllImport("kernel32")] public static extern bool FreeLibrary(IntPtr moduleHandle);

    // On win-x64 there is a single calling convention, so the delegate convention is moot.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int InitializeDelegate(ref uint version, ref uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int CommandDelegate(IntPtr client, IntPtr args);

    public static int CallInit(IntPtr functionPointer, out uint version, out uint flags){
        version = 0; flags = 0;
        var initializeDelegate = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(functionPointer, typeof(InitializeDelegate));
        return initializeDelegate(ref version, ref flags);
    }
    public static int CallCmd(IntPtr functionPointer){
        var commandDelegate = (CommandDelegate)Marshal.GetDelegateForFunctionPointer(functionPointer, typeof(CommandDelegate));
        return commandDelegate(IntPtr.Zero, IntPtr.Zero);   // null client + null args = the guarded/safe path
    }
}
'@

$pass = 0; $fail = 0
function Check($name, $cond, $detail){
  if($cond){ Write-Host ("  PASS  {0,-32} {1}" -f $name, $detail) -ForegroundColor Green; $script:pass++ }
  else     { Write-Host ("  FAIL  {0,-32} {1}" -f $name, $detail) -ForegroundColor Red;   $script:fail++ }
}

$moduleHandle = [Loader]::LoadLibrary($Dll)
Check "LoadLibrary" ($moduleHandle -ne [IntPtr]::Zero) ("handle=0x{0:X}" -f $moduleHandle.ToInt64())
if($moduleHandle -eq [IntPtr]::Zero){ Write-Host "cannot continue - DLL failed to load (LastError $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))" -ForegroundColor Red; exit 1 }

# 1. every expected export must resolve
$exports = 'DebugExtensionInitialize','DebugExtensionUninitialize','DebugExtensionNotify','hello','echo','version'
$exportAddresses = @{}
foreach($exportName in $exports){
  $exportAddress = [Loader]::GetProcAddress($moduleHandle, $exportName)
  $exportAddresses[$exportName] = $exportAddress
  Check "export: $exportName" ($exportAddress -ne [IntPtr]::Zero) ("addr=0x{0:X}" -f $exportAddress.ToInt64())
}

# 2. negative control: a name that must NOT exist
$bogus = [Loader]::GetProcAddress($moduleHandle, 'this_export_does_not_exist')
Check "negative-control" ($bogus -eq [IntPtr]::Zero) "bogus name correctly did not resolve"

# 3. DebugExtensionInitialize -> S_OK + version 0x00010001 (major<<16 | minor = 1.1)
if($exportAddresses['DebugExtensionInitialize'] -ne [IntPtr]::Zero){
  $extensionVersion = 0; $flags = 0
  $hresult = [Loader]::CallInit($exportAddresses['DebugExtensionInitialize'], [ref]$extensionVersion, [ref]$flags)
  Check "Init returns S_OK" ($hresult -eq 0) ("hresult=0x{0:X8}" -f $hresult)
  Check "Init version 0x00010001" ($extensionVersion -eq 0x00010001) ("version=0x{0:X8} (expected 0x00010001 = v1.1)" -f $extensionVersion)
  Check "Init flags 0" ($flags -eq 0) ("flags=0x{0:X8}" -f $flags)
}

# 4. commands with a null client -> guarded path returns S_OK without crashing
foreach($commandName in 'hello','echo','version'){
  if($exportAddresses[$commandName] -ne [IntPtr]::Zero){
    $hresult = [Loader]::CallCmd($exportAddresses[$commandName])
    Check "call '$commandName' (null client)" ($hresult -eq 0) ("hresult=0x{0:X8} - dispatched + returned without crash" -f $hresult)
  }
}

[void][Loader]::FreeLibrary($moduleHandle)

Write-Host ""
Write-Host ("RESULT: {0} passed, {1} failed" -f $pass, $fail) -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
exit $(if($fail -eq 0){0}else{1})
