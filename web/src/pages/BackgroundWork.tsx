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
type Hardware = components["schemas"]["HardwareView"];

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

          <VideoEncodingCard />

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
      <FailedJobs />
    </>
  );
}

/**
 * Which video encoders work on this machine (DESIGN.md 7.5): each
 * candidate's test encode, the one streams use, and the last time a
 * hardware encoder failed during a stream. "Test again" after changing
 * the machine, its drivers or the devices given to the container.
 */
function VideoEncodingCard() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [testing, setTesting] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const hardware = useQuery({
    queryKey: ["hardware"],
    queryFn: () => call(api.GET("/api/v1/admin/hardware")),
  });

  const detect = async () => {
    setTesting(true);
    setError(null);
    try {
      queryClient.setQueryData(["hardware"], await call(api.POST("/api/v1/admin/hardware/detect")));
    } catch (e) {
      setError(e);
    } finally {
      setTesting(false);
    }
  };

  const data: Hardware | undefined = hardware.data;
  return (
    <Card title={t("backgroundWork.videoHeading")} icon="device">
      <ErrorMessage error={error ?? hardware.error} />
      {data && data.encoders.length === 0 ? <p className="muted">{t("backgroundWork.videoNotYet")}</p> : null}
      {data && data.encoders.length > 0 ? (
        <>
          <p className="row-title">
            {t("backgroundWork.videoChosen")}
            <Badge tone="accent">{t(`backgroundWork.encoder.${data.chosen}`)}</Badge>
          </p>
          <ul className="plain-list" aria-label={t("backgroundWork.videoTested")}>
            {data.encoders.map((encoder) => (
              <li key={encoder.kind}>
                <span>{t(`backgroundWork.encoder.${encoder.kind}`)}</span>{" "}
                <Badge tone={encoder.works ? "accent" : "muted"}>
                  {encoder.works ? t("backgroundWork.videoWorks") : t("backgroundWork.videoNo")}
                </Badge>
                {encoder.error ? <span className="muted"> {encoder.error}</span> : null}
              </li>
            ))}
          </ul>
          {data.lastFailure ? (
            <Notice kind="warning">{t("backgroundWork.videoFailure", { failure: data.lastFailure })}</Notice>
          ) : null}
          <p className="muted">{t("backgroundWork.videoNote")}</p>
        </>
      ) : null}
      <Button onClick={() => void detect()} disabled={testing}>
        {testing ? t("backgroundWork.videoTesting") : t("backgroundWork.videoTest")}
      </Button>
    </Card>
  );
}

/**
 * Jobs that failed for good, newest first, with what went wrong as a code
 * and a way to run each again. The overview warns about them and leads
 * here. The detail text is left out: it can name a file (D-004).
 */
function FailedJobs() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const failed = useQuery({
    queryKey: ["jobs", "failed"],
    queryFn: () => call(api.GET("/api/v1/jobs", { params: { query: { state: "failed", limit: 50 } } })),
    refetchInterval: 15_000,
  });

  const retry = async (id: string): Promise<void> => {
    setError(null);
    try {
      await call(api.POST("/api/v1/jobs/{id}/retry", { params: { path: { id } } }));
    } catch (e) {
      setError(e);
    } finally {
      await queryClient.invalidateQueries({ queryKey: ["jobs", "failed"] });
      await queryClient.invalidateQueries({ queryKey: ["admin-overview"] });
    }
  };

  const items = failed.data?.items ?? [];
  return (
    <Card title={t("backgroundWork.failedHeading")} icon="alert">
      <ErrorMessage error={error ?? failed.error} />
      {failed.isSuccess && items.length === 0 ? <p className="muted">{t("backgroundWork.noFailures")}</p> : null}
      <ul className="rows">
        {items.map((job) => (
          <li key={job.id} className="row">
            <div className="row-main">
              <p className="row-title">
                {t(`backgroundWork.jobTypes.${job.type.replace(".", "_")}`, { defaultValue: job.type })}
              </p>
              <p className="row-meta">
                {t([`errors.${job.errorCode ?? "job.unexpected_error"}`, "errors.job.unexpected_error"])}
                <span className="dot" />
                {formatDateTime(job.finishedAt ?? job.createdAt)}
                <span className="dot" />
                {t("backgroundWork.attempts", { count: Number(job.attempts) })}
              </p>
            </div>
            <Button size="sm" icon="refresh" onClick={() => void retry(job.id)}>
              {t("backgroundWork.retry")}
            </Button>
          </li>
        ))}
      </ul>
    </Card>
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
