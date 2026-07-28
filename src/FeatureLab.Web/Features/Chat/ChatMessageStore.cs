using FeatureLab.Data;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Features.Chat;

public interface IChatMessageStore
{
    Task AddAsync(
        ChatMessage message,
        string authorId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatMessage>> ListRecentAsync(
        int count,
        CancellationToken cancellationToken);
}

public sealed class EfChatMessageStore(FeatureLabDbContext dbContext)
    : IChatMessageStore
{
    public async Task AddAsync(
        ChatMessage message,
        string authorId,
        CancellationToken cancellationToken)
    {
        dbContext.ChatMessages.Add(
            new PersistedChatMessage(message, authorId));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMessage>> ListRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var newestFirst = await dbContext.ChatMessages
            .AsNoTracking()
            .OrderByDescending(message => message.SentAtUtc)
            .ThenByDescending(message => message.Id)
            .Take(count)
            .ToArrayAsync(cancellationToken);

        return newestFirst
            .Reverse()
            .Select(message => message.ToMessage())
            .ToArray();
    }
}
