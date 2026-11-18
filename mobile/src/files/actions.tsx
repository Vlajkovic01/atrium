import { batchFailure, type NodeInfo } from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { BackHandler, Pressable, StyleSheet, Text, View } from "react-native";

import { useActiveServer } from "../api.ts";
import { NameDialog } from "../components/Dialog.tsx";
import type { IconName } from "../components/Icon.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { ErrorMessage } from "../components/ui.tsx";
import { radius, space, type, useTheme } from "../theme.ts";
import { useOfflineMaybe } from "../offline/OfflineProvider.tsx";
import { FolderPicker } from "./FolderPicker.tsx";

/** How long "Undo" stays after items went to the trash (as the web app's confirmations, D-045). */
const UNDO_MS = 8000;

type Asking =
  | { readonly kind: "new" }
  | { readonly kind: "rename"; readonly node: NodeInfo }
  | { readonly kind: "move" | "copy"; readonly nodes: readonly NodeInfo[] };

export interface FileActions {
  /** The ids chosen for an action on several items; empty when not choosing. */
  readonly selected: ReadonlySet<string>;
  readonly select: (node: NodeInfo) => void;
  readonly selectAll: (nodes: readonly NodeInfo[]) => void;
  readonly clearSelection: () => void;
  readonly newFolder: () => void;
  /** The actions on one item, in a panel. */
  readonly openMenu: (node: NodeInfo) => void;
  readonly rename: (node: NodeInfo) => void;
  readonly moveOrCopy: (kind: "move" | "copy", nodes: readonly NodeInfo[]) => void;
  readonly trash: (nodes: readonly NodeInfo[]) => void;
  /** The panels, dialogs and messages of the actions; drawn once by the screen. */
  readonly overlays: ReactNode;
}

/**
 * What can be done to the items of one folder (docs/UX.md, Mobile,
 * Files): new folder, rename, move, copy and the trash, for one item or
 * several. Nothing is deleted here: "Move to trash" can be undone at
 * once, and for 30 days from the Trash screen.
 */
export function useFileActions({
  folder,
  rootName,
  onDetails,
}: {
  /** The open folder; actions exist only where it can be changed. */
  readonly folder: NodeInfo | undefined;
  /** What the top folder of this folder's library is called. */
  readonly rootName: string;
  readonly onDetails: (node: NodeInfo) => void;
}): FileActions {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { server, files } = useActiveServer();
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [asking, setAsking] = useState<Asking | null>(null);
  const [menu, setMenu] = useState<NodeInfo | null>(null);
  const [busy, setBusy] = useState(false);
  const [pickerError, setPickerError] = useState<unknown>(null);
  const [error, setError] = useState<unknown>(null);
  const [undo, setUndo] = useState<{ readonly ids: readonly string[] } | null>(null);
  const canWrite = folder?.canWrite ?? false;
  const offline = useOfflineMaybe();

  // Another folder: nothing chosen there yet.
  useEffect(() => setSelected(new Set()), [folder?.id]);

  // The system's back button leaves the choosing before it leaves the folder.
  useEffect(() => {
    if (selected.size === 0) {
      return;
    }

    const handler = BackHandler.addEventListener("hardwareBackPress", () => {
      setSelected(new Set());
      return true;
    });
    return () => handler.remove();
  }, [selected.size]);

  useEffect(() => {
    if (!undo) {
      return;
    }

    const timer = setTimeout(() => setUndo(null), UNDO_MS);
    return () => clearTimeout(timer);
  }, [undo]);

  const changed = async (): Promise<void> => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: [server.id, "children"] }),
      queryClient.invalidateQueries({ queryKey: [server.id, "trash"] }),
    ]);
  };

  const trash = (nodes: readonly NodeInfo[]): void => {
    setError(null);
    setUndo(null);
    void (async () => {
      try {
        const results = await files.batch(
          "trash",
          nodes.map((n) => n.id),
        );
        // Some items may have gone and others not: say so, and offer Undo for those that went.
        setError(batchFailure(results));
        const done = results.filter((r) => r.ok).map((r) => r.id);
        if (done.length > 0) {
          setUndo({ ids: done });
        }
      } catch (e) {
        setError(e);
      } finally {
        setSelected(new Set());
        await changed();
      }
    })();
  };

  const restore = (ids: readonly string[]): void => {
    setUndo(null);
    void (async () => {
      try {
        for (const id of ids) {
          await files.restore(id);
        }
      } catch (e) {
        setError(e);
      } finally {
        await changed();
      }
    })();
  };

  const place = (kind: "move" | "copy", nodes: readonly NodeInfo[], target: string): void => {
    setBusy(true);
    setPickerError(null);
    void (async () => {
      try {
        const results = await files.batch(
          kind,
          nodes.map((n) => n.id),
          target,
        );
        setError(batchFailure(results));
        setAsking(null);
        setSelected(new Set());
      } catch (e) {
        // Nothing was done: the picker stays, with the reason.
        setPickerError(e);
      } finally {
        setBusy(false);
        await changed();
      }
    })();
  };

  const option = (label: string, icon: IconName, run: (node: NodeInfo) => void): ReactNode => (
    <SheetOption
      key={label}
      label={label}
      icon={icon}
      onPress={() => {
        const node = menu!;
        setMenu(null);
        run(node);
      }}
    />
  );

  const overlays = (
    <>
      {menu ? (
        <Sheet title={menu.name} onClose={() => setMenu(null)}>
          {canWrite ? option(t("files.rename"), "pencil", (node) => setAsking({ kind: "rename", node })) : null}
          {canWrite ? option(t("files.move"), "move", (node) => setAsking({ kind: "move", nodes: [node] })) : null}
          {canWrite ? option(t("files.copy"), "copy", (node) => setAsking({ kind: "copy", nodes: [node] })) : null}
          {canWrite ? option(t("files.delete"), "trash", (node) => trash([node])) : null}
          {offline
            ? offline.isOffline(menu.id)
              ? option(t("mobile.offline.remove"), "close", (node) => void offline.remove(node.id))
              : option(t("mobile.offline.keep"), "download", (node) => void offline.add(node))
            : null}
          {option(t("files.details"), "info", onDetails)}
        </Sheet>
      ) : null}

      {asking?.kind === "new" && folder ? (
        <NameDialog
          title={t("files.newFolder")}
          initial=""
          confirm={t("files.create")}
          onClose={() => setAsking(null)}
          onSubmit={async (name) => {
            await files.createFolder(folder.id, name);
            setAsking(null);
            await changed();
          }}
        />
      ) : null}

      {asking?.kind === "rename" ? (
        <NameDialog
          title={t("files.rename")}
          initial={asking.node.name}
          confirm={t("files.rename")}
          onClose={() => setAsking(null)}
          onSubmit={async (name) => {
            try {
              await files.rename(asking.node, name);
            } finally {
              // Also after a refusal: the item may have changed meanwhile, and the list should show it.
              await changed();
            }

            setAsking(null);
            setSelected(new Set());
          }}
        />
      ) : null}

      {(asking?.kind === "move" || asking?.kind === "copy") && folder ? (
        <FolderPicker
          title={t(asking.kind === "move" ? "files.moveTo" : "files.copyTo", { count: asking.nodes.length })}
          confirm={t(asking.kind === "move" ? "files.moveHere" : "files.copyHere")}
          rootName={rootName}
          startId={folder.id}
          excluded={new Set(asking.nodes.filter((n) => n.kind === "folder").map((n) => n.id))}
          busy={busy}
          error={pickerError}
          onClose={() => {
            setAsking(null);
            setPickerError(null);
          }}
          onPick={(target) => place(asking.kind, asking.nodes, target)}
        />
      ) : null}

      {error || undo ? (
        <View style={styles.messages} pointerEvents="box-none">
          {error ? (
            <Pressable accessibilityRole="button" accessibilityLabel={t("common.close")} onPress={() => setError(null)}>
              <ErrorMessage error={error} />
            </Pressable>
          ) : null}
          {undo ? (
            <View accessibilityRole="alert" style={[styles.undo, { backgroundColor: colors.ink }]}>
              <Text style={[styles.undoText, { color: colors.paper }]}>
                {t("files.movedToTrash", { count: undo.ids.length })}
              </Text>
              <Pressable
                accessibilityRole="button"
                hitSlop={space.md}
                onPress={() => restore(undo.ids)}
                style={styles.undoButton}
              >
                <Text style={[styles.undoAction, { color: colors.paper }]}>{t("files.undo")}</Text>
              </Pressable>
            </View>
          ) : null}
        </View>
      ) : null}
    </>
  );

  return {
    selected,
    select: (node) =>
      setSelected((before) => {
        const next = new Set(before);
        if (!next.delete(node.id)) {
          next.add(node.id);
        }

        return next;
      }),
    selectAll: (nodes) => setSelected(new Set(nodes.map((n) => n.id))),
    clearSelection: () => setSelected(new Set()),
    newFolder: () => setAsking({ kind: "new" }),
    openMenu: setMenu,
    rename: (node) => setAsking({ kind: "rename", node }),
    moveOrCopy: (kind, nodes) => setAsking({ kind, nodes }),
    trash,
    overlays,
  };
}

const styles = StyleSheet.create({
  messages: { position: "absolute", left: space.lg, right: space.lg, bottom: space.lg, gap: space.sm },
  undo: {
    minHeight: 52,
    borderRadius: radius.md,
    paddingLeft: space.lg,
    paddingRight: space.sm,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
  },
  undoText: { flex: 1, fontSize: type.small },
  undoButton: { minHeight: 44, paddingHorizontal: space.md, justifyContent: "center" },
  undoAction: { fontSize: type.small, fontWeight: "700", textDecorationLine: "underline" },
});
