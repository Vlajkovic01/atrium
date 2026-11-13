import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate, useParams } from "react-router";

import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage, PageHeader } from "../components/ui.tsx";
import { LoadMore, SelectionBar } from "../photos/Photos.tsx";
import { PhotoGrid } from "../photos/PhotoGrid.tsx";
import { asPhoto, OtherResults } from "../photos/Search.tsx";
import { Viewer } from "../photos/Viewer.tsx";
import { ViewEditor } from "./ViewEditor.tsx";

/**
 * A saved view (D-041): the files its rules choose, wherever they are.
 * Photos and videos as a grid, everything else as a list leading to its
 * folder. Nothing is moved; the view follows new files as they arrive.
 */
export function ViewPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const viewId = useParams()["viewId"]!;
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [open, setOpen] = useState<number | null>(null);

  const views = useQuery({ queryKey: ["views"], queryFn: () => call(api.GET("/api/v1/me/views")) });
  const view = views.data?.find((v) => v.id === viewId);
  const pages = useInfiniteQuery({
    queryKey: ["view", viewId],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      call(
        api.GET("/api/v1/me/views/{id}/items", {
          params: { path: { id: viewId }, query: { limit: 100, ...(pageParam ? { cursor: pageParam } : {}) } },
        }),
      ),
    getNextPageParam: (page) => page.nextCursor ?? null,
  });
  const all = useMemo(() => pages.data?.pages.flatMap((p) => p.items) ?? [], [pages.data]);
  const photos = useMemo(() => all.filter((r) => r.kind && r.thumbnailUrl && r.capturedAt).map(asPhoto), [all]);
  const others = all.filter((r) => !r.kind);
  const total = pages.data?.pages[0]?.total;

  const remove = async (): Promise<void> => {
    setError(null);
    try {
      await call(api.DELETE("/api/v1/me/views/{id}", { params: { path: { id: viewId } } }));
      await queryClient.invalidateQueries({ queryKey: ["views"] });
      await queryClient.invalidateQueries({ queryKey: ["navigation"] });
      void navigate("/files");
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="photos">
      <PageHeader
        title={view?.name ?? t("views.title")}
        lead={total !== undefined ? t("views.matching", { count: Number(total) }) : undefined}
        actions={
          view ? (
            <>
              <Button icon="pencil" onClick={() => setEditing(true)}>
                {t("views.edit")}
              </Button>
              <Button variant="ghost" icon="trash" onClick={() => setDeleting(true)}>
                {t("views.delete")}
              </Button>
            </>
          ) : null
        }
      />
      <ErrorMessage error={error ?? views.error ?? pages.error} />
      <SelectionBar
        selected={selected}
        onClear={() => setSelected(new Set())}
        onChanged={() => pages.refetch().then(() => undefined)}
      />
      {pages.isSuccess && all.length === 0 ? (
        <div className="empty">
          <Icon name="search" size={40} />
          <p>{t("views.empty")}</p>
        </div>
      ) : null}
      {photos.length > 0 ? (
        <PhotoGrid items={photos} selected={selected} onSelect={setSelected} onOpen={setOpen} />
      ) : null}
      <OtherResults results={others} />
      {pages.hasNextPage ? (
        <LoadMore onVisible={() => void pages.fetchNextPage()} busy={pages.isFetchingNextPage} />
      ) : null}

      {open !== null && photos[open] ? (
        <Viewer items={photos} index={open} onIndex={setOpen} onClose={() => setOpen(null)} />
      ) : null}
      {editing && view ? (
        <ViewEditor view={view} onClose={() => setEditing(false)} onSaved={() => setEditing(false)} />
      ) : null}
      {deleting && view ? (
        <Dialog
          title={t("views.deleteTitle", { name: view.name })}
          onClose={() => setDeleting(false)}
          actions={
            <>
              <Button onClick={() => setDeleting(false)}>{t("common.cancel")}</Button>
              <Button variant="danger" onClick={() => void remove()}>
                {t("views.delete")}
              </Button>
            </>
          }
        >
          <p>{t("views.deleteBody")}</p>
        </Dialog>
      ) : null}
    </div>
  );
}
