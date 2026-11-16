import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState, type DragEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import { api, call } from "../api.ts";
import type { IconName } from "../components/Icon.tsx";
import { Icon, isIconName, MENU_ICONS } from "../components/Icon.tsx";
import { Select } from "../components/Select.tsx";
import { Badge, Button, Card, Check, ErrorMessage, Notice, PageHeader } from "../components/ui.tsx";
import type { NavigationLayout } from "../navigation.ts";
import { useNavigation } from "../navigation.ts";
import { useSession } from "../session.tsx";
import { ViewEditor } from "./ViewEditor.tsx";

type NavItem = components["schemas"]["NavItemView"];
type NavGroup = components["schemas"]["NavGroup"];
type SavedView = components["schemas"]["SavedView"];
type Density = "comfortable" | "compact";
type AdminPosition = "bottom" | "top";

interface Entry {
  readonly key: string;
  readonly kind: string;
  readonly name: string | null;
  label: string;
  hidden: boolean;
  group: string;
  icon: string;
}

/** Entries that belong to one person: their views and the folders and albums they pinned. */
const PERSONAL = new Set(["view", "folder", "album"]);

/** Entries that can be taken off the menu, rather than only hidden. */
const REMOVABLE = new Set(["folder", "album", "separator"]);

/** The icon an entry has when the person has not chosen one. */
const USUAL_ICON: Record<string, IconName> = {
  files: "folder",
  photos: "image",
  trash: "trash",
  devices: "devices",
  account: "user",
  library: "folder",
  folder: "folder",
  view: "search",
  album: "album",
};

function newId(): string {
  return crypto.randomUUID().replaceAll("-", "");
}

/**
 * Each person arranges their own menu (D-041). The list looks like the
 * menu: entries without a group first, then each group with its entries.
 * A name is changed by typing over it; an entry joins a group by being
 * dragged into it (or with the small group list, from the keyboard).
 * Nothing here touches a file. Administration is not an entry: an admin
 * always sees it, at the top or the bottom.
 */
export function MenuSettings() {
  const { t } = useTranslation();
  const { user } = useSession();
  const isAdmin = user?.role === "admin";
  const queryClient = useQueryClient();
  const navigation = useNavigation();
  const views = useQuery({ queryKey: ["views"], queryFn: () => call(api.GET("/api/v1/me/views")) });
  const [entries, setEntries] = useState<Entry[]>([]);
  const [groups, setGroups] = useState<NavGroup[]>([]);
  const [density, setDensity] = useState<Density>("comfortable");
  const [adminPosition, setAdminPosition] = useState<AdminPosition>("bottom");
  const [choosingIcon, setChoosingIcon] = useState<string | null>(null);
  const [dragging, setDragging] = useState<string | null>(null);
  // The entry being dragged, known at once: the first dragover can come
  // before React has applied the state above.
  const dragged = useRef<string | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const [focusGroup, setFocusGroup] = useState<string | null>(null);
  // While a name is being typed, its row does not drag, so text can be selected.
  const [typing, setTyping] = useState<string | null>(null);
  const [editing, setEditing] = useState<SavedView | "new" | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  // The editable copy follows the server whenever it changes there.
  useEffect(() => {
    if (navigation.data) {
      setEntries(
        navigation.data.items.map((item: NavItem) => ({
          key: item.key,
          kind: item.kind,
          name: item.name,
          // The field shows the name in use, so it is changed by typing over it.
          label: item.label ?? (item.kind === "separator" ? "" : (item.name ?? t(`nav.${item.kind}`))),
          hidden: item.hidden,
          group: item.group ?? "",
          icon: item.icon ?? "",
        })),
      );
      setGroups(navigation.data.groups.map((g) => ({ ...g })));
      setDensity(navigation.data.density === "compact" ? "compact" : "comfortable");
      setAdminPosition(navigation.data.adminPosition === "top" ? "top" : "bottom");
    }
  }, [navigation.data, t]);

  const usual = (entry: Entry): string =>
    entry.kind === "separator" ? t("menu.separator") : (entry.name ?? t(`nav.${entry.kind}`));
  const title = (entry: Entry): string => entry.label || usual(entry);
  const layout = (shared: boolean): NavigationLayout => ({
    entries: entries
      .filter((e) => !shared || !PERSONAL.has(e.kind))
      .map((e) => {
        // Typing the usual name back is the same as keeping it.
        const label = e.label.trim();
        return {
          key: e.key,
          hidden: e.hidden,
          label: label && label !== usual(e) ? label : null,
          group: e.group || null,
          icon: e.icon || null,
        };
      }),
    groups: groups.map((g) => ({ ...g, name: g.name.trim() || t("menu.newGroupName") })),
    density,
    adminPosition,
  });

  const update = (key: string, change: Partial<Entry>): void =>
    setEntries((list) => list.map((e) => (e.key === key ? { ...e, ...change } : e)));

  /** Moves an entry up or down among the entries of its own group. */
  const step = (key: string, by: -1 | 1): void =>
    setEntries((list) => {
      const index = list.findIndex((e) => e.key === key);
      const group = list[index]!.group;
      let target = index + by;
      while (target >= 0 && target < list.length && list[target]!.group !== group) {
        target += by;
      }

      if (target < 0 || target >= list.length) {
        return list;
      }

      const next = [...list];
      [next[index], next[target]] = [next[target]!, next[index]!];
      return next;
    });

  /** Puts an entry where another one is (taking its group), or at the end of a group. */
  const place = (key: string, before: string | null, group: string): void =>
    setEntries((list) => {
      const moved = list.find((e) => e.key === key);
      if (!moved || key === before) {
        return list;
      }

      const rest = list.filter((e) => e.key !== key);
      let at = before === null ? -1 : rest.findIndex((e) => e.key === before);
      if (at < 0) {
        const last = rest.map((e) => e.group).lastIndexOf(group);
        at = last < 0 ? rest.length : last + 1;
      }

      rest.splice(at, 0, { ...moved, group });
      return rest;
    });

  const dragProps = (target: string, before: string | null, group: string) => ({
    onDragOver: (event: DragEvent) => {
      if (dragged.current !== null) {
        event.preventDefault();
        event.stopPropagation();
        setDropTarget(target);
      }
    },
    onDrop: (event: DragEvent) => {
      event.preventDefault();
      event.stopPropagation();
      if (dragged.current !== null) {
        place(dragged.current, before, group);
      }
      dragged.current = null;
      setDragging(null);
      setDropTarget(null);
    },
  });

  const run = async (action: () => Promise<unknown>, done: string): Promise<void> => {
    setError(null);
    setNotice(null);
    try {
      await action();
      await queryClient.invalidateQueries({ queryKey: ["navigation"] });
      setNotice(done);
    } catch (e) {
      setError(e);
    }
  };

  const groupOptions = [
    { value: "", label: t("menu.noGroup") },
    ...groups.map((g) => ({ value: g.id, label: g.name.trim() || t("menu.newGroupName") })),
  ];

  const row = (entry: Entry, position: number, count: number) => {
    const className = [
      "row menu-entry",
      dragging === entry.key ? "is-dragging" : "",
      dropTarget === entry.key && dragging !== entry.key ? "is-drop-target" : "",
      entry.hidden ? "is-hidden" : "",
    ]
      .filter(Boolean)
      .join(" ");
    return (
      <li
        key={entry.key}
        className={className}
        draggable={typing !== entry.key}
        onDragStart={(event) => {
          event.dataTransfer.effectAllowed = "move";
          event.dataTransfer.setData("text/plain", entry.key);
          dragged.current = entry.key;
          setDragging(entry.key);
        }}
        onDragEnd={() => {
          dragged.current = null;
          setDragging(null);
          setDropTarget(null);
        }}
        {...dragProps(entry.key, entry.key, entry.group)}
      >
        <div className="row-line">
          <span className="menu-grip" aria-hidden="true">
            ⋮⋮
          </span>
          <div className="menu-order">
            <button
              type="button"
              className="icon-button"
              aria-label={t("menu.moveUp", { name: title(entry) })}
              disabled={position === 0}
              onClick={() => step(entry.key, -1)}
            >
              <Icon name="chevronUp" size={16} />
            </button>
            <button
              type="button"
              className="icon-button"
              aria-label={t("menu.moveDown", { name: title(entry) })}
              disabled={position === count - 1}
              onClick={() => step(entry.key, 1)}
            >
              <Icon name="chevronDown" size={16} />
            </button>
          </div>
          {entry.kind === "separator" ? (
            <p className="row-main muted">{t("menu.separator")}</p>
          ) : (
            <>
              <button
                type="button"
                className="icon-button"
                aria-label={t("menu.iconFor", { name: title(entry) })}
                aria-expanded={choosingIcon === entry.key}
                title={t("menu.icon")}
                onClick={() => setChoosingIcon(choosingIcon === entry.key ? null : entry.key)}
              >
                <Icon name={isIconName(entry.icon) ? entry.icon : (USUAL_ICON[entry.kind] ?? "folder")} />
              </button>
              <div className="row-main menu-name">
                <input
                  className="inline-name"
                  value={entry.label}
                  maxLength={64}
                  aria-label={t("menu.nameFor", { name: usual(entry) })}
                  title={t("menu.renameHint")}
                  onFocus={() => setTyping(entry.key)}
                  onChange={(e) => update(entry.key, { label: e.target.value })}
                  onBlur={(e) => {
                    setTyping(null);
                    // An empty name goes back to the usual one.
                    if (e.target.value.trim() === "") {
                      update(entry.key, { label: usual(entry) });
                    }
                  }}
                />
                {entry.label.trim() && entry.label.trim() !== usual(entry) ? (
                  <span className="help">{t("menu.usuallyCalled", { name: usual(entry) })}</span>
                ) : null}
              </div>
              {["library", "view", "folder", "album"].includes(entry.kind) ? (
                <Badge tone="muted">{t(`menu.kinds.${entry.kind}`)}</Badge>
              ) : null}
              <Check
                label={t("menu.show")}
                checked={!entry.hidden}
                onChange={(e) => update(entry.key, { hidden: !e.target.checked })}
              />
            </>
          )}
          {groups.length > 0 ? (
            <Select
              label={t("menu.groupOf", { name: title(entry) })}
              hideLabel
              variant="pill"
              icon="folder"
              value={entry.group}
              options={groupOptions}
              onChange={(value: string) => place(entry.key, null, value)}
            />
          ) : null}
          {REMOVABLE.has(entry.kind) ? (
            <button
              type="button"
              className="icon-button"
              aria-label={t("menu.removeEntry", { name: title(entry) })}
              title={t("menu.remove")}
              onClick={() => setEntries((list) => list.filter((e) => e.key !== entry.key))}
            >
              <Icon name="close" size={16} />
            </button>
          ) : null}
        </div>
        {choosingIcon === entry.key ? (
          <div className="icon-choices" role="group" aria-label={t("menu.iconFor", { name: title(entry) })}>
            <Button size="sm" aria-pressed={entry.icon === ""} onClick={() => update(entry.key, { icon: "" })}>
              {t("menu.usualIcon")}
            </Button>
            {MENU_ICONS.map((name) => (
              <button
                key={name}
                type="button"
                className="icon-button"
                aria-label={name}
                title={name}
                aria-pressed={entry.icon === name}
                onClick={() => update(entry.key, { icon: name })}
              >
                <Icon name={name} size={18} />
              </button>
            ))}
          </div>
        ) : null}
      </li>
    );
  };

  const section = (group: NavGroup | null) => {
    const id = group?.id ?? "";
    const members = entries.filter((e) => e.group === id);
    const dropId = `section:${id}`;
    return (
      <section
        key={dropId}
        className={dropTarget === dropId ? "menu-section is-drop-target" : "menu-section"}
        aria-label={group ? group.name : t("menu.noGroup")}
        {...dragProps(dropId, null, id)}
      >
        {group ? (
          <div className="menu-section-head">
            <Icon name="folder" size={16} />
            <input
              className="inline-name menu-group-name"
              value={group.name}
              maxLength={64}
              placeholder={t("menu.newGroupName")}
              aria-label={t("menu.groupName")}
              autoFocus={focusGroup === group.id}
              onFocus={(e) => {
                setTyping(dropId);
                if (focusGroup === group.id) {
                  e.target.select();
                  setFocusGroup(null);
                }
              }}
              onBlur={() => setTyping(null)}
              onChange={(e) =>
                setGroups((list) => list.map((g) => (g.id === group.id ? { ...g, name: e.target.value } : g)))
              }
            />
            <Check
              label={t("menu.folded")}
              checked={group.collapsed}
              onChange={(e) =>
                setGroups((list) => list.map((g) => (g.id === group.id ? { ...g, collapsed: e.target.checked } : g)))
              }
            />
            <button
              type="button"
              className="icon-button"
              aria-label={t("menu.removeGroupNamed", { name: group.name })}
              title={t("menu.removeGroup")}
              onClick={() => {
                setGroups((list) => list.filter((g) => g.id !== group.id));
                setEntries((list) => list.map((e) => (e.group === group.id ? { ...e, group: "" } : e)));
              }}
            >
              <Icon name="trash" size={16} />
            </button>
          </div>
        ) : null}
        <ul className="rows menu-entries">{members.map((entry, index) => row(entry, index, members.length))}</ul>
        {group && members.length === 0 ? <p className="menu-drop">{t("menu.dropHere")}</p> : null}
      </section>
    );
  };

  return (
    <>
      <PageHeader title={t("menu.title")} lead={t("menu.lead")} />
      <ErrorMessage error={error ?? navigation.error} />
      {notice ? <Notice kind="ok">{notice}</Notice> : null}

      <Card title={t("menu.entries")} icon="list">
        <p className="muted">{t("menu.entriesLead")}</p>
        {section(null)}
        {groups.map((group) => section(group))}
        <div className="actions">
          <Button
            icon="folderPlus"
            onClick={() => {
              const id = newId();
              setGroups((list) => [...list, { id, name: t("menu.newGroupName"), collapsed: false }]);
              setFocusGroup(id);
            }}
          >
            {t("menu.addGroup")}
          </Button>
          <Button
            icon="plus"
            onClick={() =>
              setEntries((list) => [
                ...list,
                {
                  key: `separator:${newId()}`,
                  kind: "separator",
                  name: null,
                  label: "",
                  hidden: false,
                  group: "",
                  icon: "",
                },
              ])
            }
          >
            {t("menu.addSeparator")}
          </Button>
        </div>
        <p className="help">{t("menu.pinHint")}</p>
        <div className="inline-form">
          <Select
            label={t("menu.density")}
            value={density}
            options={[
              { value: "comfortable", label: t("menu.densityComfortable") },
              { value: "compact", label: t("menu.densityCompact") },
            ]}
            onChange={setDensity}
          />
          {isAdmin ? (
            <Select
              label={t("menu.adminPosition")}
              value={adminPosition}
              options={[
                { value: "bottom", label: t("menu.adminBottom") },
                { value: "top", label: t("menu.adminTop") },
              ]}
              onChange={setAdminPosition}
            />
          ) : null}
        </div>
        <div className="actions">
          <Button onClick={() => void run(() => call(api.DELETE("/api/v1/me/navigation")), t("menu.resetDone"))}>
            {t("menu.reset")}
          </Button>
          {isAdmin ? (
            <Button
              onClick={() =>
                void run(
                  () => call(api.PUT("/api/v1/admin/navigation-default", { body: layout(true) })),
                  t("menu.defaultDone"),
                )
              }
            >
              {t("menu.makeDefault")}
            </Button>
          ) : null}
          <Button
            variant="primary"
            onClick={() =>
              void run(() => call(api.PUT("/api/v1/me/navigation", { body: layout(false) })), t("menu.saved"))
            }
          >
            {t("common.save")}
          </Button>
        </div>
      </Card>

      <Card title={t("views.title")} icon="search">
        <p className="muted">{t("views.explain")}</p>
        <ul className="rows">
          {views.data?.map((view) => (
            <li key={view.id} className="row">
              <div className="row-main">
                <p className="row-title">
                  <Link to={`/views/${view.id}`}>{view.name}</Link>
                </p>
              </div>
              <Button size="sm" icon="pencil" onClick={() => setEditing(view)}>
                {t("views.edit")}
              </Button>
            </li>
          ))}
        </ul>
        <Button variant="primary" icon="plus" onClick={() => setEditing("new")}>
          {t("views.new")}
        </Button>
      </Card>

      {editing ? (
        <ViewEditor
          view={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={() => setEditing(null)}
        />
      ) : null}
    </>
  );
}
