import { formatDateTime, notificationFolder, notificationText, type Notification } from "@atrium/shared";
import { useRouter } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { Icon } from "./components/Icon.tsx";
import { Body, Button, ErrorMessage } from "./components/ui.tsx";
import { currentLocale } from "./i18n.ts";
import { openFolderFromOverlay } from "./navigation.ts";
import { useNotificationActions, useNotifications } from "./notifications.ts";
import { radius, space, type, useTheme } from "./theme.ts";

/**
 * The person's notifications (docs/UX.md, D-045), the same list as on the
 * web: newest first, unread marked. Opening one marks it read and goes
 * where it points, when that is a folder; each can be removed.
 */
export function NotificationsScreen() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const list = useNotifications();
  const actions = useNotificationActions();
  const [error, setError] = useState<unknown>(null);
  const [pulled, setPulled] = useState(false);
  const locale = currentLocale();
  const items = list.data?.items ?? [];
  const unread = Number(list.data?.unread ?? 0);

  const run = (action: () => Promise<void>): void => {
    setError(null);
    action().catch((e: unknown) => setError(e));
  };

  const open = (item: Notification): void => {
    if (!item.readAt) {
      run(() => actions.markRead([item.id]));
    }

    const folderId = notificationFolder(item.link);
    if (folderId) {
      openFolderFromOverlay(router, folderId);
    }
  };

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
          {t("notifications.title")}
        </Text>
      </View>

      {list.error && !list.data ? (
        <View style={styles.message}>
          <ErrorMessage error={list.error} />
          <Button onPress={() => void list.refetch()}>{t("common.retry")}</Button>
        </View>
      ) : list.isPending ? (
        <View style={styles.message}>
          <ActivityIndicator color={colors.accent} accessibilityLabel={t("common.loading")} />
        </View>
      ) : (
        <FlatList
          data={items}
          keyExtractor={(item) => item.id}
          contentContainerStyle={styles.list}
          refreshControl={
            <RefreshControl
              refreshing={pulled}
              onRefresh={() => {
                setPulled(true);
                void list.refetch().finally(() => setPulled(false));
              }}
              colors={[colors.accent]}
              tintColor={colors.accent}
            />
          }
          ListHeaderComponent={
            <View style={styles.head}>
              <ErrorMessage error={error} />
              {items.length > 0 ? (
                <View style={styles.actions}>
                  {unread > 0 ? (
                    <Button variant="ghost" onPress={() => run(() => actions.markRead(null))}>
                      {t("notifications.markAllRead")}
                    </Button>
                  ) : null}
                  <Button variant="ghost" onPress={() => run(() => actions.clear())}>
                    {t("notifications.clear")}
                  </Button>
                </View>
              ) : null}
            </View>
          }
          ListEmptyComponent={
            <View style={styles.empty}>
              <Icon name="bell" size={40} color={colors.inkFaint} />
              <Body faint>{t("notifications.empty")}</Body>
            </View>
          }
          renderItem={({ item }) => {
            const isNew = !item.readAt;
            return (
              <View style={[styles.row, { backgroundColor: isNew ? colors.accentWash : colors.surface }]}>
                <Pressable accessibilityRole="button" onPress={() => open(item)} style={styles.rowMain}>
                  {isNew ? <View style={[styles.dot, { backgroundColor: colors.accent }]} /> : null}
                  <View style={styles.rowText}>
                    <Text style={[styles.text, { color: isNew ? colors.accentWashInk : colors.ink }]}>
                      {notificationText(t, item)}
                    </Text>
                    <Text style={[styles.when, { color: colors.inkFaint }]}>
                      {formatDateTime(item.createdAt, locale)}
                    </Text>
                  </View>
                </Pressable>
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={t("notifications.remove")}
                  onPress={() => run(() => actions.remove(item.id))}
                  style={styles.remove}
                >
                  <Icon name="close" size={18} color={colors.inkSoft} />
                </Pressable>
              </View>
            );
          }}
        />
      )}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { minHeight: 56, flexDirection: "row", alignItems: "center", paddingHorizontal: space.sm, gap: space.xs },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  title: { flex: 1, fontSize: type.heading, fontWeight: "700", paddingHorizontal: space.sm },
  message: { flex: 1, padding: space.xl, gap: space.lg, justifyContent: "center" },
  list: { flexGrow: 1, paddingHorizontal: space.lg, paddingBottom: space.xl, gap: space.sm },
  head: { gap: space.sm },
  actions: { flexDirection: "row", justifyContent: "flex-end", flexWrap: "wrap" },
  empty: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.md, padding: space.xxl },
  row: { borderRadius: radius.md, flexDirection: "row", alignItems: "center" },
  rowMain: { flex: 1, flexDirection: "row", alignItems: "center", gap: space.md, padding: space.lg },
  dot: { width: 8, height: 8, borderRadius: 4 },
  rowText: { flex: 1, gap: 4 },
  text: { fontSize: type.body, lineHeight: 22 },
  when: { fontSize: type.small },
  remove: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
});
