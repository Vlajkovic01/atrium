import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call, thisDevice } from "../api.ts";
import { AuthLayout } from "../components/AuthLayout.tsx";
import { Button, ErrorMessage, Field } from "../components/ui.tsx";
import { useSession } from "../session.tsx";

export function SignIn() {
  const { t } = useTranslation();
  const { signIn } = useSession();
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [challenge, setChallenge] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const submitPassword = (event: FormEvent): void => {
    event.preventDefault();
    void run(async () => {
      const result = await call(api.POST("/api/v1/auth/login", { body: { name, password, device: thisDevice } }));
      if (result.status === "totpRequired" && result.totpChallenge) {
        setChallenge(result.totpChallenge);
        setPassword("");
      } else if (result.tokens) {
        signIn(result.tokens);
      }
    });
  };

  const submitCode = (event: FormEvent): void => {
    event.preventDefault();
    void run(async () => {
      const result = await call(
        api.POST("/api/v1/auth/login/totp", { body: { challenge: challenge ?? "", code, device: thisDevice } }),
      );
      if (result.tokens) {
        signIn(result.tokens);
      }
    });
  };

  return (
    <AuthLayout>
      {challenge === null ? (
        <form onSubmit={submitPassword} className="form">
          <h1 className="auth-title">{t("signIn.heading")}</h1>
          <p className="lead">{t("signIn.body")}</p>
          <Field
            label={t("signIn.name")}
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoComplete="username"
            autoCapitalize="none"
            spellCheck={false}
            required
            autoFocus
          />
          <Field
            label={t("signIn.password")}
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
          <ErrorMessage error={error} />
          <Button type="submit" variant="primary" block disabled={busy}>
            {t("signIn.submit")}
          </Button>
        </form>
      ) : (
        <form onSubmit={submitCode} className="form">
          <h1 className="auth-title">{t("signIn.totpHeading")}</h1>
          <p className="lead">{t("signIn.totpBody")}</p>
          <Field
            label={t("signIn.code")}
            value={code}
            onChange={(e) => setCode(e.target.value)}
            autoComplete="one-time-code"
            inputMode="text"
            spellCheck={false}
            required
            autoFocus
          />
          <ErrorMessage error={error} />
          <Button type="submit" variant="primary" block disabled={busy}>
            {t("signIn.verify")}
          </Button>
          <Button
            variant="ghost"
            block
            onClick={() => {
              setChallenge(null);
              setCode("");
              setError(null);
            }}
          >
            {t("signIn.startOver")}
          </Button>
        </form>
      )}
    </AuthLayout>
  );
}
