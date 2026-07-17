using Cruncharr.API.Controllers;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;
using Cruncharr.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cruncharr.Core.Tests;

public class CalendarLanguageFilterTests
{
    [Theory]
    [InlineData("Witch Hat Atelier Season 1 (English)", "en-us", true)]
    [InlineData("Witch Hat Atelier Season 1 (Português (Brasil))", "en-us", false)]
    [InlineData("Witch Hat Atelier Season 1 (Español)", "en-us", false)]
    [InlineData("Witch Hat Atelier Season 1 (Français)", "en-us", false)]
    [InlineData("Season 1 (Subbed)", "en-us", true)]
    [InlineData("Season 1", "en-us", true)]
    [InlineData("Witch Hat Atelier Season 1 (English)", "fr", false)]
    [InlineData("Witch Hat Atelier Season 1 (Français)", "fr", true)]
    [InlineData("L'Atelier des Sorciers", "fr", false)]
    [InlineData("Witch Hat Atelier Season 1 (Español)", "es", true)]
    [InlineData("Witch Hat Atelier Season 1 (Español (España))", "es-es", true)]
    [InlineData("Witch Hat Atelier Season 1 (Português (Brasil))", "pt-br", true)]
    [InlineData("Witch Hat Atelier Season 1 (Deutsch)", "de", true)]
    [InlineData("Witch Hat Atelier Season 1 (Italiano)", "it", true)]
    [InlineData("Witch Hat Atelier Season 1 (Русский)", "ru", true)]
    [InlineData(null, "en-us", true)]
    [InlineData("", "en-us", true)]
    [InlineData("Season 1 (Uncut)", "en-us", true)]
    public void MatchesLanguage_ReturnsCorrectResult(string? seasonName, string language, bool expected)
    {
        var result = CrSimulcastCalendarFilter.MatchesLanguage(seasonName, language);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MatchesLanguage_EnglishShowsOnlyEnglishAndSubs()
    {
        var episodes = new[]
        {
            "Witch Hat Atelier Season 1 (English)",
            "Witch Hat Atelier Season 1 (Português (Brasil))",
            "Witch Hat Atelier Season 1 (Español)",
            "Witch Hat Atelier Season 1 (Français)",
            "Season 1 (Subbed)",
            "Season 1"
        };

        var filtered = episodes.Where(e => CrSimulcastCalendarFilter.MatchesLanguage(e, "en-us")).ToList();

        Assert.Equal(3, filtered.Count);
        Assert.Contains("Witch Hat Atelier Season 1 (English)", filtered);
        Assert.Contains("Season 1 (Subbed)", filtered);
        Assert.Contains("Season 1", filtered);
    }

    [Fact]
    public void MatchesLanguage_FrenchShowsOnlyFrench()
    {
        var episodes = new[]
        {
            "Witch Hat Atelier Season 1 (English)",
            "Witch Hat Atelier Season 1 (Português (Brasil))",
            "Witch Hat Atelier Season 1 (Français)",
            "L'Atelier des Sorciers"
        };

        var filtered = episodes.Where(e => CrSimulcastCalendarFilter.MatchesLanguage(e, "fr")).ToList();

        Assert.Single(filtered);
        Assert.Contains("Witch Hat Atelier Season 1 (Français)", filtered);
    }
    [Fact]
    public async Task CustomCalendar_MergedSameDayEpisodes_AreReturnedIndividually()
    {
        var firstEpisode = new CalendarEpisode
        {
            EpisodeName = "Episode 1",
            EpisodeUrl = "/en-us/watch/EPISODE1/episode-1",
            EpisodeNumber = "1-2",
            AudioLocale = "en-US",
            CrSeriesID = "SERIES"
        };
        firstEpisode.CalendarEpisodes.Add(new CalendarEpisode
        {
            EpisodeName = "Episode 2",
            EpisodeUrl = "/en-us/watch/EPISODE2/episode-2",
            EpisodeNumber = "2",
            AudioLocale = "en-US",
            CrSeriesID = "SERIES"
        });

        var week = new CalendarWeek
        {
            FirstDayOfWeek = new DateTime(2026, 7, 13),
            CalendarDays =
            [
                new CalendarDay
                {
                    DateTime = new DateTime(2026, 7, 17),
                    DayName = "Friday",
                    CalendarEpisodes = [firstEpisode]
                }
            ]
        };

        var calendarService = new Mock<ICalendarService>();
        calendarService
            .Setup(service => service.GetCustomCalendarAsync(
                It.IsAny<DateTime>(),
                It.IsAny<string>(),
                It.IsAny<bool>()))
            .ReturnsAsync(week);

        var controller = new CalendarController(
            calendarService.Object,
            new CruncharrConfig(),
            NullLogger<CalendarController>.Instance);

        var result = await controller.GetCustomCalendar("2026-07-17", "en-us", false, "en-US");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CalendarWeekResponse>(ok.Value);
        var episodes = Assert.Single(response.Days).Episodes;

        Assert.Equal(["EPISODE1", "EPISODE2"], episodes.Select(episode => episode.Id));
        Assert.Equal(["1", "2"], episodes.Select(episode => episode.EpisodeNumber));
    }
}
