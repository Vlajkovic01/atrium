import type { components } from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call, thisDevice } from "../api.ts";
import { PhoneLink } from "../components/PhoneLink.tsx";
import { Check, ErrorMessage, Field, InsecureBanner, LanguagePicker, Notice } from "../components/ui.tsx";
import { formatBytes } from "../i18n.ts";
import { useSession } from "../session.tsx";

export type SetupState = components["schemas"]["SetupState"];
type StorageTestResult = components["schemas"]["StorageTestResult"];
type StorageKind = "localFileSystem" | "networkShare" | "s3";

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
  const [step, setStep] = useState<Step>(() => firstStep(state));
  const [token, setToken] = useState("");
  const index = STEPS.indexOf(step);

  return (
    <main className="card card-wide">
      <InsecureBanner />
      <header className="card-header">
        <h1>{t("setup.title")}</h1>
        <LanguagePicker />
      </header>
      <p className="progress-label">
        {t("setup.progress", { current: index + 1, total: STEPS.length })} · {t(`setup.steps.${step}`)}
      </p>
      <progress value={index + 1} max={STEPS.length} aria-hidden="true" />

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
      {step === "storage" ? <Storage next={() => setStep("access")} /> : null}
      {step === "access" ? <Access next={() => setStep("done")} /> : null}
      {step === "done" ? <Done /> : null}
    </main>
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
      onSubmit={(event) => {
        event.preventDefault();
        props.next();
      }}
    >
      <h2>{t("setup.welcome.heading")}</h2>
      <p>{t("setup.welcome.body")}</p>
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
        <button type="submit" className="primary">
          {t("common.continue")}
        </button>
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
    <form onSubmit={(event) => void submit(event)}>
      <h2>{t("setup.admin.heading")}</h2>
      <p>{t("setup.admin.body")}</p>
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
      {mismatch ? (
        <p className="message message-error" role="alert">
          {t("setup.admin.mismatch")}
        </p>
      ) : null}
      <ErrorMessage error={error} />
      <div className="actions">
        <button type="button" onClick={props.back}>
          {t("common.back")}
        </button>
        <button type="submit" className="primary" disabled={busy}>
          {t("setup.admin.submit")}
        </button>
      </div>
    </form>
  );
}

function Storage({ next }: { readonly next: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<StorageKind>("localFileSystem");
  const [name, setName] = useState(() => t("setup.storage.defaultName"));
  const [path, setPath] = useState("/data");
  const [createDirectory, setCreateDirectory] = useState(true);
  const [s3, setS3] = useState({
    bucket: "",
    serviceUrl: "",
    region: "",
    accessKey: "",
    secretKey: "",
    keyPrefix: "",
    forcePathStyle: true,
  });
  const [result, setResult] = useState<StorageTestResult | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState<"test" | "save" | null>(null);

  const body = () =>
    kind === "s3"
      ? {
          name,
          kind,
          local: null,
          s3: {
            bucket: s3.bucket,
            accessKey: s3.accessKey,
            secretKey: s3.secretKey,
            serviceUrl: s3.serviceUrl || null,
            region: s3.region || null,
            forcePathStyle: s3.forcePathStyle,
            keyPrefix: s3.keyPrefix || null,
          },
        }
      : { name, kind, local: { path, createDirectory }, s3: null };

  const changed = (): void => {
    setResult(null);
    setError(null);
  };

  const test = async (): Promise<void> => {
    setBusy("test");
    changed();
    try {
      setResult(await call(api.POST("/api/v1/storage-locations/test", { body: body() })));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  };

  const save = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy("save");
    changed();
    try {
      await call(api.POST("/api/v1/storage-locations", { body: body() }));
      await queryClient.invalidateQueries({ queryKey: ["setup-state"] });
      next();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  };

  const kinds: readonly (readonly [StorageKind, string])[] = [
    ["localFileSystem", t("setup.storage.kindLocal")],
    ["networkShare", t("setup.storage.kindShare")],
    ["s3", t("setup.storage.kindS3")],
  ];

  // A folder that does not exist yet is expected when it will be created
  // on saving, so the test result says so instead of reporting a failure.
  const willBeCreated = result?.errorCode === "storage.root_missing" && createDirectory && kind !== "s3";

  return (
    <form onSubmit={(event) => void save(event)}>
      <h2>{t("setup.storage.heading")}</h2>
      <p>{t("setup.storage.body")}</p>

      <fieldset>
        <legend>{t("setup.storage.kind")}</legend>
        {kinds.map(([value, label]) => (
          <label key={value} className="radio">
            <input
              type="radio"
              name="kind"
              value={value}
              checked={kind === value}
              onChange={() => {
                setKind(value);
                changed();
              }}
            />
            {label}
          </label>
        ))}
      </fieldset>

      <Field
        label={t("setup.storage.name")}
        value={name}
        onChange={(e) => {
          setName(e.target.value);
          changed();
        }}
        maxLength={128}
        required
      />

      {kind === "s3" ? (
        <>
          <Field
            label={t("setup.storage.bucket")}
            value={s3.bucket}
            onChange={(e) => {
              setS3({ ...s3, bucket: e.target.value });
              changed();
            }}
            spellCheck={false}
            required
          />
          <Field
            label={`${t("setup.storage.serviceUrl")} (${t("common.optional")})`}
            help={t("setup.storage.serviceUrlHelp", { example: "https://s3.example.com" })}
            value={s3.serviceUrl}
            onChange={(e) => {
              setS3({ ...s3, serviceUrl: e.target.value });
              changed();
            }}
            type="url"
            spellCheck={false}
          />
          <Field
            label={`${t("setup.storage.region")} (${t("common.optional")})`}
            value={s3.region}
            onChange={(e) => {
              setS3({ ...s3, region: e.target.value });
              changed();
            }}
            spellCheck={false}
          />
          <Field
            label={t("setup.storage.accessKey")}
            value={s3.accessKey}
            onChange={(e) => {
              setS3({ ...s3, accessKey: e.target.value });
              changed();
            }}
            autoComplete="off"
            spellCheck={false}
            required
          />
          <Field
            label={t("setup.storage.secretKey")}
            type="password"
            value={s3.secretKey}
            onChange={(e) => {
              setS3({ ...s3, secretKey: e.target.value });
              changed();
            }}
            autoComplete="off"
            required
          />
          <Field
            label={`${t("setup.storage.keyPrefix")} (${t("common.optional")})`}
            value={s3.keyPrefix}
            onChange={(e) => {
              setS3({ ...s3, keyPrefix: e.target.value });
              changed();
            }}
            spellCheck={false}
          />
          <Check
            label={t("setup.storage.forcePathStyle")}
            checked={s3.forcePathStyle}
            onChange={(e) => {
              setS3({ ...s3, forcePathStyle: e.target.checked });
              changed();
            }}
          />
        </>
      ) : (
        <>
          <Field
            label={t("setup.storage.path")}
            help={t("setup.storage.pathHelp", { example: "/data" })}
            value={path}
            onChange={(e) => {
              setPath(e.target.value);
              changed();
            }}
            spellCheck={false}
            required
          />
          <Check
            label={t("setup.storage.createDirectory")}
            checked={createDirectory}
            onChange={(e) => {
              setCreateDirectory(e.target.checked);
              changed();
            }}
          />
          {kind === "networkShare" ? <Notice kind="info">{t("setup.storage.shareNote")}</Notice> : null}
        </>
      )}

      {result?.ok && result.holdsAtriumData ? <Notice kind="warning">{t("setup.storage.holdsData")}</Notice> : null}
      {result?.ok && !result.holdsAtriumData ? (
        <Notice kind="ok">
          {result.freeBytes != null
            ? t("setup.storage.testOkFree", { free: formatBytes(Number(result.freeBytes)) })
            : t("setup.storage.testOk")}
        </Notice>
      ) : null}
      {result && !result.ok && !willBeCreated ? (
        <p className="message message-error" role="alert">
          {t([`errors.${result.errorCode ?? "storage.unavailable"}`, "errors.storage.unavailable"])}
        </p>
      ) : null}
      {willBeCreated ? <Notice kind="info">{t("setup.storage.willCreate")}</Notice> : null}
      <ErrorMessage error={error} />

      <p className="help">{t("setup.storage.formatNote")}</p>

      <div className="actions">
        <button type="button" onClick={() => void test()} disabled={busy !== null}>
          {busy === "test" ? t("setup.storage.testing") : t("setup.storage.test")}
        </button>
        <button type="submit" className="primary" disabled={busy !== null}>
          {t("setup.storage.save")}
        </button>
      </div>
    </form>
  );
}

function Access({ next }: { readonly next: () => void }) {
  const { t } = useTranslation();
  return (
    <section>
      <h2>{t("setup.access.heading")}</h2>
      <p>{t("setup.access.address", { address: window.location.origin })}</p>
      {window.location.protocol === "https:" ? <Notice kind="ok">{t("setup.access.secure")}</Notice> : null}
      <p>{t("setup.access.outside")}</p>
      <ul>
        <li>{t("setup.access.optionCaddy")}</li>
        <li>{t("setup.access.optionProxy")}</li>
        <li>{t("setup.access.optionTunnel")}</li>
      </ul>
      <p>{t("setup.access.later")}</p>
      <div className="actions">
        <button type="button" className="primary" onClick={next}>
          {t("common.continue")}
        </button>
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
      <h2>{t("setup.done.heading")}</h2>
      <ErrorMessage error={error} />
      {completed ? (
        <>
          <p>{t("setup.done.body")}</p>
          <h3>{t("setup.done.phoneHeading")}</h3>
          <p>{t("setup.done.phoneBody")}</p>
          <PhoneLink autoStart />
          <div className="actions">
            <button
              type="button"
              className="primary"
              onClick={() => void queryClient.invalidateQueries({ queryKey: ["setup-state"] })}
            >
              {t("setup.done.open")}
            </button>
          </div>
        </>
      ) : null}
    </section>
  );
}
