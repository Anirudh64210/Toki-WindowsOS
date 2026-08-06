using Toki.Detection;
using Xunit;

namespace Toki.Tests;

/// <summary>Friendly app names for the meeting prompt: "Slack is in a call. Record it?" - never the
/// raw process identity ("Com.tinyspeck.slackdesktop" leaking into the prompt wrapped the text and
/// clipped the notch: a real live bug).</summary>
public sealed class AppNamesTests
{
    [Theory]
    [InlineData("Com.tinyspeck.slackdesktop", "Slack")]
    [InlineData("slack.exe", "Slack")]
    [InlineData("Slack", "Slack")]
    [InlineData("ms-teams", "Microsoft Teams")]
    [InlineData("MSTeams", "Microsoft Teams")]
    [InlineData("MSTeams_8wekyb3d8bbwe", "Microsoft Teams")]
    [InlineData("chrome", "Chrome")]
    [InlineData("chrome.exe", "Chrome")]
    [InlineData("msedge.exe", "Edge")]
    [InlineData("Zoom.exe", "Zoom")]
    [InlineData("zoom.us", "Zoom")]
    [InlineData("Discord", "Discord")]
    [InlineData("firefox.exe", "Firefox")]
    [InlineData("Webex", "Webex")]
    public void Known_apps_map_to_their_human_name(string raw, string expected)
        => Assert.Equal(expected, AppNames.Friendly(raw));

    [Theory]
    [InlineData("com.acme.meetify.desktop", "Acme Meetify")]  // reverse-DNS chrome stripped, title-cased
    [InlineData("someapp.exe", "Someapp")]
    [InlineData("cool-conference-client", "Cool Conference")]
    public void Unknown_apps_are_cleaned_not_raw(string raw, string expected)
        => Assert.Equal(expected, AppNames.Friendly(raw));

    // Whatever comes in, the output is never the raw reverse-DNS identifier.
    [Theory]
    [InlineData("Com.tinyspeck.slackdesktop")]
    [InlineData("com.veryimportant.conferencing.suite.desktop")]
    public void Never_shows_reverse_dns(string raw)
    {
        var n = AppNames.Friendly(raw);
        Assert.DoesNotContain("com.", n, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("desktop", n, StringComparison.OrdinalIgnoreCase);
        Assert.True(char.IsUpper(n[0]), $"'{n}' should be title-cased");
    }

    [Fact]
    public void Empty_stays_empty()
        => Assert.Equal("", AppNames.Friendly(null));
}
