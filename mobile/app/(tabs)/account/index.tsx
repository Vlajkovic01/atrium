import { useRouter } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { StyleSheet, Text, View } from "react-native";

import { Body, Button, Heading, Notice, Screen, Title } from "../../../src/components/ui.tsx";
import { displayAddress } from "../../../src/connect.ts";
import { forgetLocalCopies } from "../../../src/files/local";
import { forgetOffline } from "../../../src/offline/OfflineProvider.tsx";
import { useNotifications } from "../../../src/notifications.ts";
import { useServers } from "../../../src/servers.tsx";
import { radius, space, type, useTheme } from "../../../src/theme.ts";

/**
 * Who is signed in where, the other servers the app knows, and signing
 * out (docs/UX.md, Mobile, Connect). Devices and the lock join it later
 * in this phase.
 */
export default function Account() {
  const { t } = useTranslation();
  const router = useRouter();
  const { colors } = useTheme();
  const { registry, servers, active } = useServers();
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const notifications = useNotifications();
  const unread = Number(notifications.data?.unread ?? 0);

  if (!active) {
    return null;
  }

  const signOut = async (): Promise<void> => {
    setBusy(true);
    try {
      await registry.remove(active.id);
      // What was opened from that server goes with it.
      forgetLocalCopies(active.id);
      await forgetOffline(active.id).catch(() => undefined);
    } finally {
      setBusy(false);
      setConfirming(false);
    }
  };

  const card = [styles.card, { backgroundColor: colors.surface, borderColor: colors.line }];

  return (
    <Screen>
      <Title>{t("nav.account")}</Title>
      <View style={card}>
        <Text style={[styles.name, { color: colors.ink }]}>
          {t("mobile.home.signedInAs", { name: active.user.displayName, server: displayAddress(active.url) })}
        </Text>
        {/* Shown for as long as the connection is not encrypted (DESIGN.md 12, risk 6). */}
        {active.url.startsWith("http://") ? <Notice kind="warning">{t("connection.insecure")}</Notice> : null}
      </View>

      {servers.length > 1 ? (
        <>
          <Heading>{t("mobile.home.servers")}</Heading>
          {servers.map((server) => (
            <View key={server.id} style={card}>
              <Text style={[styles.name, { color: colors.ink }]}>
                {t("mobile.home.signedInAs", { name: server.user.displayName, server: displayAddress(server.url) })}
              </Text>
              {server.id === active.id ? (
                <Body faint>{t("mobile.home.current")}</Body>
              ) : (
                <Button onPress={() => void registry.setActive(server.id)}>{t("mobile.home.use")}</Button>
              )}
            </View>
          ))}
        </>
      ) : null}

      <Button onPress={() => router.push("/notifications")}>
        {unread > 0 ? t("notifications.openUnread", { count: unread }) : t("notifications.open")}
      </Button>
      <Button onPress={() => router.push("/account/downloads")}>{t("mobile.offline.title")}</Button>
      <Button onPress={() => router.push("/welcome")}>{t("mobile.home.addServer")}</Button>
      {confirming ? (
        <View style={card}>
          <Body>{t("mobile.home.signOutBody", { server: displayAddress(active.url) })}</Body>
          <Button variant="danger" busy={busy} onPress={() => void signOut()}>
            {t("mobile.home.signOut")}
          </Button>
          <Button variant="ghost" onPress={() => setConfirming(false)}>
            {t("common.cancel")}
          </Button>
        </View>
      ) : (
        <Button variant="danger" onPress={() => setConfirming(true)}>
          {t("mobile.home.signOut")}
        </Button>
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { borderRadius: radius.md, borderWidth: 1, padding: space.lg, gap: space.md },
  name: { fontSize: type.body, fontWeight: "600" },
});
