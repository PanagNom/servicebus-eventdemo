namespace Messaging;

public sealed class MessageContext
{
    public required string MessageId { get; init; }
    public string? CorrelationId { get; init; }
    public required string Subject {get; init; }
    public required int DeliveryCount {get; init; }
    internal bool CompleteSuppressed { get; private set; }
    public void SimulateLostComplete() => CompleteSuppressed = true;
}
