using System;
using System.Collections.Generic;
using System.Linq;

namespace Cruncharr.Core.Utils;

public static class StringSimilarity {
    public static double CalculateSimilarity(string source, string target) {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) {
            return string.IsNullOrEmpty(source) && string.IsNullOrEmpty(target) ? 1.0 : 0.0;
        }

        int distance = LevenshteinDistance(source, target);
        return 1.0 - (double)distance / Math.Max(source.Length, target.Length);
    }

    private static int LevenshteinDistance(string source, string target) {
        if (string.IsNullOrEmpty(source)) {
            return string.IsNullOrEmpty(target) ? 0 : target.Length;
        }

        if (string.IsNullOrEmpty(target)) {
            return source.Length;
        }

        int n = source.Length;
        int m = target.Length;

        // Use a single array for distances
        int[] distances = new int[m + 1];

        // Initialize the distance array
        for (int j = 0; j <= m; j++) {
            distances[j] = j;
        }

        for (int i = 1; i <= n; i++) {
            int previousDiagonal = distances[0];
            distances[0] = i;

            for (int j = 1; j <= m; j++) {
                int previousDistance = distances[j];
                int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;

                distances[j] = Math.Min(
                    Math.Min(distances[j - 1] + 1, distances[j] + 1),
                    previousDiagonal + cost);

                previousDiagonal = previousDistance;
            }
        }

        return distances[m];
    }

    public static double CalculateCosineSimilarity(string text1, string text2) {
        var vector1 = ComputeWordFrequency(text1);
        var vector2 = ComputeWordFrequency(text2);

        return CosineSimilarity(vector1, vector2);
    }

    private static readonly char[] Delimiters = { ' ', ',', '.', ';', ':', '-', '_', '\'' };

    public static Dictionary<string, double> ComputeWordFrequency(string text) {
        var wordFrequency = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var words = SplitText(text);

        foreach (var word in words) {
            if (wordFrequency.TryGetValue(word, out double count)) {
                wordFrequency[word] = count + 1;
            } else {
                wordFrequency[word] = 1;
            }
        }

        return wordFrequency;
    }

    private static List<string> SplitText(string text) {
        var words = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++) {
            if (Array.IndexOf(Delimiters, text[i]) >= 0) {
                if (i > start) {
                    words.Add(text.Substring(start, i - start));
                }
                start = i + 1;
            }
        }

        if (start < text.Length) {
            words.Add(text.Substring(start));
        }

        return words;
    }

    private static double CosineSimilarity(Dictionary<string, double> vector1, Dictionary<string, double> vector2) {
        var intersection = vector1.Keys.Intersect(vector2.Keys);

        double dotProduct = intersection.Sum(term => vector1[term] * vector2[term]);
        double normA = Math.Sqrt(vector1.Values.Sum(val => val * val));
        double normB = Math.Sqrt(vector2.Values.Sum(val => val * val));

        if (normA == 0 || normB == 0) {
            return 0;
        }

        return dotProduct / (normA * normB);
    }
}
