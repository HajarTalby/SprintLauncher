using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace SprintLauncher.UI;

public partial class MainWindow : Window
{
    private Process? _process;
    private StreamWriter? _stdin;
    private readonly ObservableCollection<ActorItem> _actors = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _elapsed = new();
    private string? _htmlReportPath;
    private string? _artifactsDir;
    private string _repoRoot = "";
    private string? _selectedOutputFile;

    private static readonly (string Name, string Group)[] Definitions =
    [
        ("ClaudePilotage",             "FAMILLE CLAUDE"),
        ("ClaudeImplementation",       "FAMILLE CLAUDE"),
        ("GptImplementation",          "FAMILLE GPT"),
        ("GptPilotage",                "FAMILLE GPT"),
        ("CommitteePilotageClaudeChat","COMITE PILOTAGE"),
        ("CommitteePilotageGptChat",   "COMITE PILOTAGE"),
        ("CommitteeClaudeChat",        "COMITE ARBITRAGE"),
        ("CommitteeCcode",             "COMITE ARBITRAGE"),
        ("CommitteeGptChat",           "COMITE ARBITRAGE"),
        ("CommitteeCodex",             "COMITE ARBITRAGE"),
        ("ClaudeQaVerdict",            "QA"),
        ("GptQaVerdict",               "QA"),
    ];

    public MainWindow()
    {
        InitializeComponent();
        _repoRoot = FindRepoRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();
        BuildActorList();
        _timer.Tick += (_, _) => TxtTimer.Text = _elapsed.Elapsed.ToString(@"mm\:ss");
        ActorList.ItemsSource = _actors;
    }

    // ─── Actor list ────────────────────────────────────────────────────────────
    private void BuildActorList()
    {
        _actors.Clear();
        string? lastGroup = null;
        foreach (var (name, group) in Definitions)
        {
            if (group != lastGroup)
            {
                _actors.Add(new ActorItem { IsHeader = true, DisplayName = group, Color = "#89b4fa", Icon = "—" });
                lastGroup = group;
            }
            _actors.Add(new ActorItem { DisplayName = name, Icon = "·", Color = "#585b70", IsHeader = false });
        }
    }

    // ─── Run / Stop ────────────────────────────────────────────────────────────
    private void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_process is { HasExited: false })
        {
            StopProcess("Arrêté par l'utilisateur.");
            return;
        }

        var sprint = TxtSprint.Text.Trim();
        var keys   = TxtIssueKeys.Text.Trim();

        if (!string.IsNullOrWhiteSpace(sprint))
        {
            // Sprint # prioritaire — passe --sprint N au CLI
            StartRun($"--sprint {sprint}");
        }
        else if (!string.IsNullOrWhiteSpace(keys))
        {
            StartRun(keys);
        }
        else
        {
            MessageBox.Show("Remplissez le champ Sprint # ou entrez au moins une clé ticket (ex: SERZENIA-138).",
                "Sprint Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartRun(string keys)
    {
        BuildActorList();
        TxtOutput.Clear();
        TxtLog.Clear();
        TxtSelectedActor.Text = "Sélectionnez un acteur pour voir sa sortie";
        BtnOpenOutputFile.IsEnabled = false;
        TabMain.SelectedIndex = 0; // journal tab during run
        BtnOpenReport.IsEnabled = false;
        BtnOpenArtifacts.IsEnabled = false;
        PnlInteractive.Visibility = Visibility.Collapsed;
        _htmlReportPath = null;
        _artifactsDir = null;
        _selectedOutputFile = null;
        BtnRun.Content = "  Arrêter";

        var selectedMode = (CmbMode.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "execution";

        // Release mode: sprint-launcher.exe sits next to this exe — use it directly.
        // Dev mode: no side-by-side exe — call dotnet run from the repo source.
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "sprint-launcher.exe");
        bool isRelease = File.Exists(sideBySide);

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
        };

        if (isRelease)
        {
            psi.FileName = sideBySide;
            psi.WorkingDirectory = AppContext.BaseDirectory;
            foreach (var k in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(k);
        }
        else
        {
            var slProject = Path.Combine(_repoRoot, "tools", "sprint-launcher");
            psi.FileName = "dotnet";
            psi.WorkingDirectory = _repoRoot;
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(slProject);
            psi.ArgumentList.Add("--no-build");
            psi.ArgumentList.Add("--");
            foreach (var k in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(k);
        }

        psi.ArgumentList.Add("--interactive");
        if (ChkWrite.IsChecked == true)   psi.ArgumentList.Add("--write");
        if (ChkNoCache.IsChecked == true) psi.ArgumentList.Add("--no-cache");
        if (ChkResume.IsChecked == true)  psi.ArgumentList.Add("--resume");
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(selectedMode);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnLine;
        _process.ErrorDataReceived  += OnError;
        _process.Exited             += OnExited;

        _process.Start();
        _stdin = _process.StandardInput;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _elapsed.Restart();
        _timer.Start();
        AppendLog($"Run démarré — mode : {selectedMode.ToUpperInvariant()}.");
    }

    private void StopProcess(string reason)
    {
        try { _stdin?.Write("n\n"); _stdin?.Flush(); } catch { }
        try { _process?.Kill(entireProcessTree: true); } catch { }
        AppendLog("ARRET — " + reason);
    }

    // ─── Output parsing ────────────────────────────────────────────────────────
    private static readonly char[] TrimChars = ['\r', '\n', ' '];
    // Strip ANSI escape sequences (e.g. \x1b[32m)
    private static readonly Regex AnsiRegex = new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private void OnLine(object _, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        var line = AnsiRegex.Replace(e.Data, "").TrimEnd(TrimChars).Replace("\r", "");
        if (string.IsNullOrWhiteSpace(line)) return;

        Dispatcher.InvokeAsync(() => HandleLine(line));
    }

    private void OnError(object _, DataReceivedEventArgs e)
    {
        // dotnet run stderr (build output) — only show real errors
        if (e.Data is null) return;
        var line = e.Data.TrimEnd(TrimChars);
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.StartsWith("MSBuild") || line.StartsWith("Compile") ||
            line.StartsWith("Build succeeded") || line.StartsWith("Build FAILED") ||
            line.Contains("warning") || line.Contains("error"))
        {
            Dispatcher.InvokeAsync(() => AppendLog("[build] " + line));
        }
    }

    private void OnExited(object? _, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _timer.Stop();
            BtnRun.Content = "  Lancer";
            PnlInteractive.Visibility = Visibility.Collapsed;
            var code = _process?.ExitCode ?? -1;
            var done = _actors.Count(a => !a.IsHeader && a.Status is "success" or "semi" or "skipped");
            TxtStatus.Text = code == 0
                ? $"Terminé avec succès — {done} acteurs — {_elapsed.Elapsed:mm\\:ss}"
                : $"Terminé avec erreur (exit {code}) — {_elapsed.Elapsed:mm\\:ss}";
            AppendLog(code == 0 ? "Run terminé OK." : $"Run terminé exit {code}.");
            if (_htmlReportPath is not null && File.Exists(_htmlReportPath))
                BtnOpenReport.IsEnabled = true;
            if (_artifactsDir is not null && Directory.Exists(_artifactsDir))
                BtnOpenArtifacts.IsEnabled = true;
        });
    }

    private void HandleLine(string line)
    {
        // ── Actor running (terminal spinner — kept for manual CLI use) ──────────
        if (line.TrimStart().StartsWith(">") && Regex.IsMatch(line, @">\s+\w+") && !line.Contains("[") && !line.Contains("GO"))
        {
            var m = Regex.Match(line, @">\s+(\w+)");
            if (m.Success)
            {
                SetStatus(m.Groups[1].Value, "running");
                TxtStatus.Text = $"En cours : {m.Groups[1].Value}";
            }
            return; // don't log spinner lines
        }

        // ── Actor success (redirected clean format) ────────────────────────────
        if (Regex.IsMatch(line, @"^\s+ok\s+\w+"))
        {
            var m = Regex.Match(line, @"ok\s+(\w+)\s+\((\d+)s");
            if (m.Success)
            {
                var name = m.Groups[1].Value;
                var sec  = m.Groups[2].Value + "s";
                SetStatus(name, "success", sec);
                LoadActorOutput(name);
                AppendLog($"OK  {name}  ({sec})");
            }
            return;
        }

        // ── Actor success (terminal format) ───────────────────────────────────
        if (line.TrimStart().StartsWith("✓"))
        {
            var m = Regex.Match(line, @"✓\s+(\w+)\s+\((\d+)s");
            if (m.Success)
            {
                var name = m.Groups[1].Value;
                var sec  = m.Groups[2].Value + "s";
                SetStatus(name, "success", sec);
                LoadActorOutput(name);
                AppendLog($"OK  {name}  ({sec})");
            }
            return;
        }

        // ── Actor failed (redirected clean format) ────────────────────────────
        if (Regex.IsMatch(line, @"^\s+echec\s+\w+"))
        {
            var m = Regex.Match(line, @"echec\s+(\w+)\s+\((\d+)s\)");
            if (m.Success)
            {
                SetStatus(m.Groups[1].Value, "failed");
                AppendLog($"ECHEC  {m.Groups[1].Value}  ({m.Groups[2].Value}s)");
            }
            return;
        }

        // ── Actor failed (terminal format) ────────────────────────────────────
        if (line.TrimStart().StartsWith("✗"))
        {
            var m = Regex.Match(line, @"✗\s+(\w+)\s+\((\d+)s\)");
            if (m.Success)
            {
                SetStatus(m.Groups[1].Value, "failed");
                AppendLog($"ECHEC  {m.Groups[1].Value}  ({m.Groups[2].Value}s)");
            }
            return;
        }

        // ── Semi-manual ────────────────────────────────────────────────────────
        if (Regex.IsMatch(line, @"^\s+~\s+\w+") || Regex.IsMatch(line, @"^\s+~\s+\w+"))
        {
            var m = Regex.Match(line, @"~\s+(\w+)");
            if (m.Success)
            {
                SetStatus(m.Groups[1].Value, "semi");
                AppendLog($"SEMI  {m.Groups[1].Value}");
            }
            return;
        }

        // ── Skipped ────────────────────────────────────────────────────────────
        if (line.Contains("deja completé") || line.Contains("déjà complété"))
        {
            var m = Regex.Match(line, @"○\s+(\w+)");
            if (m.Success) SetStatus(m.Groups[1].Value, "skipped");
            return;
        }

        // ── Publish result ─────────────────────────────────────────────────────
        if (line.TrimStart().StartsWith("✓ [") || line.TrimStart().StartsWith("~ ["))
        {
            AppendLog(CleanSymbols(line));
            return;
        }

        // ── Group header ───────────────────────────────────────────────────────
        if (line.Contains("──") && line.Contains("FAMILLE") || line.Contains("COMITÉ") ||
            line.Contains("QA") || line.Contains("PILOTAGE"))
        {
            AppendLog("");
            AppendLog(CleanSymbols(line));
            return;
        }

        // ── Interactive checkpoint ─────────────────────────────────────────────
        if (line.Contains("GO pour continuer"))
        {
            var m = Regex.Match(line, @"continuer avec (.+?)\s*\?");
            var next = m.Success ? m.Groups[1].Value.Trim(' ', '─') : "prochain groupe";
            TxtCheckpointTitle.Text = $"Groupe terminé — Prêt pour : {next}";
            AppendLog("");
            AppendLog(">>> Checkpoint — GO ou ARRET");
            PnlInteractive.Visibility = Visibility.Visible;
            return;
        }

        // ── Artifacts dir (emitted at startup and at end) ─────────────────────
        if (line.Contains("Artefacts dans :") || line.Contains("Artefacts écrits dans"))
        {
            var m = Regex.Match(line, @"Artefacts[^\:]*:\s*(.+)");
            if (m.Success)
            {
                var path = m.Groups[1].Value.Trim();
                _artifactsDir = Path.IsPathRooted(path)
                    ? path
                    : Path.GetFullPath(Path.Combine(_repoRoot, path));
            }
            return;
        }

        // ── HTML report path ───────────────────────────────────────────────────
        if (line.Contains("Rapport HTML"))
        {
            var m = Regex.Match(line, @"Rapport HTML\s*:\s*(.+)");
            if (m.Success)
            {
                _htmlReportPath = m.Groups[1].Value.Trim();
                AppendLog("Rapport HTML disponible.");
            }
            return;
        }

        // ── Jira fetch lines ───────────────────────────────────────────────────
        if (line.Contains("Lecture") || line.Contains("commentaire") || line.Contains("Cache"))
        {
            AppendLog(CleanSymbols(line));
            return;
        }

        // ── Default: log if not noise ──────────────────────────────────────────
        if (!line.Contains("⟳") && line.Length < 200)
            AppendLog(CleanSymbols(line));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────
    private static string CleanSymbols(string s) =>
        s.Replace("──", "--").Replace("─", "-")
         .Replace("▶", ">").Replace("⟳", ">")
         .Replace("✓", "OK").Replace("✗", "ECHEC")
         .Replace("○", "o").Replace("⚠", "!")
         .Replace("—", "-").Replace("–", "-")  // em-dash, en-dash
         .Trim();

    private void SetStatus(string name, string status, string? elapsed = null)
    {
        var item = _actors.FirstOrDefault(a => !a.IsHeader && a.DisplayName == name);
        if (item is null) return;
        item.Status = status;
        if (elapsed is not null) item.Elapsed = elapsed;
        item.Icon = status switch
        {
            "running" => "►",
            "success" => "✓",
            "failed"  => "✗",
            "semi"    => "~",
            "skipped" => "○",
            _         => "·"
        };
        item.Color = status switch
        {
            "running" => "#89b4fa",
            "success" => "#a6e3a1",
            "failed"  => "#f38ba8",
            "semi"    => "#fab387",
            "skipped" => "#6c7086",
            _         => "#585b70"
        };
        var done = _actors.Count(a => !a.IsHeader && a.Status is "success" or "failed" or "semi" or "skipped");
        var total = _actors.Count(a => !a.IsHeader);
        TxtProgress.Text = $"{done}/{total}";
    }

    private void LoadActorOutput(string name)
    {
        if (_artifactsDir is null) return;
        var path = Path.Combine(_artifactsDir, $"output-{name}.txt");
        if (!File.Exists(path)) return;

        // Auto-select this actor and show its output
        var item = _actors.FirstOrDefault(a => !a.IsHeader && a.DisplayName == name);
        if (item is not null) ActorList.SelectedItem = item;

        ShowOutputFile(path, name);
    }

    private void ShowOutputFile(string path, string actorName)
    {
        _selectedOutputFile = path;
        TxtSelectedActor.Text = actorName;
        try
        {
            var content = File.ReadAllText(path);
            TxtOutput.Text = content;
            OutputScroll.ScrollToTop();
        }
        catch (Exception ex)
        {
            TxtOutput.Text = $"Impossible de lire le fichier :\n{ex.Message}";
        }
        BtnOpenOutputFile.IsEnabled = true;
        TabMain.SelectedIndex = 1; // switch to actor output tab
    }

    private void AppendLog(string line)
    {
        TxtLog.AppendText(line + "\n");
        LogScroll.ScrollToEnd();
    }

    // ─── Actor selection ───────────────────────────────────────────────────────
    private void ActorList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ActorList.SelectedItem is not ActorItem item || item.IsHeader) return;
        if (_artifactsDir is null) return;
        var path = Path.Combine(_artifactsDir, $"output-{item.DisplayName}.txt");
        if (File.Exists(path)) ShowOutputFile(path, item.DisplayName);
        else
        {
            TxtOutput.Text = "Sortie non disponible — acteur non encore exécuté.";
            TxtSelectedActor.Text = item.DisplayName;
            BtnOpenOutputFile.IsEnabled = false;
        }
    }

    // ─── Interactive checkpoint ────────────────────────────────────────────────
    private void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        PnlInteractive.Visibility = Visibility.Collapsed;
        TxtStatus.Text = "GO — poursuite vers le prochain groupe...";
        AppendLog(">>> GO");
        SendStdin("\n");
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        PnlInteractive.Visibility = Visibility.Collapsed;
        AppendLog(">>> ARRET");
        SendStdin("n\n");
    }

    private void SendStdin(string s)
    {
        try { _stdin?.Write(s); _stdin?.Flush(); } catch { }
    }

    // ─── Buttons ───────────────────────────────────────────────────────────────
    private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_htmlReportPath is not null && File.Exists(_htmlReportPath))
            Process.Start(new ProcessStartInfo(_htmlReportPath) { UseShellExecute = true });
    }

    private void BtnOpenArtifacts_Click(object sender, RoutedEventArgs e)
    {
        if (_artifactsDir is not null && Directory.Exists(_artifactsDir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_artifactsDir}\"") { UseShellExecute = true });
    }

    private void BtnOpenOutputFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOutputFile is not null && File.Exists(_selectedOutputFile))
            Process.Start(new ProcessStartInfo(_selectedOutputFile) { UseShellExecute = true });
    }

    // ─── Utilities ─────────────────────────────────────────────────────────────
    private static string? FindRepoRoot(string start)
    {
        var d = new DirectoryInfo(start);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try { _process?.Kill(entireProcessTree: true); } catch { }
        base.OnClosing(e);
    }
}

// ─── Actor item ───────────────────────────────────────────────────────────────
public class ActorItem : INotifyPropertyChanged
{
    private string _icon = "·";
    private string _color = "#585b70";
    private string _elapsed = "";
    private string _status = "waiting";

    public bool IsHeader { get; init; }
    public string DisplayName { get; init; } = "";

    public string Status  { get => _status;  set { _status = value;  Notify(); } }
    public string Icon    { get => _icon;    set { _icon = value;    Notify(); } }
    public string Color   { get => _color;   set { _color = value;   Notify(); } }
    public string Elapsed { get => _elapsed; set { _elapsed = value; Notify(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
