import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent,
  type FormEvent,
  type KeyboardEvent,
  type MouseEvent,
} from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router";

import { api, ApiError, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Select } from "../components/Select.tsx";
import { Badge, Button, ErrorMessage, Field, Notice, PageHeader, useErrorText } from "../components/ui.tsx";
import { usePin } from "../navigation.ts";
import { useSession } from "../session.tsx";
import { batch, downloadArchive, downloadFile, restore, type BatchItem, type NodeInfo } from "./actions.ts";
import { Details } from "./Details.tsx";
import { FolderPicker } from "./FolderPicker.tsx";
import { formatBytes, formatDateTime } from "./format.ts";
import { filesFromDrop, filesFromInput, useUploads } from "./uploads.tsx";

type Sort = "name" | "modified" | "size" | "type";
type View = "list" | "grid";

const VIEW_KEY = "atrium.files.view";
const NODE_DRAG = "application/x-atrium-nodes";
const PAGE = 500;
const READING_POLL = 2000;

function storedView(): View {
  try {
    return localStorage.getItem(VIEW_KEY) === "grid" ? "grid" : "list";
  } catch {
    return "list";
  }
}

/**
 * The file browser (UX.md "Files"): a folder's contents as a list or a
 * grid, with breadcrumbs, sorting, selection by mouse and keyboard,
 * uploads by button or by dropping files and folders anywhere, and the
 * actions on the selection. Everything that deletes goes to the trash.
 */
export function FileBrowser() {
  const { t } = useTranslation();
  const errorText = useErrorText();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { upload } = useUploads();
  const { user } = useSession();
  const params = useParams();

  // While an existing folder just added is read for the first time, its
  // files appear as they are found: the page keeps looking until it is done.
  const libraries = useQuery({
    queryKey: ["libraries"],
    queryFn: () => call(api.GET("/api/v1/libraries")),
    refetchInterval: (query) => (query.state.data?.some((l) => l.firstReading) ? READING_POLL : false),
  });
  const home = libraries.data?.find((l) => l.kind === "personal") ?? libraries.data?.[0];
  const folderId = params["folderId"] ?? home?.rootNodeId;
  const pin = usePin("folder", folderId);

  const [sort, setSort] = useState<Sort>("name");
  const [descending, setDescending] = useState(false);
  const [view, setView] = useState<View>(storedView);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [anchor, setAnchor] = useState<number | null>(null);
  const [details, setDetails] = useState<NodeInfo | null>(null);
  const [dialog, setDialog] = useState<"new" | "rename" | "move" | "copy" | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [undo, setUndo] = useState<{ ids: string[]; message: string } | null>(null);
  const [dropping, setDropping] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);

  const folder = useQuery({
    queryKey: ["node", folderId],
    enabled: folderId !== undefined,
    queryFn: () => call(api.GET("/api/v1/nodes/{id}", { params: { path: { id: folderId! } } })),
  });
  const reading = libraries.data?.find((l) => l.id === folder.data?.libraryId)?.firstReading ?? false;
  const path = useQuery({
    queryKey: ["path", folderId],
    enabled: folderId !== undefined,
    queryFn: () => call(api.GET("/api/v1/nodes/{id}/path", { params: { path: { id: folderId! } } })),
  });
  const children = useInfiniteQuery({
    queryKey: ["children", folderId, sort, descending],
    enabled: folderId !== undefined,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      call(
        api.GET("/api/v1/nodes/{id}/children", {
          params: {
            path: { id: folderId! },
            query: { sort, descending, limit: PAGE, ...(pageParam ? { cursor: pageParam } : {}) },
          },
        }),
      ),
    getNextPageParam: (page) => page.nextCursor ?? null,
    refetchInterval: () => (reading ? READING_POLL : false),
  });

  // The library the open folder belongs to: the personal one, or an
  // existing folder shared through a library.
  const library = libraries.data?.find((l) => l.id === folder.data?.libraryId) ?? home;
  const items = useMemo(() => children.data?.pages.flatMap((p) => p.items) ?? [], [children.data]);
  const canWrite = folder.data?.canWrite ?? false;
  const selection = items.filter((i) => selected.has(i.id));

  useEffect(() => {
    setSelected(new Set());
    setAnchor(null);
    setDetails(null);
  }, [folderId]);

  const refresh = async (...folders: (string | null | undefined)[]) => {
    for (const id of new Set([folderId, ...folders])) {
      await queryClient.invalidateQueries({ queryKey: ["children", id] });
    }

    await queryClient.invalidateQueries({ queryKey: ["usage"] });
    await queryClient.invalidateQueries({ queryKey: ["trash"] });
  };

  const run = async (action: () => Promise<void>) => {
    setError(null);
    setUndo(null);
    try {
      await action();
    } catch (e) {
      setError(e);
    }
  };

  /** Reports the items of a batch that did not go through; the rest did. */
  const reportBatch = (results: BatchItem[]) => {
    const failed = results.filter((r) => !r.ok);
    if (failed.length > 0) {
      setError(new ApiError({ code: failed[0]!.code ?? "unknown", params: failed[0]!.params ?? {}, status: 0 }));
    }
  };

  const trashSelection = () =>
    void run(async () => {
      const ids = selection.map((n) => n.id);
      const results = await batch("trash", ids);
      reportBatch(results);
      const done = results.filter((r) => r.ok).map((r) => r.id);
      setSelected(new Set());
      if (done.length > 0) {
        setUndo({ ids: done, message: t("files.movedToTrash", { count: done.length }) });
      }

      await refresh();
    });

  const moveInto = (targetId: string, ids: readonly string[], operation: "move" | "copy") =>
    void run(async () => {
      reportBatch(await batch(operation, ids, targetId));
      setSelected(new Set());
      setDialog(null);
      await refresh(targetId);
    });

  const download = () =>
    void run(async () => {
      const only = selection[0];
      if (selection.length === 1 && only && only.kind === "file") {
        await downloadFile(only.id);
      } else {
        await downloadArchive(selection.map((n) => n.id));
      }
    });

  const open = (node: NodeInfo) => {
    if (node.kind === "folder") {
      void navigate(`/files/${node.id}`);
    } else {
      setDetails(node);
    }
  };

  const click = (event: MouseEvent, node: NodeInfo, index: number) => {
    const next = new Set(event.ctrlKey || event.metaKey || event.shiftKey ? selected : []);
    if (event.shiftKey && anchor !== null) {
      const [from, to] = anchor < index ? [anchor, index] : [index, anchor];
      items.slice(from, to + 1).forEach((n) => next.add(n.id));
    } else if (next.has(node.id) && (event.ctrlKey || event.metaKey)) {
      next.delete(node.id);
    } else {
      next.add(node.id);
      setAnchor(index);
    }

    setSelected(next);
  };

  const keys = (event: KeyboardEvent) => {
    // Typing in a field is not a command; a row's checkbox is part of the list.
    if (event.target instanceof HTMLInputElement && event.target.type !== "checkbox") {
      return;
    }

    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "a") {
      event.preventDefault();
      setSelected(new Set(items.map((i) => i.id)));
    } else if (event.key === "Escape") {
      setSelected(new Set());
    } else if (event.key === "Delete" && canWrite && selection.length > 0) {
      trashSelection();
    } else if (event.key === "F2" && canWrite && selection.length === 1) {
      setDialog("rename");
    }
  };

  const drop = async (event: DragEvent) => {
    event.preventDefault();
    setDropping(false);
    if (!canWrite || !folderId || event.dataTransfer.types.includes(NODE_DRAG)) {
      return;
    }

    const files = await filesFromDrop(event.dataTransfer);
    if (files.length > 0) {
      await run(() => upload(files, folderId));
    }
  };

  if (libraries.isPending || (folderId && folder.isPending)) {
    return <p className="muted">{t("common.loading")}</p>;
  }

  if (!library) {
    return (
      <>
        <PageHeader title={t("files.title")} />
        <ErrorMessage error={libraries.error} />
        {libraries.data ? <Notice kind="info">{t("files.noLibrary")}</Notice> : null}
      </>
    );
  }

  const crumbs = [...(path.data ?? []), ...(folder.data ? [folder.data] : [])];
  const rootName = (l: { kind: string; name: string }) => (l.kind === "personal" ? t("files.myFiles") : l.name);
  const name = (node: NodeInfo) => (node.id === library.rootNodeId ? rootName(library) : node.name);
  const rootLink = (l: { kind: string; rootNodeId: string }) =>
    l.kind === "personal" ? "/files" : `/files/${l.rootNodeId}`;

  return (
    <div
      className={dropping ? "browser is-dropping" : "browser"}
      onDragOver={(event) => {
        if (canWrite && event.dataTransfer.types.includes("Files")) {
          event.preventDefault();
          setDropping(true);
        }
      }}
      onDragLeave={(event) => {
        if (event.currentTarget === event.target) {
          setDropping(false);
        }
      }}
      onDrop={(event) => void drop(event)}
    >
      <PageHeader
        title={folder.data ? name(folder.data) : t("files.title")}
        actions={
          canWrite ? (
            <>
              <Button icon="folderPlus" onClick={() => setDialog("new")}>
                {t("files.newFolder")}
              </Button>
              <Button variant="primary" icon="upload" onClick={() => fileInput.current?.click()}>
                {t("files.upload")}
              </Button>
              <Button variant="ghost" onClick={() => folderInput.current?.click()}>
                {t("files.uploadFolder")}
              </Button>
              <input
                ref={fileInput}
                type="file"
                multiple
                hidden
                data-testid="upload-files"
                onChange={(e) => {
                  const list = e.currentTarget.files;
                  if (list && folderId) {
                    void run(() => upload(filesFromInput(list), folderId));
                  }

                  e.currentTarget.value = "";
                }}
              />
              <input
                ref={(input) => {
                  folderInput.current = input;
                  input?.setAttribute("webkitdirectory", "");
                }}
                type="file"
                hidden
                onChange={(e) => {
                  const list = e.currentTarget.files;
                  if (list && folderId) {
                    void run(() => upload(filesFromInput(list), folderId));
                  }

                  e.currentTarget.value = "";
                }}
              />
            </>
          ) : (
            <Badge tone="muted">
              <Icon name="lock" size={14} /> {t("files.readOnly")}
            </Badge>
          )
        }
      />

      {(libraries.data?.length ?? 0) > 1 ? (
        <nav className="library-tabs" aria-label={t("files.libraries")}>
          {libraries.data!.map((l) => (
            <Link
              key={l.id}
              to={rootLink(l)}
              className={l.id === library.id ? "library-tab is-current" : "library-tab"}
              aria-current={l.id === library.id ? "page" : undefined}
            >
              <Icon name={l.mode === "external" ? (l.readOnly ? "lock" : "folder") : "user"} size={16} />
              {rootName(l)}
            </Link>
          ))}
        </nav>
      ) : null}

      <ErrorMessage error={pin.error} />
      {reading ? <Notice kind="info">{t("files.firstReading")}</Notice> : null}

      {/* Saying why nothing can be changed, and who can change that. */}
      {!canWrite && library.mode === "external" ? (
        <Notice kind="info">
          {library.changesAllowed
            ? t("files.readOnlyNotWriter")
            : user?.role === "admin"
              ? t("files.readOnlyFolderAdmin")
              : t("files.readOnlyFolder")}
          {!library.changesAllowed && user?.role === "admin" ? (
            <>
              {" "}
              <Link to="/admin/libraries">{t("files.manageLibraries")}</Link>
            </>
          ) : null}
        </Notice>
      ) : null}

      {/* At the top of the library the title already says where you are. */}
      {crumbs.length > 1 ? (
        <nav className="crumbs" aria-label={t("files.location")}>
          {crumbs.map((node, index) => (
            <span key={node.id} className="crumb">
              {index > 0 ? <Icon name="chevronRight" size={14} /> : null}
              {index === crumbs.length - 1 ? (
                <span aria-current="page">{name(node)}</span>
              ) : (
                <Link to={node.id === library.rootNodeId ? rootLink(library) : `/files/${node.id}`}>{name(node)}</Link>
              )}
            </span>
          ))}
        </nav>
      ) : null}

      <div className="toolbar" role="toolbar" aria-label={t("files.actions")}>
        {selection.length > 0 ? (
          <>
            <span className="toolbar-count">{t("files.selected", { count: selection.length })}</span>
            <Button size="sm" variant="ghost" icon="download" onClick={download}>
              {t("files.download")}
            </Button>
            {canWrite ? (
              <>
                {selection.length === 1 ? (
                  <Button size="sm" variant="ghost" icon="pencil" onClick={() => setDialog("rename")}>
                    {t("files.rename")}
                  </Button>
                ) : null}
                <Button size="sm" variant="ghost" icon="move" onClick={() => setDialog("move")}>
                  {t("files.move")}
                </Button>
                {/* Copying inside an existing folder would duplicate files on its own disk. */}
                {library.mode === "managed" ? (
                  <Button size="sm" variant="ghost" icon="copy" onClick={() => setDialog("copy")}>
                    {t("files.copy")}
                  </Button>
                ) : null}
                <Button size="sm" variant="ghost" icon="trash" onClick={trashSelection}>
                  {t("files.delete")}
                </Button>
              </>
            ) : null}
          </>
        ) : (
          <span className="toolbar-count muted">
            {t("files.count", { count: Number(children.data?.pages[0]?.total ?? 0) })}
          </span>
        )}
        <span className="toolbar-spacer" />
        {pin.ready ? (
          <Button size="sm" variant="ghost" icon="pin" disabled={pin.busy} onClick={() => void pin.toggle()}>
            {pin.pinned ? t("menu.unpin") : t("menu.pin")}
          </Button>
        ) : null}
        <Select
          label={t("files.sortBy")}
          hideLabel
          value={`${sort}:${descending ? "desc" : "asc"}`}
          options={(["name", "modified", "size", "type"] as const).flatMap((s) => [
            { value: `${s}:asc`, label: t(`files.sort.${s}Asc`) },
            { value: `${s}:desc`, label: t(`files.sort.${s}Desc`) },
          ])}
          onChange={(value: string) => {
            const [s, d] = value.split(":");
            setSort(s as Sort);
            setDescending(d === "desc");
          }}
        />
        <button
          type="button"
          className="icon-button"
          aria-pressed={view === "grid"}
          aria-label={view === "grid" ? t("files.showList") : t("files.showGrid")}
          onClick={() => {
            const next = view === "grid" ? "list" : "grid";
            setView(next);
            try {
              localStorage.setItem(VIEW_KEY, next);
            } catch {
              // Only this session remembers the choice.
            }
          }}
        >
          <Icon name={view === "grid" ? "list" : "grid"} size={18} />
        </button>
      </div>

      {undo ? (
        <Notice kind="ok">
          {undo.message}{" "}
          <button
            type="button"
            className="link-button"
            onClick={() =>
              void run(async () => {
                for (const id of undo.ids) {
                  await restore(id);
                }

                await refresh();
              })
            }
          >
            {t("files.undo")}
          </button>
        </Notice>
      ) : null}
      <ErrorMessage error={error} />
      {children.isError ? <ErrorMessage error={children.error} /> : null}

      <div className={details ? "browser-body has-details" : "browser-body"}>
        <div
          className={view === "grid" ? "items is-grid" : "items"}
          role="grid"
          aria-label={t("files.contents")}
          aria-multiselectable="true"
          tabIndex={0}
          onKeyDown={keys}
        >
          {children.isSuccess && items.length === 0 ? (
            <div className="empty">
              <Icon name="folder" size={36} />
              <p>{canWrite ? t("files.emptyWritable") : t("files.empty")}</p>
            </div>
          ) : null}
          {items.map((node, index) => (
            <div
              key={node.id}
              role="row"
              aria-selected={selected.has(node.id)}
              className={selected.has(node.id) ? "item is-selected" : "item"}
              draggable={canWrite}
              onDragStart={(event) => {
                const ids = selected.has(node.id) ? [...selected] : [node.id];
                event.dataTransfer.setData(NODE_DRAG, JSON.stringify(ids));
                event.dataTransfer.effectAllowed = "move";
              }}
              onDragOver={(event) => {
                if (node.kind === "folder" && canWrite && event.dataTransfer.types.includes(NODE_DRAG)) {
                  event.preventDefault();
                  event.currentTarget.classList.add("is-target");
                }
              }}
              onDragLeave={(event) => event.currentTarget.classList.remove("is-target")}
              onDrop={(event) => {
                event.currentTarget.classList.remove("is-target");
                const raw = event.dataTransfer.getData(NODE_DRAG);
                if (node.kind === "folder" && raw) {
                  event.preventDefault();
                  event.stopPropagation();
                  const ids = (JSON.parse(raw) as string[]).filter((id) => id !== node.id);
                  if (ids.length > 0) {
                    moveInto(node.id, ids, "move");
                  }
                }
              }}
              onClick={(event) => click(event, node, index)}
              onDoubleClick={() => open(node)}
            >
              <span role="gridcell" className="item-check">
                <input
                  type="checkbox"
                  aria-label={t("files.select", { name: node.name })}
                  checked={selected.has(node.id)}
                  onClick={(event) => event.stopPropagation()}
                  onChange={() => {
                    const next = new Set(selected);
                    if (next.has(node.id)) {
                      next.delete(node.id);
                    } else {
                      next.add(node.id);
                    }

                    setSelected(next);
                    setAnchor(index);
                  }}
                />
              </span>
              <span role="gridcell" className="item-name">
                <span className={node.kind === "folder" ? "item-icon is-folder" : "item-icon"}>
                  <Icon name={node.kind === "folder" ? "folder" : "file"} size={view === "grid" ? 36 : 20} />
                </span>
                <button
                  type="button"
                  className="item-open"
                  onClick={(event) => {
                    event.stopPropagation();
                    open(node);
                  }}
                >
                  {node.name}
                </button>
              </span>
              <span role="gridcell" className="item-meta">
                {node.kind === "file" ? formatBytes(Number(node.size)) : ""}
              </span>
              <span role="gridcell" className="item-meta">
                {formatDateTime(node.modifiedAt)}
              </span>
            </div>
          ))}
          {children.hasNextPage ? (
            <Button variant="ghost" onClick={() => void children.fetchNextPage()}>
              {t("files.showMore")}
            </Button>
          ) : null}
        </div>
        {details ? <Details node={details} onClose={() => setDetails(null)} /> : null}
      </div>

      {dropping ? (
        <div className="drop-overlay" aria-hidden="true">
          <Icon name="upload" size={40} />
          <p>{t("files.dropHere")}</p>
        </div>
      ) : null}

      {dialog === "new" && folderId ? (
        <NameDialog
          title={t("files.newFolder")}
          initial=""
          confirm={t("files.create")}
          onClose={() => setDialog(null)}
          onSubmit={async (value) => {
            await call(api.POST("/api/v1/nodes", { body: { parentId: folderId, name: value } }));
            setDialog(null);
            await refresh();
          }}
        />
      ) : null}
      {dialog === "rename" && selection[0] ? (
        <NameDialog
          title={t("files.rename")}
          initial={selection[0].name}
          confirm={t("files.rename")}
          onClose={() => setDialog(null)}
          onSubmit={async (value) => {
            const node = selection[0]!;
            await call(
              api.PATCH("/api/v1/nodes/{id}", {
                params: { path: { id: node.id }, header: { "If-Match": `"${node.version}"` } },
                body: { name: value, parentId: null },
              }),
            );
            setDialog(null);
            await refresh();
          }}
        />
      ) : null}
      {(dialog === "move" || dialog === "copy") && folderId ? (
        <FolderPicker
          title={
            dialog === "move"
              ? t("files.moveTo", { count: selection.length })
              : t("files.copyTo", { count: selection.length })
          }
          confirm={dialog === "move" ? t("files.moveHere") : t("files.copyHere")}
          rootId={library.rootNodeId}
          startId={folderId}
          excluded={new Set(selection.filter((n) => n.kind === "folder").map((n) => n.id))}
          onClose={() => setDialog(null)}
          onPick={(target) =>
            moveInto(
              target,
              selection.map((n) => n.id),
              dialog,
            )
          }
        />
      ) : null}
      <span className="visually-hidden" aria-live="polite">
        {error ? errorText(error) : ""}
      </span>
    </div>
  );
}

/** Asks for a name; the server's answer (taken, not allowed) is shown in place. */
function NameDialog({
  title,
  initial,
  confirm,
  onClose,
  onSubmit,
}: {
  readonly title: string;
  readonly initial: string;
  readonly confirm: string;
  readonly onClose: () => void;
  readonly onSubmit: (name: string) => Promise<void>;
}) {
  const { t } = useTranslation();
  const [value, setValue] = useState(initial);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await onSubmit(value);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog title={title} onClose={onClose}>
      <form onSubmit={(e) => void submit(e)}>
        <Field label={t("files.name")} value={value} autoFocus onChange={(e) => setValue(e.currentTarget.value)} />
        <ErrorMessage error={error} />
        <div className="dialog-actions">
          <Button variant="ghost" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button type="submit" variant="primary" disabled={busy || value.trim().length === 0}>
            {confirm}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
