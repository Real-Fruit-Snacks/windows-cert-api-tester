using System.Windows;
using System.Windows.Input;

namespace ApiTester.App;

public enum DialogChoice { Cancel, Primary, Secondary }

/// <summary>A themed two-option confirmation dialog (plus Cancel).</summary>
public partial class ChoiceDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    private ChoiceDialog(string title, string message, string primaryLabel, string secondaryLabel)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryLabel;
        SecondaryButton.Content = secondaryLabel;
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyDarkTitleBar(this);
    }

    /// <summary>Show the dialog modally and return which option was chosen.</summary>
    public static DialogChoice Show(Window owner, string title, string message,
                                    string primaryLabel, string secondaryLabel)
    {
        var dlg = new ChoiceDialog(title, message, primaryLabel, secondaryLabel) { Owner = owner };
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) { _choice = DialogChoice.Primary; DialogResult = true; }
    private void Secondary_Click(object sender, RoutedEventArgs e) { _choice = DialogChoice.Secondary; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _choice = DialogChoice.Cancel; DialogResult = false; }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _choice = DialogChoice.Cancel; DialogResult = false; }
    }
}
