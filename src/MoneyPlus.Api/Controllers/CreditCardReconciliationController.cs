using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/credit-cards")]
[Authorize]
public class CreditCardReconciliationController(
    AppDbContext db,
    ICreditCardParserService parser,
    ICreditCardReconciliationService reconciler,
    ILogger<CreditCardReconciliationController> logger) : ControllerBase
{
    /// <summary>
    /// טעינת קובץ פירוט כרטיס אשראי (Excel, CSV, PDF), פענוחו והצלבתו מול הוצאות המערכת.
    /// הפעולה מחזירה תמונת מצב מלאה (התאמות, סטיות Delta, הוצאות חסרות, הוצאות יתומות) ללא ביצוע שינוי במסד הנתונים.
    /// </summary>
    [HttpPost("reconcile-file")]
    [RequestSizeLimit(15 * 1024 * 1024)] // 15MB
    public async Task<ActionResult<CreditCardReconciliationSummaryDto>> ReconcileFile(
        Guid projectId,
        [FromForm] IFormFile file,
        [FromForm] string? cardLabel,
        [FromForm] int year,
        [FromForm] int month,
        [FromForm] decimal? toleranceDelta = 3.0m,
        [FromForm] string? filterDigits = null,
        [FromForm] int? toleranceDays = 3)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("קובץ הפירוט לא סופק או שהוא ריק.");
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound("פרויקט לא נמצא");

        var projectUsers = await db.ProjectUsers.Where(u => u.ProjectId == projectId).ToListAsync();

        // Filter and collect existing credit card payment methods in the project
        var allMethods = project.GetPaymentMethods();
        var projectCards = allMethods
            .Where(m => m.Contains("💳") || m.Contains("כרטיס") || m.Contains("אשראי") || m.Contains("ויזה") || m.Contains("כאל") || m.Contains("מקס") || m.Contains("ישראכרט") || System.Text.RegularExpressions.Regex.IsMatch(m, @"\b\d{4}\b"))
            .ToList();
        if (projectCards.Count == 0) projectCards = [.. allMethods];

        try
        {
            using var stream = file.OpenReadStream();
            var parsed = parser.ParseWithMetadata(stream, file.FileName, year, month);
            var statementItems = parsed.Items;
            var detectedDigits = parsed.DetectedCard4Digits;
            var detectedFormat = parsed.DetectedFormat;
            var detectedOwner = parsed.DetectedCardOwner
                ?? projectUsers.FirstOrDefault(u => file.FileName.Contains(u.Name, StringComparison.OrdinalIgnoreCase))?.Name;

            if (!string.IsNullOrWhiteSpace(filterDigits))
            {
                var trimmedDigits = filterDigits.Trim();
                statementItems = statementItems.Where(i => i.CardLast4Digits == trimmedDigits).ToList();
                detectedDigits = trimmedDigits;
            }

            if (statementItems.Count == 0)
            {
                var cardHintParts = new List<string>();
                if (!string.IsNullOrEmpty(parsed.DetectedIssuer)) cardHintParts.Add(parsed.DetectedIssuer);
                if (!string.IsNullOrEmpty(detectedDigits)) cardHintParts.Add($"מס׳ {detectedDigits}");
                if (!string.IsNullOrEmpty(detectedOwner)) cardHintParts.Add($"בעלים: {detectedOwner}");
                var cardHint = cardHintParts.Count > 0 ? $" ({string.Join(" · ", cardHintParts)})" : "";
                return BadRequest($"לא זוהו תנועות חיוב בקובץ '{file.FileName}'{cardHint}. ייתכן שמדובר בקובץ PDF סרוק כתמונה ללא טקסט, בקובץ סיכום בלבד, או בחשבונית ריקה מתנועות.");
            }

            string? resolvedCard = cardLabel;
            bool isAutoDetected = false;

            // Auto-identification: prioritize 4-digit card number + card owner over card name
            if (string.IsNullOrWhiteSpace(resolvedCard) || resolvedCard.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                resolvedCard = null;
                if (!string.IsNullOrEmpty(detectedDigits))
                {
                    // 1. Both 4 digits AND owner match
                    var matchedByDigitsAndOwner = !string.IsNullOrEmpty(detectedOwner)
                        ? projectCards.FirstOrDefault(c => c.Contains(detectedDigits) && c.Contains(detectedOwner, StringComparison.OrdinalIgnoreCase))
                        : null;
                    // 2. 4 digits match
                    var matchedByDigits = matchedByDigitsAndOwner ?? projectCards.FirstOrDefault(c => c.Contains(detectedDigits));
                    if (matchedByDigits != null)
                    {
                        resolvedCard = matchedByDigits;
                        isAutoDetected = true;
                    }
                }

                // 3. Unique owner match when digits are absent or didn't match
                if (string.IsNullOrEmpty(resolvedCard) && !string.IsNullOrEmpty(detectedOwner))
                {
                    var ownerMatches = projectCards.Where(c => c.Contains(detectedOwner, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (ownerMatches.Count == 1)
                    {
                        resolvedCard = ownerMatches[0];
                        isAutoDetected = true;
                    }
                }

                if (string.IsNullOrEmpty(resolvedCard) && projectCards.Count == 1)
                {
                    resolvedCard = projectCards[0];
                    isAutoDetected = true;
                }

                if (string.IsNullOrEmpty(resolvedCard))
                {
                    // Cannot auto-determine card with 100% certainty: prompt user with available cards
                    return Ok(new CreditCardReconciliationSummaryDto(
                        CardLabel: "",
                        Year: year,
                        Month: month,
                        StatementTotalAmount: statementItems.Sum(i => i.BillingAmount),
                        SystemTotalAmount: 0,
                        NetGap: 0,
                        ToleranceDelta: toleranceDelta ?? 3.0m,
                        MatchedCount: 0,
                        DeltaCount: 0,
                        MissingCount: 0,
                        OrphansCount: 0,
                        Rows: [],
                        OrphanTransactions: [],
                        NextMonthCount: 0,
                        CrossCardCount: 0,
                        DetectedCardDigits: detectedDigits,
                        DetectedFormat: detectedFormat,
                        IsCardAutoDetected: false,
                        RequiresCardSelection: true,
                        AvailableCards: projectCards,
                        StatementBillingDate: parsed.StatementBillingDate,
                        StatementBillingYear: parsed.StatementBillingYear,
                        StatementBillingMonth: parsed.StatementBillingMonth,
                        DetectedIssuer: parsed.DetectedIssuer,
                        DetectedMultiCardDigits: parsed.DetectedMultiCardDigits,
                        SourceFileName: file.FileName,
                        DetectedCardOwner: detectedOwner
                    ));
                }
            }

            var summary = await reconciler.ReconcileAsync(
                projectId,
                resolvedCard,
                year,
                month,
                toleranceDelta ?? 3.0m,
                statementItems,
                toleranceDays ?? 3);

            var finalDigits = detectedDigits;
            if (string.IsNullOrEmpty(finalDigits))
            {
                var mDigits = System.Text.RegularExpressions.Regex.Match(resolvedCard, @"\b(\d{4})\b");
                if (mDigits.Success) finalDigits = mDigits.Groups[1].Value;
            }
            var finalOwner = projectUsers.FirstOrDefault(u => resolvedCard.Contains(u.Name, StringComparison.OrdinalIgnoreCase))?.Name
                ?? summary.DetectedCardOwner
                ?? detectedOwner;

            var enrichedSummary = summary with
            {
                DetectedCardDigits = finalDigits,
                DetectedFormat = detectedFormat,
                IsCardAutoDetected = isAutoDetected,
                RequiresCardSelection = false,
                AvailableCards = projectCards,
                StatementBillingDate = parsed.StatementBillingDate,
                StatementBillingYear = parsed.StatementBillingYear,
                StatementBillingMonth = parsed.StatementBillingMonth,
                DetectedIssuer = parsed.DetectedIssuer,
                DetectedMultiCardDigits = parsed.DetectedMultiCardDigits,
                SourceFileName = file.FileName,
                DetectedCardOwner = finalOwner
            };

            logger.LogInformation(
                "Credit Card Reconciliation processed: Card {Card} (AutoDetected: {Auto}), {ItemsCount} statement items, {Matched} matched, {Missing} missing, {CrossCard} cross-card, {Orphans} orphans",
                resolvedCard, isAutoDetected, statementItems.Count, summary.MatchedCount, summary.MissingCount, summary.CrossCardCount, summary.OrphansCount);

            return Ok(enrichedSummary);
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing credit card reconciliation for project {ProjectId}", projectId);
            return StatusCode(500, $"שגיאה בעיבוד קובץ הפירוט: {ex.Message}");
        }
    }

    /// <summary>
    /// החלת כלל ההתאמות המאושרות: יצירת הוצאות חסרות, תיקון סטיות סכום לדף הפירוט, וטיפול בהוצאות יתומות.
    /// מבוצע כפעולה אטומית אחת (All-or-Nothing).
    /// </summary>
    [HttpPost("apply")]
    public async Task<ActionResult<ApplyCreditCardReconciliationResultDto>> ApplyReconciliation(
        Guid projectId,
        [FromBody] ApplyCreditCardReconciliationRequest req)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound("פרויקט לא נמצא");

        int created = 0;
        int updated = 0;
        int handledOrphans = 0;
        int crossCardHandled = 0;

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // 1. Create missing transactions (with target date if shifted to next month or custom)
            if (req.MissingToCreate != null && req.MissingToCreate.Count > 0)
            {
                foreach (var item in req.MissingToCreate)
                {
                    DateOnly txDate = item.TargetDate ?? item.Date;
                    Guid? instGroupId = null;
                    if (item.InstallmentsTotal is > 1)
                    {
                        var baseDesc = System.Text.RegularExpressions.Regex.Replace(
                            item.Description ?? "",
                            @"(?:,\s*|\s+\(?)(?:תשלום)\s+\d+\s+מתוך\s+\d+\)?\s*$|\s*\[\d+\/\d+\]\s*$",
                            "").Trim();
                        var targetStart = txDate.AddMonths(-((item.InstallmentNumber ?? 1) - 1));
                        var candidate = (await db.Transactions
                            .Where(t => t.ProjectId == projectId && t.CategoryId == item.CategoryId && t.InstallmentsTotal == item.InstallmentsTotal)
                            .ToListAsync())
                            .FirstOrDefault(t =>
                            {
                                var tBase = System.Text.RegularExpressions.Regex.Replace(
                                    t.Description ?? "",
                                    @"(?:,\s*|\s+\(?)(?:תשלום)\s+\d+\s+מתוך\s+\d+\)?\s*$|\s*\[\d+\/\d+\]\s*$",
                                    "").Trim();
                                if (!string.Equals(tBase, baseDesc, StringComparison.OrdinalIgnoreCase)) return false;
                                var tStart = t.Date.AddMonths(-((t.InstallmentNumber ?? 1) - 1));
                                return tStart.Year == targetStart.Year && tStart.Month == targetStart.Month;
                            });
                        instGroupId = candidate?.InstallmentGroupId ?? Guid.NewGuid();
                    }

                    var newTx = new Transaction
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = projectId,
                        CategoryId = item.CategoryId,
                        ExecutedByUserId = item.ExecutedByUserId,
                        Description = item.Description.Trim(),
                        Amount = Math.Abs(item.Amount),
                        Date = txDate,
                        PaymentMethod = item.InstallmentsTotal is > 1 ? PaymentMethodKind.Installments : PaymentMethodKind.CreditCard,
                        PaymentMethodLabel = string.IsNullOrWhiteSpace(item.PaymentMethodLabel) ? req.CardLabel : item.PaymentMethodLabel.Trim(),
                        InstallmentGroupId = instGroupId,
                        InstallmentNumber = item.InstallmentNumber,
                        InstallmentsTotal = item.InstallmentsTotal
                    };
                    db.Transactions.Add(newTx);
                    created++;
                }
            }

            // 2. Update delta discrepancies (and optional category/card overrides)
            if (req.DeltasToUpdate != null && req.DeltasToUpdate.Count > 0)
            {
                foreach (var delta in req.DeltasToUpdate)
                {
                    var existing = await db.Transactions.FirstOrDefaultAsync(t => t.Id == delta.TransactionId && t.ProjectId == projectId);
                    if (existing != null)
                    {
                        existing.Amount = Math.Abs(delta.NewAmount);
                        if (delta.CategoryId.HasValue && delta.CategoryId.Value != Guid.Empty)
                            existing.CategoryId = delta.CategoryId.Value;
                        if (!string.IsNullOrWhiteSpace(delta.NewPaymentMethodLabel))
                        {
                            existing.PaymentMethod = PaymentMethodKind.CreditCard;
                            existing.PaymentMethodLabel = delta.NewPaymentMethodLabel.Trim();
                        }
                        updated++;
                    }
                }
            }

            // 3. Reassign cross-card or manually linked transactions (card, optional category, amount, date)
            if (req.CrossCardToReassign != null && req.CrossCardToReassign.Count > 0)
            {
                foreach (var cross in req.CrossCardToReassign)
                {
                    var existing = await db.Transactions.FirstOrDefaultAsync(t => t.Id == cross.TransactionId && t.ProjectId == projectId);
                    if (existing != null)
                    {
                        existing.PaymentMethod = PaymentMethodKind.CreditCard;
                        existing.PaymentMethodLabel = string.IsNullOrWhiteSpace(cross.NewPaymentMethodLabel) ? req.CardLabel : cross.NewPaymentMethodLabel.Trim();
                        if (cross.CategoryId.HasValue && cross.CategoryId.Value != Guid.Empty)
                            existing.CategoryId = cross.CategoryId.Value;
                        if (cross.NewAmount.HasValue && cross.NewAmount.Value > 0)
                            existing.Amount = Math.Abs(cross.NewAmount.Value);
                        if (cross.NewDate.HasValue)
                            existing.Date = cross.NewDate.Value;
                        crossCardHandled++;
                        updated++;
                    }
                }
            }

            // 4. Handle orphan transactions
            if (req.OrphansToHandle != null && req.OrphansToHandle.Count > 0)
            {
                foreach (var orphan in req.OrphansToHandle)
                {
                    var existing = await db.Transactions.FirstOrDefaultAsync(t => t.Id == orphan.TransactionId && t.ProjectId == projectId);
                    if (existing == null) continue;

                    switch (orphan.Action?.ToLowerInvariant())
                    {
                        case "delete":
                            db.Transactions.Remove(existing);
                            handledOrphans++;
                            break;

                        case "reassignpaymentmethod":
                            if (!string.IsNullOrWhiteSpace(orphan.NewPaymentMethodLabel))
                            {
                                existing.PaymentMethodLabel = orphan.NewPaymentMethodLabel.Trim();
                                handledOrphans++;
                            }
                            break;

                        case "shiftmonth":
                            if (orphan.NewDate.HasValue)
                            {
                                existing.Date = orphan.NewDate.Value;
                                handledOrphans++;
                            }
                            break;
                    }
                }
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            logger.LogInformation(
                "Credit Card Reconciliation applied successfully for project {ProjectId}: {Created} created, {Updated} updated, {CrossCard} cross-card corrected, {Orphans} orphans handled",
                projectId, created, updated, crossCardHandled, handledOrphans);

            var messageParts = new List<string>();
            if (created > 0) messageParts.Add($"נוצרו {created} הוצאות חסרות");
            if (crossCardHandled > 0) messageParts.Add($"תוקן שיוך כרטיס עבור {crossCardHandled} עסקאות");
            if (updated - crossCardHandled > 0) messageParts.Add($"עודכנו {updated - crossCardHandled} סטיות סכום");
            if (handledOrphans > 0) messageParts.Add($"טופלו {handledOrphans} הוצאות יתומות");

            var message = messageParts.Count > 0
                ? $"הסנכרון בוצע בהצלחה! {string.Join(", ", messageParts)}."
                : "הסנכרון בוצע בהצלחה ללא שינויים נדרשים.";

            return Ok(new ApplyCreditCardReconciliationResultDto(
                CreatedCount: created,
                UpdatedCount: updated,
                HandledOrphansCount: handledOrphans,
                Message: message
            ));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            logger.LogError(ex, "Failed to apply credit card reconciliation for project {ProjectId}", projectId);
            return StatusCode(500, $"שגיאה בהחלת הסנכרון: {ex.Message}");
        }
    }

    /// <summary>
    /// שמירת סשן הצלבת כרטיסי אשראי כולל כל שיוכי הקטגוריות, המשתמשים וסטטוסי האישור.
    /// </summary>
    /// <summary>
    /// שמירת סשן הצלבת כרטיסי אשראי במסד הנתונים כולל שיוכים והגדרות ידניות.
    /// </summary>
    [HttpPost("sessions")]
    public async Task<ActionResult<CreditCardSessionSummaryDto>> SaveSession(
        Guid projectId,
        [FromBody] SaveCreditCardSessionRequest req)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound("פרויקט לא נמצא");

        Guid targetId = req.Id ?? req.SessionId ?? Guid.NewGuid();
        var session = await db.CreditCardReconciliationSessions
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == targetId);

        string sessionName = !string.IsNullOrWhiteSpace(req.SessionName) 
            ? req.SessionName.Trim() 
            : (!string.IsNullOrWhiteSpace(req.CardLabel) ? req.CardLabel.Trim() : $"התאמת ויזות {req.Month}/{req.Year}");

        // Build composite SessionDataJson if individual JSON fields are passed
        string combinedJson = req.SessionDataJson ?? "{}";
        if (!string.IsNullOrWhiteSpace(req.StatementsJson) || !string.IsNullOrWhiteSpace(req.OverridesJson) || !string.IsNullOrWhiteSpace(req.SummaryJson))
        {
            var dataObj = new Dictionary<string, string>
            {
                ["statementsJson"] = req.StatementsJson ?? "[]",
                ["matchesJson"] = req.MatchesJson ?? "[]",
                ["overridesJson"] = req.OverridesJson ?? "[]",
                ["summaryJson"] = req.SummaryJson ?? "{}"
            };
            combinedJson = JsonSerializer.Serialize(dataObj);
        }

        var (totalStatements, totalTransactions, matchRate, stJson, mtJson, ovJson, smJson) = ParseSessionPayload(combinedJson);

        if (session == null)
        {
            session = new CreditCardReconciliationSession
            {
                Id = targetId,
                ProjectId = projectId,
                CardLabel = sessionName,
                Year = req.Year,
                Month = req.Month,
                SessionDataJson = combinedJson
            };
            db.CreditCardReconciliationSessions.Add(session);
        }

        session.CardLabel = sessionName;
        session.Year = req.Year;
        session.Month = req.Month;
        session.SourceFileName = req.SourceFileName;
        session.StatementTotalAmount = req.StatementTotalAmount;
        session.SystemTotalAmount = req.SystemTotalAmount;
        session.NetGap = req.NetGap;
        session.MatchedCount = req.MatchedCount > 0 ? req.MatchedCount : (totalTransactions * matchRate / 100);
        session.DeltaCount = req.DeltaCount;
        session.MissingCount = req.MissingCount;
        session.OrphansCount = req.OrphansCount;
        session.CrossCardCount = req.CrossCardCount;
        session.Status = string.IsNullOrWhiteSpace(req.Status) ? "Draft" : req.Status;
        session.SessionDataJson = combinedJson;
        session.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return Ok(new CreditCardSessionSummaryDto(
            session.Id,
            session.ProjectId,
            session.CardLabel,
            session.CardLabel,
            session.Year,
            session.Month,
            totalStatements,
            totalTransactions,
            matchRate,
            session.SourceFileName,
            session.StatementTotalAmount,
            session.SystemTotalAmount,
            session.NetGap,
            session.MatchedCount,
            session.DeltaCount,
            session.MissingCount,
            session.OrphansCount,
            session.CrossCardCount,
            session.Status,
            session.CreatedAt,
            session.UpdatedAt,
            stJson,
            mtJson,
            ovJson,
            smJson,
            session.SessionDataJson
        ));
    }

    /// <summary>
    /// שליפת רשימת סשנים שמורים של הפרויקט עם אפשרות סינון לפי שנה וחודש.
    /// </summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<List<CreditCardSessionSummaryDto>>> GetSessions(
        Guid projectId,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null)
    {
        var q = db.CreditCardReconciliationSessions
            .Where(s => s.ProjectId == projectId);

        if (year.HasValue) q = q.Where(s => s.Year == year.Value);
        if (month.HasValue) q = q.Where(s => s.Month == month.Value);

        var dbSessions = await q
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync();

        var list = dbSessions.Select(s =>
        {
            var parsed = ParseSessionPayload(s.SessionDataJson);
            return new CreditCardSessionSummaryDto(
                s.Id,
                s.ProjectId,
                s.CardLabel,
                s.CardLabel,
                s.Year,
                s.Month,
                parsed.totalStatements > 0 ? parsed.totalStatements : 1,
                parsed.totalTransactions,
                parsed.matchRate,
                s.SourceFileName,
                s.StatementTotalAmount,
                s.SystemTotalAmount,
                s.NetGap,
                s.MatchedCount,
                s.DeltaCount,
                s.MissingCount,
                s.OrphansCount,
                s.CrossCardCount,
                s.Status,
                s.CreatedAt,
                s.UpdatedAt,
                null, null, null, null, null // omit large payload in list view
            );
        }).ToList();

        return Ok(list);
    }

    /// <summary>
    /// שליפת סשן שמור בודד כולל מלוא ה-JSON והשיוכים.
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<CreditCardSessionSummaryDto>> GetSessionById(
        Guid projectId,
        Guid sessionId)
    {
        var s = await db.CreditCardReconciliationSessions
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == sessionId);

        if (s == null) return NotFound("סשן לא נמצא");

        var parsed = ParseSessionPayload(s.SessionDataJson);

        return Ok(new CreditCardSessionSummaryDto(
            s.Id,
            s.ProjectId,
            s.CardLabel,
            s.CardLabel,
            s.Year,
            s.Month,
            parsed.totalStatements,
            parsed.totalTransactions,
            parsed.matchRate,
            s.SourceFileName,
            s.StatementTotalAmount,
            s.SystemTotalAmount,
            s.NetGap,
            s.MatchedCount,
            s.DeltaCount,
            s.MissingCount,
            s.OrphansCount,
            s.CrossCardCount,
            s.Status,
            s.CreatedAt,
            s.UpdatedAt,
            parsed.statementsJson,
            parsed.matchesJson,
            parsed.overridesJson,
            parsed.summaryJson,
            s.SessionDataJson
        ));
    }

    /// <summary>
    /// מחיקת סשן שמור.
    /// </summary>
    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(Guid projectId, Guid sessionId)
    {
        var s = await db.CreditCardReconciliationSessions
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == sessionId);

        if (s == null) return NotFound("סשן לא נמצא");

        db.CreditCardReconciliationSessions.Remove(s);
        await db.SaveChangesAsync();

        return Ok(new { success = true });
    }

    private static (int totalStatements, int totalTransactions, int matchRate, string? statementsJson, string? matchesJson, string? overridesJson, string? summaryJson) ParseSessionPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (0, 0, 0, null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? stJson = root.TryGetProperty("statementsJson", out var stEl) ? stEl.GetString() : null;
            string? mtJson = root.TryGetProperty("matchesJson", out var mtEl) ? mtEl.GetString() : null;
            string? ovJson = root.TryGetProperty("overridesJson", out var ovEl) ? ovEl.GetString() : null;
            string? smJson = root.TryGetProperty("summaryJson", out var smEl) ? smEl.GetString() : null;

            int totalStatements = 0;
            int totalTransactions = 0;
            int matchRate = 0;

            if (!string.IsNullOrWhiteSpace(smJson))
            {
                try
                {
                    using var sDoc = JsonDocument.Parse(smJson);
                    var sRoot = sDoc.RootElement;
                    if (sRoot.TryGetProperty("totalStatements", out var ts)) totalStatements = ts.GetInt32();
                    if (sRoot.TryGetProperty("totalTransactions", out var tt)) totalTransactions = tt.GetInt32();
                    if (sRoot.TryGetProperty("matchRate", out var mr)) matchRate = mr.GetInt32();
                }
                catch { }
            }

            if (totalStatements == 0 && !string.IsNullOrWhiteSpace(stJson))
            {
                try
                {
                    using var stArrDoc = JsonDocument.Parse(stJson);
                    if (stArrDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        totalStatements = stArrDoc.RootElement.GetArrayLength();
                    }
                }
                catch { }
            }

            return (totalStatements, totalTransactions, matchRate, stJson, mtJson, ovJson, smJson);
        }
        catch
        {
            return (0, 0, 0, null, null, null, null);
        }
    }
}

