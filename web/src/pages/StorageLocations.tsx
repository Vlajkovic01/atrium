import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useAdminOverview } from "../admin/AdminLayout.tsx";
import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import type { IconName } from "../components/Icon.tsx";
import { Icon } from "../components/Icon.tsx";
import { StorageForm } from "../components/StorageForm.tsx";
import { Badge, Button, ErrorMessage, Notice, PageHeader } from "../components/ui.tsx";
import { formatBytes, formatDateTime } from "../i18n.ts";

type StorageLocationView = components["schemas"]["StorageLocationView"];
type StorageTestResult = components["schemas"]["StorageTestResult"];

const kindIcon: Record<StorageLocationView["kind"], IconName> = {
  localFileSystem: "drive",
  networkShare: "share",
  s3: "cloud",
};

/**
 * The places files are stored. "Check now" does what the server does on
 * its own: it reads the location's identity first and only then tries it,
 * so an unmounted share shows up as offline instead of as an empty disk.
 */
export function StorageLocations() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [checking, setChecking] = useState<string | null>(null);
  const [results, setResults] = useState<Record<string, StorageTestResult>>({});
  const [error, setError] = useState<unknown>(null);
  const [added, setAdded] = useState(false);
  const [adding, setAdding] = useState(false);
  // What each location holds comes with the overview.
  const overview = useAdminOverview();
  const stored = new Map((overview.data?.storage ?? []).map((l) => [l.id, Number(l.storedBytes)]));
  const locations = useQuery({
    queryKey: ["storage-locations"],
    queryFn: () => call(api.GET("/api/v1/storage-locations")),
  });

  const check = async (id: string): Promise<void> => {
    setChecking(id);
    setError(null);
    try {
      const result = await call(api.POST("/api/v1/storage-locations/{id}/check", { params: { path: { id } } }));
      setResults((previous) => ({ ...previous, [id]: result }));
      await queryClient.invalidateQueries({ queryKey: ["storage-locations"] });
    } catch (e) {
      setError(e);
    } finally {
      setChecking(null);
    }
  };

  const stateBadge = (location: StorageLocationView) =>
    location.state === "online" ? (
      <Badge tone="accent">{t("storageAdmin.stateOnline")}</Badge>
    ) : location.state === "offline" ? (
      <Badge tone="danger">{t("storageAdmin.stateOffline")}</Badge>
    ) : (
      <Badge tone="muted">{t("storageAdmin.stateUnknown")}</Badge>
    );

  return (
    <>
      <PageHeader
        title={t("storageAdmin.heading")}
        lead={t("storageAdmin.body")}
        actions={
          <Button variant="primary" icon="plus" onClick={() => setAdding(true)}>
            {t("storageAdmin.addHeading")}
          </Button>
        }
      />
      <ErrorMessage error={error} />
      {added ? <Notice kind="ok">{t("storageAdmin.added")}</Notice> : null}

      {locations.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {locations.isError ? <ErrorMessage error={locations.error} /> : null}
      {locations.data ? (
        <ul className="rows">
          {locations.data.map((location) => {
            const result = results[location.id];
            return (
              <li key={location.id} className="row is-stacked">
                <div className="row-line">
                  <span className={location.state === "online" ? "row-icon is-accent" : "row-icon"}>
                    <Icon name={kindIcon[location.kind]} size={20} />
                  </span>
                  <div className="row-main">
                    <p className="row-title">
                      {location.name}
                      {stateBadge(location)}
                    </p>
                    <p className="row-meta">
                      <span className="path">{location.target}</span>
                      {stored.has(location.id) ? (
                        <>
                          <span className="dot" />
                          {t("admin.storage.stored", { size: formatBytes(stored.get(location.id)!) })}
                        </>
                      ) : null}
                      {location.lastCheckedAt ? (
                        <>
                          <span className="dot" />
                          {t("storageAdmin.lastChecked", { when: formatDateTime(location.lastCheckedAt) })}
                        </>
                      ) : null}
                    </p>
                  </div>
                  <div className="row-actions">
                    <Button
                      size="sm"
                      icon="refresh"
                      onClick={() => void check(location.id)}
                      disabled={checking !== null}
                    >
                      {checking === location.id ? t("storageAdmin.checking") : t("storageAdmin.check")}
                    </Button>
                  </div>
                </div>
                {result ? (
                  <div className="row-form">
                    {result.ok ? (
                      <Notice kind="ok">
                        {result.freeBytes != null
                          ? t("storageAdmin.healthy", { free: formatBytes(Number(result.freeBytes)) })
                          : t("storageAdmin.healthyNoFree")}
                      </Notice>
                    ) : (
                      <Notice kind="error">
                        {t([`errors.${result.errorCode ?? "storage.unavailable"}`, "errors.storage.unavailable"])}
                      </Notice>
                    )}
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      ) : null}

      {adding ? (
        <Dialog wide title={t("storageAdmin.addHeading")} onClose={() => setAdding(false)}>
          <StorageForm
            defaultName=""
            submitLabel={t("common.add")}
            onSaved={async () => {
              setAdding(false);
              setAdded(true);
              await queryClient.invalidateQueries({ queryKey: ["storage-locations"] });
              await queryClient.invalidateQueries({ queryKey: ["admin-overview"] });
            }}
          />
        </Dialog>
      ) : null}
    </>
  );
}
