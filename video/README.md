# The Toki demo film

Remotion. `src/Film.tsx` is the cut; everything else supports it.

```
npm run dev      # the studio - scrub, edit, see changes live
npm run build    # renders out/toki.mp4
```

**Rendering needs a browser flag on this machine.** Remotion's own Chrome download fails here
(it extracts into `node_modules/.remotion` and this folder is inside OneDrive, which locks files
mid-extract). Point it at the installed Chrome instead:

```
npx remotion render src/index.ts Toki out/toki.mp4 --concurrency=2 \
  --browser-executable="C:\Program Files\Google\Chrome\Application\chrome.exe"
```

Moving this folder out of OneDrive would fix that, and would stop node_modules being synced.

## How the cut is written

`BEATS` in `src/Film.tsx` is the whole edit - one row per shot:

| field | meaning |
|---|---|
| `src` | seconds into `public/raw.mp4` |
| `secs` | seconds of OUTPUT (not of source) |
| `rate` | playback rate. Under 1 holds a short moment; over 1 ramps through a wait |
| `from` / `to` | where the camera looks, in SOURCE pixels - the shot glides between them |

Source timings were found by extracting frames at 1s and 0.25s and reading them, not by scrubbing.
Two of them matter: the recording bar exists only from **37.2s to 39.4s**, and the privacy card only
from **10.72s to 11.4s** - both are played under 1x to hold them.

## Why it matches the app

- `src/theme.ts` carries the app's colours AND its easing curves, ported from `Theme/Motion.cs`.
  Captions and transitions ride `ArriveStrong` / `Depart` / `Drawer`, so the film moves like Toki.
- `src/TokiMark.tsx` is the REAL mascot - the same Figma vector the app draws, with the same
  thirteen-point eye morph. The end card's smile is the app's own smile, not a picture of it.

Both are hand-ports and will drift when the theme or the vector changes. Same caveat as `design-web/`.
