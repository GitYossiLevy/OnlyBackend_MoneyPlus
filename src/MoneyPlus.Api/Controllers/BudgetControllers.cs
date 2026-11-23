using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Hubs;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/categories")]
public class CategoriesController(AppDbContext db, IHubContext<BudgetHub> hub) : ControllerBase
{
    /// <summary>קטגוריות הוצאה עם ניצול תקציב לחודש נתון (אם נשלחו year ו-month: רק קטגוריות פעילות באותו חודש; אחרת: כל קטגוריות הפרויקט).</summary>
    [HttpGet]
    public async Task<List<CategoryDto>> Get(Guid projectId, [FromQuery] int? year, [FromQuery] int? month)
    {
        if (year.HasValue && month.HasValue)
        {
            var y = year.Value;
            var m = month.Value;
            var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
            var (curY, curM) = SavingsGoalsService.ResolveCycleForDate(DateOnly.FromDateTime(DateTime.Today), cycleDay);
            if (y * 12 + m >= curY * 12 + curM)
            {
                await SavingsGoalsService.SyncCategoryBudgetsForCycleAsync(db, projectId, y, m);
            }

            var catsForMonth = await db.Categories.AsNoTracking().Where(c => c.ProjectId == projectId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
            var (from, to) = PeriodMath.Range(y, m, cycleDay);
            var spent = await db.Transactions.AsNoTracking()
                .Where(t => t.ProjectId == projectId && t.Date >= from && t.Date <= to)
                .GroupBy(t => t.CategoryId)
                .Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) })
                .ToDictionaryAsync(x => x.Key, x => x.Sum);

            var projectMax = BudgetRolloverHelper.FindMaxMonth(catsForMonth.Select(c => c.GetMonthlyBudgets()));
            return catsForMonth
                .Where(c => c.IsActiveInMonth(y, m, spent.ContainsKey(c.Id), projectMax))
                .Select(c =>
                {
                    var s = spent.GetValueOrDefault(c.Id);
                    var planned = c.GetPlannedForMonth(y, m, projectMax);
                    var pct = planned > 0 ? (int)Math.Round(s / planned * 100) : 0;
                    return new CategoryDto(c.Id, c.Name, c.Notes, c.Periodicity, planned, s, planned - s, pct, c.Icon ?? "📁", c.IsCapitalTransfer);
                }).ToList();
        }

        var cats = await db.Categories.AsNoTracking().Where(c => c.ProjectId == projectId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        return cats.Select(c => new CategoryDto(c.Id, c.Name, c.Notes, c.Periodicity, c.PlannedAmount, 0, c.PlannedAmount, 0, c.Icon ?? "📁", c.IsCapitalTransfer)).ToList();
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> Reorder(Guid projectId, ReorderCategoriesRequest r)
    {
        var cats = await db.Categories.Where(c => c.ProjectId == projectId).ToListAsync();
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
        try { await hub.Clients.Group(BudgetHub.ProjectGroup(projectId)).SendAsync("CategoriesReordered", new { projectId, kind = "expense" }); } catch { }
        return Ok(new { ok = true });
    }

    [HttpPost]
    public async Task<ActionResult<CategoryDto>> Create(Guid projectId, CreateCategoryRequest r, [FromQuery] int? year, [FromQuery] int? month)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return BadRequest("שם קטגוריה חובה");
        if (r.PlannedAmount < 0) return BadRequest("סכום תכנון לא תקין");

        var cleanName = r.Name.Trim();
        var existing = await db.Categories.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Name.ToLower() == cleanName.ToLower());
        if (existing is not null)
        {
            if (year.HasValue && month.HasValue)
            {
                var budgets = existing.GetMonthlyBudgets();
                var key = $"{month.Value:D2}/{year.Value}";
                budgets[key] = r.PlannedAmount;
                existing.SetMonthlyBudgets(budgets);
                await db.SaveChangesAsync();
                return Ok(new CategoryDto(existing.Id, existing.Name, existing.Notes, existing.Periodicity, r.PlannedAmount, 0, r.PlannedAmount, 0, existing.Icon, existing.IsCapitalTransfer));
            }
            return Conflict(new { message = $"קטגוריה בשם '{cleanName}' כבר קיימת בפרויקט" });
        }

        var c = new Category {
            ProjectId = projectId,
            Name = cleanName,
            Notes = r.Notes,
            Periodicity = r.Periodicity,
            PlannedAmount = r.PlannedAmount,
            Icon = string.IsNullOrWhiteSpace(r.Icon) ? "📁" : r.Icon,
            IsCapitalTransfer = r.IsCapitalTransfer
        };
        if (year.HasValue && month.HasValue)
        {
            var key = $"{month.Value:D2}/{year.Value}";
            c.SetMonthlyBudgets(new Dictionary<string, decimal> { [key] = r.PlannedAmount });
        }
        db.Categories.Add(c);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { projectId }, new CategoryDto(c.Id, c.Name, c.Notes, c.Periodicity, c.PlannedAmount, 0, c.PlannedAmount, 0, c.Icon, c.IsCapitalTransfer));
    }

    [HttpPut("batch-plan")]
    public async Task<ActionResult<object>> BatchPlan(Guid projectId, [FromBody] BatchPlanCategoriesRequest r)
    {
        if (r.Year < 2000 || r.Year > 2100 || r.Month < 1 || r.Month > 12)
            return BadRequest("תאריך חודש יעד אינו תקין");
        if (r.Items is null || r.Items.Count == 0)
            return BadRequest("לא נבחרו קטגוריות לעדכון");

        var cats = await db.Categories.Where(c => c.ProjectId == projectId).ToListAsync();
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

    [HttpPut("{categoryId:guid}")]
    public async Task<ActionResult<CategoryDto>> Update(Guid projectId, Guid categoryId, UpdateCategoryRequest r, [FromQuery] int? year, [FromQuery] int? month)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return BadRequest("שם קטגוריה חובה");
        if (r.PlannedAmount < 0) return BadRequest("סכום תכנון לא תקין");
        var c = await db.Categories.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == categoryId);
        if (c is null) return NotFound();
        var oldName = c.Name;
        var oldIcon = c.Icon;
        var newName = r.Name.Trim();
        c.Name = newName;
        c.Notes = r.Notes;
        c.Periodicity = r.Periodicity;
        c.IsCapitalTransfer = r.IsCapitalTransfer;
        if (!string.IsNullOrWhiteSpace(r.Icon)) c.Icon = r.Icon;

        var budgets = c.GetMonthlyBudgets();
        if (year.HasValue && month.HasValue)
        {
            var key = $"{month.Value:D2}/{year.Value}";
            budgets[key] = r.PlannedAmount;
            c.SetMonthlyBudgets(budgets);
        }
        else
        {
            c.PlannedAmount = r.PlannedAmount;
        }

        // סנכרון שם/אייקון ביעדי חיסכון המקושרים לקטגוריה זו
        if (!string.Equals(oldName, newName, StringComparison.Ordinal) || !string.Equals(oldIcon, c.Icon, StringComparison.Ordinal))
        {
            var linkedGoals = await db.SavingsGoals
                .Where(g => g.ProjectId == projectId && g.LinkedCategoryId == categoryId)
                .ToListAsync();
            foreach (var g in linkedGoals)
            {
                if (string.Equals(g.Name.Trim(), oldName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    g.Name = newName;
                    g.UpdatedAt = DateTime.UtcNow;
                }
                if (string.Equals(g.Icon, oldIcon, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(c.Icon))
                {
                    g.Icon = c.Icon;
                    g.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        // סנכרון שם הקטגוריה בסשנים שמורים של התאמת כרטיסי אשראי
        if (!string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            var catIdStr = categoryId.ToString("D");
            var sessions = await db.CreditCardReconciliationSessions
                .Where(s => s.ProjectId == projectId &&
                            (s.SessionDataJson.Contains(catIdStr) || s.SessionDataJson.Contains(oldName)))
                .ToListAsync();
            foreach (var session in sessions)
            {
                if (TryUpdateCategoryNameInSessionJson(session.SessionDataJson, catIdStr, oldName, newName, out var updatedJson))
                {
                    session.SessionDataJson = updatedJson;
                    session.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        await db.SaveChangesAsync();
        var planned = year.HasValue && month.HasValue ? c.GetPlannedForMonth(year.Value, month.Value) : c.PlannedAmount;
        return Ok(new CategoryDto(c.Id, c.Name, c.Notes, c.Periodicity, planned, 0, planned, 0, c.Icon, c.IsCapitalTransfer));
    }

    [HttpDelete("{categoryId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid categoryId, [FromQuery] int? year, [FromQuery] int? month)
    {
        var c = await db.Categories.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == categoryId);
        if (c is null) return NotFound();

        if (year.HasValue && month.HasValue)
        {
            // הסרה מחודש ספציפי בלבד (סימון שלילי כדי לא להסתיר קטגוריות עם תקציב 0)
            var budgets = c.GetMonthlyBudgets();
            var key = $"{month.Value:D2}/{year.Value}";
            budgets[key] = -1m;
            c.SetMonthlyBudgets(budgets);
            await db.SaveChangesAsync();
            return NoContent();
        }

        var related = await db.Transactions.Where(t => t.CategoryId == categoryId).ToListAsync();
        if (related.Count > 0)
        {
            db.Transactions.RemoveRange(related);
        }
        db.Categories.Remove(c);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static bool TryUpdateCategoryNameInSessionJson(string json, string categoryId, string oldName, string newName, out string updatedJson)
    {
        updatedJson = json;
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return false;
            var changed = UpdateCategoryNameNodes(root, categoryId, oldName, newName);
            if (!changed) return false;
            updatedJson = root.ToJsonString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool UpdateCategoryNameNodes(JsonNode? node, string categoryId, string oldName, string newName)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            var idMatch = false;
            foreach (var key in new[] { "categoryId", "assignedCategoryId", "suggestedCategoryId" })
            {
                if (obj[key] is JsonValue val && val.TryGetValue<string>(out var s) &&
                    string.Equals(s, categoryId, StringComparison.OrdinalIgnoreCase))
                {
                    idMatch = true;
                    break;
                }
            }

            if (idMatch)
            {
                foreach (var nameKey in new[] { "categoryName", "suggestedCategoryName" })
                {
                    if (obj[nameKey] is JsonValue nv && nv.TryGetValue<string>(out var cur) &&
                        !string.Equals(cur, newName, StringComparison.Ordinal))
                    {
                        obj[nameKey] = newName;
                        changed = true;
                    }
                }
            }
            else if (obj["suggestedCategoryName"] is JsonValue snv && snv.TryGetValue<string>(out var sugCur) &&
                     string.Equals(sugCur, oldName, StringComparison.Ordinal))
            {
                obj["suggestedCategoryName"] = newName;
                changed = true;
            }

            foreach (var kvp in obj)
            {
                if (UpdateCategoryNameNodes(kvp.Value, categoryId, oldName, newName))
                    changed = true;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (UpdateCategoryNameNodes(item, categoryId, oldName, newName))
                    changed = true;
            }
        }
        return changed;
    }
}

[ApiController]
[Route("api/projects/{projectId:guid}/incomes")]
public class IncomesController(AppDbContext db, IHubContext<BudgetHub> hub) : ControllerBase
{
    [HttpGet]
    public async Task<List<IncomeDto>> Get(Guid projectId, [FromQuery] int? year, [FromQuery] int? month)
    {
        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var (from, to) = PeriodMath.Range(year, month, cycleDay);
        return await db.Incomes.AsNoTracking().Include(i => i.IncomeCategory)
            .Where(i => i.ProjectId == projectId && i.Date >= from && i.Date <= to)
            .OrderByDescending(i => i.Date)
            .Select(i => new IncomeDto(i.Id, i.Description, i.IncomeCategoryId, i.IncomeCategory.Name, i.ReceiptType, i.Date, i.PlannedAmount, i.ActualAmount))
            .ToListAsync();
    }

    /// <summary>הוספת הכנסה; אם נשלח NewCategoryName נוצרת קטגוריית הכנסה חדשה באותה פעולה.</summary>
    [HttpPost]
    public async Task<ActionResult<IncomeDto>> Create(Guid projectId, CreateIncomeRequest r)
    {
        IncomeCategory? cat;
        if (r.IncomeCategoryId is { } id)
            cat = await db.IncomeCategories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id);
        else if (!string.IsNullOrWhiteSpace(r.NewCategoryName))
        {
            var name = r.NewCategoryName.Trim();
            cat = await db.IncomeCategories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Name == name)
                  ?? db.IncomeCategories.Add(new IncomeCategory { ProjectId = projectId, Name = name, Icon = r.NewCategoryIcon ?? "🪙" }).Entity;
        }
        else return BadRequest("יש לבחור קטגוריית הכנסה או להזין חדשה");

        if (cat is null) return BadRequest("קטגוריית הכנסה לא נמצאה");
        if (r.Amount <= 0) return BadRequest("סכום הכנסה לא תקין");
        if (string.IsNullOrWhiteSpace(r.Description)) return BadRequest("תיאור ההכנסה הינו שדה חובה");

        var desc = r.Description.Trim();

        var income = new ProjectIncome
        {
            ProjectId = projectId, IncomeCategoryId = cat.Id, IncomeCategory = cat,
            Description = desc,
            PlannedAmount = r.PlannedAmount ?? r.Amount,
            ActualAmount = r.ActualAmount ?? r.Amount,
            ReceiptType = r.ReceiptType,
            Date = r.Date
        };
        db.Incomes.Add(income);
        await db.SaveChangesAsync();

        try
        {
            var creatorId = User.GetUserId();
            var creatorName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "בן/בת זוג";
            await hub.Clients.Group(BudgetHub.ProjectGroup(projectId)).SendAsync("TransactionAlert", new
            {
                id = income.Id.ToString(),
                type = "income_alert",
                projectId,
                creatorUserId = creatorId,
                user = creatorName,
                userName = creatorName,
                amount = income.ActualAmount,
                description = desc,
                category = cat.Name,
                title = $"הכנסה חדשה: ₪{income.ActualAmount:N0}",
                message = $"{creatorName} רשם/ה הכנסה ב{cat.Name}: {desc} (₪{income.ActualAmount:N0})"
            });
        }
        catch { }

        return CreatedAtAction(nameof(Get), new { projectId },
            new IncomeDto(income.Id, income.Description, cat.Id, cat.Name, income.ReceiptType, income.Date, income.PlannedAmount, income.ActualAmount));
    }

    [HttpPut("{incomeId:guid}")]
    public async Task<ActionResult<IncomeDto>> Update(Guid projectId, Guid incomeId, UpdateIncomeRequest r)
    {
        if (r.Amount <= 0) return BadRequest("סכום הכנסה לא תקין");
        if (string.IsNullOrWhiteSpace(r.Description)) return BadRequest("תיאור ההכנסה הינו שדה חובה");

        var cat = await db.IncomeCategories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == r.IncomeCategoryId);
        if (cat is null) return BadRequest("קטגוריית הכנסה לא נמצאה");

        var inc = await db.Incomes.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == incomeId);
        if (inc is null) return NotFound();

        inc.Description = string.IsNullOrWhiteSpace(r.Description) ? (cat.Name ?? "הכנסה") : r.Description.Trim();
        inc.PlannedAmount = r.PlannedAmount ?? r.Amount;
        inc.ActualAmount = r.ActualAmount ?? r.Amount;
        inc.Date = r.Date;
        inc.ReceiptType = r.ReceiptType;
        inc.IncomeCategoryId = cat.Id;

        await db.SaveChangesAsync();
        return Ok(new IncomeDto(inc.Id, inc.Description, cat.Id, cat.Name, inc.ReceiptType, inc.Date, inc.PlannedAmount, inc.ActualAmount));
    }

    [HttpDelete("{incomeId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid incomeId)
    {
        var inc = await db.Incomes.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == incomeId);
        if (inc is null) return NotFound();
        db.Incomes.Remove(inc);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

[ApiController]
[Route("api/projects/{projectId:guid}/transactions")]
public class TransactionsController(AppDbContext db, IHubContext<BudgetHub> hub) : ControllerBase
{
    /// <summary>פיד הוצאות עם סינון מרובה: אמצעי תשלום (CSV), קטגוריה, משתמש, טקסט. תומך בדריל-דאון קטגוריה.</summary>
    [HttpGet]
    public async Task<List<TransactionDto>> Get(Guid projectId, [FromQuery] int? year, [FromQuery] int? month,
        [FromQuery] Guid? categoryId, [FromQuery] Guid? userId, [FromQuery] string? methods, [FromQuery] string? q)
    {
        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var (from, to) = PeriodMath.Range(year, month, cycleDay);
        var query = db.Transactions.AsNoTracking().Include(t => t.Category).Include(t => t.ExecutedByUser)
            .Where(t => t.ProjectId == projectId && t.Date >= from && t.Date <= to);

        if (categoryId is { } cid) query = query.Where(t => t.CategoryId == cid);
        if (userId is { } uid) query = query.Where(t => t.ExecutedByUserId == uid);

        var rawTransactions = await query.OrderByDescending(t => t.Date).ToListAsync();
        var result = rawTransactions.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(methods))
        {
            var tokens = methods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            result = result.Where(t =>
            {
                var labelStr = (t.PaymentMethodLabel ?? "").Trim();
                var hasCustomLabel = !string.IsNullOrWhiteSpace(labelStr);

                return tokens.Any(tok =>
                {
                    var cleanTok = tok.Trim();

                    // אם לתנועה יש תווית כרטיס/אמצעי ספציפית:
                    if (hasCustomLabel)
                    {
                        return string.Equals(labelStr, cleanTok, StringComparison.OrdinalIgnoreCase) ||
                               labelStr.Contains(cleanTok, StringComparison.OrdinalIgnoreCase) ||
                               cleanTok.Contains(labelStr, StringComparison.OrdinalIgnoreCase);
                    }

                    // תנועה ללא תווית ספציפית (אמצעי תשלום כללי):
                    if (Enum.TryParse<PaymentMethodKind>(cleanTok, true, out var k) && t.PaymentMethod == k) return true;
                    var lower = cleanTok.ToLower();
                    if ((lower.Contains("אשראי") || lower.Contains("credit")) && t.PaymentMethod == PaymentMethodKind.CreditCard) return true;
                    if ((lower.Contains("תשלומים") || lower.Contains("install")) && t.PaymentMethod == PaymentMethodKind.Installments) return true;
                    if ((lower.Contains("ביט") || lower.Contains("bit")) && t.PaymentMethod == PaymentMethodKind.Bit) return true;
                    if ((lower.Contains("פייבוקס") || lower.Contains("paybox")) && t.PaymentMethod == PaymentMethodKind.Paybox) return true;
                    if ((lower.Contains("בנק") || lower.Contains("bank") || lower.Contains("העברה")) && t.PaymentMethod == PaymentMethodKind.BankTransfer) return true;
                    if ((lower.Contains("מזומן") || lower.Contains("cash")) && t.PaymentMethod == PaymentMethodKind.Cash) return true;
                    if ((lower.Contains("צ'ק") || lower.Contains("צק") || lower.Contains("שיק") || lower.Contains("check")) && t.PaymentMethod == PaymentMethodKind.Check) return true;

                    return false;
                });
            });
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var cleanQ = q.Trim();
            result = result.Where(t =>
                (t.Description != null && t.Description.Contains(cleanQ, StringComparison.OrdinalIgnoreCase)) ||
                (t.Category != null && t.Category.Name != null && t.Category.Name.Contains(cleanQ, StringComparison.OrdinalIgnoreCase)) ||
                (t.ExecutedByUser != null && t.ExecutedByUser.Name != null && t.ExecutedByUser.Name.Contains(cleanQ, StringComparison.OrdinalIgnoreCase)) ||
                (t.PaymentMethodLabel != null && t.PaymentMethodLabel.Contains(cleanQ, StringComparison.OrdinalIgnoreCase))
            );
        }

        return result.Select(t => new TransactionDto(t.Id, t.Description, t.CategoryId, t.Category?.Name ?? "", t.Amount, t.Date, t.PaymentMethod, t.PaymentMethodLabel,
            t.ExecutedByUserId, t.ExecutedByUser?.Name ?? "", t.InstallmentNumber, t.InstallmentsTotal, t.InstallmentGroupId, t.Category?.IsCapitalTransfer ?? false, t.SavingsGoalId)).ToList();
    }

    private static string ExtractInstallmentBaseDesc(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return "";
        return System.Text.RegularExpressions.Regex.Replace(
            desc,
            @"(?:,\s*|\s+\(?)(?:תשלום)\s+\d+\s+מתוך\s+\d+\)?\s*$|\s*\[\d+\/\d+\]\s*$",
            ""
        ).Trim();
    }

    private static (int? Num, int? Total) ParseInstallmentFromDesc(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return (null, null);
        var m = System.Text.RegularExpressions.Regex.Match(
            desc,
            @"(?:,\s*|\s+\(?)(?:תשלום)\s+(\d+)\s+מתוך\s+(\d+)\)?\s*$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && int.TryParse(m.Groups[2].Value, out var tot) && tot > 1)
            return (n, tot);
        return (null, null);
    }

    /// <summary>פרטי קבוצת תשלומים מלאה: סכום כולל, כמות תשלומים, תאריך תחילת העסקה ורשימת כל התשלומים.</summary>
    [HttpGet("{transactionId:guid}/installments")]
    public async Task<ActionResult<InstallmentGroupDetailsDto>> GetInstallmentGroup(Guid projectId, Guid transactionId)
    {
        var t = await db.Transactions.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == transactionId);
        if (t is null) return NotFound();

        List<Transaction> items;
        if (t.InstallmentGroupId.HasValue)
        {
            items = await db.Transactions
                .Where(x => x.ProjectId == projectId && x.InstallmentGroupId == t.InstallmentGroupId.Value)
                .OrderBy(x => x.InstallmentNumber ?? 0)
                .ThenBy(x => x.Date)
                .ToListAsync();
        }
        else
        {
            // גיבוי אוטומטי לתנועות שסונכרנו/יובאו עם ציון תשלומים אך ללא InstallmentGroupId
            var (parsedNum, parsedTot) = ParseInstallmentFromDesc(t.Description);
            var targetTotal = t.InstallmentsTotal ?? parsedTot;
            var targetNum = t.InstallmentNumber ?? parsedNum;
            var baseDesc = ExtractInstallmentBaseDesc(t.Description);

            if (targetTotal is > 1 && !string.IsNullOrWhiteSpace(baseDesc))
            {
                var targetStartMonth = t.Date.AddMonths(-((targetNum ?? 1) - 1));
                var candidates = await db.Transactions
                    .Where(x => x.ProjectId == projectId && x.CategoryId == t.CategoryId)
                    .OrderBy(x => x.Date)
                    .ToListAsync();

                items = candidates
                    .Where(x =>
                    {
                        var (xn, xt) = ParseInstallmentFromDesc(x.Description);
                        var xTot = x.InstallmentsTotal ?? xt;
                        var xNum = x.InstallmentNumber ?? xn ?? 1;
                        if (xTot != targetTotal) return false;
                        if (!string.Equals(ExtractInstallmentBaseDesc(x.Description), baseDesc, StringComparison.OrdinalIgnoreCase)) return false;
                        var xStart = x.Date.AddMonths(-(xNum - 1));
                        return xStart.Year == targetStartMonth.Year && xStart.Month == targetStartMonth.Month;
                    })
                    .OrderBy(x => x.InstallmentNumber ?? ParseInstallmentFromDesc(x.Description).Num ?? 0)
                    .ThenBy(x => x.Date)
                    .ToList();
            }
            else
            {
                items = new List<Transaction> { t };
            }
        }

        if (items.Count == 0) items = new List<Transaction> { t };

        var firstItem = items[0];
        var (fn, ft) = ParseInstallmentFromDesc(firstItem.Description);
        var firstNum = Math.Max(firstItem.InstallmentNumber ?? fn ?? 1, 1);
        var startDate = firstItem.Date.AddMonths(-(firstNum - 1));
        var totalInstallments = Math.Max(
            items.Max(x => x.InstallmentsTotal ?? ParseInstallmentFromDesc(x.Description).Total ?? 0),
            Math.Max(items.Max(x => x.InstallmentNumber ?? ParseInstallmentFromDesc(x.Description).Num ?? 0), items.Count));
        var totalAmount = items.Sum(x => x.Amount);
        var dtoList = items.Select(x =>
        {
            var (xn, _) = ParseInstallmentFromDesc(x.Description);
            return new InstallmentItemDto(x.Id, x.InstallmentNumber ?? xn ?? 1, x.Amount, x.Date);
        }).ToList();

        return Ok(new InstallmentGroupDetailsDto(t.InstallmentGroupId ?? Guid.Empty, totalInstallments, totalAmount, startDate, dtoList));
    }

    /// <summary>רישום הוצאה. עסקת תשלומים נפרסת לחודשים עוקבים, עם אפשרות לעריכה ידנית של כל תשלום.</summary>
    [HttpPost]
    public async Task<ActionResult<List<TransactionDto>>> Create(Guid projectId, CreateTransactionRequest r)
    {
        if (r.Amount <= 0) return BadRequest("סכום לא תקין");
        var category = await db.Categories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == r.CategoryId);
        var user = await db.ProjectUsers.FirstOrDefaultAsync(u => u.ProjectId == projectId && u.Id == r.ExecutedByUserId);
        if (category is null || user is null) return BadRequest("קטגוריה או משתמש לא נמצאו");
        if (string.IsNullOrWhiteSpace(r.Description)) return BadRequest("תיאור ההוצאה הינו שדה חובה");

        var desc = r.Description.Trim();

        var created = new List<Transaction>();
        if (r.Installments is > 1)
        {
            var n = r.Installments.Value;
            var amounts = r.InstallmentAmounts is { Count: var c } && c == n ? r.InstallmentAmounts : InstallmentMath.Split(r.Amount, n);
            if (Math.Abs(amounts.Sum() - r.Amount) > 0.01m) return BadRequest("סכום התשלומים אינו תואם לסכום העסקה");
            var group = Guid.NewGuid();
            for (var i = 0; i < n; i++)
                created.Add(new Transaction
                {
                    ProjectId = projectId, CategoryId = category.Id, ExecutedByUserId = user.Id,
                    Description = desc, Amount = amounts[i], Date = r.Date.AddMonths(i),
                    PaymentMethod = r.PaymentMethod, PaymentMethodLabel = r.PaymentMethodLabel,
                    InstallmentGroupId = group, InstallmentNumber = i + 1, InstallmentsTotal = n,
                    SavingsGoalId = r.SavingsGoalId
                });
        }
        else
            created.Add(new Transaction
            {
                ProjectId = projectId, CategoryId = category.Id, ExecutedByUserId = user.Id,
                Description = desc, Amount = r.Amount, Date = r.Date,
                PaymentMethod = r.PaymentMethod, PaymentMethodLabel = r.PaymentMethodLabel,
                SavingsGoalId = r.SavingsGoalId
            });

        db.Transactions.AddRange(created);
        await db.SaveChangesAsync();

        try
        {
            var creatorId = User.GetUserId();
            var creatorName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? user.Name;
            var installmentsText = r.Installments is > 1 ? $" ({r.Installments.Value} תשלומים)" : "";
            await hub.Clients.Group(BudgetHub.ProjectGroup(projectId)).SendAsync("TransactionAlert", new
            {
                id = created[0].Id.ToString(),
                type = "new_expense",
                projectId,
                creatorUserId = creatorId,
                user = user.Name,
                userName = creatorName,
                amount = r.Amount,
                description = desc,
                category = category.Name,
                title = $"הוצאה חדשה: ₪{r.Amount:N0}",
                message = $"{creatorName} ({user.Name}): {desc} ב{category.Name}{installmentsText}"
            });
        }
        catch { }

        return created.Select(t => new TransactionDto(t.Id, t.Description, category.Id, category.Name, t.Amount, t.Date, t.PaymentMethod,
            t.PaymentMethodLabel, user.Id, user.Name, t.InstallmentNumber, t.InstallmentsTotal, t.InstallmentGroupId, category.IsCapitalTransfer, t.SavingsGoalId)).ToList();
    }

    [HttpPut("{transactionId:guid}")]
    public async Task<ActionResult<TransactionDto>> Update(Guid projectId, Guid transactionId, UpdateTransactionRequest r)
    {
        if (r.Amount <= 0) return BadRequest("סכום לא תקין");
        var category = await db.Categories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == r.CategoryId);
        var user = await db.ProjectUsers.FirstOrDefaultAsync(u => u.ProjectId == projectId && u.Id == r.ExecutedByUserId);
        if (category is null || user is null) return BadRequest("קטגוריה או משתמש לא נמצאו");

        var t = await db.Transactions.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == transactionId);
        if (t is null) return NotFound();

        var desc = string.IsNullOrWhiteSpace(r.Description) ? (category.Name ?? "הוצאה") : r.Description.Trim();

        // בדיקה האם העסקה היא כבר חלק מקבוצת תשלומים קיימת
        if (t.InstallmentGroupId.HasValue)
        {
            var groupItems = await db.Transactions
                .Where(x => x.ProjectId == projectId && x.InstallmentGroupId == t.InstallmentGroupId.Value)
                .OrderBy(x => x.InstallmentNumber ?? 0)
                .ThenBy(x => x.Date)
                .ToListAsync();

            var firstItem = groupItems.Count > 0 ? groupItems[0] : t;
            var isFirst = t.Id == firstItem.Id || (t.InstallmentNumber ?? 1) == 1;

            // אם המשתמש ביטל את עסקת התשלומים או הפך אותה לתשלום בודד
            if (r.PaymentMethod != PaymentMethodKind.Installments || (r.Installments.HasValue && r.Installments.Value <= 1))
            {
                if (isFirst)
                {
                    // מחיקת כל שאר התשלומים בקבוצה
                    var siblings = groupItems.Where(x => x.Id != t.Id).ToList();
                    db.Transactions.RemoveRange(siblings);

                    t.Description = desc;
                    t.Amount = r.Amount;
                    t.Date = r.Date;
                    t.CategoryId = category.Id;
                    t.ExecutedByUserId = user.Id;
                    t.PaymentMethod = r.PaymentMethod;
                    t.PaymentMethodLabel = r.PaymentMethodLabel;
                    t.InstallmentGroupId = null;
                    t.InstallmentNumber = null;
                    t.InstallmentsTotal = null;
                }
                else
                {
                    return BadRequest("ביטול עסקת תשלומים או שינוי סוג העסקה אפשרי רק מתחילת העסקה (תשלום מס' 1)");
                }
            }
            else
            {
                // עדכון עסקת תשלומים קיימת
                if (isFirst)
                {
                    // עריכה מתחילת העסקה (תשלום 1): ניתן לשנות סכום כולל, כמות תשלומים, ותאריך התחלה
                    var n = r.Installments ?? groupItems.Count;
                    if (n <= 1) n = 2;

                    var amounts = r.InstallmentAmounts is { Count: var c } && c == n
                        ? r.InstallmentAmounts
                        : InstallmentMath.Split(r.Amount, n);

                    if (Math.Abs(amounts.Sum() - r.Amount) > 0.01m)
                        return BadRequest("סכום התשלומים אינו תואם לסכום העסקה");

                    var startDate = r.Date;
                    var hasInstSuffix = ParseInstallmentFromDesc(desc).Total.HasValue || groupItems.Any(x => ParseInstallmentFromDesc(x.Description).Total.HasValue);
                    var baseDescForSave = hasInstSuffix ? ExtractInstallmentBaseDesc(desc) : desc;

                    for (var i = 0; i < n; i++)
                    {
                        var itemDesc = hasInstSuffix && !string.IsNullOrWhiteSpace(baseDescForSave)
                            ? $"{baseDescForSave}, תשלום {i + 1} מתוך {n}"
                            : desc;
                        if (i < groupItems.Count)
                        {
                            var item = groupItems[i];
                            item.Description = itemDesc;
                            item.CategoryId = category.Id;
                            item.ExecutedByUserId = user.Id;
                            item.PaymentMethod = r.PaymentMethod;
                            item.PaymentMethodLabel = r.PaymentMethodLabel;
                            item.Amount = amounts[i];
                            item.Date = startDate.AddMonths(i);
                            item.InstallmentNumber = i + 1;
                            item.InstallmentsTotal = n;
                        }
                        else
                        {
                            // הוספת תשלום נוסף אם כמות התשלומים הוגדלה
                            db.Transactions.Add(new Transaction
                            {
                                ProjectId = projectId,
                                CategoryId = category.Id,
                                ExecutedByUserId = user.Id,
                                Description = itemDesc,
                                Amount = amounts[i],
                                Date = startDate.AddMonths(i),
                                PaymentMethod = r.PaymentMethod,
                                PaymentMethodLabel = r.PaymentMethodLabel,
                                InstallmentGroupId = t.InstallmentGroupId.Value,
                                InstallmentNumber = i + 1,
                                InstallmentsTotal = n
                            });
                        }
                    }

                    // אם כמות התשלומים הוקטנה
                    if (groupItems.Count > n)
                    {
                        db.Transactions.RemoveRange(groupItems.Skip(n));
                    }
                }
                else
                {
                    // עריכה מתשלום שאינו הראשון (למשל תשלום 2/3):
                    // הסכום הכולל קבוע לפי הסכום הקיים של כלל הקבוצה
                    // תאריך ההתחלה נשמר לפי תשלום מס' 1
                    var n = groupItems.Count;
                    var totalExpected = Math.Max(groupItems.Max(x => x.InstallmentsTotal ?? 0), Math.Max(groupItems.Max(x => x.InstallmentNumber ?? 0), n));
                    var totalDealAmount = groupItems.Sum(x => x.Amount);

                    List<decimal> amounts;
                    if (r.InstallmentAmounts is { Count: var cFull } && cFull == totalExpected && totalExpected > n)
                    {
                        amounts = groupItems.Select((item, idx) =>
                        {
                            var slotIdx = (item.InstallmentNumber ?? (idx + 1)) - 1;
                            return (slotIdx >= 0 && slotIdx < cFull) ? r.InstallmentAmounts[slotIdx] : item.Amount;
                        }).ToList();
                    }
                    else if (r.InstallmentAmounts is { Count: var c } && c == n)
                    {
                        amounts = r.InstallmentAmounts;
                    }
                    else
                    {
                        amounts = groupItems.Select(x => x.Amount).ToList();
                    }

                    // וידוא שהחלוקה החדשה תואמת בדיוק לסכום העסקה הכולל
                    if (Math.Abs(amounts.Sum() - totalDealAmount) > 0.01m)
                        return BadRequest($"סכום התשלומים ({amounts.Sum():N2} ₪) אינו תואם לסכום העסקה הכולל ({totalDealAmount:N2} ₪)");

                    var hasInstSuffix = ParseInstallmentFromDesc(desc).Total.HasValue || groupItems.Any(x => ParseInstallmentFromDesc(x.Description).Total.HasValue);
                    var baseDescForSave = hasInstSuffix ? ExtractInstallmentBaseDesc(desc) : desc;
                    var firstNum = Math.Max(firstItem.InstallmentNumber ?? 1, 1);
                    var startDate = firstItem.Date.AddMonths(-(firstNum - 1));

                    for (var i = 0; i < n; i++)
                    {
                        var item = groupItems[i];
                        var itemNum = item.InstallmentNumber ?? (i + 1);
                        item.Description = hasInstSuffix && !string.IsNullOrWhiteSpace(baseDescForSave)
                            ? $"{baseDescForSave}, תשלום {itemNum} מתוך {totalExpected}"
                            : desc;
                        item.CategoryId = category.Id;
                        item.ExecutedByUserId = user.Id;
                        item.PaymentMethod = r.PaymentMethod;
                        item.PaymentMethodLabel = r.PaymentMethodLabel;
                        item.Amount = amounts[i];
                        item.Date = startDate.AddMonths(itemNum - 1);
                        item.InstallmentNumber = itemNum;
                        item.InstallmentsTotal = totalExpected;
                    }
                }
            }
        }
        else
        {
            // העסקה במקור אינה עסקת תשלומים
            if (r.Installments is > 1)
            {
                // הפיכת עסקה רגילה לעסקת תשלומים
                var n = r.Installments.Value;
                var amounts = r.InstallmentAmounts is { Count: var c } && c == n ? r.InstallmentAmounts : InstallmentMath.Split(r.Amount, n);
                if (Math.Abs(amounts.Sum() - r.Amount) > 0.01m) return BadRequest("סכום התשלומים אינו תואם לסכום העסקה");

                var group = Guid.NewGuid();
                t.Description = desc;
                t.Amount = amounts[0];
                t.Date = r.Date;
                t.CategoryId = category.Id;
                t.ExecutedByUserId = user.Id;
                t.PaymentMethod = r.PaymentMethod;
                t.PaymentMethodLabel = r.PaymentMethodLabel;
                t.InstallmentGroupId = group;
                t.InstallmentNumber = 1;
                t.InstallmentsTotal = n;

                var newSiblings = new List<Transaction>();
                for (var i = 1; i < n; i++)
                {
                    newSiblings.Add(new Transaction
                    {
                        ProjectId = projectId,
                        CategoryId = category.Id,
                        ExecutedByUserId = user.Id,
                        Description = desc,
                        Amount = amounts[i],
                        Date = r.Date.AddMonths(i),
                        PaymentMethod = r.PaymentMethod,
                        PaymentMethodLabel = r.PaymentMethodLabel,
                        InstallmentGroupId = group,
                        InstallmentNumber = i + 1,
                        InstallmentsTotal = n
                    });
                }
                db.Transactions.AddRange(newSiblings);
            }
            else
            {
                // עדכון עסקה רגילה רגיל לחלוטין
                t.Description = desc;
                t.Amount = r.Amount;
                t.Date = r.Date;
                t.CategoryId = category.Id;
                t.ExecutedByUserId = user.Id;
                t.PaymentMethod = r.PaymentMethod;
                t.PaymentMethodLabel = r.PaymentMethodLabel;
                t.SavingsGoalId = r.SavingsGoalId;
            }
        }

        await db.SaveChangesAsync();
        return Ok(new TransactionDto(t.Id, t.Description, category.Id, category.Name, t.Amount, t.Date, t.PaymentMethod,
            t.PaymentMethodLabel, user.Id, user.Name, t.InstallmentNumber, t.InstallmentsTotal, t.InstallmentGroupId, category.IsCapitalTransfer, t.SavingsGoalId));
    }

    [HttpDelete("{transactionId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid transactionId)
    {
        var t = await db.Transactions.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == transactionId);
        if (t is null) return NotFound();
        db.Transactions.Remove(t);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public static class PeriodMath
{
    public static async Task<int> GetCycleStartDayAsync(AppDbContext db, Guid projectId)
    {
        var day = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.CycleStartDay).FirstOrDefaultAsync();
        return day <= 0 ? 1 : day;
    }

    public static (DateOnly From, DateOnly To) Range(int? year, int? month, int cycleStartDay = 1)
    {
        var today = DateTime.Today;
        var y = year ?? today.Year;
        var m = month ?? today.Month;
        var day = Math.Clamp(cycleStartDay, 1, 28);
        var from = new DateOnly(y, m, day);
        var to = from.AddMonths(1).AddDays(-1);
        return (from, to);
    }
}

public static class InstallmentMath
{
    /// <summary>חלוקה שווה; שארית האגורות נכנסת לתשלום הראשון (למשל 1,000 ל-3 = 334 + 333 + 333).</summary>
    public static List<decimal> Split(decimal total, int n)
    {
        var baseAmt = Math.Floor(total / n * 100) / 100;
        var list = Enumerable.Repeat(baseAmt, n).ToList();
        list[0] += total - baseAmt * n;
        return list;
    }
}

[ApiController]
[Route("api/projects/{projectId:guid}/cycle")]
public class CycleRolloverController(AppDbContext db, ILogger<CycleRolloverController> logger) : ControllerBase
{
    /// <summary>
    /// מעביר ומגלגל את כל קטגוריות ההכנסה וההוצאה הפעילות מחודש נתון לחודש הבא
    /// עם מספרי התכנון של החודש הנוכחי, ויוצר באופן מוגדר את החודש החדש.
    /// </summary>
    [HttpPost("rollover")]
    public async Task<ActionResult<CycleRolloverResultDto>> Rollover(
        Guid projectId,
        [FromQuery] int? fromYear,
        [FromQuery] int? fromMonth,
        [FromQuery] int? toYear,
        [FromQuery] int? toMonth)
    {
        var result = await CycleRolloverService.ExecuteRolloverAsync(db, projectId, fromYear, fromMonth, toYear, toMonth, logger);
        return Ok(result);
    }
}
