using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Fm350Sms;

/// <summary>Picks who a new message goes to: a typed number, or a contact's number found by name or number.</summary>
internal partial class NewMessageDialog : Window
{
    private readonly IReadOnlyList<ContactRow> _contacts;

    public NewMessageDialog(Window owner, IReadOnlyList<ContactRow> contacts)
    {
        _contacts = contacts;
        InitializeComponent();
        Owner = owner;
        Results.ItemsSource = Compose.Find(string.Empty, _contacts);
    }

    /// <summary>The chosen number key (valid after the dialog returned true).</summary>
    public string? NumberKey { get; private set; }

    /// <summary>A selected contact number wins; otherwise the typed text when it is a number.</summary>
    private string? Choice => (Results.SelectedItem as Recipient)?.NumberKey ?? Compose.NumberFrom(QueryBox.Text);

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        IReadOnlyList<Recipient> found = Compose.Find(QueryBox.Text, _contacts);
        Results.ItemsSource = found;
        Results.SelectedItem = Compose.NumberFrom(QueryBox.Text) is null && found.Count == 1 ? found[0] : null;
        OkButton.IsEnabled = Choice is not null;
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Max(Results.SelectedIndex, 0);
        (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) OkButton.IsEnabled = Choice is not null;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is not null) OnOk(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        NumberKey = Choice;
        if (NumberKey is not null) DialogResult = true;
    }
}
