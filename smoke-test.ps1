$ErrorActionPreference = 'Stop'
$b = 'http://localhost:5187/api'
function Post($u, $o) { Invoke-RestMethod -Method Post -Uri "$b$u" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Depth 5))) }
function Put($u, $o) { Invoke-RestMethod -Method Put -Uri "$b$u" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Depth 5))) }
function Get($u) { Invoke-RestMethod -Uri "$b$u" }
function Delete($u) { Invoke-RestMethod -Method Delete -Uri "$b$u" }

$p = Post '/projects' @{ name = 'משק בית' }
$pid_ = $p.id
Write-Host "Project: $($p.name)"

$u1 = Post "/projects/$pid_/users" @{ name = 'יוסי'; role = 'מנהל'; avatar = '👨‍💼' }
$u2 = Post "/projects/$pid_/users" @{ name = 'דניאל'; role = 'ילד/ה'; avatar = '👦' }
Write-Host "Users: $((Get "/projects/$pid_/users").Count)  (expected 2)"

$sup = Post "/projects/$pid_/categories" @{ name = 'קניות סופר'; periodicity = 'Weekly'; plannedAmount = 3500 }
$sofa = Post "/projects/$pid_/categories" @{ name = 'ספה לסלון'; periodicity = 'OneTime'; plannedAmount = 3600 }
$fun = Post "/projects/$pid_/categories" @{ name = 'בילויים ומסעדות'; periodicity = 'Monthly'; plannedAmount = 1000 }

# הכנסה עם קטגוריית הכנסה חדשה on-the-fly
$inc = Post "/projects/$pid_/incomes" @{ description = 'ייעוץ אדריכלי'; amount = 5500; date = '2026-10-27'; receiptType = 'Bonus'; newCategoryName = 'עסק עצמאי'; newCategoryIcon = '📈' }
Write-Host "Income category created inline: $($inc.incomeCategoryName)"
Write-Host "Income categories now: $((Get "/projects/$pid_/income-categories").Count)  (expected 4)"

$t1 = Post "/projects/$pid_/transactions" @{ description = 'רמי לוי'; amount = 825; date = '2026-10-15'; categoryId = $sup.id; executedByUserId = $u1.id; paymentMethod = 'CreditCard'; paymentMethodLabel = 'ויזה כאל' }
$t2 = Post "/projects/$pid_/transactions" @{ description = 'קפה'; amount = 65; date = '2026-10-14'; categoryId = $sup.id; executedByUserId = $u2.id; paymentMethod = 'Bit' }
# יצירת חריגה מכוונת בבילויים (הוצאה של 1450 מול תכנון 1000) כדי לבדוק את מנוע התובנות
$tOver = Post "/projects/$pid_/transactions" @{ description = 'מסעדת שף'; amount = 1450; date = '2026-10-18'; categoryId = $fun.id; executedByUserId = $u1.id; paymentMethod = 'CreditCard'; paymentMethodLabel = 'Max Executive' }

$inst = Post "/projects/$pid_/transactions" @{ description = 'איקאה ספה'; amount = 3600; date = '2026-10-16'; categoryId = $sofa.id; executedByUserId = $u1.id; paymentMethod = 'Installments'; installments = 3 }
Write-Host "Installments created: $($inst.Count) -> $(($inst | % { $_.amount }) -join ' + ')  (expected 3 x 1200)"

$bitOrCash = Get "/projects/$pid_/transactions?year=2026&month=10&methods=Bit,Cash"
Write-Host "Filter Bit|Cash: $($bitOrCash.Count)  (expected 1)"
$drill = Get "/projects/$pid_/transactions?year=2026&month=10&categoryId=$($sup.id)"
Write-Host "Drill-down supermarket: $($drill.Count)  (expected 2)"

$cats = Get "/projects/$pid_/categories?year=2026&month=10"
$s = $cats | ? { $_.name -eq 'קניות סופר' }
Write-Host "Supermarket spent=$($s.spent) remaining=$($s.remaining) used=$($s.percentUsed)%  (expected 890 / 2610 / 25%)"

$sum = Get "/projects/$pid_/report/summary?year=2026&month=10"
Write-Host "Summary: income=$($sum.actualIncome) expenses=$($sum.actualExpenses) balance=$($sum.actualBalance) savings=$($sum.savingsRatePercent)%"

# בדיקת מנוע התובנות הפיננסיות (Insights Engine)
$insights = Get "/projects/$pid_/insights?year=2026&month=10"
Write-Host "Insights Total: $($insights.totalInsights) | Unread: $($insights.unreadCount) | Savings Rate: $($insights.savingsRatePercent)% | Annual Projection: ₪$($insights.projectedAnnualSurplus)"
foreach ($item in $insights.insights) {
    Write-Host " -> [$($item.severity)] $($item.title): $($item.description)"
}

$rec = Get "/projects/$pid_/report/reconcile?year=2026&month=10&openingBalance=1000&bankBalance=$(1000 + $sum.actualBalance)"
Write-Host "Reconcile perfect: $($rec.isPerfect) gap=$($rec.gap)"

$close = Post "/projects/$pid_/report/close" @{ year = 2026; month = 10; bankBalance = (1000 + $sum.actualBalance); openingBalance = 1000 }
Write-Host "Close: $($close.message)"
try { $null = Post "/projects/$pid_/report/close" @{ year = 2026; month = 10; openingBalance = 0 } } catch { Write-Host "Second close rejected (expected): $($_.Exception.Response.StatusCode)" }
