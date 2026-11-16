import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage, Field } from "../components/ui.tsx";

/** Adds the chosen photos to an album, or to a new one. */
export function AddToAlbum(props: {
  readonly ids: readonly string[];
  readonly onClose: () => void;
  readonly onDone: (albumName: string) => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const albums = useQuery({ queryKey: ["albums"], queryFn: () => call(api.GET("/api/v1/albums")) });

  const finish = async (albumName: string): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["albums"] });
    await queryClient.invalidateQueries({ queryKey: ["album"] });
    props.onDone(albumName);
  };

  const addTo = async (id: string, albumName: string): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      await call(
        api.POST("/api/v1/albums/{id}/items", { params: { path: { id } }, body: { nodeIds: [...props.ids] } }),
      );
      await finish(albumName);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const create = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const album = await call(api.POST("/api/v1/albums", { body: { name, nodeIds: [...props.ids] } }));
      await finish(album.name);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog title={t("albums.addTitle", { count: props.ids.length })} onClose={props.onClose}>
      <ErrorMessage error={error} />
      <form
        className="inline-form"
        onSubmit={(event) => {
          event.preventDefault();
          void create();
        }}
      >
        <Field label={t("albums.newName")} value={name} onChange={(e) => setName(e.target.value)} maxLength={255} />
        <Button type="submit" variant="primary" icon="plus" disabled={busy || name.trim() === ""}>
          {t("albums.create")}
        </Button>
      </form>
      {albums.data && albums.data.length > 0 ? (
        <>
          <p className="muted">{t("albums.orExisting")}</p>
          <ul className="album-picks">
            {albums.data.map((album) => (
              <li key={album.id}>
                <button
                  type="button"
                  className="album-pick"
                  disabled={busy}
                  onClick={() => void addTo(album.id, album.name)}
                >
                  {album.coverUrl ? <img src={album.coverUrl} alt="" /> : <Icon name="album" size={28} />}
                  <span>
                    <strong>{album.name}</strong>
                    <span className="muted">{t("albums.count", { count: album.itemCount })}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </Dialog>
  );
}
