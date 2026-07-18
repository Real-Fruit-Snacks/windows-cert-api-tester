using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ApiTester.App;

public partial class CollectionDefaultsDialog : Window
{
    private readonly IReadOnlyList<(string Label, string? Thumbprint)> _certOptions;
    private bool _ok;
    private bool _cleared;

    private CollectionDefaultsDialog(string folderName, string? currentBaseUrl, string? currentThumbprint,
        IReadOnlyList<(string Label, string? Thumbprint)> certOptions, IReadOnlyList<string> savedBaseUrls)
    {
        InitializeComponent();
        _certOptions = certOptions;
        HeaderText.Text = $"Defaults for “{folderName}”";
        BaseUrlBox.Text = currentBaseUrl ?? "";

        var saved = new List<string> { "…" };
        saved.AddRange(savedBaseUrls);
        SavedCombo.ItemsSource = saved;
        SavedCombo.SelectedIndex = 0;

        CertCombo.ItemsSource = certOptions.Select(o => o.Label).ToList();
        int idx = certOptions.ToList().FindIndex(o => o.Thumbprint == currentThumbprint);
        CertCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyDarkTitleBar(this);
    }

    public static (bool Ok, string? BaseUrl, string? CertThumbprint) Show(
        Window owner, string folderName, string? currentBaseUrl, string? currentThumbprint,
        IReadOnlyList<(string Label, string? Thumbprint)> certOptions, IReadOnlyList<string> savedBaseUrls)
    {
        var dlg = new CollectionDefaultsDialog(folderName, currentBaseUrl, currentThumbprint, certOptions, savedBaseUrls)
        { Owner = owner };
        dlg.ShowDialog();
        if (!dlg._ok) return (false, null, null);
        if (dlg._cleared) return (true, null, null);
        string? baseUrl = string.IsNullOrWhiteSpace(dlg.BaseUrlBox.Text) ? null : dlg.BaseUrlBox.Text.Trim();
        int i = dlg.CertCombo.SelectedIndex;
        string? thumb = i >= 0 && i < certOptions.Count ? certOptions[i].Thumbprint : null;
        return (true, baseUrl, thumb);
    }

    private void SavedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedCombo.SelectedIndex > 0 && SavedCombo.SelectedItem is string s)
        {
            BaseUrlBox.Text = s;
            SavedCombo.SelectedIndex = 0;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { _ok = true; _cleared = true; Close(); }
    private void Save_Click(object sender, RoutedEventArgs e) { _ok = true; Close(); }
}
