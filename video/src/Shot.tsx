import React from "react";
import { AbsoluteFill, OffthreadVideo, staticFile } from "remotion";
import { c, FPS } from "./theme";

/**
 * One piece of the recording, FULL FRAME.
 *
 * The first cut cropped and pushed into each shot, and the owner's verdict was exact: "the editing of
 * the screens is like cut off and the full frame is not visible, instead its zoomed in". They are
 * right - this is a screen recording of a desktop app, and cropping it hides the thing the viewer is
 * being shown. No transform, no pan, no scale. The frame you recorded is the frame that plays.
 *
 * There is also no transition of any kind. The first cut dipped to black at the START of every
 * segment, which read as "blacks out every 2 seconds" - because that is exactly what it did, eleven
 * times. Straight cuts.
 */
export const Shot: React.FC<{
  /** seconds into the recording */
  from: number;
  /** seconds of output */
  secs: number;
  /** playback rate; under 1 holds a moment that is too brief in the take */
  rate?: number;
}> = ({ from, rate = 1 }) => (
  <AbsoluteFill style={{ backgroundColor: c.ink }}>
    <OffthreadVideo
      src={staticFile("raw.mp4")}
      startFrom={Math.round(from * FPS)}
      playbackRate={rate}
      muted
      style={{ width: "100%", height: "100%" }}
    />
  </AbsoluteFill>
);
