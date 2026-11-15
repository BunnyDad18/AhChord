using System.Text;

namespace WebApi.Services;

/// <summary>
/// Response-body reading with a hard size cap — the LLM probe reads an upstream response
/// the member's server controls, so an unbounded read is a memory DoS with a tail.
/// </summary>
public static class HttpReading
{
    /// <summary>
    /// Read a response body with a hard cap, for callers that need the status code
    /// themselves (the LLM probe reports a non-2xx with an upstream hint instead of throwing).
    /// Streaming, so the cap applies to bodies without Content-Length too.
    /// UTF-8 is decoded per chunk — a multi-byte character on a chunk boundary can mangle
    /// one glyph, which is harmless for a probe preview.
    /// </summary>
    public static async Task<string> ReadCappedAsStringAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken ct = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        var sb = new StringBuilder();
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"Response exceeds the {maxBytes / 1024} KB cap.");
            sb.Append(Encoding.UTF8.GetString(buffer.AsSpan(0, read)));
        }
        return sb.ToString();
    }
}
