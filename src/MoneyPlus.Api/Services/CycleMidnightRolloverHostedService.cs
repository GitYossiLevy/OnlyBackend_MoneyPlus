using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;

namespace MoneyPlus.Api.Services;

/// <summary>
/// שירות רקע במערכת הפועל ברקע ומבצע גלגול תקציב אוטומטי בחצות הלילה
/// כאשר מתחלף מחזור הסבב החודשי (למשל בחצות של ה-10 לחודש),
/// במידה והמשתמש לא לחץ ידנית על "תכנן את תקציב" במהלך יום ה-9.
/// </summary>
public class CycleMidnightRolloverHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CycleMidnightRolloverHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CycleMidnightRolloverHostedService started.");

        // בדיקה ראשונית בעת עליית השרת
        try
        {
            await CheckAndRolloverAllProjectsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred during initial cycle rollover check on boot.");
        }

        // בדיקה מחזורית כל 15 דקות לזיהוי מדויק של מעבר חצות
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAndRolloverAllProjectsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred during automatic cycle rollover check.");
            }
        }
    }

    public async Task CheckAndRolloverAllProjectsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var projects = await db.Projects.ToListAsync(ct);
        var today = DateTime.Today;

        foreach (var p in projects)
        {
            var cycleDay = p.CycleStartDay <= 0 ? 1 : Math.Clamp(p.CycleStartDay, 1, 28);

            // אם היום בחודש קטן מיום תחילת הסבב – אנו עדיין בסבב הקודם ולא הגענו למעבר הסבב
            if (today.Day < cycleDay)
            {
                continue;
            }

            // הגענו ליום הסבב או עברנו אותו (למשל ה-10 לחודש)
            // החודש הפעיל החדש הינו החודש הנוכחי
            int activeY = today.Year;
            int activeM = today.Month;

            // החודש הקודם שממנו מעתיקים את מספרי התכנון
            int prevY = activeM == 1 ? activeY - 1 : activeY;
            int prevM = activeM == 1 ? 12 : activeM - 1;

            var activeKey = $"{activeM:D2}/{activeY}";
            var prevKey = $"{prevM:D2}/{prevY}";

            // בדיקה האם לחודש החדש כבר הוגדרו יעדי תכנון בקטגוריות
            var hasActiveBudgets = await db.Categories
                .Where(c => c.ProjectId == p.Id && c.MonthlyBudgetsJson != null && c.MonthlyBudgetsJson.Contains(activeKey))
                .AnyAsync(ct);

            if (!hasActiveBudgets)
            {
                // בדיקה האם לחודש הקודם היו תקציבים שניתן לגלגל
                var hasPrevBudgets = await db.Categories
                    .Where(c => c.ProjectId == p.Id && c.MonthlyBudgetsJson != null && c.MonthlyBudgetsJson.Contains(prevKey))
                    .AnyAsync(ct);

                if (hasPrevBudgets)
                {
                    logger.LogInformation("Midnight auto-rollover triggered for project '{Name}' ({Id}) into {ActiveKey}", p.Name, p.Id, activeKey);
                    await CycleRolloverService.ExecuteRolloverAsync(db, p.Id, prevY, prevM, activeY, activeM, logger);
                }
            }
        }
    }
}

