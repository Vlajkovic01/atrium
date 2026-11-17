import { useState } from "react";
import { SafeAreaView } from "react-native-safe-area-context";

import { Albums } from "../../../src/photos/Albums.tsx";
import { Switcher, type PhotoView } from "../../../src/photos/parts.tsx";
import { Timeline } from "../../../src/photos/Timeline.tsx";
import { useTheme } from "../../../src/theme.ts";

/** Photos: the timeline, or the albums. */
export default function Photos() {
  const { colors } = useTheme();
  const [view, setView] = useState<PhotoView>("timeline");
  const switcher = <Switcher value={view} onChange={setView} />;
  return (
    <SafeAreaView edges={["top", "left", "right"]} style={{ flex: 1, backgroundColor: colors.paper }}>
      {view === "timeline" ? <Timeline switcher={switcher} /> : <Albums switcher={switcher} />}
    </SafeAreaView>
  );
}
