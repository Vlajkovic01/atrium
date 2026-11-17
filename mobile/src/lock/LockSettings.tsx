import { useTranslation } from "react-i18next";
import { Pressable, StyleSheet, Text, View } from "react-native";

import { Icon } from "../components/Icon.tsx";
import { Body, Heading } from "../components/ui.tsx";
import { radius, space, type, useTheme } from "../theme.ts";
import { lockAfterChoices, useLock } from "./lock.tsx";

/** The lock in Account: on or off, and after how long away it locks again. */
export function LockSettings() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const lock = useLock();

  return (
    <View style={[styles.card, { backgroundColor: colors.surface, borderColor: colors.line }]}>
      <Heading>{t("mobile.lock.title")}</Heading>
      {lock.available ? (
        <>
          <Pressable
            accessibilityRole="switch"
            accessibilityState={{ checked: lock.enabled }}
            aria-checked={lock.enabled}
            onPress={() => void lock.setEnabled(!lock.enabled)}
            style={styles.row}
          >
            <Icon
              name={lock.enabled ? "checkCircle" : "circle"}
              color={lock.enabled ? colors.accent : colors.inkSoft}
            />
            <Text style={[styles.label, { color: colors.ink }]}>{t("mobile.lock.enable")}</Text>
          </Pressable>
          <Body faint>{t("mobile.lock.explain")}</Body>
          {lock.enabled ? (
            <>
              <Text style={[styles.small, { color: colors.inkSoft }]}>{t("mobile.lock.after")}</Text>
              <View style={styles.choices}>
                {lockAfterChoices.map((minutes) => {
                  const on = lock.after === minutes;
                  return (
                    <Pressable
                      key={minutes}
                      accessibilityRole="radio"
                      accessibilityState={{ checked: on }}
                      aria-checked={on}
                      onPress={() => lock.setAfter(minutes)}
                      style={[
                        styles.chip,
                        {
                          backgroundColor: on ? colors.accentWash : colors.surface,
                          borderColor: on ? colors.accentWash : colors.lineStrong,
                        },
                      ]}
                    >
                      <Text style={[styles.chipText, { color: on ? colors.accentWashInk : colors.ink }]}>
                        {minutes === 0 ? t("mobile.lock.atOnce") : t("mobile.lock.minutes", { count: minutes })}
                      </Text>
                    </Pressable>
                  );
                })}
              </View>
            </>
          ) : null}
        </>
      ) : (
        <Body faint>{t("mobile.lock.unavailable")}</Body>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { borderRadius: radius.md, borderWidth: 1, padding: space.lg, gap: space.md },
  row: { flexDirection: "row", alignItems: "center", gap: space.sm, minHeight: 44 },
  label: { flex: 1, fontSize: type.body },
  small: { fontSize: type.small, fontWeight: "600" },
  choices: { flexDirection: "row", flexWrap: "wrap", gap: space.sm },
  chip: {
    minHeight: 40,
    borderRadius: radius.pill,
    borderWidth: 1,
    paddingHorizontal: space.lg,
    justifyContent: "center",
  },
  chipText: { fontSize: type.small, fontWeight: "600" },
});
