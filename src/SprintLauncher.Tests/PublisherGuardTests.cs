using SprintLauncher.Jira;
using SprintLauncher.Prompts;
using Xunit;

namespace SprintLauncher.Tests;

// Gardes du publisher testées en dry-run : aucun appel réseau n'est émis avant les gardes.
public class PublisherGuardTests
{
    private static JiraCommentPublisher DryRunPublisher() =>
        new(new HttpClient(), "https://example.atlassian.net", "user@example.com", "token", dryRun: true);

    [Fact]
    public async Task Vague_comment_is_refused()
    {
        var publisher = DryRunPublisher();
        await Assert.ThrowsAsync<VagueCommentException>(() =>
            publisher.PublishManualAsync("SERZ-1", ActorRole.GptPilotage, "TODO"));
    }

    [Fact]
    public async Task Empty_comment_is_refused()
    {
        var publisher = DryRunPublisher();
        await Assert.ThrowsAsync<VagueCommentException>(() =>
            publisher.PublishManualAsync("SERZ-1", ActorRole.GptPilotage, "   "));
    }

    [Fact]
    public async Task Valid_manual_comment_is_signed_and_dryrun()
    {
        var publisher = DryRunPublisher();
        var result = await publisher.PublishManualAsync(
            "SERZ-1", ActorRole.GptPilotage,
            "Analyse de pilotage complète : périmètre validé, risques identifiés, GO recommandé.");

        Assert.Equal(PublishStatus.DryRun, result.Status);
        Assert.Contains("[agent: gpt-chat | role: pilotage-cadrage | us: SERZ-1]", result.Message);
    }

    [Fact]
    public async Task Collective_comment_too_short_is_refused()
    {
        var publisher = DryRunPublisher();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishCollectiveAsync("SERZ-1", ActorGroup.CommitteePilotage, "court"));
    }

    [Fact]
    public async Task Collective_comment_is_signed_with_group_tag()
    {
        var publisher = DryRunPublisher();
        var result = await publisher.PublishCollectiveAsync(
            "SERZ-1", ActorGroup.Qa,
            "## Verdict QA\n\nPASS avec réserves : couverture partielle documentée.");

        Assert.Equal(PublishStatus.DryRun, result.Status);
        Assert.Contains("[collectif: qa-verdict | us: SERZ-1]", result.Message);
    }

    // ── Garde périmètre (incident sprint 6 : délibération sur SERZENIA-98 au lieu
    // de l'US de pilotage SERZENIA-111) : hors périmètre = REFUS, sur tous les chemins.

    [Fact]
    public async Task Out_of_scope_collective_publish_is_refused()
    {
        var publisher = DryRunPublisher();
        publisher.AllowedKeys = ["SERZ-111", "SERZ-112"];

        var result = await publisher.PublishCollectiveAsync(
            "SERZ-98", ActorGroup.CommitteePilotage,
            "## Délibération collective — contenu suffisamment long pour passer la garde vague.");

        Assert.Equal(PublishStatus.Skipped, result.Status);
        Assert.Contains("Garde périmètre", result.Message);
    }

    [Fact]
    public async Task Out_of_scope_manual_publish_is_refused()
    {
        var publisher = DryRunPublisher();
        publisher.AllowedKeys = ["SERZ-111"];

        var result = await publisher.PublishManualAsync(
            "AUTRE-1", ActorRole.GptPilotage,
            "Contenu valide et suffisamment long pour passer la garde de commentaire vague.");

        Assert.Equal(PublishStatus.Skipped, result.Status);
        Assert.Contains("Garde périmètre", result.Message);
    }

    [Fact]
    public async Task In_scope_publish_passes_scope_guard()
    {
        var publisher = DryRunPublisher();
        publisher.AllowedKeys = ["SERZ-111"];

        var result = await publisher.PublishCollectiveAsync(
            "serz-111", ActorGroup.Qa, // insensible à la casse
            "## Verdict QA\n\nPASS : critères DoD vérifiés un à un sur les preuves.");

        Assert.Equal(PublishStatus.DryRun, result.Status);
    }

    [Fact]
    public async Task No_scope_configured_means_no_restriction()
    {
        var publisher = DryRunPublisher(); // AllowedKeys null : comportement historique
        var result = await publisher.PublishCollectiveAsync(
            "NIMPORTE-1", ActorGroup.Qa,
            "## Verdict QA\n\nPASS : comportement rétrocompatible sans périmètre déclaré.");

        Assert.Equal(PublishStatus.DryRun, result.Status);
    }
}
