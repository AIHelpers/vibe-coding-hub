using AiCodeAgent.Core.Audit;

namespace AiCodeAgent.Core.Tests.Audit;

/// <summary>E7-S2: keys never reach an audit file — neither the ones in the environment nor the ones typed into a prompt.</summary>
public sealed class SecretRedactionTests
{
    private const string FakeKey = "zk-live-9f8e7d6c5b4a39281706f5e4d3c2b1a0";

    private static SecretRedactor WithEnv(params (string Name, string Value)[] vars) =>
        new(vars.Select(v => new KeyValuePair<string, string>(v.Name, v.Value)));

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("MY_CLIENT_SECRET")]
    [InlineData("db_password")]
    [InlineData("SOME_KEY_FILE_CONTENT")]
    public void TheValueOfAnEnvironmentVariableWithASecretName_IsMasked(string name)
    {
        var redactor = WithEnv((name, FakeKey));

        var text = redactor.Redact($"curl -H 'x: {FakeKey}' https://example.test/{FakeKey}/x");

        Assert.DoesNotContain(FakeKey, text);
        Assert.Contains(SecretRedactor.Mask, text);
    }

    [Fact]
    public void VariablesWithOtherNames_AreLeftAlone()
    {
        var redactor = WithEnv(("PATH", "/usr/local/bin:/usr/bin:/bin"), ("EDITOR", "vim-for-everything"));

        var text = redactor.Redact("PATH is /usr/local/bin:/usr/bin:/bin and EDITOR is vim-for-everything");

        Assert.Equal("PATH is /usr/local/bin:/usr/bin:/bin and EDITOR is vim-for-everything", text);
    }

    [Fact]
    public void ShortValues_AreNotTreatedAsSecrets()
    {
        var redactor = WithEnv(("KEYBOARD_LAYOUT", "us"), ("TOKEN_COUNT", "42"));

        Assert.Equal("layout us, 42 tokens", redactor.Redact("layout us, 42 tokens"));
    }

    [Fact]
    public void TheProcessEnvironment_IsRead()
    {
        const string name = "VCH_TEST_AUDIT_SECRET_TOKEN";
        Environment.SetEnvironmentVariable(name, FakeKey);
        try
        {
            var redactor = SecretRedactor.FromEnvironment();

            Assert.DoesNotContain(FakeKey, redactor.Redact($"echo ${name} printed {FakeKey}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ExplicitlyRegisteredSecrets_AreMasked()
    {
        var redactor = new SecretRedactor(Array.Empty<KeyValuePair<string, string>>(), new[] { "hunter2-hunter2" });

        Assert.Equal($"the password is {SecretRedactor.Mask}", redactor.Redact("the password is hunter2-hunter2"));
    }

    [Theory]
    [InlineData("sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("sk-proj-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("sk-AbCdEfGhIjKlMnOpQrStUvWxYz012345")]
    [InlineData("ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("github_pat_11ABCDEFG0AbCdEfGhIjKl_mnopqrstuvwxyz0123456789")]
    [InlineData("glpat-AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("AIzaSyA-AbCdEfGhIjKlMnOpQrStUvWxYz01234")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r")]
    public void WellKnownKeyShapes_AreMasked_EvenWhenNobodyRegisteredThem(string key)
    {
        var redactor = WithEnv();

        var text = redactor.Redact($"please use {key} for the call");

        Assert.DoesNotContain(key, text);
        Assert.Equal($"please use {SecretRedactor.Mask} for the call", text);
    }

    [Fact]
    public void BearerTokens_AreMasked_ButTheWordStays()
    {
        var text = WithEnv().Redact("Authorization: Bearer abcDEF123456.ghiJKL789_0-xyz");

        Assert.Equal($"Authorization: Bearer {SecretRedactor.Mask}", text);
    }

    [Fact]
    public void PrivateKeyBlocks_AreMasked()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA7\nabcdef\n-----END RSA PRIVATE KEY-----";

        var text = WithEnv().Redact($"key file:\n{pem}\nend");

        Assert.DoesNotContain("MIIEow", text);
        Assert.Equal($"key file:\n{SecretRedactor.Mask}\nend", text);
    }

    [Theory]
    [InlineData("api_key=abc123456789", "api_key=[REDACTED]")]
    [InlineData("API_KEY: abc123456789", "API_KEY: [REDACTED]")]
    [InlineData("export MY_SECRET='abc123456789'", "export MY_SECRET='[REDACTED]'")]
    [InlineData("{\"password\": \"abc123456789\"}", "{\"password\": \"[REDACTED]\"}")]
    [InlineData("client_secret = abc123456789", "client_secret = [REDACTED]")]
    [InlineData("--token abc123456789", "--token [REDACTED]")]
    [InlineData("curl --api-key abc123456789 https://example.test", "curl --api-key [REDACTED] https://example.test")]
    [InlineData("tool --password=abc123456789", "tool --password=[REDACTED]")]
    public void NameValuePairs_KeepTheNameAndLoseTheValue(string input, string expected)
    {
        Assert.Equal(expected, WithEnv().Redact(input));
    }

    [Theory]
    [InlineData("max_tokens: 100000")]
    [InlineData("tokens=250000")]
    [InlineData("the key point is simple")]
    [InlineData("use a monkey wrench, not a hammer")]
    [InlineData("password policy: at least twelve characters")]
    [InlineData("run --max-tokens 100000 --token-limit 5000")]
    [InlineData("src/AiCodeAgent.Core/Audit/SecretRedactor.cs")]
    public void OrdinaryText_IsNotTouched(string input)
    {
        Assert.Equal(input, WithEnv().Redact(input));
    }

    [Fact]
    public void Redacting_IsIdempotent()
    {
        var redactor = WithEnv(("API_KEY", FakeKey));
        var once = redactor.Redact($"api_key={FakeKey} Bearer abcDEF123456.ghiJKL789_0-xyz sk-AbCdEfGhIjKlMnOpQrStUvWxYz012345");

        Assert.Equal(once, redactor.Redact(once));
    }

    [Fact]
    public void ALongerSecret_ContainingAShorterOne_IsMaskedWhole()
    {
        var redactor = WithEnv(("A_KEY", "abcdefgh"), ("B_KEY", "abcdefgh-and-more-1234"));

        Assert.Equal(SecretRedactor.Mask, redactor.Redact("abcdefgh-and-more-1234"));
    }

    [Fact]
    public void ContainsSecret_SaysWhetherAnythingWouldChange()
    {
        var redactor = WithEnv(("API_KEY", FakeKey));

        Assert.True(redactor.ContainsSecret($"x {FakeKey}"));
        Assert.False(redactor.ContainsSecret("nothing to see"));
        Assert.False(redactor.ContainsSecret(null));
    }
}
