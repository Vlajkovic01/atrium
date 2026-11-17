import { Stack } from "expo-router";

import { useTheme } from "../../../src/theme.ts";

/** The account, with the downloads opening over it. */
export default function AccountLayout() {
  const { colors } = useTheme();
  return <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.paper } }} />;
}
