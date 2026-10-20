# BusPulse — המשך עבודה (אחרי Curlbus)

## מצב נוכחי
- Curlbus: ✅ מיוצא וגם עם בדיקות (14 בדיקות עוברות)
- Stride GTFS: ✅ סטטי, עדכני
- Stride SIRI: ❌ ישן ובלתי אמין (לא משמש)
- בנייה: ✅ 0 שגיאות

---

## שלוש החלטות בעניין הבדיקות שנעשו

### 1. TLS 1.3 בـ Program.cs
**החלטה:** הסר את הכפייה ל-TLS 1.3 בלבד. השתמש בברירת המחדל של המערכת (1.2 ומעלה).

ערוך `BusPulse/Program.cs`:
- חפש `ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls13;`
- החלף ל: `ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;`
- או פשוט הסר את השורה כולה

אחרי השינוי: `dotnet build` (צריך להצליח עם 0 שגיאות).

### 2. רכבת ישראל — ממתינה להחלטה
**אפשרות A (מומלץ):** שמור את ספק הרכבת מעוטל כברירת מחדל, חכה למפתח רשמי.
**אפשרות B:** הסר את הספק לעכשיו, השתמש רק בלוח הזמנים של Stride.

**השמר עם:** אם בחרת A, צור ספק עם יתרון בהגדרות:
```csharp
// BusPulse/Services/LiveData/RailProvider.cs
public class RailProvider : ILiveArrivalsProvider
{
    private readonly ILogger<RailProvider> _logger;
    private readonly string _apiKey;
    
    public RailProvider(ILogger<RailProvider> logger, IConfiguration config)
    {
        _logger = logger;
        _apiKey = config["RailApiKey"] ?? "";
    }
    
    public async Task<LiveArrivals> ProbeAsync(string lineCode, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogInformation("Rail API key not configured; skipping");
            return LiveArrivals.Empty();
        }
        // implement probe...
    }
}
```

אחרי כל שינוי: `dotnet build` (צריך להצליח).

### 3. docs/Claude Setup.exe
**החלטה:** הוסף ל-.gitignore, הסר מ-git.

```bash
echo "docs/Claude Setup.exe" >> .gitignore
git rm --cached "docs/Claude Setup.exe"
git add .gitignore
git commit -m "chore: exclude Claude Setup.exe from version control"
```

---

## השלבים הבאים (לפי סדר)

### Phase 2 המשך: ILiveDataAggregator
`BusPulse/Services/LiveData/LiveDataAggregator.cs`:
- קח Curlbus כראשון, Rail כשני (אם מוגדר), ואחרים לפי Phases
- לכל תוצאה: בדוק טיוטה (לא בעתיד, לא בעבר)
- אם אין חי, חזור לשדול (GTFS סטטי)
- כל ETA נושא: `Source`, `IsLive`, `ObservedAt`

**בדיקות:** לפחות 5 סצנריות: live fresh, stale, invalid, fallback, aggregation priority

אחרי: `dotnet build && dotnet test`

### Phase 1 המשך: MVVM split
פירוק `MainWindow.xaml(.cs)`:
- `ShellViewModel` (ניווט בין טאבים)
- `StopsViewModel` (טעינה אוטומטית של תחנות קרובות, רשימה חיה)
- `TripPlannerViewModel` (מוצא, יעד, זמן)
- `LinesViewModel` (קו + כיוון, עצירות רשומות, רכבים בחיים)
- `SettingsViewModel` (בית/עבודה, בדיקת חיבור לכל ספק, עדכון תדירות)

שמור `MainWindow.xaml.cs` עם כמעט אפס לוגיקה (DataContext הגדר וזהו).

אחרי: `dotnet build`

### Phase 4 המשך: טאב תחנות
`Views/StopsView.xaml`:
- כשנפתח הטאב, טעון אוטומטית תחנות קרובות (WindowLocation API או IP)
- כל תחנה: שם, קוד, מרחק
- לחץ להרחבה → הצג ETA (מהAggregator), badge "חי" / "מתוכנן"
- כל ETA עם source tooltip ("עודכן לפני X שניות")

אחרי: `dotnet build`

### Phase 6: GitHub Actions ב-windows-latest
`.github/workflows/build-test.yml`:
```yaml
name: Build & Test
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.x'
      - run: dotnet build
      - run: dotnet test --filter "Category!=Live"
      - uses: gitleaks/gitleaks-action@v2
```

---

## כלל זהב

**אחרי כל שינוי:**
```bash
dotnet build
# אם הצליח → commit
# אם כשל → תקן
```

**לעולם:**
- לא משנים קודם שמיבנה
- בדיקות כל הזמן
- commit קטנים וברורים

---

## סיכום
1. תקן TLS, בחר בעניין רכבת, הסר Setup.exe → **commit**
2. כתוב ILiveDataAggregator עם בדיקות → **commit**
3. פרק ל-MVVM: ShellVM, StopsVM, וכו' → **commits**
4. בנה את StopsView (תחנות קרובות) → **commit**
5. הוסף GitHub Actions עם gitleaks → **commit**

**אחרי כל אחד מהשלבים: `dotnet build && dotnet test`**

