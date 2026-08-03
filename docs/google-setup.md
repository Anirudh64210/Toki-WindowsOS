# Connecting Toki to Google Calendar — one-time developer setup

**Who this is for:** you, Sid, wearing your "app maker" hat — **once**. Toki's users never do any of
this; they just click **Connect Google → sign in → Allow**, exactly like connecting Google to Notion,
Calendly, Zoom, or Granola. This page is only about creating the "OAuth client" that ships *inside*
Toki so Google recognises the app.

It takes about 5 minutes and is free.

---

## Step 1 — make a Google Cloud project

1. Go to **console.cloud.google.com** and sign in with your Google account.
2. Top bar → the project dropdown → **New Project**. Name it **Toki**. Create, then make sure it's
   the selected project.

## Step 2 — turn on the Calendar API

1. Left menu → **APIs & Services → Library**.
2. Search **Google Calendar API** → click it → **Enable**.

## Step 3 — set up the consent screen

1. **APIs & Services → OAuth consent screen** (may be labelled "Google Auth Platform").
2. Choose **External** → Create.
3. Fill only what's required: app name **Toki**, your email as the support + developer contact. You can
   skip the "Scopes" step (Toki asks for permission at sign-in time instead).
4. On **Test users**, add your own Google email. Save.

> While the app is in **Testing** mode, Google logs Toki out of your calendar **every 7 days** — that's
> normal for testing. When that gets annoying, come back to this screen and press **Publish app**. You'll
> then see a one-time "Google hasn't verified this app" warning at sign-in (→ **Advanced → continue**),
> but the connection becomes permanent. Before *selling* Toki we'll pass Google's free verification
> (needs a privacy-policy page + a short demo video) — that's tracked as an M8 ship task and it makes the
> warning disappear for everyone.

## Step 4 — create the OAuth client

1. **APIs & Services → Credentials → Create credentials → OAuth client ID**.
2. Application type: **Desktop app**. Name it **Toki Windows**. Create.
3. A box shows a **Client ID** and **Client secret**. Click **Download JSON**.

> These two strings are *not* passwords. For desktop apps Google explicitly treats the "secret" as not
> secret — every installed app (gcloud, rclone, Google's own tools) ships them. Toki's security comes from
> the sign-in itself, not from hiding these.

## Step 5 — give the credentials to Toki

Pick **one** of these:

**Easiest — drop the file in.** Rename the downloaded file to `google_client.json` and place it at:

```
%APPDATA%\Toki\google_client.json
```

(Paste `%APPDATA%\Toki` into the File Explorer address bar; create the `Toki` folder if it isn't there.)

**Or — set two environment variables** (handy while developing):

```
TOKI_GOOGLE_CLIENT_ID       = <the client id>
TOKI_GOOGLE_CLIENT_SECRET   = <the client secret>
```

*(At ship time these get baked into the app itself, so end users never touch any of this.)*

---

## Step 6 — test it

From a terminal in the Toki folder:

```
# 1. sign in (opens your browser — sign in + Allow)
Toki.exe --caltest connect

# 2. read your next 7 days
Toki.exe --caltest

# 3. create a throwaway event, then auto-delete it (nothing left behind)
Toki.exe --caltest insert

# sign out + revoke, if you want to reset
Toki.exe --caltest disconnect
```

`--caltest connect` should end with **"PASS — connected as you@gmail.com"**, and your
`%APPDATA%\Toki\settings.json` will contain a `GoogleRefreshTokenProtected` value that's scrambled
(DPAPI-encrypted) — never your token in plain text.

You can also do it from the app: open Toki → **Connect Google** on the Home card (or **Settings → Google
Calendar**).
