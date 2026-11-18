namespace Fm350Sms;

internal static class DraftsTests
{
    internal static void Tests()
    {
        var d = new Drafts();
        string box = d.Switch("n:+972501111111", string.Empty); // A shown, empty box
        SelfTest.Check("drafts first thread starts empty", box == string.Empty);

        box = d.Switch("n:+972502222222", "half typed to A"); // user typed in A, then picked B
        SelfTest.Check("drafts switch A->B: B starts empty", box == string.Empty, box);

        box = d.Switch("n:+972501111111", "to B"); // back to A
        SelfTest.Check("drafts switch B->A restores A", box == "half typed to A", box);

        box = d.Switch("n:+972501111111", box); // a reload on the same thread keeps the live text
        SelfTest.Check("drafts same thread keeps text", box == "half typed to A", box);

        // A sends: only A's draft is cleared; B keeps its text.
        d.Clear("n:+972501111111");
        box = d.Switch("n:+972502222222", string.Empty); // the box was cleared by the send
        SelfTest.Check("drafts send clears only that thread", box == "to B", box);
        box = d.Switch("n:+972501111111", box);
        SelfTest.Check("drafts sent thread stays empty", box == string.Empty, box);

        // A new message to a number without a thread has its own key, distinct from any thread id.
        string newKey = Drafts.NewMessageKey("+972503333333");
        SelfTest.Check("drafts new-message key is not a thread id",
            newKey != "n:+972503333333" && !newKey.StartsWith("n:") && !newKey.StartsWith("c:"));
        box = d.Switch(newKey, box);
        SelfTest.Check("drafts new message starts empty", box == string.Empty, box);

        // The new message becomes a thread (or a thread moves under a contact): the text follows it.
        box = d.Switch("n:+972502222222", "new msg text");
        d.Rename(newKey, "n:+972503333333");
        box = d.Switch("n:+972503333333", box);
        SelfTest.Check("drafts rename carries the text", box == "new msg text", box);

        // The shown new-message draft becomes a thread while the user types: the live text stays in the box.
        string k4 = Drafts.NewMessageKey("+972504444444");
        box = d.Switch(k4, box);
        d.Rename(k4, "n:+972504444444");
        string live = d.Switch("n:+972504444444", "live");
        SelfTest.Check("drafts rename of the shown draft keeps the box", live == "live" && d.Current == "n:+972504444444");
        box = d.Switch("n:+972503333333", live);

        // Nothing shown (thread deleted): the text is kept under its key, the box is empty.
        box = d.Switch(null, box);
        SelfTest.Check("drafts no thread empties the box", box == string.Empty && d.Get("n:+972503333333") == "new msg text");
    }
}
