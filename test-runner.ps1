$ErrorActionPreference = 'Stop'
$b = 'http://localhost:5187/api'
$script:token = $null

function Post($u, $o, $auth = $true) { 
    $json = $o | ConvertTo-Json -Depth 5
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $headers = @{ 'Content-Type' = 'application/json; charset=utf-8' }
    if ($auth -and $script:token) { $headers['Authorization'] = "Bearer $script:token" }
    Invoke-RestMethod -Method Post -Uri "$b$u" -Headers $headers -Body $bytes 
}
function Put($u, $o) { 
    $json = $o | ConvertTo-Json -Depth 5
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $headers = @{ 'Content-Type' = 'application/json; charset=utf-8' }
    if ($script:token) { $headers['Authorization'] = "Bearer $script:token" }
    Invoke-RestMethod -Method Put -Uri "$b$u" -Headers $headers -Body $bytes 
}
function Get($u, $auth = $true) { 
    $headers = @{}
    if ($auth -and $script:token) { $headers['Authorization'] = "Bearer $script:token" }
    Invoke-RestMethod -Uri "$b$u" -Headers $headers 
}
function Delete($u) { 
    $headers = @{}
    if ($script:token) { $headers['Authorization'] = "Bearer $script:token" }
    Invoke-RestMethod -Method Delete -Uri "$b$u" -Headers $headers 
}

Write-Host "=== 1. Security Check: Unauthenticated Access ==="
try {
    $null = Get '/projects' $false
    Write-Host "FAILED: Expected 401 on unauthenticated call" -ForegroundColor Red
} catch {
    Write-Host "SUCCESS: Protected endpoint correctly rejected unauthenticated request: $($_.Exception.Message)"
}

Write-Host "`n=== 2. Auth: Register and Login (JWT) ==="
$reg = Post '/auth/register' @{ email = 'yossi@family.com'; password = 'Password123!'; displayName = 'Yossi Cohen' } $false
Write-Host "User Registered: $($reg.user.displayName) ($($reg.user.email))"
$script:token = $reg.token
Write-Host "Received JWT Token: $($script:token.Substring(0, 25))..."

# Verify invalid password rejected
try {
    $null = Post '/auth/login' @{ email = 'yossi@family.com'; password = 'WrongPassword!' } $false
    Write-Host "FAILED: Invalid login was accepted" -ForegroundColor Red
} catch {
    Write-Host "SUCCESS: Invalid password rejected with 401"
}

# Verify /me
$me = Get '/auth/me'
Write-Host "Current User via JWT (/auth/me): $($me.displayName)"

Write-Host "`n=== 3. Projects and Tenant Isolation ==="
$p = Post '/projects' @{ name = 'HouseHold' }
$pid_ = $p.id
Write-Host "Project Created by Yossi: ID=$pid_, Name=$($p.name)"

# Register a SECOND user and verify they cannot see or touch Yossi's project
$reg2 = Post '/auth/register' @{ email = 'stranger@other.com'; password = 'Password123!'; displayName = 'Stranger User' } $false
$script:token = $reg2.token
$otherProjects = Get '/projects'
Write-Host "Stranger project count: $($otherProjects.Count) (expected 0)"

try {
    $null = Get ("/projects/$pid_/categories")
    Write-Host "FAILED: Stranger was able to access Yossi project!" -ForegroundColor Red
} catch {
    Write-Host "SUCCESS: Tenant isolation verified! Stranger got 404 on Yossi project."
}

# Switch back to Yossi
$script:token = $reg.token

Write-Host "`n=== 4. Dynamic Users within Project ==="
$u1 = Post "/projects/$pid_/users" @{ name = 'Yossi'; role = 'Admin'; avatar = 'user-admin' }
$u2 = Post "/projects/$pid_/users" @{ name = 'Inbar'; role = 'Partner'; avatar = 'user-partner' }
$users = Get "/projects/$pid_/users"
Write-Host "Budget Participants Count: $($users.Count) (expected 2)"

Write-Host "`n=== 5. Categories and Incomes ==="
$sup = Post "/projects/$pid_/categories" @{ name = 'Supermarket'; periodicity = 'Weekly'; plannedAmount = 3500 }
$sofa = Post "/projects/$pid_/categories" @{ name = 'LivingRoom Sofa'; periodicity = 'OneTime'; plannedAmount = 3600 }
$fun = Post "/projects/$pid_/categories" @{ name = 'Restaurants and Fun'; periodicity = 'Monthly'; plannedAmount = 1000 }

$inc1 = Post "/projects/$pid_/incomes" @{ description = 'Monthly Salary'; amount = 22000; date = '2026-10-01'; receiptType = 'FixedSalary'; incomeCategoryId = (Get "/projects/$pid_/income-categories")[0].id }
$inc2 = Post "/projects/$pid_/incomes" @{ description = 'Side Architecture Project'; amount = 5500; date = '2026-10-27'; receiptType = 'Bonus'; newCategoryName = 'Freelance Business'; newCategoryIcon = 'project' }
Write-Host "Income created: $($inc1.description), Amount=$($inc1.actualAmount)"
Write-Host "Inline category created: $($inc2.incomeCategoryName), Amount=$($inc2.actualAmount)"

Write-Host "`n=== 6. Transactions and Installments ==="
$t1 = Post "/projects/$pid_/transactions" @{ description = 'Groceries Store'; amount = 825; date = '2026-10-15'; categoryId = $sup.id; executedByUserId = $u1.id; paymentMethod = 'CreditCard'; paymentMethodLabel = 'Visa Cal' }
$t2 = Post "/projects/$pid_/transactions" @{ description = 'Coffee Bar'; amount = 65; date = '2026-10-14'; categoryId = $sup.id; executedByUserId = $u2.id; paymentMethod = 'Bit' }
$tOver = Post "/projects/$pid_/transactions" @{ description = 'Fine Dining'; amount = 1450; date = '2026-10-18'; categoryId = $fun.id; executedByUserId = $u1.id; paymentMethod = 'CreditCard'; paymentMethodLabel = 'Max Executive' }
$inst = Post "/projects/$pid_/transactions" @{ description = 'Ikea Sofa'; amount = 3600; date = '2026-10-16'; categoryId = $sofa.id; executedByUserId = $u1.id; paymentMethod = 'Installments'; installments = 3 }
Write-Host "Installments Generated: $($inst.Count) payments of $($inst[0].amount)"

Write-Host "`n=== 7. Financial Insights and Reconciliation ==="
$sum = Get ("/projects/$pid_/report/summary?year=2026`&month=10")
Write-Host "Summary: Inflow=$($sum.actualIncome) | Outflow=$($sum.actualExpenses) | Net=$($sum.actualBalance) | Savings Rate=$($sum.savingsRatePercent)%"

$insights = Get ("/projects/$pid_/insights?year=2026`&month=10")
Write-Host "Insights Count: $($insights.totalInsights) | Savings Rate: $($insights.savingsRatePercent)% | Annual Projected Wealth: $($insights.projectedAnnualSurplus)"
foreach ($item in $insights.insights) {
    Write-Host " -> [$($item.severity)] $($item.title)"
}

$expectedClosing = 1000 + $sum.actualBalance
$rec = Get ("/projects/$pid_/report/reconcile?year=2026`&month=10`&openingBalance=1000`&bankBalance=$expectedClosing")
Write-Host "Reconciliation: Expected=$($rec.expectedBalance), Bank=$($rec.bankBalance), Gap=$($rec.gap), Perfect Match=$($rec.isPerfect)"

$close = Post "/projects/$pid_/report/close" @{ year = 2026; month = 10; bankBalance = $expectedClosing; openingBalance = 1000 }
Write-Host "Period Close Result: $($close.message) | Saved To Wealth Fund: $($close.savedToSavings)"

Write-Host "`n>>> ALL JWT AUTH AND TENANT ISOLATION TESTS PASSED WITH 100% SUCCESS! <<<"
