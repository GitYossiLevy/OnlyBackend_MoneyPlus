using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Controllers;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Services;

public interface IFinancialInsightsService
{
    Task<InsightsSummaryDto> GenerateInsightsAsync(Guid projectId, int year, int month);
    Task<AnalyticsDashboardDto> GetAnalyticsDashboardAsync(Guid projectId, int year, int? month);
}

public class FinancialInsightsService(AppDbContext db) : IFinancialInsightsService
{
    public async Task<AnalyticsDashboardDto> GetAnalyticsDashboardAsync(Guid projectId, int year, int? month)
    {
        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        DateOnly from, to;
        string periodLabel;

        if (month.HasValue)
        {
            (from, to) = PeriodMath.Range(year, month.Value, cycleDay);
            periodLabel = $"חודש {month.Value}/{year}";
        }
        else
        {
            var (yStart, _) = PeriodMath.Range(year, 1, cycleDay);
            var (_, yEnd) = PeriodMath.Range(year, 12, cycleDay);
            from = yStart;
            to = yEnd;
            periodLabel = $"שנת {year}";
        }

        var incomes = await db.Incomes.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.Date >= from && i.Date <= to)
            .Select(i => new { i.ActualAmount })
            .ToListAsync();
        var totalIncome = incomes.Sum(i => i.ActualAmount);

        var transactions = await db.Transactions.AsNoTracking()
            .Include(t => t.Category)
            .Include(t => t.ExecutedByUser)
            .Where(t => t.ProjectId == projectId && t.Date >= from && t.Date <= to)
            .ToListAsync();
        var totalExpenses = transactions.Where(t => t.Category == null || !t.Category.IsCapitalTransfer).Sum(t => t.Amount);
        var totalCapitalTransfers = transactions.Where(t => t.Category != null && t.Category.IsCapitalTransfer).Sum(t => t.Amount);
        var totalOutflows = totalExpenses + totalCapitalTransfers;
        var netCashFlow = totalIncome - totalOutflows;

        // 1. Clusters classification
        var clustersMap = new Dictionary<string, (string Name, string Icon, string Color, List<ClusterItemDto> Items)>
        {
            ["fixed_debt"] = ("התחייבויות קבועות וחובות", "🏠", "#6366f1", new List<ClusterItemDto>()),
            ["essential"] = ("משתנות חיוניות", "🛒", "#0ea5e9", new List<ClusterItemDto>()),
            ["lifestyle"] = ("סגנון חיים ומותרות", "✨", "#f59e0b", new List<ClusterItemDto>()),
            ["wealth"] = ("צבירת הון וחיסכון", "💎", "#10b981", new List<ClusterItemDto>()),
            ["periodic"] = ("תקופתיות וחריגות", "⚡", "#ec4899", new List<ClusterItemDto>())
        };

        var catTotals = transactions
            .GroupBy(t => new { Name = t.Category?.Name ?? "אחר", Icon = t.Category?.Icon ?? "📋", IsCapital = t.Category?.IsCapitalTransfer ?? false, Periodicity = t.Category?.Periodicity ?? BudgetPeriodicity.Monthly })
            .Select(g => new { g.Key.Name, g.Key.Icon, g.Key.IsCapital, g.Key.Periodicity, Total = g.Sum(t => t.Amount) })
            .OrderByDescending(x => x.Total)
            .ToList();

        static string NormalizeName(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            // הסרת אימוג'ים וסימנים מיוחדים להשוואה מדויקת
            var noEmoji = System.Text.RegularExpressions.Regex.Replace(s, @"\p{Cs}|\p{So}|\p{Sk}|\p{Sm}|\p{Sc}", "");
            var clean = noEmoji.Replace("\"", "").Replace("'", "").Replace("-", " ").Replace("/", " ").Replace("+", " ").Trim().ToLower();
            // נרמול אותיות סופיות (מנצפ"ך) לאותיות רגילות כדי שחיפוש שורשים יעבוד באופן מושלם
            var chars = clean.Select(ch => ch switch
            {
                'ך' => 'כ',
                'ם' => 'מ',
                'ן' => 'נ',
                'ף' => 'פ',
                'ץ' => 'צ',
                _ => ch
            });
            return new string(chars.ToArray());
        }

        foreach (var c in catTotals)
        {
            var rawName = c.Name.Trim();
            var norm = NormalizeName(rawName);
            var pct = totalOutflows > 0 ? (double)Math.Round(c.Total / totalOutflows * 100, 1) : 0;
            var item = new ClusterItemDto(rawName, c.Icon, c.Total, pct);

            // 1. צבירת הון וחיסכון (Wealth & Savings)
            if (c.IsCapital || 
                norm.Contains("השקע") || 
                norm.Contains("חסכונ") || norm.Contains("חיסכונ") || 
                norm.Contains("פקמ") || norm.Contains("קרנ כספית") || 
                norm.Contains("קופת גמל") || norm.Contains("השתלמות") || 
                norm.Contains("מניות") || norm.Contains("נדלנ"))
            {
                clustersMap["wealth"].Items.Add(item);
            }
            // 1b. קטגוריות שהוגדרו מפורשות כ"חד פעמי" -> תקופתיות וחריגות
            else if (c.Periodicity == BudgetPeriodicity.OneTime)
            {
                clustersMap["periodic"].Items.Add(item);
            }
            // 2. התחייבויות קבועות וחובות (Fixed Obligations & Debt)
            // כל הוצאה חוזרת, חוזית ומחייבת שלא ניתן לבטל או לחיות בלעדיה
            else if (
                norm.Contains("משכנת") || 
                norm.Contains("שכר דירה") || norm.Contains("שכירות") || norm.Contains("דיור") ||
                norm.Contains("ארנונ") || norm.Contains("ארנונה") ||
                norm.Contains("מימ") || norm.Contains("תמי 4") || norm.Contains("תמי4") || norm.Contains("בר מימ") ||
                norm.Contains("חשמל") || 
                norm.Contains("גז") || 
                norm.Contains("ועד") || 
                norm.Contains("ביטוח") || 
                norm.Contains("הלווא") || norm.Contains("מימונ") || norm.Contains("ליסינג") || norm.Contains("חוב") || norm.Contains("אשראי") || norm.Contains("בלונ") || norm.Contains("פריהמ") ||
                norm.Contains("אינטרנט") || norm.Contains("סלולר") || norm.Contains("סלולאר") || norm.Contains("טלפונ") || norm.Contains("תקשורת") || norm.Contains("בזק") || norm.Contains("כבלימ") || norm.Contains("טלויז") || norm.Contains("יס") || norm.Contains("הוט") ||
                norm.Contains("חינוכ") || norm.Contains("לימוד") || norm.Contains("מעונ") || norm.Contains("צהרונ") || norm.Contains("פעוטונ") || norm.Contains("גנ") || norm.Contains("בית ספר") || norm.Contains("מטפלת") || norm.Contains("חוג") || norm.Contains("צופימ") || norm.Contains("עזרה בלימוד") || norm.Contains("שיעור פרטי") || norm.Contains("שיעורימ פרטיימ") || norm.Contains("קלינאית") || norm.Contains("ציוד לימוד") || norm.Contains("בייבי סיטר") || norm.Contains("בייביסיטר") ||
                norm.Contains("פנסיה") || norm.Contains("חשבונות") || norm.Contains("אחזקת בית") ||
                norm.Contains("עמלות") || norm.Contains("בנק") || norm.Contains("אגרות") ||
                norm.Contains("מס הכנסה") || norm.Contains("ביטוח לאומי") || norm.Contains("מעמ") || norm.Contains("רואה חשבונ") || norm.Contains("עורכ דינ") || norm.Contains("עוד") || norm.Contains("שכט") ||
                norm.Contains("אמא") || norm.Contains("דמי כיס") || norm.Contains("צהל") || norm.Contains("כרטיס ליעוז") ||
                norm.Contains("טסט") || norm.Contains("רישוי") || norm.Contains("נהיגה") ||
                norm.Contains("החיימ בפלוס"))
            {
                clustersMap["fixed_debt"].Items.Add(item);
            }
            // 3. משתנות חיוניות (Essential Daily Needs)
            // צרכי קיום יומיומיים משתנים: מזון, דלק, תחבורה שוטפת ובריאות
            else if (
                norm.Contains("סופר") || norm.Contains("מכולת") || norm.Contains("מזונ") || norm.Contains("קניות") || norm.Contains("supermarket") ||
                norm.Contains("דלק") || norm.Contains("חניה") || norm.Contains("חנייה") || norm.Contains("רכבת") || norm.Contains("אוטובוס") || norm.Contains("רב קו") || norm.Contains("מונית") || norm.Contains("פנגו") || norm.Contains("סלופארק") || norm.Contains("כביש 6") || norm.Contains("רחיצה") ||
                norm.Contains("קופת חולימ") || norm.Contains("פארמ") || norm.Contains("מרקחת") || norm.Contains("תרופות") || norm.Contains("רופא") || norm.Contains("רפואה") || norm.Contains("בריאות") || norm.Contains("שיניימ") || norm.Contains("דיקור") || norm.Contains("טיפול רפואי") || norm.Contains("טיפול משלימ") || norm.Contains("משקפ") || norm.Contains("אופטיק") || norm.Contains("עיניימ"))
            {
                clustersMap["essential"].Items.Add(item);
            }
            // 4. תקופתיות וחריגות (Periodic & Irregular)
            // הוצאות נקודתיות שאינן חודשיות שוטפות
            else if (
                norm.Contains("שיפוצ") || norm.Contains("ריהוט") || norm.Contains("מכשירי חשמל") || norm.Contains("שדרוג הבית") || norm.Contains("עוברימ דירה") || norm.Contains("קניית בית") || norm.Contains("מכירת בית") || norm.Contains("מחשב") ||
                norm.Contains("חופש") || norm.Contains("נופש") || norm.Contains("טיסות") || norm.Contains("מלונ") || norm.Contains("חול") ||
                norm.Contains("ארוע") || norm.Contains("אירוע") || norm.Contains("חתונה") || norm.Contains("בר מצווה") || norm.Contains("בת מצווה") || norm.Contains("אולמ") || norm.Contains("יומולדת") || norm.Contains("יומהולדת") || norm.Contains("פורימ") || norm.Contains("חגימ") ||
                norm.Contains("מוסכ") || norm.Contains("תיקונ רכב") || norm.Contains("טיפול רכב") || norm.Contains("הוצאת רכב חריגה") || norm.Contains("רכב חריג") ||
                norm.Contains("בלתמ") || norm.Contains("קורונה") || norm.Contains("טלפונימ סלולאריימ"))
            {
                clustersMap["periodic"].Items.Add(item);
            }
            // 5. סגנון חיים ומותרות (Lifestyle & Discretionary)
            // בילויים, מסעדות, שופינג, ביגוד, טיפוח ומותרות
            else
            {
                clustersMap["lifestyle"].Items.Add(item);
            }
        }

        var clusters = clustersMap.Select(kv =>
        {
            var sum = kv.Value.Items.Sum(x => x.Amount);
            var pct = totalOutflows > 0 ? (double)Math.Round(sum / totalOutflows * 100, 1) : 0;
            return new BudgetClusterDto(kv.Key, kv.Value.Name, kv.Value.Icon, kv.Value.Color, sum, pct, kv.Value.Items);
        }).OrderByDescending(c => c.Amount).ToList();

        // 2. Real Savings Accumulated
        var realSavings = clustersMap["wealth"].Items.Sum(x => x.Amount);
        var realSavingsRate = totalIncome > 0 ? Math.Round(realSavings / totalIncome * 100, 1) : 0m;

        // 3. DTI (Debt to Income - יחס החזר חובות מתוך הכנסה נטו)
        // החרגת תשלומי ביטוח חיים/מבנה מהחוב - חוב כולל רק החזרי הלוואות ומשכנתה בפועל
        var effectiveIncome = totalIncome;
        if (effectiveIncome <= 0)
        {
            var incCatsList = await db.IncomeCategories.AsNoTracking().Where(c => c.ProjectId == projectId).ToListAsync();
            var incProjMax = BudgetRolloverHelper.FindMaxMonth(incCatsList.Select(c => c.GetMonthlyBudgets()));
            var plannedIncs = incCatsList.Sum(c => month.HasValue
                ? c.GetPlannedForMonth(year, month.Value, incProjMax)
                : Enumerable.Range(1, 12).Sum(m => c.GetPlannedForMonth(year, m, incProjMax)));
            if (plannedIncs > 0) effectiveIncome = plannedIncs;
        }

        var totalDebt = transactions
            .Where(t => t.Category != null &&
                        !t.Category.Name.Contains("ביטוח") &&
                        (t.Category.Name.Contains("משכנת") || t.Category.Name.Contains("הלווא") || t.Category.Name.Contains("מימון") || t.Category.Name.Contains("ליסינג") || t.Category.Name.Contains("חוב")))
            .Sum(t => t.Amount);

        var dtiPercent = effectiveIncome > 0 ? Math.Round(totalDebt / effectiveIncome * 100, 1) : 0m;
        string dtiStatus = dtiPercent > 45 ? "Danger" : dtiPercent >= 35 ? "Caution" : "Safe";
        string dtiLabel = dtiPercent > 45 ? "עומס חוב חריג (מעל 45%)" : dtiPercent >= 35 ? "רמת סיכון מתונה (35%-45%)" : "יחס חוב תקין ומאוזן (<35%)";
        var safeCeiling = effectiveIncome * 0.35m;
        var dti = new DtiGaugeDto(dtiPercent, dtiStatus, dtiLabel, totalDebt, effectiveIncome, safeCeiling);

        // 4. Top Spenders (Top 5)
        var categories = await db.Categories.AsNoTracking().Where(c => c.ProjectId == projectId).ToListAsync();
        var expProjMax = BudgetRolloverHelper.FindMaxMonth(categories.Select(c => c.GetMonthlyBudgets()));
        var plannedPerCat = categories
            .GroupBy(c => c.Name.Trim())
            .ToDictionary(
                g => g.Key,
                g => g.Sum(c => month.HasValue ? c.GetPlannedForMonth(year, month.Value, expProjMax) : Enumerable.Range(1, 12).Sum(m => c.GetPlannedForMonth(year, m, expProjMax)))
            );

        var topSpenders = catTotals.Take(5).Select(c =>
        {
            var pct = totalOutflows > 0 ? (double)Math.Round(c.Total / totalOutflows * 100, 1) : 0;
            var planned = plannedPerCat.GetValueOrDefault(c.Name.Trim(), 0m);
            return new TopSpenderDto(c.Name, c.Icon, c.Total, pct, planned);
        }).ToList();

        // 5. Partner Breakdown
        var partners = transactions
            .GroupBy(t => t.ExecutedByUser != null ? t.ExecutedByUser.Name : "ענבר")
            .Select(g =>
            {
                var sum = g.Sum(t => t.Amount);
                var count = g.Count();
                var pct = totalOutflows > 0 ? (double)Math.Round(sum / totalOutflows * 100, 1) : 0;
                var avg = count > 0 ? Math.Round(sum / count, 1) : 0m;
                var firstUser = g.First().ExecutedByUser;
                var avatar = firstUser != null && !string.IsNullOrWhiteSpace(firstUser.Avatar) ? firstUser.Avatar : (g.Key.Contains("יוסי") ? "👨‍💼" : "👩‍💼");
                var userId = firstUser != null ? firstUser.Id : Guid.Empty;
                return new PartnerExpenseDto(userId, g.Key, avatar, sum, count, pct, avg);
            })
            .OrderByDescending(p => p.TotalAmount)
            .ToList();

        // 6. Payment Methods Breakdown
        var paymentMethods = transactions
            .GroupBy(t => string.IsNullOrWhiteSpace(t.PaymentMethodLabel) ? t.PaymentMethod.ToString() : t.PaymentMethodLabel)
            .Select(g =>
            {
                var sum = g.Sum(t => t.Amount);
                var pct = totalOutflows > 0 ? (double)Math.Round(sum / totalOutflows * 100, 1) : 0;
                return new PaymentMethodBreakdownDto(g.First().PaymentMethod.ToString(), g.Key, sum, g.Count(), pct);
            })
            .OrderByDescending(p => p.TotalAmount)
            .ToList();

        // 7. Contextual AI Insights
        var insights = new List<FinancialInsightDto>();

        // DTI Insight
        if (dtiPercent > 45)
        {
            insights.Add(new FinancialInsightDto(
                "dti-danger",
                "DebtLoad",
                "Danger",
                $"🚨 עומס חוב חריג: {dtiPercent:F1}% מההכנסה משועבד לחובות!",
                $"החזרי החובות (משכנתא והלוואות בסך ₪{totalDebt:N0}) גוזלים {dtiPercent:F1}% מהכנסתכם (₪{effectiveIncome:N0}). הרף המומלץ בכלכלת המשפחה הוא עד 35% (תקרה של ₪{safeCeiling:N0}). מומלץ לבחון איחוד הלוואות ומחזור משכנתא.",
                dtiPercent,
                "report",
                false
            ));
        }

        // Balloon loan insight
        var balloonTx = transactions.FirstOrDefault(t => t.Description != null && t.Description.Contains("בלון"));
        if (balloonTx != null)
        {
            insights.Add(new FinancialInsightDto(
                "balloon-alert",
                "DebtOptimization",
                "Warning",
                "⏳ פצצת זמן תזרימית: הלוואת בלון (תשלומי ריבית בלבד)",
                $"משולמת ריבית חודשית שוטפת בלבד של ₪{balloonTx.Amount:N0} (הפניקס פריים). הקרן אינה פוחתת ועומדת לפירעון מלא ב-04/2029! חובה לייעד כרית חיסכון נגדית בקרן כספית לפירעון הקרן.",
                balloonTx.Amount,
                "categories",
                false
            ));
        }

        // Real Wealth accumulation vs nominal cash flow
        if (realSavings > 0)
        {
            insights.Add(new FinancialInsightDto(
                "real-wealth-building",
                "WealthAccumulation",
                "Success",
                $"💎 צבירת הון אמיתית: ₪{realSavings:N0} ({realSavingsRate:F1}% מההכנסה)",
                $"אף שהמאזן החודשי הנומינלי מציג פער, הפרשתם ₪{realSavings:N0} לנכסים פיננסיים (השקעות, קרנות כספיות, פק\"מ וילדות). זהו כסף שנשאר בידיכם ומגדיל את ההון העצמי!",
                realSavings,
                "incomes",
                false
            ));
        }

        // Cash spend check
        var cashSpent = transactions.Where(t => t.PaymentMethod == PaymentMethodKind.Cash || (t.PaymentMethodLabel != null && t.PaymentMethodLabel.Contains("מזומן"))).Sum(t => t.Amount);
        if (cashSpent > 1500)
        {
            insights.Add(new FinancialInsightDto(
                "cash-drain",
                "CashControl",
                "Warning",
                $"💸 שימוש מוגבר במזומן: ₪{cashSpent:N0}",
                $"תשלומי מזומן בסך ₪{cashSpent:N0} בוצעו בתקופה זו. הוצאות מזומן נוטות להישמט ממעקב בנקאי ועלולות ליצור דליפה תקציבית סמויה. מומלץ להעביר תשלומים קבועים להוראות קבע או העברות.",
                cashSpent,
                "feed",
                false
            ));
        }

        // Insurance optimization
        var insuranceSpent = transactions.Where(t => t.Category != null && t.Category.Name.Contains("ביטוח")).Sum(t => t.Amount);
        if (insuranceSpent > 1500)
        {
            insights.Add(new FinancialInsightDto(
                "insurance-check",
                "InsuranceOptimization",
                "Info",
                $"🛡️ תיק ביטוחים חודשי: ₪{insuranceSpent:N0}",
                $"סך תשלומי הביטוח (חיים, בריאות, מבנה ורכב) עומד על ₪{insuranceSpent:N0}. מומלץ לבצע בדיקת 'הר הביטוח' אחת לשנה למניעת כפל ביטוחי – פוטנציאל חיסכון משפחתי מוערך של ₪300-₪800 בחודש.",
                insuranceSpent,
                "categories",
                false
            ));
        }

        return new AnalyticsDashboardDto(
            year,
            month,
            periodLabel,
            totalIncome,
            totalExpenses,
            totalCapitalTransfers,
            netCashFlow,
            realSavings,
            realSavingsRate,
            dti,
            clusters,
            topSpenders,
            partners,
            paymentMethods,
            insights
        );
    }

    public async Task<InsightsSummaryDto> GenerateInsightsAsync(Guid projectId, int year, int month)
    {
        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var (from, to) = PeriodMath.Range(year, month, cycleDay);

        // 1. נתוני הכנסות והוצאות לתקופה
        var incomes = await db.Incomes.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.Date >= from && i.Date <= to)
            .Select(i => new { i.ActualAmount })
            .ToListAsync();

        var actualIncome = incomes.Sum(i => i.ActualAmount);

        var transactions = await db.Transactions.AsNoTracking()
            .Include(t => t.Category)
            .Where(t => t.ProjectId == projectId && t.Date >= from && t.Date <= to)
            .ToListAsync();

        var actualExpenses = transactions.Where(t => t.Category == null || !t.Category.IsCapitalTransfer).Sum(t => t.Amount);
        var capitalTransfers = transactions.Where(t => t.Category != null && t.Category.IsCapitalTransfer).Sum(t => t.Amount);
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();
        var expProjMax = BudgetRolloverHelper.FindMaxMonth(categories.Select(c => c.GetMonthlyBudgets()));

        var operatingSurplus = actualIncome - actualExpenses;
        var netCashFlow = operatingSurplus - capitalTransfers;
        var savingsRate = actualIncome > 0 ? Math.Round(operatingSurplus / actualIncome * 100, 1) : 0m;
        var annualProjection = operatingSurplus > 0 ? operatingSurplus * 12 : 0m;

        var insights = new List<FinancialInsightDto>();

        // תובנה עליונה קריטית: היתכנות חיסכון החודש והסכום המדויק
        if (actualIncome == 0 && actualExpenses == 0)
        {
            insights.Add(new FinancialInsightDto(
                "monthly-saving-feasibility",
                "SavingFeasibility",
                "Info",
                "ℹ️ עדיין לא הוזנו תנועות החודש",
                "הזינו הכנסות והוצאות כדי לחשב האם ניתן לחסוך החודש ואיזה סכום.",
                0m,
                "incomes",
                false
            ));
        }
        else if (netCashFlow > 0)
        {
            insights.Add(new FinancialInsightDto(
                "monthly-saving-feasibility",
                "SavingFeasibility",
                "Success",
                $"💰 כן! החודש ניתן לחסוך ₪{netCashFlow:N0}",
                $"הכנסות החודש (₪{actualIncome:N0}) עולות על ההוצאות (₪{actualExpenses:N0}). יש לכם עודף תזרימי פנוי לחיסכון בסך ₪{netCashFlow:N0} (שיעור חיסכון של {savingsRate:F1}%).",
                netCashFlow,
                "report",
                false
            ));
        }
        else if (operatingSurplus > 0 && capitalTransfers > 0)
        {
            insights.Add(new FinancialInsightDto(
                "monthly-saving-feasibility",
                "SavingFeasibility",
                "Success",
                $"💎 הפרשתם ₪{capitalTransfers:N0} לחיסכון והשקעות החודש",
                $"התקציב התפעולי השוטף בעודף חיובי של ₪{operatingSurplus:N0}! היתרה בעו״ש קטנה ב-₪{Math.Abs(netCashFlow):N0} כיוון שהעברתם ₪{capitalTransfers:N0} לבניית הון וחיסכון (העברה הונית).",
                capitalTransfers,
                "report",
                false
            ));
        }
        else if (netCashFlow == 0)
        {
            insights.Add(new FinancialInsightDto(
                "monthly-saving-feasibility",
                "SavingFeasibility",
                "Warning",
                "⚖️ החודש המאזן מאופס (₪0) – אין חיסכון פנוי",
                $"סך ההכנסות וההוצאות זהים (₪{actualIncome:N0}). לא נותר סכום פנוי להפקדה לחיסכון החודש.",
                0m,
                "report",
                false
            ));
        }
        else
        {
            insights.Add(new FinancialInsightDto(
                "monthly-saving-feasibility",
                "SavingFeasibility",
                "Danger",
                $"⛔ לא, החודש לא ניתן לחסוך (גירעון שוטף של -₪{Math.Abs(operatingSurplus):N0})",
                $"ההוצאות השוטפות (₪{actualExpenses:N0}) עולות על ההכנסות (₪{actualIncome:N0}). קיים גירעון תפעולי של ₪{Math.Abs(operatingSurplus):N0}, ולכן לא ניתן להפריש כספים לחיסכון החודש.",
                netCashFlow,
                "report",
                false
            ));
        }

        // תובנה 1: שיעור חיסכון בפועל מול יעד 20%
        if (actualIncome > 0)
        {
            if (savingsRate >= 20m)
            {
                insights.Add(new FinancialInsightDto(
                    "savings-rate",
                    "SavingsRate",
                    "Success",
                    $"🎯 שיעור חיסכון בפועל: {savingsRate:F1}%!",
                    $"מתוך הכנסה של ₪{actualIncome:N0} נותרו ₪{netCashFlow:N0}. הנכם עומדים מעל יעד ה-20% המומלץ למשק בית לביסוס איתנות ועצמאות כלכלית.",
                    savingsRate,
                    "report",
                    false
                ));
            }
            else if (savingsRate >= 0m)
            {
                insights.Add(new FinancialInsightDto(
                    "savings-rate",
                    "SavingsRate",
                    "Warning",
                    $"⚡ שיעור חיסכון מתון: {savingsRate:F1}%",
                    $"נותרו ₪{netCashFlow:N0} מתוך ₪{actualIncome:N0}. מומלץ למפות הוצאות גמישות כדי להגיע ליעד החיסכון המומלץ של 20% לחודש.",
                    savingsRate,
                    "report",
                    false
                ));
            }
            else
            {
                insights.Add(new FinancialInsightDto(
                    "savings-rate",
                    "SavingsRate",
                    "Danger",
                    $"🚨 גירעון תזרימי חודשי: -₪{Math.Abs(netCashFlow):N0}!",
                    $"ההוצאות (₪{actualExpenses:N0}) עולות על ההכנסות (₪{actualIncome:N0}). סכנה לצבירת חובות יקרים בריבית פריים – מומלץ לעצור הוצאות לא חיוניות מיד.",
                    savingsRate,
                    "report",
                    false
                ));
            }
        }

        // תובנה 2: צפי צבירה שנתית (Run-Rate)
        if (netCashFlow > 0)
        {
            insights.Add(new FinancialInsightDto(
                "annual-projection",
                "AnnualProjection",
                "Info",
                $"📅 צפי צבירה שנתית לפרויקט: ₪{annualProjection:N0}",
                "בקצב הנוכחי תסיימו את השנה עם עודף צבירה שיאפשר השקעה מניבה, יצירת קרן חירום ל-6 חודשים או פירעון התחייבויות מוקדם.",
                annualProjection,
                "report",
                false
            ));
        }

        // תובנה 3: זיהוי חריגות תקציב לפי קטגוריות (Budget Variance)
        var spentPerCat = transactions
            .GroupBy(t => t.CategoryId)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

        foreach (var cat in categories)
        {
            var spent = spentPerCat.GetValueOrDefault(cat.Id, 0m);
            var planned = cat.GetPlannedForMonth(year, month, expProjMax);
            if (planned > 0 && spent > planned)
            {
                var over = spent - planned;
                var pctOver = Math.Round(over / planned * 100, 0);
                insights.Add(new FinancialInsightDto(
                    $"overspend-{cat.Id}",
                    "Overspending",
                    "Danger",
                    $"⚠️ התראה קריטית: חריגה ב{cat.Name}!",
                    $"הוצאתם ₪{spent:N0} מתוך תקציב מתוכנן של ₪{planned:N0} (חריגה של ₪{over:N0}, שהם {pctOver}% מעבר לתקציב המתוכנן).",
                    over,
                    "feed",
                    false
                ));
            }
        }

        // תובנה 3.5: מעקב עמידה ביעדי חיסכון (Savings Goals Pace)
        var savingsOverview = await SavingsGoalsService.GetOverviewAsync(db, projectId, year, month);
        if (savingsOverview.TotalGoalsCount > 0)
        {
            if (savingsOverview.OverallPaceStatus == "Behind")
            {
                var gap = Math.Max(0m, savingsOverview.TotalRequiredThisCycle - savingsOverview.TotalDepositedThisCycle);
                insights.Add(new FinancialInsightDto(
                    $"savings-goals-pace-{year}-{month}",
                    "SavingsGoals",
                    "Danger",
                    "🎯 התראת חריגה ביעדי החיסכון",
                    savingsOverview.OverallStatusMessage,
                    gap,
                    "categories",
                    false
                ));
            }
            else if (savingsOverview.OverallPaceStatus == "CyclePending")
            {
                var rem = Math.Max(0m, savingsOverview.TotalRequiredThisCycle - savingsOverview.TotalDepositedThisCycle);
                insights.Add(new FinancialInsightDto(
                    $"savings-goals-pace-{year}-{month}",
                    "SavingsGoals",
                    "Warning",
                    $"🎯 נותרו ₪{rem:N0} ליעדי החיסכון החודש",
                    savingsOverview.OverallStatusMessage,
                    rem,
                    "categories",
                    false
                ));
            }
            else
            {
                insights.Add(new FinancialInsightDto(
                    $"savings-goals-pace-{year}-{month}",
                    "SavingsGoals",
                    "Success",
                    "🎯 הכל טוב! חוסכים בקצב מעולה ליעדים",
                    savingsOverview.OverallStatusMessage,
                    savingsOverview.TotalDepositedThisCycle,
                    "categories",
                    false
                ));
            }
        }

        // תובנה 4: קרן הוצאות עונתיות (Sinking Fund Recommendation)
        insights.Add(new FinancialInsightDto(
            "seasonal-buffer",
            "SeasonalBuffer",
            "Warning",
            "🏖️ היערכות מוקדמת לעומס עונתי ובלת\"מים",
            "היסטורית, עונות הקיץ והחגים מאופיינות בזינוק של עד 40% בהוצאות המשפחה. מומלץ להפריש ₪1,500 החודש לקרן צבירה ייעודית שתמנע כניסה לחובות.",
            1500m,
            "categories",
            false
        ));

        var unreadCount = insights.Count(i => !i.IsRead);
        return new InsightsSummaryDto(insights.Count, unreadCount, savingsRate, annualProjection, insights);
    }
}
