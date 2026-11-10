using Atrium.Core.Identity;

namespace Atrium.Core.Tests;

public sealed class UserNameTests
{
    [Theory]
    [InlineData("ana", "ana", "ana")]
    [InlineData("  Ana  ", "Ana", "ana")]
    [InlineData("Stefan.V-01_x", "Stefan.V-01_x", "stefan.v-01_x")]
    [InlineData("Петар", "Петар", "петар")]
    [InlineData("Đorđe", "Đorđe", "đorđe")]
    public void Valid_names_keep_their_case_and_get_a_case_folded_key(string input, string name, string key)
    {
        Assert.True(UserName.TryNormalize(input, out var actualName, out var actualKey));
        Assert.Equal(name, actualName);
        Assert.Equal(key, actualKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("back\\slash")]
    [InlineData("..")]
    [InlineData("-_-")]
    [InlineData("a@b")]
    [InlineData("tab\tname")]
    [InlineData("null\0byte")]
    public void Invalid_names_are_rejected(string? input)
    {
        Assert.False(UserName.TryNormalize(input, out _, out _));
    }

    [Fact]
    public void Names_longer_than_the_limit_are_rejected()
    {
        Assert.True(UserName.TryNormalize(new string('a', UserName.MaxLength), out _, out _));
        Assert.False(UserName.TryNormalize(new string('a', UserName.MaxLength + 1), out _, out _));
    }

    [Fact]
    public void Composed_and_decomposed_forms_of_a_name_share_one_key()
    {
        var composed = "José";
        var decomposed = "José";

        Assert.True(UserName.TryNormalize(composed, out _, out var key1));
        Assert.True(UserName.TryNormalize(decomposed, out _, out var key2));

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Lookup_key_matches_the_stored_key_whatever_case_was_typed()
    {
        Assert.True(UserName.TryNormalize("Ana", out _, out var stored));

        Assert.Equal(stored, UserName.KeyOf(" ANA "));
    }
}
