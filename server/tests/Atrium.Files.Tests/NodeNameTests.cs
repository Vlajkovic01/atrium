using Atrium.Core.Files;

namespace Atrium.Files.Tests;

public sealed class NodeNameTests
{
    [Theory]
    [InlineData("Report.pdf")]
    [InlineData("holiday photo 2024.jpg")]
    [InlineData(".gitignore")]
    [InlineData("Ćevapi i šljivovica")]
    [InlineData("a")]
    [InlineData("file:with?odd*chars")]
    [InlineData("CONFIG")]
    [InlineData("console.log")]
    public void Ordinary_names_are_accepted(string input)
    {
        Assert.True(NodeName.TryNormalize(input, out var name, out _));
        Assert.Equal(input, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("nul\0byte")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("   ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("Lpt1.tar.gz")]
    [InlineData("aux ")]
    public void Names_that_are_unsafe_or_impossible_on_some_system_are_refused(string input)
    {
        Assert.False(NodeName.TryNormalize(input, out _, out _));
    }

    [Fact]
    public void A_name_longer_than_255_characters_is_refused()
    {
        Assert.True(NodeName.TryNormalize(new string('a', 255), out _, out _));
        Assert.False(NodeName.TryNormalize(new string('a', 256), out _, out _));
    }

    [Fact]
    public void Names_differing_only_by_case_or_composition_share_a_key()
    {
        // "č" as one character, and as "c" followed by a combining caron.
        NodeName.TryNormalize("Račun.PDF", out var composed, out var a);
        NodeName.TryNormalize("račun.pdf", out var decomposed, out var b);

        Assert.Equal(a, b);
        Assert.Equal("Račun.PDF", composed);
        Assert.Equal("račun.pdf", decomposed);
    }

    [Theory]
    [InlineData("Report.pdf", 2, "Report (2).pdf")]
    [InlineData("archive.tar.gz", 3, "archive.tar (3).gz")]
    [InlineData("Makefile", 2, "Makefile (2)")]
    [InlineData(".bashrc", 2, ".bashrc (2)")]
    public void Numbered_names_keep_the_extension(string name, int number, string expected)
    {
        Assert.Equal(expected, NodeName.Numbered(name, number));
    }

    [Fact]
    public void A_numbered_name_stays_within_the_limit()
    {
        var numbered = NodeName.Numbered(new string('a', 251) + ".txt", 12);

        Assert.Equal(NodeName.MaxLength, numbered.Length);
        Assert.EndsWith(" (12).txt", numbered, StringComparison.Ordinal);
        Assert.True(NodeName.TryNormalize(numbered, out _, out _));
    }
}
