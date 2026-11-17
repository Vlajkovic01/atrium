import {
  fileCategory,
  fileOrderKey,
  fileOrderLabelKey,
  fileSorts,
  formatBytes,
  formatDateTime,
  homeLibrary,
  type FileCategory,
  type FileOrder,
  type LibraryInfo,
  type NodeInfo,
} from "@atrium/shared";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  FlatList,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { Icon, type IconName } from "../components/Icon.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { Body, Button, ErrorMessage, Notice } from "../components/ui.tsx";
import { currentLocale } from "../i18n.ts";
import { radius, space, type, useTheme } from "../theme.ts";
import { setFileOrder, useFileOrder } from "./order.ts";

const PAGE = 200;
/** While a folder just added to the server is read for the first time, its files appear as they are found. */
const READING_POLL = 3000;

const categoryIcons: Record<FileCategory, IconName> = {
  image: "image",
  video: "video",
  audio: "music",
  pdf: "book",
  text: "text",
  document: "text",
  archive: "archive",
  other: "file",
};

const orders: readonly FileOrder[] = fileSorts.flatMap((sort) => [
  { sort, descending: false },
  { sort, descending: true },
]);

/**
 * One folder (docs/UX.md, Mobile, Files): its contents in the chosen
 * order, the way back up, and the other libraries at the top level. A
 * folder opens another screen of the same kind, so the system's back
 * button goes up one folder. Without `folderId` it shows where the
 * person's files start.
 */
export function FolderScreen({ folderId: given }: { readonly folderId?: string }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const { server, files } = useActiveServer();
  const order = useFileOrder();
  const [sorting, setSorting] = useState(false);
  const [details, setDetails] = useState<NodeInfo | null>(null);

  const libraries = useQuery({
    queryKey: [server.id, "libraries"],
    queryFn: files.libraries,
    refetchInterval: (query) => (query.state.data?.some((l) => l.firstReading) ? READING_POLL : false),
  });
  const home = libraries.data ? homeLibrary(libraries.data) : undefined;
  const folderId = given ?? home?.rootNodeId;

  const folder = useQuery({
    queryKey: [server.id, "node", folderId],
    enabled: folderId !== undefined,
    queryFn: () => files.node(folderId!),
  });
  const path = useQuery({
    queryKey: [server.id, "path", folderId],
    enabled: folderId !== undefined,
    queryFn: () => files.path(folderId!),
  });
  const library = libraries.data?.find((l) => l.id === folder.data?.libraryId);
  const reading = library?.firstReading ?? false;
  const children = useInfiniteQuery({
    queryKey: [server.id, "children", folderId, order.sort, order.descending],
    enabled: folderId !== undefined,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => files.children(folderId!, { ...order, limit: PAGE, cursor: pageParam }),
    getNextPageParam: (page) => page.nextCursor ?? null,
    refetchInterval: () => (reading ? READING_POLL : false),
  });
  const items = useMemo(() => children.data?.pages.flatMap((p) => p.items) ?? [], [children.data]);
  const [pulled, setPulled] = useState(false);

  const rootName = (l: LibraryInfo): string => (l.kind === "personal" ? t("files.myFiles") : l.name);
  const nameOf = (node: NodeInfo): string =>
    library && node.id === library.rootNodeId ? rootName(library) : node.name;
  const atRoot = library !== undefined && folderId === library.rootNodeId;
  const title = folder.data ? nameOf(folder.data) : t("files.title");
  const locale = currentLocale();
  // The folders above this one, from the library's top down; the folder itself is the screen's title.
  const above = (path.data ?? []).filter((node) => node.id !== folderId);

  const openFolder = (id: string): void => {
    router.push({ pathname: "/files/[folderId]", params: { folderId: id } });
  };

  /** Back to where the person's files start, closing every folder opened since. */
  const goHome = (): void => {
    if (router.canDismiss()) {
      router.dismissAll();
    } else {
      router.replace("/files");
    }
  };

  /**
   * Up to a folder above this one. Folders are opened one over another,
   * so going up closes that many screens and Back keeps meaning "one
   * folder up". A folder reached another way (a link into the app) has
   * nothing under it to return to, and is replaced instead.
   */
  const goUp = (levels: number, id: string): void => {
    if (id === home?.rootNodeId) {
      goHome();
    } else if (router.canDismiss()) {
      router.dismiss(levels);
    } else {
      router.replace({ pathname: "/files/[folderId]", params: { folderId: id } });
    }
  };

  const refresh = async (): Promise<void> => {
    setPulled(true);
    try {
      await Promise.all([libraries.refetch(), folder.refetch(), path.refetch(), children.refetch()]);
    } finally {
      setPulled(false);
    }
  };

  const error = libraries.error ?? folder.error ?? children.error;
  const loading = libraries.isPending || (folderId !== undefined && (folder.isPending || children.isPending));
  const total = Number(children.data?.pages[0]?.total ?? items.length);

  const header = (
    <View style={styles.listHead}>
      {/* The other places files live in: offered where the person's files start and at each library's top. */}
      {(given === undefined || atRoot) && (libraries.data?.length ?? 0) > 1 ? (
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          accessibilityLabel={t("files.libraries")}
          contentContainerStyle={styles.chips}
        >
          {libraries.data!.map((l) => {
            const current = l.id === library?.id;
            return (
              <Pressable
                key={l.id}
                accessibilityRole="button"
                accessibilityState={{ selected: current }}
                onPress={() => {
                  if (current) {
                    return;
                  }

                  if (l.id === home?.id) {
                    goHome();
                  } else if (given === undefined) {
                    openFolder(l.rootNodeId);
                  } else {
                    router.replace({ pathname: "/files/[folderId]", params: { folderId: l.rootNodeId } });
                  }
                }}
                style={[
                  styles.chip,
                  {
                    backgroundColor: current ? colors.accentWash : colors.surface,
                    borderColor: current ? colors.accentWash : colors.lineStrong,
                  },
                ]}
              >
                <Icon
                  name={l.kind === "personal" ? "user" : l.readOnly ? "lock" : "folder"}
                  size={18}
                  color={current ? colors.accentWashInk : colors.inkSoft}
                />
                <Text style={[styles.chipText, { color: current ? colors.accentWashInk : colors.ink }]}>
                  {rootName(l)}
                </Text>
              </Pressable>
            );
          })}
        </ScrollView>
      ) : null}

      {/* Where this folder is: each folder above it, one touch away. */}
      {above.length > 0 ? (
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          accessibilityLabel={t("files.location")}
          contentContainerStyle={styles.crumbs}
        >
          {above.map((node, index) => (
            <View key={node.id} style={styles.crumb}>
              {index > 0 ? <Icon name="chevronRight" size={14} color={colors.inkFaint} /> : null}
              <Pressable
                accessibilityRole="link"
                hitSlop={space.sm}
                onPress={() => goUp(above.length - index, node.id)}
              >
                <Text style={[styles.crumbText, { color: colors.accent }]}>{nameOf(node)}</Text>
              </Pressable>
            </View>
          ))}
        </ScrollView>
      ) : null}

      {reading ? <Notice kind="info">{t("files.firstReading")}</Notice> : null}

      {/* Saying why nothing can be changed here, and who can change that (D-040). */}
      {folder.data && !folder.data.canWrite && library?.mode === "external" ? (
        <Notice kind="info">{library.changesAllowed ? t("files.readOnlyNotWriter") : t("files.readOnlyFolder")}</Notice>
      ) : null}
    </View>
  );

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={[styles.flex, { backgroundColor: colors.paper }]}>
      <View style={styles.bar}>
        {given !== undefined ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common.back")}
            onPress={() => (router.canGoBack() ? router.back() : router.replace("/files"))}
            style={styles.barButton}
          >
            <Icon name="chevronLeft" />
          </Pressable>
        ) : null}
        <Text accessibilityRole="header" numberOfLines={1} style={[styles.barTitle, { color: colors.ink }]}>
          {title}
        </Text>
        {folder.data && !folder.data.canWrite ? (
          <View accessible accessibilityLabel={t("files.readOnly")} style={styles.barButton}>
            <Icon name="lock" size={20} color={colors.inkFaint} />
          </View>
        ) : null}
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={`${t("files.sortBy")}: ${t(fileOrderLabelKey(order))}`}
          onPress={() => setSorting(true)}
          style={styles.barButton}
        >
          <Icon name="sort" />
        </Pressable>
      </View>

      {libraries.data && libraries.data.length === 0 ? (
        <View style={styles.message}>
          <Body>{t("files.noLibrary")}</Body>
        </View>
      ) : error && items.length === 0 ? (
        <View style={styles.message}>
          <ErrorMessage error={error} />
          <Button onPress={() => void refresh()} busy={pulled}>
            {t("common.retry")}
          </Button>
        </View>
      ) : loading ? (
        <View style={styles.message}>
          <ActivityIndicator color={colors.accent} accessibilityLabel={t("common.loading")} />
        </View>
      ) : (
        <FlatList
          data={items}
          keyExtractor={(node) => node.id}
          accessibilityLabel={t("files.contents")}
          contentContainerStyle={styles.list}
          ListHeaderComponent={header}
          ListEmptyComponent={
            <View style={styles.empty}>
              <Icon name="folder" size={40} color={colors.inkFaint} />
              <Body faint>{t("files.empty")}</Body>
            </View>
          }
          ListFooterComponent={
            items.length > 0 ? (
              <View style={styles.foot}>
                {children.isFetchingNextPage ? <ActivityIndicator color={colors.accent} /> : null}
                {children.error ? <ErrorMessage error={children.error} /> : null}
                <Text style={[styles.count, { color: colors.inkFaint }]}>{t("files.count", { count: total })}</Text>
              </View>
            ) : undefined
          }
          refreshControl={
            <RefreshControl
              refreshing={pulled}
              onRefresh={() => void refresh()}
              colors={[colors.accent]}
              tintColor={colors.accent}
            />
          }
          onEndReachedThreshold={0.6}
          onEndReached={() => {
            if (children.hasNextPage && !children.isFetchingNextPage) {
              void children.fetchNextPage();
            }
          }}
          renderItem={({ item }) => (
            <Row
              node={item}
              detail={
                item.kind === "folder"
                  ? formatDateTime(item.modifiedAt, locale)
                  : `${formatBytes(Number(item.size), locale)} · ${formatDateTime(item.modifiedAt, locale)}`
              }
              onPress={() => (item.kind === "folder" ? openFolder(item.id) : setDetails(item))}
            />
          )}
        />
      )}

      {sorting ? (
        <Sheet title={t("files.sortBy")} onClose={() => setSorting(false)}>
          {orders.map((choice) => (
            <SheetOption
              key={fileOrderKey(choice)}
              label={t(fileOrderLabelKey(choice))}
              selected={fileOrderKey(choice) === fileOrderKey(order)}
              onPress={() => {
                setFileOrder(choice);
                setSorting(false);
              }}
            />
          ))}
        </Sheet>
      ) : null}

      {details ? (
        <Sheet title={details.name} onClose={() => setDetails(null)}>
          <View style={styles.facts}>
            <Fact label={t("files.kind")} value={t(`mobile.files.kinds.${fileCategory(details)}`)} />
            <Fact label={t("files.size")} value={formatBytes(Number(details.size), locale)} />
            <Fact label={t("files.modified")} value={formatDateTime(details.modifiedAt, locale)} />
            <Fact label={t("files.added")} value={formatDateTime(details.createdAt, locale)} />
          </View>
        </Sheet>
      ) : null}
    </SafeAreaView>
  );
}

function Row({
  node,
  detail,
  onPress,
}: {
  readonly node: NodeInfo;
  readonly detail: string;
  readonly onPress: () => void;
}) {
  const { colors } = useTheme();
  const isFolder = node.kind === "folder";
  return (
    <Pressable
      onPress={onPress}
      accessibilityRole="button"
      accessibilityLabel={`${node.name}, ${detail}`}
      style={({ pressed }) => [styles.row, { backgroundColor: pressed ? colors.surfaceSunk : "transparent" }]}
    >
      <View
        style={[
          styles.tile,
          { backgroundColor: isFolder ? colors.accentWash : colors.surface, borderColor: colors.line },
        ]}
      >
        <Icon
          name={isFolder ? "folder" : categoryIcons[fileCategory(node)]}
          color={isFolder ? colors.accentWashInk : colors.inkSoft}
        />
      </View>
      <View style={styles.rowText}>
        <Text numberOfLines={2} style={[styles.name, { color: colors.ink }]}>
          {node.name}
        </Text>
        <Text numberOfLines={1} style={[styles.detail, { color: colors.inkFaint }]}>
          {detail}
        </Text>
      </View>
      {isFolder ? <Icon name="chevronRight" size={20} color={colors.inkFaint} /> : null}
    </Pressable>
  );
}

function Fact({ label, value }: { readonly label: string; readonly value: string }) {
  const { colors } = useTheme();
  return (
    <View style={[styles.fact, { borderColor: colors.line }]}>
      <Text style={[styles.detail, { color: colors.inkFaint }]}>{label}</Text>
      <Text style={[styles.factValue, { color: colors.ink }]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { minHeight: 56, flexDirection: "row", alignItems: "center", paddingHorizontal: space.sm, gap: space.xs },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  barTitle: { flex: 1, fontSize: type.heading, fontWeight: "700", paddingHorizontal: space.sm },
  message: { flex: 1, padding: space.xl, gap: space.lg, justifyContent: "center" },
  list: { flexGrow: 1, paddingBottom: space.xl },
  listHead: { gap: space.md, paddingHorizontal: space.lg, paddingBottom: space.sm },
  chips: { gap: space.sm, paddingVertical: space.xs },
  chip: {
    minHeight: 40,
    borderRadius: radius.pill,
    borderWidth: 1,
    paddingHorizontal: space.lg,
    flexDirection: "row",
    alignItems: "center",
    gap: space.sm,
  },
  chipText: { fontSize: type.small, fontWeight: "600" },
  crumbs: { alignItems: "center", gap: space.xs, paddingVertical: space.xs },
  crumb: { flexDirection: "row", alignItems: "center", gap: space.xs },
  crumbText: { fontSize: type.small },
  row: {
    minHeight: 64,
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
  empty: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.md, padding: space.xxl },
  foot: { alignItems: "center", gap: space.md, padding: space.lg },
  count: { fontSize: type.small },
  facts: { paddingHorizontal: space.md, paddingBottom: space.md },
  fact: { paddingVertical: space.md, borderBottomWidth: StyleSheet.hairlineWidth, gap: 2 },
  factValue: { fontSize: type.body },
});
