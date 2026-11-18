import { formatPhotoDay, type AlbumView } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useState, type ReactElement } from "react";
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
} from "react-native";

import { useActiveServer } from "../api.ts";
import { NameDialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Body, Button, ErrorMessage } from "../components/ui.tsx";
import { absoluteUrl } from "../files/local";
import { currentLocale } from "../i18n.ts";
import { radius, space, type, useTheme } from "../theme.ts";
import { photoStyles } from "./PhotoGrid.tsx";

/** "2 May 2024 – 14 June 2024", or one day, for an album's dates. */
export function albumSpan(album: Pick<AlbumView, "newestAt" | "oldestAt">, locale: string): string {
  if (!album.newestAt || !album.oldestAt) {
    return "";
  }

  const first = formatPhotoDay(album.oldestAt.slice(0, 10), locale);
  const last = formatPhotoDay(album.newestAt.slice(0, 10), locale);
  return first === last ? first : `${first} – ${last}`;
}

/** The person's albums (docs/UX.md, Mobile, Photos): cover, name, how many, and when. */
export function Albums({ switcher }: { readonly switcher: ReactElement }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const { width } = useWindowDimensions();
  const { server, albums } = useActiveServer();
  const queryClient = useQueryClient();
  const list = useQuery({ queryKey: [server.id, "albums"], queryFn: albums.list });
  const [creating, setCreating] = useState(false);
  const [pulled, setPulled] = useState(false);
  const locale = currentLocale();
  const columns = width >= 600 ? 3 : 2;
  const tile = (width - space.lg * 2 - space.md * (columns - 1)) / columns;

  const open = (id: string): void => {
    router.push({ pathname: "/photos/album/[albumId]", params: { albumId: id } });
  };

  return (
    <>
      <View style={photoStyles.bar}>
        <Text accessibilityRole="header" numberOfLines={1} style={[photoStyles.barTitle, { color: colors.ink }]}>
          {t("photos.title")}
        </Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("albums.new")}
          onPress={() => setCreating(true)}
          style={photoStyles.barButton}
        >
          <Icon name="plus" />
        </Pressable>
      </View>
      {switcher}

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
          key={columns}
          data={list.data}
          numColumns={columns}
          keyExtractor={(album) => album.id}
          contentContainerStyle={styles.list}
          columnWrapperStyle={styles.columns}
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
          ListEmptyComponent={
            <View style={styles.empty}>
              <Icon name="album" size={40} color={colors.inkFaint} />
              <Body faint>{t("albums.empty")}</Body>
            </View>
          }
          renderItem={({ item: album }) => (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`${album.name}, ${t("albums.count", { count: Number(album.itemCount) })}`}
              onPress={() => open(album.id)}
              style={{ width: tile }}
            >
              <View style={[styles.cover, { height: tile, backgroundColor: colors.surfaceSunk }]}>
                {album.coverUrl ? (
                  <Image source={{ uri: absoluteUrl(server.url, album.coverUrl) }} style={styles.fill} />
                ) : (
                  <Icon name="album" size={36} color={colors.inkFaint} />
                )}
              </View>
              <Text numberOfLines={1} style={[styles.name, { color: colors.ink }]}>
                {album.name}
              </Text>
              <Text numberOfLines={2} style={[styles.detail, { color: colors.inkFaint }]}>
                {[t("albums.count", { count: Number(album.itemCount) }), albumSpan(album, locale)]
                  .filter(Boolean)
                  .join(" · ")}
              </Text>
            </Pressable>
          )}
        />
      )}

      {creating ? (
        <NameDialog
          title={t("albums.new")}
          initial=""
          confirm={t("albums.create")}
          onClose={() => setCreating(false)}
          onSubmit={async (name) => {
            const album = await albums.create(name);
            await queryClient.invalidateQueries({ queryKey: [server.id, "albums"] });
            setCreating(false);
            open(album.id);
          }}
        />
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  fill: { width: "100%", height: "100%" },
  message: { flex: 1, padding: space.xl, gap: space.lg, justifyContent: "center" },
  list: { flexGrow: 1, paddingHorizontal: space.lg, paddingBottom: space.xl, gap: space.lg },
  columns: { gap: space.md },
  cover: { borderRadius: radius.md, overflow: "hidden", alignItems: "center", justifyContent: "center" },
  name: { fontSize: type.body, fontWeight: "600", marginTop: space.sm },
  detail: { fontSize: type.small },
  empty: { flex: 1, alignItems: "center", justifyContent: "center", gap: space.md, padding: space.xxl },
});
