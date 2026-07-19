using SprintLauncher.Dialogue;
using Xunit;

namespace SprintLauncher.Tests;

// SERZENIA-144 Lot 3 : pièces jointes sur une intervention/directive.
public class DirectiveAttachmentsTests : IDisposable
{
    private readonly string _dir;

    public DirectiveAttachmentsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sl-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── Encode / Extract round-trip ────────────────────────────────────────────

    [Fact]
    public void Encode_then_extract_roundtrips_text_and_paths()
    {
        var encoded = DirectiveAttachments.Encode("regarde cette capture", ["C:\\shots\\a.png", "C:\\shots\\b.png"]);
        var (text, paths) = DirectiveAttachments.Extract(encoded);

        Assert.Equal("regarde cette capture", text);
        Assert.Equal(new[] { "C:\\shots\\a.png", "C:\\shots\\b.png" }, paths);
    }

    [Fact]
    public void Extract_without_marker_returns_text_untouched_and_empty_paths()
    {
        var (text, paths) = DirectiveAttachments.Extract("@ccode corrige le bug de layout");

        Assert.Equal("@ccode corrige le bug de layout", text);
        Assert.Empty(paths);
    }

    [Fact]
    public void Encode_with_no_paths_returns_text_unchanged()
    {
        var encoded = DirectiveAttachments.Encode("continue", []);
        Assert.Equal("continue", encoded);
    }

    [Fact]
    public void Extract_single_attachment_path()
    {
        var encoded = DirectiveAttachments.Encode("vois le mockup", ["C:\\design\\mockup.pdf"]);
        var (text, paths) = DirectiveAttachments.Extract(encoded);

        Assert.Equal("vois le mockup", text);
        Assert.Equal(["C:\\design\\mockup.pdf"], paths);
    }

    [Fact]
    public void Extract_leaves_at_prefix_intact_for_addressing()
    {
        // Le marqueur ne doit pas interférer avec l'adressage @cible qui s'applique
        // AVANT l'extraction dans le flux réel (AddDirectiveAsync).
        var encoded = DirectiveAttachments.Encode("@codex regarde ça", ["C:\\a.png"]);
        var (text, _) = DirectiveAttachments.Extract(encoded);
        Assert.StartsWith("@codex", text);
    }

    // ── CopyToRunFolder ─────────────────────────────────────────────────────────

    [Fact]
    public void CopyToRunFolder_copies_existing_files_to_destination()
    {
        var src = Path.Combine(_dir, "source.png");
        File.WriteAllText(src, "fake-image-bytes");
        var destDir = Path.Combine(_dir, "attachments", "ClaudeImplementation");

        var copied = DirectiveAttachments.CopyToRunFolder([src], destDir);

        var copiedPath = Assert.Single(copied);
        Assert.True(File.Exists(copiedPath));
        Assert.Equal("fake-image-bytes", File.ReadAllText(copiedPath));
        Assert.StartsWith(Path.GetFullPath(destDir), copiedPath);
    }

    [Fact]
    public void CopyToRunFolder_skips_missing_file_without_throwing()
    {
        var missing = Path.Combine(_dir, "does-not-exist.png");
        var destDir = Path.Combine(_dir, "attachments", "GptImplementation");

        var copied = DirectiveAttachments.CopyToRunFolder([missing], destDir);

        Assert.Empty(copied);
    }

    [Fact]
    public void CopyToRunFolder_avoids_overwriting_same_named_file()
    {
        var src1 = Path.Combine(_dir, "shot.png");
        File.WriteAllText(src1, "premiere-version");
        var destDir = Path.Combine(_dir, "attachments", "ClaudeQaVerdict");

        var first = DirectiveAttachments.CopyToRunFolder([src1], destDir);
        // Deuxième pièce jointe, même nom de fichier source (répertoire différent) :
        var srcDir2 = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(srcDir2);
        var src2 = Path.Combine(srcDir2, "shot.png");
        File.WriteAllText(src2, "deuxieme-version");
        var second = DirectiveAttachments.CopyToRunFolder([src2], destDir);

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotEqual(first[0], second[0]); // pas d'écrasement — deux fichiers distincts
        Assert.Equal("premiere-version", File.ReadAllText(first[0]));
        Assert.Equal("deuxieme-version", File.ReadAllText(second[0]));
    }

    [Fact]
    public void CopyToRunFolder_with_no_paths_returns_empty_without_creating_dir()
    {
        var destDir = Path.Combine(_dir, "attachments", "unused");
        var copied = DirectiveAttachments.CopyToRunFolder([], destDir);

        Assert.Empty(copied);
        Assert.False(Directory.Exists(destDir));
    }

    // ── FormatForPrompt ─────────────────────────────────────────────────────────

    [Fact]
    public void FormatForPrompt_lists_each_copied_path()
    {
        var section = DirectiveAttachments.FormatForPrompt(["C:\\run\\a.png", "C:\\run\\b.mp4"]);

        Assert.Contains("C:\\run\\a.png", section);
        Assert.Contains("C:\\run\\b.mp4", section);
    }

    [Fact]
    public void FormatForPrompt_with_no_paths_is_empty()
    {
        Assert.Equal("", DirectiveAttachments.FormatForPrompt([]));
    }
}
