import { formatBytes, offlineTotals } from "@atrium/shared";
import { useRouter } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { Icon } from "../components/Icon.tsx";
import { Body } from "../components/ui.tsx";
import { FileViewer } from "../files/FileViewer.tsx";
import { nodeIcon } from "../files/icons.ts";
import { currentLocale } from "../i18n.ts";
import type { OfflineFile, OfflineRoot } from "../platform/db";
import { radius, space, type, useTheme } from "../theme.ts";
import { useOffline } from "./OfflineProvider.tsx";

/**
 * What is kept on this phone (docs/UX.md, Mobile, Offline): each offline
 * item with its progress and size, the space used in all, and a way to
 * remove an item from the phone, which leaves it on the server. Whole
 * copies open here without a connection.
 */
export function DownloadsScreen() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const offline = useOffline();
  const [expanded, setExpanded] = useState<string | null>(null);
  const [open, setOpen] = useState<{ readonly list: readonly OfflineFile[]; readonly index: number } | null>(null);
  const locale = currentLocale();
  const all = offlineTotals(offline.files);

  const filesOf = (root: OfflineRoot) => offline.files.filter((f) => f.rootId === root.nodeId);
  const whole = (list: readonly OfflineFile[]) => list.filter((f) => f.path !== null && f.bytes >= f.size);

  const openFile = (list: readonly OfflineFile[], file: OfflineFile): void => {
    const ready = whole(list);
    const index = ready.findIndex((f) => f.nodeId === file.nodeId);
    if (index >= 0) {
      setOpen({ list: ready, index });
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
          {t("mobile.offline.title")}
        </Text>
        {offline.busy ? <ActivityIndicator color={colors.accent} style={styles.barButton} /> : null}
      </View>

      <FlatList
        data={offline.roots}
        keyExtractor={(root) => root.nodeId}
        contentContainerStyle={styles.list}
        ListHeaderComponent={
          offline.roots.length > 0 ? (
            <Body faint>{t("mobile.offline.used", { size: formatBytes(all.bytes, locale), count: all.files })}</Body>
          ) : undefined
        }
        ListEmptyComponent={
          <View style={styles.empty}>
            <Icon name="download" size={40} color={colors.inkFaint} />
            <Body faint>{t("mobile.offline.empty")}</Body>
          </View>
        }
        renderItem={({ item: root }) => {
          const list = filesOf(root);
          const totals = offlineTotals(list);
          const failed = list.filter((f) => f.error !== null).length;
          const ready = totals.files > 0 && totals.whole === totals.files;
          const state =
            failed > 0
              ? t("mobile.offline.failed", { count: failed })
              : ready
                ? t("mobile.offline.ready", { size: formatBytes(totals.size, locale) })
                : totals.files === 0 && root.kind === "folder"
                  ? t("mobile.offline.reading")
                  : t("mobile.offline.fetching", {
                      done: formatBytes(totals.bytes, locale),
                      size: formatBytes(totals.size, locale),
                    });
          const percent = totals.size === 0 ? (ready ? 100 : 0) : Math.floor((totals.bytes / totals.size) * 100);
          return (
            <View style={[styles.card, { backgroundColor: colors.surface, borderColor: colors.line }]}>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={`${root.name}, ${state}`}
                onPress={() =>
                  root.kind === "file"
                    ? list[0] && openFile(list, list[0])
                    : setExpanded(expanded === root.nodeId ? null : root.nodeId)
                }
                style={styles.cardMain}
              >
                <Icon
                  name={
                    root.kind === "folder" ? "folder" : nodeIcon({ ...root, mediaType: list[0]?.mediaType ?? null })
                  }
                />
                <View style={styles.cardText}>
                  <Text numberOfLines={2} style={[styles.name, { color: colors.ink }]}>
                    {root.name}
                  </Text>
                  <Text style={[styles.state, { color: failed > 0 ? colors.danger : colors.inkFaint }]}>{state}</Text>
                  {!ready ? (
                    <View style={[styles.track, { backgroundColor: colors.line }]}>
                      <View style={[styles.fill, { width: `${percent}%`, backgroundColor: colors.accent }]} />
                    </View>
                  ) : null}
                </View>
              </Pressable>
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={`${t("mobile.offline.remove")}: ${root.name}`}
                onPress={() => void offline.remove(root.nodeId)}
                style={styles.remove}
              >
                <Icon name="trash" size={20} color={colors.inkSoft} />
              </Pressable>
              {expanded === root.nodeId ? (
                <View style={styles.files}>
                  {list.map((file) => {
                    const isWhole = file.path !== null && file.bytes >= file.size;
                    return (
                      <Pressable
                        key={file.nodeId}
                        accessibilityRole="button"
                        accessibilityLabel={file.name}
                        accessibilityState={{ disabled: !isWhole }}
                        disabled={!isWhole}
                        onPress={() => openFile(list, file)}
                        style={styles.file}
                      >
                        <Icon name={nodeIcon({ ...file, kind: "file" })} size={18} color={colors.inkSoft} />
                        <Text
                          numberOfLines={1}
                          style={[styles.fileName, { color: isWhole ? colors.ink : colors.inkFaint }]}
                        >
                          {file.name}
                        </Text>
                        <Text style={[styles.fileSize, { color: colors.inkFaint }]}>
                          {formatBytes(file.size, locale)}
                        </Text>
                      </Pressable>
                    );
                  })}
                </View>
              ) : null}
            </View>
          );
        }}
      />

      {open ? (
        <FileViewer
          files={open.list.map((f) => ({
            id: f.nodeId,
            name: f.name,
            mediaType: f.mediaType,
            size: f.size,
            version: f.version,
          }))}
          index={open.index}
          onIndex={(index) => setOpen({ ...open, index })}
          onClose={() => setOpen(null)}
          onDetails={() => undefined}
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
  list: { flexGrow: 1, paddingHorizontal: space.lg, paddingBottom: space.xl, gap: space.md },
  empty: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.md, padding: space.xxl },
  card: { borderRadius: radius.md, borderWidth: 1, flexDirection: "row", flexWrap: "wrap", alignItems: "center" },
  cardMain: { flex: 1, flexDirection: "row", alignItems: "center", gap: space.md, padding: space.lg },
  cardText: { flex: 1, gap: 4 },
  name: { fontSize: type.body, fontWeight: "600" },
  state: { fontSize: type.small },
  track: { height: 4, borderRadius: 2, overflow: "hidden", marginTop: 2 },
  fill: { height: 4 },
  remove: { width: 48, height: 48, alignItems: "center", justifyContent: "center", marginRight: space.xs },
  files: { width: "100%", paddingHorizontal: space.lg, paddingBottom: space.md, gap: space.xs },
  file: { minHeight: 40, flexDirection: "row", alignItems: "center", gap: space.sm },
  fileName: { flex: 1, fontSize: type.small },
  fileSize: { fontSize: type.small },
});
