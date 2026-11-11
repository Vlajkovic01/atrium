using System.Text;
using Atrium.Identity.Codes;
using Atrium.Identity.Totp;

namespace Atrium.Identity.Tests;

public sealed class CodeTests
{
    [Fact]
    public void Tokens_are_long_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => SecretCode.NewToken()).ToList();

        Assert.Equal(1000, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.Equal(43, t.Length); // 32 bytes in unpadded base64url
            Assert.Matches("^[A-Za-z0-9_-]+$", t);
        });
    }

    [Fact]
    public void Token_hash_is_stable_and_does_not_contain_the_token()
    {
        var token = SecretCode.NewToken();

        Assert.Equal(SecretCode.Hash(token), SecretCode.Hash(token));
        Assert.Equal(SecretCode.HashLength, SecretCode.Hash(token).Length);
        Assert.NotEqual(SecretCode.Hash(token), SecretCode.Hash(token + "x"));
    }

    [Fact]
    public void User_codes_use_only_the_unambiguous_alphabet()
    {
        for (var i = 0; i < 500; i++)
        {
            var code = UserCode.New();

            Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$", code);
            Assert.Equal(code, UserCode.Normalize(code));
        }
    }

    [Theory]
    [InlineData("BCDF-GHJK", "BCDF-GHJK")]
    [InlineData("bcdfghjk", "BCDF-GHJK")]
    [InlineData(" bcdf ghjk ", "BCDF-GHJK")]
    [InlineData("b-c-d-f-g-h-j-k", "BCDF-GHJK")]
    public void Typed_codes_are_normalized(string typed, string expected)
    {
        Assert.Equal(expected, UserCode.Normalize(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BCDF-GHJ")]
    [InlineData("BCDF-GHJKL")]
    [InlineData("ABCD-EFGH")]
    [InlineData("BCDF-GH1K")]
    [InlineData("BCDF_GHJK")]
    [InlineData("BCDF-GHJK; DROP TABLE Users")]
    public void Anything_that_cannot_be_a_code_is_rejected(string? typed)
    {
        Assert.Null(UserCode.Normalize(typed));
    }

    // RFC 6238 appendix B, SHA-1 rows, reduced from eight digits to six.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Totp_matches_the_rfc_test_vectors(long unixSeconds, string expected)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        Assert.Equal(expected, TotpGenerator.CodeAt(secret, TotpGenerator.StepAt(time)));
    }

    [Fact]
    public void Totp_accepts_the_current_code_and_one_step_of_drift()
    {
        var secret = TotpGenerator.NewSecret();
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var step = TotpGenerator.StepAt(now);

        Assert.Equal(step, TotpGenerator.Verify(secret, TotpGenerator.CodeAt(secret, step), now, lastUsedStep: null));
        Assert.Equal(step - 1, TotpGenerator.Verify(secret, TotpGenerator.CodeAt(secret, step - 1), now, null));
        Assert.Equal(step + 1, TotpGenerator.Verify(secret, TotpGenerator.CodeAt(secret, step + 1), now, null));
        Assert.Null(TotpGenerator.Verify(secret, TotpGenerator.CodeAt(secret, step - 2), now, null));
        Assert.Null(TotpGenerator.Verify(secret, TotpGenerator.CodeAt(secret, step + 2), now, null));
    }

    [Fact]
    public void Totp_code_cannot_be_used_twice()
    {
        var secret = TotpGenerator.NewSecret();
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var code = TotpGenerator.CodeAt(secret, TotpGenerator.StepAt(now));

        var used = TotpGenerator.Verify(secret, code, now, lastUsedStep: null);

        Assert.NotNull(used);
        Assert.Null(TotpGenerator.Verify(secret, code, now, lastUsedStep: used));
        // An older code is refused too once a newer one was used.
        Assert.Null(TotpGenerator.Verify(secret, TotpGenerator.CodeAt(secret, used.Value - 1), now, lastUsedStep: used));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("١٢٣٤٥٦")]
    public void Totp_rejects_input_that_is_not_six_ascii_digits(string? code)
    {
        Assert.Null(TotpGenerator.Verify(TotpGenerator.NewSecret(), code, DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void Totp_accepts_a_code_typed_with_a_space()
    {
        var secret = TotpGenerator.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var code = TotpGenerator.CodeAt(secret, TotpGenerator.StepAt(now));

        Assert.NotNull(TotpGenerator.Verify(secret, $"{code[..3]} {code[3..]}", now, null));
    }

    [Fact]
    public void Totp_code_from_another_secret_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var other = Encoding.ASCII.GetBytes("09876543210987654321");

        Assert.Null(TotpGenerator.Verify(secret, TotpGenerator.CodeAt(other, TotpGenerator.StepAt(now)), now, null));
    }

    [Fact]
    public void Provisioning_uri_is_what_authenticator_apps_expect()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");

        var uri = TotpGenerator.ProvisioningUri("Atrium Home", "ana", secret);

        Assert.Equal(
            "otpauth://totp/Atrium%20Home:ana?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Atrium%20Home&algorithm=SHA1&digits=6&period=30",
            uri);
    }

    // RFC 4648 section 10, without padding.
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_the_rfc_test_vectors(string plain, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(plain)));
        Assert.True(Base32.TryDecode(encoded, out var decoded));
        Assert.Equal(plain, Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Base32_decoding_ignores_case_and_padding_and_rejects_other_characters()
    {
        Assert.True(Base32.TryDecode("mzxw6ytboi======", out var decoded));
        Assert.Equal("foobar", Encoding.ASCII.GetString(decoded));
        Assert.False(Base32.TryDecode("MZXW6YT1", out _));
        Assert.False(Base32.TryDecode(null, out _));
    }
}
