namespace VMentory.Core.Persistence;

public class AuditEventEntity
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Username { get; set; }
    public string Verb { get; set; } = "";
    public bool Allowed { get; set; }
    public string? CorrelationId { get; set; }
    public string? Detail { get; set; }
}
