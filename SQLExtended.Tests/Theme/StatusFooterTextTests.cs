using SQLExtended.Theme;
using Xunit;

namespace SQLExtended.Tests.Theme;

/// <summary>A status line ending in an ellipsis means work is running, and the footer shows the busy look.</summary>
public class StatusFooterTextTests
{
    [Theory]
    [InlineData("Exporting to C:\\Schemas\u2026")]
    [InlineData("Searching...")]
    [InlineData("Connecting\u2026  ")]
    [InlineData("Reading waits\u2026  (2 of 5)")]
    [InlineData("Validating tables\u2026  (12/40)")]
    [InlineData("Exporting Sales \u2014 Tables, 1,200 files written\u2026")]
    public void InProgressText_IsBusy(string text) => Assert.True(StatusFooterText.LooksBusy(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("80 object scripts written to C:\\Schemas\\New folder")]
    [InlineData("Opened. (Press F5 to run \u2014 execute command unavailable.)")]
    [InlineData("Ready \u2014 type to search")]
    [InlineData("Copied to clipboard.")]
    [InlineData("Scripted Sales \u2014 Tables: 120 in 1.2s, 14 queries")]
    public void SettledText_IsNotBusy(string text) => Assert.False(StatusFooterText.LooksBusy(text));
}
