import type { components } from "@atrium/shared";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { NavLink, Outlet } from "react-router";

import { api, call } from "../api.ts";
import type { IconName } from "../components/Icon.tsx";
import { Icon } from "../components/Icon.tsx";

export type AdminOverview = components["schemas"]["AdminOverview"];
export type AdminAttention = components["schemas"]["AdminAttention"];

/** The sections of Administration, in the order of the tabs (D-042). */
export const ADMIN_SECTIONS: readonly { readonly id: string; readonly icon: IconName }[] = [
  { id: "", icon: "grid" },
  { id: "people", icon: "users" },
  { id: "storage", icon: "drive" },
  { id: "libraries", icon: "folder" },
  { id: "background-work", icon: "history" },
  { id: "backups", icon: "shield" },
];

export function sectionPath(id: string): string {
  return id ? `/admin/${id}` : "/admin";
}

/** The overview, kept fresh while an admin is signed in: the menu badge counts its warnings. */
export function useAdminOverview(enabled = true) {
  return useQuery({
    queryKey: ["admin-overview"],
    queryFn: () => call(api.GET("/api/v1/admin/overview")),
    enabled,
    refetchInterval: 60_000,
  });
}

export function warnings(overview: AdminOverview | undefined, section?: string): number {
  return (overview?.attention ?? []).filter(
    (a) => a.severity === "warning" && (section === undefined || a.section === section),
  ).length;
}

/**
 * Administration as one place (D-042): a single menu entry, the overview
 * first, and every section one tab away. A tab shows how many warnings
 * its section has, so trouble is found without opening each page.
 */
export function AdminLayout() {
  const { t } = useTranslation();
  const overview = useAdminOverview();

  return (
    <div className="admin">
      <nav className="admin-tabs" aria-label={t("admin.sections")}>
        {ADMIN_SECTIONS.map((section) => {
          const count = section.id ? warnings(overview.data, section.id) : 0;
          return (
            <NavLink key={section.id} to={sectionPath(section.id)} end={section.id === ""} className="admin-tab">
              <Icon name={section.icon} size={16} />
              <span>{t(`admin.section.${section.id || "overview"}`)}</span>
              {count > 0 ? (
                <span className="count-badge" aria-label={t("admin.warningCount", { count })}>
                  {count}
                </span>
              ) : null}
            </NavLink>
          );
        })}
      </nav>
      <Outlet />
    </div>
  );
}
