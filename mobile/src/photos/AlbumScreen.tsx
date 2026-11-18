import type { PhotoItem } from "@atrium/shared";
import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { ConfirmDialog, NameDialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { Button, ErrorMessage } from "../components/ui.tsx";
import { FileViewer } from "../files/FileViewer.tsx";
import { currentLocale } from "../i18n.ts";
import { space, type, useTheme } from "../theme.ts";
import { albumSpan } from "./Albums.tsx";
import { PhotoGrid, PhotoInfo, SizeButtons, photoStyles } from "./PhotoGrid.tsx";
import { SelectionBar } from "./parts.tsx";
import { useSelection } from "./selection.ts";

const PAGE = 150;

/**
 * One album: its photos and videos by day. The album can be renamed and
 * deleted; chosen items can be taken out of it or made its cover. None of
 * this touches the files themselves.
 */
export function AlbumScreen({ albumId }: { readonly albumId: string }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const { server, albums } = useActiveServer();
  const queryClient = useQueryClient();
  const selection = useSelection();
  const [open, setOpen] = useState<string | null>(null);
  const [info, setInfo] = useState<PhotoItem | null>(null);
  const [menu, setMenu] = useState(false);
  const [asking, setAsking] = useState<"rename" | "delete" | null>(null);
  const [error, setError] = useState<unknown>(null);
  const locale = currentLocale();

  const album = useQuery({ queryKey: [server.id, "album", albumId], queryFn: () => albums.get(albumId) });
  const pages = useInfiniteQuery({
    queryKey: [server.id, "album", albumId, "items"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => albums.items(albumId, { limit: PAGE, cursor: pageParam }),
    getNextPageParam: (page) => page.nextCursor ?? null,
  });
  const items = useMemo(() => pages.data?.pages.flatMap((p) => p.items) ?? [], [pages.data]);
  const viewing = open === null ? -1 : items.findIndex((item) => item.id === open);
  const chosen = selection.selected ? [...selection.selected] : [];

  const changed = async (): Promise<void> => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: [server.id, "album", albumId] }),
      queryClient.invalidateQueries({ queryKey: [server.id, "albums"] }),
    ]);
  };

  const run = (action: () => Promise<unknown>): void => {
    setError(null);
    action()
      .catch((e: unknown) => setError(e))
      .finally(() => void changed());
  };

  const leave = (): void => (router.canGoBack() ? router.back() : router.replace("/photos"));
  const current = { id: albumId, version: album.data?.version ?? 0 };

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={[styles.flex, { backgroundColor: colors.paper }]}>
      {selection.selected ? (
        <SelectionBar count={selection.count} onClear={selection.clear} />
      ) : (
        <View style={[photoStyles.bar, styles.bar]}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common.back")}
            onPress={leave}
            style={photoStyles.barButton}
          >
            <Icon name="chevronLeft" />
          </Pressable>
          <Text accessibilityRole="header" numberOfLines={1} style={[photoStyles.barTitle, { color: colors.ink }]}>
            {album.data?.name ?? ""}
          </Text>
          <SizeButtons />
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("files.actions")}
            onPress={() => setMenu(true)}
            style={photoStyles.barButton}
          >
            <Icon name="more" />
          </Pressable>
        </View>
      )}

      <PhotoGrid
        items={items}
        pending={pages.isPending}
        error={pages.error ?? album.error}
        loadingMore={pages.isFetchingNextPage}
        empty={t("albums.emptyAlbum")}
        header={
          album.data ? (
            <View style={styles.head}>
              <Text style={[styles.detail, { color: colors.inkFaint }]}>
                {[t("albums.count", { count: Number(album.data.itemCount) }), albumSpan(album.data, locale)]
                  .filter(Boolean)
                  .join(" · ")}
              </Text>
              <ErrorMessage error={error} />
            </View>
          ) : undefined
        }
        selected={selection.selected}
        onPress={(item) => (selection.selected ? selection.toggle(item.id) : setOpen(item.id))}
        onLongPress={(item) => selection.toggle(item.id)}
        onEndReached={() => {
          if (pages.hasNextPage && !pages.isFetchingNextPage) {
            void pages.fetchNextPage();
          }
        }}
        onRefresh={() => Promise.all([album.refetch(), pages.refetch()])}
      />

      {selection.selected ? (
        <View style={[styles.actions, { backgroundColor: colors.surface, borderColor: colors.line }]}>
          {chosen.length === 1 ? (
            <Button
              onPress={() =>
                run(async () => {
                  await albums.setCover(current, chosen[0]!);
                  selection.clear();
                })
              }
            >
              {t("albums.makeCover")}
            </Button>
          ) : null}
          <Button
            variant="danger"
            onPress={() =>
              run(async () => {
                await albums.remove(albumId, chosen);
                selection.clear();
              })
            }
          >
            {t("albums.removeFrom")}
          </Button>
        </View>
      ) : null}

      {menu && album.data ? (
        <Sheet title={album.data.name} onClose={() => setMenu(false)}>
          <SheetOption
            label={t("files.rename")}
            icon="pencil"
            onPress={() => {
              setMenu(false);
              setAsking("rename");
            }}
          />
          <SheetOption
            label={t("albums.delete")}
            icon="trash"
            onPress={() => {
              setMenu(false);
              setAsking("delete");
            }}
          />
        </Sheet>
      ) : null}

      {asking === "rename" && album.data ? (
        <NameDialog
          title={t("files.rename")}
          initial={album.data.name}
          confirm={t("common.save")}
          onClose={() => setAsking(null)}
          onSubmit={async (name) => {
            try {
              await albums.rename(current, name);
            } finally {
              // Also after a refusal: someone may have changed the album meanwhile.
              await changed();
            }

            setAsking(null);
          }}
        />
      ) : null}

      {asking === "delete" && album.data ? (
        <ConfirmDialog
          title={t("albums.deleteTitle", { name: album.data.name })}
          body={t("albums.deleteBody")}
          confirm={t("albums.delete")}
          onClose={() => setAsking(null)}
          onConfirm={async () => {
            await albums.delete(albumId);
            await queryClient.invalidateQueries({ queryKey: [server.id, "albums"] });
            setAsking(null);
            leave();
          }}
        />
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

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { paddingLeft: space.sm },
  head: { paddingHorizontal: space.md, paddingTop: space.xs, gap: space.sm },
  detail: { fontSize: type.small },
  actions: { flexDirection: "row", gap: space.sm, borderTopWidth: 1, padding: space.md, justifyContent: "flex-end" },
});
