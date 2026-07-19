using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
    private FlowDocument _chatDoc = new();
    private bool _chatHasTurns;
    private string _lastRunKeys = "";
    // Acteur en train de travailler : cible d'une intervention live (chat live).
    private string? _activeActor;
    // TOUS les acteurs en cours (pipeline parallèle : deux moteurs peuvent tourner
    // en même temps). Le live ne route QUE vers un acteur réellement en cours —
    // sinon le message pourrit dans une inbox que personne ne lit (constat
    // 2026-07-17 : intervention routée vers un moteur mort au quota).
    private readonly HashSet<string> _runningActors = new(StringComparer.OrdinalIgnoreCase);
    // Correction d'un envoi (retour Hajar 2026-07-17) : dernier texte envoyé + lignes
    // mises en file qu'une version corrigée doit annuler (« !cancel » côté CLI).
    private string? _lastSentRaw;
    private List<string> _lastQueuedLines = [];
    private List<string> _cancelOnNextSend = [];
    // Pièces jointes du dernier envoi — restaurées par « Corriger » (SERZENIA-144 Lot 3).
    private List<string> _lastAttachments = [];
    private bool _lastRunWasDryRun = true;
    private bool _publishMode; // run courant = --publish-from-artifacts / --create-us (pas de pipeline acteurs)
    private string? _usProposalsRefKey;
    private readonly List<UsProposalDialog.ProposalView> _usProposals = [];
    // true = le CLI attend une réponse (checkpoint) → le champ envoie sur stdin ;
    // false = run en cours → le champ dépose pending-directive.txt (consommé à la prochaine US).
    private bool _checkpointActive;

    // Pièces jointes en attente sur la prochaine intervention (SERZENIA-144 Lot 3) :
    // chemins SOURCES choisis via le sélecteur de fichiers, vidés à l'envoi.
    private readonly List<string> _pendingAttachments = [];
    private const string AttachStartMarker = "[[SL_ATTACH]]";
    private const string AttachEndMarker = "[[/SL_ATTACH]]";

    // Panneau en mode « run » : directive déposable à tout moment, boutons de checkpoint inactifs.
    private void ShowRunModePanel()
    {
        _checkpointActive = false;
        TxtCheckpointTitle.Text = "Run en cours";
        TxtCheckpointHint.Text = "Dépose une directive à tout moment : l'outil la publiera sur Jira et l'appliquera à partir de la prochaine US ou du prochain checkpoint.";
        BtnGo.IsEnabled = false;
        BtnStop.IsEnabled = false;
        BtnConclude.IsEnabled = false;
        PnlInterventionInput.Visibility = Visibility.Visible;
        PnlInteractive.Visibility = Visibility.Visible;
    }

    // Liste de secours affichée avant réception du manifeste CLI (événement "manifest").
    private static readonly (string Name, string Group, string GroupName)[] Definitions =
    [
        ("ClaudePilotage",             "FAMILLE CLAUDE",   "FamilyClaude"),
        ("ClaudeImplementation",       "FAMILLE CLAUDE",   "FamilyClaude"),
        ("GptImplementation",          "FAMILLE GPT",      "FamilyGpt"),
        ("GptPilotage",                "FAMILLE GPT",      "FamilyGpt"),
        ("AnalysisCcode",              "ANALYSE",          "Analysis"),
        ("AnalysisCodex",              "ANALYSE",          "Analysis"),
        ("CommitteePilotageClaudeChat","COMITE PILOTAGE",  "CommitteePilotage"),
        ("CommitteePilotageGptChat",   "COMITE PILOTAGE",  "CommitteePilotage"),
        ("CommitteeClaudeChat",        "COMITE ARBITRAGE", "CommitteeArbitrage"),
        ("CommitteeCcode",             "COMITE ARBITRAGE", "CommitteeArbitrage"),
        ("CommitteeGptChat",           "COMITE ARBITRAGE", "CommitteeArbitrage"),
        ("CommitteeCodex",             "COMITE ARBITRAGE", "CommitteeArbitrage"),
        ("ClaudeQaVerdict",            "QA",               "Qa"),
        ("GptQaVerdict",               "QA",               "Qa"),
    ];

    public MainWindow()
    {
        InitializeComponent();
        _repoRoot = FindRepoRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();
        BuildActorList();
        _timer.Tick += (_, _) =>
        {
            TxtTimer.Text = _elapsed.Elapsed.ToString(@"mm\:ss");
            RefreshLiveOutput();
            RefreshDecisions();
        };
        ActorList.ItemsSource = _actors;
        ShowPreRunPanel(); // directives déposables AVANT même le lancement
    }

    // Avant tout run : le champ est déjà là — les directives déposées seront
    // publiées et appliquées dès le démarrage du prochain run.
    private void ShowPreRunPanel()
    {
        _checkpointActive = false;
        TxtCheckpointTitle.Text = "Prêt — directives déposables dès maintenant";
        TxtCheckpointHint.Text = "Écris tes décisions/directives (une par envoi, elles s'accumulent) : le prochain run les publiera sur l'US de pilotage et les appliquera dès le départ.";
        BtnGo.IsEnabled = false;
        BtnStop.IsEnabled = false;
        BtnConclude.IsEnabled = false;
        PnlInterventionInput.Visibility = Visibility.Visible;
        PnlInteractive.Visibility = Visibility.Visible;
    }

    // ─── Sortie live : ce que fait l'acteur PENDANT son tour ───────────────────
    private long _liveLastLength = -1;

    private void RefreshLiveOutput()
    {
        if (_artifactsDir is null || TabMain.SelectedIndex != 1) return;
        if (ActorList.SelectedItem is not ActorItem item || item.IsHeader || item.Status != "running") return;

        var livePath = Path.Combine(_artifactsDir, $"live-{item.DisplayName}.txt");
        if (!File.Exists(livePath))
        {
            if (_liveLastLength != 0)
            {
                _liveLastLength = 0;
                TxtSelectedActor.Text = $"{item.DisplayName} — en cours d'exécution…";
                OutputViewer.Document = new FlowDocument(new Paragraph(new Run(
                    "L'acteur travaille — sa sortie s'affichera ici au fil de l'eau " +
                    "(les acteurs claude n'émettent leur sortie qu'à la fin du tour).")));
            }
            return;
        }

        try
        {
            var info = new FileInfo(livePath);
            if (info.Length == _liveLastLength) return; // rien de nouveau
            _liveLastLength = info.Length;

            using var fs = new FileStream(livePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            var content = reader.ReadToEnd();

            TxtSelectedActor.Text = $"{item.DisplayName} — en cours (sortie live, {content.Length} car.)";
            OutputViewer.Document = MarkdownToFlow(content);
            if (OutputViewer.Template?.FindName("PART_ContentHost", OutputViewer) is System.Windows.Controls.ScrollViewer sv)
                sv.ScrollToEnd();
        }
        catch (IOException) { /* fichier en cours d'écriture — prochain tick */ }
    }

    // ─── Actor list ────────────────────────────────────────────────────────────
    private void BuildActorList()
    {
        _actors.Clear();
        string? lastGroup = null;
        foreach (var (name, group, groupName) in Definitions)
        {
            if (group != lastGroup)
            {
                _actors.Add(new ActorItem { IsHeader = true, DisplayName = group, GroupName = groupName, Color = "#89b4fa", Icon = "—" });
                lastGroup = group;
            }
            _actors.Add(new ActorItem { DisplayName = name, GroupName = groupName, Icon = "·", Color = "#585b70", IsHeader = false });
        }
    }

    // Reconstruit la liste depuis le manifeste émis par le CLI — plus de liste codée en dur.
    private void RebuildActorsFromManifest(JsonElement roles)
    {
        _actors.Clear();
        string? lastGroup = null;
        foreach (var r in roles.EnumerateArray())
        {
            var name = r.GetProperty("name").GetString() ?? "";
            var groupName = r.GetProperty("group").GetString() ?? "";
            var groupLabel = r.GetProperty("groupLabel").GetString() ?? groupName;
            if (groupLabel != lastGroup)
            {
                _actors.Add(new ActorItem { IsHeader = true, DisplayName = groupLabel, GroupName = groupName, Color = "#89b4fa", Icon = "—" });
                lastGroup = groupLabel;
            }
            _actors.Add(new ActorItem { DisplayName = name, GroupName = groupName, Icon = "·", Color = "#585b70", IsHeader = false });
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
        OutputViewer.Document = new FlowDocument();
        _chatDoc = NewChatDocument();
        ChatViewer.Document = _chatDoc;
        _chatHasTurns = false;
        _publishMode = false;
        TxtLog.Clear();
        TxtPrompt.Clear();
        TxtSelectedActor.Text = "Sélectionnez un acteur pour voir sa sortie";
        BtnOpenOutputFile.IsEnabled = false;
        TabMain.SelectedIndex = 0; // journal tab during run
        BtnOpenReport.IsEnabled = false;
        BtnOpenArtifacts.IsEnabled = false;
        BtnPublish.IsEnabled = false;
        ShowRunModePanel();
        _htmlReportPath = null;
        _artifactsDir = null;
        _selectedOutputFile = null;
        BtnRun.Content = "  Arrêter";

        var selectedMode = (CmbMode.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "execution";
        _lastRunKeys = keys;
        _lastRunWasDryRun = ChkWrite.IsChecked != true;
        _runStartTime = DateTime.Now;

        var cliArgs = new List<string>(keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            "--interactive",
        };
        if (ChkWrite.IsChecked == true)   cliArgs.Add("--write");
        if (ChkNoCache.IsChecked == true) cliArgs.Add("--no-cache");
        if (ChkResume.IsChecked == true)  cliArgs.Add("--resume");
        cliArgs.Add("--mode");
        cliArgs.Add(selectedMode);

        StartProcess(BuildPsi(cliArgs));
        AppendLog($"Run démarré — mode : {selectedMode.ToUpperInvariant()}.");
    }

    // Publication des sorties dry-run validées — aucune réexécution d'acteur.
    private void StartPublish(string rolesCsv)
    {
        _publishMode = true;
        BtnPublish.IsEnabled = false;
        BtnRun.Content = "  Arrêter";
        TabMain.SelectedIndex = 0;

        var cliArgs = new List<string>(_lastRunKeys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            "--publish-from-artifacts",
            "--write",
            "--roles", rolesCsv,
        };

        StartProcess(BuildPsi(cliArgs));
        AppendLog($"Publication Jira démarrée ({rolesCsv}).");
    }

    // Release : sprint-launcher.exe à côté de l'UI. Dev : dotnet run depuis le repo source.
    private ProcessStartInfo BuildPsi(List<string> cliArgs)
    {
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
            // UTF-8 SANS BOM : Encoding.UTF8 émettrait un BOM (﻿) en tête de la
            // 1re écriture stdin — le CLI lirait alors la 1re réponse (« GO ») comme
            // « ﻿ », donc comme une DIRECTIVE parasite, pas un GO. (Sans encodage
            // UTF-8 du tout, les accents des interventions seraient altérés.)
            StandardInputEncoding  = new System.Text.UTF8Encoding(false),
        };

        if (isRelease)
        {
            psi.FileName = sideBySide;
            psi.WorkingDirectory = AppContext.BaseDirectory;
        }
        else
        {
            var slProject = Path.Combine(_repoRoot, "src", "SprintLauncher");
            psi.FileName = "dotnet";
            psi.WorkingDirectory = _repoRoot;
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(slProject);
            psi.ArgumentList.Add("--no-build");
            psi.ArgumentList.Add("--");
        }

        foreach (var a in cliArgs)
            psi.ArgumentList.Add(a);
        return psi;
    }

    private void StartProcess(ProcessStartInfo psi)
    {
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

        // Protocole structuré CLI→UI (SERZENIA-143) — prioritaire sur le parsing texte.
        if (e.Data.StartsWith("@@EVENT "))
        {
            var json = e.Data["@@EVENT ".Length..];
            Dispatcher.InvokeAsync(() => HandleEvent(json));
            return;
        }

        var line = AnsiRegex.Replace(e.Data, "").TrimEnd(TrimChars).Replace("\r", "");
        if (string.IsNullOrWhiteSpace(line)) return;

        Dispatcher.InvokeAsync(() => HandleLine(line));
    }

    // ─── Événements structurés ─────────────────────────────────────────────────
    private void HandleEvent(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { AppendLog(json); return; }

        using (doc)
        {
            var type = doc.RootElement.GetProperty("type").GetString();
            var data = doc.RootElement.GetProperty("data");

            switch (type)
            {
                case "manifest":
                    _artifactsDir = data.GetProperty("artifactsDir").GetString();
                    _lastRunWasDryRun = data.GetProperty("dryRun").GetBoolean();
                    RebuildActorsFromManifest(data.GetProperty("roles"));
                    BtnOpenArtifacts.IsEnabled = Directory.Exists(_artifactsDir);
                    break;

                case "group":
                    AppendLog("");
                    AppendLog("-- " + (data.GetProperty("label").GetString() ?? "") + " --");
                    break;

                case "actor-start":
                {
                    var role = data.GetProperty("role").GetString() ?? "";
                    _activeActor = role; // cible d'une intervention live non adressée
                    _runningActors.Add(role);
                    SetStatus(role, "running");
                    TxtStatus.Text = $"En cours : {role}";
                    break;
                }

                case "actor-done":
                {
                    var role = data.GetProperty("role").GetString() ?? "";
                    _runningActors.Remove(role);
                    if (_activeActor == role) _activeActor = _runningActors.FirstOrDefault();
                    var success = data.GetProperty("success").GetBoolean();
                    var semi = data.GetProperty("semiManual").GetBoolean();
                    var secs = data.GetProperty("seconds").GetInt32();
                    if (semi)         { SetStatus(role, "semi");    AppendLog($"SEMI  {role}"); }
                    else if (success) { SetStatus(role, "success", secs + "s"); LoadActorOutput(role); AppendLog($"OK  {role}  ({secs}s)"); }
                    else              { SetStatus(role, "failed");  AppendLog($"ECHEC  {role}  ({secs}s)"); }
                    break;
                }

                case "turn":
                {
                    var speaker = data.GetProperty("speaker").GetString() ?? "";
                    var content = data.GetProperty("content").GetString() ?? "";
                    var round   = data.GetProperty("round").GetInt32();
                    var isIntervention = data.GetProperty("isIntervention").GetBoolean();
                    var isFinal = data.TryGetProperty("isFinalSynthesis", out var f) && f.GetBoolean();
                    AppendChatTurn(speaker, content, isIntervention, round, isFinal);
                    break;
                }

                case "checkpoint":
                {
                    var kind = data.GetProperty("kind").GetString();
                    _checkpointActive = true;
                    BtnGo.IsEnabled = true;
                    BtnStop.IsEnabled = true;
                    BtnConclude.IsEnabled = kind is "round" or "review";
                    if (kind == "review")
                    {
                        var group = data.GetProperty("group").GetString() ?? "";
                        TxtCheckpointTitle.Text = $"Revue croisée reçue — {group.Replace("RevueCroisee-", "")}";
                        TxtCheckpointHint.Text  = "GO = l'implémenteur applique les correctifs · écris une directive pour les orienter · n dans le champ = publier la revue sans correctifs.";
                        PnlInterventionInput.Visibility = Visibility.Visible;
                        PnlInteractive.Visibility = Visibility.Visible;
                        TabMain.SelectedIndex = 2;
                        TxtIntervention.Focus();
                    }
                    else if (kind == "round")
                    {
                        var group = data.GetProperty("group").GetString() ?? "";
                        var round = data.GetProperty("round").GetInt32();
                        TxtCheckpointTitle.Text = $"Discussion {group} — round {round - 1} terminé";
                        TxtCheckpointHint.Text  = "GO = les acteurs continuent seuls · écris un message pour intervenir avec autorité · Conclure = synthèse finale immédiate.";
                        PnlInterventionInput.Visibility = Visibility.Visible;
                        PnlInteractive.Visibility = Visibility.Visible;
                        TabMain.SelectedIndex = 2; // onglet DISCUSSION
                        TxtIntervention.Focus();
                    }
                    else
                    {
                        var next = data.TryGetProperty("next", out var n) ? n.GetString() : "prochain groupe";
                        TxtCheckpointTitle.Text = $"Groupe terminé — Prêt pour : {next}";
                        TxtCheckpointHint.Text  = "GO = continuer · écris une directive : elle sera appliquée avec autorité au groupe suivant · ARRET = sauvegarder et quitter.";
                        PnlInterventionInput.Visibility = Visibility.Visible;
                        PnlInteractive.Visibility = Visibility.Visible;
                        TxtIntervention.Focus();
                    }
                    AppendLog("");
                    AppendLog(">>> Checkpoint — en attente de ta décision");
                    break;
                }

                case "publish":
                {
                    var key = data.GetProperty("key").GetString();
                    var actor = data.GetProperty("actor").GetString();
                    var status = data.GetProperty("status").GetString();
                    AppendLog($"PUBLISH [{key}] {actor} : {status}");
                    break;
                }

                case "run-end":
                {
                    _htmlReportPath = data.GetProperty("reportPath").GetString();
                    _artifactsDir = data.GetProperty("artifactsDir").GetString();
                    if (data.TryGetProperty("publishable", out var p) && p.GetBoolean())
                        EnablePublishSelection();
                    break;
                }

                case "live-delivered":
                {
                    // Confirmation de remise d'une intervention live : visible dans le
                    // fil DISCUSSION et le journal, pas seulement dans la sortie acteur.
                    var target = data.GetProperty("target").GetString() ?? "";
                    var txt = data.GetProperty("text").GetString() ?? "";
                    AppendChatTurn("Sprint Launcher", $"⚑ Intervention transmise à **{target}** en cours de tour — il doit en accuser réception dans sa sortie.", isIntervention: true, round: 0, isFinal: false);
                    AppendLog($"⚑ live → {target} : {txt}");
                    break;
                }

                case "engine-reactivated":
                {
                    var engine = data.GetProperty("engine").GetString() ?? "";
                    AppendChatTurn("Sprint Launcher", $"⚡ **{engine}** réactivé sur intervention de Hajar (quota signalé disponible) — il reprend au prochain tour.", isIntervention: true, round: 0, isFinal: false);
                    AppendLog($"⚡ {engine} réactivé (quota)");
                    break;
                }

                case "implementation-us":
                {
                    var key = data.GetProperty("key").GetString();
                    var engine = data.GetProperty("engine").GetString();
                    var relief = data.TryGetProperty("relief", out var rl) && rl.GetBoolean();
                    _activeActor = engine; // moteur d'implémentation = cible d'une intervention live
                    TxtStatus.Text = $"Implémentation {key} → {engine}{(relief ? " (relève)" : "")}";
                    AppendLog(relief ? $"US {key} -> RELEVE par {engine}" : $"US {key} -> {engine}");
                    break;
                }

                case "qa-execution":
                    AppendLog("QA outillée : exécution réelle de build+tests en cours (logs injectés au verdict)...");
                    TxtStatus.Text = "QA outillée — exécution réelle des tests";
                    break;

                case "ecarts":
                {
                    var count = data.GetProperty("count").GetInt32();
                    var cycle = data.GetProperty("cycle").GetInt32();
                    if (count == 0)
                    {
                        AppendLog("DoD : AUCUN ecart au verdict QA — sprint cloturable (decision finale : Hajar).");
                        TxtStatus.Text = "✔ DoD : aucun écart — sprint clôturable";
                    }
                    else
                    {
                        AppendLog($"!! {count} ecart(s) au verdict QA — remediation (cycle {cycle + 1}).");
                        TxtStatus.Text = $"⚠ {count} écart(s) — remédiation en cours";
                    }
                    break;
                }

                case "implementation-blocked":
                {
                    var key = data.GetProperty("key").GetString();
                    AppendLog($"!! {key} NON implementee — l'acteur attendait un GO (rejouee au prochain --resume).");
                    break;
                }

                case "actor-heartbeat":
                {
                    var role = data.GetProperty("role").GetString();
                    var secs = data.GetProperty("seconds").GetInt32();
                    TxtStatus.Text = $"{role} travaille depuis {secs / 60} min (vivant — sortie à la fin du tour ou en live)";
                    break;
                }

                case "quota-wait":
                {
                    var minutes = data.GetProperty("minutes").GetInt32();
                    TxtStatus.Text = $"⏳ Quota épuisé des deux côtés — nouvelle tentative automatique dans {minutes} min";
                    AppendLog($"!! Quota epuise des deux cotes — l'outil reste ouvert et retente dans {minutes} min.");
                    break;
                }

                case "quota-retry":
                {
                    TxtStatus.Text = "⏳ Fenêtre de quota retentée — moteurs réactivés";
                    AppendLog("Quota : moteurs reactives, reprise automatique.");
                    break;
                }

                case "quota":
                {
                    var engine = data.GetProperty("engine").GetString() ?? "";
                    TxtStatus.Text = $"⚠ Quota épuisé : {engine} — relève en cours";
                    AppendLog($"!! QUOTA epuise : {engine} — l'autre moteur prend la releve.");
                    SetStatus(engine, "failed");
                    break;
                }

                case "litige":
                {
                    var detail = data.TryGetProperty("detail", out var d) ? d.GetString() : "";
                    TxtStatus.Text = $"⚠ Litige détecté : {detail}";
                    AppendLog("");
                    AppendLog($"!! LITIGE detecte en analyse : {detail} — l'arbitrage sera propose.");
                    break;
                }

                case "us-proposals":
                {
                    _usProposals.Clear();
                    _usProposalsRefKey = data.GetProperty("refKey").GetString();
                    foreach (var prop in data.GetProperty("proposals").EnumerateArray())
                        _usProposals.Add(new UsProposalDialog.ProposalView(
                            prop.GetProperty("summary").GetString() ?? "",
                            prop.GetProperty("description").GetString() ?? ""));
                    BtnCreateUs.IsEnabled = _usProposals.Count > 0;
                    AppendLog("");
                    AppendLog($"Le cadrage propose {_usProposals.Count} US — clique « Créer US proposées » pour les valider.");
                    break;
                }

                case "us-created":
                {
                    var key = data.TryGetProperty("key", out var k) ? k.GetString() : null;
                    var summary = data.GetProperty("summary").GetString();
                    var isDry = data.GetProperty("dryRun").GetBoolean();
                    var err = data.TryGetProperty("error", out var er) ? er.GetString() : null;
                    AppendLog(err is not null ? $"ECHEC US '{summary}' : {err}"
                        : isDry ? $"DRY-RUN US : {summary}"
                        : $"US CREEE {key} : {summary}");
                    break;
                }
            }
        }
    }

    // Après un dry-run complet : cases à cocher sur les sorties publiables.
    private void EnablePublishSelection()
    {
        bool any = false;
        var collectiveGroups = new[] { "Analysis", "CommitteePilotage", "CommitteeArbitrage", "Qa" };
        foreach (var item in _actors)
        {
            if (item.IsHeader && collectiveGroups.Contains(item.GroupName))
            {
                // Le header d'un groupe collectif représente le commentaire de synthèse de la discussion
                var groupDone = _actors.Any(a => !a.IsHeader && a.GroupName == item.GroupName && a.Status == "success");
                if (groupDone) { item.CheckVisibility = "Visible"; item.IsChecked = true; any = true; }
            }
            else if (!item.IsHeader && item.Status == "success" && !collectiveGroups.Contains(item.GroupName))
            {
                item.CheckVisibility = "Visible";
                item.IsChecked = true;
                any = true;
            }
        }
        if (any)
        {
            BtnPublish.IsEnabled = true;
            AppendLog("");
            AppendLog("Dry-run validable : coche les sorties à publier puis clique « Publier sélection → Jira ».");
        }
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
            HideCheckpoint();
            var code = _process?.ExitCode ?? -1;

            if (_publishMode)
            {
                _publishMode = false;
                TxtStatus.Text = code == 0
                    ? $"Publication Jira terminée — {_elapsed.Elapsed:mm\\:ss}"
                    : $"Publication échouée (exit {code}) — voir le journal";
                AppendLog(code == 0 ? "Publication terminée." : $"Publication échouée exit {code}.");
                ShowPreRunPanel();
                return;
            }

            var done = _actors.Count(a => !a.IsHeader && a.Status is "success" or "semi" or "skipped");
            TxtStatus.Text = code == 0
                ? $"Terminé avec succès — {done} acteurs — {_elapsed.Elapsed:mm\\:ss}"
                : $"Terminé avec erreur (exit {code}) — {_elapsed.Elapsed:mm\\:ss}";
            AppendLog(code == 0 ? "Run terminé OK." : $"Run terminé exit {code}.");
            if (_htmlReportPath is not null && File.Exists(_htmlReportPath))
                BtnOpenReport.IsEnabled = true;
            if (_artifactsDir is not null && Directory.Exists(_artifactsDir))
                BtnOpenArtifacts.IsEnabled = true;
            ShowPreRunPanel(); // le champ reste disponible entre les runs
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

        // ── Interactive checkpoint (legacy — l'événement "checkpoint" fait foi) ─
        if (line.Contains("GO pour continuer"))
        {
            if (PnlInteractive.Visibility == Visibility.Visible) return; // déjà affiché par l'événement
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

    // ─── Markdown → FlowDocument (rendu style Claude/ChatGPT) ────────────────
    private static readonly Brush ColorFg       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cdd6f4")!);
    private static readonly Brush ColorH2       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#89b4fa")!);
    private static readonly Brush ColorH3       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#a6adc8")!);
    private static readonly Brush ColorBullet   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6c7086")!);
    private static readonly Brush ColorBold     = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffffff")!);
    private static readonly Brush ColorApprover = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f9e2af")!);
    private static readonly Brush ColorMuted    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6c7086")!);
    private static readonly FontFamily SansFont = new("Segoe UI, Arial");

    // ─── Fil de discussion (onglet DISCUSSION) ─────────────────────────────────
    private static FlowDocument NewChatDocument() => new()
    {
        FontFamily  = SansFont,
        FontSize    = 14,
        Foreground  = ColorFg,
        LineHeight  = 24,
        PagePadding = new Thickness(20, 16, 20, 16),
    };

    private void AppendChatTurn(string speaker, string content, bool isIntervention, int round, bool isFinal)
    {
        if (!_chatHasTurns)
        {
            _chatHasTurns = true;
            TabMain.SelectedIndex = 2; // bascule sur l'onglet DISCUSSION au premier tour
        }

        var label = isIntervention
            ? $"⚑ {speaker}"
            : isFinal ? $"{speaker} — synthèse finale" : $"{speaker} — round {round}";

        var header = new Paragraph { Margin = new Thickness(0, 16, 0, 4) };
        header.Inlines.Add(new Run(label)
        {
            FontWeight = FontWeights.Bold,
            FontSize = 13.5,
            Foreground = isIntervention ? ColorApprover : ColorH2,
        });
        // Date + heure sur CHAQUE message, y compris ceux de Hajar (demande 2026-07-17).
        header.Inlines.Add(new Run($"   {DateTime.Now:dd/MM HH:mm:ss}")
        {
            FontSize = 11,
            Foreground = ColorMuted,
        });
        _chatDoc.Blocks.Add(header);

        // Réutilise le rendu markdown existant, blocs déplacés dans le fil
        var body = MarkdownToFlow(content);
        while (body.Blocks.FirstBlock is { } block)
        {
            body.Blocks.Remove(block);
            _chatDoc.Blocks.Add(block);
        }

        ScrollChatToEnd();
    }

    private void ScrollChatToEnd()
    {
        if (ChatViewer.Template?.FindName("PART_ContentHost", ChatViewer) is System.Windows.Controls.ScrollViewer sv)
            sv.ScrollToEnd();
    }

    private static FlowDocument MarkdownToFlow(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily  = SansFont,
            FontSize    = 14,
            Foreground  = ColorFg,
            LineHeight  = 24,
            PagePadding = new Thickness(20, 16, 20, 16),
        };

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // H2 — ## Titre
            if (line.StartsWith("## "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 18, 0, 4), LineHeight = 28 };
                p.Inlines.Add(new Run(line[3..])
                    { FontSize = 17, FontWeight = FontWeights.Bold, Foreground = ColorH2 });
                doc.Blocks.Add(p);
                continue;
            }

            // H3 — ### Titre
            if (line.StartsWith("### "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 12, 0, 2), LineHeight = 26 };
                p.Inlines.Add(new Run(line[4..])
                    { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = ColorH3 });
                doc.Blocks.Add(p);
                continue;
            }

            // Bullet — - text ou   - text
            if (Regex.IsMatch(line, @"^(\s*)- (.*)"))
            {
                var m = Regex.Match(line, @"^(\s*)- (.*)");
                var indent = m.Groups[1].Value.Length * 8.0 + 16.0;
                var p = new Paragraph { Margin = new Thickness(indent, 1, 0, 1), TextIndent = -14, LineHeight = 22 };
                p.Inlines.Add(new Run("• ") { Foreground = ColorBullet });
                AddBoldInlines(p, m.Groups[2].Value);
                doc.Blocks.Add(p);
                continue;
            }

            // Ligne vide → espacement entre blocs
            if (string.IsNullOrWhiteSpace(line))
            {
                doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 3, 0, 3) });
                continue;
            }

            // Paragraphe normal
            var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2), LineHeight = 22 };
            AddBoldInlines(para, line);
            doc.Blocks.Add(para);
        }

        return doc;
    }

    private static void AddBoldInlines(Paragraph p, string text)
    {
        // Découpe sur **bold** et *italic*
        var parts = Regex.Split(text, @"\*\*(.+?)\*\*");
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i])) continue;
            if (i % 2 == 1)
                p.Inlines.Add(new Run(parts[i]) { FontWeight = FontWeights.Bold, Foreground = ColorBold });
            else
                p.Inlines.Add(new Run(parts[i]));
        }
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

    private DateTime _runStartTime = DateTime.MinValue;

    private void ShowOutputFile(string path, string actorName)
    {
        _selectedOutputFile = path;
        // Horodatage + alerte explicite : une sortie antérieure au run courant est marquée
        var ts = File.GetLastWriteTime(path);
        var stale = _runStartTime != DateTime.MinValue && ts < _runStartTime;
        TxtSelectedActor.Text = stale
            ? $"⚠ RUN PRÉCÉDENT — {actorName} (sortie du {ts:dd/MM HH:mm}, avant le run en cours) — les décisions demandées ici ont pu être actées depuis : voir onglet DÉCISIONS"
            : $"{actorName} — sortie du {ts:dd/MM HH:mm}";
        try
        {
            var content = File.ReadAllText(path);
            OutputViewer.Document = MarkdownToFlow(content);
        }
        catch (Exception ex)
        {
            OutputViewer.Document = new FlowDocument(
                new Paragraph(new Run($"Impossible de lire le fichier :\n{ex.Message}")));
        }
        BtnOpenOutputFile.IsEnabled = true;
        TabMain.SelectedIndex = 1; // switch to actor output tab
    }

    private void AppendLog(string line)
    {
        // Horodatage systématique (demande de Hajar, 2026-07-17) — les lignes déjà
        // horodatées par le CLI ne sont pas doublées.
        var stamped = line.StartsWith('[') ? line : $"[{DateTime.Now:dd/MM HH:mm:ss}] {line}";
        TxtLog.AppendText(stamped + "\n");
        LogScroll.ScrollToEnd();
    }

    // ─── Onglet DÉCISIONS : registre des décisions actées par Hajar ───────────
    // Le registre est reconstruit au démarrage de chaque run (scan des commentaires
    // Jira du sprint) et injecté dans chaque prompt — cet onglet le rend visible
    // pour vérifier qu'une réponse donnée a bien été enregistrée.
    private DateTime _decisionsLastWrite = DateTime.MinValue;

    private void RefreshDecisions()
    {
        if (TabMain.SelectedIndex != 4) return;
        var path = _artifactsDir is null ? null : Path.Combine(_artifactsDir, "decisions-registry.md");
        if (path is null || !File.Exists(path))
        {
            if (_decisionsLastWrite != DateTime.MinValue || DecisionsViewer.Document is null)
            {
                _decisionsLastWrite = DateTime.MinValue;
                TxtDecisionsHeader.Text = "Aucun registre pour l'instant — il est reconstruit au démarrage de chaque run (scan des commentaires Jira du sprint).";
                DecisionsViewer.Document = new FlowDocument(new Paragraph(new Run(
                    "Les décisions que tu donnes aux checkpoints sont publiées sur l'US de pilotage, " +
                    "puis reprises ici au prochain démarrage et injectées dans chaque prompt d'acteur " +
                    "avec la consigne « NE LES REDEMANDE JAMAIS ».")));
            }
            return;
        }
        try
        {
            var ts = File.GetLastWriteTime(path);
            if (ts == _decisionsLastWrite) return; // rien de nouveau
            _decisionsLastWrite = ts;
            TxtDecisionsHeader.Text =
                $"Registre des décisions actées — reconstruit au démarrage du run ({ts:dd/MM HH:mm}), injecté dans chaque prompt avec « NE LES REDEMANDE JAMAIS »";
            DecisionsViewer.Document = MarkdownToFlow(File.ReadAllText(path));
        }
        catch (IOException ex)
        {
            DecisionsViewer.Document = new FlowDocument(new Paragraph(new Run($"Lecture impossible : {ex.Message}")));
        }
    }

    private void TabMain_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, TabMain)) return;
        RefreshDecisions();
    }

    // ─── Actor selection ───────────────────────────────────────────────────────
    // Un acteur sans sortie dans le run courant (ex. complété lors d'un run précédent,
    // repris) affiche sa dernière sortie ARCHIVÉE, avec le bandeau RUN PRÉCÉDENT.
    private string? FindArchivedFile(string fileName)
    {
        if (_artifactsDir is null) return null;
        var archiveRoot = Path.Combine(_artifactsDir, "archive");
        if (!Directory.Exists(archiveRoot)) return null;
        return Directory.GetDirectories(archiveRoot)
            .OrderByDescending(d => d)
            .Select(d => Path.Combine(d, fileName))
            .FirstOrDefault(File.Exists);
    }

    private void ActorList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ActorList.SelectedItem is not ActorItem item || item.IsHeader) return;
        if (_artifactsDir is null) return;
        var path = Path.Combine(_artifactsDir, $"output-{item.DisplayName}.txt");
        if (!File.Exists(path))
            path = FindArchivedFile($"output-{item.DisplayName}.txt") ?? path;
        if (File.Exists(path)) ShowOutputFile(path, item.DisplayName);
        else
        {
            OutputViewer.Document = new FlowDocument(
                new Paragraph(new Run("Sortie non disponible — acteur jamais exécuté sur ce sprint.")));
            TxtSelectedActor.Text = item.DisplayName;
            BtnOpenOutputFile.IsEnabled = false;
        }

        // Onglet PROMPT : ce qui a réellement été envoyé à l'acteur
        var promptPath = Path.Combine(_artifactsDir, $"prompt-{item.DisplayName}.txt");
        if (!File.Exists(promptPath))
            promptPath = FindArchivedFile($"prompt-{item.DisplayName}.txt") ?? promptPath;
        if (File.Exists(promptPath))
        {
            TxtPromptActor.Text = $"{item.DisplayName} — prompt envoyé (dernier tour)";
            try { TxtPrompt.Text = File.ReadAllText(promptPath); }
            catch (IOException ex) { TxtPrompt.Text = $"Lecture impossible : {ex.Message}"; }
        }
        else
        {
            TxtPromptActor.Text = $"{item.DisplayName} — prompt non encore généré";
            TxtPrompt.Text = "";
        }
    }

    // ─── Interactive checkpoint + intervention ─────────────────────────────────
    private void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        if (!_checkpointActive) return;
        ShowRunModePanel();
        TxtStatus.Text = "GO — la discussion continue...";
        AppendLog(">>> GO");
        SendStdin("\n");
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_checkpointActive) return;
        HideCheckpoint();
        AppendLog(">>> ARRET");
        SendStdin("n\n");
    }

    private void BtnSendIntervention_Click(object sender, RoutedEventArgs e) => SendInterventionText();

    private void TxtIntervention_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendInterventionText();
    }

    // ─── Pièces jointes (SERZENIA-144 Lot 3) ───────────────────────────────────
    // Sélection de fichiers pour la prochaine intervention — le CLI copie les
    // fichiers dans le dossier du run de l'acteur ciblé et les référence par leur
    // chemin dans son prompt (image, document, vidéo… — jamais d'OCR).
    private void BtnAttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Joindre des fichiers à l'intervention",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (var path in dialog.FileNames)
            if (!_pendingAttachments.Contains(path, StringComparer.OrdinalIgnoreCase))
                _pendingAttachments.Add(path);

        UpdateAttachmentsSummary();
    }

    private void BtnClearAttachments_Click(object sender, RoutedEventArgs e)
    {
        _pendingAttachments.Clear();
        UpdateAttachmentsSummary();
    }

    private void UpdateAttachmentsSummary()
    {
        if (_pendingAttachments.Count == 0)
        {
            PnlAttachmentsSummary.Visibility = Visibility.Collapsed;
            TxtAttachmentsSummary.Text = "";
            return;
        }
        var names = string.Join(", ", _pendingAttachments.Select(Path.GetFileName));
        TxtAttachmentsSummary.Text = $"📎 {_pendingAttachments.Count} pièce(s) jointe(s) en attente : {names}";
        PnlAttachmentsSummary.Visibility = Visibility.Visible;
    }

    // Encode les chemins sources en fin de ligne — miroir de
    // SprintLauncher.Dialogue.DirectiveAttachments.Encode (CLI) côté UI, qui n'a
    // pas de référence au projet core (elle lance le CLI en sous-processus).
    private static string EncodeAttachments(string text, IReadOnlyList<string> filePaths) =>
        filePaths.Count == 0 ? text : $"{text} {AttachStartMarker}{string.Join('|', filePaths)}{AttachEndMarker}";

    private void SendInterventionText()
    {
        var text = TxtIntervention.Text.Trim();
        TxtIntervention.Clear();
        var attachments = _pendingAttachments.ToList();
        _pendingAttachments.Clear();
        UpdateAttachmentsSummary();
        var attachSuffix = attachments.Count > 0 ? $" [+{attachments.Count} pièce(s) jointe(s)]" : "";

        // À un checkpoint : réponse directe au CLI (stdin)
        if (_checkpointActive)
        {
            ShowRunModePanel();
            if (string.IsNullOrEmpty(text) && attachments.Count == 0)
            {
                AppendLog(">>> GO");
                SendStdin("\n");
                return;
            }
            TxtStatus.Text = "Intervention envoyée — prise en compte immédiatement.";
            AppendLog($">>> Intervention : {text}{attachSuffix}");
            SendStdin(EncodeAttachments(text, attachments) + "\n");
            return;
        }

        // Hors checkpoint : directive déposée (elles s'ACCUMULENT), remise à son
        // destinataire dès qu'il prend la parole — ou par le prochain run si aucun
        // run n'est actif (dépôt AVANT lancement).
        if (string.IsNullOrEmpty(text) && attachments.Count == 0) return;
        var targetDir = _process is { HasExited: false } && _artifactsDir is not null
            ? _artifactsDir
            : AppContext.BaseDirectory; // pré-directive : ramassée au démarrage du prochain run
        try
        {
            // Version corrigée d'un envoi : annuler d'abord les directives en file de
            // la version précédente (les lignes déjà lues en live ne se rappellent pas —
            // la correction les remplace par autorité de la consigne la plus récente).
            if (_cancelOnNextSend.Count > 0)
            {
                foreach (var line in _cancelOnNextSend)
                    File.AppendAllText(Path.Combine(targetDir, "pending-directive.txt"), "!cancel " + line + Environment.NewLine);
                AppendLog($">>> {_cancelOnNextSend.Count} directive(s) de l'envoi précédent annulée(s) — remplacée(s) par la version corrigée.");
                _cancelOnNextSend = [];
            }
            _lastQueuedLines = [];

            // ADRESSAGE MULTIPLE (« @ccode et @codex … ») : chaque destinataire reçoit
            // le message — l'acteur ACTIF en live (inbox lue en cours de tour), les
            // autres en directive (remise à leur prochaine prise de parole). Les alias
            // (@ccode, @codex…) sont résolus AVANT comparaison — sans ça, « @ccode » ≠
            // « ClaudeImplementation » et tout partait en file (retours 2026-07-17).
            var (targets, multiBody) = SplitDirectiveTargets(text);
            // Le marqueur de pièces jointes est ajouté à ce qui est ÉCRIT (fichier/stdin) —
            // jamais à multiBody, qui reste le texte lisible pour l'affichage, le log et
            // la correspondance !cancel (le CLI extrait le marqueur avant de comparer).
            var bodyToWrite = EncodeAttachments(multiBody, attachments);
            var running = _process is { HasExited: false };
            var liveSent = new List<string>();
            var queued = new List<string>();

            if (targets.Count == 0)
            {
                // Non adressé : live vers l'acteur actif s'il TOURNE réellement, sinon file.
                if (running && _activeActor is not null && _runningActors.Contains(_activeActor) && _artifactsDir is not null)
                {
                    File.AppendAllText(Path.Combine(_artifactsDir, $"live-input-{_activeActor}.txt"), bodyToWrite + Environment.NewLine);
                    liveSent.Add(_activeActor);
                }
                else
                {
                    File.AppendAllText(Path.Combine(targetDir, "pending-directive.txt"), bodyToWrite + Environment.NewLine);
                    _lastQueuedLines.Add(multiBody);
                    queued.Add("prochain acteur");
                }
            }
            else
            {
                foreach (var t in targets)
                {
                    var resolved = ResolveActorAlias(t) ?? t;
                    // Live vers N'IMPORTE QUEL acteur en cours (pipeline parallèle :
                    // deux moteurs peuvent tourner), jamais vers un acteur à l'arrêt.
                    if (running && _artifactsDir is not null && _runningActors.Contains(resolved))
                    {
                        File.AppendAllText(Path.Combine(_artifactsDir, $"live-input-{resolved}.txt"), bodyToWrite + Environment.NewLine);
                        liveSent.Add(resolved);
                    }
                    else
                    {
                        File.AppendAllText(Path.Combine(targetDir, "pending-directive.txt"), $"@{resolved} {bodyToWrite}" + Environment.NewLine);
                        _lastQueuedLines.Add($"@{resolved} {multiBody}");
                        queued.Add(resolved);
                    }
                }
            }

            var destLabel = string.Join(", ", liveSent.Select(a => $"{a} (live)").Concat(queued));
            AppendChatTurn($"Hajar → {destLabel}", multiBody + attachSuffix, isIntervention: true, round: 0, isFinal: false);
            AppendLog($">>> Intervention → {destLabel} : {multiBody}{attachSuffix}");
            _lastSentRaw = text;
            _lastAttachments = attachments;
            BtnEditLast.IsEnabled = true;
            TxtStatus.Text = liveSent.Count > 0
                ? $"Intervention envoyée — {string.Join(", ", liveSent)} la lira en cours de tour" +
                  (queued.Count > 0 ? $" ; {string.Join(", ", queued)} à sa prochaine prise de parole." : ".")
                : running
                    ? $"Directive déposée → {string.Join(", ", queued)} — remise à la prochaine prise de parole."
                    : "Directive pré-déposée — remise dès le lancement du prochain run.";
        }
        catch (IOException ex)
        {
            AppendLog($"(intervention non déposée : {ex.Message})");
        }
    }

    // Miroir UI des alias de DirectiveAddressing (CLI) : @ccode/@codex/@gpt… → nom de
    // rôle exact, pour que le routage live reconnaisse l'acteur actif. Null = pas de
    // cible (message pour l'acteur en cours).
    private static string? ResolveActorAlias(string? target) => target?.ToLowerInvariant() switch
    {
        null => null,
        "ccode" or "claude-code" or "claudecode" or "claudeimpl" => "ClaudeImplementation",
        "codex" or "gpt" or "gptimpl" => "GptImplementation",
        var t => t, // rôle exact ou groupe : comparé tel quel
    };

    // Miroir UI de DirectiveAddressing.ParseMulti (CLI) : « @ccode et @codex fais X »
    // → cibles [ccode, codex] + corps « fais X ». Les connecteurs entre cibles
    // (et, virgule, +, &) sont absorbés. Le CLI reste seul juge du routage réel.
    private static (List<string> Targets, string Body) SplitDirectiveTargets(string text)
    {
        var targets = new List<string>();
        var rest = text.Trim();
        while (rest.StartsWith('@'))
        {
            var sep = rest.IndexOfAny([' ', ':', ',', '\t', '\n']);
            if (sep <= 1) break;
            targets.Add(rest[1..sep].Trim());
            rest = rest[(sep + 1)..].TrimStart(' ', ':', ',', '+', '&');
            // « et @codex … » : absorber le connecteur si une cible suit.
            if (rest.StartsWith("et ", StringComparison.OrdinalIgnoreCase) && rest[3..].TrimStart().StartsWith('@'))
                rest = rest[3..].TrimStart();
        }
        return (targets, rest.Trim());
    }

    // ✎ Corriger : recharge le dernier envoi ; l'envoi suivant annule les directives
    // en file de la version précédente (« !cancel » côté CLI) et les remplace.
    private void BtnEditLast_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSentRaw is null) return;
        TxtIntervention.Text = _lastSentRaw;
        TxtIntervention.Focus();
        TxtIntervention.CaretIndex = TxtIntervention.Text.Length;
        _cancelOnNextSend = [.. _lastQueuedLines];
        _pendingAttachments.Clear();
        _pendingAttachments.AddRange(_lastAttachments);
        UpdateAttachmentsSummary();
        TxtStatus.Text = _cancelOnNextSend.Count > 0
            ? "Corrige puis renvoie — les directives non remises de l'envoi précédent seront annulées et remplacées."
            : "Corrige puis renvoie — le message précédent a déjà été lu en live : ta correction le remplacera par autorité.";
    }

    private void BtnConclude_Click(object sender, RoutedEventArgs e)
    {
        if (!_checkpointActive) return;
        ShowRunModePanel();
        TxtStatus.Text = "Clôture demandée — tour de synthèse finale en cours...";
        AppendLog(">>> Conclure maintenant");
        SendStdin("fin\n");
    }

    private void HideCheckpoint()
    {
        PnlInteractive.Visibility = Visibility.Collapsed;
        PnlInterventionInput.Visibility = Visibility.Collapsed;
    }

    // ─── Publication des sorties validées ──────────────────────────────────────
    private void BtnPublish_Click(object sender, RoutedEventArgs e)
    {
        if (_process is { HasExited: false })
        {
            MessageBox.Show("Un run est encore en cours — attends la fin avant de publier.",
                "Sprint Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = _actors
            .Where(a => a.CheckVisibility == "Visible" && a.IsChecked)
            .Select(a => a.IsHeader ? a.GroupName : a.DisplayName)
            .Distinct()
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("Aucune sortie cochée — coche au moins un acteur ou un groupe dans la liste.",
                "Sprint Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Publier {selected.Count} sortie(s) validée(s) sur Jira ({_lastRunKeys}) ?\n\n" +
            "Les commentaires seront réellement postés — exactement le contenu que tu as relu, sans réexécution des acteurs.",
            "Publication Jira", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        StartPublish(string.Join(",", selected));
    }

    // ─── Création des US validées (cadrage) ────────────────────────────────────
    private void BtnCreateUs_Click(object sender, RoutedEventArgs e)
    {
        if (_usProposals.Count == 0 || _artifactsDir is null) return;
        if (_process is { HasExited: false })
        {
            MessageBox.Show("Un run est encore en cours — attends la fin avant de créer les US.",
                "Sprint Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new UsProposalDialog(_usProposals) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected.Count == 0) return;

        // Écrit la sélection validée — c'est CE fichier que le CLI crée, rien d'autre.
        var selectedFile = Path.Combine(_artifactsDir, "us-proposals-selected.json");
        var json = JsonSerializer.Serialize(
            dialog.Selected.Select(p => new { summary = p.Summary, description = p.Description }),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(selectedFile, json);

        var confirm = MessageBox.Show(
            $"Créer {dialog.Selected.Count} US dans Jira, liées à {_usProposalsRefKey} ?\n\nCette action écrit réellement dans Jira.",
            "Création d'US Jira", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _publishMode = true;
        BtnCreateUs.IsEnabled = false;
        BtnRun.Content = "  Arrêter";
        TabMain.SelectedIndex = 0;

        var cliArgs = new List<string>();
        if (_usProposalsRefKey is not null) cliArgs.Add(_usProposalsRefKey);
        cliArgs.Add("--create-us");
        cliArgs.Add(selectedFile);
        cliArgs.Add("--write");

        StartProcess(BuildPsi(cliArgs));
        AppendLog($"Création de {dialog.Selected.Count} US démarrée...");
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
    private bool _isChecked;
    private string _checkVisibility = "Collapsed";

    public bool IsHeader { get; init; }
    public string DisplayName { get; init; } = "";
    public string GroupName { get; init; } = "";

    public string Status  { get => _status;  set { _status = value;  Notify(); } }
    public string Icon    { get => _icon;    set { _icon = value;    Notify(); } }
    public string Color   { get => _color;   set { _color = value;   Notify(); } }
    public string Elapsed { get => _elapsed; set { _elapsed = value; Notify(); } }
    public bool   IsChecked       { get => _isChecked;       set { _isChecked = value;       Notify(); } }
    public string CheckVisibility { get => _checkVisibility; set { _checkVisibility = value; Notify(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
