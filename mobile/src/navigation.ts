import type { useRouter } from "expo-router";

type Router = ReturnType<typeof useRouter>;

/**
 * Opens a folder in Files from a screen shown over the tabs (search, the
 * notifications). That screen is closed first, so it does not stay
 * hidden beneath and come back later; the folder then opens with where
 * files start beneath it, so Back leads there.
 */
export function openFolderFromOverlay(router: Router, folderId: string): void {
  if (router.canDismiss()) {
    router.dismissAll();
  }

  router.push({ pathname: "/files/[folderId]", params: { folderId } }, { withAnchor: true });
}
