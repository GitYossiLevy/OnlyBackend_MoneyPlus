using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Services;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Key { get; set; } = "MoneyPlusSuperSecureFallbackSigningKey2026DevEnvironment!";
    public string Issuer { get; set; } = "MoneyPlus";
    public string Audience { get; set; } = "MoneyPlus.Client";
    public int ExpiryMinutes { get; set; } = 60 * 24 * 365; // 365 ימים (שנה מלאה) לסשן קבוע ונטול ניתוקים
}

/// <summary>PBKDF2-SHA256 עם salt אקראי. פורמט: v1.{iterations}.{saltB64}.{hashB64}</summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000, SaltSize = 16, HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 4 || p[0] != "v1" || !int.TryParse(p[1], out var iter)) return false;
        var salt = Convert.FromBase64String(p[2]);
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public interface ITokenService
{
    (string Token, DateTime ExpiresAt) Create(AppUser user);
}

public class TokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public (string Token, DateTime ExpiresAt) Create(AppUser user)
    {
        var expires = DateTime.UtcNow.AddMinutes(_o.ExpiryMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("name", user.DisplayName),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key)), SecurityAlgorithms.HmacSha256),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

public static class ClaimsExtensions
{
    /// <summary>מזהה החשבון המחובר מתוך ה-JWT (claim "sub").</summary>
    public static Guid? GetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
