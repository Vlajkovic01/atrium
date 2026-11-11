import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";

import { api, call } from "../api.ts";
import { PhoneLink } from "../components/PhoneLink.tsx";
import { Check, ErrorMessage, Field, Notice } from "../components/ui.tsx";
import { formatDateTime } from "../i18n.ts";
import { useSession } from "../session.tsx";

type PairingDetails = components["schemas"]["PairingDetails"];

export function Devices() {
  const { t } = useTranslation();
  return (
    <>
      <h1>{t("devices.heading")}</h1>
      <p>{t("devices.body")}</p>
      <SessionList />
      <section className="panel">
        <h2>{t("devices.approve.heading")}</h2>
        <p>{t("devices.approve.body")}</p>
        <ApproveDevice />
      </section>
      <section className="panel">
        <h2>{t("devices.phone.heading")}</h2>
        <p>{t("devices.phone.body")}</p>
        <PhoneLink />
      </section>
    </>
  );
}

function SessionList() {
  const { t } = useTranslation();
  const { signOut } = useSession();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const sessions = useQuery({
    queryKey: ["sessions"],
    queryFn: () => call(api.GET("/api/v1/sessions")),
  });

  const revoke = async (id: string, name: string, current: boolean): Promise<void> => {
    if (!window.confirm(t("devices.signOutConfirm", { device: name }))) {
      return;
    }

    setError(null);
    try {
      if (current) {
        await signOut();
        return;
      }

      await call(api.DELETE("/api/v1/sessions/{id}", { params: { path: { id } } }));
      await queryClient.invalidateQueries({ queryKey: ["sessions"] });
    } catch (e) {
      setError(e);
    }
  };

  if (sessions.isPending) {
    return <p>{t("common.loading")}</p>;
  }

  if (sessions.isError) {
    return <ErrorMessage error={sessions.error} />;
  }

  return (
    <>
      <ErrorMessage error={error} />
      <ul className="list">
        {sessions.data.map((session) => (
          <li key={session.id}>
            <div>
              <strong>{session.deviceName}</strong>
              {session.current ? <span className="badge">{t("devices.thisDevice")}</span> : null}
              {session.readOnly ? <span className="badge">{t("devices.readOnly")}</span> : null}
              <p className="help">
                {t(`devices.kinds.${session.deviceKind}`)} ·{" "}
                {t("devices.lastUsed", { when: formatDateTime(session.lastSeenAt) })}
                {session.lastIp ? ` · ${session.lastIp}` : ""}
              </p>
            </div>
            <button type="button" onClick={() => void revoke(session.id, session.deviceName, session.current)}>
              {t("devices.signOut")}
            </button>
          </li>
        ))}
      </ul>
    </>
  );
}

/**
 * Approving a device without a keyboard (DESIGN.md 6.4). The code comes
 * from the address when the device's QR code was scanned, or is typed.
 * The user always sees which device is asking before deciding.
 */
function ApproveDevice() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const [code, setCode] = useState(() => params.get("code") ?? "");
  const [details, setDetails] = useState<PairingDetails | null>(null);
  const [readOnly, setReadOnly] = useState(false);
  const [hidePersonal, setHidePersonal] = useState(false);
  const [outcome, setOutcome] = useState<"approved" | "denied" | "notFound" | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true);
    setError(null);
    setOutcome(null);
    try {
      await action();
    } catch (e) {
      // "Not found" is an expected answer here, with its own explanation.
      if (e instanceof Error && e.message === "resource.not_found") {
        setDetails(null);
        setOutcome("notFound");
      } else {
        setError(e);
      }
    } finally {
      setBusy(false);
    }
  };

  const find = (event: FormEvent): void => {
    event.preventDefault();
    void run(async () => {
      setDetails(
        await call(api.GET("/api/v1/auth/pairing/{userCode}", { params: { path: { userCode: code.trim() } } })),
      );
    });
  };

  const finish = (result: "approved" | "denied"): void => {
    setDetails(null);
    setCode("");
    setOutcome(result);
    if (params.has("code")) {
      setParams({}, { replace: true });
    }
  };

  const approve = (): void => {
    if (!details) {
      return;
    }

    void run(async () => {
      await call(
        api.POST("/api/v1/auth/pairing/{userCode}/approve", {
          params: { path: { userCode: details.userCode } },
          body: { readOnly, hidePersonalLibrary: hidePersonal },
        }),
      );
      finish("approved");
      await queryClient.invalidateQueries({ queryKey: ["sessions"] });
    });
  };

  const deny = (): void => {
    if (!details) {
      return;
    }

    void run(async () => {
      await call(
        api.POST("/api/v1/auth/pairing/{userCode}/deny", { params: { path: { userCode: details.userCode } } }),
      );
      finish("denied");
    });
  };

  return (
    <>
      {outcome === "approved" ? <Notice kind="ok">{t("devices.approve.approved")}</Notice> : null}
      {outcome === "denied" ? <Notice kind="info">{t("devices.approve.denied")}</Notice> : null}
      {outcome === "notFound" ? <Notice kind="warning">{t("devices.approve.notFound")}</Notice> : null}
      <ErrorMessage error={error} />

      {details === null ? (
        <form onSubmit={find}>
          <Field
            label={t("devices.approve.code")}
            value={code}
            onChange={(e) => setCode(e.target.value)}
            placeholder="XXXX-XXXX"
            autoComplete="off"
            autoCapitalize="characters"
            spellCheck={false}
            required
          />
          <div className="actions">
            <button type="submit" disabled={busy}>
              {t("devices.approve.find")}
            </button>
          </div>
        </form>
      ) : (
        <div className="approval">
          <p>
            <strong>{t("devices.approve.found", { device: details.deviceName })}</strong>
          </p>
          <p className="help">
            {t(`devices.kinds.${details.deviceKind}`)}
            {details.platform ? ` · ${details.platform}` : ""}
            {details.requestIp ? ` · ${t("devices.approve.from", { address: details.requestIp })}` : ""}
          </p>
          <p className="code">{details.userCode}</p>
          <Notice kind="warning">{t("devices.approve.warning")}</Notice>
          <Check
            label={t("devices.approve.readOnly")}
            checked={readOnly}
            onChange={(e) => setReadOnly(e.target.checked)}
          />
          <Check
            label={t("devices.approve.hidePersonal")}
            checked={hidePersonal}
            onChange={(e) => setHidePersonal(e.target.checked)}
          />
          <div className="actions">
            <button type="button" onClick={deny} disabled={busy}>
              {t("devices.approve.deny")}
            </button>
            <button type="button" className="primary" onClick={approve} disabled={busy}>
              {t("devices.approve.approve")}
            </button>
          </div>
        </div>
      )}
    </>
  );
}
