using Cruncharr.Core.Utils;

namespace Cruncharr.Core.Tests;

public class StringSimilarityTests
{
    [Theory]
    [InlineData("Attack on Titan", "Attack on Titan", 1.0)]
    [InlineData("Attack on Titan", "attack on titan", 0.8)]
    [InlineData("Attack on Titan", "Attack on Titan Season 2", 0.6)]
    [InlineData("Hello World", "Goodbye World", 0.4)]
    [InlineData("Hello", "", 0.0)]
    [InlineData("", "Hello", 0.0)]
    [InlineData("", "", 1.0)]
    public void CalculateSimilarity_VariousInputs_ReturnsExpectedScore(string source, string target, double expectedMinScore)
    {
        var score = StringSimilarity.CalculateSimilarity(source, target);
        Assert.True(score >= expectedMinScore, $"Expected score >= {expectedMinScore}, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_IdenticalStrings_ReturnsOne()
    {
        var score = StringSimilarity.CalculateSimilarity("Attack on Titan", "Attack on Titan");
        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public void CalculateSimilarity_CompletelyDifferent_ReturnsLowScore()
    {
        var score = StringSimilarity.CalculateSimilarity("Attack on Titan", "My Hero Academia");
        Assert.True(score < 0.5, $"Expected score < 0.5 for completely different strings, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_CaseDifference_ReducesSimilarity()
    {
        var score = StringSimilarity.CalculateSimilarity("Attack on Titan", "ATTACK ON TITAN");

        // Case difference significantly reduces similarity for this algorithm
        Assert.True(score > 0.2, $"Expected score > 0.2, got {score}");
        Assert.True(score < 0.5, $"Expected score < 0.5, got {score}");
    }

    [Fact]
    public void CalculateSimilarity_TypoTolerance_HandlesSmallDifferences()
    {
        var score = StringSimilarity.CalculateSimilarity("Attack on Titan", "Attak on Titan");
        Assert.True(score > 0.8, $"Expected score > 0.8 for typo, got {score}");
    }

    [Theory]
    [InlineData("The quick brown fox", "The quick brown fox", 1.0)]
    [InlineData("The quick brown fox", "The fast brown fox", 0.7)]
    [InlineData("Hello World", "Goodbye World", 0.0)]
    public void CalculateCosineSimilarity_VariousInputs_ReturnsExpectedScore(string text1, string text2, double expectedMinScore)
    {
        var score = StringSimilarity.CalculateCosineSimilarity(text1, text2);
        Assert.True(score >= expectedMinScore, $"Expected score >= {expectedMinScore}, got {score}");
    }

    [Fact]
    public void CalculateCosineSimilarity_IdenticalTexts_ReturnsOne()
    {
        var score = StringSimilarity.CalculateCosineSimilarity(
            "This is a test description for an episode",
            "This is a test description for an episode"
        );
        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public void CalculateCosineSimilarity_SimilarDescriptions_ReturnsHighScore()
    {
        var score = StringSimilarity.CalculateCosineSimilarity(
            "Eren Yeager discovers the power of the Titans and vows to eliminate them all",
            "Eren Yeager discovers his Titan powers and swears to destroy all Titans"
        );
        Assert.True(score > 0.5, $"Expected score > 0.5 for similar descriptions, got {score}");
    }

    [Fact]
    public void CalculateCosineSimilarity_DifferentDescriptions_ReturnsLowScore()
    {
        var score = StringSimilarity.CalculateCosineSimilarity(
            "A group of friends go on a camping trip in the mountains",
            "A detective solves a murder mystery in a small town"
        );
        Assert.True(score < 0.5, $"Expected score < 0.5 for different descriptions, got {score}");
    }

    [Fact]
    public void CalculateCosineSimilarity_EmptyStrings_ReturnsZero()
    {
        var score = StringSimilarity.CalculateCosineSimilarity("", "");
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void CalculateCosineSimilarity_OneEmpty_ReturnsZero()
    {
        var score = StringSimilarity.CalculateCosineSimilarity("Some text", "");
        Assert.Equal(0.0, score);
    }
}