import { isLocale } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { QRCodeSVG } from "qrcode.react";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import { api, call } from "../api.ts";
import { Badge, Button, Card, ErrorMessage, Field, LanguagePicker, Notice, PageHeader } from "../components/ui.tsx";
import { currentLocale, formatBytes } from "../i18n.ts";

const MIN_PASSWORD_LENGTH = 8;

export function Account() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const me = useQuery({ queryKey: ["me"], queryFn: () => call(api.GET("/api/v1/me")) });
  const [displayName, setDisplayName] = useState("");
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (me.data) {
      setDisplayName(me.data.displayName);
    }
  }, [me.data]);

  // The language chosen here follows the account to other devices.
  const language = i18n.language;
  const savedLocale = me.data?.locale;
  useEffect(() => {
    if (savedLocale !== undefined && isLocale(language) && language !== savedLocale) {
      void call(api.PATCH("/api/v1/me", { body: { displayName: null, locale: language } }))
        .then(() => queryClient.invalidateQueries({ queryKey: ["me"] }))
        .catch(() => {
          // The language still applies on this browser.
        });
    }
  }, [language, savedLocale, queryClient]);

  if (me.isPending) {
    return <p className="muted">{t("common.loading")}</p>;
  }

  if (me.isError) {
    return <ErrorMessage error={me.error} />;
  }

  const save = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setError(null);
    setSaved(false);
    try {
      await call(api.PATCH("/api/v1/me", { body: { displayName, locale: currentLocale() } }));
      await queryClient.invalidateQueries({ queryKey: ["me"] });
      setSaved(true);
    } catch (e) {
      setError(e);
    }
  };

  return (
    <>
      <PageHeader title={t("account.heading")} lead={t("account.body")} />
      {me.data.passwordResetByAdmin ? <Notice kind="warning">{t("account.resetNotice")}</Notice> : null}

      <StorageUsage />

      <Card title={t("account.profileHeading")} icon="user">
        <form className="form" onSubmit={(event) => void save(event)}>
          <p className="card-lead">{t("account.signedInAs", { name: me.data.name })}</p>
          <Field
            label={t("account.displayName")}
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            maxLength={128}
            required
          />
          <div className="field">
            <span className="field-label">{t("language.label")}</span>
            <LanguagePicker />
          </div>
          {saved ? <Notice kind="ok">{t("account.saved")}</Notice> : null}
          <ErrorMessage error={error} />
          <div className="actions">
            <Button type="submit" variant="primary">
              {t("common.save")}
            </Button>
          </div>
        </form>
      </Card>

      <Card title={t("menu.openSettings")} icon="list">
        <p className="card-lead">{t("menu.openSettingsLead")}</p>
        <Link className="btn" to="/menu">
          {t("menu.openSettings")}
        </Link>
      </Card>

      <TwoStep enabled={me.data.totpEnabled} />
      <ChangePassword />
    </>
  );
}

type TwoStepStage =
  | { readonly kind: "idle" }
  | { readonly kind: "enrolling"; readonly secret: string; readonly uri: string }
  | { readonly kind: "codes"; readonly codes: readonly string[] }
  | { readonly kind: "disabling" };

/**
 * Two-step verification. It is enforced only after the user has proven
 * their authenticator works, and the recovery codes are shown exactly
 * once, so the screen makes the user acknowledge them.
 */
function TwoStep({ enabled }: { readonly enabled: boolean }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [stage, setStage] = useState<TwoStepStage>({ kind: "idle" });
  const [code, setCode] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);

  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const begin = (): void =>
    void run(async () => {
      const enrollment = await call(api.POST("/api/v1/me/totp"));
      setCode("");
      setStage({ kind: "enrolling", secret: enrollment.secret, uri: enrollment.provisioningUri });
    });

  const confirm = (event: FormEvent): void => {
    event.preventDefault();
    void run(async () => {
      const result = await call(api.POST("/api/v1/me/totp/confirm", { body: { code } }));
      setStage({ kind: "codes", codes: result.codes });
      await queryClient.invalidateQueries({ queryKey: ["me"] });
    });
  };

  const disable = (event: FormEvent): void => {
    event.preventDefault();
    void run(async () => {
      await call(api.POST("/api/v1/me/totp/disable", { body: { password } }));
      setPassword("");
      setStage({ kind: "idle" });
      setNotice(t("account.totp.disabled"));
      await queryClient.invalidateQueries({ queryKey: ["me"] });
    });
  };

  const copyCodes = async (codes: readonly string[]): Promise<void> => {
    try {
      await navigator.clipboard.writeText(codes.join("\n"));
      setCopied(true);
    } catch {
      // Copying is a convenience; the codes are on screen to write down.
    }
  };

  return (
    <Card
      title={t("account.totp.heading")}
      icon="shield"
      aside={
        <Badge tone={enabled ? "accent" : "muted"}>
          {t(enabled ? "account.totp.stateOn" : "account.totp.stateOff")}
        </Badge>
      }
    >
      <p className="card-lead">{enabled ? t("account.totp.on") : t("account.totp.off")}</p>
      {notice ? <Notice kind="ok">{notice}</Notice> : null}
      <ErrorMessage error={error} />

      {stage.kind === "idle" && !enabled ? (
        <div className="actions is-start">
          <Button variant="primary" onClick={begin} disabled={busy}>
            {t("account.totp.enable")}
          </Button>
        </div>
      ) : null}

      {stage.kind === "idle" && enabled ? (
        <div className="actions is-start">
          <Button onClick={() => setStage({ kind: "disabling" })}>{t("account.totp.disable")}</Button>
        </div>
      ) : null}

      {stage.kind === "enrolling" ? (
        <form className="form" onSubmit={confirm}>
          <p>{t("account.totp.scan")}</p>
          <div className="enroll">
            <figure className="qr-frame">
              <QRCodeSVG
                value={stage.uri}
                size={184}
                marginSize={0}
                bgColor="#ffffff"
                fgColor="#10201b"
                role="img"
                aria-label={t("account.totp.qrLabel")}
              />
            </figure>
            <div>
              <p className="fineprint">{t("account.totp.manual")}</p>
              <p className="secret">{stage.secret.replace(/(.{4})/g, "$1 ").trim()}</p>
            </div>
          </div>
          <Field
            label={t("account.totp.code")}
            value={code}
            onChange={(e) => setCode(e.target.value)}
            autoComplete="one-time-code"
            inputMode="numeric"
            spellCheck={false}
            required
          />
          <div className="actions">
            <Button variant="ghost" onClick={() => setStage({ kind: "idle" })}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" variant="primary" disabled={busy}>
              {t("account.totp.confirm")}
            </Button>
          </div>
        </form>
      ) : null}

      {stage.kind === "codes" ? (
        <div className="form">
          <h3 className="section-title">{t("account.totp.recoveryHeading")}</h3>
          <p>{t("account.totp.recoveryBody")}</p>
          <ul className="codes">
            {stage.codes.map((recovery) => (
              <li key={recovery}>{recovery}</li>
            ))}
          </ul>
          <div className="actions">
            <Button icon="copy" onClick={() => void copyCodes(stage.codes)}>
              {copied ? t("common.copied") : t("common.copy")}
            </Button>
            <Button variant="primary" onClick={() => setStage({ kind: "idle" })}>
              {t("account.totp.recoveryDone")}
            </Button>
          </div>
        </div>
      ) : null}

      {stage.kind === "disabling" ? (
        <form className="form" onSubmit={disable}>
          <p>{t("account.totp.disableBody")}</p>
          <Field
            label={t("signIn.password")}
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
          <div className="actions">
            <Button variant="ghost" onClick={() => setStage({ kind: "idle" })}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" variant="danger" disabled={busy}>
              {t("account.totp.disable")}
            </Button>
          </div>
        </form>
      ) : null}
    </Card>
  );
}

function ChangePassword() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [changed, setChanged] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setChanged(false);
    try {
      await call(api.POST("/api/v1/me/password", { body: { currentPassword: current, newPassword: next } }));
      setCurrent("");
      setNext("");
      setChanged(true);
      await queryClient.invalidateQueries({ queryKey: ["me"] });
      await queryClient.invalidateQueries({ queryKey: ["sessions"] });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t("account.passwordHeading")} icon="key">
      <form className="form" onSubmit={(event) => void submit(event)}>
        <Field
          label={t("account.currentPassword")}
          type="password"
          value={current}
          onChange={(e) => setCurrent(e.target.value)}
          autoComplete="current-password"
          required
        />
        <Field
          label={t("account.newPassword")}
          help={t("setup.admin.passwordHelp", { minLength: MIN_PASSWORD_LENGTH })}
          type="password"
          value={next}
          onChange={(e) => setNext(e.target.value)}
          autoComplete="new-password"
          minLength={MIN_PASSWORD_LENGTH}
          required
        />
        {changed ? <Notice kind="ok">{t("account.passwordChanged")}</Notice> : null}
        <ErrorMessage error={error} />
        <div className="actions">
          <Button type="submit" variant="primary" disabled={busy}>
            {t("account.changePassword")}
          </Button>
        </div>
      </form>
    </Card>
  );
}

/** Space used against the quota, split the way it is charged: files, trash and earlier versions. */
function StorageUsage() {
  const { t } = useTranslation();
  const usage = useQuery({ queryKey: ["usage"], queryFn: () => call(api.GET("/api/v1/me/usage")) });
  if (!usage.data) {
    return null;
  }

  const used = Number(usage.data.usedBytes);
  const quota =
    usage.data.quotaBytes === null || usage.data.quotaBytes === undefined ? null : Number(usage.data.quotaBytes);
  return (
    <Card title={t("account.storageHeading")} icon="drive">
      <p className="card-lead">
        {quota === null
          ? t("account.storageUsed", { used: formatBytes(used) })
          : t("account.storageOfQuota", { used: formatBytes(used), quota: formatBytes(quota) })}
      </p>
      {quota !== null ? (
        <progress
          className="upload-progress"
          max={quota}
          value={Math.min(used, quota)}
          aria-label={t("account.storageHeading")}
        />
      ) : null}
      <p className="row-meta">
        {t("account.storageSplit", {
          files: formatBytes(Number(usage.data.filesBytes)),
          trash: formatBytes(Number(usage.data.trashBytes)),
          versions: formatBytes(Number(usage.data.versionsBytes)),
        })}
      </p>
    </Card>
  );
}
