import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { Icon } from "./Icon.tsx";
import { HeroArt, Logo } from "./Logo.tsx";
import { InsecureBanner, LanguagePicker } from "./ui.tsx";

export interface Step {
  readonly key: string;
  readonly label: string;
}

/**
 * The frame of the screens shown before the app itself: sign-in and the
 * setup wizard. One side carries the brand and, during setup, the list of
 * steps; the other side is the form. On a narrow screen the brand side
 * folds into a band at the top.
 */
export function AuthLayout(props: {
  readonly children: ReactNode;
  readonly steps?: readonly Step[];
  readonly current?: number;
  readonly wide?: boolean;
}) {
  const { t } = useTranslation();
  const { steps, current = 0 } = props;

  return (
    <div className="auth">
      <aside className="auth-hero">
        <HeroArt />
        <div className="hero-brand">
          <Logo size={36} />
        </div>
        <div className="hero-copy">
          <p className="hero-tagline">{t("brand.tagline")}</p>
          <p className="hero-subline">{t("brand.subline")}</p>
        </div>
        {steps ? (
          <ol className="stepper" aria-label={t("setup.title")}>
            {steps.map((step, index) => {
              const state = index < current ? "is-done" : index === current ? "is-current" : "";
              return (
                <li key={step.key} className={`step ${state}`} aria-current={index === current ? "step" : undefined}>
                  <span className="step-marker">{index < current ? <Icon name="check" size={14} /> : index + 1}</span>
                  <span className="step-label">{step.label}</span>
                </li>
              );
            })}
          </ol>
        ) : (
          <ul className="hero-points">
            <li>
              <Icon name="shield" size={18} />
              {t("brand.pointOwn")}
            </li>
            <li>
              <Icon name="devices" size={18} />
              {t("brand.pointApps")}
            </li>
            <li>
              <Icon name="lock" size={18} />
              {t("brand.pointOpen")}
            </li>
          </ul>
        )}
      </aside>

      <div className="auth-main">
        <header className="auth-top">
          <LanguagePicker />
        </header>
        <InsecureBanner />
        <main className={props.wide ? "auth-content is-wide" : "auth-content"}>{props.children}</main>
      </div>
    </div>
  );
}
