import {
  ApiError,
  daysUntil,
  formatBytes,
  libraryRootName,
  trashLocation,
  trashTotals,
  type TrashItem,
} from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { ConfirmDialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { Body, Button, ErrorMessage } from "../components/ui.tsx";
import { currentLocale } from "../i18n.ts";
import { radius, space, type, useTheme } from "../theme.ts";
import { FolderPicker } from "./FolderPicker.tsx";
import { nodeIcon } from "./icons.ts";

/**
 * The trash (docs/UX.md, Trash): what was deleted, where it was and how
 * long it stays. Items can be put back or removed for good; removing for
 * good always asks first and says how much goes.
 */
export function TrashScreen() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const { server, files } = useActiveServer();
  const queryClient = useQueryClient();
  const locale = currentLocale();
  const [chosen, setChosen] = useState<TrashItem | null>(null);
  const [confirming, setConfirming] = useState<TrashItem | "all" | null>(null);
  const [placing, setPlacing] = useState<TrashItem | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [placeError, setPlaceError] = useState<unknown>(null);
  const [pulled, setPulled] = useState(false);

  const trash = useQuery({ queryKey: [server.id, "trash"], queryFn: files.trash });
  const libraries = useQuery({ queryKey: [server.id, "libraries"], queryFn: files.libraries });
  const items = trash.data ?? [];
  const totals = trashTotals(items);

  const changed = async (): Promise<void> => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: [server.id, "trash"] }),
      queryClient.invalidateQueries({ queryKey: [server.id, "children"] }),
    ]);
  };

  const restore = (item: TrashItem): void => {
    setError(null);
    void (async () => {
      try {
        await files.restore(item.node.id);
      } catch (e) {
        // The folder it came from is in the trash too: ask where to put it.
        if (e instanceof ApiError && e.problem.code === "node.in_trash") {
          setPlacing(item);
        } else {
          setError(e);
        }
      } finally {
        await changed();
      }
    })();
  };

  const restoreInto = (item: TrashItem, parentId: string): void => {
    setBusy(true);
    setPlaceError(null);
    void (async () => {
      try {
        await files.restore(item.node.id, parentId);
        setPlacing(null);
      } catch (e) {
        setPlaceError(e);
      } finally {
        setBusy(false);
        await changed();
      }
    })();
  };

  const placingLibrary = placing ? libraries.data?.find((l) => l.id === placing.node.libraryId) : undefined;
  const confirmTotals = confirming === null ? null : confirming === "all" ? totals : trashTotals([confirming]);

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={[styles.flex, { backgroundColor: colors.paper }]}>
      <View style={styles.bar}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("common.back")}
          onPress={() => (router.canGoBack() ? router.back() : router.replace("/files"))}
          style={styles.barButton}
        >
          <Icon name="chevronLeft" />
        </Pressable>
        <Text accessibilityRole="header" numberOfLines={1} style={[styles.barTitle, { color: colors.ink }]}>
          {t("trash.title")}
        </Text>
      </View>

      {trash.error && items.length === 0 ? (
        <View style={styles.message}>
          <ErrorMessage error={trash.error} />
          <Button onPress={() => void trash.refetch()}>{t("common.retry")}</Button>
        </View>
      ) : trash.isPending ? (
        <View style={styles.message}>
          <ActivityIndicator color={colors.accent} accessibilityLabel={t("common.loading")} />
        </View>
      ) : (
        <FlatList
          data={items}
          keyExtractor={(item) => item.node.id}
          contentContainerStyle={styles.list}
          refreshControl={
            <RefreshControl
              refreshing={pulled}
              onRefresh={() => {
                setPulled(true);
                void trash.refetch().finally(() => setPulled(false));
              }}
              colors={[colors.accent]}
              tintColor={colors.accent}
            />
          }
          ListHeaderComponent={
            <View style={styles.head}>
              <Body faint>{t("trash.lead")}</Body>
              <ErrorMessage error={error} />
              {items.length > 0 ? (
                <Button variant="danger" onPress={() => setConfirming("all")}>
                  {t("trash.empty")}
                </Button>
              ) : null}
            </View>
          }
          ListEmptyComponent={
            <View style={styles.empty}>
              <Icon name="trash" size={40} color={colors.inkFaint} />
              <Body faint>{t("trash.nothing")}</Body>
            </View>
          }
          renderItem={({ item }) => {
            const where = trashLocation(item, libraries.data ?? [], t("files.myFiles")).join(" › ");
            const detail = [
              t("trash.daysLeft", { count: daysUntil(item.deleteAt) }),
              formatBytes(Number(item.bytes), locale),
            ].join(" · ");
            return (
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={`${item.node.name}, ${detail}`}
                onPress={() => setChosen(item)}
                style={({ pressed }) => [styles.row, { backgroundColor: pressed ? colors.surfaceSunk : "transparent" }]}
              >
                <View style={[styles.tile, { backgroundColor: colors.surface, borderColor: colors.line }]}>
                  <Icon name={nodeIcon(item.node)} color={colors.inkSoft} />
                </View>
                <View style={styles.rowText}>
                  <Text numberOfLines={2} style={[styles.name, { color: colors.ink }]}>
                    {item.node.name}
                  </Text>
                  {where ? (
                    <Text numberOfLines={1} style={[styles.detail, { color: colors.inkFaint }]}>
                      {t("trash.from")}: {where}
                    </Text>
                  ) : null}
                  <Text numberOfLines={1} style={[styles.detail, { color: colors.inkFaint }]}>
                    {detail}
                  </Text>
                </View>
                <Icon name="more" color={colors.inkSoft} />
              </Pressable>
            );
          }}
        />
      )}

      {chosen ? (
        <Sheet title={chosen.node.name} onClose={() => setChosen(null)}>
          <SheetOption
            label={t("trash.restore")}
            icon="restore"
            onPress={() => {
              setChosen(null);
              restore(chosen);
            }}
          />
          <SheetOption
            label={t("trash.deleteForever")}
            icon="trash"
            onPress={() => {
              setChosen(null);
              setConfirming(chosen);
            }}
          />
        </Sheet>
      ) : null}

      {confirming && confirmTotals ? (
        <ConfirmDialog
          title={t(confirming === "all" ? "trash.emptyTitle" : "trash.deleteTitle")}
          body={t("trash.confirm", { count: confirmTotals.count, size: formatBytes(confirmTotals.bytes, locale) })}
          confirm={t(confirming === "all" ? "trash.empty" : "trash.deleteForever")}
          onClose={() => setConfirming(null)}
          onConfirm={async () => {
            try {
              await (confirming === "all" ? files.emptyTrash() : files.deleteForever(confirming.node.id));
              setConfirming(null);
            } finally {
              await changed();
            }
          }}
        />
      ) : null}

      {placing && placingLibrary ? (
        <FolderPicker
          title={t("trash.restoreTo")}
          confirm={t("trash.restoreHere")}
          rootName={libraryRootName(placingLibrary, t("files.myFiles"))}
          startId={placingLibrary.rootNodeId}
          excluded={new Set()}
          busy={busy}
          error={placeError}
          onClose={() => {
            setPlacing(null);
            setPlaceError(null);
          }}
          onPick={(target) => restoreInto(placing, target)}
        />
      ) : null}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { minHeight: 56, flexDirection: "row", alignItems: "center", paddingHorizontal: space.sm, gap: space.xs },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  barTitle: { flex: 1, fontSize: type.heading, fontWeight: "700", paddingHorizontal: space.sm },
  message: { flex: 1, padding: space.xl, gap: space.lg, justifyContent: "center" },
  list: { flexGrow: 1, paddingBottom: space.xl },
  head: { gap: space.md, paddingHorizontal: space.lg, paddingBottom: space.md },
  empty: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.md, padding: space.xxl },
  row: {
    minHeight: 72,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
  },
  tile: {
    width: 44,
    height: 44,
    borderRadius: radius.sm,
    borderWidth: 1,
    alignItems: "center",
    justifyContent: "center",
  },
  rowText: { flex: 1, gap: 2 },
  name: { fontSize: type.body, fontWeight: "500" },
  detail: { fontSize: type.small },
});
