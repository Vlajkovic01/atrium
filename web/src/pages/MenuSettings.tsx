import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import { api, call } from "../api.ts";
import type { IconName } from "../components/Icon.tsx";
import { Icon, isIconName, MENU_ICONS } from "../components/Icon.tsx";
import { Select } from "../components/Select.tsx";
import { Badge, Button, Card, Check, ErrorMessage, Field, Notice, PageHeader } from "../components/ui.tsx";
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
 * Each person arranges their own menu (D-041): order (buttons or dragging),
 * what shows, own names and icons, groups that fold, separators, folders
 * and albums pinned from their pages, views made of rules, and how dense
 * the menu is. Nothing here touches a file. Administration is not an
 * entry: an admin always sees it, at the top or the bottom.
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
  const [newGroup, setNewGroup] = useState("");
  const [choosingIcon, setChoosingIcon] = useState<string | null>(null);
  const [dragging, setDragging] = useState<number | null>(null);
  const [dropTarget, setDropTarget] = useState<number | null>(null);
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
          label: item.label ?? "",
          hidden: item.hidden,
          group: item.group ?? "",
          icon: item.icon ?? "",
        })),
      );
      setGroups(navigation.data.groups.map((g) => ({ ...g })));
      setDensity(navigation.data.density === "compact" ? "compact" : "comfortable");
      setAdminPosition(navigation.data.adminPosition === "top" ? "top" : "bottom");
    }
  }, [navigation.data]);

  const usual = (entry: Entry): string =>
    entry.kind === "separator" ? t("menu.separator") : (entry.name ?? t(`nav.${entry.kind}`));
  const title = (entry: Entry): string => entry.label || usual(entry);
  const layout = (shared: boolean): NavigationLayout => ({
    entries: entries
      .filter((e) => !shared || !PERSONAL.has(e.kind))
      .map((e) => ({
        key: e.key,
        hidden: e.hidden,
        label: e.label.trim() || null,
        group: e.group || null,
        icon: e.icon || null,
      })),
    groups,
    density,
    adminPosition,
  });

  const update = (index: number, change: Partial<Entry>): void =>
    setEntries((list) => list.map((e, i) => (i === index ? { ...e, ...change } : e)));
  const moveTo = (from: number, to: number): void =>
    setEntries((list) => {
      if (to < 0 || to >= list.length || from === to) {
        return list;
      }

      const next = [...list];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved!);
      return next;
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

  const actions = (
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
        onClick={() => void run(() => call(api.PUT("/api/v1/me/navigation", { body: layout(false) })), t("menu.saved"))}
      >
        {t("common.save")}
      </Button>
    </div>
  );

  return (
    <>
      <PageHeader title={t("menu.title")} lead={t("menu.lead")} />
      <ErrorMessage error={error ?? navigation.error} />
      {notice ? <Notice kind="ok">{notice}</Notice> : null}

      <Card title={t("menu.look")} icon="grid">
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
        <p className="help">{t("menu.saveHint")}</p>
      </Card>

      <Card title={t("menu.entries")} icon="list">
        <p className="muted">{t("menu.entriesLead")}</p>
        <ul className="rows menu-entries">
          {entries.map((entry, index) => {
            const className = [
              "row is-stacked",
              dragging === index ? "is-dragging" : "",
              dropTarget === index && dragging !== index ? "is-drop-target" : "",
            ]
              .filter(Boolean)
              .join(" ");
            return (
              <li
                key={entry.key}
                className={className}
                draggable
                onDragStart={(event) => {
                  event.dataTransfer.effectAllowed = "move";
                  setDragging(index);
                }}
                onDragOver={(event) => {
                  if (dragging !== null) {
                    event.preventDefault();
                    setDropTarget(index);
                  }
                }}
                onDrop={(event) => {
                  event.preventDefault();
                  if (dragging !== null) {
                    moveTo(dragging, index);
                  }
                  setDragging(null);
                  setDropTarget(null);
                }}
                onDragEnd={() => {
                  setDragging(null);
                  setDropTarget(null);
                }}
              >
                <div className="row-line">
                  <div className="menu-order">
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("menu.moveUp", { name: title(entry) })}
                      disabled={index === 0}
                      onClick={() => moveTo(index, index - 1)}
                    >
                      <Icon name="chevronUp" size={16} />
                    </button>
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("menu.moveDown", { name: title(entry) })}
                      disabled={index === entries.length - 1}
                      onClick={() => moveTo(index, index + 1)}
                    >
                      <Icon name="chevronDown" size={16} />
                    </button>
                  </div>
                  {entry.kind === "separator" ? null : (
                    <Icon name={isIconName(entry.icon) ? entry.icon : (USUAL_ICON[entry.kind] ?? "folder")} />
                  )}
                  <div className="row-main">
                    <p className="row-title">
                      {title(entry)}
                      {["library", "view", "folder", "album"].includes(entry.kind) ? (
                        <Badge tone="muted">{t(`menu.kinds.${entry.kind}`)}</Badge>
                      ) : null}
                    </p>
                  </div>
                  {entry.kind === "separator" ? null : (
                    <Check
                      label={t("menu.show")}
                      checked={!entry.hidden}
                      onChange={(e) => update(index, { hidden: !e.target.checked })}
                    />
                  )}
                  {REMOVABLE.has(entry.kind) ? (
                    <Button
                      size="sm"
                      variant="ghost"
                      icon="close"
                      onClick={() => setEntries((list) => list.filter((e) => e.key !== entry.key))}
                    >
                      {t("menu.remove")}
                    </Button>
                  ) : null}
                </div>
                <div className="row-form inline-form">
                  {entry.kind === "separator" ? null : (
                    <>
                      <Field
                        label={t("menu.ownName")}
                        value={entry.label}
                        placeholder={usual(entry)}
                        maxLength={64}
                        onChange={(e) => update(index, { label: e.target.value })}
                      />
                      <Button
                        size="sm"
                        aria-expanded={choosingIcon === entry.key}
                        onClick={() => setChoosingIcon(choosingIcon === entry.key ? null : entry.key)}
                      >
                        {t("menu.icon")}
                      </Button>
                    </>
                  )}
                  {groups.length > 0 ? (
                    <Select
                      label={t("menu.group")}
                      value={entry.group}
                      options={[
                        { value: "", label: t("menu.noGroup") },
                        ...groups.map((g) => ({ value: g.id, label: g.name })),
                      ]}
                      onChange={(value: string) => update(index, { group: value })}
                    />
                  ) : null}
                </div>
                {choosingIcon === entry.key ? (
                  <div className="icon-choices" role="group" aria-label={t("menu.iconFor", { name: title(entry) })}>
                    <Button size="sm" aria-pressed={entry.icon === ""} onClick={() => update(index, { icon: "" })}>
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
                        onClick={() => update(index, { icon: name })}
                      >
                        <Icon name={name} size={18} />
                      </button>
                    ))}
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
        <p className="help">{t("menu.pinHint")}</p>
        <div className="actions">
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
        {actions}
      </Card>

      <Card title={t("menu.groups")} icon="folder">
        <p className="muted">{t("menu.groupsLead")}</p>
        <ul className="rows">
          {groups.map((group, index) => (
            <li key={group.id} className="row">
              <div className="row-main inline-form">
                <Field
                  label={t("menu.groupName")}
                  value={group.name}
                  maxLength={64}
                  onChange={(e) =>
                    setGroups((list) => list.map((g, i) => (i === index ? { ...g, name: e.target.value } : g)))
                  }
                />
                <Check
                  label={t("menu.folded")}
                  checked={group.collapsed}
                  onChange={(e) =>
                    setGroups((list) => list.map((g, i) => (i === index ? { ...g, collapsed: e.target.checked } : g)))
                  }
                />
              </div>
              <Button
                size="sm"
                variant="ghost"
                icon="trash"
                onClick={() => {
                  setGroups((list) => list.filter((g) => g.id !== group.id));
                  setEntries((list) => list.map((e) => (e.group === group.id ? { ...e, group: "" } : e)));
                }}
              >
                {t("menu.removeGroup")}
              </Button>
            </li>
          ))}
        </ul>
        <form
          className="inline-form"
          onSubmit={(event) => {
            event.preventDefault();
            if (newGroup.trim()) {
              setGroups((list) => [...list, { id: newId(), name: newGroup.trim(), collapsed: false }]);
              setNewGroup("");
            }
          }}
        >
          <Field
            label={t("menu.newGroup")}
            value={newGroup}
            maxLength={64}
            onChange={(e) => setNewGroup(e.target.value)}
          />
          <Button type="submit" icon="plus" disabled={newGroup.trim() === ""}>
            {t("menu.addGroup")}
          </Button>
        </form>
        <p className="help">{t("menu.saveHint")}</p>
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
