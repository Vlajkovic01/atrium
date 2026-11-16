import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import { Icon } from "../components/Icon.tsx";
import { Badge, ErrorMessage, Notice, PageHeader } from "../components/ui.tsx";
import { formatAgo, formatBytes, formatDateTime } from "../i18n.ts";
import type { AdminAttention } from "./AdminLayout.tsx";
import { sectionPath, useAdminOverview } from "./AdminLayout.tsx";

function Stat(props: { readonly value: ReactNode; readonly label: string; readonly tone?: "danger" }) {
  return (
    <div className={props.tone ? `stat is-${props.tone}` : "stat"}>
      <span className="stat-value">{props.value}</span>
      <span className="stat-label">{props.label}</span>
    </div>
  );
}

function Tile(props: {
  readonly title: string;
  readonly icon: Parameters<typeof Icon>[0]["name"];
  readonly section: string;
  readonly children: ReactNode;
}) {
  const { t } = useTranslation();
  return (
    <section className="card admin-tile" aria-label={props.title}>
      <header className="admin-tile-head">
        <Icon name={props.icon} size={18} />
        <h2>{props.title}</h2>
        <Link to={sectionPath(props.section)} className="admin-tile-link">
          {t("admin.open")}
          <Icon name="chevronRight" size={14} />
        </Link>
      </header>
      {props.children}
    </section>
  );
}

/**
 * The server at a glance (D-042): what needs attention first, each item
 * leading to the section that deals with it, then one tile per section.
 * Counts and sizes only, never file names.
 */
export function AdminOverviewPage() {
  const { t } = useTranslation();
  const overview = useAdminOverview();
  const data = overview.data;

  const attentionText = (a: AdminAttention): string =>
    t(`admin.attention.${a.code.replace(".", "_")}`, { name: a.name ?? "", count: Number(a.count ?? 0) });

  return (
    <>
      <PageHeader title={t("admin.title")} lead={t("admin.lead")} />
      <ErrorMessage error={overview.error} />
      {overview.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {data ? (
        <>
          {data.attention.length === 0 ? (
            <Notice kind="ok">{t("admin.allGood")}</Notice>
          ) : (
            <ul className="attention" aria-label={t("admin.attentionHeading")}>
              {data.attention.map((a, index) => (
                <li key={`${a.code}-${a.name ?? index}`} className={`attention-item is-${a.severity}`}>
                  <Icon name={a.severity === "warning" ? "alert" : "info"} size={18} />
                  <span>{attentionText(a)}</span>
                  <Link to={sectionPath(a.section)}>{t(`admin.section.${a.section}`)}</Link>
                </li>
              ))}
            </ul>
          )}

          <div className="admin-grid">
            <Tile title={t("admin.section.people")} icon="users" section="people">
              <div className="stats">
                <Stat value={data.people.total} label={t("admin.people.total")} />
                <Stat value={data.people.admins} label={t("admin.people.admins")} />
                <Stat value={data.people.disabled} label={t("admin.people.disabled")} />
                {Number(data.people.pendingDeletion) > 0 ? (
                  <Stat value={data.people.pendingDeletion} label={t("admin.people.pendingDeletion")} />
                ) : null}
              </div>
            </Tile>

            <Tile title={t("admin.section.storage")} icon="drive" section="storage">
              {data.storage.length === 0 ? <p className="muted">{t("admin.storage.none")}</p> : null}
              <ul className="plain-list admin-list">
                {data.storage.map((location) => (
                  <li key={location.id}>
                    <span className="admin-list-name">{location.name}</span>
                    <Badge tone={location.state === "offline" ? "danger" : "muted"}>
                      {t(`admin.storage.state.${location.state}`)}
                    </Badge>
                    <span className="muted">
                      {t("admin.storage.stored", { size: formatBytes(Number(location.storedBytes)) })}
                    </span>
                  </li>
                ))}
              </ul>
            </Tile>

            <Tile title={t("admin.section.libraries")} icon="folder" section="libraries">
              <div className="stats">
                <Stat value={data.libraries.folders} label={t("admin.libraries.folders")} />
                <Stat
                  value={data.libraries.offline}
                  label={t("admin.libraries.offline")}
                  tone={Number(data.libraries.offline) > 0 ? "danger" : undefined}
                />
                <Stat value={data.libraries.changesAllowed} label={t("admin.libraries.changesAllowed")} />
              </div>
            </Tile>

            <Tile title={t("admin.section.background-work")} icon="history" section="background-work">
              <div className="stats">
                <Stat value={data.jobs.running} label={t("admin.jobs.running")} />
                <Stat value={data.jobs.waiting} label={t("admin.jobs.waiting")} />
                <Stat
                  value={data.jobs.failedLastDay}
                  label={t("admin.jobs.failed")}
                  tone={Number(data.jobs.failedLastDay) > 0 ? "danger" : undefined}
                />
              </div>
              {data.jobs.paused ? (
                <p className="muted">
                  {data.jobs.pausedUntil
                    ? t("admin.jobs.pausedUntil", { time: formatDateTime(data.jobs.pausedUntil) })
                    : t("admin.jobs.paused")}
                </p>
              ) : null}
            </Tile>

            <Tile title={t("admin.section.backups")} icon="shield" section="backups">
              <dl className="facts">
                <dt>{t("admin.backups.last")}</dt>
                <dd>{data.backup.lastAt ? formatAgo(data.backup.lastAt) : t("admin.backups.never")}</dd>
                <dt>{t("admin.integrity.last")}</dt>
                <dd>
                  {data.integrity.running
                    ? t("admin.integrity.running")
                    : data.integrity.lastAt
                      ? formatAgo(data.integrity.lastAt)
                      : t("admin.integrity.never")}
                </dd>
                {Number(data.integrity.problems) > 0 ? (
                  <>
                    <dt>{t("admin.integrity.problems")}</dt>
                    <dd className="is-danger">{Number(data.integrity.problems)}</dd>
                  </>
                ) : null}
              </dl>
            </Tile>
          </div>
        </>
      ) : null}
    </>
  );
}
