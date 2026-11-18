import type { components } from "../api/schema.d.ts";

type NodeInfo = components["schemas"]["NodeInfo"];
type Described = Pick<NodeInfo, "name" | "mediaType">;

/** How a browser can show a file by itself (D-044). */
export type PreviewKind = "image" | "video" | "audio" | "pdf" | "text" | "office" | "none";

/** What a file is, whether or not a client can show it: chooses its icon and its words. */
export type FileCategory = "image" | "video" | "audio" | "pdf" | "text" | "document" | "archive" | "other";

/** Text is shown up to this size; a larger file would freeze the screen. */
export const TEXT_PREVIEW_LIMIT = 2 * 1024 * 1024;

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
const ARCHIVE_EXTENSIONS = new Set(["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso"]);
// What browsers play without help; other formats wait for streaming (Phase 5).
const VIDEO_TYPES = new Set(["video/mp4", "video/webm", "video/quicktime", "video/x-m4v"]);
const AUDIO_TYPES = new Set(["audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav", "audio/flac", "audio/x-wav"]);
const IMAGE_TYPES = new Set(["image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/bmp"]);

/** The part of a name after its last dot, in lower case; the whole name when it has no dot (".gitignore", "Makefile"). */
export function fileExtension(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot < 0 ? name.toLowerCase() : name.slice(dot + 1).toLowerCase();
}

function isText(type: string, ext: string): boolean {
  return type.startsWith("text/") || type === "application/json" || TEXT_EXTENSIONS.has(ext);
}

/** How a file can be shown in a browser, from its type and its name. */
export function previewKind(node: Pick<NodeInfo, "name" | "mediaType" | "size">): PreviewKind {
  const type = (node.mediaType ?? "").toLowerCase();
  const ext = fileExtension(node.name);
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

  if (isText(type, ext) && Number(node.size) <= TEXT_PREVIEW_LIMIT) {
    return "text";
  }

  return "none";
}

/** What a file is, from its type and its name. Says nothing about whether it can be shown. */
export function fileCategory(node: Described): FileCategory {
  const type = (node.mediaType ?? "").toLowerCase();
  const ext = fileExtension(node.name);
  if (type.startsWith("image/")) {
    return "image";
  }

  if (type.startsWith("video/")) {
    return "video";
  }

  if (type.startsWith("audio/")) {
    return "audio";
  }

  if (type === "application/pdf" || ext === "pdf") {
    return "pdf";
  }

  if (OFFICE_EXTENSIONS.has(ext)) {
    return "document";
  }

  if (ARCHIVE_EXTENSIONS.has(ext)) {
    return "archive";
  }

  return isText(type, ext) ? "text" : "other";
}
