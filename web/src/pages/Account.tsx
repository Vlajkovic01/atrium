import { isLocale } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { ErrorMessage, Field, LanguagePicker, Notice } from "../components/ui.tsx";
import { currentLocale } from "../i18n.ts";

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
    return <p>{t("common.loading")}</p>;
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
      <h1>{t("account.heading")}</h1>
      <p>{t("account.signedInAs", { name: me.data.name })}</p>
      {me.data.passwordResetByAdmin ? <Notice kind="warning">{t("account.resetNotice")}</Notice> : null}

      <form className="panel" onSubmit={(event) => void save(event)}>
        <Field
          label={t("account.displayName")}
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          maxLength={128}
          required
        />
        <LanguagePicker />
        {saved ? <Notice kind="ok">{t("account.saved")}</Notice> : null}
        <ErrorMessage error={error} />
        <div className="actions">
          <button type="submit" className="primary">
            {t("common.save")}
          </button>
        </div>
      </form>

      <ChangePassword />
    </>
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
    <form className="panel" onSubmit={(event) => void submit(event)}>
      <h2>{t("account.passwordHeading")}</h2>
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
        <button type="submit" className="primary" disabled={busy}>
          {t("account.changePassword")}
        </button>
      </div>
    </form>
  );
}
