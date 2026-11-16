import { Stack } from "expo-router";
import { StatusBar } from "expo-status-bar";
import { useEffect, useState } from "react";
import { View } from "react-native";

import { initI18n } from "../src/i18n.ts";
import { useTheme } from "../src/theme.ts";

/** The frame around every screen: translations ready before anything is drawn, colours from the system's light or dark setting. */
export default function RootLayout() {
  const { colors, isDark } = useTheme();
  const [ready, setReady] = useState(false);

  useEffect(() => {
    void initI18n().then(() => setReady(true));
  }, []);

  if (!ready) {
    return <View style={{ flex: 1, backgroundColor: colors.paper }} />;
  }

  return (
    <>
      <StatusBar style={isDark ? "light" : "dark"} />
      <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.paper } }} />
    </>
  );
}
