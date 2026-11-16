import type { components } from "@atrium/shared";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useSearchParams } from "react-router";

import { api, call } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { ErrorMessage, PageHeader } from "../components/ui.tsx";
import { FilePreview } from "../files/FilePreview.tsx";
import { formatBytes, currentLocale } from "../i18n.ts";
import { parseSearch } from "./layout.ts";
import { LoadMore, PhotoTabs, SelectionBar } from "./Photos.tsx";
import { formatDay, PhotoGrid, type PhotoItem } from "./PhotoGrid.tsx";
import { Viewer } from "./Viewer.tsx";

type SearchResult = components["schemas"]["SearchResultView"];
type SearchType = "photo" | "video" | "document" | "folder";

const TYPES: readonly SearchType[] = ["photo", "video", "document", "folder"];

/** A search result that is a photo or video, in the shape the grid uses. */
export function asPhoto(result: SearchResult): PhotoItem {
  return {
    id: result.node.id,
    libraryId: result.node.libraryId,
    parentId: result.node.parentId,
    name: result.node.name,
    kind: result.kind!,
    mediaType: result.node.mediaType,
    size: result.node.size,
    capturedAt: result.capturedAt!,
    width: result.width,
    height: result.height,
    durationMs: null,
    thumbnailUrl: result.thumbnailUrl!,
  };
}

/**
 * One field for names, types and dates (UX.md Search): "beach 2024" finds
 * names with "beach" from 2024, "May 2024" or "maj 2024" a month. Chips
 * narrow by type. Photos and videos show as a grid, everything else as a
 * list that leads to its folder.
 */
export function Search() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const [text, setText] = useState(params.get("q") ?? "");
  const [query, setQuery] = useState(params.get("q") ?? "");
  const type = (params.get("type") as SearchType | null) ?? null;
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [open, setOpen] = useState<number | null>(null);

  // Search as the person types, once they pause.
  useEffect(() => {
    const timer = setTimeout(() => setQuery(text.trim()), 300);
    return () => clearTimeout(timer);
  }, [text]);

  useEffect(() => {
    const next = new URLSearchParams(params);
    if (query) {
      next.set("q", query);
    } else {
      next.delete("q");
    }

    if (next.toString() !== params.toString()) {
      setParams(next, { replace: true });
    }
  }, [query, params, setParams]);

  const parsed = useMemo(() => parseSearch(query, currentLocale()), [query]);
  const active = query !== "" || type !== null;
  const results = useInfiniteQuery({
    queryKey: ["search", parsed, type],
    enabled: active,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      call(
        api.GET("/api/v1/search", {
          params: {
            query: {
              limit: 100,
              ...(parsed.words ? { q: parsed.words } : {}),
              ...(type ? { type } : {}),
              ...(parsed.range ? { from: parsed.range.from, to: parsed.range.to } : {}),
              ...(pageParam ? { cursor: pageParam } : {}),
            },
          },
        }),
      ),
    getNextPageParam: (page) => page.nextCursor ?? null,
  });
  const all = useMemo(() => results.data?.pages.flatMap((p) => p.items) ?? [], [results.data]);
  const photos = useMemo(() => all.filter((r) => r.kind && r.thumbnailUrl && r.capturedAt).map(asPhoto), [all]);
  const others = all.filter((r) => !r.kind);

  const chooseType = (value: SearchType | null): void => {
    const next = new URLSearchParams(params);
    if (value) {
      next.set("type", value);
    } else {
      next.delete("type");
    }

    setParams(next, { replace: true });
  };

  return (
    <div className="photos">
      <PageHeader title={t("search.title")} />
      <PhotoTabs />
      <form className="search-form" role="search" onSubmit={(event) => event.preventDefault()}>
        <label className="search-field">
          <Icon name="search" size={18} />
          <span className="visually-hidden">{t("search.label")}</span>
          <input
            type="search"
            value={text}
            placeholder={t("search.placeholder")}
            onChange={(e) => setText(e.target.value)}
            autoFocus
          />
        </label>
        <div className="chips" role="group" aria-label={t("search.type")}>
          {TYPES.map((value) => (
            <button
              key={value}
              type="button"
              className={type === value ? "chip is-on" : "chip"}
              aria-pressed={type === value}
              onClick={() => chooseType(type === value ? null : value)}
            >
              {t(`search.types.${value}`)}
            </button>
          ))}
          {parsed.range ? (
            <span className="chip is-on is-static">
              {formatDay(parsed.range.from.slice(0, 10))} –{" "}
              {formatDay(new Date(Date.parse(parsed.range.to) - 86_400_000).toISOString().slice(0, 10))}
            </span>
          ) : null}
        </div>
      </form>

      <ErrorMessage error={results.error} />
      <SelectionBar
        selected={selected}
        onClear={() => setSelected(new Set())}
        onChanged={() => results.refetch().then(() => undefined)}
      />
      {!active ? <p className="muted">{t("search.hint")}</p> : null}
      {active && results.isSuccess && all.length === 0 ? <p className="muted">{t("search.nothing")}</p> : null}

      {photos.length > 0 ? (
        <PhotoGrid items={photos} selected={selected} onSelect={setSelected} onOpen={setOpen} />
      ) : null}
      <OtherResults results={others} />
      {results.hasNextPage ? (
        <LoadMore onVisible={() => void results.fetchNextPage()} busy={results.isFetchingNextPage} />
      ) : null}

      {open !== null && photos[open] ? (
        <Viewer items={photos} index={open} onIndex={setOpen} onClose={() => setOpen(null)} />
      ) : null}
    </div>
  );
}

/** Results that are not photos or videos: a list, each leading to its folder. */
/** Files and folders that are not photos: a file opens where it is shown, a folder opens in Files. */
export function OtherResults({ results }: { readonly results: readonly SearchResult[] }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState<number | null>(null);
  const files = results.filter((r) => r.node.kind === "file").map((r) => r.node);
  if (results.length === 0) {
    return null;
  }

  return (
    <>
      {open !== null && files[open] ? (
        <FilePreview files={files} index={open} onIndex={setOpen} onClose={() => setOpen(null)} />
      ) : null}
      <ul className="rows search-others">
        {results.map((r) => (
          <li key={r.node.id} className="row">
            <span className="row-icon">
              <Icon name={r.node.kind === "folder" ? "folder" : "file"} size={20} />
            </span>
            <div className="row-main">
              <p className="row-title">
                {r.node.kind === "folder" ? (
                  <Link to={`/files/${r.node.id}`}>{r.node.name}</Link>
                ) : (
                  <button
                    type="button"
                    className="link-button"
                    onClick={() => setOpen(files.findIndex((f) => f.id === r.node.id))}
                  >
                    {r.node.name}
                  </button>
                )}
              </p>
              <p className="row-meta">
                {r.node.kind === "file" ? (
                  <>
                    {formatBytes(Number(r.node.size))}
                    <span className="dot" />
                    <Link to={`/files/${r.node.parentId ?? ""}`}>{t("preview.showInFolder")}</Link>
                  </>
                ) : (
                  t("files.folder")
                )}
              </p>
            </div>
          </li>
        ))}
      </ul>
    </>
  );
}
