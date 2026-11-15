using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WebApi.Identity;

/// <summary>Issues HS256 JWTs with the locked claim set: <c>sub</c>, <c>email</c>, <c>name</c>, <c>is_admin</c>.</summary>
public sealed class JwtTokenService
{
    private readonly byte[] _signingKey;
    private readonly int _expiryMinutes;

    public JwtTokenService(string signingKey, int expiryMinutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        _signingKey = Encoding.UTF8.GetBytes(signingKey);
        _expiryMinutes = expiryMinutes;
    }

    public (string Token, DateTime ExpiresAt) CreateToken(ApplicationUser user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_expiryMinutes);
        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim("sub", user.Id),
                new Claim("email", user.Email ?? string.Empty),
                new Claim("name", user.DisplayName ?? user.Email ?? string.Empty),
                new Claim("is_admin", user.IsAdmin ? "true" : "false"),
                // Public-demo gate 4: Identity rotates the security stamp when the password
                // changes, and Program.cs's OnTokenValidated compares it per request — this
                // claim is what makes the 30-day token revocable.
                new Claim("sstamp", user.SecurityStamp ?? string.Empty)
            },
            expires: expiresAt,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(_signingKey), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
