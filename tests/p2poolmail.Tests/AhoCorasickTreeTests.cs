using p2poolmail;

namespace p2poolmail.Tests;

public class AhoCorasickTreeTests
{
    [Fact]
    public void Constructor_NullKeywords_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AhoCorasickTree(null!));
    }

    [Fact]
    public void Constructor_EmptyKeywords_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AhoCorasickTree([]));
    }

    [Fact]
    public void Constructor_NonAsciiPattern_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AhoCorasickTree(["你好"]));
    }

    [Fact]
    public void Contains_SingleKeyword_Found()
    {
        var tree = new AhoCorasickTree(["SHARE FOUND"]);
        Assert.True(tree.Contains("... SHARE FOUND ..."));
        Assert.False(tree.Contains("nothing here"));
    }

    [Fact]
    public void Contains_IsAsciiCaseInsensitive()
    {
        var tree = new AhoCorasickTree(["got a payout"]);
        Assert.True(tree.Contains("GOT A PAYOUT"));
        Assert.True(tree.Contains("Got a Payout"));
        Assert.True(tree.Contains("got a payout"));
    }

    [Fact]
    public void Contains_NonAsciiText_RestartsAtRootWithoutThrowing()
    {
        var tree = new AhoCorasickTree(["error"]);
        // Non-ASCII chars map to -1 and reset to root; must not throw. The match
        // after the non-ASCII run must still be found.
        Assert.True(tree.Contains("こんにちは错误: error 你好"));
        Assert.False(tree.Contains("こんにちは错误你好"));
    }

    [Fact]
    public void FirstMatch_ReturnsEarliestEndingPatternId()
    {
        var tree = new AhoCorasickTree(["payout", "got a payout"]);
        // "got a payout" ends at the same position as "payout" but "payout" alone
        // also ends at index of 't' in a text containing only "payout".
        Assert.Equal(0, tree.FirstMatch("payout"));
        Assert.Equal(1, tree.FirstMatch("got a payout"));
        Assert.Equal(-1, tree.FirstMatch("no match"));
    }

    [Fact]
    public void FirstMatch_IsCaseInsensitive()
    {
        var tree = new AhoCorasickTree(["ZMQ is not running"]);
        Assert.Equal(0, tree.FirstMatch("zmq IS NOT RUNNING"));
    }

    [Fact]
    public void Search_FindsOverlappingPatterns_WithCorrectOffsets()
    {
        var tree = new AhoCorasickTree(["he", "she", "hers"]);
        var hits = tree.Search("ushers").ToList();

        // In "ushers": "she" starts at 1, "hers" starts at 2, "he" starts at 2.
        Assert.Contains(hits, h => h.Key == 1 && h.Value == 1); // she
        Assert.Contains(hits, h => h.Key == 0 && h.Value == 2); // he
        Assert.Contains(hits, h => h.Key == 2 && h.Value == 2); // hers
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Search_EmptyText_YieldsNothing()
    {
        var tree = new AhoCorasickTree(["x"]);
        Assert.Empty(tree.Search(""));
    }

    [Fact]
    public void Search_NullText_Throws()
    {
        var tree = new AhoCorasickTree(["x"]);
        Assert.Throws<ArgumentNullException>(() => tree.Search(null!).ToList());
    }

    [Fact]
    public void Contains_PatternAtVeryStart_Matches()
    {
        var tree = new AhoCorasickTree(["SHARE FOUND"]);
        Assert.True(tree.Contains("SHARE FOUND first line"));
    }

    [Fact]
    public void Contains_KeywordAcrossRepeatedPattern_MatchesEachOccurrence()
    {
        var tree = new AhoCorasickTree(["ab"]);
        Assert.True(tree.Contains("xxabxx"));
        Assert.True(tree.Contains("abab"));
    }
}
