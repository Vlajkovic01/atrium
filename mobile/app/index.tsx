import Constants from "expo-constants";
import { useTranslation } from "react-i18next";
import { StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { LogoMark } from "../src/components/Logo.tsx";
import { radius, space, type, useTheme } from "../src/theme.ts";

/** First launch: what Atrium is and how to connect. The ways to connect arrive with the next change. */
export default function Welcome() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const version = Constants.expoConfig?.version ?? "";

  return (
    <SafeAreaView style={[styles.screen, { backgroundColor: colors.paper }]}>
      <View style={styles.center}>
        <LogoMark size={72} />
        <Text accessibilityRole="header" style={[styles.title, { color: colors.ink }]}>
          {t("mobile.welcome.heading")}
        </Text>
        <Text style={[styles.body, { color: colors.inkSoft }]}>{t("mobile.welcome.body")}</Text>
        <View style={[styles.note, { backgroundColor: colors.accentWash }]}>
          <Text style={[styles.noteText, { color: colors.accentWashInk }]}>{t("mobile.welcome.how")}</Text>
        </View>
      </View>
      <Text style={[styles.version, { color: colors.inkFaint }]}>{t("mobile.about.version", { version })}</Text>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, padding: space.xl },
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.lg },
  title: { fontSize: type.title, fontWeight: "700", textAlign: "center" },
  body: { fontSize: type.body, lineHeight: 24, textAlign: "center", maxWidth: 420 },
  note: { borderRadius: radius.md, padding: space.lg, maxWidth: 420 },
  noteText: { fontSize: type.small, lineHeight: 20, textAlign: "center" },
  version: { fontSize: type.small, textAlign: "center" },
});
