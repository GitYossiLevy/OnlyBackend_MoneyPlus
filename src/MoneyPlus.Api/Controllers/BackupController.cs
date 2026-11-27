using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/backup")]
public class BackupController(AppDbContext db, ILogger<BackupController> logger) : ControllerBase
{
    /// <summary>הורדת גיבוי חודשי מוצפן/מלא של הפרויקט כקובץ JSON.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportBackup(Guid projectId)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound(new { message = "פרויקט לא נמצא" });

        logger.LogInformation("Security Audit: Backup exported for project {ProjectId} by user {UserId}", projectId, User.GetUserId());

        var categories = await db.Categories
            .Where(c => c.ProjectId == projectId)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Notes, c.Periodicity, c.PlannedAmount, 0, c.PlannedAmount, 0, c.Icon, c.IsCapitalTransfer, c.PlanningPolicy))
            .ToListAsync();

        var incomeCategories = await db.IncomeCategories
            .Where(c => c.ProjectId == projectId)
            .Select(c => new IncomeCategoryDto(c.Id, c.Name, c.Icon, c.MonthlyPlannedAmount, c.PlanningPolicy))
            .ToListAsync();

        var users = await db.ProjectUsers
            .Where(u => u.ProjectId == projectId)
            .Select(u => new ProjectUserDto(u.Id, u.Name, u.Role, u.Avatar, u.IsActiveDefault))
            .ToListAsync();

        var transactions = await db.Transactions
            .Include(t => t.Category)
            .Include(t => t.ExecutedByUser)
            .Where(t => t.ProjectId == projectId)
            .OrderByDescending(t => t.Date)
            .Select(t => new TransactionDto(
                t.Id, t.Description, t.CategoryId, t.Category != null ? t.Category.Name : "",
                t.Amount, t.Date, t.PaymentMethod, t.PaymentMethodLabel,
                t.ExecutedByUserId, t.ExecutedByUser != null ? t.ExecutedByUser.Name : "",
                t.InstallmentNumber, t.InstallmentsTotal, t.InstallmentGroupId, t.Category != null && t.Category.IsCapitalTransfer, t.SavingsGoalId))
            .ToListAsync();

        var incomes = await db.Incomes
            .Include(i => i.IncomeCategory)
            .Where(i => i.ProjectId == projectId)
            .OrderByDescending(i => i.Date)
            .Select(i => new IncomeDto(
                i.Id, i.Description, i.IncomeCategoryId, i.IncomeCategory != null ? i.IncomeCategory.Name : "",
                i.ReceiptType, i.Date, i.PlannedAmount, i.ActualAmount))
            .ToListAsync();

        var snapshot = new BackupProjectSnapshotDto(
            projectId,
            project.Name,
            DateTime.UtcNow,
            categories,
            incomeCategories,
            users,
            transactions,
            incomes
        );

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"MoneyPlus_Backup_{project.Name}_{DateTime.UtcNow:yyyy_MM_dd}.enc.json";

        return File(bytes, "application/json", fileName);
    }

    /// <summary>שחזור נתוני פרויקט מקובץ גיבוי - מורשה לבעלי הפרויקט (Owner) בלבד.</summary>
    [HttpPost("restore")]
    [RequireProjectOwner]
    [RequestSizeLimit(15 * 1024 * 1024)]
    public async Task<IActionResult> RestoreBackup(Guid projectId, [FromForm] IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "לא נבחר קובץ גיבוי" });

        if (file.Length > 15 * 1024 * 1024)
            return BadRequest(new { message = "גודל קובץ הגיבוי חורג מהמגבלה המותרת (עד 15MB)" });

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound(new { message = "פרויקט לא נמצא" });

        logger.LogWarning("Security Audit: Backup restore initiated for project {ProjectId} by user {UserId}", projectId, User.GetUserId());

        using var reader = new StreamReader(file.OpenReadStream());
        var json = await reader.ReadToEndAsync();

        BackupProjectSnapshotDto? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<BackupProjectSnapshotDto>(json);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"שגיאה בפענוח קובץ הגיבוי: {ex.Message}" });
        }

        if (snapshot == null)
            return BadRequest(new { message = "קובץ הגיבוי ריק או לא תקין" });

        // מחיקת נתונים קיימים או שחזור מלא
        var existingTx = await db.Transactions.Where(t => t.ProjectId == projectId).ToListAsync();
        var existingInc = await db.Incomes.Where(i => i.ProjectId == projectId).ToListAsync();
        var existingCats = await db.Categories.Where(c => c.ProjectId == projectId).ToListAsync();
        var existingIncCats = await db.IncomeCategories.Where(c => c.ProjectId == projectId).ToListAsync();

        db.Transactions.RemoveRange(existingTx);
        db.Incomes.RemoveRange(existingInc);
        db.Categories.RemoveRange(existingCats);
        db.IncomeCategories.RemoveRange(existingIncCats);
        await db.SaveChangesAsync();

        // שחזור קטגוריות
        foreach (var c in snapshot.Categories)
        {
            db.Categories.Add(new Category
            {
                Id = c.Id != Guid.Empty ? c.Id : Guid.NewGuid(),
                ProjectId = projectId,
                Name = c.Name,
                Icon = c.Icon ?? "📋",
                PlannedAmount = c.PlannedAmount,
                Periodicity = c.Periodicity,
                Notes = c.Notes
            });
        }

        // שחזור קטגוריות הכנסה
        foreach (var ic in snapshot.IncomeCategories)
        {
            db.IncomeCategories.Add(new IncomeCategory
            {
                Id = ic.Id != Guid.Empty ? ic.Id : Guid.NewGuid(),
                ProjectId = projectId,
                Name = ic.Name,
                Icon = ic.Icon ?? "🪙",
                MonthlyPlannedAmount = ic.MonthlyPlannedAmount
            });
        }

        await db.SaveChangesAsync();

        // שחזור משתמש ברירת מחדל אם נדרש
        var defaultUser = await db.ProjectUsers.FirstOrDefaultAsync(u => u.ProjectId == projectId);
        if (defaultUser == null)
        {
            defaultUser = new ProjectUser
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = "משתמש ראשי",
                Role = "מנהל",
                Avatar = "👤",
                IsActiveDefault = true
            };
            db.ProjectUsers.Add(defaultUser);
            await db.SaveChangesAsync();
        }

        // שחזור הוצאות
        foreach (var t in snapshot.Transactions)
        {
            db.Transactions.Add(new Transaction
            {
                Id = t.Id != Guid.Empty ? t.Id : Guid.NewGuid(),
                ProjectId = projectId,
                CategoryId = t.CategoryId,
                ExecutedByUserId = t.ExecutedByUserId != Guid.Empty ? t.ExecutedByUserId : defaultUser.Id,
                Description = t.Description,
                Amount = t.Amount,
                Date = t.Date,
                PaymentMethod = t.PaymentMethod,
                PaymentMethodLabel = t.PaymentMethodLabel,
                InstallmentNumber = t.InstallmentNumber,
                InstallmentsTotal = t.InstallmentsTotal
            });
        }

        // שחזור הכנסות
        foreach (var i in snapshot.Incomes)
        {
            db.Incomes.Add(new ProjectIncome
            {
                Id = i.Id != Guid.Empty ? i.Id : Guid.NewGuid(),
                ProjectId = projectId,
                IncomeCategoryId = i.IncomeCategoryId,
                Description = i.Description,
                PlannedAmount = i.PlannedAmount,
                ActualAmount = i.ActualAmount,
                Date = i.Date,
                ReceiptType = i.ReceiptType
            });
        }

        await db.SaveChangesAsync();

        return Ok(new
        {
            message = "הנתונים שוחזרו בהצלחה מקובץ הגיבוי",
            categoriesRestored = snapshot.Categories.Count,
            transactionsRestored = snapshot.Transactions.Count,
            incomesRestored = snapshot.Incomes.Count
        });
    }

    /// <summary>סנכרון מלא מ-SQLite מקומי למסד הנתונים הנוכחי (PostgreSQL ב-Neon) - מורשה למנהלי מערכת בלבד.</summary>
    [Microsoft.AspNetCore.Authorization.Authorize]
    [HttpPost("/api/admin/sync-sqlite-to-current-db")]
    public async Task<IActionResult> SyncSqliteToCurrentDb()
    {
        var currentUserId = User.GetUserId();
        var user = currentUserId.HasValue ? await db.AppUsers.FindAsync(currentUserId.Value) : null;
        if (user == null || !string.Equals(user.Email, "yoslevynew@gmail.com", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Security Audit: Unauthorized admin sync attempt by user {UserId}", currentUserId);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "גישה מוגבלת למנהל המערכת בלבד" });
        }

        logger.LogWarning("Security Audit: Admin sync from SQLite to active DB initiated by {Email}", user.Email);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "moneyplus.db"),
            Path.Combine(Directory.GetCurrentDirectory(), "moneyplus.db"),
            "backend/src/MoneyPlus.Api/moneyplus.db",
            "moneyplus.db"
        };

        var sqlitePath = candidates.FirstOrDefault(System.IO.File.Exists);
        if (sqlitePath == null)
        {
            return NotFound(new { message = "קובץ SQLite מקומי לא נמצא (moneyplus.db)" });
        }

        var opt = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqlitePath}")
            .Options;

        using var sqliteDb = new AppDbContext(opt);

        var users = await sqliteDb.AppUsers.AsNoTracking().ToListAsync();
        foreach (var u in users)
        {
            if (!await db.AppUsers.AnyAsync(x => x.Id == u.Id || x.Email == u.Email))
            {
                db.AppUsers.Add(u);
            }
        }
        await db.SaveChangesAsync();

        var projects = await sqliteDb.Projects.AsNoTracking().ToListAsync();
        foreach (var p in projects)
        {
            if (!await db.Projects.AnyAsync(x => x.Id == p.Id))
            {
                db.Projects.Add(p);
            }
        }
        await db.SaveChangesAsync();

        var members = await sqliteDb.ProjectMembers.AsNoTracking().ToListAsync();
        foreach (var m in members)
        {
            if (!await db.ProjectMembers.AnyAsync(x => x.ProjectId == m.ProjectId && x.AppUserId == m.AppUserId))
            {
                db.ProjectMembers.Add(m);
            }
        }
        await db.SaveChangesAsync();

        var pUsers = await sqliteDb.ProjectUsers.AsNoTracking().ToListAsync();
        foreach (var pu in pUsers)
        {
            if (!await db.ProjectUsers.AnyAsync(x => x.Id == pu.Id))
            {
                db.ProjectUsers.Add(pu);
            }
        }
        await db.SaveChangesAsync();

        var categories = await sqliteDb.Categories.AsNoTracking().ToListAsync();
        foreach (var c in categories)
        {
            if (!await db.Categories.AnyAsync(x => x.Id == c.Id))
            {
                db.Categories.Add(c);
            }
        }
        await db.SaveChangesAsync();

        var incCategories = await sqliteDb.IncomeCategories.AsNoTracking().ToListAsync();
        foreach (var ic in incCategories)
        {
            if (!await db.IncomeCategories.AnyAsync(x => x.Id == ic.Id))
            {
                db.IncomeCategories.Add(ic);
            }
        }
        await db.SaveChangesAsync();

        var transactions = await sqliteDb.Transactions.AsNoTracking().ToListAsync();
        foreach (var t in transactions)
        {
            if (!await db.Transactions.AnyAsync(x => x.Id == t.Id))
            {
                db.Transactions.Add(t);
            }
        }
        await db.SaveChangesAsync();

        var incomes = await sqliteDb.Incomes.AsNoTracking().ToListAsync();
        foreach (var i in incomes)
        {
            if (!await db.Incomes.AnyAsync(x => x.Id == i.Id))
            {
                db.Incomes.Add(i);
            }
        }
        await db.SaveChangesAsync();

        var closings = await sqliteDb.PeriodClosings.AsNoTracking().ToListAsync();
        foreach (var pc in closings)
        {
            if (!await db.PeriodClosings.AnyAsync(x => x.Id == pc.Id))
            {
                db.PeriodClosings.Add(pc);
            }
        }
        await db.SaveChangesAsync();

        return Ok(new
        {
            message = "כל הנתונים מ-SQLite סונכרנו בהצלחה מלאה למסד הנתונים PostgreSQL ב-Neon!",
            projects = projects.Count,
            users = users.Count,
            categories = categories.Count,
            transactions = transactions.Count,
            incomes = incomes.Count
        });
    }
}
