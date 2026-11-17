using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Services;

/// <summary>
/// סימון פעולה ככזו הדורשת הרשאת מנהל פרויקט (Owner) בלבד (למשל: מחיקת פרויקט, שחזור גיבוי).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequireProjectOwnerAttribute : Attribute
{
}

/// <summary>
/// פילטר גלובלי: כל route שמכיל {projectId} דורש שהמשתמש המחובר חבר בפרויקט.
/// מחזיר 404 (לא 403) כאשר המשתמש אינו חבר כדי לא לחשוף קיום פרויקטים של אחרים.
/// אם הפעולה מסומנת ב-[RequireProjectOwner], מוודא שהמשתמש הוא Owner, אחרת מחזיר 403 Forbidden.
/// </summary>
public class ProjectAccessFilter(AppDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (ctx.RouteData.Values.TryGetValue("projectId", out var raw))
        {
            var userId = ctx.HttpContext.User.GetUserId();
            if (userId is null || !Guid.TryParse(raw?.ToString(), out var projectId))
            {
                ctx.Result = new NotFoundResult();
                return;
            }

            var member = await db.ProjectMembers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.AppUserId == userId);

            if (member is null)
            {
                ctx.Result = new NotFoundResult();
                return;
            }

            // בדיקת הרשאת בעלים (Owner) עבור פעולות הרסניות או ניהוליות
            var requiresOwner = ctx.ActionDescriptor.EndpointMetadata.OfType<RequireProjectOwnerAttribute>().Any();
            if (requiresOwner && member.Role != MemberRole.Owner)
            {
                ctx.Result = new ObjectResult(new { message = "פעולה זו מותרת לבעלי הפרויקט (Owner) בלבד" })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }
        }
        await next();
    }
}
