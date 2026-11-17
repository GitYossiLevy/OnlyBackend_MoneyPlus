using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

/// <summary>דו"ח מאזן, התאמת עו"ש וסגירת תקופה.</summary>
[ApiController]
[Route("api/projects/{projectId:guid}/report")]
public class ReportController(AppDbContext db) : ControllerBase
{
    private async Task<PeriodSummaryDto> BuildSummary(Guid projectId, int year, int month)
    {
        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var (from, to) = PeriodMath.Range(year, month, cycleDay);
        var expCats = await db.Categories.AsNoTracking().Where(c => c.ProjectId == projectId).ToListAsync();
        var expProjMax = BudgetRolloverHelper.FindMaxMonth(expCats.Select(c => c.GetMonthlyBudgets()));

        // הפרדה בין קטגוריות הוצאה תפעולית לבין העברות הוניות (חיסכון/השקעות)
        var opCats = expCats.Where(c => !c.IsCapitalTransfer).ToList();
        var capCats = expCats.Where(c => c.IsCapitalTransfer).ToList();
        var capCatIds = capCats.Select(c => c.Id).ToHashSet();

        var plannedExp = opCats.Sum(c => c.GetPlannedForMonth(year, month, expProjMax));
        var plannedCapital = capCats.Sum(c => c.GetPlannedForMonth(year, month, expProjMax));

        var monthTx = await db.Transactions.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.Date >= from && t.Date <= to)
            .Select(t => new { t.CategoryId, t.Amount })
            .ToListAsync();
        var actualExp = monthTx.Where(t => !capCatIds.Contains(t.CategoryId)).Sum(t => t.Amount);
        var actualCapital = monthTx.Where(t => capCatIds.Contains(t.CategoryId)).Sum(t => t.Amount);

        var incomeCats = await db.IncomeCategories.AsNoTracking().Where(c => c.ProjectId == projectId).ToListAsync();
        var incProjMax = BudgetRolloverHelper.FindMaxMonth(incomeCats.Select(c => c.GetMonthlyBudgets()));
        var incomes = await db.Incomes.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.Date >= from && i.Date <= to)
            .Select(i => new { i.IncomeCategoryId, i.ActualAmount })
            .ToListAsync();

        var activeIncomeCatIds = incomes.Select(i => i.IncomeCategoryId).ToHashSet();
        decimal plannedInc = 0;
        foreach (var cat in incomeCats)
        {
            if (cat.IsActiveInMonth(year, month, activeIncomeCatIds.Contains(cat.Id), incProjMax))
            {
                plannedInc += cat.GetPlannedForMonth(year, month, incProjMax);
            }
        }
        var actualInc = incomes.Sum(i => i.ActualAmount);

        var plannedBal = plannedInc - plannedExp;
        var operatingBal = actualInc - actualExp; // עודף תפעולי שוטף (הכנסות מול הוצאות צריכה)
        var actualBal = operatingBal - actualCapital; // שינוי נטו בעו"ש (מפחית הוצאות צריכה והעברות לחיסכון)
        var balGap = actualBal - plannedBal;
        var savings = actualInc > 0 ? Math.Round(operatingBal / actualInc * 100, 1) : 0;

        return new PeriodSummaryDto(year, month, plannedExp, actualExp, plannedExp - actualExp,
            plannedInc, actualInc, actualInc - plannedInc, plannedBal, actualBal, balGap, savings,
            actualCapital, plannedCapital, operatingBal);
    }

    [HttpGet("summary")]
    public Task<PeriodSummaryDto> Summary(Guid projectId, [FromQuery] int year, [FromQuery] int month) => BuildSummary(projectId, year, month);

    /// <summary>מבט על שנתי: סיכום 12 חודשים עם יתרה נטו (+ או -), הכנסות, הוצאות וממוצעים.</summary>
    [HttpGet("annual-overview")]
    public async Task<AnnualOverviewDto> AnnualOverview(Guid projectId, [FromQuery] int year)
    {
        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var monthsList = new List<MonthOverviewDto>();
        var monthNames = new[] { "", "ינואר", "פברואר", "מרץ", "אפריל", "מאי", "יוני", "יולי", "אוגוסט", "ספטמבר", "אוקטובר", "נובמבר", "דצמבר" };

        var expCats = await db.Categories.AsNoTracking().Where(c => c.ProjectId == projectId).ToListAsync();
        var expProjMax = BudgetRolloverHelper.FindMaxMonth(expCats.Select(c => c.GetMonthlyBudgets()));
        var opCats = expCats.Where(c => !c.IsCapitalTransfer).ToList();
        var capCats = expCats.Where(c => c.IsCapitalTransfer).ToList();
        var capCatIds = capCats.Select(c => c.Id).ToHashSet();

        var incomeCats = await db.IncomeCategories.AsNoTracking().Where(c => c.ProjectId == projectId).ToListAsync();
        var incProjMax = BudgetRolloverHelper.FindMaxMonth(incomeCats.Select(c => c.GetMonthlyBudgets()));

        var (yearStart, _) = PeriodMath.Range(year, 1, cycleDay);
        var (_, yearEnd) = PeriodMath.Range(year, 12, cycleDay);

        var allYearTx = await db.Transactions.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.Date >= yearStart && t.Date <= yearEnd)
            .Select(t => new { t.CategoryId, t.Amount, t.Date })
            .ToListAsync();

        var allYearIncomes = await db.Incomes.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.Date >= yearStart && i.Date <= yearEnd)
            .Select(i => new { i.IncomeCategoryId, i.ActualAmount, i.Date })
            .ToListAsync();

        decimal totalPlannedInc = 0;
        decimal totalActualInc = 0;
        decimal totalPlannedExp = 0;
        decimal totalActualExp = 0;
        decimal totalActualCapital = 0;

        for (int m = 1; m <= 12; m++)
        {
            var (from, to) = PeriodMath.Range(year, m, cycleDay);
            var mTx = allYearTx.Where(t => t.Date >= from && t.Date <= to).ToList();
            var mInc = allYearIncomes.Where(i => i.Date >= from && i.Date <= to).ToList();

            var plannedExpMonth = opCats.Sum(c => c.GetPlannedForMonth(year, m, expProjMax));
            var actualExp = mTx.Where(t => !capCatIds.Contains(t.CategoryId)).Sum(t => t.Amount);
            var actualCapital = mTx.Where(t => capCatIds.Contains(t.CategoryId)).Sum(t => t.Amount);

            var activeIncomeCatIdsMonth = mInc.Select(i => i.IncomeCategoryId).ToHashSet();
            decimal plannedInc = 0;
            foreach (var cat in incomeCats)
            {
                if (cat.IsActiveInMonth(year, m, activeIncomeCatIdsMonth.Contains(cat.Id), incProjMax))
                {
                    plannedInc += cat.GetPlannedForMonth(year, m, incProjMax);
                }
            }
            var actualInc = mInc.Sum(i => i.ActualAmount);

            var plannedBal = plannedInc - plannedExpMonth;
            var operatingBal = actualInc - actualExp;
            var actualBal = operatingBal - actualCapital;
            var balGap = actualBal - plannedBal;
            var savings = actualInc > 0 ? Math.Round(operatingBal / actualInc * 100, 1) : 0;

            totalPlannedInc += plannedInc;
            totalActualInc += actualInc;
            totalPlannedExp += plannedExpMonth;
            totalActualExp += actualExp;
            totalActualCapital += actualCapital;

            monthsList.Add(new MonthOverviewDto(
                m,
                monthNames[m],
                plannedInc,
                actualInc,
                plannedExpMonth,
                actualExp,
                plannedBal,
                actualBal,
                balGap,
                savings,
                actualCapital
            ));
        }

        var totalPlannedBal = totalPlannedInc - totalPlannedExp;
        var totalActualBal = totalActualInc - (totalActualExp + totalActualCapital);
        var avgMonthlyBal = Math.Round(totalActualBal / 12, 1);
        var annualSavingsRate = totalActualInc > 0 ? Math.Round((totalActualInc - totalActualExp) / totalActualInc * 100, 1) : 0;
        var posCount = monthsList.Count(m => m.ActualBalance >= 0);
        var negCount = monthsList.Count(m => m.ActualBalance < 0);

        return new AnnualOverviewDto(
            year,
            totalPlannedInc,
            totalActualInc,
            totalPlannedExp,
            totalActualExp,
            totalPlannedBal,
            totalActualBal,
            avgMonthlyBal,
            annualSavingsRate,
            posCount,
            negCount,
            monthsList,
            totalActualCapital
        );
    }

    /// <summary>התאמת עו"ש: יתרה צפויה = יתרת פתיחה + הכנסות − הוצאות. הנתון מוזן רק במסך הדו"ח.</summary>
    [HttpGet("reconcile")]
    public async Task<ReconciliationDto> Reconcile(Guid projectId, [FromQuery] int year, [FromQuery] int month,
        [FromQuery] decimal openingBalance, [FromQuery] decimal bankBalance)
    {
        var s = await BuildSummary(projectId, year, month);
        var expected = openingBalance + s.ActualBalance;
        var gap = bankBalance - expected;
        return new ReconciliationDto(expected, bankBalance, gap, gap == 0);
    }

    public record ClosePeriodRequest(int Year, int Month, decimal? BankBalance, decimal OpeningBalance);

    /// <summary>סיכום תקופה: הפקת תובנות, התאמת עו"ש ושמירת snapshot ללא שום חסימה או נעילת תכנון עתידי.</summary>
    [HttpPost("close")]
    public async Task<IActionResult> Close(Guid projectId, ClosePeriodRequest r)
    {
        var s = await BuildSummary(projectId, r.Year, r.Month);
        decimal? gap = r.BankBalance is { } bank ? bank - (r.OpeningBalance + s.ActualBalance) : null;

        var existing = await db.PeriodClosings.FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Year == r.Year && p.Month == r.Month);
        if (existing is not null)
        {
            existing.TotalIncome = s.ActualIncome;
            existing.TotalExpenses = s.ActualExpenses;
            existing.BankBalanceEntered = r.BankBalance;
            existing.ReconciliationGap = gap;
            existing.ClosedAt = DateTime.UtcNow;
        }
        else
        {
            db.PeriodClosings.Add(new PeriodClosing
            {
                ProjectId = projectId, Year = r.Year, Month = r.Month,
                TotalIncome = s.ActualIncome, TotalExpenses = s.ActualExpenses,
                BankBalanceEntered = r.BankBalance, ReconciliationGap = gap
            });
        }

        await db.SaveChangesAsync();
        return Ok(new { message = "חודש סוכם בהצלחה", savedToSavings = s.ActualBalance, gap });
    }
}
