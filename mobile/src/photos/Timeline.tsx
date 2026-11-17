import { bucketRange, type PhotoItem } from "@atrium/shared";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, StyleSheet, Text, View } from "react-native";

import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { Button } from "../components/ui.tsx";
import { FileViewer } from "../files/FileViewer.tsx";
import { currentLocale } from "../i18n.ts";
import { space, type, useTheme } from "../theme.ts";
import { AddToAlbum } from "./AddToAlbum.tsx";
import { PhotoGrid, PhotoInfo, SizeButtons, photoStyles } from "./PhotoGrid.tsx";
import { SelectionBar, Toast } from "./parts.tsx";
import { useSelection } from "./selection.ts";

const PAGE = 200;

/**
 * The photo timeline (docs/UX.md, Mobile, Photos): photos and videos
 * newest first, by day. A month can be jumped to; touching a picture opens
 * it; a long press starts choosing pictures to add to an album.
 */
export function Timeline({ switcher }: { readonly switcher: React.ReactElement }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { server, photos } = useActiveServer();
  const [start, setStart] = useState<{ readonly year: number; readonly month: number } | null>(null);
  const [jumping, setJumping] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [info, setInfo] = useState<PhotoItem | null>(null);
  const [adding, setAdding] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const selection = useSelection();
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
  const viewing = open === null ? -1 : items.findIndex((item) => item.id === open);

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

  return (
    <>
      {selection.selected ? (
        <SelectionBar count={selection.count} onClear={selection.clear} />
      ) : (
        <View style={photoStyles.bar}>
          <Text accessibilityRole="header" numberOfLines={1} style={[photoStyles.barTitle, { color: colors.ink }]}>
            {t("photos.title")}
          </Text>
          <SizeButtons />
          {years.length > 0 ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("photos.jumpTo")}
              onPress={() => setJumping(true)}
              style={photoStyles.barButton}
            >
              <Icon name="calendar" />
            </Pressable>
          ) : null}
        </View>
      )}
      {selection.selected ? null : switcher}

      <PhotoGrid
        items={items}
        pending={timeline.isPending}
        error={timeline.error}
        loadingMore={timeline.isFetchingNextPage}
        empty={t("photos.empty")}
        header={
          start ? (
            <View style={styles.head}>
              <Button variant="ghost" onPress={() => setStart(null)}>
                {t("photos.backToNewest")}
              </Button>
            </View>
          ) : undefined
        }
        selected={selection.selected}
        onPress={(item) => (selection.selected ? selection.toggle(item.id) : setOpen(item.id))}
        onLongPress={(item) => selection.toggle(item.id)}
        onEndReached={() => {
          if (timeline.hasNextPage && !timeline.isFetchingNextPage) {
            void timeline.fetchNextPage();
          }
        }}
        onRefresh={() => Promise.all([buckets.refetch(), timeline.refetch()])}
      />

      {selection.selected ? (
        <View style={[styles.actions, { backgroundColor: colors.surface, borderColor: colors.line }]}>
          <Button variant="primary" onPress={() => setAdding(true)}>
            {t("albums.addTo")}
          </Button>
        </View>
      ) : null}

      {message ? <Toast message={message} onDone={() => setMessage(null)} /> : null}

      {adding && selection.selected ? (
        <AddToAlbum
          ids={[...selection.selected]}
          onClose={() => setAdding(false)}
          onDone={(text) => {
            setAdding(false);
            selection.clear();
            setMessage(text);
          }}
        />
      ) : null}

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
    </>
  );
}

const styles = StyleSheet.create({
  head: { paddingHorizontal: space.md },
  actions: { borderTopWidth: 1, padding: space.md },
  year: { gap: space.xs, paddingBottom: space.md },
  yearTitle: { fontSize: type.small, fontWeight: "700", paddingHorizontal: space.md, paddingTop: space.sm },
});
