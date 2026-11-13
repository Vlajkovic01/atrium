import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Badge, Button, Card, ErrorMessage, Field, Notice, PageHeader } from "../components/ui.tsx";
import { formatBytes, formatDateTime } from "../i18n.ts";

type Info = components["schemas"]["BackgroundWorkInfo"];
type ResourceClass = components["schemas"]["JobResourceClass"];

const queryKey = ["background-work"];

/**
 * How much of the machine background jobs may use, and a pause for when
 * the owner needs the machine for something else. Refreshes on its own
 * so the running and waiting counts stay current.
 */
export function BackgroundWork() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const info = useQuery({
    queryKey,
    queryFn: () => call(api.GET("/api/v1/admin/background-work")),
    refetchInterval: 5000,
  });

  const apply = async (request: () => Promise<Info>): Promise<boolean> => {
    setBusy(true);
    setError(null);
    try {
      queryClient.setQueryData(queryKey, await request());
      return true;
    } catch (e) {
      setError(e);
      return false;
    } finally {
      setBusy(false);
    }
  };

  const pause = (minutes: number | null) =>
    void apply(() => call(api.POST("/api/v1/admin/background-work/pause", { body: { minutes } })));
  const resume = () => void apply(() => call(api.POST("/api/v1/admin/background-work/resume")));

  const data = info.data;
  return (
    <>
      <PageHeader title={t("backgroundWork.heading")} lead={t("backgroundWork.body")} />
      <ErrorMessage error={error} />
      {info.isPending ? <p className="muted">{t("common.loading")}</p> : null}
      {info.isError ? <ErrorMessage error={info.error} /> : null}
      {data ? (
        <>
          <Card title={t("backgroundWork.machine")} icon="device">
            <p className="row-title">
              {t("backgroundWork.profile")}
              <Badge tone="accent">{t(`backgroundWork.profile${capitalize(data.profile)}`)}</Badge>
            </p>
            <p className="muted">
              {t("backgroundWork.machineFacts", {
                cores: Number(data.processorCount),
                memory: formatBytes(Number(data.memoryBytes)),
              })}
            </p>
          </Card>

          <Card title={t("backgroundWork.pauseHeading")} icon="pause">
            <Notice kind={data.paused ? "warning" : "ok"}>{pauseText(data, t)}</Notice>
            <p className="muted">{t("backgroundWork.pauseNote")}</p>
            <div className="actions">
              {data.pausedByAdmin ? (
                <Button variant="primary" icon="play" onClick={resume} disabled={busy}>
                  {t("backgroundWork.resume")}
                </Button>
              ) : null}
              <Button icon="pause" onClick={() => pause(60)} disabled={busy}>
                {t("backgroundWork.pauseHour")}
              </Button>
              <Button icon="pause" onClick={() => pause(24 * 60)} disabled={busy}>
                {t("backgroundWork.pauseDay")}
              </Button>
              <Button icon="pause" onClick={() => pause(null)} disabled={busy}>
                {t("backgroundWork.pauseOpen")}
              </Button>
            </div>
          </Card>

          <LimitsCard
            key={data.classes.map((c) => `${c.resourceClass}:${c.override ?? ""}`).join(",")}
            info={data}
            busy={busy}
            onSave={(limits) =>
              apply(() => call(api.PUT("/api/v1/admin/background-work/limits", { body: { limits } })))
            }
          />
        </>
      ) : null}
    </>
  );
}

function LimitsCard(props: {
  readonly info: Info;
  readonly busy: boolean;
  readonly onSave: (limits: { resourceClass: ResourceClass; limit: number | null }[]) => Promise<boolean>;
}) {
  const { t } = useTranslation();
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(props.info.classes.map((c) => [c.resourceClass, c.override == null ? "" : String(c.override)])),
  );
  const [saved, setSaved] = useState(false);
  const max = Number(props.info.maxLimit);

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setSaved(false);
    const limits = props.info.classes.map((c) => {
      const text = values[c.resourceClass]?.trim() ?? "";
      return { resourceClass: c.resourceClass, limit: text === "" ? null : Number(text) };
    });
    setSaved(await props.onSave(limits));
  };

  return (
    <Card title={t("backgroundWork.limitsHeading")} icon="list">
      <p className="muted">{t("backgroundWork.limitsNote")}</p>
      {saved ? <Notice kind="ok">{t("backgroundWork.saved")}</Notice> : null}
      <form onSubmit={(event) => void submit(event)}>
        {props.info.classes.map((c) => (
          <Field
            key={c.resourceClass}
            label={t(`backgroundWork.class.${c.resourceClass}`)}
            type="number"
            min={1}
            max={max}
            inputMode="numeric"
            placeholder={t("backgroundWork.default", { value: Number(c.default) })}
            value={values[c.resourceClass] ?? ""}
            onChange={(event) => setValues((previous) => ({ ...previous, [c.resourceClass]: event.target.value }))}
            help={t("backgroundWork.activity", { running: Number(c.running), queued: Number(c.queued) })}
          />
        ))}
        <div className="actions">
          <Button type="submit" variant="primary" disabled={props.busy}>
            {t("backgroundWork.save")}
          </Button>
        </div>
      </form>
    </Card>
  );
}

function pauseText(info: Info, t: TFunction): string {
  if (!info.paused) {
    return t("backgroundWork.running");
  }

  if (!info.pausedByAdmin) {
    return t("backgroundWork.pausedBusy");
  }

  return info.pausedUntil
    ? t("backgroundWork.pausedUntil", { when: formatDateTime(info.pausedUntil) })
    : t("backgroundWork.pausedOpen");
}

function capitalize(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}
