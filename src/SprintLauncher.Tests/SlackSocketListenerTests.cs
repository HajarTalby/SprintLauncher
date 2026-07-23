using System.Text;
using SprintLauncher.Notifications;
using Xunit;

namespace SprintLauncher.Tests;

public class SlackSocketListenerTests
{
    [Fact]
    public void Parse_reads_hello_envelope()
    {
        var envelope = SlackSocketListener.Parse("{\"type\":\"hello\"}");

        Assert.Equal("hello", envelope.Type);
        Assert.Null(envelope.EnvelopeId);
        Assert.Null(envelope.EventType);
    }

    [Fact]
    public void Parse_reads_disconnect_envelope()
    {
        var envelope = SlackSocketListener.Parse("{\"type\":\"disconnect\",\"reason\":\"warning\"}");

        Assert.Equal("disconnect", envelope.Type);
    }

    [Fact]
    public void Parse_reads_complete_events_api_message()
    {
        var envelope = SlackSocketListener.Parse("""
            {
              "type": "events_api",
              "envelope_id": "abc",
              "payload": { "event": {
                "type": "message", "channel": "C123", "text": "Bonjour",
                "subtype": "", "bot_id": "B123", "app_id": "A123", "user": "U123"
              }}
            }
            """);

        Assert.Equal("events_api", envelope.Type);
        Assert.Equal("abc", envelope.EnvelopeId);
        Assert.Equal("message", envelope.EventType);
        Assert.Equal("C123", envelope.Channel);
        Assert.Equal("Bonjour", envelope.Text);
        Assert.Equal("", envelope.Subtype);
        Assert.Equal("B123", envelope.BotId);
        Assert.Equal("A123", envelope.AppId);
        Assert.Equal("U123", envelope.User);
    }

    [Fact]
    public void BuildAck_returns_expected_json()
    {
        Assert.Equal("{\"envelope_id\":\"abc\"}", SlackSocketListener.BuildAck("abc"));
    }

    [Theory]
    [InlineData("bot_message", null, null, true)]
    [InlineData(null, "B123", null, true)]
    [InlineData(null, null, "A123", true)]
    [InlineData(null, null, null, false)]
    public void IsFromBot_filters_bot_markers(string? subtype, string? botId, string? appId, bool expected)
    {
        var envelope = new ParsedEnvelope("events_api", "id", "message", "C1", "texte", subtype, botId, appId, "U1");

        Assert.Equal(expected, SlackSocketListener.IsFromBot(envelope));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("message_changed", false)]
    [InlineData("message_deleted", false)]
    [InlineData("channel_join", false)]
    public void IsHumanMessage_keeps_only_messages_without_subtype(string? subtype, bool expected)
    {
        var envelope = new ParsedEnvelope("events_api", "id", "message", "C1", "texte", subtype, null, null, "U1");

        Assert.Equal(expected, SlackSocketListener.IsHumanMessage(envelope));
    }

    [Fact]
    public void ChannelActorMap_resolves_known_channels_case_insensitively()
    {
        var map = new ChannelActorMap(new Dictionary<string, string>
        {
            ["SLACK_CHANNEL_CCODE"] = "Ccode",
            ["slack_channel_ag"] = "Cag",
            ["SLACK_CHANNEL_CODEX"] = "Ccodex",
            ["SLACK_CHANNEL_SL"] = "Csl",
        });

        Assert.Equal("ccode", map.ResolveActor("CCODE"));
        Assert.Equal("ag", map.ResolveActor("cAG"));
        Assert.Equal("codex", map.ResolveActor("ccOdEx"));
        Assert.Equal("sl", map.ResolveActor("cSL"));
        Assert.Null(map.ResolveActor("Cunknown"));
    }

    [Fact]
    public void AppendToInbox_appends_flattened_utf8_lines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"slack-listener-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "live-input-codex.txt");
            File.WriteAllText(path, "deja la\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            SlackSocketListener.AppendToInbox(directory, "codex", "premiere\r\ndeuxieme\ntroisieme");

            Assert.Equal("deja la\npremiere deuxieme troisieme\n", File.ReadAllText(path));
            Assert.False(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Addressed_message_from_codex_channel_is_written_to_target_actor_inbox()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"slack-listener-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            const string text = "@ccode fais X";
            var actorKey = SlackSocketListener.RouteActorKey("codex", text);

            SlackSocketListener.AppendToInbox(directory, actorKey, text);

            Assert.Equal("@ccode fais X\n", File.ReadAllText(Path.Combine(directory, "live-input-ccode.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "live-input-codex.txt")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
