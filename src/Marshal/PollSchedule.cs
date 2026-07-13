using System.Globalization;
using NCrontab;

namespace Marshal;

/// <summary>A validated Azure Functions-style polling schedule backed by either NCRONTAB or a fixed TimeSpan</summary>
public sealed class PollSchedule
{
    /// <summary>Fastest allowed polling frequency</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly CrontabSchedule? _crontab;
    private readonly TimeSpan? _interval;

    private PollSchedule(string expression, CrontabSchedule crontab)
    {
        Expression = expression;
        _crontab = crontab;
    }

    private PollSchedule(string expression, TimeSpan interval)
    {
        Expression = expression;
        _interval = interval;
    }

    /// <summary>The configured expression</summary>
    public string Expression { get; }

    /// <summary>Whether the expression is a fixed elapsed-time interval rather than NCRONTAB</summary>
    public bool IsInterval => _interval.HasValue;

    /// <summary>Parses a six-field Azure NCRONTAB expression, five-field crontab expression, or invariant .NET TimeSpan value</summary>
    /// <exception cref="FormatException">The expression is empty, malformed, or can fire more often than once per minute</exception>
    public static PollSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new FormatException("the value is empty");
        }

        string trimmed = expression.Trim();
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out TimeSpan interval))
        {
            if (interval < MinimumInterval)
            {
                throw new FormatException($"a TimeSpan must be at least {MinimumInterval:c}");
            }

            return new PollSchedule(trimmed, interval);
        }

        string[] fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool includingSeconds = fields.Length switch
        {
            5 => false,
            6 => true,
            _ => throw new FormatException("NCRONTAB needs five fields, or six fields with seconds first")
        };

        if (includingSeconds && !string.Equals(fields[0], "0", StringComparison.Ordinal))
        {
            throw new FormatException("the NCRONTAB seconds field must be exactly 0 so polling cannot run more than once per minute");
        }

        try
        {
            CrontabSchedule schedule = CrontabSchedule.Parse(trimmed, new CrontabSchedule.ParseOptions { IncludingSeconds = includingSeconds });
            DateTime start = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime end = start.AddYears(400);
            if (schedule.GetNextOccurrence(start, end) == end)
            {
                throw new FormatException("the NCRONTAB expression has no possible occurrence");
            }

            return new PollSchedule(trimmed, schedule);
        }
        catch (CrontabException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    /// <summary>Returns the first scheduled UTC occurrence strictly after <paramref name="utcNow"/></summary>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset utcNow)
    {
        if (_interval is TimeSpan interval)
        {
            return utcNow + interval;
        }

        DateTime next = _crontab!.GetNextOccurrence(utcNow.UtcDateTime);
        return new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Utc));
    }
}
