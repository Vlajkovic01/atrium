namespace Atrium.Storage.Tests;

public sealed class StorageKeyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("blobs/ab/cd/abcdef0123456789")]
    [InlineData("staging/0190a5c2-upload")]
    [InlineData("backups/2026-11-10.tar.gz")]
    [InlineData("фото/Летовање 2024/слика 1.jpg")]
    [InlineData("with space/and.dots/file.name.ext")]
    [InlineData(".hidden/file")]
    [InlineData("console/null.txt")]
    public void Ordinary_keys_are_valid(string key)
    {
        Assert.True(StorageKey.IsValid(key));
        StorageKey.Validate(key);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/etc/passwd", "empty segment")]
    [InlineData("a//b", "empty segment")]
    [InlineData("a/", "empty segment")]
    [InlineData("..", "relative segment")]
    [InlineData("../outside", "relative segment")]
    [InlineData("a/../../outside", "relative segment")]
    [InlineData("a/./b", "relative segment")]
    [InlineData("a\\..\\outside", "forbidden character")]
    [InlineData("C:/Windows/system32", "forbidden character")]
    [InlineData("file.txt:stream", "forbidden character")]
    [InlineData("what?", "forbidden character")]
    [InlineData("star*", "forbidden character")]
    [InlineData("pipe|name", "forbidden character")]
    [InlineData("null\0byte", "control character")]
    [InlineData("new\nline", "control character")]
    [InlineData("trailing.", "trailing dot or space")]
    [InlineData("trailing ", "trailing dot or space")]
    [InlineData("a/dir./file", "trailing dot or space")]
    [InlineData("NUL", "reserved device name")]
    [InlineData("a/con.txt", "reserved device name")]
    [InlineData("a/LPT1", "reserved device name")]
    [InlineData(".atrium-tmp/write-1", "reserved name")]
    [InlineData(".ATRIUM-TMP/write-1", "reserved name")]
    public void Dangerous_or_ambiguous_keys_are_rejected_with_a_reason(string key, string reason)
    {
        Assert.False(StorageKey.IsValid(key));
        var error = Assert.Throws<InvalidStorageKeyException>(() => StorageKey.Validate(key));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(key, error.Key);
    }

    [Fact]
    public void The_error_message_does_not_repeat_the_key()
    {
        var error = Assert.Throws<InvalidStorageKeyException>(() => StorageKey.Validate("../secret-name"));

        Assert.DoesNotContain("secret-name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Length_limits_are_enforced()
    {
        Assert.True(StorageKey.IsValid(new string('a', StorageKey.MaxSegmentBytes)));
        Assert.False(StorageKey.IsValid(new string('a', StorageKey.MaxSegmentBytes + 1)));
        // Two bytes per character in UTF-8: half as many characters fit.
        Assert.False(StorageKey.IsValid(new string('ш', 128)));

        var segment = new string('a', 100);
        var longKey = string.Join('/', Enumerable.Repeat(segment, 11));
        Assert.True(longKey.Length > StorageKey.MaxLength);
        Assert.False(StorageKey.IsValid(longKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("blobs")]
    [InlineData("blobs/")]
    [InlineData("blobs/ab")]
    public void Listing_prefixes_may_be_empty_or_end_in_a_slash(string prefix)
    {
        StorageKey.ValidatePrefix(prefix);
    }

    [Theory]
    [InlineData("../")]
    [InlineData("/")]
    [InlineData("a//")]
    [InlineData(".atrium-tmp/")]
    public void Listing_prefixes_follow_the_same_safety_rules(string prefix)
    {
        Assert.Throws<InvalidStorageKeyException>(() => StorageKey.ValidatePrefix(prefix));
    }

    [Fact]
    public void A_missing_key_is_not_valid()
    {
        Assert.False(StorageKey.IsValid(null));
    }
}
