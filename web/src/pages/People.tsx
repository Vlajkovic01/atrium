import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Select } from "../components/Select.tsx";
import { Avatar, Badge, Button, ErrorMessage, Field, Flash, PageHeader } from "../components/ui.tsx";
import { formatAgo, formatBytes, formatDateTime } from "../i18n.ts";
import { useSession } from "../session.tsx";

type AdminUserView = components["schemas"]["AdminUserView"];
type UserRole = components["schemas"]["UserRole"];
type RoleFilter = "all" | UserRole;
type StatusFilter = "all" | "active" | "disabled" | "deletion";

const ROLES: readonly UserRole[] = ["admin", "user", "guest"];
const MIN_PASSWORD_LENGTH = 8;
const GB = 1024 * 1024 * 1024;

function matches(person: AdminUserView, search: string, role: RoleFilter, status: StatusFilter): boolean {
  const words = search.trim().toLocaleLowerCase();
  if (words && !`${person.displayName} ${person.name}`.toLocaleLowerCase().includes(words)) {
    return false;
  }

  if (role !== "all" && person.role !== role) {
    return false;
  }

  switch (status) {
    case "active":
      return !person.disabled;
    case "disabled":
      return person.disabled && !person.deletionScheduledAt;
    case "deletion":
      return !!person.deletionScheduledAt;
    default:
      return true;
  }
}

/** Space used, against the quota when there is one. */
function Usage({ person }: { readonly person: AdminUserView }) {
  const { t } = useTranslation();
  const used = Number(person.usedBytes ?? 0);
  const quota = person.quotaBytes == null ? null : Number(person.quotaBytes);
  const share = quota ? Math.min(1, used / quota) : null;
  return (
    <div className="usage">
      <span className="usage-text">
        {quota
          ? t("people.usedOf", { used: formatBytes(used), quota: formatBytes(quota) })
          : t("people.used", { used: formatBytes(used) })}
      </span>
      {share !== null ? (
        <span className={share > 0.9 ? "usage-bar is-full" : "usage-bar"} aria-hidden="true">
          <span style={{ width: `${Math.round(share * 100)}%` }} />
        </span>
      ) : null}
    </div>
  );
}

/**
 * Administration of accounts (DESIGN.md 6.3, D-042). The list finds a
 * person by name, role or state and shows what each uses and when they
 * were last active; everything else about one person is in one dialog.
 * An admin sees accounts and sizes, never content. Every change sends the
 * version that was read, so two admins cannot silently undo each other.
 */
export function People() {
  const { t } = useTranslation();
  const { user: me } = useSession();
  const queryClient = useQueryClient();
  const [notice, setNotice] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [managing, setManaging] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [role, setRole] = useState<RoleFilter>("all");
  const [status, setStatus] = useState<StatusFilter>("all");
  const people = useQuery({
    queryKey: ["people"],
    queryFn: () => call(api.GET("/api/v1/users", { params: { query: { limit: 200 } } })),
  });

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["people"] });
    await queryClient.invalidateQueries({ queryKey: ["admin-overview"] });
  };

  const all = people.data?.items ?? [];
  const shown = all.filter((p) => matches(p, search, role, status));
  const current = all.find((p) => p.id === managing);

  return (
    <>
      <PageHeader
        title={t("people.heading")}
        lead={t("people.body")}
        actions={
          <Button variant="primary" icon="plus" onClick={() => setAdding(true)}>
            {t("people.create")}
          </Button>
        }
      />
      <Flash message={notice} />
      {people.isError ? <ErrorMessage error={people.error} /> : null}

      <div className="filters" role="search">
        <Field
          label={t("people.search")}
          type="search"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder={t("people.searchPlaceholder")}
        />
        <Select
          label={t("people.role")}
          value={role}
          options={[
            { value: "all", label: t("people.allRoles") },
            ...ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) })),
          ]}
          onChange={setRole}
        />
        <Select
          label={t("people.status")}
          value={status}
          options={(["all", "active", "disabled", "deletion"] as const).map((s) => ({
            value: s,
            label: t(`people.statuses.${s}`),
          }))}
          onChange={setStatus}
        />
      </div>

      {people.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {people.isSuccess ? (
        <p className="muted" aria-live="polite">
          {t("people.shown", { count: shown.length, total: all.length })}
        </p>
      ) : null}
      <ul className="rows people">
        {shown.map((person) => (
          <li key={person.id} className="row">
            <Avatar name={person.displayName} />
            <div className="row-main">
              <p className="row-title">
                {person.displayName}
                {person.id === me?.id ? <Badge tone="accent">{t("people.you")}</Badge> : null}
                {person.deletionScheduledAt ? (
                  <Badge tone="danger">
                    {t("people.deletionBadge", {
                      date: formatDateTime(person.deleteAfter ?? person.deletionScheduledAt),
                    })}
                  </Badge>
                ) : person.disabled ? (
                  <Badge tone="danger">{t("people.disabledBadge")}</Badge>
                ) : null}
                {person.totpEnabled ? <Badge tone="muted">{t("people.twoStep")}</Badge> : null}
              </p>
              <p className="row-meta">
                {t(`roles.${person.role}`)}
                <span className="dot" />
                {person.name}
                <span className="dot" />
                {person.lastActiveAt
                  ? t("people.lastActive", { when: formatAgo(person.lastActiveAt) })
                  : t("people.neverActive")}
              </p>
            </div>
            <Usage person={person} />
            <Button size="sm" onClick={() => setManaging(person.id)}>
              {t("people.manage")}
            </Button>
          </li>
        ))}
      </ul>

      {adding ? (
        <AddPerson
          onClose={() => setAdding(false)}
          onCreated={async (name) => {
            setAdding(false);
            setNotice(t("people.created", { name }));
            await refresh();
          }}
        />
      ) : null}
      {current ? (
        <ManagePerson
          person={current}
          isMe={current.id === me?.id}
          onClose={() => setManaging(null)}
          onChanged={refresh}
        />
      ) : null}
    </>
  );
}

/** Everything about one account, in sections: role, space, access, deletion. */
function ManagePerson(props: {
  readonly person: AdminUserView;
  readonly isMe: boolean;
  readonly onClose: () => void;
  readonly onChanged: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const { person, isMe } = props;
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [quotaGb, setQuotaGb] = useState(person.quotaBytes == null ? "" : String(Number(person.quotaBytes) / GB));
  const [confirmingOff, setConfirmingOff] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const run = async (action: () => Promise<unknown>, done?: string): Promise<void> => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
      if (done) {
        setNotice(done);
      }
    } catch (e) {
      setError(e);
    } finally {
      await props.onChanged();
      setBusy(false);
    }
  };

  const patch = (body: { role?: UserRole; disabled?: boolean; quotaBytes?: number | null; clearQuota?: boolean }) =>
    call(
      api.PATCH("/api/v1/users/{id}", {
        params: { path: { id: person.id }, header: { "If-Match": `"${person.version}"` } },
        body: {
          displayName: null,
          role: body.role ?? null,
          disabled: body.disabled ?? null,
          quotaBytes: body.quotaBytes ?? null,
          clearQuota: body.clearQuota ?? false,
        },
      }),
    );

  const saveQuota = (event: FormEvent): void => {
    event.preventDefault();
    const value = quotaGb.trim();
    void run(
      () => (value === "" ? patch({ clearQuota: true }) : patch({ quotaBytes: Math.round(Number(value) * GB) })),
      t("people.quotaSaved"),
    );
  };

  return (
    <Dialog
      wide
      title={person.displayName}
      onClose={props.onClose}
      actions={<Button onClick={props.onClose}>{t("common.close")}</Button>}
    >
      <p className="muted">
        {t(`roles.${person.role}`)}
        <span className="dot" />
        {person.name}
        <span className="dot" />
        {t("people.since", { date: formatDateTime(person.createdAt) })}
      </p>
      <ErrorMessage error={error} />
      <Flash message={notice} />

      <section className="manage-section" aria-labelledby="manage-role">
        <h3 id="manage-role">{t("people.role")}</h3>
        {isMe ? <p className="help">{t("people.ownRole")}</p> : null}
        <Select
          label={t("people.roleOf", { name: person.name })}
          hideLabel
          value={person.role}
          disabled={isMe || busy}
          options={ROLES.map((role) => ({ value: role, label: t(`roles.${role}`) }))}
          onChange={(role) => void run(() => patch({ role }), t("people.roleSaved"))}
        />
        <p className="help">{t(`people.roleHelp.${person.role}`)}</p>
      </section>

      <section className="manage-section" aria-labelledby="manage-space">
        <h3 id="manage-space">{t("people.space")}</h3>
        <Usage person={person} />
        <form className="inline-form" onSubmit={saveQuota}>
          <Field
            label={t("people.quota")}
            help={t("people.quotaHelp")}
            type="number"
            min={0}
            step="any"
            value={quotaGb}
            onChange={(e) => setQuotaGb(e.target.value)}
          />
          <Button type="submit" disabled={busy}>
            {t("people.saveQuota")}
          </Button>
        </form>
      </section>

      {isMe ? null : (
        <section className="manage-section" aria-labelledby="manage-access">
          <h3 id="manage-access">{t("people.access")}</h3>
          <div className="actions is-start">
            {person.disabled ? (
              person.deletionScheduledAt ? null : (
                <Button disabled={busy} onClick={() => void run(() => patch({ disabled: false }), t("people.enabled"))}>
                  {t("people.enable")}
                </Button>
              )
            ) : (
              <Button disabled={busy} onClick={() => setConfirmingOff(true)}>
                {t("people.disable")}
              </Button>
            )}
            <Button disabled={busy} onClick={() => setResetting(!resetting)}>
              {t("people.resetPassword")}
            </Button>
          </div>
          {confirmingOff ? (
            <div className="confirm-box">
              <p>{t("people.disableConfirm", { name: person.name })}</p>
              <div className="actions">
                <Button variant="ghost" onClick={() => setConfirmingOff(false)}>
                  {t("common.cancel")}
                </Button>
                <Button
                  variant="danger"
                  onClick={() => {
                    setConfirmingOff(false);
                    void run(() => patch({ disabled: true }), t("people.disabled"));
                  }}
                >
                  {t("people.disable")}
                </Button>
              </div>
            </div>
          ) : null}
          {resetting ? (
            <ResetPassword
              person={person}
              onCancel={() => setResetting(false)}
              onDone={() => {
                setResetting(false);
                setNotice(t("people.resetDone", { name: person.name }));
              }}
            />
          ) : null}
        </section>
      )}

      {isMe ? null : (
        <section className="manage-section is-danger" aria-labelledby="manage-delete">
          <h3 id="manage-delete">{t("people.deleteHeading")}</h3>
          {person.deletionScheduledAt ? (
            <>
              <p>
                {t("people.deletionScheduled", {
                  date: formatDateTime(person.deleteAfter ?? person.deletionScheduledAt),
                })}
              </p>
              <Button
                disabled={busy}
                onClick={() =>
                  void run(
                    () => call(api.DELETE("/api/v1/users/{id}/deletion", { params: { path: { id: person.id } } })),
                    t("people.deletionCancelled"),
                  )
                }
              >
                {t("people.cancelDeletion")}
              </Button>
            </>
          ) : deleting ? (
            <DeletePerson
              person={person}
              onCancel={() => setDeleting(false)}
              onScheduled={async () => {
                setDeleting(false);
                await props.onChanged();
              }}
            />
          ) : (
            <>
              <p className="help">{t("people.deleteLead")}</p>
              <Button variant="danger" icon="trash" onClick={() => setDeleting(true)}>
                {t("people.delete")}
              </Button>
            </>
          )}
        </section>
      )}
    </Dialog>
  );
}

/** Says what deleting removes, then asks for the account's name. */
function DeletePerson(props: {
  readonly person: AdminUserView;
  readonly onCancel: () => void;
  readonly onScheduled: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const [typed, setTyped] = useState("");
  const [error, setError] = useState<unknown>(null);
  const impact = useQuery({
    queryKey: ["user-deletion", props.person.id],
    queryFn: () => call(api.GET("/api/v1/users/{id}/deletion", { params: { path: { id: props.person.id } } })),
  });

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setError(null);
    try {
      await call(
        api.POST("/api/v1/users/{id}/deletion", {
          params: { path: { id: props.person.id } },
          body: { confirmName: typed },
        }),
      );
      await props.onScheduled();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <form className="confirm-box form" onSubmit={(event) => void submit(event)}>
      {impact.data ? (
        <p>
          {t("people.deleteImpact", {
            files: impact.data.files,
            folders: impact.data.folders,
            size: formatBytes(Number(impact.data.bytes)),
          })}
        </p>
      ) : null}
      <p>{t("people.deleteExplain")}</p>
      <Field
        label={t("people.typeName", { name: props.person.name })}
        value={typed}
        onChange={(e) => setTyped(e.target.value)}
        autoComplete="off"
        autoCapitalize="none"
        spellCheck={false}
        autoFocus
      />
      <ErrorMessage error={error ?? impact.error} />
      <div className="actions">
        <Button variant="ghost" onClick={props.onCancel}>
          {t("common.cancel")}
        </Button>
        <Button type="submit" variant="danger" disabled={typed !== props.person.name}>
          <Icon name="trash" size={16} /> {t("people.deleteConfirm")}
        </Button>
      </div>
    </form>
  );
}

function ResetPassword(props: {
  readonly person: AdminUserView;
  readonly onCancel: () => void;
  readonly onDone: () => void;
}) {
  const { t } = useTranslation();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await call(
        api.POST("/api/v1/users/{id}/reset-password", {
          params: { path: { id: props.person.id } },
          body: { newPassword: password },
        }),
      );
      props.onDone();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="confirm-box form" onSubmit={(event) => void submit(event)}>
      <Field
        label={t("people.newPasswordFor", { name: props.person.name })}
        help={t("people.resetHelp")}
        type="password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        autoComplete="new-password"
        minLength={MIN_PASSWORD_LENGTH}
        required
        autoFocus
      />
      <ErrorMessage error={error} />
      <div className="actions">
        <Button variant="ghost" onClick={props.onCancel}>
          {t("common.cancel")}
        </Button>
        <Button type="submit" variant="primary" disabled={busy}>
          {t("people.resetConfirm")}
        </Button>
      </div>
    </form>
  );
}

function AddPerson(props: { readonly onClose: () => void; readonly onCreated: (name: string) => Promise<void> }) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<UserRole>("user");
  const [quotaGb, setQuotaGb] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const created = await call(
        api.POST("/api/v1/users", {
          body: {
            name,
            displayName: displayName.trim() || null,
            password,
            role,
            quotaBytes: quotaGb.trim() === "" ? null : Math.round(Number(quotaGb) * GB),
            locale: null,
          },
        }),
      );
      await props.onCreated(created.name);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      title={t("people.addHeading")}
      onClose={props.onClose}
      actions={
        <>
          <Button onClick={props.onClose}>{t("common.cancel")}</Button>
          <Button type="submit" form="add-person" variant="primary" disabled={busy} icon="plus">
            {t("people.create")}
          </Button>
        </>
      }
    >
      <form id="add-person" className="form" onSubmit={(event) => void submit(event)}>
        <Field
          label={t("people.name")}
          help={t("setup.admin.nameHelp")}
          value={name}
          onChange={(e) => setName(e.target.value)}
          autoComplete="off"
          autoCapitalize="none"
          spellCheck={false}
          required
          autoFocus
        />
        <Field
          label={t("people.displayName")}
          help={t("people.displayNameHelp")}
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          maxLength={128}
        />
        <Field
          label={t("people.password")}
          help={t("setup.admin.passwordHelp", { minLength: MIN_PASSWORD_LENGTH })}
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="new-password"
          minLength={MIN_PASSWORD_LENGTH}
          required
        />
        <div className="field-row">
          <Select
            label={t("people.role")}
            value={role}
            options={ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) }))}
            onChange={setRole}
          />
          <Field
            label={t("people.quota")}
            help={t("people.quotaHelp")}
            type="number"
            min={0}
            step="any"
            value={quotaGb}
            onChange={(e) => setQuotaGb(e.target.value)}
          />
        </div>
        <p className="help">{t(`people.roleHelp.${role}`)}</p>
        <ErrorMessage error={error} />
      </form>
    </Dialog>
  );
}
