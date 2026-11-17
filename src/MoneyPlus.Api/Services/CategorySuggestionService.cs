using Microsoft.EntityFrameworkCore;
using MoneyPlus.Api.Data;
using MoneyPlus.Core.Entities;

namespace MoneyPlus.Api.Services;

public static class CategorySuggestionService
{
    private static readonly (string[] MerchantKeywords, string[] CategoryKeywords)[] KeywordMap =
    [
        (["שופרסל", "רמי לוי", "ויקטורי", "יוחננוף", "אושר עד", "חצי חינם", "מגה", "טיב טעם", "am:pm", "ampm", "yellow", "סופר", "מכולת", "ירקות", "מאפיה", "מאפיית", "shufersal", "rami levy"], ["מזון", "סופר", "מכולת", "קניות", "אוכל"]),
        (["wolt", "וולט", "תן ביס", "tenbis", "מקדונלד", "mcdonald", "ארומה", "aroma", "קפה", "cafe", "coffee", "מסעדה", "פיצה", "בורגר", "שווארמה", "סושי", "גלידה"], ["מסעדות", "בילויים", "אוכל בחוץ", "קפה", "פנאי"]),
        (["פז", "דלק", "סונול", "דור אלון", "ten", "תחנת דלק", "sonol", "paz", "delek"], ["דלק", "רכב", "תחבורה"]),
        (["פנגו", "pango", "סלופארק", "cellopark", "חניה", "כביש 6", "נתיב מהיר", "רכבת", "רב קו", "אגד", "דן", "gett", "גט", "מונית"], ["תחבורה", "חניה", "רכב", "נסיעות"]),
        (["סופר פארם", "super-pharm", "be", "פארם", "בית מרקחת", "קופת חולים", "מכבי", "כללית", "מאוחדת", "לאומית", "מרפאה", "רופא"], ["בריאות", "פארם", "רפואה", "תרופות"]),
        (["איקאה", "ikea", "אייס", "ace", "הום סנטר", "home center", "טמבור", "כלי בית"], ["בית", "תחזוקת בית", "ריהוט", "ציוד"]),
        (["זארה", "zara", "קסטרו", "castro", "פוקס", "fox", "רנואר", "hm", "h&m", "טרמינל", "terminal", "אסוס", "asos", "נייקי", "nike", "אדידס", "adidas"], ["ביגוד", "אופנה", "הלבשה", "שופינג", "קניות"]),
        (["פרטנר", "partner", "סלקום", "cellcom", "פלאפון", "pelephone", "הוט", "hot", "יס", "yes", "בזק", "bezeq", "נטפליקס", "netflix", "ספוטיפיי", "spotify", "apple", "google"], ["תקשורת", "אינטרנט", "סלולר", "מנויים", "טלוויזיה"]),
        (["חשמל", "חברת החשמל", "מים", "מי אביבים", "הגיחון", "ארנונה", "עיריית", "גז", "אמישראגז", "פזגז"], ["חשבונות", "דיור", "בית", "ארנונה", "חשמל", "מים"]),
        (["אמאזון", "amazon", "אליאקספרס", "aliexpress", "איביי", "ebay", "ksp", "אייבורי", "ivory", "באג", "bug"], ["שופינג", "קניות", "אלקטרוניקה", "מחשוב", "שונות"])
    ];

    public static async Task<(Guid? CategoryId, double Confidence)> SuggestCategoryAsync(
        AppDbContext db,
        Guid projectId,
        string merchantName,
        string rawText)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        if (categories.Count == 0)
            return (null, 0);

        var combined = $"{merchantName} {rawText}".Trim().ToLowerInvariant();
        var cleanMerchant = merchantName.Trim().ToLowerInvariant();

        // 1. התאמה היסטורית לפי תיאורי הוצאות קודמות בפרויקט
        if (!string.IsNullOrWhiteSpace(cleanMerchant))
        {
            var recentTxs = await db.Transactions
                .AsNoTracking()
                .Where(t => t.ProjectId == projectId && !string.IsNullOrEmpty(t.Description))
                .OrderByDescending(t => t.Date)
                .Take(350)
                .Select(t => new { t.CategoryId, Description = t.Description.ToLower() })
                .ToListAsync();

            var catIds = categories.Select(c => c.Id).ToHashSet();
            var matched = recentTxs
                .Where(t => catIds.Contains(t.CategoryId) &&
                            (t.Description.Contains(cleanMerchant) || cleanMerchant.Contains(t.Description)))
                .GroupBy(t => t.CategoryId)
                .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();

            if (matched is not null)
                return (matched.CategoryId, 0.92);
        }

        // 2. התאמת מילות מפתח בין שם בית העסק לשמות הקטגוריות בפרויקט
        foreach (var (merchantKeys, catKeys) in KeywordMap)
        {
            if (!merchantKeys.Any(k => combined.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            var matchedCat = categories.FirstOrDefault(c =>
            {
                var catName = c.Name.ToLowerInvariant();
                return catKeys.Any(ck => catName.Contains(ck, StringComparison.OrdinalIgnoreCase));
            });

            if (matchedCat is not null)
                return (matchedCat.Id, 0.78);
        }

        // 3. התאמה ישירה אם שם הקטגוריה מופיע בטקסט ההתראה
        var directCat = categories.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.Name) &&
            c.Name.Length >= 2 &&
            combined.Contains(c.Name.Trim().ToLowerInvariant()));

        if (directCat is not null)
            return (directCat.Id, 0.72);

        // 4. Fallback לקטגוריית "שונות" / "אחר" או קטגוריה לא-הון ראשונה
        var fallback = categories.FirstOrDefault(c =>
                           c.Name.Contains("שונות") || c.Name.Contains("אחר") || c.Name.Contains("כללי"))
                       ?? categories.FirstOrDefault(c => !c.IsCapitalTransfer)
                       ?? categories.First();

        return (fallback.Id, 0.35);
    }
}

