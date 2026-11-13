import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router";

import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage, Field, PageHeader } from "../components/ui.tsx";
import { usePin } from "../navigation.ts";
import { LoadMore, PhotoTabs, SelectionBar } from "./Photos.tsx";
import { formatDay, PhotoGrid, type PhotoItem } from "./PhotoGrid.tsx";
import { Viewer } from "./Viewer.tsx";

/** "May 2023 – June 2024", or one month, for an album's dates. */
function span(newest?: string | null, oldest?: string | null): string {
  if (!newest || !oldest) {
    return "";
  }

  const first = formatDay(oldest.slice(0, 10));
  const last = formatDay(newest.slice(0, 10));
  return first === last ? first : `${first} – ${last}`;
}

/** The person's albums (UX.md Photos): covers, names, counts. */
export function Albums() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [error, setError] = useState<unknown>(null);
  const albums = useQuery({ queryKey: ["albums"], queryFn: () => call(api.GET("/api/v1/albums")) });

  const create = async (): Promise<void> => {
    setError(null);
    try {
      const album = await call(api.POST("/api/v1/albums", { body: { name, nodeIds: [] } }));
      await queryClient.invalidateQueries({ queryKey: ["albums"] });
      void navigate(`/photos/albums/${album.id}`);
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="photos">
      <PageHeader
        title={t("albums.title")}
        actions={
          <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>
            {t("albums.new")}
          </Button>
        }
      />
      <PhotoTabs />
      <ErrorMessage error={albums.error} />
      {albums.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {albums.data?.length === 0 ? (
        <div className="empty">
          <Icon name="album" size={40} />
          <p>{t("albums.empty")}</p>
        </div>
      ) : null}
      <ul className="album-grid">
        {albums.data?.map((album) => (
          <li key={album.id}>
            <Link to={`/photos/albums/${album.id}`} className="album-card">
              <span className="album-cover">
                {album.coverUrl ? <img src={album.coverUrl} alt="" loading="lazy" /> : <Icon name="album" size={36} />}
              </span>
              <span className="album-name">{album.name}</span>
              <span className="muted">{t("albums.count", { count: album.itemCount })}</span>
            </Link>
          </li>
        ))}
      </ul>

      {creating ? (
        <Dialog
          title={t("albums.new")}
          onClose={() => setCreating(false)}
          actions={
            <>
              <Button onClick={() => setCreating(false)}>{t("common.cancel")}</Button>
              <Button variant="primary" disabled={name.trim() === ""} onClick={() => void create()}>
                {t("albums.create")}
              </Button>
            </>
          }
        >
          <ErrorMessage error={error} />
          <Field
            label={t("albums.newName")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={255}
            autoFocus
          />
        </Dialog>
      ) : null}
    </div>
  );
}

/** One album as a timeline of its items, with rename, cover, remove and delete. */
export function AlbumPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const albumId = useParams()["albumId"]!;
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [open, setOpen] = useState<number | null>(null);
  const [dialog, setDialog] = useState<"rename" | "delete" | null>(null);
  const [name, setName] = useState("");
  const [error, setError] = useState<unknown>(null);
  const pin = usePin("album", albumId);

  const album = useQuery({
    queryKey: ["album", albumId],
    queryFn: () => call(api.GET("/api/v1/albums/{id}", { params: { path: { id: albumId } } })),
  });
  const pages = useInfiniteQuery({
    queryKey: ["album", albumId, "items"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      call(
        api.GET("/api/v1/albums/{id}/items", {
          params: { path: { id: albumId }, query: { limit: 150, ...(pageParam ? { cursor: pageParam } : {}) } },
        }),
      ),
    getNextPageParam: (page) => page.nextCursor ?? null,
  });
  const items: PhotoItem[] = useMemo(() => pages.data?.pages.flatMap((p) => p.items) ?? [], [pages.data]);

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["album", albumId] });
    await queryClient.invalidateQueries({ queryKey: ["albums"] });
  };

  const run = async (action: () => Promise<unknown>): Promise<void> => {
    setError(null);
    try {
      await action();
      await refresh();
    } catch (e) {
      setError(e);
    }
  };

  const ifMatch = { "If-Match": `"${album.data?.version ?? 0}"` };
  const removeSelected = (): Promise<void> =>
    run(async () => {
      await call(
        api.POST("/api/v1/albums/{id}/items/remove", {
          params: { path: { id: albumId } },
          body: { nodeIds: [...selected] },
        }),
      );
      setSelected(new Set());
    });
  const setCover = (id: string): Promise<void> =>
    run(() =>
      call(
        api.PATCH("/api/v1/albums/{id}", {
          params: { path: { id: albumId } },
          headers: ifMatch,
          body: { name: null, coverNodeId: id },
        }),
      ),
    );

  return (
    <div className="photos">
      <PageHeader
        title={album.data?.name ?? t("albums.title")}
        lead={
          album.data
            ? `${t("albums.count", { count: album.data.itemCount })} · ${span(album.data.newestAt, album.data.oldestAt)}`
            : undefined
        }
        actions={
          album.data ? (
            <>
              <Button
                icon="pencil"
                onClick={() => {
                  setName(album.data.name);
                  setDialog("rename");
                }}
              >
                {t("files.rename")}
              </Button>
              {pin.ready ? (
                <Button icon="pin" disabled={pin.busy} onClick={() => void pin.toggle()}>
                  {pin.pinned ? t("menu.unpin") : t("menu.pin")}
                </Button>
              ) : null}
              <Button variant="ghost" icon="trash" onClick={() => setDialog("delete")}>
                {t("albums.delete")}
              </Button>
            </>
          ) : null
        }
      />
      <PhotoTabs />
      <ErrorMessage error={error ?? pin.error ?? album.error ?? pages.error} />
      <SelectionBar
        selected={selected}
        onClear={() => setSelected(new Set())}
        onChanged={refresh}
        extra={
          <Button size="sm" variant="ghost" icon="close" onClick={() => void removeSelected()}>
            {t("albums.removeFrom")}
          </Button>
        }
      />
      {pages.isSuccess && items.length === 0 ? (
        <div className="empty">
          <Icon name="album" size={40} />
          <p>{t("albums.emptyAlbum")}</p>
        </div>
      ) : null}
      <PhotoGrid items={items} selected={selected} onSelect={setSelected} onOpen={setOpen} />
      {pages.hasNextPage ? (
        <LoadMore onVisible={() => void pages.fetchNextPage()} busy={pages.isFetchingNextPage} />
      ) : null}

      {open !== null && items[open] ? (
        <Viewer
          items={items}
          index={open}
          onIndex={setOpen}
          onClose={() => setOpen(null)}
          actions={(item) => (
            <Button size="sm" variant="ghost" icon="image" onClick={() => void setCover(item.id)}>
              {t("albums.makeCover")}
            </Button>
          )}
        />
      ) : null}

      {dialog === "rename" ? (
        <Dialog
          title={t("files.rename")}
          onClose={() => setDialog(null)}
          actions={
            <>
              <Button onClick={() => setDialog(null)}>{t("common.cancel")}</Button>
              <Button
                variant="primary"
                disabled={name.trim() === ""}
                onClick={() =>
                  void run(async () => {
                    await call(
                      api.PATCH("/api/v1/albums/{id}", {
                        params: { path: { id: albumId } },
                        headers: ifMatch,
                        body: { name, coverNodeId: null },
                      }),
                    );
                    setDialog(null);
                  })
                }
              >
                {t("common.save")}
              </Button>
            </>
          }
        >
          <Field
            label={t("albums.newName")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={255}
            autoFocus
          />
        </Dialog>
      ) : null}

      {dialog === "delete" ? (
        <Dialog
          title={t("albums.deleteTitle", { name: album.data?.name ?? "" })}
          onClose={() => setDialog(null)}
          actions={
            <>
              <Button onClick={() => setDialog(null)}>{t("common.cancel")}</Button>
              <Button
                variant="danger"
                onClick={() =>
                  void run(async () => {
                    await call(api.DELETE("/api/v1/albums/{id}", { params: { path: { id: albumId } } }));
                    await queryClient.invalidateQueries({ queryKey: ["albums"] });
                    void navigate("/photos/albums");
                  })
                }
              >
                {t("albums.delete")}
              </Button>
            </>
          }
        >
          <p>{t("albums.deleteBody")}</p>
        </Dialog>
      ) : null}
    </div>
  );
}
