namespace CopilotScope.Collector.Otlp;

/// <summary>Attribute value — flattened to string/long/double/bool for dashboard purposes.</summary>
public readonly record struct AttrValue(string? S, long? I, double? D, bool? B)
{
    public static AttrValue Str(string s) => new(s, null, null, null);
    public static AttrValue Int(long i) => new(null, i, null, null);
    public static AttrValue Dbl(double d) => new(null, null, d, null);
    public static AttrValue Bool(bool b) => new(null, null, null, b);

    public override string ToString() => S ?? I?.ToString() ?? D?.ToString("G") ?? B?.ToString() ?? "";
    public double AsDouble() => D ?? I ?? (B == true ? 1 : 0);
}

public sealed class OtlpSpan
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public double DurationMs => (End - Start).TotalMilliseconds;
    public int StatusCode { get; init; }          // 0 unset, 1 ok, 2 error
    public string? StatusMessage { get; init; }
    public Dictionary<string, AttrValue> Attributes { get; init; } = new();
    public Dictionary<string, AttrValue> Resource { get; init; } = new();

    public string? Attr(string key) => Attributes.TryGetValue(key, out var v) ? v.ToString() : null;
    public long? AttrLong(string key) => Attributes.TryGetValue(key, out var v) ? (v.I ?? (long?)v.D) : null;
}

public sealed class OtlpMetricPoint
{
    public required string MetricName { get; init; }
    public string? Unit { get; init; }
    public MetricKind Kind { get; init; }
    public DateTimeOffset Time { get; init; }
    public double Value { get; init; }            // gauge/sum value, or histogram sum
    public long Count { get; init; }              // histogram count, 1 otherwise
    public Dictionary<string, AttrValue> Attributes { get; init; } = new();
    public Dictionary<string, AttrValue> Resource { get; init; } = new();

    public string? Attr(string key) => Attributes.TryGetValue(key, out var v) ? v.ToString() : null;
}

public enum MetricKind { Gauge, Sum, Histogram }

public sealed class OtlpLogEvent
{
    public string? EventName { get; init; }
    public DateTimeOffset Time { get; init; }
    public int SeverityNumber { get; init; }
    /// <summary>Settable so privacy mode can drop it: on the GenAI content events the body
    /// IS the prompt or the completion, so an init-only body would be the one content path
    /// redaction could not close.</summary>
    public string? Body { get; set; }
    public string? TraceId { get; init; }
    public Dictionary<string, AttrValue> Attributes { get; init; } = new();
    public Dictionary<string, AttrValue> Resource { get; init; } = new();
}

public sealed class OtlpBatch
{
    public List<OtlpSpan> Spans { get; } = new();
    public List<OtlpMetricPoint> Metrics { get; } = new();
    public List<OtlpLogEvent> Logs { get; } = new();
}
