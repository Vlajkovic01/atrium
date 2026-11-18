import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { Stack } from "expo-router";
import { StatusBar } from "expo-status-bar";
import { useEffect, useState } from "react";
import { View } from "react-native";

import { forgetLocalCopies } from "../src/files/local";
import { loadFileOrder } from "../src/files/order.ts";
import { initI18n } from "../src/i18n.ts";
import { loadDensity } from "../src/photos/density.ts";
import { ServersProvider } from "../src/servers.tsx";
import { useTheme } from "../src/theme.ts";

// What screens read from a server is kept for a short while, so going
// back to a folder shows it at once and then checks it again. A failed
// request is repeated once; the screen then says what went wrong.
const queries = new QueryClient({
  defaultOptions: { queries: { staleTime: 10_000, retry: 1 } },
});

/**
 * The frame around every screen: translations, settings and the list of
 * servers are ready before anything is drawn, colours follow the system's
 * light or dark setting.
 */
export default function RootLayout() {
  const { colors, isDark } = useTheme();
  const [ready, setReady] = useState(false);

  useEffect(() => {
    // Copies of files opened last time are no longer looked at (D-052).
    forgetLocalCopies();
    void Promise.all([initI18n(), loadFileOrder(), loadDensity()]).then(() => setReady(true));
  }, []);

  if (!ready) {
    return <View style={{ flex: 1, backgroundColor: colors.paper }} />;
  }

  return (
    <QueryClientProvider client={queries}>
      <ServersProvider>
        <StatusBar style={isDark ? "light" : "dark"} />
        <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.paper } }} />
      </ServersProvider>
    </QueryClientProvider>
  );
}
