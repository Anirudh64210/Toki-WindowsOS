import React from "react";
import { AbsoluteFill, Easing, Sequence, interpolate, useCurrentFrame } from "remotion";
import { Shot } from "./Shot";
import { TokiMark } from "./TokiMark";
import { c, ease, font, FPS } from "./theme";

const bez = (e: readonly number[]) => Easing.bezier(e[0], e[1], e[2], e[3]);
const f = (n: number) => Math.round(n * FPS);

/**
 * ══════════════════════ THE EDIT ══════════════════════
 *
 * This is not a montage. It is the recording with the waste taken out - which is what was actually
 * asked for, twice, after a first attempt that cropped every shot and flashed black between them:
 * "just edit the video, to remove any waste time/seconds, and clean it and make it smooth, concise".
 *
 * So: FULL FRAME throughout, straight cuts, no captions, no dips, no zooms. A short top and tail
 * because it is being sent to people, and nothing else added.
 *
 * The in and out points came from extracting the recording at 1s and 0.25s and reading the frames.
 * The take is 2:37 and about two minutes of it is the app thinking, the cursor travelling, or an
 * empty desktop. What is left is 36 seconds where something happens in every one of them.
 */
type Cut = { key: string; from: number; secs: number; rate?: number };

export const CUTS: Cut[] = [
  { key: "home",        from: 12.45, secs: 2.0 },   // the notch, open
  { key: "calendar",    from: 19.05, secs: 1.4 },   // the week
  { key: "composer",    from: 21.25, secs: 2.0 },   // making a meeting
  { key: "settings",    from: 26.45, secs: 2.4 },   // notch settings + the light-theme flash at 28.2
  { key: "takenotes",   from: 33.85, secs: 1.0 },   // the click
  { key: "recording",   from: 37.25, secs: 2.2 },   // the bar - only exists 37.2-39.4 in the take
  { key: "tidying",     from: 39.75, secs: 1.7 },   // ends before 42.0, where "nothing was said" appears
  { key: "transcript",  from: 52.45, secs: 2.6 },
  { key: "libopen",     from: 60.45, secs: 2.0 },   // the Library blooming out of the notch
  { key: "ask",         from: 116.5, secs: 5.0 },   // a real question and a real answer
  { key: "list",        from: 123.9, secs: 4.0 },   // the list, with real notes in it
  { key: "note",        from: 135.9, secs: 4.4 },   // one note open
  { key: "libsettings", from: 142.1, secs: 3.6 },
  // 0.7s in the take, so it plays at half speed to be readable at all.
  { key: "privacy",     from: 10.72, secs: 1.4, rate: 0.5 },
];

const TITLE = f(1.6);
const END = f(2.6);

export const filmLength = () => TITLE + CUTS.reduce((a, x) => a + f(x.secs), 0) + END;

/** A second and a half: Toki, the name, gone. */
const Title: React.FC = () => {
  const frame = useCurrentFrame();
  const pop = interpolate(frame, [0, 26], [0.9, 1], { extrapolateRight: "clamp", easing: bez(ease.arrive) });
  const inn = interpolate(frame, [0, 20], [0, 1], { extrapolateRight: "clamp", easing: bez(ease.arrive) });
  const out = interpolate(frame, [TITLE - 10, TITLE], [1, 0], { extrapolateLeft: "clamp", easing: bez(ease.depart) });
  return (
    <AbsoluteFill style={{ backgroundColor: c.ink, justifyContent: "center", alignItems: "center", opacity: out }}>
      <div style={{ opacity: inn, transform: `scale(${pop})`, display: "flex", alignItems: "center", gap: 26 }}>
        <TokiMark size={120} />
        <div style={{ fontFamily: font, fontSize: 78, fontWeight: 600, color: c.textHi, letterSpacing: -1.6 }}>Toki</div>
      </div>
    </AbsoluteFill>
  );
};

/** The tail: what it costs, and the one thing that makes it different. */
const EndCard: React.FC = () => {
  const frame = useCurrentFrame();
  const inn = interpolate(frame, [0, 18], [0, 1], { extrapolateRight: "clamp", easing: bez(ease.arrive) });
  const smile = interpolate(frame, [10, 44], [0, 1], { extrapolateRight: "clamp", easing: bez(ease.arrive) });
  return (
    <AbsoluteFill style={{ backgroundColor: c.ink, justifyContent: "center", alignItems: "center", opacity: inn }}>
      <TokiMark size={150} smile={smile} />
      <div style={{ fontFamily: font, fontSize: 46, fontWeight: 600, color: c.textHi, marginTop: 32, letterSpacing: -0.8 }}>
        $20 once. Nothing leaves your laptop.
      </div>
    </AbsoluteFill>
  );
};

export const Film: React.FC = () => {
  let at = 0;
  const seqs: React.ReactNode[] = [
    <Sequence key="title" from={0} durationInFrames={TITLE}>
      <Title />
    </Sequence>,
  ];
  at += TITLE;

  for (const x of CUTS) {
    const d = f(x.secs);
    seqs.push(
      <Sequence key={x.key} from={at} durationInFrames={d}>
        <Shot from={x.from} secs={x.secs} rate={x.rate} />
      </Sequence>
    );
    at += d;
  }

  seqs.push(
    <Sequence key="end" from={at} durationInFrames={END}>
      <EndCard />
    </Sequence>
  );

  return <AbsoluteFill style={{ backgroundColor: c.ink }}>{seqs}</AbsoluteFill>;
};
