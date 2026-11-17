using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/captured-expenses")]
public class CapturedExpensesController(AppDbContext db) : ControllerBase
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
            if (cat is null && item.SuggestedCategoryId is null)
            {
                var (sugId, conf) = await CategorySuggestionService.SuggestCategoryAsync(
                    db, projectId, item.MerchantName, item.RawNotificationText);
                if (sugId.HasValue)
                {
                    cat = await db.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sugId.Value);
                    item.SuggestedCategoryId = sugId;
                    item.ConfidenceScore = conf;
                }
            }

            result.Add(ToDto(item, cat));
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

        // מניעת כפילות לפי ExternalKey או אותו סכום בטווח של 3 דקות
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
        var dup = await db.CapturedExpenses
            .Include(c => c.SuggestedCategory)
            .FirstOrDefaultAsync(c =>
                c.ProjectId == projectId &&
                c.Amount == req.Amount &&
                c.DetectedAt >= windowStart &&
                c.DetectedAt <= windowEnd &&
                c.Status != CapturedExpenseStatus.Dismissed);
        if (dup is not null)
            return Ok(ToDto(dup, dup.SuggestedCategory));

        var (suggestedCategoryId, confidence) = await CategorySuggestionService.SuggestCategoryAsync(
            db, projectId, merchant, req.RawNotificationText ?? "");

        var entity = new CapturedExpense
        {
            ProjectId = projectId,
            Amount = Math.Round(req.Amount, 2),
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

        return Ok(ToDto(entity, cat));
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

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == req.CategoryId && c.ProjectId == projectId);
        if (category is null)
            return BadRequest("קטגוריה לא תקינה");

        var userId = req.ExecutedByUserId;
        ProjectUser? execUser = null;
        if (userId.HasValue)
            execUser = await db.ProjectUsers.FirstOrDefaultAsync(u => u.Id == userId.Value && u.ProjectId == projectId);
        execUser ??= await db.ProjectUsers.FirstOrDefaultAsync(u => u.ProjectId == projectId);
        if (execUser is null)
            return BadRequest("לא נמצא משתמש משויך לפרויקט");

        var finalAmount = req.Amount.HasValue && req.Amount.Value > 0 ? Math.Round(req.Amount.Value, 2) : item.Amount;
        var finalMerchant = !string.IsNullOrWhiteSpace(req.MerchantName) ? req.MerchantName.Trim() : item.MerchantName;
        var finalPaymentMethod = req.PaymentMethod ?? item.PaymentMethod;
        var finalLabel = req.PaymentMethodLabel ?? item.PaymentMethodLabel;
        var txDate = req.Date ?? DateOnly.FromDateTime(item.DetectedAt.ToLocalTime());

        var tx = new Transaction
        {
            ProjectId = projectId,
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
        await db.SaveChangesAsync();

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
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static CapturedExpenseDto ToDto(CapturedExpense item, Category? cat) => new(
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
        item.CreatedTransactionId
    );
}

