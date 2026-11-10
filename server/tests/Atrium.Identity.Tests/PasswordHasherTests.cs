using Atrium.Identity.Passwords;

namespace Atrium.Identity.Tests;

public sealed class PasswordHasherTests
{
    // Small cost keeps the tests fast; the algorithm and format are the same.
    private static readonly Argon2Settings Fast = new(MemoryKib: 1024, Passes: 1);
    private readonly PasswordHasher _hasher = new(Fast);

    [Fact]
    public void A_hash_verifies_with_the_right_password_only()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.Equal(PasswordVerification.Success, _hasher.Verify("correct horse battery staple", hash));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("correct horse battery stapl", hash));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void A_hash_is_in_phc_format_and_does_not_contain_the_password()
    {
        var hash = _hasher.Hash("hunter2hunter2");

        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", hash, StringComparison.Ordinal);
        Assert.Equal(6, hash.Split('$').Length);
    }

    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        Assert.NotEqual(_hasher.Hash("same password"), _hasher.Hash("same password"));
    }

    [Fact]
    public void A_hash_with_a_salt_length_this_server_never_produces_fails_without_throwing()
    {
        // An eight-byte salt ("somesalt"). libsodium only accepts sixteen
        // bytes, so this must be refused before it reaches the library.
        const string foreign =
            "$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc";

        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("password", foreign));
    }

    [Fact]
    public void A_stored_hash_verifies_after_the_hasher_is_recreated()
    {
        var hash = new PasswordHasher(Fast).Hash("my password");

        Assert.Equal(PasswordVerification.Success, new PasswordHasher(Fast).Verify("my password", hash));
    }

    [Fact]
    public void A_hash_made_with_other_settings_verifies_and_asks_for_a_rehash()
    {
        var old = new PasswordHasher(new Argon2Settings(MemoryKib: 512, Passes: 1)).Hash("my password");

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, _hasher.Verify("my password", old));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("not my password", old));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=2$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$c29tZXNhbHQ")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$$")]
    [InlineData("$argon2id$v=19$m=abc,t=1,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$c29tZXNhbHQ$!!!notbase64!!!")]
    public void Malformed_stored_hashes_fail_without_throwing(string stored)
    {
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("anything", stored));
    }

    [Theory]
    [InlineData("$argon2id$v=19$m=2000000000,t=1,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=1024,t=5000,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    public void A_stored_hash_demanding_absurd_cost_is_refused_instead_of_computed(string stored)
    {
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("anything", stored));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("short", false)]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("a perfectly reasonable passphrase", true)]
    public void Policy_requires_a_minimum_length(string? password, bool acceptable)
    {
        Assert.Equal(acceptable, PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void Policy_caps_the_length()
    {
        Assert.True(PasswordPolicy.IsAcceptable(new string('x', PasswordPolicy.MaxLength)));
        Assert.False(PasswordPolicy.IsAcceptable(new string('x', PasswordPolicy.MaxLength + 1)));
    }

    [Fact]
    public async Task Service_verifies_and_the_decoy_never_succeeds()
    {
        using var service = new PasswordService(_hasher);

        var hash = await service.HashAsync("my password", CancellationToken.None);

        Assert.Equal(PasswordVerification.Success, await service.VerifyAsync("my password", hash, CancellationToken.None));
        Assert.Equal(PasswordVerification.Failed, await service.VerifyAsync("other", hash, CancellationToken.None));
        await service.VerifyDecoyAsync("my password", CancellationToken.None);
    }

    [Fact]
    public async Task Service_never_runs_more_than_two_hashes_at_once()
    {
        using var service = new PasswordService(new PasswordHasher(new Argon2Settings(MemoryKib: 8 * 1024, Passes: 2)));

        // Ten callers at once must all finish; the limit queues, it does not reject.
        var hashes = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(i => service.HashAsync($"password {i}", CancellationToken.None)));

        Assert.Equal(10, hashes.Distinct().Count());
    }
}
