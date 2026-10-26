using SQLExtended.Cache;
using SQLExtended.Search;
using Xunit;

namespace SQLExtended.Tests.Search;

/// <summary>
/// Pins SQL Search's "Match whole word" (issue #9) and the SQLite LIKE escaping. Both fail silently on screen:
/// a word boundary drawn in the wrong place either buries the term again or hides the hit, and an unescaped
/// <c>_</c> turned every column-name search into a wildcard.
/// </summary>
public class SearchTextMatcherTests
{
    [Theory]
    [InlineData("person")]
    [InlineData("dbo.person")]
    [InlineData("[person]")]
    [InlineData("WHERE person = 1")]
    [InlineData("SELECT * FROM PERSON")]
    [InlineData("person.name")]
    [InlineData("(person)")]
    public void WholeWord_FindsTheTermStandingAlone(string text) => Assert.True(SearchTextMatcher.Contains(text, "person", wholeWord: true));

    /// <summary>
    /// Identifier characters are word characters, so <c>@person</c>, <c>#person</c> and <c>person_id</c> are
    /// different names, not the column <c>person</c>.
    /// </summary>
    [Theory]
    [InlineData("personal")]
    [InlineData("personification")]
    [InlineData("PersonAddress")]
    [InlineData("vPerson")]
    [InlineData("person_id")]
    [InlineData("@person")]
    [InlineData("#person")]
    [InlineData("person$")]
    [InlineData("person2")]
    public void WholeWord_SkipsTheTermInsideALongerName(string text) => Assert.False(SearchTextMatcher.Contains(text, "person", wholeWord: true));

    [Fact]
    public void WholeWord_KeepsLookingPastAPartialMatch()
    {
        Assert.Equal(13, SearchTextMatcher.IndexOf("personal and person", "person", 0, wholeWord: true));
        Assert.Equal(13, SearchTextMatcher.IndexOf("personal and person", "person", 1, wholeWord: true));
    }

    /// <summary>A term that starts or ends with a non-word character is already its own edge on that side.</summary>
    [Theory]
    [InlineData("[person]", "SELECT [person].id")]
    [InlineData("@id", "WHERE x=@id")]
    [InlineData("dbo.person", "FROM dbo.person p")]
    [InlineData(".person", "dbo.person")]
    public void WholeWord_OnlyChecksTheSidesWhereTheTermIsAWord(string term, string text) => Assert.True(SearchTextMatcher.Contains(text, term, wholeWord: true));

    [Fact]
    public void WholeWord_StillChecksTheWordSideOfAMixedTerm()
    {
        Assert.False(SearchTextMatcher.Contains("dbo.personal", "dbo.person", wholeWord: true));
        Assert.False(SearchTextMatcher.Contains("x=@identity", "@id", wholeWord: true));
    }

    [Fact]
    public void Substring_IsUnchangedWithTheBoxOff()
    {
        Assert.True(SearchTextMatcher.Contains("PersonAddress", "person", wholeWord: false));
        Assert.Equal(-1, SearchTextMatcher.IndexOf(null, "person", 0, wholeWord: false));
        Assert.Equal(-1, SearchTextMatcher.IndexOf("person", "", 0, wholeWord: false));
    }

    [Fact]
    public void WholeWord_DoesNotRunOffTheEnd()
    {
        Assert.Equal(-1, SearchTextMatcher.IndexOf("personX", "person", 0, wholeWord: true));
        Assert.Equal(-1, SearchTextMatcher.IndexOf("per", "person", 0, wholeWord: true));
        Assert.Equal(-1, SearchTextMatcher.IndexOf("person", "person", 6, wholeWord: true));
    }

    /// <summary>
    /// SQLite's LIKE treats <c>%</c> and <c>_</c> as wildcards; the query declares <c>ESCAPE '\'</c>. It has no
    /// <c>[…]</c> class, so unlike the msdb pattern a bracket passes through as itself.
    /// </summary>
    [Theory]
    [InlineData("person", "%person%")]
    [InlineData("person_id", @"%person\_id%")]
    [InlineData("50%", @"%50\%%")]
    [InlineData(@"C:\Data", @"%C:\\Data%")]
    [InlineData("[dbo]", "%[dbo]%")]
    public void SqliteLikePattern_EscapesItsWildcards(string term, string expected) => Assert.Equal(expected, SearchTextMatcher.BuildSqliteLikePattern(term));

    private static JobStepMatch Step(string command, string stepName = "Load", string jobName = "Nightly") =>
        new() { Command = command, StepName = stepName, JobName = jobName };

    /// <summary>
    /// Under whole word the server only matched substrings, so Classify is the match: a step with no whole-word
    /// hit in any field is rejected, not kept with the generic label the substring search gives it.
    /// </summary>
    [Fact]
    public void JobStep_WholeWord_RejectsASubstringOnlyRow()
    {
        Assert.False(JobStepSearchService.Classify(Step("EXEC dbo.usp_LoadPersonal;"), "person", wholeWord: true));
        Assert.True(JobStepSearchService.Classify(Step("EXEC dbo.usp_LoadPersonal;"), "person"));
    }

    [Fact]
    public void JobStep_WholeWord_SnippetsTheWholeWordHitNotTheFirstSubstring()
    {
        var step = Step("UPDATE dbo.personal SET x = 1; DELETE dbo.person WHERE id = 2;");
        Assert.True(JobStepSearchService.Classify(step, "person", wholeWord: true));

        Assert.Equal("Command", step.MatchedIn);
        Assert.Contains("DELETE dbo.person", step.Snippet);
    }

    [Fact]
    public void JobStep_WholeWord_FallsBackToTheNames()
    {
        var step = Step("EXEC dbo.usp_personal;", stepName: "Step one", jobName: "Person load");
        Assert.True(JobStepSearchService.Classify(step, "person", wholeWord: true));
        Assert.Equal("JobName", step.MatchedIn);
    }
}
