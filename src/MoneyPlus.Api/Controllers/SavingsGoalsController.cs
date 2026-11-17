using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/savings-goals")]
public class SavingsGoalsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SavingsGoalsOverviewDto>> GetOverview(
        Guid projectId,
        [FromQuery] int? year,
        [FromQuery] int? month)
    {
        var overview = await SavingsGoalsService.GetOverviewAsync(db, projectId, year, month);
        return Ok(overview);
    }

    [HttpPost("simulate")]
    public ActionResult<SavingsSimulationResultDto> Simulate(Guid projectId, [FromBody] SavingsSimulationRequest req)
    {
        return Ok(SavingsGoalsService.RunSimulation(req));
    }

    [HttpPost]
    public async Task<ActionResult<SavingsGoalDto>> Create(Guid projectId, [FromBody] UpsertSavingsGoalRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("שם היעד נדרש");
        if (req.TargetAmount <= 0) return BadRequest("סכום היעד חייב להיות חיובי");
        if (req.TargetDate <= req.StartDate) return BadRequest("תאריך היעד חייב להיות מאוחר מתאריך ההתחלה");

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project is null) return NotFound("פרויקט לא נמצא");

        var icon = string.IsNullOrWhiteSpace(req.Icon) ? "🎯" : req.Icon.Trim();
        var color = string.IsNullOrWhiteSpace(req.Color) ? "#0E7490" : req.Color.Trim();

        Guid? linkedCategoryId = req.LinkedCategoryId;
        if (linkedCategoryId.HasValue)
        {
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == linkedCategoryId.Value);
            if (cat is null) return BadRequest("הקטגוריה שנבחרה אינה קיימת בפרויקט");
        }
        else
        {
            var existingCat = await db.Categories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Name == req.Name.Trim());
            if (existingCat is not null)
            {
                linkedCategoryId = existingCat.Id;
            }
            else
            {
                var newCat = new Category
                {
                    ProjectId = projectId,
                    Name = req.Name.Trim(),
                    Icon = icon,
                    Periodicity = BudgetPeriodicity.Monthly,
                    PlannedAmount = 0m,
                    IsCapitalTransfer = false,
                    AutoRollover = true,
                    Notes = "נוצר אוטומטית עבור יעד חיסכון"
                };
                db.Categories.Add(newCat);
                linkedCategoryId = newCat.Id;
            }
        }

        var goal = new SavingsGoal
        {
            ProjectId = projectId,
            Name = req.Name.Trim(),
            Icon = icon,
            Color = color,
            VehicleType = req.VehicleType,
            TargetAmount = req.TargetAmount,
            InitialAmount = Math.Max(0m, req.InitialAmount),
            AnnualInterestRate = Math.Max(0m, req.AnnualInterestRate),
            ManagementFeeAnnualPct = Math.Max(0m, req.ManagementFeeAnnualPct),
            DepositFeePct = Math.Max(0m, req.DepositFeePct),
            CapitalGainsTaxPct = Math.Max(0m, req.CapitalGainsTaxPct),
            StartDate = req.StartDate,
            TargetDate = req.TargetDate,
            LinkedCategoryId = linkedCategoryId,
            AutoSyncCategoryBudget = req.AutoSyncCategoryBudget,
            IsActive = req.IsActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SavingsGoals.Add(goal);
        await db.SaveChangesAsync();

        var cycleDay = project.CycleStartDay <= 0 ? 1 : Math.Clamp(project.CycleStartDay, 1, 28);
        var (curY, curM) = SavingsGoalsService.ResolveCycleForDate(DateOnly.FromDateTime(DateTime.Today), cycleDay);
        if (goal.AutoSyncCategoryBudget)
        {
            await SavingsGoalsService.SyncCategoryBudgetsForCycleAsync(db, projectId, curY, curM);
        }

        var overview = await SavingsGoalsService.GetOverviewAsync(db, projectId, curY, curM);
        var dto = overview.Goals.First(g => g.Id == goal.Id);
        return CreatedAtAction(nameof(GetOverview), new { projectId }, dto);
    }

    [HttpPut("{goalId:guid}")]
    public async Task<ActionResult<SavingsGoalDto>> Update(
        Guid projectId,
        Guid goalId,
        [FromBody] UpsertSavingsGoalRequest req)
    {
        var goal = await db.SavingsGoals.FirstOrDefaultAsync(g => g.ProjectId == projectId && g.Id == goalId);
        if (goal is null) return NotFound("יעד חיסכון לא נמצא");
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("שם היעד נדרש");
        if (req.TargetAmount <= 0) return BadRequest("סכום היעד חייב להיות חיובי");
        if (req.TargetDate <= req.StartDate) return BadRequest("תאריך היעד חייב להיות מאוחר מתאריך ההתחלה");

        goal.Name = req.Name.Trim();
        goal.Icon = string.IsNullOrWhiteSpace(req.Icon) ? goal.Icon : req.Icon.Trim();
        goal.Color = string.IsNullOrWhiteSpace(req.Color) ? goal.Color : req.Color.Trim();
        goal.VehicleType = req.VehicleType;
        goal.TargetAmount = req.TargetAmount;
        goal.InitialAmount = Math.Max(0m, req.InitialAmount);
        goal.AnnualInterestRate = Math.Max(0m, req.AnnualInterestRate);
        goal.ManagementFeeAnnualPct = Math.Max(0m, req.ManagementFeeAnnualPct);
        goal.DepositFeePct = Math.Max(0m, req.DepositFeePct);
        goal.CapitalGainsTaxPct = Math.Max(0m, req.CapitalGainsTaxPct);
        goal.StartDate = req.StartDate;
        goal.TargetDate = req.TargetDate;
        goal.AutoSyncCategoryBudget = req.AutoSyncCategoryBudget;
        goal.IsActive = req.IsActive;
        goal.UpdatedAt = DateTime.UtcNow;

        if (req.LinkedCategoryId.HasValue)
        {
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == req.LinkedCategoryId.Value);
            if (cat is not null)
            {
                goal.LinkedCategoryId = cat.Id;
            }
        }

        await db.SaveChangesAsync();

        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var (curY, curM) = SavingsGoalsService.ResolveCycleForDate(DateOnly.FromDateTime(DateTime.Today), cycleDay);
        if (goal.AutoSyncCategoryBudget)
        {
            await SavingsGoalsService.SyncCategoryBudgetsForCycleAsync(db, projectId, curY, curM);
        }

        var overview = await SavingsGoalsService.GetOverviewAsync(db, projectId, curY, curM);
        var dto = overview.Goals.First(g => g.Id == goal.Id);
        return Ok(dto);
    }

    [HttpDelete("{goalId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid goalId, [FromQuery] bool deleteHistory = false)
    {
        var goal = await db.SavingsGoals.FirstOrDefaultAsync(g => g.ProjectId == projectId && g.Id == goalId);
        if (goal is null) return NotFound();

        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var (curY, curM) = SavingsGoalsService.ResolveCycleForDate(DateOnly.FromDateTime(DateTime.Today), cycleDay);
        var (cycleFrom, _) = PeriodMath.Range(curY, curM, cycleDay);

        var hasPriorMonthDeposits = await db.Transactions.AnyAsync(t =>
            t.ProjectId == projectId &&
            t.Date < cycleFrom &&
            (t.SavingsGoalId == goalId ||
             (t.SavingsGoalId == null && goal.LinkedCategoryId.HasValue && t.CategoryId == goal.LinkedCategoryId.Value)));

        if (hasPriorMonthDeposits)
        {
            return BadRequest("לא ניתן למחוק יעד חיסכון שהוקצו לו סכומים מחודשים קודמים — ניתן להשעות אותו במקום.");
        }

        var linkedTxs = await db.Transactions
            .Where(t => t.ProjectId == projectId &&
                        (t.SavingsGoalId == goalId ||
                         (deleteHistory && t.SavingsGoalId == null && goal.LinkedCategoryId.HasValue && t.CategoryId == goal.LinkedCategoryId.Value)))
            .ToListAsync();

        if (deleteHistory)
        {
            db.Transactions.RemoveRange(linkedTxs);
        }
        else
        {
            foreach (var t in linkedTxs) t.SavingsGoalId = null;
        }

        db.SavingsGoals.Remove(goal);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{goalId:guid}/deposit")]
    public async Task<ActionResult<SavingsGoalDto>> QuickDeposit(
        Guid projectId,
        Guid goalId,
        [FromBody] QuickSavingsDepositRequest req)
    {
        var goal = await db.SavingsGoals
            .Include(g => g.LinkedCategory)
            .FirstOrDefaultAsync(g => g.ProjectId == projectId && g.Id == goalId);
        if (goal is null) return NotFound("יעד חיסכון לא נמצא");

        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var date = req.Date ?? DateOnly.FromDateTime(DateTime.Today);
        var (curY, curM) = SavingsGoalsService.ResolveCycleForDate(date, cycleDay);

        decimal amount;
        if (req.Amount.HasValue && req.Amount.Value > 0)
        {
            amount = req.Amount.Value;
        }
        else
        {
            var preOverview = await SavingsGoalsService.GetOverviewAsync(db, projectId, curY, curM);
            var currentDto = preOverview.Goals.First(g => g.Id == goalId);
            amount = currentDto.CurrentCycleRemaining > 0 ? currentDto.CurrentCycleRemaining : currentDto.CurrentCycleRequiredPmt;
        }

        if (amount <= 0) return BadRequest("סכום ההפקדה חייב להיות חיובי");

        Guid categoryId;
        if (goal.LinkedCategoryId.HasValue)
        {
            categoryId = goal.LinkedCategoryId.Value;
        }
        else
        {
            var cat = new Category
            {
                ProjectId = projectId,
                Name = goal.Name,
                Icon = goal.Icon,
                Periodicity = BudgetPeriodicity.Monthly,
                PlannedAmount = amount,
                IsCapitalTransfer = false,
                AutoRollover = true,
                Notes = "נוצר אוטומטית עבור יעד חיסכון"
            };
            db.Categories.Add(cat);
            await db.SaveChangesAsync();
            goal.LinkedCategoryId = cat.Id;
            categoryId = cat.Id;
        }

        var user = req.ExecutedByUserId.HasValue
            ? await db.ProjectUsers.FirstOrDefaultAsync(u => u.ProjectId == projectId && u.Id == req.ExecutedByUserId.Value)
            : null;
        user ??= await db.ProjectUsers.Where(u => u.ProjectId == projectId)
            .OrderByDescending(u => u.IsActiveDefault)
            .FirstOrDefaultAsync();
        if (user is null) return BadRequest("לא נמצא משתמש פעיל בפרויקט");

        var tx = new Transaction
        {
            ProjectId = projectId,
            CategoryId = categoryId,
            ExecutedByUserId = user.Id,
            Description = string.IsNullOrWhiteSpace(req.Description) ? $"הפקדה ליעד: {goal.Name}" : req.Description.Trim(),
            Amount = Math.Round(amount, 2),
            Date = date,
            PaymentMethod = req.PaymentMethod ?? PaymentMethodKind.BankTransfer,
            PaymentMethodLabel = string.IsNullOrWhiteSpace(req.PaymentMethodLabel) ? "העברה בנקאית" : req.PaymentMethodLabel.Trim(),
            SavingsGoalId = goal.Id
        };

        db.Transactions.Add(tx);
        await db.SaveChangesAsync();

        var overview = await SavingsGoalsService.GetOverviewAsync(db, projectId, curY, curM);
        return Ok(overview.Goals.First(g => g.Id == goalId));
    }
}

