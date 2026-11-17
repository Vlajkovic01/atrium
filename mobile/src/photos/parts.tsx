import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, StyleSheet, Text, View } from "react-native";

import { Icon } from "../components/Icon.tsx";
import { radius, space, type, useTheme } from "../theme.ts";
import { photoStyles } from "./PhotoGrid.tsx";

/** How long a confirmation stays, as on the web (D-045). */
const TOAST_MS = 8000;

/** The top bar while items are being chosen: how many, and a way out. */
export function SelectionBar({ count, onClear }: { readonly count: number; readonly onClear: () => void }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  return (
    <View style={[photoStyles.bar, styles.selectionBar]}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("photos.clearSelection")}
        onPress={onClear}
        style={photoStyles.barButton}
      >
        <Icon name="close" />
      </Pressable>
      <Text accessibilityRole="header" numberOfLines={1} style={[photoStyles.barTitle, { color: colors.ink }]}>
        {t("files.selected", { count })}
      </Text>
    </View>
  );
}

/** A confirmation at the bottom that goes away by itself. */
export function Toast({ message, onDone }: { readonly message: string; readonly onDone: () => void }) {
  const { colors } = useTheme();
  useEffect(() => {
    const timer = setTimeout(onDone, TOAST_MS);
    return () => clearTimeout(timer);
  }, [message, onDone]);

  return (
    <View pointerEvents="box-none" style={styles.toastBox}>
      <Pressable accessibilityRole="alert" onPress={onDone} style={[styles.toast, { backgroundColor: colors.ink }]}>
        <Text style={[styles.toastText, { color: colors.paper }]}>{message}</Text>
      </Pressable>
    </View>
  );
}

export type PhotoView = "timeline" | "albums";

/** Timeline or albums, the two ways into the photos. */
export function Switcher({
  value,
  onChange,
}: {
  readonly value: PhotoView;
  readonly onChange: (v: PhotoView) => void;
}) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const choices: [PhotoView, string][] = [
    ["timeline", t("photos.timeline")],
    ["albums", t("albums.title")],
  ];
  return (
    <View accessibilityRole="tablist" style={[styles.switcher, { backgroundColor: colors.surfaceSunk }]}>
      {choices.map(([key, label]) => {
        const current = key === value;
        return (
          <Pressable
            key={key}
            accessibilityRole="tab"
            accessibilityState={{ selected: current }}
            onPress={() => onChange(key)}
            style={[styles.choice, current && { backgroundColor: colors.surface, borderColor: colors.line }]}
          >
            <Text style={[styles.choiceText, { color: current ? colors.ink : colors.inkSoft }]}>{label}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  selectionBar: { paddingLeft: space.sm },
  toastBox: { position: "absolute", left: space.lg, right: space.lg, bottom: space.lg },
  toast: { minHeight: 52, borderRadius: radius.md, paddingHorizontal: space.lg, justifyContent: "center" },
  toastText: { fontSize: type.small },
  switcher: {
    flexDirection: "row",
    marginHorizontal: space.lg,
    marginBottom: space.sm,
    padding: 3,
    borderRadius: radius.md,
  },
  choice: {
    flex: 1,
    minHeight: 40,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: radius.sm,
    borderWidth: 1,
    borderColor: "transparent",
  },
  choiceText: { fontSize: type.small, fontWeight: "600" },
});
