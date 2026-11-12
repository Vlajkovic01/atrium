import type { components } from "@atrium/shared";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { formatBytes } from "../i18n.ts";
import { Button, Check, ChoiceCards, ErrorMessage, Field, Notice, type Choice } from "./ui.tsx";

type StorageTestResult = components["schemas"]["StorageTestResult"];
type StorageKind = "localFileSystem" | "networkShare" | "s3";

/**
 * Describes a storage location, tries it, and saves it. Used by the setup
 * wizard for the first location and by the admin screen for more.
 *
 * Testing never creates anything. A folder that does not exist is created
 * only when the form is saved with "create the folder" ticked.
 */
export function StorageForm(props: {
  readonly defaultName: string;
  readonly submitLabel: string;
  readonly onSaved: () => void | Promise<void>;
}) {
  const { t } = useTranslation();
  const [kind, setKind] = useState<StorageKind>("localFileSystem");
  const [name, setName] = useState(props.defaultName);
  const [path, setPath] = useState("/data");
  const [createDirectory, setCreateDirectory] = useState(true);
  const [s3, setS3] = useState({
    bucket: "",
    serviceUrl: "",
    region: "",
    accessKey: "",
    secretKey: "",
    keyPrefix: "",
    forcePathStyle: true,
  });
  const [result, setResult] = useState<StorageTestResult | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState<"test" | "save" | null>(null);

  const body = () =>
    kind === "s3"
      ? {
          name,
          kind,
          local: null,
          s3: {
            bucket: s3.bucket,
            accessKey: s3.accessKey,
            secretKey: s3.secretKey,
            serviceUrl: s3.serviceUrl || null,
            region: s3.region || null,
            forcePathStyle: s3.forcePathStyle,
            keyPrefix: s3.keyPrefix || null,
          },
        }
      : { name, kind, local: { path, createDirectory }, s3: null };

  // Any change makes the last test result stale.
  const changed = (): void => {
    setResult(null);
    setError(null);
  };

  const test = async (): Promise<void> => {
    setBusy("test");
    changed();
    try {
      setResult(await call(api.POST("/api/v1/storage-locations/test", { body: body() })));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  };

  const save = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setBusy("save");
    changed();
    try {
      await call(api.POST("/api/v1/storage-locations", { body: body() }));
      await props.onSaved();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  };

  const kinds: readonly Choice<StorageKind>[] = [
    {
      value: "localFileSystem",
      title: t("setup.storage.kindLocal"),
      help: t("setup.storage.kindLocalHelp"),
      icon: "drive",
    },
    {
      value: "networkShare",
      title: t("setup.storage.kindShare"),
      help: t("setup.storage.kindShareHelp"),
      icon: "share",
    },
    { value: "s3", title: t("setup.storage.kindS3"), help: t("setup.storage.kindS3Help"), icon: "cloud" },
  ];

  // A folder that does not exist yet is expected when it will be created
  // on saving, so the test says that instead of reporting a failure.
  const willBeCreated = result?.errorCode === "storage.root_missing" && createDirectory && kind !== "s3";
  const optional = ` (${t("common.optional")})`;

  return (
    <form onSubmit={(event) => void save(event)} className="form">
      <ChoiceCards
        legend={t("setup.storage.kind")}
        name="storage-kind"
        value={kind}
        choices={kinds}
        onChange={(value) => {
          setKind(value);
          changed();
        }}
      />

      <Field
        label={t("setup.storage.name")}
        value={name}
        onChange={(e) => {
          setName(e.target.value);
          changed();
        }}
        maxLength={128}
        required
      />

      {kind === "s3" ? (
        <>
          <div className="field-row">
            <Field
              label={t("setup.storage.bucket")}
              value={s3.bucket}
              onChange={(e) => {
                setS3({ ...s3, bucket: e.target.value });
                changed();
              }}
              spellCheck={false}
              required
            />
            <Field
              label={t("setup.storage.region") + optional}
              value={s3.region}
              onChange={(e) => {
                setS3({ ...s3, region: e.target.value });
                changed();
              }}
              spellCheck={false}
            />
          </div>
          <Field
            label={t("setup.storage.serviceUrl") + optional}
            help={t("setup.storage.serviceUrlHelp", { example: "https://s3.example.com" })}
            value={s3.serviceUrl}
            onChange={(e) => {
              setS3({ ...s3, serviceUrl: e.target.value });
              changed();
            }}
            type="url"
            spellCheck={false}
          />
          <div className="field-row">
            <Field
              label={t("setup.storage.accessKey")}
              value={s3.accessKey}
              onChange={(e) => {
                setS3({ ...s3, accessKey: e.target.value });
                changed();
              }}
              autoComplete="off"
              spellCheck={false}
              required
            />
            <Field
              label={t("setup.storage.secretKey")}
              type="password"
              value={s3.secretKey}
              onChange={(e) => {
                setS3({ ...s3, secretKey: e.target.value });
                changed();
              }}
              autoComplete="off"
              required
            />
          </div>
          <Field
            label={t("setup.storage.keyPrefix") + optional}
            value={s3.keyPrefix}
            onChange={(e) => {
              setS3({ ...s3, keyPrefix: e.target.value });
              changed();
            }}
            spellCheck={false}
          />
          <Check
            label={t("setup.storage.forcePathStyle")}
            checked={s3.forcePathStyle}
            onChange={(e) => {
              setS3({ ...s3, forcePathStyle: e.target.checked });
              changed();
            }}
          />
        </>
      ) : (
        <>
          <Field
            label={t("setup.storage.path")}
            help={t("setup.storage.pathHelp", { example: "/data" })}
            value={path}
            onChange={(e) => {
              setPath(e.target.value);
              changed();
            }}
            spellCheck={false}
            required
          />
          <Check
            label={t("setup.storage.createDirectory")}
            checked={createDirectory}
            onChange={(e) => {
              setCreateDirectory(e.target.checked);
              changed();
            }}
          />
          {kind === "networkShare" ? <Notice kind="info">{t("setup.storage.shareNote")}</Notice> : null}
        </>
      )}

      {result?.ok && result.holdsAtriumData ? <Notice kind="warning">{t("setup.storage.holdsData")}</Notice> : null}
      {result?.ok && !result.holdsAtriumData ? (
        <Notice kind="ok">
          {result.freeBytes != null
            ? t("setup.storage.testOkFree", { free: formatBytes(Number(result.freeBytes)) })
            : t("setup.storage.testOk")}
        </Notice>
      ) : null}
      {result && !result.ok && !willBeCreated ? (
        <Notice kind="error">
          {t([`errors.${result.errorCode ?? "storage.unavailable"}`, "errors.storage.unavailable"])}
        </Notice>
      ) : null}
      {willBeCreated ? <Notice kind="info">{t("setup.storage.willCreate")}</Notice> : null}
      <ErrorMessage error={error} />

      <p className="fineprint">{t("setup.storage.formatNote")}</p>

      <div className="actions">
        <Button onClick={() => void test()} disabled={busy !== null} icon="refresh">
          {busy === "test" ? t("setup.storage.testing") : t("setup.storage.test")}
        </Button>
        <Button type="submit" variant="primary" disabled={busy !== null}>
          {props.submitLabel}
        </Button>
      </div>
    </form>
  );
}
