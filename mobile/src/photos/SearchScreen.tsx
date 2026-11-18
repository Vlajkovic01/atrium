import {
  formatBytes,
  formatPhotoDay,
  parseSearch,
  searchResultAsPhoto,
  searchTypes,
  type NodeInfo,
  type PhotoItem,
  type SearchType,
} from "@atrium/shared";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, ScrollView, StyleSheet, Text, TextInput, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Body, ErrorMessage } from "../components/ui.tsx";
import { FileViewer } from "../files/FileViewer.tsx";
import { nodeIcon } from "../files/icons.ts";
import { currentLocale } from "../i18n.ts";
import { openFolderFromOverlay } from "../navigation.ts";
import { radius, space, type, useTheme } from "../theme.ts";
import { PhotoGrid, PhotoInfo, photoStyles } from "./PhotoGrid.tsx";

const PAGE = 100;
/** Searching waits this long after the last key, so each word is not sent half typed. */
const TYPING_MS = 300;

/**
 * One field for names, kinds and dates (docs/UX.md, Mobile, Photos and
 * Files): "beach 2024" finds names with "beach" from 2024, "May 2024" or
 * "maj 2024" a month. A kind narrows it. Photos and videos show as a grid;
 * other files open in the viewer, folders in Files.
 */
export function SearchScreen() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const { server, search } = useActiveServer();
  const [text, setText] = useState("");
  const [query, setQuery] = useState("");
  const [kind, setKind] = useState<SearchType | null>(null);
  const [openPhoto, setOpenPhoto] = useState<string | null>(null);
  const [openFile, setOpenFile] = useState<string | null>(null);
  const [info, setInfo] = useState<PhotoItem | null>(null);
  const locale = currentLocale();

  useEffect(() => {
    const timer = setTimeout(() => setQuery(text.trim()), TYPING_MS);
    return () => clearTimeout(timer);
  }, [text]);

  const parsed = useMemo(() => parseSearch(query, locale), [query, locale]);
  const active = query !== "" || kind !== null;
  const results = useInfiniteQuery({
    queryKey: [server.id, "search", parsed, kind],
    enabled: active,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      search.search({ words: parsed.words, range: parsed.range, type: kind, limit: PAGE, cursor: pageParam }),
    getNextPageParam: (page) => page.nextCursor ?? null,
  });
  const all = useMemo(() => results.data?.pages.flatMap((p) => p.items) ?? [], [results.data]);
  const photos = useMemo(() => all.map(searchResultAsPhoto).filter((p): p is PhotoItem => p !== null), [all]);
  const others = useMemo(() => all.filter((r) => searchResultAsPhoto(r) === null).map((r) => r.node), [all]);
  const files = others.filter((node) => node.kind === "file");
  const photoIndex = openPhoto === null ? -1 : photos.findIndex((p) => p.id === openPhoto);
  const fileIndex = openFile === null ? -1 : files.findIndex((f) => f.id === openFile);

  // The last day of a range, which ends at the start of the next day.
  const rangeLabel = parsed.range
    ? `${formatPhotoDay(parsed.range.from.slice(0, 10), locale)} – ${formatPhotoDay(
        new Date(Date.parse(parsed.range.to) - 86_400_000).toISOString().slice(0, 10),
        locale,
      )}`
    : null;

  const openOther = (node: NodeInfo): void => {
    if (node.kind === "folder") {
      openFolderFromOverlay(router, node.id);
    } else {
      setOpenFile(node.id);
    }
  };

  const header = (
    <View style={styles.head}>
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        accessibilityLabel={t("search.type")}
        contentContainerStyle={styles.chips}
      >
        {searchTypes.map((value) => {
          const on = kind === value;
          return (
            <Pressable
              key={value}
              accessibilityRole="button"
              accessibilityState={{ selected: on }}
              onPress={() => setKind(on ? null : value)}
              style={[
                styles.chip,
                {
                  backgroundColor: on ? colors.accentWash : colors.surface,
                  borderColor: on ? colors.accentWash : colors.lineStrong,
                },
              ]}
            >
              <Text style={[styles.chipText, { color: on ? colors.accentWashInk : colors.ink }]}>
                {t(`search.types.${value}`)}
              </Text>
            </Pressable>
          );
        })}
      </ScrollView>
      {rangeLabel ? (
        <View style={styles.range}>
          <Icon name="calendar" size={18} color={colors.accent} />
          <Text style={[styles.rangeText, { color: colors.inkSoft }]}>{rangeLabel}</Text>
        </View>
      ) : null}
      <ErrorMessage error={results.error} />
      {!active ? <Body faint>{t("search.hint")}</Body> : null}
      {active && results.isSuccess && all.length === 0 ? <Body faint>{t("search.nothing")}</Body> : null}
      {others.map((node) => (
        <Pressable
          key={node.id}
          accessibilityRole="button"
          accessibilityLabel={node.name}
          onPress={() => openOther(node)}
          style={({ pressed }) => [styles.other, { backgroundColor: pressed ? colors.surfaceSunk : "transparent" }]}
        >
          <View style={[styles.tile, { backgroundColor: node.kind === "folder" ? colors.accentWash : colors.surface }]}>
            <Icon name={nodeIcon(node)} color={node.kind === "folder" ? colors.accentWashInk : colors.inkSoft} />
          </View>
          <View style={styles.otherText}>
            <Text numberOfLines={2} style={[styles.otherName, { color: colors.ink }]}>
              {node.name}
            </Text>
            {node.kind === "file" ? (
              <Text style={[styles.otherDetail, { color: colors.inkFaint }]}>
                {formatBytes(Number(node.size), locale)}
              </Text>
            ) : null}
          </View>
          {node.kind === "folder" ? <Icon name="chevronRight" size={20} color={colors.inkFaint} /> : null}
        </Pressable>
      ))}
    </View>
  );

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={[styles.flex, { backgroundColor: colors.paper }]}>
      <View style={[photoStyles.bar, styles.bar]}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("common.back")}
          onPress={() => (router.canGoBack() ? router.back() : router.replace("/photos"))}
          style={photoStyles.barButton}
        >
          <Icon name="chevronLeft" />
        </Pressable>
        <View style={[styles.field, { backgroundColor: colors.surface, borderColor: colors.lineStrong }]}>
          <Icon name="search" size={18} color={colors.inkFaint} />
          <TextInput
            value={text}
            onChangeText={setText}
            autoFocus
            accessibilityLabel={t("search.label")}
            placeholder={t("search.placeholder")}
            placeholderTextColor={colors.inkFaint}
            returnKeyType="search"
            autoCorrect={false}
            style={[styles.input, { color: colors.ink }]}
          />
          {text ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("common.close")}
              onPress={() => setText("")}
              hitSlop={space.sm}
            >
              <Icon name="close" size={18} color={colors.inkFaint} />
            </Pressable>
          ) : null}
        </View>
      </View>

      <PhotoGrid
        items={active ? photos : []}
        pending={active && results.isPending}
        error={null}
        loadingMore={results.isFetchingNextPage}
        empty={null}
        header={header}
        onPress={(item) => setOpenPhoto(item.id)}
        onEndReached={() => {
          if (results.hasNextPage && !results.isFetchingNextPage) {
            void results.fetchNextPage();
          }
        }}
        onRefresh={() => results.refetch()}
      />

      {photoIndex >= 0 ? (
        <FileViewer
          files={photos}
          index={photoIndex}
          onIndex={(index) => setOpenPhoto(photos[index]?.id ?? null)}
          onClose={() => setOpenPhoto(null)}
          onDetails={setInfo}
        />
      ) : null}
      {fileIndex >= 0 ? (
        <FileViewer
          files={files}
          index={fileIndex}
          onIndex={(index) => setOpenFile(files[index]?.id ?? null)}
          onClose={() => setOpenFile(null)}
          onDetails={() => undefined}
        />
      ) : null}
      {info ? <PhotoInfo item={info} onClose={() => setInfo(null)} /> : null}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { paddingLeft: space.sm, paddingRight: space.lg, gap: space.xs },
  field: {
    flex: 1,
    minHeight: 46,
    borderRadius: radius.pill,
    borderWidth: 1,
    paddingHorizontal: space.md,
    flexDirection: "row",
    alignItems: "center",
    gap: space.sm,
  },
  input: { flex: 1, fontSize: type.body, paddingVertical: space.sm },
  head: { gap: space.sm, paddingHorizontal: space.sm, paddingBottom: space.sm },
  chips: { gap: space.sm, paddingVertical: space.xs },
  chip: {
    minHeight: 38,
    borderRadius: radius.pill,
    borderWidth: 1,
    paddingHorizontal: space.lg,
    justifyContent: "center",
  },
  chipText: { fontSize: type.small, fontWeight: "600" },
  range: { flexDirection: "row", alignItems: "center", gap: space.sm },
  rangeText: { fontSize: type.small },
  other: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
    paddingVertical: space.sm,
    borderRadius: radius.sm,
  },
  tile: { width: 44, height: 44, borderRadius: radius.sm, alignItems: "center", justifyContent: "center" },
  otherText: { flex: 1, gap: 2 },
  otherName: { fontSize: type.body, fontWeight: "500" },
  otherDetail: { fontSize: type.small },
});
