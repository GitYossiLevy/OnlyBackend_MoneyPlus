using System.Net;
using System.Net.Mail;
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

public class SmtpOptions
{
    public const string Section = "Smtp";
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
}

public interface IEmailSender
{
    bool IsConfigured { get; }
    Task<bool> SendPasswordResetCodeAsync(string toEmail, string displayName, string code, CancellationToken ct = default);
}

public class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private (string? Host, int Port, bool EnableSsl, string? User, string? Password, string? From) ResolveSettings()
    {
        var host = Environment.GetEnvironmentVariable("SMTP_HOST") ?? config["Smtp:Host"];
        var portRaw = Environment.GetEnvironmentVariable("SMTP_PORT") ?? config["Smtp:Port"];
        var port = int.TryParse(portRaw, out var p) && p > 0 ? p : 587;
        var sslRaw = Environment.GetEnvironmentVariable("SMTP_ENABLE_SSL") ?? config["Smtp:EnableSsl"];
        var enableSsl = !string.Equals(sslRaw, "false", StringComparison.OrdinalIgnoreCase);
        var user = Environment.GetEnvironmentVariable("SMTP_USER") ?? config["Smtp:User"];
        var pass = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? config["Smtp:Password"];
        var from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? config["Smtp:From"] ?? user;
        return (host?.Trim(), port, enableSsl, user?.Trim(), pass, from?.Trim());
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveSettings().Host) && !string.IsNullOrWhiteSpace(ResolveSettings().From);

    public async Task<bool> SendPasswordResetCodeAsync(string toEmail, string displayName, string code, CancellationToken ct = default)
    {
        var s = ResolveSettings();
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.From))
        {
            logger.LogWarning("SMTP is not configured. Password reset code for {Email}: {Code}", toEmail, code);
            return false;
        }

        using var msg = new MailMessage
        {
            From = new MailAddress(s.From, "MoneyPlus"),
            Subject = "MoneyPlus - קוד לאיפוס סיסמה",
            IsBodyHtml = true,
            Body = $"""
                <div dir="rtl" style="font-family: Arial, sans-serif; line-height: 1.6; color: #1e293b;">
                  <h2>שלום {WebUtility.HtmlEncode(displayName)},</h2>
                  <p>התקבלה בקשה לאיפוס הסיסמה של חשבונך ב-MoneyPlus.</p>
                  <p>קוד האימות שלך הוא:</p>
                  <div style="font-size: 28px; font-weight: 800; letter-spacing: 6px; padding: 12px 18px; background: #eef2ff; color: #4f46e5; border-radius: 10px; display: inline-block; margin: 8px 0;">
                    {WebUtility.HtmlEncode(code)}
                  </div>
                  <p>הקוד תקף למשך 15 דקות. אם לא ביקשת לאפס את הסיסמה, ניתן להתעלם מהודעה זו.</p>
                </div>
                """
        };
        msg.To.Add(new MailAddress(toEmail));

        using var client = new SmtpClient(s.Host, s.Port)
        {
            EnableSsl = s.EnableSsl,
            Timeout = 10_000,
        };
        if (!string.IsNullOrWhiteSpace(s.User) && !string.IsNullOrWhiteSpace(s.Password))
        {
            client.Credentials = new NetworkCredential(s.User, s.Password);
        }

        await client.SendMailAsync(msg, ct);
        logger.LogInformation("Password reset email sent to {Email}", toEmail);
        return true;
    }
}
