import React from "react";
import { useCurrentFrame } from "remotion";

/**
 * Toki himself - the SAME vector the app draws, straight from the Figma file (Udtb5KHB2iTLIVP5FhKaSl,
 * node 325:288). Not a screen-grab of him and not a redraw: the identical path, the identical
 * gradient, the identical eye geometry. That is why he can be a real element in the film rather than
 * a picture pasted into it.
 *
 * He breathes here too, on the app's own numbers (a ~3.5s cycle at under 2% amplitude).
 */
export const TokiMark: React.FC<{
  size?: number;
  /** 0 = eyes open, 1 = the ^ ^ smile. The app morphs between these; so does this. */
  smile?: number;
  breathe?: boolean;
}> = ({ size = 220, smile = 0, breathe = true }) => {
  const frame = useCurrentFrame();

  // 3.5s cycle, <=2% - the researched relaxed-breathing numbers the control uses.
  const t = breathe ? Math.sin((frame / 60) * ((2 * Math.PI) / 3.5)) : 0;
  const sx = 1 + t * 0.014;
  const sy = 1 - t * 0.018;

  // The eye, as one shape of thirteen points. Open is an exact ellipse (kappa); the smile is the same
  // points moved into a caret. Interpolating between them IS the app's eye morph.
  const OPEN: [number, number][] = [
    [3.523, 0.0],
    [5.469, 0.0], [7.046, 3.415], [7.046, 7.628],
    [7.046, 11.842], [5.469, 15.257], [3.523, 15.257],
    [1.577, 15.257], [0.0, 11.842], [0.0, 7.628],
    [0.0, 3.415], [1.577, 0.0], [3.523, 0.0],
  ];
  const HAPPY: [number, number][] = [
    [3.523, 3.2],
    [5.792, 5.444], [8.062, 7.688], [10.4, 10.0],
    [8.337, 8.98], [5.724, 7.688], [3.523, 6.6],
    [1.324, 7.688], [-1.288, 8.98], [-3.35, 10.0],
    [-1.013, 7.688], [1.255, 5.444], [3.523, 3.2],
  ];
  const eyePath = (ox: number) => {
    const p = OPEN.map((o, i) => [
      ox + o[0] + (HAPPY[i][0] - o[0]) * smile,
      o[1] + (HAPPY[i][1] - o[1]) * smile,
    ]);
    return (
      `M${p[0][0]},${p[0][1]}` +
      [0, 1, 2, 3]
        .map((s) => {
          const a = p[1 + s * 3], b = p[2 + s * 3], e = p[3 + s * 3];
          return `C${a[0]},${a[1]} ${b[0]},${b[1]} ${e[0]},${e[1]}`;
        })
        .join(" ") +
      "Z"
    );
  };

  const BODY =
    "M27.4168 0.0439105C27.7899 0.0125787 28.3679 0 28.7515 0C32.9906 0.0038307 37.0098 1.64726 39.729 4.48815C41.0304 5.84354 41.3616 6.74919 43.3084 7.73882C44.755 8.47438 45.3972 8.46849 46.959 8.8559C49.3313 9.4639 51.4908 10.5823 53.2392 12.1085C55.9217 14.4471 57.4299 17.621 57.4319 20.9312C57.4208 24.6086 55.6357 28.0527 52.4072 30.4546C50.8977 31.5775 49.6979 31.9525 48.5994 33.6049C47.8552 34.6249 47.664 35.7215 47.2942 36.8678C46.5434 39.1941 44.9545 41.292 42.8656 42.886C38.3887 46.3021 34.3829 40.0285 28.7515 40.0182C22.6942 40.021 17.7107 46.0289 13.3369 41.8383C12.0847 40.6385 11.091 39.2388 10.4618 37.7249C9.7477 36.0068 9.75444 34.6283 8.45776 33.1075C7.47901 31.9594 6.60185 31.5174 5.34464 30.6747C2.54343 28.7976 0.532911 25.6181 0.116654 22.5663C-0.690722 17.0413 2.7529 11.7093 8.58423 9.45527C10.6896 8.64178 12.4614 8.64504 14.3439 7.63665C15.9027 6.80167 16.4434 5.85612 17.5137 4.69924C20.0818 1.9233 23.402 0.444534 27.4168 0.0439105Z";

  return (
    <svg
      width={size}
      height={(size / 57.4319) * 43.9042}
      viewBox="0 0 57.4319 43.9042"
      style={{ overflow: "visible", transform: `scale(${sx}, ${sy})`, transformOrigin: "50% 90%" }}
    >
      <defs>
        <linearGradient id="tokiBody" x1="0" y1="15.38" x2="0" y2="43.9" gradientUnits="userSpaceOnUse">
          <stop stopColor="#01A4FF" />
          <stop offset="1" stopColor="#F0FAF9" />
        </linearGradient>
        <radialGradient id="tokiRim" cx="0.5" cy="0.46" r="0.58">
          <stop offset="0.58" stopColor="#C7F0FC" stopOpacity="0" />
          <stop offset="0.86" stopColor="#C7F0FC" stopOpacity="0.30" />
          <stop offset="1" stopColor="#C7F0FC" stopOpacity="0.72" />
        </radialGradient>
        <linearGradient id="tokiCrown" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#C7F0FC" stopOpacity="0.86" />
          <stop offset="0.10" stopColor="#C7F0FC" stopOpacity="0.69" />
          <stop offset="0.25" stopColor="#C7F0FC" stopOpacity="0.20" />
          <stop offset="0.42" stopColor="#C7F0FC" stopOpacity="0" />
        </linearGradient>
        <linearGradient id="tokiEye" x1="0" y1="0" x2="0" y2="1.6358">
          <stop stopColor="#F5F5F0" />
          <stop offset="1" stopColor="#91E4F9" />
        </linearGradient>
      </defs>
      <path d={BODY} fill="url(#tokiBody)" />
      <path d={BODY} fill="url(#tokiRim)" />
      <path d={BODY} fill="url(#tokiCrown)" />
      <path d={eyePath(18.988)} transform="translate(0,10.255)" fill="url(#tokiEye)" />
      <path d={eyePath(31.49)} transform="translate(0,10.255)" fill="url(#tokiEye)" />
    </svg>
  );
};
