import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Button } from "../components/ui.tsx";

/**
 * Chooses a folder to move or copy into: browse down from the library's
 * top, go back up with the breadcrumbs, confirm the folder shown.
 */
export function FolderPicker({
  title,
  confirm,
  rootId,
  startId,
  excluded,
  onPick,
  onClose,
}: {
  readonly title: string;
  readonly confirm: string;
  readonly rootId: string;
  readonly startId: string;
  /** The items being moved: a folder cannot go into itself. */
  readonly excluded: ReadonlySet<string>;
  readonly onPick: (folderId: string) => void;
  readonly onClose: () => void;
}) {
  const { t } = useTranslation();
  const [folderId, setFolderId] = useState(startId);
  const path = useQuery({
    queryKey: ["path", folderId],
    queryFn: () => call(api.GET("/api/v1/nodes/{id}/path", { params: { path: { id: folderId } } })),
  });
  const current = useQuery({
    queryKey: ["node", folderId],
    queryFn: () => call(api.GET("/api/v1/nodes/{id}", { params: { path: { id: folderId } } })),
  });
  const folders = useQuery({
    queryKey: ["children", folderId, "folders"],
    queryFn: () =>
      call(
        api.GET("/api/v1/nodes/{id}/children", {
          params: { path: { id: folderId }, query: { kind: "folder", limit: 1000 } },
        }),
      ),
  });

  const crumbs = [...(path.data ?? []), ...(current.data ? [current.data] : [])];
  return (
    <Dialog
      title={title}
      onClose={onClose}
      wide
      actions={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button variant="primary" disabled={excluded.has(folderId)} onClick={() => onPick(folderId)}>
            {confirm}
          </Button>
        </>
      }
    >
      <nav className="crumbs" aria-label={t("files.location")}>
        {crumbs.map((node, index) => (
          <span key={node.id} className="crumb">
            {index > 0 ? <Icon name="chevronRight" size={14} /> : null}
            <button type="button" className="crumb-button" onClick={() => setFolderId(node.id)}>
              {node.id === rootId ? t("files.myFiles") : node.name}
            </button>
          </span>
        ))}
      </nav>
      <ul className="picker-list">
        {folders.data?.items.length === 0 ? <li className="muted">{t("files.noSubfolders")}</li> : null}
        {folders.data?.items.map((folder) => (
          <li key={folder.id}>
            <button
              type="button"
              className="picker-item"
              disabled={excluded.has(folder.id)}
              onClick={() => setFolderId(folder.id)}
            >
              <Icon name="folder" size={18} />
              <span>{folder.name}</span>
              <Icon name="chevronRight" size={16} />
            </button>
          </li>
        ))}
      </ul>
    </Dialog>
  );
}
