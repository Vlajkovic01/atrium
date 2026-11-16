import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage } from "../components/ui.tsx";
import { formatBytes } from "../i18n.ts";
import { downloadFile, type NodeInfo } from "./actions.ts";

export type PreviewKind = "image" | "video" | "audio" | "pdf" | "text" | "office" | "none";

/** Text is shown up to this size; a larger file would freeze the page. */
const TEXT_LIMIT = 2 * 1024 * 1024;

const TEXT_EXTENSIONS = new Set([
  "txt",
  "md",
  "markdown",
  "csv",
  "tsv",
  "log",
  "json",
  "xml",
  "yaml",
  "yml",
  "toml",
  "ini",
  "cfg",
  "conf",
  "env",
  "html",
  "htm",
  "css",
  "js",
  "mjs",
  "cjs",
  "ts",
  "tsx",
  "jsx",
  "py",
  "rb",
  "go",
  "rs",
  "java",
  "kt",
  "c",
  "h",
  "cpp",
  "hpp",
  "cs",
  "php",
  "sh",
  "bash",
  "ps1",
  "bat",
  "sql",
  "srt",
  "vtt",
  "ass",
  "tex",
  "gitignore",
]);
const OFFICE_EXTENSIONS = new Set(["doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf"]);
// What browsers play without help; other formats wait for streaming (Phase 5).
const VIDEO_TYPES = new Set(["video/mp4", "video/webm", "video/quicktime", "video/x-m4v"]);
const AUDIO_TYPES = new Set(["audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav", "audio/flac", "audio/x-wav"]);
const IMAGE_TYPES = new Set(["image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/bmp"]);

function extension(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot < 0 ? name.toLowerCase() : name.slice(dot + 1).toLowerCase();
}

/** How a file can be shown in the browser, from its type and its name. */
export function previewKind(node: Pick<NodeInfo, "name" | "mediaType" | "size">): PreviewKind {
  const type = (node.mediaType ?? "").toLowerCase();
  const ext = extension(node.name);
  if (IMAGE_TYPES.has(type)) {
    return "image";
  }

  if (VIDEO_TYPES.has(type)) {
    return "video";
  }

  if (AUDIO_TYPES.has(type)) {
    return "audio";
  }

  if (type === "application/pdf" || ext === "pdf") {
    return "pdf";
  }

  if (OFFICE_EXTENSIONS.has(ext)) {
    return "office";
  }

  if (
    (type.startsWith("text/") || TEXT_EXTENSIONS.has(ext) || type === "application/json") &&
    Number(node.size) <= TEXT_LIMIT
  ) {
    return "text";
  }

  return "none";
}

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
            aria-label={t("photos.previous")}
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
            aria-label={t("photos.next")}
            onClick={() => props.onIndex(props.index + 1)}
          >
            <Icon name="chevronRight" size={28} />
          </button>
        ) : null}
      </div>
    </div>
  );
}
