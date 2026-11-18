using System.Windows;
using System.Windows.Controls;

namespace Fm350Sms;

/// <summary>Name and numbers of a manual contact: used by שמור איש קשר, שנה שם and איש קשר חדש.</summary>
internal partial class ContactDialog : Window
{
    public ContactDialog(Window owner, string title, string name, IEnumerable<string> numberKeys)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        NameBox.Text = name;
        NumbersBox.Text = string.Join(Environment.NewLine, numberKeys.Select(PhoneNumbers.Display));
        NameBox.SelectAll();
    }

    /// <summary>The name typed, trimmed (valid after the dialog returned true).</summary>
    public string ContactName => NameBox.Text.Trim();

    /// <summary>The numbers typed, as keys (valid after the dialog returned true).</summary>
    public IReadOnlyList<string> NumberKeys => ContactEdits.ParseNumbers(NumbersBox.Text);

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (ContactName.Length == 0 || NumberKeys.Count == 0)
        {
            ErrorText.Text = "יש להזין שם ולפחות מספר אחד.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
