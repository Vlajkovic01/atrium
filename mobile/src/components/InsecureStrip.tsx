import { isUnencrypted } from "@atrium/shared";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, StyleSheet, Text, View } from "react-native";

import { space, useTheme } from "../theme.ts";
import { Icon } from "./Icon.tsx";

/**
 * A strip above the tabs, on every signed-in screen, while the server in
 * front is reached over plain HTTP (DESIGN.md 12, risk 6). Plain HTTP is
 * allowed only to a local address (D-049), but it is still readable by
 * anyone on that network, so the mark stays as long as the connection
 * does. A tap opens the explanation in place.
 */
export function InsecureStrip({ url }: { readonly url: string }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const [open, setOpen] = useState(false);
  if (!isUnencrypted(url)) {
    return null;
  }

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded: open }}
      accessibilityHint={t("connection.unencryptedHint")}
      onPress={() => setOpen((value) => !value)}
      style={[styles.strip, { backgroundColor: colors.goldWash, borderTopColor: colors.line }]}
      testID="insecure-strip"
    >
      <View style={styles.row}>
        <Icon name="lock" size={16} color={colors.goldInk} />
        <Text style={[styles.label, { color: colors.goldInk }]}>{t("connection.unencrypted")}</Text>
        <Icon name="info" size={16} color={colors.goldInk} />
      </View>
      {open ? <Text style={[styles.body, { color: colors.goldInk }]}>{t("connection.insecure")}</Text> : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  strip: {
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: space.md,
    paddingVertical: space.xs,
    minHeight: 32,
    justifyContent: "center",
  },
  row: { flexDirection: "row", alignItems: "center", gap: space.xs },
  label: { flex: 1, fontSize: 13, fontWeight: "600" },
  body: { fontSize: 13, lineHeight: 18, paddingTop: space.xs, paddingBottom: space.xs },
});
