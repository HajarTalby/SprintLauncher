using System.Text;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class RetrospectiveJournalTests
{
    [Fact]
    public void Extracts_retro_block_and_stops_at_next_level_two_heading()
    {
        const string output = """
            Travail termine.

            ##RETRO
            - La revue croisee a detecte le probleme avant QA.
            - Le handoff doit citer les fichiers modifies.

            ## Resultat
            Cette section ne fait pas partie de la retro.
            """;

        var points = RetrospectiveJournal.ExtractPoints(output);

        var point = Assert.Single(points);
        Assert.Contains("revue croisee", point);
        Assert.Contains("handoff", point);
        Assert.DoesNotContain("Resultat", point);
    }

    [Fact]
    public void Missing_marker_produces_no_point()
    {
        var points = RetrospectiveJournal.ExtractPoints("Travail termine sans apprentissage retro.");

        Assert.Empty(points);
    }

    [Theory]
    [InlineData("##RETRO point sur la meme ligne")]
    [InlineData("###RETRO\n- mauvais niveau de titre")]
    [InlineData("##RETRO\n  \n")]
    public void Malformed_or_empty_marker_produces_no_point(string output)
    {
        var points = RetrospectiveJournal.ExtractPoints(output);

        Assert.Empty(points);
    }

    [Fact]
    public async Task Output_without_marker_does_not_create_a_file()
    {
        var root = NewTemporaryRoot();
        try
        {
            var journal = new RetrospectiveJournal(root, "sprint6");

            var result = await journal.CaptureAsync(
                ActorRole.ClaudeImplementation,
                "Implementation terminee, aucun point a consigner.");

            Assert.False(result.HasContent);
            Assert.False(Directory.Exists(journal.DirectoryPath));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Append_is_immediate_per_actor_and_uses_utf8_without_bom()
    {
        var root = NewTemporaryRoot();
        try
        {
            var journal = new RetrospectiveJournal(root, "sprint6");

            var claude = await journal.CaptureAsync(
                ActorRole.ClaudeImplementation,
                "Sortie\n##RETRO\n- Point Claude persiste immediatement.");
            Assert.True(claude.Persisted);
            Assert.True(File.Exists(claude.FilePath));
            Assert.Contains("Point Claude", await File.ReadAllTextAsync(claude.FilePath!));

            var claudeSecond = await journal.CaptureAsync(
                ActorRole.ClaudeImplementation,
                "##RETRO\n- Deuxieme point Claude ajoute sans ecraser le premier.");
            var gpt = await journal.CaptureAsync(
                ActorRole.GptImplementation,
                "##RETRO\n- Point GPT dans un autre fichier.");

            Assert.True(claudeSecond.Persisted);
            Assert.True(gpt.Persisted);
            Assert.Equal(claude.FilePath, claudeSecond.FilePath);
            Assert.NotEqual(claude.FilePath, gpt.FilePath);
            Assert.True(File.Exists(gpt.FilePath));
            Assert.Equal(Path.Combine(root, "retro", "sprint6"), journal.DirectoryPath);
            Assert.Equal(ArtifactNaming.Retrospective(ActorRole.ClaudeImplementation), Path.GetFileName(claude.FilePath));
            var claudeContent = await File.ReadAllTextAsync(claude.FilePath!);
            Assert.Contains("Point Claude", claudeContent);
            Assert.Contains("Deuxieme point Claude", claudeContent);
            Assert.DoesNotContain("Point GPT", claudeContent);
            Assert.Contains("Point GPT", await File.ReadAllTextAsync(gpt.FilePath!));

            var bytes = await File.ReadAllBytesAsync(claude.FilePath!);
            var bom = Encoding.UTF8.GetPreamble();
            Assert.False(bytes.Length >= bom.Length && bytes.AsSpan(0, bom.Length).SequenceEqual(bom));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Io_exception_during_append_is_reported_without_throwing()
    {
        var root = NewTemporaryRoot();
        try
        {
            var journal = new RetrospectiveJournal(root, "sprint6");
            Directory.CreateDirectory(Path.GetDirectoryName(journal.DirectoryPath)!);
            await File.WriteAllTextAsync(journal.DirectoryPath, "ce chemin est un fichier");

            var result = await journal.CaptureAsync(
                ActorRole.GptImplementation,
                "##RETRO\n- Ce point ne peut pas etre ecrit.");

            Assert.True(result.HasContent);
            Assert.False(result.Persisted);
            Assert.NotNull(result.Error);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Final_synthesis_is_appended_without_requiring_the_incremental_marker()
    {
        var root = NewTemporaryRoot();
        try
        {
            var journal = new RetrospectiveJournal(root, "sprint6");

            var result = await journal.AppendFinalSynthesisAsync(
                ActorRole.RetrospectiveClaude,
                "## Ce qui a bien marche\n- Une preuve concrete.");

            Assert.True(result.Persisted);
            Assert.Contains("SYNTHESE FINALE", await File.ReadAllTextAsync(result.FilePath!));
            Assert.Contains("Une preuve concrete", await File.ReadAllTextAsync(result.FilePath!));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Final_phase_reloads_points_after_process_interruption()
    {
        var root = NewTemporaryRoot();
        try
        {
            var beforeInterruption = new RetrospectiveJournal(root, "sprint6");
            var persisted = await beforeInterruption.CaptureAsync(
                ActorRole.AnalysisCodex,
                "Analyse interrompue ensuite.\n##RETRO\n- Le contexte de reprise doit inclure le litige initial.");
            Assert.True(persisted.Persisted);

            // Nouvelle instance = nouveau processus apres arret/crash, sans etat en memoire.
            var finalPhase = new RetrospectiveJournal(root, "sprint6");
            var context = await finalPhase.ReadForFinalPhaseAsync();

            Assert.Contains(ArtifactNaming.Retrospective(ActorRole.AnalysisCodex), context);
            Assert.Contains("litige initial", context);
            Assert.Contains("matiere premiere de la synthese finale", context);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void Common_prompt_documents_the_optional_retro_marker()
    {
        var builder = new PromptBuilder();
        var issues = new List<SprintLauncher.Jira.JiraIssue>
        {
            new("SERZENIA-145", "Retro au fil du sprint", "Description", []),
        };

        var prompt = builder.Build(ActorRole.GptImplementation, issues, "SERZENIA-145");

        Assert.Contains("CAPTURE RÉTROSPECTIVE AU FIL DU SPRINT", prompt.SystemPrompt);
        Assert.Contains(RetrospectiveJournal.Marker, prompt.SystemPrompt);
        Assert.Contains("N'ajoute pas ce bloc si tu n'as aucun point utile", prompt.SystemPrompt);
    }

    private static string NewTemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), "SprintLauncherTests", Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
