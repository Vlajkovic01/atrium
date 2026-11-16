import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { api, call } from "./api.ts";

export type Navigation = components["schemas"]["NavigationView"];
export type NavigationLayout = components["schemas"]["NavigationLayout"];

/** The menu as it is now, ready to save back with a change (D-041). */
export function toLayout(navigation: Navigation): NavigationLayout {
  return {
    entries: navigation.items.map((item) => ({
      key: item.key,
      hidden: item.hidden,
      label: item.label,
      group: item.group,
      icon: item.icon ?? null,
    })),
    groups: navigation.groups,
    density: navigation.density,
    adminPosition: navigation.adminPosition,
  };
}

export function useNavigation() {
  return useQuery({ queryKey: ["navigation"], queryFn: () => call(api.GET("/api/v1/me/navigation")) });
}

/** Saves a change to the person's menu and shows it at once. */
export function useSaveNavigation() {
  const queryClient = useQueryClient();
  return async (layout: NavigationLayout): Promise<void> => {
    const saved = await call(api.PUT("/api/v1/me/navigation", { body: layout }));
    queryClient.setQueryData(["navigation"], saved);
  };
}

/**
 * Pins a folder or an album to the menu, or takes it off again. Only the
 * menu changes; the folder or album itself is untouched.
 */
export function usePin(kind: "folder" | "album", id: string | undefined) {
  const navigation = useNavigation();
  const save = useSaveNavigation();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const key = `${kind}:${(id ?? "").replaceAll("-", "")}`;
  const pinned = navigation.data?.items.some((item) => item.key === key) ?? false;

  const toggle = async (): Promise<void> => {
    if (!navigation.data || !id) {
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const layout = toLayout(navigation.data);
      await save({
        ...layout,
        entries: pinned
          ? layout.entries.filter((e) => e.key !== key)
          : [...layout.entries, { key, hidden: false, label: null, group: null, icon: null }],
      });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return { pinned, toggle, busy, error, ready: navigation.isSuccess && !!id };
}
