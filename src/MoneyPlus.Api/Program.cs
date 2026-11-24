using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Hubs;
using MoneyPlus.Api.Services;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o => o.Filters.Add<ProjectAccessFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

// מסד נתונים: ענן מוצפן (PostgreSQL / Supabase / Neon) או מקומי (SQLite)
var envProvider = Environment.GetEnvironmentVariable("DATABASE_PROVIDER");
var dbProvider = !string.IsNullOrWhiteSpace(envProvider)
    ? envProvider
    : (builder.Configuration["DatabaseProvider"] ?? "Sqlite");
var defaultConn = builder.Configuration.GetConnectionString("Default");
var envDbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var cfgPostgres = builder.Configuration.GetConnectionString("Postgres");
var postgresConn = !string.IsNullOrWhiteSpace(envDbUrl)
    ? envDbUrl
    : (!string.IsNullOrWhiteSpace(cfgPostgres) ? cfgPostgres : null);

var isPostgres = dbProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(postgresConn);
if (isPostgres)
{
    var targetConn = NormalizePostgresConnectionString(!string.IsNullOrWhiteSpace(postgresConn) ? postgresConn : defaultConn ?? "");
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(targetConn));
}
else
{
    builder.Services.AddDbContext<AppDbContext>(o =>
        o.UseSqlite(defaultConn ?? "Data Source=moneyplus.db"));
}

builder.Services.AddScoped<IFinancialInsightsService, FinancialInsightsService>();
builder.Services.AddScoped<IPdfReportParser, PdfReportParser>();
builder.Services.AddScoped<ICreditCardParserService, CreditCardParserService>();
builder.Services.AddScoped<ICreditCardReconciliationService, CreditCardReconciliationService>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<CycleMidnightRolloverHostedService>();

// ===== JWT =====
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
{
    if (builder.Environment.IsDevelopment())
    {
        jwt.Key = "MoneyPlusSuperSecureFallbackSigningKey2026DevEnvironment!";
    }
    else
    {
        throw new InvalidOperationException("Jwt:Key is missing or shorter than 32 characters (256 bits). Configure a secure JWT secret in environment or configuration.");
    }
}

builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false; // נשארים עם "sub" ולא ממופים ל-ClaimTypes
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
    };
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});
// ברירת מחדל: הכל דורש התחברות. רק [AllowAnonymous] פתוח.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// הגנה בסיסית מפני brute-force על login/register
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
    ?? builder.Configuration["CORS_ALLOWED_ORIGINS"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? Array.Empty<string>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    p.AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials();

    if (configuredOrigins.Length > 0)
    {
        p.WithOrigins(configuredOrigins);
    }
    else
    {
        // ברירת מחדל לפיתוח/סביבה מקומית: תמיכה ב-localhost, 127.0.0.1 ומעטפת Capacitor / Android WebView לנייד, וחסימת כל מקור זדוני חיצוני
        p.SetIsOriginAllowed(origin =>
        {
            if (string.IsNullOrWhiteSpace(origin)) return false;
            if (origin.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("appassets.androidplatform.net", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("capacitor", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("ionic", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        });
    }
}));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync(); // יוחלף ב-Migrations לפני פרודקשן
    try
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"InactivePaymentMethodsJson\" TEXT;");
    }
    catch
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN InactivePaymentMethodsJson TEXT;");
        }
        catch
        {
            // Column already exists
        }
    }

    try
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"ProjectType\" TEXT NOT NULL DEFAULT 'התנהלות חודשית';");
    }
    catch
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN ProjectType TEXT DEFAULT 'התנהלות חודשית';");
        }
        catch
        {
            // Column already exists
        }
    }

    try
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"PlanningBasis\" TEXT NOT NULL DEFAULT 'smart';");
    }
    catch
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN PlanningBasis TEXT DEFAULT 'smart';");
        }
        catch
        {
            // Column already exists
        }
    }

    try
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Categories\" ADD COLUMN IF NOT EXISTS \"MonthlyBudgetsJson\" TEXT;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Categories\" ADD COLUMN IF NOT EXISTS \"IsCapitalTransfer\" BOOLEAN NOT NULL DEFAULT FALSE;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"IncomeCategories\" ADD COLUMN IF NOT EXISTS \"MonthlyBudgetsJson\" TEXT;");
    }
    catch
    {
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Categories ADD COLUMN MonthlyBudgetsJson TEXT;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Categories ADD COLUMN IsCapitalTransfer INTEGER DEFAULT 0;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE IncomeCategories ADD COLUMN MonthlyBudgetsJson TEXT;"); } catch { }
    }

    if (isPostgres)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                ALTER TABLE ""Transactions"" ADD COLUMN IF NOT EXISTS ""SavingsGoalId"" uuid;
                CREATE TABLE IF NOT EXISTS ""SavingsGoals"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ProjectId"" uuid NOT NULL REFERENCES ""Projects""(""Id"") ON DELETE CASCADE,
                    ""Name"" text NOT NULL,
                    ""Icon"" text NOT NULL DEFAULT '🎯',
                    ""Color"" text NOT NULL DEFAULT '#0E7490',
                    ""VehicleType"" integer NOT NULL DEFAULT 3,
                    ""TargetAmount"" numeric(18,2) NOT NULL,
                    ""InitialAmount"" numeric(18,2) NOT NULL DEFAULT 0,
                    ""AnnualInterestRate"" numeric(9,4) NOT NULL DEFAULT 0,
                    ""ManagementFeeAnnualPct"" numeric(9,4) NOT NULL DEFAULT 0,
                    ""DepositFeePct"" numeric(9,4) NOT NULL DEFAULT 0,
                    ""CapitalGainsTaxPct"" numeric(9,4) NOT NULL DEFAULT 25,
                    ""StartDate"" date NOT NULL,
                    ""TargetDate"" date NOT NULL,
                    ""LinkedCategoryId"" uuid NULL REFERENCES ""Categories""(""Id"") ON DELETE SET NULL,
                    ""AutoSyncCategoryBudget"" boolean NOT NULL DEFAULT TRUE,
                    ""IsActive"" boolean NOT NULL DEFAULT TRUE,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""UpdatedAt"" timestamp with time zone NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_SavingsGoals_ProjectId"" ON ""SavingsGoals"" (""ProjectId"");
                CREATE INDEX IF NOT EXISTS ""IX_Transactions_ProjectId_SavingsGoalId"" ON ""Transactions"" (""ProjectId"", ""SavingsGoalId"");
                CREATE TABLE IF NOT EXISTS ""CapturedExpenses"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ProjectId"" uuid NOT NULL REFERENCES ""Projects""(""Id"") ON DELETE CASCADE,
                    ""Amount"" numeric(18,2) NOT NULL,
                    ""MerchantName"" text NOT NULL,
                    ""RawNotificationText"" text NOT NULL DEFAULT '',
                    ""SourceApp"" text NOT NULL DEFAULT 'GoogleWallet',
                    ""PaymentMethod"" integer NOT NULL DEFAULT 0,
                    ""PaymentMethodLabel"" text NULL,
                    ""SuggestedCategoryId"" uuid NULL REFERENCES ""Categories""(""Id"") ON DELETE SET NULL,
                    ""ConfidenceScore"" double precision NOT NULL DEFAULT 0,
                    ""ExternalKey"" text NULL,
                    ""DetectedAt"" timestamp with time zone NOT NULL,
                    ""Status"" integer NOT NULL DEFAULT 0,
                    ""CreatedTransactionId"" uuid NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_CapturedExpenses_ProjectId_Status_DetectedAt"" ON ""CapturedExpenses"" (""ProjectId"", ""Status"", ""DetectedAt"");
                ALTER TABLE ""CapturedExpenses"" ADD COLUMN IF NOT EXISTS ""CapturedByDeviceId"" text NULL;
                ALTER TABLE ""CapturedExpenses"" ADD COLUMN IF NOT EXISTS ""CapturedByUserId"" uuid NULL;
                ALTER TABLE ""CapturedExpenses"" ADD COLUMN IF NOT EXISTS ""HandledByUserName"" text NULL;
                ALTER TABLE ""CapturedExpenses"" ADD COLUMN IF NOT EXISTS ""HandledAt"" timestamp with time zone NULL;
                ALTER TABLE ""AppUsers"" ADD COLUMN IF NOT EXISTS ""PasswordResetCodeHash"" text NULL;
                ALTER TABLE ""AppUsers"" ADD COLUMN IF NOT EXISTS ""PasswordResetExpiresAtUtc"" timestamp with time zone NULL;
                ALTER TABLE ""Categories"" ADD COLUMN IF NOT EXISTS ""SortOrder"" integer NOT NULL DEFAULT 0;
                ALTER TABLE ""IncomeCategories"" ADD COLUMN IF NOT EXISTS ""SortOrder"" integer NOT NULL DEFAULT 0;
                CREATE TABLE IF NOT EXISTS ""MerchantCategoryRules"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ProjectId"" uuid NOT NULL REFERENCES ""Projects""(""Id"") ON DELETE CASCADE,
                    ""NormalizedMerchant"" text NOT NULL,
                    ""DisplayMerchant"" text NOT NULL DEFAULT '',
                    ""CategoryId"" uuid NOT NULL REFERENCES ""Categories""(""Id"") ON DELETE CASCADE,
                    ""PaymentMethodLabel"" text NULL,
                    ""HitCount"" integer NOT NULL DEFAULT 1,
                    ""CorrectionCount"" integer NOT NULL DEFAULT 0,
                    ""LastUsedAtUtc"" timestamp with time zone NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MerchantCategoryRules_ProjectId_NormalizedMerchant"" ON ""MerchantCategoryRules"" (""ProjectId"", ""NormalizedMerchant"");
            ");
        }
        catch { }
    }
    else
    {
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Transactions ADD COLUMN SavingsGoalId TEXT;"); } catch { }
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""SavingsGoals"" (
                    ""Id"" TEXT NOT NULL PRIMARY KEY,
                    ""ProjectId"" TEXT NOT NULL REFERENCES ""Projects""(""Id"") ON DELETE CASCADE,
                    ""Name"" TEXT NOT NULL,
                    ""Icon"" TEXT NOT NULL DEFAULT '🎯',
                    ""Color"" TEXT NOT NULL DEFAULT '#0E7490',
                    ""VehicleType"" INTEGER NOT NULL DEFAULT 3,
                    ""TargetAmount"" TEXT NOT NULL,
                    ""InitialAmount"" TEXT NOT NULL DEFAULT '0',
                    ""AnnualInterestRate"" TEXT NOT NULL DEFAULT '0',
                    ""ManagementFeeAnnualPct"" TEXT NOT NULL DEFAULT '0',
                    ""DepositFeePct"" TEXT NOT NULL DEFAULT '0',
                    ""CapitalGainsTaxPct"" TEXT NOT NULL DEFAULT '25',
                    ""StartDate"" TEXT NOT NULL,
                    ""TargetDate"" TEXT NOT NULL,
                    ""LinkedCategoryId"" TEXT NULL REFERENCES ""Categories""(""Id"") ON DELETE SET NULL,
                    ""AutoSyncCategoryBudget"" INTEGER NOT NULL DEFAULT 1,
                    ""IsActive"" INTEGER NOT NULL DEFAULT 1,
                    ""CreatedAt"" TEXT NOT NULL,
                    ""UpdatedAt"" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_SavingsGoals_ProjectId"" ON ""SavingsGoals"" (""ProjectId"");
                CREATE INDEX IF NOT EXISTS ""IX_Transactions_ProjectId_SavingsGoalId"" ON ""Transactions"" (""ProjectId"", ""SavingsGoalId"");
                CREATE TABLE IF NOT EXISTS ""CapturedExpenses"" (
                    ""Id"" TEXT NOT NULL PRIMARY KEY,
                    ""ProjectId"" TEXT NOT NULL REFERENCES ""Projects""(""Id"") ON DELETE CASCADE,
                    ""Amount"" TEXT NOT NULL,
                    ""MerchantName"" TEXT NOT NULL,
                    ""RawNotificationText"" TEXT NOT NULL DEFAULT '',
                    ""SourceApp"" TEXT NOT NULL DEFAULT 'GoogleWallet',
                    ""PaymentMethod"" INTEGER NOT NULL DEFAULT 0,
                    ""PaymentMethodLabel"" TEXT NULL,
                    ""SuggestedCategoryId"" TEXT NULL REFERENCES ""Categories""(""Id"") ON DELETE SET NULL,
                    ""ConfidenceScore"" REAL NOT NULL DEFAULT 0,
                    ""ExternalKey"" TEXT NULL,
                    ""DetectedAt"" TEXT NOT NULL,
                    ""Status"" INTEGER NOT NULL DEFAULT 0,
                    ""CreatedTransactionId"" TEXT NULL,
                    ""CapturedByDeviceId"" TEXT NULL,
                    ""CapturedByUserId"" TEXT NULL,
                    ""HandledByUserName"" TEXT NULL,
                    ""HandledAt"" TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_CapturedExpenses_ProjectId_Status_DetectedAt"" ON ""CapturedExpenses"" (""ProjectId"", ""Status"", ""DetectedAt"");
            ");
        }
        catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE CapturedExpenses ADD COLUMN CapturedByDeviceId TEXT;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE CapturedExpenses ADD COLUMN CapturedByUserId TEXT;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE CapturedExpenses ADD COLUMN HandledByUserName TEXT;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE CapturedExpenses ADD COLUMN HandledAt TEXT;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE AppUsers ADD COLUMN PasswordResetCodeHash TEXT;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE AppUsers ADD COLUMN PasswordResetExpiresAtUtc TEXT;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Categories ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;"); } catch { }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE IncomeCategories ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;"); } catch { }
    }

    if (!isPostgres)
    {
        // ביצועי SQLite מואצים: WAL, סנכרון Normal, מטמון בזיכרון ואינדקסים מהירים
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA cache_size = -16000;
                PRAGMA busy_timeout = 5000;
                CREATE INDEX IF NOT EXISTS ""IX_Transactions_ProjectId_InstallmentGroupId"" ON ""Transactions"" (""ProjectId"", ""InstallmentGroupId"");
                CREATE INDEX IF NOT EXISTS ""IX_Categories_ProjectId"" ON ""Categories"" (""ProjectId"");
                CREATE INDEX IF NOT EXISTS ""IX_IncomeCategories_ProjectId"" ON ""IncomeCategories"" (""ProjectId"");
            ");
        }
        catch { }
    }

    // סנכרון כרטיסי האשראי (💳) לכל הפרויקטים הפעילים של אותו משתמש (כולל פרויקט עסקי וכו')
    try
    {
        static bool IsCreditCardMethod(string m) =>
            m.StartsWith("💳") || m.Contains("ויזה") || m.Contains("ישראכרט") ||
            m.Contains("מאסטרקארד") || m.Contains("כאל") || m.Contains("מקס");

        var allActiveProjects = await db.Projects.Where(p => p.IsActive).ToListAsync();
        var memberships = await db.ProjectMembers.AsNoTracking().ToListAsync();
        var userProjectsMap = memberships
            .GroupBy(m => m.AppUserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ProjectId).ToHashSet());

        var changedProjects = false;
        foreach (var kvp in userProjectsMap)
        {
            var userProjs = allActiveProjects.Where(p => kvp.Value.Contains(p.Id)).ToList();
            var sharedCards = userProjs
                .SelectMany(p => p.GetPaymentMethods())
                .Where(IsCreditCardMethod)
                .Distinct()
                .ToList();
            if (sharedCards.Count == 0) continue;

            foreach (var proj in userProjs)
            {
                var currentMethods = proj.GetPaymentMethods();
                var inactiveSet = proj.GetInactivePaymentMethods().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missingCards = sharedCards
                    .Where(c => !currentMethods.Contains(c, StringComparer.OrdinalIgnoreCase) && !inactiveSet.Contains(c))
                    .ToList();
                if (missingCards.Count > 0)
                {
                    proj.SetPaymentMethods(currentMethods.Concat(missingCards));
                    changedProjects = true;
                }
            }
        }
        if (changedProjects)
        {
            await db.SaveChangesAsync();
        }
    }
    catch { }
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// כותרות אבטחה קשיחות לפי תקן OWASP ASVS
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; connect-src 'self' https: ws: wss:; script-src 'self' 'unsafe-inline' 'unsafe-eval';");
    await next();
});

if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<BudgetHub>("/hubs/budget");

// נקודת קצה לניטור זמינות 24/7 (UptimeRobot / Health Check) ללא צורך באימות (זמינה ב-/health וב-/api/health)
var healthPayload = () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    database = isPostgres ? "Postgres" : "Sqlite",
    service = "MoneyPlus.Api"
});
app.MapGet("/health", healthPayload).AllowAnonymous();
app.MapGet("/api/health", healthPayload).AllowAnonymous();

app.Run();

public partial class Program
{
    public static string NormalizePostgresConnectionString(string connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return connStr;
        if (connStr.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || connStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(connStr);
                var userInfo = uri.UserInfo.Split(':');
                var user = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "";
                var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
                var db = uri.AbsolutePath.TrimStart('/');
                var port = uri.Port > 0 ? uri.Port : 5432;
                var trustCert = connStr.Contains("Trust Server Certificate=true", StringComparison.OrdinalIgnoreCase) ? ";Trust Server Certificate=true;" : "";
                return $"Host={uri.Host};Port={port};Database={db};Username={user};Password={pass};SSL Mode=Require{trustCert}";
            }
            catch
            {
                return connStr;
            }
        }
        return connStr;
    }
}
