import React from "react";
import { Composition } from "remotion";
import { Film, filmLength } from "./Film";
import { FPS } from "./theme";

export const RemotionRoot: React.FC = () => (
  <Composition
    id="Toki"
    component={Film}
    durationInFrames={filmLength()}
    fps={FPS}
    width={1920}
    height={1080}
  />
);
