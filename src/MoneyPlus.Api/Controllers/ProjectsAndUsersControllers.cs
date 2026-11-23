using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController(AppDbContext db, ILogger<ProjectsController> logger) : ControllerBase
{
    public record ProjectDto(Guid Id, string Name, string DefaultCurrency, int CycleStartDay, List<string> PaymentMethods, string? DailyReminderTime, string ProjectType = "התנהלות חודשית", List<string>? InactivePaymentMethods = null, string PlanningBasis = "smart");
    public record CreateProjectRequest(string Name, string? DefaultCurrency, int? CycleStartDay, List<string>? PaymentMethods, string? DailyReminderTime, string? ProjectType, List<string>? InactivePaymentMethods = null, string? PlanningBasis = null);

    private static string NormalizePlanningBasis(string? basis) => basis?.Trim() switch
    {
        "avg3m" => "avg3m",
        "currentPlanned" => "currentPlanned",
        "currentActual" => "currentActual",
        _ => "smart"
    };

    private static ProjectDto ToProjectDto(Project p) => new(
        p.Id,
        p.Name,
        p.DefaultCurrency,
        p.CycleStartDay,
        p.GetPaymentMethods(),
        p.DailyReminderTime,
        string.IsNullOrWhiteSpace(p.ProjectType) ? "התנהלות חודשית" : p.ProjectType,
        p.GetInactivePaymentMethods(),
        NormalizePlanningBasis(p.PlanningBasis)
    );

    [HttpGet]
    public async Task<List<ProjectDto>> Get()
    {
        var uid = User.GetUserId();
        var projects = await db.ProjectMembers.AsNoTracking().Where(m => m.AppUserId == uid && m.Project.IsActive)
            .OrderBy(m => m.Project.CreatedAt)
            .Select(m => m.Project).ToListAsync();

        return projects.Select(ToProjectDto).ToList();
    }

    [HttpGet("{projectId:guid}")]
    public async Task<ActionResult<ProjectDto>> GetById(Guid projectId)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId && x.IsActive);
        if (p is null) return NotFound();
        return Ok(ToProjectDto(p));
    }

    [HttpPost]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return BadRequest("שם פרויקט חובה");
        var uid = User.GetUserId();
        if (uid is null) return Unauthorized();
        var p = new Project
        {
            Name = r.Name.Trim(),
            ProjectType = string.IsNullOrWhiteSpace(r.ProjectType) ? "התנהלות חודשית" : r.ProjectType.Trim(),
            DefaultCurrency = r.DefaultCurrency ?? "ILS",
            CycleStartDay = r.CycleStartDay ?? 10,
            DailyReminderTime = r.DailyReminderTime ?? "20:30"
        };
        if (r.PaymentMethods is not null && r.PaymentMethods.Count > 0)
        {
            p.SetPaymentMethods(r.PaymentMethods);
        }
        else
        {
            // ירושה אוטומטית של כרטיסי האשראי (💳) מפרויקטים קיימים של המשתמש כדי שבכל הפרויקטים יהיו כרטיסי האשראי
            var existingMemberProjects = await db.ProjectMembers.AsNoTracking()
                .Where(m => m.AppUserId == uid.Value && m.Project.IsActive)
                .Select(m => m.Project)
                .ToListAsync();
            var sharedCards = existingMemberProjects
                .SelectMany(ep => ep.GetPaymentMethods())
                .Where(m => m.StartsWith("💳") || m.Contains("ויזה") || m.Contains("ישראכרט") || m.Contains("מאסטרקארד") || m.Contains("כאל") || m.Contains("מקס"))
                .Distinct()
                .ToList();
            if (sharedCards.Count > 0)
            {
                p.SetPaymentMethods(p.GetPaymentMethods().Concat(sharedCards));
            }
        }

        // כל פרויקט חדש מתחיל עם קטגוריות הכנסה בסיסיות ומשתמשי הבית
        p.IncomeCategories =
        [
            new() { ProjectId = p.Id, Name = "שכר עבודה", Icon = "💼" },
            new() { ProjectId = p.Id, Name = "קצבאות וסיוע", Icon = "🏛️" },
            new() { ProjectId = p.Id, Name = "מתנות ואחר", Icon = "🎁" }
        ];
        p.Users =
        [
            new() { ProjectId = p.Id, Name = "יוסי", Role = "מנהל ראשי 👑", Avatar = "👨‍💼", IsActiveDefault = true },
            new() { ProjectId = p.Id, Name = "ענבר", Role = "שותפה 🤝", Avatar = "👩‍💼", IsActiveDefault = false }
        ];
        db.Projects.Add(p);
        db.ProjectMembers.Add(new ProjectMember { ProjectId = p.Id, AppUserId = uid.Value, Role = MemberRole.Owner });
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), ToProjectDto(p));
    }

    public record UpdateProjectRequest(string? Name, string? DefaultCurrency, int? CycleStartDay, List<string>? PaymentMethods, string? DailyReminderTime, string? ProjectType, List<string>? InactivePaymentMethods = null, string? PlanningBasis = null);

    [HttpPut("{projectId:guid}")]
    public async Task<ActionResult<ProjectDto>> Update(Guid projectId, [FromBody] UpdateProjectRequest r)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(r.Name)) p.Name = r.Name.Trim();
        if (!string.IsNullOrWhiteSpace(r.ProjectType)) p.ProjectType = r.ProjectType.Trim();
        if (!string.IsNullOrWhiteSpace(r.DefaultCurrency)) p.DefaultCurrency = r.DefaultCurrency.Trim();
        if (r.CycleStartDay.HasValue) p.CycleStartDay = Math.Clamp(r.CycleStartDay.Value, 1, 28);
        if (!string.IsNullOrWhiteSpace(r.PlanningBasis)) p.PlanningBasis = NormalizePlanningBasis(r.PlanningBasis);
        if (r.PaymentMethods is not null)
        {
            var oldMethods = p.GetPaymentMethods();
            var systemMethods = oldMethods.Where(m => m == "עסקת תשלומים" || m == "Installments" || m.Contains("תשלומים")).ToList();
            var candidateMethods = r.PaymentMethods.Union(systemMethods).ToList();

            var removedMethods = oldMethods
                .Where(m => m != "עסקת תשלומים" && m != "Installments" && !m.Contains("תשלומים"))
                .Except(candidateMethods)
                .ToList();

            if (removedMethods.Count > 0)
            {
                var txLabels = await db.Transactions
                    .Where(t => t.ProjectId == projectId && !string.IsNullOrEmpty(t.PaymentMethodLabel))
                    .Select(t => t.PaymentMethodLabel!)
                    .Distinct()
                    .ToListAsync();

                foreach (var rm in removedMethods)
                {
                    var rmClean = System.Text.RegularExpressions.Regex.Replace(rm, @"^[\p{Cs}\p{So}\p{Sk}\p{Sm}\s]+", "").Trim();
                    if (txLabels.Any(l => l.Equals(rm, StringComparison.OrdinalIgnoreCase) ||
                                          System.Text.RegularExpressions.Regex.Replace(l, @"^[\p{Cs}\p{So}\p{Sk}\p{Sm}\s]+", "").Trim().Equals(rmClean, StringComparison.OrdinalIgnoreCase)))
                    {
                        return BadRequest($"לא ניתן למחוק את אמצעי התשלום \"{rm}\" מכיוון שקיימות תנועות המשויכות אליו. ניתן לשנות את שמו בלבד.");
                    }
                }
            }
            p.SetPaymentMethods(candidateMethods);
        }
        if (r.InactivePaymentMethods is not null)
        {
            p.SetInactivePaymentMethods(r.InactivePaymentMethods);
        }
        if (r.DailyReminderTime is not null) p.DailyReminderTime = string.IsNullOrWhiteSpace(r.DailyReminderTime) ? null : r.DailyReminderTime.Trim();
        await db.SaveChangesAsync();
        return Ok(ToProjectDto(p));
    }

    [HttpGet("{projectId:guid}/payment-methods/in-use")]
    public async Task<ActionResult<List<string>>> GetInUsePaymentMethods(Guid projectId)
    {
        var labels = await db.Transactions
            .Where(t => t.ProjectId == projectId)
            .Select(t => !string.IsNullOrEmpty(t.PaymentMethodLabel) ? t.PaymentMethodLabel : t.PaymentMethod.ToString())
            .Distinct()
            .ToListAsync();
        return Ok(labels);
    }

    [HttpDelete("{projectId:guid}")]
    [RequireProjectOwner]
    public async Task<IActionResult> Delete(Guid projectId)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return NotFound();
        p.IsActive = false;
        await db.SaveChangesAsync();
        logger.LogWarning("Security Audit: Project {ProjectId} deactivated (deleted) by user {UserId}", projectId, User.GetUserId());
        return NoContent();
    }

    [HttpPost("{projectId:guid}/payment-methods/rename")]
    public async Task<ActionResult<ProjectDto>> RenamePaymentMethod(Guid projectId, [FromBody] RenamePaymentMethodRequest req)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return NotFound("פרויקט לא נמצא");

        var oldClean = req.OldName.Trim();
        var newClean = req.NewName.Trim();
        if (string.IsNullOrWhiteSpace(oldClean) || string.IsNullOrWhiteSpace(newClean))
            return BadRequest("שמות אמצעי התשלום לא יכולים להיות ריקים");

        var methods = p.GetPaymentMethods();
        var oldNameNoEmoji = System.Text.RegularExpressions.Regex.Replace(oldClean, @"^[\p{Cs}\p{So}\p{Sk}\p{Sm}\s]+", "").Trim();
        
        var index = methods.FindIndex(m => 
            m.Equals(oldClean, StringComparison.OrdinalIgnoreCase) || 
            System.Text.RegularExpressions.Regex.Replace(m, @"^[\p{Cs}\p{So}\p{Sk}\p{Sm}\s]+", "").Trim().Equals(oldNameNoEmoji, StringComparison.OrdinalIgnoreCase));
        
        if (index >= 0)
        {
            methods[index] = newClean;
        }
        else
        {
            methods.Add(newClean);
        }
        p.SetPaymentMethods(methods);

        var txs = await db.Transactions.Where(t => t.ProjectId == projectId).ToListAsync();
        foreach (var t in txs)
        {
            var tLabelNoEmoji = t.PaymentMethodLabel != null 
                ? System.Text.RegularExpressions.Regex.Replace(t.PaymentMethodLabel, @"^[\p{Cs}\p{So}\p{Sk}\p{Sm}\s]+", "").Trim() 
                : "";

            if (string.Equals(t.PaymentMethodLabel, oldClean, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(tLabelNoEmoji) && string.Equals(tLabelNoEmoji, oldNameNoEmoji, StringComparison.OrdinalIgnoreCase)) ||
                (string.IsNullOrEmpty(t.PaymentMethodLabel) && string.Equals(t.PaymentMethod.ToString(), oldNameNoEmoji, StringComparison.OrdinalIgnoreCase)))
            {
                t.PaymentMethodLabel = newClean;
            }
        }

        var inactives = p.GetInactivePaymentMethods();
        var inactIdx = inactives.FindIndex(m =>
            m.Equals(oldClean, StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.Replace(m, @"^[\p{Cs}\p{So}\p{Sk}\p{Sm}\s]+", "").Trim().Equals(oldNameNoEmoji, StringComparison.OrdinalIgnoreCase));
        if (inactIdx >= 0)
        {
            inactives[inactIdx] = newClean;
            p.SetInactivePaymentMethods(inactives);
        }

        await db.SaveChangesAsync();
        return Ok(new ProjectDto(p.Id, p.Name, p.DefaultCurrency, p.CycleStartDay, p.GetPaymentMethods(), p.DailyReminderTime, string.IsNullOrWhiteSpace(p.ProjectType) ? "התנהלות חודשית" : p.ProjectType, p.GetInactivePaymentMethods()));
    }
}

/// <summary>משתמשים דינאמיים – מנוהלים בהגדרות בלבד, ללא קיבוע.</summary>
[ApiController]
[Route("api/projects/{projectId:guid}/users")]
public class ProjectUsersController(AppDbContext db) : ControllerBase
{
    public record UpdateProjectUserRequest(string? Name, string? Role, string? Avatar);

    [HttpGet]
    public async Task<List<ProjectUserDto>> Get(Guid projectId) =>
        await db.ProjectUsers.AsNoTracking().Where(u => u.ProjectId == projectId).OrderBy(u => u.CreatedAt)
            .Select(u => new ProjectUserDto(u.Id, u.Name, u.Role, u.Avatar, u.IsActiveDefault)).ToListAsync();

    [HttpPost]
    public async Task<ActionResult<ProjectUserDto>> Add(Guid projectId, CreateProjectUserRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return BadRequest("שם משתמש חובה");
        var isFirst = !await db.ProjectUsers.AnyAsync(u => u.ProjectId == projectId);
        var u = new ProjectUser { ProjectId = projectId, Name = r.Name.Trim(), Role = r.Role, Avatar = string.IsNullOrWhiteSpace(r.Avatar) ? "🧑" : r.Avatar, IsActiveDefault = isFirst };
        db.ProjectUsers.Add(u);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { projectId }, new ProjectUserDto(u.Id, u.Name, u.Role, u.Avatar, u.IsActiveDefault));
    }

    [HttpPut("{userId:guid}")]
    public async Task<ActionResult<ProjectUserDto>> Update(Guid projectId, Guid userId, UpdateProjectUserRequest r)
    {
        var u = await db.ProjectUsers.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == userId);
        if (u is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(r.Name)) u.Name = r.Name.Trim();
        if (!string.IsNullOrWhiteSpace(r.Role)) u.Role = r.Role.Trim();
        if (!string.IsNullOrWhiteSpace(r.Avatar)) u.Avatar = r.Avatar.Trim();
        await db.SaveChangesAsync();
        return Ok(new ProjectUserDto(u.Id, u.Name, u.Role, u.Avatar, u.IsActiveDefault));
    }

    [HttpPut("{userId:guid}/activate")]
    public async Task<IActionResult> SetActive(Guid projectId, Guid userId)
    {
        var users = await db.ProjectUsers.Where(u => u.ProjectId == projectId).ToListAsync();
        if (users.All(u => u.Id != userId)) return NotFound();
        users.ForEach(u => u.IsActiveDefault = u.Id == userId);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid userId)
    {
        var count = await db.ProjectUsers.CountAsync(u => u.ProjectId == projectId);
        if (count <= 1) return BadRequest("חובה להשאיר לפחות משתמש אחד");
        if (await db.Transactions.AnyAsync(t => t.ExecutedByUserId == userId))
            return Conflict("למשתמש יש תנועות היסטוריות – לא ניתן למחוק");
        var u = await db.ProjectUsers.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == userId);
        if (u is null) return NotFound();
        db.ProjectUsers.Remove(u);
        if (u.IsActiveDefault)
            (await db.ProjectUsers.FirstAsync(x => x.ProjectId == projectId && x.Id != userId)).IsActiveDefault = true;
        await db.SaveChangesAsync();
        return NoContent();
    }
}

[ApiController]
[Route("api/projects/{projectId:guid}/income-categories")]
public class IncomeCategoriesController(AppDbContext db, Microsoft.AspNetCore.SignalR.IHubContext<MoneyPlus.Api.Hubs.BudgetHub> hub) : ControllerBase
{
    [HttpGet]
    public async Task<List<IncomeCategoryDto>> Get(Guid projectId, [FromQuery] int? year, [FromQuery] int? month)
    {
        var cats = await db.IncomeCategories.AsNoTracking().Where(c => c.ProjectId == projectId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        if (year.HasValue && month.HasValue)
        {
            var y = year.Value;
            var m = month.Value;
            var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
            var (from, to) = PeriodMath.Range(y, m, cycleDay);
            var activeCatIds = await db.Incomes.AsNoTracking()
                .Where(i => i.ProjectId == projectId && i.Date >= from && i.Date <= to)
                .Select(i => i.IncomeCategoryId)
                .Distinct()
                .ToListAsync();
            var activeSet = activeCatIds.ToHashSet();
            var projectMax = BudgetRolloverHelper.FindMaxMonth(cats.Select(c => c.GetMonthlyBudgets()));

            return cats
                .Where(c => c.IsActiveInMonth(y, m, activeSet.Contains(c.Id), projectMax))
                .Select(c => new IncomeCategoryDto(c.Id, c.Name, c.Icon, c.GetPlannedForMonth(y, m, projectMax)))
                .ToList();
        }

        return cats.Select(c => new IncomeCategoryDto(c.Id, c.Name, c.Icon, c.MonthlyPlannedAmount)).ToList();
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> Reorder(Guid projectId, ReorderCategoriesRequest r)
    {
        var cats = await db.IncomeCategories.Where(c => c.ProjectId == projectId).ToListAsync();
        var orderMap = new Dictionary<Guid, int>();
        if (r.OrderedIds is { Count: > 0 })
        {
            for (var i = 0; i < r.OrderedIds.Count; i++)
                orderMap[r.OrderedIds[i]] = i + 1;
        }
        foreach (var c in cats)
        {
            c.SortOrder = orderMap.TryGetValue(c.Id, out var pos) ? pos : 0;
        }
        await db.SaveChangesAsync();
        try { await hub.Clients.Group(MoneyPlus.Api.Hubs.BudgetHub.ProjectGroup(projectId)).SendAsync("CategoriesReordered", new { projectId, kind = "income" }); } catch { }
        return Ok(new { ok = true });
    }

    [HttpPost]
    public async Task<ActionResult<IncomeCategoryDto>> Create(Guid projectId, CreateIncomeCategoryRequest r, [FromQuery] int? year, [FromQuery] int? month)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return BadRequest("שם קטגוריה חובה");
        var cleanName = r.Name.Trim();
        var existing = await db.IncomeCategories.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Name.ToLower() == cleanName.ToLower());
        if (existing is not null)
        {
            if (year.HasValue && month.HasValue)
            {
                var budgets = existing.GetMonthlyBudgets();
                var key = $"{month.Value:D2}/{year.Value}";
                budgets[key] = r.MonthlyPlannedAmount;
                existing.SetMonthlyBudgets(budgets);
                await db.SaveChangesAsync();
                return Ok(new IncomeCategoryDto(existing.Id, existing.Name, existing.Icon, r.MonthlyPlannedAmount));
            }
            return Conflict(new { message = $"קטגוריית הכנסה בשם '{cleanName}' כבר קיימת בפרויקט" });
        }

        var c = new IncomeCategory { ProjectId = projectId, Name = cleanName, Icon = string.IsNullOrWhiteSpace(r.Icon) ? "🪙" : r.Icon, MonthlyPlannedAmount = r.MonthlyPlannedAmount };
        if (year.HasValue && month.HasValue)
        {
            var key = $"{month.Value:D2}/{year.Value}";
            c.SetMonthlyBudgets(new Dictionary<string, decimal> { [key] = r.MonthlyPlannedAmount });
        }
        db.IncomeCategories.Add(c);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { projectId }, new IncomeCategoryDto(c.Id, c.Name, c.Icon, c.MonthlyPlannedAmount));
    }

    [HttpPut("batch-plan")]
    public async Task<ActionResult<object>> BatchPlan(Guid projectId, [FromBody] BatchPlanCategoriesRequest r)
    {
        if (r.Year < 2000 || r.Year > 2100 || r.Month < 1 || r.Month > 12)
            return BadRequest("תאריך חודש יעד אינו תקין");
        if (r.Items is null || r.Items.Count == 0)
            return BadRequest("לא נבחרו קטגוריות הכנסה לעדכון");

        var cats = await db.IncomeCategories.Where(c => c.ProjectId == projectId).ToListAsync();
        var catById = cats.ToDictionary(c => c.Id);
        var key = $"{r.Month:D2}/{r.Year}";
        int updated = 0;

        foreach (var item in r.Items)
        {
            if (!catById.TryGetValue(item.CategoryId, out var c)) continue;
            var budgets = c.GetMonthlyBudgets();
            budgets[key] = item.Hidden ? -1m : Math.Max(0m, item.PlannedAmount);
            c.SetMonthlyBudgets(budgets);
            updated++;
        }

        await db.SaveChangesAsync();
        return Ok(new { updatedCount = updated, year = r.Year, month = r.Month });
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<IncomeCategoryDto>> Update(Guid projectId, Guid id, UpdateIncomeCategoryRequest r, [FromQuery] int? year, [FromQuery] int? month)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return BadRequest("שם קטגוריה חובה");
        var c = await db.IncomeCategories.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id);
        if (c is null) return NotFound();
        c.Name = r.Name.Trim();
        if (!string.IsNullOrWhiteSpace(r.Icon)) c.Icon = r.Icon;

        var budgets = c.GetMonthlyBudgets();
        if (year.HasValue && month.HasValue)
        {
            var key = $"{month.Value:D2}/{year.Value}";
            budgets[key] = r.MonthlyPlannedAmount;
            c.SetMonthlyBudgets(budgets);
        }
        else
        {
            c.MonthlyPlannedAmount = r.MonthlyPlannedAmount;
        }

        await db.SaveChangesAsync();
        var planned = year.HasValue && month.HasValue ? c.GetPlannedForMonth(year.Value, month.Value) : c.MonthlyPlannedAmount;
        return Ok(new IncomeCategoryDto(c.Id, c.Name, c.Icon, planned));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, [FromQuery] int? year, [FromQuery] int? month)
    {
        var c = await db.IncomeCategories.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id);
        if (c is null) return NotFound();

        if (year.HasValue && month.HasValue)
        {
            var budgets = c.GetMonthlyBudgets();
            var key = $"{month.Value:D2}/{year.Value}";
            budgets[key] = -1m;
            c.SetMonthlyBudgets(budgets);
            await db.SaveChangesAsync();
            return NoContent();
        }

        var related = await db.Incomes.Where(i => i.IncomeCategoryId == id).ToListAsync();
        if (related.Count > 0)
        {
            db.Incomes.RemoveRange(related);
        }
        db.IncomeCategories.Remove(c);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
