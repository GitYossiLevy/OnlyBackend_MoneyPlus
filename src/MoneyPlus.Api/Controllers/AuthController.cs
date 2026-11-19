using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController(AppDbContext db, ITokenService tokens, IEmailSender emailSender, ILogger<AuthController> logger) : ControllerBase
{
    private static readonly HashSet<string> WeakPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password123", "password1234", "12345678", "123456789", "1234567890",
        "qwertyuiop", "admin123", "moneyplus", "moneyplus123", "letmein123"
    };

    private static (bool IsValid, string? Error) ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            return (false, "הסיסמה חייבת להכיל לפחות 10 תווים");
        if (password.Length > 128)
            return (false, "הסיסמה אינה יכולה לעלות על 128 תווים");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return (false, "הסיסמה חייבת לשלב אותיות ומספרים");
        if (WeakPasswords.Contains(password.Trim()))
            return (false, "הסיסמה שנבחרה פשוטה ונפוצה מדי. יש לבחור סיסמה מורכבת יותר");
        return (true, null);
    }

    private static string? NormalizeEmail(string? raw)
    {
        var e = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return null;
        return MailAddress.TryCreate(e, out var m) && m.Address == e ? e : null;
    }

    private AuthResponse Issue(AppUser u)
    {
        var (token, exp) = tokens.Create(u);
        return new AuthResponse(token, exp, new AccountDto(u.Id, u.Email, u.DisplayName));
    }

    [AllowAnonymous, HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest r)
    {
        var email = NormalizeEmail(r.Email);
        if (email is null) return BadRequest("כתובת אימייל לא תקינה");
        
        var (isValidPass, passError) = ValidatePassword(r.Password);
        if (!isValidPass) return BadRequest(passError);

        if (string.IsNullOrWhiteSpace(r.DisplayName)) return BadRequest("שם תצוגה חובה");
        if (await db.AppUsers.AnyAsync(u => u.Email == email)) return Conflict("כתובת האימייל כבר רשומה");

        var user = new AppUser { Email = email, DisplayName = r.DisplayName.Trim(), PasswordHash = PasswordHasher.Hash(r.Password!) };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();

        logger.LogInformation("Security Audit: New user registration successful for {Email} from IP {IP}", email, HttpContext.Connection.RemoteIpAddress);
        return Ok(Issue(user));
    }

    [AllowAnonymous, HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest r)
    {
        var email = NormalizeEmail(r.Email);
        var user = email is null ? null : await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email);
        // הודעה אחידה – לא חושפים אם האימייל קיים
        if (user is null || string.IsNullOrEmpty(r.Password) || !PasswordHasher.Verify(r.Password, user.PasswordHash))
        {
            logger.LogWarning("Security Audit: Failed login attempt for email {Email} from IP {IP}", r.Email, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("אימייל או סיסמה שגויים");
        }

        logger.LogInformation("Security Audit: Successful login for user {UserId} ({Email}) from IP {IP}", user.Id, user.Email, HttpContext.Connection.RemoteIpAddress);
        return Ok(Issue(user));
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        var id = User.GetUserId();
        logger.LogInformation("Security Audit: User {UserId} logged out from IP {IP}", id, HttpContext.Connection.RemoteIpAddress);
        return Ok(new { message = "ההתנתקות בוצעה בהצלחה" });
    }

    [HttpGet("me")]
    public async Task<ActionResult<AccountDto>> Me()
    {
        var id = User.GetUserId();
        var u = id is null ? null : await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        return u is null ? Unauthorized() : new AccountDto(u.Id, u.Email, u.DisplayName);
    }

    public record UpdateProfileRequest(string DisplayName);

    [HttpPut("profile")]
    public async Task<ActionResult<AccountDto>> UpdateProfile(UpdateProfileRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.DisplayName)) return BadRequest("שם תצוגה חובה");
        var id = User.GetUserId();
        if (id is null) return Unauthorized();
        var u = await db.AppUsers.FindAsync(id.Value);
        if (u is null) return NotFound();
        u.DisplayName = r.DisplayName.Trim();
        await db.SaveChangesAsync();
        return Ok(new AccountDto(u.Id, u.Email, u.DisplayName));
    }

    public record SystemUserDto(Guid Id, string Email, string DisplayName, DateTime CreatedAt);
    public record UpdateUserRequest(string? DisplayName, string? Email);
    public record AdminResetPasswordRequest(string NewPassword);

    /// <summary>רשימת כל משתמשי המערכת הרשומים (מורשה למשתמשים מאומתים בלבד).</summary>
    [Authorize]
    [HttpGet("users")]
    public async Task<ActionResult<List<SystemUserDto>>> GetUsers()
    {
        var users = await db.AppUsers
            .OrderBy(u => u.CreatedAt)
            .Select(u => new SystemUserDto(u.Id, u.Email, u.DisplayName, u.CreatedAt))
            .ToListAsync();
        return Ok(users);
    }

    /// <summary>עריכת פרטי משתמש מערכת (שם תצוגה, אימייל).</summary>
    [Authorize]
    [HttpPut("users/{userId:guid}")]
    public async Task<ActionResult<SystemUserDto>> UpdateUser(Guid userId, UpdateUserRequest r)
    {
        var callerId = User.GetUserId();
        var targetUser = await db.AppUsers.FindAsync(userId);
        if (targetUser is null) return NotFound(new { message = "משתמש לא נמצא" });

        if (!string.IsNullOrWhiteSpace(r.DisplayName))
        {
            targetUser.DisplayName = r.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(r.Email))
        {
            var email = NormalizeEmail(r.Email);
            if (email is null) return BadRequest(new { message = "כתובת אימייל לא תקינה" });
            if (email != targetUser.Email && await db.AppUsers.AnyAsync(u => u.Email == email && u.Id != userId))
            {
                return Conflict(new { message = "כתובת האימייל כבר בשימוש על ידי משתמש אחר" });
            }
            targetUser.Email = email;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Security Audit: User {TargetUserId} updated by {CallerId}", userId, callerId);
        return Ok(new SystemUserDto(targetUser.Id, targetUser.Email, targetUser.DisplayName, targetUser.CreatedAt));
    }

    /// <summary>איפוס סיסמה למשתמש מערכת (מוגן תחת אימות ורושם Audit Log מלא).</summary>
    [Authorize]
    [HttpPost("users/{userId:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid userId, AdminResetPasswordRequest r)
    {
        var callerId = User.GetUserId();
        var (isValid, passError) = ValidatePassword(r.NewPassword);
        if (!isValid) return BadRequest(new { message = passError });

        var targetUser = await db.AppUsers.FindAsync(userId);
        if (targetUser is null) return NotFound(new { message = "משתמש לא נמצא" });

        targetUser.PasswordHash = PasswordHasher.Hash(r.NewPassword);
        await db.SaveChangesAsync();

        logger.LogWarning("Security Audit: Password for user {TargetUserId} ({Email}) was reset by user {CallerId} from IP {IP}",
            targetUser.Id, targetUser.Email, callerId, HttpContext.Connection.RemoteIpAddress);

        return Ok(new { message = "הסיסמה אופסה בהצלחה. כעת המשתמש יכול להתחבר עם הסיסמה החדשה." });
    }

    /// <summary>מחיקת משתמש כניסה למערכת (לא ניתן למחוק את המשתמש המחובר או את המשתמש האחרון).</summary>
    [Authorize]
    [HttpDelete("users/{userId:guid}")]
    public async Task<IActionResult> DeleteSystemUser(Guid userId)
    {
        var callerId = User.GetUserId();
        if (callerId == userId)
            return BadRequest(new { message = "לא ניתן למחוק את המשתמש המחובר כעת למערכת" });

        var targetUser = await db.AppUsers.FindAsync(userId);
        if (targetUser is null) return NotFound(new { message = "משתמש לא נמצא" });

        var totalUsers = await db.AppUsers.CountAsync();
        if (totalUsers <= 1)
            return BadRequest(new { message = "לא ניתן למחוק את משתמש הכניסה האחרון במערכת" });

        var memberships = await db.ProjectMembers.Where(m => m.AppUserId == userId).ToListAsync();
        if (memberships.Count > 0)
            db.ProjectMembers.RemoveRange(memberships);

        db.AppUsers.Remove(targetUser);
        await db.SaveChangesAsync();

        logger.LogWarning("Security Audit: System user {TargetUserId} ({Email}) deleted by user {CallerId} from IP {IP}",
            targetUser.Id, targetUser.Email, callerId, HttpContext.Connection.RemoteIpAddress);

        return Ok(new { message = $"משתמש הכניסה \"{targetUser.DisplayName}\" נמחק בהצלחה" });
    }

    public record ForgotPasswordRequest(string Email);
    public record ResetPasswordByCodeRequest(string Email, string Code, string NewPassword);

    /// <summary>שליחת קוד איפוס סיסמה למייל מתוך מסך ההתחברות.</summary>
    [AllowAnonymous, HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest r, CancellationToken ct)
    {
        var email = NormalizeEmail(r.Email);
        if (email is null) return BadRequest(new { message = "כתובת אימייל לא תקינה" });

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
        bool emailSent = false;
        if (user is not null)
        {
            var code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString();
            user.PasswordResetCodeHash = PasswordHasher.Hash(code);
            user.PasswordResetExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
            await db.SaveChangesAsync(ct);

            try
            {
                emailSent = await emailSender.SendPasswordResetCodeAsync(user.Email, user.DisplayName, code, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed sending password reset email to {Email}", user.Email);
                return StatusCode(502, new { message = "שליחת מייל האיפוס נכשלה. בדוק את הגדרות שרת הדואר (SMTP)." });
            }
        }

        if (!emailSender.IsConfigured)
        {
            return Ok(new
            {
                message = "קוד איפוס נוצר, אך שרת שליחת המייל (SMTP) אינו מוגדר עדיין בסביבה.",
                emailConfigured = false,
                emailSent = false
            });
        }

        return Ok(new
        {
            message = "אם כתובת האימייל רשומה במערכת, נשלח אליה מייל עם קוד איפוס סיסמה.",
            emailConfigured = true,
            emailSent
        });
    }

    /// <summary>אימות קוד האיפוס וקביעת סיסמה חדשה ממסך ההתחברות.</summary>
    [AllowAnonymous, HttpPost("reset-password-by-code")]
    public async Task<ActionResult<AuthResponse>> ResetPasswordByCode(ResetPasswordByCodeRequest r, CancellationToken ct)
    {
        var email = NormalizeEmail(r.Email);
        if (email is null) return BadRequest(new { message = "כתובת אימייל לא תקינה" });

        var (isValidPass, passError) = ValidatePassword(r.NewPassword);
        if (!isValidPass) return BadRequest(new { message = passError });

        var code = r.Code?.Trim() ?? "";
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null
            || string.IsNullOrWhiteSpace(user.PasswordResetCodeHash)
            || user.PasswordResetExpiresAtUtc is null
            || user.PasswordResetExpiresAtUtc < DateTime.UtcNow
            || !PasswordHasher.Verify(code, user.PasswordResetCodeHash))
        {
            return BadRequest(new { message = "קוד האיפוס שגוי או שפג תוקפו" });
        }

        user.PasswordHash = PasswordHasher.Hash(r.NewPassword);
        user.PasswordResetCodeHash = null;
        user.PasswordResetExpiresAtUtc = null;
        await db.SaveChangesAsync(ct);

        logger.LogWarning("Security Audit: Password reset completed via email code for {Email} from IP {IP}", email, HttpContext.Connection.RemoteIpAddress);
        return Ok(Issue(user));
    }
}
