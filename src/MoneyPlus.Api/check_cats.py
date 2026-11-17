import sqlite3
import re

conn = sqlite3.connect('backend/src/MoneyPlus.Api/moneyplus.db')
c = conn.cursor()

def normalize_name(s):
    if not s:
        return ""
    no_emoji = re.sub(r'[\U00010000-\U0010ffff]|[\u200d\ufe0f\u203c-\u3299]', '', s)
    return no_emoji.replace('"', '').replace("'", '').replace('-', ' ').replace('/', ' ').strip().lower()

# Check projects
projects = c.execute("SELECT Id, Name FROM Projects").fetchall()
print("Projects:")
for p in projects:
    print(f"  {p[0]}: {p[1]}")

print("\n--- Testing transactions and their categories ---")
# Get all distinct category names that have transactions in 2026
tx_cats = c.execute("""
    SELECT DISTINCT c.Id, c.Name, c.IsCapitalTransfer, p.Name as ProjectName
    FROM Transactions t
    LEFT JOIN Categories c ON t.CategoryId = c.Id
    JOIN Projects p ON t.ProjectId = p.Id
    WHERE t.Date LIKE '2026-%'
    ORDER BY p.Name, c.Name
""").fetchall()

print(f"Total categories with transactions in 2026: {len(tx_cats)}")
for row in tx_cats:
    cid, cname, is_cap, pname = row
    cname = cname or "אחר (ללא קטגוריה)"
    print(f"[{pname}] {cname} (Capital: {is_cap})")

