using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ApiTester.App;

public partial class EnvironmentsWindow : Window
{
    private readonly ObservableCollection<ApiEnvironment> _envs;

    public EnvironmentsWindow(ObservableCollection<ApiEnvironment> environments)
    {
        InitializeComponent();
        _envs = environments;
        EnvList.ItemsSource = _envs;
        if (_envs.Count > 0) EnvList.SelectedIndex = 0;
        UpdateVarsVisibility();
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyDarkTitleBar(this);
    }

    private ApiEnvironment? Selected => EnvList.SelectedItem as ApiEnvironment;

    private void EnvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        VarsItems.ItemsSource = Selected?.Variables;
        UpdateVarsVisibility();
    }

    private void UpdateVarsVisibility()
    {
        bool has = Selected is not null;
        VarsItems.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        AddVarButton.IsEnabled = has;
        NoEnvHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NewEnv_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "New environment", "Environment name", "New environment");
        if (string.IsNullOrWhiteSpace(name)) return;
        var env = new ApiEnvironment { Name = name };
        _envs.Add(env);
        EnvList.SelectedItem = env;
    }

    private void RenameEnv_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } env) return;
        var name = InputDialog.Show(this, "Rename environment", "Environment name", env.Name);
        if (!string.IsNullOrWhiteSpace(name)) env.Name = name;
    }

    private void DeleteEnv_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } env) _envs.Remove(env);
        UpdateVarsVisibility();
    }

    private void AddVar_Click(object sender, RoutedEventArgs e) => Selected?.Variables.Add(new Variable());

    private void RemoveVar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Variable v }) Selected?.Variables.Remove(v);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
