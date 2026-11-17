namespace MoneyPlus.Core.Entities;

public enum BudgetPeriodicity { Weekly, Monthly, Annual, OneTime }

public enum ReceiptType { FixedSalary, Bonus, Reimbursement, Other }

public enum PaymentMethodKind { CreditCard, Installments, Bit, Paybox, BankTransfer, Cash, Check, Other }

/// <summary>פרויקט תקציב (משק בית, טיול, חתונה...) – גבול ההפרדה של כל הנתונים.</summary>
public class Project
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string ProjectType { get; set; } = "התנהלות חודשית";
    public string DefaultCurrency { get; set; } = "ILS";
    public int CycleStartDay { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public string? DailyReminderTime { get; set; } = "20:30";
    public string? CustomPaymentMethodsJson { get; set; }
    public string? InactivePaymentMethodsJson { get; set; }

    public static readonly string[] DefaultPaymentMethods =
    [
        "כרטיס אשראי", "עסקת תשלומים", "ביט (Bit)", "פייבוקס (Paybox)", "העברה בנקאית", "מזומן", "צ'ק"
    ];

    public List<string> GetPaymentMethods()
    {
        if (string.IsNullOrWhiteSpace(CustomPaymentMethodsJson))
            return [.. DefaultPaymentMethods];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(CustomPaymentMethodsJson) ?? [.. DefaultPaymentMethods];
        }
        catch
        {
            return [.. DefaultPaymentMethods];
        }
    }

    public void SetPaymentMethods(IEnumerable<string> methods)
    {
        var cleaned = methods.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList();
        CustomPaymentMethodsJson = System.Text.Json.JsonSerializer.Serialize(cleaned);
    }

    public List<string> GetInactivePaymentMethods()
    {
        if (string.IsNullOrWhiteSpace(InactivePaymentMethodsJson))
            return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(InactivePaymentMethodsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SetInactivePaymentMethods(IEnumerable<string> methods)
    {
        var cleaned = methods.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList();
        InactivePaymentMethodsJson = System.Text.Json.JsonSerializer.Serialize(cleaned);
    }

    public ICollection<ProjectUser> Users { get; set; } = [];
    public ICollection<Category> Categories { get; set; } = [];
    public ICollection<IncomeCategory> IncomeCategories { get; set; } = [];
    public ICollection<ProjectIncome> Incomes { get; set; } = [];
    public ICollection<Transaction> Transactions { get; set; } = [];
}

/// <summary>משתמש דינאמי ברמת הפרויקט (ללא קיבוע).</summary>
public class ProjectUser
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public string Role { get; set; } = "שותף/ה";
    public string Avatar { get; set; } = "🧑";
    public bool IsActiveDefault { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
}

/// <summary>קטגוריית הוצאה (תקציב מתוכנן + מחזוריות).</summary>
public class Category
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public string? Notes { get; set; }
    public BudgetPeriodicity Periodicity { get; set; } = BudgetPeriodicity.Monthly;
    public decimal PlannedAmount { get; set; }
    public string Icon { get; set; } = "📁";
    public bool IsCapitalTransfer { get; set; } = false;
    public bool AutoRollover { get; set; } = true;
    public string? MonthlyBudgetsJson { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    private string? _cachedBudgetsJson;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    private Dictionary<string, decimal>? _cachedBudgetsMap;

    private Dictionary<string, decimal> GetMonthlyBudgetsCached()
    {
        if (_cachedBudgetsMap is not null && string.Equals(_cachedBudgetsJson, MonthlyBudgetsJson, StringComparison.Ordinal))
            return _cachedBudgetsMap;
        if (string.IsNullOrWhiteSpace(MonthlyBudgetsJson))
        {
            _cachedBudgetsJson = MonthlyBudgetsJson;
            _cachedBudgetsMap = new();
            return _cachedBudgetsMap;
        }
        try
        {
            _cachedBudgetsMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(MonthlyBudgetsJson) ?? new();
        }
        catch
        {
            _cachedBudgetsMap = new();
        }
        _cachedBudgetsJson = MonthlyBudgetsJson;
        return _cachedBudgetsMap;
    }

    public Dictionary<string, decimal> GetMonthlyBudgets() => new(GetMonthlyBudgetsCached());

    public void SetMonthlyBudgets(Dictionary<string, decimal> budgets)
    {
        MonthlyBudgetsJson = budgets.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(budgets) : null;
        _cachedBudgetsJson = MonthlyBudgetsJson;
        _cachedBudgetsMap = new(budgets);
    }

    public decimal GetPlannedForMonth(int year, int month, (int Year, int Month)? projectMax = null)
    {
        return BudgetRolloverHelper.CalculatePlanned(GetMonthlyBudgetsCached(), PlannedAmount, year, month, projectMax);
    }

    public bool IsActiveInMonth(int year, int month, bool hasActivityInMonth, (int Year, int Month)? projectMax = null)
    {
        return BudgetRolloverHelper.CheckIsActive(GetMonthlyBudgetsCached(), PlannedAmount, year, month, hasActivityInMonth, projectMax);
    }

    public Project Project { get; set; } = null!;
    public ICollection<Transaction> Transactions { get; set; } = [];
}

/// <summary>עוזר לוגיקת גלגול תקציב (Rollover) חודש בחודשו ומעבר לחודש חדש.</summary>
public static class BudgetRolloverHelper
{
    public static List<(int Year, int Month, decimal Amount)> ParseBudgets(Dictionary<string, decimal> map)
    {
        var list = new List<(int Year, int Month, decimal Amount)>();
        foreach (var (k, v) in map)
        {
            var parts = k.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var y))
            {
                list.Add((y, m, v));
            }
        }
        return list;
    }

    public static (int Year, int Month)? FindMaxMonth(IEnumerable<Dictionary<string, decimal>> allMaps)
    {
        int maxY = 0, maxM = 0;
        foreach (var map in allMaps)
        {
            foreach (var k in map.Keys)
            {
                var parts = k.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var y))
                {
                    if (y > maxY || (y == maxY && m > maxM))
                    {
                        maxY = y;
                        maxM = m;
                    }
                }
            }
        }
        return maxY > 0 ? (maxY, maxM) : null;
    }

    public static decimal CalculatePlanned(Dictionary<string, decimal> map, decimal defaultAmount, int year, int month, (int Year, int Month)? projectMax = null)
    {
        if (map.Count == 0) return defaultAmount;
        var key = $"{month:D2}/{year}";
        if (map.TryGetValue(key, out var val)) return val;

        var parsed = ParseBudgets(map);
        if (parsed.Count == 0) return defaultAmount;

        var prior = parsed
            .Where(x => x.Year < year || (x.Year == year && x.Month < month))
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .FirstOrDefault();

        if (prior == default || prior.Amount <= 0) return 0m;

        // בדיקה האם החודש המבוקש הינו חודש חדש/עתידי
        if (projectMax.HasValue)
        {
            var isFutureOrNew = year > projectMax.Value.Year || (year == projectMax.Value.Year && month > projectMax.Value.Month);
            if (isFutureOrNew)
            {
                // גלגול לקטגוריות שהיו פעילות בחודש האחרון של הפרויקט (או חודש אחד לפניו)
                var monthsFromProjMax = (projectMax.Value.Year - prior.Year) * 12 + (projectMax.Value.Month - prior.Month);
                if (monthsFromProjMax <= 1) return prior.Amount;
            }
        }
        else
        {
            // אם projectMax לא סופק במפורש: בדיקה האם הקטגוריה הייתה פעילה בחודש הקודם (מרחק עד חודש 1)
            var monthsDiff = (year - prior.Year) * 12 + (month - prior.Month);
            if (monthsDiff <= 1) return prior.Amount;
        }

        return 0m;
    }

    public static bool CheckIsActive(Dictionary<string, decimal> map, decimal defaultAmount, int year, int month, bool hasActivityInMonth, (int Year, int Month)? projectMax = null)
    {
        if (hasActivityInMonth) return true;
        if (map.Count == 0) return true;
        var key = $"{month:D2}/{year}";
        if (map.TryGetValue(key, out var val)) return val > 0;
        return CalculatePlanned(map, defaultAmount, year, month, projectMax) > 0;
    }
}

/// <summary>קטגוריית הכנסה – נפרדת לחלוטין מקטגוריות הוצאה.</summary>
public class IncomeCategory
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public string Icon { get; set; } = "🪙";
    public decimal MonthlyPlannedAmount { get; set; }
    public string? MonthlyBudgetsJson { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    private string? _cachedBudgetsJson;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    private Dictionary<string, decimal>? _cachedBudgetsMap;

    private Dictionary<string, decimal> GetMonthlyBudgetsCached()
    {
        if (_cachedBudgetsMap is not null && string.Equals(_cachedBudgetsJson, MonthlyBudgetsJson, StringComparison.Ordinal))
            return _cachedBudgetsMap;
        if (string.IsNullOrWhiteSpace(MonthlyBudgetsJson))
        {
            _cachedBudgetsJson = MonthlyBudgetsJson;
            _cachedBudgetsMap = new();
            return _cachedBudgetsMap;
        }
        try
        {
            _cachedBudgetsMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(MonthlyBudgetsJson) ?? new();
        }
        catch
        {
            _cachedBudgetsMap = new();
        }
        _cachedBudgetsJson = MonthlyBudgetsJson;
        return _cachedBudgetsMap;
    }

    public Dictionary<string, decimal> GetMonthlyBudgets() => new(GetMonthlyBudgetsCached());

    public void SetMonthlyBudgets(Dictionary<string, decimal> budgets)
    {
        MonthlyBudgetsJson = budgets.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(budgets) : null;
        _cachedBudgetsJson = MonthlyBudgetsJson;
        _cachedBudgetsMap = new(budgets);
    }

    public decimal GetPlannedForMonth(int year, int month, (int Year, int Month)? projectMax = null)
    {
        return BudgetRolloverHelper.CalculatePlanned(GetMonthlyBudgetsCached(), MonthlyPlannedAmount, year, month, projectMax);
    }

    public bool IsActiveInMonth(int year, int month, bool hasActivityInMonth, (int Year, int Month)? projectMax = null)
    {
        return BudgetRolloverHelper.CheckIsActive(GetMonthlyBudgetsCached(), MonthlyPlannedAmount, year, month, hasActivityInMonth, projectMax);
    }

    public Project Project { get; set; } = null!;
    public ICollection<ProjectIncome> Incomes { get; set; } = [];
}

public class ProjectIncome
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required Guid IncomeCategoryId { get; set; }
    public required string Description { get; set; }
    public decimal PlannedAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public ReceiptType ReceiptType { get; set; } = ReceiptType.FixedSalary;
    public DateOnly Date { get; set; }

    public Project Project { get; set; } = null!;
    public IncomeCategory IncomeCategory { get; set; } = null!;
}

/// <summary>הוצאה בפועל. עסקת תשלומים נפרסת למספר רשומות עם אותו InstallmentGroupId.</summary>
public class Transaction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required Guid CategoryId { get; set; }
    public required Guid ExecutedByUserId { get; set; }
    public required string Description { get; set; }
    public required decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    public PaymentMethodKind PaymentMethod { get; set; } = PaymentMethodKind.CreditCard;
    public string? PaymentMethodLabel { get; set; } // למשל "ויזה כאל"
    public Guid? InstallmentGroupId { get; set; }
    public int? InstallmentNumber { get; set; }
    public int? InstallmentsTotal { get; set; }
    public Guid? SavingsGoalId { get; set; }

    public Project Project { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public ProjectUser ExecutedByUser { get; set; } = null!;
}

public enum MemberRole { Owner, Member }

/// <summary>חשבון התחברות (שונה מ-ProjectUser שהוא משתתף בתקציב).</summary>
public class AppUser
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Email { get; set; }          // מנורמל ל-lowercase, ייחודי
    public required string PasswordHash { get; set; }   // PBKDF2 – לעולם לא מוחזר ללקוח
    public required string DisplayName { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>שיוך חשבון לפרויקט – בסיס לבידוד נתונים בין משקי בית.</summary>
public class ProjectMember
{
    public required Guid ProjectId { get; init; }
    public required Guid AppUserId { get; init; }
    public MemberRole Role { get; set; } = MemberRole.Member;

    public Project Project { get; set; } = null!;
    public AppUser AppUser { get; set; } = null!;
}

/// <summary>סגירת תקופה: מאזן + התאמת עו"ש, ננעל ומשוכפל לחודש הבא.</summary>
public class PeriodClosing
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal TotalIncome { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal? BankBalanceEntered { get; set; }
    public decimal? ReconciliationGap { get; set; }
    public DateTime ClosedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>סשן שמור של הצלבת והתאמת כרטיסי אשראי – מאפשר טעינה חוזרת של השיוכים והאישורים שבוצעו.</summary>
public class CreditCardReconciliationSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string CardLabel { get; set; }
    public required int Year { get; set; }
    public required int Month { get; set; }
    public string? SourceFileName { get; set; }
    public decimal StatementTotalAmount { get; set; }
    public decimal SystemTotalAmount { get; set; }
    public decimal NetGap { get; set; }
    public int MatchedCount { get; set; }
    public int DeltaCount { get; set; }
    public int MissingCount { get; set; }
    public int OrphansCount { get; set; }
    public int CrossCardCount { get; set; }
    public string Status { get; set; } = "Draft"; // "Draft", "Approved", "Applied"
    public required string SessionDataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
}

public enum SavingsVehicleType
{
    Custom = 0,
    StudyFund = 1,          // קרן השתלמות (0% מס)
    BankDeposit = 2,        // פיקדון בנקאי (15% מס נומינלי)
    InvestmentProvident = 3,// קופת גמל להשקעה / תיק השקעות (25% מס ריאלי)
    CheckingZero = 4        // עו"ש / חיסכון ללא ריבית (0% ריבית, 0% מס)
}

/// <summary>יעד חיסכון חכם עם חישוב ריבית דריבית, דמי ניהול, מס רווחי הון וסנכרון תקציב מחזורי.</summary>
public class SavingsGoal
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public string Icon { get; set; } = "🎯";
    public string Color { get; set; } = "#0E7490";
    public SavingsVehicleType VehicleType { get; set; } = SavingsVehicleType.InvestmentProvident;
    public decimal TargetAmount { get; set; }
    public decimal InitialAmount { get; set; }
    public decimal AnnualInterestRate { get; set; }
    public decimal ManagementFeeAnnualPct { get; set; }
    public decimal DepositFeePct { get; set; }
    public decimal CapitalGainsTaxPct { get; set; } = 25m;
    public DateOnly StartDate { get; set; }
    public DateOnly TargetDate { get; set; }
    public Guid? LinkedCategoryId { get; set; }
    public bool AutoSyncCategoryBudget { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Project Project { get; set; } = null!;
    public Category? LinkedCategory { get; set; }
}
