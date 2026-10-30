using System.Windows;

namespace AnovaDesk;

public enum CloseChoice
{
    Cancel,
    Minimize,
    Exit,
}

public partial class ClosePrompt : Window
{
    /// <summary>What the user picked. Stays Cancel if the dialog is dismissed.</summary>
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    public ClosePrompt() => InitializeComponent();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Minimize;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        DialogResult = true;
    }
}
