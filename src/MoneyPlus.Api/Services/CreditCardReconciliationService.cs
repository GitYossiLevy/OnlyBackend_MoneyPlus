using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Services;

public interface ICreditCardReconciliationService
{
    Task<CreditCardReconciliationSummaryDto> ReconcileAsync(
        Guid projectId,
        string cardLabel,
        int year,
        int month,
        decimal toleranceDelta,
        List<CreditCardStatementItemDto> statementItems,
        int toleranceDays = 3);
}

public class CreditCardReconciliationService(AppDbContext db) : ICreditCardReconciliationService
{
    public async Task<CreditCardReconciliationSummaryDto> ReconcileAsync(
        Guid projectId,
        string cardLabel,
        int year,
        int month,
        decimal toleranceDelta,
        List<CreditCardStatementItemDto> statementItems,
        int toleranceDays = 3)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new InvalidOperationException("פרויקט לא נמצא");

        int cycleDay = project.CycleStartDay > 0 ? project.CycleStartDay : 1;
        var startCycle = new DateOnly(year, month, cycleDay);
        var endCycle = startCycle.AddMonths(1).AddDays(-1);

        // Calculate next cycle range to detect transactions accidentally entered in next month
        var nextCycleStart = startCycle.AddMonths(1);
        var nextCycleEnd = nextCycleStart.AddMonths(1).AddDays(-1);

        // Fetch all categories & users for quick lookup & suggestions
        var categories = await db.Categories.Where(c => c.ProjectId == projectId).ToListAsync();
        var users = await db.ProjectUsers.Where(u => u.ProjectId == projectId).ToListAsync();

        // 1. Fetch system transactions for this card in the target cycle and next cycle
        var cardClean = CleanLabel(cardLabel);
        var cardDigits = ExtractDigits(cardLabel);

        var extendedStart = startCycle.AddDays(-Math.Max(toleranceDays, 3));
        var extendedEnd = endCycle.AddDays(Math.Max(toleranceDays, 3));

        var allWindowTxs = await db.Transactions
            .Include(t => t.Category)
            .Include(t => t.ExecutedByUser)
            .Where(t => t.ProjectId == projectId && t.Date >= extendedStart && t.Date <= extendedEnd)
            .ToListAsync();

        var allCycleTxs = allWindowTxs
            .Where(t => t.Date >= startCycle && t.Date <= endCycle)
            .ToList();

        var nextCycleTxs = await db.Transactions
            .Include(t => t.Category)
            .Include(t => t.ExecutedByUser)
            .Where(t => t.ProjectId == projectId && t.Date >= nextCycleStart && t.Date <= nextCycleEnd)
            .ToListAsync();

        // Filter transactions for this specific card vs ALL other payment methods (Cash, Bit, BankTransfer, other cards)
        var ownerNames = users.Select(u => u.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var cardTxs = allCycleTxs.Where(t => IsMatchToCard(t, cardClean, cardDigits, ownerNames)).ToList();
        var otherCycleTxs = allWindowTxs
            .Concat(nextCycleTxs)
            .Where(t => !IsMatchToCard(t, cardClean, cardDigits, ownerNames))
            .DistinctBy(t => t.Id)
            .ToList();
        var cardNextCycleTxs = nextCycleTxs.Where(t => IsMatchToCard(t, cardClean, cardDigits, ownerNames)).ToList();
        var matchedNextCycleTxIds = new HashSet<Guid>();
        var matchedOtherTxIds = new HashSet<Guid>();

        // 2. Build vendor-history intelligence dictionary from previous transactions in project
        var historyTxs = await db.Transactions
            .Where(t => t.ProjectId == projectId)
            .OrderByDescending(t => t.Date)
            .Take(1500)
            .Select(t => new { t.Description, t.CategoryId, t.ExecutedByUserId, t.PaymentMethodLabel })
            .ToListAsync();

        var vendorDict = new Dictionary<string, (Guid CategoryId, Guid UserId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in historyTxs)
        {
            var cleanDesc = NormalizeName(h.Description);
            if (!string.IsNullOrEmpty(cleanDesc) && !vendorDict.ContainsKey(cleanDesc))
            {
                vendorDict[cleanDesc] = (h.CategoryId, h.ExecutedByUserId);
            }
        }

        // Determine card owner: first from label match against project users, then from dominant transaction executor on this card
        var labelMatchedUser = users.FirstOrDefault(u => cardLabel.Contains(u.Name, StringComparison.OrdinalIgnoreCase));
        var dominantHistoryUserId = cardTxs
            .GroupBy(t => t.ExecutedByUserId)
            .OrderByDescending(g => g.Count())
            .Select(g => (Guid?)g.Key)
            .FirstOrDefault()
            ?? historyTxs
                .Where(h => !string.IsNullOrWhiteSpace(h.PaymentMethodLabel) &&
                            ((!string.IsNullOrEmpty(cardDigits) && h.PaymentMethodLabel.Contains(cardDigits)) ||
                             CleanLabel(h.PaymentMethodLabel).Equals(cardClean, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(h => h.ExecutedByUserId)
                .OrderByDescending(g => g.Count())
                .Select(g => (Guid?)g.Key)
                .FirstOrDefault();

        var resolvedOwnerUser = labelMatchedUser ?? (dominantHistoryUserId.HasValue ? users.FirstOrDefault(u => u.Id == dominantHistoryUserId.Value) : null);
        var defaultUser = resolvedOwnerUser ?? users.FirstOrDefault();
        var defaultCat = categories.FirstOrDefault(c => !c.IsCapitalTransfer) ?? categories.FirstOrDefault();

        // 3. Perform smart multi-pass matching
        var matchedTxIds = new HashSet<Guid>();
        var reconciledRows = new List<CreditCardReconciledRowDto>();

        foreach (var item in statementItems)
        {
            var cleanItemName = NormalizeName(item.CleanBusinessName);
            Transaction? bestMatch = null;
            decimal bestDiff = decimal.MaxValue;
            CardReconciliationStatus status = CardReconciliationStatus.MissingInSystem;

            // Pass 1: Exact amount match within +/- toleranceDays with similar name (or sole candidate)
            var candidatesExact = cardTxs
                .Where(t => !matchedTxIds.Contains(t.Id) && Math.Abs(t.Amount - item.BillingAmount) == 0)
                .OrderBy(t => Math.Abs(t.Date.DayNumber - item.Date.DayNumber))
                .ToList();

            foreach (var cand in candidatesExact)
            {
                int dayDiff = Math.Abs(cand.Date.DayNumber - item.Date.DayNumber);
                if (dayDiff <= toleranceDays)
                {
                    var candName = NormalizeName(cand.Description);
                    if (IsNameSimilar(candName, cleanItemName) || candidatesExact.Count == 1)
                    {
                        bestMatch = cand;
                        bestDiff = 0;
                        status = dayDiff == 0 ? CardReconciliationStatus.Match : CardReconciliationStatus.DateShiftMatch;
                        break;
                    }
                }
            }

            // Pass 2: Delta mismatch within toleranceDelta
            if (bestMatch == null)
            {
                var candidatesDelta = cardTxs
                    .Where(t => !matchedTxIds.Contains(t.Id))
                    .Select(t => new { Tx = t, Diff = Math.Abs(t.Amount - item.BillingAmount), DayDiff = Math.Abs(t.Date.DayNumber - item.Date.DayNumber) })
                    .Where(x => x.Diff > 0 && x.Diff <= toleranceDelta && x.DayDiff <= Math.Max(toleranceDays, 2))
                    .OrderBy(x => x.Diff)
                    .ThenBy(x => x.DayDiff)
                    .ToList();

                foreach (var cand in candidatesDelta)
                {
                    var candName = NormalizeName(cand.Tx.Description);
                    if (IsNameSimilar(candName, cleanItemName) || cand.DayDiff <= 2)
                    {
                        bestMatch = cand.Tx;
                        bestDiff = cand.Diff;
                        status = CardReconciliationStatus.DeltaMismatch;
                        break;
                    }
                }
            }

            // Pass 3: Exceeded delta but clearly same transaction (e.g. tip or major price correction)
            if (bestMatch == null)
            {
                var candExceeded = cardTxs
                    .Where(t => !matchedTxIds.Contains(t.Id))
                    .Select(t => new { Tx = t, Diff = Math.Abs(t.Amount - item.BillingAmount), DayDiff = Math.Abs(t.Date.DayNumber - item.Date.DayNumber) })
                    .Where(x => x.Diff > toleranceDelta && x.Diff <= 30 && x.DayDiff <= Math.Max(toleranceDays, 2))
                    .OrderBy(x => x.Diff)
                    .ToList();

                foreach (var cand in candExceeded)
                {
                    var candName = NormalizeName(cand.Tx.Description);
                    if (IsNameSimilar(candName, cleanItemName))
                    {
                        bestMatch = cand.Tx;
                        bestDiff = cand.Diff;
                        status = CardReconciliationStatus.DeltaExceeded;
                        break;
                    }
                }
            }

            // Pass 3.5: Approximate match (recurring bills like יס with shifted date/slightly different amount, or differently named BIT/merchant)
            if (bestMatch == null)
            {
                var approxNameMatch = cardTxs
                    .Where(t => !matchedTxIds.Contains(t.Id))
                    .Select(t => new { Tx = t, Diff = Math.Abs(t.Amount - item.BillingAmount), DayDiff = Math.Abs(t.Date.DayNumber - item.Date.DayNumber) })
                    .Where(x => IsNameSimilar(NormalizeName(x.Tx.Description), cleanItemName)
                             && x.Diff <= Math.Max(toleranceDelta, Math.Max(50m, item.BillingAmount * 0.25m))
                             && x.DayDiff <= 25)
                    .OrderBy(x => x.Diff)
                    .ThenBy(x => x.DayDiff)
                    .FirstOrDefault();

                if (approxNameMatch != null)
                {
                    bestMatch = approxNameMatch.Tx;
                    bestDiff = approxNameMatch.Diff;
                    status = approxNameMatch.Diff <= toleranceDelta
                        ? CardReconciliationStatus.DateShiftMatch
                        : CardReconciliationStatus.DeltaExceeded;
                }
                else
                {
                    var approxAmtMatch = cardTxs
                        .Where(t => !matchedTxIds.Contains(t.Id))
                        .Select(t => new { Tx = t, Diff = Math.Abs(t.Amount - item.BillingAmount), DayDiff = Math.Abs(t.Date.DayNumber - item.Date.DayNumber) })
                        .Where(x => x.Diff <= Math.Max(toleranceDelta, 15m) && x.DayDiff <= 10)
                        .OrderBy(x => x.Diff)
                        .ThenBy(x => x.DayDiff)
                        .FirstOrDefault();

                    if (approxAmtMatch != null)
                    {
                        bestMatch = approxAmtMatch.Tx;
                        bestDiff = approxAmtMatch.Diff;
                        status = approxAmtMatch.Diff <= toleranceDelta
                            ? CardReconciliationStatus.DateShiftMatch
                            : CardReconciliationStatus.DeltaExceeded;
                    }
                }
            }

            if (bestMatch != null)
            {
                matchedTxIds.Add(bestMatch.Id);
                var matchedDto = new TransactionDto(
                    bestMatch.Id,
                    bestMatch.Description,
                    bestMatch.CategoryId,
                    bestMatch.Category?.Name ?? "",
                    bestMatch.Amount,
                    bestMatch.Date,
                    bestMatch.PaymentMethod,
                    bestMatch.PaymentMethodLabel,
                    bestMatch.ExecutedByUserId,
                    bestMatch.ExecutedByUser?.Name ?? "",
                    bestMatch.InstallmentNumber,
                    bestMatch.InstallmentsTotal,
                    bestMatch.InstallmentGroupId,
                    bestMatch.Category?.IsCapitalTransfer ?? false
                );

                int? matchDayDiff = Math.Abs(bestMatch.Date.DayNumber - item.Date.DayNumber);
                reconciledRows.Add(new CreditCardReconciledRowDto(
                    Id: Guid.NewGuid(),
                    StatementItem: item,
                    Status: status,
                    MatchedTransaction: matchedDto,
                    Delta: bestDiff == decimal.MaxValue ? 0 : bestDiff,
                    SuggestedCategoryId: null,
                    SuggestedCategoryName: null,
                    SuggestedUserId: null,
                    SuggestedUserName: null,
                    DayDifference: matchDayDiff
                ));
            }
            else
            {
                // Pass 4: Cross-Card / Wrong Payment Method Discrepancy Search!
                // Checks ALL project expenses (Cash, Bit, BankTransfer, other Credit Cards) within +/- max(toleranceDays, 3) days
                Transaction? otherCardMatch = null;
                int crossMaxDays = Math.Max(toleranceDays, 3);
                var crossCandidates = otherCycleTxs
                    .Where(t => !matchedOtherTxIds.Contains(t.Id))
                    .Select(t => new { Tx = t, Diff = Math.Abs(t.Amount - item.BillingAmount), DayDiff = Math.Abs(t.Date.DayNumber - item.Date.DayNumber) })
                    .Where(x => x.DayDiff <= crossMaxDays && (x.Diff <= 0.05m || (x.Diff <= toleranceDelta && IsNameSimilar(NormalizeName(x.Tx.Description), cleanItemName))))
                    .OrderBy(x => x.Diff)
                    .ThenBy(x => x.DayDiff)
                    .ToList();

                if (crossCandidates.Count > 0)
                {
                    var cand = crossCandidates.First();
                    otherCardMatch = cand.Tx;
                    matchedOtherTxIds.Add(otherCardMatch.Id);

                    var otherDto = new TransactionDto(
                        otherCardMatch.Id,
                        otherCardMatch.Description,
                        otherCardMatch.CategoryId,
                        otherCardMatch.Category?.Name ?? "",
                        otherCardMatch.Amount,
                        otherCardMatch.Date,
                        otherCardMatch.PaymentMethod,
                        otherCardMatch.PaymentMethodLabel,
                        otherCardMatch.ExecutedByUserId,
                        otherCardMatch.ExecutedByUser?.Name ?? "",
                        otherCardMatch.InstallmentNumber,
                        otherCardMatch.InstallmentsTotal,
                        otherCardMatch.InstallmentGroupId,
                        otherCardMatch.Category?.IsCapitalTransfer ?? false
                    );

                    int? crossDayDiff = Math.Abs(otherCardMatch.Date.DayNumber - item.Date.DayNumber);
                    bool isGenericCard = IsGenericCreditCard(otherCardMatch);

                    // אם התנועה במערכת רשומה כ"כרטיס אשראי" גנרי (ללא 4 ספרות של כרטיס אחר),
                    // התאמת סכום+תאריך היא התאמה אמינה לכרטיס הנוכחי ולא "כרטיס אחר" שדורש אישור
                    // (גם אם שם המבצע הרשום שונה מברירת המחדל של הכרטיס, למשל בייבוא היסטורי).
                    if (isGenericCard)
                    {
                        var matchedGenericStatus = cand.Diff == 0
                            ? (crossDayDiff > 0 ? CardReconciliationStatus.DateShiftMatch : CardReconciliationStatus.Match)
                            : CardReconciliationStatus.DeltaMismatch;

                        reconciledRows.Add(new CreditCardReconciledRowDto(
                            Id: Guid.NewGuid(),
                            StatementItem: item,
                            Status: matchedGenericStatus,
                            MatchedTransaction: otherDto,
                            Delta: cand.Diff,
                            SuggestedCategoryId: null,
                            SuggestedCategoryName: null,
                            SuggestedUserId: null,
                            SuggestedUserName: null,
                            DayDifference: crossDayDiff
                        ));
                    }
                    else
                    {
                        reconciledRows.Add(new CreditCardReconciledRowDto(
                            Id: Guid.NewGuid(),
                            StatementItem: item,
                            Status: CardReconciliationStatus.FoundInOtherCard,
                            MatchedTransaction: otherDto,
                            Delta: cand.Diff,
                            SuggestedCategoryId: otherCardMatch.CategoryId,
                            SuggestedCategoryName: otherCardMatch.Category?.Name,
                            SuggestedUserId: otherCardMatch.ExecutedByUserId,
                            SuggestedUserName: otherCardMatch.ExecutedByUser?.Name,
                            OtherCardName: FormatPaymentMethodHebrew(otherCardMatch),
                            OtherCardTxId: otherCardMatch.Id,
                            DayDifference: crossDayDiff
                        ));
                    }
                }
                else
                {
                    // Pass 5: Check if transaction was entered in the NEXT month by mistake!
                    Transaction? nextMonthMatch = null;
                    var nextCandidates = cardNextCycleTxs
                        .Where(t => !matchedNextCycleTxIds.Contains(t.Id))
                        .Select(t => new { Tx = t, Diff = Math.Abs(t.Amount - item.BillingAmount) })
                        .Where(x => x.Diff <= toleranceDelta)
                        .OrderBy(x => x.Diff)
                        .ToList();

                    foreach (var cand in nextCandidates)
                    {
                        var candName = NormalizeName(cand.Tx.Description);
                        if (IsNameSimilar(candName, cleanItemName) || cand.Diff == 0)
                        {
                            nextMonthMatch = cand.Tx;
                            break;
                        }
                    }

                    if (nextMonthMatch != null)
                    {
                        matchedNextCycleTxIds.Add(nextMonthMatch.Id);
                        var nextMonthDto = new TransactionDto(
                            nextMonthMatch.Id,
                            nextMonthMatch.Description,
                            nextMonthMatch.CategoryId,
                            nextMonthMatch.Category?.Name ?? "",
                            nextMonthMatch.Amount,
                            nextMonthMatch.Date,
                            nextMonthMatch.PaymentMethod,
                            nextMonthMatch.PaymentMethodLabel,
                            nextMonthMatch.ExecutedByUserId,
                            nextMonthMatch.ExecutedByUser?.Name ?? "",
                            nextMonthMatch.InstallmentNumber,
                            nextMonthMatch.InstallmentsTotal,
                            nextMonthMatch.InstallmentGroupId,
                            nextMonthMatch.Category?.IsCapitalTransfer ?? false
                        );

                        int? nextDayDiff = Math.Abs(nextMonthMatch.Date.DayNumber - item.Date.DayNumber);
                        reconciledRows.Add(new CreditCardReconciledRowDto(
                            Id: Guid.NewGuid(),
                            StatementItem: item,
                            Status: CardReconciliationStatus.FoundInNextMonth,
                            MatchedTransaction: nextMonthDto,
                            Delta: Math.Abs(nextMonthMatch.Amount - item.BillingAmount),
                            SuggestedCategoryId: nextMonthMatch.CategoryId,
                            SuggestedCategoryName: nextMonthMatch.Category?.Name,
                            SuggestedUserId: nextMonthMatch.ExecutedByUserId,
                            SuggestedUserName: nextMonthMatch.ExecutedByUser?.Name,
                            DayDifference: nextDayDiff
                        ));
                    }
                    else if (item.Date > endCycle)
                    {
                        // Statement item itself has date belonging to the next cycle
                        reconciledRows.Add(new CreditCardReconciledRowDto(
                            Id: Guid.NewGuid(),
                            StatementItem: item,
                            Status: CardReconciliationStatus.FoundInNextMonth,
                            MatchedTransaction: null,
                            Delta: 0,
                            SuggestedCategoryId: null,
                            SuggestedCategoryName: null,
                            SuggestedUserId: null,
                            SuggestedUserName: null,
                            DayDifference: null
                        ));
                    }
                    else
                    {
                        // Genuine Missing in system for THIS month
                        var (sugCatId, sugUserId) = SuggestCategoryAndUser(cleanItemName, vendorDict, categories, users, defaultCat?.Id, defaultUser?.Id);
                        var sugCat = categories.FirstOrDefault(c => c.Id == sugCatId);
                        var sugUser = users.FirstOrDefault(u => u.Id == sugUserId);

                        reconciledRows.Add(new CreditCardReconciledRowDto(
                            Id: Guid.NewGuid(),
                            StatementItem: item,
                            Status: CardReconciliationStatus.MissingInSystem,
                            MatchedTransaction: null,
                            Delta: 0,
                            SuggestedCategoryId: sugCatId,
                            SuggestedCategoryName: sugCat?.Name,
                            SuggestedUserId: sugUserId,
                            SuggestedUserName: sugUser?.Name,
                            DayDifference: null
                        ));
                    }
                }
            }
        }

        // 4. Identify Orphan transactions (in system for this card, not matched to statement!)
        var orphanTxs = cardTxs
            .Where(t => !matchedTxIds.Contains(t.Id))
            .Select(t => new TransactionDto(
                t.Id,
                t.Description,
                t.CategoryId,
                t.Category?.Name ?? "",
                t.Amount,
                t.Date,
                t.PaymentMethod,
                t.PaymentMethodLabel,
                t.ExecutedByUserId,
                t.ExecutedByUser?.Name ?? "",
                t.InstallmentNumber,
                t.InstallmentsTotal,
                t.InstallmentGroupId,
                t.Category?.IsCapitalTransfer ?? false
            ))
            .OrderByDescending(t => t.Date)
            .ToList();

        // 5. Compute summary aggregates
        decimal statementTotal = statementItems.Sum(i => i.BillingAmount);
        decimal systemTotal = cardTxs.Sum(t => t.Amount);
        decimal netGap = statementTotal - systemTotal;

        int matchedCount = reconciledRows.Count(r => r.Status == CardReconciliationStatus.Match);
        int dateShiftCount = reconciledRows.Count(r => r.Status == CardReconciliationStatus.DateShiftMatch);
        int deltaCount = reconciledRows.Count(r => r.Status is CardReconciliationStatus.DeltaMismatch or CardReconciliationStatus.DeltaExceeded);
        int missingCount = reconciledRows.Count(r => r.Status == CardReconciliationStatus.MissingInSystem);
        int nextMonthCount = reconciledRows.Count(r => r.Status == CardReconciliationStatus.FoundInNextMonth);
        int crossCardCount = reconciledRows.Count(r => r.Status == CardReconciliationStatus.FoundInOtherCard);
        int orphansCount = orphanTxs.Count;

        return new CreditCardReconciliationSummaryDto(
            CardLabel: cardLabel,
            Year: year,
            Month: month,
            StatementTotalAmount: statementTotal,
            SystemTotalAmount: systemTotal,
            NetGap: netGap,
            ToleranceDelta: toleranceDelta,
            MatchedCount: matchedCount,
            DeltaCount: deltaCount,
            MissingCount: missingCount,
            OrphansCount: orphansCount,
            Rows: reconciledRows,
            OrphanTransactions: orphanTxs,
            NextMonthCount: nextMonthCount,
            CrossCardCount: crossCardCount,
            DetectedCardDigits: cardDigits,
            IsCardAutoDetected: false,
            DateShiftCount: dateShiftCount,
            ToleranceDays: toleranceDays,
            DetectedCardOwner: resolvedOwnerUser?.Name
        );
    }

    private static string FormatPaymentMethodHebrew(Transaction t)
    {
        var desc = t.Description ?? "";
        var lbl = t.PaymentMethodLabel?.Trim() ?? "";
        var hasCardDigits = !string.IsNullOrEmpty(ExtractDigits(lbl));

        // חיוב קבוע שמשויך לכרטיס אשראי ספציפי (כולל 4 ספרות) נשמר עם שם הכרטיס ולא מסווג כהוראת קבע בנקאית
        if (hasCardDigits)
            return lbl;

        if (lbl.Contains("הוראת קבע") || lbl.Contains("חיוב קבוע") || lbl.Contains("הו\"ק") || lbl.Contains("הו״ק"))
            return "הוראת קבע";

        if (string.IsNullOrWhiteSpace(lbl) || lbl == "מזומן")
        {
            if (desc.Contains("הוראת קבע") || desc.Contains("חיוב קבוע") || desc.Contains("הו\"ק") || desc.Contains("הו״ק"))
                return "הוראת קבע";
        }

        if (!string.IsNullOrWhiteSpace(lbl))
            return lbl;

        return t.PaymentMethod switch
        {
            PaymentMethodKind.Cash => "מזומן",
            PaymentMethodKind.BankTransfer => "העברה בנקאית",
            PaymentMethodKind.Bit => "ביט",
            PaymentMethodKind.Paybox => "PayBox",
            PaymentMethodKind.Check => "צ'ק",
            PaymentMethodKind.Installments => "תשלומים",
            PaymentMethodKind.Other => "אמצעי תשלום אחר",
            _ => "כרטיס אשראי אחר"
        };
    }

    private static (Guid? CategoryId, Guid? UserId) SuggestCategoryAndUser(
        string cleanName,
        Dictionary<string, (Guid CategoryId, Guid UserId)> vendorDict,
        List<Category> categories,
        List<ProjectUser> users,
        Guid? fallbackCatId,
        Guid? fallbackUserId)
    {
        // 1. Direct dictionary match
        if (vendorDict.TryGetValue(cleanName, out var match))
        {
            return (match.CategoryId, match.UserId);
        }

        // 2. Partial dictionary match
        foreach (var kvp in vendorDict)
        {
            if (cleanName.Contains(kvp.Key) || kvp.Key.Contains(cleanName))
            {
                return (kvp.Value.CategoryId, kvp.Value.UserId);
            }
        }

        // 3. Keyword heuristic matching
        var lower = cleanName.ToLower();
        Category? matchedCat = null;

        if (lower.Contains("דלק") || lower.Contains("סונול") || lower.Contains("פז") || lower.Contains("דור אלון") || lower.Contains("טן"))
            matchedCat = categories.FirstOrDefault(c => c.Name.Contains("דלק") || c.Name.Contains("רכב"));
        else if (lower.Contains("סופר") || lower.Contains("רמי לוי") || lower.Contains("שופרסל") || lower.Contains("יוחננוף") || lower.Contains("ויקטורי") || lower.Contains("טיב טעם"))
            matchedCat = categories.FirstOrDefault(c => c.Name.Contains("סופר") || c.Name.Contains("מזון") || c.Name.Contains("אוכל"));
        else if (lower.Contains("פארם") || lower.Contains("בי קוסמטיקס") || lower.Contains("בית מרקחת"))
            matchedCat = categories.FirstOrDefault(c => c.Name.Contains("פארם") || c.Name.Contains("בריאות"));
        else if (lower.Contains("קפה") || lower.Contains("וולט") || lower.Contains("תן ביס") || lower.Contains("מסעדה") || lower.Contains("מקדונלד"))
            matchedCat = categories.FirstOrDefault(c => c.Name.Contains("מסעדות") || c.Name.Contains("בילוי") || c.Name.Contains("קפה"));
        else if (lower.Contains("פנגו") || lower.Contains("סלו") || lower.Contains("חניה") || lower.Contains("חניון") || lower.Contains("כביש 6"))
            matchedCat = categories.FirstOrDefault(c => c.Name.Contains("רכב") || c.Name.Contains("תחבורה") || c.Name.Contains("חניה"));

        return (matchedCat?.Id ?? fallbackCatId, fallbackUserId);
    }

    private static bool IsGenericCreditCard(Transaction t)
    {
        if (t.PaymentMethod is not (PaymentMethodKind.CreditCard or PaymentMethodKind.Installments))
            return false;

        var raw = t.PaymentMethodLabel?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return true;

        // אם יש בתווית 4 ספרות של כרטיס ספציפי – זה אינו כרטיס גנרי
        if (!string.IsNullOrEmpty(ExtractDigits(raw)))
            return false;

        var clean = CleanLabel(raw).ToLowerInvariant();
        return clean is "כרטיס אשראי" or "כרטיס אשראי 1" or "creditcard" or "credit card"
            or "עסקת תשלומים" or "תשלומים" or "ויזה" or "מאסטרקארד" or "מסטרקארד" or "ישראכרט";
    }

    private static bool IsMatchToCard(Transaction t, string cardClean, string? cardDigits, List<string>? ownerNames = null)
    {
        var label = CleanLabel(t.PaymentMethodLabel);
        var labelDigits = ExtractDigits(label);

        // 1. Strict 4-digit card number priority (most reliable identifier)
        if (!string.IsNullOrEmpty(cardDigits) && !string.IsNullOrEmpty(labelDigits))
        {
            return string.Equals(labelDigits, cardDigits, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrEmpty(cardDigits) && label.Contains(cardDigits))
        {
            return true;
        }

        // 2. Exact normalized card label match
        if (string.Equals(label, cardClean, StringComparison.OrdinalIgnoreCase))
            return true;

        // 3. Card owner / holder priority (only when no conflicting 4-digit numbers exist)
        if (ownerNames != null)
        {
            foreach (var owner in ownerNames)
            {
                if (!string.IsNullOrWhiteSpace(owner) &&
                    label.Contains(owner, StringComparison.OrdinalIgnoreCase) &&
                    cardClean.Contains(owner, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Fallback common owner names
        if (label.Contains("ענבר") && cardClean.Contains("ענבר")) return true;
        if (label.Contains("יוסי") && cardClean.Contains("יוסי")) return true;

        return false;
    }

    private static readonly (string AliasA, string AliasB)[] BrandAliases =
    [
        ("יס", "yes"), ("יס", "dbs"), ("יס", "די בי אס"), ("יס", "דיביאם"),
        ("הוט", "hot"), ("סלקום", "cellcom"), ("פרטנר", "partner"), ("בזק", "bezeq"),
        ("ביט", "bit"), ("פייבוקס", "paybox"), ("פז", "paz"), ("סונול", "sonol"),
        ("שופרסל", "shufersal"), ("וולט", "wolt"), ("אמזון", "amazon"),
        ("גוגל", "google"), ("אפל", "apple"), ("נטפליקס", "netflix"), ("ספוטיפיי", "spotify")
    ];

    private static bool IsNameSimilar(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var (aliasA, aliasB) in BrandAliases)
        {
            bool aHas = a.Contains(aliasA, StringComparison.OrdinalIgnoreCase) || a.Contains(aliasB, StringComparison.OrdinalIgnoreCase);
            bool bHas = b.Contains(aliasA, StringComparison.OrdinalIgnoreCase) || b.Contains(aliasB, StringComparison.OrdinalIgnoreCase);
            if (aHas && bHas) return true;
        }

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return wordsA.Any(w => w.Length >= 2 && wordsB.Any(wb => string.Equals(wb, w, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var clean = Regex.Replace(s, @"[\""\'\`\(\)\-\.]", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean;
    }

    private static string CleanLabel(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return Regex.Replace(s, @"^[\p{Cs}\p{So}\p{Sk}\p{Sm}\s]+", "").Trim();
    }

    private static string? ExtractDigits(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"\b(\d{4})\b");
        return m.Success ? m.Groups[1].Value : null;
    }
}

