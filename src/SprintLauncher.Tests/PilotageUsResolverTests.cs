using SprintLauncher.Jira;
using Xunit;

namespace SprintLauncher.Tests;

public class PilotageUsResolverTests
{
    private static JiraIssue Us(string key, string summary) =>
        new(key, summary, "", []);

    // Le cas exact du sprint 6 : la liste commence par SERZENIA-98 (une US de reste à
    // faire), l'US de pilotage est SERZENIA-111. L'ancien code publiait sur la 98.
    [Fact]
    public void Sprint6_pilotage_us_is_111_not_the_first_ticket()
    {
        var issues = new[]
        {
            Us("SERZENIA-98",  "Remédiation QA visuelle"),
            Us("SERZENIA-111", "Pilotage Sprint 6"),
            Us("SERZENIA-112", "Accès utilisateur provider réel"),
        };
        var keys = issues.Select(i => i.Key).ToArray();

        var res = PilotageUsResolver.Resolve(issues, keys, sprintId: "6");

        Assert.Equal("SERZENIA-111", res.Key);
        Assert.False(res.IsFallback);
    }

    [Fact]
    public void Pilotage_us_is_found_without_sprint_id()
    {
        var issues = new[] { Us("A-1", "Autre chose"), Us("A-2", "Pilotage Sprint 7") };
        var res = PilotageUsResolver.Resolve(issues, ["A-1", "A-2"]);

        Assert.Equal("A-2", res.Key);
        Assert.False(res.IsFallback);
    }

    [Fact]
    public void Explicit_key_wins_over_detection()
    {
        var issues = new[] { Us("A-1", "Pilotage Sprint 6"), Us("A-2", "Autre") };
        var res = PilotageUsResolver.Resolve(issues, ["A-1", "A-2"], sprintId: "6", forcedKey: "a-2");

        Assert.Equal("A-2", res.Key);
        Assert.False(res.IsFallback);
    }

    // Le sprint voisin ne doit pas capter le pilotage : « Pilotage Sprint 5 » dans un
    // périmètre de sprint 6 n'est pas l'US de pilotage du run.
    [Fact]
    public void Sprint_number_disambiguates_between_pilotage_us()
    {
        var issues = new[] { Us("A-1", "Pilotage Sprint 5"), Us("A-2", "Pilotage Sprint 6") };
        var res = PilotageUsResolver.Resolve(issues, ["A-1", "A-2"], sprintId: "6");

        Assert.Equal("A-2", res.Key);
        Assert.False(res.IsFallback);
    }

    // Ambiguïté non tranchable → repli SIGNALÉ, jamais un choix silencieux.
    [Fact]
    public void Several_pilotage_candidates_fall_back_and_say_so()
    {
        var issues = new[] { Us("A-1", "Pilotage machin"), Us("A-2", "Pilotage truc") };
        var res = PilotageUsResolver.Resolve(issues, ["A-1", "A-2"]);

        Assert.True(res.IsFallback);
        Assert.Contains("candidates", res.Reason);
    }

    [Fact]
    public void No_pilotage_us_falls_back_to_first_key_and_says_so()
    {
        var issues = new[] { Us("A-1", "Une US"), Us("A-2", "Une autre") };
        var res = PilotageUsResolver.Resolve(issues, ["A-1", "A-2"]);

        Assert.Equal("A-1", res.Key);
        Assert.True(res.IsFallback);
    }
}
