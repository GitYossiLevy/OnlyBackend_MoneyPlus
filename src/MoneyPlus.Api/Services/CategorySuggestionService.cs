using System.Text.RegularExpressions;
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
        (["פז", "דלק", "סונול", "דור אלון", "סדש", "סד\"ש", "ten", "תחנת דלק", "sonol", "paz", "delek"], ["דלק", "רכב", "תחבורה"]),
        (["פנגו", "pango", "סלופארק", "cellopark", "חניה", "כביש 6", "נתיב מהיר", "רכבת", "רב קו", "אגד", "דן", "gett", "גט", "מונית"], ["תחבורה", "חניה", "רכב", "נסיעות"]),
        (["סופר פארם", "super-pharm", "be", "פארם", "בית מרקחת", "קופת חולים", "מכבי", "כללית", "מאוחדת", "לאומית", "מרפאה", "רופא"], ["בריאות", "פארם", "רפואה", "תרופות"]),
        (["איקאה", "ikea", "אייס", "ace", "הום סנטר", "home center", "טמבור", "כלי בית"], ["בית", "תחזוקת בית", "ריהוט", "ציוד"]),
        (["זארה", "zara", "קסטרו", "castro", "פוקס", "fox", "רנואר", "hm", "h&m", "טרמינל", "terminal", "אסוס", "asos", "נייקי", "nike", "אדידס", "adidas"], ["ביגוד", "אופנה", "הלבשה", "שופינג", "קניות"]),
        (["פרטנר", "partner", "סלקום", "cellcom", "פלאפון", "pelephone", "הוט", "hot", "יס", "yes", "בזק", "bezeq", "נטפליקס", "netflix", "ספוטיפיי", "spotify", "apple", "google"], ["תקשורת", "אינטרנט", "סלולר", "מנויים", "טלוויזיה"]),
        (["חשמל", "חברת החשמל", "מים", "מי אביבים", "הגיחון", "ארנונה", "עיריית", "גז", "אמישראגז", "פזגז"], ["חשבונות", "דיור", "בית", "ארנונה", "חשמל", "מים"]),
        (["אמאזון", "amazon", "אליאקספרס", "aliexpress", "איביי", "ebay", "ksp", "אייבורי", "ivory", "באג", "bug"], ["שופינג", "קניות", "אלקטרוניקה", "מחשוב", "שונות"])
    ];

    public static string NormalizeMerchant(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var clean = raw.Trim().ToLowerInvariant();
        clean = Regex.Replace(clean, @"\b(בע""מ|בע״מ|בעמ|ltd|inc)\b", "", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"[\""\'׳״`.,\-/|]+", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Length > 80 ? clean[..80].Trim() : clean;
    }

    public static async Task<(Guid? CategoryId, double Confidence, bool IsLearnedRule)> SuggestCategoryWithSourceAsync(
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
            return (null, 0, false);

        var catIds = categories.Select(c => c.Id).ToHashSet();
        var normMerchant = NormalizeMerchant(merchantName);
        var combined = $"{merchantName} {rawText}".Trim().ToLowerInvariant();

        // 0. כללי למידה מפורשים (MerchantCategoryRules) — עדיפות ראשונה
        if (!string.IsNullOrWhiteSpace(normMerchant))
        {
            var rules = await db.MerchantCategoryRules
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .OrderByDescending(r => r.HitCount)
                .ThenByDescending(r => r.LastUsedAtUtc)
                .ToListAsync();

            var exactRule = rules.FirstOrDefault(r =>
                catIds.Contains(r.CategoryId) &&
                string.Equals(r.NormalizedMerchant, normMerchant, StringComparison.OrdinalIgnoreCase));

            var fuzzyRule = exactRule ?? rules.FirstOrDefault(r =>
                catIds.Contains(r.CategoryId) &&
                r.NormalizedMerchant.Length >= 3 &&
                (normMerchant.Contains(r.NormalizedMerchant, StringComparison.OrdinalIgnoreCase) ||
                 r.NormalizedMerchant.Contains(normMerchant, StringComparison.OrdinalIgnoreCase)));

            if (fuzzyRule is not null)
            {
                // אם בית העסק תוקן בעבר לקטגוריות שונות, שומרים ביטחון בינוני כדי שהמשתמש יוודא בחלונית
                var conf = fuzzyRule.CorrectionCount > 0 ? 0.72 : 0.96;
                return (fuzzyRule.CategoryId, conf, true);
            }
        }

        // 1. התאמה היסטורית לפי תיאורי הוצאות קודמות בפרויקט
        if (!string.IsNullOrWhiteSpace(normMerchant))
        {
            var recentTxs = await db.Transactions
                .AsNoTracking()
                .Where(t => t.ProjectId == projectId && !string.IsNullOrEmpty(t.Description))
                .OrderByDescending(t => t.Date)
                .Take(350)
                .Select(t => new { t.CategoryId, Description = t.Description.ToLower() })
                .ToListAsync();

            var matched = recentTxs
                .Where(t => catIds.Contains(t.CategoryId) &&
                            (t.Description.Contains(normMerchant) || normMerchant.Contains(t.Description)))
                .GroupBy(t => t.CategoryId)
                .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();

            if (matched is not null)
                return (matched.CategoryId, matched.Count >= 2 ? 0.90 : 0.82, false);
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
                return (matchedCat.Id, 0.78, false);
        }

        // 3. התאמה ישירה אם שם הקטגוריה מופיע בטקסט ההתראה
        var directCat = categories.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.Name) &&
            c.Name.Length >= 2 &&
            combined.Contains(c.Name.Trim().ToLowerInvariant()));

        if (directCat is not null)
            return (directCat.Id, 0.72, false);

        // 4. ללא זיהוי מובהק — החזרת null כדי שהמשתמש יבחר בקלות בחלונית החיפוש
        return (null, 0.0, false);
    }

    public static async Task<(Guid? CategoryId, double Confidence)> SuggestCategoryAsync(
        AppDbContext db,
        Guid projectId,
        string merchantName,
        string rawText)
    {
        var (catId, conf, _) = await SuggestCategoryWithSourceAsync(db, projectId, merchantName, rawText);
        return (catId, conf);
    }

    public static async Task LearnRuleAsync(
        AppDbContext db,
        Guid projectId,
        string merchantName,
        Guid categoryId,
        string? paymentMethodLabel)
    {
        var norm = NormalizeMerchant(merchantName);
        if (string.IsNullOrWhiteSpace(norm) || norm.Length < 2) return;

        var existing = await db.MerchantCategoryRules
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.NormalizedMerchant == norm);

        if (existing is null)
        {
            db.MerchantCategoryRules.Add(new MerchantCategoryRule
            {
                ProjectId = projectId,
                NormalizedMerchant = norm,
                DisplayMerchant = merchantName.Trim(),
                CategoryId = categoryId,
                PaymentMethodLabel = string.IsNullOrWhiteSpace(paymentMethodLabel) ? null : paymentMethodLabel.Trim(),
                HitCount = 1,
                CorrectionCount = 0,
                LastUsedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            if (existing.CategoryId != categoryId)
            {
                existing.CategoryId = categoryId;
                existing.CorrectionCount += 1;
            }
            existing.DisplayMerchant = merchantName.Trim();
            if (!string.IsNullOrWhiteSpace(paymentMethodLabel))
                existing.PaymentMethodLabel = paymentMethodLabel.Trim();
            existing.HitCount += 1;
            existing.LastUsedAtUtc = DateTime.UtcNow;
        }
    }
}
