using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-122: the conversion input cap is enforced in BYTES — the limit is named
/// MaxConversionInputBytes and the message says "max 512 KB". The old check compared
/// string.Length (UTF-16 code units), so a CJK/em-dash-heavy paste passed at up to
/// ~3× the promised byte bound. The cap runs before LLM resolution, so these tests
/// never need a configured endpoint.
/// </summary>
public class ConvertInputCapTests
{
    [Fact]
    public async Task Enqueue_MultibyteContentOverTheByteCap_IsRejected()
    {
        using var app = new ApiTestFactory();
        var member = await app.RegisterUserAsync();

        var cjk = new string('あ', 300_000); // 300 K chars, 900 KB UTF-8 — over the cap
        var res = await member.Client.PostAsync("/api/import/convert",
            ApiTestFactory.Json(new { content = cjk }));

        Assert.Equal(400, (int)res.StatusCode);
        Assert.Contains("too large", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enqueue_MultibyteContentUnderTheByteCap_PassesTheCap()
    {
        using var app = new ApiTestFactory();
        var member = await app.RegisterUserAsync();

        // 100 K chars = 300 KB UTF-8 — under the cap. The cap answers 400; passing it
        // moves on to LLM resolution (503 unconfigured / 202 configured — both are
        // environment-dependent, so the assertion is only "not the cap's 400").
        var cjk = new string('あ', 100_000);
        var res = await member.Client.PostAsync("/api/import/convert",
            ApiTestFactory.Json(new { content = cjk }));

        Assert.NotEqual(400, (int)res.StatusCode);
    }

    [Fact]
    public async Task Enqueue_AsciiContentUnderTheCap_PassesTheCap()
    {
        using var app = new ApiTestFactory();
        var member = await app.RegisterUserAsync();

        var ascii = new string('a', InputLimits.MaxConversionInputBytes - 1024) + " [G] chord line";
        var res = await member.Client.PostAsync("/api/import/convert",
            ApiTestFactory.Json(new { content = ascii }));

        Assert.NotEqual(400, (int)res.StatusCode);
    }
}
