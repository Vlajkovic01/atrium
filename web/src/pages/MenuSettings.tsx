import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import { api, call } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Select } from "../components/Select.tsx";
import { Badge, Button, Card, Check, ErrorMessage, Field, Notice, PageHeader } from "../components/ui.tsx";
import { useSession } from "../session.tsx";
import { ViewEditor } from "./ViewEditor.tsx";

type NavItem = components["schemas"]["NavItemView"];
type NavGroup = components["schemas"]["NavGroup"];
type SavedView = components["schemas"]["SavedView"];

interface Entry {
  readonly key: string;
  readonly kind: string;
  readonly name: string | null;
  label: string;
  hidden: boolean;
  group: string;
}

function newId(): string {
  return crypto.randomUUID().replaceAll("-", "");
}

/**
 * Each person arranges their own menu (D-041): order, what shows, own
 * names, groups, and views made of rules. Nothing here touches a file.
 * Administration is not part of it: an admin always sees it.
 */
export function MenuSettings() {
  const { t } = useTranslation();
  const { user } = useSession();
  const queryClient = useQueryClient();
  const navigation = useQuery({ queryKey: ["navigation"], queryFn: () => call(api.GET("/api/v1/me/navigation")) });
  const views = useQuery({ queryKey: ["views"], queryFn: () => call(api.GET("/api/v1/me/views")) });
  const [entries, setEntries] = useState<Entry[]>([]);
  const [groups, setGroups] = useState<NavGroup[]>([]);
  const [newGroup, setNewGroup] = useState("");
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
        })),
      );
      setGroups(navigation.data.groups.map((g) => ({ ...g })));
    }
  }, [navigation.data]);

  const usual = (entry: Entry): string => entry.name ?? t(`nav.${entry.kind}`);
  const layout = (onlyShared: boolean) => ({
    entries: entries
      .filter((e) => !onlyShared || e.kind !== "view")
      .map((e) => ({ key: e.key, hidden: e.hidden, label: e.label.trim() || null, group: e.group || null })),
    groups,
  });

  const update = (index: number, change: Partial<Entry>): void =>
    setEntries((list) => list.map((e, i) => (i === index ? { ...e, ...change } : e)));
  const move = (index: number, by: number): void =>
    setEntries((list) => {
      const target = index + by;
      if (target < 0 || target >= list.length) {
        return list;
      }

      const next = [...list];
      [next[index], next[target]] = [next[target]!, next[index]!];
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

  return (
    <>
      <PageHeader title={t("menu.title")} lead={t("menu.lead")} />
      <ErrorMessage error={error ?? navigation.error} />
      {notice ? <Notice kind="ok">{notice}</Notice> : null}

      <Card title={t("menu.entries")} icon="list">
        <ul className="rows menu-entries">
          {entries.map((entry, index) => (
            <li key={entry.key} className="row is-stacked">
              <div className="row-line">
                <div className="menu-order">
                  <button
                    type="button"
                    className="icon-button"
                    aria-label={t("menu.moveUp", { name: entry.label || usual(entry) })}
                    disabled={index === 0}
                    onClick={() => move(index, -1)}
                  >
                    <Icon name="chevronUp" size={16} />
                  </button>
                  <button
                    type="button"
                    className="icon-button"
                    aria-label={t("menu.moveDown", { name: entry.label || usual(entry) })}
                    disabled={index === entries.length - 1}
                    onClick={() => move(index, 1)}
                  >
                    <Icon name="chevronDown" size={16} />
                  </button>
                </div>
                <div className="row-main">
                  <p className="row-title">
                    {entry.label || usual(entry)}
                    {entry.kind === "library" ? <Badge tone="muted">{t("menu.kindLibrary")}</Badge> : null}
                    {entry.kind === "view" ? <Badge tone="muted">{t("menu.kindView")}</Badge> : null}
                  </p>
                </div>
                <Check
                  label={t("menu.show")}
                  checked={!entry.hidden}
                  onChange={(e) => update(index, { hidden: !e.target.checked })}
                />
              </div>
              <div className="row-form inline-form">
                <Field
                  label={t("menu.ownName")}
                  value={entry.label}
                  placeholder={usual(entry)}
                  maxLength={64}
                  onChange={(e) => update(index, { label: e.target.value })}
                />
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
            </li>
          ))}
        </ul>
        <div className="actions">
          <Button onClick={() => void run(() => call(api.DELETE("/api/v1/me/navigation")), t("menu.resetDone"))}>
            {t("menu.reset")}
          </Button>
          {user?.role === "admin" ? (
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
              setGroups((list) => [...list, { id: newId(), name: newGroup.trim() }]);
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
