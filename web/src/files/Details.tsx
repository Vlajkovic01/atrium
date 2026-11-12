import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Button, ErrorMessage } from "../components/ui.tsx";
import { downloadFile, type NodeInfo } from "./actions.ts";
import { formatBytes, formatDateTime } from "./format.ts";

/**
 * Facts about one item and, for a file, its earlier versions: each can be
 * downloaded, made current again (the current content is kept as a
 * version in turn) or removed.
 */
export function Details({ node, onClose }: { readonly node: NodeInfo; readonly onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const isFile = node.kind === "file";
  const versions = useQuery({
    queryKey: ["versions", node.id],
    enabled: isFile,
    queryFn: () => call(api.GET("/api/v1/nodes/{id}/versions", { params: { path: { id: node.id } } })),
  });

  const act = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(e);
    } finally {
      await queryClient.invalidateQueries({ queryKey: ["versions", node.id] });
      await queryClient.invalidateQueries({ queryKey: ["children", node.parentId] });
    }
  };

  return (
    <aside className="details" aria-label={t("files.details")}>
      <header className="details-header">
        <Icon name={isFile ? "file" : "folder"} size={22} />
        <h2 className="details-title">{node.name}</h2>
        <button type="button" className="icon-button" aria-label={t("common.close")} onClick={onClose}>
          <Icon name="close" size={18} />
        </button>
      </header>
      <dl className="facts">
        <dt>{t("files.kind")}</dt>
        <dd>{isFile ? (node.mediaType ?? t("files.file")) : t("files.folder")}</dd>
        {isFile ? (
          <>
            <dt>{t("files.size")}</dt>
            <dd>{formatBytes(Number(node.size))}</dd>
          </>
        ) : null}
        <dt>{t("files.modified")}</dt>
        <dd>{formatDateTime(node.modifiedAt)}</dd>
        <dt>{t("files.added")}</dt>
        <dd>{formatDateTime(node.createdAt)}</dd>
      </dl>
      <ErrorMessage error={error} />
      {isFile ? (
        <section>
          <h3 className="section-title">{t("files.versions")}</h3>
          {versions.data?.length === 0 ? <p className="muted">{t("files.noVersions")}</p> : null}
          <ul className="rows">
            {versions.data?.map((version) => (
              <li key={version.id} className="row is-stacked">
                <div className="row-line">
                  <div className="row-main">
                    <p className="row-title">{formatDateTime(version.modifiedAt)}</p>
                    <p className="row-meta">
                      {formatBytes(Number(version.size))}
                      <span className="dot" />
                      {t("files.replacedAt", { when: formatDateTime(version.replacedAt) })}
                    </p>
                  </div>
                </div>
                <div className="row-actions">
                  <Button
                    size="sm"
                    variant="ghost"
                    icon="download"
                    onClick={() => void act(() => downloadFile(node.id, version.id))}
                  >
                    {t("files.download")}
                  </Button>
                  {node.canWrite ? (
                    <>
                      <Button
                        size="sm"
                        variant="ghost"
                        icon="restore"
                        onClick={() =>
                          void act(() =>
                            call(
                              api.POST("/api/v1/nodes/{id}/versions/{versionId}/restore", {
                                params: { path: { id: node.id, versionId: version.id } },
                              }),
                            ),
                          )
                        }
                      >
                        {t("files.restoreVersion")}
                      </Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        icon="trash"
                        onClick={() => {
                          if (window.confirm(t("files.deleteVersionConfirm"))) {
                            void act(() =>
                              call(
                                api.DELETE("/api/v1/nodes/{id}/versions/{versionId}", {
                                  params: { path: { id: node.id, versionId: version.id } },
                                }),
                              ),
                            );
                          }
                        }}
                      >
                        {t("files.deleteVersion")}
                      </Button>
                    </>
                  ) : null}
                </div>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </aside>
  );
}
