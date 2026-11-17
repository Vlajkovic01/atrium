import { Stack } from "expo-router";

import { useTheme } from "../../../src/theme.ts";

/** The timeline and the albums, with an album opening over them. */
export default function PhotosLayout() {
  const { colors } = useTheme();
  return <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.paper } }} />;
}
