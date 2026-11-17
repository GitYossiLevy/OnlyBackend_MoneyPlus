using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/excel")]
public class ExcelSyncController(AppDbContext db, IPdfReportParser pdfParser) : ControllerBase
{
    private static PaymentMethodKind MapPaymentMethodKind(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return PaymentMethodKind.CreditCard;
        var l = label.ToLowerInvariant();
        if (l.Contains("מזומן") || l.Contains("cash")) return PaymentMethodKind.Cash;
        if (l.Contains("תשלומ") || l.Contains("installment")) return PaymentMethodKind.Installments;
        if (l.Contains("העברה") || l.Contains("bank") || l.Contains("בנק")) return PaymentMethodKind.BankTransfer;
        if (l.Contains("הוראת קבע") || l.Contains("חיוב קבוע")) return PaymentMethodKind.BankTransfer;
        if (l.Contains("ביט") || l.Contains("bit")) return PaymentMethodKind.Bit;
        if (l.Contains("פייבוקס") || l.Contains("paybox")) return PaymentMethodKind.Paybox;
        if (l.Contains("צ'ק") || l.Contains("צק") || l.Contains("המחאה") || l.Contains("check")) return PaymentMethodKind.Check;
        return PaymentMethodKind.CreditCard; // ברירת מחדל כולל כרטיסים ייעודיים כמו יוסי 2520, יוסי 0643, עסק 5528
    }

    private static string NormalizePaymentMethodLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var clean = raw.Trim();
        var stripped = System.Text.RegularExpressions.Regex.Replace(clean, @"^[\uD83C-\uDBFF\uDC00-\uDFFF\u2600-\u27BF\s]+", "").Trim();
        var lower = stripped.ToLowerInvariant();

        if (lower == "ביט" || lower == "bit" || lower.Contains("ביט (bit)"))
            return "📱 ביט (Bit)";
        if (lower == "פייבוקס" || lower == "paybox" || lower.Contains("פייבוקס (paybox)"))
            return "📦 פייבוקס (Paybox)";
        if (lower == "מזומן" || lower == "cash")
            return "💵 מזומן";
        if (lower == "העברה בנקאית" || lower == "bank transfer" || lower == "העברה")
            return "🏦 העברה בנקאית";
        if (lower == "חיוב קבוע" || lower == "הוראת קבע" || lower == "standing order")
            return "חיוב קבוע";
        if (lower == "צ'ק" || lower == "צק" || lower == "שיק" || lower == "check")
            return "📝 צ'ק";

        // מיפוי כרטיסים קנוניים
        if (lower.Contains("2520")) return "💳 2520 (יוסי)";
        if (lower.Contains("0643")) return "💳 0643 (יוסי)";
        if (lower.Contains("6036")) return "💳 ויזה 6036 (יוסי)";
        if (lower.Contains("כאל ישן") || (lower.Contains("כאל") && lower.Contains("יוסי"))) return "💳 ויזה כאל ישן (יוסי)";
        if (lower.Contains("2025")) return "💳 כרטיס 2025 (יוסי)";
        if (lower.Contains("5528")) return "💳 5528 עסק (ענבר)";
        if (lower.Contains("3812")) return "💳 ויזה 3812 (ענבר)";
        if (lower.Contains("0201")) return "💳 ויזה 0201 (ענבר)";

        // כרטיס אשראי כללי
        if (lower == "כרטיס אשראי" || lower == "כרטיס אשראי 1" || lower == "creditcard")
            return "כרטיס אשראי";

        if (clean.StartsWith("💳")) return clean;
        return $"💳 {stripped}";
    }

    private static string PickCategoryIcon(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("סופר") || n.Contains("מזון") || n.Contains("אוכל") || n.Contains("שופרסל") || n.Contains("רמי לוי")) return "🛒";
        if (n.Contains("דלק") || n.Contains("רכב") || n.Contains("מוסך") || n.Contains("תחבורה") || n.Contains("חניה")) return "🚗";
        if (n.Contains("מסעדה") || n.Contains("קפה") || n.Contains("פיצה") || n.Contains("בילוי") || n.Contains("פאב")) return "☕";
        if (n.Contains("חשמל") || n.Contains("מים") || n.Contains("ארנונה") || n.Contains("גז") || n.Contains("חשבונות")) return "💡";
        if (n.Contains("דיור") || n.Contains("שכירות") || n.Contains("משכנתא") || n.Contains("ועד בית")) return "🏠";
        if (n.Contains("חינוך") || n.Contains("גן") || n.Contains("בית ספר") || n.Contains("חוג") || n.Contains("לימודים")) return "🎓";
        if (n.Contains("בריאות") || n.Contains("פארם") || n.Contains("תרופות") || n.Contains("רופא") || n.Contains("סופר פארם")) return "💊";
        if (n.Contains("ביגוד") || n.Contains("בגדים") || n.Contains("אופנה") || n.Contains("זארה") || n.Contains("נעליים")) return "👗";
        if (n.Contains("ביטוח") || n.Contains("פנסיה") || n.Contains("קרן")) return "🛡️";
        if (n.Contains("תקשורת") || n.Contains("אינטרנט") || n.Contains("סלולר") || n.Contains("טלפון")) return "📱";
        if (n.Contains("מתנה") || n.Contains("תרומה") || n.Contains("אירוע")) return "🎁";
        return "📋";
    }

    /// <summary>הורדת דו"ח אקסל חודשי מעוצב ומפורט עם תמיכת עברית מלאה (RTL).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportExcel(Guid projectId, [FromQuery] int year, [FromQuery] int month)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound(new { message = "פרויקט לא נמצא" });

        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var (from, to) = PeriodMath.Range(year, month, cycleDay);

        var transactions = await db.Transactions
            .Include(t => t.Category)
            .Include(t => t.ExecutedByUser)
            .Where(t => t.ProjectId == projectId && t.Date >= from && t.Date <= to)
            .OrderByDescending(t => t.Date)
            .ToListAsync();

        var incomes = await db.Incomes
            .Include(i => i.IncomeCategory)
            .Where(i => i.ProjectId == projectId && i.Date >= from && i.Date <= to)
            .OrderByDescending(i => i.Date)
            .ToListAsync();

        var categories = await db.Categories
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();

        var incomeCats = await db.IncomeCategories
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();

        decimal totalPlannedExp = categories.Sum(c => c.PlannedAmount);
        decimal totalActualExp = transactions.Sum(t => t.Amount);

        decimal totalPlannedInc = 0;
        foreach (var cat in incomeCats)
        {
            var catIncomes = incomes.Where(i => i.IncomeCategoryId == cat.Id).ToList();
            var maxPlanned = catIncomes.Count > 0 ? catIncomes.Max(i => i.PlannedAmount) : 0;
            totalPlannedInc += Math.Max(cat.MonthlyPlannedAmount, maxPlanned);
        }
        decimal totalActualInc = incomes.Sum(i => i.ActualAmount);

        decimal netBalance = totalActualInc - totalActualExp;
        decimal savingsRate = totalActualInc > 0 ? Math.Round(netBalance / totalActualInc * 100, 1) : 0;

        using var wb = new XLWorkbook();

        // ================= SHEET 1: סיכום מאזן חודשי =================
        var wsSummary = wb.Worksheets.Add("מאזן וסיכום חודשי");
        wsSummary.RightToLeft = true;

        // Header Title
        wsSummary.Cell("A1").Value = $"MoneyPlus | דו״ח מאזן חודשי - {project.Name}";
        wsSummary.Range("A1:E1").Merge().Style
            .Font.SetBold(true).Font.SetFontSize(16).Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#312E81"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        wsSummary.Cell("A2").Value = $"תקופה: {from:dd/MM/yyyy} עד {to:dd/MM/yyyy} (חודש {month}/{year})";
        wsSummary.Range("A2:E2").Merge().Style
            .Font.SetItalic(true).Font.SetFontSize(11).Font.SetFontColor(XLColor.FromHtml("#475569"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        // KPI Box Table
        wsSummary.Cell("A4").Value = "מדד פיננסי";
        wsSummary.Cell("B4").Value = "תכנון (₪)";
        wsSummary.Cell("C4").Value = "ביצוע בפועל (₪)";
        wsSummary.Cell("D4").Value = "פער (₪)";
        wsSummary.Cell("E4").Value = "סטטוס";
        wsSummary.Range("A4:E4").Style
            .Font.SetBold(true).Font.SetFontSize(11).Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#4338CA"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        // Row 5: Incomes
        wsSummary.Cell("A5").Value = "סך הכנסות חודשיות";
        wsSummary.Cell("B5").Value = totalPlannedInc;
        wsSummary.Cell("C5").Value = totalActualInc;
        wsSummary.Cell("D5").Value = totalActualInc - totalPlannedInc;
        wsSummary.Cell("E5").Value = totalActualInc >= totalPlannedInc ? "עמידה ביעד ✓" : "פחות מהמתוכנן";
        wsSummary.Range("B5:D5").Style.NumberFormat.Format = "₪#,##0.00";

        // Row 6: Expenses
        wsSummary.Cell("A6").Value = "סך הוצאות חודשיות";
        wsSummary.Cell("B6").Value = totalPlannedExp;
        wsSummary.Cell("C6").Value = totalActualExp;
        wsSummary.Cell("D6").Value = totalPlannedExp - totalActualExp;
        wsSummary.Cell("E6").Value = totalActualExp <= totalPlannedExp ? "במסגרת התקציב ✓" : "חריגה מתקציב ⚠️";
        wsSummary.Range("B6:D6").Style.NumberFormat.Format = "₪#,##0.00";

        // Row 7: Net Balance
        wsSummary.Cell("A7").Value = "מאזן נטו (הכנסות פחות הוצאות)";
        wsSummary.Cell("B7").Value = totalPlannedInc - totalPlannedExp;
        wsSummary.Cell("C7").Value = netBalance;
        wsSummary.Cell("D7").Value = netBalance - (totalPlannedInc - totalPlannedExp);
        wsSummary.Cell("E7").Value = netBalance >= 0 ? $"עודף (+{savingsRate}%)" : "גירעון חודשי";
        wsSummary.Range("A7:E7").Style.Font.SetBold(true);
        wsSummary.Range("B7:D7").Style.NumberFormat.Format = "₪#,##0.00";
        wsSummary.Cell("E7").Style.Font.SetFontColor(netBalance >= 0 ? XLColor.FromHtml("#059669") : XLColor.FromHtml("#DC2626"));

        wsSummary.Range("A4:E7").Style.Border.SetOutsideBorder(XLBorderStyleValues.Medium).Border.SetInsideBorder(XLBorderStyleValues.Thin);

        // Breakdown by Category Title
        wsSummary.Cell("A9").Value = "פילוח תקציב מול ביצוע לפי קטגוריות הוצאה";
        wsSummary.Range("A9:E9").Merge().Style
            .Font.SetBold(true).Font.SetFontSize(13).Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#1E293B"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        wsSummary.Cell("A10").Value = "שם קטגוריה";
        wsSummary.Cell("B10").Value = "תקציב יעד (₪)";
        wsSummary.Cell("C10").Value = "הוצאה בפועל (₪)";
        wsSummary.Cell("D10").Value = "יתרה לניצול (₪)";
        wsSummary.Cell("E10").Value = "% ניצול תקציב";
        wsSummary.Range("A10:E10").Style
            .Font.SetBold(true).Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#334155"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        int rowIdx = 11;
        foreach (var c in categories.OrderBy(c => c.Name))
        {
            var spent = transactions.Where(t => t.CategoryId == c.Id).Sum(t => t.Amount);
            var remaining = c.PlannedAmount - spent;
            var pct = c.PlannedAmount > 0 ? Math.Round(spent / c.PlannedAmount * 100, 1) : (spent > 0 ? 100 : 0);

            wsSummary.Cell(rowIdx, 1).Value = $"{c.Icon} {c.Name}";
            wsSummary.Cell(rowIdx, 2).Value = c.PlannedAmount;
            wsSummary.Cell(rowIdx, 3).Value = spent;
            wsSummary.Cell(rowIdx, 4).Value = remaining;
            wsSummary.Cell(rowIdx, 5).Value = $"{pct}%";

            wsSummary.Cell(rowIdx, 2).Style.NumberFormat.Format = "₪#,##0.00";
            wsSummary.Cell(rowIdx, 3).Style.NumberFormat.Format = "₪#,##0.00";
            wsSummary.Cell(rowIdx, 4).Style.NumberFormat.Format = "₪#,##0.00";

            if (spent > c.PlannedAmount && c.PlannedAmount > 0)
            {
                wsSummary.Cell(rowIdx, 4).Style.Font.SetFontColor(XLColor.FromHtml("#DC2626")).Font.SetBold(true);
                wsSummary.Cell(rowIdx, 5).Style.Font.SetFontColor(XLColor.FromHtml("#DC2626")).Font.SetBold(true);
            }

            rowIdx++;
        }

        wsSummary.Range($"A10:E{rowIdx - 1}").Style.Border.SetOutsideBorder(XLBorderStyleValues.Medium).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        wsSummary.Columns().AdjustToContents();

        // ================= SHEET 2: פירוט הוצאות =================
        var wsExp = wb.Worksheets.Add("פירוט הוצאות");
        wsExp.RightToLeft = true;

        wsExp.Cell("A1").Value = "תאריך";
        wsExp.Cell("B1").Value = "קטגוריה";
        wsExp.Cell("C1").Value = "תיאור ההוצאה";
        wsExp.Cell("D1").Value = "סכום (₪)";
        wsExp.Cell("E1").Value = "אמצעי תשלום";
        wsExp.Cell("F1").Value = "שולם ע״י";
        wsExp.Cell("G1").Value = "תשלומים";

        wsExp.Range("A1:G1").Style
            .Font.SetBold(true).Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#4F46E5"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        int expRow = 2;
        foreach (var t in transactions)
        {
            wsExp.Cell(expRow, 1).Value = t.Date.ToString("dd/MM/yyyy");
            wsExp.Cell(expRow, 2).Value = $"{t.Category?.Icon} {t.Category?.Name}";
            wsExp.Cell(expRow, 3).Value = t.Description;
            wsExp.Cell(expRow, 4).Value = t.Amount;
            wsExp.Cell(expRow, 4).Style.NumberFormat.Format = "₪#,##0.00";
            wsExp.Cell(expRow, 5).Value = t.PaymentMethodLabel ?? t.PaymentMethod.ToString();
            wsExp.Cell(expRow, 6).Value = t.ExecutedByUser?.Name ?? "לא מוגדר";
            wsExp.Cell(expRow, 7).Value = t.InstallmentsTotal.HasValue && t.InstallmentsTotal.Value > 1
                ? $"תשלום {t.InstallmentNumber} מתוך {t.InstallmentsTotal}"
                : "רגיל (1)";
            expRow++;
        }

        if (transactions.Count > 0)
        {
            wsExp.Cell(expRow, 3).Value = "סה״כ הוצאות";
            wsExp.Cell(expRow, 3).Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            wsExp.Cell(expRow, 4).FormulaA1 = $"SUM(D2:D{expRow - 1})";
            wsExp.Cell(expRow, 4).Style.Font.SetBold(true).NumberFormat.Format = "₪#,##0.00";
            wsExp.Range($"A1:G{expRow}").Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorder(XLBorderStyleValues.Medium);
        }
        wsExp.Columns().AdjustToContents();

        // ================= SHEET 3: פירוט הכנסות =================
        var wsInc = wb.Worksheets.Add("פירוט הכנסות");
        wsInc.RightToLeft = true;

        wsInc.Cell("A1").Value = "תאריך";
        wsInc.Cell("B1").Value = "קטגוריה";
        wsInc.Cell("C1").Value = "מקור ההכנסה";
        wsInc.Cell("D1").Value = "סכום מתוכנן (₪)";
        wsInc.Cell("E1").Value = "סכום בפועל (₪)";
        wsInc.Cell("F1").Value = "פער (₪)";
        wsInc.Cell("G1").Value = "סוג תקבול";

        wsInc.Range("A1:G1").Style
            .Font.SetBold(true).Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#059669"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        int incRow = 2;
        foreach (var i in incomes)
        {
            wsInc.Cell(incRow, 1).Value = i.Date.ToString("dd/MM/yyyy");
            wsInc.Cell(incRow, 2).Value = $"{i.IncomeCategory?.Icon} {i.IncomeCategory?.Name}";
            wsInc.Cell(incRow, 3).Value = i.Description;
            wsInc.Cell(incRow, 4).Value = i.PlannedAmount;
            wsInc.Cell(incRow, 4).Style.NumberFormat.Format = "₪#,##0.00";
            wsInc.Cell(incRow, 5).Value = i.ActualAmount;
            wsInc.Cell(incRow, 5).Style.NumberFormat.Format = "₪#,##0.00";
            wsInc.Cell(incRow, 6).Value = i.ActualAmount - i.PlannedAmount;
            wsInc.Cell(incRow, 6).Style.NumberFormat.Format = "₪#,##0.00";
            wsInc.Cell(incRow, 7).Value = i.ReceiptType.ToString();
            incRow++;
        }

        if (incomes.Count > 0)
        {
            wsInc.Cell(incRow, 3).Value = "סה״כ הכנסות";
            wsInc.Cell(incRow, 3).Style.Font.SetBold(true).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
            wsInc.Cell(incRow, 5).FormulaA1 = $"SUM(E2:E{incRow - 1})";
            wsInc.Cell(incRow, 5).Style.Font.SetBold(true).NumberFormat.Format = "₪#,##0.00";
            wsInc.Range($"A1:G{incRow}").Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorder(XLBorderStyleValues.Medium);
        }
        wsInc.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"MoneyPlus_Report_{year}_{month:D2}.xlsx");
    }

    /// <summary>טעינה אוטומטית של קובץ ההיסטוריה המלא (RonitPlus_Full_History_2019_2026.xlsx או קובץ פרויקט ייעודי) ישירות למסד הנתונים.</summary>
    [HttpPost("import-history")]
    public async Task<ActionResult<ExcelImportResultDto>> ImportHistoryFromServerFile(
        Guid projectId,
        [FromQuery] bool replaceExisting = true,
        [FromQuery] string? fileName = null)
    {
        var targetName = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            var projName = (await db.Projects.Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync() ?? "").Trim();
            targetName = projName switch
            {
                var n when n.Contains("העסק של ענבר") || n.Contains("עסק פרטי") => "RonitPlus_Project_1226.xlsx",
                var n when n.Contains("בת מצווה ליעוז") => "RonitPlus_Project_1227.xlsx",
                var n when n.Contains("רכישת בית") => "RonitPlus_Project_5425.xlsx",
                var n when n.Contains("שיפוץ האורנית") => "RonitPlus_Project_5978.xlsx",
                var n when n.Contains("שיפוץ גינה") => "RonitPlus_Project_13605.xlsx",
                var n when n.Contains("בת מצווה אלמוג כיתה") => "RonitPlus_Project_14429.xlsx",
                var n when n.Contains("בת מצווה אלמוג משפחה") => "RonitPlus_Project_17073.xlsx",
                var n when n.Contains("פראג עם ליעוז") => "RonitPlus_Project_20472.xlsx",
                var n when n.Contains("טיול לאוסטריה") => "RonitPlus_Project_20889.xlsx",
                var n when n.Contains("בת מצווה יערה") => "RonitPlus_Project_23379.xlsx",
                _ => "RonitPlus_Full_History_2019_2026.xlsx"
            };
        }

        string[] candidatePaths =
        [
            Path.Combine(Directory.GetCurrentDirectory(), targetName),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", targetName),
            Path.Combine(@"c:\Users\Home\.gemini\antigravity\scratch\money_plus", targetName)
        ];

        var filePath = candidatePaths.FirstOrDefault(System.IO.File.Exists);
        if (filePath == null)
            return NotFound(new { message = $"קובץ ההיסטוריה {targetName} לא נמצא בשרת" });

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var msCopy = new MemoryStream();
        await fs.CopyToAsync(msCopy);
        msCopy.Position = 0;
        var formFile = new FormFile(msCopy, 0, msCopy.Length, "file", Path.GetFileName(filePath));
        return await ImportExcel(projectId, 2026, 10, replaceExisting, formFile);
    }

    /// <summary>טעינת דו"ח Excel / PDF / CSV חודשי או רב-שנתי, יצירת קטגוריות חסרות, קליטת אמצעי תשלום חדשים ושיוך משתמשים.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(15 * 1024 * 1024)]
    public async Task<ActionResult<ExcelImportResultDto>> ImportExcel(
        Guid projectId,
        [FromQuery] int year,
        [FromQuery] int month,
        [FromQuery] bool? replaceExisting,
        [FromForm] IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "לא נבחר קובץ להעלאה" });

        if (file.Length > 15 * 1024 * 1024)
            return BadRequest(new { message = "גודל הקובץ חורג מהמגבלה המותרת (עד 15MB)" });

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return NotFound(new { message = "פרויקט לא נמצא" });

        // אם המשתמש בחר להחליף נתונים קיימים בחודש זה (למניעת כפילויות בריצה חוזרת)
        if (replaceExisting == true)
        {
            var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
            var (from, to) = PeriodMath.Range(year, month, cycleDay);
            var oldTxs = await db.Transactions.Where(t => t.ProjectId == projectId && t.Date >= from && t.Date <= to).ToListAsync();
            db.Transactions.RemoveRange(oldTxs);
            var oldIncomes = await db.Incomes.Where(i => i.ProjectId == projectId && i.Date >= from && i.Date <= to).ToListAsync();
            db.Incomes.RemoveRange(oldIncomes);
            await db.SaveChangesAsync();
        }

        var defaultUser = await db.ProjectUsers
            .Where(u => u.ProjectId == projectId)
            .OrderByDescending(u => u.IsActiveDefault)
            .FirstOrDefaultAsync();

        if (defaultUser == null)
        {
            defaultUser = new ProjectUser
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = "ענבר",
                Role = "שותף/ה 🤝",
                Avatar = "👩‍💼",
                IsActiveDefault = true
            };
            db.ProjectUsers.Add(defaultUser);
            await db.SaveChangesAsync();
        }

        var projectUsers = await db.ProjectUsers.Where(u => u.ProjectId == projectId).ToListAsync();

        ProjectUser ResolveUser(string? userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return defaultUser;
            var clean = userName.Trim();
            var match = projectUsers.FirstOrDefault(u =>
                u.Name.Trim().Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                u.Name.Trim().Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                clean.Contains(u.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            var newUser = new ProjectUser
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = clean,
                Role = clean.Contains("יוסי") ? "מנהל ראשי 👑" : "שותף/ה 🤝",
                Avatar = clean.Contains("יוסי") ? "👨‍💼" : "👩‍💼",
                IsActiveDefault = false
            };
            db.ProjectUsers.Add(newUser);
            projectUsers.Add(newUser);
            return newUser;
        }

        var existingCategories = await db.Categories
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();
        var catLookup = existingCategories
            .GroupBy(c => c.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var existingIncomeCats = await db.IncomeCategories
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();
        var incCatLookup = existingIncomeCats
            .GroupBy(c => c.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var projectMethods = project.GetPaymentMethods();
        var newPaymentMethodsAdded = new List<string>();

        void EnsurePaymentMethod(string? method)
        {
            if (string.IsNullOrWhiteSpace(method)) return;
            var normalized = NormalizePaymentMethodLabel(method);
            if (string.IsNullOrWhiteSpace(normalized)) return;

            var clean = System.Text.RegularExpressions.Regex.Replace(normalized, @"^[\uD83C-\uDBFF\uDC00-\uDFFF\u2600-\u27BF\s]+", "").Trim().ToLowerInvariant();
            if (clean == "כרטיס אשראי" || clean == "כרטיס אשראי 1" || clean == "creditcard" || clean.Contains("תשלומים"))
                return;

            bool exists = projectMethods.Any(m =>
            {
                var mClean = System.Text.RegularExpressions.Regex.Replace(m, @"^[\uD83C-\uDBFF\uDC00-\uDFFF\u2600-\u27BF\s]+", "").Trim().ToLowerInvariant();
                return mClean == clean;
            });

            if (!exists)
            {
                projectMethods.Add(normalized);
                project.SetPaymentMethods(projectMethods);
                if (!newPaymentMethodsAdded.Contains(normalized))
                    newPaymentMethodsAdded.Add(normalized);
            }
        }

        var newCategoriesCreated = new List<string>();
        var warnings = new List<string>();
        int importedExpenses = 0;
        int importedIncomes = 0;
        decimal totalExp = 0;
        decimal totalInc = 0;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (ext == ".pdf")
        {
            using var stream = file.OpenReadStream();
            var pdfRows = pdfParser.Parse(stream, year, month);

            foreach (var row in pdfRows)
            {
                try
                {
                    if (row.IsIncome)
                    {
                        var catKey = row.CategoryName.Trim().ToLowerInvariant();
                        if (!incCatLookup.TryGetValue(catKey, out var incCat))
                        {
                            incCat = new IncomeCategory
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                Name = row.CategoryName.Trim(),
                                Icon = "🪙",
                                MonthlyPlannedAmount = row.PlannedAmount ?? row.Amount
                            };
                            db.IncomeCategories.Add(incCat);
                            incCatLookup[catKey] = incCat;
                            if (!newCategoriesCreated.Contains(incCat.Name))
                                newCategoriesCreated.Add(incCat.Name);
                        }
                        else if (row.PlannedAmount.HasValue && row.PlannedAmount.Value > 0)
                        {
                            incCat.MonthlyPlannedAmount = row.PlannedAmount.Value;
                        }

                        if (row.Amount > 0)
                        {
                            var inc = new ProjectIncome
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                IncomeCategoryId = incCat.Id,
                                Description = row.Description,
                                PlannedAmount = row.PlannedAmount ?? row.Amount,
                                ActualAmount = row.Amount,
                                Date = row.Date,
                                ReceiptType = ReceiptType.FixedSalary
                            };
                            db.Incomes.Add(inc);
                            importedIncomes++;
                            totalInc += row.Amount;
                        }
                    }
                    else
                    {
                        var catKey = row.CategoryName.Trim().ToLowerInvariant();
                        if (!catLookup.TryGetValue(catKey, out var expCat))
                        {
                            expCat = new Category
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                Name = row.CategoryName.Trim(),
                                Icon = PickCategoryIcon(row.CategoryName),
                                PlannedAmount = row.PlannedAmount ?? 0,
                                Periodicity = BudgetPeriodicity.Monthly
                            };
                            db.Categories.Add(expCat);
                            catLookup[catKey] = expCat;
                            if (!newCategoriesCreated.Contains(expCat.Name))
                                newCategoriesCreated.Add(expCat.Name);
                        }
                        else if (row.PlannedAmount.HasValue && row.PlannedAmount.Value > 0)
                        {
                            expCat.PlannedAmount = row.PlannedAmount.Value;
                        }

                        if (row.Amount > 0)
                        {
                            var assignedUser = ResolveUser(row.UserName);
                            var rawMethod = string.IsNullOrWhiteSpace(row.PaymentMethod) ? "כרטיס אשראי" : row.PaymentMethod.Trim();
                            var methodLabel = NormalizePaymentMethodLabel(rawMethod);
                            EnsurePaymentMethod(methodLabel);

                            var tx = new Transaction
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                CategoryId = expCat.Id,
                                ExecutedByUserId = assignedUser.Id,
                                Description = row.Description,
                                Amount = row.Amount,
                                Date = row.Date,
                                PaymentMethod = MapPaymentMethodKind(methodLabel),
                                PaymentMethodLabel = methodLabel
                            };
                            db.Transactions.Add(tx);
                            importedExpenses++;
                            totalExp += row.Amount;
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"תנועה מ-PDF לא יובאה: {ex.Message}");
                }
            }
        }
        else if (ext == ".xlsx" || ext == ".xls")
        {
            using var stream = file.OpenReadStream();
            using var wb = new XLWorkbook(stream);

            var hasFullHistorySheets = wb.Worksheets.Any(w => w.Name.Contains("פירוט כל ההוצאות") || w.Name.Contains("פירוט כל ההכנסות"));

            // אם זה קובץ היסטוריה רב-שנתי ונבחרה החלפה – ננקה את כל התנועות והקטגוריות הקיימות בפרויקט למניעת כפילויות וערבוב פרויקטים
            if (hasFullHistorySheets && replaceExisting == true)
            {
                project.CycleStartDay = 10;
                await db.Transactions.Where(t => t.ProjectId == projectId).ExecuteDeleteAsync();
                await db.Incomes.Where(i => i.ProjectId == projectId).ExecuteDeleteAsync();
                await db.Categories.Where(c => c.ProjectId == projectId).ExecuteDeleteAsync();
                await db.IncomeCategories.Where(c => c.ProjectId == projectId).ExecuteDeleteAsync();
                foreach (var c in existingCategories) db.Entry(c).State = EntityState.Detached;
                foreach (var ic in existingIncomeCats) db.Entry(ic).State = EntityState.Detached;
                catLookup.Clear();
                incCatLookup.Clear();
            }

            // שלב א': קריאת תקציבים מתוכננים ותדירות מתוך גיליון "סיכום תזרים וקטגוריות" (אם קיים)
            var summarySheet = wb.Worksheets.FirstOrDefault(w => w.Name.Contains("סיכום תזרים וקטגוריות"));
            if (summarySheet != null)
            {
                var seenExpBudgets = new HashSet<string>();
                var seenIncBudgets = new HashSet<string>();
                var expMonthlyBudgets = new Dictionary<string, Dictionary<string, decimal>>();
                var incMonthlyBudgets = new Dictionary<string, Dictionary<string, decimal>>();
                int lastSumRow = summarySheet.LastRowUsed()?.RowNumber() ?? 0;

                for (int r = 2; r <= lastSumRow; r++)
                {
                    var ymLabel = summarySheet.Cell(r, 1).GetString().Trim();
                    var kind = summarySheet.Cell(r, 3).GetString().Trim();
                    var catName = summarySheet.Cell(r, 4).GetString().Trim();
                    var freq = summarySheet.Cell(r, 5).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(catName)) continue;

                    var plannedStr = summarySheet.Cell(r, 6).GetString().Replace("₪", "").Replace(",", "").Trim();
                    decimal.TryParse(plannedStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var plannedVal);

                    var catKey = catName.ToLowerInvariant();
                    if (kind == "הכנסה")
                    {
                        if (!incMonthlyBudgets.TryGetValue(catKey, out var mMap))
                        {
                            mMap = new Dictionary<string, decimal>();
                            incMonthlyBudgets[catKey] = mMap;
                        }
                        if (!string.IsNullOrWhiteSpace(ymLabel))
                        {
                            mMap[ymLabel] = mMap.GetValueOrDefault(ymLabel) + plannedVal;
                        }

                        if (!incCatLookup.TryGetValue(catKey, out var incCat))
                        {
                            incCat = new IncomeCategory
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                Name = catName,
                                Icon = "🪙",
                                MonthlyPlannedAmount = plannedVal
                            };
                            db.IncomeCategories.Add(incCat);
                            incCatLookup[catKey] = incCat;
                            if (!newCategoriesCreated.Contains(catName))
                                newCategoriesCreated.Add(catName);
                            if (plannedVal > 0) seenIncBudgets.Add(catKey);
                        }
                        else if (!seenIncBudgets.Contains(catKey) && plannedVal > 0)
                        {
                            incCat.MonthlyPlannedAmount = plannedVal;
                            seenIncBudgets.Add(catKey);
                        }
                    }
                    else if (kind == "הוצאה")
                    {
                        if (!expMonthlyBudgets.TryGetValue(catKey, out var mMap))
                        {
                            mMap = new Dictionary<string, decimal>();
                            expMonthlyBudgets[catKey] = mMap;
                        }
                        if (!string.IsNullOrWhiteSpace(ymLabel))
                        {
                            mMap[ymLabel] = mMap.GetValueOrDefault(ymLabel) + plannedVal;
                        }

                        var periodicity = freq.Contains("שבועי") ? BudgetPeriodicity.Weekly
                            : freq.Contains("חד פעמי") ? BudgetPeriodicity.OneTime
                            : BudgetPeriodicity.Monthly;

                        if (!catLookup.TryGetValue(catKey, out var expCat))
                        {
                            expCat = new Category
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                Name = catName,
                                Icon = PickCategoryIcon(catName),
                                PlannedAmount = plannedVal,
                                Periodicity = periodicity
                            };
                            db.Categories.Add(expCat);
                            catLookup[catKey] = expCat;
                            if (!newCategoriesCreated.Contains(catName))
                                newCategoriesCreated.Add(catName);
                            if (plannedVal > 0) seenExpBudgets.Add(catKey);
                        }
                        else if (!seenExpBudgets.Contains(catKey))
                        {
                            if (plannedVal > 0)
                            {
                                expCat.PlannedAmount = plannedVal;
                                seenExpBudgets.Add(catKey);
                            }
                            expCat.Periodicity = periodicity;
                        }
                    }
                }

                foreach (var (catKey, mMap) in incMonthlyBudgets)
                {
                    if (incCatLookup.TryGetValue(catKey, out var incCat))
                    {
                        incCat.SetMonthlyBudgets(mMap);
                    }
                }

                foreach (var (catKey, mMap) in expMonthlyBudgets)
                {
                    if (catLookup.TryGetValue(catKey, out var expCat))
                    {
                        expCat.SetMonthlyBudgets(mMap);
                    }
                }
            }

            // שלב ב': בחירת גיליונות הפירוט לייבוא תנועות (דילוג על גיליונות סיכום וגיליונות שנתיים כדי למנוע כפילויות)
            var sheetsToProcess = hasFullHistorySheets
                ? wb.Worksheets.Where(w => w.Name.Contains("פירוט כל ההכנסות") || w.Name.Contains("פירוט כל ההוצאות")).ToList()
                : wb.Worksheets.Where(w => !w.Name.Contains("סיכום") && !w.Name.Contains("מאזן") && !w.Name.StartsWith("שנת ")).ToList();

            string[] supportedDateFormats = ["dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "yyyy-MM-dd"];

            foreach (var ws in sheetsToProcess)
            {
                var sheetName = ws.Name.ToLowerInvariant();
                bool isIncomeSheet = sheetName.Contains("הכנס") || sheetName.Contains("income");

                int headerRow = 1;
                int dateCol = -1, descCol = -1, catCol = -1, amountCol = -1, plannedCol = -1, methodCol = -1, instCol = -1, userCol = -1;

                for (int r = 1; r <= Math.Min(10, ws.LastRowUsed()?.RowNumber() ?? 1); r++)
                {
                    for (int c = 1; c <= Math.Min(20, ws.LastColumnUsed()?.ColumnNumber() ?? 1); c++)
                    {
                        var val = ws.Cell(r, c).GetString().Trim().ToLowerInvariant();
                        if (dateCol == -1 && (val == "תאריך" || val.Contains("תאריך") || val.Contains("date"))) dateCol = c;
                        else if (catCol == -1 && (val.Contains("קטגור") || val.Contains("category") || val.Contains("סעיף"))) catCol = c;
                        else if (descCol == -1 && (val.Contains("תיאור") || val.Contains("עסק") || val.Contains("פרטים") || val.Contains("desc") || val.Contains("מקור"))) descCol = c;
                        else if (amountCol == -1 && (val.Contains("סכום בפועל") || val.Contains("סכום החיוב") || (val.Contains("סכום") && !val.Contains("כולל")) || val.Contains("amount"))) amountCol = c;
                        else if (plannedCol == -1 && val.Contains("תכנון")) plannedCol = c;
                        else if (methodCol == -1 && (val.Contains("אמצעי") || val.Contains("כרטיס") || val.Contains("אופן תקבול") || val.Contains("method"))) methodCol = c;
                        else if (instCol == -1 && (val == "תשלום" || val.Contains("מספר תשלום"))) instCol = c;
                        else if (userCol == -1 && (val.Contains("מבצע") || val.Contains("משתמש") || val.Contains("user"))) userCol = c;
                    }
                    if (amountCol > 0 && (descCol > 0 || catCol > 0))
                    {
                        headerRow = r;
                        break;
                    }
                }

                if (amountCol == -1) amountCol = 4;
                if (descCol == -1) descCol = 3;
                if (catCol == -1) catCol = 2;
                if (dateCol == -1) dateCol = 1;

                int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
                for (int r = headerRow + 1; r <= lastRow; r++)
                {
                    try
                    {
                        var amtStr = ws.Cell(r, amountCol).GetString().Replace("₪", "").Replace(",", "").Trim();
                        if (string.IsNullOrWhiteSpace(amtStr) || !decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                        {
                            if (!decimal.TryParse(amtStr, out amount)) continue;
                        }

                        if (amount == 0) continue;

                        var desc = descCol > 0 ? ws.Cell(r, descCol).GetString().Trim() : "תנועה מיובאת";
                        if (string.IsNullOrWhiteSpace(desc)) desc = "תנועה מיובאת";

                        var catName = catCol > 0 ? ws.Cell(r, catCol).GetString().Trim() : "כללי";
                        if (string.IsNullOrWhiteSpace(catName)) catName = "כללי";

                        DateOnly date;
                        var dateCell = ws.Cell(r, dateCol);
                        if (dateCell.DataType == XLDataType.DateTime)
                        {
                            date = DateOnly.FromDateTime(dateCell.GetDateTime());
                        }
                        else
                        {
                            var rawDateStr = dateCell.GetString().Trim();
                            if (!DateOnly.TryParseExact(rawDateStr, supportedDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
                                !DateOnly.TryParse(rawDateStr, CultureInfo.GetCultureInfo("he-IL"), DateTimeStyles.None, out date) &&
                                !DateOnly.TryParse(rawDateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                            {
                                date = new DateOnly(year, month, Math.Min(15, DateTime.DaysInMonth(year, month)));
                            }
                        }

                        var rawMethod = methodCol > 0 ? ws.Cell(r, methodCol).GetString().Trim() : "כרטיס אשראי";
                        if (string.IsNullOrWhiteSpace(rawMethod)) rawMethod = "כרטיס אשראי";
                        var methodLabel = NormalizePaymentMethodLabel(rawMethod);
                        if (!isIncomeSheet)
                        {
                            EnsurePaymentMethod(methodLabel);
                        }

                        var userName = userCol > 0 ? ws.Cell(r, userCol).GetString().Trim() : null;
                        var assignedUser = ResolveUser(userName);

                        if (isIncomeSheet || (amount < 0 && sheetName.Contains("חשבון")))
                        {
                            var incAmount = isIncomeSheet ? amount : Math.Abs(amount);
                            decimal plannedAmt = Math.Abs(amount);
                            if (plannedCol > 0)
                            {
                                var pStr = ws.Cell(r, plannedCol).GetString().Replace("₪", "").Replace(",", "").Trim();
                                if (decimal.TryParse(pStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedP) && parsedP > 0)
                                    plannedAmt = parsedP;
                            }

                            var catKey = catName.ToLowerInvariant();
                            if (!incCatLookup.TryGetValue(catKey, out var incCat))
                            {
                                incCat = new IncomeCategory
                                {
                                    Id = Guid.NewGuid(),
                                    ProjectId = projectId,
                                    Name = catName,
                                    Icon = "🪙",
                                    MonthlyPlannedAmount = plannedAmt
                                };
                                db.IncomeCategories.Add(incCat);
                                incCatLookup[catKey] = incCat;
                                if (!newCategoriesCreated.Contains(catName))
                                    newCategoriesCreated.Add(catName);
                            }

                            var inc = new ProjectIncome
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                IncomeCategoryId = incCat.Id,
                                Description = desc,
                                PlannedAmount = plannedAmt,
                                ActualAmount = incAmount,
                                Date = date,
                                ReceiptType = ReceiptType.FixedSalary
                            };
                            db.Incomes.Add(inc);
                            importedIncomes++;
                            totalInc += incAmount;
                        }
                        else
                        {
                            var expAmount = (sheetName.Contains("הוצא") || sheetName.Contains("expense")) ? amount : Math.Abs(amount);
                            var catKey = catName.ToLowerInvariant();
                            if (!catLookup.TryGetValue(catKey, out var expCat))
                            {
                                expCat = new Category
                                {
                                    Id = Guid.NewGuid(),
                                    ProjectId = projectId,
                                    Name = catName,
                                    Icon = PickCategoryIcon(catName),
                                    PlannedAmount = 0,
                                    Periodicity = BudgetPeriodicity.Monthly
                                };
                                db.Categories.Add(expCat);
                                catLookup[catKey] = expCat;
                                if (!newCategoriesCreated.Contains(catName))
                                    newCategoriesCreated.Add(catName);
                            }

                            int? instNum = null;
                            int? instTot = null;
                            if (instCol > 0)
                            {
                                var instStr = ws.Cell(r, instCol).GetString().Trim();
                                var mInst = System.Text.RegularExpressions.Regex.Match(instStr, @"תשלום\s+(\d+)\s+מתוך\s+(\d+)");
                                if (mInst.Success)
                                {
                                    instNum = int.Parse(mInst.Groups[1].Value);
                                    instTot = int.Parse(mInst.Groups[2].Value);
                                }
                                else
                                {
                                    var mTotOnly = System.Text.RegularExpressions.Regex.Match(instStr, @"(\d+)\s+תשלומים");
                                    if (mTotOnly.Success) instTot = int.Parse(mTotOnly.Groups[1].Value);
                                }
                            }

                            var pmKind = instTot is > 1 ? PaymentMethodKind.Installments : MapPaymentMethodKind(methodLabel);

                            var tx = new Transaction
                            {
                                Id = Guid.NewGuid(),
                                ProjectId = projectId,
                                CategoryId = expCat.Id,
                                ExecutedByUserId = assignedUser.Id,
                                Description = desc,
                                Amount = expAmount,
                                Date = date,
                                PaymentMethod = pmKind,
                                PaymentMethodLabel = methodLabel,
                                InstallmentNumber = instNum,
                                InstallmentsTotal = instTot
                            };
                            db.Transactions.Add(tx);
                            importedExpenses++;
                            totalExp += expAmount;
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"שורה {r} לא יובאה: {ex.Message}");
                    }
                }
            }
        }
        else if (ext == ".csv")
        {
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            string? line;
            int lineNum = 0;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lineNum++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(new[] { ',', ';' });
                if (parts.Length < 2) continue;

                if (lineNum == 1 && (parts[0].Contains("תאריך") || parts[0].ToLowerInvariant().Contains("date")))
                    continue;

                try
                {
                    var dateStr = parts[0].Trim();
                    var catName = parts.Length > 1 ? parts[1].Trim() : "כללי";
                    var desc = parts.Length > 2 ? parts[2].Trim() : "תנועה מיובאת";
                    var amtStr = parts.Length > 3 ? parts[3].Trim() : parts[1].Trim();
                    var rawMethod = parts.Length > 4 ? parts[4].Trim() : "כרטיס אשראי";
                    var methodLabel = NormalizePaymentMethodLabel(rawMethod);
                    var userName = parts.Length > 5 ? parts[5].Trim() : null;

                    amtStr = amtStr.Replace("₪", "").Replace(",", "").Trim();
                    if (!decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                    {
                        if (!decimal.TryParse(amtStr, out amount)) continue;
                    }

                    if (amount == 0) continue;

                    DateOnly date;
                    if (!DateOnly.TryParse(dateStr, CultureInfo.GetCultureInfo("he-IL"), out date) &&
                        !DateOnly.TryParse(dateStr, CultureInfo.InvariantCulture, out date))
                    {
                        date = new DateOnly(year, month, Math.Min(15, DateTime.DaysInMonth(year, month)));
                    }

                    EnsurePaymentMethod(methodLabel);
                    var assignedUser = ResolveUser(userName);

                    var positiveAmount = Math.Abs(amount);
                    var catKey = catName.ToLowerInvariant();
                    if (!catLookup.TryGetValue(catKey, out var expCat))
                    {
                        expCat = new Category
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = projectId,
                            Name = catName,
                            Icon = PickCategoryIcon(catName),
                            PlannedAmount = 0,
                            Periodicity = BudgetPeriodicity.Monthly
                        };
                        db.Categories.Add(expCat);
                        catLookup[catKey] = expCat;
                        if (!newCategoriesCreated.Contains(catName))
                            newCategoriesCreated.Add(catName);
                    }

                    var tx = new Transaction
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = projectId,
                        CategoryId = expCat.Id,
                        ExecutedByUserId = assignedUser.Id,
                        Description = desc,
                        Amount = positiveAmount,
                        Date = date,
                        PaymentMethod = MapPaymentMethodKind(methodLabel),
                        PaymentMethodLabel = methodLabel
                    };
                    db.Transactions.Add(tx);
                    importedExpenses++;
                    totalExp += positiveAmount;
                }
                catch (Exception ex)
                {
                    warnings.Add($"שורה {lineNum} ב-CSV נכשלה: {ex.Message}");
                }
            }
        }
        else
        {
            return BadRequest(new { message = "פורמט קובץ לא נתמך. יש להעלות קובץ אקסל (.xlsx, .xls), מסמך PDF (.pdf) או קובץ .csv בלבד." });
        }

        // סנכרון וקישור עסקאות תשלומים ויצירת תשלומים עתידיים חסרים
        var pendingInstallments = db.Transactions.Local
            .Where(t => t.ProjectId == projectId && t.InstallmentsTotal.HasValue && t.InstallmentsTotal.Value > 1)
            .ToList();

        if (pendingInstallments.Count > 0)
        {
            var groups = pendingInstallments.GroupBy(t =>
            {
                var cleanDesc = System.Text.RegularExpressions.Regex.Replace(t.Description ?? "", @"\s*\(?תשלום\s+\d+\s+מתוך\s+\d+\)?|\s*\[\d+\/\d+\]", "").Trim();
                return (t.CategoryId, cleanDesc, t.InstallmentsTotal.Value);
            });

            foreach (var g in groups)
            {
                var existingGroup = g.FirstOrDefault(x => x.InstallmentGroupId.HasValue)?.InstallmentGroupId;
                var groupId = existingGroup ?? Guid.NewGuid();
                var items = g.OrderBy(x => x.InstallmentNumber ?? 1).ThenBy(x => x.Date).ToList();

                foreach (var item in items)
                {
                    item.InstallmentGroupId = groupId;
                }

                var maxInstNum = items.Max(x => x.InstallmentNumber ?? 1);
                var totalExpected = g.Key.Item3;
                if (maxInstNum < totalExpected && items.Count > 0)
                {
                    var lastItem = items.Last();
                    for (int nextNum = maxInstNum + 1; nextNum <= totalExpected; nextNum++)
                    {
                        var offsetMonths = nextNum - maxInstNum;
                        var futureDate = lastItem.Date.AddMonths(offsetMonths);
                        db.Transactions.Add(new Transaction
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = projectId,
                            CategoryId = lastItem.CategoryId,
                            ExecutedByUserId = lastItem.ExecutedByUserId,
                            Description = lastItem.Description,
                            Amount = lastItem.Amount,
                            Date = futureDate,
                            PaymentMethod = PaymentMethodKind.Installments,
                            PaymentMethodLabel = lastItem.PaymentMethodLabel,
                            InstallmentGroupId = groupId,
                            InstallmentNumber = nextNum,
                            InstallmentsTotal = totalExpected
                        });
                        importedExpenses++;
                    }
                }
            }
        }

        await db.SaveChangesAsync();

        return Ok(new ExcelImportResultDto(
            importedExpenses,
            importedIncomes,
            totalExp,
            totalInc,
            newCategoriesCreated,
            newPaymentMethodsAdded,
            warnings
        ));
    }
}
