import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Badge, Button, Card, ErrorMessage, Notice, PageHeader } from "../components/ui.tsx";
import { formatAgo, formatBytes, formatDateTime } from "../i18n.ts";

const POLL = 2000;

/** Follows a job until it ends; returns whether it is still going and how it ended. */
function useJob(id: string | null) {
  const job = useQuery({
    queryKey: ["job", id],
    enabled: id !== null,
    queryFn: () => call(api.GET("/api/v1/jobs/{id}", { params: { path: { id: id! } } })),
    refetchInterval: (query) => {
      const state = query.state.data?.state;
      return state === "queued" || state === "running" ? POLL : false;
    },
  });
  const state = job.data?.state;
  return { busy: id !== null && (state === undefined || state === "queued" || state === "running"), state };
}

/**
 * Backups and the storage check (D-042, UX.md). A backup holds the
 * database: names, folders, people and settings, not the files themselves,
 * which stay on their storage locations. Restoring is a command-line step
 * until Phase 11, and the page says so.
 */
export function Backups() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [backupJob, setBackupJob] = useState<string | null>(null);
  const [checkJob, setCheckJob] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  const backup = useJob(backupJob);
  const check = useJob(checkJob);
  const backups = useQuery({
    queryKey: ["admin-backups", backup.state],
    queryFn: () => call(api.GET("/api/v1/admin/backups")),
  });
  const integrity = useQuery({
    queryKey: ["admin-integrity", check.state],
    queryFn: () => call(api.GET("/api/v1/admin/integrity")),
    refetchInterval: (query) => (query.state.data?.runningJobId ? POLL : false),
  });

  const start = async (action: () => Promise<{ jobId: string }>, follow: (id: string) => void): Promise<void> => {
    setError(null);
    try {
      follow((await action()).jobId);
      await queryClient.invalidateQueries({ queryKey: ["admin-overview"] });
    } catch (e) {
      setError(e);
    }
  };

  const report = integrity.data?.report;
  const checking = check.busy || !!integrity.data?.runningJobId;
  const list = [...(backups.data ?? [])].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));

  return (
    <>
      <PageHeader title={t("admin.section.backups")} lead={t("admin.backups.lead")} />
      <ErrorMessage error={error ?? backups.error ?? integrity.error} />

      <Card
        title={t("admin.backups.heading")}
        icon="shield"
        aside={
          <Button
            variant="primary"
            icon="plus"
            disabled={backup.busy}
            onClick={() => void start(() => call(api.POST("/api/v1/admin/backups")), setBackupJob)}
          >
            {backup.busy ? t("admin.backups.running") : t("admin.backups.now")}
          </Button>
        }
      >
        <p className="card-lead">{t("admin.backups.what")}</p>
        {backup.state === "succeeded" ? <Notice kind="ok">{t("admin.backups.done")}</Notice> : null}
        {backup.state === "failed" ? <Notice kind="error">{t("admin.backups.failed")}</Notice> : null}
        {list.length === 0 && backups.isSuccess ? <p className="muted">{t("admin.backups.never")}</p> : null}
        <ul className="rows">
          {list.map((item) => (
            <li key={item.name} className="row">
              <div className="row-main">
                <p className="row-title">
                  {formatDateTime(item.createdAt)}
                  <Badge tone="muted">{t(`admin.backups.reason.${item.reason}`, { defaultValue: item.reason })}</Badge>
                </p>
                <p className="row-meta">
                  {formatAgo(item.createdAt)}
                  <span className="dot" />
                  {formatBytes(Number(item.size))}
                </p>
              </div>
            </li>
          ))}
        </ul>
        <p className="help">{t("admin.backups.restore")}</p>
      </Card>

      <Card
        title={t("admin.integrity.heading")}
        icon="checkCircle"
        aside={
          <Button
            icon="refresh"
            disabled={checking}
            onClick={() => void start(() => call(api.POST("/api/v1/admin/integrity/check")), setCheckJob)}
          >
            {checking ? t("admin.integrity.running") : t("admin.integrity.now")}
          </Button>
        }
      >
        <p className="card-lead">{t("admin.integrity.what")}</p>
        {report ? (
          <>
            <dl className="facts">
              <dt>{t("admin.integrity.last")}</dt>
              <dd>{formatDateTime(report.finishedAt ?? report.startedAt)}</dd>
              <dt>{t("admin.integrity.checked")}</dt>
              <dd>
                {t("admin.integrity.checkedValue", {
                  count: Number(report.blobsChecked),
                  size: formatBytes(Number(report.bytesChecked)),
                })}
              </dd>
              <dt>{t("admin.integrity.missing")}</dt>
              <dd className={Number(report.missing) > 0 ? "is-danger" : undefined}>{Number(report.missing)}</dd>
              <dt>{t("admin.integrity.damaged")}</dt>
              <dd className={Number(report.damaged) > 0 ? "is-danger" : undefined}>{Number(report.damaged)}</dd>
              <dt>{t("admin.integrity.unknown")}</dt>
              <dd>{Number(report.unknown)}</dd>
            </dl>
            {Number(report.missing) + Number(report.damaged) === 0 ? (
              <Notice kind="ok">{t("admin.integrity.clean")}</Notice>
            ) : (
              <Notice kind="warning">{t("admin.integrity.explainProblems")}</Notice>
            )}
            {report.issues.length > 0 ? (
              <ul className="rows">
                {report.issues.slice(0, 50).map((issue, index) => (
                  <li key={`${issue.key ?? issue.blobId ?? ""}-${index}`} className="row">
                    <div className="row-main">
                      <p className="row-title">{t(`admin.integrity.problem.${issue.problem}`)}</p>
                      <p className="row-meta">{issue.key ?? issue.blobId}</p>
                    </div>
                  </li>
                ))}
              </ul>
            ) : null}
          </>
        ) : (
          <p className="muted">{t("admin.integrity.never")}</p>
        )}
      </Card>
    </>
  );
}
