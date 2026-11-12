import type { components } from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call, thisDevice } from "../api.ts";
import { AuthLayout } from "../components/AuthLayout.tsx";
import { PhoneLink } from "../components/PhoneLink.tsx";
import { StorageForm } from "../components/StorageForm.tsx";
import { Button, ErrorMessage, Field, Notice } from "../components/ui.tsx";
import { useSession } from "../session.tsx";

export type SetupState = components["schemas"]["SetupState"];

const STEPS = ["welcome", "admin", "storage", "access", "done"] as const;
type Step = (typeof STEPS)[number];

const MIN_PASSWORD_LENGTH = 8;

/** Where the wizard resumes, given what the server already has. */
function firstStep(state: SetupState): Step {
  if (!state.adminExists) {
    return "welcome";
  }

  return state.storageConfigured ? "access" : "storage";
}

export function Setup({ state }: { readonly state: SetupState }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [step, setStep] = useState<Step>(() => firstStep(state));
  const [token, setToken] = useState("");
  const index = STEPS.indexOf(step);

  return (
    <AuthLayout wide current={index} steps={STEPS.map((key) => ({ key, label: t(`setup.steps.${key}`) }))}>
      <p className="eyebrow">{t("setup.progress", { current: index + 1, total: STEPS.length })}</p>

      {step === "welcome" ? (
        <Welcome
          tokenRequired={state.setupTokenRequired}
          token={token}
          setToken={setToken}
          next={() => setStep("admin")}
        />
      ) : null}
      {step === "admin" ? (
        <Admin token={token} back={() => setStep("welcome")} next={() => setStep("storage")} />
      ) : null}
      {step === "storage" ? (
        <section>
          <h1 className="auth-title">{t("setup.storage.heading")}</h1>
          <p className="lead">{t("setup.storage.body")}</p>
          <StorageForm
            defaultName={t("setup.storage.defaultName")}
            submitLabel={t("setup.storage.save")}
            onSaved={async () => {
              await queryClient.invalidateQueries({ queryKey: ["setup-state"] });
              setStep("access");
            }}
          />
        </section>
      ) : null}
      {step === "access" ? <Access next={() => setStep("done")} /> : null}
      {step === "done" ? <Done /> : null}
    </AuthLayout>
  );
}

function Welcome(props: {
  readonly tokenRequired: boolean;
  readonly token: string;
  readonly setToken: (value: string) => void;
  readonly next: () => void;
}) {
  const { t } = useTranslation();
  return (
    <form
      className="form"
      onSubmit={(event) => {
        event.preventDefault();
        props.next();
      }}
    >
      <h1 className="auth-title">{t("setup.welcome.heading")}</h1>
      <p className="lead">{t("setup.welcome.body")}</p>
      {props.tokenRequired ? (
        <Field
          label={t("setup.welcome.tokenLabel")}
          help={t("setup.welcome.tokenHelp", { command: "docker compose logs atrium" })}
          value={props.token}
          onChange={(e) => props.setToken(e.target.value)}
          autoComplete="off"
          spellCheck={false}
          required
        />
      ) : null}
      <div className="actions">
        <Button type="submit" variant="primary">
          {t("common.continue")}
        </Button>
      </div>
    </form>
  );
}

function Admin(props: { readonly token: string; readonly back: () => void; readonly next: () => void }) {
  const { t } = useTranslation();
  const { signIn } = useSession();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [mismatch, setMismatch] = useState(false);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setError(null);
    setMismatch(password !== confirm);
    if (password !== confirm) {
      return;
    }

    setBusy(true);
    try {
      await call(
        api.POST("/api/v1/setup/admin", {
          body: { name, displayName: null, password, locale: null, setupToken: props.token || null },
        }),
      );
      // The wizard continues as the admin it just created.
      const login = await call(api.POST("/api/v1/auth/login", { body: { name, password, device: thisDevice } }));
      if (login.tokens) {
        signIn(login.tokens);
      }

      await queryClient.invalidateQueries({ queryKey: ["setup-state"] });
      props.next();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="form" onSubmit={(event) => void submit(event)}>
      <h1 className="auth-title">{t("setup.admin.heading")}</h1>
      <p className="lead">{t("setup.admin.body")}</p>
      <Field
        label={t("setup.admin.name")}
        help={t("setup.admin.nameHelp")}
        value={name}
        onChange={(e) => setName(e.target.value)}
        autoComplete="username"
        autoCapitalize="none"
        spellCheck={false}
        required
        autoFocus
      />
      <Field
        label={t("setup.admin.password")}
        help={t("setup.admin.passwordHelp", { minLength: MIN_PASSWORD_LENGTH })}
        type="password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        autoComplete="new-password"
        minLength={MIN_PASSWORD_LENGTH}
        required
      />
      <Field
        label={t("setup.admin.confirm")}
        type="password"
        value={confirm}
        onChange={(e) => setConfirm(e.target.value)}
        autoComplete="new-password"
        required
      />
      {mismatch ? <Notice kind="error">{t("setup.admin.mismatch")}</Notice> : null}
      <ErrorMessage error={error} />
      <div className="actions">
        <Button variant="ghost" onClick={props.back}>
          {t("common.back")}
        </Button>
        <Button type="submit" variant="primary" disabled={busy}>
          {t("setup.admin.submit")}
        </Button>
      </div>
    </form>
  );
}

function Access({ next }: { readonly next: () => void }) {
  const { t } = useTranslation();
  return (
    <section>
      <h1 className="auth-title">{t("setup.access.heading")}</h1>
      <p className="lead">{t("setup.access.address", { address: window.location.origin })}</p>
      {window.location.protocol === "https:" ? <Notice kind="ok">{t("setup.access.secure")}</Notice> : null}
      <p>{t("setup.access.outside")}</p>
      <ul className="options">
        <li>{t("setup.access.optionCaddy")}</li>
        <li>{t("setup.access.optionProxy")}</li>
        <li>{t("setup.access.optionTunnel")}</li>
      </ul>
      <p className="fineprint">{t("setup.access.later")}</p>
      <div className="actions">
        <Button variant="primary" onClick={next}>
          {t("common.continue")}
        </Button>
      </div>
    </section>
  );
}

function Done() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const [completed, setCompleted] = useState(false);

  useEffect(() => {
    let cancelled = false;
    call(api.POST("/api/v1/setup/complete"))
      .then(() => {
        if (!cancelled) {
          setCompleted(true);
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) {
          setError(e);
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <section>
      <h1 className="auth-title">{t("setup.done.heading")}</h1>
      <ErrorMessage error={error} />
      {completed ? (
        <>
          <p className="lead">{t("setup.done.body")}</p>
          <div className="callout">
            <h2 className="callout-title">{t("setup.done.phoneHeading")}</h2>
            <p>{t("setup.done.phoneBody")}</p>
            <PhoneLink autoStart />
          </div>
          <div className="actions">
            <Button variant="primary" onClick={() => void queryClient.invalidateQueries({ queryKey: ["setup-state"] })}>
              {t("setup.done.open")}
            </Button>
          </div>
        </>
      ) : null}
    </section>
  );
}
