using Microsoft.Extensions.Configuration;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>ST-30: per-field resolution order — user row → global row → .env.</summary>
public class LlmSettingsProviderTests
{
    private static IConfiguration Env(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private static LlmSettings Row(string? userId, string? baseUrl = null, string? model = null, string? apiKey = null, int maxTokens = 0) =>
        new() { UserId = userId, BaseUrl = baseUrl, Model = model, ApiKey = apiKey, MaxTokens = maxTokens };

    [Fact]
    public void Merge_NothingAnywhere_IsNone()
    {
        var result = LlmSettingsProvider.Merge(null, null, Env());
        Assert.Null(result.BaseUrl);
        Assert.Equal("none", result.Source);
        Assert.Equal(4096, result.MaxTokens); // env default
    }

    [Fact]
    public void Merge_EnvOnly_UsesEnvValues()
    {
        var result = LlmSettingsProvider.Merge(null, null, Env(
            ("LLM_BASE_URL", "http://env.test/v1"),
            ("LLM_MODEL", "env-model"),
            ("LLM_API_KEY", "env-key")));

        Assert.Equal("http://env.test/v1", result.BaseUrl);
        Assert.Equal("env-model", result.Model);
        Assert.Equal("env-key", result.ApiKey);
        Assert.Equal("env", result.Source);
    }

    [Fact]
    public void Merge_GlobalRow_UsesGlobalValues()
    {
        var result = LlmSettingsProvider.Merge(null, Row(null, "http://global.test/v1", "g-model"), Env());
        Assert.Equal("http://global.test/v1", result.BaseUrl);
        Assert.Equal("g-model", result.Model);
        Assert.Equal("global", result.Source);
    }

    [Fact]
    public void Merge_UserRow_UsesUserValues()
    {
        var result = LlmSettingsProvider.Merge(Row("u1", "http://user.test/v1"), null, Env());
        Assert.Equal("http://user.test/v1", result.BaseUrl);
        Assert.Equal("user", result.Source);
    }

    [Fact]
    public void Merge_UserAndGlobal_MergesPerField_FirstNonEmptyWins()
    {
        // User sets only the base URL; model/key fall through to the global row.
        var result = LlmSettingsProvider.Merge(
            Row("u1", baseUrl: "http://user.test/v1"),
            Row(null, baseUrl: "http://global.test/v1", model: "g-model", apiKey: "g-key"),
            Env());

        Assert.Equal("http://user.test/v1", result.BaseUrl);
        Assert.Equal("g-model", result.Model);
        Assert.Equal("g-key", result.ApiKey);
        Assert.Equal("user", result.Source); // source follows the base URL
    }

    [Fact]
    public void Merge_UserRowBeatsEnv_ForEveryField()
    {
        var result = LlmSettingsProvider.Merge(
            Row("u1", "http://user.test/v1", "u-model", "u-key"),
            null,
            Env(("LLM_BASE_URL", "http://env.test/v1"), ("LLM_MODEL", "env-model"), ("LLM_API_KEY", "env-key")));

        Assert.Equal("http://user.test/v1", result.BaseUrl);
        Assert.Equal("u-model", result.Model);
        Assert.Equal("u-key", result.ApiKey);
    }

    [Fact]
    public void Merge_WhitespaceOnlyValues_AreTreatedAsEmpty()
    {
        var result = LlmSettingsProvider.Merge(
            Row("u1", baseUrl: "   "),
            Row(null, baseUrl: "http://global.test/v1"),
            Env());

        Assert.Equal("http://global.test/v1", result.BaseUrl);
        Assert.Equal("global", result.Source);
    }

    [Theory]
    [InlineData(2048, 512, null, 2048)]   // user wins
    [InlineData(0, 512, null, 512)]       // user unset → global
    [InlineData(0, 0, "777", 777)]        // rows unset → env
    [InlineData(0, 0, null, 4096)]        // nothing set → default
    public void Merge_MaxTokens_FollowsUserGlobalEnvOrder(int user, int global, string? envValue, int expected)
    {
        var values = new List<(string, string?)>();
        if (envValue is not null) values.Add(("LLM_MAX_TOKENS", envValue));
        var result = LlmSettingsProvider.Merge(
            Row("u1", maxTokens: user),
            Row(null, maxTokens: global),
            Env([.. values]));

        Assert.Equal(expected, result.MaxTokens);
    }

    // --- /v1 normalization ------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("http://x/v1", "http://x/v1")]          // already present — untouched
    [InlineData("http://x/v1/", "http://x/v1")]         // trailing slash trimmed, no double /v1
    [InlineData("http://x:1234", "http://x:1234/v1")]   // bare host:port gets /v1
    [InlineData("  http://x  ", "http://x/v1")]         // surrounding whitespace trimmed
    public static void NormalizeBaseUrl_AppendsV1WhenMissing(string? input, string? expected)
        => Assert.Equal(expected, LlmSettingsProvider.NormalizeBaseUrl(input));

    [Fact]
    public void Merge_NormalizesBaseUrl_RegardlessOfSourceLevel()
    {
        var result = LlmSettingsProvider.Merge(Row("u1", baseUrl: "http://user.test"), null, Env());
        Assert.Equal("http://user.test/v1", result.BaseUrl);

        var fromEnv = LlmSettingsProvider.Merge(null, null, Env(("LLM_BASE_URL", "http://env.test")));
        Assert.Equal("http://env.test/v1", fromEnv.BaseUrl);
    }
}
