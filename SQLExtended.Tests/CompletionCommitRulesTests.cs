using System;
using System.Linq;
using SQLExtended.IntelliSense;
using Xunit;

namespace SQLExtended.Tests;

/// <summary>
/// Issue #11: <c>DELETE FROM</c> + Space became <c>DELETE DATEFROMPARTS</c>. Space ends every word, so any item
/// hard-selected when a word is finished replaces it. These pin the two rules that stop that for a whole class of
/// words, rather than one missing keyword at a time.
/// </summary>
public class CompletionCommitRulesTests
{
    [Theory]
    [InlineData("FROM", "DATEFROMPARTS")]       // the reported case
    [InlineData("FROM", "DATETIME2FROMPARTS")]
    [InlineData("IS", "ISNULL")]                // WHERE x IS + Space
    [InlineData("IN", "INT")]
    [InlineData("AS", "ASCII")]
    [InlineData("ON", "ONLINE")]
    [InlineData("TOP", "TOPOLOGY")]
    public void Space_does_not_replace_a_finished_word_with_a_different_item(string typed, string item)
    {
        Assert.False(CompletionCommitRules.SpaceMayCommit(typed, item, item));
    }

    [Theory]
    [InlineData("FROM", "FROM")]
    [InlineData("Cust", "dbo.Customer")]
    [InlineData("Customer", "dbo.Customer")]
    [InlineData("GETD", "GETDATE")]
    public void Space_still_commits_a_prefix_match(string typed, string item)
    {
        Assert.True(CompletionCommitRules.SpaceMayCommit(typed, item, item));
    }

    [Fact]
    public void Space_does_not_commit_when_nothing_was_typed()
    {
        Assert.False(CompletionCommitRules.SpaceMayCommit("", "dbo.Customer", "dbo.Customer"));
    }

    [Fact]
    public void Space_does_not_commit_a_multi_word_item_on_its_first_word()
    {
        Assert.False(CompletionCommitRules.SpaceMayCommit("CREATE", "CREATE TABLE", "CREATE TABLE"));
        Assert.True(CompletionCommitRules.SpaceMayCommit("CREATE T", "CREATE TABLE", "CREATE TABLE"));
    }

    [Fact]
    public void A_bracketed_form_in_the_filter_text_counts_as_a_prefix_match()
    {
        // t.[Ong is how anyone reaches a column with a space in it; that must stay hard-selected.
        Assert.True(CompletionCommitRules.IsWordStartMatch("[Ong", "Ongoing Qty", "Ongoing Qty [Ongoing Qty]"));
        Assert.False(CompletionCommitRules.IsWordStartMatch("going", "Ongoing Qty", "Ongoing Qty [Ongoing Qty]"));
    }

    [Fact]
    public void No_reserved_word_can_be_replaced_by_a_built_in_function_on_Space()
    {
        // The broad version of the reported bug: for every reserved word a user might finish, no function in the
        // list may be space-committed over it.
        string[] words = { "FROM", "IS", "IN", "AS", "ON", "OR", "AND", "NOT", "BY", "TOP", "SET", "END", "CASE", "WHEN", "THEN", "ELSE", "INTO", "WHERE", "NULL" };
        var offenders = (from w in words
                         from f in SqlBuiltInFunctions.All
                         where !string.Equals(f.Name, w, StringComparison.OrdinalIgnoreCase)
                         where CompletionCommitRules.SpaceMayCommit(w, f.Name, f.Name)
                         select $"{w} -> {f.Name}").ToList();
        Assert.Empty(offenders);
    }
}
