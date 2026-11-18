# FM350 SMS desktop app — design

Date: 2026-11-18
Status: draft, awaiting approval (builder check done, user answered its two questions)

## Intent

The FM350 modem's SIM receives SMS. A background receiver (`src/Fm350Sms`, v0.1.0)
already reads them over MBIM (`IMbnSms`), shows a Windows toast per message and appends
them to `%LOCALAPPDATA%\FM350Sms\messages.txt`. Receiving and toasts are verified on
the user's machine (Partner, Israel; Hebrew UCS2 SMS).

The user wants this turned into a real program that **keeps every message** and shows
**sender names** taken from **their Google account contacts**. An SMS carries only a
number or an alphanumeric sender ID, so names have to come from a contact list the user
supplies.

## Goals

- A Windows desktop app with a window to browse all received SMS, grouped by sender
  like a phone's messages app, with search.
- Every message is stored durably and kept forever (until the user deletes data).
- Senders are shown by name when their number is in the user's Google contacts.
- Keeps receiving in the background when the window is closed (tray icon). Toasts
  show the contact name.
- Starts at logon, no administrator rights, like v0.1.0.
- Messages already saved by v0.1.0 (`messages.txt`) are carried over.

## Sending SMS (added by the user)

- A compose box at the bottom of every conversation, and a "New message" button that
  takes a number or searches contacts by name.
- Sent over MBIM with `IMbnSms.SmsSendPdu` (SMS-SUBMIT PDU built by the app): GSM 7-bit
  when every character fits, otherwise UCS2 (Hebrew). Long text is split into
  concatenated parts (153 GSM / 67 UCS2 characters per part); the compose box shows
  characters left and the number of parts, since each part is billed as one SMS.
- Sent messages are stored in `messages` with `direction = out` and a `status`
  (`sending`, `sent`, `failed` + error); failed ones show a "retry" action. Incoming
  messages are `direction = in`. Sent messages appear in the thread as right-hand
  bubbles; no toast for them.
- Pressing Send is the user's explicit action; nothing is ever sent automatically.
- Verification: whether this modem/firmware accepts `SmsSendPdu` is not yet known.
  It is tested by sending one real SMS to a number the user chooses, only after the
  user agrees to that specific test.

## Saving numbers as contacts (added by the user)

- A thread whose sender is not in any contact shows a "Save contact" action
  (name + the number prefilled); "New contact" in the menu adds one by hand.
- Saved contacts are `manual` contacts, so imports never overwrite them.
- "Export contacts" writes the manual contacts as a Google CSV file, so the user can
  import them into Google Contacts and keep both in sync by hand.

## Contacts file in the project (added by the user)

- The user's Google export is copied to `contacts/contacts.csv` in the project folder.
  `contacts/` is in `.gitignore`, so it is never committed or pushed (the repo's remote
  is a public GitHub repository). "Import contacts" opens that folder by default.

## Non-goals

- Live sync with Google (People API / OAuth / CardDAV). Explicitly rejected for now:
  needs a Google Cloud project, an "unverified app" warning, and a stored access token.
  Can be added later.
- Reading the SIM phonebook (not reachable over MBIM on this modem).
- Class 0 (flash) SMS, MMS.
- Sync between machines, cloud backup.

## Anti-goals

- Losing or duplicating a message (e.g. a crash between saving and marking handled,
  or re-importing `messages.txt` twice).
- Storing any Google credential or token on the PC.
- Two receivers running at once (old v0.1.0 watcher and the new app) and racing,
  or one deleting messages from the modem before the other saved them.

## Approaches considered (contacts)

1. **Import an exported contacts file — chosen.** The user exports from
   contacts.google.com (Export → vCard or Google CSV) and clicks "Import contacts".
   Works immediately, no Google setup, no credentials on the PC. Cost: not automatic;
   new contacts appear after the next import.
2. Google People API sync. Always current, but requires the user to create a Google
   Cloud OAuth client, shows an unverified-app warning, and keeps a refresh token.
3. CardDAV against Google. Same OAuth requirement as 2, with no benefit over it.

The user picked 1.

## Decided by the user

- Names come from their Google account contacts, via file import (approach 1).
- **A conversation is one contact, not one number.** Messages from all numbers of the
  same contact appear in one thread under the contact's name, like a phone. Numbers not
  in any contact are their own thread.
- **Messages can be deleted in the app** — a single message or a whole conversation,
  after a confirmation. Deletion is local only (never from the modem), and a deleted
  message must not reappear on the next modem poll.

## Builder's calls (proposed by Claude, the user can override)

- **Platform:** WPF on .NET 10 (`net10.0-windows10.0.26100.0`), x64, same toolchain as
  the existing projects. The tray icon uses WinForms `NotifyIcon`.
- **One process replaces v0.1.0.** `src/Fm350Sms` becomes the WPF app (same exe name
  `Fm350Sms.exe`, same `%LOCALAPPDATA%\FM350Sms` folder, same HKCU Run value
  `FM350 SMS`, same toast AppUserModelId `FM350.Sms`). Upgrading = running
  `Install-Sms.ps1` again. The receive engine (`ModemSmsClient`, `SmsPdu`, `Notifier`,
  the PDU-hash "handled" logic and multi-part joining from `Inbox`) is reused, not
  rewritten. A single-instance mutex covers app + receiver; starting the exe again
  brings the existing window to the front.
- **Storage:** SQLite (`Microsoft.Data.Sqlite`) at `%LOCALAPPDATA%\FM350Sms\sms.db`.
  - `messages(id, sender, sender_key, sent_at, received_at, body, source)` where
    `source` is `modem` or `import-txt`.
  - `message_parts(pdu_hash PRIMARY KEY, message_id NULL)` — replaces `state.json`; the
    message and its PDU hashes are written in one transaction, so a message is either
    fully saved or not at all. Deleting a message deletes its `messages` row but keeps
    its `message_parts` rows (with `message_id` set to NULL), so the modem copy is
    still recognised as handled and never comes back.
  - `contacts(id, display_name, origin)` with `origin` = `google-import` or `manual`,
    and `contact_numbers(contact_id, number_key)`. A Google re-import replaces all
    `google-import` rows; `manual` contacts are never touched by an import.
  - **Grouping is computed at read time, never stored on message rows**, so a contacts
    re-import can merge or split threads without migrating data. A message belongs to
    the contact whose number matches its `sender_key` (manual contacts checked before
    imported ones; exact key before the last-9-digits fallback); otherwise its thread is
    its own `sender_key`.
  - **Rename** on a thread creates (or updates) a `manual` contact holding all numbers
    currently in that thread, so it overrides the imported name and survives re-imports.
  - **Delete** is in the message's and the conversation's context menu, with a
    confirmation dialog; deleting a conversation deletes all messages in that thread.
    No undo.
  - On first run, existing `state.json` hashes are migrated into `message_parts`
    (as already-handled, without a message row) so old modem messages don't re-toast,
    and `messages.txt` is parsed and imported once (marker in a `meta` table).
    `messages.txt` and `state.json` are then left in place untouched as a backup and
    no longer written.
- **Number matching.** `sender_key` is a normalized form: strip spaces, dashes,
  parentheses; `00` prefix → `+`; a leading national `0` → `+972` (default country
  Israel, a setting later if needed). Match exact key first, then fall back to the last
  9 digits when both sides have at least 9. Alphanumeric senders ("Partner") are their
  own key and are shown as-is unless a manual name is set.
- **Contact import formats:** Google CSV is the primary format — the user's real export
  (`Downloads\contacts.csv`, 2026-11-18) is the current Google layout: UTF-8 with BOM,
  45 columns, `First Name`, `Middle Name`, `Last Name`, `Nickname`, `File As`,
  `Organization Name`, and `Phone N - Label` / `Phone N - Value` pairs (N = 1..k, read
  every such column present; a value may hold several numbers separated by ` ::: `).
  Also accept the older Google layout (`Name`, `Given Name`, `Family Name`, …) by header
  name, never by column position. Name = First + Middle + Last, else `File As`, else
  `Nickname`, else `Organization Name`. vCard 3.0/4.0 (`.vcf`, multiple `TEL` per card,
  quoted-printable and UTF-8 names) is a secondary format. The import shows a summary
  (contacts read, numbers read, numbers matched to existing senders).
  Phone formats seen in the real file, which normalization must handle: `0501234567`,
  `050-123-4567`, `+972501234567`, `+972 50-123-4567`, 9-digit numbers without the
  leading 0, and short codes like `*123` / `123` (exact match only, never last-9).
  The contacts file is personal data: it is read in place, never copied into the repo.
- **Window layout (Hebrew UI, right-to-left):**

  ```
  ┌──────────────────────────────────────────────────────────────┐
  │ [חיפוש...]                               [ייבוא אנשי קשר]   │
  ├──────────────────────┬───────────────────────────────────────┤
  │ אמא            04:39 │  אמא  (+972 50-555-0123)   [שנה שם]   │
  │ מממ                  │                                       │
  │──────────────────────│        ┌──────────────────┐           │
  │ Partner     אתמול    │        │ מממ              │           │
  │ החשבונית שלך...      │        │ 18/11 04:39      │           │
  │──────────────────────│        └──────────────────┘           │
  │ +972 52-...  15/11   │                                       │
  └──────────────────────┴───────────────────────────────────────┘
  ```

  Conversation list (right side in RTL) sorted by newest message, showing name or
  number, last-message preview and time. The selected conversation shows all its
  messages oldest→newest with date/time, text selectable and copyable. Search filters
  across all message bodies, names and numbers. "Rename" sets a manual name for that
  sender. Status bar: modem connected / not available, last check time.
- **Tray:** closing the window hides it; tray menu has Open, Import contacts, Exit.
  Clicking a toast opens the app on that conversation.
- **Modem deletion:** keep v0.1.0 behaviour — off by default, `--delete-after-save`
  (and a checkbox in a small settings menu) deletes only messages whose transaction
  committed.
- **Uninstall:** `Uninstall-Sms.ps1` keeps `sms.db` (and the old `messages.txt`)
  unless `-RemoveMessages` is given.
- **Tests:** keep the PDU self-test; add unit-testable pure functions for number
  normalization, vCard/CSV parsing and `messages.txt` parsing, run by
  `Fm350Sms.exe --self-test` so CI keeps working without a test project.

## Constraints

- No admin rights, per-user install, works without the modem present (app opens,
  shows stored history, status says modem unavailable, receiver retries).
- Unpackaged app; toasts rely on the HKCU AppUserModelId registration already working.
- Must not interfere with `FM350UnlockService` or the modem's data connection: reads
  only, deletes only when the user opted in.
- Data stays local; no network access at all.

## Left to the builder

- Exact XAML styling, icons, fonts (should look like a clean modern Windows app and
  render Hebrew and mixed Hebrew/English correctly).
- Threading model between the MTA receive loop and the WPF UI thread.
- Whether to use MVVM helpers or plain code-behind.
- Exact `messages.txt` parsing rules (format is `[yyyy-MM-dd HH:mm:ss] sender`, newline,
  body, blank line, as written by v0.1.0).
