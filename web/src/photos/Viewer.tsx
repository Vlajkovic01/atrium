import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call, currentAccessToken, refreshAccessToken } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage } from "../components/ui.tsx";
import { downloadFile } from "../files/actions.ts";
import { formatBytes, formatDateTime } from "../i18n.ts";
import { formatDuration, type PhotoItem } from "./PhotoGrid.tsx";

/**
 * The large image of a photo, fetched with the session's token and shown
 * from memory: the signed URL of the grid is for the small size only.
 */
function useLargeImage(id: string, enabled: boolean): string | null {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!enabled) {
      return;
    }

    let objectUrl: string | null = null;
    let cancelled = false;
    const load = async (retry: boolean): Promise<void> => {
      const response = await fetch(`/api/v1/nodes/${id}/thumbnail?size=large`, {
        headers: { Authorization: `Bearer ${currentAccessToken() ?? ""}` },
      });
      if (response.status === 401 && retry && (await refreshAccessToken())) {
        return load(false);
      }

      if (!response.ok || cancelled) {
        return;
      }

      objectUrl = URL.createObjectURL(await response.blob());
      setUrl(objectUrl);
    };
    setUrl(null);
    void load(true);
    return () => {
      cancelled = true;
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
    };
  }, [id, enabled]);
  return url;
}

/**
 * Full-window viewer (UX.md Photos): previous and next with the arrow
 * keys, Escape closes, the information panel shows what was read from the
 * file. Videos play the original file.
 */
export function Viewer(props: {
  readonly items: readonly PhotoItem[];
  readonly index: number;
  readonly onIndex: (index: number) => void;
  readonly onClose: () => void;
  readonly actions?: (item: PhotoItem) => React.ReactNode;
}) {
  const { t } = useTranslation();
  const item = props.items[props.index]!;
  const [info, setInfo] = useState(false);
  const dialog = useRef<HTMLDivElement>(null);
  const large = useLargeImage(item.id, item.kind === "photo");
  const video = useQuery({
    queryKey: ["video-url", item.id],
    enabled: item.kind === "video",
    queryFn: () =>
      call(
        api.POST("/api/v1/nodes/{id}/content-url", {
          params: { path: { id: item.id } },
          body: { inline: true, versionId: null },
        }),
      ),
    staleTime: 30 * 60 * 1000,
  });
  const details = useQuery({
    queryKey: ["media-info", item.id],
    enabled: info,
    queryFn: () => call(api.GET("/api/v1/nodes/{id}/media-info", { params: { path: { id: item.id } } })),
    // A photo just uploaded is still being read; its details follow shortly.
    refetchInterval: (query) => (query.state.data?.thumbnailState === "pending" ? 2000 : false),
  });
  const width = details.data?.width ?? item.width;
  const height = details.data?.height ?? item.height;

  useEffect(() => {
    dialog.current?.focus();
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        props.onClose();
      } else if (event.key === "ArrowLeft" && props.index > 0) {
        props.onIndex(props.index - 1);
      } else if (event.key === "ArrowRight" && props.index < props.items.length - 1) {
        props.onIndex(props.index + 1);
      } else if (event.key === "i") {
        setInfo((value) => !value);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [props]);

  return (
    <div className="viewer" role="dialog" aria-modal="true" aria-label={item.name} ref={dialog} tabIndex={-1}>
      <div className="viewer-bar">
        <button type="button" className="icon-button" aria-label={t("common.close")} onClick={props.onClose}>
          <Icon name="close" />
        </button>
        <span className="viewer-title">{item.name}</span>
        <div className="viewer-actions">
          {props.actions?.(item)}
          <Button size="sm" variant="ghost" icon="download" onClick={() => void downloadFile(item.id)}>
            {t("files.download")}
          </Button>
          <Button size="sm" variant="ghost" icon="info" aria-pressed={info} onClick={() => setInfo((value) => !value)}>
            {t("photos.info")}
          </Button>
        </div>
      </div>

      <div className="viewer-stage">
        {props.index > 0 ? (
          <button
            type="button"
            className="viewer-nav is-prev"
            aria-label={t("photos.previous")}
            onClick={() => props.onIndex(props.index - 1)}
          >
            <Icon name="chevronLeft" size={28} />
          </button>
        ) : null}
        {item.kind === "video" ? (
          video.data ? (
            <video key={item.id} className="viewer-media" src={video.data.url} controls autoPlay playsInline />
          ) : (
            <ErrorMessage error={video.error} />
          )
        ) : (
          <img key={item.id} className="viewer-media" src={large ?? item.thumbnailUrl} alt={item.name} />
        )}
        {props.index < props.items.length - 1 ? (
          <button
            type="button"
            className="viewer-nav is-next"
            aria-label={t("photos.next")}
            onClick={() => props.onIndex(props.index + 1)}
          >
            <Icon name="chevronRight" size={28} />
          </button>
        ) : null}

        {info ? (
          <aside className="viewer-info" aria-label={t("photos.info")}>
            <dl>
              <dt>{t("photos.taken")}</dt>
              <dd>{formatDateTime(item.capturedAt.replace(/Z$|[+-]\d\d:\d\d$/, ""))}</dd>
              {details.data?.cameraMake || details.data?.cameraModel ? (
                <>
                  <dt>{t("photos.camera")}</dt>
                  <dd>{[details.data.cameraMake, details.data.cameraModel].filter(Boolean).join(" ")}</dd>
                </>
              ) : null}
              {width && height ? (
                <>
                  <dt>{t("photos.dimensions")}</dt>
                  <dd>
                    {Number(width)} × {Number(height)}
                  </dd>
                </>
              ) : null}
              {item.durationMs != null ? (
                <>
                  <dt>{t("photos.duration")}</dt>
                  <dd>{formatDuration(Number(item.durationMs))}</dd>
                </>
              ) : null}
              <dt>{t("photos.fileSize")}</dt>
              <dd>{formatBytes(Number(item.size))}</dd>
              {details.data?.format ? (
                <>
                  <dt>{t("photos.format")}</dt>
                  <dd>
                    {[details.data.format, details.data.videoCodec, details.data.audioCodec].filter(Boolean).join(", ")}
                  </dd>
                </>
              ) : null}
            </dl>
          </aside>
        ) : null}
      </div>
    </div>
  );
}
