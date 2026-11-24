namespace MoneyPlus.Core.Entities;

public enum CapturedExpenseStatus
{
    Pending = 0,
    Approved = 1,
    Dismissed = 2
}

public class CapturedExpense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public decimal Amount { get; set; }
    public string MerchantName { get; set; } = string.Empty;
    public string RawNotificationText { get; set; } = string.Empty;
    public string SourceApp { get; set; } = "GoogleWallet";
    public PaymentMethodKind PaymentMethod { get; set; } = PaymentMethodKind.CreditCard;
    public string? PaymentMethodLabel { get; set; }
    public Guid? SuggestedCategoryId { get; set; }
    public Category? SuggestedCategory { get; set; }
    public double ConfidenceScore { get; set; }
    public string? ExternalKey { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public CapturedExpenseStatus Status { get; set; } = CapturedExpenseStatus.Pending;
    public Guid? CreatedTransactionId { get; set; }
    public string? CapturedByDeviceId { get; set; }
    public Guid? CapturedByUserId { get; set; }
    public string? HandledByUserName { get; set; }
    public DateTime? HandledAt { get; set; }
}

/// <summary>כלל למידה שמור: מיפוי אוטומטי מבית עסק לקטגוריה (ושימור מונה אישורים/תיקונים).</summary>
public class MerchantCategoryRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string NormalizedMerchant { get; set; } = string.Empty;
    public string DisplayMerchant { get; set; } = string.Empty;
    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
    public string? PaymentMethodLabel { get; set; }
    public int HitCount { get; set; } = 1;
    public int CorrectionCount { get; set; } = 0;
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;
}

