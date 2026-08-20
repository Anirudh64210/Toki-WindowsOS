# 24. Toki connects to your calendar

**What we set out to do.** Give Toki a safe, private link to your Google Calendar - the foundation for
everything calendar-shaped that comes next: seeing your next meeting on the notch, prepping you before a
call, and letting you create a meeting from Toki. This step is just the *plumbing* - the pipe that lets
Toki read and add events. The visible features sit on top of it and come next.

## The one rule we never break

Even for something as connected as a calendar, Toki's promise holds: **nothing routes through a Toki
server, because there is no Toki server.** When you connect, your own web browser talks **directly to
Google**, and Google hands the "key" straight to the copy of Toki on your PC. We're never in the middle.
Your calendar stays between you and Google.

## How signing in works (the "valet key")

Think of it like a hotel valet key. When you click **Connect Google**:

1. Your browser opens Google's own sign-in page - you type your password *to Google*, never to us.
2. You approve a short, specific permission: **"Toki wants to see and add calendar events."** Nothing
   else - no email, no files, no contacts.
3. Google hands your PC a **"valet key"** (a refresh token). It opens your calendar and *only* your
   calendar. It can be revoked any time, from Toki or from your Google account.

That key is the sensitive part, so we lock it down hard:

- It's **encrypted on your machine**, tied to your Windows login. Another person on the PC - or the file
  copied to a different computer - **can't** read it.
- It is **never written to any log** or shown on screen.
- We checked the saved file by eye: there's **no readable token in it**, just a scrambled blob.

## What each person has to do (spoiler: almost nothing)

You, as the app's *maker*, do a **one-time** 5-minute setup with Google to register "Toki" as an app -
that's a developer chore, done once, not something customers ever see.

Everyone who *uses* Toki just clicks **Connect Google → sign in → Allow**. Three clicks. Exactly like
connecting Google to Notion, Calendly, Zoom, or Granola. (There's a temporary "Google hasn't verified
this app yet" warning that customers click past; it disappears once we pass Google's free review before
selling - that's on the ship list.)

## The smallest possible permission

We deliberately ask for the **narrowest** access that still lets Toki be useful: read your events, and add
an event. That's it. No Gmail. No Drive. No contacts. If Toki ever gets hold of the wrong thing, it
simply *can't* - the door was never opened.

## Proving it actually works (not just "the code looks right")

We don't trust code we haven't watched run. So we built a hidden test switch and ran the whole thing
against a real Google account, live:

- **Sign in** → "connected as you@gmail.com." ✔
- **Read** the next 7 days from the real calendar. ✔
- **Create** a throwaway event, then **delete** it - so nothing was left behind. ✔
- **Auto-renew** the key when it expires, with no re-login. ✔
- **The saved key was checked** - encrypted, no plain text. ✔

One real bug showed up and got fixed on the spot: the very first "read" came back rejected, because a
`+` in the date was being read as a blank space by the web address. Encoded it properly, and the calendar
came through. (This is exactly why we test on a real machine instead of assuming.)

## What's next (built to make these easy)

The pipe is in. Sitting on top of it, coming next:

- **Your next meeting on the notch** - quietly, at the edge of the screen.
- **Prep cards** - before a call: the last decisions, open to-dos, and who's coming.
- **Make a meeting from Toki** - the "add event" half is already built and tested.
