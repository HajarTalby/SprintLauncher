namespace SprintLauncher.Notify;

public sealed record NotifyOptions(string Actor, string Level, string Text, string? Context)
{
    private static readonly HashSet<string> Actors = new(StringComparer.OrdinalIgnoreCase)
    {
        "ccode", "ag", "codex", "sl"
    };

    private static readonly HashSet<string> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        "info", "warn", "blocked"
    };

    public string ActorKey => Actor.ToUpperInvariant();

    public static bool TryParse(string[] args, out NotifyOptions? options, out string error)
    {
        options = null;
        error = string.Empty;
        string? actor = null;
        string? level = null;
        string? text = null;
        string? context = null;

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name is "--actor" or "--level" or "--text" or "--context")
            {
                if (i + 1 >= args.Length)
                {
                    error = $"Missing value for {name}.";
                    return false;
                }

                var value = args[++i];
                switch (name)
                {
                    case "--actor":
                        actor = value;
                        break;
                    case "--level":
                        level = value;
                        break;
                    case "--text":
                        text = value;
                        break;
                    case "--context":
                        context = value;
                        break;
                }
            }
            else
            {
                error = $"Unknown argument {name}.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(actor) || !Actors.Contains(actor))
        {
            error = "Invalid or missing --actor. Expected ccode, ag, codex, or sl.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(level) || !Levels.Contains(level))
        {
            error = "Invalid or missing --level. Expected info, warn, or blocked.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Missing --text.";
            return false;
        }

        options = new NotifyOptions(
            actor.Trim().ToLowerInvariant(),
            level.Trim().ToLowerInvariant(),
            text.Trim(),
            string.IsNullOrWhiteSpace(context) ? null : context.Trim());
        return true;
    }
}
