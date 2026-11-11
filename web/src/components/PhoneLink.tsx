import { QRCodeSVG } from "qrcode.react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { ErrorMessage, Notice } from "./ui.tsx";

interface Code {
  readonly value: string;
  readonly expiresAt: number;
}

/**
 * Shows a QR code that signs a phone in to this account (DESIGN.md 6.5).
 * The code holds the server's address and a single-use link code; the
 * countdown shows how long it still works.
 */
export function PhoneLink({ autoStart = false }: { readonly autoStart?: boolean }) {
  const { t } = useTranslation();
  const [code, setCode] = useState<Code | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const create = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const link = await call(api.POST("/api/v1/auth/link-codes"));
      setNow(Date.now());
      setCode({ value: link.code, expiresAt: Date.now() + Number(link.expiresInSeconds) * 1000 });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    if (autoStart) {
      void create();
    }
    // Only on first show; a new code is asked for with the button.
  }, []);

  useEffect(() => {
    if (!code) {
      return;
    }

    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [code]);

  const secondsLeft = code ? Math.max(0, Math.ceil((code.expiresAt - now) / 1000)) : 0;
  const expired = code !== null && secondsLeft === 0;
  const content = code
    ? `atrium://link?server=${encodeURIComponent(window.location.origin)}&code=${encodeURIComponent(code.value)}`
    : "";

  return (
    <div className="phone-link">
      <ErrorMessage error={error} />
      {code && !expired ? (
        <>
          <QRCodeSVG value={content} size={220} marginSize={2} role="img" aria-label={t("devices.phone.codeLabel")} />
          <p className="help">{t("devices.phone.validFor", { seconds: secondsLeft })}</p>
        </>
      ) : null}
      {expired ? <Notice kind="warning">{t("devices.phone.expired")}</Notice> : null}
      {!code || expired ? (
        <button type="button" onClick={() => void create()} disabled={busy}>
          {code ? t("setup.done.newCode") : t("devices.phone.show")}
        </button>
      ) : null}
    </div>
  );
}
