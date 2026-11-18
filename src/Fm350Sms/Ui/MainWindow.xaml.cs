using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fm350Sms;

/// <summary>
/// Thread list, message pane with the compose box, search box, menus and status bar. Closing hides the window (the app
/// keeps running in the tray). Contact actions (import, export, save, rename) are in MainWindow.Contacts.cs.
/// </summary>
internal partial class MainWindow : Window
{
    private const string ConfirmDelete = "למחוק? אי אפשר לבטל.";
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush UnavailableBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

    private readonly SmsDatabase _db;
    private readonly SmsEngine _engine;
    private readonly Settings _settings;
    private readonly DispatcherTimer _statusTimer;
    private IReadOnlyList<MessageRow> _messages = [];
    private IReadOnlyList<ContactRow> _contacts = [];
    private string? _selectedId;

    /// <summary>A number picked in הודעה חדשה that has no thread yet: the pane shows an empty conversation with it.</summary>
    private string? _draftKey;

    /// <summary>The number the compose box sends to; reset to the thread's latest number when another thread is chosen.</summary>
    private string? _recipientKey;

    /// <summary>Compose text per conversation, so a half-typed message never moves to another one.</summary>
    private readonly Drafts _drafts = new();

    private ThreadInfo? _shown;
    private bool _allowClose;
    private bool _reloading;

    public MainWindow(SmsDatabase db, SmsEngine engine, Settings settings)
    {
        _db = db;
        _engine = engine;
        _settings = settings;
        InitializeComponent();
        DeleteAfterSaveItem.IsChecked = engine.DeleteAfterSave;
        CounterText.Text = Compose.Counter(string.Empty);
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => UpdateLastCheck(), Dispatcher);
        _statusTimer.Start();
        Reload();
    }

    public void SetIcon(System.Drawing.Icon icon) =>
        Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    /// <summary>Shows the window in front of other windows.</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; // Activate alone may only flash the taskbar button
        Topmost = false;
        Focus();
    }

    /// <summary>Brings the window to the front on <paramref name="threadId"/>. False when no such thread exists now.</summary>
    public bool ShowThread(string threadId)
    {
        BringToFront();
        SelectThread(threadId);
        return ThreadList.SelectedItem is ThreadItem { Id: var id } && id == threadId;
    }

    /// <summary>Re-reads messages and contacts, keeping the search, the selected thread and the compose text.</summary>
    public void Reload()
    {
        _messages = _db.AllMessages();
        _contacts = _db.Contacts();
        IReadOnlyList<ThreadInfo> all = Threads.Build(_messages, _contacts);

        // A draft becomes a real thread once a message to or from its number exists.
        if (_draftKey is not null && all.Any(t => t.Id == Threads.IdFor(_draftKey, _contacts)))
        {
            _selectedId = Threads.IdFor(_draftKey, _contacts);
            _drafts.Rename(Drafts.NewMessageKey(_draftKey), _selectedId); // the text being typed stays
            _draftKey = null;
        }

        IReadOnlyList<ThreadInfo> threads = Threads.Search(SearchBox.Text, _messages, _contacts);
        var now = DateTime.Now;
        List<ThreadItem> items = threads.Select(t => ThreadItem.From(t, now)).ToList();

        _reloading = true;
        try
        {
            ThreadList.ItemsSource = items;
            ThreadList.SelectedItem = _draftKey is null ? items.FirstOrDefault(i => i.Id == _selectedId) : null;
        }
        finally
        {
            _reloading = false;
        }

        NoThreadsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountsText.Text = $"{all.Count} שיחות · {_messages.Count} הודעות";
        ShowMessages(_draftKey is { } draft ? DraftThread(draft) : (ThreadList.SelectedItem as ThreadItem)?.Info);
    }

    public void SetModemAvailable(bool available)
    {
        ModemDot.Fill = available ? ConnectedBrush : UnavailableBrush;
        ModemText.Text = available ? "מודם מחובר" : "מודם לא זמין";
        UpdateLastCheck();
    }

    /// <summary>Closes for real (tray Exit).</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        base.OnClosed(e);
    }

    private IEnumerable<ThreadItem> CurrentThreads() => ThreadList.ItemsSource as IEnumerable<ThreadItem> ?? [];

    /// <summary>Selects a thread, clearing the search when it would hide it.</summary>
    private void SelectThread(string threadId)
    {
        _draftKey = null;
        if (_selectedId != threadId) _recipientKey = null;
        _selectedId = threadId;
        if (!CurrentThreads().Any(t => t.Id == threadId) && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty; // reloads
        }

        Reload();
    }

    /// <summary>The conversation shown for a number that has no messages yet.</summary>
    private ThreadInfo DraftThread(string key)
    {
        string id = Threads.IdFor(key, _contacts);
        ContactRow? contact = id.StartsWith("c:") ? _contacts.FirstOrDefault(c => "c:" + c.Id == id) : null;
        return new ThreadInfo(id, contact?.DisplayName ?? PhoneNumbers.Display(key), [key], contact?.Id, contact is not null, null);
    }

    private void UpdateLastCheck() =>
        LastCheckText.Text = _engine.LastCheck?.ToString("HH:mm:ss") ?? "—";

    private void ShowMessages(ThreadInfo? thread)
    {
        _shown = thread;
        SwitchDraft(thread is null ? null : _draftKey is { } draft ? Drafts.NewMessageKey(draft) : thread.Id);
        if (thread is null)
        {
            HeaderTitle.Text = string.Empty;
            HeaderNumbers.Text = string.Empty;
            HeaderAction.Visibility = Visibility.Collapsed;
            ComposePanel.Visibility = Visibility.Collapsed;
            MessageList.ItemsSource = null;
            NoSelectionText.Visibility = Visibility.Visible;
            return;
        }

        HeaderTitle.Text = thread.Title;
        HeaderTitle.FlowDirection = Flow(thread.Title);
        IReadOnlyList<string> contactNumbers = thread.ContactId is { } cid
            ? _contacts.FirstOrDefault(c => c.Id == cid)?.NumberKeys ?? thread.NumberKeys
            : [];
        HeaderNumbers.Text = string.Join(", ", contactNumbers.Select(PhoneNumbers.Display));
        HeaderAction.Content = thread.IsSavedContact ? "שנה שם" : "שמור איש קשר";
        HeaderAction.Visibility = Visibility.Visible;

        bool belongs = _recipientKey is not null && (thread.NumberKeys.Contains(_recipientKey) || contactNumbers.Contains(_recipientKey));
        if (!belongs || !Compose.CanSendTo(_recipientKey!))
        {
            _recipientKey = thread.NumberKeys.FirstOrDefault(Compose.CanSendTo); // latest number first
        }

        ComposePanel.Visibility = _recipientKey is null ? Visibility.Collapsed : Visibility.Visible;
        RecipientNumber.Text = _recipientKey is null ? string.Empty : PhoneNumbers.Display(_recipientKey);
        UpdateSendButton();

        var now = DateTime.Now;
        MessageList.ItemsSource = Threads.MessagesOf(thread.Id, _messages, _contacts).Select(m => MessageItem.From(m, now)).ToList();
        NoSelectionText.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(MessageScroller.ScrollToEnd, DispatcherPriority.Loaded);
    }

    /// <summary>Saves the compose text under the conversation it was typed in and shows the text saved for <paramref name="key"/>.</summary>
    private void SwitchDraft(string? key)
    {
        if (key == _drafts.Current) return;
        string text = _drafts.Switch(key, ComposeBox.Text);
        if (ComposeBox.Text != text) ComposeBox.Text = text;
        ComposeBox.CaretIndex = text.Length;
    }

    private void OnThreadSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_reloading) return;
        var thread = ThreadList.SelectedItem as ThreadItem;
        if (thread is null && _draftKey is not null) return; // the draft is shown without a list selection
        _draftKey = null;
        _recipientKey = null;
        _selectedId = thread?.Id;
        ShowMessages(thread?.Info);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsInitialized) Reload();
    }

    // ---- Compose and send ----

    private void OnNewMessage(object sender, RoutedEventArgs e)
    {
        var dialog = new NewMessageDialog(this, _contacts);
        if (dialog.ShowDialog() != true || dialog.NumberKey is not { } key) return;

        string id = Threads.IdFor(key, _contacts);
        if (Threads.Build(_messages, _contacts).Any(t => t.Id == id))
        {
            SelectThread(id);
            _recipientKey = key; // a contact thread: send to the number that was picked
            Reload();
        }
        else
        {
            SearchBox.Text = string.Empty;
            _draftKey = key;
            _recipientKey = key;
            Reload();
        }

        ComposeBox.Focus();
    }

    private void OnComposeChanged(object sender, TextChangedEventArgs e)
    {
        CounterText.Text = Compose.Counter(ComposeBox.Text);
        UpdateSendButton();
    }

    private void UpdateSendButton() =>
        SendButton.IsEnabled = _recipientKey is not null && Compose.CanSend(ComposeBox.Text);

    /// <summary>Enter sends (when Send is enabled; otherwise it does nothing); Shift+Enter is a new line. Ctrl+Enter also sends.
    /// Enter that confirms an IME composition (Key.ImeProcessed) is left alone.</summary>
    private void OnComposeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed || e.Key != Key.Enter) return;
        switch (Compose.KeyActionFor(e.Key, Keyboard.Modifiers, SendButton.IsEnabled))
        {
            case Compose.KeyAction.Send:
                e.Handled = true;
                OnSend(sender, e);
                break;
            case Compose.KeyAction.Ignore:
                e.Handled = true;
                break;
        }
    }

    /// <summary>The user's explicit send: stores the message as Sending and hands it to the engine.</summary>
    private void OnSend(object sender, RoutedEventArgs e)
    {
        string text = ComposeBox.Text;
        if (_recipientKey is not { } to || !Compose.CanSend(text)) return;

        long id = _db.AddOutgoing(to, text);
        Queue(id);
        if (_drafts.Current is { } key) _drafts.Clear(key); // only this conversation's draft
        ComposeBox.Clear();
        Reload();
    }

    /// <summary>נסה שוב, or שלח שוב after a send that may have gone out (timeout, some parts sent): that one asks first.</summary>
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is not MessageItem item) return;
        if (item.MaybeSent &&
            MessageBox.Show(this, Compose.ResendConfirm, Compose.RetryLabelAgain, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != MessageBoxResult.Yes)
        {
            return;
        }

        _db.SetStatus(item.Id, SendStatus.Sending, null);
        Queue(item.Id);
        Reload();
    }

    private void Queue(long id)
    {
        try
        {
            _engine.QueueSend(id);
        }
        catch (ObjectDisposedException)
        {
            _db.SetStatus(id, SendStatus.Failed, "האפליקציה נסגרת");
        }
    }

    // ---- Copy and delete ----

    private void OnCopyMessage(object sender, RoutedEventArgs e)
    {
        if (MenuItemData(sender) is not MessageItem item) return;
        string text = ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is TextBox { SelectionLength: > 0 } box
            ? box.SelectedText
            : item.Body;
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException ex)
        {
            Log.Write("Copy failed: " + ex.Message); // the clipboard is busy in another app
        }
    }

    private void OnDeleteMessage(object sender, RoutedEventArgs e)
    {
        if (MenuItemData(sender) is not MessageItem item || !Confirm("מחק הודעה")) return;
        _db.DeleteMessages([item.Id]);
        Reload();
    }

    private void OnDeleteThread(object sender, RoutedEventArgs e)
    {
        if (MenuItemData(sender) is not ThreadItem thread || !Confirm("מחק שיחה")) return;
        _db.DeleteMessages(Threads.MessagesOf(thread.Id, _messages, _contacts).Select(m => m.Id).ToList());
        if (_selectedId == thread.Id) _selectedId = null;
        Reload();
    }

    private static object? MenuItemData(object sender) => (sender as FrameworkElement)?.DataContext;

    private bool Confirm(string title) =>
        MessageBox.Show(this, ConfirmDelete, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == MessageBoxResult.Yes;

    // ---- Settings ----

    private void OnDeleteAfterSaveClick(object sender, RoutedEventArgs e)
    {
        bool on = DeleteAfterSaveItem.IsChecked;
        _engine.DeleteAfterSave = on; // applies from the next poll, no restart
        _settings.DeleteAfterSave = on;
        try
        {
            _settings.Save(Paths.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not save settings: " + ex.Message);
            Info("הגדרות", "לא ניתן לשמור את ההגדרות: " + ex.Message, MessageBoxImage.Error);
        }
    }

    private void Info(string title, string text, MessageBoxImage image = MessageBoxImage.Information) =>
        MessageBox.Show(this, text, title, MessageBoxButton.OK, image, MessageBoxResult.OK,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

    /// <summary>A row of the thread list.</summary>
    private sealed record ThreadItem(
        string Id, string Title, FlowDirection TitleFlow, string Preview, FlowDirection PreviewFlow, string Time, ThreadInfo Info)
    {
        public static ThreadItem From(ThreadInfo t, DateTime now)
        {
            string preview = t.Last is null ? string.Empty : DisplayFormat.Preview(t.Last.Body);
            if (t.Last?.Direction == Direction.Out) preview = "אני: " + preview;
            return new ThreadItem(
                t.Id, t.Title, Flow(t.Title), preview, Flow(preview),
                t.Last is null ? string.Empty : DisplayFormat.ThreadTime(t.Last.SentAt ?? t.Last.ReceivedAt, now), t);
        }
    }

    /// <summary>A bubble of the message pane. Sent messages sit on the right, received ones on the left.</summary>
    private sealed record MessageItem(
        long Id, string Body, FlowDirection BodyFlow, string Time, string Status, Brush StatusBrush, string? Error,
        Visibility RetryVisibility, string RetryLabel, bool MaybeSent, HorizontalAlignment Alignment, Brush Background)
    {
        public static MessageItem From(MessageRow m, DateTime now)
        {
            bool outgoing = m.Direction == Direction.Out;
            string status = Compose.StatusLabel(m.Status, m.Error);
            bool failed = m.Status == SendStatus.Failed;
            bool retry = Compose.CanRetry(m.Status, m.Error);
            Brush statusBrush = failed ? UnavailableBrush : (Brush)Application.Current.Resources["SubtleBrush"];

            // The window is right-to-left, so Left is the visual right edge.
            return new MessageItem(
                m.Id, m.Body, Flow(m.Body), DisplayFormat.MessageTime(m.SentAt ?? m.ReceivedAt, now), status, statusBrush,
                outgoing ? m.Error : null, retry ? Visibility.Visible : Visibility.Collapsed,
                Compose.RetryLabel(m.Status, m.Error), Compose.MaybeSent(m.Status, m.Error),
                outgoing ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                (Brush)Application.Current.Resources[outgoing ? "OutgoingBrush" : "IncomingBrush"]);
        }
    }

    private static FlowDirection Flow(string text) =>
        DisplayFormat.IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
}
