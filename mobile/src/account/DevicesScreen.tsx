import { formatDateTime, type SessionInfo } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { ConfirmDialog } from "../components/Dialog.tsx";
import { Icon, type IconName } from "../components/Icon.tsx";
import { Body, Button, ErrorMessage } from "../components/ui.tsx";
import { currentLocale } from "../i18n.ts";
import { radius, space, type, useTheme } from "../theme.ts";

const kindIcon: Record<SessionInfo["deviceKind"], IconName> = {
  web: "browser",
  mobile: "phone",
  tv: "tv",
  other: "file",
};

/**
 * Every device signed in to the person's account on the server in front
 * (docs/UX.md, Devices): what it is, when it was last used, and a way to
 * sign it out. This phone itself signs out from the Account screen. A TV
 * is approved from here.
 */
export function DevicesScreen() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const { server, devices } = useActiveServer();
  const queryClient = useQueryClient();
  const sessions = useQuery({ queryKey: [server.id, "sessions"], queryFn: devices.sessions });
  const [signingOut, setSigningOut] = useState<SessionInfo | null>(null);
  const locale = currentLocale();

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={[styles.flex, { backgroundColor: colors.paper }]}>
      <View style={styles.bar}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("common.back")}
          onPress={() => (router.canGoBack() ? router.back() : router.replace("/account"))}
          style={styles.barButton}
        >
          <Icon name="chevronLeft" />
        </Pressable>
        <Text accessibilityRole="header" numberOfLines={1} style={[styles.title, { color: colors.ink }]}>
          {t("devices.heading")}
        </Text>
      </View>

      {sessions.isPending ? (
        <View style={styles.message}>
          <ActivityIndicator color={colors.accent} accessibilityLabel={t("common.loading")} />
        </View>
      ) : (
        <FlatList
          data={sessions.data ?? []}
          keyExtractor={(s) => s.id}
          contentContainerStyle={styles.list}
          ListHeaderComponent={
            <View style={styles.head}>
              <Body faint>{t("devices.body")}</Body>
              <Button variant="primary" onPress={() => router.push("/account/approve")}>
                {t("devices.approve.heading")}
              </Button>
              <ErrorMessage error={sessions.error} />
            </View>
          }
          renderItem={({ item }) => (
            <View style={[styles.card, { backgroundColor: colors.surface, borderColor: colors.line }]}>
              <Icon name={kindIcon[item.deviceKind]} color={colors.inkSoft} />
              <View style={styles.text}>
                <Text style={[styles.name, { color: colors.ink }]}>{item.deviceName}</Text>
                <Text style={[styles.detail, { color: colors.inkFaint }]}>
                  {[t(`devices.kinds.${item.deviceKind}`), item.platform].filter(Boolean).join(" · ")}
                </Text>
                <Text style={[styles.detail, { color: colors.inkFaint }]}>
                  {item.current
                    ? t("devices.thisDevice")
                    : t("devices.lastUsed", { when: formatDateTime(item.lastSeenAt, locale) })}
                  {item.readOnly ? ` · ${t("devices.readOnly")}` : ""}
                </Text>
              </View>
              {item.current ? null : (
                <Button variant="ghost" onPress={() => setSigningOut(item)}>
                  {t("devices.signOut")}
                </Button>
              )}
            </View>
          )}
        />
      )}

      {signingOut ? (
        <ConfirmDialog
          title={t("devices.signOutConfirm", { device: signingOut.deviceName })}
          body={t("mobile.devices.signOutBody")}
          confirm={t("devices.signOut")}
          onClose={() => setSigningOut(null)}
          onConfirm={async () => {
            await devices.signOut(signingOut.id);
            await queryClient.invalidateQueries({ queryKey: [server.id, "sessions"] });
            setSigningOut(null);
          }}
        />
      ) : null}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { minHeight: 56, flexDirection: "row", alignItems: "center", paddingHorizontal: space.sm, gap: space.xs },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  title: { flex: 1, fontSize: type.heading, fontWeight: "700", paddingHorizontal: space.sm },
  message: { flex: 1, padding: space.xl, justifyContent: "center" },
  list: { flexGrow: 1, paddingHorizontal: space.lg, paddingBottom: space.xl, gap: space.md },
  head: { gap: space.md, paddingBottom: space.sm },
  card: {
    borderRadius: radius.md,
    borderWidth: 1,
    padding: space.lg,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
  },
  text: { flex: 1, gap: 2 },
  name: { fontSize: type.body, fontWeight: "600" },
  detail: { fontSize: type.small },
});
