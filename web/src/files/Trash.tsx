import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import { api, ApiError, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage, Notice, PageHeader } from "../components/ui.tsx";
import { FolderPicker } from "./FolderPicker.tsx";
import { daysUntil, formatBytes, formatDay } from "./format.ts";

/**
 * The trash (UX.md "Trash"): what was deleted, when it goes for good, and
 * restore. Deleting permanently asks first and says how much will go.
 */
export function Trash() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const [confirm, setConfirm] = useState<{ ids: string[] | "all"; count: number; bytes: number } | null>(null);
  const [placing, setPlacing] = useState<string | null>(null);
  const trash = useQuery({ queryKey: ["trash"], queryFn: () => call(api.GET("/api/v1/trash")) });
  const libraries = useQuery({ queryKey: ["libraries"], queryFn: () => call(api.GET("/api/v1/libraries")) });
  const rootId = libraries.data?.[0]?.rootNodeId;

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ["trash"] });
    await queryClient.invalidateQueries({ queryKey: ["children"] });
    await queryClient.invalidateQueries({ queryKey: ["usage"] });
  };

  const act = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(e);
    } finally {
      await refresh();
    }
  };

  const restore = (id: string, parentId: string | null) =>
    act(async () => {
      try {
        await call(
          api.POST("/api/v1/trash/{id}/restore", {
            params: { path: { id } },
            body: { parentId, onConflict: "rename" },
          }),
        );
      } catch (e) {
        // The folder it came from is in the trash too: ask where to put it.
        if (parentId === null && e instanceof ApiError && e.problem.code === "node.in_trash") {
          setPlacing(id);
          return;
        }

        throw e;
      }
    });

  const items = trash.data ?? [];
  const totals = {
    count: items.reduce((n, i) => n + Number(i.itemCount), 0),
    bytes: items.reduce((n, i) => n + Number(i.bytes), 0),
  };

  return (
    <>
      <PageHeader
        title={t("trash.title")}
        lead={t("trash.lead")}
        actions={
          items.length > 0 ? (
            <Button variant="danger" icon="trash" onClick={() => setConfirm({ ids: "all", ...totals })}>
              {t("trash.empty")}
            </Button>
          ) : null
        }
      />
      <ErrorMessage error={error} />
      {trash.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {trash.isSuccess && items.length === 0 ? (
        <div className="empty">
          <Icon name="trash" size={36} />
          <p>{t("trash.nothing")}</p>
        </div>
      ) : null}
      <ul className="rows">
        {items.map((item) => (
          <li key={item.node.id} className="row">
            <span className="row-icon">
              <Icon name={item.node.kind === "folder" ? "folder" : "file"} />
            </span>
            <div className="row-main">
              <p className="row-title">{item.node.name}</p>
              <p className="row-meta trash-origin">
                <span>{t("trash.from")}</span>
                {item.location.map((folder, index) => {
                  const label = index === 0 ? t("files.myFiles") : folder.name;
                  return (
                    <span key={folder.id} className="crumb">
                      {index > 0 ? <Icon name="chevronRight" size={12} /> : null}
                      {folder.inTrash ? (
                        <span title={t("trash.folderInTrash")}>{label}</span>
                      ) : (
                        <Link to={index === 0 ? "/files" : `/files/${folder.id}`}>{label}</Link>
                      )}
                    </span>
                  );
                })}
              </p>
              <p className="row-meta">
                {formatBytes(Number(item.bytes))}
                {Number(item.itemCount) > 1 ? (
                  <>
                    <span className="dot" />
                    {t("trash.items", { count: item.itemCount })}
                  </>
                ) : null}
                <span className="dot" />
                {t("trash.deletedOn", { when: formatDay(item.node.trashedAt!) })}
                <span className="dot" />
                {t("trash.daysLeft", { count: daysUntil(item.deleteAt) })}
              </p>
            </div>
            <div className="row-actions">
              <Button size="sm" variant="ghost" icon="restore" onClick={() => void restore(item.node.id, null)}>
                {t("trash.restore")}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                icon="trash"
                onClick={() =>
                  setConfirm({ ids: [item.node.id], count: Number(item.itemCount), bytes: Number(item.bytes) })
                }
              >
                {t("trash.deleteForever")}
              </Button>
            </div>
          </li>
        ))}
      </ul>

      {confirm ? (
        <Dialog
          title={confirm.ids === "all" ? t("trash.emptyTitle") : t("trash.deleteTitle")}
          onClose={() => setConfirm(null)}
          actions={
            <>
              <Button variant="ghost" onClick={() => setConfirm(null)}>
                {t("common.cancel")}
              </Button>
              <Button
                variant="danger"
                onClick={() => {
                  const target = confirm.ids;
                  setConfirm(null);
                  void act(async () => {
                    if (target === "all") {
                      await call(api.DELETE("/api/v1/trash"));
                    } else {
                      for (const id of target) {
                        await call(api.DELETE("/api/v1/trash/{id}", { params: { path: { id } } }));
                      }
                    }
                  });
                }}
              >
                {t("trash.deleteForever")}
              </Button>
            </>
          }
        >
          <Notice kind="warning">
            {t("trash.confirm", { count: confirm.count, size: formatBytes(confirm.bytes) })}
          </Notice>
        </Dialog>
      ) : null}

      {placing && rootId ? (
        <FolderPicker
          title={t("trash.restoreTo")}
          confirm={t("trash.restoreHere")}
          rootId={rootId}
          startId={rootId}
          excluded={new Set()}
          onClose={() => setPlacing(null)}
          onPick={(target) => {
            const id = placing;
            setPlacing(null);
            void restore(id, target);
          }}
        />
      ) : null}
    </>
  );
}
