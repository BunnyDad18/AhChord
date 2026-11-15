using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>
/// ST-70c (profile image): the upload validation rules — extension allowlist mapped to
/// canonical content types, size cap. The bytes live on the user row; the GET is anonymous
/// (an &lt;img&gt; can't send Bearer) with a client-side ?v= cache-bust from AvatarUpdatedAt.
/// </summary>
public class AvatarEndpointsTests
{
    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".PNG", "image/png")]          // case-insensitive
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".webp", "image/webp")]
    public void AllowedExtensions_MapToCanonicalTypes(string ext, string expected)
    {
        Assert.True(AuthEndpoints.TryAvatarType(ext, out var ct));
        Assert.Equal(expected, ct);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".svg")]   // script-capable, never a profile image
    [InlineData(".gif")]
    [InlineData("")]
    public void DisallowedExtensions_Rejected(string ext)
        => Assert.False(AuthEndpoints.TryAvatarType(ext, out _));

    [Fact]
    public void SizeCap_IsOneMegabyte()
        => Assert.Equal(1024 * 1024, AuthEndpoints.MaxAvatarSizeBytes);
}
