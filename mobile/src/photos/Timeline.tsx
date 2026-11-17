import {
  bucketRange,
  formatBytes,
  formatTakenAt,
  formatDuration,
  formatPhotoDay,
  groupByDay,
  justify,
  type PhotoItem,
  type Placed,
} from "@atrium/shared";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  FlatList,
  Image,
  Pressable,
  RefreshControl,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
  type GestureResponderEvent,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { Body, Button, ErrorMessage } from "../components/ui.tsx";
import { FileViewer } from "../files/FileViewer.tsx";
import { absoluteUrl } from "../files/local";
import { currentLocale } from "../i18n.ts";
import { space, type, useTheme } from "../theme.ts";
import { densities, stepDensity, useDensity } from "./density.ts";

const PAGE = 200;
const GAP = 3;
const SIDE = space.xs;
/** How far two fingers must spread or close, as a share of where they started, to change the size of the pictures. */
const PINCH_STEP = 0.35;

type Row =
  | { readonly key: string; readonly kind: "day"; readonly day: string }
  | { readonly key: string; readonly kind: "photos"; readonly cells: readonly Placed<PhotoItem>[] };

/**
 * The photo timeline (docs/UX.md, Mobile, Photos): photos and videos
 * newest first, by day, in justified rows. Two fingers spread or close to
 * change the size of the pictures, as the size button does; a month can
 * be jumped to; touching a picture opens it.
 */
export function Timeline() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { width } = useWindowDimensions();
  const { server, photos } = useActiveServer();
  const density = useDensity();
  const [start, setStart] = useState<{ readonly year: number; readonly month: number } | null>(null);
  const [jumping, setJumping] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [info, setInfo] = useState<PhotoItem | null>(null);
  const [pulled, setPulled] = useState(false);
  const pinch = useRef<number | null>(null);
  const locale = currentLocale();

  const buckets = useQuery({ queryKey: [server.id, "timeline-buckets"], queryFn: photos.buckets });
  const to = start ? bucketRange(start.year, start.month).to : undefined;
  const timeline = useInfiniteQuery({
    queryKey: [server.id, "timeline", to ?? "newest"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => photos.timeline({ limit: PAGE, cursor: pageParam, ...(to ? { to } : {}) }),
    getNextPageParam: (page) => page.nextCursor ?? null,
  });
  const items = useMemo(() => timeline.data?.pages.flatMap((p) => p.items) ?? [], [timeline.data]);

  const rows = useMemo(() => {
    const out: Row[] = [];
    for (const day of groupByDay(items)) {
      out.push({ key: `d${day.day}`, kind: "day", day: day.day });
      justify(day.items, width - SIDE * 2, densities[density] ?? densities[1], GAP).forEach((cells, index) =>
        out.push({ key: `r${day.day}:${index}`, kind: "photos", cells }),
      );
    }

    return out;
  }, [items, width, density]);

  const viewing = open === null ? -1 : items.findIndex((item) => item.id === open);

  const refresh = async (): Promise<void> => {
    setPulled(true);
    try {
      await Promise.all([buckets.refetch(), timeline.refetch()]);
    } finally {
      setPulled(false);
    }
  };

  // Two fingers: compare their distance with where they started.
  const distance = (event: GestureResponderEvent): number | null => {
    const [a, b] = event.nativeEvent.touches;
    return a && b ? Math.hypot(a.pageX - b.pageX, a.pageY - b.pageY) : null;
  };
  const onTouchMove = (event: GestureResponderEvent): void => {
    const now = distance(event);
    if (now === null) {
      pinch.current = null;
      return;
    }

    if (pinch.current === null) {
      pinch.current = now;
      return;
    }

    const change = now / pinch.current - 1;
    if (Math.abs(change) >= PINCH_STEP) {
      // Spreading makes the pictures larger, closing makes them smaller.
      stepDensity(change > 0 ? 1 : -1);
      pinch.current = now;
    }
  };

  const years = useMemo(() => {
    const byYear = new Map<number, { month: number; count: number }[]>();
    for (const b of buckets.data ?? []) {
      const list = byYear.get(Number(b.year)) ?? [];
      list.push({ month: Number(b.month), count: Number(b.count) });
      byYear.set(Number(b.year), list);
    }

    return [...byYear.entries()];
  }, [buckets.data]);

  const monthName = (year: number, month: number): string =>
    new Intl.DateTimeFormat(locale, { month: "long", year: "numeric", timeZone: "UTC" }).format(
      new Date(Date.UTC(year, month - 1, 15)),
    );

  const body = () => {
    if (timeline.error && items.length === 0) {
      return (
        <View style={styles.message}>
          <ErrorMessage error={timeline.error} />
          <Button onPress={() => void refresh()} busy={pulled}>
            {t("common.retry")}
          </Button>
        </View>
      );
    }

    if (timeline.isPending) {
      return (
        <View style={styles.message}>
          <ActivityIndicator color={colors.accent} accessibilityLabel={t("common.loading")} />
        </View>
      );
    }

    return (
      <FlatList
        data={rows}
        keyExtractor={(row) => row.key}
        contentContainerStyle={styles.list}
        onTouchMove={onTouchMove}
        onTouchEnd={() => (pinch.current = null)}
        refreshControl={
          <RefreshControl
            refreshing={pulled}
            onRefresh={() => void refresh()}
            colors={[colors.accent]}
            tintColor={colors.accent}
          />
        }
        onEndReachedThreshold={1}
        onEndReached={() => {
          if (timeline.hasNextPage && !timeline.isFetchingNextPage) {
            void timeline.fetchNextPage();
          }
        }}
        ListHeaderComponent={
          start ? (
            <View style={styles.head}>
              <Button variant="ghost" onPress={() => setStart(null)}>
                {t("photos.backToNewest")}
              </Button>
            </View>
          ) : undefined
        }
        ListEmptyComponent={
          <View style={styles.empty}>
            <Icon name="image" size={40} color={colors.inkFaint} />
            <Body faint>{t("photos.empty")}</Body>
          </View>
        }
        ListFooterComponent={
          timeline.isFetchingNextPage ? (
            <ActivityIndicator style={styles.more} color={colors.accent} />
          ) : timeline.error ? (
            <ErrorMessage error={timeline.error} />
          ) : undefined
        }
        renderItem={({ item: row }) =>
          row.kind === "day" ? (
            <Text accessibilityRole="header" style={[styles.day, { color: colors.ink }]}>
              {formatPhotoDay(row.day, locale)}
            </Text>
          ) : (
            <View style={styles.row}>
              {row.cells.map((cell) => (
                <Pressable
                  key={cell.item.id}
                  accessibilityRole="button"
                  accessibilityLabel={cell.item.name}
                  onPress={() => setOpen(cell.item.id)}
                  style={{ width: cell.width, height: cell.height, backgroundColor: colors.surfaceSunk }}
                >
                  <Image
                    source={{ uri: absoluteUrl(server.url, cell.item.thumbnailUrl) }}
                    style={styles.fill}
                    resizeMode="cover"
                  />
                  {cell.item.kind === "video" ? (
                    <View style={styles.badge}>
                      <Icon name="video" size={14} color="#fff" />
                      {cell.item.durationMs != null ? (
                        <Text style={styles.badgeText}>{formatDuration(Number(cell.item.durationMs))}</Text>
                      ) : null}
                    </View>
                  ) : null}
                </Pressable>
              ))}
            </View>
          )
        }
      />
    );
  };

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={[styles.flex, { backgroundColor: colors.paper }]}>
      <View style={styles.bar}>
        <Text accessibilityRole="header" numberOfLines={1} style={[styles.barTitle, { color: colors.ink }]}>
          {t("photos.title")}
        </Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("mobile.photos.smaller")}
          disabled={density === 0}
          onPress={() => stepDensity(-1)}
          style={[styles.barButton, density === 0 && styles.off]}
        >
          <Icon name="grid" />
        </Pressable>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("mobile.photos.larger")}
          disabled={density === densities.length - 1}
          onPress={() => stepDensity(1)}
          style={[styles.barButton, density === densities.length - 1 && styles.off]}
        >
          <Icon name="image" />
        </Pressable>
        {years.length > 0 ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("photos.jumpTo")}
            onPress={() => setJumping(true)}
            style={styles.barButton}
          >
            <Icon name="calendar" />
          </Pressable>
        ) : null}
      </View>

      {body()}

      {jumping ? (
        <Sheet title={t("photos.jumpTo")} onClose={() => setJumping(false)}>
          {years.map(([year, months]) => (
            <View key={year} style={styles.year}>
              <Text accessibilityRole="header" style={[styles.yearTitle, { color: colors.inkSoft }]}>
                {year}
              </Text>
              {months.map(({ month, count }) => (
                <SheetOption
                  key={month}
                  label={`${monthName(year, month)} · ${t("photos.monthCount", { count })}`}
                  selected={start?.year === year && start.month === month}
                  onPress={() => {
                    setStart({ year, month });
                    setJumping(false);
                  }}
                />
              ))}
            </View>
          ))}
        </Sheet>
      ) : null}

      {viewing >= 0 ? (
        <FileViewer
          files={items}
          index={viewing}
          onIndex={(index) => setOpen(items[index]?.id ?? null)}
          onClose={() => setOpen(null)}
          onDetails={setInfo}
        />
      ) : null}

      {info ? <PhotoInfo item={info} onClose={() => setInfo(null)} /> : null}
    </SafeAreaView>
  );
}

/** What is known of one photo or video, read when asked for. */
function PhotoInfo({ item, onClose }: { readonly item: PhotoItem; readonly onClose: () => void }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { server, photos } = useActiveServer();
  const locale = currentLocale();
  const media = useQuery({ queryKey: [server.id, "media-info", item.id], queryFn: () => photos.mediaInfo(item.id) });
  const info = media.data;
  const camera = [info?.cameraMake, info?.cameraModel].filter(Boolean).join(" ");
  const facts: [string, string | null][] = [
    [t("photos.taken"), formatTakenAt(item.capturedAt, locale)],
    [t("photos.camera"), camera || null],
    [t("photos.dimensions"), info?.width && info.height ? `${info.width} × ${info.height}` : null],
    [t("photos.duration"), info?.durationMs != null ? formatDuration(Number(info.durationMs)) : null],
    [t("photos.fileSize"), formatBytes(Number(item.size), locale)],
    [t("photos.format"), info?.format ?? null],
  ];

  return (
    <Sheet title={item.name} onClose={onClose}>
      <ErrorMessage error={media.error} />
      <View style={styles.facts}>
        {facts
          .filter((fact): fact is [string, string] => fact[1] !== null)
          .map(([label, value]) => (
            <View key={label} style={[styles.fact, { borderColor: colors.line }]}>
              <Text style={[styles.factLabel, { color: colors.inkFaint }]}>{label}</Text>
              <Text style={[styles.factValue, { color: colors.ink }]}>{value}</Text>
            </View>
          ))}
      </View>
    </Sheet>
  );
}

const styles = StyleSheet.create({
  facts: { paddingHorizontal: space.md, paddingBottom: space.md },
  fact: { paddingVertical: space.md, borderBottomWidth: StyleSheet.hairlineWidth, gap: 2 },
  factLabel: { fontSize: type.small },
  factValue: { fontSize: type.body },
  flex: { flex: 1 },
  fill: { width: "100%", height: "100%" },
  bar: { minHeight: 56, flexDirection: "row", alignItems: "center", paddingLeft: space.lg, paddingRight: space.sm },
  barTitle: { flex: 1, fontSize: type.heading, fontWeight: "700" },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  off: { opacity: 0.35 },
  message: { flex: 1, padding: space.xl, gap: space.lg, justifyContent: "center" },
  list: { flexGrow: 1, paddingHorizontal: SIDE, paddingBottom: space.xl },
  head: { paddingHorizontal: space.md },
  day: {
    fontSize: type.small,
    fontWeight: "600",
    paddingHorizontal: space.md,
    paddingTop: space.lg,
    paddingBottom: space.sm,
  },
  row: { flexDirection: "row", gap: GAP, marginBottom: GAP },
  badge: {
    position: "absolute",
    right: 4,
    bottom: 4,
    flexDirection: "row",
    alignItems: "center",
    gap: 3,
    paddingHorizontal: 5,
    paddingVertical: 2,
    borderRadius: 6,
    backgroundColor: "rgba(0, 0, 0, 0.55)",
  },
  badgeText: { color: "#fff", fontSize: 12, fontWeight: "600" },
  empty: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.md, padding: space.xxl },
  more: { padding: space.lg },
  year: { gap: space.xs, paddingBottom: space.md },
  yearTitle: { fontSize: type.small, fontWeight: "700", paddingHorizontal: space.md, paddingTop: space.sm },
});
