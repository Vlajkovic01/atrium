import { fileCategory, type NodeInfo } from "@atrium/shared";

import type { IconName } from "../components/Icon.tsx";

const categoryIcons = {
  image: "image",
  video: "video",
  audio: "music",
  pdf: "book",
  text: "text",
  document: "text",
  archive: "archive",
  other: "file",
} as const satisfies Record<ReturnType<typeof fileCategory>, IconName>;

/** The icon of an item in every list of the app: a folder, or what kind of file it is. */
export function nodeIcon(node: Pick<NodeInfo, "kind" | "name" | "mediaType">): IconName {
  return node.kind === "folder" ? "folder" : categoryIcons[fileCategory(node)];
}
