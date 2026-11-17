import type { NodeInfo } from "@atrium/shared";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, FlatList, Modal, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Body, Button, ErrorMessage } from "../components/ui.tsx";
import { radius, space, type, useTheme } from "../theme.ts";

/**
 * Chooses a folder to move, copy or restore into: browse down from the
 * library's top, go back up through the path, confirm the folder shown.
 * It stays inside one library, as the web app's does.
 */
export function FolderPicker({
  title,
  confirm,
  rootName,
  startId,
  excluded,
  busy,
  error,
  onPick,
  onClose,
}: {
  readonly title: string;
  readonly confirm: string;
  /** What the library's top folder is called. */
  readonly rootName: string;
  readonly startId: string;
  /** The items being moved: a folder cannot go into itself. */
  readonly excluded: ReadonlySet<string>;
  readonly busy: boolean;
  readonly error: unknown;
  readonly onPick: (folderId: string) => void;
  readonly onClose: () => void;
}) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { server, files } = useActiveServer();
  const [folderId, setFolderId] = useState(startId);

  const path = useQuery({ queryKey: [server.id, "path", folderId], queryFn: () => files.path(folderId) });
  const current = useQuery({ queryKey: [server.id, "node", folderId], queryFn: () => files.node(folderId) });
  const folders = useQuery({
    queryKey: [server.id, "children", folderId, "folders"],
    queryFn: () => files.children(folderId, { sort: "name", descending: false, kind: "folder", limit: 1000 }),
  });

  const above = (path.data ?? []).filter((node) => node.id !== folderId);
  const nameOf = (node: NodeInfo): string => (node.parentId === null ? rootName : node.name);
  const failure = folders.error ?? current.error;

  return (
    <Modal animationType="slide" onRequestClose={onClose}>
      <SafeAreaView style={[styles.flex, { backgroundColor: colors.paper }]}>
        <View style={styles.bar}>
          <Text accessibilityRole="header" numberOfLines={2} style={[styles.title, { color: colors.ink }]}>
            {title}
          </Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common.cancel")}
            onPress={onClose}
            style={styles.barButton}
          >
            <Icon name="close" />
          </Pressable>
        </View>

        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          accessibilityLabel={t("files.location")}
          style={styles.crumbsRow}
          contentContainerStyle={styles.crumbs}
        >
          {above.map((node) => (
            <View key={node.id} style={styles.crumb}>
              <Pressable accessibilityRole="link" hitSlop={space.sm} onPress={() => setFolderId(node.id)}>
                <Text style={[styles.crumbText, { color: colors.accent }]}>{nameOf(node)}</Text>
              </Pressable>
              <Icon name="chevronRight" size={14} color={colors.inkFaint} />
            </View>
          ))}
          <Text style={[styles.crumbText, { color: colors.ink, fontWeight: "600" }]}>
            {current.data ? nameOf(current.data) : ""}
          </Text>
        </ScrollView>

        {failure ? (
          <View style={styles.message}>
            <ErrorMessage error={failure} />
          </View>
        ) : folders.isPending ? (
          <View style={styles.message}>
            <ActivityIndicator color={colors.accent} accessibilityLabel={t("common.loading")} />
          </View>
        ) : (
          <FlatList
            data={folders.data?.items ?? []}
            keyExtractor={(node) => node.id}
            contentContainerStyle={styles.list}
            ListEmptyComponent={
              <View style={styles.message}>
                <Body faint>{t("files.noSubfolders")}</Body>
              </View>
            }
            renderItem={({ item }) => {
              const blocked = excluded.has(item.id);
              return (
                <Pressable
                  disabled={blocked}
                  accessibilityRole="button"
                  accessibilityState={{ disabled: blocked }}
                  onPress={() => setFolderId(item.id)}
                  style={({ pressed }) => [
                    styles.row,
                    { opacity: blocked ? 0.45 : 1, backgroundColor: pressed ? colors.surfaceSunk : "transparent" },
                  ]}
                >
                  <View style={[styles.tile, { backgroundColor: colors.accentWash }]}>
                    <Icon name="folder" color={colors.accentWashInk} />
                  </View>
                  <Text numberOfLines={2} style={[styles.name, { color: colors.ink }]}>
                    {item.name}
                  </Text>
                  {blocked ? null : <Icon name="chevronRight" size={20} color={colors.inkFaint} />}
                </Pressable>
              );
            }}
          />
        )}

        <View style={[styles.foot, { borderColor: colors.line, backgroundColor: colors.surface }]}>
          <ErrorMessage error={error} />
          <Button variant="primary" busy={busy} disabled={!current.data?.canWrite} onPress={() => onPick(folderId)}>
            {confirm}
          </Button>
        </View>
      </SafeAreaView>
    </Modal>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { minHeight: 56, flexDirection: "row", alignItems: "center", paddingLeft: space.lg, paddingRight: space.sm },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  title: { flex: 1, fontSize: type.heading, fontWeight: "700" },
  crumbsRow: { flexGrow: 0 },
  crumbs: { alignItems: "center", gap: space.xs, paddingHorizontal: space.lg, paddingVertical: space.sm },
  crumb: { flexDirection: "row", alignItems: "center", gap: space.xs },
  crumbText: { fontSize: type.small },
  message: { flex: 1, padding: space.xl, justifyContent: "center", alignItems: "center" },
  list: { flexGrow: 1, paddingBottom: space.lg },
  row: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
  },
  tile: { width: 44, height: 44, borderRadius: radius.sm, alignItems: "center", justifyContent: "center" },
  name: { flex: 1, fontSize: type.body, fontWeight: "500" },
  foot: { padding: space.lg, gap: space.md, borderTopWidth: 1 },
});
