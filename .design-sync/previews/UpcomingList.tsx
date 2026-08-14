import { UpcomingList, GlanceRow } from "toki-design";

const ground: React.CSSProperties = { padding: 20, background: "#0E0E10", borderRadius: 12, width: 360 };

export const ComingUp = () => (
  <div style={ground}>
    <UpcomingList>
      <GlanceRow day={14} month="AUG" title="Design review - PixelFlow" meta="2:00 PM · 30 min"
                 countdown="in 12 min" joinable accent="teal" />
      <GlanceRow day={14} month="AUG" title="Sprint planning" meta="4:00 PM · 60 min" joinable accent="blue" />
      <GlanceRow day={15} month="AUG" title="1:1 with Ana" meta="10:30 AM · 45 min" accent="pink" />
    </UpcomingList>
  </div>
);
