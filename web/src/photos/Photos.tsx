import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { NavLink } from "react-router";

import { api, call } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage, Flash, PageHeader } from "../components/ui.tsx";
import { batch, downloadArchive } from "../files/actions.ts";
import { currentLocale } from "../i18n.ts";
import { AddToAlbum } from "./AddToAlbum.tsx";
import { bucketRange } from "./layout.ts";
import { PhotoGrid, type PhotoItem } from "./PhotoGrid.tsx";
import { Viewer } from "./Viewer.tsx";

const PAGE = 150;

/** Photos, Albums and Search as tabs of one section. */
export function PhotoTabs() {
  const { t } = useTranslation();
  return (
    <nav className="library-tabs" aria-label={t("nav.photos")}>
      <NavLink to="/photos" end className={({ isActive }) => (isActive ? "library-tab is-current" : "library-tab")}>
        <Icon name="image" size={16} />
        {t("photos.timeline")}
      </NavLink>
      <NavLink to="/photos/albums" className={({ isActive }) => (isActive ? "library-tab is-current" : "library-tab")}>
        <Icon name="album" size={16} />
        {t("albums.title")}
      </NavLink>
      <NavLink to="/search" className={({ isActive }) => (isActive ? "library-tab is-current" : "library-tab")}>
        <Icon name="search" size={16} />
        {t("search.title")}
      </NavLink>
    </nav>
  );
}

/** Loads the next page when the end of the grid scrolls into view. */
export function LoadMore(props: { readonly onVisible: () => void; readonly busy: boolean }) {
  const { t } = useTranslation();
  const sentinel = useRef<HTMLDivElement>(null);
  const { onVisible } = props;
  useEffect(() => {
    const element = sentinel.current;
    if (!element) {
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          onVisible();
        }
      },
      { rootMargin: "800px" },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [onVisible]);
  return (
    <div ref={sentinel} className="load-more">
      {props.busy ? <span className="muted">{t("common.loading")}</span> : null}
    </div>
  );
}

/**
 * What can be done with selected photos: add to an album, download,
 * move to the trash. Shared by the timeline, albums and search.
 */
export function SelectionBar(props: {
  readonly selected: ReadonlySet<string>;
  readonly onClear: () => void;
  readonly onChanged: () => Promise<void>;
  readonly extra?: ReactNode;
}) {
  const { t } = useTranslation();
  const [adding, setAdding] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const ids = [...props.selected];

  const trash = async (): Promise<void> => {
    setError(null);
    try {
      const results = await batch("trash", ids);
      const failed = results.filter((r) => !r.ok);
      setNotice(t("photos.trashed", { count: ids.length - failed.length }));
      props.onClear();
      await props.onChanged();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <>
      <Flash message={notice} />
      <ErrorMessage error={error} />
      {ids.length > 0 ? (
        <div className="toolbar selection-bar" role="toolbar" aria-label={t("files.actions")}>
          <span className="toolbar-count">{t("files.selected", { count: ids.length })}</span>
          <Button size="sm" variant="ghost" icon="album" onClick={() => setAdding(true)}>
            {t("albums.addTo")}
          </Button>
          <Button size="sm" variant="ghost" icon="download" onClick={() => void downloadArchive(ids)}>
            {t("files.download")}
          </Button>
          {props.extra}
          <Button size="sm" variant="ghost" icon="trash" onClick={() => void trash()}>
            {t("files.delete")}
          </Button>
          <Button size="sm" variant="ghost" icon="close" onClick={props.onClear}>
            {t("photos.clearSelection")}
          </Button>
        </div>
      ) : null}
      {adding ? (
        <AddToAlbum
          ids={ids}
          onClose={() => setAdding(false)}
          onDone={(name) => {
            setAdding(false);
            setNotice(t("albums.added", { count: ids.length, name }));
            props.onClear();
          }}
        />
      ) : null}
    </>
  );
}

/**
 * The timeline (UX.md Photos): every photo and video the person can see,
 * newest first, grouped by day, with a month scrubber that jumps anywhere
 * at once, however many photos there are.
 */
export function Photos() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [start, setStart] = useState<{ year: number; month: number } | null>(null);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [open, setOpen] = useState<number | null>(null);

  const buckets = useQuery({
    queryKey: ["timeline-buckets"],
    queryFn: () => call(api.GET("/api/v1/timeline/buckets")),
  });
  const to = start ? bucketRange(start.year, start.month).to : undefined;
  const timeline = useInfiniteQuery({
    queryKey: ["timeline", to ?? "newest"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      call(
        api.GET("/api/v1/timeline", {
          params: { query: { limit: PAGE, ...(to ? { to } : {}), ...(pageParam ? { cursor: pageParam } : {}) } },
        }),
      ),
    getNextPageParam: (page) => page.nextCursor ?? null,
  });
  const items: PhotoItem[] = useMemo(() => timeline.data?.pages.flatMap((p) => p.items) ?? [], [timeline.data]);

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["timeline"] });
    await queryClient.invalidateQueries({ queryKey: ["timeline-buckets"] });
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
  const monthName = (month: number): string =>
    new Intl.DateTimeFormat(currentLocale(), { month: "short", timeZone: "UTC" }).format(
      new Date(Date.UTC(2000, month - 1, 15)),
    );

  const loadMore = (): void => {
    if (timeline.hasNextPage && !timeline.isFetchingNextPage) {
      void timeline.fetchNextPage();
    }
  };

  return (
    <div className="photos">
      <PageHeader title={t("photos.title")} />
      <PhotoTabs />
      <SelectionBar selected={selected} onClear={() => setSelected(new Set())} onChanged={refresh} />
      <ErrorMessage error={timeline.error} />
      {start ? (
        <Button size="sm" variant="ghost" icon="chevronLeft" onClick={() => setStart(null)}>
          {t("photos.backToNewest")}
        </Button>
      ) : null}

      <div className="photos-body">
        <div className="photos-main">
          {timeline.isPending ? <p className="muted">{t("common.loading")}</p> : null}
          {timeline.isSuccess && items.length === 0 ? (
            <div className="empty">
              <Icon name="image" size={40} />
              <p>{t("photos.empty")}</p>
            </div>
          ) : null}
          <PhotoGrid items={items} selected={selected} onSelect={setSelected} onOpen={setOpen} />
          {timeline.hasNextPage ? <LoadMore onVisible={loadMore} busy={timeline.isFetchingNextPage} /> : null}
        </div>

        {years.length > 0 ? (
          <nav className="scrubber" aria-label={t("photos.jumpTo")}>
            {years.map(([year, months]) => (
              <div key={year} className="scrubber-year">
                <span className="scrubber-year-label">{year}</span>
                {months.map((m) => (
                  <button
                    key={m.month}
                    type="button"
                    className={
                      start?.year === year && start.month === m.month ? "scrubber-month is-current" : "scrubber-month"
                    }
                    title={t("photos.monthCount", { count: m.count })}
                    onClick={() => setStart({ year, month: m.month })}
                  >
                    {monthName(m.month)}
                  </button>
                ))}
              </div>
            ))}
          </nav>
        ) : null}
      </div>

      {open !== null && items[open] ? (
        <Viewer
          items={items}
          index={open}
          onIndex={(index) => {
            setOpen(index);
            if (index >= items.length - 5) {
              loadMore();
            }
          }}
          onClose={() => setOpen(null)}
        />
      ) : null}
    </div>
  );
}
