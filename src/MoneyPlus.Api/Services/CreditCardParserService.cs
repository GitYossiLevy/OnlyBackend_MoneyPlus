using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MoneyPlus.Api.Dtos;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MoneyPlus.Api.Services;

/// <summary>
/// תוצאת פענוח קובץ פירוט אשראי כולל מטא-דאטה של הכרטיס, המנפיק, תאריך חיוב וחודש החשבונית.
/// </summary>
public record CreditCardParseResult(
    List<CreditCardStatementItemDto> Items,
    string? DetectedCard4Digits,
    string DetectedFormat,
    string? DetectedIssuer = null,
    DateOnly? StatementBillingDate = null,
    int? StatementBillingYear = null,
    int? StatementBillingMonth = null,
    List<string>? DetectedMultiCardDigits = null,
    string? DetectedCardOwner = null
);

public interface ICreditCardParserService
{
    (List<CreditCardStatementItemDto> Items, string? DetectedCard4Digits, string DetectedFormat) Parse(
        Stream stream, string fileName, int targetYear, int targetMonth);

    CreditCardParseResult ParseWithMetadata(
        Stream stream, string fileName, int targetYear, int targetMonth);
}

public class CreditCardParserService : ICreditCardParserService
{
    private static readonly Regex FullDateRegex = new(@"(?<!\d)(\d{1,2})[/.-](\d{1,2})[/.-](\d{2,4})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex IsoDateRegex = new(@"(?<!\d)(\d{4})[/.-](\d{1,2})[/.-](\d{1,2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ShortDateRegex = new(@"(?<!\d)(\d{1,2})[/.-](\d{1,2})(?!\d|[/.-]\d)", RegexOptions.Compiled);
    private static readonly Regex MonthYearRegex = new(@"(?<!\d)(0?[1-9]|1[0-2])[/.-](20\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex AmountTokenRegex = new(@"(-?\s*[₪$€]?\s*(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?:\.[0-9]{1,2})?\s*[₪$€]?-?)", RegexOptions.Compiled);
    private static readonly Regex InstallmentsRegex = new(@"(\d{1,2})\s*(?:מתוך|מ|/)\s*(\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex CardDigitsRegex = new(
        @"(?:כרטיס|ויזה|מספר|מס['׳]|סיום|ספרות|לכרטיס|card|ending)\s*(?:[\u0590-\u05FF""'׳-]{1,14}[\s-]*){0,3}?(?:[:*xX-]*\s*)?(\d{4})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MaskedCardDigitsRegex = new(@"(?:\*{4}|[xX]{4}|ב-)[\s-]*(\d{4})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex BillingDateCueRegex = new(
        @"(?:תאריך\s*חיוב|מועד\s*חיוב|לחיוב\s*בתאריך|יום\s*חיוב|חיוב\s*ב-?)\s*[:\s]*(\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4})",
        RegexOptions.Compiled);
    private static readonly Regex HebrewRunRegex = new(@"[\u0590-\u05FF""'׳״]+", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> HebrewMonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ינואר"] = 1, ["פברואר"] = 2, ["מרץ"] = 3, ["מרס"] = 3, ["אפריל"] = 4,
        ["מאי"] = 5, ["יוני"] = 6, ["יולי"] = 7, ["אוגוסט"] = 8,
        ["ספטמבר"] = 9, ["אוקטובר"] = 10, ["נובמבר"] = 11, ["דצמבר"] = 12,
    };

    private static readonly string[] LogicalHebrewCues =
        ["תאריך", "עסקה", "חיוב", "סכום", "כרטיס", "עסק", "תשלום", "יתרה", "הכל", "ישראכרט", "מקס", "כאל", "מועד", "פירוט"];
    private static readonly string[] ReversedHebrewCues =
        ["ךיראת", "הקסע", "בויח", "םוכס", "סיטרכ", "קסע", "םולשת", "הרתי", "לכה", "טרכארשי", "טרקארשי", "סקמ", "לאכ", "דעומ", "טוריפ"];
    private static readonly string[] HebrewBigramCues =
        ["בית עסק", "תאריך עסקה", "סכום חיוב", "סכום עסקה", "מועד חיוב", "תאריך חיוב", "כרטיס המסתיים", "פירוט חיובים", "פירוט עסקאות", "סך הכל", "תחנת דלק"];

    public (List<CreditCardStatementItemDto> Items, string? DetectedCard4Digits, string DetectedFormat) Parse(
        Stream stream, string fileName, int targetYear, int targetMonth)
    {
        var full = ParseWithMetadata(stream, fileName, targetYear, targetMonth);
        return (full.Items, full.DetectedCard4Digits, full.DetectedFormat);
    }

    public CreditCardParseResult ParseWithMetadata(
        Stream stream, string fileName, int targetYear, int targetMonth)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".xlsx" or ".xls")
            return ParseExcel(stream, fileName, targetYear, targetMonth);
        if (ext is ".csv" or ".txt")
            return ParseCsv(stream, fileName, targetYear, targetMonth);
        if (ext is ".pdf")
            return ParsePdf(stream, fileName, targetYear, targetMonth);

        throw new NotSupportedException($"פורמט הקובץ '{ext}' אינו נתמך. יש להעלות קובץ מסוג Excel (.xlsx, .xls), CSV או PDF.");
    }

    private static string CleanBusinessName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var clean = Regex.Replace(raw.Trim(), @"\s+", " ");
        clean = Regex.Replace(clean, @"\b(בע""מ|בעמ|בע'מ|בע״מ)\b", "", RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"[\""\'\`]", "").Trim();
        clean = clean.Trim('-', '|', ':', ',', '.').Trim();
        return clean;
    }

    private static string? DetectIssuer(string text, string fileName)
    {
        var combined = $"{fileName} {text}";
        if (Regex.IsMatch(combined, @"ישראכרט|Isracard", RegexOptions.IgnoreCase)) return "ישראכרט";
        if (Regex.IsMatch(combined, @"אמריקן\s*אקספרס|Amex|American\s*Express", RegexOptions.IgnoreCase)) return "אמריקן אקספרס";
        if (Regex.IsMatch(combined, @"כאל|CAL\b|ויזה\s*כאל", RegexOptions.IgnoreCase)) return "ויזה כאל";
        if (Regex.IsMatch(combined, @"מקס|MAX\b|לאומי\s*קארד|Leumi\s*Card", RegexOptions.IgnoreCase)) return "מקס";
        if (Regex.IsMatch(combined, @"דיינרס|Diners", RegexOptions.IgnoreCase)) return "דיינרס";
        if (Regex.IsMatch(combined, @"ויזה|VISA", RegexOptions.IgnoreCase)) return "ויזה";
        return null;
    }

    private static readonly Regex BidiControlsRegex = new(@"[\u200E\u200F\u202A-\u202E\u2066-\u2069]", RegexOptions.Compiled);
    private static readonly Regex CardDashDigitsRegex = new(
        @"(?:כרטיס|ויזה|מאסטרקארד|מסטרקארד|ישראכרט|אמריקן|דיינרס|קורפורייט|visa|mastercard|isracard|amex)[^\d\r\n]{0,40}?[-–—:\s]\s*(\d{4})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? TryExtractCardDigits(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = BidiControlsRegex.Replace(text, "");
        var m = CardDigitsRegex.Match(clean);
        if (m.Success) return m.Groups[1].Value;
        var m2 = MaskedCardDigitsRegex.Match(clean);
        if (m2.Success) return m2.Groups[1].Value;
        var m3 = CardDashDigitsRegex.Match(clean);
        return m3.Success ? m3.Groups[1].Value : null;
    }

    private static (DateOnly? BillingDate, int? Year, int? Month) ExtractBillingPeriod(
        IEnumerable<string> headerLines,
        string fileName,
        List<CreditCardStatementItemDto> items,
        DateOnly? fallbackChargeDate = null)
    {
        DateOnly? billingDate = fallbackChargeDate;
        int? billYear = fallbackChargeDate?.Year;
        int? billMonth = fallbackChargeDate?.Month;

        foreach (var raw in headerLines.Concat([fileName]))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (!billingDate.HasValue)
            {
                var cueMatch = BillingDateCueRegex.Match(raw);
                if (cueMatch.Success && TryParseDate(cueMatch.Groups[1].Value, DateTime.Now.Year, DateTime.Now.Month, out var parsedDt))
                {
                    billingDate = parsedDt;
                    billYear = parsedDt.Year;
                    billMonth = parsedDt.Month;
                }
            }

            if (!billMonth.HasValue)
            {
                foreach (var kvp in HebrewMonthMap)
                {
                    var hm = Regex.Match(raw, $@"{Regex.Escape(kvp.Key)}\s*[-/,]?\s*(20\d{{2}})");
                    if (hm.Success)
                    {
                        billMonth = kvp.Value;
                        billYear = int.Parse(hm.Groups[1].Value);
                        break;
                    }
                }
            }

            if (!billMonth.HasValue && (raw.Contains("חודש") || raw.Contains("חיוב") || raw.Contains("פירוט") || raw == fileName))
            {
                var my = MonthYearRegex.Match(raw);
                if (my.Success)
                {
                    billMonth = int.Parse(my.Groups[1].Value);
                    billYear = int.Parse(my.Groups[2].Value);
                }
            }
        }

        if (!billYear.HasValue && billingDate.HasValue)
        {
            billYear = billingDate.Value.Year;
            billMonth = billingDate.Value.Month;
        }

        // Fall back to dominant transaction month when statement headers carry no explicit billing period
        if (!billMonth.HasValue && items.Count > 0)
        {
            var dominant = items
                .GroupBy(i => (i.Date.Year, i.Date.Month))
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key.Year * 100 + g.Key.Month)
                .First().Key;
            billYear = dominant.Year;
            billMonth = dominant.Month;
        }

        return (billingDate, billYear, billMonth);
    }

    private CreditCardParseResult FinalizeResult(
        List<CreditCardStatementItemDto> items,
        string? primaryDigits,
        string baseFormat,
        IEnumerable<string> headerLines,
        string fileName,
        DateOnly? fallbackChargeDate = null)
    {
        var allHeaderText = string.Join(" ", headerLines);
        var issuer = DetectIssuer(allHeaderText, fileName);
        var formatLabel = issuer != null ? $"{issuer} ({baseFormat})" : baseFormat;

        if (string.IsNullOrEmpty(primaryDigits))
            primaryDigits = TryExtractCardDigits(fileName);

        var distinctItemDigits = items
            .Select(i => i.CardLast4Digits)
            .Where(d => !string.IsNullOrEmpty(d))
            .Cast<string>()
            .Distinct()
            .ToList();

        if (string.IsNullOrEmpty(primaryDigits) && distinctItemDigits.Count == 1)
            primaryDigits = distinctItemDigits[0];

        var (billDate, billYear, billMonth) = ExtractBillingPeriod(headerLines, fileName, items, fallbackChargeDate);
        var detectedOwner = TryExtractCardOwner(headerLines);

        return new CreditCardParseResult(
            Items: items,
            DetectedCard4Digits: primaryDigits,
            DetectedFormat: formatLabel,
            DetectedIssuer: issuer,
            StatementBillingDate: billDate,
            StatementBillingYear: billYear,
            StatementBillingMonth: billMonth,
            DetectedMultiCardDigits: distinctItemDigits.Count > 1 ? distinctItemDigits : null,
            DetectedCardOwner: detectedOwner
        );
    }

    private static string? TryExtractCardOwner(IEnumerable<string> headerLines)
    {
        var ownerRegex = new Regex(
            @"(?:לכבוד|שם\s*(?:הלקוח|בעל\s*הכרטיס|מחזיק\s*הכרטיס|החבר)|בעל\s*הכרטיס|מחזיק\s*הכרטיס|עבור)\s*[:\-]\s*([\u0590-\u05FFA-Za-z\s""'׳]{2,35})",
            RegexOptions.IgnoreCase);

        foreach (var line in headerLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = ownerRegex.Match(line);
            if (!m.Success) continue;

            var candidate = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
            // Remove common non-name trailing cues if captured on same line
            candidate = Regex.Split(candidate, @"\b(?:ת\.?ז|ח\.?פ|תאריך|מספר|כרטיס|מועד|סניף|חשבון)\b", RegexOptions.IgnoreCase)[0].Trim();
            if (candidate.Length >= 2 && candidate.Length <= 30 && !candidate.Contains("ישראכרט") && !candidate.Contains("ויזה") && !candidate.Contains("מקס"))
            {
                return candidate;
            }
        }
        return null;
    }

    // ==================== 1. EXCEL + HTML/XML .XLS FALLBACK ====================

    private CreditCardParseResult ParseExcel(Stream stream, string fileName, int targetYear, int targetMonth)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        try
        {
            using var workbook = new XLWorkbook(ms);
            if (!workbook.Worksheets.Any())
                throw new InvalidOperationException("קובץ האקסל אינו מכיל גיליונות.");

            var allItems = new List<CreditCardStatementItemDto>();
            var allHeaders = new List<string>();
            string? primaryDigits = null;
            DateOnly? chargeDateHint = null;

            foreach (var worksheet in workbook.Worksheets)
            {
                var rows = new List<List<string>>();
                int maxRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
                int maxCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
                if (maxRow == 0 || maxCol == 0) continue;

                for (int r = 1; r <= maxRow; r++)
                {
                    var row = worksheet.Row(r);
                    var rowCells = new List<string>(maxCol);
                    bool any = false;
                    for (int c = 1; c <= maxCol; c++)
                    {
                        var cell = row.Cell(c);
                        string val;
                        if (cell.DataType == XLDataType.DateTime)
                            val = cell.GetDateTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                        else
                            val = cell.GetString().Trim();
                        if (!string.IsNullOrEmpty(val)) any = true;
                        rowCells.Add(val);
                    }
                    if (any) rows.Add(rowCells);
                }

                if (rows.Count == 0) continue;
                var sheetResult = ParseTabularRows(rows, "Excel", fileName, targetYear, targetMonth, primaryDigits);
                allItems.AddRange(sheetResult.Items);
                if (allHeaders.Count < 45)
                    allHeaders.AddRange(rows.Take(12).Select(rc => string.Join(" ", rc.Where(c => !string.IsNullOrWhiteSpace(c)))));
                primaryDigits ??= sheetResult.DetectedCard4Digits;
                chargeDateHint ??= sheetResult.StatementBillingDate;
            }

            return FinalizeResult(allItems, primaryDigits, "Excel", allHeaders, fileName, chargeDateHint);
        }
        catch (Exception) when (Path.GetExtension(fileName).Equals(".xls", StringComparison.OrdinalIgnoreCase))
        {
            // Israeli banks/card issuers frequently serve UTF-8 or Windows-1255 HTML tables with .xls extension
            ms.Position = 0;
            var content = ReadTextWithHebrewFallback(ms);
            var tableRows = ExtractHtmlOrXmlTableRows(content);
            if (tableRows.Count > 0)
                return ParseTabularRows(tableRows, "Excel/HTML", fileName, targetYear, targetMonth);
            throw new NotSupportedException("קובץ .xls בינארי ישן אינו נתמך ישירות. שמור כ-.xlsx, CSV או PDF וטען מחדש.");
        }
    }

    private static List<List<string>> ExtractHtmlOrXmlTableRows(string content)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrWhiteSpace(content)) return rows;

        var trMatches = Regex.Matches(content, @"<(?:tr|ss:Row|Row)\b[^>]*>(.*?)</(?:tr|ss:Row|Row)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match tr in trMatches)
        {
            var cells = new List<string>();
            var tdMatches = Regex.Matches(tr.Groups[1].Value, @"<(?:t[dh]|ss:Data|Data)\b[^>]*>(.*?)</(?:t[dh]|ss:Data|Data)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match td in tdMatches)
            {
                var plain = Regex.Replace(td.Groups[1].Value, @"<[^>]+>", " ");
                plain = WebUtility.HtmlDecode(plain);
                plain = Regex.Replace(plain, @"\s+", " ").Trim();
                cells.Add(plain);
            }
            if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                rows.Add(cells);
        }
        return rows;
    }

    private static string ReadTextWithHebrewFallback(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = reader.ReadToEnd();
        if (content.Contains('\uFFFD'))
        {
            stream.Seek(0, SeekOrigin.Begin);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var win1255Reader = new StreamReader(stream, Encoding.GetEncoding("windows-1255"), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            content = win1255Reader.ReadToEnd();
        }
        return content;
    }

    // ==================== 2. CSV ====================

    private CreditCardParseResult ParseCsv(Stream stream, string fileName, int targetYear, int targetMonth)
    {
        var content = ReadTextWithHebrewFallback(stream);
        char delimiter = ',';
        var firstNonEmpty = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        if (firstNonEmpty.Count(c => c == '\t') > firstNonEmpty.Count(c => c == ',')) delimiter = '\t';
        else if (firstNonEmpty.Count(c => c == ';') > firstNonEmpty.Count(c => c == ',')) delimiter = ';';

        var rows = ParseCsvContent(content, delimiter);
        return ParseTabularRows(rows, "CSV", fileName, targetYear, targetMonth);
    }

    private static List<List<string>> ParseCsvContent(string content, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    row.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                    row.Add(sb.ToString().Trim());
                    sb.Clear();
                    rows.Add(row);
                    row = new List<string>();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        if (sb.Length > 0 || row.Count > 0)
        {
            row.Add(sb.ToString().Trim());
            rows.Add(row);
        }

        return rows;
    }

    // ==================== SHARED TABULAR PARSER (EXCEL / CSV / HTML-XLS) ====================

    private CreditCardParseResult ParseTabularRows(
        List<List<string>> rows,
        string baseFormat,
        string fileName,
        int targetYear,
        int targetMonth,
        string? inheritedDigits = null)
    {
        var items = new List<CreditCardStatementItemDto>();
        var headerLines = new List<string>();
        string? primaryDigits = inheritedDigits;
        string? currentDigits = inheritedDigits;

        int headerIdx = -1;
        int dateCol = -1, chargeDateCol = -1, nameCol = -1, billAmtCol = -1, dealAmtCol = -1, installCol = -1, cardCol = -1, currencyCol = -1;

        static bool TryMatchTabularHeader(
            List<string> cells,
            out int tDate, out int tChargeDate, out int tName,
            out int tBill, out int tDeal, out int tInst, out int tCard, out int tCurr)
        {
            tDate = -1; tChargeDate = -1; tName = -1; tBill = -1; tDeal = -1; tInst = -1; tCard = -1; tCurr = -1;
            if (cells.Count(c => !string.IsNullOrWhiteSpace(c)) < 3) return false;

            for (int col = 0; col < cells.Count; col++)
            {
                var val = cells[col].Trim();
                if (string.IsNullOrEmpty(val)) continue;

                if (tChargeDate == -1 && (val.Contains("תאריך חיוב") || val.Contains("מועד חיוב") || val.Contains("חיוב בחשבון")))
                    tChargeDate = col;
                else if (tDate == -1 && (val.Contains("תאריך עסקה") || val.Contains("תאריך רכישה") || val.Contains("תאריך") || val.Equals("Date", StringComparison.OrdinalIgnoreCase)))
                    tDate = col;
                else if (tName == -1 && (val.Contains("שם בית") || val.Contains("שם עסק") || val.Contains("בית עסק") || val.Contains("בית העסק") || val.Contains("תיאור") || val.Equals("Description", StringComparison.OrdinalIgnoreCase) || val.Equals("Merchant", StringComparison.OrdinalIgnoreCase)))
                    tName = col;
                else if (tBill == -1 && (val.Contains("סכום לחיוב") || val.Contains("סכום חיוב") || val.Contains("חיוב בפועל") || val.Contains("סכום בש\"ח") || val.Contains("סכום בש״ח") || val.Contains("חיוב בש\"ח") || val.Contains("חיוב בש״ח") || val.Contains("סכום חיוב בש") || val.Contains("סכום לחיוב בש") || val.Contains("לחיוב בש")))
                    tBill = col;
                else if (tDeal == -1 && (val.Contains("סכום עסקה") || val.Contains("סכום מקורי") || val.Contains("סכום העסקה") || val.Contains("סכום במט\"ח") || val.Contains("סכום במט״ח") || val.Equals("Amount", StringComparison.OrdinalIgnoreCase) || val == "סכום"))
                    tDeal = col;
                else if (tCurr == -1 && (val.Contains("מטבע") || val.Equals("Currency", StringComparison.OrdinalIgnoreCase)))
                    tCurr = col;
                else if (tInst == -1 && (val.Contains("תשלום") || val.Contains("פירוט")))
                    tInst = col;
                else if (tCard == -1 && (val.Contains("כרטיס") || val.Contains("4 ספרות") || val.Contains("ספרות")))
                    tCard = col;
            }

            if (tDate != -1 && tName != -1 && (tBill != -1 || tDeal != -1))
            {
                if (tBill == -1) tBill = tDeal;
                if (tDeal == -1) tDeal = tBill;
                return true;
            }
            return false;
        }

        for (int r = 0; r < Math.Min(rows.Count, 35); r++)
        {
            var cells = rows[r];
            var joined = string.Join(" ", cells.Where(c => !string.IsNullOrWhiteSpace(c)));
            headerLines.Add(joined);

            var digits = TryExtractCardDigits(joined);
            if (!string.IsNullOrEmpty(digits) && string.IsNullOrEmpty(primaryDigits))
                primaryDigits = currentDigits = digits;

            if (TryMatchTabularHeader(cells, out var tDate, out var tChargeDate, out var tName, out var tBill, out var tDeal, out var tInst, out var tCard, out var tCurr))
            {
                headerIdx = r;
                dateCol = tDate;
                chargeDateCol = tChargeDate;
                nameCol = tName;
                billAmtCol = tBill;
                dealAmtCol = tDeal;
                installCol = tInst;
                cardCol = tCard;
                currencyCol = tCurr;
                break;
            }
        }

        if (headerIdx == -1)
        {
            dateCol = 0; nameCol = 1; dealAmtCol = 2; billAmtCol = 2;
        }

        DateOnly? chargeDateHint = null;
        for (int r = headerIdx + 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            var joined = string.Join(" ", cells.Where(c => !string.IsNullOrWhiteSpace(c)));
            if (joined.Length == 0) continue;

            // Detect mid-file section card divider (consolidated multi-card Excel exports)
            var sectionDigits = TryExtractCardDigits(joined);
            if (!string.IsNullOrEmpty(sectionDigits) && (joined.Contains("כרטיס") || joined.Contains("סה\"כ") == false))
            {
                currentDigits = sectionDigits;
                primaryDigits ??= sectionDigits;
            }

            // Re-detect secondary table headers in the same sheet (e.g., Foreign Currency / Abroad section)
            if (TryMatchTabularHeader(cells, out var nDate, out var nChargeDate, out var nName, out var nBill, out var nDeal, out var nInst, out var nCard, out var nCurr))
            {
                dateCol = nDate;
                chargeDateCol = nChargeDate;
                nameCol = nName;
                billAmtCol = nBill;
                dealAmtCol = nDeal;
                installCol = nInst;
                cardCol = nCard;
                currencyCol = nCurr;
                continue;
            }

            if (cells.Count <= Math.Max(dateCol, Math.Max(nameCol, billAmtCol))) continue;

            var dateVal = cells[dateCol];
            var nameVal = cells[nameCol];
            var billAmtVal = cells[billAmtCol];

            if (string.IsNullOrWhiteSpace(nameVal) || IsSummaryRow(nameVal)) continue;
            if (!TryParseDate(dateVal, targetYear, targetMonth, out var dt)) continue;

            DateOnly? rowChargeDate = null;
            if (chargeDateCol != -1 && chargeDateCol < cells.Count &&
                TryParseDate(cells[chargeDateCol], targetYear, targetMonth, out var cDt))
            {
                rowChargeDate = cDt;
                chargeDateHint ??= cDt;
            }

            // עסקאות בחיוב מחוץ למועד (למשל עסקת חו"ל שנקלטה באוגוסט אך חויבה בבנק באוקטובר)
            string? outOfCycleNote = null;
            if (rowChargeDate.HasValue && Math.Abs(rowChargeDate.Value.DayNumber - dt.DayNumber) > 20)
            {
                outOfCycleNote = $"תאריך רכישה: {dt:dd/MM/yyyy} | חיוב בבנק: {rowChargeDate.Value:dd/MM/yyyy}";
                dt = rowChargeDate.Value;
            }

            var billAmt = ParseDecimal(billAmtVal);
            var dealAmt = dealAmtCol != -1 && dealAmtCol < cells.Count ? ParseDecimal(cells[dealAmtCol]) : billAmt;
            if (billAmt <= 0 && dealAmt > 0) billAmt = dealAmt;
            if (billAmt <= 0) continue;

            var (curInstall, totInstall) = ExtractInstallments(
                installCol != -1 && installCol < cells.Count ? $"{cells[installCol]} {nameVal}" : nameVal);

            string? rowCardDigits = currentDigits ?? primaryDigits;
            if (cardCol != -1 && cardCol < cells.Count)
            {
                var cm = Regex.Match(cells[cardCol], @"\b(\d{4})\b");
                if (cm.Success)
                {
                    rowCardDigits = cm.Groups[1].Value;
                    primaryDigits ??= rowCardDigits;
                }
            }

            string? foreignNote = null;
            if (currencyCol != -1 && currencyCol < cells.Count)
            {
                var curr = cells[currencyCol].Trim();
                if (!string.IsNullOrWhiteSpace(curr) && !curr.Contains("₪") && !curr.Contains("ש\"ח") && !curr.Contains("ש״ח") && !curr.Equals("ILS", StringComparison.OrdinalIgnoreCase))
                {
                    foreignNote = $"עסקת מט\"ח ({curr} {dealAmt:N2})";
                }
            }

            var combinedNote = string.Join(" | ", new[] { foreignNote, outOfCycleNote }.Where(x => !string.IsNullOrWhiteSpace(x)));
            items.Add(new CreditCardStatementItemDto(
                RawBusinessName: nameVal,
                CleanBusinessName: CleanBusinessName(nameVal),
                Date: dt,
                BillingAmount: Math.Abs(billAmt),
                DealAmount: dealAmt > 0 ? Math.Abs(dealAmt) : Math.Abs(billAmt),
                InstallmentCurrent: curInstall,
                InstallmentTotal: totInstall,
                CardLast4Digits: rowCardDigits,
                Notes: string.IsNullOrWhiteSpace(combinedNote) ? null : combinedNote
            ));
        }

        return FinalizeResult(items, primaryDigits, baseFormat, headerLines, fileName, chargeDateHint);
    }

    // ==================== 3. SPATIAL RTL-AWARE PDF STATEMENT PARSER ====================

    private sealed class PdfColumnMap
    {
        public double? TxDateX { get; set; }
        public double? ChargeDateX { get; set; }
        public double? BillAmountX { get; set; }
        public double? DealAmountX { get; set; }
        public double? InstallmentX { get; set; }
        public double? CardDigitsX { get; set; }
        public double? MerchantMinX { get; set; }
        public double? MerchantMaxX { get; set; }
    }

    private CreditCardParseResult ParsePdf(Stream stream, string fileName, int targetYear, int targetMonth)
    {
        using var doc = PdfDocument.Open(stream);
        var pages = doc.GetPages().ToList();
        var allWordsPerPage = pages.Select(p => p.GetWords().ToList()).ToList();

        bool isVisualRtl = DetectVisualRtlDocument(allWordsPerPage);
        bool wordsFlowRtlX = DetectWordsFlowRtlX(allWordsPerPage, isVisualRtl);

        var items = new List<CreditCardStatementItemDto>();
        var headerLines = new List<string>();
        string? primaryDigits = null;
        string? activeSectionDigits = null;
        DateOnly? chargeDateHint = null;
        PdfColumnMap? colMap = null;

        for (int pIdx = 0; pIdx < pages.Count; pIdx++)
        {
            var words = allWordsPerPage[pIdx];
            if (words.Count == 0) continue;

            var lineGroups = ClusterWordsByLine(words, yTolerance: 4.5);

            foreach (var lineWords in lineGroups)
            {
                var lineOrderedLeft = lineWords.OrderBy(w => w.BoundingBox.Left).ToList();
                var rawLineText = NormalizeLineForHeaderScan(lineOrderedLeft, isVisualRtl, wordsFlowRtlX);
                var altLineText = NormalizeLineForHeaderScan(lineOrderedLeft, isVisualRtl, !wordsFlowRtlX);
                if (string.IsNullOrWhiteSpace(rawLineText)) continue;

                if (headerLines.Count < 45)
                {
                    headerLines.Add(rawLineText);
                    if (altLineText != rawLineText)
                        headerLines.Add(altLineText);
                }

                // Track card section switches inside consolidated multi-card statements
                var lineCardDigits = TryExtractCardDigits(rawLineText) ?? TryExtractCardDigits(altLineText);
                if (!string.IsNullOrEmpty(lineCardDigits))
                {
                    activeSectionDigits = lineCardDigits;
                    primaryDigits ??= lineCardDigits;
                }

                // Detect table column header rows and update horizontal column anchors
                var detectedHeaders = TryDetectPdfColumnMap(lineOrderedLeft, isVisualRtl, wordsFlowRtlX);
                if (detectedHeaders != null)
                {
                    colMap = detectedHeaders;
                    continue;
                }

                if (IsSummaryRow(rawLineText) || IsHeaderMetadataLine(rawLineText) || IsHeaderMetadataLine(altLineText))
                    continue;

                var parsed = ParsePdfTransactionLine(
                    lineOrderedLeft, rawLineText, colMap, isVisualRtl, wordsFlowRtlX,
                    activeSectionDigits ?? primaryDigits, targetYear, targetMonth, ref chargeDateHint);
                if (parsed != null)
                {
                    if (!string.IsNullOrEmpty(parsed.CardLast4Digits))
                        primaryDigits ??= parsed.CardLast4Digits;
                    items.Add(parsed);
                }
            }
        }

        // Secondary recovery pass if spatial parser yielded nothing on non-standard plain-text PDFs
        if (items.Count == 0)
        {
            foreach (var page in pages)
            {
                foreach (var rawLine in page.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var normalized = isVisualRtl ? ReverseHebrewRunsInString(rawLine) : rawLine.Trim();
                    if (IsSummaryRow(normalized) || IsHeaderMetadataLine(normalized)) continue;
                    var fallbackItem = ParseLooseTextLine(normalized, primaryDigits, targetYear, targetMonth);
                    if (fallbackItem != null) items.Add(fallbackItem);
                }
            }
        }

        return FinalizeResult(items, primaryDigits, "PDF", headerLines, fileName, chargeDateHint);
    }

    private static bool DetectWordsFlowRtlX(List<List<Word>> pagesWords, bool isVisualRtl)
    {
        if (isVisualRtl) return true;

        int rtlHits = 0;
        int ltrHits = 0;
        foreach (var words in pagesWords.Take(2))
        {
            foreach (var line in ClusterWordsByLine(words, yTolerance: 4.5).Take(20))
            {
                var rtlLine = string.Join(" ", line.OrderByDescending(w => w.BoundingBox.Right)
                    .Select(w => NormalizeWordText(w.Text, false)));
                var ltrLine = string.Join(" ", line.OrderBy(w => w.BoundingBox.Left)
                    .Select(w => NormalizeWordText(w.Text, false)));

                foreach (var cue in HebrewBigramCues)
                {
                    if (rtlLine.Contains(cue)) rtlHits++;
                    if (ltrLine.Contains(cue)) ltrHits++;
                }
            }
        }

        return rtlHits >= ltrHits;
    }

    private static bool IsHeaderMetadataLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.Contains("תאריך עסקה")
            || line.Contains("שם בית עסק")
            || line.Contains("מועד חיוב")
            || line.Contains("תאריך חיוב:")
            || line.Contains("תאריך חיוב ")
            || line.Contains("חשבון חודש")
            || line.Contains("פירוט חיובים")
            || line.Contains("פירוט עסקאות")
            || line.Contains("כרטיס המסתיים")
            || line.Contains("מסגרת אשראי")
            || line.Contains("לכבוד ");
    }

    private static bool DetectVisualRtlDocument(List<List<Word>> pagesWords)
    {
        int normalScore = 0;
        int reversedScore = 0;

        foreach (var words in pagesWords.Take(3))
        {
            foreach (var w in words)
            {
                var txt = w.Text;
                if (LogicalHebrewCues.Any(c => txt.Contains(c))) normalScore++;
                if (ReversedHebrewCues.Any(c => txt.Contains(c))) reversedScore++;
            }
        }

        return reversedScore > normalScore;
    }

    private static List<List<Word>> ClusterWordsByLine(List<Word> words, double yTolerance)
    {
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var lines = new List<List<Word>>();
        var currentLine = new List<Word>();
        double anchorY = 0;

        foreach (var w in sorted)
        {
            double y = w.BoundingBox.Bottom;
            if (currentLine.Count == 0)
            {
                currentLine.Add(w);
                anchorY = y;
            }
            else if (Math.Abs(y - anchorY) <= yTolerance)
            {
                currentLine.Add(w);
                anchorY = currentLine.Average(x => x.BoundingBox.Bottom);
            }
            else
            {
                lines.Add(currentLine);
                currentLine = [w];
                anchorY = y;
            }
        }

        if (currentLine.Count > 0) lines.Add(currentLine);
        return lines;
    }

    private static string NormalizeWordText(string text, bool isVisualRtl)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (!isVisualRtl && !ReversedHebrewCues.Any(text.Contains))
            return text.Trim();
        return ReverseHebrewRunsInString(text).Trim();
    }

    private static string ReverseHebrewRunsInString(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return HebrewRunRegex.Replace(input, m =>
        {
            var chars = m.Value.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }).Trim();
    }

    private static string NormalizeLineForHeaderScan(List<Word> wordsLeftToRight, bool isVisualRtl, bool wordsFlowRtlX)
    {
        var ordered = wordsFlowRtlX
            ? wordsLeftToRight.OrderByDescending(w => w.BoundingBox.Right)
            : wordsLeftToRight.OrderBy(w => w.BoundingBox.Left);

        return string.Join(" ", ordered
            .Select(w => NormalizeWordText(w.Text, isVisualRtl))
            .Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private static PdfColumnMap? TryDetectPdfColumnMap(List<Word> wordsLeftToRight, bool isVisualRtl, bool wordsFlowRtlX)
    {
        // Group adjacent words closely positioned horizontally (X gap < 14pt) into logical column headers
        var phrases = new List<(string Text, double CenterX, double MinX, double MaxX)>();
        var cur = new List<Word>();

        foreach (var w in wordsLeftToRight)
        {
            if (cur.Count == 0 || (w.BoundingBox.Left - cur[^1].BoundingBox.Right) <= 14.0)
            {
                cur.Add(w);
            }
            else
            {
                phrases.Add(BuildHeaderPhrase(cur, isVisualRtl, wordsFlowRtlX));
                cur = [w];
            }
        }
        if (cur.Count > 0) phrases.Add(BuildHeaderPhrase(cur, isVisualRtl, wordsFlowRtlX));

        var map = new PdfColumnMap();
        int matchedCols = 0;

        foreach (var (txt, cx, minX, maxX) in phrases)
        {
            if (txt.Contains("תאריך חיוב") || txt.Contains("מועד חיוב"))
            {
                map.ChargeDateX ??= cx;
                matchedCols++;
            }
            else if (txt.Contains("תאריך") || txt.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                map.TxDateX ??= cx;
                matchedCols++;
            }
            else if (txt.Contains("סכום לחיוב") || txt.Contains("סכום חיוב") || txt.Contains("לחיוב") || txt.Contains("בחיוב") || txt.Contains("חיוב בש") || txt.Contains("לחיוב בש"))
            {
                map.BillAmountX ??= cx;
                matchedCols++;
            }
            else if (txt.Contains("סכום עסקה") || txt.Contains("סכום מקורי") || txt == "סכום" || txt.Contains("סכום בש") || txt.Contains("במט\"ח") || txt.Contains("במט״ח") || txt.Contains("סכום במט"))
            {
                map.DealAmountX ??= cx;
                matchedCols++;
            }
            else if (txt.Contains("בית עסק") || txt.Contains("בית העסק") || txt.Contains("שם עסק") || txt.Contains("שם בית") || txt.Contains("תיאור") || txt == "עסק")
            {
                map.MerchantMinX = minX - 65;
                map.MerchantMaxX = maxX + 65;
                matchedCols++;
            }
            else if (txt.Contains("תשלום") || txt.Contains("הערות") || txt.Contains("פירוט"))
            {
                map.InstallmentX ??= cx;
            }
            else if (txt.Contains("כרטיס") || txt.Contains("4 ספרות") || txt.Contains("ספרות"))
            {
                map.CardDigitsX ??= cx;
            }
        }

        if (matchedCols >= 2 && (map.TxDateX.HasValue || map.ChargeDateX.HasValue) && (map.BillAmountX.HasValue || map.DealAmountX.HasValue))
        {
            map.TxDateX ??= map.ChargeDateX;
            map.BillAmountX ??= map.DealAmountX;
            return map;
        }

        return null;
    }

    private static (string Text, double CenterX, double MinX, double MaxX) BuildHeaderPhrase(List<Word> words, bool isVisualRtl, bool wordsFlowRtlX)
    {
        var baseSeq = wordsFlowRtlX
            ? words.OrderByDescending(w => w.BoundingBox.Right)
            : words.OrderBy(w => w.BoundingBox.Left);
        var ordered = baseSeq
            .Select(w => NormalizeWordText(w.Text, isVisualRtl))
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var minX = words.Min(w => w.BoundingBox.Left);
        var maxX = words.Max(w => w.BoundingBox.Right);
        return (string.Join(" ", ordered), (minX + maxX) / 2.0, minX, maxX);
    }

    private sealed record PdfNumericToken(
        Word Word,
        decimal Value,
        double CenterX,
        bool HasCurrencyIls,
        bool HasForeignCurrency,
        bool HasDecimalPoint,
        bool IsNegative
    );

    private CreditCardStatementItemDto? ParsePdfTransactionLine(
        List<Word> wordsLeftToRight,
        string normalizedLine,
        PdfColumnMap? colMap,
        bool isVisualRtl,
        bool wordsFlowRtlX,
        string? defaultCardDigits,
        int targetYear,
        int targetMonth,
        ref DateOnly? chargeDateHint)
    {
        var usedWords = new HashSet<Word>();
        var rowDates = new List<(Word Word, DateOnly Date, double CenterX)>();

        foreach (var w in wordsLeftToRight)
        {
            var token = w.Text.Trim();
            if (TryParseDate(token, targetYear, targetMonth, out var parsedDt))
            {
                rowDates.Add((w, parsedDt, (w.BoundingBox.Left + w.BoundingBox.Right) / 2.0));
                usedWords.Add(w);
            }
        }

        if (rowDates.Count == 0) return null;

        // Determine transaction date vs billing/charge date using column anchors when available
        DateOnly txDate = rowDates[0].Date;
        if (rowDates.Count >= 2)
        {
            if (colMap?.TxDateX != null)
            {
                var bestTx = rowDates.OrderBy(d => Math.Abs(d.CenterX - colMap.TxDateX.Value)).First();
                txDate = bestTx.Date;
                var otherDate = rowDates.FirstOrDefault(d => !ReferenceEquals(d.Word, bestTx.Word));
                if (!chargeDateHint.HasValue && otherDate.Word != null)
                    chargeDateHint = otherDate.Date;
            }
            else
            {
                // Transaction date is chronologically <= charge date when both appear
                var orderedByChrono = rowDates.OrderBy(d => d.Date).ToList();
                txDate = orderedByChrono[0].Date;
                if (!chargeDateHint.HasValue)
                    chargeDateHint = orderedByChrono[^1].Date;
            }
        }

        // Scan installment patterns on full line & mark participating words so "2 מתוך 6" never leaks into merchant text
        var (curInstall, totInstall) = ExtractInstallments(normalizedLine);
        if (curInstall.HasValue && totInstall.HasValue)
        {
            for (int i = 0; i < wordsLeftToRight.Count; i++)
            {
                var norm = NormalizeWordText(wordsLeftToRight[i].Text, isVisualRtl);
                if (norm is "מתוך" or "תשלום")
                {
                    usedWords.Add(wordsLeftToRight[i]);
                    if (i > 0 && int.TryParse(wordsLeftToRight[i - 1].Text.Trim(), out _))
                        usedWords.Add(wordsLeftToRight[i - 1]);
                    if (i + 1 < wordsLeftToRight.Count && int.TryParse(wordsLeftToRight[i + 1].Text.Trim(), out _))
                        usedWords.Add(wordsLeftToRight[i + 1]);
                }
            }
        }

        // Identify candidate amount tokens (excluding dates, installment ratios, voucher integers, card digits)
        string? rowCardDigits = defaultCardDigits;
        var numericTokens = new List<PdfNumericToken>();

        foreach (var w in wordsLeftToRight)
        {
            if (usedWords.Contains(w)) continue;
            var raw = w.Text.Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            // Check explicit installment token like "2/6"
            if (Regex.IsMatch(raw, @"^\d{1,2}/\d{1,2}$"))
            {
                usedWords.Add(w);
                continue;
            }

            // Check card column 4-digit match
            double cx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2.0;
            if (colMap?.CardDigitsX != null && Math.Abs(cx - colMap.CardDigitsX.Value) <= 35 && Regex.IsMatch(raw, @"^\d{4}$"))
            {
                rowCardDigits = raw;
                usedWords.Add(w);
                continue;
            }

            if (!Regex.IsMatch(raw, @"\d")) continue;

            // Only treat word as monetary candidate if it cleanly parses as numeric/currency value (not "פוקס 10" or "קניון 365")
            if (TryParsePdfAmountToken(raw, colMap, cx, rowCardDigits, out var parsedToken, w))
            {
                numericTokens.Add(parsedToken);
            }
        }

        if (numericTokens.Count == 0) return null;

        // Pick billing amount & deal amount cleanly
        var ilsTokens = numericTokens.Where(t => !t.HasForeignCurrency).ToList();
        var pool = ilsTokens.Count > 0 ? ilsTokens : numericTokens;

        PdfNumericToken billingTok;
        PdfNumericToken? dealTok = null;

        if (colMap?.BillAmountX != null)
        {
            billingTok = pool.OrderBy(t => Math.Abs(t.CenterX - colMap.BillAmountX.Value)).First();
            if (colMap.DealAmountX != null && pool.Count > 1)
                dealTok = pool.Where(t => !ReferenceEquals(t.Word, billingTok.Word))
                              .OrderBy(t => Math.Abs(t.CenterX - colMap.DealAmountX.Value))
                              .FirstOrDefault();
        }
        else
        {
            // Prefer decimal/currency-tagged tokens over bare integers
            var weighted = pool.OrderByDescending(t => (t.HasCurrencyIls ? 2 : 0) + (t.HasDecimalPoint ? 1 : 0)).ToList();
            if (pool.Count >= 2 && weighted[0].HasDecimalPoint && pool[0].HasDecimalPoint)
            {
                // Standard Israeli RTL tables place Billing Amount on the leftmost column (smallest CenterX)
                billingTok = pool.OrderBy(t => t.CenterX).First();
                dealTok = pool.OrderBy(t => t.CenterX).Last();
            }
            else
            {
                billingTok = weighted.First();
            }
        }

        if (billingTok.Value <= 0) return null;

        usedWords.Add(billingTok.Word);
        if (dealTok != null) usedWords.Add(dealTok.Word);

        // Reconstruct business description strictly from non-date/non-amount/non-currency-marker words
        var merchantWords = wordsLeftToRight
            .Where(w => !usedWords.Contains(w) && !IsNoiseSymbolToken(w.Text))
            .ToList();

        // Filter words to merchant column band when column bounds are wide enough and clear
        if (colMap?.MerchantMinX != null && colMap.MerchantMaxX != null && merchantWords.Count > 1)
        {
            var inBand = merchantWords
                .Where(w => w.BoundingBox.Right >= colMap.MerchantMinX.Value && w.BoundingBox.Left <= colMap.MerchantMaxX.Value)
                .ToList();
            if (inBand.Count > 0)
                merchantWords = inBand;
        }

        var merchantName = ReconstructMerchantText(merchantWords, isVisualRtl, wordsFlowRtlX);
        if (string.IsNullOrWhiteSpace(merchantName) || merchantName.Length < 2 || IsSummaryRow(merchantName))
            return null;

        return new CreditCardStatementItemDto(
            RawBusinessName: merchantName,
            CleanBusinessName: CleanBusinessName(merchantName),
            Date: txDate,
            BillingAmount: Math.Abs(billingTok.Value),
            DealAmount: dealTok != null && dealTok.Value > 0 ? Math.Abs(dealTok.Value) : Math.Abs(billingTok.Value),
            InstallmentCurrent: curInstall,
            InstallmentTotal: totInstall,
            CardLast4Digits: rowCardDigits,
            Notes: curInstall.HasValue && totInstall.HasValue ? $"תשלום {curInstall} מתוך {totInstall}" : null
        );
    }

    private static bool TryParsePdfAmountToken(
        string raw,
        PdfColumnMap? colMap,
        double cx,
        string? knownCardDigits,
        out PdfNumericToken token,
        Word word)
    {
        token = default!;
        bool hasForeign = raw.Contains('$') || raw.Contains('€') || raw.Contains('£') ||
                          raw.Contains("USD", StringComparison.OrdinalIgnoreCase) ||
                          raw.Contains("EUR", StringComparison.OrdinalIgnoreCase) ||
                          raw.Contains("GBP", StringComparison.OrdinalIgnoreCase) ||
                          raw.Contains("CHF", StringComparison.OrdinalIgnoreCase) ||
                          raw.Contains("JPY", StringComparison.OrdinalIgnoreCase) ||
                          raw.Contains("THB", StringComparison.OrdinalIgnoreCase) ||
                          raw.Contains("CAD", StringComparison.OrdinalIgnoreCase) ||
                          raw.Contains("AUD", StringComparison.OrdinalIgnoreCase);
        bool hasIls = raw.Contains('₪');
        bool hasDecimal = Regex.IsMatch(raw, @"\.\d{1,2}\b");

        // Strip known currency symbols & commas to validate purely numeric shape
        var stripped = Regex.Replace(raw, @"(USD|EUR|GBP|CHF|JPY|THB|CAD|AUD|ILS)", "", RegexOptions.IgnoreCase)
            .Replace("₪", "").Replace("$", "").Replace("€", "").Replace("£", "").Replace(",", "").Trim();
        bool isNegative = stripped.StartsWith('-') || stripped.EndsWith('-');
        stripped = stripped.Trim('-').Trim();

        if (!Regex.IsMatch(stripped, @"^\d+(?:\.\d{1,2})?$"))
            return false;

        // Reject card last-4-digits or long voucher/reference integers (>= 5 digits without decimal)
        if (!hasDecimal && !hasIls)
        {
            if (stripped == knownCardDigits) return false;
            if (stripped.Length >= 5 && !stripped.Contains('.')) return false;

            // Reject integers appearing inside the Merchant column
            if (colMap?.MerchantMinX != null && colMap.MerchantMaxX != null &&
                cx >= colMap.MerchantMinX.Value && cx <= colMap.MerchantMaxX.Value)
                return false;
        }

        if (!decimal.TryParse(stripped, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            return false;
        if (amount <= 0 || isNegative)
            return false; // Skip refunds/credits instead of adding them as positive expenses

        token = new PdfNumericToken(word, amount, cx, hasIls, hasForeign, hasDecimal, isNegative);
        return true;
    }

    private static bool IsNoiseSymbolToken(string raw)
    {
        var t = raw.Trim();
        return t is "₪" or "$" or "€" or "£" or "-" or "|" or "/" or "מתוך" or "תשלום" or "ש\"ח" or "ש״ח" or "ILS" or "USD" or "EUR" or "GBP" or "CHF" or "JPY" or "THB" or "CAD" or "AUD";
    }

    private static string ReconstructMerchantText(List<Word> words, bool isVisualRtl, bool wordsFlowRtlX)
    {
        if (words.Count == 0) return "";

        if (!wordsFlowRtlX)
        {
            var orderedLtr = words.OrderBy(w => w.BoundingBox.Left)
                .Select(w => Regex.Replace(NormalizeWordText(w.Text, isVisualRtl), @"\b(תשלום|מתוך)\b", "").Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return Regex.Replace(string.Join(" ", orderedLtr), @"\s+", " ").Trim();
        }

        // Reconstruct visually Right-to-Left across cells while keeping contiguous Latin/number tokens in natural LTR order
        var orderedRtl = words.OrderByDescending(w => w.BoundingBox.Right).ToList();
        var tokens = new List<string>();
        var ltrBuffer = new List<string>();

        void FlushLtr()
        {
            if (ltrBuffer.Count == 0) return;
            ltrBuffer.Reverse(); // Reverse RTL-sorted English tokens back to LTR reading order
            tokens.AddRange(ltrBuffer);
            ltrBuffer.Clear();
        }

        foreach (var w in orderedRtl)
        {
            var fixedText = NormalizeWordText(w.Text, isVisualRtl);
            fixedText = Regex.Replace(fixedText, @"\b(תשלום|מתוך)\b", "").Trim();
            if (string.IsNullOrWhiteSpace(fixedText)) continue;

            bool hasHebrew = fixedText.Any(c => c >= 0x0590 && c <= 0x05FF);
            if (!hasHebrew && Regex.IsMatch(fixedText, @"[A-Za-z0-9]"))
            {
                ltrBuffer.Add(fixedText);
            }
            else
            {
                FlushLtr();
                tokens.Add(fixedText);
            }
        }
        FlushLtr();

        return Regex.Replace(string.Join(" ", tokens), @"\s+", " ").Trim();
    }

    private static CreditCardStatementItemDto? ParseLooseTextLine(
        string cleanLine, string? cardDigits, int targetYear, int targetMonth)
    {
        var dateMatch = FullDateRegex.Match(cleanLine);
        if (!dateMatch.Success) dateMatch = IsoDateRegex.Match(cleanLine);
        if (!dateMatch.Success) return null;

        if (!TryParseDate(dateMatch.Value, targetYear, targetMonth, out var dt)) return null;

        var amtMatches = AmountTokenRegex.Matches(cleanLine);
        var candidates = amtMatches
            .Select(m => m.Value)
            .Where(s => s.Contains('.') || s.Contains('₪'))
            .Select(ParseDecimal)
            .Where(v => v > 0)
            .ToList();

        if (candidates.Count == 0)
        {
            var fallback = amtMatches.Select(m => ParseDecimal(m.Value)).LastOrDefault(v => v > 0 && v < 100000);
            if (fallback <= 0) return null;
            candidates.Add(fallback);
        }

        var billAmt = candidates[^1];
        var desc = Regex.Replace(cleanLine, Regex.Escape(dateMatch.Value), "");
        foreach (Match m in amtMatches)
        {
            if (m.Value.Contains('.') || m.Value.Contains('₪'))
                desc = desc.Replace(m.Value, "");
        }
        desc = CleanBusinessName(desc);
        if (string.IsNullOrWhiteSpace(desc) || desc.Length < 2) return null;

        var (cur, tot) = ExtractInstallments(cleanLine);
        return new CreditCardStatementItemDto(
            RawBusinessName: desc,
            CleanBusinessName: desc,
            Date: dt,
            BillingAmount: billAmt,
            DealAmount: billAmt,
            InstallmentCurrent: cur,
            InstallmentTotal: tot,
            CardLast4Digits: cardDigits,
            Notes: cur.HasValue && tot.HasValue ? $"תשלום {cur} מתוך {tot}" : null
        );
    }

    // ==================== SHARED LOW-LEVEL HELPERS ====================

    private static bool IsSummaryRow(string text)
    {
        return text.Contains("סה\"כ") || text.Contains("סה״כ") || text.Contains("סהכ") || text.Contains("סך הכל") ||
               text.Contains("יתרה לתשלום") || text.Equals("Total", StringComparison.OrdinalIgnoreCase);
    }

    private static (int? Current, int? Total) ExtractInstallments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        // Avoid confusing dd/MM dates with installments like 12/10
        var cleaned = FullDateRegex.Replace(text, "");
        var m = InstallmentsRegex.Match(cleaned);
        if (!m.Success) return (null, null);
        if (int.TryParse(m.Groups[1].Value, out int c) && int.TryParse(m.Groups[2].Value, out int t) && t >= 2 && c >= 1 && c <= t)
            return (c, t);
        return (null, null);
    }

    private static string[] SplitCsvLine(string line, char delimiter)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    private static bool TryParseDate(string raw, int defaultYear, int defaultMonth, out DateOnly dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var iso = IsoDateRegex.Match(raw);
        if (iso.Success)
        {
            int y = int.Parse(iso.Groups[1].Value);
            int m = int.Parse(iso.Groups[2].Value);
            int d = int.Parse(iso.Groups[3].Value);
            if (y >= 2000 && m is >= 1 and <= 12 && d is >= 1 and <= 31)
            {
                try { dt = new DateOnly(y, m, d); return true; } catch { }
            }
        }

        var full = FullDateRegex.Match(raw);
        if (full.Success)
        {
            int day = int.Parse(full.Groups[1].Value);
            int month = int.Parse(full.Groups[2].Value);
            int year = int.Parse(full.Groups[3].Value);
            if (year < 100) year += 2000;
            if (month > 12 && day <= 12) (day, month) = (month, day);
            try { dt = new DateOnly(year, month, day); return true; } catch { return false; }
        }

        var shortMatch = ShortDateRegex.Match(raw);
        if (shortMatch.Success)
        {
            int day = int.Parse(shortMatch.Groups[1].Value);
            int month = int.Parse(shortMatch.Groups[2].Value);
            if (month is >= 1 and <= 12 && day is >= 1 and <= 31)
            {
                int year = month > defaultMonth + 1 ? defaultYear - 1 : defaultYear;
                try { dt = new DateOnly(year, month, day); return true; } catch { return false; }
            }
        }

        return false;
    }

    private static decimal ParseDecimal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var clean = Regex.Replace(raw, @"[^\d.-]", "");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : 0;
    }
}
