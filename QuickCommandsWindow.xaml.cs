using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SerialTool;

public partial class QuickCommandsWindow : Window
{
    private readonly ObservableCollection<QuickCommand> _items;

    public QuickCommandsWindow(IEnumerable<QuickCommand> commands)
    {
        InitializeComponent();
        _items = new ObservableCollection<QuickCommand>(commands.Select(c => c.Clone()));
        CmdGrid.ItemsSource = _items;
    }

    public List<QuickCommand> Result { get; private set; } = new();

    private void CommitEdits()
    {
        CmdGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CmdGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        var c = new QuickCommand { Name = $"指令{_items.Count + 1}" };
        _items.Add(c);
        CmdGrid.SelectedItem = c;
        CmdGrid.ScrollIntoView(c);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        if (CmdGrid.SelectedItem is QuickCommand c) _items.Remove(c);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        CommitEdits();
        if (CmdGrid.SelectedItem is not QuickCommand c) return;
        int i = _items.IndexOf(c);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        CmdGrid.SelectedItem = c;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        Result = _items
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrEmpty(c.Content))
            .Select(c => c.Clone())
            .ToList();
        DialogResult = true;
    }
}
