using System.Net;
using System.Text;
using WebApi.Services;
using Xunit;

namespace WebApi.Tests;

/// <summary>
/// The capped response reader (shared by the LLM probe): streaming read with a hard size cap,
/// so an upstream that sends an unbounded body cannot turn a probe into a memory DoS.
/// </summary>
public class HttpReadingTests
{
    private static HttpResponseMessage Response(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task ReadCapped_ReadsTheWholeBody_WhenUnderTheCap()
    {
        var body = Encoding.UTF8.GetBytes("<html><title>Test Song — Chords & Tabs</title></html>");
        var text = await HttpReading.ReadCappedAsStringAsync(Response(body), 64 * 1024);
        Assert.Contains("Test Song", text);
    }

    [Fact]
    public async Task ReadCapped_ThrowsPastTheCap()
    {
        var body = new byte[64 * 1024];
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HttpReading.ReadCappedAsStringAsync(Response(body), 32 * 1024));
    }
}
