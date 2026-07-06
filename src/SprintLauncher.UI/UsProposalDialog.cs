using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SprintLauncher.UI;

/// <summary>
/// Fenêtre de validation des US proposées par la session de cadrage (SERZENIA-143 lot 3).
/// L'approbatrice coche les US à créer, lit chaque description, puis confirme.
/// Rien n'est créé dans Jira tant que cette validation n'a pas eu lieu.
/// </summary>
public sealed class UsProposalDialog : Window
{
    public sealed record ProposalView(string Summary, string Description);

    private readonly List<(CheckBox Box, ProposalView Proposal)> _rows = [];
    private readonly TextBox _preview;

    public List<ProposalView> Selected { get; } = [];

    public UsProposalDialog(IReadOnlyList<ProposalView> proposals)
    {
        Title = $"Cadrage — {proposals.Count} US proposée(s) à valider";
        Width = 980; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)new BrushConverter().ConvertFromString("#1e1e2e")!;
        Foreground = (Brush)new BrushConverter().ConvertFromString("#cdd6f4")!;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var header = new TextBlock
        {
            Text = "Coche les US à créer dans Jira. Clique un titre pour relire sa description (template SERZENIA-89). " +
                   "Pour modifier une US : rejette-la ici et ajuste-la dans Jira après création, ou édite us-proposals.json.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#a6adc8")!,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(header, 0); Grid.SetColumnSpan(header, 3);
        root.Children.Add(header);

        // Liste des US avec cases à cocher
        var listPanel = new StackPanel();
        foreach (var p in proposals)
        {
            var box = new CheckBox
            {
                IsChecked = true,
                Foreground = Foreground,
                Margin = new Thickness(0, 4, 0, 4),
                Content = new TextBlock { Text = p.Summary, TextWrapping = TextWrapping.Wrap, MaxWidth = 270 },
            };
            box.Checked += (_, _) => { };
            var row = (box, new ProposalView(p.Summary, p.Description));
            _rows.Add(row);
            box.GotFocus += (_, _) => ShowPreview(row.Item2);
            box.MouseEnter += (_, _) => { };
            listPanel.Children.Add(box);
        }
        var listScroll = new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)new BrushConverter().ConvertFromString("#181825")!,
            Padding = new Thickness(10),
        };
        Grid.SetRow(listScroll, 1); Grid.SetColumn(listScroll, 0);
        root.Children.Add(listScroll);

        // Aperçu description
        _preview = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)new BrushConverter().ConvertFromString("#11111b")!,
            Foreground = Foreground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            FontSize = 12.5,
            Text = proposals.Count > 0 ? proposals[0].Description : "",
        };
        Grid.SetRow(_preview, 1); Grid.SetColumn(_preview, 2);
        root.Children.Add(_preview);

        // Boutons
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var btnCancel = new Button
        {
            Content = "Annuler",
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(0, 0, 10, 0),
            Background = (Brush)new BrushConverter().ConvertFromString("#313244")!,
            Foreground = Foreground,
            BorderThickness = new Thickness(0),
        };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnOk = new Button
        {
            Content = "Créer les US cochées dans Jira",
            Padding = new Thickness(18, 8, 18, 8),
            FontWeight = FontWeights.SemiBold,
            Background = (Brush)new BrushConverter().ConvertFromString("#a6e3a1")!,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#1e1e2e")!,
            BorderThickness = new Thickness(0),
        };
        btnOk.Click += (_, _) =>
        {
            Selected.Clear();
            Selected.AddRange(_rows.Where(r => r.Box.IsChecked == true).Select(r => r.Proposal));
            DialogResult = Selected.Count > 0;
            Close();
        };
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnOk);
        Grid.SetRow(buttons, 2); Grid.SetColumnSpan(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
    }

    private void ShowPreview(ProposalView p) => _preview.Text = p.Description;
}
