using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MoneyPlus.Api.Services;

public record ParsedTransactionRow(
    DateOnly Date,
    string CategoryName,
    string Description,
    decimal Amount,
    string PaymentMethod,
    string UserName,
    bool IsIncome = false,
    decimal? PlannedAmount = null
);

public interface IPdfReportParser
{
    List<ParsedTransactionRow> Parse(Stream pdfStream, int defaultYear, int defaultMonth);
}

public class PdfReportParser : IPdfReportParser
{
    private static readonly Regex DateRegex = new(@"\b(\d{1,2})[/.-](\d{1,2})[/.-](\d{2,4})\b", RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]{1,2})?|[0-9]+(?:\.[0-9]{1,2})?)", RegexOptions.Compiled);

    public List<ParsedTransactionRow> Parse(Stream pdfStream, int defaultYear, int defaultMonth)
    {
        var results = new List<ParsedTransactionRow>();

        using var doc = PdfDocument.Open(pdfStream);
        if (doc.NumberOfPages == 0) return results;

        if (DetectBudgetPlanReport(doc))
        {
            results = ParseBudgetPlanReport(doc, defaultYear, defaultMonth);
        }
        else if (DetectSplitReport(doc))
        {
            results = ParseSplitColumnReport(doc, defaultYear, defaultMonth);
        }
        else
        {
            results = ParseStandardTableReport(doc, defaultYear, defaultMonth);
        }

        return results;
    }

    private static bool DetectBudgetPlanReport(PdfDocument doc)
    {
        var p1 = doc.GetPage(1).Text;
        return (p1.Contains("תכנון") || p1.Contains("ןונכת")) &&
               (p1.Contains("ביצוע") || p1.Contains("עוציב")) &&
               (p1.Contains("הכנסות") || p1.Contains("תוסנכה") || p1.Contains("הוצאות") || p1.Contains("תואצוה"));
    }

    private static List<ParsedTransactionRow> ParseBudgetPlanReport(PdfDocument doc, int defaultYear, int defaultMonth)
    {
        var results = new List<ParsedTransactionRow>();
        bool currentSectionIsIncome = true;

        for (int pageNum = 1; pageNum <= Math.Min(3, doc.NumberOfPages); pageNum++)
        {
            var page = doc.GetPage(pageNum);
            var words = page.GetWords().ToList();
            if (words.Count == 0) continue;

            var lineGroups = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 5.0) * 5.0)
                .OrderByDescending(g => g.Key);

            foreach (var group in lineGroups)
            {
                var lineWords = group.OrderBy(w => w.BoundingBox.Left).ToList();
                var lineText = string.Join(" ", lineWords.Select(w => w.Text));

                // Switch section
                if (lineText.Contains("הכנסות") || lineText.Contains("תוסנכה"))
                {
                    currentSectionIsIncome = true;
                    continue;
                }
                if (lineText.Contains("הוצאות") || lineText.Contains("תואצוה"))
                {
                    currentSectionIsIncome = false;
                    continue;
                }

                // Skip header or totals lines
                if (lineText.Contains("תכנון") || lineText.Contains("ןונכת") ||
                    lineText.Contains("ביצוע") || lineText.Contains("עוציב") ||
                    lineText.Contains("סה\"כ") || lineText.Contains("לכה ךס") ||
                    lineText.Contains("סך הכל") || lineText.Contains("יתרה (") || lineText.Contains("הרתי"))
                {
                    continue;
                }

                // In table:
                // Left column: Actual (Left < 300)
                // Middle column: Planned (300 <= Left < 420)
                // Right column: Category Name (Left >= 420)
                var actualWords = group.Where(w => w.BoundingBox.Left < 300).OrderBy(w => w.BoundingBox.Left).ToList();
                var plannedWords = group.Where(w => w.BoundingBox.Left >= 300 && w.BoundingBox.Left < 420).OrderBy(w => w.BoundingBox.Left).ToList();
                var catWords = group.Where(w => w.BoundingBox.Left >= 420).OrderByDescending(w => w.BoundingBox.Left).ToList();

                var catName = FixHebrewPhrase(catWords);
                if (string.IsNullOrWhiteSpace(catName)) continue;

                // Clean prefix letters if present like "א ", "אא ", "אאא "
                catName = Regex.Replace(catName, @"^[א-ת]{1,3}\s+", "").Trim();

                decimal actual = 0;
                if (actualWords.Count > 0)
                {
                    var clean = string.Join("", actualWords.Select(w => w.Text)).Replace("₪", "").Replace(",", "").Trim();
                    decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out actual);
                }

                decimal planned = 0;
                if (plannedWords.Count > 0)
                {
                    var clean = string.Join("", plannedWords.Select(w => w.Text)).Replace("₪", "").Replace(",", "").Trim();
                    decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out planned);
                }

                results.Add(new ParsedTransactionRow(
                    Date: new DateOnly(defaultYear, defaultMonth, 10),
                    CategoryName: catName,
                    Description: catName,
                    Amount: actual,
                    PaymentMethod: "כרטיס אשראי",
                    UserName: "יוסי",
                    IsIncome: currentSectionIsIncome,
                    PlannedAmount: planned
                ));
            }
        }

        return results;
    }

    private static bool DetectSplitReport(PdfDocument doc)
    {
        if (doc.NumberOfPages < 2) return false;

        var half = doc.NumberOfPages / 2;
        var p1 = doc.GetPage(1).Text;
        var pSecondHalf = doc.GetPage(half + 1).Text;

        bool p1HasDate = p1.Contains("תאריך") || p1.Contains("ךיראת") || p1.Contains("קטגוריה") || p1.Contains("הירוגטק");
        bool p2HasAmount = pSecondHalf.Contains("סכום") || pSecondHalf.Contains("םוכס") || pSecondHalf.Contains("אמצעי") || pSecondHalf.Contains("םולשת") || pSecondHalf.Contains("עצבמ");

        return p1HasDate && p2HasAmount;
    }

    private static string FixHebrewWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return "";
        bool hasHebrew = false;
        foreach (char c in word)
        {
            if (c >= 0x0590 && c <= 0x05FF) { hasHebrew = true; break; }
        }
        if (!hasHebrew) return word;
        var chars = word.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static string FixHebrewPhrase(IEnumerable<Word> wordsInReadingOrder)
    {
        var fixedWords = wordsInReadingOrder.Select(w => FixHebrewWord(w.Text)).Where(t => !string.IsNullOrWhiteSpace(t));
        return string.Join(" ", fixedWords).Trim();
    }

    private static string NormalizePaymentMethod(List<Word> words)
    {
        var raw = string.Join(" ", words.Select(w => w.Text)).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "כרטיס אשראי";

        if (raw.Contains("2520")) return "יוסי 2520";
        if (raw.Contains("0643")) return "יוסי 0643";
        if (raw.Contains("5528")) return "עסק 5528";
        if (raw.Contains("ןמוזמ") || raw.Contains("מזומן")) return "מזומן";
        if (raw.Contains("םימולשת") || raw.Contains("תשלומים")) return "תשלומים";
        if (raw.Contains("הרבעה") || raw.Contains("העברה")) return "העברה בנקאית";
        if (raw.Contains("עובק בויח") || raw.Contains("חיוב קבוע") || raw.Contains("עבק תארוה") || raw.Contains("הוראת קבע")) return "חיוב קבוע";

        var phrase = FixHebrewPhrase(words.OrderByDescending(w => w.BoundingBox.Left));
        return string.IsNullOrWhiteSpace(phrase) ? "כרטיס אשראי" : phrase;
    }

    private static string NormalizeUserName(List<Word> words, string paymentMethod)
    {
        var raw = string.Join(" ", words.Select(w => w.Text)).Trim();
        if (raw.Contains("יסוי") || raw.Contains("יוסי")) return "יוסי";
        if (raw.Contains("רבנע") || raw.Contains("ענבר")) return "ענבר";

        if (paymentMethod.Contains("יוסי") || paymentMethod.Contains("עסק")) return "יוסי";
        return "ענבר";
    }

    private static List<ParsedTransactionRow> ParseSplitColumnReport(PdfDocument doc, int defaultYear, int defaultMonth)
    {
        var results = new List<ParsedTransactionRow>();
        int half = doc.NumberOfPages / 2;

        for (int p = 1; p <= half; p++)
        {
            var leftPage = doc.GetPage(p);
            int rightPageNum = half + p;
            if (rightPageNum > doc.NumberOfPages) break;
            var rightPage = doc.GetPage(rightPageNum);

            var leftRows = ExtractLeftRows(leftPage, defaultYear, defaultMonth);
            var rightRows = ExtractRightRows(rightPage);

            int count = Math.Min(leftRows.Count, rightRows.Count);
            for (int i = 0; i < count; i++)
            {
                var left = leftRows[i];
                var right = rightRows[i];

                if (right.Amount <= 0) continue;

                results.Add(new ParsedTransactionRow(
                    Date: left.Date,
                    CategoryName: string.IsNullOrWhiteSpace(left.Category) ? "כללי" : left.Category.Trim(),
                    Description: left.Description?.Trim() ?? "",
                    Amount: right.Amount,
                    PaymentMethod: string.IsNullOrWhiteSpace(right.PaymentMethod) ? "כרטיס אשראי" : right.PaymentMethod.Trim(),
                    UserName: string.IsNullOrWhiteSpace(right.UserName) ? "ענבר" : right.UserName.Trim(),
                    IsIncome: false
                ));
            }
        }

        return results;
    }

    private class LeftRowData
    {
        public double Y { get; set; }
        public DateOnly Date { get; set; }
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private class RightRowData
    {
        public double Y { get; set; }
        public decimal Amount { get; set; }
        public string PaymentMethod { get; set; } = "";
        public string UserName { get; set; } = "";
    }

    private static List<LeftRowData> ExtractLeftRows(Page page, int defaultYear, int defaultMonth)
    {
        var rows = new List<LeftRowData>();
        var words = page.GetWords().ToList();
        if (words.Count == 0) return rows;

        var lineGroups = words
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 4.0) * 4.0)
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var group in lineGroups)
        {
            var lineWords = group.OrderBy(w => w.BoundingBox.Left).ToList();
            var lineText = string.Join(" ", lineWords.Select(w => w.Text));

            if (lineText.Contains("תאריך") || lineText.Contains("ךיראת") || lineText.Contains("לכה ךס") || lineText.Contains("סך הכל"))
                continue;

            var dateMatch = DateRegex.Match(lineText);
            if (!dateMatch.Success) continue;

            DateOnly date;
            if (!DateOnly.TryParseExact(dateMatch.Value, new[] { "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                date = new DateOnly(defaultYear, defaultMonth, 15);
            }

            // Columns by X:
            // Date: Left >= 490
            // Category: 370 <= Left < 490
            // Description: Left < 370
            var catWords = group.Where(w => w.BoundingBox.Left >= 370 && w.BoundingBox.Left < 490).OrderByDescending(w => w.BoundingBox.Left).ToList();
            var descWords = group.Where(w => w.BoundingBox.Left < 370).OrderByDescending(w => w.BoundingBox.Left).ToList();

            var category = FixHebrewPhrase(catWords);
            var description = FixHebrewPhrase(descWords);

            rows.Add(new LeftRowData
            {
                Y = group.Key,
                Date = date,
                Category = string.IsNullOrWhiteSpace(category) ? "כללי" : category,
                Description = description
            });
        }

        return rows;
    }

    private static List<RightRowData> ExtractRightRows(Page page)
    {
        var rows = new List<RightRowData>();
        var words = page.GetWords().ToList();
        if (words.Count == 0) return rows;

        var lineGroups = words
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 4.0) * 4.0)
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var group in lineGroups)
        {
            var lineWords = group.OrderBy(w => w.BoundingBox.Left).ToList();
            var lineText = string.Join(" ", lineWords.Select(w => w.Text));

            if (lineText.Contains("םוכס") || lineText.Contains("סכום") || lineText.Contains("הקסעה") || lineText.Contains("לכה ךס") || lineText.Contains("סך הכל"))
                continue;

            decimal amount = 0;
            var amtWords = group.Where(w => w.BoundingBox.Left >= 500).OrderBy(w => w.BoundingBox.Left).ToList();
            if (amtWords.Count > 0)
            {
                var clean = string.Join("", amtWords.Select(w => w.Text)).Replace("₪", "").Replace(",", "").Trim();
                decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
            }

            if (amount <= 0)
            {
                var amtMatch = AmountRegex.Match(lineText);
                if (amtMatch.Success)
                {
                    var clean = amtMatch.Value.Replace("₪", "").Replace(",", "").Trim();
                    decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
                }
            }

            if (amount <= 0) continue;

            // Payment method words: 425 <= Left < 500
            var methodWords = group.Where(w => w.BoundingBox.Left >= 425 && w.BoundingBox.Left < 500).OrderBy(w => w.BoundingBox.Left).ToList();
            var paymentMethod = NormalizePaymentMethod(methodWords);

            // User words: Left < 425
            var userWords = group.Where(w => w.BoundingBox.Left < 425).OrderBy(w => w.BoundingBox.Left).ToList();
            var userName = NormalizeUserName(userWords, paymentMethod);

            rows.Add(new RightRowData
            {
                Y = group.Key,
                Amount = amount,
                PaymentMethod = paymentMethod,
                UserName = userName
            });
        }

        return rows;
    }

    private static List<ParsedTransactionRow> ParseStandardTableReport(PdfDocument doc, int defaultYear, int defaultMonth)
    {
        var results = new List<ParsedTransactionRow>();

        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0) continue;

            var lineGroups = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 4.0) * 4.0)
                .OrderByDescending(g => g.Key);

            foreach (var group in lineGroups)
            {
                var lineText = string.Join(" ", group.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
                if (lineText.Contains("תאריך") || lineText.Contains("ךיראת") || lineText.Contains("סך הכל") || lineText.Contains("לכה ךס")) continue;

                var dateMatch = DateRegex.Match(lineText);
                if (!dateMatch.Success) continue;

                DateOnly date;
                if (!DateOnly.TryParseExact(dateMatch.Value, new[] { "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    date = new DateOnly(defaultYear, defaultMonth, 15);
                }

                decimal amount = 0;
                var amountMatch = AmountRegex.Match(lineText);
                if (amountMatch.Success)
                {
                    var clean = amountMatch.Value.Replace("₪", "").Replace(",", "").Trim();
                    decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
                }

                if (amount <= 0) continue;

                var remaining = lineText.Replace(dateMatch.Value, "").Replace(amountMatch.Value, "").Trim();

                results.Add(new ParsedTransactionRow(
                    Date: date,
                    CategoryName: "כללי",
                    Description: remaining,
                    Amount: amount,
                    PaymentMethod: "כרטיס אשראי",
                    UserName: "ענבר",
                    IsIncome: false
                ));
            }
        }

        return results;
    }
}
