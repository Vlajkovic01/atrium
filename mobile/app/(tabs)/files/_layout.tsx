import { Stack } from "expo-router";

import { useTheme } from "../../../src/theme.ts";

/** Folders open one over another, so the back button goes up one folder at a time. */
export default function FilesLayout() {
  const { colors } = useTheme();
  return <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.paper } }} />;
}
