import { previewKind } from "@atrium/shared";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage } from "../components/ui.tsx";
import { formatBytes } from "../i18n.ts";
import { downloadFile, type NodeInfo } from "./actions.ts";

function TextBody({ url }: { readonly url: string }) {
  const { t } = useTranslation();
  const text = useQuery({
    queryKey: ["preview-text", url],
    queryFn: async () => {
      const response = await fetch(url);
      if (!response.ok) {
        throw new Error(t("errors.unknown"));
      }

      return response.text();
    },
    staleTime: Infinity,
  });
  if (text.isError) {
    return <ErrorMessage error={text.error} />;
  }

  return text.data === undefined ? (
    <p className="muted">{t("common.loading")}</p>
  ) : (
    <pre className="preview-text" tabIndex={0}>
      {text.data}
    </pre>
  );
}

/**
 * Shows a file of any kind the browser can show by itself (D-044):
 * pictures, playable video and audio, PDF and text or code. For anything
 * else it says so and offers the download. Arrows move through the
 * folder's files, Escape closes. Nothing is converted on the server.
 */
export function FilePreview(props: {
  readonly files: readonly NodeInfo[];
  readonly index: number;
  readonly onIndex: (index: number) => void;
  readonly onClose: () => void;
  readonly onDetails?: (node: NodeInfo) => void;
}) {
  const { t } = useTranslation();
  const node = props.files[props.index]!;
  const kind = previewKind(node);
  const root = useRef<HTMLDivElement>(null);
  const content = useQuery({
    queryKey: ["content-url", node.id, node.version],
    enabled: kind !== "none" && kind !== "office",
    queryFn: () =>
      call(
        api.POST("/api/v1/nodes/{id}/content-url", {
          params: { path: { id: node.id } },
          body: { inline: true, versionId: null },
        }),
      ),
    staleTime: 30 * 60 * 1000,
  });

  useEffect(() => {
    root.current?.focus();
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        props.onClose();
      } else if (event.key === "ArrowLeft" && props.index > 0) {
        props.onIndex(props.index - 1);
      } else if (event.key === "ArrowRight" && props.index < props.files.length - 1) {
        props.onIndex(props.index + 1);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [props]);

  const url = content.data?.url;
  const body = () => {
    if (kind === "none" || kind === "office") {
      return (
        <div className="preview-none">
          <Icon name="file" size={48} />
          <p>{t(kind === "office" ? "preview.office" : "preview.none")}</p>
          <Button variant="primary" icon="download" onClick={() => void downloadFile(node.id)}>
            {t("files.download")}
          </Button>
        </div>
      );
    }

    if (content.isError) {
      return <ErrorMessage error={content.error} />;
    }

    if (!url) {
      return <p className="muted">{t("common.loading")}</p>;
    }

    switch (kind) {
      case "image":
        return <img key={node.id} className="viewer-media" src={url} alt={node.name} />;
      case "video":
        return <video key={node.id} className="viewer-media" src={url} controls autoPlay playsInline />;
      case "audio":
        return (
          <div className="preview-audio">
            <Icon name="music" size={56} />
            <audio key={node.id} src={url} controls autoPlay />
          </div>
        );
      case "pdf":
        return <iframe key={node.id} className="preview-frame" src={url} title={node.name} />;
      default:
        return <TextBody url={url} />;
    }
  };

  return (
    <div className="viewer" role="dialog" aria-modal="true" aria-label={node.name} ref={root} tabIndex={-1}>
      <div className="viewer-bar">
        <button type="button" className="icon-button" aria-label={t("common.close")} onClick={props.onClose}>
          <Icon name="close" />
        </button>
        <span className="viewer-title">
          {node.name}
          <span className="muted"> · {formatBytes(Number(node.size))}</span>
        </span>
        <div className="viewer-actions">
          {props.onDetails ? (
            <Button size="sm" variant="ghost" icon="info" onClick={() => props.onDetails!(node)}>
              {t("preview.details")}
            </Button>
          ) : null}
          <Button size="sm" variant="ghost" icon="download" onClick={() => void downloadFile(node.id)}>
            {t("files.download")}
          </Button>
        </div>
      </div>
      <div className={`viewer-stage is-${kind}`}>
        {props.index > 0 ? (
          <button
            type="button"
            className="viewer-nav is-prev"
            aria-label={t("preview.previous")}
            onClick={() => props.onIndex(props.index - 1)}
          >
            <Icon name="chevronLeft" size={28} />
          </button>
        ) : null}
        {body()}
        {props.index < props.files.length - 1 ? (
          <button
            type="button"
            className="viewer-nav is-next"
            aria-label={t("preview.next")}
            onClick={() => props.onIndex(props.index + 1)}
          >
            <Icon name="chevronRight" size={28} />
          </button>
        ) : null}
      </div>
    </div>
  );
}
