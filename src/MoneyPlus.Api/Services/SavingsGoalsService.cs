using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Controllers;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Services;

public static class SavingsGoalsService
{
    public sealed record SimState(
        decimal GrossFinal,
        decimal NetFinal,
        decimal TotalPrincipal,
        decimal TotalGrossInterest,
        decimal TotalManagementFees,
        decimal TotalDepositFees,
        decimal EstimatedTax,
        List<SavingsYearlyBreakdownDto> YearlyBreakdown,
        List<SavingsSchedulePointDto> Trajectory
    );

    public static int MonthsBetween(DateOnly start, DateOnly target)
    {
        var m = (target.Year - start.Year) * 12 + (target.Month - start.Month);
        return Math.Max(1, m);
    }

    public static (int Year, int Month) ResolveCycleForDate(DateOnly date, int cycleStartDay)
    {
        var d = Math.Clamp(cycleStartDay <= 0 ? 1 : cycleStartDay, 1, 28);
        if (date.Day < d)
        {
            return date.Month == 1 ? (date.Year - 1, 12) : (date.Year, date.Month - 1);
        }
        return (date.Year, date.Month);
    }

    public static SimState Simulate(
        decimal initialAmount,
        decimal monthlyDeposit,
        int months,
        decimal annualInterestRate,
        decimal managementFeeAnnualPct,
        decimal depositFeePct,
        decimal capitalGainsTaxPct,
        decimal? alreadyInvestedPrincipalOverride = null,
        DateOnly? startCycleDate = null)
    {
        var n = Math.Max(1, months);
        var monthlyInterestRate = annualInterestRate / 100m / 12m;
        var monthlyMgmtRate = managementFeeAnnualPct / 100m / 12m;
        var depFeeRate = Math.Clamp(depositFeePct, 0m, 99m) / 100m;
        var taxRate = Math.Clamp(capitalGainsTaxPct, 0m, 99m) / 100m;

        var balance = Math.Max(0m, initialAmount);
        var principal = alreadyInvestedPrincipalOverride ?? Math.Max(0m, initialAmount);
        var totalInterest = 0m;
        var totalMgmtFees = 0m;
        var totalDepositFees = 0m;

        var yearly = new List<SavingsYearlyBreakdownDto>();
        var trajectory = new List<SavingsSchedulePointDto>();

        var baseDate = startCycleDate ?? DateOnly.FromDateTime(DateTime.Today);
        trajectory.Add(new SavingsSchedulePointDto(
            0,
            $"{baseDate.Month:D2}/{baseDate.Year}",
            Math.Round(balance, 2),
            null,
            Math.Round(principal, 2),
            0m,
            0m,
            0m
        ));

        var yearInterest = 0m;
        var yearMgmtFees = 0m;

        for (var k = 1; k <= n; k++)
        {
            var interest = balance * monthlyInterestRate;
            var mgmtFee = balance * monthlyMgmtRate;
            var depFee = monthlyDeposit * depFeeRate;
            var netDeposit = monthlyDeposit - depFee;

            balance = balance + interest - mgmtFee + netDeposit;
            principal += monthlyDeposit;
            totalInterest += interest;
            totalMgmtFees += mgmtFee;
            totalDepositFees += depFee;

            yearInterest += interest;
            yearMgmtFees += mgmtFee;

            var profit = Math.Max(0m, balance - principal);
            var tax = profit * taxRate;
            var netBalance = balance - tax;

            var pointDate = baseDate.AddMonths(k - 1);
            trajectory.Add(new SavingsSchedulePointDto(
                k,
                $"{pointDate.Month:D2}/{pointDate.Year}",
                Math.Round(netBalance, 2),
                null,
                Math.Round(principal, 2),
                Math.Round(totalInterest, 2),
                Math.Round(totalMgmtFees + totalDepositFees, 2),
                Math.Round(tax, 2)
            ));

            if (k % 12 == 0 || k == n)
            {
                yearly.Add(new SavingsYearlyBreakdownDto(
                    (k - 1) / 12 + 1,
                    Math.Round(principal, 2),
                    Math.Round(yearInterest, 2),
                    Math.Round(yearMgmtFees, 2),
                    Math.Round(tax, 2),
                    Math.Round(netBalance, 2)
                ));
                yearInterest = 0m;
                yearMgmtFees = 0m;
            }
        }

        var finalProfit = Math.Max(0m, balance - principal);
        var finalTax = finalProfit * taxRate;
        var finalNet = balance - finalTax;

        return new SimState(
            Math.Round(balance, 2),
            Math.Round(finalNet, 2),
            Math.Round(principal, 2),
            Math.Round(totalInterest, 2),
            Math.Round(totalMgmtFees, 2),
            Math.Round(totalDepositFees, 2),
            Math.Round(finalTax, 2),
            yearly,
            trajectory
        );
    }

    public static decimal SolveRequiredMonthlyDeposit(
        decimal targetNetAmount,
        decimal initialAmount,
        int months,
        decimal annualInterestRate,
        decimal managementFeeAnnualPct,
        decimal depositFeePct,
        decimal capitalGainsTaxPct,
        decimal? alreadyInvestedPrincipalOverride = null)
    {
        if (targetNetAmount <= 0m || months <= 0) return 0m;

        var zeroSim = Simulate(
            initialAmount, 0m, months,
            annualInterestRate, managementFeeAnnualPct, depositFeePct, capitalGainsTaxPct,
            alreadyInvestedPrincipalOverride);
        if (zeroSim.NetFinal >= targetNetAmount) return 0m;

        decimal lo = 0m;
        decimal hi = Math.Max(100m, (targetNetAmount - initialAmount) / Math.Max(1, months) * 3m + 1000m);

        // Expand upper bound if fees/taxes make hi insufficient
        for (var i = 0; i < 12; i++)
        {
            var check = Simulate(initialAmount, hi, months, annualInterestRate, managementFeeAnnualPct, depositFeePct, capitalGainsTaxPct, alreadyInvestedPrincipalOverride);
            if (check.NetFinal >= targetNetAmount) break;
            hi *= 2m;
        }

        for (var iter = 0; iter < 48; iter++)
        {
            var mid = (lo + hi) / 2m;
            var sim = Simulate(initialAmount, mid, months, annualInterestRate, managementFeeAnnualPct, depositFeePct, capitalGainsTaxPct, alreadyInvestedPrincipalOverride);
            if (sim.NetFinal >= targetNetAmount) hi = mid;
            else lo = mid;
        }

        return Math.Round(hi, 2);
    }

    public static SavingsSimulationResultDto RunSimulation(SavingsSimulationRequest req)
    {
        var months = Math.Clamp(req.Months, 1, 600);
        var isTargetMode = !string.Equals(req.Mode, "PmtToTarget", StringComparison.OrdinalIgnoreCase);

        var pmt = isTargetMode
            ? SolveRequiredMonthlyDeposit(
                req.TargetAmount, req.InitialAmount, months,
                req.AnnualInterestRate, req.ManagementFeeAnnualPct, req.DepositFeePct, req.CapitalGainsTaxPct)
            : Math.Max(0m, req.MonthlyDeposit);

        var sim = Simulate(
            req.InitialAmount, pmt, months,
            req.AnnualInterestRate, req.ManagementFeeAnnualPct, req.DepositFeePct, req.CapitalGainsTaxPct);

        return new SavingsSimulationResultDto(
            pmt,
            sim.NetFinal,
            sim.GrossFinal,
            sim.TotalPrincipal,
            sim.TotalGrossInterest,
            sim.TotalManagementFees,
            sim.TotalDepositFees,
            sim.EstimatedTax,
            sim.YearlyBreakdown,
            sim.Trajectory
        );
    }

    public static async Task<SavingsGoalsOverviewDto> GetOverviewAsync(
        AppDbContext db,
        Guid projectId,
        int? year,
        int? month)
    {
        var cycleDay = await PeriodMath.GetCycleStartDayAsync(db, projectId);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (curY, curM) = year.HasValue && month.HasValue
            ? (year.Value, month.Value)
            : ResolveCycleForDate(today, cycleDay);

        var (cycleFrom, cycleTo) = PeriodMath.Range(curY, curM, cycleDay);

        var goals = await db.SavingsGoals
            .AsNoTracking()
            .Include(g => g.LinkedCategory)
            .Where(g => g.ProjectId == projectId)
            .OrderByDescending(g => g.IsActive)
            .ThenBy(g => g.TargetDate)
            .ToListAsync();

        var goalCatIds = goals.Where(g => g.LinkedCategoryId.HasValue).Select(g => g.LinkedCategoryId!.Value).Distinct().ToList();
        var goalIds = goals.Select(g => g.Id).ToList();

        var txs = await db.Transactions
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId &&
                        ((t.SavingsGoalId.HasValue && goalIds.Contains(t.SavingsGoalId.Value)) ||
                         (t.SavingsGoalId == null && goalCatIds.Contains(t.CategoryId))))
            .ToListAsync();

        var dtos = new List<SavingsGoalDto>();
        foreach (var g in goals)
        {
            var goalTxs = txs.Where(t =>
                t.SavingsGoalId == g.Id ||
                (t.SavingsGoalId == null && g.LinkedCategoryId.HasValue && t.CategoryId == g.LinkedCategoryId.Value)
            ).ToList();

            dtos.Add(BuildGoalDto(g, goalTxs, curY, curM, cycleDay, cycleFrom, cycleTo));
        }

        var activeDtos = dtos.Where(d => d.IsActive).ToList();
        var totalReq = activeDtos.Sum(d => d.CurrentCycleRequiredPmt);
        var totalDep = activeDtos.Sum(d => d.CurrentCycleDeposited);
        var totalNet = dtos.Sum(d => d.ActualBalanceNet);
        var totalTarget = activeDtos.Sum(d => d.TargetAmount);

        string overallStatus;
        string overallMsg;
        if (activeDtos.Count == 0)
        {
            overallStatus = "OnTrack";
            overallMsg = "טרם הוגדרו יעדי חיסכון בפרויקט זה. ניתן להגדיר יעד חדש בהגדרות החיסכון.";
        }
        else if (activeDtos.Any(d => d.PaceStatus == "Behind"))
        {
            var behindCount = activeDtos.Count(d => d.PaceStatus == "Behind");
            overallStatus = "Behind";
            overallMsg = $"שים לב: {behindCount} יעדי חיסכון בפיגור מצטבר — הסכום החודשי המומלץ עודכן אוטומטית.";
        }
        else if (totalReq > 0 && totalDep + 1m < totalReq)
        {
            overallStatus = "CyclePending";
            overallMsg = $"נותרו ₪{(totalReq - totalDep):N0} להפקדה במחזור {curM:D2}/{curY} כדי לעמוד בכל היעדים.";
        }
        else
        {
            overallStatus = "OnTrack";
            overallMsg = $"הכל מצוין! חוסכים בקצב טוב לכל {activeDtos.Count} היעדים הפעילים.";
        }

        return new SavingsGoalsOverviewDto(
            curY,
            curM,
            activeDtos.Count,
            Math.Round(totalReq, 2),
            Math.Round(totalDep, 2),
            Math.Round(totalNet, 2),
            Math.Round(totalTarget, 2),
            overallStatus,
            overallMsg,
            dtos
        );
    }

    public static SavingsGoalDto BuildGoalDto(
        SavingsGoal g,
        List<Transaction> goalTxs,
        int curY,
        int curM,
        int cycleDay,
        DateOnly cycleFrom,
        DateOnly cycleTo)
    {
        var (startY, startM) = ResolveCycleForDate(g.StartDate, cycleDay);
        var (targetY, targetM) = ResolveCycleForDate(g.TargetDate, cycleDay);

        var totalMonths = Math.Max(1, (targetY - startY) * 12 + (targetM - startM));
        var elapsedBeforeCurrent = Math.Clamp((curY - startY) * 12 + (curM - startM), 0, totalMonths);
        var remainingMonths = Math.Max(1, totalMonths - elapsedBeforeCurrent);

        var originalPmt = SolveRequiredMonthlyDeposit(
            g.TargetAmount, g.InitialAmount, totalMonths,
            g.AnnualInterestRate, g.ManagementFeeAnnualPct, g.DepositFeePct, g.CapitalGainsTaxPct);

        var plannedSim = Simulate(
            g.InitialAmount, originalPmt, totalMonths,
            g.AnnualInterestRate, g.ManagementFeeAnnualPct, g.DepositFeePct, g.CapitalGainsTaxPct,
            null, new DateOnly(startY, startM, Math.Clamp(cycleDay, 1, 28)));

        // Group actual deposits by cycle index relative to startY/startM
        var depositsByCycle = new Dictionary<int, decimal>();
        foreach (var t in goalTxs)
        {
            var (ty, tm) = ResolveCycleForDate(t.Date, cycleDay);
            var idx = (ty - startY) * 12 + (tm - startM);
            depositsByCycle[idx] = depositsByCycle.GetValueOrDefault(idx) + t.Amount;
        }

        // Simulate actual balance up to start of current cycle
        var monthlyInterestRate = g.AnnualInterestRate / 100m / 12m;
        var monthlyMgmtRate = g.ManagementFeeAnnualPct / 100m / 12m;
        var depFeeRate = Math.Clamp(g.DepositFeePct, 0m, 99m) / 100m;
        var taxRate = Math.Clamp(g.CapitalGainsTaxPct, 0m, 99m) / 100m;

        var actualGross = Math.Max(0m, g.InitialAmount);
        var actualPrincipal = Math.Max(0m, g.InitialAmount);
        var actualTrajectoryByMonth = new Dictionary<int, decimal> { [0] = Math.Round(actualGross, 2) };
        var actualDepositByMonth = new Dictionary<int, decimal> { [0] = Math.Round(actualGross, 2) };

        for (var k = 1; k <= elapsedBeforeCurrent; k++)
        {
            var dep = depositsByCycle.GetValueOrDefault(k - 1);
            var interest = actualGross * monthlyInterestRate;
            var mgmt = actualGross * monthlyMgmtRate;
            var netDep = dep * (1m - depFeeRate);
            actualGross = actualGross + interest - mgmt + netDep;
            actualPrincipal += dep;

            var profit = Math.Max(0m, actualGross - actualPrincipal);
            var netAtK = actualGross - profit * taxRate;
            actualTrajectoryByMonth[k] = Math.Round(netAtK, 2);
            actualDepositByMonth[k] = Math.Round(dep, 2);
        }

        // Required PMT from this cycle onward based on actual balance at start of current cycle
        var currentCycleRequiredPmt = SolveRequiredMonthlyDeposit(
            g.TargetAmount, actualGross, remainingMonths,
            g.AnnualInterestRate, g.ManagementFeeAnnualPct, g.DepositFeePct, g.CapitalGainsTaxPct,
            actualPrincipal);

        // Deposits already made in the current cycle
        var currentCycleDeposited = goalTxs
            .Where(t => t.Date >= cycleFrom && t.Date <= cycleTo)
            .Sum(t => t.Amount);

        // Actual balance including current cycle deposits so far
        var currentGrossNow = actualGross;
        var currentPrincipalNow = actualPrincipal + currentCycleDeposited;
        if (currentCycleDeposited > 0)
        {
            currentGrossNow += currentCycleDeposited * (1m - depFeeRate);
        }
        var currentProfitNow = Math.Max(0m, currentGrossNow - currentPrincipalNow);
        var actualBalanceNet = Math.Round(currentGrossNow - currentProfitNow * taxRate, 2);

        // Include current cycle actual point in trajectory
        var currentCyclePointIdx = elapsedBeforeCurrent + 1;
        if (currentCyclePointIdx <= totalMonths)
        {
            actualTrajectoryByMonth[currentCyclePointIdx] = actualBalanceNet;
            actualDepositByMonth[currentCyclePointIdx] = Math.Round(currentCycleDeposited, 2);
        }

        // Next cycle projected PMT if nothing more is deposited this cycle
        decimal nextCycleProjectedPmtIfZero;
        var afterThisCycleGross = currentGrossNow + currentGrossNow * (monthlyInterestRate - monthlyMgmtRate);
        if (remainingMonths <= 1)
        {
            nextCycleProjectedPmtIfZero = Math.Max(0m, Math.Round(g.TargetAmount - actualBalanceNet, 2));
        }
        else
        {
            nextCycleProjectedPmtIfZero = SolveRequiredMonthlyDeposit(
                g.TargetAmount, afterThisCycleGross, remainingMonths - 1,
                g.AnnualInterestRate, g.ManagementFeeAnnualPct, g.DepositFeePct, g.CapitalGainsTaxPct,
                currentPrincipalNow);
        }

        // Adjusted trajectory from current cycle forward using recalculated nextCycleProjectedPmtIfZero
        var adjustedBalanceByMonth = new Dictionary<int, decimal>();
        var adjustedDepositByMonth = new Dictionary<int, decimal>();
        foreach (var kvp in actualTrajectoryByMonth)
            adjustedBalanceByMonth[kvp.Key] = kvp.Value;
        foreach (var kvp in actualDepositByMonth)
            adjustedDepositByMonth[kvp.Key] = kvp.Value;

        if (remainingMonths > 1 && currentCyclePointIdx < totalMonths)
        {
            var futureSim = Simulate(
                afterThisCycleGross, nextCycleProjectedPmtIfZero, remainingMonths - 1,
                g.AnnualInterestRate, g.ManagementFeeAnnualPct, g.DepositFeePct, g.CapitalGainsTaxPct,
                currentPrincipalNow);
            for (var i = 1; i < futureSim.Trajectory.Count; i++)
            {
                var targetIdx = currentCyclePointIdx + i;
                if (targetIdx <= totalMonths)
                {
                    adjustedBalanceByMonth[targetIdx] = futureSim.Trajectory[i].PlannedBalanceNet;
                    adjustedDepositByMonth[targetIdx] = nextCycleProjectedPmtIfZero;
                }
            }
        }

        var expectedPointIdx = Math.Clamp(elapsedBeforeCurrent, 0, plannedSim.Trajectory.Count - 1);
        var expectedToDateNet = plannedSim.Trajectory[expectedPointIdx].PlannedBalanceNet;
        var paceDelta = Math.Round(actualBalanceNet - expectedToDateNet, 2);
        var currentCycleRemaining = Math.Max(0m, Math.Round(currentCycleRequiredPmt - currentCycleDeposited, 2));

        // Pace projection to end of target date if user continues at currentCycleRequiredPmt
        var forwardSim = Simulate(
            actualGross, currentCycleRequiredPmt, remainingMonths,
            g.AnnualInterestRate, g.ManagementFeeAnnualPct, g.DepositFeePct, g.CapitalGainsTaxPct,
            actualPrincipal);

        var totalDepositedActual = Math.Round(goalTxs.Sum(t => t.Amount), 2);
        var progressPct = g.TargetAmount > 0
            ? (int)Math.Clamp(Math.Round(actualBalanceNet / g.TargetAmount * 100m), 0, 100)
            : 100;

        string paceStatus;
        string statusMsg;
        if (actualBalanceNet >= g.TargetAmount)
        {
            paceStatus = "Completed";
            statusMsg = "היעד הושלם במלואו! כל הכבוד.";
        }
        else if (paceDelta < -Math.Max(50m, originalPmt * 0.15m) && elapsedBeforeCurrent > 0)
        {
            paceStatus = "Behind";
            statusMsg = $"חריגה של ₪{Math.Abs(paceDelta):N0} מהקצב המקורי — הסכום המומלץ עודכן ל-₪{currentCycleRequiredPmt:N0}/חודש.";
        }
        else if (currentCycleRemaining > 1m)
        {
            paceStatus = "CyclePending";
            statusMsg = currentCycleDeposited > 0
                ? $"הופקדו ₪{currentCycleDeposited:N0} מתוך ₪{currentCycleRequiredPmt:N0} במחזור {curM:D2}/{curY} (נותרו ₪{currentCycleRemaining:N0}). ללא הפקדה נוספת החודש, החודשים הבאים יעודכנו ל-₪{nextCycleProjectedPmtIfZero:N0}."
                : $"נותרו ₪{currentCycleRemaining:N0} להפקדה במחזור {curM:D2}/{curY} כדי לשמור על הקצב (ללא הפקדה, החודשים הבאים יעלו ל-₪{nextCycleProjectedPmtIfZero:N0}).";
        }
        else if (paceDelta > Math.Max(50m, originalPmt * 0.1m) || nextCycleProjectedPmtIfZero + 1m < originalPmt)
        {
            paceStatus = "Ahead";
            var displayNewPmt = remainingMonths > 1 ? nextCycleProjectedPmtIfZero : currentCycleRequiredPmt;
            statusMsg = $"מקדימים את היעד ב-₪{Math.Max(0m, paceDelta):N0}! ההפקדה החודשית הנדרשת ירדה ל-₪{displayNewPmt:N0}/חודש.";
        }
        else
        {
            paceStatus = "OnTrack";
            statusMsg = "הכל טוב! חוסכים בקצב מדויק ליעד.";
        }

        // Merge actual + adjusted trajectory points onto planned trajectory
        var mergedTrajectory = plannedSim.Trajectory.Select(pt => new SavingsSchedulePointDto(
            pt.MonthIndex,
            pt.Label,
            pt.PlannedBalanceNet,
            actualTrajectoryByMonth.TryGetValue(pt.MonthIndex, out var actVal) ? actVal : null,
            pt.PrincipalAccumulated,
            pt.GrossInterestAccumulated,
            pt.FeesAccumulated,
            pt.TaxEstimated,
            adjustedBalanceByMonth.TryGetValue(pt.MonthIndex, out var adjBal) ? adjBal : null,
            adjustedDepositByMonth.TryGetValue(pt.MonthIndex, out var adjDep) ? adjDep : null,
            actualDepositByMonth.TryGetValue(pt.MonthIndex, out var actDep) ? actDep : null
        )).ToList();

        var adjustedDepositsList = Enumerable.Range(1, totalMonths)
            .Select(k => adjustedDepositByMonth.TryGetValue(k, out var adjD)
                ? adjD
                : actualDepositByMonth.GetValueOrDefault(k, 0m))
            .ToList();
        var adjustedYearlyBreakdown = BuildYearlyBreakdown(
            g.InitialAmount, adjustedDepositsList,
            g.AnnualInterestRate, g.ManagementFeeAnnualPct, g.DepositFeePct, g.CapitalGainsTaxPct);

        return new SavingsGoalDto(
            g.Id,
            g.Name,
            g.Icon,
            g.Color,
            g.VehicleType,
            g.TargetAmount,
            g.InitialAmount,
            g.AnnualInterestRate,
            g.ManagementFeeAnnualPct,
            g.DepositFeePct,
            g.CapitalGainsTaxPct,
            g.StartDate,
            g.TargetDate,
            g.LinkedCategoryId,
            g.LinkedCategory?.Name,
            g.AutoSyncCategoryBudget,
            g.IsActive,
            totalMonths,
            elapsedBeforeCurrent,
            remainingMonths,
            originalPmt,
            currentCycleRequiredPmt,
            nextCycleProjectedPmtIfZero,
            Math.Round(currentCycleDeposited, 2),
            currentCycleRemaining,
            totalDepositedActual,
            actualBalanceNet,
            expectedToDateNet,
            paceDelta,
            progressPct,
            paceStatus,
            statusMsg,
            forwardSim.NetFinal,
            plannedSim.EstimatedTax,
            plannedSim.TotalGrossInterest,
            adjustedYearlyBreakdown,
            mergedTrajectory
        );
    }

    private static List<SavingsYearlyBreakdownDto> BuildYearlyBreakdown(
        decimal initialAmount,
        IReadOnlyList<decimal> monthlyDeposits,
        decimal annualInterestRate,
        decimal managementFeeAnnualPct,
        decimal depositFeePct,
        decimal capitalGainsTaxPct)
    {
        var yearly = new List<SavingsYearlyBreakdownDto>();
        var monthlyInterestRate = annualInterestRate / 100m / 12m;
        var monthlyMgmtRate = managementFeeAnnualPct / 100m / 12m;
        var depositFeeRate = depositFeePct / 100m;
        var taxRate = capitalGainsTaxPct / 100m;

        var gross = initialAmount;
        var principal = initialAmount;
        var yearInterest = 0m;
        var yearFees = 0m;

        for (var k = 1; k <= monthlyDeposits.Count; k++)
        {
            var dep = monthlyDeposits[k - 1];
            var depFee = dep * depositFeeRate;
            var netDep = dep - depFee;
            gross += netDep;
            principal += netDep;

            var interest = gross * monthlyInterestRate;
            gross += interest;

            var mgmtFee = gross * monthlyMgmtRate;
            gross = Math.Max(0m, gross - mgmtFee);

            yearInterest += interest;
            yearFees += depFee + mgmtFee;

            if (k % 12 == 0 || k == monthlyDeposits.Count)
            {
                var yearNumber = (k - 1) / 12 + 1;
                var profit = Math.Max(0m, gross - principal);
                var tax = profit * taxRate;
                var net = gross - tax;
                yearly.Add(new SavingsYearlyBreakdownDto(
                    yearNumber,
                    Math.Round(principal, 2),
                    Math.Round(yearInterest, 2),
                    Math.Round(yearFees, 2),
                    Math.Round(tax, 2),
                    Math.Round(net, 2)));
                yearInterest = 0m;
                yearFees = 0m;
            }
        }

        return yearly;
    }

    /// <summary>מסנכרן אוטומטית את תקציב הקטגוריות המקושרות ליעדי החיסכון עבור המחזור המבוקש (כולל איפוס קטגוריות של יעדים שהושעו).</summary>
    public static async Task SyncCategoryBudgetsForCycleAsync(AppDbContext db, Guid projectId, int year, int month)
    {
        var overview = await GetOverviewAsync(db, projectId, year, month);
        var allWithCat = overview.Goals
            .Where(g => g.AutoSyncCategoryBudget && g.LinkedCategoryId.HasValue)
            .GroupBy(g => g.LinkedCategoryId!.Value)
            .ToList();

        if (allWithCat.Count == 0) return;

        var catIds = allWithCat.Select(grp => grp.Key).ToList();
        var cats = await db.Categories.Where(c => c.ProjectId == projectId && catIds.Contains(c.Id)).ToListAsync();
        var monthKey = $"{month:D2}/{year}";

        foreach (var grp in allWithCat)
        {
            var cat = cats.FirstOrDefault(c => c.Id == grp.Key);
            if (cat is null) continue;

            // רק יעדים פעילים תורמים לתקציב המחזור; אם כולם מושעים — התקציב מתאפס ל-0 כדי שלא יעבור מחודש לחודש
            var requiredForCat = Math.Round(grp.Where(x => x.IsActive).Sum(x => x.CurrentCycleRequiredPmt), 2);
            var budgets = cat.GetMonthlyBudgets();
            budgets[monthKey] = requiredForCat;
            cat.SetMonthlyBudgets(budgets);
            cat.PlannedAmount = requiredForCat;
            if (cat.Notes == "נוצר אוטומטית עבור יעד חיסכון")
            {
                cat.IsCapitalTransfer = false;
            }
        }

        await db.SaveChangesAsync();
    }
}
