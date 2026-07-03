using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public interface IVectorStore
{
    Task InsertTicketKnowledge(TicketKnowledge ticketKnowledge, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TicketKnowledge>> SearchSimilarTickets(float[] embedding, int limit = 5, IReadOnlyList<string>? selectedGroupIds = null, CancellationToken cancellationToken = default);
    Task<double> GetFeedbackScore(string ticketId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimeline(int monthCount = 6, CancellationToken cancellationToken = default);
}

public interface ITicketKnowledgeEmbeddingStore
{
    Task<TicketKnowledge> AddTicketKnowledgeAsync(TicketKnowledge ticketKnowledge, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TicketKnowledge>> GetTicketKnowledgeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IncidentTimelinePoint>> GetIncidentTimelineAsync(int monthCount = 6, CancellationToken cancellationToken = default);
}

public interface IVectorSimilaritySearch
{
    IReadOnlyList<TicketKnowledge> Search(
        float[] queryEmbedding,
        IReadOnlyList<TicketKnowledge> candidates,
        IReadOnlyDictionary<string, FeedbackEvidence> feedbackEvidence,
        int limit);
}

public interface IVectorStoreCacheInvalidator
{
    void Invalidate();
}