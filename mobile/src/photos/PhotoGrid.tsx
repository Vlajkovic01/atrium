import {
  formatBytes,
  formatDuration,
  formatPhotoDay,
  formatTakenAt,
  groupByDay,
  justify,
  type PhotoItem,
  type Placed,
} from "@atrium/shared";
import { useQuery } from "@tanstack/react-query";
import { useMemo, useRef, useState, type ReactElement } from "react";
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

import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Sheet } from "../components/Sheet.tsx";
import { Body, Button, ErrorMessage } from "../components/ui.tsx";
import { absoluteUrl } from "../files/local";
import { currentLocale } from "../i18n.ts";
import { space, type, useTheme } from "../theme.ts";
import { densities, stepDensity, useDensity } from "./density.ts";

const GAP = 3;
const SIDE = space.xs;
/** How far two fingers must spread or close, as a share of where they started, to change the size of the pictures. */
const PINCH_STEP = 0.35;

type Row =
  | { readonly key: string; readonly kind: "day"; readonly day: string }
  | { readonly key: string; readonly kind: "photos"; readonly cells: readonly Placed<PhotoItem>[] };

/**
 * Photos and videos by day, in justified rows (the timeline and every
 * album). Two fingers spread or close to change the size of the pictures.
 * While items are being chosen, a touch chooses instead of opening.
 */
export function PhotoGrid({
  items,
  pending,
  error,
  loadingMore,
  empty,
  header,
  selected,
  onPress,
  onLongPress,
  onEndReached,
  onRefresh,
}: {
  readonly items: readonly PhotoItem[];
  readonly pending: boolean;
  readonly error: unknown;
  readonly loadingMore: boolean;
  /** Said when there is nothing to show; null to say nothing. */
  readonly empty: string | null;
  readonly header?: ReactElement | undefined;
  /** The chosen items; undefined while nothing is being chosen. */
  readonly selected?: ReadonlySet<string> | undefined;
  readonly onPress: (item: PhotoItem) => void;
  /** Starts choosing; leave out where nothing can be chosen. */
  readonly onLongPress?: ((item: PhotoItem) => void) | undefined;
  readonly onEndReached: () => void;
  readonly onRefresh: () => Promise<unknown>;
}) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { width } = useWindowDimensions();
  const { server } = useActiveServer();
  const density = useDensity();
  const pinch = useRef<number | null>(null);
  const [pulled, setPulled] = useState(false);
  const locale = currentLocale();

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

  const refresh = async (): Promise<void> => {
    setPulled(true);
    try {
      await onRefresh();
    } finally {
      setPulled(false);
    }
  };

  // Two fingers: compare their distance with where they started.
  const onTouchMove = (event: GestureResponderEvent): void => {
    const [a, b] = event.nativeEvent.touches;
    const now = a && b ? Math.hypot(a.pageX - b.pageX, a.pageY - b.pageY) : null;
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

  if (error && items.length === 0) {
    return (
      <View style={styles.message}>
        <ErrorMessage error={error} />
        <Button onPress={() => void refresh()} busy={pulled}>
          {t("common.retry")}
        </Button>
      </View>
    );
  }

  if (pending) {
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
      onEndReached={onEndReached}
      ListHeaderComponent={header}
      ListEmptyComponent={
        empty === null ? undefined : (
          <View style={styles.empty}>
            <Icon name="image" size={40} color={colors.inkFaint} />
            <Body faint>{empty}</Body>
          </View>
        )
      }
      ListFooterComponent={
        loadingMore ? (
          <ActivityIndicator style={styles.more} color={colors.accent} />
        ) : error ? (
          <ErrorMessage error={error} />
        ) : undefined
      }
      renderItem={({ item: row }) =>
        row.kind === "day" ? (
          <Text accessibilityRole="header" style={[styles.day, { color: colors.ink }]}>
            {formatPhotoDay(row.day, locale)}
          </Text>
        ) : (
          <View style={styles.row}>
            {row.cells.map((cell) => {
              const chosen = selected?.has(cell.item.id) ?? false;
              return (
                <Pressable
                  key={cell.item.id}
                  accessibilityRole="button"
                  accessibilityLabel={cell.item.name}
                  {...(selected ? { accessibilityState: { selected: chosen } } : {})}
                  onPress={() => onPress(cell.item)}
                  {...(onLongPress ? { onLongPress: () => onLongPress(cell.item) } : {})}
                  style={{ width: cell.width, height: cell.height, backgroundColor: colors.surfaceSunk }}
                >
                  <Image
                    source={{ uri: absoluteUrl(server.url, cell.item.thumbnailUrl) }}
                    style={[styles.fill, chosen && styles.chosenImage]}
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
                  {selected ? (
                    <View style={[styles.tick, { backgroundColor: chosen ? colors.accent : "rgba(0, 0, 0, 0.3)" }]}>
                      <Icon name={chosen ? "check" : "circle"} size={16} color="#fff" />
                    </View>
                  ) : null}
                </Pressable>
              );
            })}
          </View>
        )
      }
    />
  );
}

/** What is known of one photo or video, read when asked for. */
export function PhotoInfo({ item, onClose }: { readonly item: PhotoItem; readonly onClose: () => void }) {
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

/** The size buttons of a photo screen's top bar, for one hand and for screen readers. */
export function SizeButtons() {
  const { t } = useTranslation();
  const density = useDensity();
  return (
    <>
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
    </>
  );
}

export const photoStyles = StyleSheet.create({
  bar: { minHeight: 56, flexDirection: "row", alignItems: "center", paddingLeft: space.lg, paddingRight: space.sm },
  barTitle: { flex: 1, fontSize: type.heading, fontWeight: "700" },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
});

const styles = StyleSheet.create({
  ...photoStyles,
  fill: { width: "100%", height: "100%" },
  chosenImage: { transform: [{ scale: 0.88 }], borderRadius: 6 },
  off: { opacity: 0.35 },
  message: { flex: 1, padding: space.xl, gap: space.lg, justifyContent: "center" },
  list: { flexGrow: 1, paddingHorizontal: SIDE, paddingBottom: space.xl },
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
  tick: {
    position: "absolute",
    left: 6,
    top: 6,
    width: 24,
    height: 24,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  empty: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.md, padding: space.xxl },
  more: { padding: space.lg },
  facts: { paddingHorizontal: space.md, paddingBottom: space.md },
  fact: { paddingVertical: space.md, borderBottomWidth: StyleSheet.hairlineWidth, gap: 2 },
  factLabel: { fontSize: type.small },
  factValue: { fontSize: type.body },
});
