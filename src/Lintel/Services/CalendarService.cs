using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Lintel.Core;

namespace Lintel.Services;

public sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool AllDay, string? Location, int FeedIndex);

/// <summary>
/// Reads iCalendar (.ics) subscription links. Google, Outlook.com and iCloud all
/// publish one, so this covers most people without any OAuth setup.
/// </summary>
public sealed class CalendarService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _refreshing;

    public IReadOnlyList<CalendarEvent> Events { get; private set; } = Array.Empty<CalendarEvent>();
    public string? LastError { get; private set; }
    public event Action? Changed;

    public CalendarService(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var feeds = _settings.CalendarFeeds.ToList();
            var from = DateTime.Today;
            var to = from.AddDays(2);
            var all = new List<CalendarEvent>();
            string? error = null;

            for (var i = 0; i < feeds.Count; i++)
            {
                try
                {
                    var url = feeds[i].Trim();
                    if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
                    var text = await Http.GetStringAsync(url);
                    all.AddRange(IcsParser.Parse(text, from, to, i));
                }
                catch (Exception ex)
                {
                    error = $"Feed {i + 1}: {ex.Message}";
                    Log.Error("calendar.fetch", ex);
                }
            }

            Events = all.OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start).ToList();
            LastError = error;
            Changed?.Invoke();
        }
        finally
        {
            _refreshing = false;
        }
    }
}

/// <summary>Small, forgiving RFC 5545 reader covering the parts calendars actually emit.</summary>
internal static class IcsParser
{
    private sealed class Prop
    {
        public string Name = "";
        public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
        public string Value = "";
    }

    private sealed class RawEvent
    {
        public string Uid = "";
        public string Summary = "";
        public string? Location;
        public Prop? Start, End, Duration, RRule, RecurrenceId;
        public List<Prop> ExDates = new();
        public bool Cancelled;
    }

    public static IEnumerable<CalendarEvent> Parse(string text, DateTime from, DateTime to, int feedIndex)
    {
        var events = new List<RawEvent>();
        RawEvent? current = null;
        foreach (var line in Unfold(text))
        {
            var p = ParseLine(line);
            if (p == null) continue;
            if (p.Name == "BEGIN" && p.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new RawEvent(); continue; }
            if (p.Name == "END" && p.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null) events.Add(current);
                current = null;
                continue;
            }
            if (current == null) continue;
            switch (p.Name)
            {
                case "UID": current.Uid = p.Value; break;
                case "SUMMARY": current.Summary = Unescape(p.Value); break;
                case "LOCATION": current.Location = Unescape(p.Value); break;
                case "DTSTART": current.Start = p; break;
                case "DTEND": current.End = p; break;
                case "DURATION": current.Duration = p; break;
                case "RRULE": current.RRule = p; break;
                case "RECURRENCE-ID": current.RecurrenceId = p; break;
                case "EXDATE": current.ExDates.Add(p); break;
                case "STATUS": current.Cancelled = p.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
            }
        }

        // Instances moved or edited individually replace the generated occurrence.
        var overrides = new HashSet<(string, DateTime)>();
        foreach (var e in events.Where(e => e.RecurrenceId != null))
        {
            var rid = ReadDate(e.RecurrenceId!);
            if (rid != null) overrides.Add((e.Uid, rid.Value.Local));
        }

        foreach (var e in events)
        {
            if (e.Start == null) continue;
            var start = ReadDate(e.Start);
            if (start == null) continue;
            var length = EventLength(e, start.Value);

            if (e.Cancelled) continue;

            if (e.RRule == null || e.RecurrenceId != null)
            {
                var s = start.Value.Local;
                if (s < to && s + length > from)
                    yield return new CalendarEvent(Title(e), s, s + length, start.Value.AllDay, e.Location, feedIndex);
                continue;
            }

            var exdates = new HashSet<DateTime>();
            foreach (var ex in e.ExDates)
                foreach (var part in ex.Value.Split(','))
                {
                    var single = new Prop { Name = ex.Name, Params = ex.Params, Value = part };
                    var d = ReadDate(single);
                    if (d != null) exdates.Add(d.Value.Local);
                }

            foreach (var occ in Expand(start.Value, e.RRule.Value, from - length, to))
            {
                if (exdates.Contains(occ) || overrides.Contains((e.Uid, occ))) continue;
                yield return new CalendarEvent(Title(e), occ, occ + length, start.Value.AllDay, e.Location, feedIndex);
            }
        }
    }

    private static string Title(RawEvent e) => string.IsNullOrWhiteSpace(e.Summary) ? "(No title)" : e.Summary;

    private static TimeSpan EventLength(RawEvent e, IcsDate start)
    {
        if (e.End != null && ReadDate(e.End) is { } end && end.Local > start.Local) return end.Local - start.Local;
        if (e.Duration != null && TryDuration(e.Duration.Value, out var d)) return d;
        return start.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    private readonly record struct IcsDate(DateTime Local, bool AllDay, DateTime Wall, TimeZoneInfo? Zone, bool Utc);

    private static IcsDate? ReadDate(Prop p)
    {
        var v = p.Value.Trim();
        var isDate = (p.Params.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || v.Length == 8;
        if (isDate)
        {
            if (!DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return null;
            return new IcsDate(day, true, day, null, false);
        }

        var utc = v.EndsWith('Z');
        if (utc) v = v[..^1];
        if (!DateTime.TryParseExact(v, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return null;

        if (utc)
        {
            var asUtc = DateTime.SpecifyKind(wall, DateTimeKind.Utc);
            return new IcsDate(asUtc.ToLocalTime(), false, wall, TimeZoneInfo.Utc, true);
        }

        TimeZoneInfo? zone = null;
        if (p.Params.TryGetValue("TZID", out var tzid)) zone = FindZone(tzid.Trim('"'));
        return new IcsDate(ToLocal(wall, zone), false, wall, zone, false);
    }

    private static DateTime ToLocal(DateTime wall, TimeZoneInfo? zone)
    {
        if (zone == null) return DateTime.SpecifyKind(wall, DateTimeKind.Local);
        try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local); }
        catch { return wall; }
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            // Some exporters prefix IANA names, e.g. "/mozilla.org/.../Europe/Berlin".
            var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(parts[^2] + "/" + parts[^1]); } catch { }
            }
            return null;
        }
    }

    /// <summary>Generates occurrences (local start times) overlapping [from, to).</summary>
    private static IEnumerable<DateTime> Expand(IcsDate start, string rrule, DateTime from, DateTime to)
    {
        var rule = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .ToDictionary(x => x[0].ToUpperInvariant(), x => x[1], StringComparer.OrdinalIgnoreCase);

        var freq = rule.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        var interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), out var c) ? c : null;
        DateTime? until = null;
        if (rule.TryGetValue("UNTIL", out var u))
        {
            var ud = ReadDate(new Prop { Name = "UNTIL", Value = u });
            if (ud != null) until = ud.Value.AllDay ? ud.Value.Local.AddDays(1).AddTicks(-1) : ud.Value.Local;
        }
        var byDay = rule.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(ParseByDay).Where(x => x != null).Select(x => x!.Value).ToList()
            : new List<(int Ordinal, DayOfWeek Day)>();
        var byMonthDay = rule.TryGetValue("BYMONTHDAY", out var bmd)
            ? bmd.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n != 0).ToList()
            : new List<int>();

        var firstDay = start.Wall.Date;
        var timeOfDay = start.Wall.TimeOfDay;
        var weekAnchor = firstDay.AddDays(-(((int)firstDay.DayOfWeek + 6) % 7)); // Monday of first week
        var produced = 0;

        for (var day = firstDay; day < to.AddDays(1); day = day.AddDays(1))
        {
            if (!Matches(day)) continue;

            var wall = day + timeOfDay;
            var local = start.AllDay ? wall : start.Utc
                ? DateTime.SpecifyKind(wall, DateTimeKind.Utc).ToLocalTime()
                : ToLocal(wall, start.Zone);

            produced++;
            if (until != null && local > until) yield break;
            if (count != null && produced > count) yield break;
            if (local >= from && local < to) yield return local;
        }

        bool Matches(DateTime day)
        {
            switch (freq)
            {
                case "DAILY":
                    return (day - firstDay).Days % interval == 0 && (byDay.Count == 0 || byDay.Any(b => b.Day == day.DayOfWeek));
                case "WEEKLY":
                    if ((day - weekAnchor).Days / 7 % interval != 0) return false;
                    return byDay.Count == 0 ? day.DayOfWeek == firstDay.DayOfWeek : byDay.Any(b => b.Day == day.DayOfWeek);
                case "MONTHLY":
                    var months = (day.Year - firstDay.Year) * 12 + day.Month - firstDay.Month;
                    if (months % interval != 0) return false;
                    if (byDay.Count > 0) return byDay.Any(b => b.Day == day.DayOfWeek && OrdinalMatches(day, b.Ordinal));
                    if (byMonthDay.Count > 0) return byMonthDay.Any(n => n > 0 ? day.Day == n : day.Day == DateTime.DaysInMonth(day.Year, day.Month) + n + 1);
                    return day.Day == firstDay.Day;
                case "YEARLY":
                    if ((day.Year - firstDay.Year) % interval != 0) return false;
                    return day.Month == firstDay.Month && day.Day == firstDay.Day;
                default:
                    return day == firstDay;
            }
        }
    }

    private static bool OrdinalMatches(DateTime day, int ordinal)
    {
        if (ordinal == 0) return true;
        if (ordinal > 0) return (day.Day - 1) / 7 + 1 == ordinal;
        var fromEnd = (DateTime.DaysInMonth(day.Year, day.Month) - day.Day) / 7 + 1;
        return fromEnd == -ordinal;
    }

    private static (int Ordinal, DayOfWeek Day)? ParseByDay(string token)
    {
        token = token.Trim().ToUpperInvariant();
        if (token.Length < 2) return null;
        var code = token[^2..];
        DayOfWeek? day = code switch
        {
            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
            "SU" => DayOfWeek.Sunday, _ => null,
        };
        if (day == null) return null;
        var ordinal = token.Length > 2 && int.TryParse(token[..^2], out var o) ? o : 0;
        return (ordinal, day.Value);
    }

    private static bool TryDuration(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var v = value.Trim().ToUpperInvariant();
        var negative = v.StartsWith('-');
        v = v.TrimStart('+', '-');
        if (!v.StartsWith('P')) return false;
        var inTime = false;
        var number = new StringBuilder();
        foreach (var ch in v[1..])
        {
            if (char.IsDigit(ch)) { number.Append(ch); continue; }
            if (ch == 'T') { inTime = true; continue; }
            if (!int.TryParse(number.ToString(), out var n)) return false;
            number.Clear();
            result += ch switch
            {
                'W' => TimeSpan.FromDays(7 * n),
                'D' => TimeSpan.FromDays(n),
                'H' when inTime => TimeSpan.FromHours(n),
                'M' when inTime => TimeSpan.FromMinutes(n),
                'S' when inTime => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero,
            };
        }
        if (negative) result = -result;
        return true;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static Prop? ParseLine(string line)
    {
        // Find the first ':' that isn't inside a quoted parameter value.
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;

        var head = line[..colon].Split(';');
        var prop = new Prop { Name = head[0].Trim().ToUpperInvariant(), Value = line[(colon + 1)..] };
        foreach (var param in head.Skip(1))
        {
            var eq = param.IndexOf('=');
            if (eq > 0) prop.Params[param[..eq]] = param[(eq + 1)..];
        }
        return prop;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}
