namespace FeatureLab.Features.Chat;

public sealed class PersistedChatMessage
{
    private PersistedChatMessage()
    {
    }

    public PersistedChatMessage(
        ChatMessage message,
        string authorId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorId);

        Id = message.Id;
        AuthorId = authorId;
        Sender = message.Sender;
        Text = message.Text;
        SentAtUtc = message.SentAtUtc.UtcDateTime;
    }

    public Guid Id { get; private set; }

    public string AuthorId { get; private set; } = string.Empty;

    public string Sender { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public DateTime SentAtUtc { get; private set; }

    public ChatMessage ToMessage() =>
        new(
            Id,
            Sender,
            Text,
            new DateTimeOffset(
                DateTime.SpecifyKind(SentAtUtc, DateTimeKind.Utc)));
}
