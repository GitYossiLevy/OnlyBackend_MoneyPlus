using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Services;

public record CycleRolloverResultDto(
    bool Success,
    string From,
    string To,
    int CategoriesRolledOver,
    int IncomeCategoriesRolledOver,
    string Message
);

public static class CycleRolloverService
{
    public static async Task<CycleRolloverResultDto> ExecuteRolloverAsync(
        AppDbContext db,
        Guid projectId,
        int? fromYear,
        int? fromMonth,
        int? toYear,
        int? toMonth,
        ILogger? logger = null)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project is null)
        {
            return new CycleRolloverResultDto(false, "", "", 0, 0, "פרויקט לא נמצא");
        }

        var cycleDay = project.CycleStartDay <= 0 ? 1 : Math.Clamp(project.CycleStartDay, 1, 28);
        var today = DateTime.Today;

        int fY = fromYear ?? today.Year;
        int fM = fromMonth ?? today.Month;

        if (!fromYear.HasValue || !fromMonth.HasValue)
        {
            if (today.Day < cycleDay)
            {
                if (today.Month == 1) { fY = today.Year - 1; fM = 12; }
                else { fY = today.Year; fM = today.Month - 1; }
            }
            else
            {
                fY = today.Year;
                fM = today.Month;
            }
        }

        int tY = toYear ?? (fM == 12 ? fY + 1 : fY);
        int tM = toMonth ?? (fM == 12 ? 1 : fM + 1);

        var fromKey = $"{fM:D2}/{fY}";
        var toKey = $"{tM:D2}/{tY}";

        // 1. גלגול קטגוריות הוצאה
        var expCats = await db.Categories.Where(c => c.ProjectId == projectId).ToListAsync();
        var expProjMax = BudgetRolloverHelper.FindMaxMonth(expCats.Select(c => c.GetMonthlyBudgets()));
        int expCount = 0;

        foreach (var cat in expCats)
        {
            var plannedInFromMonth = cat.GetPlannedForMonth(fY, fM, expProjMax);
            if (plannedInFromMonth > 0)
            {
                var budgets = cat.GetMonthlyBudgets();
                budgets[toKey] = plannedInFromMonth;
                cat.SetMonthlyBudgets(budgets);
                expCount++;
            }
        }

        // 2. גלגול קטגוריות הכנסה
        var incCats = await db.IncomeCategories.Where(c => c.ProjectId == projectId).ToListAsync();
        var incProjMax = BudgetRolloverHelper.FindMaxMonth(incCats.Select(c => c.GetMonthlyBudgets()));
        int incCount = 0;

        foreach (var cat in incCats)
        {
            var plannedInFromMonth = cat.GetPlannedForMonth(fY, fM, incProjMax);
            if (plannedInFromMonth > 0)
            {
                var budgets = cat.GetMonthlyBudgets();
                budgets[toKey] = plannedInFromMonth;
                cat.SetMonthlyBudgets(budgets);
                incCount++;
            }
        }

        await db.SaveChangesAsync();

        // סנכרון חכם של יעדי החיסכון למחזור החדש: חישוב מחדש של ה-PMT הנדרש ועדכון תקציב הקטגוריות המקושרות
        await SavingsGoalsService.SyncCategoryBudgetsForCycleAsync(db, projectId, tY, tM);

        logger?.LogInformation(
            "CycleRolloverService: Successfully rolled over project '{Name}' ({Id}) from {From} to {To}: {ExpCount} expenses, {IncCount} incomes",
            project.Name, projectId, fromKey, toKey, expCount, incCount);

        return new CycleRolloverResultDto(
            true,
            fromKey,
            toKey,
            expCount,
            incCount,
            $"סנכרון ותכנון חודש {toKey} בוצע בהצלחה: גולגלו {expCount} קטגוריות הוצאה ו-{incCount} קטגוריות הכנסה מחודש {fromKey}."
        );
    }
}

