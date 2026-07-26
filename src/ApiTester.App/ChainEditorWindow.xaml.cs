using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Builds one chain's ordered steps by picking saved requests, left to right. This window
/// edits the chain's steps; <see cref="ChainRunWindow"/> runs it — both against the same saved chain.
/// Running goes through the shared <c>ChainRunner</c> in <c>ApiTester.Core</c>, so there is one
/// execution path rather than a second copy.
/// <para>There is deliberately no OK/Cancel: the chain instance handed in is the one the sidebar holds,
/// so every edit is already applied, which is how the rest of this app's editors behave. A Cancel would
/// have to unwind reorderings — a new mechanism for no gain.</para></summary>
public partial class ChainEditorWindow : Window
{
    private readonly RequestChain _chain;

    /// <summary>Every runnable saved request, flattened out of the collections tree.</summary>
    private readonly List<CollectionNode> _available = new();

    private readonly ObservableCollection<ChainStepRow> _rows = new();

    /// <summary>Edit one chain's ordered steps. Takes the workspace so the picker can offer every
    /// saved request, and edits the chain in place — the sidebar holds the same instance.</summary>
    public ChainEditorWindow(RequestChain chain, AppState state)
    {
        InitializeComponent();
        _chain = chain;

        HeaderText.Text = string.IsNullOrWhiteSpace(chain.Name) ? "EDIT CHAIN" : chain.Name.ToUpperInvariant();

        Flatten(state.Collections);
        AvailableList.ItemsSource = _available;
        StepsList.ItemsSource = _rows;
        EnvironmentBox.Text = chain.EnvironmentName ?? "";
        RefreshSteps();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    /// <summary>Depth-first so the picker lists requests in the order the sidebar tree shows them.
    /// Folders aren't runnable, so only leaves carrying a request are offered.</summary>
    private void Flatten(IEnumerable<CollectionNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (!n.IsFolder && n.Request is not null) _available.Add(n);
            if (n.Children.Count > 0) Flatten(n.Children);
        }
    }

    /// <summary>Rebuild the step rows from the chain. A row's position is 1-based and therefore depends
    /// on order, and <see cref="ChainStep"/> raises no change notification, so a reorder or a removal can
    /// only reach the list by rebuilding — the list cannot observe a <see cref="List{T}"/> on its own.</summary>
    private void RefreshSteps()
    {
        var keep = (StepsList.SelectedItem as ChainStepRow)?.Step;

        _rows.Clear();
        for (int i = 0; i < _chain.Steps.Count; i++)
        {
            var step = _chain.Steps[i];
            _rows.Add(new ChainStepRow(step, i + 1, _available.FirstOrDefault(n => n.Id == step.RequestId)));
        }

        if (keep is not null)
            StepsList.SelectedItem = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, keep));

        NoRequestsHint.Visibility = _available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoStepsHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddStep_Click(object sender, RoutedEventArgs e) => AddSelectedRequest();

    private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddSelectedRequest();

    private void AddSelectedRequest()
    {
        if (AvailableList.SelectedItem is not CollectionNode node) return;
        _chain.Steps.Add(new ChainStep { RequestId = node.Id });
        RefreshSteps();
        StepsList.SelectedIndex = _rows.Count - 1;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        int from = StepsList.SelectedIndex;
        int to = from + delta;
        if (from < 0 || to < 0 || to >= _chain.Steps.Count) return;
        (_chain.Steps[from], _chain.Steps[to]) = (_chain.Steps[to], _chain.Steps[from]);
        RefreshSteps();
        StepsList.SelectedIndex = to;
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        int i = StepsList.SelectedIndex;
        if (i < 0 || i >= _chain.Steps.Count) return;
        _chain.Steps.RemoveAt(i);
        RefreshSteps();
        if (_rows.Count > 0) StepsList.SelectedIndex = Math.Min(i, _rows.Count - 1);
    }

    /// <summary>An empty box means no environment rather than an environment called "".</summary>
    private void EnvironmentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = EnvironmentBox.Text.Trim();
        _chain.EnvironmentName = string.IsNullOrEmpty(name) ? null : name;
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>One display row of the steps list. <see cref="ChainStep"/> stores only a request id, so
    /// the row resolves the saved request's badge and name, carries the 1-based position the list can't
    /// derive itself, and passes <see cref="StopOnFailure"/> straight through to the step so the row's
    /// two-way CheckBox binding edits the model without needing change notification.
    /// <para>Public because WPF's binding engine reflects over the item type.</para></summary>
    public sealed class ChainStepRow
    {
        public ChainStepRow(ChainStep step, int position, CollectionNode? node)
        {
            Step = step;
            Position = position + ".";
            Missing = node is null;
            Badge = node?.MethodBadge ?? "?";
            Label = node?.Name
                    ?? (string.IsNullOrEmpty(step.RequestId)
                            ? "(missing request)"
                            : $"(missing request: {step.RequestId})");
        }

        internal ChainStep Step { get; }

        public string Position { get; }
        public string Badge { get; }
        public string Label { get; }

        /// <summary>True when the step's request id no longer resolves — the request was deleted after
        /// the chain was built. Visible in the row rather than silently blank.</summary>
        public bool Missing { get; }

        public bool StopOnFailure
        {
            get => Step.StopOnFailure;
            set => Step.StopOnFailure = value;
        }
    }
}
