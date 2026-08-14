import { GlassPanel } from "toki-design";

const wallpaper: React.CSSProperties = {
  padding: 28,
  background: "linear-gradient(135deg, #7fa66a 0%, #c9c08f 45%, #e8b7a0 100%)",
  borderRadius: 12,
};

export const OpaqueCard = () => (
  <div style={{ padding: 24, background: "#0E0E10", borderRadius: 12 }}>
    <GlassPanel width={320}>
      <div style={{ fontSize: 13, fontWeight: 600 }}>Welcome back, Sid!</div>
      <div style={{ fontSize: 11, color: "var(--tk-text-mid)", marginTop: 4 }}>
        The hovering card: radius 24, white-10% rim, top-edge highlight, soft shadow.
      </div>
    </GlassPanel>
  </div>
);

export const TranslucentOverWallpaper = () => (
  <div style={wallpaper}>
    <GlassPanel width={320} translucent>
      <div style={{ fontSize: 13, fontWeight: 600 }}>Warm dark glass</div>
      <div style={{ fontSize: 11, color: "var(--tk-text-mid)", marginTop: 4 }}>
        rgba(33,30,30,0.5) + backdrop blur - the desktop shows through.
      </div>
    </GlassPanel>
  </div>
);
