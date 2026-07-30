using WinDbgAotExt;
using Xunit;

namespace WinDbgAotExt.Tests;

/// <summary>
/// Locks the hostfxr init classification (persona sweep 2026-07-30, P1).
///
/// Why these exist: the shipped guard was <c>hresult &lt; 0</c>, which treats hostfxr's documented
/// success-with-caveat codes 1 (Success_HostAlreadyInitialized) and 2
/// (Success_DifferentRuntimeProperties) as a clean boot — even though both mean a runtime was
/// already hosted and the bridge's requested .NET 10 config may not be the one loaded.
///
/// MEASURED before writing any of this (`Host --probe-double-init`, AOT, hostfxr 10.x): a second
/// <c>hostfxr_initialize_for_runtime_config</c> with the first context still open does not return
/// at all — it blocks. So in production the pre-flight <c>DetectExistingClr</c> is the guard that
/// actually fires; this classifier is the second line, for configurations where hostfxr does
/// return one of the caveat codes.
/// </summary>
public class ClrHostInitTests
{
	[Fact]
	public void PlainSuccessWithContextProceeds()
	{
		Assert.Null(ClrHost.ClassifyInitResult(0, hasContext: true));
	}

	[Fact]
	public void SuccessButNoContextIsRefused()
	{
		string? problem = ClrHost.ClassifyInitResult(0, hasContext: false);
		Assert.NotNull(problem);
		Assert.Contains("no host context", problem);
	}

	[Theory]
	[InlineData(1)]  // Success_HostAlreadyInitialized
	[InlineData(2)]  // Success_DifferentRuntimeProperties
	public void AlreadyInitializedCodesAreRefusedNotTreatedAsSuccess(int hresult)
	{
		// The regression this whole file exists for: these are >= 0, so the old guard passed them.
		Assert.True(hresult >= 0, "precondition: the code under test is a POSITIVE hresult");
		string? problem = ClrHost.ClassifyInitResult(hresult, hasContext: true);
		Assert.NotNull(problem);
		Assert.Contains("ALREADY initialized", problem);
		Assert.Contains(ClrHost.ForceBootVariable, problem);   // the refusal names its own escape hatch
	}

	[Fact]
	public void ManagedHostFailureCarriesItsSpecificHint()
	{
		string? problem = ClrHost.ClassifyInitResult(unchecked((int)0x80008081), hasContext: false);
		Assert.NotNull(problem);
		Assert.Contains("0x80008081", problem);
		Assert.Contains("cdb", problem);       // says what to DO, not just what failed
	}

	[Fact]
	public void OtherFailuresStillReportTheirCode()
	{
		string? problem = ClrHost.ClassifyInitResult(unchecked((int)0x80008093), hasContext: false);
		Assert.NotNull(problem);
		Assert.Contains("0x80008093", problem);
		Assert.DoesNotContain("cdb", problem); // the managed-host hint must not fire on every failure
	}

	[Theory]
	[InlineData("1", true)]
	[InlineData("true", true)]
	[InlineData("TRUE", true)]
	[InlineData("yes", true)]
	[InlineData(" 1 ", true)]
	[InlineData("0", false)]
	[InlineData("false", false)]
	[InlineData("", false)]
	[InlineData("   ", false)]
	[InlineData(null, false)]
	[InlineData("maybe", false)]
	public void ForceBootParsingAcceptsOnlyAffirmatives(string? value, bool expected)
	{
		Assert.Equal(expected, ClrHost.IsForceBootRequested(value));
	}
}
