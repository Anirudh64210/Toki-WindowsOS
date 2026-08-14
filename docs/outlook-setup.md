# Connecting Toki to Outlook

*What Sid has to do once, before the Outlook button can work. Roughly ten minutes, free, no card.*

Toki talks to Outlook the same way it talks to Google: your browser signs in directly with Microsoft,
Microsoft sends the answer back to a one-off address on your own PC, and nothing passes through a Toki
server. For that to happen, Microsoft needs to know Toki exists. That is an **app registration**, and it
is the Outlook equivalent of the Google OAuth client in `docs/google-setup.md`.

**You can have Google and Outlook connected at the same time.** They are separate connections, Toki
merges them into one agenda, and disconnecting one leaves the other alone.

---

## 1. Register Toki with Microsoft

1. Go to <https://entra.microsoft.com> and sign in with any Microsoft account (work or personal).
2. In the left menu: **Applications** → **App registrations** → **New registration**.
3. Fill in:
   - **Name**: `Toki`
   - **Supported account types**: **Accounts in any organizational directory and personal Microsoft
     accounts**. This is the one that lets both a work address and an `@outlook.com` address sign in.
     Picking a narrower option is the most common reason a sign-in later says "this account cannot be
     used here".
   - **Redirect URI**: choose **Public client/native (mobile & desktop)** and enter
     `http://localhost`.

     > Toki opens a fresh port on `127.0.0.1` each time it signs you in, and Microsoft (unlike Google)
     > allows any port under `http://localhost` for a public client. That is why one entry covers every
     > run.
4. Press **Register**.

## 2. Turn on the calendar permission

1. Open your new app → **API permissions** → **Add a permission** → **Microsoft Graph** →
   **Delegated permissions**.
2. Tick **Calendars.ReadWrite**, then **Add permissions**.
   - `offline_access`, `openid`, `email` and `profile` are requested at sign-in and need no setup here.
   - **ReadWrite rather than Read** because Toki creates and edits events. Asking for read-only now
     would only force a second consent screen the first time you make a meeting.
3. No admin consent is needed for a personal calendar. On a locked-down company tenant your IT admin
   may have to approve it once - that is their policy, not a Toki requirement.

## 3. Confirm it is a public client

Open **Authentication** and check that **Allow public client flows** is **Yes**.

Toki uses PKCE and holds **no client secret** - for a desktop app a secret would be shipped inside the
download, so it would not be secret, and Microsoft treats this app type accordingly. If this setting is
off, sign-in fails with `invalid_client`.

## 4. Give the id to Toki

Copy the **Application (client) ID** from the app's Overview page. It looks like
`11111111-2222-3333-4444-555555555555`. It is not a secret; it identifies the app, not you.

Then, whichever suits you:

- **The file** (what most people want) - create
  `%APPDATA%\Toki\microsoft_client.json` containing:

  ```json
  { "client_id": "11111111-2222-3333-4444-555555555555" }
  ```

- **An environment variable** (handy while developing) - set `TOKI_MS_CLIENT_ID`.

- **Built in** - fill `EmbeddedClientId` in `src/Toki/Calendar/MicrosoftAuth.cs` before publishing, so
  the shipped app needs no setup at all.

Toki looks in that order and uses the first one it finds.

## 5. Connect

Open Toki → **Library** → **Settings** → **Calendars** → **Connect Outlook**. Your browser opens,
Microsoft asks you to allow Toki to read and write your calendar, and the tab tells you when it is
done. The row then shrinks to a small mark and the address you signed in as.

---

## If something goes wrong

| What you see | What it means |
|---|---|
| "Outlook sign-in isn't set up in this build yet" | Toki found no client id. Step 4. |
| `invalid_client` / "isn't set up correctly" | "Allow public client flows" is off, or the id is mistyped. Steps 3 and 4. |
| "This account cannot be used" at Microsoft | The app was registered for a narrower set of account types. Step 1, second bullet. |
| Signed in fine, then stopped working the next day | The refresh token did not save. Toki stores the rotated token on every refresh; if this recurs, check that `%APPDATA%\Toki\settings.json` is writable. |
| Meetings show at the wrong time | Report it. Toki asks Graph for UTC explicitly and attaches the zone by hand, because Microsoft sends timestamps with no offset - if a mailbox slips through with an odd zone id, that is a bug worth a log line. |

## What Toki stores

Only the long-lived refresh token, encrypted with Windows DPAPI so it can be read by your Windows
account on this PC and nowhere else, plus the address to show as "Connected as ...". The short-lived
access token is never written to disk. Disconnecting deletes both.

Microsoft has no revoke endpoint for a public client, so **Disconnect removes Toki's copy** and access
is withdrawn properly from
<https://account.live.com/consent/Manage> (personal) or your organisation's My Apps page.
