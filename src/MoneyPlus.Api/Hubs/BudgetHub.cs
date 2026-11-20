using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Api.Services;

namespace MoneyPlus.Api.Hubs;

/// <summary>
/// SignalR Hub לסנכרון בזמן אמת והתראות מיידיות על הוצאות והכנסות בין משתתפי הפרויקט.
/// </summary>
[Authorize]
public class BudgetHub(AppDbContext db) : Hub
{
    public static string ProjectGroup(Guid projectId) => $"project:{projectId}";

    /// <summary>
    /// מצרף את המשתמש המחובר לחדר ההתראות של הפרויקט, לאחר אימות חברותו בטבלת ProjectMembers.
    /// </summary>
    public async Task JoinProject(Guid projectId)
    {
        var userId = Context.User?.GetUserId();
        if (userId is null) return;

        var isMember = await db.ProjectMembers
            .AsNoTracking()
            .AnyAsync(m => m.ProjectId == projectId && m.AppUserId == userId);

        if (!isMember) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, ProjectGroup(projectId));
    }

    public async Task LeaveProject(Guid projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ProjectGroup(projectId));
    }
}

