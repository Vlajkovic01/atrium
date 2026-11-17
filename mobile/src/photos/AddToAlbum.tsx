import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useActiveServer } from "../api.ts";
import { NameDialog } from "../components/Dialog.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { Body, ErrorMessage } from "../components/ui.tsx";

/**
 * Puts the chosen photos and videos into an album: an existing one, or a
 * new one named here. Only the album changes; the files stay where they are.
 */
export function AddToAlbum({
  ids,
  onClose,
  onDone,
}: {
  readonly ids: readonly string[];
  readonly onClose: () => void;
  /** Called with what to tell the person, once the items are in the album. */
  readonly onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { server, albums } = useActiveServer();
  const queryClient = useQueryClient();
  const list = useQuery({ queryKey: [server.id, "albums"], queryFn: albums.list });
  const [naming, setNaming] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const finish = async (name: string): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: [server.id, "albums"] });
    await queryClient.invalidateQueries({ queryKey: [server.id, "album"] });
    onDone(t("albums.added", { count: ids.length, name }));
  };

  if (naming) {
    return (
      <NameDialog
        title={t("albums.new")}
        initial=""
        confirm={t("albums.create")}
        onClose={() => setNaming(false)}
        onSubmit={async (name) => {
          const album = await albums.create(name, ids);
          await finish(album.name);
        }}
      />
    );
  }

  return (
    <Sheet title={t("albums.addTitle", { count: ids.length })} onClose={onClose}>
      <SheetOption label={t("albums.new")} icon="plus" onPress={() => setNaming(true)} />
      {list.data && list.data.length > 0 ? <Body faint>{t("albums.orExisting")}</Body> : null}
      {list.data?.map((album) => (
        <SheetOption
          key={album.id}
          label={`${album.name} · ${t("albums.count", { count: Number(album.itemCount) })}`}
          icon="album"
          onPress={() => {
            setError(null);
            albums
              .add(album.id, ids)
              .then(() => finish(album.name))
              .catch((e: unknown) => setError(e));
          }}
        />
      ))}
      <ErrorMessage error={error ?? list.error} />
    </Sheet>
  );
}
