# Toki - UI screens for design handoff

Every screen in Toki as it exists today (v1-iter3, 2026-08-05), exported straight from the app's own
render harness - not hand-taken screenshots. They're pixel-exact, consistently sized, and transparent
where the real UI is transparent.

**To import into Figma:** open the target file → select all 24 PNGs in `ui-screens/` → drag them onto
the canvas. Figma places them in filename order, which is the flow order below. The `NN-` prefixes are
there for exactly that reason - keep them.

## Regenerating these

They are **generated, not curated**. After any UI change, re-run:

```
Toki.exe --render  <dir>     # the notch states  (20 frames)
Toki.exe --libshot <dir>     # the Library window (4 pages + a dev calibration sheet)
```

then re-copy into `ui-screens/` with the names below. `buttons.png` from `--libshot` is a developer
calibration sheet (red centre-lines) and is deliberately **excluded** from this folder.

---

## ⚠️ Before sharing outside the team

These render with **real data from this machine** - a real email address, real meeting titles and
attendee names, and the real saved profile name. Specifically:

| Frame | Contains |
|---|---|
| `03-01-notch-home` | email address, real upcoming meeting titles |
| `04-01-calendar-agenda`, `04-03-calendar-row-hover` | real meeting titles + attendees |
| `04-04-prep-card` | a real meeting and its open items |
| `02-06-notes-ready`, `06-02-library-notes` | real meeting notes content |
| `06-04-library-settings` | the saved profile name |

If the design team is external, swap in dummy data first (rename the meetings in the app, sign out of
the calendar, or set a placeholder name) and re-run the harness. Nothing here leaves the laptop on its
own - but a Figma file does.

---

## The flow

### ① First run - `01-*`
Seen exactly once, on install. Notch-native; there is no separate wizard window.

| Frame | What it is |
|---|---|
| `01-01-onboarding-name` | "What should I call you?" - the answer drives the greeting everywhere |
| `01-02-onboarding-privacy` | The privacy promise, framed as the pitch: everything stays on this laptop |
| `01-03-onboarding-all-set` | Confirmation, then it lands on Home with the new name already in the greeting |

*Re-runnable any time from the tray: right-click Toki → "Replay first-run setup".*

### ② A meeting happens - `02-*`
The core loop. Toki notices a call, offers to record, transcribes and summarises on-device.

| Frame | What it is |
|---|---|
| `02-01-meeting-detected` | The prompt. Toki never records without this |
| `02-02-meeting-detected-slack` | Same prompt with a real app identity mapped to a friendly name |
| `02-03-meeting-detected-long-app-name` | The stress case - a very long app name must still fit |
| `02-04-recording-expanded` | Recording, expanded. Deliberately tiny (~93×268) |
| `02-05-summarizing` | After the call, while the on-device model writes |
| `02-06-notes-ready` | The finished note |
| `02-07-nothing-was-said` | The honest empty state - a silent meeting saves nothing |

### ③ The notch at rest - `03-*`

| Frame | What it is |
|---|---|
| `03-01-notch-home` | The hub: greeting, Calendar / Take Notes, "COMING UP" |
| `03-02-notch-library` | Recent meetings, in the notch |
| `03-03-notch-settings` | **UI/visibility only** - theme, glassy, ghost. Everything else lives in the Library window |
| `03-04-about-built-with` | Open-source attribution. A licence obligation, not decoration |

### ④ Calendar & prep - `04-*`

| Frame | What it is |
|---|---|
| `04-01-calendar-agenda` | One continuous agenda; accent bars, attendee avatars, amber countdown when imminent |
| `04-02-calendar-create` | Creating a meeting - structured title / day / time fields |
| `04-03-calendar-row-hover` | A row hover-expanded to reveal Edit / Open / Delete inline |
| `04-04-prep-card` | Auto-surfaced ~5 min before a meeting: last time's recap, still-open items, agenda |
| `04-05-prep-card-first-time` | The same card when there's no history - honest, not padded |

### ⑤ Ask by voice - `05-*`

| Frame | What it is |
|---|---|
| `05-01-ask-on-the-notch` | Hold `Ctrl+Alt+Space` → speak → release → the answer streams here. Push-to-talk only; the mic runs *only* while held |

### ⑥ The Library window - `06-*`
The full-size window, opened from the notch.

| Frame | What it is |
|---|---|
| `06-01-library-ask` | The Ask landing - mascot, one identity line, the prompt bar |
| `06-02-library-notes` | The note list + reader |
| `06-03-library-templates` | The eight note templates |
| `06-04-library-settings` | Name, appearance, AI summaries + bring-your-own API key, calendar, about |

---

## Context the frames can't show

- **The notch is a single morphing window** at the top-centre of the screen - not multiple overlays.
  Every `02`/`03`/`04`/`05` frame is the *same window* changing size and content.
- **Ghost mode** (default on) hides the notch from screen recordings and screen shares, so it never
  appears in a Zoom share. That's why these are rendered rather than screen-captured - a normal
  capture can't see it.
- **Everything is on-device.** Transcription and summaries run locally; there's no server, no account,
  no telemetry. The only optional cloud path is the user's own API key.
- **Restraint is the design.** Toki should appear only when it has something worth showing.
