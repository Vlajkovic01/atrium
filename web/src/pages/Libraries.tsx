import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Badge, Button, Card, Check, ErrorMessage, Field, Notice, PageHeader } from "../components/ui.tsx";
import { formatDateTime } from "../i18n.ts";
import { useSession } from "../session.tsx";

type Library = components["schemas"]["AdminLibraryView"];
type Person = components["schemas"]["AdminUserView"];
type PathCheck = components["schemas"]["CheckPathResponse"];

const queryKey = ["admin-libraries"];

/**
 * Existing folders shown in Atrium without copying them (DESIGN.md 4.4).
 * Adding one only reads the folder; removing one removes Atrium's index
 * and leaves every file where it is.
 */
export function Libraries() {
  const { t } = useTranslation();
  const { user } = useSession();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const [removing, setRemoving] = useState<Library | null>(null);
  const [editing, setEditing] = useState<Library | null>(null);
  const [enabling, setEnabling] = useState<Library | null>(null);
  const libraries = useQuery({
    queryKey,
    queryFn: () => call(api.GET("/api/v1/admin/libraries")),
    // A running scan shows its progress.
    refetchInterval: (query) => (query.state.data?.some((l) => l.scan) ? 1500 : false),
  });
  const people = useQuery({
    queryKey: ["people"],
    queryFn: () => call(api.GET("/api/v1/users", { params: { query: { limit: 200 } } })),
  });

  // The menu lists the folders the admin can open, so it follows every change here.
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey });
    await queryClient.invalidateQueries({ queryKey: ["libraries"] });
    await queryClient.invalidateQueries({ queryKey: ["navigation"] });
  };

  const scan = async (library: Library): Promise<void> => {
    setError(null);
    try {
      await call(api.POST("/api/v1/admin/libraries/{id}/scan", { params: { path: { id: library.id } } }));
    } catch (e) {
      setError(e);
    } finally {
      await refresh();
    }
  };

  // Switching changes off is always safe, so it needs no confirmation.
  const stopChanges = async (library: Library): Promise<void> => {
    setError(null);
    try {
      await call(
        api.PUT("/api/v1/admin/libraries/{id}/writes", {
          params: { path: { id: library.id } },
          body: { enabled: false, confirmName: null, writerIds: null },
        }),
      );
    } catch (e) {
      setError(e);
    } finally {
      await refresh();
    }
  };

  return (
    <>
      <PageHeader title={t("libraries.heading")} lead={t("libraries.body")} />
      <ErrorMessage error={error} />
      {libraries.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {libraries.isError ? <ErrorMessage error={libraries.error} /> : null}
      {libraries.data?.length === 0 ? <p className="muted">{t("libraries.empty")}</p> : null}
      {libraries.data && libraries.data.length > 0 ? (
        <ul className="rows">
          {libraries.data.map((library) => (
            <li key={library.id} className="row is-stacked">
              <div className="row-line">
                <span className={library.state === "online" ? "row-icon is-accent" : "row-icon"}>
                  <Icon name={library.readOnly ? "lock" : "folder"} size={20} />
                </span>
                <div className="row-main">
                  <p className="row-title">
                    {library.name}
                    {library.state === "online" ? (
                      <Badge tone="accent">{t("libraries.online")}</Badge>
                    ) : (
                      <Badge tone="danger">{t("libraries.offline")}</Badge>
                    )}
                    {library.readOnly ? (
                      <Badge tone="muted">{t("libraries.readOnly")}</Badge>
                    ) : (
                      <Badge tone="gold">{t("libraries.writesOn")}</Badge>
                    )}
                    {library.showInTimeline ? null : <Badge tone="muted">{t("libraries.notOnTimeline")}</Badge>}
                  </p>
                  <p className="row-meta">
                    <span className="path">{library.path}</span>
                    <span className="dot" />
                    {t("libraries.files", { count: Number(library.fileCount) })}
                    <span className="dot" />
                    {t("libraries.people", { count: library.userIds.length })}
                    {library.readOnly ? null : (
                      <>
                        <span className="dot" />
                        {t("libraries.writers", { count: library.writerIds.length })}
                      </>
                    )}
                    {library.lastScanAt ? (
                      <>
                        <span className="dot" />
                        {t("libraries.lastScan", { when: formatDateTime(library.lastScanAt) })}
                      </>
                    ) : null}
                  </p>
                </div>
                <div className="row-actions">
                  {user && library.userIds.includes(user.id) ? (
                    <Link className="btn btn-primary btn-sm" to={`/files/${library.rootNodeId}`}>
                      <Icon name="folder" size={16} />
                      {t("libraries.open")}
                    </Link>
                  ) : null}
                  <Button size="sm" icon="refresh" onClick={() => void scan(library)} disabled={library.scan !== null}>
                    {library.scan
                      ? t("libraries.scanning", { percent: Math.round(Number(library.scan.progress ?? 0) * 100) })
                      : t("libraries.scan")}
                  </Button>
                  <Button size="sm" icon="users" onClick={() => setEditing(library)}>
                    {t("libraries.access")}
                  </Button>
                  {library.readOnly ? (
                    <Button size="sm" icon="pencil" onClick={() => setEnabling(library)}>
                      {t("libraries.allowChanges")}
                    </Button>
                  ) : (
                    <Button size="sm" icon="lock" onClick={() => void stopChanges(library)}>
                      {t("libraries.stopChanges")}
                    </Button>
                  )}
                  <Button size="sm" variant="ghost" icon="trash" onClick={() => setRemoving(library)}>
                    {t("libraries.remove")}
                  </Button>
                </div>
              </div>
              {!library.readOnly && library.writerIds.length === 0 ? (
                <div className="row-form">
                  <Notice kind="warning">{t("libraries.nobodyWrites")}</Notice>
                </div>
              ) : null}
              {user && !library.userIds.includes(user.id) ? (
                <div className="row-form">
                  <p className="muted">{t("libraries.notForYou")}</p>
                </div>
              ) : null}
              {library.state === "offline" && library.stateReason ? (
                <div className="row-form">
                  <Notice kind="warning">{t(`errors.${library.stateReason}`)}</Notice>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}

      <Card title={t("libraries.addHeading")} icon="plus">
        <AddLibraryForm people={people.data?.items ?? []} onAdded={refresh} />
      </Card>

      {editing ? (
        <AccessDialog
          library={editing}
          people={people.data?.items ?? []}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await refresh();
          }}
        />
      ) : null}
      {enabling ? (
        <EnableWritesDialog
          library={enabling}
          people={people.data?.items ?? []}
          me={user?.id ?? null}
          onClose={() => setEnabling(null)}
          onEnabled={async () => {
            setEnabling(null);
            await refresh();
          }}
        />
      ) : null}
      {removing ? (
        <RemoveDialog
          library={removing}
          onClose={() => setRemoving(null)}
          onRemoved={async () => {
            setRemoving(null);
            await refresh();
          }}
        />
      ) : null}
    </>
  );
}

function PeopleChecks(props: {
  readonly people: readonly Person[];
  readonly selected: ReadonlySet<string>;
  readonly onChange: (selected: Set<string>) => void;
}) {
  const { t } = useTranslation();
  return (
    <fieldset className="choices">
      <legend>{t("libraries.whoSees")}</legend>
      {props.people.map((person) => (
        <Check
          key={person.id}
          label={person.displayName === person.name ? person.name : `${person.displayName} (${person.name})`}
          checked={props.selected.has(person.id)}
          onChange={(event) => {
            const next = new Set(props.selected);
            if (event.target.checked) {
              next.add(person.id);
            } else {
              next.delete(person.id);
            }

            props.onChange(next);
          }}
        />
      ))}
    </fieldset>
  );
}

function AddLibraryForm({
  people,
  onAdded,
}: {
  readonly people: readonly Person[];
  readonly onAdded: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [path, setPath] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [timeline, setTimeline] = useState(true);
  const [check, setCheck] = useState<PathCheck | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [added, setAdded] = useState(false);

  const tryPath = async (): Promise<void> => {
    setError(null);
    setCheck(null);
    try {
      setCheck(await call(api.POST("/api/v1/admin/libraries/check-path", { body: { path } })));
    } catch (e) {
      setError(e);
    }
  };

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setAdded(false);
    try {
      await call(
        api.POST("/api/v1/admin/libraries", {
          body: { name, path, userIds: [...selected], showInTimeline: timeline },
        }),
      );
      setName("");
      setPath("");
      setSelected(new Set());
      setCheck(null);
      setAdded(true);
      await onAdded();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(event) => void submit(event)}>
      <Notice kind="info">{t("libraries.addNote")}</Notice>
      {added ? <Notice kind="ok">{t("libraries.added")}</Notice> : null}
      <ErrorMessage error={error} />
      <Field
        label={t("libraries.name")}
        value={name}
        onChange={(e) => setName(e.target.value)}
        required
        maxLength={128}
      />
      <Field
        label={t("libraries.path")}
        value={path}
        onChange={(e) => {
          setPath(e.target.value);
          setCheck(null);
        }}
        required
        spellCheck={false}
        placeholder={t("libraries.pathExample")}
        help={t("libraries.pathHelp")}
      />
      {check ? (
        check.ok ? (
          <Notice kind="ok">{t("libraries.pathOk", { count: Number(check.entryCount ?? 0) })}</Notice>
        ) : (
          <Notice kind="error">{t(`errors.${check.errorCode ?? "library.path_unreadable"}`)}</Notice>
        )
      ) : null}
      <Check
        label={t("libraries.showInTimeline")}
        checked={timeline}
        onChange={(event) => setTimeline(event.target.checked)}
      />
      <p className="help">{t("libraries.showInTimelineHelp")}</p>
      <PeopleChecks people={people} selected={selected} onChange={setSelected} />
      <div className="actions">
        <Button icon="check" onClick={() => void tryPath()} disabled={path.trim() === ""}>
          {t("libraries.tryPath")}
        </Button>
        <Button type="submit" variant="primary" disabled={busy}>
          {t("common.add")}
        </Button>
      </div>
    </form>
  );
}

function AccessDialog(props: {
  readonly library: Library;
  readonly people: readonly Person[];
  readonly onClose: () => void;
  readonly onSaved: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const [selected, setSelected] = useState<Set<string>>(new Set(props.library.userIds));
  const [writers, setWriters] = useState<Set<string>>(new Set(props.library.writerIds));
  const [timeline, setTimeline] = useState(props.library.showInTimeline);
  const [error, setError] = useState<unknown>(null);

  const save = async (): Promise<void> => {
    setError(null);
    try {
      await call(
        api.PATCH("/api/v1/admin/libraries/{id}", {
          params: { path: { id: props.library.id } },
          headers: { "If-Match": `"${props.library.version}"` },
          body: {
            name: null,
            userIds: [...selected],
            writerIds: [...writers].filter((id) => selected.has(id)),
            showInTimeline: timeline,
          },
        }),
      );
      await props.onSaved();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Dialog
      title={t("libraries.accessTitle", { name: props.library.name })}
      onClose={props.onClose}
      actions={
        <>
          <Button onClick={props.onClose}>{t("common.cancel")}</Button>
          <Button variant="primary" onClick={() => void save()}>
            {t("common.save")}
          </Button>
        </>
      }
    >
      <ErrorMessage error={error} />
      <Check
        label={t("libraries.showInTimeline")}
        checked={timeline}
        onChange={(event) => setTimeline(event.target.checked)}
      />
      <PeopleChecks people={props.people} selected={selected} onChange={setSelected} />
      <fieldset className="choices">
        <legend>{t("libraries.whoChanges")}</legend>
        <p className="muted">{props.library.readOnly ? t("libraries.whoChangesOff") : t("libraries.whoChangesOn")}</p>
        {props.people
          .filter((person) => selected.has(person.id))
          .map((person) => (
            <Check
              key={person.id}
              label={person.displayName === person.name ? person.name : `${person.displayName} (${person.name})`}
              checked={writers.has(person.id)}
              onChange={(event) => {
                const next = new Set(writers);
                if (event.target.checked) {
                  next.add(person.id);
                } else {
                  next.delete(person.id);
                }

                setWriters(next);
              }}
            />
          ))}
      </fieldset>
    </Dialog>
  );
}

/**
 * Letting Atrium change the files of someone's own folder is the one step
 * here that can cost data, so it says plainly what changes and asks for
 * the library's name before it happens.
 */
function EnableWritesDialog(props: {
  readonly library: Library;
  readonly people: readonly Person[];
  readonly me: string | null;
  readonly onClose: () => void;
  readonly onEnabled: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const [typed, setTyped] = useState("");
  const [error, setError] = useState<unknown>(null);
  const viewers = props.people.filter((person) => props.library.userIds.includes(person.id));

  // Who makes changes is chosen here, so changes are never on for nobody.
  // The admin doing this is suggested when they can see the folder.
  const [writers, setWriters] = useState<Set<string>>(
    () =>
      new Set(
        props.library.writerIds.length > 0
          ? props.library.writerIds
          : props.me && props.library.userIds.includes(props.me)
            ? [props.me]
            : [],
      ),
  );

  const enable = async (): Promise<void> => {
    setError(null);
    try {
      await call(
        api.PUT("/api/v1/admin/libraries/{id}/writes", {
          params: { path: { id: props.library.id } },
          body: { enabled: true, confirmName: typed, writerIds: [...writers] },
        }),
      );
      await props.onEnabled();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Dialog
      title={t("libraries.enableTitle", { name: props.library.name })}
      onClose={props.onClose}
      actions={
        <>
          <Button onClick={props.onClose}>{t("common.cancel")}</Button>
          <Button
            variant="danger"
            onClick={() => void enable()}
            disabled={typed !== props.library.name || writers.size === 0}
          >
            {t("libraries.enableConfirm")}
          </Button>
        </>
      }
    >
      <Notice kind="warning">{t("libraries.enableWarning", { path: props.library.path ?? "" })}</Notice>
      <ul className="plain-list">
        <li>{t("libraries.enableRename")}</li>
        <li>{t("libraries.enableDelete")}</li>
        <li>{t("libraries.enableUpload")}</li>
        <li>{t("libraries.enableOff")}</li>
      </ul>
      <fieldset className="choices">
        <legend>{t("libraries.whoChanges")}</legend>
        {viewers.length === 0 ? <Notice kind="warning">{t("libraries.nobodySees")}</Notice> : null}
        {viewers.map((person) => (
          <Check
            key={person.id}
            label={person.displayName === person.name ? person.name : `${person.displayName} (${person.name})`}
            checked={writers.has(person.id)}
            onChange={(event) => {
              const next = new Set(writers);
              if (event.target.checked) {
                next.add(person.id);
              } else {
                next.delete(person.id);
              }

              setWriters(next);
            }}
          />
        ))}
      </fieldset>
      <ErrorMessage error={error} />
      <Field
        label={t("libraries.typeName", { name: props.library.name })}
        value={typed}
        onChange={(e) => setTyped(e.target.value)}
        autoComplete="off"
      />
    </Dialog>
  );
}

function RemoveDialog(props: {
  readonly library: Library;
  readonly onClose: () => void;
  readonly onRemoved: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const [typed, setTyped] = useState("");
  const [error, setError] = useState<unknown>(null);

  const remove = async (): Promise<void> => {
    setError(null);
    try {
      await call(
        api.DELETE("/api/v1/admin/libraries/{id}", {
          params: { path: { id: props.library.id }, query: { confirmName: typed } },
        }),
      );
      await props.onRemoved();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Dialog
      title={t("libraries.removeTitle", { name: props.library.name })}
      onClose={props.onClose}
      actions={
        <>
          <Button onClick={props.onClose}>{t("common.cancel")}</Button>
          <Button variant="danger" onClick={() => void remove()} disabled={typed !== props.library.name}>
            {t("libraries.remove")}
          </Button>
        </>
      }
    >
      <p>{t("libraries.removeBody")}</p>
      <ErrorMessage error={error} />
      <Field
        label={t("libraries.typeName", { name: props.library.name })}
        value={typed}
        onChange={(e) => setTyped(e.target.value)}
        autoComplete="off"
      />
    </Dialog>
  );
}
