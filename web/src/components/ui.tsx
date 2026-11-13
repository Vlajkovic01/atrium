import { errorMessageKeys, localeNames, supportedLocales, type Locale } from "@atrium/shared";
import { useEffect, useId, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "../api.ts";
import { currentLocale, setLocale } from "../i18n.ts";
import { Icon, type IconName } from "./Icon.tsx";
import { Select } from "./Select.tsx";

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

type Tone = "ok" | "info" | "warning" | "error";

const toneIcon: Record<Tone, IconName> = { ok: "checkCircle", info: "info", warning: "alert", error: "error" };

/** A message with an icon. Errors interrupt screen readers; the rest are announced politely. */
export function Notice({
  kind,
  children,
  onClose,
}: {
  readonly kind: Tone;
  readonly children: ReactNode;
  readonly onClose?: () => void;
}) {
  const { t } = useTranslation();
  return (
    <div className={`alert alert-${kind}`} role={kind === "error" ? "alert" : "status"}>
      <Icon name={toneIcon[kind]} />
      <div className="alert-text">{children}</div>
      {onClose ? (
        <button type="button" className="icon-button alert-close" aria-label={t("common.close")} onClick={onClose}>
          <Icon name="close" size={16} />
        </button>
      ) : null}
    </div>
  );
}

/** How long a confirmation stays before it goes away by itself (D-045). */
export const FLASH_MS = 8000;

/**
 * A confirmation that something was done, such as "Saved". It can be
 * closed, and goes away by itself after a few seconds; a new message
 * starts the time again. Errors never go away by themselves.
 */
export function Flash({ message }: { readonly message: string | null }) {
  const [shown, setShown] = useState(message);
  useEffect(() => {
    setShown(message);
    if (!message) {
      return;
    }

    const timer = setTimeout(() => setShown(null), FLASH_MS);
    return () => clearTimeout(timer);
  }, [message]);
  return shown ? (
    <Notice kind="ok" onClose={() => setShown(null)}>
      {shown}
    </Notice>
  ) : null;
}

export function ErrorMessage({ error }: { readonly error: unknown }) {
  const text = useErrorText();
  return error ? <Notice kind="error">{text(error)}</Notice> : null;
}

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  readonly variant?: "primary" | "secondary" | "ghost" | "danger";
  readonly size?: "md" | "sm";
  readonly icon?: IconName;
  readonly block?: boolean;
}

export function Button({
  variant = "secondary",
  size = "md",
  icon,
  block,
  children,
  type,
  className,
  ...rest
}: ButtonProps) {
  const classes = ["btn", `btn-${variant}`, size === "sm" ? "btn-sm" : "", block ? "btn-block" : "", className ?? ""]
    .filter(Boolean)
    .join(" ");
  return (
    <button type={type ?? "button"} className={classes} {...rest}>
      {icon ? <Icon name={icon} size={size === "sm" ? 16 : 18} /> : null}
      {children}
    </button>
  );
}

interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
  readonly help?: ReactNode;
}

/**
 * A labelled input. The help text is tied to the input for screen
 * readers, and a password can be shown while typing it.
 */
export function Field({ label, help, type, ...input }: FieldProps) {
  const { t } = useTranslation();
  const id = useId();
  const helpId = `${id}-help`;
  const isPassword = type === "password";
  const [shown, setShown] = useState(false);

  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <div className={isPassword ? "control has-toggle" : "control"}>
        <input
          id={id}
          type={isPassword && shown ? "text" : type}
          aria-describedby={help ? helpId : undefined}
          {...input}
        />
        {isPassword ? (
          <button
            type="button"
            className="control-toggle"
            aria-label={shown ? t("common.hidePassword") : t("common.showPassword")}
            aria-pressed={shown}
            onClick={() => setShown((value) => !value)}
          >
            <Icon name={shown ? "eyeOff" : "eye"} size={18} />
          </button>
        ) : null}
      </div>
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

export interface Choice<T extends string> {
  readonly value: T;
  readonly title: string;
  readonly help: string;
  readonly icon: IconName;
}

/** A choice between a few options, each explained: radio buttons drawn as cards. */
export function ChoiceCards<T extends string>(props: {
  readonly legend: string;
  readonly name: string;
  readonly value: T;
  readonly choices: readonly Choice<T>[];
  readonly onChange: (value: T) => void;
}) {
  return (
    <fieldset className="choices">
      <legend>{props.legend}</legend>
      <div className="choice-grid">
        {props.choices.map((choice) => {
          const id = `${props.name}-${choice.value}`;
          return (
            <div key={choice.value} className="choice">
              <input
                type="radio"
                id={id}
                name={props.name}
                value={choice.value}
                checked={props.value === choice.value}
                onChange={() => props.onChange(choice.value)}
                aria-describedby={`${id}-help`}
              />
              <label htmlFor={id}>
                <span className="choice-icon">
                  <Icon name={choice.icon} size={22} />
                </span>
                <span className="choice-title">{choice.title}</span>
              </label>
              <p id={`${id}-help`} className="choice-help">
                {choice.help}
              </p>
            </div>
          );
        })}
      </div>
    </fieldset>
  );
}

export function LanguagePicker() {
  const { t } = useTranslation();
  return (
    <Select
      variant="pill"
      icon="globe"
      label={t("language.label")}
      hideLabel
      value={currentLocale()}
      // Each language is named in itself, so it can be found by someone
      // who cannot read the current one.
      options={supportedLocales.map((locale) => ({ value: locale, label: localeNames[locale] }))}
      onChange={(locale: Locale) => void setLocale(locale)}
    />
  );
}
export function Badge({
  tone = "muted",
  children,
}: {
  readonly tone?: "accent" | "gold" | "muted" | "danger";
  readonly children: ReactNode;
}) {
  return <span className={`badge badge-${tone}`}>{children}</span>;
}

/** A person's initials on a tinted disc. */
export function Avatar({ name }: { readonly name: string }) {
  const initials = name
    .split(/[\s._-]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]!.toUpperCase())
    .join("");
  return (
    <span className="avatar" aria-hidden="true">
      {initials || "?"}
    </span>
  );
}

export function PageHeader({
  title,
  lead,
  actions,
}: {
  readonly title: string;
  readonly lead?: string;
  readonly actions?: ReactNode;
}) {
  return (
    <header className="page-header">
      <div>
        <h1 className="page-title">{title}</h1>
        {lead ? <p className="page-lead">{lead}</p> : null}
      </div>
      {actions ? <div className="page-actions">{actions}</div> : null}
    </header>
  );
}

export function Card({
  title,
  icon,
  children,
  aside,
}: {
  readonly title?: string;
  readonly icon?: IconName;
  readonly children: ReactNode;
  readonly aside?: ReactNode;
}) {
  return (
    <section className="card">
      {title ? (
        <header className="card-header">
          <h2 className="card-title">
            {icon ? (
              <span className="card-icon">
                <Icon name={icon} size={18} />
              </span>
            ) : null}
            {title}
          </h2>
          {aside}
        </header>
      ) : null}
      <div className="card-body">{children}</div>
    </section>
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
    <div className="insecure" role="alert">
      <Icon name="alert" size={18} />
      <span>{t("connection.insecure")}</span>
    </div>
  );
}
