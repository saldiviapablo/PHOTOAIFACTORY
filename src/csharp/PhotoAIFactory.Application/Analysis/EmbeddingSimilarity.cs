namespace PhotoAIFactory.Application.Analysis;

public static class EmbeddingSimilarity
{
    public static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Count == 0 || left.Count != right.Count)
        {
            throw new ArgumentException("Embeddings must be non-empty and have equal dimensions.");
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        if (leftNorm <= 0 || rightNorm <= 0)
        {
            return 0;
        }

        return Math.Clamp(dot / Math.Sqrt(leftNorm * rightNorm), -1, 1);
    }
}
