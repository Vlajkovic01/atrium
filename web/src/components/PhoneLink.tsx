import { QRCodeSVG } from "qrcode.react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Button, ErrorMessage, Notice } from "./ui.tsx";

interface Code {
  readonly value: string;
  readonly expiresAt: number;
  readonly lifetime: number;
}

/**
 * Shows a QR code that signs a phone in to this account (DESIGN.md 6.5).
 * The code holds the server's address and a single-use link code; the
 * bar underneath shows how long it still works.
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
      const lifetime = Number(link.expiresInSeconds);
      setNow(Date.now());
      setCode({ value: link.code, expiresAt: Date.now() + lifetime * 1000, lifetime });
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
    <div className="qr">
      <ErrorMessage error={error} />
      {code && !expired ? (
        <figure className="qr-frame">
          <QRCodeSVG
            value={content}
            size={208}
            marginSize={0}
            bgColor="#ffffff"
            fgColor="#10201b"
            role="img"
            aria-label={t("devices.phone.codeLabel")}
          />
          <progress className="countdown" value={secondsLeft} max={code.lifetime} aria-hidden="true" />
          <figcaption className="qr-caption">{t("devices.phone.validFor", { seconds: secondsLeft })}</figcaption>
        </figure>
      ) : null}
      {expired ? <Notice kind="warning">{t("devices.phone.expired")}</Notice> : null}
      {!code || expired ? (
        <Button onClick={() => void create()} disabled={busy} icon="qr">
          {code ? t("setup.done.newCode") : t("devices.phone.show")}
        </Button>
      ) : null}
    </div>
  );
}
