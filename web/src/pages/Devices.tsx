import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";

import { api, ApiError, call } from "../api.ts";
import type { IconName } from "../components/Icon.tsx";
import { Icon } from "../components/Icon.tsx";
import { PhoneLink } from "../components/PhoneLink.tsx";
import { Badge, Button, Card, Check, ErrorMessage, Field, Notice, PageHeader } from "../components/ui.tsx";
import { formatDateTime } from "../i18n.ts";
import { useSession } from "../session.tsx";

type PairingDetails = components["schemas"]["PairingDetails"];
type DeviceKind = components["schemas"]["DeviceKind"];

const kindIcon: Record<DeviceKind, IconName> = { web: "browser", mobile: "phone", tv: "tv", other: "device" };

export function Devices() {
  const { t } = useTranslation();
  return (
    <>
      <PageHeader title={t("devices.heading")} lead={t("devices.body")} />
      <SessionList />
      <div className="card-grid">
        <Card title={t("devices.approve.heading")} icon="tv">
          <p className="card-lead">{t("devices.approve.body")}</p>
          <ApproveDevice />
        </Card>
        <Card title={t("devices.phone.heading")} icon="phone">
          <p className="card-lead">{t("devices.phone.body")}</p>
          <PhoneLink />
        </Card>
      </div>
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
    return <p className="muted">{t("common.loading")}</p>;
  }

  if (sessions.isError) {
    return <ErrorMessage error={sessions.error} />;
  }

  return (
    <>
      <ErrorMessage error={error} />
      <ul className="rows">
        {sessions.data.map((session) => (
          <li key={session.id} className="row">
            <span className={session.current ? "row-icon is-accent" : "row-icon"}>
              <Icon name={kindIcon[session.deviceKind]} size={20} />
            </span>
            <div className="row-main">
              <p className="row-title">
                {session.deviceName}
                {session.current ? <Badge tone="accent">{t("devices.thisDevice")}</Badge> : null}
                {session.readOnly ? <Badge tone="gold">{t("devices.readOnly")}</Badge> : null}
              </p>
              <p className="row-meta">
                {t(`devices.kinds.${session.deviceKind}`)}
                <span className="dot" />
                {t("devices.lastUsed", { when: formatDateTime(session.lastSeenAt) })}
                {session.lastIp ? (
                  <>
                    <span className="dot" />
                    {session.lastIp}
                  </>
                ) : null}
              </p>
            </div>
            <div className="row-actions">
              <Button
                size="sm"
                variant="ghost"
                onClick={() => void revoke(session.id, session.deviceName, session.current)}
              >
                {t("devices.signOut")}
              </Button>
            </div>
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
      if (e instanceof ApiError && e.problem.code === "resource.not_found") {
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
        <form onSubmit={find} className="form">
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
            <Button type="submit" disabled={busy}>
              {t("devices.approve.find")}
            </Button>
          </div>
        </form>
      ) : (
        <div className="approval">
          <div className="approval-device">
            <span className="row-icon is-accent">
              <Icon name={kindIcon[details.deviceKind]} size={22} />
            </span>
            <div>
              <p className="row-title">{t("devices.approve.found", { device: details.deviceName })}</p>
              <p className="row-meta">
                {t(`devices.kinds.${details.deviceKind}`)}
                {details.platform ? (
                  <>
                    <span className="dot" />
                    {details.platform}
                  </>
                ) : null}
                {details.requestIp ? (
                  <>
                    <span className="dot" />
                    {t("devices.approve.from", { address: details.requestIp })}
                  </>
                ) : null}
              </p>
            </div>
          </div>
          <p className="pair-code" aria-label={details.userCode}>
            {[...details.userCode].map((char, index) =>
              char === "-" ? (
                <span key={index} className="pair-dash" aria-hidden="true" />
              ) : (
                <span key={index} className="pair-char" aria-hidden="true">
                  {char}
                </span>
              ),
            )}
          </p>
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
            <Button onClick={deny} disabled={busy}>
              {t("devices.approve.deny")}
            </Button>
            <Button variant="primary" onClick={approve} disabled={busy}>
              {t("devices.approve.approve")}
            </Button>
          </div>
        </div>
      )}
    </>
  );
}
