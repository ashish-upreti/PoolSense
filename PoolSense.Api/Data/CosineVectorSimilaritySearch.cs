using PoolSense.Api.Models;

namespace PoolSense.Api.Data;

public sealed class CosineVectorSimilaritySearch : IVectorSimilaritySearch
{
    private const int FeedbackRerankMultiplier = 5;
    private const double MaxFeedbackWeight = 0.20d;
    private const double MinFeedbackWeight = -0.20d;

    public IReadOnlyList<TicketKnowledge> Search(
        float[] queryEmbedding,
        IReadOnlyList<TicketKnowledge> candidates,
        IReadOnlyDictionary<string, double> feedbackScores,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(feedbackScores);

        if (limit <= 0 || queryEmbedding.Length == 0 || candidates.Count == 0)
        {
            return [];
        }

        var candidateLimit = Math.Max(limit, limit * FeedbackRerankMultiplier);

        return candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                VectorSimilarity = CalculateCosineSimilarity(queryEmbedding, candidate.Embedding)
            })
            .OrderByDescending(candidate => candidate.VectorSimilarity)
            .ThenBy(candidate => candidate.Candidate.TicketId, StringComparer.OrdinalIgnoreCase)
            .Take(candidateLimit)
            .Select(candidate => CloneWithSimilarity(
                candidate.Candidate,
                candidate.VectorSimilarity + GetFeedbackScore(candidate.Candidate.TicketId, feedbackScores)))
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.TicketId, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static double CalculateCosineSimilarity(float[] queryEmbedding, float[] candidateEmbedding)
    {
        if (candidateEmbedding.Length == 0 || queryEmbedding.Length != candidateEmbedding.Length)
        {
            return 0;
        }

        double dotProduct = 0;
        double queryMagnitude = 0;
        double candidateMagnitude = 0;

        for (var index = 0; index < queryEmbedding.Length; index++)
        {
            var queryValue = queryEmbedding[index];
            var candidateValue = candidateEmbedding[index];
            dotProduct += queryValue * candidateValue;
            queryMagnitude += queryValue * queryValue;
            candidateMagnitude += candidateValue * candidateValue;
        }

        if (queryMagnitude <= 0 || candidateMagnitude <= 0)
        {
            return 0;
        }

        return dotProduct / (Math.Sqrt(queryMagnitude) * Math.Sqrt(candidateMagnitude));
    }

    private static double GetFeedbackScore(string ticketId, IReadOnlyDictionary<string, double> feedbackScores)
    {
        if (string.IsNullOrWhiteSpace(ticketId)
            || !feedbackScores.TryGetValue(ticketId, out var score))
        {
            return 0;
        }

        return Math.Max(MinFeedbackWeight, Math.Min(MaxFeedbackWeight, score));
    }

    private static TicketKnowledge CloneWithSimilarity(TicketKnowledge ticketKnowledge, double similarity)
    {
        return new TicketKnowledge
        {
            Id = ticketKnowledge.Id,
            TicketId = ticketKnowledge.TicketId,
            SourceEventId = ticketKnowledge.SourceEventId,
            Problem = ticketKnowledge.Problem,
            RootCause = ticketKnowledge.RootCause,
            Resolution = ticketKnowledge.Resolution,
            Keywords = ticketKnowledge.Keywords.ToArray(),
            SearchVariants = ticketKnowledge.SearchVariants.ToList(),
            Embedding = ticketKnowledge.Embedding.ToArray(),
            Application = ticketKnowledge.Application,
            KnowledgeYear = ticketKnowledge.KnowledgeYear,
            SourceStatus = ticketKnowledge.SourceStatus,
            SourceSubmittedAt = ticketKnowledge.SourceSubmittedAt,
            SourceClosedAt = ticketKnowledge.SourceClosedAt,
            SubmitterId = ticketKnowledge.SubmitterId,
            LifeguardId = ticketKnowledge.LifeguardId,
            SourceProject = ticketKnowledge.SourceProject,
            CreatedAt = ticketKnowledge.CreatedAt,
            Similarity = similarity
        };
    }
}