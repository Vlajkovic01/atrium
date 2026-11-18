import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Modal, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { radius, space, type, useTheme } from "../theme.ts";
import { Icon, type IconName } from "./Icon.tsx";

/**
 * A panel that rises from the bottom edge, for choices and details that
 * belong to the screen behind it. Closes by the back button, by touching
 * outside it, or by its own close button.
 */
export function Sheet({
  title,
  onClose,
  children,
}: {
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
}) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const insets = useSafeAreaInsets();

  return (
    <Modal transparent animationType="slide" onRequestClose={onClose} statusBarTranslucent>
      <View style={styles.root}>
        <Pressable
          style={[StyleSheet.absoluteFill, styles.shade]}
          onPress={onClose}
          // A touch outside closes; for a screen reader the panel's own button does, so this is not announced twice.
          accessible={false}
          importantForAccessibility="no-hide-descendants"
          accessibilityElementsHidden
        />
        <View
          accessibilityViewIsModal
          style={[
            styles.panel,
            { backgroundColor: colors.surface, borderColor: colors.line, paddingBottom: insets.bottom + space.lg },
          ]}
        >
          <View style={styles.head}>
            <Text accessibilityRole="header" numberOfLines={2} style={[styles.title, { color: colors.ink }]}>
              {title}
            </Text>
            <Pressable
              onPress={onClose}
              accessibilityRole="button"
              accessibilityLabel={t("common.close")}
              hitSlop={space.md}
              style={styles.close}
            >
              <Icon name="close" color={colors.inkSoft} />
            </Pressable>
          </View>
          <ScrollView contentContainerStyle={styles.body}>{children}</ScrollView>
        </View>
      </View>
    </Modal>
  );
}

/** One choice in a sheet: a full-width row, with a tick when it is the current one. */
export function SheetOption({
  label,
  icon,
  selected = false,
  onPress,
}: {
  readonly label: string;
  readonly icon?: IconName;
  readonly selected?: boolean;
  readonly onPress: () => void;
}) {
  const { colors } = useTheme();
  return (
    <Pressable
      onPress={onPress}
      accessibilityRole="button"
      accessibilityState={{ selected }}
      style={({ pressed }) => [
        styles.option,
        { backgroundColor: selected ? colors.accentWash : pressed ? colors.surfaceSunk : "transparent" },
      ]}
    >
      {icon ? <Icon name={icon} color={selected ? colors.accentWashInk : colors.inkSoft} /> : null}
      <Text style={[styles.optionText, { color: selected ? colors.accentWashInk : colors.ink }]}>{label}</Text>
      {selected ? <Icon name="check" color={colors.accentWashInk} /> : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, justifyContent: "flex-end" },
  shade: { backgroundColor: "rgba(8, 14, 12, 0.5)" },
  panel: {
    maxHeight: "85%",
    width: "100%",
    maxWidth: 560,
    alignSelf: "center",
    borderTopLeftRadius: radius.lg,
    borderTopRightRadius: radius.lg,
    borderWidth: 1,
    borderBottomWidth: 0,
    paddingTop: space.lg,
  },
  head: {
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
    paddingHorizontal: space.xl,
    paddingBottom: space.md,
  },
  title: { flex: 1, fontSize: type.heading, fontWeight: "600" },
  close: { width: 44, height: 44, alignItems: "center", justifyContent: "center" },
  body: { paddingHorizontal: space.md, gap: space.xs },
  option: {
    minHeight: 52,
    borderRadius: radius.md,
    paddingHorizontal: space.md,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
  },
  optionText: { flex: 1, fontSize: type.body },
});
