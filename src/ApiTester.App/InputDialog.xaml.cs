using System.Windows;
using System.Windows.Input;

namespace ApiTester.App;

public partial class InputDialog : Window
{
    private readonly bool _multiline;

    private InputDialog(string title, string prompt, string initial, bool multiline)
    {
        InitializeComponent();
        _multiline = multiline;
        Title = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        if (multiline)
        {
            Width = 560;
            Input.AcceptsReturn = true;
            Input.TextWrapping = TextWrapping.Wrap;
            Input.Height = 170;
            Input.VerticalContentAlignment = VerticalAlignment.Top;
            Input.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
            Input.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        }
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    /// <summary>Show a modal single-line prompt. Returns the entered text, or null if cancelled.</summary>
    public static string? Show(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(title, prompt, initial, multiline: false) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Input.Text.Trim() : null;
    }

    /// <summary>Show a modal multi-line prompt (Ctrl+Enter accepts). Returns text, or null if cancelled.</summary>
    public static string? ShowMultiline(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(title, prompt, initial, multiline: true) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Input.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; return; }
        if (e.Key == Key.Enter)
        {
            // Single-line: Enter accepts. Multi-line: only Ctrl+Enter accepts (plain Enter is a newline).
            if (!_multiline || Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; DialogResult = true; }
        }
    }
}
