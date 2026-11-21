using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Dtos;

public record ProjectUserDto(Guid Id, string Name, string Role, string Avatar, bool IsActiveDefault);
public record CreateProjectUserRequest(string Name, string Role, string Avatar);

public record IncomeCategoryDto(Guid Id, string Name, string Icon, decimal MonthlyPlannedAmount);
public record CreateIncomeCategoryRequest(string Name, string? Icon, decimal MonthlyPlannedAmount);
public record UpdateIncomeCategoryRequest(string Name, string? Icon, decimal MonthlyPlannedAmount);

public record CategoryDto(Guid Id, string Name, string? Notes, BudgetPeriodicity Periodicity, decimal PlannedAmount, decimal Spent, decimal Remaining, int PercentUsed, string? Icon, bool IsCapitalTransfer = false);
public record CreateCategoryRequest(string Name, string? Notes, BudgetPeriodicity Periodicity, decimal PlannedAmount, string? Icon = null, bool IsCapitalTransfer = false);
public record UpdateCategoryRequest(string Name, string? Notes, BudgetPeriodicity Periodicity, decimal PlannedAmount, string? Icon = null, bool IsCapitalTransfer = false);
public record ReorderCategoriesRequest(List<Guid> OrderedIds);

public record IncomeDto(Guid Id, string Description, Guid IncomeCategoryId, string IncomeCategoryName, ReceiptType ReceiptType, DateOnly Date, decimal PlannedAmount, decimal ActualAmount);
public record CreateIncomeRequest(string Description, decimal Amount, DateOnly Date, ReceiptType ReceiptType, Guid? IncomeCategoryId, string? NewCategoryName, string? NewCategoryIcon, decimal? PlannedAmount = null, decimal? ActualAmount = null);
public record UpdateIncomeRequest(string Description, decimal Amount, DateOnly Date, ReceiptType ReceiptType, Guid IncomeCategoryId, decimal? PlannedAmount = null, decimal? ActualAmount = null);

public record TransactionDto(Guid Id, string Description, Guid CategoryId, string CategoryName, decimal Amount, DateOnly Date, PaymentMethodKind PaymentMethod, string? PaymentMethodLabel, Guid ExecutedByUserId, string ExecutedByUserName, int? InstallmentNumber, int? InstallmentsTotal, Guid? InstallmentGroupId = null, bool IsCapitalTransfer = false, Guid? SavingsGoalId = null);
public record InstallmentItemDto(Guid TransactionId, int InstallmentNumber, decimal Amount, DateOnly Date);
public record InstallmentGroupDetailsDto(Guid InstallmentGroupId, int TotalInstallments, decimal TotalAmount, DateOnly StartDate, List<InstallmentItemDto> Items);
/// <summary>InstallmentAmounts – אופציונלי: סכום ידני לכל תשלום (עריכה ידנית). אחרת מחולק שווה.</summary>
public record CreateTransactionRequest(string Description, decimal Amount, DateOnly Date, Guid CategoryId, Guid ExecutedByUserId, PaymentMethodKind PaymentMethod, string? PaymentMethodLabel, int? Installments, List<decimal>? InstallmentAmounts, Guid? SavingsGoalId = null);
public record UpdateTransactionRequest(string Description, decimal Amount, DateOnly Date, Guid CategoryId, Guid ExecutedByUserId, PaymentMethodKind PaymentMethod, string? PaymentMethodLabel, int? Installments = null, List<decimal>? InstallmentAmounts = null, Guid? SavingsGoalId = null);

public record RenamePaymentMethodRequest(string OldName, string NewName);

public record PeriodSummaryDto(int Year, int Month, decimal PlannedExpenses, decimal ActualExpenses, decimal ExpensesRemaining,
    decimal PlannedIncome, decimal ActualIncome, decimal IncomeGap,
    decimal PlannedBalance, decimal ActualBalance, decimal BalanceGap, decimal SavingsRatePercent,
    decimal ActualCapitalTransfers = 0, decimal PlannedCapitalTransfers = 0, decimal OperatingActualBalance = 0);
public record ReconciliationDto(decimal ExpectedBalance, decimal BankBalance, decimal Gap, bool IsPerfect);

public record MonthOverviewDto(
    int Month,
    string MonthName,
    decimal PlannedIncome,
    decimal ActualIncome,
    decimal PlannedExpenses,
    decimal ActualExpenses,
    decimal PlannedBalance,
    decimal ActualBalance,
    decimal BalanceGap,
    decimal SavingsRatePercent,
    decimal ActualCapitalTransfers = 0
);

public record AnnualOverviewDto(
    int Year,
    decimal TotalAnnualPlannedIncome,
    decimal TotalAnnualActualIncome,
    decimal TotalAnnualPlannedExpenses,
    decimal TotalAnnualActualExpenses,
    decimal TotalAnnualPlannedBalance,
    decimal TotalAnnualActualBalance,
    decimal AverageMonthlyBalance,
    decimal AnnualSavingsRatePercent,
    int PositiveMonthsCount,
    int NegativeMonthsCount,
    List<MonthOverviewDto> Months,
    decimal TotalAnnualActualCapitalTransfers = 0
);

// Auth DTOs
public record RegisterRequest(string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record AccountDto(Guid Id, string Email, string DisplayName);
public record AuthResponse(string Token, DateTime ExpiresAt, AccountDto User);

// Financial Insights Engine DTOs
public record FinancialInsightDto(string Id, string Type, string Severity, string Title, string Description, decimal? MetricValue, string? ActionTab, bool IsRead);
public record InsightsSummaryDto(int TotalInsights, int UnreadCount, decimal SavingsRatePercent, decimal ProjectedAnnualSurplus, List<FinancialInsightDto> Insights);

// Excel & Backup Engine DTOs
public record ExcelImportResultDto(
    int ImportedExpensesCount,
    int ImportedIncomesCount,
    decimal TotalExpensesAmount,
    decimal TotalIncomesAmount,
    List<string> NewCategoriesCreated,
    List<string> NewPaymentMethodsAdded,
    List<string> Warnings
);

public record BackupProjectSnapshotDto(
    Guid ProjectId,
    string ProjectName,
    DateTime ExportedAtUtc,
    List<CategoryDto> Categories,
    List<IncomeCategoryDto> IncomeCategories,
    List<ProjectUserDto> Users,
    List<TransactionDto> Transactions,
    List<IncomeDto> Incomes
);

// Advanced Financial Analytics & Insights DTOs
public record ClusterItemDto(
    string Name,
    string Icon,
    decimal Amount,
    double Percentage
);

public record BudgetClusterDto(
    string Id,
    string Name,
    string Icon,
    string Color,
    decimal Amount,
    double Percentage,
    List<ClusterItemDto> Items
);

public record DtiGaugeDto(
    decimal DtiPercent,
    string Status, // Safe, Caution, Danger
    string StatusLabel,
    decimal TotalDebtAmount,
    decimal TotalIncomeAmount,
    decimal SafeCeilingAmount
);

public record TopSpenderDto(
    string CategoryName,
    string Icon,
    decimal Amount,
    double PercentageOfTotal,
    decimal PlannedAmount
);

public record PartnerExpenseDto(
    Guid UserId,
    string UserName,
    string Avatar,
    decimal TotalAmount,
    int TransactionCount,
    double PercentageOfTotal,
    decimal AverageTransactionAmount
);

public record PaymentMethodBreakdownDto(
    string MethodKind,
    string MethodLabel,
    decimal TotalAmount,
    int Count,
    double Percentage
);

public record AnalyticsDashboardDto(
    int Year,
    int? Month,
    string PeriodLabel,
    decimal TotalIncome,
    decimal TotalExpenses,
    decimal TotalCapitalTransfers,
    decimal NetCashFlow,
    decimal RealSavingsAccumulated,
    decimal RealSavingsRatePercent,
    DtiGaugeDto Dti,
    List<BudgetClusterDto> Clusters,
    List<TopSpenderDto> TopSpenders,
    List<PartnerExpenseDto> Partners,
    List<PaymentMethodBreakdownDto> PaymentMethods,
    List<FinancialInsightDto> Insights
);

// ================= Credit Card Statement Reconciliation DTOs =================
public enum CardReconciliationStatus
{
    Match,             // התאמה מלאה: סכום ותאריך מדויק (ירוק)
    DateShiftMatch,    // תאימות בסכום עם סטיית תאריך בימים (צהוב)
    DeltaMismatch,     // סטיית סכום בתוך גבולות ה-Delta המקובלת (כתום)
    DeltaExceeded,     // סטיית סכום החורגת מה-Delta המקובלת (אדום)
    MissingInSystem,   // תנועה שקיימת בדף הפירוט אך חסרה לחלוטין במערכת (סגול/כחול)
    OrphanInSystem,    // תנועה שרשומה במערכת עבור כרטיס זה אך לא מופיעה בפירוט (אדום)
    FoundInNextMonth,  // זוהתה הוצאה תואמת שהוזנה בחודש הבא בטעות (תכלת)
    FoundInOtherCard   // זוהתה הוצאה תואמת שהוזנה בכרטיס או אמצעי תשלום אחר בטעות (צהוב/ענבר)
}

public record CreditCardStatementItemDto(
    string RawBusinessName,
    string CleanBusinessName,
    DateOnly Date,
    decimal BillingAmount,
    decimal? DealAmount,
    int? InstallmentCurrent,
    int? InstallmentTotal,
    string? CardLast4Digits,
    string? Notes
);

public record CreditCardReconciledRowDto(
    Guid Id,
    CreditCardStatementItemDto StatementItem,
    CardReconciliationStatus Status,
    TransactionDto? MatchedTransaction,
    decimal Delta,
    Guid? SuggestedCategoryId,
    string? SuggestedCategoryName,
    Guid? SuggestedUserId,
    string? SuggestedUserName,
    string? OtherCardName = null,
    Guid? OtherCardTxId = null,
    int? DayDifference = null
);

public record CreditCardReconciliationSummaryDto(
    string CardLabel,
    int Year,
    int Month,
    decimal StatementTotalAmount,
    decimal SystemTotalAmount,
    decimal NetGap,
    decimal ToleranceDelta,
    int MatchedCount,
    int DeltaCount,
    int MissingCount,
    int OrphansCount,
    List<CreditCardReconciledRowDto> Rows,
    List<TransactionDto> OrphanTransactions,
    int NextMonthCount = 0,
    int CrossCardCount = 0,
    string? DetectedCardDigits = null,
    string? DetectedFormat = null,
    bool IsCardAutoDetected = false,
    bool RequiresCardSelection = false,
    List<string>? AvailableCards = null,
    DateOnly? StatementBillingDate = null,
    int? StatementBillingYear = null,
    int? StatementBillingMonth = null,
    string? DetectedIssuer = null,
    List<string>? DetectedMultiCardDigits = null,
    string? SourceFileName = null,
    int DateShiftCount = 0,
    int ToleranceDays = 3,
    string? DetectedCardOwner = null
);

public record CreateMissingItemDto(
    string Description,
    decimal Amount,
    DateOnly Date,
    Guid CategoryId,
    Guid ExecutedByUserId,
    string PaymentMethodLabel,
    int? InstallmentNumber,
    int? InstallmentsTotal,
    DateOnly? TargetDate = null,
    bool IsShiftedToNextMonth = false
);

public record ReassignCrossCardItemDto(
    Guid TransactionId,
    string NewPaymentMethodLabel,
    Guid? CategoryId = null,
    decimal? NewAmount = null,
    DateOnly? NewDate = null
);

public record UpdateDeltaItemDto(
    Guid TransactionId,
    decimal NewAmount,
    Guid? CategoryId = null,
    string? NewPaymentMethodLabel = null
);

public record HandleOrphanItemDto(
    Guid TransactionId,
    string Action, // "delete", "reassignPaymentMethod", "shiftMonth"
    string? NewPaymentMethodLabel,
    DateOnly? NewDate
);

public record SaveCreditCardSessionRequest(
    Guid? Id = null,
    Guid? SessionId = null,
    string? SessionName = null,
    string? CardLabel = null,
    int Year = 2026,
    int Month = 1,
    string? SourceFileName = null,
    decimal StatementTotalAmount = 0,
    decimal SystemTotalAmount = 0,
    decimal NetGap = 0,
    int MatchedCount = 0,
    int DeltaCount = 0,
    int MissingCount = 0,
    int OrphansCount = 0,
    int CrossCardCount = 0,
    string? Status = "Draft",
    string? StatementsJson = null,
    string? MatchesJson = null,
    string? OverridesJson = null,
    string? SummaryJson = null,
    string? SessionDataJson = null
);

public record CreditCardSessionSummaryDto(
    Guid Id,
    Guid ProjectId,
    string SessionName,
    string CardLabel,
    int Year,
    int Month,
    int TotalStatements,
    int TotalTransactions,
    int MatchRate,
    string? SourceFileName,
    decimal StatementTotalAmount,
    decimal SystemTotalAmount,
    decimal NetGap,
    int MatchedCount,
    int DeltaCount,
    int MissingCount,
    int OrphansCount,
    int CrossCardCount,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? StatementsJson = null,
    string? MatchesJson = null,
    string? OverridesJson = null,
    string? SummaryJson = null,
    string? SessionDataJson = null
);

public record ApplyCreditCardReconciliationRequest(
    Guid ProjectId,
    int Year,
    int Month,
    string CardLabel,
    List<CreateMissingItemDto> MissingToCreate,
    List<UpdateDeltaItemDto> DeltasToUpdate,
    List<HandleOrphanItemDto> OrphansToHandle,
    List<ReassignCrossCardItemDto>? CrossCardToReassign = null
);

public record ApplyCreditCardReconciliationResultDto(
    int CreatedCount,
    int UpdatedCount,
    int HandledOrphansCount,
    string Message
);

// === Savings Goals (יעדי חיסכון חכמים) ===

public record SavingsSchedulePointDto(
    int MonthIndex,
    string Label,
    decimal PlannedBalanceNet,
    decimal? ActualBalanceNet,
    decimal PrincipalAccumulated,
    decimal GrossInterestAccumulated,
    decimal FeesAccumulated,
    decimal TaxEstimated,
    decimal? AdjustedBalanceNet = null,
    decimal? AdjustedMonthlyDeposit = null,
    decimal? ActualMonthlyDeposit = null
);

public record SavingsYearlyBreakdownDto(
    int YearNumber,
    decimal PrincipalTotal,
    decimal InterestEarned,
    decimal ManagementFees,
    decimal EstimatedTax,
    decimal NetBalance
);

public record SavingsSimulationRequest(
    string Mode, // "TargetToPmt" | "PmtToTarget"
    decimal TargetAmount,
    decimal InitialAmount,
    decimal MonthlyDeposit,
    int Months,
    decimal AnnualInterestRate,
    decimal ManagementFeeAnnualPct,
    decimal DepositFeePct,
    decimal CapitalGainsTaxPct
);

public record SavingsSimulationResultDto(
    decimal RequiredMonthlyDeposit,
    decimal ProjectedNetFinal,
    decimal ProjectedGrossFinal,
    decimal TotalPrincipal,
    decimal TotalGrossInterest,
    decimal TotalManagementFees,
    decimal TotalDepositFees,
    decimal EstimatedTax,
    List<SavingsYearlyBreakdownDto> YearlyBreakdown,
    List<SavingsSchedulePointDto> Trajectory
);

public record UpsertSavingsGoalRequest(
    string Name,
    string? Icon,
    string? Color,
    SavingsVehicleType VehicleType,
    decimal TargetAmount,
    decimal InitialAmount,
    decimal AnnualInterestRate,
    decimal ManagementFeeAnnualPct,
    decimal DepositFeePct,
    decimal CapitalGainsTaxPct,
    DateOnly StartDate,
    DateOnly TargetDate,
    Guid? LinkedCategoryId,
    bool AutoSyncCategoryBudget = true,
    bool IsActive = true
);

public record QuickSavingsDepositRequest(
    decimal? Amount = null,
    DateOnly? Date = null,
    string? Description = null,
    Guid? ExecutedByUserId = null,
    PaymentMethodKind? PaymentMethod = null,
    string? PaymentMethodLabel = null
);

public record SavingsGoalDto(
    Guid Id,
    string Name,
    string Icon,
    string Color,
    SavingsVehicleType VehicleType,
    decimal TargetAmount,
    decimal InitialAmount,
    decimal AnnualInterestRate,
    decimal ManagementFeeAnnualPct,
    decimal DepositFeePct,
    decimal CapitalGainsTaxPct,
    DateOnly StartDate,
    DateOnly TargetDate,
    Guid? LinkedCategoryId,
    string? LinkedCategoryName,
    bool AutoSyncCategoryBudget,
    bool IsActive,
    int TotalMonths,
    int ElapsedMonths,
    int RemainingMonths,
    decimal OriginalMonthlyPmt,
    decimal CurrentCycleRequiredPmt,
    decimal NextCycleProjectedPmtIfZero,
    decimal CurrentCycleDeposited,
    decimal CurrentCycleRemaining,
    decimal TotalDepositedActual,
    decimal ActualBalanceNet,
    decimal ExpectedBalanceToDateNet,
    decimal PaceDelta,
    int ProgressPercent,
    string PaceStatus, // "Ahead" | "OnTrack" | "CyclePending" | "Behind" | "Completed"
    string StatusMessage,
    decimal ProjectedNetFinalAtCurrentPace,
    decimal EstimatedTax,
    decimal TotalGrossInterest,
    List<SavingsYearlyBreakdownDto> YearlyBreakdown,
    List<SavingsSchedulePointDto> Trajectory
);

public record SavingsGoalsOverviewDto(
    int Year,
    int Month,
    int TotalGoalsCount,
    decimal TotalRequiredThisCycle,
    decimal TotalDepositedThisCycle,
    decimal TotalAccumulatedNet,
    decimal TotalTargetAmount,
    string OverallPaceStatus,
    string OverallStatusMessage,
    List<SavingsGoalDto> Goals
);

public record CapturedExpenseDto(
    Guid Id,
    decimal Amount,
    string MerchantName,
    string RawNotificationText,
    string SourceApp,
    PaymentMethodKind PaymentMethod,
    string? PaymentMethodLabel,
    Guid? SuggestedCategoryId,
    string? SuggestedCategoryName,
    string? SuggestedCategoryIcon,
    double ConfidenceScore,
    DateTime DetectedAt,
    CapturedExpenseStatus Status,
    Guid? CreatedTransactionId
);

public record IngestCapturedExpenseRequest(
    decimal Amount,
    string MerchantName,
    string? RawNotificationText,
    string? SourceApp,
    PaymentMethodKind? PaymentMethod,
    string? PaymentMethodLabel,
    string? ExternalKey,
    DateTime? DetectedAt,
    string? DeviceId = null,
    Guid? CapturedByUserId = null
);

public record ApproveCapturedExpenseRequest(
    Guid CategoryId,
    decimal? Amount = null,
    string? MerchantName = null,
    PaymentMethodKind? PaymentMethod = null,
    string? PaymentMethodLabel = null,
    Guid? ExecutedByUserId = null,
    DateOnly? Date = null
);




