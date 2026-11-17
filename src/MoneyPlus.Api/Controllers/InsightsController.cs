using Microsoft.AspNetCore.Mvc;
using MoneyPlus.Api.Dtos;
using MoneyPlus.Api.Services;

namespace MoneyPlus.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/insights")]
public class InsightsController(IFinancialInsightsService insightsService) : ControllerBase
{
    /// <summary>
    /// שליפת תובנות פיננסיות חכמות מבוססות ביצועי תזרים ותקציב לחודש נתון.
    /// מחשב שיעור חיסכון, צפי צבירה שנתית, התראות חריגה מקטגוריות והמלצות לקרן בלת"מים.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<InsightsSummaryDto>> Get(Guid projectId, [FromQuery] int? year, [FromQuery] int? month)
    {
        var today = DateTime.Today;
        var y = year ?? today.Year;
        var m = month ?? today.Month;

        var result = await insightsService.GenerateInsightsAsync(projectId, y, m);
        return Ok(result);
    }

    /// <summary>
    /// שליפת דשבורד אנליטיקה פיננסית מתקדם:
    /// אשכולות תקציב, שעון חוגה DTI, 5 שואבי תקציב גדולים, פילוח שותפים, אמצעי תשלום ותובנות.
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<AnalyticsDashboardDto>> GetDashboard(Guid projectId, [FromQuery] int? year, [FromQuery] int? month)
    {
        var today = DateTime.Today;
        var y = year ?? today.Year;

        var result = await insightsService.GetAnalyticsDashboardAsync(projectId, y, month);
        return Ok(result);
    }
}
