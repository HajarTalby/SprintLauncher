using System.Text.Json;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class LiveChatTests : IDisposable
{
    private readonly string _dir;

    public LiveChatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sl-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── LiveChatProtocol : framing claude stream-json ─────────────────────────────

    [Fact]
    public void Claude_user_message_is_valid_single_line_json()
    {
        var line = LiveChatProtocol.ClaudeUserMessage("Providers paiement : branche-les.");

        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("user", root.GetProperty("type").GetString());
        var content = root.GetProperty("message").GetProperty("content")[0];
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("Providers paiement : branche-les.", content.GetProperty("text").GetString());
    }

    [Fact]
    public void Claude_message_escapes_newlines_and_quotes()
    {
        var line = LiveChatProtocol.ClaudeUserMessage("ligne1\nligne2 \"citation\"");
        Assert.DoesNotContain('\n', line[..^0].Replace("\\n", "")); // pas de \n littéral hors échappement
        using var doc = JsonDocument.Parse(line); // parse = preuve d'échappement correct
        Assert.Equal("ligne1\nligne2 \"citation\"",
            doc.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("go", true)]
    public void IsSendable_rejects_empty(string? text, bool expected) =>
        Assert.Equal(expected, LiveChatProtocol.IsSendable(text));

    // ── LiveInputInbox : drain incrémental ────────────────────────────────────────

    [Fact]
    public void Inbox_drains_only_new_complete_lines()
    {
        var path = Path.Combine(_dir, "inbox.txt");
        File.WriteAllText(path, "");
        var inbox = new LiveInputInbox(path);

        Assert.Empty(inbox.DrainNewLines());

        File.AppendAllText(path, "premier\ndeuxieme\n");
        Assert.Equal(new[] { "premier", "deuxieme" }, inbox.DrainNewLines());

        // Rien de neuf → vide, pas de rejeu.
        Assert.Empty(inbox.DrainNewLines());

        File.AppendAllText(path, "troisieme\n");
        Assert.Equal(new[] { "troisieme" }, inbox.DrainNewLines());
    }

    [Fact]
    public void Inbox_ignores_partial_line_until_complete()
    {
        var path = Path.Combine(_dir, "inbox.txt");
        File.WriteAllText(path, "");
        var inbox = new LiveInputInbox(path);

        File.AppendAllText(path, "sans fin de ligne");
        Assert.Empty(inbox.DrainNewLines()); // ligne incomplète : pas consommée

        File.AppendAllText(path, " maintenant\n");
        Assert.Equal(new[] { "sans fin de ligne maintenant" }, inbox.DrainNewLines());
    }

    [Fact]
    public void Inbox_starts_at_end_of_preexisting_file()
    {
        // Une intervention d'un tour précédent ne doit pas être rejouée.
        var path = Path.Combine(_dir, "inbox.txt");
        File.WriteAllText(path, "ancienne intervention\n");
        var inbox = new LiveInputInbox(path);

        Assert.Empty(inbox.DrainNewLines());
        File.AppendAllText(path, "nouvelle\n");
        Assert.Equal(new[] { "nouvelle" }, inbox.DrainNewLines());
    }

    [Fact]
    public void Inbox_path_for_role_is_per_role()
    {
        var p1 = LiveInputInbox.PathFor(_dir, ActorRole.GptImplementation);
        var p2 = LiveInputInbox.PathFor(_dir, ActorRole.ClaudeImplementation);
        Assert.NotEqual(p1, p2);
        Assert.Contains("GptImplementation", p1);
    }

    // ── CodexAppServerProtocol : JSON-RPC ────────────────────────────────────────

    [Fact]
    public void Codex_turn_start_has_correct_method_and_input()
    {
        var line = CodexAppServerProtocol.TurnStart(3, "thread-1", "analyse ceci");
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("turn/start", root.GetProperty("method").GetString());
        var p = root.GetProperty("params");
        Assert.Equal("thread-1", p.GetProperty("threadId").GetString());
        Assert.Equal("text", p.GetProperty("input")[0].GetProperty("type").GetString());
        Assert.Equal("analyse ceci", p.GetProperty("input")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Codex_steer_carries_expected_turn_id()
    {
        var line = CodexAppServerProtocol.TurnSteer(5, "thread-1", "turn-9", "en fait, branche les providers");
        using var doc = JsonDocument.Parse(line);
        var p = doc.RootElement.GetProperty("params");
        Assert.Equal("turn/steer", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("turn-9", p.GetProperty("expectedTurnId").GetString());
        Assert.Equal("thread-1", p.GetProperty("threadId").GetString());
    }

    [Fact]
    public void Codex_parses_thread_and_turn_ids_from_responses()
    {
        var threadResp = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"thread\":{\"id\":\"th-42\"}}}";
        var turnResp = "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"turn\":{\"id\":\"tn-7\"}}}";

        var m1 = CodexAppServerProtocol.Parse(threadResp);
        var m2 = CodexAppServerProtocol.Parse(turnResp);

        Assert.NotNull(m1);
        Assert.NotNull(m2);
        Assert.Equal("th-42", CodexAppServerProtocol.ThreadId(m1!.Root));
        Assert.Equal("tn-7", CodexAppServerProtocol.TurnId(m2!.Root));
    }

    [Fact]
    public void Codex_parses_agent_delta_and_turn_completed()
    {
        var delta = "{\"jsonrpc\":\"2.0\",\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"Bonjour\",\"turnId\":\"tn-7\",\"threadId\":\"th-42\",\"itemId\":\"i1\"}}";
        var done = "{\"jsonrpc\":\"2.0\",\"method\":\"turn/completed\",\"params\":{}}";

        var md = CodexAppServerProtocol.Parse(delta)!;
        var mc = CodexAppServerProtocol.Parse(done)!;

        Assert.Equal("Bonjour", CodexAppServerProtocol.AgentDelta(md.Method, md.Root));
        Assert.True(CodexAppServerProtocol.IsTurnCompleted(mc.Method));
        Assert.Null(CodexAppServerProtocol.AgentDelta(mc.Method, mc.Root));
    }

    [Fact]
    public void Codex_parse_returns_null_on_garbage()
    {
        Assert.Null(CodexAppServerProtocol.Parse("pas du json"));
        Assert.Null(CodexAppServerProtocol.Parse(""));
    }
}
