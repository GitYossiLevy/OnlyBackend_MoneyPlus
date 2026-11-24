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
[Route("api/projects/{projectId:guid}/captured-expenses")]
public class CapturedExpensesController(
    AppDbContext db,
    IHubContext<BudgetHub> hub) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CapturedExpenseDto>>> GetPending(
        Guid projectId,
        [FromQuery] string? deviceId = null,
        [FromQuery] bool onlyMine = false)
    {
        var query = db.CapturedExpenses
            .AsNoTracking()
            .Include(c => c.SuggestedCategory)
            .Where(c => c.ProjectId == projectId && c.Status == CapturedExpenseStatus.Pending);

        if (onlyMine && !string.IsNullOrWhiteSpace(deviceId))
        {
            query = query.Where(c => c.CapturedByDeviceId == null || c.CapturedByDeviceId == deviceId);
        }

        var items = await query
            .OrderByDescending(c => c.DetectedAt)
            .ToListAsync();

        var result = new List<CapturedExpenseDto>(items.Count);
        foreach (var item in items)
        {
            var cat = item.SuggestedCategory;
            bool isLearned = item.ConfidenceScore >= 0.90;
            if (cat is null && item.SuggestedCategoryId is null)
            {
                var (sugId, conf, learned) = await CategorySuggestionService.SuggestCategoryWithSourceAsync(
                    db, projectId, item.MerchantName, item.RawNotificationText);
                if (sugId.HasValue)
                {
                    cat = await db.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sugId.Value);
                    item.SuggestedCategoryId = sugId;
                    item.ConfidenceScore = conf;
                    isLearned = learned;
                }
            }

            var txDate = DateOnly.FromDateTime(item.DetectedAt.ToLocalTime());
            var (dupId, dupSummary) = await FindDuplicateTransactionAsync(projectId, item.Amount, txDate);
            result.Add(ToDto(item, cat, isLearned, dupId, dupSummary));
        }

        return Ok(result);
    }

    [HttpPost("ingest")]
    public async Task<ActionResult<CapturedExpenseDto>> Ingest(Guid projectId, [FromBody] IngestCapturedExpenseRequest req)
    {
        if (req.Amount <= 0)
            return BadRequest("הסכום חייב להיות חיובי");

        var detectedAt = req.DetectedAt?.ToUniversalTime() ?? DateTime.UtcNow;
        var merchant = string.IsNullOrWhiteSpace(req.MerchantName) ? "תשלום מזוהה" : req.MerchantName.Trim();
        var roundedAmt = Math.Round(req.Amount, 2);

        // מניעת כפילות לפי ExternalKey או אותו סכום בטווח של 3 דקות (למשל Google Pay + SMS על אותה קנייה)
        if (!string.IsNullOrWhiteSpace(req.ExternalKey))
        {
            var existingByKey = await db.CapturedExpenses
                .Include(c => c.SuggestedCategory)
                .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.ExternalKey == req.ExternalKey);
            if (existingByKey is not null)
                return Ok(ToDto(existingByKey, existingByKey.SuggestedCategory));
        }

        var windowStart = detectedAt.AddMinutes(-3);
        var windowEnd = detectedAt.AddMinutes(3);
        var dupCaptured = await db.CapturedExpenses
            .Include(c => c.SuggestedCategory)
            .FirstOrDefaultAsync(c =>
                c.ProjectId == projectId &&
                c.Amount == roundedAmt &&
                c.DetectedAt >= windowStart &&
                c.DetectedAt <= windowEnd &&
                c.Status != CapturedExpenseStatus.Dismissed);
        if (dupCaptured is not null)
            return Ok(ToDto(dupCaptured, dupCaptured.SuggestedCategory));

        var (suggestedCategoryId, confidence, isLearned) = await CategorySuggestionService.SuggestCategoryWithSourceAsync(
            db, projectId, merchant, req.RawNotificationText ?? "");

        var entity = new CapturedExpense
        {
            ProjectId = projectId,
            Amount = roundedAmt,
            MerchantName = merchant,
            RawNotificationText = req.RawNotificationText?.Trim() ?? "",
            SourceApp = string.IsNullOrWhiteSpace(req.SourceApp) ? "GoogleWallet" : req.SourceApp.Trim(),
            PaymentMethod = req.PaymentMethod ?? PaymentMethodKind.CreditCard,
            PaymentMethodLabel = req.PaymentMethodLabel?.Trim(),
            SuggestedCategoryId = suggestedCategoryId,
            ConfidenceScore = confidence,
            ExternalKey = req.ExternalKey?.Trim(),
            DetectedAt = detectedAt,
            Status = CapturedExpenseStatus.Pending,
            CapturedByDeviceId = req.DeviceId?.Trim(),
            CapturedByUserId = req.CapturedByUserId
        };

        db.CapturedExpenses.Add(entity);
        await db.SaveChangesAsync();

        var cat = suggestedCategoryId.HasValue
            ? await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == suggestedCategoryId.Value)
            : null;

        var txDate = DateOnly.FromDateTime(detectedAt.ToLocalTime());
        var (dupTxId, dupSummary) = await FindDuplicateTransactionAsync(projectId, roundedAmt, txDate);

        return Ok(ToDto(entity, cat, isLearned, dupTxId, dupSummary));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<TransactionDto>> Approve(
        Guid projectId,
        Guid id,
        [FromBody] ApproveCapturedExpenseRequest req)
    {
        var item = await db.CapturedExpenses
            .FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == projectId);
        if (item is null)
            return NotFound("ההוצאה הממתינה לא נמצאה");

        if (item.Status == CapturedExpenseStatus.Approved)
        {
            var byWho = string.IsNullOrWhiteSpace(item.HandledByUserName) ? "משתמש אחר" : item.HandledByUserName;
            return Conflict($"ההוצאה כבר אושרה ע\"י {byWho} — לא נוצרה כפילות.");
        }
        if (item.Status == CapturedExpenseStatus.Dismissed)
        {
            return Conflict("ההוצאה כבר נדחתה או טופלה ממכשיר אחר.");
        }

        var effectiveProjectId = req.TargetProjectId ?? projectId;
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == req.CategoryId && c.ProjectId == effectiveProjectId);
        if (category is null)
            return BadRequest("קטגוריה לא תקינה בפרויקט הנבחר");

        var userId = req.ExecutedByUserId;
        ProjectUser? execUser = null;
        if (userId.HasValue)
            execUser = await db.ProjectUsers.FirstOrDefaultAsync(u => u.Id == userId.Value && u.ProjectId == effectiveProjectId);
        if (execUser is null)
        {
            var loggedName = User.FindFirst("name")?.Value ?? User.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(loggedName))
            {
                var first = loggedName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? loggedName;
                execUser = await db.ProjectUsers.FirstOrDefaultAsync(u =>
                    u.ProjectId == effectiveProjectId &&
                    (u.Name == loggedName || u.Name.StartsWith(first)));
            }
        }
        execUser ??= await db.ProjectUsers.FirstOrDefaultAsync(u => u.ProjectId == effectiveProjectId);
        if (execUser is null)
            return BadRequest("לא נמצא משתמש משויך לפרויקט");

        var finalAmount = req.Amount.HasValue && req.Amount.Value > 0 ? Math.Round(req.Amount.Value, 2) : item.Amount;
        var finalMerchant = !string.IsNullOrWhiteSpace(req.MerchantName) ? req.MerchantName.Trim() : item.MerchantName;
        var finalPaymentMethod = req.PaymentMethod ?? item.PaymentMethod;
        var finalLabel = req.PaymentMethodLabel ?? item.PaymentMethodLabel;
        var txDate = req.Date ?? DateOnly.FromDateTime(item.DetectedAt.ToLocalTime());

        // הגנה שנייה בזמן אמת מפני כפילות (למשל אם שותף אחר הזין את ההוצאה ידנית בינתיים)
        if (!req.ForceDuplicate)
        {
            var (dupTxId, dupSummary) = await FindDuplicateTransactionAsync(effectiveProjectId, finalAmount, txDate);
            if (dupTxId.HasValue)
            {
                return Conflict(new
                {
                    code = "DUPLICATE_DETECTED",
                    duplicateTransactionId = dupTxId.Value,
                    message = $"נמצאה הוצאה זהה שכבר הוזנה במערכת ({dupSummary}). לא נוצרה כפילות."
                });
            }
        }

        var tx = new Transaction
        {
            ProjectId = effectiveProjectId,
            Description = finalMerchant,
            CategoryId = category.Id,
            Amount = finalAmount,
            Date = txDate,
            PaymentMethod = finalPaymentMethod,
            PaymentMethodLabel = finalLabel,
            ExecutedByUserId = execUser.Id
        };

        db.Transactions.Add(tx);
        item.Status = CapturedExpenseStatus.Approved;
        item.CreatedTransactionId = tx.Id;
        item.HandledByUserName = execUser.Name;
        item.HandledAt = DateTime.UtcNow;

        if (req.RememberRule)
        {
            await CategorySuggestionService.LearnRuleAsync(db, effectiveProjectId, finalMerchant, category.Id, finalLabel);
        }

        await db.SaveChangesAsync();

        try
        {
            var creatorId = User.GetUserId();
            var creatorName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? execUser.Name;
            await hub.Clients.Group(BudgetHub.ProjectGroup(effectiveProjectId)).SendAsync("TransactionAlert", new
            {
                id = tx.Id.ToString(),
                type = "new_expense",
                projectId = effectiveProjectId,
                creatorUserId = creatorId,
                user = execUser.Name,
                userName = creatorName,
                amount = finalAmount,
                description = finalMerchant,
                category = category.Name,
                title = $"הוצאה חדשה: ₪{finalAmount:N0}",
                message = $"{creatorName} ({execUser.Name}): {finalMerchant} ב{category.Name}"
            });
        }
        catch { }

        return Ok(new TransactionDto(
            tx.Id, tx.Description, tx.CategoryId, category.Name,
            tx.Amount, tx.Date, tx.PaymentMethod, tx.PaymentMethodLabel,
            tx.ExecutedByUserId, execUser.Name, null, null, null,
            category.IsCapitalTransfer, tx.SavingsGoalId));
    }

    [HttpPost("{id:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid projectId, Guid id)
    {
        var item = await db.CapturedExpenses
            .FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == projectId);
        if (item is null)
            return NotFound();

        item.Status = CapturedExpenseStatus.Dismissed;
        item.HandledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("merchant-rules")]
    public async Task<ActionResult<List<MerchantCategoryRuleDto>>> GetMerchantRules(Guid projectId)
    {
        var rules = await db.MerchantCategoryRules
            .AsNoTracking()
            .Include(r => r.Category)
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.HitCount)
            .ThenByDescending(r => r.LastUsedAtUtc)
            .ToListAsync();

        return Ok(rules.Select(r => new MerchantCategoryRuleDto(
            r.Id, r.ProjectId, r.NormalizedMerchant, r.DisplayMerchant,
            r.CategoryId, r.Category?.Name ?? "", r.PaymentMethodLabel,
            r.HitCount, r.CorrectionCount, r.LastUsedAtUtc)).ToList());
    }

    [HttpDelete("merchant-rules/{ruleId:guid}")]
    public async Task<IActionResult> DeleteMerchantRule(Guid projectId, Guid ruleId)
    {
        var rule = await db.MerchantCategoryRules.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == ruleId);
        if (rule is null) return NotFound();
        db.MerchantCategoryRules.Remove(rule);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<(Guid? TransactionId, string? Summary)> FindDuplicateTransactionAsync(
        Guid projectId,
        decimal amount,
        DateOnly txDate)
    {
        var fromDate = txDate.AddDays(-1);
        var toDate = txDate.AddDays(1);
        var minAmt = amount - 1.0m;
        var maxAmt = amount + 1.0m;

        var dup = await db.Transactions
            .AsNoTracking()
            .Include(t => t.Category)
            .Include(t => t.ExecutedByUser)
            .Where(t =>
                t.ProjectId == projectId &&
                t.Date >= fromDate &&
                t.Date <= toDate &&
                t.Amount >= minAmt &&
                t.Amount <= maxAmt)
            .OrderByDescending(t => t.Date)
            .FirstOrDefaultAsync();

        if (dup is null) return (null, null);

        var who = dup.ExecutedByUser?.Name ?? "שותף";
        var catName = dup.Category?.Name ?? "";
        var summary = $"{who} · {dup.Description} ב{catName} (₪{Math.Round(dup.Amount):N0}, {dup.Date:dd/MM})";
        return (dup.Id, summary);
    }

    private static CapturedExpenseDto ToDto(
        CapturedExpense item,
        Category? cat,
        bool isLearnedRule = false,
        Guid? duplicateTxId = null,
        string? duplicateSummary = null) => new(
        item.Id,
        item.Amount,
        item.MerchantName,
        item.RawNotificationText,
        item.SourceApp,
        item.PaymentMethod,
        item.PaymentMethodLabel,
        item.SuggestedCategoryId,
        cat?.Name,
        cat?.Icon,
        item.ConfidenceScore,
        item.DetectedAt,
        item.Status,
        item.CreatedTransactionId,
        isLearnedRule,
        duplicateTxId,
        duplicateSummary
    );
}
