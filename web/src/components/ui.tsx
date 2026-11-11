import { localeNames, supportedLocales, type Locale } from "@atrium/shared";
import { errorMessageKeys } from "@atrium/shared";
import { useId, type InputHTMLAttributes, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "../api.ts";
import { currentLocale, setLocale } from "../i18n.ts";

/** The message for an error from the API, in the user's language. */
export function useErrorText(): (error: unknown) => string {
  const { t } = useTranslation();
  return (error) => {
    if (error instanceof ApiError) {
      return t([...errorMessageKeys(error.problem.code)], error.problem.params);
    }

    return t("errors.unknown");
  };
}

export function ErrorMessage({ error }: { readonly error: unknown }) {
  const text = useErrorText();
  if (!error) {
    return null;
  }

  return (
    <p className="message message-error" role="alert">
      {text(error)}
    </p>
  );
}

export function Notice({ kind, children }: { readonly kind: "ok" | "info" | "warning"; readonly children: ReactNode }) {
  return (
    <p className={`message message-${kind}`} role="status">
      {children}
    </p>
  );
}

interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
  readonly help?: ReactNode;
}

/** A labelled input. The help text is tied to the input for screen readers. */
export function Field({ label, help, ...input }: FieldProps) {
  const id = useId();
  const helpId = `${id}-help`;
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} aria-describedby={help ? helpId : undefined} {...input} />
      {help ? (
        <p id={helpId} className="help">
          {help}
        </p>
      ) : null}
    </div>
  );
}

interface CheckProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
}

export function Check({ label, ...input }: CheckProps) {
  const id = useId();
  return (
    <div className="check">
      <input id={id} type="checkbox" {...input} />
      <label htmlFor={id}>{label}</label>
    </div>
  );
}

export function LanguagePicker() {
  const { t } = useTranslation();
  const id = useId();
  return (
    <div className="language">
      <label htmlFor={id}>{t("language.label")}</label>
      <select id={id} value={currentLocale()} onChange={(event) => void setLocale(event.target.value as Locale)}>
        {supportedLocales.map((locale) => (
          // Each language is named in its own script, so it can be found
          // by someone who cannot read the current one.
          <option key={locale} value={locale} lang={locale}>
            {localeNames[locale]}
          </option>
        ))}
      </select>
    </div>
  );
}

/** Shown on every screen when the page was loaded over plain HTTP from another machine. */
export function InsecureBanner() {
  const { t } = useTranslation();
  const local = ["localhost", "127.0.0.1", "[::1]"].includes(window.location.hostname);
  if (window.location.protocol === "https:" || local) {
    return null;
  }

  return (
    <p className="insecure" role="alert">
      {t("connection.insecure")}
    </p>
  );
}
