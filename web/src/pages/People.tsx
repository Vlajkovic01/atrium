import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Select } from "../components/Select.tsx";
import { Avatar, Badge, Button, Card, ErrorMessage, Field, Notice, PageHeader } from "../components/ui.tsx";
import { useSession } from "../session.tsx";

type AdminUserView = components["schemas"]["AdminUserView"];
type UserRole = components["schemas"]["UserRole"];

const ROLES: readonly UserRole[] = ["admin", "user", "guest"];
const MIN_PASSWORD_LENGTH = 8;

/**
 * Administration of accounts (DESIGN.md 6.3). An admin sees accounts,
 * never content. Every change sends the version that was read, so two
 * admins editing the same person cannot silently undo each other.
 */
export function People() {
  const { t } = useTranslation();
  const { user: me } = useSession();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [resetting, setResetting] = useState<string | null>(null);
  const people = useQuery({
    queryKey: ["people"],
    queryFn: () => call(api.GET("/api/v1/users", { params: { query: { limit: 200 } } })),
  });

  const refresh = (): Promise<void> => queryClient.invalidateQueries({ queryKey: ["people"] });

  const change = async (person: AdminUserView, body: { role?: UserRole; disabled?: boolean }): Promise<void> => {
    setError(null);
    setNotice(null);
    try {
      await call(
        api.PATCH("/api/v1/users/{id}", {
          params: { path: { id: person.id }, header: { "If-Match": `"${person.version}"` } },
          body: {
            displayName: null,
            role: body.role ?? null,
            disabled: body.disabled ?? null,
            quotaBytes: null,
            clearQuota: false,
          },
        }),
      );
    } catch (e) {
      setError(e);
    } finally {
      await refresh();
    }
  };

  const toggleDisabled = (person: AdminUserView): void => {
    if (!person.disabled && !window.confirm(t("people.disableConfirm", { name: person.name }))) {
      return;
    }

    void change(person, { disabled: !person.disabled });
  };

  return (
    <>
      <PageHeader title={t("people.heading")} lead={t("people.body")} />
      {notice ? <Notice kind="ok">{notice}</Notice> : null}
      <ErrorMessage error={error} />

      {people.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {people.isError ? <ErrorMessage error={people.error} /> : null}
      {people.data ? (
        <ul className="rows">
          {people.data.items.map((person) => {
            const isMe = person.id === me?.id;
            return (
              <li key={person.id} className="row is-stacked">
                <div className="row-line">
                  <Avatar name={person.displayName} />
                  <div className="row-main">
                    <p className="row-title">
                      {person.displayName}
                      {isMe ? <Badge tone="accent">{t("people.you")}</Badge> : null}
                      {person.disabled ? <Badge tone="danger">{t("people.disabledBadge")}</Badge> : null}
                      {person.totpEnabled ? <Badge tone="muted">{t("people.twoStep")}</Badge> : null}
                    </p>
                    <p className="row-meta">
                      {t(`roles.${person.role}`)}
                      {person.displayName === person.name ? null : (
                        <>
                          <span className="dot" />
                          {person.name}
                        </>
                      )}
                    </p>
                  </div>
                  <div className="row-actions">
                    <RoleSelect person={person} disabled={isMe} onChange={(role) => void change(person, { role })} />
                    {isMe ? null : (
                      <>
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => setResetting(resetting === person.id ? null : person.id)}
                        >
                          {t("people.resetPassword")}
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => toggleDisabled(person)}>
                          {person.disabled ? t("people.enable") : t("people.disable")}
                        </Button>
                      </>
                    )}
                  </div>
                </div>
                {resetting === person.id ? (
                  <ResetPassword
                    person={person}
                    onCancel={() => setResetting(null)}
                    onDone={() => {
                      setResetting(null);
                      setNotice(t("people.resetDone", { name: person.name }));
                    }}
                  />
                ) : null}
              </li>
            );
          })}
        </ul>
      ) : null}

      <AddPerson
        onCreated={async (name) => {
          setNotice(t("people.created", { name }));
          await refresh();
        }}
      />
    </>
  );
}

function RoleSelect(props: {
  readonly person: AdminUserView;
  readonly disabled: boolean;
  readonly onChange: (role: UserRole) => void;
}) {
  const { t } = useTranslation();
  return (
    <Select
      variant="inline"
      label={t("people.roleOf", { name: props.person.name })}
      hideLabel
      value={props.person.role}
      disabled={props.disabled}
      options={ROLES.map((role) => ({ value: role, label: t(`roles.${role}`) }))}
      onChange={props.onChange}
    />
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
    <form className="row-form form" onSubmit={(event) => void submit(event)}>
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

function AddPerson({ onCreated }: { readonly onCreated: (name: string) => Promise<void> }) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<UserRole>("user");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const created = await call(
        api.POST("/api/v1/users", {
          body: { name, displayName: null, password, role, quotaBytes: null, locale: null },
        }),
      );
      setName("");
      setPassword("");
      setRole("user");
      await onCreated(created.name);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t("people.addHeading")} icon="plus">
      <form className="form" onSubmit={(event) => void submit(event)}>
        <div className="field-row">
          <Field
            label={t("people.name")}
            help={t("setup.admin.nameHelp")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoComplete="off"
            autoCapitalize="none"
            spellCheck={false}
            required
          />
          <Select
            label={t("people.role")}
            value={role}
            options={ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) }))}
            onChange={setRole}
          />{" "}
        </div>
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
        <ErrorMessage error={error} />
        <div className="actions">
          <Button type="submit" variant="primary" disabled={busy} icon="plus">
            {t("people.create")}
          </Button>
        </div>
      </form>
    </Card>
  );
}
