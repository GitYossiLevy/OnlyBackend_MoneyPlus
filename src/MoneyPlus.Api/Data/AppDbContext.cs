using Microsoft.EntityFrameworkCore;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectUser> ProjectUsers => Set<ProjectUser>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<IncomeCategory> IncomeCategories => Set<IncomeCategory>();
    public DbSet<ProjectIncome> Incomes => Set<ProjectIncome>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<PeriodClosing> PeriodClosings => Set<PeriodClosing>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<CreditCardReconciliationSession> CreditCardReconciliationSessions => Set<CreditCardReconciliationSession>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<CapturedExpense> CapturedExpenses => Set<CapturedExpense>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>().HasMany(p => p.Users).WithOne(u => u.Project).HasForeignKey(u => u.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Project>().HasMany(p => p.Categories).WithOne(c => c.Project).HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Project>().HasMany(p => p.IncomeCategories).WithOne(c => c.Project).HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Project>().HasMany(p => p.Incomes).WithOne(i => i.Project).HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Project>().HasMany(p => p.Transactions).WithOne(t => t.Project).HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CreditCardReconciliationSession>().HasOne(s => s.Project).WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SavingsGoal>().HasOne(g => g.Project).WithMany().HasForeignKey(g => g.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SavingsGoal>().HasOne(g => g.LinkedCategory).WithMany().HasForeignKey(g => g.LinkedCategoryId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<SavingsGoal>().Property(g => g.TargetAmount).HasPrecision(18, 2);
        b.Entity<SavingsGoal>().Property(g => g.InitialAmount).HasPrecision(18, 2);
        b.Entity<SavingsGoal>().Property(g => g.AnnualInterestRate).HasPrecision(9, 4);
        b.Entity<SavingsGoal>().Property(g => g.ManagementFeeAnnualPct).HasPrecision(9, 4);
        b.Entity<SavingsGoal>().Property(g => g.DepositFeePct).HasPrecision(9, 4);
        b.Entity<SavingsGoal>().Property(g => g.CapitalGainsTaxPct).HasPrecision(9, 4);
        b.Entity<SavingsGoal>().HasIndex(g => g.ProjectId);
        b.Entity<CapturedExpense>().HasOne(c => c.Project).WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CapturedExpense>().HasOne(c => c.SuggestedCategory).WithMany().HasForeignKey(c => c.SuggestedCategoryId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<CapturedExpense>().Property(c => c.Amount).HasPrecision(18, 2);
        b.Entity<CapturedExpense>().HasIndex(c => new { c.ProjectId, c.Status, c.DetectedAt });
        b.Entity<Transaction>().HasIndex(t => new { t.ProjectId, t.SavingsGoalId });

        // מחיקת משתמש/קטגוריה לא מוחקת היסטוריית תנועות
        b.Entity<Transaction>().HasOne(t => t.Category).WithMany(c => c.Transactions).HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Transaction>().HasOne(t => t.ExecutedByUser).WithMany().HasForeignKey(t => t.ExecutedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProjectIncome>().HasOne(i => i.IncomeCategory).WithMany(c => c.Incomes).HasForeignKey(i => i.IncomeCategoryId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Transaction>().Property(t => t.Amount).HasPrecision(18, 2);
        b.Entity<Category>().Property(c => c.PlannedAmount).HasPrecision(18, 2);
        b.Entity<IncomeCategory>().Property(c => c.MonthlyPlannedAmount).HasPrecision(18, 2);
        b.Entity<ProjectIncome>().Property(i => i.PlannedAmount).HasPrecision(18, 2);
        b.Entity<ProjectIncome>().Property(i => i.ActualAmount).HasPrecision(18, 2);
        b.Entity<PeriodClosing>().Property(p => p.TotalIncome).HasPrecision(18, 2);
        b.Entity<PeriodClosing>().Property(p => p.TotalExpenses).HasPrecision(18, 2);

        b.Entity<Transaction>().HasIndex(t => new { t.ProjectId, t.Date });
        b.Entity<Transaction>().HasIndex(t => new { t.ProjectId, t.InstallmentGroupId });
        b.Entity<Category>().HasIndex(c => c.ProjectId);
        b.Entity<IncomeCategory>().HasIndex(c => c.ProjectId);
        b.Entity<ProjectIncome>().HasIndex(i => new { i.ProjectId, i.Date });
        b.Entity<PeriodClosing>().HasIndex(p => new { p.ProjectId, p.Year, p.Month }).IsUnique();

        b.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        b.Entity<ProjectMember>().HasKey(m => new { m.ProjectId, m.AppUserId });
        b.Entity<ProjectMember>().HasOne(m => m.Project).WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectMember>().HasOne(m => m.AppUser).WithMany().HasForeignKey(m => m.AppUserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectMember>().Property(m => m.Role).HasConversion<string>();
    }
}
